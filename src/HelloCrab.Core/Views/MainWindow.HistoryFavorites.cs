using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HelloCrab.Core.Models;
using HelloCrab.Core.Services.History;
using HelloCrab.Core.Services.Localization;
using HelloCrab.Core.Utilities;
using HelloCrab.Core.ViewModels;

namespace HelloCrab.Core.Views;

public partial class MainWindow
{
    private const string FavoriteMenuClass = "helloCrabHistoryFavoriteMenu";
    private const string TransparentBatchEditorClass = "helloCrabTransparentBatchEditor";

    // 不修改现有 MainWindow 构造函数和 XAML：在 DataContext 就绪后动态加入收藏控件。
    private static readonly IDisposable HistoryFavoritesDataContextHandler =
        StyledElement.DataContextProperty.Changed.AddClassHandler<MainWindow>((window, _) =>
            Dispatcher.UIThread.Post(window.EnsureHistoryFavoriteControls, DispatcherPriority.Loaded));

    // 批量弹窗由代码动态创建。模板加载后直接清除 Fluent TextBox 内部实际绘制背景的控件，
    // 避免普通 Background/主题资源被模板状态覆盖后仍显示一块不透明底色。
    private static readonly IDisposable TransparentBatchEditorLoadedHandler =
        Control.LoadedEvent.AddClassHandler<TextBox>((textBox, _) =>
        {
            if (!IsBatchCaptureEditor(textBox))
                return;

            AttachTransparentBatchEditor(textBox);
        });

    private readonly HistoryFavoritesStore _historyFavoritesStore = new();
    private readonly HashSet<string> _historyFavoriteKeys = new(StringComparer.OrdinalIgnoreCase);
    private MainWindowViewModel? _historyFavoritesViewModel;
    private Button? _historyFavoritesButton;
    private TextBlock? _historyFavoritesStar;
    private bool _historyFavoritesInitialized;
    private bool _showFavoritesOnly;
    private bool _isApplyingHistoryFavoriteFilter;

    private static bool IsBatchCaptureEditor(TextBox textBox)
    {
        if (!textBox.AcceptsReturn || textBox.MinHeight < 250)
            return false;

        return TopLevel.GetTopLevel(textBox) is Window { Owner: MainWindow };
    }

    private static void AttachTransparentBatchEditor(TextBox textBox)
    {
        if (!textBox.Classes.Contains(TransparentBatchEditorClass))
        {
            textBox.Classes.Add(TransparentBatchEditorClass);
            textBox.TemplateApplied += (_, _) => QueueTransparentBatchEditor(textBox);
            textBox.GotFocus += (_, _) => QueueTransparentBatchEditor(textBox);
            textBox.PointerEntered += (_, _) => QueueTransparentBatchEditor(textBox);
        }

        QueueTransparentBatchEditor(textBox);
    }

    private static void QueueTransparentBatchEditor(TextBox textBox)
        => Dispatcher.UIThread.Post(
            () => ForceTransparentBatchEditor(textBox),
            DispatcherPriority.Render);

    private static void ForceTransparentBatchEditor(TextBox textBox)
    {
        textBox.Background = Brushes.Transparent;
        textBox.Resources["TextControlBackground"] = Brushes.Transparent;
        textBox.Resources["TextControlBackgroundPointerOver"] = Brushes.Transparent;
        textBox.Resources["TextControlBackgroundFocused"] = Brushes.Transparent;
        textBox.Resources["TextControlBackgroundDisabled"] = Brushes.Transparent;
        textBox.ApplyTemplate();

        // Avalonia Fluent 主题真正绘制底色的是模板里的 PART_BorderElement。
        // 给模板部件设置本地值后，pointerover/focus 样式不能再次把它覆盖成实色。
        var templateBorder = textBox
            .GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(border =>
                string.Equals(border.Name, "PART_BorderElement", StringComparison.Ordinal)
                || ReferenceEquals(border.TemplatedParent, textBox));
        if (templateBorder is not null)
            templateBorder.Background = Brushes.Transparent;

        foreach (var scrollViewer in textBox
                     .GetVisualDescendants()
                     .OfType<ScrollViewer>()
                     .Where(scrollViewer =>
                         string.Equals(scrollViewer.Name, "PART_ContentHost", StringComparison.Ordinal)
                         || ReferenceEquals(scrollViewer.TemplatedParent, textBox)))
        {
            scrollViewer.Background = Brushes.Transparent;
        }
    }

    private void EnsureHistoryFavoriteControls()
    {
        if (_historyFavoritesInitialized
            || DataContext is not MainWindowViewModel viewModel
            || HistoryList.Parent is not Grid historyGrid)
        {
            return;
        }

        _historyFavoritesInitialized = true;
        _historyFavoritesViewModel = viewModel;

        _historyFavoritesStar = new TextBlock
        {
            Text = "★",
            FontSize = 24,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _historyFavoritesButton = new Button
        {
            Width = 40,
            Height = 38,
            MinWidth = 40,
            MinHeight = 38,
            Margin = new Thickness(0, -5, 0, 0),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(7),
            Content = _historyFavoritesStar
        };
        _historyFavoritesButton.Click += HistoryFavoritesButton_Click;
        Grid.SetRow(_historyFavoritesButton, 0);
        historyGrid.Children.Add(_historyFavoritesButton);

        HistoryList.AddHandler(
            InputElement.ContextRequestedEvent,
            HistoryFavoriteContextRequested,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        viewModel.PropertyChanged += HistoryFavoritesViewModel_PropertyChanged;
        viewModel.DownloadHistory.CollectionChanged += HistoryDownloadCollectionChanged;
        if (LocalizationService.Current is { } localization)
            localization.LanguageChanged += HistoryFavoritesLanguageChanged;
        Closed += HistoryFavoritesWindowClosed;

        UpdateHistoryFavoritesButton();
        _ = LoadHistoryFavoritesAsync();
    }

    private async Task LoadHistoryFavoritesAsync()
    {
        try
        {
            var values = await _historyFavoritesStore.LoadAsync();
            Dispatcher.UIThread.Post(() =>
            {
                _historyFavoriteKeys.Clear();
                _historyFavoriteKeys.UnionWith(values);
                UpdateHistoryFavoritesButton();
                ApplyHistoryFavoriteFilter();
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dispatcher.UIThread.Post(() => _historyFavoritesViewModel?.AddRemoteLog(
                FormatFavoriteText(
                    "读取收藏列表失败：{0}",
                    "Failed to load favorites: {0}",
                    "お気に入り一覧の読み込みに失敗しました：{0}",
                    ex.Message)));
        }
    }

    private void HistoryFavoritesButton_Click(object? sender, RoutedEventArgs e)
    {
        _showFavoritesOnly = !_showFavoritesOnly;
        UpdateHistoryFavoritesButton();
        ApplyHistoryFavoriteFilter();
    }

    private void UpdateHistoryFavoritesButton()
    {
        if (_historyFavoritesButton is null || _historyFavoritesStar is null)
            return;

        var normalColor = _historyFavoritesViewModel?.IsDarkTheme == true
            ? "#D4D7E2"
            : "#6F7484";
        _historyFavoritesStar.Foreground = new SolidColorBrush(Color.Parse(
            _showFavoritesOnly ? "#FD6F71" : normalColor));

        ToolTip.SetTip(
            _historyFavoritesButton,
            _showFavoritesOnly
                ? FavoriteText("显示全部作者", "Show all authors", "すべての投稿者を表示")
                : FavoriteText("仅显示收藏作者", "Show favorite authors only", "お気に入りの投稿者のみ表示"));
    }

    private void HistoryFavoriteContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var current = e.Source as Control;
        Control? menuHost = null;
        DownloadHistoryItem? historyItem = null;

        while (current is not null && !ReferenceEquals(current, HistoryList))
        {
            if (current.DataContext is DownloadHistoryItem item
                && current.ContextMenu is not null)
            {
                menuHost = current;
                historyItem = item;
                break;
            }

            current = current.Parent as Control;
        }

        if (menuHost?.ContextMenu is not { } menu || historyItem is null)
            return;

        for (var index = menu.Items.Count - 1; index >= 0; index--)
        {
            if (menu.Items[index] is MenuItem oldItem
                && oldItem.Classes.Contains(FavoriteMenuClass))
            {
                menu.Items.RemoveAt(index);
            }
        }

        var isFavorite = IsHistoryFavorite(historyItem);
        var favoriteMenuItem = new MenuItem
        {
            Header = isFavorite
                ? FavoriteText("取消收藏", "Remove from favorites", "お気に入りを解除")
                : FavoriteText("收藏", "Add to favorites", "お気に入りに追加"),
            Tag = historyItem
        };
        favoriteMenuItem.Classes.Add(FavoriteMenuClass);
        favoriteMenuItem.Click += async (_, _) => await ToggleHistoryFavoriteAsync(historyItem);

        var insertIndex = menu.Items.Count;
        for (var index = 0; index < menu.Items.Count; index++)
        {
            if (menu.Items[index] is Separator)
            {
                insertIndex = index;
                break;
            }
        }

        menu.Items.Insert(insertIndex, favoriteMenuItem);
    }

    private async Task ToggleHistoryFavoriteAsync(DownloadHistoryItem item)
    {
        var key = BuildHistoryFavoriteKey(item);
        if (string.IsNullOrWhiteSpace(key))
            return;

        var added = _historyFavoriteKeys.Add(key);
        if (!added)
            _historyFavoriteKeys.Remove(key);

        ApplyHistoryFavoriteFilter();

        var authorName = string.IsNullOrWhiteSpace(item.UserName)
            ? item.UserId
            : item.UserName;
        _historyFavoritesViewModel?.AddRemoteLog(added
            ? FormatFavoriteText("已收藏作者：{0}", "Author added to favorites: {0}", "投稿者をお気に入りに追加しました：{0}", authorName)
            : FormatFavoriteText("已取消收藏：{0}", "Author removed from favorites: {0}", "お気に入りを解除しました：{0}", authorName));

        try
        {
            await _historyFavoritesStore.SaveAsync(_historyFavoriteKeys.ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _historyFavoritesViewModel?.AddRemoteLog(FormatFavoriteText(
                "保存收藏列表失败：{0}",
                "Failed to save favorites: {0}",
                "お気に入り一覧の保存に失敗しました：{0}",
                ex.Message));
        }
    }

    private void HistoryFavoritesViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.HistorySearchText))
        {
            Dispatcher.UIThread.Post(ApplyHistoryFavoriteFilter, DispatcherPriority.Background);
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsDarkTheme))
        {
            UpdateHistoryFavoritesButton();
        }
    }

    private void HistoryDownloadCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => Dispatcher.UIThread.Post(ApplyHistoryFavoriteFilter, DispatcherPriority.Background);

    private void HistoryFavoritesLanguageChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(UpdateHistoryFavoritesButton);

    private void ApplyHistoryFavoriteFilter()
    {
        if (_isApplyingHistoryFavoriteFilter || _historyFavoritesViewModel is not { } viewModel)
            return;

        var query = viewModel.HistorySearchText.Trim();
        var filtered = viewModel.DownloadHistory
            .Where(item => !_showFavoritesOnly || IsHistoryFavorite(item))
            .Where(item => MatchesHistorySearch(item, query))
            .ToArray();

        _isApplyingHistoryFavoriteFilter = true;
        try
        {
            HistoryCollectionSynchronizer.Sync(
                viewModel.FilteredDownloadHistory,
                filtered);
        }
        finally
        {
            _isApplyingHistoryFavoriteFilter = false;
        }
    }

    private bool IsHistoryFavorite(DownloadHistoryItem item)
        => _historyFavoriteKeys.Contains(BuildHistoryFavoriteKey(item));

    private static string BuildHistoryFavoriteKey(DownloadHistoryItem item)
    {
        var identity = !string.IsNullOrWhiteSpace(item.UserId)
            ? item.UserId.Trim()
            : item.OriginalUrl.Trim();
        if (string.IsNullOrWhiteSpace(identity))
            return string.Empty;

        return $"{item.Platform.Trim().ToLowerInvariant()}::{identity}";
    }

    private static bool MatchesHistorySearch(DownloadHistoryItem item, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;

        return ContainsIgnoreCase(item.UserName, query)
               || ContainsIgnoreCase(item.UserId, query)
               || ContainsIgnoreCase(item.Platform, query)
               || ContainsIgnoreCase(item.PlatformDisplayText, query)
               || ContainsIgnoreCase(item.OriginalUrl, query);
    }

    private static bool ContainsIgnoreCase(string? value, string query)
        => !string.IsNullOrWhiteSpace(value)
           && value.Contains(query, StringComparison.OrdinalIgnoreCase);

    private void HistoryFavoritesWindowClosed(object? sender, EventArgs e)
    {
        if (_historyFavoritesViewModel is { } viewModel)
        {
            viewModel.PropertyChanged -= HistoryFavoritesViewModel_PropertyChanged;
            viewModel.DownloadHistory.CollectionChanged -= HistoryDownloadCollectionChanged;
        }

        if (LocalizationService.Current is { } localization)
            localization.LanguageChanged -= HistoryFavoritesLanguageChanged;
    }

    private static string FavoriteText(string zhCn, string enUs, string jaJp)
    {
        var code = LocalizationService.Current?.CurrentLanguageCode ?? "zh-CN";
        if (code.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            return enUs;
        if (code.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
            return jaJp;
        return zhCn;
    }

    private static string FormatFavoriteText(
        string zhCn,
        string enUs,
        string jaJp,
        params object?[] arguments)
        => string.Format(FavoriteText(zhCn, enUs, jaJp), arguments);
}
