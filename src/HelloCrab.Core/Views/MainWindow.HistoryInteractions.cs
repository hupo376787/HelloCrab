using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HelloCrab.Core.Models;
using HelloCrab.Core.ViewModels;

namespace HelloCrab.Core.Views;

public partial class MainWindow
{
    private const double HistoryAutoScrollEdge = 52d;
    private const double HistoryAutoScrollStep = 18d;

    private static readonly IDisposable HistoryInteractionsDataContextHandler =
        StyledElement.DataContextProperty.Changed.AddClassHandler<MainWindow>((window, _) =>
            Dispatcher.UIThread.Post(
                window.InstallHistoryInteractions,
                DispatcherPriority.Loaded));

    private MainWindowViewModel? _historyInteractionsViewModel;
    private ScrollViewer? _historyListScrollViewer;
    private DispatcherTimer? _historyAutoScrollTimer;
    private Point _lastHistoryPointerInList;
    private int _historyAutoScrollDirection;
    private bool _historyInteractionsInstalled;
    private bool _historyFilterSuppressedForDrag;
    private bool _historyScrollRestorePending;
    private Vector _lastHistoryScrollOffset;
    private bool _lastHistoryWasAtBottom;
    private Vector _historyScrollOffsetBeforeRefresh;
    private bool _historyWasAtBottomBeforeRefresh;
    private long _historyScrollRestoreVersion;
    private long _historyFavoriteRestoreVersion;

    private void InstallHistoryInteractions()
    {
        if (_historyInteractionsInstalled
            || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        _historyInteractionsInstalled = true;
        _historyInteractionsViewModel = viewModel;

        HistoryList.AddHandler(
            PointerPressedEvent,
            HistoryList_DoubleClickPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        AddHandler(
            PointerMovedEvent,
            HistoryAutoScroll_PointerMoved,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        AddHandler(
            PointerReleasedEvent,
            HistoryAutoScroll_PointerReleased,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        viewModel.FilteredDownloadHistory.CollectionChanged +=
            HistoryFilteredCollectionChanged;
        Closed += HistoryInteractionsWindowClosed;

        _ = GetHistoryListScrollViewer();
    }

    private void HistoryList_DoubleClickPointerPressed(
        object? sender,
        PointerPressedEventArgs e)
    {
        if (e.ClickCount < 2
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || _historyInteractionsViewModel is not { } viewModel)
        {
            return;
        }

        var current = e.Source as Control;
        while (current is not null && !ReferenceEquals(current, HistoryList))
        {
            if (current.DataContext is DownloadHistoryItem item)
            {
                EndHistoryDrag(saveOrder: false);
                StopHistoryAutoScroll();
                viewModel.OpenHistoryFolder(item);
                e.Handled = true;
                return;
            }

            current = current.Parent as Control;
        }
    }

    private void HistoryAutoScroll_PointerMoved(object? sender, PointerEventArgs e)
    {
        _lastHistoryPointerInList = e.GetPosition(HistoryList);

        if (!_isHistoryDragging || _draggedHistoryItem is null)
        {
            StopHistoryAutoScroll();
            return;
        }

        // 完整历史集合在拖动预览期间也会发生 Move。暂时挡住收藏筛选的
        // 异步重建，避免拖动到一半时列表被 Clear/Add 重置。
        if (!_historyFilterSuppressedForDrag)
        {
            _historyFilterSuppressedForDrag = true;
            _isApplyingHistoryFavoriteFilter = true;
        }

        var listHeight = HistoryList.Bounds.Height;
        _historyAutoScrollDirection = _lastHistoryPointerInList.Y switch
        {
            < HistoryAutoScrollEdge => -1,
            _ when listHeight > 0
                   && _lastHistoryPointerInList.Y > listHeight - HistoryAutoScrollEdge => 1,
            _ => 0
        };

        if (_historyAutoScrollDirection == 0)
        {
            StopHistoryAutoScroll();
            return;
        }

        MoveDraggedHistoryItemToVisibleEdge(_historyAutoScrollDirection);
        EnsureHistoryAutoScrollTimer().Start();
    }

    private void HistoryAutoScroll_PointerReleased(
        object? sender,
        PointerReleasedEventArgs e)
    {
        StopHistoryAutoScroll();
        ReleaseHistoryFilterAfterDrag();
    }

    private DispatcherTimer EnsureHistoryAutoScrollTimer()
    {
        if (_historyAutoScrollTimer is not null)
            return _historyAutoScrollTimer;

        _historyAutoScrollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(35)
        };
        _historyAutoScrollTimer.Tick += HistoryAutoScrollTimer_Tick;
        return _historyAutoScrollTimer;
    }

    private void HistoryAutoScrollTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isHistoryDragging
            || _draggedHistoryItem is null
            || _historyAutoScrollDirection == 0)
        {
            StopHistoryAutoScroll();
            ReleaseHistoryFilterAfterDrag();
            return;
        }

        var scrollViewer = GetHistoryListScrollViewer();
        if (scrollViewer is null)
            return;

        var maximum = Math.Max(
            0d,
            scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var nextY = Math.Clamp(
            scrollViewer.Offset.Y
            + _historyAutoScrollDirection * HistoryAutoScrollStep,
            0d,
            maximum);

        if (Math.Abs(nextY - scrollViewer.Offset.Y) < 0.1d)
            return;

        scrollViewer.Offset = new Vector(scrollViewer.Offset.X, nextY);
        MoveDraggedHistoryItemToVisibleEdge(_historyAutoScrollDirection);
    }

    private void MoveDraggedHistoryItemToVisibleEdge(int direction)
    {
        if (_draggedHistoryItem is null
            || _historyInteractionsViewModel is not { } viewModel)
        {
            return;
        }

        var realizedIndexes = HistoryList
            .GetRealizedContainers()
            .Select(HistoryList.IndexFromContainer)
            .Where(index => index >= 0)
            .OrderBy(index => index)
            .ToArray();
        if (realizedIndexes.Length == 0)
            return;

        var targetIndex = direction < 0
            ? realizedIndexes[0]
            : realizedIndexes[^1];
        viewModel.MoveHistoryItemPreview(_draggedHistoryItem, targetIndex);
    }

    private void StopHistoryAutoScroll()
    {
        _historyAutoScrollDirection = 0;
        _historyAutoScrollTimer?.Stop();
    }

    private void ReleaseHistoryFilterAfterDrag()
    {
        if (!_historyFilterSuppressedForDrag)
            return;

        _historyFilterSuppressedForDrag = false;
        _isApplyingHistoryFavoriteFilter = false;
        QueueHistoryFavoriteFilterRestore();
    }

    private void HistoryFilteredCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        if (!_isHistoryDragging)
            QueueHistoryScrollRestore();

        // PersistHistoryOrderAsync 完成后，ViewModel 会先按普通搜索规则刷新一次。
        // 收藏模式下在本轮集合变化结束后重新应用收藏筛选，保持当前视图不变。
        if (_showFavoritesOnly
            && !_isApplyingHistoryFavoriteFilter
            && !_isHistoryDragging)
        {
            QueueHistoryFavoriteFilterRestore();
        }
    }

    private void QueueHistoryFavoriteFilterRestore()
    {
        if (!_showFavoritesOnly)
            return;

        var version = Interlocked.Increment(ref _historyFavoriteRestoreVersion);
        Dispatcher.UIThread.Post(
            () =>
            {
                if (version != Interlocked.Read(ref _historyFavoriteRestoreVersion)
                    || _isHistoryDragging
                    || !_showFavoritesOnly)
                {
                    return;
                }

                ApplyHistoryFavoriteFilter();
            },
            DispatcherPriority.Background);
    }

    private void QueueHistoryScrollRestore()
    {
        var scrollViewer = GetHistoryListScrollViewer();
        if (scrollViewer is null)
            return;

        if (!_historyScrollRestorePending)
        {
            _historyScrollRestorePending = true;
            _historyScrollOffsetBeforeRefresh = _lastHistoryScrollOffset;
            _historyWasAtBottomBeforeRefresh = _lastHistoryWasAtBottom;
        }

        var version = Interlocked.Increment(ref _historyScrollRestoreVersion);
        Dispatcher.UIThread.Post(
            () => RestoreHistoryScrollPosition(version),
            DispatcherPriority.Background);
    }

    private void RestoreHistoryScrollPosition(long version)
    {
        if (version != Interlocked.Read(ref _historyScrollRestoreVersion)
            || !_historyScrollRestorePending)
        {
            return;
        }

        var scrollViewer = GetHistoryListScrollViewer();
        if (scrollViewer is null)
        {
            _historyScrollRestorePending = false;
            return;
        }

        var maximum = Math.Max(
            0d,
            scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var targetY = _historyWasAtBottomBeforeRefresh
            ? maximum
            : Math.Clamp(_historyScrollOffsetBeforeRefresh.Y, 0d, maximum);

        scrollViewer.Offset = new Vector(
            _historyScrollOffsetBeforeRefresh.X,
            targetY);
        _historyScrollRestorePending = false;
        CaptureHistoryScrollSnapshot(scrollViewer);
    }

    private ScrollViewer? GetHistoryListScrollViewer()
    {
        if (_historyListScrollViewer is not null)
            return _historyListScrollViewer;

        var scrollViewer = HistoryList
            .GetVisualDescendants()
            .OfType<ScrollViewer>()
            .FirstOrDefault();
        if (scrollViewer is null)
            return null;

        _historyListScrollViewer = scrollViewer;
        scrollViewer.ScrollChanged += HistoryListScrollViewer_ScrollChanged;
        CaptureHistoryScrollSnapshot(scrollViewer);
        return scrollViewer;
    }

    private void HistoryListScrollViewer_ScrollChanged(
        object? sender,
        ScrollChangedEventArgs e)
    {
        // Clear() 会先把集合计数变为 0，并可能立刻把 Offset 改成 0。
        // 这不是用户滚动，不能覆盖刷新前保存的真实位置。
        if (!_historyScrollRestorePending
            && _historyInteractionsViewModel?.FilteredDownloadHistory.Count > 0
            && sender is ScrollViewer scrollViewer)
        {
            CaptureHistoryScrollSnapshot(scrollViewer);
        }
    }

    private void CaptureHistoryScrollSnapshot(ScrollViewer scrollViewer)
    {
        _lastHistoryScrollOffset = scrollViewer.Offset;
        var maximum = Math.Max(
            0d,
            scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        _lastHistoryWasAtBottom = maximum - scrollViewer.Offset.Y <= 2d;
    }

    private void HistoryInteractionsWindowClosed(object? sender, EventArgs e)
    {
        StopHistoryAutoScroll();
        ReleaseHistoryFilterAfterDrag();

        if (_historyAutoScrollTimer is not null)
            _historyAutoScrollTimer.Tick -= HistoryAutoScrollTimer_Tick;
        if (_historyListScrollViewer is not null)
            _historyListScrollViewer.ScrollChanged -= HistoryListScrollViewer_ScrollChanged;
        if (_historyInteractionsViewModel is not null)
        {
            _historyInteractionsViewModel.FilteredDownloadHistory.CollectionChanged -=
                HistoryFilteredCollectionChanged;
        }
    }
}
