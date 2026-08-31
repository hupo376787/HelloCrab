using System.Globalization;
using System.Text.Json;

namespace HelloCrab.Core.Utilities;

/// <summary>
/// 从作品接口中读取含义明确的“作者作品总数”字段。
/// 不读取普通 count/total，避免误把分页条数、点赞数等统计当作作品数。
/// </summary>
public static class AuthorWorkCountReader
{
    public static int? TryRead(string platformId, JsonElement root)
    {
        var propertyNames = platformId.ToLowerInvariant() switch
        {
            "douyin" => new[] { "aweme_count", "awemeCount", "work_count", "workCount" },
            "tiktok" => new[] { "videoCount", "video_count" },
            "kuaishou" or "kuaishou-live" => new[]
            {
                "photoCount", "photo_count", "workCount", "work_count", "作品数"
            },
            "xiaohongshu" => new[] { "noteCount", "note_count", "notesCount", "notes_count" },
            "weibo" or "x" => new[] { "statuses_count", "statusesCount" },
            "instagram" => new[] { "media_count", "mediaCount" },
            "pinterest" => new[] { "pin_count", "pinCount", "pins_count", "pinsCount" },
            "meipian" => new[] { "article_count", "articleCount" },
            "youtube" => new[] { "videoCount", "video_count" },
            _ => Array.Empty<string>()
        };

        if (propertyNames.Length == 0)
            return null;

        foreach (var propertyName in propertyNames)
        {
            if (TryFindPropertyValue(root, propertyName, 0, out var count))
                return count;
        }

        return null;
    }

    private static bool TryFindPropertyValue(
        JsonElement element,
        string propertyName,
        int depth,
        out int count)
    {
        count = 0;
        if (depth > 12)
            return false;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase)
                    && TryReadNonNegativeInt(property.Value, out count))
                {
                    return true;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                if (TryFindPropertyValue(property.Value, propertyName, depth + 1, out count))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindPropertyValue(item, propertyName, depth + 1, out count))
                    return true;
            }
        }

        return false;
    }

    private static bool TryReadNonNegativeInt(JsonElement element, out int count)
    {
        count = 0;
        long value;
        if (element.ValueKind == JsonValueKind.Number)
        {
            if (!element.TryGetInt64(out value))
                return false;
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString();
            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return false;
        }
        else
        {
            return false;
        }

        if (value is < 0 or > int.MaxValue)
            return false;

        count = (int)value;
        return true;
    }
}
