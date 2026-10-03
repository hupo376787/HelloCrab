using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace HelloCrab.Desktop;

/// <summary>
/// 桌面端启动闪屏。只承载轻量 UI，实际启动阶段由 App 更新进度。
/// </summary>
public sealed class SplashWindow : Window
{
    private readonly ProgressBar _progressBar;
    private readonly TextBlock _statusText;
    private readonly TextBlock _detailText;
    private readonly TextBlock _percentText;
    private readonly Button _closeButton;

    public SplashWindow()
    {
        Title = "HelloCrab";
        Width = 560;
        Height = 330;
        MinWidth = 560;
        MinHeight = 330;
        MaxWidth = 560;
        MaxHeight = 330;
        CanResize = false;
        ShowInTaskbar = true;
        Topmost = true;
        WindowDecorations = WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.Parse("#FFF4F6FC"));

        var logo = new Image
        {
            Width = 68,
            Height = 68,
            Stretch = Stretch.Uniform,
            Source = LoadLogo(),
            VerticalAlignment = VerticalAlignment.Center
        };

        var title = new TextBlock
        {
            Text = "HelloCrab",
            FontSize = 28,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#FF172033"))
        };

        var subtitle = new TextBlock
        {
            Text = "正在准备应用，请稍候",
            Margin = new Thickness(0, 3, 0, 0),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#FF7A8498"))
        };

        var titleStack = new StackPanel
        {
            Spacing = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { title, subtitle }
        };

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { logo, titleStack }
        };

        _statusText = new TextBlock
        {
            Text = "正在启动 HelloCrab…",
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#FF26324A")),
            VerticalAlignment = VerticalAlignment.Center
        };

        _percentText = new TextBlock
        {
            Text = "0%",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#FF7C3AED")),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };

        var statusGrid = new Grid();
        statusGrid.ColumnDefinitions.Add(
            new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        statusGrid.ColumnDefinitions.Add(
            new ColumnDefinition(GridLength.Auto));
        statusGrid.Children.Add(_statusText);
        Grid.SetColumn(_percentText, 1);
        statusGrid.Children.Add(_percentText);

        _progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Height = 8,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Foreground = new SolidColorBrush(Color.Parse("#FF7C3AED")),
            Background = new SolidColorBrush(Color.Parse("#FFE3E6F0"))
        };

        _detailText = new TextBlock
        {
            Text = "准备应用运行环境",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#FF7A8498")),
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 18
        };

        _closeButton = new Button
        {
            Content = "关闭",
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(20, 7),
            Margin = new Thickness(0, 4, 0, 0)
        };
        _closeButton.Click += (_, _) => Close();

        var content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                header,
                new Border { Height = 10, Background = Brushes.Transparent },
                statusGrid,
                _progressBar,
                _detailText,
                _closeButton
            }
        };

        Content = new Border
        {
            Margin = new Thickness(1),
            Padding = new Thickness(34, 30),
            CornerRadius = new CornerRadius(18),
            Background = new SolidColorBrush(Color.Parse("#FFF9FAFE")),
            BorderBrush = new SolidColorBrush(Color.Parse("#337C3AED")),
            BorderThickness = new Thickness(1),
            Child = content
        };
    }

    public async Task WaitUntilPresentedAsync()
    {
        // 等待 Render 优先级队列执行，并额外给窗口系统一个很短的提交时间。
        // 这样最低 2 秒是从用户真正看到 Avalonia 闪屏后开始计算，而不是从
        // Opened 事件触发但 UI 线程仍在初始化时开始计算。
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Task.Delay(80);
    }

    public void SetProgress(double percent, string status, string? detail = null)
    {
        var value = Math.Clamp(percent, 0d, 100d);
        _progressBar.IsIndeterminate = false;
        _progressBar.Value = value;
        _percentText.Text = $"{value:0}%";
        _statusText.Text = status;
        _detailText.Text = detail ?? string.Empty;
    }

    public void ShowFailure(string message)
    {
        _progressBar.IsIndeterminate = false;
        _progressBar.Value = 100;
        _progressBar.Foreground = new SolidColorBrush(Color.Parse("#FFDC2626"));
        _percentText.Text = "!";
        _percentText.Foreground = new SolidColorBrush(Color.Parse("#FFDC2626"));
        _statusText.Text = "启动失败";
        _statusText.Foreground = new SolidColorBrush(Color.Parse("#FFB91C1C"));
        _detailText.Text = string.IsNullOrWhiteSpace(message)
            ? "初始化过程中发生未知错误。"
            : message;
        _detailText.Foreground = new SolidColorBrush(Color.Parse("#FFB91C1C"));
        _closeButton.IsVisible = true;
    }

    private static Bitmap? LoadLogo()
    {
        try
        {
            using var stream = AssetLoader.Open(
                new Uri("avares://HelloCrab.Desktop/Assets/app-icon.png"));
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }
}
