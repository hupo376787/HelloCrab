using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using HelloCrab.Core.Remote.ViewModels;
using ToolGood.Words.Pinyin;

namespace HelloCrab.Core.Remote.Views;

/// <summary>
/// 远程历史搜索的“全部读音”补充。
///
/// WordsHelper.GetPinyin/GetPinyinForName 会根据词组/姓名上下文选择一个主要读音，
/// 这适合完整姓名搜索，但不能保证用户输入单个拼音时命中所有同音字/多音字。
/// 例如搜索 hui 时，应同时命中包含“会、回、慧、汇”等读音为 hui 的作者名。
///
/// 这里使用 WordsHelper.GetAllPinyin(char) 为每个汉字缓存全部无声调读音，
/// 并用 KMP 状态机在所有可能读音路径上做 contains 匹配。这样既支持单字全部读音，
/// 也支持跨多个汉字的连续拼音，同时避免枚举多音字的指数级组合。
/// </summary>
public partial class RemoteMainView
{
    private static readonly IDisposable RemoteHistoryAllPinyinDataContextHandler =
        StyledElement.DataContextProperty.Changed.AddClassHandler<RemoteMainView>((view, _) =>
            Dispatcher.UIThread.Post(
                view.InitializeRemoteHistoryAllPinyinSearch,
                DispatcherPriority.Background));

    private readonly Dictionary<string, string[][]> _remoteHistoryAllPinyinNameCache =
        new(StringComparer.Ordinal);
    private readonly Dictionary<char, string[]> _remoteHistoryAllPinyinCharCache = new();

    private bool _remoteHistoryAllPinyinSearchInitialized;
    private int _remoteHistoryAllPinyinSearchInstallAttempts;

    private void InitializeRemoteHistoryAllPinyinSearch()
    {
        if (_remoteHistoryAllPinyinSearchInitialized)
            return;

        if (!_remoteHistoryUnifiedSearchInitialized
            || (_browserHistorySearchBox is null && _mobileHistorySearchBox is null))
        {
            if (_remoteHistoryAllPinyinSearchInstallAttempts++ < 24)
            {
                Dispatcher.UIThread.Post(
                    InitializeRemoteHistoryAllPinyinSearch,
                    DispatcherPriority.Background);
            }
            return;
        }

        _remoteHistoryAllPinyinSearchInitialized = true;
        ConfigureAllPinyinHistorySearchBox(_browserHistorySearchBox);
        ConfigureAllPinyinHistorySearchBox(_mobileHistorySearchBox);
    }

    private void ConfigureAllPinyinHistorySearchBox(TextBox? searchBox)
    {
        if (searchBox is null)
            return;

        // 只保留一个最终搜索入口，避免旧基础过滤/单读音过滤再次覆盖结果。
        searchBox.TextChanged -= HistorySearchBox_TextChanged;
        searchBox.TextChanged -= RemoteHistoryEnhancedSearchTextChanged;
        searchBox.TextChanged -= UnifiedRemoteHistorySearchTextChanged;
        searchBox.TextChanged -= AllPinyinRemoteHistorySearchTextChanged;
        searchBox.TextChanged += AllPinyinRemoteHistorySearchTextChanged;
    }

    private void AllPinyinRemoteHistorySearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_syncingHistorySearch || sender is not TextBox source)
            return;

        _remoteHistorySearchText = source.Text ?? string.Empty;

        _syncingHistorySearch = true;
        try
        {
            if (!ReferenceEquals(source, _browserHistorySearchBox)
                && _browserHistorySearchBox is not null
                && !string.Equals(
                    _browserHistorySearchBox.Text,
                    _remoteHistorySearchText,
                    StringComparison.Ordinal))
            {
                _browserHistorySearchBox.Text = _remoteHistorySearchText;
            }

            if (!ReferenceEquals(source, _mobileHistorySearchBox)
                && _mobileHistorySearchBox is not null
                && !string.Equals(
                    _mobileHistorySearchBox.Text,
                    _remoteHistorySearchText,
                    StringComparison.Ordinal))
            {
                _mobileHistorySearchBox.Text = _remoteHistorySearchText;
            }
        }
        finally
        {
            _syncingHistorySearch = false;
        }

        ApplyRemoteHistoryAllPinyinFilter();
    }

    private void ApplyRemoteHistoryAllPinyinFilter()
    {
        if (_remoteHistoryEnhancementViewModel is not { } viewModel)
            return;

        var keywords = (_remoteHistorySearchText ?? string.Empty)
            .Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var desired = viewModel.History
            .Where(item => keywords.Length == 0
                           || keywords.All(keyword =>
                               RemoteHistoryItemMatchesAllPinyin(item, keyword)))
            .ToArray();

        if (!_remoteFilteredHistory.SequenceEqual(desired))
        {
            _remoteFilteredHistory.Clear();
            foreach (var item in desired)
                _remoteFilteredHistory.Add(item);
        }

        var countText = keywords.Length == 0
            ? $"共 {viewModel.History.Count} 位作者"
            : $"找到 {desired.Length} 位作者 · 共 {viewModel.History.Count} 位";

        if (_browserHistoryCountText is not null)
            _browserHistoryCountText.Text = countText;
        if (_mobileHistoryCountText is not null)
            _mobileHistoryCountText.Text = countText;
    }

    private bool RemoteHistoryItemMatchesAllPinyin(
        RemoteHistoryItemViewModel item,
        string keyword)
    {
        // 先保留原来的中文/UID/平台/完整拼音/姓名拼音/首字母匹配。
        if (RemoteHistoryItemMatches(item, keyword))
            return true;

        if (string.IsNullOrWhiteSpace(item.UserName)
            || !keyword.Any(IsRemoteHistoryAsciiLetter))
        {
            return false;
        }

        var normalizedKeyword = NormalizeRemoteHistoryPinyin(keyword);
        if (normalizedKeyword.Length == 0)
            return false;

        var readingGroups = GetRemoteHistoryAllPinyinReadings(item.UserName);
        return MatchesRemoteHistoryAllPinyin(readingGroups, normalizedKeyword);
    }

    private string[][] GetRemoteHistoryAllPinyinReadings(string userName)
    {
        if (_remoteHistoryAllPinyinNameCache.TryGetValue(userName, out var cached))
            return cached;

        var result = new string[userName.Length][];
        for (var i = 0; i < userName.Length; i++)
        {
            var ch = userName[i];
            if (IsRemoteHistoryPinyinChineseChar(ch))
            {
                result[i] = GetRemoteHistoryAllPinyinForChar(ch);
                continue;
            }

            var normalized = NormalizeRemoteHistoryPinyin(ch.ToString());
            result[i] = normalized.Length == 0
                ? Array.Empty<string>()
                : new[] { normalized };
        }

        if (_remoteHistoryAllPinyinNameCache.Count >= 4096)
            _remoteHistoryAllPinyinNameCache.Clear();

        _remoteHistoryAllPinyinNameCache[userName] = result;
        return result;
    }

    private string[] GetRemoteHistoryAllPinyinForChar(char ch)
    {
        if (_remoteHistoryAllPinyinCharCache.TryGetValue(ch, out var cached))
            return cached;

        string[] readings;
        try
        {
            readings = WordsHelper.GetAllPinyin(ch)
                .Select(NormalizeRemoteHistoryPinyin)
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            readings = Array.Empty<string>();
        }

        _remoteHistoryAllPinyinCharCache[ch] = readings;
        return readings;
    }

    private static bool IsRemoteHistoryPinyinChineseChar(char ch)
        => ch is >= '\u3400' and <= '\u4DB5'
           || ch is >= '\u4E00' and <= '\u9FD5';

    private static bool MatchesRemoteHistoryAllPinyin(
        IReadOnlyList<string[]> readingGroups,
        string query)
    {
        if (query.Length == 0 || readingGroups.Count == 0)
            return false;

        // KMP 让“contains”匹配可以从作者拼音中的任意位置开始；状态集合则表示
        // 多音字每一种读音分支。只保存 query.Length 个状态，不生成所有拼音组合。
        var prefixTable = BuildRemoteHistoryPinyinPrefixTable(query);
        var states = new HashSet<int> { 0 };

        foreach (var alternatives in readingGroups)
        {
            // Emoji、标点等没有可搜索读音，和原有 Normalize 行为一致：直接忽略。
            if (alternatives.Length == 0)
                continue;

            var nextStates = new HashSet<int>();
            foreach (var state in states)
            {
                foreach (var reading in alternatives)
                {
                    var nextState = state;
                    foreach (var ch in reading)
                    {
                        nextState = AdvanceRemoteHistoryPinyinMatch(
                            query,
                            prefixTable,
                            nextState,
                            ch);
                        if (nextState == query.Length)
                            return true;
                    }

                    nextStates.Add(nextState);
                }
            }

            states = nextStates;
            if (states.Count == 0)
                states.Add(0);
        }

        return false;
    }

    private static int[] BuildRemoteHistoryPinyinPrefixTable(string query)
    {
        var prefix = new int[query.Length];
        for (var i = 1; i < query.Length; i++)
        {
            var j = prefix[i - 1];
            while (j > 0 && query[i] != query[j])
                j = prefix[j - 1];

            if (query[i] == query[j])
                j++;

            prefix[i] = j;
        }

        return prefix;
    }

    private static int AdvanceRemoteHistoryPinyinMatch(
        string query,
        IReadOnlyList<int> prefix,
        int state,
        char ch)
    {
        while (state > 0 && query[state] != ch)
            state = prefix[state - 1];

        if (query[state] == ch)
            state++;

        return state;
    }
}
