using HelloCrab.Core.Services.Localization;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using HelloCrab.Core.Models;
using HelloCrab.Core.Services.Browser;

namespace HelloCrab.Core.Sites.Weibo;

/// <summary>
/// 解析微博作者主页的 /ajax/statuses/mymblog 响应。
/// 图片严格优先使用 pic_infos.{picId}.largest.url，并按 pic_ids 保持原始图集顺序；type=livephoto 时同时解析同级 video 动态资源；
/// 视频优先解析 page_info/mix_media_info 中 media_info.playback_list，按实际分辨率、
/// 清晰度索引、码率和文件大小选择最高画质 MP4。
/// </summary>
public sealed class WeiboSiteAdapter : ISiteAdapter
{
    public string Id => "weibo";
    public string DisplayName => RuntimeLocalization.Get("Platform.weibo", "微博网页版");
    public string HomeUrl => "https://weibo.com/";

    public bool CanHandlePage(string pageUrl)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri)
            || !IsWeiboHost(uri.Host))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(TryReadProfileUid(uri));
    }

    public bool IsTargetResponse(
        string responseUrl,
        string resourceType,
        int statusCode,
        string? requestBody)
    {
        if (statusCode is < 200 or >= 300
            || (!resourceType.Equals("xhr", StringComparison.OrdinalIgnoreCase)
                && !resourceType.Equals("fetch", StringComparison.OrdinalIgnoreCase))
            || !Uri.TryCreate(responseUrl, UriKind.Absolute, out var uri)
            || !IsWeiboHost(uri.Host))
        {
            return false;
        }

        return uri.AbsolutePath.TrimEnd('/').Equals(
            "/ajax/statuses/mymblog",
            StringComparison.OrdinalIgnoreCase);
    }

    public ParsedWorkBatch ParseResponse(
        string responseUrl,
        string responseJson,
        string pageUrl,
        string? requestBody)
    {
        var pageUid = Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri)
            ? TryReadProfileUid(pageUri)
            : null;
        var requestUid = ReadQueryValue(responseUrl, "uid");

        if (!string.IsNullOrWhiteSpace(pageUid)
            && !string.IsNullOrWhiteSpace(requestUid)
            && !string.Equals(pageUid, requestUid, StringComparison.Ordinal))
        {
            return new ParsedWorkBatch(
                Array.Empty<WorkItem>(),
                null,
                null,
                RuntimeLocalization.Format("Weibo.OtherAuthorIgnored", "已忽略非目标微博作者响应：当前主页 UID={0}，接口 UID={1}。", pageUid, requestUid));
        }

        var expectedUid = requestUid ?? pageUid;
        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;
        if (!TryGetObject(root, "data", out var data)
            || !TryGetArray(data, "list", out var list))
        {
            return new ParsedWorkBatch(Array.Empty<WorkItem>(), null, null);
        }

        var works = new List<WorkItem>();
        var rejectedCount = 0;
        var retweetedCount = 0;
        foreach (var status in list.EnumerateArray())
        {
            // 微博接口会把转发微博作为当前作者主页列表中的一条记录返回。
            // 只要 retweeted_status 存在且不是 JSON null，就说明当前记录是转发，
            // 整条跳过，不能下载转发正文自身或被转发微博中的任何媒体。
            if (status.TryGetProperty("retweeted_status", out var retweetedStatus)
                && retweetedStatus.ValueKind is not JsonValueKind.Null
                    and not JsonValueKind.Undefined)
            {
                retweetedCount++;
                continue;
            }

            if (!TryGetObject(status, "user", out var user))
                continue;

            var authorId = ReadFirstString(user, "idstr", "id");
            if (string.IsNullOrWhiteSpace(authorId))
                continue;

            // mymblog 偶尔会混入推荐、广告或其他作者内容，目标 UID 已知时采用严格过滤。
            if (!string.IsNullOrWhiteSpace(expectedUid)
                && !string.Equals(expectedUid, authorId, StringComparison.Ordinal))
            {
                rejectedCount++;
                continue;
            }

            var assets = ParseMediaAssets(status);
            if (assets.Count == 0)
                continue;

            var workId = ReadFirstString(status, "idstr", "mid", "id");
            if (string.IsNullOrWhiteSpace(workId))
                continue;

            var authorName = ReadFirstString(user, "screen_name", "name") ?? RuntimeLocalization.Get("Common.UnknownAuthor", "未知作者");
            var authorAvatar = ReadFirstString(
                user,
                "avatar_hd",
                "avatar_large",
                "profile_image_url");
            var description = NormalizeDescription(
                ReadFirstString(status, "text_raw", "text") ?? RuntimeLocalization.Get("Common.UnknownTitle", "无标题"));
            var createTime = ParseCreatedAt(ReadFirstString(status, "created_at"));
            var mblogId = ReadFirstString(status, "mblogid");
            var sourceUrl = string.IsNullOrWhiteSpace(mblogId)
                ? $"https://weibo.com/u/{Uri.EscapeDataString(authorId)}"
                : $"https://weibo.com/{Uri.EscapeDataString(authorId)}/{Uri.EscapeDataString(mblogId)}";
            var authorPageUrl = $"https://weibo.com/u/{Uri.EscapeDataString(authorId)}";

            works.Add(new WorkItem(
                Id,
                workId,
                authorId,
                authorName,
                authorAvatar,
                description,
                createTime,
                assets,
                sourceUrl)
            {
                AuthorPageUrl = authorPageUrl,
                MediaRefererUrl = sourceUrl
            });
        }

        var sinceId = ReadFirstString(data, "since_id");
        bool? hasMore = list.GetArrayLength() == 0
            ? false
            : !string.IsNullOrWhiteSpace(sinceId)
              && !sinceId.Equals("0", StringComparison.OrdinalIgnoreCase);
        var diagnostics = new List<string>(2);
        if (retweetedCount > 0)
            diagnostics.Add(RuntimeLocalization.Format("Weibo.RetweetsFiltered", "已过滤 {0} 条转发微博，仅采集原创微博。", retweetedCount));
        if (rejectedCount > 0)
            diagnostics.Add(RuntimeLocalization.Format("Weibo.OtherFiltered", "已过滤 {0} 条非目标微博作者内容。", rejectedCount));

        var diagnostic = diagnostics.Count > 0
            ? string.Join(" ", diagnostics)
            : null;

        return new ParsedWorkBatch(
            works,
            hasMore,
            sinceId,
            diagnostic,
            rejectedCount + retweetedCount);
    }

    public async Task ScrollNextAsync(
        IBrowserAutomationService browser,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 微博新版页面并不保证 document 是实际滚动容器；部分布局会把作者作品流
        // 放在带 overflow-y 的内部容器中。这里按“包含作品节点数量 + 可滚动范围”
        // 动态选择容器，并优先选择真正包住作品流的内部滚动元素。
        var result = await browser.EvaluatePageAsync("""
            () => {
                const workSelector = [
                    'article',
                    '[mid]',
                    '[data-testid*="feed" i]',
                    '[class*="Feed_wrap" i]',
                    '[class*="Feed_card" i]',
                    '[class*="feed-card" i]',
                    'a[href*="/status/"]'
                ].join(',');

                function describe(el, isRoot) {
                    if (isRoot) return 'document';
                    const id = el.id ? `#${el.id}` : '';
                    const classes = typeof el.className === 'string'
                        ? el.className.trim().split(/\s+/).filter(Boolean).slice(0, 3)
                            .map(x => `.${x}`).join('')
                        : '';
                    return `${el.tagName.toLowerCase()}${id}${classes}`;
                }

                function rootHeight(root) {
                    return Math.max(
                        root?.scrollHeight || 0,
                        document.documentElement?.scrollHeight || 0,
                        document.body?.scrollHeight || 0);
                }

                function findScroller() {
                    const root = document.scrollingElement || document.documentElement;
                    const candidates = [{ el: root, isRoot: true }];

                    for (const el of document.querySelectorAll('body *')) {
                        const style = getComputedStyle(el);
                        if (!/(auto|scroll|overlay)/.test(style.overflowY)) continue;
                        if (el.scrollHeight <= el.clientHeight + 80) continue;

                        const rect = el.getBoundingClientRect();
                        if (rect.width < 260 || rect.height < 240) continue;
                        if (rect.bottom <= 0 || rect.top >= window.innerHeight) continue;
                        if (style.display === 'none' || style.visibility === 'hidden') continue;
                        candidates.push({ el, isRoot: false });
                    }

                    let best = candidates[0];
                    let bestScore = -1;
                    for (const candidate of candidates) {
                        const el = candidate.el;
                        const viewport = candidate.isRoot
                            ? (window.innerHeight || root.clientHeight || 0)
                            : el.clientHeight;
                        const height = candidate.isRoot ? rootHeight(root) : el.scrollHeight;
                        const range = Math.max(0, height - viewport);
                        if (range < 80) continue;

                        const workCount = candidate.isRoot
                            ? document.querySelectorAll(workSelector).length
                            : el.querySelectorAll(workSelector).length;
                        const nonRootBonus = candidate.isRoot ? 0 : 250000;
                        const score = workCount * 1000000 + nonRootBonus + range + viewport;
                        if (score > bestScore) {
                            bestScore = score;
                            best = candidate;
                        }
                    }

                    return best;
                }

                const selected = findScroller();
                const el = selected.el;
                const root = document.scrollingElement || document.documentElement;
                const getTop = () => selected.isRoot
                    ? Math.max(window.scrollY || 0, root.scrollTop || 0)
                    : el.scrollTop;
                const viewport = selected.isRoot
                    ? (window.innerHeight || root.clientHeight || 800)
                    : el.clientHeight;
                const height = selected.isRoot ? rootHeight(root) : el.scrollHeight;
                const max = Math.max(0, height - viewport);
                const before = getTop();

                const workRoot = selected.isRoot ? document : el;
                const works = [...workRoot.querySelectorAll(workSelector)].filter(item => {
                    const rect = item.getBoundingClientRect();
                    return rect.width > 30 && rect.height > 30;
                });

                // 先把当前最后一个作品带到容器底部，再继续分段下移，保证微博的
                // IntersectionObserver / 懒加载哨兵有机会进入可视区。
                works.at(-1)?.scrollIntoView({ block: 'end', inline: 'nearest', behavior: 'auto' });
                const afterIntoView = getTop();
                const start = Math.max(before, afterIntoView);
                let target = Math.min(max, start + Math.max(700, viewport * 0.82));
                if (max - target < Math.max(180, viewport * 0.28))
                    target = max;

                if (selected.isRoot) {
                    window.scrollTo({ top: target, behavior: 'auto' });
                    root.scrollTop = target;
                    document.documentElement.scrollTop = target;
                    if (document.body) document.body.scrollTop = target;
                    window.dispatchEvent(new Event('scroll'));
                    document.dispatchEvent(new Event('scroll', { bubbles: true }));
                } else {
                    if (!el.hasAttribute('tabindex')) el.setAttribute('tabindex', '-1');
                    try { el.focus({ preventScroll: true }); } catch { el.focus(); }
                    el.scrollTo({ top: target, behavior: 'auto' });
                    el.scrollTop = target;
                    el.dispatchEvent(new Event('scroll', { bubbles: true }));
                }

                const rect = selected.isRoot
                    ? { left: 0, top: 0, width: window.innerWidth, height: window.innerHeight }
                    : el.getBoundingClientRect();

                return {
                    x: Math.max(1, Math.min(window.innerWidth - 2, rect.left + rect.width / 2)),
                    y: Math.max(1, Math.min(window.innerHeight - 2, rect.top + rect.height / 2)),
                    wheelDelta: Math.max(560, Math.round(viewport * 0.76)),
                    before,
                    afterScript: getTop(),
                    container: describe(el, selected.isRoot)
                };
            }
            """, cancellationToken);

        if (result.ValueKind == JsonValueKind.Object)
        {
            var x = ReadDouble(result, "x", 1);
            var y = ReadDouble(result, "y", 1);
            var delta = ReadDouble(result, "wheelDelta", 760);
            var before = ReadDouble(result, "before");

            // 采集锁会在捕获阶段拦截 wheel/keydown。微博旧实现没有像抖音、快手
            // 一样临时放行自动化输入，导致真实滚轮实际上落在锁定遮罩上。
            await SetAutomationInputPassThroughAsync(browser, true, cancellationToken);
            try
            {
                await browser.MoveMouseAsync(x, y, cancellationToken);
                await browser.WheelAsync(0, delta, cancellationToken);
                await Task.Delay(300, cancellationToken);

                // 程序化滚动和真实 wheel 都没有移动时，再用原生 PageDown 兜底。
                var afterWheel = await GetScrollStateAsync(browser, cancellationToken);
                if (afterWheel.ScrollY <= before + 5)
                {
                    await browser.PressKeyAsync("PageDown", cancellationToken);
                    await Task.Delay(250, cancellationToken);
                }
            }
            finally
            {
                await SetAutomationInputPassThroughAsync(browser, false, cancellationToken);
            }
        }

        // 页面滚动本身也使用轻微抖动，避免固定节奏反复触发分页。
        await Task.Delay(Random.Shared.Next(1_050, 1_750), cancellationToken);
    }

    public async Task<PageScrollState> GetScrollStateAsync(
        IBrowserAutomationService browser,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await browser.EvaluatePageAsync("""
            () => {
                const workSelector = [
                    'article',
                    '[mid]',
                    '[data-testid*="feed" i]',
                    '[class*="Feed_wrap" i]',
                    '[class*="Feed_card" i]',
                    '[class*="feed-card" i]',
                    'a[href*="/status/"]'
                ].join(',');

                function describe(el, isRoot) {
                    if (isRoot) return 'document';
                    const id = el.id ? `#${el.id}` : '';
                    const classes = typeof el.className === 'string'
                        ? el.className.trim().split(/\s+/).filter(Boolean).slice(0, 3)
                            .map(x => `.${x}`).join('')
                        : '';
                    return `${el.tagName.toLowerCase()}${id}${classes}`;
                }

                function rootHeight(root) {
                    return Math.max(
                        root?.scrollHeight || 0,
                        document.documentElement?.scrollHeight || 0,
                        document.body?.scrollHeight || 0);
                }

                const root = document.scrollingElement || document.documentElement;
                const candidates = [{ el: root, isRoot: true }];
                for (const el of document.querySelectorAll('body *')) {
                    const style = getComputedStyle(el);
                    if (!/(auto|scroll|overlay)/.test(style.overflowY)) continue;
                    if (el.scrollHeight <= el.clientHeight + 80) continue;
                    const rect = el.getBoundingClientRect();
                    if (rect.width < 260 || rect.height < 240) continue;
                    if (rect.bottom <= 0 || rect.top >= window.innerHeight) continue;
                    if (style.display === 'none' || style.visibility === 'hidden') continue;
                    candidates.push({ el, isRoot: false });
                }

                let selected = candidates[0];
                let bestScore = -1;
                for (const candidate of candidates) {
                    const el = candidate.el;
                    const viewport = candidate.isRoot
                        ? (window.innerHeight || root.clientHeight || 0)
                        : el.clientHeight;
                    const height = candidate.isRoot ? rootHeight(root) : el.scrollHeight;
                    const range = Math.max(0, height - viewport);
                    if (range < 80) continue;
                    const count = candidate.isRoot
                        ? document.querySelectorAll(workSelector).length
                        : el.querySelectorAll(workSelector).length;
                    const nonRootBonus = candidate.isRoot ? 0 : 250000;
                    const score = count * 1000000 + nonRootBonus + range + viewport;
                    if (score > bestScore) {
                        bestScore = score;
                        selected = candidate;
                    }
                }

                const el = selected.el;
                const viewport = selected.isRoot
                    ? (window.innerHeight || root.clientHeight || 0)
                    : el.clientHeight;
                const height = selected.isRoot ? rootHeight(root) : el.scrollHeight;
                return {
                    scrollTop: selected.isRoot
                        ? Math.max(window.scrollY || 0, root.scrollTop || 0)
                        : el.scrollTop,
                    viewportHeight: viewport,
                    documentHeight: height,
                    containerName: describe(el, selected.isRoot),
                    workItemCount: selected.isRoot
                        ? document.querySelectorAll(workSelector).length
                        : el.querySelectorAll(workSelector).length
                };
            }
            """, cancellationToken);

        if (result.ValueKind != JsonValueKind.Object)
            return new PageScrollState(0, 0, 0);

        return new PageScrollState(
            ReadDouble(result, "scrollTop"),
            ReadDouble(result, "viewportHeight"),
            ReadDouble(result, "documentHeight"),
            ReadFirstString(result, "containerName") ?? "document",
            (int)ReadInt64(result, "workItemCount"));
    }

    private static async Task SetAutomationInputPassThroughAsync(
        IBrowserAutomationService browser,
        bool enabled,
        CancellationToken cancellationToken)
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
                enabled,
                cancellationToken);
        }
        catch
        {
            // 未开启页面锁定、页面正在跳转或已经关闭时，不阻断滚动流程。
        }
    }

    private static List<MediaAsset> ParseMediaAssets(JsonElement status)
    {
        // 微博图文混排使用 mix_media_info.items，并且顺序就是页面展示顺序。
        // 每个 item 可能是图片，也可能是带 media_info 的视频。
        if (TryGetObject(status, "mix_media_info", out var mixMediaInfo)
            && TryGetArray(mixMediaInfo, "items", out var mixedItems))
        {
            var mixedAssets = new List<MediaAsset>();
            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var index = 0;
            foreach (var item in mixedItems.EnumerateArray())
            {
                if (!TryGetObject(item, "data", out var itemData))
                    continue;

                var itemType = ReadFirstString(item, "type")
                               ?? ReadFirstString(itemData, "type", "object_type");
                if (itemType?.Contains("video", StringComparison.OrdinalIgnoreCase) == true
                    || TryGetObject(itemData, "media_info", out _))
                {
                    if (TryGetObject(itemData, "media_info", out var mediaInfo)
                        && TryParseBestVideoAsset(mediaInfo, index, out var video))
                    {
                        var uniqueCandidates = video.CandidateUrls
                            .Where(seenUrls.Add)
                            .ToArray();
                        if (uniqueCandidates.Length > 0)
                        {
                            mixedAssets.Add(video with { CandidateUrls = uniqueCandidates });
                            index++;
                        }
                    }

                    continue;
                }

                if (TryParseLargestImageAsset(itemData, index, seenUrls, out var image))
                {
                    mixedAssets.Add(image);
                    if (TryParseLivePhotoAsset(itemData, index, seenUrls, out var livePhoto))
                        mixedAssets.Add(livePhoto);
                    index++;
                }
            }

            if (mixedAssets.Count > 0)
                return mixedAssets;
        }

        // 普通视频微博使用 page_info.media_info。此时 pic_infos 往往只是视频封面，
        // 不应再当成一张独立作品图片下载。
        if (TryGetObject(status, "page_info", out var pageInfo)
            && TryGetObject(pageInfo, "media_info", out var pageMediaInfo)
            && TryParseBestVideoAsset(pageMediaInfo, 0, out var pageVideo))
        {
            var assets = new List<MediaAsset> { pageVideo };
            var cover = ParseVideoCover(pageInfo, pageMediaInfo);
            if (cover is not null)
                assets.Add(cover);
            return assets;
        }

        return ParseLargestImages(status);
    }

    private static List<MediaAsset> ParseLargestImages(JsonElement status)
    {
        var assets = new List<MediaAsset>();
        if (!TryGetObject(status, "pic_infos", out var picInfos))
            return assets;

        var orderedPicIds = new List<string>();
        if (TryGetArray(status, "pic_ids", out var picIds))
        {
            foreach (var item in picIds.EnumerateArray())
            {
                var id = ReadElementString(item);
                if (!string.IsNullOrWhiteSpace(id))
                    orderedPicIds.Add(id);
            }
        }

        if (orderedPicIds.Count == 0)
            orderedPicIds.AddRange(picInfos.EnumerateObject().Select(property => property.Name));

        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var picId in orderedPicIds)
        {
            if (!picInfos.TryGetProperty(picId, out var info)
                || info.ValueKind != JsonValueKind.Object
                || !TryGetObject(info, "largest", out var largest))
            {
                continue;
            }

            var url = ReadFirstString(largest, "url");
            if (string.IsNullOrWhiteSpace(url) || !seenUrls.Add(url))
                continue;

            assets.Add(new MediaAsset(
                MediaAssetType.Image,
                index,
                new[] { url },
                Width: (int)ReadInt64(largest, "width"),
                Height: (int)ReadInt64(largest, "height")));
            if (TryParseLivePhotoAsset(info, index, seenUrls, out var livePhoto))
                assets.Add(livePhoto);
            index++;
        }

        return assets;
    }

    private static bool TryParseLargestImageAsset(
        JsonElement itemData,
        int index,
        HashSet<string> seenUrls,
        out MediaAsset asset)
    {
        asset = default!;
        JsonElement largest;
        if (TryGetObject(itemData, "largest", out var directLargest))
        {
            largest = directLargest;
        }
        else if (TryGetObject(itemData, "pic_info", out var picInfo)
                 && TryGetObject(picInfo, "pic_big", out var picBig))
        {
            largest = picBig;
        }
        else
        {
            return false;
        }

        var url = NormalizeMediaUrl(ReadFirstString(largest, "url"));
        if (string.IsNullOrWhiteSpace(url) || !seenUrls.Add(url))
            return false;

        asset = new MediaAsset(
            MediaAssetType.Image,
            index,
            new[] { url },
            Width: (int)ReadInt64(largest, "width"),
            Height: (int)ReadInt64(largest, "height"));
        return true;
    }

    private static bool TryParseLivePhotoAsset(
        JsonElement info,
        int index,
        HashSet<string> seenUrls,
        out MediaAsset asset)
    {
        asset = default!;
        var type = ReadFirstString(info, "type", "object_type");
        if (!string.Equals(type, "livephoto", StringComparison.OrdinalIgnoreCase))
            return false;

        var url = NormalizeMediaUrl(ReadFirstString(info, "video"));
        if (string.IsNullOrWhiteSpace(url) || !seenUrls.Add(url))
            return false;

        asset = new MediaAsset(
            MediaAssetType.LivePhoto,
            index,
            new[] { url });
        return true;
    }

    private static bool TryParseBestVideoAsset(
        JsonElement mediaInfo,
        int index,
        out MediaAsset asset)
    {
        var candidates = new List<WeiboVideoCandidate>();
        if (TryGetArray(mediaInfo, "playback_list", out var playbackList))
        {
            foreach (var entry in playbackList.EnumerateArray())
            {
                if (!TryGetObject(entry, "play_info", out var playInfo))
                    continue;

                var mime = ReadFirstString(playInfo, "mime");
                var type = ReadInt64(playInfo, "type");
                if (type != 1
                    || mime?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) != true)
                {
                    continue;
                }

                var url = NormalizeMediaUrl(ReadFirstString(playInfo, "url"));
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                var width = (int)ReadInt64(playInfo, "width");
                var height = (int)ReadInt64(playInfo, "height");
                var bitrate = ReadInt64(playInfo, "bitrate");
                var size = ReadInt64(playInfo, "size");
                var qualityIndex = TryGetObject(entry, "meta", out var meta)
                    ? ReadInt64(meta, "quality_index")
                    : 0;
                var codec = ReadFirstString(playInfo, "video_codecs");
                candidates.Add(new WeiboVideoCandidate(
                    url,
                    width,
                    height,
                    bitrate,
                    size,
                    qualityIndex,
                    codec));
            }
        }

        // 部分旧微博没有 playback_list，只提供这些兼容字段。
        AddLegacyVideoCandidates(mediaInfo, candidates);

        var ordered = candidates
            .Where(candidate => Uri.TryCreate(candidate.Url, UriKind.Absolute, out _))
            .GroupBy(candidate => candidate.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(candidate => (long)candidate.Width * candidate.Height)
            .ThenByDescending(candidate => candidate.QualityIndex)
            .ThenByDescending(candidate => candidate.Bitrate)
            .ThenByDescending(candidate => candidate.Size)
            .ToArray();

        if (ordered.Length == 0)
        {
            asset = default!;
            return false;
        }

        var best = ordered[0];
        asset = new MediaAsset(
            MediaAssetType.Video,
            index,
            ordered.Select(candidate => candidate.Url).ToArray(),
            best.Bitrate,
            best.Width,
            best.Height,
            best.Codec);
        return true;
    }

    private static void AddLegacyVideoCandidates(
        JsonElement mediaInfo,
        List<WeiboVideoCandidate> candidates)
    {
        var knownFields = new (string Name, int Width, int Height, long Quality)[]
        {
            ("mp4_2160p_mp4", 3840, 2160, 2160),
            ("mp4_1440p_mp4", 2560, 1440, 1440),
            ("mp4_1080p_mp4", 1920, 1080, 1080),
            ("hevc_mp4_1080p", 1920, 1080, 1080),
            ("mp4_720p_mp4", 1280, 720, 720),
            ("hevc_mp4_720p", 1280, 720, 720),
            ("h265_mp4_hd", 852, 480, 480),
            ("mp4_hd_url", 852, 480, 480),
            ("stream_url_hd", 852, 480, 480),
            ("mp4_sd_url", 640, 360, 360),
            ("stream_url", 640, 360, 360)
        };

        foreach (var field in knownFields)
        {
            var url = NormalizeMediaUrl(ReadFirstString(mediaInfo, field.Name));
            if (string.IsNullOrWhiteSpace(url))
                continue;

            candidates.Add(new WeiboVideoCandidate(
                url,
                field.Width,
                field.Height,
                0,
                0,
                field.Quality,
                null));
        }
    }

    private static MediaAsset? ParseVideoCover(JsonElement pageInfo, JsonElement mediaInfo)
    {
        string? url = null;
        var width = 0;
        var height = 0;

        if (TryGetObject(mediaInfo, "big_pic_info", out var bigPicInfo)
            && TryGetObject(bigPicInfo, "pic_big", out var picBig))
        {
            url = ReadFirstString(picBig, "url");
            width = (int)ReadInt64(picBig, "width");
            height = (int)ReadInt64(picBig, "height");
        }

        url ??= ReadFirstString(pageInfo, "page_pic");
        url = NormalizeMediaUrl(url);
        return string.IsNullOrWhiteSpace(url)
            ? null
            : new MediaAsset(MediaAssetType.Cover, 0, new[] { url }, Width: width, Height: height);
    }

    private static string? NormalizeMediaUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var url = WebUtility.HtmlDecode(value.Trim());
        if (url.StartsWith("//", StringComparison.Ordinal))
            return "https:" + url;

        // 微博接口中仍有大量 http://f.video.weibocdn.com 地址，浏览器实际支持 HTTPS。
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return "https://" + url[7..];

        return url;
    }

    private sealed record WeiboVideoCandidate(
        string Url,
        int Width,
        int Height,
        long Bitrate,
        long Size,
        long QualityIndex,
        string? Codec);

    private static long ParseCreatedAt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return DateTimeOffset.Now.ToUnixTimeSeconds();

        var normalized = Regex.Replace(
            value.Trim(),
            @"(?<=[+-]\d{2})(\d{2})(?=\s\d{4}$)",
            ":$1");
        if (DateTimeOffset.TryParseExact(
                normalized,
                "ddd MMM dd HH:mm:ss zzz yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var exact))
        {
            return exact.ToUnixTimeSeconds();
        }

        return DateTimeOffset.TryParse(
            normalized,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out var parsed)
            ? parsed.ToUnixTimeSeconds()
            : DateTimeOffset.Now.ToUnixTimeSeconds();
    }

    private static string NormalizeDescription(string value)
    {
        var decoded = WebUtility.HtmlDecode(value);
        var withoutHtml = Regex.Replace(decoded, "<[^>]+>", " ");
        var withoutInvisible = withoutHtml
            .Replace("\u200B", string.Empty, StringComparison.Ordinal)
            .Replace("\uFEFF", string.Empty, StringComparison.Ordinal);
        var collapsed = Regex.Replace(withoutInvisible, @"\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(collapsed) ? RuntimeLocalization.Get("Common.UnknownTitle", "无标题") : collapsed;
    }

    private static string? TryReadProfileUid(Uri uri)
    {
        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
            return null;

        if (segments.Length >= 2
            && (segments[0].Equals("u", StringComparison.OrdinalIgnoreCase)
                || segments[0].Equals("profile", StringComparison.OrdinalIgnoreCase))
            && IsNumericUid(segments[1]))
        {
            return Uri.UnescapeDataString(segments[1]);
        }

        return IsNumericUid(segments[0])
            ? Uri.UnescapeDataString(segments[0])
            : null;
    }

    private static bool IsNumericUid(string value)
        => value.Length >= 5 && value.All(char.IsDigit);

    private static bool IsWeiboHost(string host)
        => host.Equals("weibo.com", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".weibo.com", StringComparison.OrdinalIgnoreCase);

    private static string? ReadQueryValue(string url, string key)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        foreach (var pair in uri.Query.TrimStart('?')
                     .Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var name = WebUtility.UrlDecode(parts[0]);
            if (!string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
                continue;

            var value = parts.Length == 2 ? WebUtility.UrlDecode(parts[1]) : string.Empty;
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        return null;
    }

    private static bool TryGetObject(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out value)
            && value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryGetArray(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out value)
            && value.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string? ReadFirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
                continue;

            var text = ReadElementString(value);
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return null;
    }

    private static string? ReadElementString(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };

    private static long ReadInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return 0;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;

        return value.ValueKind == JsonValueKind.String
               && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            ? number
            : 0;
    }

    private static double ReadDouble(JsonElement element, string name, double fallback = 0)
    {
        if (!element.TryGetProperty(name, out var value))
            return fallback;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            return number;

        return fallback;
    }
}
