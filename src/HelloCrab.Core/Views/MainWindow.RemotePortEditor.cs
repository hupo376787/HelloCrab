using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HelloCrab.Core.ViewModels;

namespace HelloCrab.Core.Views;

public partial class MainWindow
{
    private static readonly IDisposable RemotePortEditorDataContextHandler =
        StyledElement.DataContextProperty.Changed.AddClassHandler<MainWindow>((window, _) =>
            Dispatcher.UIThread.Post(
                window.EnsureRemotePortEditor,
                DispatcherPriority.Loaded));

    private bool _remotePortEditorInstalled;
    private int _remotePortEditorInstallAttempts;

    private void EnsureRemotePortEditor()
    {
        if (_remotePortEditorInstalled || DataContext is not MainWindowViewModel viewModel)
            return;

        var portText = this
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(textBlock =>
                Grid.GetRow(textBlock) == 1
                && Grid.GetColumn(textBlock) == 1
                && textBlock.Parent is Grid grid
                && grid.RowDefinitions.Count == 3
                && grid.ColumnDefinitions.Count == 2
                && grid.Children
                    .OfType<StackPanel>()
                    .Any(panel =>
                        Grid.GetRow(panel) == 2
                        && Grid.GetColumn(panel) == 1));

        if (portText?.Parent is not Grid remoteSettingsGrid)
        {
            if (++_remotePortEditorInstallAttempts <= 8)
            {
                Dispatcher.UIThread.Post(
                    EnsureRemotePortEditor,
                    DispatcherPriority.Background);
            }
            return;
        }

        remoteSettingsGrid.Children.Remove(portText);
        var editor = new RemotePortEditor
        {
            DataContext = viewModel,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(editor, 1);
        Grid.SetColumn(editor, 1);
        remoteSettingsGrid.Children.Add(editor);

        _remotePortEditorInstalled = true;
    }
}
