using System.ComponentModel;
using Avalonia;
using Avalonia.Threading;
using HelloCrab.Core.ViewModels;

namespace HelloCrab.Core.Views;

public partial class MainWindow
{
    private static readonly IDisposable WorkCountPushPlusDataContextHandler =
        StyledElement.DataContextProperty.Changed.AddClassHandler<MainWindow>((window, _) =>
            Dispatcher.UIThread.Post(
                window.InstallWorkCountPushPlusMonitor,
                DispatcherPriority.Loaded));

    private MainWindowViewModel? _workCountPushPlusViewModel;
    private bool _workCountPushPlusSent;
    private bool _wasCapturingForWorkCountPushPlus;
    private bool _workCountPushPlusClosedHooked;

    private void InstallWorkCountPushPlusMonitor()
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        if (_workCountPushPlusViewModel is not null)
            _workCountPushPlusViewModel.PropertyChanged -= WorkCountPushPlusViewModel_PropertyChanged;

        _workCountPushPlusViewModel = viewModel;
        _workCountPushPlusViewModel.PropertyChanged += WorkCountPushPlusViewModel_PropertyChanged;
        _wasCapturingForWorkCountPushPlus = viewModel.IsCapturing;
        _workCountPushPlusSent = false;

        if (!_workCountPushPlusClosedHooked)
        {
            _workCountPushPlusClosedHooked = true;
            Closed += WorkCountPushPlusWindow_Closed;
        }
    }

    private void WorkCountPushPlusViewModel_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (sender is not MainWindowViewModel viewModel)
            return;

        if (e.PropertyName == nameof(MainWindowViewModel.IsCapturing))
        {
            if (viewModel.IsCapturing && !_wasCapturingForWorkCountPushPlus)
                _workCountPushPlusSent = false;

            _wasCapturingForWorkCountPushPlus = viewModel.IsCapturing;
            return;
        }

        if (e.PropertyName is nameof(MainWindowViewModel.TotalWorkCountText)
            or nameof(MainWindowViewModel.DiscoveredCount)
            or nameof(MainWindowViewModel.PushPlusToken))
        {
            TrySendWorkCountPushPlus(viewModel);
        }
    }

    private void TrySendWorkCountPushPlus(MainWindowViewModel viewModel)
    {
        if (_workCountPushPlusSent
            || !viewModel.IsCapturing
            || string.IsNullOrWhiteSpace(viewModel.PushPlusToken))
        {
            return;
        }

        int? actualTotalWorkCount = null;
        if (int.TryParse(viewModel.TotalWorkCountText, out var totalWorkCount))
        {
            // 平台已经明确给出作品总数时，只在总数真正超过 500 时提醒。
            if (totalWorkCount <= 500)
                return;

            actualTotalWorkCount = totalWorkCount;
        }
        else
        {
            // 没有作品总数字段的平台，使用分页累计发现数作为兜底阈值。
            if (viewModel.DiscoveredCount < 500)
                return;
        }

        // 先置位再异步发送，避免 TotalWorkCount / DiscoveredCount 连续变更导致重复通知。
        _workCountPushPlusSent = true;
        _ = viewModel.SendWorkCountExceededPushPlusAsync(actualTotalWorkCount);
    }

    private void WorkCountPushPlusWindow_Closed(object? sender, EventArgs e)
    {
        if (_workCountPushPlusViewModel is not null)
            _workCountPushPlusViewModel.PropertyChanged -= WorkCountPushPlusViewModel_PropertyChanged;

        _workCountPushPlusViewModel = null;
        Closed -= WorkCountPushPlusWindow_Closed;
        _workCountPushPlusClosedHooked = false;
    }
}
