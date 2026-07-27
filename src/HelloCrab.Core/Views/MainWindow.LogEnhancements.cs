using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HelloCrab.Core.Models;
using HelloCrab.Core.Services.Localization;
using HelloCrab.Core.ViewModels;

namespace HelloCrab.Core.Views;

public partial class MainWindow
{
    private static readonly IDisposable LogEnhancementsDataContextHandler =
        StyledElement.DataContextProperty.Changed.AddClassHandler<MainWindow>((window, _) =>
            Dispatcher.UIThread.Post(
                window.InstallLogEnhancements,
                DispatcherPriority.Loaded));

    private ObservableCollection<string>? _enhancedLogs;
    private MainWindowViewModel? _enhancedLogViewModel;
    private MenuItem? _copyLogsMenuItem;
    private bool _logEnhancementsUiInstalled;
    private bool _isLogPinnedToTop = true;
    private bool _isProgrammaticLogScroll;
    private bool _keepLogPinnedDuringInsert;
    private bool _wasCapturingForUrlLog;
    private bool _wasManualBatchRunningForUrlLog;
    private bool _awaitingBatchOriginalUrl;
    private string? _lastLoggedCaptureUrl;
    private string? _lastLoggedBatchOriginalUrl;

    private void InstallLogEnhancements()
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        // MainWindow 原逻辑会无条件把日志拉回顶部。移除旧处理器，改用“用户仍在顶部时才跟随”。
        if (_subscribedLogs is not null)
            _subscribedLogs.CollectionChanged -= Logs_CollectionChanged;

        if (_enhancedLogs is not null)
            _enhancedLogs.CollectionChanged -= EnhancedLogs_CollectionChanged;
        if (_enhancedLogViewModel is not null)
            _enhancedLogViewModel.PropertyChanged -= EnhancedLogViewModel_PropertyChanged;

        _enhancedLogs = viewModel.Logs;
        _enhancedLogViewModel = viewModel;
        _enhancedLogs.CollectionChanged += EnhancedLogs_CollectionChanged;
        _enhancedLogViewModel.PropertyChanged += EnhancedLogViewModel_PropertyChanged;

        _wasCapturingForUrlLog = viewModel.IsCapturing;
        _wasManualBatchRunningForUrlLog = viewModel.IsManualBatchRunning;
        _awaitingBatchOriginalUrl = viewModel.IsManualBatchRunning && !viewModel.IsCapturing;
        _isLogPinnedToTop = LogScrollViewer.Offset.Y <= 1;

        if (_logEnhancementsUiInstalled)
            return;

        _logEnhancementsUiInstalled = true;
        LogScrollViewer.PropertyChanged += LogScrollViewer_PropertyChanged;
        EnsureCopyLogsContextMenu();

        // 原收藏菜单在 Tunnel 阶段创建；这里在 Bubble 阶段把“收藏”改成更明确的“收藏该作者”。
        HistoryList.AddHandler(
            InputElement.ContextRequestedEvent,
            UpdateHistoryFavoriteMenuText,
            RoutingStrategies.Bubble,
            handledEventsToo: true);

        if (LocalizationService.Current is { } localization)
            localization.LanguageChanged += LogEnhancementsLanguageChanged;
        Closed += LogEnhancementsWindowClosed;
    }

    private void LogScrollViewer_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != ScrollViewer.OffsetProperty
            || _isProgrammaticLogScroll
            || _keepLogPinnedDuringInsert)
        {
            return;
        }

        _isLogPinnedToTop = LogScrollViewer.Offset.Y <= 1;
    }

    private void EnhancedLogs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is not (NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
            || !_isLogPinnedToTop)
        {
            return;
        }

        _keepLogPinnedDuringInsert = true;
        Dispatcher.UIThread.Post(ScrollEnhancedLogToTop, DispatcherPriority.Render);
        Dispatcher.UIThread.Post(() =>
        {
            ScrollEnhancedLogToTop();
            _keepLogPinnedDuringInsert = false;
            _isLogPinnedToTop = LogScrollViewer.Offset.Y <= 1;
        }, DispatcherPriority.Background);
    }

    private void ScrollEnhancedLogToTop()
    {
        if (_enhancedLogs is null || _enhancedLogs.Count == 0)
            return;

        _isProgrammaticLogScroll = true;
        try
        {
            var offset = LogScrollViewer.Offset;
            if (offset.Y != 0)
                LogScrollViewer.Offset = new Vector(offset.X, 0);
        }
        finally
        {
            _isProgrammaticLogScroll = false;
        }
    }

    private void EnsureCopyLogsContextMenu()
    {
        var contextMenu = LogScrollViewer.ContextMenu ?? new ContextMenu();
        if (LogScrollViewer.ContextMenu is null)
            LogScrollViewer.ContextMenu = contextMenu;

        _copyLogsMenuItem = new MenuItem();
        _copyLogsMenuItem.Click += CopyLogsMenuItem_Click;
        contextMenu.Items.Add(_copyLogsMenuItem);
        RefreshCopyLogsMenuText();
    }

    private async void CopyLogsMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (_enhancedLogViewModel is not { } viewModel)
            return;

        var text = string.Join(Environment.NewLine, viewModel.Logs);
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(text);
    }

    private void EnhancedLogViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MainWindowViewModel viewModel)
            return;

        if (e.PropertyName == nameof(MainWindowViewModel.IsManualBatchRunning))
        {
            if (viewModel.IsManualBatchRunning && !_wasManualBatchRunningForUrlLog)
            {
                _awaitingBatchOriginalUrl = true;
                _lastLoggedBatchOriginalUrl = null;
            }
            else if (!viewModel.IsManualBatchRunning)
            {
                _awaitingBatchOriginalUrl = false;
                _lastLoggedBatchOriginalUrl = null;
            }

            _wasManualBatchRunningForUrlLog = viewModel.IsManualBatchRunning;
            return;
        }

        if (e.PropertyName == nameof(MainWindowViewModel.CurrentUrl))
        {
            if (viewModel.IsManualBatchRunning
                && !viewModel.IsCapturing
                && _awaitingBatchOriginalUrl)
            {
                // SelectedPlatform 与 CurrentUrl 在同一个批量循环中连续赋值，延迟到本轮 UI 消息结束，
                // 读取最终写入的 TXT 原始 URL，避免把平台首页误记为原始地址。
                Dispatcher.UIThread.Post(
                    () => TryLogBatchOriginalUrl(viewModel),
                    DispatcherPriority.Normal);
            }

            if (viewModel.IsCapturing)
                LogCurrentCaptureUrl(viewModel);
            return;
        }

        if (e.PropertyName != nameof(MainWindowViewModel.IsCapturing))
            return;

        if (viewModel.IsCapturing && !_wasCapturingForUrlLog)
        {
            LogCurrentCaptureUrl(viewModel);
            _awaitingBatchOriginalUrl = false;
        }
        else if (!viewModel.IsCapturing && _wasCapturingForUrlLog)
        {
            _lastLoggedCaptureUrl = null;
            if (viewModel.IsManualBatchRunning)
            {
                _awaitingBatchOriginalUrl = true;
                _lastLoggedBatchOriginalUrl = null;
            }
        }

        _wasCapturingForUrlLog = viewModel.IsCapturing;
    }

    private void TryLogBatchOriginalUrl(MainWindowViewModel viewModel)
    {
        if (!viewModel.IsManualBatchRunning
            || viewModel.IsCapturing
            || !_awaitingBatchOriginalUrl
            || !IsHttpUrl(viewModel.CurrentUrl)
            || string.Equals(
                _lastLoggedBatchOriginalUrl,
                viewModel.CurrentUrl,
                StringComparison.Ordinal))
        {
            return;
        }

        _lastLoggedBatchOriginalUrl = viewModel.CurrentUrl;
        _awaitingBatchOriginalUrl = false;
        viewModel.AddRemoteLog(string.Format(
            LogUiText(
                "批量 TXT 原始 URL：{0}",
                "Original batch TXT URL: {0}",
                "一括 TXT の元 URL：{0}"),
            viewModel.CurrentUrl));
    }

    private void LogCurrentCaptureUrl(MainWindowViewModel viewModel)
    {
        var url = viewModel.CurrentUrl;
        if (!IsHttpUrl(url)
            || string.Equals(_lastLoggedCaptureUrl, url, StringComparison.Ordinal))
        {
            return;
        }

        _lastLoggedCaptureUrl = url;
        viewModel.AddRemoteLog(string.Format(
            LogUiText(
                "当前采集 URL：{0}",
                "Current capture URL: {0}",
                "現在の収集 URL：{0}"),
            url));
    }

    private void UpdateHistoryFavoriteMenuText(object? sender, ContextRequestedEventArgs e)
    {
        var current = e.Source as Control;
        DownloadHistoryItem? historyItem = null;
        ContextMenu? contextMenu = null;

        while (current is not null && !ReferenceEquals(current, HistoryList))
        {
            if (current.DataContext is DownloadHistoryItem item
                && current.ContextMenu is { } menu)
            {
                historyItem = item;
                contextMenu = menu;
                break;
            }

            current = current.Parent as Control;
        }

        if (historyItem is null || contextMenu is null)
            return;

        var favoriteMenuItem = contextMenu.Items
            .OfType<MenuItem>()
            .FirstOrDefault(item => item.Classes.Contains(FavoriteMenuClass));
        if (favoriteMenuItem is null)
            return;

        favoriteMenuItem.Header = IsHistoryFavorite(historyItem)
            ? LogUiText("取消收藏", "Remove from favorites", "お気に入りを解除")
            : LogUiText("收藏该作者", "Add this author to favorites", "この投稿者をお気に入りに追加");
    }

    private void LogEnhancementsLanguageChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(RefreshCopyLogsMenuText);

    private void RefreshCopyLogsMenuText()
    {
        if (_copyLogsMenuItem is not null)
        {
            _copyLogsMenuItem.Header = LogUiText(
                "复制日志",
                "Copy logs",
                "ログをコピー");
        }
    }

    private void LogEnhancementsWindowClosed(object? sender, EventArgs e)
    {
        if (_enhancedLogs is not null)
            _enhancedLogs.CollectionChanged -= EnhancedLogs_CollectionChanged;
        if (_enhancedLogViewModel is not null)
            _enhancedLogViewModel.PropertyChanged -= EnhancedLogViewModel_PropertyChanged;
        if (LocalizationService.Current is { } localization)
            localization.LanguageChanged -= LogEnhancementsLanguageChanged;
    }

    private static bool IsHttpUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && uri.Scheme is "http" or "https";

    private static string LogUiText(string zhCn, string enUs, string jaJp)
    {
        var code = LocalizationService.Current?.CurrentLanguageCode ?? "zh-CN";
        if (code.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            return enUs;
        if (code.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
            return jaJp;
        return zhCn;
    }
}
