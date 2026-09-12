using System.ComponentModel;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using HelloCrab.Core.Services.Localization;
using HelloCrab.Core.ViewModels;

namespace HelloCrab.Core.Views;

public partial class MainWindow
{
    private static readonly IDisposable AboutDataContextHandler =
        StyledElement.DataContextProperty.Changed.AddClassHandler<MainWindow>((window, _) =>
            Dispatcher.UIThread.Post(
                window.InstallAboutSection,
                DispatcherPriority.Loaded));

    private MainWindowViewModel? _aboutViewModel;
    private TextBlock? _aboutTitleText;
    private TextBlock? _aboutDescriptionText;
    private TextBlock? _aboutVersionText;
    private bool _aboutClosedHooked;

    private void InstallAboutSection()
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        if (_aboutViewModel is not null && !ReferenceEquals(_aboutViewModel, viewModel))
            _aboutViewModel.PropertyChanged -= AboutViewModel_PropertyChanged;

        if (!ReferenceEquals(_aboutViewModel, viewModel))
        {
            _aboutViewModel = viewModel;
            _aboutViewModel.PropertyChanged += AboutViewModel_PropertyChanged;
        }

        if (!_aboutClosedHooked)
        {
            _aboutClosedHooked = true;
            Closed += AboutWindow_Closed;
        }

        if (this.GetLogicalDescendants()
            .OfType<Control>()
            .Any(control => string.Equals(control.Name, "AboutHelloCrabCard", StringComparison.Ordinal)))
        {
            UpdateAboutSectionText();
            return;
        }

        // 左侧主设置区是窗口中唯一一个直接承载多张 card 的 ScrollViewer/StackPanel。
        // 通过结构定位而不是依赖具体卡片顺序，后续前面继续增减设置项时“关于”仍保持在最底部。
        var leftStack = this.GetLogicalDescendants()
            .OfType<ScrollViewer>()
            .Select(viewer => viewer.Content as StackPanel)
            .FirstOrDefault(stack => stack is not null
                                     && stack.Children.OfType<Border>().Count() >= 5);
        if (leftStack is null)
            return;

        var logo = new Border
        {
            Width = 68,
            Height = 68,
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(15),
            Background = new SolidColorBrush(Color.Parse("#247C3AED")),
            BorderBrush = new SolidColorBrush(Color.Parse("#557C3AED")),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new Image
            {
                Source = LoadAboutLogo(),
                Stretch = Stretch.Uniform
            }
        };

        _aboutTitleText = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeight.SemiBold
        };
        _aboutTitleText.Classes.Add("caption");

        var productName = new TextBlock
        {
            Text = "HelloCrab",
            FontSize = 21,
            FontWeight = FontWeight.Bold
        };

        _aboutDescriptionText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 292
        };
        _aboutDescriptionText.Classes.Add("caption");

        _aboutVersionText = new TextBlock
        {
            Text = ReadDisplayVersion(),
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };

        var versionBadge = new Border
        {
            Padding = new Thickness(8, 3),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.Parse("#267C3AED")),
            BorderBrush = new SolidColorBrush(Color.Parse("#557C3AED")),
            BorderThickness = new Thickness(1),
            Child = _aboutVersionText
        };

        var techText = new TextBlock
        {
            Text = "Avalonia · Playwright · FFmpeg · YOLO",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        techText.Classes.Add("caption");

        var meta = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 4, 0, 0),
            Children =
            {
                versionBadge,
                techText
            }
        };

        var github = new TextBlock
        {
            Text = "GitHub: https://github.com/hupo376787/HelloCrab",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        };
        github.Classes.Add("caption");

        var copyright = new TextBlock
        {
            Text = "© 2026 HelloCrab",
            Margin = new Thickness(0, 2, 0, 0)
        };
        copyright.Classes.Add("caption");

        var details = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                _aboutTitleText,
                productName,
                _aboutDescriptionText,
                meta,
                github,
                copyright
            }
        };

        var content = new Grid
        {
            ColumnSpacing = 14
        };
        content.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(68)));
        content.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        content.Children.Add(logo);
        Grid.SetColumn(details, 1);
        content.Children.Add(details);

        var card = new Border
        {
            Name = "AboutHelloCrabCard",
            Child = content
        };
        card.Classes.Add("card");

        leftStack.Children.Add(card);
        UpdateAboutSectionText();
    }

    private void AboutViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.SelectedLanguage))
            return;

        // SelectedLanguage 的 PropertyChanged 先于 LocalizationService.Apply 触发；
        // 排到当前 UI 消息之后再刷新，确保读到刚刚切换的新语言。
        Dispatcher.UIThread.Post(UpdateAboutSectionText, DispatcherPriority.Background);
    }

    private void UpdateAboutSectionText()
    {
        var code = LocalizationService.Current?.CurrentLanguageCode ?? "zh-CN";
        if (code.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            if (_aboutTitleText is not null)
                _aboutTitleText.Text = "About HelloCrab";
            if (_aboutDescriptionText is not null)
                _aboutDescriptionText.Text = "A cross-platform tool for collecting, downloading, and intelligently processing social media posts.";
        }
        else if (code.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
        {
            if (_aboutTitleText is not null)
                _aboutTitleText.Text = "HelloCrab について";
            if (_aboutDescriptionText is not null)
                _aboutDescriptionText.Text = "SNS 投稿の収集・ダウンロード・インテリジェント処理に対応するクロスプラットフォームツール。";
        }
        else
        {
            if (_aboutTitleText is not null)
                _aboutTitleText.Text = "关于 HelloCrab";
            if (_aboutDescriptionText is not null)
                _aboutDescriptionText.Text = "跨平台社交媒体作品采集、下载与智能处理工具。";
        }

        if (_aboutVersionText is not null)
            _aboutVersionText.Text = ReadDisplayVersion();
    }

    private static IImage? LoadAboutLogo()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://HelloCrab.Core/Assets/app-icon.png"));
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    private static string ReadDisplayVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(MainWindow).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion?
            .Split('+', 2)[0]
            .Trim();
        if (!string.IsNullOrWhiteSpace(informational))
            return $"v{informational}";

        var version = assembly.GetName().Version;
        if (version is null)
            return "v1.0.0";

        return $"v{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

    private void AboutWindow_Closed(object? sender, EventArgs e)
    {
        if (_aboutViewModel is not null)
            _aboutViewModel.PropertyChanged -= AboutViewModel_PropertyChanged;

        _aboutViewModel = null;
        Closed -= AboutWindow_Closed;
        _aboutClosedHooked = false;
    }
}
