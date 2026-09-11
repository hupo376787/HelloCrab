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
/// The right-side desktop drag glyph is kept visually identical, but opens the author menu on web.
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
        _finalBrowserHistoryList.ItemTemplate = new FuncDataTemplate<RemoteHistoryItemViewModel>(
            (_, _) => CreateBrowserDesktopHistoryRow(),
            supportsRecycling: true);
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

        var actionGlyph = new TextBlock
        {
            Text = "⋮⋮",
            FontSize = 17,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        actionGlyph.Classes.Add("browserDesktopHistorySecondary");
        ToolTip.SetTip(actionGlyph, "作者操作");
        actionGlyph.PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(actionGlyph);
            if (!point.Properties.IsLeftButtonPressed)
                return;

            if (actionGlyph.DataContext is RemoteHistoryItemViewModel)
                CreateRecyclableRemoteHistoryContextMenu(actionGlyph).Open(actionGlyph);

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
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        contentGrid.Children.Add(avatarBorder);
        Grid.SetColumn(textStack, 1);
        contentGrid.Children.Add(textStack);
        Grid.SetColumn(actionGlyph, 2);
        contentGrid.Children.Add(actionGlyph);

        var row = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(11),
            Margin = new Thickness(0, 0, 0, 10),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
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
