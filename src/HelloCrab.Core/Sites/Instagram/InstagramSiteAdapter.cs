using HelloCrab.Core.Services.Localization;
using System.Net;
using System.Text.Json;
using HelloCrab.Core.Models;
using HelloCrab.Core.Services.Browser;
using HelloCrab.Core.Sites;

namespace HelloCrab.Core.Sites.Instagram;

public sealed class InstagramSiteAdapter : ISiteAdapter
{
    private static readonly HashSet<string> ReservedProfileSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "accounts",
        "about",
        "api",
        "developer",
        "direct",
        "explore",
        "graphql",
        "legal",
        "p",
        "privacy",
        "reel",
        "reels",
        "stories",
        "terms",
        "web"
    };

    public string Id => "instagram";
    public string DisplayName => "Instagram";
    public string HomeUrl => "https://www.instagram.com/";

    public bool CanHandlePage(string pageUrl)
        => !string.IsNullOrWhiteSpace(TryReadProfileUsername(pageUrl));

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
            || !IsInstagramHost(uri.Host))
        {
            return false;
        }

        // Instagram 的作者作品列表与其他页面数据共用 GraphQL 入口。
        var isGraphQlEndpoint =
            uri.AbsolutePath.Contains("/graphql/query", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.Contains("/api/graphql", StringComparison.OrdinalIgnoreCase);
        if (!isGraphQlEndpoint)
            return false;

        // 某些请求只有 doc_id/variables，没有 friendly_name，此时只能在解析响应时
        // 通过 xdt_api__v1__feed__user_timeline_graphql_connection 严格确认。
        if (string.IsNullOrWhiteSpace(requestBody))
            return true;

        var decodedBody = WebUtility.UrlDecode(requestBody);
        if (!decodedBody.Contains("fb_api_req_friendly_name", StringComparison.OrdinalIgnoreCase))
            return true;

        // 已知的作者主页首屏与分页操作名。过滤通知、弹窗等其他 GraphQL 请求，
        // 避免它们被错误计入作品页数。
        return decodedBody.Contains("ProfilePosts", StringComparison.OrdinalIgnoreCase)
               || decodedBody.Contains("ProfilePageContent", StringComparison.OrdinalIgnoreCase)
               || decodedBody.Contains("UserTimeline", StringComparison.OrdinalIgnoreCase);
    }

    public ParsedWorkBatch ParseResponse(
        string responseUrl,
        string responseJson,
        string pageUrl,
        string? requestBody)
    {
        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;
        if (!TryGetObject(root, "data", out var data)
            || !TryGetObject(
                data,
                "xdt_api__v1__feed__user_timeline_graphql_connection",
                out var connection))
        {
            // /graphql/query 还承载通知、弹窗等请求；不是作者作品时间线时静默忽略。
            return new ParsedWorkBatch(Array.Empty<WorkItem>(), null, null);
        }

        var expectedUsername = TryReadProfileUsername(pageUrl);
        var works = new List<WorkItem>();
        var rejectedWorkCount = 0;
        if (TryGetArray(connection, "edges", out var edges))
        {
            foreach (var edge in edges.EnumerateArray())
            {
                if (!TryGetObject(edge, "node", out var node))
                    continue;

                var work = ParseWork(node, pageUrl, expectedUsername, out var rejected);
                if (rejected)
                    rejectedWorkCount++;
                if (work is not null)
                    works.Add(work);
            }
        }

        bool? hasMore = null;
        string? cursor = null;
        if (TryGetObject(connection, "page_info", out var pageInfo))
        {
            hasMore = ReadBoolean(pageInfo, "has_next_page");
            cursor = ReadString(pageInfo, "end_cursor");
        }

        var diagnostic = rejectedWorkCount > 0
            ? RuntimeLocalization.Format("Instagram.Filtered", "已过滤 {0} 个非目标 Instagram 作者作品，未加入下载队列。", rejectedWorkCount)
            : null;

        return new ParsedWorkBatch(
            works,
            hasMore,
            cursor,
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
            new[] { "after", "cursor" },
            out nextRequest);

    public async Task ScrollNextAsync(
        IBrowserAutomationService browser,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await browser.EvaluatePageAsync("""
            () => {
                const root = document.scrollingElement || document.documentElement;
                const viewport = window.innerHeight || root.clientHeight || 0;
                const height = Math.max(
                    root.scrollHeight || 0,
                    document.documentElement?.scrollHeight || 0,
                    document.body?.scrollHeight || 0);
                const before = Math.max(window.scrollY || 0, root.scrollTop || 0);
                const posts = document.querySelectorAll(
                    'main a[href*="/p/"], main a[href*="/reel/"]');
                posts[posts.length - 1]?.scrollIntoView({
                    block: 'end',
                    inline: 'nearest',
                    behavior: 'auto'
                });
                const target = Math.max(0, height - viewport);
                window.scrollTo({ top: target, behavior: 'auto' });
                root.scrollTop = target;
                window.dispatchEvent(new Event('scroll'));
                document.dispatchEvent(new Event('scroll', { bubbles: true }));
                return {
                    before,
                    after: Math.max(window.scrollY || 0, root.scrollTop || 0),
                    height,
                    viewport,
                    count: posts.length
                };
            }
            """, cancellationToken);

        await Task.Delay(Random.Shared.Next(800, 1_301), cancellationToken);
    }

    public async Task<PageScrollState> GetScrollStateAsync(
        IBrowserAutomationService browser,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await browser.EvaluatePageAsync("""
            () => {
                const root = document.scrollingElement || document.documentElement;
                return {
                    scrollY: Math.max(window.scrollY || 0, root.scrollTop || 0),
                    viewportHeight: window.innerHeight || root.clientHeight || 0,
                    documentHeight: Math.max(
                        root.scrollHeight || 0,
                        document.documentElement?.scrollHeight || 0,
                        document.body?.scrollHeight || 0),
                    containerName: 'document',
                    workItemCount: document.querySelectorAll(
                        'main a[href*="/p/"], main a[href*="/reel/"]').length
                };
            }
            """, cancellationToken);

        return new PageScrollState(
            ReadDouble(result, "scrollY"),
            ReadDouble(result, "viewportHeight"),
            ReadDouble(result, "documentHeight"),
            ReadString(result, "containerName") ?? "document",
            (int)Math.Clamp(ReadInt64(result, "workItemCount"), 0, int.MaxValue));
    }

    private static WorkItem? ParseWork(
        JsonElement node,
        string pageUrl,
        string? expectedUsername,
        out bool rejected)
    {
        rejected = false;
        if (!TryGetObject(node, "user", out var user)
            && !TryGetObject(node, "owner", out user))
        {
            return null;
        }

        var username = ReadString(user, "username");
        if (!string.IsNullOrWhiteSpace(expectedUsername)
            && !string.Equals(expectedUsername, username, StringComparison.OrdinalIgnoreCase))
        {
            rejected = true;
            return null;
        }

        var workId = ReadString(node, "pk")
                     ?? ReadString(node, "id")
                     ?? ReadString(node, "code");
        var code = ReadString(node, "code");
        if (string.IsNullOrWhiteSpace(workId) || string.IsNullOrWhiteSpace(code))
            return null;

        var authorId = ReadString(user, "pk")
                       ?? ReadString(user, "id")
                       ?? username
                       ?? "unknown-author";
        var authorName = ReadString(user, "full_name")
                         ?? username
                         ?? RuntimeLocalization.Get("Common.UnknownAuthor", "未知作者");
        var authorAvatarUrl = ReadNestedString(user, "hd_profile_pic_url_info", "url")
                              ?? ReadString(user, "profile_pic_url");
        var description = ReadCaptionText(node)
                          ?? ReadString(node, "title")
                          ?? ReadString(node, "headline")
                          ?? string.Empty;
        var createTime = ReadInt64(node, "taken_at");
        var mediaType = ReadInt32(node, "media_type");
        var assets = ParsePrimaryAssets(node, mediaType);
        if (!assets.Any(asset => asset.Type is MediaAssetType.Video or MediaAssetType.Image))
            return null;

        // image_versions2.candidates 是同一张图片/视频封面的多种尺寸，不是图集。
        // 真正的图集位于 carousel_media，且可能混合图片和视频。
        if ((mediaType == 2 || mediaType == 8 || assets.Any(x => x.Type == MediaAssetType.Video))
            && ParseImageAsset(node, 0, MediaAssetType.Cover) is { } cover)
        {
            assets.Add(cover);
        }

        var productType = ReadString(node, "product_type");
        var sourceKind = string.Equals(productType, "clips", StringComparison.OrdinalIgnoreCase)
                         || mediaType == 2
            ? "reel"
            : "p";
        var sourceUrl = $"https://www.instagram.com/{sourceKind}/{Uri.EscapeDataString(code)}/";
        var authorPageUrl = !string.IsNullOrWhiteSpace(username)
            ? $"https://www.instagram.com/{Uri.EscapeDataString(username)}/"
            : pageUrl;

        return new WorkItem(
            "instagram",
            workId,
            authorId,
            authorName,
            authorAvatarUrl,
            description,
            createTime,
            assets,
            sourceUrl)
        {
            AuthorPageUrl = authorPageUrl,
            MediaRefererUrl = sourceUrl
        };
    }

    private static List<MediaAsset> ParsePrimaryAssets(JsonElement node, int mediaType)
    {
        var assets = new List<MediaAsset>();
        if (TryGetArray(node, "carousel_media", out var carouselMedia)
            && carouselMedia.GetArrayLength() > 0)
        {
            var index = 1;
            foreach (var child in carouselMedia.EnumerateArray())
            {
                var childMediaType = ReadInt32(child, "media_type");
                var asset = ParsePrimaryAsset(child, childMediaType, index);
                if (asset is not null)
                    assets.Add(asset);
                index++;
            }

            return assets;
        }

        var primary = ParsePrimaryAsset(node, mediaType, 1);
        if (primary is not null)
            assets.Add(primary);
        return assets;
    }

    private static MediaAsset? ParsePrimaryAsset(JsonElement media, int mediaType, int index)
    {
        // node.media_type 是作品媒体类型：1=图片、2=视频、8=轮播图集。
        // 仍以字段是否实际存在作为兜底，以兼容 Instagram 调整枚举值或返回不完整数据。
        if (mediaType == 2 || HasNonEmptyArray(media, "video_versions"))
            return ParseVideoAsset(media, index);

        return ParseImageAsset(media, index, MediaAssetType.Image);
    }

    private static MediaAsset? ParseVideoAsset(JsonElement media, int index)
    {
        if (!TryGetArray(media, "video_versions", out var videoVersions))
            return null;

        var candidates = new List<Rendition>();
        foreach (var item in videoVersions.EnumerateArray())
        {
            var url = ReadString(item, "url");
            if (string.IsNullOrWhiteSpace(url))
                continue;

            candidates.Add(new Rendition(
                url,
                ReadInt32(item, "width"),
                ReadInt32(item, "height")));
        }

        // video_versions.type（样例中的 101/102/103）不是作品媒体类型，
        // 公开结构也没有稳定的质量等级定义。样例中这些 type 甚至指向相同 URL，
        // 因此只按像素面积选择，并保留其余不同 URL 作为失败回退。
        var ordered = candidates
            .OrderByDescending(item => (long)item.Width * item.Height)
            .ThenByDescending(item => item.Width)
            .ThenByDescending(item => item.Height)
            .ToArray();
        if (ordered.Length == 0)
            return null;

        var urls = ordered
            .Select(item => item.Url)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new MediaAsset(
            MediaAssetType.Video,
            index,
            urls,
            Width: ordered[0].Width,
            Height: ordered[0].Height);
    }

    private static MediaAsset? ParseImageAsset(
        JsonElement media,
        int index,
        MediaAssetType assetType)
    {
        if (!TryGetObject(media, "image_versions2", out var imageVersions)
            || !TryGetArray(imageVersions, "candidates", out var imageCandidates))
        {
            return null;
        }

        var candidates = new List<Rendition>();
        foreach (var item in imageCandidates.EnumerateArray())
        {
            var url = ReadString(item, "url");
            if (string.IsNullOrWhiteSpace(url))
                continue;

            candidates.Add(new Rendition(
                url,
                ReadInt32(item, "width"),
                ReadInt32(item, "height")));
        }

        var ordered = candidates
            .OrderByDescending(item => (long)item.Width * item.Height)
            .ThenByDescending(item => item.Width)
            .ThenByDescending(item => item.Height)
            .ToArray();
        if (ordered.Length == 0)
            return null;

        var urls = ordered
            .Select(item => item.Url)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new MediaAsset(
            assetType,
            index,
            urls,
            Width: ordered[0].Width,
            Height: ordered[0].Height);
    }

    private static bool IsInstagramHost(string host)
        => host.Equals("instagram.com", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".instagram.com", StringComparison.OrdinalIgnoreCase);

    private static string? TryReadProfileUsername(string pageUrl)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri)
            || !IsInstagramHost(uri.Host))
        {
            return null;
        }

        var segment = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(segment)
            || ReservedProfileSegments.Contains(segment))
        {
            return null;
        }

        return Uri.UnescapeDataString(segment).TrimStart('@').Trim();
    }

    private static string? ReadCaptionText(JsonElement node)
    {
        if (!node.TryGetProperty("caption", out var caption))
            return null;

        if (caption.ValueKind == JsonValueKind.String)
            return NormalizeText(caption.GetString());
        if (caption.ValueKind == JsonValueKind.Object)
            return NormalizeText(ReadString(caption, "text"));
        return null;
    }

    private static string? NormalizeText(string? value)
    {
        var text = value?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static bool HasNonEmptyArray(JsonElement element, string propertyName)
        => TryGetArray(element, propertyName, out var array) && array.GetArrayLength() > 0;

    private static bool TryGetObject(JsonElement element, string propertyName, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(propertyName, out value)
               && value.ValueKind == JsonValueKind.Object;
    }

    private static bool TryGetArray(JsonElement element, string propertyName, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(propertyName, out value)
               && value.ValueKind == JsonValueKind.Array;
    }

    private static string? ReadNestedString(
        JsonElement element,
        string objectName,
        string propertyName)
        => TryGetObject(element, objectName, out var nested)
            ? ReadString(nested, propertyName)
            : null;

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => NormalizeText(value.GetString()),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static int ReadInt32(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        return value.ValueKind == JsonValueKind.String
               && int.TryParse(value.GetString(), out number)
            ? number
            : 0;
    }

    private static long ReadInt64(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;
        return value.ValueKind == JsonValueKind.String
               && long.TryParse(value.GetString(), out number)
            ? number
            : 0;
    }

    private static bool? ReadBoolean(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt32(out var number) => number != 0,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var result) => result,
            _ => null
        };
    }

    private static double ReadDouble(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            return number;
        return value.ValueKind == JsonValueKind.String
               && double.TryParse(value.GetString(), out number)
            ? number
            : 0;
    }

    private sealed record Rendition(string Url, int Width, int Height);
}
