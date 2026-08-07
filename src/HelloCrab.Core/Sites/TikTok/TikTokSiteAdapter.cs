using HelloCrab.Core.Services.Localization;
using System.Net;
using System.Text.Json;
using HelloCrab.Core.Models;
using HelloCrab.Core.Services.Browser;

namespace HelloCrab.Core.Sites.TikTok;

/// <summary>
/// TikTok Web 作者主页适配器。
///
/// 作品列表来自 /api/post/item_list/。接口中的顶层 video.playAddr 往往不是
/// 最高分辨率，因此必须遍历 video.bitrateInfo[].PlayAddr，并按实际宽高选择。
/// </summary>
public sealed class TikTokSiteAdapter : ISiteAdapter
{
    public string Id => "tiktok";
    public string DisplayName => "TikTok Web";
    public string HomeUrl => "https://www.tiktok.com/";

    public bool CanHandlePage(string pageUrl)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri)
            || !uri.Host.EndsWith("tiktok.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(segment => segment.StartsWith('@'));
    }

    public bool IsTargetResponse(string responseUrl, string resourceType, int statusCode, string? requestBody)
        => statusCode is >= 200 and < 300
           && (resourceType.Equals("xhr", StringComparison.OrdinalIgnoreCase)
               || resourceType.Equals("fetch", StringComparison.OrdinalIgnoreCase))
           && responseUrl.Contains("/api/post/item_list/", StringComparison.OrdinalIgnoreCase);

    public ParsedWorkBatch ParseResponse(string responseUrl, string responseJson, string pageUrl, string? requestBody)
    {
        var expectedUniqueId = TryReadPageUniqueId(pageUrl);
        var expectedSecUid = ReadQueryValue(responseUrl, "secUid")
                             ?? ReadQueryValue(responseUrl, "sec_uid");

        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;
        var works = new List<WorkItem>();
        var rejected = 0;

        if (!TryGetArray(root, "itemList", out var itemList))
        {
            return new ParsedWorkBatch(
                works,
                ReadBoolean(root, "hasMore"),
                ReadStringOrNumber(root, "cursor"),
                RuntimeLocalization.Get("TikTok.Error.NoItemList", "TikTok 作品接口中没有 itemList。可能是登录、风控或页面尚未加载完成。"));
        }

        foreach (var item in itemList.EnumerateArray())
        {
            var workId = ReadStringOrNumber(item, "id");
            if (string.IsNullOrWhiteSpace(workId))
                continue;

            if (!TryGetObject(item, "author", out var author))
                continue;

            var uniqueId = ReadString(author, "uniqueId")
                           ?? ReadString(author, "unique_id");
            var secUid = ReadString(author, "secUid")
                         ?? ReadString(author, "sec_uid");

            // 页面用户名和请求 secUid 都可用于阻止旧标签页、推荐预取或其他作者
            // 的延迟响应进入本次下载队列。
            if (!string.IsNullOrWhiteSpace(expectedUniqueId)
                && !string.Equals(expectedUniqueId, uniqueId, StringComparison.OrdinalIgnoreCase))
            {
                rejected++;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(expectedSecUid)
                && !string.IsNullOrWhiteSpace(secUid)
                && !string.Equals(expectedSecUid, secUid, StringComparison.Ordinal))
            {
                rejected++;
                continue;
            }

            var videoAsset = ParseBestVideo(item);
            if (videoAsset is null)
                continue;

            var authorId = ReadStringOrNumber(author, "id")
                           ?? secUid
                           ?? uniqueId
                           ?? "unknown-author";
            var authorName = ReadString(author, "nickname")
                             ?? uniqueId
                             ?? "Unknown author";
            var avatarUrl = ParseAuthorAvatar(author);
            var description = ReadString(item, "desc") ?? "Untitled";
            var createTime = ReadInt64(item, "createTime");
            if (createTime <= 0)
                createTime = ReadInt64(item, "create_time");

            var sourceUrl = !string.IsNullOrWhiteSpace(uniqueId)
                ? $"https://www.tiktok.com/@{Uri.EscapeDataString(uniqueId)}/video/{workId}"
                : pageUrl;

            var assets = new List<MediaAsset> { videoAsset };
            var cover = ParseCover(item);
            if (cover is not null)
                assets.Add(cover);
            var music = ParseMusic(item);
            if (music is not null)
                assets.Add(music);

            works.Add(new WorkItem(
                Id,
                workId,
                authorId,
                authorName,
                avatarUrl,
                description,
                createTime,
                assets,
                sourceUrl)
            {
                AuthorPageUrl = pageUrl,
                MediaRefererUrl = sourceUrl
            });
        }

        var diagnosticParts = new List<string>
        {
            RuntimeLocalization.Format("TikTok.PageSummary", "TikTok 本页解析到 {0} 个视频。", works.Count)
        };
        if (rejected > 0)
            diagnosticParts.Add(RuntimeLocalization.Format("TikTok.Filtered", "已过滤 {0} 个非目标作者作品。", rejected));

        return new ParsedWorkBatch(
            works,
            ReadBoolean(root, "hasMore"),
            ReadStringOrNumber(root, "cursor"),
            string.Join(' ', diagnosticParts),
            rejected);
    }

    public async Task ScrollNextAsync(IBrowserAutomationService browser, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var result = await browser.EvaluatePageAsync("""
            () => {
                const workSelector = [
                    'a[href*="/video/"]',
                    '[data-e2e="user-post-item"]',
                    '[data-e2e*="user-post"]',
                    '[data-e2e*="post-item"]'
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
                        if (rect.width < 240 || rect.height < 240) continue;
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
                            best = candidate;
                            bestScore = score;
                        }
                    }
                    return best;
                }

                const selected = findScroller();
                const el = selected.el;
                const works = [...document.querySelectorAll(workSelector)].filter(item => {
                    const rect = item.getBoundingClientRect();
                    return rect.width > 20 && rect.height > 20;
                });
                works.at(-1)?.scrollIntoView({ block: 'end', inline: 'nearest', behavior: 'auto' });

                const before = selected.isRoot ? window.scrollY : el.scrollTop;
                const viewport = selected.isRoot ? window.innerHeight : el.clientHeight;
                const max = Math.max(0, el.scrollHeight - viewport);
                let target = Math.min(max, before + Math.max(760, viewport * 0.88));
                if (max - target < Math.max(180, viewport * 0.3)) target = max;

                if (selected.isRoot) {
                    window.scrollTo({ top: target, behavior: 'auto' });
                    el.scrollTop = target;
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
                    wheelDelta: Math.max(600, Math.round(viewport * 0.78)),
                    container: describe(el, selected.isRoot)
                };
            }
            """, cancellationToken);

        if (result.ValueKind == JsonValueKind.Object)
        {
            var x = ReadDouble(result, "x", 1);
            var y = ReadDouble(result, "y", 1);
            var delta = ReadDouble(result, "wheelDelta", 900);
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

        await Task.Delay(1_100, cancellationToken);
    }

    public async Task<PageScrollState> GetScrollStateAsync(
        IBrowserAutomationService browser,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await browser.EvaluatePageAsync("""
            () => {
                const workSelector = [
                    'a[href*="/video/"]',
                    '[data-e2e="user-post-item"]',
                    '[data-e2e*="user-post"]',
                    '[data-e2e*="post-item"]'
                ].join(',');
                const root = document.scrollingElement || document.documentElement;
                const candidates = [{ el: root, isRoot: true }];
                for (const el of document.querySelectorAll('body *')) {
                    const style = getComputedStyle(el);
                    if (!/(auto|scroll|overlay)/.test(style.overflowY)) continue;
                    if (el.scrollHeight <= el.clientHeight + 100) continue;
                    const rect = el.getBoundingClientRect();
                    if (rect.width < 240 || rect.height < 240) continue;
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
                        selected = candidate;
                        bestScore = score;
                    }
                }

                const el = selected.el;
                const classes = !selected.isRoot && typeof el.className === 'string'
                    ? el.className.trim().split(/\s+/).filter(Boolean).slice(0, 2).map(x => `.${x}`).join('')
                    : '';
                return {
                    scrollTop: selected.isRoot ? window.scrollY : el.scrollTop,
                    viewportHeight: selected.isRoot ? window.innerHeight : el.clientHeight,
                    documentHeight: el.scrollHeight,
                    containerName: selected.isRoot ? 'document' : `${el.tagName.toLowerCase()}${classes}`,
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

    private static MediaAsset? ParseBestVideo(JsonElement item)
    {
        if (!TryGetObject(item, "video", out var video))
            return null;

        var candidates = new List<VideoCandidate>();
        if (TryGetArray(video, "bitrateInfo", out var bitrateInfo))
        {
            foreach (var entry in bitrateInfo.EnumerateArray())
            {
                if (!TryGetObject(entry, "PlayAddr", out var playAddr)
                    && !TryGetObject(entry, "playAddr", out playAddr))
                {
                    continue;
                }

                var urls = ReadUrlList(playAddr, "UrlList", "urlList", "url_list");
                if (urls.Count == 0)
                    continue;

                var width = (int)ReadInt64(playAddr, "Width");
                if (width <= 0)
                    width = (int)ReadInt64(playAddr, "width");
                var height = (int)ReadInt64(playAddr, "Height");
                if (height <= 0)
                    height = (int)ReadInt64(playAddr, "height");
                var bitrate = ReadInt64(entry, "Bitrate");
                if (bitrate <= 0)
                    bitrate = ReadInt64(entry, "bitrate");
                var codec = ReadString(entry, "CodecType")
                            ?? ReadString(entry, "codecType")
                            ?? ReadString(entry, "GearName");

                candidates.Add(new VideoCandidate(width, height, bitrate, codec, urls));
            }
        }

        // 某些响应只提供 PlayAddrStruct 或顶层 playAddr。
        if (TryGetObject(video, "PlayAddrStruct", out var directStruct))
        {
            var urls = ReadUrlList(directStruct, "UrlList", "urlList", "url_list");
            if (urls.Count > 0)
            {
                candidates.Add(new VideoCandidate(
                    (int)(ReadInt64(directStruct, "Width") > 0
                        ? ReadInt64(directStruct, "Width")
                        : ReadInt64(directStruct, "width")),
                    (int)(ReadInt64(directStruct, "Height") > 0
                        ? ReadInt64(directStruct, "Height")
                        : ReadInt64(directStruct, "height")),
                    ReadInt64(video, "bitrate"),
                    ReadString(video, "codecType"),
                    urls));
            }
        }

        var topLevelPlayAddr = ReadString(video, "playAddr");
        if (!string.IsNullOrWhiteSpace(topLevelPlayAddr))
        {
            candidates.Add(new VideoCandidate(
                (int)ReadInt64(video, "width"),
                (int)ReadInt64(video, "height"),
                ReadInt64(video, "bitrate"),
                ReadString(video, "codecType"),
                new[] { topLevelPlayAddr }));
        }

        var ordered = candidates
            .Where(candidate => candidate.Urls.Count > 0)
            .OrderByDescending(candidate => (long)candidate.Width * candidate.Height)
            .ThenByDescending(candidate => Math.Max(candidate.Width, candidate.Height))
            .ThenByDescending(candidate => candidate.Bitrate)
            .ThenBy(candidate => IsH264(candidate.Codec) ? 0 : 1)
            .ToArray();
        if (ordered.Length == 0)
            return null;

        var best = ordered[0];
        var allUrls = NormalizeUrls(ordered.SelectMany(candidate => candidate.Urls));
        return new MediaAsset(
            MediaAssetType.Video,
            1,
            allUrls,
            best.Bitrate,
            best.Width,
            best.Height,
            best.Codec);
    }

    private static MediaAsset? ParseCover(JsonElement item)
    {
        if (!TryGetObject(item, "video", out var video))
            return null;

        var urls = new List<string>();
        AddString(video, "originCover", urls);
        AddString(video, "cover", urls);
        AddString(video, "dynamicCover", urls);
        if (TryGetObject(video, "zoomCover", out var zoomCover))
        {
            foreach (var property in zoomCover.EnumerateObject()
                         .OrderByDescending(property => int.TryParse(property.Name, out var size) ? size : 0))
            {
                if (property.Value.ValueKind == JsonValueKind.String
                    && property.Value.GetString() is { Length: > 0 } url)
                {
                    urls.Add(url);
                }
            }
        }

        var normalized = NormalizeUrls(urls);
        return normalized.Count == 0
            ? null
            : new MediaAsset(MediaAssetType.Cover, 1, normalized);
    }

    private static MediaAsset? ParseMusic(JsonElement item)
    {
        if (!TryGetObject(item, "music", out var music))
            return null;

        var urls = new List<string>();
        AddString(music, "playUrl", urls);
        AddString(music, "play_url", urls);
        var normalized = NormalizeUrls(urls);
        return normalized.Count == 0
            ? null
            : new MediaAsset(MediaAssetType.Music, 1, normalized, Codec: "audio");
    }

    private static string? ParseAuthorAvatar(JsonElement author)
    {
        var urls = new List<string>();
        AddString(author, "avatarLarger", urls);
        AddString(author, "avatarMedium", urls);
        AddString(author, "avatarThumb", urls);
        AddString(author, "avatar_larger", urls);
        AddString(author, "avatar_medium", urls);
        AddString(author, "avatar_thumb", urls);
        return NormalizeUrls(urls).FirstOrDefault();
    }

    private static string? TryReadPageUniqueId(string pageUrl)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri))
            return null;

        foreach (var segment in uri.AbsolutePath
                     .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!segment.StartsWith('@'))
                continue;
            var value = Uri.UnescapeDataString(segment[1..]).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    private static string? ReadQueryValue(string url, string key)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        foreach (var pair in uri.Query.TrimStart('?')
                     .Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var name = WebUtility.UrlDecode(separator >= 0 ? pair[..separator] : pair);
            if (!string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
                continue;
            var value = WebUtility.UrlDecode(separator >= 0 ? pair[(separator + 1)..] : string.Empty);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        return null;
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
                    if (overlay) overlay.style.pointerEvents = enabled ? 'none' : 'auto';
                }
                """,
                enabled);
        }
        catch
        {
            // 未开启页面锁定时没有遮罩，不影响滚动逻辑。
        }
    }

    private static IReadOnlyList<string> ReadUrlList(JsonElement parent, params string[] names)
    {
        var urls = new List<string>();
        foreach (var name in names)
        {
            if (!TryGetProperty(parent, name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String
                        && item.GetString() is { Length: > 0 } url)
                    {
                        urls.Add(url);
                    }
                }
            }
            else if (value.ValueKind == JsonValueKind.String
                     && value.GetString() is { Length: > 0 } directUrl)
            {
                urls.Add(directUrl);
            }
        }
        return NormalizeUrls(urls);
    }

    private static IReadOnlyList<string> NormalizeUrls(IEnumerable<string> urls)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in urls)
        {
            var value = WebUtility.HtmlDecode(raw)?.Trim();
            if (string.IsNullOrWhiteSpace(value))
                continue;
            if (value.StartsWith("//", StringComparison.Ordinal))
                value = "https:" + value;
            if (!Uri.TryCreate(value, UriKind.Absolute, out _))
                continue;
            if (seen.Add(value))
                result.Add(value);
        }
        return result;
    }

    private static void AddString(JsonElement parent, string propertyName, List<string> target)
    {
        if (TryGetProperty(parent, propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text)
        {
            target.Add(text);
        }
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(propertyName, out value))
                return true;
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static bool TryGetObject(JsonElement element, string propertyName, out JsonElement value)
        => TryGetProperty(element, propertyName, out value) && value.ValueKind == JsonValueKind.Object;

    private static bool TryGetArray(JsonElement element, string propertyName, out JsonElement value)
        => TryGetProperty(element, propertyName, out value) && value.ValueKind == JsonValueKind.Array;

    private static string? ReadString(JsonElement element, string propertyName)
        => TryGetProperty(element, propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadStringOrNumber(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static long ReadInt64(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;
        return value.ValueKind == JsonValueKind.String
               && long.TryParse(value.GetString(), out number)
            ? number
            : 0;
    }

    private static bool? ReadBoolean(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt64(out var number) => number != 0,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var boolean) => boolean,
            JsonValueKind.String when long.TryParse(value.GetString(), out var number) => number != 0,
            _ => null
        };
    }

    private static double ReadDouble(JsonElement element, string propertyName, double fallback = 0)
        => TryGetProperty(element, propertyName, out var value) && value.TryGetDouble(out var number)
            ? number
            : fallback;

    private static bool IsH264(string? codec)
        => codec?.Contains("264", StringComparison.OrdinalIgnoreCase) == true
           || codec?.Contains("avc", StringComparison.OrdinalIgnoreCase) == true;

    private sealed record VideoCandidate(
        int Width,
        int Height,
        long Bitrate,
        string? Codec,
        IReadOnlyList<string> Urls);
}
