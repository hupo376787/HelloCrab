using System.Text;
using ToolGood.Words.Pinyin;

namespace HelloCrab.Core.Utilities;

/// <summary>
/// 下载历史作者搜索共用的拼音匹配核心。
/// Desktop / Browser WASM / mobile 都通过这一处执行作者名拼音匹配。
///
/// Browser/WASM 端优先使用桌面主机随历史 DTO 一起下发的预计算拼音索引，
/// 避免浏览器运行时对第三方拼音词典资源的加载差异影响搜索结果；桌面端仍然
/// 使用同一方法构建这份索引，因此两端的匹配规则保持一致。
/// </summary>
public sealed class HistoryPinyinMatcher
{
    private const int MaxCacheEntries = 4096;
    private static readonly object SharedCacheGate = new();
    private static readonly Dictionary<string, string> SharedSearchTextCache =
        new(StringComparer.Ordinal);

    public bool Matches(
        string? userName,
        string? userId,
        string? platform,
        string? platformDisplayText,
        string keyword,
        string? precomputedPinyinSearchText = null)
    {
        if (ContainsText(userName, keyword)
            || ContainsText(userId, keyword)
            || ContainsText(platform, keyword)
            || ContainsText(platformDisplayText, keyword))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(userName)
            || !keyword.Any(IsAsciiLetter))
        {
            return false;
        }

        var normalizedKeyword = Normalize(keyword);
        if (normalizedKeyword.Length == 0)
            return false;

        var searchText = string.IsNullOrWhiteSpace(precomputedPinyinSearchText)
            ? BuildSearchText(userName)
            : precomputedPinyinSearchText;

        return !string.IsNullOrWhiteSpace(searchText)
               && searchText.Contains(normalizedKeyword, StringComparison.OrdinalIgnoreCase);
    }

    public void Clear()
    {
        lock (SharedCacheGate)
            SharedSearchTextCache.Clear();
    }

    /// <summary>
    /// 构建供桌面端和远程端共同使用的拼音搜索索引。
    /// 索引包含：普通全拼、姓名模式全拼、首字母，以及每个汉字的全部读音。
    /// 因此输入 HUI 可以命中作者名中的“会 / 回 / 慧 / 汇”等字；完整全拼和首字母
    /// 搜索仍然继续支持。
    /// </summary>
    public static string BuildSearchText(string? userName)
    {
        if (string.IsNullOrWhiteSpace(userName))
            return string.Empty;

        lock (SharedCacheGate)
        {
            if (SharedSearchTextCache.TryGetValue(userName, out var cached))
                return cached;
        }

        var parts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddConverted(parts, () => WordsHelper.GetPinyin(userName));
        AddConverted(parts, () => WordsHelper.GetPinyinForName(userName));
        AddConverted(parts, () => WordsHelper.GetFirstPinyin(userName));

        foreach (var ch in userName)
        {
            if (!IsSupportedChineseChar(ch))
                continue;

            try
            {
                foreach (var reading in WordsHelper.GetAllPinyin(ch))
                {
                    var normalized = Normalize(reading);
                    if (normalized.Length > 0)
                        parts.Add(normalized);
                }
            }
            catch
            {
                // 个别字符读取失败时保留其余拼音索引，不让一次异常使整个作者不可搜索。
            }
        }

        var result = string.Join('|', parts);
        lock (SharedCacheGate)
        {
            if (SharedSearchTextCache.Count >= MaxCacheEntries)
                SharedSearchTextCache.Clear();

            SharedSearchTextCache[userName] = result;
        }

        return result;
    }

    private static void AddConverted(HashSet<string> target, Func<string> converter)
    {
        try
        {
            var normalized = Normalize(converter());
            if (normalized.Length > 0)
                target.Add(normalized);
        }
        catch
        {
            // 保留其它索引。WASM 若无法加载第三方词典时会使用主机下发的预计算结果。
        }
    }

    private static bool ContainsText(string? value, string keyword)
        => !string.IsNullOrWhiteSpace(value)
           && value.Contains(keyword, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (IsAsciiLetter(ch) || ch is >= '0' and <= '9')
                builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }

    private static bool IsAsciiLetter(char ch)
        => (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z');

    private static bool IsSupportedChineseChar(char ch)
        => ch is >= '\u3400' and <= '\u4DB5'
           || ch is >= '\u4E00' and <= '\u9FD5';
}
