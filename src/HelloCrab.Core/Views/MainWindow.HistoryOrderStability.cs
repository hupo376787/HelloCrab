using System.Collections.Specialized;
using Avalonia;
using Avalonia.Threading;
using HelloCrab.Core.Models;
using HelloCrab.Core.ViewModels;

namespace HelloCrab.Core.Views;

public partial class MainWindow
{
    private static readonly IDisposable HistoryOrderDataContextHandler =
        StyledElement.DataContextProperty.Changed.AddClassHandler<MainWindow>((window, _) =>
            Dispatcher.UIThread.Post(
                window.InstallStableHistoryOrder,
                DispatcherPriority.Loaded));

    private MainWindowViewModel? _historyOrderViewModel;
    private readonly List<int> _stableHistoryOrder = [];
    private long _historyOrderReconcileVersion;
    private bool _isRestoringHistoryOrder;

    private void InstallStableHistoryOrder()
    {
        if (ReferenceEquals(_historyOrderViewModel, DataContext))
            return;

        if (_historyOrderViewModel is not null)
        {
            _historyOrderViewModel.DownloadHistory.CollectionChanged -=
                StableHistory_CollectionChanged;
        }

        _historyOrderViewModel = DataContext as MainWindowViewModel;
        _stableHistoryOrder.Clear();
        Interlocked.Increment(ref _historyOrderReconcileVersion);

        if (_historyOrderViewModel is null)
            return;

        _historyOrderViewModel.DownloadHistory.CollectionChanged +=
            StableHistory_CollectionChanged;
        QueueHistoryOrderReconcile();
    }

    private void StableHistory_CollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        if (_isRestoringHistoryOrder || _historyOrderViewModel is null)
            return;

        // MoveHistoryItemPreview 只在真实鼠标拖动期间修改完整历史集合。
        // 用户拖动产生的顺序立即成为新的稳定顺序，随后由现有逻辑写入 History.json。
        if (_isHistoryDragging)
        {
            RememberCurrentHistoryOrder();
            Interlocked.Increment(ref _historyOrderReconcileVersion);
            return;
        }

        // 下载历史服务可能连续执行 Insert/Move。等本轮同步全部完成后再统一判断，
        // 并在界面绘制前恢复已有作者原来的位置，避免列表肉眼可见地跳动。
        QueueHistoryOrderReconcile();
    }

    private void QueueHistoryOrderReconcile()
    {
        var version = Interlocked.Increment(ref _historyOrderReconcileVersion);
        Dispatcher.UIThread.Post(
            () => ReconcileHistoryOrder(version),
            DispatcherPriority.Render);
    }

    private void ReconcileHistoryOrder(long version)
    {
        if (version != Interlocked.Read(ref _historyOrderReconcileVersion)
            || _historyOrderViewModel is not { } viewModel
            || _isHistoryDragging
            || _isRestoringHistoryOrder)
        {
            return;
        }

        var currentIds = viewModel.DownloadHistory.Select(item => item.Id).ToArray();
        if (_stableHistoryOrder.Count == 0)
        {
            _stableHistoryOrder.AddRange(currentIds);
            return;
        }

        var currentSet = currentIds.ToHashSet();
        var stableSet = _stableHistoryOrder.ToHashSet();

        // 新作者仍保持服务当前的插入位置（目前为列表顶部）；已有作者严格按照
        // 用户最后一次手动调整后的顺序排列。已删除作者会自然从稳定顺序中移除。
        var desiredIds = currentIds
            .Where(id => !stableSet.Contains(id))
            .Concat(_stableHistoryOrder.Where(currentSet.Contains))
            .ToArray();

        if (currentIds.SequenceEqual(desiredIds))
        {
            _stableHistoryOrder.Clear();
            _stableHistoryOrder.AddRange(currentIds);
            return;
        }

        _isRestoringHistoryOrder = true;
        try
        {
            for (var targetIndex = 0; targetIndex < desiredIds.Length; targetIndex++)
            {
                var desiredId = desiredIds[targetIndex];
                var currentIndex = viewModel.DownloadHistory
                    .Select((item, index) => (item.Id, index))
                    .FirstOrDefault(pair => pair.Id == desiredId)
                    .index;

                if (currentIndex != targetIndex)
                    viewModel.DownloadHistory.Move(currentIndex, targetIndex);
            }

            for (var index = 0; index < viewModel.DownloadHistory.Count; index++)
                viewModel.DownloadHistory[index].SortOrder = index;

            _stableHistoryOrder.Clear();
            _stableHistoryOrder.AddRange(desiredIds);
        }
        finally
        {
            _isRestoringHistoryOrder = false;
        }

        // 覆盖下载服务刚才自动写入的“最近作者置顶”顺序。
        // 此后只有用户拖动、添加新作者或删除作者才会真正改变持久化顺序。
        _ = viewModel.PersistHistoryOrderAsync();
    }

    private void RememberCurrentHistoryOrder()
    {
        if (_historyOrderViewModel is null)
            return;

        _stableHistoryOrder.Clear();
        _stableHistoryOrder.AddRange(
            _historyOrderViewModel.DownloadHistory.Select(item => item.Id));
    }
}
