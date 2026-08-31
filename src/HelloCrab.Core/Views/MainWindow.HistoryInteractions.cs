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
    private long _historyFavoriteRestoreVersion;
    private long _historyScrollRestoreVersion;

    private void InstallHistoryInteractions()
    {
        if (_historyInteractionsInstalled
            || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        _historyInteractionsInstalled = true;
        _historyInteractionsViewModel = viewModel;
        viewModel.DownloadHistory.CollectionChanged += HistoryDownloadHistory_CollectionChanged;

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

        Closed += HistoryInteractionsWindowClosed;
        _ = GetHistoryListScrollViewer();
    }

    private void HistoryDownloadHistory_CollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        // 新作者固定插入主集合头部。Avalonia 的虚拟 ListBox 会在随后同步可见集合、
        // 重算布局时尝试保留旧锚点，某些情况下会把滚动位置直接推到列表末尾。
        // 在主集合先发生 Add、可见集合尚未改变的这个时点保存真实偏移，布局后再恢复。
        if (e.Action != NotifyCollectionChangedAction.Add
            || e.NewStartingIndex != 0
            || _showFavoritesOnly
            || _isHistoryDragging
            || _historyInteractionsViewModel is not { } viewModel
            || !string.IsNullOrWhiteSpace(viewModel.HistorySearchText)
            || GetHistoryListScrollViewer() is not { } scrollViewer)
        {
            return;
        }

        var savedOffset = scrollViewer.Offset;
        var version = Interlocked.Increment(ref _historyScrollRestoreVersion);
        Dispatcher.UIThread.Post(
            () => RestoreHistoryScrollOffset(version, savedOffset),
            DispatcherPriority.Render);
        Dispatcher.UIThread.Post(
            () => RestoreHistoryScrollOffset(version, savedOffset),
            DispatcherPriority.Background);
    }

    private void RestoreHistoryScrollOffset(long version, Vector savedOffset)
    {
        if (version != Interlocked.Read(ref _historyScrollRestoreVersion)
            || _showFavoritesOnly
            || _isHistoryDragging
            || _historyInteractionsViewModel is not { } viewModel
            || !string.IsNullOrWhiteSpace(viewModel.HistorySearchText)
            || GetHistoryListScrollViewer() is not { } scrollViewer)
        {
            return;
        }

        var maximumY = Math.Max(0d, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var restoredOffset = new Vector(
            savedOffset.X,
            Math.Clamp(savedOffset.Y, 0d, maximumY));
        if (scrollViewer.Offset != restoredOffset)
            scrollViewer.Offset = restoredOffset;
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

        // 拖动过程中 DownloadHistory 会发生 Move。收藏模式暂时停止异步筛选，
        // 避免当前被拖动的项目在移动过程中从可见集合里被重新排列。
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

    private ScrollViewer? GetHistoryListScrollViewer()
    {
        if (_historyListScrollViewer is not null)
            return _historyListScrollViewer;

        _historyListScrollViewer = HistoryList
            .GetVisualDescendants()
            .OfType<ScrollViewer>()
            .FirstOrDefault();
        return _historyListScrollViewer;
    }

    private void HistoryInteractionsWindowClosed(object? sender, EventArgs e)
    {
        StopHistoryAutoScroll();
        ReleaseHistoryFilterAfterDrag();

        if (_historyInteractionsViewModel is not null)
            _historyInteractionsViewModel.DownloadHistory.CollectionChanged -= HistoryDownloadHistory_CollectionChanged;

        if (_historyAutoScrollTimer is not null)
            _historyAutoScrollTimer.Tick -= HistoryAutoScrollTimer_Tick;
    }
}
