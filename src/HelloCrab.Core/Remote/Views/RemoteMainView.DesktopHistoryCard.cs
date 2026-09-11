using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using HelloCrab.Core.Remote.ViewModels;

namespace HelloCrab.Core.Remote.Views;

/// <summary>
/// Browser history cards mirror the desktop HistoryList item layout.
/// The right-side desktop drag area opens the author menu on web.
/// </summary>
public partial class RemoteMainView
{
    private static readonly IDisposable BrowserDesktopHistoryCardDataContextHandler =
        StyledElement.DataContextProperty.Changed.AddClassHandler<RemoteMainView>((view, _) =>
            Dispatcher.UIThread.Post(
                view.InitializeBrowserDesktopHistoryCard,
                DispatcherPriority.Background));

    private static readonly Lazy<Bitmap?> BrowserHistoryFallbackIcon = new(LoadBrowserHistoryFallbackIcon);

    private bool _browserDesktopHistoryCardInitialized;
    private int _browserDesktopHistoryCardInstallAttempts;

    private void InitializeBrowserDesktopHistoryCard()
    {
        if (_browserDesktopHistoryCardInitialized || !OperatingSystem.IsBrowser())
            return;

        if (!_finalRemoteUiPolishInitialized || _finalBrowserHistoryList is null)
        {
            if (_browserDesktopHistoryCardInstallAttempts++ < 32)
            {
                Dispatcher.UIThread.Post(
                    InitializeBrowserDesktopHistoryCard,
                    DispatcherPriority.Background);
            }
            return;
        }

        _browserDesktopHistoryCardInitialized = true;

        _finalBrowserHistoryList.Classes.Add("browserHistoryList");
        _finalBrowserHistoryList.Margin = new Thickness(0);
        _finalBrowserHistoryList.ItemTemplate = new FuncDataTemplate<RemoteHistoryItemViewModel>(
            (_, _) => CreateBrowserDesktopHistoryRow(),
            supportsRecycling: true);

        // 空的“操作结果”TextBlock 之前仍会占一行高度，造成计数文字与第一张卡片之间的大空白。
        if (_legacyHistoryCard?.Child is StackPanel browserHistoryStack)
            browserHistoryStack.Spacing = 5;

        if (_browserHistoryActionText is not null)
        {
            UpdateBrowserHistoryActionTextVisibility();
            _browserHistoryActionText.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBlock.TextProperty)
                    UpdateBrowserHistoryActionTextVisibility();
            };
        }
    }

    private void UpdateBrowserHistoryActionTextVisibility()
    {
        if (_browserHistoryActionText is not null)
            _browserHistoryActionText.IsVisible = !string.IsNullOrWhiteSpace(_browserHistoryActionText.Text);
    }

    private Control CreateBrowserDesktopHistoryRow()
    {
        var fallbackImage = new Image
        {
            Source = BrowserHistoryFallbackIcon.Value,
            Margin = new Thickness(8),
            Opacity = 0.82,
            Stretch = Stretch.Uniform,
            IsHitTestVisible = false
        };

        var avatar = new Image
        {
            Stretch = Stretch.UniformToFill,
            IsHitTestVisible = false
        };
        avatar.Bind(Image.SourceProperty, new Binding(nameof(RemoteHistoryItemViewModel.AvatarImage)));

        var avatarGrid = new Grid();
        avatarGrid.Children.Add(fallbackImage);
        avatarGrid.Children.Add(avatar);

        var avatarBorder = new Border
        {
            Width = 58,
            Height = 58,
            CornerRadius = new CornerRadius(29),
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.Parse("#24FFFFFF")),
            VerticalAlignment = VerticalAlignment.Center,
            Child = avatarGrid
        };

        var nameText = new TextBlock
        {
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        };
        nameText.Classes.Add("emojiText");
        nameText.Classes.Add("browserDesktopHistoryPrimary");
        nameText.DataContextChanged += (_, _) => QueueFinalRemoteHistoryNameRefresh(nameText);
        nameText.AttachedToVisualTree += (_, _) => QueueFinalRemoteHistoryNameRefresh(nameText);

        var platformText = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeight.SemiBold
        };
        platformText.Classes.Add("browserDesktopHistoryPrimary");
        platformText.Bind(TextBlock.TextProperty, new Binding(nameof(RemoteHistoryItemViewModel.PlatformDisplayText)));

        var platformBadge = new Border
        {
            Padding = new Thickness(7, 2),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.Parse("#267C3AED")),
            BorderBrush = new SolidColorBrush(Color.Parse("#557C3AED")),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = platformText
        };

        var titleGrid = new Grid { ColumnSpacing = 7 };
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
            MinWidth = 0
        });
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleGrid.Children.Add(nameText);
        Grid.SetColumn(platformBadge, 1);
        titleGrid.Children.Add(platformBadge);

        var uidText = CreateBrowserDesktopHistoryCaption();
        uidText.Bind(TextBlock.TextProperty, new Binding(nameof(RemoteHistoryItemViewModel.UidText)));
        uidText.TextTrimming = TextTrimming.CharacterEllipsis;

        var summaryText = CreateBrowserDesktopHistoryCaption();
        summaryText.Bind(TextBlock.TextProperty, new Binding(nameof(RemoteHistoryItemViewModel.ItemsSummary)));

        var updatedText = CreateBrowserDesktopHistoryCaption();
        updatedText.Bind(TextBlock.TextProperty, new Binding(nameof(RemoteHistoryItemViewModel.UpdatedAtText)));

        var textStack = new StackPanel
        {
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { titleGrid, uidText, summaryText, updatedText }
        };

        // 不再使用“⋮⋮ / ⋯”字体字符。WASM 字体缺少对应 glyph 时会显示方框，
        // 直接绘制三个圆点，任何浏览器字体环境下都不会乱码。
        var dots = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 3,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        for (var index = 0; index < 3; index++)
        {
            var dot = new Border
            {
                Width = 4,
                Height = 4,
                CornerRadius = new CornerRadius(2)
            };
            dot.Classes.Add("browserHistoryActionDot");
            dots.Children.Add(dot);
        }

        var actionHost = new Border
        {
            Width = 34,
            Height = 34,
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = dots
        };
        ToolTip.SetTip(actionHost, "作者操作");
        actionHost.PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(actionHost);
            if (!point.Properties.IsLeftButtonPressed)
                return;

            if (actionHost.DataContext is RemoteHistoryItemViewModel)
                CreateRecyclableRemoteHistoryContextMenu(actionHost).Open(actionHost);

            e.Handled = true;
        };

        var contentGrid = new Grid
        {
            ColumnSpacing = 11,
            MinWidth = 0
        };
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
            MinWidth = 0
        });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        contentGrid.Children.Add(avatarBorder);
        Grid.SetColumn(textStack, 1);
        contentGrid.Children.Add(textStack);
        Grid.SetColumn(actionHost, 2);
        contentGrid.Children.Add(actionHost);

        var row = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(11),
            Margin = new Thickness(0, 5),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Child = contentGrid
        };
        row.Classes.Add("browserDesktopHistoryItem");
        row.ContextMenu = CreateRecyclableRemoteHistoryContextMenu(row);

        return row;
    }

    private static TextBlock CreateBrowserDesktopHistoryCaption()
    {
        var text = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap
        };
        text.Classes.Add("browserDesktopHistorySecondary");
        return text;
    }

    private static Bitmap? LoadBrowserHistoryFallbackIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://HelloCrab.Core/Remote/Assets/app-icon.png"));
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }
}
