using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using HelloCrab.Core.Models;
using HelloCrab.Core.Services.Browser;

namespace HelloCrab.Core.Sites.X;

/// <summary>
/// X（原 Twitter）作者主页适配器。
/// 捕获登录浏览器中的作者时间线 GraphQL 响应，不依赖固定 queryId。
/// </summary>
public sealed partial class XSiteAdapter : ISiteAdapter
{
    private static readonly HashSet<string> ReservedRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "account", "compose", "connect_people", "download", "explore", "hashtag",
        "home", "i", "intent", "jobs", "lists", "login", "messages", "notifications",
        "premium", "privacy", "search", "settings", "share", "signup", "tos", "verified-orgs"
    };

    public string Id => "x";
    public string DisplayName => "X (Twitter)";
    public string HomeUrl => "https://x.com/";

    public bool CanHandlePage(string pageUrl)
        => TryGetProfileName(pageUrl) is not null;

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
            || !IsXHost(uri.Host)
            || !uri.AbsolutePath.Contains("/graphql/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var text = WebUtility.UrlDecode(responseUrl + " " + requestBody);
        return text.Contains("UserTweets", StringComparison.OrdinalIgnoreCase)
               || text.Contains("UserMedia", StringComparison.OrdinalIgnoreCase)
               || text.Contains("UserTweetsAndReplies", StringComparison.OrdinalIgnoreCase)
               || (text.Contains("userId", StringComparison.OrdinalIgnoreCase)
                   && (text.Contains("cursor", StringComparison.OrdinalIgnoreCase)
                       || text.Contains("count", StringComparison.OrdinalIgnoreCase)));
    }

    public ParsedWorkBatch ParseResponse(
        string responseUrl,
        string responseJson,
        string pageUrl,
        string? requestBody)
    {
        using var document = JsonDocument.Parse(responseJson);
        var expectedName = TryGetProfileName(pageUrl);
        var rawTweets = new List<JsonElement>();
        string? bottomCursor = null;
        var foundTimeline = false;

        CollectTimeline(document.RootElement, rawTweets, ref bottomCursor, ref foundTimeline);
        if (!foundTimeline)
            return new ParsedWorkBatch(Array.Empty<WorkItem>(), null, null);

        var works = new List<WorkItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rejected = 0;

        foreach (var raw in rawTweets)
        {
            var tweet = UnwrapTweet(raw);
            if (tweet is null)
                continue;

            var work = ParseTweet(tweet.Value, expectedName, out var filtered);
            if (filtered)
                rejected++;
            if (work is not null && seen.Add(work.WorkId))
                works.Add(work);
        }

        var diagnostic = $"X 本页解析到 {works.Count} 个媒体作品。";
        if (rejected > 0)
            diagnostic += $" 已过滤 {rejected} 个转帖、推广内容或非目标作者作品。";

        return new ParsedWorkBatch(
            works,
            bottomCursor is not null,
            bottomCursor,
            diagnostic,
            rejected);
    }

    public async Task ScrollNextAsync(
        IBrowserAutomationService browser,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await browser.EvaluatePageAsync("""
            () => {
                const root = document.scrollingElement || document.documentElement;
                const viewport = window.innerHeight || root.clientHeight || 800;
                const before = Math.max(window.scrollY || 0, root.scrollTop || 0);
                const height = Math.max(root.scrollHeight || 0,
                    document.documentElement?.scrollHeight || 0,
                    document.body?.scrollHeight || 0);
                const max = Math.max(0, height - viewport);
                const target = Math.min(max, before + Math.max(650, viewport * 0.82));
                window.scrollTo({ top: target, behavior: 'auto' });
                root.scrollTop = target;
                return {
                    x: Math.max(1, Math.round(window.innerWidth * 0.72)),
                    y: Math.max(1, Math.round(window.innerHeight * 0.72)),
                    delta: Math.max(520, Math.round(viewport * 0.72)),
                    before
                };
            }
            """, cancellationToken);

        var x = ReadDouble(result, "x", 1);
        var y = ReadDouble(result, "y", 1);
        var delta = ReadDouble(result, "delta", 700);
        var before = ReadDouble(result, "before");

        await SetInputPassThroughAsync(browser, true, cancellationToken);
        try
        {
            await browser.MoveMouseAsync(x, y, cancellationToken);
            await browser.WheelAsync(0, delta, cancellationToken);
            await Task.Delay(350, cancellationToken);
            var after = await GetScrollStateAsync(browser, cancellationToken);
            if (after.ScrollY <= before + 5)
                await browser.PressKeyAsync("PageDown", cancellationToken);
        }
        finally
        {
            await SetInputPassThroughAsync(browser, false, CancellationToken.None);
        }

        // 保持温和的页面操作频率，避免短时间密集请求。
        await Task.Delay(Random.Shared.Next(1_400, 2_301), cancellationToken);
    }

    public async Task<PageScrollState> GetScrollStateAsync(
        IBrowserAutomationService browser,
        CancellationToken cancellationToken)
    {
        var result = await browser.EvaluatePageAsync("""
            () => {
                const root = document.scrollingElement || document.documentElement;
                return {
                    scrollY: Math.max(window.scrollY || 0, root.scrollTop || 0),
                    viewportHeight: window.innerHeight || root.clientHeight || 0,
                    documentHeight: Math.max(root.scrollHeight || 0,
                        document.documentElement?.scrollHeight || 0,
                        document.body?.scrollHeight || 0),
                    workItemCount: document.querySelectorAll(
                        'article[data-testid="tweet"], a[href*="/status/"]').length
                };
            }
            """, cancellationToken);

        return new PageScrollState(
            ReadDouble(result, "scrollY"),
            ReadDouble(result, "viewportHeight"),
            ReadDouble(result, "documentHeight"),
            "document",
            (int)Math.Clamp(ReadInt64(result, "workItemCount"), 0, int.MaxValue));
    }

    private static WorkItem? ParseTweet(
        JsonElement tweet,
        string? expectedName,
        out bool filtered)
    {
        filtered = false;
        if (!TryObject(tweet, "legacy", out var legacy))
            return null;

        var text = ReadNestedString(tweet, "note_tweet", "note_tweet_results", "result", "text")
                   ?? ReadString(legacy, "full_text")
                   ?? ReadString(legacy, "text")
                   ?? string.Empty;

        if (tweet.TryGetProperty("retweeted_status_result", out _)
            || legacy.TryGetProperty("retweeted_status_result", out _)
            || text.StartsWith("RT @", StringComparison.OrdinalIgnoreCase))
        {
            filtered = true;
            return null;
        }

        var user = ReadUser(tweet);
        if (user is null || !TryObject(user.Value, "legacy", out var userLegacy))
            return null;

        var screenName = ReadString(userLegacy, "screen_name");
        if (string.IsNullOrWhiteSpace(screenName))
            return null;

        if (!string.IsNullOrWhiteSpace(expectedName)
            && !screenName.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
        {
            filtered = true;
            return null;
        }

        var workId = ReadString(tweet, "rest_id")
                     ?? ReadString(legacy, "id_str")
                     ?? ReadString(legacy, "id");
        if (string.IsNullOrWhiteSpace(workId))
            return null;

        var assets = ParseAssets(tweet, legacy);
        if (assets.Count == 0)
            return null;

        var authorId = ReadString(user.Value, "rest_id")
                       ?? ReadString(userLegacy, "id_str")
                       ?? screenName;
        var authorName = ReadString(userLegacy, "name") ?? screenName;
        var avatar = NormalizeAvatar(
            ReadString(userLegacy, "profile_image_url_https")
            ?? ReadString(userLegacy, "profile_image_url"));
        var sourceUrl = $"https://x.com/{Uri.EscapeDataString(screenName)}/status/{workId}";

        return new WorkItem(
            "x",
            workId,
            authorId,
            authorName,
            avatar,
            text,
            ParseCreatedAt(ReadString(legacy, "created_at")),
            assets,
            sourceUrl)
        {
            AuthorPageUrl = $"https://x.com/{Uri.EscapeDataString(screenName)}",
            MediaRefererUrl = sourceUrl
        };
    }

    private static List<MediaAsset> ParseAssets(JsonElement tweet, JsonElement legacy)
    {
        JsonElement media;
        if (TryObject(legacy, "extended_entities", out var extended)
            && TryArray(extended, "media", out var extendedMedia))
        {
            media = extendedMedia;
        }
        else if (TryObject(legacy, "entities", out var entities)
                 && TryArray(entities, "media", out var entityMedia))
        {
            media = entityMedia;
        }
        else
        {
            media = default;
        }

        var assets = new List<MediaAsset>();
        if (media.ValueKind == JsonValueKind.Array)
        {
            var index = 1;
            foreach (var item in media.EnumerateArray())
            {
                var type = ReadString(item, "type") ?? string.Empty;
                var width = ReadNestedInt(item, "original_info", "width");
                var height = ReadNestedInt(item, "original_info", "height");
                var image = ReadString(item, "media_url_https") ?? ReadString(item, "media_url");

                if (type.Equals("photo", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(image))
                {
                    assets.Add(new MediaAsset(
                        MediaAssetType.Image,
                        index,
                        DistinctUrls(ToOriginalImage(image), image),
                        Width: width,
                        Height: height));
                }
                else if (type.Equals("video", StringComparison.OrdinalIgnoreCase)
                         || type.Equals("animated_gif", StringComparison.OrdinalIgnoreCase))
                {
                    var video = ParseVideo(item, index, width, height);
                    if (video is not null)
                        assets.Add(video);
                    if (!string.IsNullOrWhiteSpace(image))
                    {
                        assets.Add(new MediaAsset(
                            MediaAssetType.Cover,
                            index,
                            DistinctUrls(ToOriginalImage(image), image),
                            Width: width,
                            Height: height));
                    }
                }
                index++;
            }
        }

        // 少量播放器卡片不带 extended_entities，递归收集卡片里的 video.twimg.com MP4。
        if (!assets.Any(asset => asset.Type is MediaAssetType.Video or MediaAssetType.Image))
        {
            var cardUrls = new HashSet<string>(StringComparer.Ordinal);
            CollectCardVideos(tweet, cardUrls, 0);
            var ordered = cardUrls
                .Select(url => new VideoCandidate(url, 0, ParseDimensions(url).Width, ParseDimensions(url).Height))
                .OrderByDescending(item => (long)item.Width * item.Height)
                .ToArray();
            if (ordered.Length > 0)
            {
                assets.Add(new MediaAsset(
                    MediaAssetType.Video,
                    1,
                    ordered.Select(item => item.Url).ToArray(),
                    Width: ordered[0].Width,
                    Height: ordered[0].Height,
                    Codec: "h264"));
            }
        }

        return assets;
    }

    private static MediaAsset? ParseVideo(
        JsonElement media,
        int index,
        int fallbackWidth,
        int fallbackHeight)
    {
        if (!TryObject(media, "video_info", out var videoInfo)
            || !TryArray(videoInfo, "variants", out var variants))
            return null;

        var candidates = new List<VideoCandidate>();
        foreach (var variant in variants.EnumerateArray())
        {
            var url = ReadString(variant, "url");
            var contentType = ReadString(variant, "content_type");
            if (string.IsNullOrWhiteSpace(url)
                || !string.Equals(contentType, "video/mp4", StringComparison.OrdinalIgnoreCase))
                continue;

            var size = ParseDimensions(url);
            candidates.Add(new VideoCandidate(
                url,
                ReadInt64(variant, "bitrate"),
                size.Width > 0 ? size.Width : fallbackWidth,
                size.Height > 0 ? size.Height : fallbackHeight));
        }

        var ordered = candidates
            .OrderByDescending(item => item.Bitrate)
            .ThenByDescending(item => (long)item.Width * item.Height)
            .ToArray();
        if (ordered.Length == 0)
            return null;

        return new MediaAsset(
            MediaAssetType.Video,
            index,
            ordered.Select(item => item.Url).Distinct(StringComparer.Ordinal).ToArray(),
            ordered[0].Bitrate,
            ordered[0].Width,
            ordered[0].Height,
            "h264");
    }

    private static void CollectTimeline(
        JsonElement element,
        List<JsonElement> tweets,
        ref string? bottomCursor,
        ref bool foundTimeline)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryArray(element, "instructions", out var instructions))
            {
                foundTimeline = true;
                foreach (var instruction in instructions.EnumerateArray())
                {
                    if (TryArray(instruction, "entries", out var entries))
                    {
                        foreach (var entry in entries.EnumerateArray())
                            ProcessEntry(entry, tweets, ref bottomCursor);
                    }
                    if (TryObject(instruction, "entry", out var singleEntry))
                        ProcessEntry(singleEntry, tweets, ref bottomCursor);
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                if (!property.NameEquals("instructions"))
                    CollectTimeline(property.Value, tweets, ref bottomCursor, ref foundTimeline);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectTimeline(item, tweets, ref bottomCursor, ref foundTimeline);
        }
    }

    private static void ProcessEntry(
        JsonElement entry,
        List<JsonElement> tweets,
        ref string? bottomCursor)
    {
        if (!TryObject(entry, "content", out var content))
            return;
        FindBottomCursor(content, ref bottomCursor);
        FindTweetResults(content, tweets);
    }

    private static void FindTweetResults(JsonElement element, List<JsonElement> tweets)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if ((property.NameEquals("tweet_results") || property.NameEquals("tweetResult"))
                    && TryObject(property.Value, "result", out var result))
                {
                    tweets.Add(result);
                    continue;
                }
                if (property.NameEquals("quoted_status_result")
                    || property.NameEquals("retweeted_status_result"))
                    continue;
                FindTweetResults(property.Value, tweets);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                FindTweetResults(item, tweets);
        }
    }

    private static void FindBottomCursor(JsonElement element, ref string? cursor)
    {
        if (cursor is not null)
            return;
        if (element.ValueKind == JsonValueKind.Object)
        {
            var type = ReadString(element, "cursorType") ?? ReadString(element, "cursor_type");
            if (string.Equals(type, "Bottom", StringComparison.OrdinalIgnoreCase))
            {
                cursor = ReadString(element, "value");
                if (cursor is not null)
                    return;
            }
            foreach (var property in element.EnumerateObject())
                FindBottomCursor(property.Value, ref cursor);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                FindBottomCursor(item, ref cursor);
        }
    }

    private static JsonElement? UnwrapTweet(JsonElement value)
    {
        var current = value;
        for (var i = 0; i < 5 && current.ValueKind == JsonValueKind.Object; i++)
        {
            if (TryObject(current, "legacy", out _))
                return current;
            if (TryObject(current, "tweet", out var tweet))
            {
                current = tweet;
                continue;
            }
            if (TryObject(current, "result", out var result))
            {
                current = result;
                continue;
            }
            break;
        }
        return null;
    }

    private static JsonElement? ReadUser(JsonElement tweet)
    {
        if (!TryObject(tweet, "core", out var core)
            || !TryObject(core, "user_results", out var results)
            || !TryObject(results, "result", out var current))
            return null;

        for (var i = 0; i < 4 && current.ValueKind == JsonValueKind.Object; i++)
        {
            if (TryObject(current, "legacy", out _))
                return current;
            if (TryObject(current, "user", out var user))
            {
                current = user;
                continue;
            }
            if (TryObject(current, "result", out var nested))
            {
                current = nested;
                continue;
            }
            break;
        }
        return null;
    }

    private static void CollectCardVideos(JsonElement element, HashSet<string> urls, int depth)
    {
        if (depth > 12)
            return;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (!property.NameEquals("quoted_status_result")
                    && !property.NameEquals("retweeted_status_result"))
                    CollectCardVideos(property.Value, urls, depth + 1);
            }
            return;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectCardVideos(item, urls, depth + 1);
            return;
        }
        if (element.ValueKind != JsonValueKind.String)
            return;

        var text = element.GetString()?.Trim();
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && uri.Host.Equals("video.twimg.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            urls.Add(text!);
    }

    private static string? TryGetProfileName(string pageUrl)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri) || !IsXHost(uri.Host))
            return null;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || ReservedRoutes.Contains(parts[0]))
            return null;
        if (parts.Length > 1
            && !parts[1].Equals("media", StringComparison.OrdinalIgnoreCase)
            && !parts[1].Equals("with_replies", StringComparison.OrdinalIgnoreCase))
            return null;
        var name = Uri.UnescapeDataString(parts[0]);
        return UserNameRegex().IsMatch(name) ? name : null;
    }

    private static bool IsXHost(string host)
        => host.Equals("x.com", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".x.com", StringComparison.OrdinalIgnoreCase)
           || host.Equals("twitter.com", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".twitter.com", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> DistinctUrls(params string[] urls)
        => urls.Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string ToOriginalImage(string url)
    {
        try
        {
            var builder = new UriBuilder(url);
            var values = builder.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2))
                .ToDictionary(
                    pair => WebUtility.UrlDecode(pair[0]),
                    pair => pair.Length > 1 ? WebUtility.UrlDecode(pair[1]) : string.Empty,
                    StringComparer.OrdinalIgnoreCase);
            values["name"] = "orig";
            builder.Query = string.Join('&', values.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
            return builder.Uri.AbsoluteUri;
        }
        catch
        {
            return url;
        }
    }

    private static string? NormalizeAvatar(string? url)
        => string.IsNullOrWhiteSpace(url)
            ? null
            : url.Replace("_normal.", "_400x400.", StringComparison.OrdinalIgnoreCase);

    private static long ParseCreatedAt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;
        var match = CreatedAtRegex().Match(value);
        var normalized = match.Success
            ? $"{match.Groups["prefix"].Value}{match.Groups["sign"].Value}" +
              $"{match.Groups["hour"].Value}:{match.Groups["minute"].Value} {match.Groups["year"].Value}"
            : value;
        return DateTimeOffset.TryParseExact(
                   normalized,
                   "ddd MMM dd HH:mm:ss zzz yyyy",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AllowWhiteSpaces,
                   out var timestamp)
               || DateTimeOffset.TryParse(
                   normalized,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AllowWhiteSpaces,
                   out timestamp)
            ? timestamp.ToUnixTimeSeconds()
            : 0;
    }

    private static (int Width, int Height) ParseDimensions(string url)
    {
        var match = DimensionsRegex().Match(url);
        return match.Success
               && int.TryParse(match.Groups["width"].Value, out var width)
               && int.TryParse(match.Groups["height"].Value, out var height)
            ? (width, height)
            : (0, 0);
    }

    private static async Task SetInputPassThroughAsync(
        IBrowserAutomationService browser,
        bool enabled,
        CancellationToken cancellationToken)
    {
        try
        {
            await browser.EvaluatePageAsync("""
                enabled => {
                    window.__smcAllowAutomationInput = enabled;
                    const overlay = document.getElementById('__social_media_crawler_capture_lock__');
                    if (overlay) overlay.style.pointerEvents = enabled ? 'none' : 'auto';
                }
                """, enabled, cancellationToken);
        }
        catch
        {
            // 页面未锁定或正在切换时不影响后续滚动。
        }
    }

    private static bool TryObject(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(name, out value)
               && value.ValueKind == JsonValueKind.Object;
    }

    private static bool TryArray(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(name, out value)
               && value.ValueKind == JsonValueKind.Array;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static string? ReadNestedString(
        JsonElement element,
        string first,
        string second,
        string third,
        string name)
        => TryObject(element, first, out var a)
           && TryObject(a, second, out var b)
           && TryObject(b, third, out var c)
            ? ReadString(c, name)
            : null;

    private static int ReadNestedInt(JsonElement element, string objectName, string name)
        => TryObject(element, objectName, out var nested)
            ? (int)Math.Clamp(ReadInt64(nested, name), 0, int.MaxValue)
            : 0;

    private static long ReadInt64(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
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
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return fallback;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            return number;
        return value.ValueKind == JsonValueKind.String
               && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            ? number
            : fallback;
    }

    private sealed record VideoCandidate(string Url, long Bitrate, int Width, int Height);

    [GeneratedRegex("^[A-Za-z0-9_]{1,15}$", RegexOptions.CultureInvariant)]
    private static partial Regex UserNameRegex();

    [GeneratedRegex(
        "^(?<prefix>[A-Za-z]{3} [A-Za-z]{3} \\d{1,2} \\d{2}:\\d{2}:\\d{2} )" +
        "(?<sign>[+-])(?<hour>\\d{2})(?<minute>\\d{2}) (?<year>\\d{4})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex CreatedAtRegex();

    [GeneratedRegex(@"/vid/(?<width>\d+)x(?<height>\d+)/", RegexOptions.CultureInvariant)]
    private static partial Regex DimensionsRegex();
}