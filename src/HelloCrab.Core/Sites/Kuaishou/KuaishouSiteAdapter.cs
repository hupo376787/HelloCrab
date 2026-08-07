using HelloCrab.Core.Services.Localization;
using System.Net;
using System.Text.Json;
using HelloCrab.Core.Models;
using HelloCrab.Core.Services.Browser;

namespace HelloCrab.Core.Sites.Kuaishou;

/// <summary>
/// 快手网页版作者主页适配器。
///
/// 快手作者作品列表同时兼容 www.kuaishou.com 的 /rest/v/profile/feed、
/// live.kuaishou.com 的 /live_api/profile/public，以及旧版 GraphQL。
/// 两种 REST 接口中的主页 principalId/profileId 与作品 author.id 可能不是同一种 ID，
/// 因此以每批响应中占多数的 author.id 锁定目标作者，避免推荐作品进入下载队列。
/// </summary>
public sealed class KuaishouSiteAdapter : ISiteAdapter
{
    public string Id => "kuaishou";
    public string DisplayName => RuntimeLocalization.Get("Platform.kuaishou", "快手网页版");
    public string HomeUrl => "https://www.kuaishou.com/";

    public bool CanHandlePage(string pageUrl)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri))
            return false;

        return IsKuaishouHost(uri.Host)
               && !string.IsNullOrWhiteSpace(TryReadProfileUserId(pageUrl));
    }

    public bool IsTargetResponse(string responseUrl, string resourceType, int statusCode, string? requestBody)
    {
        if (statusCode is < 200 or >= 300
            || (!resourceType.Equals("xhr", StringComparison.OrdinalIgnoreCase)
                && !resourceType.Equals("fetch", StringComparison.OrdinalIgnoreCase))
            || !Uri.TryCreate(responseUrl, UriKind.Absolute, out var uri)
            || !IsKuaishouHost(uri.Host))
        {
            return false;
        }

        // 当前快手 PC 作者主页的作品分页接口。查询字符串中的 __NS_hxfalcon
        // 是动态风控参数，不能参与固定匹配，因此只校验路径。
        if (IsRestProfileFeed(uri) || IsLivePublicProfile(uri))
            return true;

        // 保留旧版 GraphQL 兼容，避免不同账号/灰度版本仍使用旧接口时失效。
        var isGraphQlEndpoint = uri.AbsolutePath.Equals("/graphql", StringComparison.OrdinalIgnoreCase)
                                || uri.AbsolutePath.EndsWith("/m_graphql", StringComparison.OrdinalIgnoreCase)
                                || uri.AbsolutePath.Contains("/graphql/", StringComparison.OrdinalIgnoreCase);
        if (!isGraphQlEndpoint)
            return false;

        // Playwright 能读取 POST body 时只接收作者作品列表操作，避免资料、推荐、心跳等
        // 其他 GraphQL 响应被计入“作品页数”并干扰自动结束判断。
        return string.IsNullOrWhiteSpace(requestBody)
               || requestBody.Contains("visionProfilePhotoList", StringComparison.OrdinalIgnoreCase)
               || requestBody.Contains("publicFeeds", StringComparison.OrdinalIgnoreCase);
    }

    public ParsedWorkBatch ParseResponse(string responseUrl, string responseJson, string pageUrl, string? requestBody)
    {
        using var document = JsonDocument.Parse(responseJson);
        if (!TryFindPhotoListPayload(document.RootElement, out var payload))
        {
            // 作者主页还会产生用户资料、推荐等 GraphQL 响应。它们不是错误，静默忽略。
            return new ParsedWorkBatch(Array.Empty<WorkItem>(), null, null);
        }

        if (!TryGetArray(payload, "feeds", out var feeds)
            && !TryGetArray(payload, "list", out feeds)
            && !TryGetArray(payload, "photoList", out feeds))
        {
            return new ParsedWorkBatch(
                Array.Empty<WorkItem>(),
                ReadHasMore(payload),
                ReadCursor(payload));
        }

        var pageUserId = TryReadProfileUserId(pageUrl);
        var requestUserId = TryReadRequestUserId(requestBody);
        var isRestProfileFeed = Uri.TryCreate(responseUrl, UriKind.Absolute, out var responseUri)
                                && IsRestProfileFeed(responseUri);
        var isLivePublicProfile = responseUri is not null && IsLivePublicProfile(responseUri);
        var usesDominantAuthorLock = isRestProfileFeed || isLivePublicProfile;

        // 两种 REST 接口中的 profileId/principalId 与 feeds/list 中的 author.id 可能不是
        // 同一种 ID，不能直接相等比较。以本批响应中出现次数最多的 author.id 锁定作者，
        // 并继续逐条过滤少数混入的推荐作品。GraphQL 仍优先使用请求体中的作者 ID。
        var batchAuthorId = usesDominantAuthorLock ? FindDominantAuthorId(feeds) : null;
        var expectedUserId = usesDominantAuthorLock
            ? batchAuthorId
            : requestUserId ?? pageUserId;
        var works = new List<WorkItem>();
        var rejected = 0;

        foreach (var feed in feeds.EnumerateArray())
        {
            var photo = TryGetObject(feed, "photo", out var photoElement)
                ? photoElement
                : feed;
            var author = TryGetObject(feed, "author", out var authorElement)
                ? authorElement
                : TryGetObject(feed, "user", out var userElement)
                    ? userElement
                    : TryGetObject(photo, "author", out var photoAuthor)
                        ? photoAuthor
                        : TryGetObject(photo, "user", out var photoUser)
                            ? photoUser
                            : default;

            var authorIds = ReadAuthorIds(author);
            if (!string.IsNullOrWhiteSpace(expectedUserId)
                && !authorIds.Any(id => SameId(id, expectedUserId)))
            {
                // 快手 GraphQL 同一端点也会返回推荐内容。目标作者已从主页 URL 明确时
                // 采用 fail-closed，author.id 缺失或不一致的作品都不进入队列。
                rejected++;
                continue;
            }

            var workId = ReadFirstString(photo, "id", "photoId", "photo_id")
                         ?? ReadFirstString(feed, "id", "photoId", "photo_id");
            if (string.IsNullOrWhiteSpace(workId))
                continue;

            var authorId = authorIds.FirstOrDefault(id => SameId(id, expectedUserId))
                           ?? authorIds.FirstOrDefault()
                           ?? expectedUserId;
            if (string.IsNullOrWhiteSpace(authorId))
            {
                rejected++;
                continue;
            }

            var authorName = ReadFirstString(author, "name", "nickname", "userName") ?? RuntimeLocalization.Get("Common.UnknownAuthor", "未知作者");
            var authorAvatar = ParseAuthorAvatar(author);
            var caption = ReadFirstString(photo, "caption", "title", "description", "desc")
                          ?? ReadFirstString(feed, "caption", "title", "description", "desc")
                          ?? ReadFirstString(feed, "musicName", "music_name")
                          ?? RuntimeLocalization.Get("Common.UnknownTitle", "无标题");
            var timestamp = NormalizeTimestamp(ReadFirstInt64(photo, "timestamp", "createTime", "create_time"));
            if (timestamp == 0)
                timestamp = NormalizeTimestamp(ReadFirstInt64(feed, "timestamp", "createTime", "create_time"));
            if (timestamp == 0)
                timestamp = TryReadTimestampFromMediaUrl(feed, photo);

            // photoUrl/photoUrls 是作者页视频的直接播放地址；只有没有视频地址时，
            // 才把 imgUrls/images 等字段当作图集，避免把视频缩略图误判成图片作品。
            var assets = new List<MediaAsset>();
            var video = ParseVideo(feed, photo);
            if (video is not null)
                assets.Add(video);
            else
                assets.AddRange(ParseImages(feed, photo));

            if (!assets.Any(asset => asset.Type is MediaAssetType.Video or MediaAssetType.Image))
                continue;

            var cover = ParseCover(feed, photo, assets);
            if (cover is not null)
                assets.Add(cover);

            var music = ParseMusic(feed, photo);
            if (music is not null)
                assets.Add(music);

            works.Add(new WorkItem(
                Id,
                workId,
                authorId,
                authorName,
                authorAvatar,
                caption,
                timestamp,
                assets,
                pageUrl));
        }

        var diagnostic = rejected > 0
            ? RuntimeLocalization.Format("Kuaishou.Filtered", "已过滤 {0} 个非目标快手作者作品，未加入下载队列。", rejected)
            : null;

        return new ParsedWorkBatch(
            works,
            ReadHasMore(payload),
            ReadCursor(payload),
            diagnostic,
            rejected);
    }

    public async Task ScrollNextAsync(IBrowserAutomationService browser, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var result = await browser.EvaluatePageAsync("""
            () => {
                const workSelector = [
                    'a[href*="/short-video/"]',
                    '[class*="photo-card" i]',
                    '[class*="video-card" i]',
                    '[class*="feed-card" i]',
                    '[data-photo-id]'
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
                    if (rect.width < 220 || rect.height < 220) continue;
                    if (style.display === 'none' || style.visibility === 'hidden') continue;
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

                const visibleWorks = [...document.querySelectorAll(workSelector)].filter(item => {
                    const rect = item.getBoundingClientRect();
                    return rect.width > 20 && rect.height > 20;
                });
                visibleWorks.at(-1)?.scrollIntoView({ block: 'end', inline: 'nearest', behavior: 'auto' });

                const el = selected.el;
                const before = selected.isRoot ? window.scrollY : el.scrollTop;
                const viewport = selected.isRoot ? window.innerHeight : el.clientHeight;
                const max = Math.max(0, el.scrollHeight - viewport);
                let target = Math.min(max, before + Math.max(720, viewport * 0.88));
                if (max - target < Math.max(200, viewport * 0.3)) target = max;

                if (selected.isRoot) {
                    window.scrollTo({ top: target, behavior: 'auto' });
                    root.scrollTop = target;
                    window.dispatchEvent(new Event('scroll'));
                } else {
                    if (!el.hasAttribute('tabindex')) el.setAttribute('tabindex', '-1');
                    try { el.focus({ preventScroll: true }); } catch { }
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
                    wheelDelta: Math.max(520, Math.round(viewport * 0.78)),
                    container: describe(el, selected.isRoot)
                };
            }
            """, cancellationToken);

        if (result.ValueKind == JsonValueKind.Object)
        {
            var x = ReadDouble(result, "x", 1);
            var y = ReadDouble(result, "y", 1);
            var delta = ReadDouble(result, "wheelDelta", 800);
            await SetAutomationInputPassThroughAsync(browser, true);
            try
            {
                await browser.MoveMouseAsync(x, y, cancellationToken);
                await browser.WheelAsync(0, delta, cancellationToken);
                await Task.Delay(250, cancellationToken);
                await browser.PressKeyAsync("End", cancellationToken);
            }
            finally
            {
                await SetAutomationInputPassThroughAsync(browser, false);
            }
        }

        await Task.Delay(1_400, cancellationToken);
    }

    public async Task<PageScrollState> GetScrollStateAsync(
        IBrowserAutomationService browser,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await browser.EvaluatePageAsync("""
            () => {
                const workSelector = [
                    'a[href*="/short-video/"]',
                    '[class*="photo-card" i]',
                    '[class*="video-card" i]',
                    '[class*="feed-card" i]',
                    '[data-photo-id]'
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
                    if (rect.width < 220 || rect.height < 220) continue;
                    if (style.display === 'none' || style.visibility === 'hidden') continue;
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
                return {
                    scrollTop: selected.isRoot ? window.scrollY : el.scrollTop,
                    viewportHeight: selected.isRoot ? window.innerHeight : el.clientHeight,
                    documentHeight: el.scrollHeight,
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
            ReadString(result, "containerName") ?? "document",
            (int)ReadDouble(result, "workItemCount"));
    }

    private static bool TryFindPhotoListPayload(JsonElement root, out JsonElement payload)
    {
        payload = default;

        // 当前 /rest/v/profile/feed 响应的根节点就是：
        // { result, pcursor, feeds: [...] }。
        if (root.ValueKind == JsonValueKind.Object
            && TryGetArray(root, "feeds", out _))
        {
            payload = root;
            return true;
        }

        // live.kuaishou.com/live_api/profile/public 响应为：
        // { data: { result, pcursor, list: [...], live: {...} } }。
        // data.live 是直播状态，普通作品只解析 data.list。
        if (root.ValueKind == JsonValueKind.Object
            && TryGetObject(root, "data", out var liveData)
            && TryGetArray(liveData, "list", out _))
        {
            payload = liveData;
            return true;
        }

        // 兼容普通 GraphQL 响应和少数情况下的批量 GraphQL 数组响应。
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (TryFindPhotoListPayload(item, out payload))
                    return true;
            }

            return false;
        }

        var current = TryGetObject(root, "data", out var data) ? data : root;
        foreach (var name in new[]
                 {
                     "visionProfilePhotoList",
                     "profilePhotoList",
                     "visionProfilePhotoListV2",
                     "publicFeeds"
                 })
        {
            if (TryGetObject(current, name, out payload))
                return true;
        }

        return false;
    }

    private static List<MediaAsset> ParseImages(JsonElement feed, JsonElement photo)
    {
        var urls = new List<string>();
        foreach (var property in new[] { "imgUrls", "imageUrls", "images", "atlasUrls", "imageList" })
        {
            AddUrlsFromFlexibleProperty(photo, property, urls);
            AddUrlsFromFlexibleProperty(feed, property, urls);
        }

        var normalized = NormalizeUrls(urls);
        var result = new List<MediaAsset>();
        for (var index = 0; index < normalized.Count; index++)
        {
            result.Add(new MediaAsset(
                MediaAssetType.Image,
                index + 1,
                new[] { normalized[index] }));
        }

        return result;
    }

    private static MediaAsset? ParseVideo(JsonElement feed, JsonElement photo)
    {
        var urls = new List<string>();
        foreach (var property in new[]
                 {
                     "photoUrl", "photoUrls", "playUrl", "playUrls",
                     "videoUrl", "videoUrls", "downloadUrl", "downloadUrls"
                 })
        {
            AddUrlsFromFlexibleProperty(photo, property, urls);
            AddUrlsFromFlexibleProperty(feed, property, urls);
        }

        // 部分返回把播放地址放在 currentWork/playInfo 中。
        foreach (var containerName in new[] { "currentWork", "playInfo", "videoResource", "video" })
        {
            if (!TryGetObject(photo, containerName, out var container)
                && !TryGetObject(feed, containerName, out container))
            {
                continue;
            }

            foreach (var property in new[] { "playUrl", "photoUrl", "url", "urls", "manifest" })
                AddUrlsFromFlexibleProperty(container, property, urls);
        }

        var normalized = NormalizeUrls(urls);
        return normalized.Count == 0
            ? null
            : new MediaAsset(
                MediaAssetType.Video,
                1,
                normalized,
                Width: (int)ReadFirstInt64(photo, "width"),
                Height: (int)ReadFirstInt64(photo, "height"));
    }

    private static MediaAsset? ParseCover(
        JsonElement feed,
        JsonElement photo,
        IReadOnlyList<MediaAsset> assets)
    {
        var urls = new List<string>();
        foreach (var property in new[]
                 {
                     "coverUrl", "coverUrls", "thumbnailUrl", "thumbnailUrls",
                     "poster", "animatedCoverUrl"
                 })
        {
            AddUrlsFromFlexibleProperty(photo, property, urls);
            AddUrlsFromFlexibleProperty(feed, property, urls);
        }

        if (urls.Count == 0)
        {
            var firstImage = assets.FirstOrDefault(asset => asset.Type == MediaAssetType.Image);
            if (firstImage is not null)
                urls.AddRange(firstImage.CandidateUrls);
        }

        var normalized = NormalizeUrls(urls);
        return normalized.Count == 0
            ? null
            : new MediaAsset(MediaAssetType.Cover, 1, normalized);
    }

    private static MediaAsset? ParseMusic(JsonElement feed, JsonElement photo)
    {
        var urls = new List<string>();
        foreach (var property in new[] { "musicUrl", "audioUrl", "soundUrl" })
        {
            AddUrlsFromFlexibleProperty(photo, property, urls);
            AddUrlsFromFlexibleProperty(feed, property, urls);
        }

        if (TryGetObject(photo, "music", out var music)
            || TryGetObject(feed, "music", out music))
        {
            foreach (var property in new[] { "url", "playUrl", "play_url", "audioUrl" })
                AddUrlsFromFlexibleProperty(music, property, urls);
        }

        var normalized = NormalizeUrls(urls);
        return normalized.Count == 0
            ? null
            : new MediaAsset(MediaAssetType.Music, 1, normalized, Codec: "audio");
    }

    private static string? ParseAuthorAvatar(JsonElement author)
    {
        var urls = new List<string>();
        foreach (var property in new[]
                 {
                     "headerUrl", "headerUrls", "avatar", "avatarUrl", "avatarUrls",
                     "profile", "head", "headUrl", "bigHead"
                 })
        {
            AddUrlsFromFlexibleProperty(author, property, urls);
        }

        return NormalizeUrls(urls).FirstOrDefault();
    }

    private static IReadOnlyList<string> ReadAuthorIds(JsonElement author)
    {
        var result = new List<string>();
        foreach (var property in new[] { "id", "eid", "userId", "user_id", "principalId", "kwaiId" })
        {
            var value = ReadString(author, property)?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                result.Add(value);
        }

        return result.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string? FindDominantAuthorId(JsonElement feeds)
    {
        if (feeds.ValueKind != JsonValueKind.Array)
            return null;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var feed in feeds.EnumerateArray())
        {
            var photo = TryGetObject(feed, "photo", out var photoElement)
                ? photoElement
                : feed;
            var author = TryGetObject(feed, "author", out var authorElement)
                ? authorElement
                : TryGetObject(feed, "user", out var userElement)
                    ? userElement
                    : TryGetObject(photo, "author", out var photoAuthor)
                        ? photoAuthor
                        : TryGetObject(photo, "user", out var photoUser)
                            ? photoUser
                            : default;

            foreach (var id in ReadAuthorIds(author))
            {
                counts.TryGetValue(id, out var count);
                counts[id] = count + 1;
            }
        }

        return counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key)
            .FirstOrDefault();
    }

    private static bool IsRestProfileFeed(Uri uri)
    {
        var path = uri.AbsolutePath.TrimEnd('/');
        return path.Equals("/rest/v/profile/feed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLivePublicProfile(Uri uri)
    {
        var path = uri.AbsolutePath.TrimEnd('/');
        return uri.Host.Equals("live.kuaishou.com", StringComparison.OrdinalIgnoreCase)
               && path.Equals("/live_api/profile/public", StringComparison.OrdinalIgnoreCase);
    }

    private static long TryReadTimestampFromMediaUrl(JsonElement feed, JsonElement photo)
    {
        var urls = new List<string>();
        foreach (var property in new[]
                 {
                     "poster", "coverUrl", "animatedCoverUrl", "playUrl", "photoUrl"
                 })
        {
            AddUrlsFromFlexibleProperty(photo, property, urls);
            AddUrlsFromFlexibleProperty(feed, property, urls);
        }

        foreach (var url in NormalizeUrls(urls))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                continue;

            var segments = uri.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var index = 0; index < segments.Length - 4; index++)
            {
                if (!segments[index].Equals("upic", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!int.TryParse(segments[index + 1], out var year)
                    || !int.TryParse(segments[index + 2], out var month)
                    || !int.TryParse(segments[index + 3], out var day)
                    || !int.TryParse(segments[index + 4], out var hour))
                {
                    continue;
                }

                try
                {
                    return new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.FromHours(8))
                        .ToUnixTimeSeconds();
                }
                catch (ArgumentOutOfRangeException)
                {
                    // URL 中的日期片段不合法时继续尝试其他候选地址。
                }
            }
        }

        return 0;
    }

    private static string? TryReadRequestUserId(string? requestBody)
    {
        if (string.IsNullOrWhiteSpace(requestBody))
            return null;

        try
        {
            using var document = JsonDocument.Parse(requestBody);
            if (!TryGetObject(document.RootElement, "variables", out var variables))
                return null;

            return ReadFirstString(variables, "userId", "principalId", "authorId", "eid");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryReadProfileUserId(string pageUrl)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri))
            return null;

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (!segments[index].Equals("profile", StringComparison.OrdinalIgnoreCase)
                && !segments[index].Equals("u", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = Uri.UnescapeDataString(segments[index + 1]).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    private static bool IsKuaishouHost(string host)
        => host.Equals("kuaishou.com", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".kuaishou.com", StringComparison.OrdinalIgnoreCase);

    private static bool SameId(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left)
           && !string.IsNullOrWhiteSpace(right)
           && string.Equals(left.Trim(), right.Trim(), StringComparison.Ordinal);

    private static string? ReadCursor(JsonElement payload)
        => ReadFirstString(payload, "pcursor", "cursor", "nextCursor", "next_cursor");

    private static bool? ReadHasMore(JsonElement payload)
    {
        foreach (var property in new[] { "hasMore", "has_more" })
        {
            if (!TryGetProperty(payload, property, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.True)
                return true;
            if (value.ValueKind == JsonValueKind.False)
                return false;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number != 0;
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
                return number != 0;
        }

        var cursor = ReadCursor(payload);
        if (cursor is null)
            return null;

        return !string.IsNullOrWhiteSpace(cursor)
               && !cursor.Equals("no_more", StringComparison.OrdinalIgnoreCase)
               && !cursor.Equals("nomore", StringComparison.OrdinalIgnoreCase)
               && !cursor.Equals("null", StringComparison.OrdinalIgnoreCase)
               && !cursor.Equals("0", StringComparison.OrdinalIgnoreCase);
    }

    private static long NormalizeTimestamp(long value)
    {
        if (value <= 0)
            return 0;

        // 快手 timestamp 常见为毫秒，WorkItem 统一使用 Unix 秒。
        return value > 9_999_999_999L ? value / 1000 : value;
    }

    private static void AddUrlsFromFlexibleProperty(
        JsonElement parent,
        string propertyName,
        List<string> target)
    {
        if (!TryGetProperty(parent, propertyName, out var value))
            return;

        AddUrlsFromValue(value, target, 0);
    }

    private static void AddUrlsFromValue(JsonElement value, List<string> target, int depth)
    {
        if (depth > 5)
            return;

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
            {
                var text = value.GetString();
                if (string.IsNullOrWhiteSpace(text))
                    return;

                if (Uri.TryCreate(WebUtility.HtmlDecode(text), UriKind.Absolute, out _))
                {
                    target.Add(text);
                    return;
                }

                // 老接口中的 imgUrls.json 可能是序列化后的 JSON 字符串。
                var trimmed = text.Trim();
                if ((trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                    || (trimmed.StartsWith('{') && trimmed.EndsWith('}')))
                {
                    try
                    {
                        using var nested = JsonDocument.Parse(trimmed);
                        AddUrlsFromValue(nested.RootElement, target, depth + 1);
                    }
                    catch (JsonException)
                    {
                    }
                }

                return;
            }
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                    AddUrlsFromValue(item, target, depth + 1);
                return;
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    if (property.Name.Equals("cdn", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Equals("width", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Equals("height", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Equals("size", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    AddUrlsFromValue(property.Value, target, depth + 1);
                }
                return;
        }
    }

    private static IReadOnlyList<string> NormalizeUrls(IEnumerable<string> urls)
        => urls
            .Select(static url => WebUtility.HtmlDecode(url))
            .OfType<string>()
            .Select(static value => value.Trim())
            .Where(static value => value.Length > 0
                                   && Uri.TryCreate(value, UriKind.Absolute, out var uri)
                                   && uri.Scheme is "http" or "https")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string? ReadFirstString(JsonElement parent, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = ReadString(parent, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static long ReadFirstInt64(JsonElement parent, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = ReadInt64(parent, propertyName);
            if (value != 0)
                return value;
        }

        return 0;
    }

    private static string? ReadString(JsonElement parent, string propertyName)
    {
        if (!TryGetProperty(parent, propertyName, out var value))
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
        if (!TryGetProperty(parent, propertyName, out var value))
            return 0;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number))
            return number;
        return 0;
    }

    private static double ReadDouble(JsonElement parent, string propertyName, double fallback = 0)
        => TryGetProperty(parent, propertyName, out var value) && value.TryGetDouble(out var result)
            ? result
            : fallback;

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

    private static async Task SetAutomationInputPassThroughAsync(
        IBrowserAutomationService browser,
        bool enabled)
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
}
