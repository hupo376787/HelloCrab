using System.Globalization;
using System.Text;
using HelloCrab.Core.Services.Settings;

namespace HelloCrab.Core.Utilities;

public static class FileNameHelper
{
    private static readonly HashSet<char> InvalidChars = Path.GetInvalidFileNameChars().ToHashSet();

    public static string Sanitize(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "无标题";

        if (maxLength <= 0)
            return "无标题";

        var normalized = value.Normalize();
        var builder = new StringBuilder(Math.Min(normalized.Length, maxLength));
        var previousWasSpace = false;
        var elements = StringInfo.GetTextElementEnumerator(normalized);

        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            var containsInvalidCharacter = element.Any(ch => InvalidChars.Contains(ch) || char.IsControl(ch));
            var isWhitespace = !containsInvalidCharacter && element.All(char.IsWhiteSpace);
            var mapped = containsInvalidCharacter ? "_" : isWhitespace ? " " : element;

            if (isWhitespace)
            {
                if (previousWasSpace)
                    continue;

                previousWasSpace = true;
            }
            else
            {
                previousWasSpace = false;
            }

            // 按 Unicode 文本元素追加，避免截断 Emoji 的代理对、变体选择符或 ZWJ 组合。
            if (builder.Length + mapped.Length > maxLength)
                break;

            builder.Append(mapped);
        }

        var result = builder.ToString().Trim(' ', '.', '_');
        return string.IsNullOrWhiteSpace(result) ? "无标题" : result;
    }

    public static string BuildAuthorFolderName(string? authorName, string? authorId, int maxLength = 120)
    {
        var safeId = string.IsNullOrWhiteSpace(authorId)
            ? string.Empty
            : Sanitize(authorId, Math.Min(64, Math.Max(1, maxLength - 3)));

        if (string.IsNullOrWhiteSpace(safeId))
            return Sanitize(authorName, maxLength);

        // 始终为“昵称(UID)”，并优先完整保留 UID；昵称过长时只截短昵称。
        var suffix = $"({safeId})";
        var nameLength = Math.Max(1, maxLength - suffix.Length);
        var safeName = Sanitize(authorName, nameLength);
        return Sanitize(safeName + suffix, maxLength);
    }

    public static string BuildWorkBaseName(
        DateTimeOffset localPublishedAt,
        string? description,
        string? workId,
        bool includeWorkId,
        int? maxLength = null)
    {
        var effectiveMaxLength = Math.Max(
            1,
            maxLength ?? LongFileNameState.CurrentWorkBaseNameMaxLength);
        var datePrefix = localPublishedAt.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
        var safeWorkId = includeWorkId && !string.IsNullOrWhiteSpace(workId)
            ? Sanitize(workId, 64)
            : string.Empty;
        var idSuffix = string.IsNullOrWhiteSpace(safeWorkId) ? string.Empty : $"_{safeWorkId}";

        // 格式：yyyy-MM-dd HH-mm-ss标题[_作品ID]
        // 没有文案时只使用发布时间；图集序号由下载层最后追加。
        var titleLength = Math.Max(1, effectiveMaxLength - datePrefix.Length - idSuffix.Length);
        var safeTitle = string.IsNullOrWhiteSpace(description)
            ? string.Empty
            : Sanitize(description, titleLength);
        return Sanitize(datePrefix + safeTitle + idSuffix, effectiveMaxLength);
    }

    public static string ShortenId(string id)
        => id.Length <= 12 ? id : id[^12..];
}
