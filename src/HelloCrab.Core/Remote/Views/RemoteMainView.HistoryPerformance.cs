using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using HelloCrab.Core.Remote.ViewModels;

namespace HelloCrab.Core.Remote.Views;

/// <summary>
/// 历史列表滚动性能修正。
///
/// Avalonia 的 ListBox 默认使用 VirtualizingStackPanel，但复杂 DataTemplate 如果不支持
/// recycling，滚动过程中仍会频繁销毁/重建模板内容。这里在历史 UI 创建完成后替换成
/// 可回收模板，并给 VirtualizingStackPanel 增加一屏缓存；同时固定行高，减少滚动时的
/// Measure/Arrange 估算波动。历史列表滚动期间也暂缓主机快照中的日志/历史重建。
/// </summary>
public partial class RemoteMainView
{
    private static readonly IDisposable RemoteHistoryPerformanceDataContextHandler =
        StyledElement.DataContextProperty.Changed.AddClassHandler<RemoteMainView>((view, _) =>
            Dispatcher.UIThread.Post(
                view.InitializeRemoteHistoryPerformance,
                DispatcherPriority.Loaded));

    private bool _remoteHistoryPerformanceInitialized;
    private int _remoteHistoryPerformanceInstallAttempts;

    private void InitializeRemoteHistoryPerformance()
    {
        if (_remoteHistoryPerformanceInitialized)
            return;

        if (!_remoteHistoryUiInitialized)
        {
            RetryRemoteHistoryPerformanceInitialization();
            return;
        }

        var browserList = FindRemoteHistoryList(_legacyHistoryCard?.Child);
        var mobileList = FindRemoteHistoryList(_mobileHistorySurface?.Child);

        if (browserList is null && mobileList is null)
        {
            RetryRemoteHistoryPerformanceInitialization();
            return;
        }

        if (browserList is not null)
            OptimizeRemoteHistoryList(browserList);
        if (mobileList is not null)
            OptimizeRemoteHistoryList(mobileList);

        _remoteHistoryPerformanceInitialized = true;
        _remoteHistoryPerformanceInstallAttempts = 0;
    }

    private void RetryRemoteHistoryPerformanceInitialization()
    {
        if (_remoteHistoryPerformanceInstallAttempts++ >= 12)
            return;

        Dispatcher.UIThread.Post(
            InitializeRemoteHistoryPerformance,
            DispatcherPriority.Background);
    }

    private static ListBox? FindRemoteHistoryList(Control? root)
    {
        if (root is ListBox listBox)
            return listBox;

        if (root is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (FindRemoteHistoryList(child) is { } nested)
                    return nested;
            }
        }
        else if (root is ContentControl contentControl
                 && contentControl.Content is Control content)
        {
            return FindRemoteHistoryList(content);
        }
        else if (root is Border border && border.Child is { } child)
        {
            return FindRemoteHistoryList(child);
        }

        return null;
    }

    private void OptimizeRemoteHistoryList(ListBox list)
    {
        // Avalonia 12.1 的 VirtualizingStackPanel CacheLength 是以 viewport 为单位的缓存。
        // 保留上下各一屏已实现元素，减少快速滚动时频繁 Measure/Arrange 和模板切换。
        list.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel
        {
            CacheLength = 1.0
        });

        // FuncDataTemplate 在 supportsRecycling=true 时会直接复用已有 Control。
        // 因此模板内部不能捕获首次创建时的 item，所有命令都从当前 DataContext 取作者。
        list.ItemTemplate = new FuncDataTemplate<RemoteHistoryItemViewModel>(
            (_, _) => CreateRecyclableRemoteHistoryRow(),
            supportsRecycling: true);

        list.SetValue(ScrollViewer.IsScrollChainingEnabledProperty, false);
        list.AddHandler(ScrollViewer.ScrollChangedEvent, RemoteHistoryList_ScrollChanged);
    }

    private Control CreateRecyclableRemoteHistoryRow()
    {
        var avatarFallback = new TextBlock
        {
            Text = "人",
            FontSize = 17,
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
            Width = 50,
            Height = 50,
            CornerRadius = new CornerRadius(25),
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromArgb(32, 128, 128, 128)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = avatarGrid
        };

        var nameText = new TextBlock
        {
            FontWeight = FontWeight.SemiBold,
            FontSize = 15,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        };
        nameText.DataContextChanged += (_, _) =>
            Dispatcher.UIThread.Post(
                () => RefreshRecyclableRemoteHistoryName(nameText),
                DispatcherPriority.Background);

        var uidText = CreateRemoteHistoryPerformanceCaption();
        uidText.TextTrimming = TextTrimming.CharacterEllipsis;
        uidText.Bind(
            TextBlock.TextProperty,
            new Binding(nameof(RemoteHistoryItemViewModel.UidText)));

        var platformText = CreateRemoteHistoryPerformanceCaption();
        platformText.TextTrimming = TextTrimming.CharacterEllipsis;
        platformText.Bind(
            TextBlock.TextProperty,
            new Binding(nameof(RemoteHistoryItemViewModel.Platform))
            {
                StringFormat = "平台：{0}"
            });

        var summaryText = CreateRemoteHistoryPerformanceCaption();
        summaryText.Bind(
            TextBlock.TextProperty,
            new Binding(nameof(RemoteHistoryItemViewModel.ItemsSummary)));

        var updatedText = CreateRemoteHistoryPerformanceCaption();
        updatedText.Bind(
            TextBlock.TextProperty,
            new Binding(nameof(RemoteHistoryItemViewModel.UpdatedAtText)));

        var textStack = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                nameText,
                uidText,
                platformText,
                summaryText,
                updatedText
            }
        };

        var moreButton = new Button
        {
            Content = "⋯",
            Width = 38,
            Height = 38,
            MinWidth = 38,
            MinHeight = 38,
            Padding = new Thickness(0),
            FontSize = 20,
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
            ColumnSpacing = 10
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(avatarBorder);
        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);
        Grid.SetColumn(moreButton, 2);
        grid.Children.Add(moreButton);

        var row = new Border
        {
            Height = 104,
            MinHeight = 104,
            MaxHeight = 104,
            ClipToBounds = true,
            Padding = new Thickness(4, 8),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = grid
        };
        row.ContextMenu = CreateRecyclableRemoteHistoryContextMenu(row);
        return row;
    }

    private static TextBlock CreateRemoteHistoryPerformanceCaption()
    {
        var text = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        text.Classes.Add("caption");
        return text;
    }

    private static void RefreshRecyclableRemoteHistoryName(TextBlock textBlock)
    {
        if (textBlock.DataContext is not RemoteHistoryItemViewModel item)
            return;

        var name = item.UserName ?? string.Empty;

        // 不使用上一版 emojiText 的全局 DataContext handler，避免 recycling 时
        // “Emoji 作者 -> 普通作者”沿用旧 Inlines。每次都按当前 DataContext 重建当前昵称。
        textBlock.Inlines?.Clear();
        textBlock.ClearValue(TextBlock.TextProperty);

        if (OperatingSystem.IsBrowser() && ContainsRemoteHistoryEmoji(name))
        {
            RenderRemoteHistoryEmojiName(textBlock, name);
            return;
        }

        textBlock.Text = name;
    }

    private ContextMenu CreateRecyclableRemoteHistoryContextMenu(Control owner)
    {
        return new ContextMenu
        {
            ItemsSource = new object[]
            {
                CreateRecyclableRemoteHistoryMenuItem(
                    owner,
                    "查看作者主页",
                    item => ExecuteRemoteHistoryActionAsync(item, "open-home")),
                CreateRecyclableRemoteHistoryMenuItem(
                    owner,
                    "复制作者URL",
                    CopyRemoteHistoryAuthorUrlAsync),
                CreateRecyclableRemoteHistoryMenuItem(
                    owner,
                    "打开作者文件夹",
                    item => ExecuteRemoteHistoryActionAsync(item, "open-folder")),
                CreateRecyclableRemoteHistoryMenuItem(
                    owner,
                    "重新采集数据",
                    item => ExecuteRemoteHistoryActionAsync(item, "recollect")),
                new Separator(),
                CreateRecyclableRemoteHistoryMenuItem(
                    owner,
                    "从历史列表移除",
                    item =>
                    {
                        ShowRemoteHistoryDeleteDialog(item);
                        return Task.CompletedTask;
                    })
            }
        };
    }

    private static MenuItem CreateRecyclableRemoteHistoryMenuItem(
        Control owner,
        string header,
        Func<RemoteHistoryItemViewModel, Task> action)
    {
        var menuItem = new MenuItem { Header = header };
        menuItem.Click += async (_, _) =>
        {
            if (owner.DataContext is RemoteHistoryItemViewModel item)
                await action(item);
        };
        return menuItem;
    }

    private void RemoteHistoryList_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (DataContext is RemoteMainViewModel viewModel)
            viewModel.SetUserScrolling(true);

        // 与主页滚动共用同一个 idle timer。历史列表停止滚动 300ms 后，
        // 再一次性应用轮询期间暂存的历史/日志快照，避免滚动帧中途重建集合。
        _scrollIdleTimer.Stop();
        _scrollIdleTimer.Start();
    }
}
