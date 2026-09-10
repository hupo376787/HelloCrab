using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using HelloCrab.Core.Remote.ViewModels;

namespace HelloCrab.Core.Remote.Views;

/// <summary>
/// Web / 手机远程端最终布局与交互修正：
/// - 网页历史列表高度随视口变化；滚动条拖动保持实时滚动，不使用 deferred scrolling。
/// - 历史搜索框垂直居中并增加清除按钮。
/// - 手机历史页进入时不自动聚焦搜索框；返回按钮透明并居中。
/// - 手机首页/历史/日志隐藏滚动条，日志区域增高。
/// - 宽屏中间列宽度只由视口决定，不再被长日志文本的 DesiredSize 撑开。
/// - 历史昵称在 recycling 后重新刷新 Twemoji，避免 Emoji 丢失。
/// </summary>
public partial class RemoteMainView
{
    private const double FinalWideLeftWidth = 430d;
    private const double FinalWideRightWidth = 390d;
    private const double FinalWideColumnSpacing = 28d;

    private static readonly IDisposable FinalRemoteUiPolishDataContextHandler =
        StyledElement.DataContextProperty.Changed.AddClassHandler<RemoteMainView>((view, _) =>
            Dispatcher.UIThread.Post(
                view.InitializeFinalRemoteUiPolish,
                DispatcherPriority.Background));

    private readonly HashSet<TextBox> _finalPolishedHistorySearchBoxes = new();
    private ListBox? _finalBrowserHistoryList;
    private ListBox? _finalMobileHistoryList;
    private Button? _finalMobileHistoryBackButton;
    private bool _finalRemoteUiPolishInitialized;
    private int _finalRemoteUiPolishInstallAttempts;

    private void InitializeFinalRemoteUiPolish()
    {
        if (_finalRemoteUiPolishInitialized)
            return;

        if (!_remoteHistoryUiInitialized
            || !_remoteHistoryPerformanceInitialized
            || !_remoteHistoryPolishInitialized
            || !_remoteHistoryUnifiedSearchInitialized)
        {
            RetryFinalRemoteUiPolishInitialization();
            return;
        }

        _finalBrowserHistoryList = FindRemoteHistoryList(_legacyHistoryCard?.Child);
        _finalMobileHistoryList = FindRemoteHistoryList(_mobileHistorySurface?.Child);

        ConfigureFinalHistorySearchBox(_browserHistorySearchBox, isMobile: false);
        ConfigureFinalHistorySearchBox(_mobileHistorySearchBox, isMobile: true);

        if (_finalBrowserHistoryList is not null)
            ConfigureFinalHistoryList(_finalBrowserHistoryList, isMobile: false);

        if (_finalMobileHistoryList is not null)
            ConfigureFinalHistoryList(_finalMobileHistoryList, isMobile: true);

        if (OperatingSystem.IsBrowser())
            ConfigureFinalBrowserLayout();

        if (DataContext is RemoteMainViewModel viewModel && viewModel.IsNativeMobileClient)
            ConfigureFinalMobileLayout();

        _finalRemoteUiPolishInitialized = true;
        SizeChanged += (_, _) => UpdateFinalResponsiveSizing();
        UpdateFinalResponsiveSizing();
    }

    private void RetryFinalRemoteUiPolishInitialization()
    {
        if (_finalRemoteUiPolishInstallAttempts++ >= 24)
            return;

        Dispatcher.UIThread.Post(
            InitializeFinalRemoteUiPolish,
            DispatcherPriority.Background);
    }

    private void ConfigureFinalHistorySearchBox(TextBox? searchBox, bool isMobile)
    {
        if (searchBox is null || !_finalPolishedHistorySearchBoxes.Add(searchBox))
            return;

        searchBox.VerticalContentAlignment = VerticalAlignment.Center;
        searchBox.Padding = new Thickness(12, 0, 42, 0);

        if (searchBox.Parent is not Panel parent)
            return;

        var originalIndex = parent.Children.IndexOf(searchBox);
        if (originalIndex < 0)
            return;

        var gridRow = Grid.GetRow(searchBox);
        var gridColumn = Grid.GetColumn(searchBox);
        var gridRowSpan = Grid.GetRowSpan(searchBox);
        var gridColumnSpan = Grid.GetColumnSpan(searchBox);

        parent.Children.RemoveAt(originalIndex);

        var clearButton = new Button
        {
            Content = "×",
            Width = 30,
            Height = 30,
            MinWidth = 30,
            MinHeight = 30,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 6, 0),
            FontSize = 18,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Focusable = false,
            IsVisible = !string.IsNullOrEmpty(searchBox.Text)
        };
        ToolTip.SetTip(clearButton, "清除搜索");
        clearButton.Click += (_, _) => searchBox.Text = string.Empty;

        searchBox.TextChanged += (_, _) =>
            clearButton.IsVisible = !string.IsNullOrEmpty(searchBox.Text);

        var wrapper = new Grid
        {
            MinHeight = 42,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        wrapper.Children.Add(searchBox);
        wrapper.Children.Add(clearButton);

        Grid.SetRow(wrapper, gridRow);
        Grid.SetColumn(wrapper, gridColumn);
        Grid.SetRowSpan(wrapper, gridRowSpan);
        Grid.SetColumnSpan(wrapper, gridColumnSpan);
        parent.Children.Insert(originalIndex, wrapper);

        if (!isMobile)
            return;

        // ShowMobileHistoryPage() 旧逻辑仍会调用 Focus()。进入页面时先让搜索框不可聚焦，
        // IsVisible 变为 true 后旧 Focus() 会失败；下一次 Dispatcher 再恢复可聚焦，
        // 用户真正点击输入框时仍可正常输入，但不会一进页面就弹软键盘。
        searchBox.Focusable = false;
    }

    private void ConfigureFinalHistoryList(ListBox list, bool isMobile)
    {
        // 继续使用虚拟化 + recycling，但减少预创建缓存量，降低 WASM 快速拖动时的布局压力。
        list.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel
        {
            CacheLength = isMobile ? 0.5 : 0.35
        });

        list.ItemTemplate = new FuncDataTemplate<RemoteHistoryItemViewModel>(
            (_, _) => CreateFinalRemoteHistoryRow(),
            supportsRecycling: true);

        ScrollViewer.SetIsDeferredScrollingEnabled(list, false);
        ScrollViewer.SetIsScrollChainingEnabled(list, false);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);

        if (isMobile)
            ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Hidden);
        else
            ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
    }

    private Control CreateFinalRemoteHistoryRow()
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
        nameText.DataContextChanged += (_, _) => QueueFinalRemoteHistoryNameRefresh(nameText);
        nameText.AttachedToVisualTree += (_, _) => QueueFinalRemoteHistoryNameRefresh(nameText);

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
            ColumnSpacing = 8,
            MinWidth = 0
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
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
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = grid
        };
        row.ContextMenu = CreateRecyclableRemoteHistoryContextMenu(row);
        return row;
    }

    private static void QueueFinalRemoteHistoryNameRefresh(TextBlock textBlock)
    {
        Dispatcher.UIThread.Post(
            () => RefreshRecyclableRemoteHistoryName(textBlock),
            DispatcherPriority.Loaded);
    }

    private void ConfigureFinalBrowserLayout()
    {
        if (_finalBrowserHistoryList is not null)
        {
            // 上一版打开 deferred scrolling 后，Thumb 拖动期间内容故意不动；
            // 这里恢复 Avalonia 默认的实时滚动行为。
            ScrollViewer.SetIsDeferredScrollingEnabled(_finalBrowserHistoryList, false);
        }

        if (_browserWideGrid is null
            || _browserWideGrid.ColumnDefinitions.Count < 3
            || _browserWideCenterColumn is null)
        {
            return;
        }

        _browserWideGrid.ColumnDefinitions[0].Width = new GridLength(FinalWideLeftWidth);
        _browserWideGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
        _browserWideGrid.ColumnDefinitions[1].MinWidth = 0;
        _browserWideGrid.ColumnDefinitions[2].Width = new GridLength(FinalWideRightWidth);

        _browserWideCenterColumn.MinWidth = 0;
        _browserWideCenterColumn.HorizontalAlignment = HorizontalAlignment.Stretch;
        _browserWideCenterColumn.ClipToBounds = true;

        if (_browserLogsCard is not null)
        {
            _browserLogsCard.MinWidth = 0;
            _browserLogsCard.HorizontalAlignment = HorizontalAlignment.Stretch;
            _browserLogsCard.ClipToBounds = true;

            if (FindRemoteHistoryList(_browserLogsCard) is { } logsList)
            {
                logsList.MinWidth = 0;
                logsList.HorizontalAlignment = HorizontalAlignment.Stretch;
                ScrollViewer.SetHorizontalScrollBarVisibility(logsList, ScrollBarVisibility.Disabled);

                var itemStyle = new Style(x => x.OfType<ListBoxItem>());
                itemStyle.Add(new Setter(
                    ListBoxItem.HorizontalContentAlignmentProperty,
                    HorizontalAlignment.Stretch));
                logsList.Styles.Add(itemStyle);
            }
        }
    }

    private void ConfigureFinalMobileLayout()
    {
        RootScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        RootScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;

        if (RootScrollViewer.Content is StackPanel rootStack)
        {
            var logCard = FindSectionCard(rootStack, "最新日志");
            if (FindRemoteHistoryList(logCard) is { } logList)
            {
                logList.Height = 420;
                ScrollViewer.SetVerticalScrollBarVisibility(logList, ScrollBarVisibility.Hidden);
                ScrollViewer.SetHorizontalScrollBarVisibility(logList, ScrollBarVisibility.Disabled);
            }
        }

        if (_finalMobileHistoryList is not null)
        {
            ScrollViewer.SetVerticalScrollBarVisibility(_finalMobileHistoryList, ScrollBarVisibility.Hidden);
            ScrollViewer.SetHorizontalScrollBarVisibility(_finalMobileHistoryList, ScrollBarVisibility.Disabled);
        }

        if (_mobileHistorySurface?.Child is Grid pageGrid)
        {
            var header = pageGrid.Children
                .OfType<Grid>()
                .FirstOrDefault(child => Grid.GetRow(child) == 0);
            _finalMobileHistoryBackButton = header?.Children.OfType<Button>().FirstOrDefault();
        }

        if (_finalMobileHistoryBackButton is not null)
        {
            var backButton = _finalMobileHistoryBackButton;
            backButton.Background = Brushes.Transparent;
            backButton.BorderBrush = Brushes.Transparent;
            backButton.BorderThickness = new Thickness(0);
            backButton.Padding = new Thickness(0);
            backButton.HorizontalContentAlignment = HorizontalAlignment.Center;
            backButton.VerticalContentAlignment = VerticalAlignment.Center;

            var glyph = new TextBlock
            {
                Text = "‹",
                FontSize = 28,
                LineHeight = 28,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            glyph.Bind(
                TextBlock.ForegroundProperty,
                new Binding(nameof(Button.Foreground)) { Source = backButton });
            backButton.Content = glyph;
        }

        if (_mobileHistoryOverlay is not null && _mobileHistorySearchBox is not null)
        {
            _mobileHistoryOverlay.PropertyChanged += (_, e) =>
            {
                if (e.Property != Visual.IsVisibleProperty)
                    return;

                var searchBox = _mobileHistorySearchBox;
                if (searchBox is null)
                    return;

                if (!_mobileHistoryOverlay.IsVisible)
                {
                    searchBox.Focusable = false;
                    return;
                }

                // 必须同步先禁用 Focusable，挡住 ShowMobileHistoryPage() 紧随其后的 Focus()。
                searchBox.Focusable = false;
                Dispatcher.UIThread.Post(
                    () =>
                    {
                        if (_mobileHistoryOverlay?.IsVisible != true)
                            return;

                        _finalMobileHistoryBackButton?.Focus();
                        searchBox.Focusable = true;
                    },
                    DispatcherPriority.Background);
            };
        }
    }

    private void UpdateFinalResponsiveSizing()
    {
        if (OperatingSystem.IsBrowser() && _finalBrowserHistoryList is not null)
        {
            // 右栏标题、搜索框、计数及卡片边距大约占 200px；剩余区域给历史列表。
            // 小窗口仍保留可用下限，大屏则随视口继续增高。
            var desiredHeight = Math.Clamp(Bounds.Height - 220d, 520d, 1200d);
            _finalBrowserHistoryList.Height = desiredHeight;
        }

        if (!OperatingSystem.IsBrowser()
            || !_browserWideLayoutActive
            || _browserWideCenterColumn is null)
        {
            return;
        }

        // 宽屏 Grid 的可用宽度由视口决定。显式限制中栏宽度，避免 ListBox 中一条超长日志
        // 通过 DesiredSize 反向撑宽 Star 列，造成“日志越长，中间列越宽”的视觉跳变。
        var contentWidth = Math.Min(
            Math.Max(0d, Bounds.Width - 36d),
            BrowserWideContentMaxWidth);
        var centerWidth = Math.Max(
            320d,
            contentWidth - FinalWideLeftWidth - FinalWideRightWidth - FinalWideColumnSpacing);

        _browserWideCenterColumn.Width = centerWidth;
        _browserWideCenterColumn.MaxWidth = centerWidth;
        _browserWideCenterColumn.MinWidth = 0;
    }
}
