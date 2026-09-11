using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using HelloCrab.Core.Remote.ViewModels;
using HelloCrab.Core.Utilities;

namespace HelloCrab.Core.Remote.Views;

/// <summary>
/// Web / mobile 下载历史搜索的最终统一入口。
///
/// 旧实现曾经同时存在基础文本筛选、远程拼音增强、统一搜索、全部读音搜索等多层处理，
/// 在 WASM 中会出现后执行的旧筛选覆盖拼音结果。这里把输入、数据刷新以及旧筛选覆盖后的
/// 最终结果都收口到与桌面端共用的 HistoryPinyinMatcher。
/// </summary>
public partial class RemoteMainView
{
    private static readonly IDisposable SharedRemoteHistoryPinyinDataContextHandler =
        StyledElement.DataContextProperty.Changed.AddClassHandler<RemoteMainView>((view, _) =>
            Dispatcher.UIThread.Post(
                view.EnsureSharedRemoteHistoryPinyinSearch,
                DispatcherPriority.Background));

    private readonly HistoryPinyinMatcher _sharedRemoteHistoryPinyinMatcher = new();
    private readonly HashSet<RemoteHistoryItemViewModel> _sharedRemoteHistoryObservedItems = new();

    private RemoteMainViewModel? _sharedRemoteHistoryViewModel;
    private bool _sharedRemoteHistoryPinyinSearchInitialized;
    private bool _sharedRemoteHistoryPinyinFilterApplying;
    private bool _sharedRemoteHistoryPinyinFilterQueued;
    private int _sharedRemoteHistoryPinyinSearchInstallAttempts;

    private void EnsureSharedRemoteHistoryPinyinSearch()
    {
        if (_sharedRemoteHistoryPinyinSearchInitialized)
            return;

        if (!_remoteHistoryUiInitialized
            || !_remoteHistoryUnifiedSearchInitialized
            || (_browserHistorySearchBox is null && _mobileHistorySearchBox is null))
        {
            if (_sharedRemoteHistoryPinyinSearchInstallAttempts++ < 64)
            {
                Dispatcher.UIThread.Post(
                    EnsureSharedRemoteHistoryPinyinSearch,
                    DispatcherPriority.Background);
            }
            return;
        }

        // 先让旧的“全部读音”增强完成安装，再移除其 TextChanged，确保共享入口最后接管。
        if (!_remoteHistoryAllPinyinSearchInitialized)
            InitializeRemoteHistoryAllPinyinSearch();

        _sharedRemoteHistoryPinyinSearchInitialized = true;

        ConfigureSharedRemoteHistorySearchBox(_browserHistorySearchBox);
        ConfigureSharedRemoteHistorySearchBox(_mobileHistorySearchBox);

        _remoteFilteredHistory.CollectionChanged += SharedRemoteFilteredHistoryChanged;
        BindSharedRemoteHistoryViewModel(_remoteHistoryViewModel);
        ApplySharedRemoteHistoryPinyinFilter();
    }

    private void ConfigureSharedRemoteHistorySearchBox(TextBox? searchBox)
    {
        if (searchBox is null)
            return;

        // 移除所有历史遗留搜索入口，只保留共享 matcher。
        searchBox.TextChanged -= HistorySearchBox_TextChanged;
        searchBox.TextChanged -= RemoteHistoryEnhancedSearchTextChanged;
        searchBox.TextChanged -= UnifiedRemoteHistorySearchTextChanged;
        searchBox.TextChanged -= AllPinyinRemoteHistorySearchTextChanged;
        searchBox.TextChanged -= SharedRemoteHistorySearchTextChanged;
        searchBox.TextChanged += SharedRemoteHistorySearchTextChanged;
    }

    private void SharedRemoteHistorySearchTextChanged(object? sender, TextChangedEventArgs e)
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

        ApplySharedRemoteHistoryPinyinFilter();
    }

    private void BindSharedRemoteHistoryViewModel(RemoteMainViewModel? viewModel)
    {
        if (viewModel is null || ReferenceEquals(_sharedRemoteHistoryViewModel, viewModel))
            return;

        if (_sharedRemoteHistoryViewModel is not null)
            _sharedRemoteHistoryViewModel.History.CollectionChanged -= SharedRemoteHistoryChanged;

        ClearSharedRemoteHistoryItemSubscriptions();
        _sharedRemoteHistoryViewModel = viewModel;
        _sharedRemoteHistoryViewModel.History.CollectionChanged += SharedRemoteHistoryChanged;
        RefreshSharedRemoteHistoryItemSubscriptions();
    }

    private void SharedRemoteHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshSharedRemoteHistoryItemSubscriptions();
        QueueSharedRemoteHistoryPinyinFilter();
    }

    private void RefreshSharedRemoteHistoryItemSubscriptions()
    {
        if (_sharedRemoteHistoryViewModel is null)
            return;

        var current = _sharedRemoteHistoryViewModel.History.ToHashSet();

        foreach (var item in _sharedRemoteHistoryObservedItems
                     .Where(item => !current.Contains(item))
                     .ToArray())
        {
            item.PropertyChanged -= SharedRemoteHistoryItemChanged;
            _sharedRemoteHistoryObservedItems.Remove(item);
        }

        foreach (var item in current)
        {
            if (_sharedRemoteHistoryObservedItems.Add(item))
                item.PropertyChanged += SharedRemoteHistoryItemChanged;
        }
    }

    private void ClearSharedRemoteHistoryItemSubscriptions()
    {
        foreach (var item in _sharedRemoteHistoryObservedItems)
            item.PropertyChanged -= SharedRemoteHistoryItemChanged;

        _sharedRemoteHistoryObservedItems.Clear();
    }

    private void SharedRemoteHistoryItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RemoteHistoryItemViewModel.UserName)
            or nameof(RemoteHistoryItemViewModel.UserId)
            or nameof(RemoteHistoryItemViewModel.Platform)
            or nameof(RemoteHistoryItemViewModel.PlatformDisplayText))
        {
            QueueSharedRemoteHistoryPinyinFilter();
        }
    }

    private void SharedRemoteFilteredHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 旧逻辑在数据刷新时仍可能短暂改写列表；有搜索词时再次在队列末尾用共享 matcher 收口。
        if (!_sharedRemoteHistoryPinyinFilterApplying
            && !string.IsNullOrWhiteSpace(_remoteHistorySearchText))
        {
            QueueSharedRemoteHistoryPinyinFilter();
        }
    }

    private void QueueSharedRemoteHistoryPinyinFilter()
    {
        if (_sharedRemoteHistoryPinyinFilterQueued)
            return;

        _sharedRemoteHistoryPinyinFilterQueued = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _sharedRemoteHistoryPinyinFilterQueued = false;
                ApplySharedRemoteHistoryPinyinFilter();
            },
            DispatcherPriority.Background);
    }

    private void ApplySharedRemoteHistoryPinyinFilter()
    {
        var viewModel = _sharedRemoteHistoryViewModel ?? _remoteHistoryViewModel;
        if (viewModel is null)
            return;

        if (!ReferenceEquals(_sharedRemoteHistoryViewModel, viewModel))
            BindSharedRemoteHistoryViewModel(viewModel);

        var keywords = (_remoteHistorySearchText ?? string.Empty)
            .Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var desired = viewModel.History
            .Where(item => keywords.Length == 0
                           || keywords.All(keyword =>
                               _sharedRemoteHistoryPinyinMatcher.Matches(
                                   item.UserName,
                                   item.UserId,
                                   item.Platform,
                                   item.PlatformDisplayText,
                                   keyword)))
            .ToArray();

        _sharedRemoteHistoryPinyinFilterApplying = true;
        try
        {
            if (!_remoteFilteredHistory.SequenceEqual(desired))
            {
                _remoteFilteredHistory.Clear();
                foreach (var item in desired)
                    _remoteFilteredHistory.Add(item);
            }
        }
        finally
        {
            _sharedRemoteHistoryPinyinFilterApplying = false;
        }

        var countText = keywords.Length == 0
            ? $"共 {viewModel.History.Count} 位作者"
            : $"找到 {desired.Length} 位作者 · 共 {viewModel.History.Count} 位";

        if (_browserHistoryCountText is not null)
            _browserHistoryCountText.Text = countText;
        if (_mobileHistoryCountText is not null)
            _mobileHistoryCountText.Text = countText;
    }
}
