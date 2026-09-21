using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using HelloCrab.Core.Utilities;

namespace HelloCrab.Core.Views;

/// <summary>
/// 让桌面下载历史搜索也明确走共享的 HistoryPinyinMatcher。
/// 这样 Browser/WASM 与桌面不再分别维护拼音转换实现。
/// </summary>
public partial class MainWindow
{
    private static readonly IDisposable SharedHistoryPinyinDataContextHandler =
        StyledElement.DataContextProperty.Changed.AddClassHandler<MainWindow>((window, _) =>
            Dispatcher.UIThread.Post(
                window.EnsureSharedHistoryPinyinSearch,
                DispatcherPriority.Background));

    private readonly HistoryPinyinMatcher _sharedHistoryPinyinMatcher = new();
    private bool _sharedHistoryPinyinSearchInitialized;
    private int _sharedHistoryPinyinSearchInstallAttempts;

    private void EnsureSharedHistoryPinyinSearch()
    {
        if (_sharedHistoryPinyinSearchInitialized)
            return;

        if (!_historyPinyinSearchInitialized)
            EnsureHistoryPinyinSearch();

        if (!_historyPinyinSearchInitialized
            || _historyPinyinSearchBox is null
            || _historyPinyinViewModel is null)
        {
            if (_sharedHistoryPinyinSearchInstallAttempts++ < 24)
            {
                Dispatcher.UIThread.Post(
                    EnsureSharedHistoryPinyinSearch,
                    DispatcherPriority.Background);
            }
            return;
        }

        _sharedHistoryPinyinSearchInitialized = true;

        // 搜索输入只保留共享 matcher 这一条路径；旧实现仍保留在文件中用于兼容已有内部刷新逻辑。
        _historyPinyinSearchBox.TextChanged -= HistoryPinyinSearchTextChanged;
        _historyPinyinSearchBox.TextChanged -= SharedHistoryPinyinSearchTextChanged;
        _historyPinyinSearchBox.TextChanged += SharedHistoryPinyinSearchTextChanged;
    }

    private void SharedHistoryPinyinSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        RefreshHistoryPinyinClearButton();

        Dispatcher.UIThread.Post(
            ApplySharedHistoryPinyinSearch,
            DispatcherPriority.Background);
    }

    private void ApplySharedHistoryPinyinSearch()
    {
        if (_historyPinyinFilterApplying
            || _historyPinyinViewModel is not { } viewModel
            || _historyPinyinSearchBox is null)
        {
            return;
        }

        var keywords = (_historyPinyinSearchBox.Text ?? string.Empty)
            .Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // 与原桌面行为一致：空搜索继续由 ViewModel / 收藏筛选维护列表。
        if (keywords.Length == 0)
            return;

        var matches = viewModel.DownloadHistory
            .Where(item => !_showFavoritesOnly || IsHistoryFavorite(item))
            .Where(item => keywords.All(keyword =>
                _sharedHistoryPinyinMatcher.Matches(
                    item.UserName,
                    item.UserId,
                    item.Platform,
                    item.PlatformDisplayText,
                    keyword)))
            .ToArray();

        _historyPinyinFilterApplying = true;
        try
        {
            HistoryCollectionSynchronizer.Sync(
                viewModel.FilteredDownloadHistory,
                matches);
        }
        finally
        {
            _historyPinyinFilterApplying = false;
        }
    }
}
