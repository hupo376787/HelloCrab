using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using HelloCrab.Core.Remote.ViewModels;

namespace HelloCrab.Core.Remote.Views;

/// <summary>
/// 历史列表第二阶段性能与排版修正。
/// 重点针对 Browser/WASM 拖动滚动条时的重布局开销，并压缩作者行的无效上下留白。
/// </summary>
public partial class RemoteMainView
{
    private static readonly IDisposable RemoteHistoryPolishDataContextHandler =
        StyledElement.DataContextProperty.Changed.AddClassHandler<RemoteMainView>((view, _) =>
            Dispatcher.UIThread.Post(
                view.InitializeRemoteHistoryPolish,
                DispatcherPriority.Background));

    private bool _remoteHistoryPolishInitialized;
    private int _remoteHistoryPolishInstallAttempts;

    private void InitializeRemoteHistoryPolish()
    {
        if (_remoteHistoryPolishInitialized)
            return;

        if (!_remoteHistoryPerformanceInitialized)
        {
            RetryRemoteHistoryPolishInitialization();
            return;
        }

        var browserList = FindRemoteHistoryList(_legacyHistoryCard?.Child);
        var mobileList = FindRemoteHistoryList(_mobileHistorySurface?.Child);
        if (browserList is null && mobileList is null)
        {
            RetryRemoteHistoryPolishInitialization();
            return;
        }

        if (browserList is not null)
            PolishRemoteHistoryList(browserList, deferThumbScrolling: true);
        if (mobileList is not null)
            PolishRemoteHistoryList(mobileList, deferThumbScrolling: false);

        _remoteHistoryPolishInitialized = true;
    }

    private void RetryRemoteHistoryPolishInitialization()
    {
        if (_remoteHistoryPolishInstallAttempts++ >= 16)
            return;

        Dispatcher.UIThread.Post(
            InitializeRemoteHistoryPolish,
            DispatcherPriority.Background);
    }

    private void PolishRemoteHistoryList(ListBox list, bool deferThumbScrolling)
    {
        // CacheLength 太大时，拖动滚动条会一次实现多屏元素；缩到半屏仍能兼顾滚轮滚动。
        list.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel
        {
            CacheLength = 0.5
        });

        // Avalonia 官方为重内容提供 deferred scrolling：拖动 Thumb 时只移动滑块，
        // 松开后一次定位内容，避免 WASM 主线程在每个 pointer move 上反复布局。
        list.SetValue(ScrollViewer.IsDeferredScrollingEnabledProperty, deferThumbScrolling);
        list.SetValue(ScrollViewer.IsScrollChainingEnabledProperty, false);
        list.IsTextSearchEnabled = false;
        list.AutoScrollToSelectedItem = false;
        list.IsTabStop = false;

        var itemStyle = new Style(x => x.OfType<ListBoxItem>());
        itemStyle.Add(new Setter(ListBoxItem.PaddingProperty, new Thickness(0)));
        itemStyle.Add(new Setter(ListBoxItem.MarginProperty, new Thickness(0)));
        itemStyle.Add(new Setter(ListBoxItem.MinHeightProperty, 0d));
        itemStyle.Add(new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        list.Styles.Add(itemStyle);

        list.ItemTemplate = new FuncDataTemplate<RemoteHistoryItemViewModel>(
            (_, _) => CreateCompactRecyclableRemoteHistoryRow(),
            supportsRecycling: true);

        // 历史页不需要“选中”语义。及时清空选中项，避免 ListBox 为焦点项保留额外布局状态，
        // 同时去掉截图中大块的蓝色选中背景。
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedIndex >= 0)
                list.SelectedIndex = -1;
        };
    }

    private Control CreateCompactRecyclableRemoteHistoryRow()
    {
        var avatarFallback = new TextBlock
        {
            Text = "人",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        avatarFallback.Classes.Add("caption");

        var avatar = new Image
        {
            Stretch = Stretch.UniformToFill,
            IsHitTestVisible = false
        };
        avatar.Bind(
            Image.SourceProperty,
            new Binding(nameof(RemoteHistoryItemViewModel.AvatarImage)));

        var avatarGrid = new Grid();
        avatarGrid.Children.Add(avatarFallback);
        avatarGrid.Children.Add(avatar);

        var avatarBorder = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(22),
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromArgb(32, 128, 128, 128)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = avatarGrid
        };

        var nameText = new TextBlock
        {
            FontWeight = FontWeight.SemiBold,
            FontSize = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        };
        nameText.DataContextChanged += (_, _) =>
            RefreshRecyclableRemoteHistoryName(nameText);

        var uidText = CreateCompactHistoryCaption();
        uidText.Bind(
            TextBlock.TextProperty,
            new Binding(nameof(RemoteHistoryItemViewModel.UidText)));

        var platformText = CreateCompactHistoryCaption();
        platformText.Bind(
            TextBlock.TextProperty,
            new Binding(nameof(RemoteHistoryItemViewModel.Platform))
            {
                StringFormat = "平台：{0}"
            });

        var summaryText = CreateCompactHistoryCaption();
        summaryText.Bind(
            TextBlock.TextProperty,
            new Binding(nameof(RemoteHistoryItemViewModel.ItemsSummary)));

        var metaGrid = new Grid
        {
            ColumnSpacing = 8
        };
        metaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        metaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        metaGrid.Children.Add(platformText);
        Grid.SetColumn(summaryText, 1);
        metaGrid.Children.Add(summaryText);

        var updatedText = CreateCompactHistoryCaption();
        updatedText.Bind(
            TextBlock.TextProperty,
            new Binding(nameof(RemoteHistoryItemViewModel.UpdatedAtText)));

        var textStack = new StackPanel
        {
            Spacing = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                nameText,
                uidText,
                metaGrid,
                updatedText
            }
        };

        var moreButton = new Button
        {
            Content = "⋯",
            Width = 34,
            Height = 34,
            MinWidth = 34,
            MinHeight = 34,
            Padding = new Thickness(0),
            FontSize = 18,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(moreButton, "作者操作");
        moreButton.Classes.Add("action");
        moreButton.Classes.Add("secondary");
        moreButton.Click += (_, _) =>
        {
            if (moreButton.DataContext is RemoteHistoryItemViewModel)
                CreateRecyclableRemoteHistoryContextMenu(moreButton).Open(moreButton);
        };

        var grid = new Grid
        {
            ColumnSpacing = 8
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        grid.Children.Add(avatarBorder);
        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);
        Grid.SetColumn(moreButton, 2);
        grid.Children.Add(moreButton);

        var row = new Border
        {
            Height = 82,
            MinHeight = 82,
            MaxHeight = 82,
            ClipToBounds = true,
            Padding = new Thickness(3, 5),
            BorderBrush = new SolidColorBrush(Color.FromArgb(48, 128, 128, 128)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = grid
        };
        row.ContextMenu = CreateRecyclableRemoteHistoryContextMenu(row);
        return row;
    }

    private static TextBlock CreateCompactHistoryCaption()
    {
        var text = new TextBlock
        {
            FontSize = 11.5,
            LineHeight = 15,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        text.Classes.Add("caption");
        return text;
    }
}
