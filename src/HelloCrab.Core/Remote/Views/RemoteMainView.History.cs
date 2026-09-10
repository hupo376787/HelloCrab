using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using HelloCrab.Core.Contracts;
using HelloCrab.Core.Remote.Services;
using HelloCrab.Core.Remote.ViewModels;

namespace HelloCrab.Core.Remote.Views;

public partial class RemoteMainView
{
    private static readonly IDisposable RemoteHistoryDataContextHandler =
        StyledElement.DataContextProperty.Changed.AddClassHandler<RemoteMainView>((view, _) =>
            Dispatcher.UIThread.Post(
                view.InitializeRemoteHistoryUi,
                DispatcherPriority.Loaded));

    private readonly ObservableCollection<RemoteHistoryItemViewModel> _remoteFilteredHistory = new();
    private readonly HashSet<RemoteHistoryItemViewModel> _remoteHistorySubscriptions = new();

    private RemoteMainViewModel? _remoteHistoryViewModel;
    private bool _remoteHistoryUiInitialized;
    private bool _syncingHistorySearch;
    private string _remoteHistorySearchText = string.Empty;
    private Border? _legacyHistoryCard;
    private TextBox? _browserHistorySearchBox;
    private TextBox? _mobileHistorySearchBox;
    private TextBlock? _browserHistoryCountText;
    private TextBlock? _mobileHistoryCountText;
    private TextBlock? _browserHistoryActionText;
    private TextBlock? _mobileHistoryActionText;
    private Grid? _mobileHistoryOverlay;
    private Border? _mobileHistorySurface;
    private Grid? _remoteHistoryDeleteOverlay;
    private Border? _remoteHistoryDeleteSurface;
    private TextBlock? _remoteHistoryDeleteAuthorText;
    private TextBlock? _remoteHistoryDeletePathText;
    private RemoteHistoryItemViewModel? _pendingRemoteHistoryDeleteItem;

    private void InitializeRemoteHistoryUi()
    {
        if (DataContext is not RemoteMainViewModel viewModel
            || RootScrollViewer?.Content is not StackPanel rootStack)
        {
            return;
        }

        if (!_remoteHistoryUiInitialized)
        {
            _legacyHistoryCard = FindSectionCard(rootStack, "下载历史");
            if (_legacyHistoryCard is not null)
            {
                if (viewModel.IsBrowserClient)
                    _legacyHistoryCard.Child = CreateBrowserHistoryContent();

                // 手机端改为独立历史页面，避免主控制页继续渲染一整段历史列表。
                _legacyHistoryCard.IsVisible = viewModel.IsBrowserClient;
            }

            if (viewModel.IsNativeMobileClient)
            {
                AddMobileHistoryButton(rootStack);
                AddMobileHistoryOverlay();
            }

            AddRemoteHistoryDeleteOverlay();
            _remoteHistoryUiInitialized = true;
        }

        BindRemoteHistoryViewModel(viewModel);
    }

    private static Border? FindSectionCard(StackPanel rootStack, string title)
    {
        foreach (var border in rootStack.Children.OfType<Border>())
        {
            if (border.Child is not StackPanel stack)
                continue;

            if (stack.Children.OfType<TextBlock>().Any(text =>
                    string.Equals(text.Text, title, StringComparison.Ordinal)))
            {
                return border;
            }
        }

        return null;
    }

    private void AddMobileHistoryButton(StackPanel rootStack)
    {
        var hostCard = FindSectionCard(rootStack, "主机操作");
        if (hostCard?.Child is not StackPanel hostStack
            || hostStack.Children.OfType<Button>().Any(button =>
                string.Equals(button.Content?.ToString(), "历史列表", StringComparison.Ordinal)))
        {
            return;
        }

        var button = new Button
        {
            Content = "历史列表",
            Height = 46,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        button.Classes.Add("action");
        button.Classes.Add("secondary");
        button.Classes.Add("hostAction");
        button.Click += (_, _) => ShowMobileHistoryPage();

        var insertIndex = Math.Max(1, hostStack.Children.Count - 1);
        hostStack.Children.Insert(insertIndex, button);
    }

    private StackPanel CreateBrowserHistoryContent()
    {
        _browserHistorySearchBox = CreateHistorySearchBox();
        _browserHistoryCountText = CreateCaptionTextBlock();
        _browserHistoryActionText = CreateCaptionTextBlock();

        var list = CreateHistoryList();
        list.Height = 430;

        return new StackPanel
        {
            Spacing = 9,
            Children =
            {
                new TextBlock
                {
                    Text = "下载历史",
                    FontSize = 17,
                    FontWeight = FontWeight.SemiBold
                },
                _browserHistorySearchBox,
                _browserHistoryCountText,
                _browserHistoryActionText,
                list
            }
        };
    }

    private TextBox CreateHistorySearchBox()
    {
        var searchBox = new TextBox
        {
            PlaceholderText = "请输入作者名字，id，平台",
            MinHeight = 42,
            Text = _remoteHistorySearchText
        };
        searchBox.TextChanged += HistorySearchBox_TextChanged;
        return searchBox;
    }

    private ListBox CreateHistoryList()
    {
        return new ListBox
        {
            ItemsSource = _remoteFilteredHistory,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ItemTemplate = new FuncDataTemplate<RemoteHistoryItemViewModel>(
                (item, _) => CreateHistoryRow(item),
                supportsRecycling: false)
        };
    }

    private Control CreateHistoryRow(RemoteHistoryItemViewModel item)
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
            Stretch = Stretch.UniformToFill
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
            VerticalAlignment = VerticalAlignment.Top,
            Child = avatarGrid
        };

        var nameText = new TextBlock
        {
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        nameText.Classes.Add("emojiText");
        nameText.Bind(
            TextBlock.TextProperty,
            new Binding(nameof(RemoteHistoryItemViewModel.UserName)));

        var uidText = CreateCaptionTextBlock();
        uidText.TextTrimming = TextTrimming.CharacterEllipsis;
        uidText.Bind(
            TextBlock.TextProperty,
            new Binding(nameof(RemoteHistoryItemViewModel.UidText)));

        var platformText = CreateCaptionTextBlock();
        platformText.Text = string.IsNullOrWhiteSpace(item.Platform)
            ? string.Empty
            : $"平台：{item.Platform}";
        platformText.TextTrimming = TextTrimming.CharacterEllipsis;

        var summaryText = CreateCaptionTextBlock();
        summaryText.Bind(
            TextBlock.TextProperty,
            new Binding(nameof(RemoteHistoryItemViewModel.ItemsSummary)));

        var updatedText = CreateCaptionTextBlock();
        updatedText.Bind(
            TextBlock.TextProperty,
            new Binding(nameof(RemoteHistoryItemViewModel.UpdatedAtText)));

        var textStack = new StackPanel
        {
            Spacing = 2,
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
            Width = 40,
            Height = 40,
            MinWidth = 40,
            Padding = new Thickness(0),
            FontSize = 21,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(moreButton, "作者操作");
        moreButton.Classes.Add("action");
        moreButton.Classes.Add("secondary");
        moreButton.Click += (_, _) => CreateHistoryContextMenu(item).Open(moreButton);

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
            DataContext = item,
            Padding = new Thickness(4, 10),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = grid
        };
        row.ContextMenu = CreateHistoryContextMenu(item);
        return row;
    }

    private ContextMenu CreateHistoryContextMenu(RemoteHistoryItemViewModel item)
    {
        var openHome = CreateHistoryMenuItem(
            "查看作者主页",
            async () => await ExecuteRemoteHistoryActionAsync(item, "open-home"));
        var copyUrl = CreateHistoryMenuItem(
            "复制作者URL",
            async () => await CopyRemoteHistoryAuthorUrlAsync(item));
        var openFolder = CreateHistoryMenuItem(
            "打开作者文件夹",
            async () => await ExecuteRemoteHistoryActionAsync(item, "open-folder"));
        var recollect = CreateHistoryMenuItem(
            "重新采集数据",
            async () => await ExecuteRemoteHistoryActionAsync(item, "recollect"));
        var remove = CreateHistoryMenuItem(
            "从历史列表移除",
            () =>
            {
                ShowRemoteHistoryDeleteDialog(item);
                return Task.CompletedTask;
            });

        return new ContextMenu
        {
            ItemsSource = new object[]
            {
                openHome,
                copyUrl,
                openFolder,
                recollect,
                new Separator(),
                remove
            }
        };
    }

    private static MenuItem CreateHistoryMenuItem(string header, Func<Task> action)
    {
        var menuItem = new MenuItem { Header = header };
        menuItem.Click += async (_, _) => await action();
        return menuItem;
    }

    private void AddMobileHistoryOverlay()
    {
        if (_mobileHistoryOverlay is not null
            || Content is not Grid rootGrid)
        {
            return;
        }

        _mobileHistorySearchBox = CreateHistorySearchBox();
        _mobileHistoryCountText = CreateCaptionTextBlock();
        _mobileHistoryActionText = CreateCaptionTextBlock();

        var backButton = new Button
        {
            Content = "‹",
            Width = 42,
            Height = 42,
            MinWidth = 42,
            Padding = new Thickness(0),
            FontSize = 30,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        backButton.Classes.Add("action");
        backButton.Classes.Add("secondary");
        backButton.Click += (_, _) => HideMobileHistoryPage();

        var header = new Grid { ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(backButton);

        var title = new TextBlock
        {
            Text = "下载历史",
            FontSize = 21,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(title, 1);
        header.Children.Add(title);

        var list = CreateHistoryList();

        var pageGrid = new Grid
        {
            RowSpacing = 10
        };
        pageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pageGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        pageGrid.Children.Add(header);
        Grid.SetRow(_mobileHistorySearchBox, 1);
        pageGrid.Children.Add(_mobileHistorySearchBox);
        Grid.SetRow(_mobileHistoryCountText, 2);
        pageGrid.Children.Add(_mobileHistoryCountText);
        Grid.SetRow(_mobileHistoryActionText, 3);
        pageGrid.Children.Add(_mobileHistoryActionText);
        Grid.SetRow(list, 4);
        pageGrid.Children.Add(list);

        _mobileHistorySurface = new Border
        {
            Margin = new Thickness(10),
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(14),
            BorderBrush = new SolidColorBrush(Color.FromArgb(55, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = pageGrid
        };

        _mobileHistoryOverlay = new Grid
        {
            IsVisible = false,
            ZIndex = 2500
        };
        _mobileHistoryOverlay.Children.Add(_mobileHistorySurface);
        rootGrid.Children.Add(_mobileHistoryOverlay);
    }

    private void ShowMobileHistoryPage()
    {
        if (_mobileHistoryOverlay is null
            || DataContext is not RemoteMainViewModel viewModel)
        {
            return;
        }

        ApplyRemoteHistorySurfaceTheme(viewModel);
        _mobileHistoryOverlay.IsVisible = true;
        _mobileHistorySearchBox?.Focus();
    }

    private void HideMobileHistoryPage()
    {
        if (_mobileHistoryOverlay is not null)
            _mobileHistoryOverlay.IsVisible = false;
    }

    private void AddRemoteHistoryDeleteOverlay()
    {
        if (_remoteHistoryDeleteOverlay is not null
            || Content is not Grid rootGrid)
        {
            return;
        }

        _remoteHistoryDeleteAuthorText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap
        };
        _remoteHistoryDeletePathText = CreateCaptionTextBlock();
        _remoteHistoryDeletePathText.TextWrapping = TextWrapping.Wrap;

        var cancelButton = new Button
        {
            Content = "取消",
            MinHeight = 44
        };
        cancelButton.Classes.Add("action");
        cancelButton.Classes.Add("secondary");
        cancelButton.Click += (_, _) => HideRemoteHistoryDeleteDialog();

        var historyOnlyButton = new Button
        {
            Content = "仅移除历史",
            MinHeight = 44
        };
        historyOnlyButton.Classes.Add("action");
        historyOnlyButton.Classes.Add("secondary");
        historyOnlyButton.Click += async (_, _) => await CompleteRemoteHistoryDeleteAsync(false);

        var deleteFilesButton = new Button
        {
            Content = "同时删除磁盘文件",
            MinHeight = 44
        };
        deleteFilesButton.Classes.Add("action");
        deleteFilesButton.Classes.Add("danger");
        deleteFilesButton.Click += async (_, _) => await CompleteRemoteHistoryDeleteAsync(true);

        var buttonStack = new StackPanel
        {
            Spacing = 9,
            Children =
            {
                historyOnlyButton,
                deleteFilesButton,
                cancelButton
            }
        };

        var contentStack = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "删除作者记录？",
                    FontSize = 20,
                    FontWeight = FontWeight.SemiBold
                },
                new TextBlock
                {
                    Text = "请选择仅从历史列表移除，或同时删除磁盘中的作者文件。",
                    TextWrapping = TextWrapping.Wrap
                },
                _remoteHistoryDeleteAuthorText,
                _remoteHistoryDeletePathText,
                new TextBlock
                {
                    Text = "删除磁盘文件后无法恢复。",
                    Foreground = new SolidColorBrush(Color.Parse("#F59E0B")),
                    TextWrapping = TextWrapping.Wrap
                },
                buttonStack
            }
        };

        _remoteHistoryDeleteSurface = new Border
        {
            MaxWidth = 460,
            Margin = new Thickness(20),
            Padding = new Thickness(22),
            CornerRadius = new CornerRadius(12),
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Child = contentStack
        };

        _remoteHistoryDeleteOverlay = new Grid
        {
            Background = new SolidColorBrush(Color.Parse("#99000000")),
            IsVisible = false,
            ZIndex = 3200
        };
        _remoteHistoryDeleteOverlay.Children.Add(_remoteHistoryDeleteSurface);
        rootGrid.Children.Add(_remoteHistoryDeleteOverlay);
    }

    private void ShowRemoteHistoryDeleteDialog(RemoteHistoryItemViewModel item)
    {
        if (_remoteHistoryDeleteOverlay is null
            || DataContext is not RemoteMainViewModel viewModel)
        {
            return;
        }

        _pendingRemoteHistoryDeleteItem = item;
        if (_remoteHistoryDeleteAuthorText is not null)
            _remoteHistoryDeleteAuthorText.Text = $"作者：{item.UserName}（UID：{item.UserId}）";

        if (_remoteHistoryDeletePathText is not null)
        {
            _remoteHistoryDeletePathText.Text = string.IsNullOrWhiteSpace(item.FolderPath)
                ? "磁盘目录：由桌面端根据当前下载目录解析"
                : $"磁盘目录：{item.FolderPath}";
        }

        ApplyRemoteHistorySurfaceTheme(viewModel);
        _remoteHistoryDeleteOverlay.IsVisible = true;
    }

    private void HideRemoteHistoryDeleteDialog()
    {
        if (_remoteHistoryDeleteOverlay is not null)
            _remoteHistoryDeleteOverlay.IsVisible = false;
        _pendingRemoteHistoryDeleteItem = null;
    }

    private async Task CompleteRemoteHistoryDeleteAsync(bool deleteDiskFiles)
    {
        var item = _pendingRemoteHistoryDeleteItem;
        HideRemoteHistoryDeleteDialog();
        if (item is null)
            return;

        await ExecuteRemoteHistoryActionAsync(
            item,
            deleteDiskFiles ? "remove-files" : "remove-history");
    }

    private void ApplyRemoteHistorySurfaceTheme(RemoteMainViewModel viewModel)
    {
        var surfaceBrush = new SolidColorBrush(Color.Parse(
            viewModel.IsRemoteDarkTheme ? "#FF111827" : "#FFF8FAFC"));
        var pageBrush = new SolidColorBrush(Color.Parse(
            viewModel.IsRemoteDarkTheme ? "#FF0B1220" : "#FFF1F5F9"));

        if (_mobileHistoryOverlay is not null)
            _mobileHistoryOverlay.Background = pageBrush;
        if (_mobileHistorySurface is not null)
            _mobileHistorySurface.Background = surfaceBrush;
        if (_remoteHistoryDeleteSurface is not null)
            _remoteHistoryDeleteSurface.Background = surfaceBrush;
    }

    private void BindRemoteHistoryViewModel(RemoteMainViewModel viewModel)
    {
        if (ReferenceEquals(_remoteHistoryViewModel, viewModel))
        {
            ApplyRemoteHistoryFilter();
            return;
        }

        if (_remoteHistoryViewModel is not null)
            _remoteHistoryViewModel.History.CollectionChanged -= RemoteHistory_CollectionChanged;

        ClearRemoteHistoryItemSubscriptions();
        _remoteHistoryViewModel = viewModel;
        _remoteHistoryViewModel.History.CollectionChanged += RemoteHistory_CollectionChanged;
        RefreshRemoteHistoryItemSubscriptions();
        ApplyRemoteHistoryFilter();
    }

    private void RemoteHistory_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshRemoteHistoryItemSubscriptions();
        ApplyRemoteHistoryFilter();
    }

    private void RefreshRemoteHistoryItemSubscriptions()
    {
        ClearRemoteHistoryItemSubscriptions();
        if (_remoteHistoryViewModel is null)
            return;

        foreach (var item in _remoteHistoryViewModel.History)
        {
            if (_remoteHistorySubscriptions.Add(item))
                item.PropertyChanged += RemoteHistoryItem_PropertyChanged;
        }
    }

    private void ClearRemoteHistoryItemSubscriptions()
    {
        foreach (var item in _remoteHistorySubscriptions)
            item.PropertyChanged -= RemoteHistoryItem_PropertyChanged;
        _remoteHistorySubscriptions.Clear();
    }

    private void RemoteHistoryItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RemoteHistoryItemViewModel.UserName)
            or nameof(RemoteHistoryItemViewModel.UserId)
            or nameof(RemoteHistoryItemViewModel.Platform))
        {
            ApplyRemoteHistoryFilter();
        }
    }

    private void HistorySearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_syncingHistorySearch || sender is not TextBox source)
            return;

        _remoteHistorySearchText = source.Text ?? string.Empty;
        _syncingHistorySearch = true;
        try
        {
            if (!ReferenceEquals(source, _browserHistorySearchBox)
                && _browserHistorySearchBox is not null)
            {
                _browserHistorySearchBox.Text = _remoteHistorySearchText;
            }

            if (!ReferenceEquals(source, _mobileHistorySearchBox)
                && _mobileHistorySearchBox is not null)
            {
                _mobileHistorySearchBox.Text = _remoteHistorySearchText;
            }
        }
        finally
        {
            _syncingHistorySearch = false;
        }

        ApplyRemoteHistoryFilter();
    }

    private void ApplyRemoteHistoryFilter()
    {
        if (_remoteHistoryViewModel is null)
            return;

        var query = _remoteHistorySearchText.Trim();
        var desired = _remoteHistoryViewModel.History
            .Where(item => string.IsNullOrWhiteSpace(query)
                           || ContainsIgnoreCase(item.UserName, query)
                           || ContainsIgnoreCase(item.UserId, query)
                           || ContainsIgnoreCase(item.Platform, query))
            .ToArray();

        if (!_remoteFilteredHistory.SequenceEqual(desired))
        {
            _remoteFilteredHistory.Clear();
            foreach (var item in desired)
                _remoteFilteredHistory.Add(item);
        }

        var countText = string.IsNullOrWhiteSpace(query)
            ? $"共 {_remoteHistoryViewModel.History.Count} 位作者"
            : $"找到 {desired.Length} 位作者 · 共 {_remoteHistoryViewModel.History.Count} 位";

        if (_browserHistoryCountText is not null)
            _browserHistoryCountText.Text = countText;
        if (_mobileHistoryCountText is not null)
            _mobileHistoryCountText.Text = countText;
    }

    private static bool ContainsIgnoreCase(string? source, string value)
        => !string.IsNullOrWhiteSpace(source)
           && source.Contains(value, StringComparison.OrdinalIgnoreCase);

    private async Task<RemoteCommandResult?> ExecuteRemoteHistoryActionAsync(
        RemoteHistoryItemViewModel item,
        string action)
    {
        if (DataContext is not RemoteMainViewModel viewModel)
            return null;

        if (!viewModel.IsConnected)
        {
            SetRemoteHistoryActionStatus("尚未连接桌面客户端。", isError: true);
            return null;
        }

        SetRemoteHistoryActionStatus("正在发送到桌面端…", isError: false);
        try
        {
            using var client = new RemoteCrawlerClient();
            client.Configure(viewModel.ServerAddress, viewModel.AccessToken);
            var result = await client.ExecuteActionAsync($"history:{item.Id}:{action}");

            SetRemoteHistoryActionStatus(result.Message, isError: !result.Success);
            return result;
        }
        catch (Exception ex)
        {
            SetRemoteHistoryActionStatus($"操作失败：{ex.Message}", isError: true);
            return null;
        }
    }

    private async Task CopyRemoteHistoryAuthorUrlAsync(RemoteHistoryItemViewModel item)
    {
        var result = await ExecuteRemoteHistoryActionAsync(item, "copy-author-url");
        if (result is not { Success: true })
            return;

        var authorUrl = result.Message?.Trim();
        if (string.IsNullOrWhiteSpace(authorUrl))
        {
            SetRemoteHistoryActionStatus("桌面端没有返回作者 URL。", isError: true);
            return;
        }

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard
                            ?? throw new InvalidOperationException("系统剪贴板不可用。");
            await clipboard.SetTextAsync(authorUrl);
            SetRemoteHistoryActionStatus($"已复制作者 URL：{item.UserName}", isError: false);
        }
        catch (Exception ex)
        {
            SetRemoteHistoryActionStatus($"复制失败：{ex.Message}", isError: true);
        }
    }

    private void SetRemoteHistoryActionStatus(string? message, bool isError)
    {
        var text = string.IsNullOrWhiteSpace(message) ? string.Empty : message.Trim();
        var brush = isError
            ? new SolidColorBrush(Color.Parse("#EF4444"))
            : new SolidColorBrush(Color.Parse("#22C55E"));

        if (_browserHistoryActionText is not null)
        {
            _browserHistoryActionText.Text = text;
            _browserHistoryActionText.Foreground = brush;
        }

        if (_mobileHistoryActionText is not null)
        {
            _mobileHistoryActionText.Text = text;
            _mobileHistoryActionText.Foreground = brush;
        }
    }

    private static TextBlock CreateCaptionTextBlock()
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap
        };
        text.Classes.Add("caption");
        return text;
    }
}
