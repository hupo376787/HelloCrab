using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace HelloCrab.Core.Remote.Views;

/// <summary>
/// 统一 Web/手机历史搜索入口。
/// 原来的基础搜索和拼音增强各自监听 TextChanged，会出现基础筛选最后覆盖拼音结果的竞态。
/// 这里在增强逻辑安装完成后移除两个旧监听，只保留一个入口，确保中文、UID、平台、
/// 全拼、姓名拼音和拼音首字母使用同一套筛选结果。
/// </summary>
public partial class RemoteMainView
{
    private static readonly IDisposable RemoteHistoryUnifiedSearchDataContextHandler =
        StyledElement.DataContextProperty.Changed.AddClassHandler<RemoteMainView>((view, _) =>
            Dispatcher.UIThread.Post(
                view.InitializeRemoteHistoryUnifiedSearch,
                DispatcherPriority.Background));

    private bool _remoteHistoryUnifiedSearchInitialized;
    private int _remoteHistoryUnifiedSearchInstallAttempts;

    private void InitializeRemoteHistoryUnifiedSearch()
    {
        if (_remoteHistoryUnifiedSearchInitialized)
            return;

        if (!_remoteHistoryEnhancementsInitialized
            || (_browserHistorySearchBox is null && _mobileHistorySearchBox is null))
        {
            if (_remoteHistoryUnifiedSearchInstallAttempts++ < 16)
            {
                Dispatcher.UIThread.Post(
                    InitializeRemoteHistoryUnifiedSearch,
                    DispatcherPriority.Background);
            }
            return;
        }

        _remoteHistoryUnifiedSearchInitialized = true;

        ConfigureUnifiedHistorySearchBox(_browserHistorySearchBox);
        ConfigureUnifiedHistorySearchBox(_mobileHistorySearchBox);
    }

    private void ConfigureUnifiedHistorySearchBox(TextBox? searchBox)
    {
        if (searchBox is null)
            return;

        searchBox.TextChanged -= HistorySearchBox_TextChanged;
        searchBox.TextChanged -= RemoteHistoryEnhancedSearchTextChanged;
        searchBox.TextChanged -= UnifiedRemoteHistorySearchTextChanged;
        searchBox.TextChanged += UnifiedRemoteHistorySearchTextChanged;
    }

    private void UnifiedRemoteHistorySearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_syncingHistorySearch || sender is not TextBox source)
            return;

        _remoteHistorySearchText = source.Text ?? string.Empty;

        _syncingHistorySearch = true;
        try
        {
            if (!ReferenceEquals(source, _browserHistorySearchBox)
                && _browserHistorySearchBox is not null
                && !string.Equals(_browserHistorySearchBox.Text, _remoteHistorySearchText, StringComparison.Ordinal))
            {
                _browserHistorySearchBox.Text = _remoteHistorySearchText;
            }

            if (!ReferenceEquals(source, _mobileHistorySearchBox)
                && _mobileHistorySearchBox is not null
                && !string.Equals(_mobileHistorySearchBox.Text, _remoteHistorySearchText, StringComparison.Ordinal))
            {
                _mobileHistorySearchBox.Text = _remoteHistorySearchText;
            }
        }
        finally
        {
            _syncingHistorySearch = false;
        }

        ApplyRemoteHistoryEnhancedFilter();
    }
}
