using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HelloCrab.Core.Remote.ViewModels;

namespace HelloCrab.Core.Remote.Views;

public partial class RemoteMainView : UserControl
{
    private readonly DispatcherTimer _scrollIdleTimer;

    public RemoteMainView()
    {
        InitializeComponent();

        // ScrollChanged 在手指拖动和惯性滚动期间都会持续触发。
        // 300ms 没有新滚动后，才让 ViewModel 一次性应用暂存的日志/历史快照。
        _scrollIdleTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _scrollIdleTimer.Tick += (_, _) =>
        {
            _scrollIdleTimer.Stop();
            if (DataContext is RemoteMainViewModel viewModel)
                viewModel.SetUserScrolling(false);
        };

        DetachedFromVisualTree += (_, _) =>
        {
            _scrollIdleTimer.Stop();
            if (DataContext is RemoteMainViewModel viewModel)
                viewModel.SetUserScrolling(false);
        };
    }


    private void StopCaptureButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RemoteMainViewModel viewModel
            || !viewModel.StopCaptureCommand.CanExecute(null))
        {
            return;
        }

        StopCaptureConfirmOverlay.IsVisible = true;
    }

    private void StopCaptureCancelButton_Click(object? sender, RoutedEventArgs e)
    {
        StopCaptureConfirmOverlay.IsVisible = false;
    }

    private async void StopCaptureConfirmButton_Click(object? sender, RoutedEventArgs e)
    {
        StopCaptureConfirmOverlay.IsVisible = false;

        if (DataContext is RemoteMainViewModel viewModel
            && viewModel.StopCaptureCommand.CanExecute(null))
        {
            await viewModel.StopCaptureCommand.ExecuteAsync(null);
        }
    }

    private void RootScrollViewer_OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (DataContext is RemoteMainViewModel viewModel)
            viewModel.SetUserScrolling(true);

        _scrollIdleTimer.Stop();
        _scrollIdleTimer.Start();
    }
}
