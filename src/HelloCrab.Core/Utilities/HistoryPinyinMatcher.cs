using System.Text;
using ToolGood.Words.Pinyin;

namespace HelloCrab.Core.Utilities;

/// <summary>
/// 下载历史作者搜索共用的拼音匹配核心。
/// Desktop / Browser WASM / mobile 都通过这一处执行作者名拼音转换，
/// 避免不同前端各自维护一份实现后出现行为偏差。
/// </summary>
public sealed class HistoryPinyinMatcher
{
    private readonly Dictionary<string, HistoryPinyinIndex> _cache =
        new(StringComparer.Ordinal);

    private readonly record struct HistoryPinyinIndex(
        string Full,
        string NameFull,
        string Initials);

    public bool Matches(
        string? userName,
        string? userId,
        string? platform,
        string? platformDisplayText,
        string keyword)
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

        var pinyin = GetIndex(userName);
        return pinyin.Full.Contains(normalizedKeyword, StringComparison.OrdinalIgnoreCase)
               || pinyin.NameFull.Contains(normalizedKeyword, StringComparison.OrdinalIgnoreCase)
               || pinyin.Initials.Contains(normalizedKeyword, StringComparison.OrdinalIgnoreCase);
    }

    public void Clear() => _cache.Clear();

    private HistoryPinyinIndex GetIndex(string userName)
    {
        if (_cache.TryGetValue(userName, out var cached))
            return cached;

        // 与桌面端现有行为完全一致：普通全拼、姓名模式全拼、拼音首字母。
        var full = TryConvert(() => WordsHelper.GetPinyin(userName));
        var nameFull = TryConvert(() => WordsHelper.GetPinyinForName(userName));
        var initials = TryConvert(() => WordsHelper.GetFirstPinyin(userName));
        var result = new HistoryPinyinIndex(full, nameFull, initials);

        if (_cache.Count >= 4096)
            _cache.Clear();

        _cache[userName] = result;
        return result;
    }

    private static string TryConvert(Func<string> converter)
    {
        try
        {
            return Normalize(converter());
        }
        catch
        {
            return string.Empty;
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
}
