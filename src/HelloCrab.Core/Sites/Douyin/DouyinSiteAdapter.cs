using HelloCrab.Core.Services.Localization;
using System.Net;
using System.Text.Json;
using HelloCrab.Core.Models;
using HelloCrab.Core.Sites;
using HelloCrab.Core.Services.Browser;
using HelloCrab.Core.Utilities;

namespace HelloCrab.Core.Sites.Douyin;

public sealed class DouyinSiteAdapter : ISiteAdapter
{
    public string Id => "douyin";
    public string DisplayName => RuntimeLocalization.Get("Platform.douyin", "抖音网页版");
    public string HomeUrl => "https://www.douyin.com/";

    public bool CanHandlePage(string pageUrl)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri))
            return false;

        return uri.Host.EndsWith("douyin.com", StringComparison.OrdinalIgnoreCase)
               && uri.AbsolutePath.Contains("/user/", StringComparison.OrdinalIgnoreCase);
    }

    public bool IsTargetResponse(string responseUrl, string resourceType, int statusCode, string? requestBody)
    {
        if (statusCode is < 200 or >= 300)
            return false;

        // profile/other 在不同 Chromium/缓存路径下可能被标记为 xhr、fetch 或 other。
        // URL 已做精确接口匹配，因此不再用资源类型把作者作品数响应过滤掉。
        if (IsUserProfileResponse(responseUrl))
            return true;

        return (resourceType.Equals("xhr", StringComparison.OrdinalIgnoreCase)
                || resourceType.Equals("fetch", StringComparison.OrdinalIgnoreCase))
               && responseUrl.Contains(
                   "/aweme/v1/web/aweme/post",
                   StringComparison.OrdinalIgnoreCase);
    }

    public bool TryHandleAuxiliaryResponse(
        string responseUrl,
        string responseJson,
        string pageUrl,
        string? requestBody,
        out string? diagnostic)
    {
        diagnostic = null;
        return IsUserProfileResponse(responseUrl);
    }

    public int? TryReadTotalWorkCount(
        string responseUrl,
        string responseJson,
        string pageUrl,
        string? requestBody)
    {
        if (!IsUserProfileResponse(responseUrl))
            return null;

        var pageSecUserId = TryReadPageSecUserId(pageUrl);
        var responseSecUserId = ReadQueryValue(responseUrl, "sec_user_id")
                                ?? ReadQueryValue(responseUrl, "sec_uid");
        if (!string.IsNullOrWhiteSpace(pageSecUserId)
            && !string.IsNullOrWhiteSpace(responseSecUserId)
            && !SameSecUserId(pageSecUserId, responseSecUserId))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(responseJson);
            var root = document.RootElement;
            if (ReadInt64(root, "status_code") != 0)
                return null;

            var countRoot = root;
            if (TryGetObject(root, "user", out var user))
            {
                var expectedSecUserId = pageSecUserId ?? responseSecUserId;
                var userSecUserId = ReadString(user, "sec_uid");
                // 新版接口可能省略 user.sec_uid；只有双方都有值且确实不一致时才拒绝。
                if (!string.IsNullOrWhiteSpace(expectedSecUserId)
                    && !string.IsNullOrWhiteSpace(userSecUserId)
                    && !SameSecUserId(expectedSecUserId, userSecUserId))
                {
                    return null;
                }

                countRoot = user;
            }

            return AuthorWorkCountReader.TryRead(Id, countRoot)
                   ?? AuthorWorkCountReader.TryRead(Id, root);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public ParsedWorkBatch ParseResponse(string responseUrl, string responseJson, string pageUrl, string? requestBody)
    {
        var pageSecUserId = TryReadPageSecUserId(pageUrl);
        var responseSecUserId = ReadQueryValue(responseUrl, "sec_user_id")
                                ?? ReadQueryValue(responseUrl, "sec_uid");

        // 作者主页 URL 一般是 /user/{sec_uid}，作品接口也会携带同一个
        // sec_user_id。先在响应级别校验，可直接挡住旧标签页、旧作者延迟响应。
        if (!string.IsNullOrWhiteSpace(pageSecUserId)
            && !string.IsNullOrWhiteSpace(responseSecUserId)
            && !SameSecUserId(pageSecUserId, responseSecUserId))
        {
            return new ParsedWorkBatch(
                Array.Empty<WorkItem>(),
                null,
                null,
                RuntimeLocalization.Format("Douyin.OtherAuthorIgnored", "已忽略非目标作者接口响应：目标={0}，响应={1}", ShortId(pageSecUserId), ShortId(responseSecUserId)));
        }

        // /user/self 等特殊地址无法直接提供 sec_uid 时，以本次 post 请求中的
        // sec_user_id 作为会话目标；之后仍逐条验证 aweme.author.sec_uid。
        var expectedSecUserId = pageSecUserId ?? responseSecUserId;

        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;
        var works = new List<WorkItem>();
        var rejectedWorkCount = 0;

        if (!TryGetArray(root, "aweme_list", out var awemeList))
            return new ParsedWorkBatch(works, ReadHasMore(root), ReadString(root, "max_cursor"));

        foreach (var aweme in awemeList.EnumerateArray())
        {
            var workId = ReadString(aweme, "aweme_id");
            if (string.IsNullOrWhiteSpace(workId))
                continue;

            var author = TryGetObject(aweme, "author", out var authorElement)
                ? authorElement
                : default;
            var authorSecUserId = ReadString(author, "sec_uid");

            // 不能只按接口路径 /aweme/post 判断。推荐预取、旧请求、置顶/协作异常
            // 数据都可能带入不同 author。目标身份已知时采用 fail-closed：作者
            // sec_uid 缺失或不一致都不进入下载队列。
            if (!string.IsNullOrWhiteSpace(expectedSecUserId)
                && !SameSecUserId(expectedSecUserId, authorSecUserId))
            {
                rejectedWorkCount++;
                continue;
            }

            // 下载目录按“作者名 + UID”命名。抖音的 UID 来自 author.uid；
            // sec_uid 仅在 uid 缺失时作为兼容兜底。
            var authorId = ReadString(author, "uid")
                           ?? authorSecUserId
                           ?? "unknown-author";
            var authorName = ReadString(author, "nickname") ?? RuntimeLocalization.Get("Common.UnknownAuthor", "未知作者");
            var authorAvatarUrl = ParseAuthorAvatar(author);
            var description = ReadString(aweme, "desc") ?? RuntimeLocalization.Get("Common.UnknownTitle", "无标题");
            var createTime = ReadInt64(aweme, "create_time");

            var assets = ParseImages(aweme);
            if (assets.Count == 0)
            {
                var video = ParseBestVideo(aweme);
                if (video is not null)
                    assets.Add(video);
            }

            // 没有主体视频/图片的响应不作为作品下载。
            if (!assets.Any(x => x.Type is MediaAssetType.Video or MediaAssetType.Image))
                continue;

            var cover = ParseCover(aweme);
            if (cover is not null)
                assets.Add(cover);

            var music = ParseMusic(aweme);
            if (music is not null)
                assets.Add(music);

            works.Add(new WorkItem(
                Id,
                workId,
                authorId,
                authorName,
                authorAvatarUrl,
                description,
                createTime,
                assets,
                pageUrl));
        }

        var diagnostic = rejectedWorkCount > 0
            ? RuntimeLocalization.Format("Douyin.Filtered", "已过滤 {0} 个非目标作者作品，未加入下载队列。", rejectedWorkCount)
            : null;

        return new ParsedWorkBatch(
            works,
            ReadHasMore(root),
            ReadString(root, "max_cursor"),
            diagnostic,
            rejectedWorkCount)
        {
            TotalWorkCount = HelloCrab.Core.Utilities.AuthorWorkCountReader.TryRead(Id, root)
        };
    }

    public bool TryCreateCursorRequest(
        BrowserRequestSnapshot previousRequest,
        string cursor,
        out BrowserPageRequest nextRequest)
        => CursorRequestRewriter.TryRewrite(
            previousRequest,
            cursor,
            new[] { "max_cursor", "cursor" },
            out nextRequest);

    private static string? TryReadPageSecUserId(string pageUrl)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri))
            return null;

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (!segments[index].Equals("user", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = Uri.UnescapeDataString(segments[index + 1]).Trim();
            if (string.IsNullOrWhiteSpace(value)
                || value.Equals("self", StringComparison.OrdinalIgnoreCase)
                || value.Equals("me", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return value;
        }

        return null;
    }

    private static bool IsUserProfileResponse(string responseUrl)
        => Uri.TryCreate(responseUrl, UriKind.Absolute, out var uri)
           && uri.Host.EndsWith("douyin.com", StringComparison.OrdinalIgnoreCase)
           && uri.AbsolutePath.TrimEnd('/').Equals(
               "/aweme/v1/web/user/profile/other",
               StringComparison.OrdinalIgnoreCase);

    private static string? ReadQueryValue(string url, string key)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Query))
        {
            return null;
        }

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var rawName = separator >= 0 ? pair[..separator] : pair;
            if (!string.Equals(
                    WebUtility.UrlDecode(rawName),
                    key,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rawValue = separator >= 0 ? pair[(separator + 1)..] : string.Empty;
            var value = WebUtility.UrlDecode(rawValue)?.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    private static bool SameSecUserId(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left)
           && !string.IsNullOrWhiteSpace(right)
           && string.Equals(left.Trim(), right.Trim(), StringComparison.Ordinal);

    private static string ShortId(string value)
        => value.Length <= 16 ? value : $"{value[..8]}…{value[^6..]}";

    public async Task ScrollNextAsync(IBrowserAutomationService browser, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 抖音页面并不始终使用 window 作为滚动容器。这里动态寻找包含作品卡片、
        // 且确实可以纵向滚动的元素，然后分段滚动，避免一次跳到底部时漏触发懒加载。
        var result = await browser.EvaluatePageAsync("""
            () => {
                const workSelector = [
                    'a[href*="/video/"]',
                    'a[href*="/note/"]',
                    '[data-e2e*="post-item"]',
                    '[data-e2e*="user-post"]'
                ].join(',');

                function describe(el, isRoot) {
                    if (isRoot) return 'document';
                    const id = el.id ? `#${el.id}` : '';
                    const classes = typeof el.className === 'string'
                        ? el.className.trim().split(/\s+/).filter(Boolean).slice(0, 2).map(x => `.${x}`).join('')
                        : '';
                    return `${el.tagName.toLowerCase()}${id}${classes}`;
                }

                function findScroller() {
                    const root = document.scrollingElement || document.documentElement;
                    const candidates = [{ el: root, isRoot: true }];

                    for (const el of document.querySelectorAll('body *')) {
                        const style = getComputedStyle(el);
                        if (!/(auto|scroll|overlay)/.test(style.overflowY)) continue;
                        if (el.scrollHeight <= el.clientHeight + 100) continue;

                        const rect = el.getBoundingClientRect();
                        if (rect.width < 200 || rect.height < 200) continue;
                        if (style.display === 'none' || style.visibility === 'hidden') continue;
                        candidates.push({ el, isRoot: false });
                    }

                    let best = candidates[0];
                    let bestScore = -1;
                    for (const candidate of candidates) {
                        const el = candidate.el;
                        const range = Math.max(0, el.scrollHeight - el.clientHeight);
                        if (range < 80) continue;

                        const workCount = candidate.isRoot
                            ? document.querySelectorAll(workSelector).length
                            : el.querySelectorAll(workSelector).length;
                        const score = workCount * 1000000 + range + el.clientHeight;
                        if (score > bestScore) {
                            bestScore = score;
                            best = candidate;
                        }
                    }

                    return best;
                }

                const selected = findScroller();
                const el = selected.el;

                // 先把当前已经渲染出的最后一个作品卡片带到视口底部。即使页面改了
                // 滚动容器结构，scrollIntoView 也会沿正确的祖先容器完成滚动。
                const visibleWorks = [...document.querySelectorAll(workSelector)].filter(item => {
                    const rect = item.getBoundingClientRect();
                    return rect.width > 20 && rect.height > 20;
                });
                visibleWorks.at(-1)?.scrollIntoView({ block: 'end', inline: 'nearest', behavior: 'auto' });

                const before = selected.isRoot ? window.scrollY : el.scrollTop;
                const viewport = selected.isRoot ? window.innerHeight : el.clientHeight;
                const height = el.scrollHeight;
                const max = Math.max(0, height - viewport);
                const step = Math.max(700, viewport * 0.82);
                let target = Math.min(max, before + step);

                // 靠近底部时直接贴近底部，使加载哨兵进入可视区。
                if (max - target < Math.max(180, viewport * 0.28))
                    target = max;

                if (!selected.isRoot) {
                    if (!el.hasAttribute('tabindex')) el.setAttribute('tabindex', '-1');
                    try { el.focus({ preventScroll: true }); } catch { }
                    el.scrollTo({ top: target, behavior: 'auto' });
                    el.scrollTop = target;
                    el.dispatchEvent(new Event('scroll', { bubbles: true }));
                } else {
                    window.scrollTo({ top: target, behavior: 'auto' });
                    el.scrollTop = target;
                    window.dispatchEvent(new Event('scroll'));
                }

                const rect = selected.isRoot
                    ? { left: 0, top: 0, width: window.innerWidth, height: window.innerHeight }
                    : el.getBoundingClientRect();

                return {
                    x: Math.max(1, Math.min(window.innerWidth - 2, rect.left + rect.width / 2)),
                    y: Math.max(1, Math.min(window.innerHeight - 2, rect.top + rect.height / 2)),
                    wheelDelta: Math.max(500, Math.round(viewport * 0.75)),
                    container: describe(el, selected.isRoot)
                };
            }
            """);

        // 某些版本的页面只在真实 wheel 输入后启动下一页加载。Playwright 的鼠标事件
        // 不要求 Chrome 窗口处于前台。
        if (result.ValueKind == JsonValueKind.Object)
        {
            var x = result.TryGetProperty("x", out var xElement) ? xElement.GetDouble() : 1;
            var y = result.TryGetProperty("y", out var yElement) ? yElement.GetDouble() : 1;
            var delta = result.TryGetProperty("wheelDelta", out var deltaElement) ? deltaElement.GetDouble() : 800;

            // 采集页面锁定时，短暂允许 Playwright 的真实 wheel/keyboard 输入穿透遮罩。
            await SetAutomationInputPassThroughAsync(browser, true);
            try
            {
                await browser.MoveMouseAsync(x, y, cancellationToken);
                await browser.WheelAsync(0, delta, cancellationToken);
                await Task.Delay(250, cancellationToken);

                // 上面的脚本已将真实滚动容器设为焦点，End 可作为浏览器原生输入兜底。
                await browser.PressKeyAsync("End", cancellationToken);
            }
            finally
            {
                await SetAutomationInputPassThroughAsync(browser, false);
            }
        }

        await Task.Delay(1_200, cancellationToken);

        // 到达底部却没有触发时，先轻微上移再回到底部，重新触发 IntersectionObserver。
        await browser.EvaluatePageAsync("""
            () => {
                const workSelector = [
                    'a[href*="/video/"]',
                    'a[href*="/note/"]',
                    '[data-e2e*="post-item"]',
                    '[data-e2e*="user-post"]'
                ].join(',');

                const root = document.scrollingElement || document.documentElement;
                const candidates = [{ el: root, isRoot: true }];
                for (const el of document.querySelectorAll('body *')) {
                    const style = getComputedStyle(el);
                    if (!/(auto|scroll|overlay)/.test(style.overflowY)) continue;
                    if (el.scrollHeight <= el.clientHeight + 100) continue;
                    const rect = el.getBoundingClientRect();
                    if (rect.width < 200 || rect.height < 200) continue;
                    candidates.push({ el, isRoot: false });
                }

                let selected = candidates[0];
                let bestScore = -1;
                for (const candidate of candidates) {
                    const el = candidate.el;
                    const range = Math.max(0, el.scrollHeight - el.clientHeight);
                    if (range < 80) continue;
                    const count = candidate.isRoot
                        ? document.querySelectorAll(workSelector).length
                        : el.querySelectorAll(workSelector).length;
                    const score = count * 1000000 + range + el.clientHeight;
                    if (score > bestScore) {
                        bestScore = score;
                        selected = candidate;
                    }
                }

                const el = selected.el;
                const top = selected.isRoot ? window.scrollY : el.scrollTop;
                const viewport = selected.isRoot ? window.innerHeight : el.clientHeight;
                const max = Math.max(0, el.scrollHeight - viewport);
                if (max <= 0 || top < max - 100) return;

                const up = Math.max(0, max - Math.max(260, viewport * 0.38));
                if (selected.isRoot) {
                    window.scrollTo({ top: up, behavior: 'auto' });
                    window.dispatchEvent(new Event('scroll'));
                    setTimeout(() => {
                        window.scrollTo({ top: max, behavior: 'auto' });
                        window.dispatchEvent(new Event('scroll'));
                    }, 180);
                } else {
                    el.scrollTop = up;
                    el.dispatchEvent(new Event('scroll', { bubbles: true }));
                    setTimeout(() => {
                        el.scrollTop = max;
                        el.dispatchEvent(new Event('scroll', { bubbles: true }));
                    }, 180);
                }
            }
            """);

        await Task.Delay(800, cancellationToken);
    }

    private static async Task SetAutomationInputPassThroughAsync(IBrowserAutomationService browser, bool enabled)
    {
        try
        {
            await browser.EvaluatePageAsync(
                """
                enabled => {
                    window.__smcAllowAutomationInput = enabled;
                    const overlay = document.getElementById('__social_media_crawler_capture_lock__');
                    if (overlay) {
                        overlay.style.pointerEvents = enabled ? 'none' : 'auto';
                        if (!enabled) {
                            try { overlay.focus({ preventScroll: true }); } catch { overlay.focus(); }
                        }
                    }
                }
                """,
                enabled);
        }
        catch
        {
            // 未开启页面锁定时没有遮罩，不影响滚动逻辑。
        }
    }

    public async Task<PageScrollState> GetScrollStateAsync(IBrowserAutomationService browser, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await browser.EvaluatePageAsync("""
            () => {
                const workSelector = [
                    'a[href*="/video/"]',
                    'a[href*="/note/"]',
                    '[data-e2e*="post-item"]',
                    '[data-e2e*="user-post"]'
                ].join(',');

                function describe(el, isRoot) {
                    if (isRoot) return 'document';
                    const id = el.id ? `#${el.id}` : '';
                    const classes = typeof el.className === 'string'
                        ? el.className.trim().split(/\s+/).filter(Boolean).slice(0, 2).map(x => `.${x}`).join('')
                        : '';
                    return `${el.tagName.toLowerCase()}${id}${classes}`;
                }

                const root = document.scrollingElement || document.documentElement;
                const candidates = [{ el: root, isRoot: true }];
                for (const el of document.querySelectorAll('body *')) {
                    const style = getComputedStyle(el);
                    if (!/(auto|scroll|overlay)/.test(style.overflowY)) continue;
                    if (el.scrollHeight <= el.clientHeight + 100) continue;
                    const rect = el.getBoundingClientRect();
                    if (rect.width < 200 || rect.height < 200) continue;
                    if (style.display === 'none' || style.visibility === 'hidden') continue;
                    candidates.push({ el, isRoot: false });
                }

                let best = candidates[0];
                let bestScore = -1;
                for (const candidate of candidates) {
                    const el = candidate.el;
                    const range = Math.max(0, el.scrollHeight - el.clientHeight);
                    if (range < 80) continue;
                    const count = candidate.isRoot
                        ? document.querySelectorAll(workSelector).length
                        : el.querySelectorAll(workSelector).length;
                    const score = count * 1000000 + range + el.clientHeight;
                    if (score > bestScore) {
                        bestScore = score;
                        best = candidate;
                    }
                }

                const el = best.el;
                return {
                    scrollTop: best.isRoot ? window.scrollY : el.scrollTop,
                    viewportHeight: best.isRoot ? window.innerHeight : el.clientHeight,
                    documentHeight: el.scrollHeight,
                    containerName: describe(el, best.isRoot),
                    workItemCount: best.isRoot
                        ? document.querySelectorAll(workSelector).length
                        : el.querySelectorAll(workSelector).length
                };
            }
            """);

        if (result.ValueKind != JsonValueKind.Object)
            return new PageScrollState(0, 0, 0);

        return new PageScrollState(
            ReadDouble(result, "scrollTop"),
            ReadDouble(result, "viewportHeight"),
            ReadDouble(result, "documentHeight"),
            ReadText(result, "containerName") ?? "document",
            (int)ReadDouble(result, "workItemCount"));
    }

    private static double ReadDouble(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.TryGetDouble(out var number)
            ? number
            : 0;

    private static string? ReadText(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static List<MediaAsset> ParseImages(JsonElement aweme)
    {
        var imageObjects = new List<JsonElement>();

        if (TryGetArray(aweme, "images", out var directImages))
            imageObjects.AddRange(directImages.EnumerateArray());

        if (TryGetObject(aweme, "image_post_info", out var postInfo)
            && TryGetArray(postInfo, "images", out var postImages))
        {
            imageObjects.AddRange(postImages.EnumerateArray());
        }

        var result = new List<MediaAsset>();
        var seenImages = new HashSet<string>(StringComparer.Ordinal);
        var index = 1;
        foreach (var image in imageObjects)
        {
            var urls = ReadImageUrls(image);
            if (urls.Count == 0 || !seenImages.Add(urls[0]))
                continue;

            result.Add(new MediaAsset(
                MediaAssetType.Image,
                index++,
                urls,
                Width: (int)ReadInt64(image, "width"),
                Height: (int)ReadInt64(image, "height")));
        }

        return result;
    }

    private static MediaAsset? ParseBestVideo(JsonElement aweme)
    {
        if (!TryGetObject(aweme, "video", out var video))
            return null;

        var width = (int)ReadInt64(video, "width");
        var height = (int)ReadInt64(video, "height");
        var orderedUrls = new List<string>();
        var selectedCodec = (string?)null;
        var selectedBitrate = 0L;
        var selectedWidth = width;
        var selectedHeight = height;

        // 按用户指定的顺序排列候选地址：H.265 -> H.264 -> bit_rate 最高码率。
        // 下载器会按此顺序尝试；某个 CDN 地址失败时仍可自动降级到后续来源。
        var directH265 = ReadUrlListFromKnownContainers(video, "play_addr_265", "play_addr_bytevc1");
        if (directH265.Count > 0)
        {
            orderedUrls.AddRange(directH265);
            selectedCodec = "h265";
        }

        var directH264 = ReadUrlListFromKnownContainers(video, "play_addr_h264");
        if (directH264.Count > 0)
        {
            orderedUrls.AddRange(directH264);
            selectedCodec ??= "h264";
        }

        var bitrateCandidates = new List<(long Bitrate, int Width, int Height, string? Codec, IReadOnlyList<string> Urls)>();
        if (TryGetArray(video, "bit_rate", out var bitRates))
        {
            foreach (var item in bitRates.EnumerateArray())
            {
                // 同一档位内仍优先 H.265，其次 H.264，最后通用 play_addr。
                var urls = ReadUrlListFromKnownContainers(item, "play_addr_265", "play_addr_bytevc1");
                var codec = urls.Count > 0 ? "h265" : null;

                if (urls.Count == 0)
                {
                    urls = ReadUrlListFromKnownContainers(item, "play_addr_h264");
                    codec = urls.Count > 0 ? "h264" : null;
                }

                if (urls.Count == 0)
                    urls = ReadUrlListFromKnownContainers(item, "play_addr");
                if (urls.Count == 0)
                    continue;

                var bitrate = ReadInt64(item, "bit_rate");
                var itemWidth = (int)ReadInt64(item, "width");
                var itemHeight = (int)ReadInt64(item, "height");
                codec ??= ReadString(item, "gear_name");
                if (ReadBoolean(item, "is_h265"))
                    codec = "h265";

                bitrateCandidates.Add((bitrate, itemWidth, itemHeight, codec, urls));
            }
        }

        var sortedBitrates = bitrateCandidates
            .OrderByDescending(x => x.Bitrate)
            .ThenByDescending(x => (long)x.Width * x.Height)
            .ToArray();

        if (sortedBitrates.Length > 0)
        {
            var best = sortedBitrates[0];
            if (orderedUrls.Count == 0)
            {
                selectedBitrate = best.Bitrate;
                selectedCodec = best.Codec;
                selectedWidth = best.Width;
                selectedHeight = best.Height;
            }

            foreach (var candidate in sortedBitrates)
                orderedUrls.AddRange(candidate.Urls);
        }

        orderedUrls.AddRange(ReadUrlListFromKnownContainers(video, "play_addr", "download_addr"));
        var normalized = NormalizeUrls(orderedUrls);
        if (normalized.Count == 0)
            return null;

        return new MediaAsset(
            MediaAssetType.Video,
            1,
            normalized,
            selectedBitrate,
            selectedWidth,
            selectedHeight,
            selectedCodec);
    }


    private static string? ParseAuthorAvatar(JsonElement author)
    {
        if (author.ValueKind != JsonValueKind.Object)
            return null;

        var urls = new List<string>();
        AddUrlsFromContainer(author, "avatar_larger", urls);
        AddUrlsFromContainer(author, "avatar_300x300", urls);
        AddUrlsFromContainer(author, "avatar_medium", urls);
        AddUrlsFromContainer(author, "avatar_thumb", urls);
        AddUrlsFromContainer(author, "avatar_168x168", urls);
        return NormalizeUrls(urls).FirstOrDefault();
    }

    private static MediaAsset? ParseCover(JsonElement aweme)
    {
        var urls = new List<string>();

        if (TryGetObject(aweme, "video", out var video))
        {
            AddUrlsFromContainer(video, "cover", urls);
            AddUrlsFromContainer(video, "origin_cover", urls);
            AddUrlsFromContainer(video, "raw_cover", urls);
        }

        AddUrlsFromContainer(aweme, "cover", urls);
        AddUrlsFromContainer(aweme, "origin_cover", urls);

        // 图集作品未必有 video.cover，使用第一张原图作为作品封面兜底。
        if (urls.Count == 0)
        {
            var images = ParseImages(aweme);
            var firstImage = images.FirstOrDefault();
            if (firstImage is not null)
                urls.AddRange(firstImage.CandidateUrls);
        }

        var normalized = NormalizeUrls(urls);
        return normalized.Count == 0
            ? null
            : new MediaAsset(MediaAssetType.Cover, 1, normalized);
    }

    private static MediaAsset? ParseMusic(JsonElement aweme)
    {
        if (!TryGetObject(aweme, "music", out var music))
            return null;

        var urls = new List<string>();
        AddUrlsFromFlexibleProperty(music, "play_url", urls);
        AddUrlsFromFlexibleProperty(music, "playUrl", urls);
        AddUrlsFromFlexibleProperty(music, "audition_url", urls);

        var normalized = NormalizeUrls(urls);
        return normalized.Count == 0
            ? null
            : new MediaAsset(MediaAssetType.Music, 1, normalized, Codec: "audio");
    }

    private static IReadOnlyList<string> ReadImageUrls(JsonElement image)
    {
        var urls = new List<string>();
        AddUrlsFromContainer(image, "origin_image", urls);
        AddUrlsFromContainer(image, "display_image", urls);
        AddUrlsFromContainer(image, "download_image", urls);
        AddStringArray(image, "download_url_list", urls);
        AddStringArray(image, "url_list", urls);
        AddUrlsFromContainer(image, "owner_watermark_image", urls);
        AddUrlsFromContainer(image, "thumbnail", urls);
        return NormalizeUrls(urls);
    }

    private static IReadOnlyList<string> ReadUrlListFromKnownContainers(JsonElement parent, params string[] names)
    {
        var urls = new List<string>();
        foreach (var name in names)
            AddUrlsFromContainer(parent, name, urls);
        return NormalizeUrls(urls);
    }

    private static void AddUrlsFromContainer(JsonElement parent, string propertyName, List<string> target)
    {
        if (!TryGetObject(parent, propertyName, out var container))
            return;

        AddStringArray(container, "url_list", target);
        AddStringArray(container, "download_url_list", target);
    }

    private static void AddUrlsFromFlexibleProperty(JsonElement parent, string propertyName, List<string> target)
    {
        if (!TryGetProperty(parent, propertyName, out var value))
            return;

        switch (value.ValueKind)
        {
            case JsonValueKind.String when value.GetString() is { Length: > 0 } url:
                target.Add(url);
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } arrayUrl)
                        target.Add(arrayUrl);
                }
                break;
            case JsonValueKind.Object:
                AddStringArray(value, "url_list", target);
                AddStringArray(value, "download_url_list", target);
                if (TryGetProperty(value, "url", out var direct)
                    && direct.ValueKind == JsonValueKind.String
                    && direct.GetString() is { Length: > 0 } directUrl)
                {
                    target.Add(directUrl);
                }
                break;
        }
    }

    private static void AddStringArray(JsonElement parent, string propertyName, List<string> target)
    {
        if (!TryGetArray(parent, propertyName, out var array))
            return;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } value)
                target.Add(value);
        }
    }

    private static IReadOnlyList<string> NormalizeUrls(IEnumerable<string> urls)
        => urls
            .Select(static url => WebUtility.HtmlDecode(url))
            .OfType<string>()
            .Where(static url => Uri.TryCreate(url, UriKind.Absolute, out _))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static bool? ReadHasMore(JsonElement root)
    {
        if (!TryGetProperty(root, "has_more", out var element))
            return null;

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when element.TryGetInt32(out var value) => value != 0,
            JsonValueKind.String when int.TryParse(element.GetString(), out var value) => value != 0,
            _ => null
        };
    }

    private static string? ReadString(JsonElement parent, string propertyName)
    {
        if (parent.ValueKind != JsonValueKind.Object || !TryGetProperty(parent, propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static long ReadInt64(JsonElement parent, string propertyName)
    {
        if (parent.ValueKind != JsonValueKind.Object || !TryGetProperty(parent, propertyName, out var value))
            return 0;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number))
            return number;
        return 0;
    }

    private static bool ReadBoolean(JsonElement parent, string propertyName)
    {
        if (parent.ValueKind != JsonValueKind.Object || !TryGetProperty(parent, propertyName, out var value))
            return false;
        return value.ValueKind == JsonValueKind.True
               || (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number != 0);
    }

    private static bool TryGetObject(JsonElement parent, string propertyName, out JsonElement value)
        => TryGetProperty(parent, propertyName, out value) && value.ValueKind == JsonValueKind.Object;

    private static bool TryGetArray(JsonElement parent, string propertyName, out JsonElement value)
        => TryGetProperty(parent, propertyName, out value) && value.ValueKind == JsonValueKind.Array;

    private static bool TryGetProperty(JsonElement parent, string propertyName, out JsonElement value)
    {
        value = default;
        if (parent.ValueKind != JsonValueKind.Object)
            return false;

        if (parent.TryGetProperty(propertyName, out value))
            return true;

        foreach (var property in parent.EnumerateObject())
        {
            if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }
}
