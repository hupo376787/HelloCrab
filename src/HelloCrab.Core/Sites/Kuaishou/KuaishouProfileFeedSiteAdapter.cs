using System.Net;
using System.Text.Json;
using HelloCrab.Core.Models;
using HelloCrab.Core.Services.Browser;
using HelloCrab.Core.Services.Localization;

namespace HelloCrab.Core.Sites.Kuaishou;

/// <summary>
/// 当前快手 PC 作者主页适配器。
/// 默认打开 https://www.kuaishou.com/，进入作者主页后只监听
/// https://www.kuaishou.com/rest/v/profile/feed 的 Fetch/XHR 响应。
/// 响应根结构：{ result, pcursor, feeds: [...] }。
/// </summary>
public sealed class KuaishouProfileFeedSiteAdapter : ISiteAdapter
{
    private readonly KuaishouSiteAdapter _scrollAdapter = new();

    public string Id => "kuaishou";
    public string DisplayName => RuntimeLocalization.Get("Platform.kuaishou", "快手网页版");
    public string HomeUrl => "https://www.kuaishou.com/";

    public bool CanHandlePage(string pageUrl)
        => Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri)
           && uri.Host.Equals("www.kuaishou.com", StringComparison.OrdinalIgnoreCase)
           && uri.AbsolutePath.Split(
               '/',
               StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               is ["profile", var profileId, ..]
           && !string.IsNullOrWhiteSpace(profileId);

    public bool IsTargetResponse(
        string responseUrl,
        string resourceType,
        int statusCode,
        string? requestBody)
    {
        if (statusCode is < 200 or >= 300
            || (!resourceType.Equals("xhr", StringComparison.OrdinalIgnoreCase)
                && !resourceType.Equals("fetch", StringComparison.OrdinalIgnoreCase))
            || !Uri.TryCreate(responseUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host.Equals("www.kuaishou.com", StringComparison.OrdinalIgnoreCase)
               && uri.AbsolutePath.TrimEnd('/').Equals(
                   "/rest/v/profile/feed",
                   StringComparison.OrdinalIgnoreCase);
    }

    public ParsedWorkBatch ParseResponse(
        string responseUrl,
        string responseJson,
        string pageUrl,
        string? requestBody)
    {
        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object
            || ReadInt64(root, "result") != 1
            || !TryGetArray(root, "feeds", out var feeds))
        {
            return new ParsedWorkBatch(
                Array.Empty<WorkItem>(),
                ReadHasMore(root),
                ReadString(root, "pcursor"));
        }

        // profile URL 中的 principalId 和 feed.author.id 不是同一种 ID。
        // 以本批 feeds 中占多数的 author.id 锁定目标作者，过滤偶发推荐项。
        var expectedAuthorId = FindDominantAuthorId(feeds);
        var works = new List<WorkItem>();
        var rejected = 0;

        foreach (var feed in feeds.EnumerateArray())
        {
            if (!TryGetObject(feed, "photo", out var photo)
                || !TryGetObject(feed, "author", out var author))
            {
                continue;
            }

            var authorId = ReadString(author, "id");
            if (string.IsNullOrWhiteSpace(authorId))
                continue;

            if (!string.IsNullOrWhiteSpace(expectedAuthorId)
                && !string.Equals(authorId, expectedAuthorId, StringComparison.Ordinal))
            {
                rejected++;
                continue;
            }

            var workId = ReadString(photo, "id");
            if (string.IsNullOrWhiteSpace(workId))
                continue;

            var assets = new List<MediaAsset>();
            var video = ParseVideo(photo);
            if (video is not null)
                assets.Add(video);

            if (assets.Count == 0)
                continue;

            var coverUrl = ReadString(photo, "coverUrl");
            if (IsHttpUrl(coverUrl))
                assets.Add(new MediaAsset(MediaAssetType.Cover, 1, new[] { WebUtility.HtmlDecode(coverUrl!) }));

            var authorName = ReadString(author, "name")
                             ?? RuntimeLocalization.Get("Common.UnknownAuthor", "未知作者");
            var authorAvatar = ReadString(author, "headerUrl");
            var caption = ReadString(photo, "caption")
                          ?? RuntimeLocalization.Get("Common.UnknownTitle", "无标题");
            var timestamp = NormalizeTimestamp(ReadInt64(photo, "timestamp"));

            works.Add(new WorkItem(
                Id,
                workId,
                authorId,
                authorName,
                IsHttpUrl(authorAvatar) ? WebUtility.HtmlDecode(authorAvatar!) : null,
                caption,
                timestamp,
                assets,
                pageUrl));
        }

        var diagnostic = rejected > 0
            ? RuntimeLocalization.Format(
                "Kuaishou.Filtered",
                "已过滤 {0} 个非目标快手作者作品，未加入下载队列。",
                rejected)
            : null;

        return new ParsedWorkBatch(
            works,
            ReadHasMore(root),
            ReadString(root, "pcursor"),
            diagnostic,
            rejected)
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
            new[] { "pcursor", "cursor" },
            out nextRequest);

    public Task ScrollNextAsync(
        IBrowserAutomationService browser,
        CancellationToken cancellationToken)
        => _scrollAdapter.ScrollNextAsync(browser, cancellationToken);

    public Task<PageScrollState> GetScrollStateAsync(
        IBrowserAutomationService browser,
        CancellationToken cancellationToken)
        => _scrollAdapter.GetScrollStateAsync(browser, cancellationToken);

    private static MediaAsset? ParseVideo(JsonElement photo)
    {
        var candidates = new List<VideoCandidate>();

        // 普通 manifest 是 AVC/H.264，优先；H.265 作为备用。
        AddManifestCandidates(photo, "manifest", "h264", 0, candidates);
        AddManifestCandidates(photo, "manifestH265", "h265", 1, candidates);

        var ordered = candidates
            .OrderBy(item => item.Priority)
            .ThenByDescending(item => (long)item.Width * item.Height)
            .ThenByDescending(item => item.Bitrate)
            .ToArray();

        var urls = ordered.Select(item => item.Url).ToList();

        // 真实 profile/feed 响应同时提供 photoUrls[]，作为 manifest CDN 失效时的兜底。
        if (TryGetArray(photo, "photoUrls", out var photoUrls))
        {
            foreach (var entry in photoUrls.EnumerateArray())
            {
                var url = entry.ValueKind == JsonValueKind.String
                    ? entry.GetString()
                    : ReadString(entry, "url");
                if (IsHttpUrl(url))
                    urls.Add(WebUtility.HtmlDecode(url!));
            }
        }

        var normalized = urls
            .Where(IsHttpUrl)
            .Select(WebUtility.HtmlDecode)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (normalized.Length == 0)
            return null;

        var best = ordered.FirstOrDefault();
        return new MediaAsset(
            MediaAssetType.Video,
            1,
            normalized,
            best?.Bitrate ?? 0,
            best?.Width ?? (int)ReadInt64(photo, "width"),
            best?.Height ?? (int)ReadInt64(photo, "height"),
            best?.Codec);
    }

    private static void AddManifestCandidates(
        JsonElement photo,
        string propertyName,
        string codec,
        int priority,
        List<VideoCandidate> target)
    {
        if (!TryGetObject(photo, propertyName, out var manifest)
            || !TryGetArray(manifest, "adaptationSet", out var adaptationSets))
        {
            return;
        }

        foreach (var adaptationSet in adaptationSets.EnumerateArray())
        {
            if (!TryGetArray(adaptationSet, "representation", out var representations))
                continue;

            foreach (var representation in representations.EnumerateArray())
            {
                var bitrate = ReadInt64(representation, "avgBitrate");
                if (bitrate <= 0)
                    bitrate = ReadInt64(representation, "maxBitrate");

                var width = (int)ReadInt64(representation, "width");
                var height = (int)ReadInt64(representation, "height");
                var url = ReadString(representation, "url");
                if (IsHttpUrl(url))
                {
                    target.Add(new VideoCandidate(
                        WebUtility.HtmlDecode(url!),
                        bitrate,
                        width,
                        height,
                        codec,
                        priority));
                }

                if (!TryGetArray(representation, "backupUrl", out var backupUrls))
                    continue;

                foreach (var backup in backupUrls.EnumerateArray())
                {
                    var backupUrl = backup.ValueKind == JsonValueKind.String
                        ? backup.GetString()
                        : ReadString(backup, "url");
                    if (!IsHttpUrl(backupUrl))
                        continue;

                    target.Add(new VideoCandidate(
                        WebUtility.HtmlDecode(backupUrl!),
                        bitrate,
                        width,
                        height,
                        codec,
                        priority));
                }
            }
        }
    }

    private static string? FindDominantAuthorId(JsonElement feeds)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var feed in feeds.EnumerateArray())
        {
            if (!TryGetObject(feed, "author", out var author))
                continue;

            var id = ReadString(author, "id");
            if (string.IsNullOrWhiteSpace(id))
                continue;

            counts.TryGetValue(id, out var count);
            counts[id] = count + 1;
        }

        return counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key)
            .FirstOrDefault();
    }

    private static bool? ReadHasMore(JsonElement root)
    {
        var cursor = ReadString(root, "pcursor");
        if (cursor is null)
            return null;

        return !string.IsNullOrWhiteSpace(cursor)
               && !cursor.Equals("no_more", StringComparison.OrdinalIgnoreCase)
               && !cursor.Equals("nomore", StringComparison.OrdinalIgnoreCase)
               && !cursor.Equals("null", StringComparison.OrdinalIgnoreCase)
               && !cursor.Equals("0", StringComparison.OrdinalIgnoreCase);
    }

    private static long NormalizeTimestamp(long value)
        => value > 9_999_999_999L ? value / 1000 : Math.Max(0, value);

    private static bool IsHttpUrl(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && Uri.TryCreate(WebUtility.HtmlDecode(value), UriKind.Absolute, out var uri)
           && uri.Scheme is "http" or "https";

    private static bool TryGetObject(JsonElement parent, string propertyName, out JsonElement value)
        => TryGetProperty(parent, propertyName, out value) && value.ValueKind == JsonValueKind.Object;

    private static bool TryGetArray(JsonElement parent, string propertyName, out JsonElement value)
        => TryGetProperty(parent, propertyName, out value) && value.ValueKind == JsonValueKind.Array;

    private static bool TryGetProperty(JsonElement parent, string propertyName, out JsonElement value)
    {
        value = default;
        return parent.ValueKind == JsonValueKind.Object
               && parent.TryGetProperty(propertyName, out value);
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

    private sealed record VideoCandidate(
        string Url,
        long Bitrate,
        int Width,
        int Height,
        string Codec,
        int Priority);
}
