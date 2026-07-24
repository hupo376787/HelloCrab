using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using HelloCrab.Core.Models;
using HelloCrab.Core.Services.Browser;

namespace HelloCrab.Core.Sites.Meipian;

/// <summary>
/// 美篇网页版作者专栏适配器。
///
/// 作者主页 /c/{userid} 的首批文章直接位于 articlecontent 中；继续滚动后，
/// load_columns_article.php 以 JSON 数组返回后续文章。文章详情页中的
/// var ARTICLE_DETAIL 保存作者、标题、发布时间、封面、背景音乐和图集原图地址。
/// </summary>
public sealed class MeipianSiteAdapter : ISiteAdapter
{
    private const string ArticleDetailMarker = "var ARTICLE_DETAIL";

    private static readonly Regex ArticleContentStartRegex = new(
        "<div\\s+[^>]*class\\s*=\\s*[\\\"'][^\\\"']*\\barticlecontent\\b[^\\\"']*[\\\"'][^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ArticleItemRegex = new(
        "<div\\s+[^>]*data-id\\s*=\\s*[\\\"'](?<numericId>\\d+)[\\\"'][^>]*>" +
        ".*?<h3[^>]*>\\s*<a[^>]+href\\s*=\\s*[\\\"']" +
        "(?<url>(?:https?:)?//(?:www\\.)?meipian\\.cn/(?<mask>[A-Za-z0-9]+)(?:\\?[^\\\"']*)?|/(?<relativeMask>[A-Za-z0-9]+)(?:\\?[^\\\"']*)?)" +
        "[\\\"'][^>]*>(?<title>.*?)</a>\\s*</h3>" +
        "\\s*<p[^>]*>(?<abstract>.*?)</p>" +
        "\\s*<p[^>]*>(?<date>\\d{4}-\\d{1,2}-\\d{1,2})</p>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex AuthorNameRegex = new(
        "<div\\s+[^>]*class\\s*=\\s*[\\\"'][^\\\"']*\\binfo\\b[^\\\"']*[\\\"'][^>]*>\\s*<h2[^>]*>(?<name>.*?)</h2>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex AuthorAvatarRegex = new(
        "class\\s*=\\s*[\\\"'][^\\\"']*\\bheaderimg\\b[^\\\"']*[\\\"'][^>]*background-image\\s*:\\s*url\\(\\s*[\\\"']?(?<url>[^)\\\"']+)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private readonly ConcurrentDictionary<string, ProfileInfo> _profiles =
        new(StringComparer.Ordinal);

    public string Id => "meipian";
    public string DisplayName => "美篇网页版";
    public string HomeUrl => "https://www.meipian.cn/";

    public bool CanHandlePage(string pageUrl)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri))
            return false;

        return IsMeipianHost(uri.Host)
               && TryReadProfileUserId(uri) is not null;
    }

    public bool IsTargetResponse(
        string responseUrl,
        string resourceType,
        int statusCode,
        string? requestBody)
    {
        if (statusCode is < 200 or >= 300
            || !Uri.TryCreate(responseUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (IsProfileDocument(uri))
            return resourceType.Equals("document", StringComparison.OrdinalIgnoreCase);

        return IsArticlePagingApi(uri)
               && (resourceType.Equals("xhr", StringComparison.OrdinalIgnoreCase)
                   || resourceType.Equals("fetch", StringComparison.OrdinalIgnoreCase));
    }

    public ParsedWorkBatch ParseResponse(
        string responseUrl,
        string responseJson,
        string pageUrl,
        string? requestBody)
    {
        if (!Uri.TryCreate(responseUrl, UriKind.Absolute, out var uri))
            return new ParsedWorkBatch(Array.Empty<WorkItem>(), null, null);

        return IsArticlePagingApi(uri)
            ? ParsePagingResponse(responseJson, responseUrl, pageUrl)
            : ParseProfileDocument(responseJson, pageUrl);
    }

    public async Task<WorkItem?> ResolveWorkAsync(
        WorkItem work,
        IBrowserAutomationService browser,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(work.SourceUrl, UriKind.Absolute, out var sourceUri)
            || !IsArticleDocument(sourceUri))
        {
            return work;
        }

        var html = await browser.FetchTextAsync(work.SourceUrl, cancellationToken);
        if (string.IsNullOrWhiteSpace(html))
            throw new InvalidOperationException("美篇文章详情返回了空文档。");

        var articleDetailJson = ExtractArticleDetailJson(html);
        using var document = JsonDocument.Parse(articleDetailJson);
        var root = document.RootElement;
        if (!TryGetObject(root, "article", out var article))
            throw new InvalidOperationException("ARTICLE_DETAIL 中没有 article 数据。");

        var detailWorkId = ReadFirstString(article, "mask_id", "maskId") ?? work.WorkId;
        if (!string.Equals(detailWorkId, work.WorkId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("文章详情标识与列表标识不一致。");

        var author = TryGetObject(root, "author", out var authorElement)
            ? authorElement
            : default;
        var authorId = ReadFirstString(author, "id", "user_id", "userId")
                       ?? ReadFirstString(article, "user_id", "userId")
                       ?? work.AuthorId;
        if (!SameId(authorId, work.AuthorId))
            return null;

        var authorName = ReadFirstString(author, "nickname", "plainNickname", "name")
                         ?? work.AuthorName;
        var authorAvatar = NormalizeUrl(ReadFirstString(
                               author,
                               "head_img_url",
                               "headImgUrl",
                               "avatar"))
                           ?? work.AuthorAvatarUrl;
        var title = ReadFirstString(article, "title") ?? work.Description;
        var createTime = NormalizeTimestamp(ReadFirstInt64(
            article,
            "create_time",
            "createTime",
            "first_share_time"));
        if (createTime <= 0)
            createTime = work.CreateTime;

        var assets = new List<MediaAsset>();
        var seenImages = new HashSet<string>(StringComparer.Ordinal);
        if (TryReadContentArray(root, article, out var content))
        {
            var imageIndex = 1;
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || ReadFirstInt64(item, "img_del", "image_del") != 0)
                {
                    continue;
                }

                var imageUrl = NormalizeUrl(ReadFirstString(
                    item,
                    "img_url",
                    "image_url",
                    "imageUrl"));
                if (string.IsNullOrWhiteSpace(imageUrl) || !seenImages.Add(imageUrl))
                    continue;

                assets.Add(new MediaAsset(
                    MediaAssetType.Image,
                    imageIndex++,
                    new[] { imageUrl },
                    Width: (int)Math.Clamp(ReadFirstInt64(item, "img_width", "width"), 0, int.MaxValue),
                    Height: (int)Math.Clamp(ReadFirstInt64(item, "img_height", "height"), 0, int.MaxValue)));
            }
        }

        // 少数纯视频文章没有 img_url。仅在没有图集时读取明确的视频地址，
        // 避免把缩略图、跳转链接或统计接口误当作视频资源。
        if (!assets.Any(x => x.Type == MediaAssetType.Image))
        {
            var videoUrls = new List<string>();
            CollectNamedMediaUrls(root, "video", videoUrls, maxDepth: 7);
            var candidates = videoUrls
                .Select(NormalizeUrl)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (candidates.Length > 0)
                assets.Add(new MediaAsset(MediaAssetType.Video, 1, candidates));
        }

        var coverUrl = NormalizeUrl(ReadFirstString(
            article,
            "cover_img_url",
            "cover_thumb",
            "cover"));
        if (!string.IsNullOrWhiteSpace(coverUrl))
            assets.Add(new MediaAsset(MediaAssetType.Cover, 0, new[] { coverUrl }));

        var musicUrl = NormalizeUrl(ReadFirstString(article, "music_url", "musicUrl"));
        if (!string.IsNullOrWhiteSpace(musicUrl))
            assets.Add(new MediaAsset(MediaAssetType.Music, 0, new[] { musicUrl }));

        if (!assets.Any(x => x.Type is MediaAssetType.Image or MediaAssetType.Video))
            return null;

        _profiles[authorId] = new ProfileInfo(authorName, authorAvatar);
        var canonicalSourceUrl = $"https://www.meipian.cn/{Uri.EscapeDataString(detailWorkId)}";
        return work with
        {
            AuthorId = authorId,
            AuthorName = authorName,
            AuthorAvatarUrl = authorAvatar,
            Description = title,
            CreateTime = createTime,
            Assets = assets,
            SourceUrl = canonicalSourceUrl,
            AuthorPageUrl = $"https://www.meipian.cn/c/{Uri.EscapeDataString(authorId)}",
            MediaRefererUrl = canonicalSourceUrl
        };
    }

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
                const last = document.querySelector('.articlecontent li.sel > div[data-id]:last-child');
                last?.scrollIntoView({ block: 'end', inline: 'nearest', behavior: 'auto' });
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
                    count: document.querySelectorAll('.articlecontent li.sel > div[data-id]').length
                };
            }
            """, cancellationToken);

        await Task.Delay(Random.Shared.Next(900, 1_501), cancellationToken);
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
                    workItemCount: document.querySelectorAll('.articlecontent li.sel > div[data-id]').length
                };
            }
            """, cancellationToken);

        return new PageScrollState(
            ReadDouble(result, "scrollY"),
            ReadDouble(result, "viewportHeight"),
            ReadDouble(result, "documentHeight"),
            ReadFirstString(result, "containerName") ?? "document",
            (int)Math.Clamp(ReadFirstInt64(result, "workItemCount"), 0, int.MaxValue));
    }

    private ParsedWorkBatch ParseProfileDocument(string html, string pageUrl)
    {
        var userId = TryReadProfileUserId(pageUrl);
        if (string.IsNullOrWhiteSpace(userId))
            return new ParsedWorkBatch(Array.Empty<WorkItem>(), null, null);

        var authorNameMatch = AuthorNameRegex.Match(html);
        var authorName = authorNameMatch.Success
            ? DecodeText(authorNameMatch.Groups["name"].Value)
            : string.Empty;
        if (string.IsNullOrWhiteSpace(authorName))
            authorName = $"美篇用户 {userId}";

        var avatarMatch = AuthorAvatarRegex.Match(html);
        var authorAvatar = avatarMatch.Success
            ? NormalizeUrl(avatarMatch.Groups["url"].Value)
            : null;
        _profiles[userId] = new ProfileInfo(authorName, authorAvatar);

        var contentMatch = ArticleContentStartRegex.Match(html);
        if (!contentMatch.Success)
        {
            return new ParsedWorkBatch(
                Array.Empty<WorkItem>(),
                null,
                null,
                "美篇作者页中没有找到 div.articlecontent。");
        }

        var sectionEnd = html.IndexOf("noarticle", contentMatch.Index, StringComparison.OrdinalIgnoreCase);
        var section = sectionEnd > contentMatch.Index
            ? html[contentMatch.Index..sectionEnd]
            : html[contentMatch.Index..];

        var works = new List<WorkItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? cursor = null;
        foreach (Match match in ArticleItemRegex.Matches(section))
        {
            var maskId = match.Groups["mask"].Success
                ? match.Groups["mask"].Value
                : match.Groups["relativeMask"].Value;
            if (string.IsNullOrWhiteSpace(maskId) || !seen.Add(maskId))
                continue;

            cursor = match.Groups["numericId"].Value;
            var title = DecodeText(match.Groups["title"].Value);
            var description = string.IsNullOrWhiteSpace(title)
                ? DecodeText(match.Groups["abstract"].Value)
                : title;
            var sourceUrl = $"https://www.meipian.cn/{Uri.EscapeDataString(maskId)}";
            works.Add(new WorkItem(
                Id,
                maskId,
                userId,
                authorName,
                authorAvatar,
                string.IsNullOrWhiteSpace(description) ? "无标题" : description,
                ParseDateTimestamp(match.Groups["date"].Value),
                Array.Empty<MediaAsset>(),
                sourceUrl)
            {
                AuthorPageUrl = pageUrl,
                MediaRefererUrl = sourceUrl
            });
        }

        bool? hasMore = works.Count == 0 ? null : works.Count >= 10;
        return new ParsedWorkBatch(
            works,
            hasMore,
            cursor,
            $"美篇首屏发现 {works.Count} 篇文章，将逐篇读取 ARTICLE_DETAIL 图集数据。");
    }

    private ParsedWorkBatch ParsePagingResponse(string json, string responseUrl, string pageUrl)
    {
        var normalizedJson = json.Trim();
        if (normalizedJson.Length > 1 && normalizedJson[0] == '"')
            normalizedJson = JsonSerializer.Deserialize<string>(normalizedJson) ?? "[]";

        using var document = JsonDocument.Parse(normalizedJson);
        var root = document.RootElement;
        JsonElement list;
        if (root.ValueKind == JsonValueKind.Array)
        {
            list = root;
        }
        else if (TryGetArray(root, "data", out var data))
        {
            list = data;
        }
        else if (TryGetArray(root, "list", out var listElement))
        {
            list = listElement;
        }
        else
        {
            return new ParsedWorkBatch(Array.Empty<WorkItem>(), false, null);
        }

        var userId = TryReadProfileUserId(pageUrl)
                     ?? TryReadQueryValue(responseUrl, "userid");
        if (string.IsNullOrWhiteSpace(userId))
            return new ParsedWorkBatch(Array.Empty<WorkItem>(), null, null);

        var profile = _profiles.TryGetValue(userId, out var cached)
            ? cached
            : new ProfileInfo($"美篇用户 {userId}", null);
        var authorPageUrl = $"https://www.meipian.cn/c/{Uri.EscapeDataString(userId)}";
        var works = new List<WorkItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? cursor = null;
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var maskId = ReadFirstString(item, "mask_id", "maskId");
            if (string.IsNullOrWhiteSpace(maskId) || !seen.Add(maskId))
                continue;

            cursor = ReadFirstString(item, "id") ?? cursor;
            var title = ReadFirstString(item, "title")
                        ?? ReadFirstString(item, "abstract")
                        ?? "无标题";
            var sourceUrl = $"https://www.meipian.cn/{Uri.EscapeDataString(maskId)}";
            var assets = new List<MediaAsset>();
            var coverUrl = NormalizeUrl(ReadFirstString(
                item,
                "cover_img_url",
                "coverImgUrl",
                "cover"));
            if (!string.IsNullOrWhiteSpace(coverUrl))
                assets.Add(new MediaAsset(MediaAssetType.Cover, 0, new[] { coverUrl }));

            works.Add(new WorkItem(
                Id,
                maskId,
                userId,
                profile.Name,
                profile.AvatarUrl,
                title,
                NormalizeTimestamp(ReadFirstInt64(item, "create_time", "createTime")),
                assets,
                sourceUrl)
            {
                AuthorPageUrl = authorPageUrl,
                MediaRefererUrl = sourceUrl
            });
        }

        var hasMore = list.GetArrayLength() >= 10;
        return new ParsedWorkBatch(
            works,
            hasMore,
            cursor,
            works.Count == 0
                ? "美篇分页接口已返回空列表。"
                : $"美篇分页返回 {works.Count} 篇文章。");
    }

    private static string ExtractArticleDetailJson(string html)
    {
        var markerIndex = html.IndexOf(ArticleDetailMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
            throw new InvalidOperationException("详情文档中没有找到 var ARTICLE_DETAIL。");

        var assignmentIndex = html.IndexOf('=', markerIndex + ArticleDetailMarker.Length);
        var objectStart = assignmentIndex < 0 ? -1 : html.IndexOf('{', assignmentIndex + 1);
        if (objectStart < 0)
            throw new InvalidOperationException("ARTICLE_DETAIL 没有有效的对象起始位置。");

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = objectStart; index < html.Length; index++)
        {
            var ch = html[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
            }
            else if (ch == '{')
            {
                depth++;
            }
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                    return html.Substring(objectStart, index - objectStart + 1);
            }
        }

        throw new InvalidOperationException("ARTICLE_DETAIL 对象没有正常结束。");
    }

    private static bool TryReadContentArray(
        JsonElement root,
        JsonElement article,
        out JsonElement content)
    {
        if (TryGetArray(root, "content", out content))
            return true;

        if (TryGetObject(article, "content", out var articleContent)
            && TryGetArray(articleContent, "content", out content))
        {
            return true;
        }

        content = default;
        return false;
    }

    private static void CollectNamedMediaUrls(
        JsonElement element,
        string nameToken,
        ICollection<string> output,
        int maxDepth,
        int depth = 0)
    {
        if (depth > maxDepth)
            return;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var name = property.Name;
                if (name.Contains(nameToken, StringComparison.OrdinalIgnoreCase)
                    && name.Contains("url", StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        var value = property.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(value)
                            && !name.Contains("thumbnail", StringComparison.OrdinalIgnoreCase)
                            && !name.Contains("cover", StringComparison.OrdinalIgnoreCase))
                        {
                            output.Add(value);
                        }
                    }
                    else if (property.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var child in property.Value.EnumerateArray())
                        {
                            if (child.ValueKind == JsonValueKind.String
                                && !string.IsNullOrWhiteSpace(child.GetString()))
                            {
                                output.Add(child.GetString()!);
                            }
                        }
                    }
                }

                CollectNamedMediaUrls(property.Value, nameToken, output, maxDepth, depth + 1);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                CollectNamedMediaUrls(child, nameToken, output, maxDepth, depth + 1);
        }
    }

    private static string? TryReadProfileUserId(string pageUrl)
        => Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri)
            ? TryReadProfileUserId(uri)
            : null;

    private static string? TryReadProfileUserId(Uri uri)
    {
        if (!IsMeipianHost(uri.Host))
            return null;

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length == 2
               && segments[0].Equals("c", StringComparison.OrdinalIgnoreCase)
               && segments[1].All(char.IsDigit)
            ? segments[1]
            : null;
    }

    private static string? TryReadQueryValue(string url, string name)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        foreach (var part in uri.Query.TrimStart('?')
                     .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0].Equals(name, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(pair[1]);
        }

        return null;
    }

    private static bool IsProfileDocument(Uri uri)
        => TryReadProfileUserId(uri) is not null;

    private static bool IsArticleDocument(Uri uri)
    {
        if (!IsMeipianHost(uri.Host))
            return false;

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length == 1
               && segments[0].Length >= 4
               && segments[0].All(char.IsLetterOrDigit);
    }

    private static bool IsArticlePagingApi(Uri uri)
        => IsMeipianHost(uri.Host)
           && uri.AbsolutePath.Equals(
               "/static/action/load_columns_article.php",
               StringComparison.OrdinalIgnoreCase);

    private static bool IsMeipianHost(string host)
        => host.Equals("meipian.cn", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".meipian.cn", StringComparison.OrdinalIgnoreCase);

    private static string DecodeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var decoded = WebUtility.HtmlDecode(value);
        decoded = Regex.Replace(decoded, "<[^>]+>", " ");
        decoded = Regex.Replace(decoded.Replace('\u00A0', ' '), "\\s+", " ");
        return decoded.Trim();
    }

    private static string? NormalizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var url = WebUtility.HtmlDecode(value).Trim().Trim('"', '\'');
        if (url.StartsWith("//", StringComparison.Ordinal))
            return "https:" + url;
        if (url.StartsWith("/", StringComparison.Ordinal))
            return "https://www.meipian.cn" + url;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.ToString()
            : null;
    }

    private static long ParseDateTimestamp(string value)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                new[] { "yyyy-M-d", "yyyy-MM-dd" },
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var date))
        {
            return 0;
        }

        return date.ToUnixTimeSeconds();
    }

    private static long NormalizeTimestamp(long value)
    {
        if (value <= 0)
            return 0;

        while (value > 99_999_999_999)
            value /= 1000;
        return value;
    }

    private static bool SameId(string left, string right)
        => string.Equals(left.Trim(), right.Trim(), StringComparison.Ordinal);

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
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
            else if (value.ValueKind == JsonValueKind.Number)
            {
                return value.GetRawText();
            }
        }

        return null;
    }

    private static long ReadFirstInt64(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return 0;

        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
                return number;
            if (value.ValueKind == JsonValueKind.String
                && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            {
                return number;
            }
        }

        return 0;
    }

    private static double ReadDouble(JsonElement element, string name, double fallback = 0)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return fallback;
    }

    private sealed record ProfileInfo(string Name, string? AvatarUrl);
}
