using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HelloCrab.Core.ViewModels;

namespace HelloCrab.Core.Views;

public partial class MainWindow
{
    private MainWindowViewModel? _diskSpacePromptViewModel;
    private TaskCompletionSource<bool>? _diskSpaceDecision;

    private void BindDiskSpacePrompt()
    {
        UnbindDiskSpacePrompt();
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        _diskSpacePromptViewModel = viewModel;
        viewModel.DiskSpaceRetryPromptAsync = ShowDiskSpaceRetryPromptAsync;
    }

    private void UnbindDiskSpacePrompt()
    {
        if (_diskSpacePromptViewModel is not null)
            _diskSpacePromptViewModel.DiskSpaceRetryPromptAsync = null;
        _diskSpacePromptViewModel = null;
    }

    private async Task<bool> ShowDiskSpaceRetryPromptAsync(CancellationToken cancellationToken)
    {
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _diskSpaceDecision?.TrySetResult(false);
            _diskSpaceDecision = decision;
            DiskSpaceOverlay.IsVisible = true;
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            if (!IsVisible)
                Show();
            Activate();
        });

        using var cancellationRegistration = cancellationToken.Register(
            () => decision.TrySetCanceled(cancellationToken));
        try
        {
            return await decision.Task;
        }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_diskSpaceDecision, decision))
                    return;
                DiskSpaceOverlay.IsVisible = false;
                _diskSpaceDecision = null;
            });
        }
    }

    private void DiskSpaceRetryButton_Click(object? sender, RoutedEventArgs e)
        => _diskSpaceDecision?.TrySetResult(true);

    private void DiskSpaceStopButton_Click(object? sender, RoutedEventArgs e)
        => _diskSpaceDecision?.TrySetResult(false);

    private void CancelDiskSpacePrompt()
        => _diskSpaceDecision?.TrySetResult(false);
}
