using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using HelloCrab.Core.Remote.ViewModels;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace HelloCrab.Core.Remote.Views;

/// <summary>
/// Shared About/footer polish for Browser, Android and iOS, plus native-mobile theme icon alignment.
/// </summary>
public partial class RemoteMainView
{
    private const string RemoteGitHubHome = "https://github.com/hupo376787/HelloCrab";
    private const string RemoteGitHubTextName = "RemoteGitHubHomeText";

    private static readonly IDisposable RemoteAboutPolishDataContextHandler =
        StyledElement.DataContextProperty.Changed.AddClassHandler<RemoteMainView>((view, _) =>
            Dispatcher.UIThread.Post(
                view.InitializeRemoteAboutPolish,
                DispatcherPriority.Loaded));

    private bool _remoteAboutPolishInitialized;
    private int _remoteAboutPolishInstallAttempts;
    private Button? _nativeThemeButton;
    private Grid? _nativeThemeIconHost;
    private RemoteMainViewModel? _nativeThemeViewModel;

    private void InitializeRemoteAboutPolish()
    {
        if (DataContext is not RemoteMainViewModel viewModel
            || RootScrollViewer?.Content is not StackPanel rootStack)
        {
            RetryRemoteAboutPolishInitialization();
            return;
        }

        var footerReady = EnsureRemoteGitHubHome(rootStack);
        var themeReady = !viewModel.IsNativeMobileClient || EnsureNativeThemeButtonCentered(viewModel);
        if (!footerReady || !themeReady)
        {
            RetryRemoteAboutPolishInitialization();
            return;
        }

        _remoteAboutPolishInitialized = true;
        _remoteAboutPolishInstallAttempts = 0;
    }

    private void RetryRemoteAboutPolishInitialization()
    {
        if (_remoteAboutPolishInitialized || _remoteAboutPolishInstallAttempts++ >= 32)
            return;

        Dispatcher.UIThread.Post(
            InitializeRemoteAboutPolish,
            DispatcherPriority.Background);
    }

    private bool EnsureRemoteGitHubHome(StackPanel rootStack)
    {
        if (this.GetLogicalDescendants()
            .OfType<TextBlock>()
            .Any(text => string.Equals(text.Name, RemoteGitHubTextName, StringComparison.Ordinal)))
        {
            return true;
        }

        var footer = rootStack.Children
            .OfType<StackPanel>()
            .FirstOrDefault(panel => panel.Children
                .OfType<TextBlock>()
                .Any(text => text.Text?.StartsWith(
                    "Web 和手机端是远程控制器",
                    StringComparison.Ordinal) == true));
        if (footer is null)
            return false;

        var github = new TextBlock
        {
            Name = RemoteGitHubTextName,
            Text = $"GitHub: {RemoteGitHubHome}",
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        github.Classes.Add("caption");

        footer.Children.Insert(Math.Min(1, footer.Children.Count), github);
        return true;
    }

    private bool EnsureNativeThemeButtonCentered(RemoteMainViewModel viewModel)
    {
        if (_nativeThemeButton is null)
        {
            _nativeThemeButton = this.GetLogicalDescendants()
                .OfType<Button>()
                .FirstOrDefault(button => button.Classes.Contains("themeIcon"));
        }

        if (_nativeThemeButton is null)
            return false;

        if (_nativeThemeIconHost is null)
        {
            _nativeThemeButton.Padding = new Thickness(0);
            _nativeThemeButton.HorizontalContentAlignment = HorizontalAlignment.Center;
            _nativeThemeButton.VerticalContentAlignment = VerticalAlignment.Center;

            _nativeThemeIconHost = new Grid
            {
                Width = 22,
                Height = 22,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            _nativeThemeButton.Content = _nativeThemeIconHost;
        }

        if (!ReferenceEquals(_nativeThemeViewModel, viewModel))
        {
            if (_nativeThemeViewModel is not null)
                _nativeThemeViewModel.PropertyChanged -= NativeThemeViewModel_PropertyChanged;

            _nativeThemeViewModel = viewModel;
            _nativeThemeViewModel.PropertyChanged += NativeThemeViewModel_PropertyChanged;
        }

        RefreshNativeThemeIcon();
        return true;
    }

    private void NativeThemeViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RemoteMainViewModel.IsRemoteDarkTheme))
            return;

        if (Dispatcher.UIThread.CheckAccess())
            RefreshNativeThemeIcon();
        else
            Dispatcher.UIThread.Post(RefreshNativeThemeIcon, DispatcherPriority.Background);
    }

    private void RefreshNativeThemeIcon()
    {
        if (_nativeThemeIconHost is null || _nativeThemeViewModel is null)
            return;

        _nativeThemeIconHost.Children.Clear();

        var canvas = new Canvas
        {
            Width = 24,
            Height = 24,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };

        if (_nativeThemeViewModel.IsRemoteDarkTheme)
        {
            var center = new Ellipse
            {
                Width = 10,
                Height = 10,
                Stroke = Brushes.White,
                StrokeThickness = 2
            };
            Canvas.SetLeft(center, 7);
            Canvas.SetTop(center, 7);
            canvas.Children.Add(center);

            canvas.Children.Add(new ShapePath
            {
                Data = Geometry.Parse(
                    "M12,1 L12,4 M12,20 L12,23 M1,12 L4,12 M20,12 L23,12 M4.22,4.22 L6.34,6.34 M17.66,17.66 L19.78,19.78 M4.22,19.78 L6.34,17.66 M17.66,6.34 L19.78,4.22"),
                Stroke = Brushes.White,
                StrokeThickness = 2,
                StrokeLineCap = PenLineCap.Round
            });
        }
        else
        {
            // Put the moon inside a fixed 24x24 coordinate space instead of Viewbox-sizing the
            // Path from its own non-zero bounds. The tiny translation compensates the geometry's
            // optical offset so the visible crescent is centered inside the 44x44 button.
            canvas.Children.Add(new ShapePath
            {
                Data = Geometry.Parse(
                    "M20.2,15.3 C18.8,16 17.2,16.4 15.5,16.4 C11.1,16.4 7.6,12.9 7.6,8.5 C7.6,6.8 8,5.2 8.7,3.8 C5.1,5.1 2.5,8.5 2.5,12.5 C2.5,17.5 6.5,21.5 11.5,21.5 C15.5,21.5 18.9,18.9 20.2,15.3 Z"),
                Fill = Brushes.White,
                RenderTransform = new TranslateTransform(0.65, -0.65)
            });
        }

        _nativeThemeIconHost.Children.Add(new Viewbox
        {
            Width = 22,
            Height = 22,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = canvas,
            IsHitTestVisible = false
        });
    }
}
