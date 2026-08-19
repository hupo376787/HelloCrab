using System.Net;
using System.Text.Json;
using HelloCrab.Core.Models;
using HelloCrab.Core.Services.Browser;

namespace HelloCrab.Core.Sites.Douyin;

/// <summary>
/// 在现有抖音作者主页适配器基础上补充 Live Photo 解析。
/// 顶层 aweme.is_live_photo == 1 表示该图文作品包含实况图；
/// 每张图片的动态部分从 images[].video.play_addr 等字段读取。
/// </summary>
public sealed class DouyinLivePhotoSiteAdapter : ISiteAdapter
{
    private readonly DouyinSiteAdapter _inner = new();

    public string Id => _inner.Id;
    public string DisplayName => _inner.DisplayName;
    public string HomeUrl => _inner.HomeUrl;

    public bool CanHandlePage(string pageUrl)
        => _inner.CanHandlePage(pageUrl);

    public bool IsTargetResponse(
        string responseUrl,
        string resourceType,
        int statusCode,
        string? requestBody)
        => _inner.IsTargetResponse(responseUrl, resourceType, statusCode, requestBody);

    public ParsedWorkBatch ParseResponse(
        string responseUrl,
        string responseJson,
        string pageUrl,
        string? requestBody)
    {
        var parsed = _inner.ParseResponse(responseUrl, responseJson, pageUrl, requestBody);
        if (parsed.Works.Count == 0)
            return parsed;

        try
        {
            using var document = JsonDocument.Parse(responseJson);
            if (!TryGetArray(document.RootElement, "aweme_list", out var awemeList))
                return parsed;

            var awemeById = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var aweme in awemeList.EnumerateArray())
            {
                var awemeId = ReadString(aweme, "aweme_id");
                if (!string.IsNullOrWhiteSpace(awemeId))
                    awemeById[awemeId] = aweme;
            }

            var changed = false;
            var works = new WorkItem[parsed.Works.Count];
            for (var i = 0; i < parsed.Works.Count; i++)
            {
                var work = parsed.Works[i];
                works[i] = work;

                if (work.Assets.Any(asset => asset.Type == MediaAssetType.LivePhoto)
                    || !awemeById.TryGetValue(work.WorkId, out var aweme)
                    || ReadInt64(aweme, "is_live_photo") != 1)
                {
                    continue;
                }

                var livePhotoAssets = ParseLivePhotoAssets(aweme);
                if (livePhotoAssets.Count == 0)
                    continue;

                works[i] = work with
                {
                    Assets = work.Assets.Concat(livePhotoAssets).ToArray()
                };
                changed = true;
            }

            return changed ? parsed with { Works = works } : parsed;
        }
        catch (JsonException)
        {
            // 普通解析已经由基础适配器完成；补充 Live Photo 失败时不影响原作品下载。
            return parsed;
        }
    }

    public Task ScrollNextAsync(
        IBrowserAutomationService browser,
        CancellationToken cancellationToken)
        => _inner.ScrollNextAsync(browser, cancellationToken);

    public Task<PageScrollState> GetScrollStateAsync(
        IBrowserAutomationService browser,
        CancellationToken cancellationToken)
        => _inner.GetScrollStateAsync(browser, cancellationToken);

    private static IReadOnlyList<MediaAsset> ParseLivePhotoAssets(JsonElement aweme)
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
            // 与基础 DouyinSiteAdapter 的静态图片去重方式保持一致，
            // 保证 Live Photo 序号与对应静态图片的 _01/_02/... 完全一致。
            var imageUrls = ReadImageUrls(image);
            if (imageUrls.Count == 0 || !seenImages.Add(imageUrls[0]))
                continue;

            var livePhoto = ParseImageLivePhoto(image, index);
            if (livePhoto is not null)
                result.Add(livePhoto);

            index++;
        }

        return result;
    }

    private static MediaAsset? ParseImageLivePhoto(JsonElement image, int index)
    {
        if (!TryGetObject(image, "video", out var video))
            return null;

        var urls = new List<string>();

        // 实况 MP4 位于每张 image.video 中；优先更高效/无水印的播放地址。
        foreach (var property in new[]
                 {
                     "play_addr_265",
                     "play_addr_bytevc1",
                     "play_addr_h264",
                     "play_addr",
                     "download_addr"
                 })
        {
            AddUrlsFromContainer(video, property, urls);
        }

        if (TryGetArray(video, "bit_rate", out var bitRates))
        {
            foreach (var item in bitRates.EnumerateArray()
                         .OrderByDescending(item => ReadInt64(item, "bit_rate")))
            {
                foreach (var property in new[]
                         {
                             "play_addr_265",
                             "play_addr_bytevc1",
                             "play_addr_h264",
                             "play_addr"
                         })
                {
                    AddUrlsFromContainer(item, property, urls);
                }
            }
        }

        var normalized = NormalizeUrls(urls);
        if (normalized.Count == 0)
            return null;

        var width = (int)ReadInt64(video, "width");
        var height = (int)ReadInt64(video, "height");
        if (width <= 0)
            width = (int)ReadInt64(image, "width");
        if (height <= 0)
            height = (int)ReadInt64(image, "height");

        return new MediaAsset(
            MediaAssetType.LivePhoto,
            index,
            normalized,
            Width: width,
            Height: height,
            Codec: "mp4");
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

    private static void AddUrlsFromContainer(
        JsonElement parent,
        string propertyName,
        List<string> target)
    {
        if (!TryGetObject(parent, propertyName, out var container))
            return;

        AddStringArray(container, "url_list", target);
        AddStringArray(container, "download_url_list", target);

        var direct = ReadString(container, "url");
        if (!string.IsNullOrWhiteSpace(direct))
            target.Add(direct);
    }

    private static void AddStringArray(
        JsonElement parent,
        string propertyName,
        List<string> target)
    {
        if (!TryGetArray(parent, propertyName, out var array))
            return;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String
                && item.GetString() is { Length: > 0 } value)
            {
                target.Add(value);
            }
        }
    }

    private static IReadOnlyList<string> NormalizeUrls(IEnumerable<string> urls)
        => urls
            .Select(WebUtility.HtmlDecode)
            .Where(static url => !string.IsNullOrWhiteSpace(url)
                                 && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                                 && uri.Scheme is "http" or "https")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

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
