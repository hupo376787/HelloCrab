using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using IconPath = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using HelloCrab.Core.Models;
using HelloCrab.Core.Services.Localization;
using HelloCrab.Core.Utilities;
using HelloCrab.Core.ViewModels;
using ToolGood.Words.Pinyin;

namespace HelloCrab.Core.Views;

public partial class MainWindow
{
    private static readonly IDisposable HistoryPinyinSearchDataContextHandler =
        StyledElement.DataContextProperty.Changed.AddClassHandler<MainWindow>((window, _) =>
            Dispatcher.UIThread.Post(
                window.EnsureHistoryPinyinSearch,
                DispatcherPriority.Loaded));

    private readonly Dictionary<string, HistoryPinyinIndex> _historyPinyinCache =
        new(StringComparer.Ordinal);
    private readonly HashSet<DownloadHistoryItem> _historyPinyinObservedItems = new();

    private MainWindowViewModel? _historyPinyinViewModel;
    private TextBox? _historyPinyinSearchBox;
    private Button? _historyPinyinClearButton;
    private IconPath? _historyPinyinClearIcon;
    private Button? _historySearchDropdownButton;
    private IconPath? _historySearchDropdownIcon;
    private ContextMenu? _historySearchDropdownMenu;
    private bool _historyPinyinSearchInitialized;
    private bool _historyPinyinFilterApplying;
    private bool _historyPinyinFavoriteButtonHooked;
    private int _historyPinyinInstallAttempts;
    private int _historyPinyinFavoriteHookAttempts;

    private readonly record struct HistoryPinyinIndex(
        string Full,
        string NameFull,
        string Initials);

    private void EnsureHistoryPinyinSearch()
    {
        if (_historyPinyinSearchInitialized)
            return;

        if (DataContext is not MainWindowViewModel viewModel
            || HistoryList.Parent is not Grid historyGrid)
        {
            RetryHistoryPinyinSearchInstall();
            return;
        }

        var searchBox = historyGrid.Children
            .OfType<TextBox>()
            .FirstOrDefault(textBox =>
                Grid.GetRow(textBox) == 2
                && !textBox.AcceptsReturn);
        if (searchBox is null)
        {
            RetryHistoryPinyinSearchInstall();
            return;
        }

        _historyPinyinSearchInitialized = true;
        _historyPinyinViewModel = viewModel;
        _historyPinyinSearchBox = searchBox;

        // 给右侧两个操作按钮留出空间，避免输入文字延伸到按钮下面。
        searchBox.Padding = new Thickness(12, 0, 72, 0);
        searchBox.TextChanged += HistoryPinyinSearchTextChanged;
        searchBox.KeyDown += HistorySearchBox_KeyDown;
        searchBox.LostFocus += HistorySearchBox_LostFocus;

        _historyPinyinClearIcon = CreateHistorySearchActionIcon(
            "M3.5,3.5 L12.5,12.5 M12.5,3.5 L3.5,12.5",
            viewModel.IsDarkTheme);
        var clearButton = new Button
        {
            Content = _historyPinyinClearIcon,
            Width = 30,
            Height = 30,
            MinWidth = 30,
            MinHeight = 30,
            Margin = new Thickness(0, 0, 35, 10),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            IsVisible = !string.IsNullOrWhiteSpace(searchBox.Text)
        };
        clearButton.Click += HistoryPinyinClearButtonClick;
        Grid.SetRow(clearButton, 2);
        historyGrid.Children.Add(clearButton);
        _historyPinyinClearButton = clearButton;

        _historySearchDropdownIcon = CreateHistorySearchActionIcon(
            "M3,6 L8,11 L13,6",
            viewModel.IsDarkTheme);
        var dropdownButton = new Button
        {
            Content = _historySearchDropdownIcon,
            Width = 30,
            Height = 30,
            MinWidth = 30,
            MinHeight = 30,
            Margin = new Thickness(0, 0, 5, 10),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6)
        };
        dropdownButton.Click += HistorySearchDropdownButton_Click;
        Grid.SetRow(dropdownButton, 2);
        historyGrid.Children.Add(dropdownButton);
        _historySearchDropdownButton = dropdownButton;
        _historySearchDropdownMenu = new ContextMenu
        {
            MaxHeight = 360,
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            PlacementTarget = searchBox
        };

        viewModel.PropertyChanged += HistoryPinyinViewModelPropertyChanged;
        viewModel.DownloadHistory.CollectionChanged += HistoryPinyinDownloadHistoryChanged;
        viewModel.FilteredDownloadHistory.CollectionChanged += HistoryPinyinFilteredHistoryChanged;
        RewireHistoryPinyinItemHandlers();

        if (LocalizationService.Current is { } localization)
            localization.LanguageChanged += HistoryPinyinLanguageChanged;
        Closed += HistoryPinyinWindowClosed;

        RefreshHistoryPinyinClearButton();
        HookHistoryPinyinFavoriteButton();
    }

    private static IconPath CreateHistorySearchActionIcon(string geometry, bool isDarkTheme)
        => new()
        {
            Data = Geometry.Parse(geometry),
            Width = 16,
            Height = 16,
            Stretch = Stretch.None,
            Stroke = new SolidColorBrush(Color.Parse(isDarkTheme ? "#D4D7E2" : "#6F7484")),
            StrokeThickness = 1.8,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false
        };

    private void RetryHistoryPinyinSearchInstall()
    {
        if (_historyPinyinInstallAttempts++ >= 6)
            return;

        Dispatcher.UIThread.Post(
            EnsureHistoryPinyinSearch,
            DispatcherPriority.Background);
    }

    private void HistoryPinyinSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        RefreshHistoryPinyinClearButton();
        QueueHistoryPinyinSearchRefresh();
    }

    private void HistorySearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            _historyPinyinViewModel?.RememberHistorySearchTerm();
    }

    private void HistorySearchBox_LostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _historyPinyinViewModel?.RememberHistorySearchTerm();

    private void HistorySearchDropdownButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_historyPinyinViewModel is not { } viewModel
            || _historyPinyinSearchBox is not { } searchBox
            || _historySearchDropdownMenu is not { } menu)
            return;

        viewModel.RememberHistorySearchTerm();
        menu.Items.Clear();
        menu.Width = searchBox.Bounds.Width;
        foreach (var term in viewModel.HistorySearchTerms)
        {
            var item = new MenuItem { Header = term };
            item.Click += (_, _) =>
            {
                viewModel.HistorySearchText = term;
                viewModel.RememberHistorySearchTerm();
            };
            menu.Items.Add(item);
        }

        if (menu.Items.Count == 0)
            menu.Items.Add(new MenuItem
            {
                Header = LocalizationService.Current?.Get("History.SearchHistoryEmpty", "暂无搜索记录")
                         ?? "暂无搜索记录",
                IsEnabled = false
            });

        menu.Open(searchBox);
        e.Handled = true;
    }

    private void HistoryPinyinClearButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_historyPinyinViewModel is { } viewModel)
            viewModel.HistorySearchText = string.Empty;

        _historyPinyinSearchBox?.Focus();
        e.Handled = true;
    }

    private void RefreshHistoryPinyinClearButton()
    {
        if (_historyPinyinClearButton is null)
            return;

        _historyPinyinClearButton.IsVisible =
            !string.IsNullOrWhiteSpace(_historyPinyinSearchBox?.Text);

        var code = LocalizationService.Current?.CurrentLanguageCode ?? "zh-CN";
        ToolTip.SetTip(
            _historyPinyinClearButton,
            code.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                ? "Clear search"
                : code.StartsWith("ja", StringComparison.OrdinalIgnoreCase)
                    ? "検索をクリア"
                    : "清除搜索");
    }

    private void HistoryPinyinViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.IsDarkTheme)
            || _historyPinyinClearButton is null
            || sender is not MainWindowViewModel viewModel)
        {
            return;
        }

        var iconBrush = new SolidColorBrush(Color.Parse(
            viewModel.IsDarkTheme ? "#D4D7E2" : "#6F7484"));
        if (_historyPinyinClearIcon is not null)
            _historyPinyinClearIcon.Stroke = iconBrush;
        if (_historySearchDropdownIcon is not null)
            _historySearchDropdownIcon.Stroke = iconBrush;
    }

    private void QueueHistoryPinyinSearchRefresh()
    {
        if (_historyPinyinFilterApplying)
            return;

        Dispatcher.UIThread.Post(
            ApplyHistoryPinyinSearch,
            DispatcherPriority.Background);
    }

    private void ApplyHistoryPinyinSearch()
    {
        if (_historyPinyinFilterApplying
            || _historyPinyinViewModel is not { } viewModel
            || _historyPinyinSearchBox is null)
        {
            return;
        }

        var keywords = (_historyPinyinSearchBox.Text ?? string.Empty)
            .Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // 搜索为空时继续由现有 ViewModel 和收藏筛选逻辑维护列表。
        if (keywords.Length == 0)
            return;

        var matches = viewModel.DownloadHistory
            .Where(item => !_showFavoritesOnly || IsHistoryFavorite(item))
            .Where(item => keywords.All(keyword => HistoryPinyinItemMatches(item, keyword)))
            .ToArray();

        _historyPinyinFilterApplying = true;
        try
        {
            HistoryCollectionSynchronizer.Sync(
                viewModel.FilteredDownloadHistory,
                matches);
        }
        finally
        {
            _historyPinyinFilterApplying = false;
        }
    }

    private bool HistoryPinyinItemMatches(DownloadHistoryItem item, string keyword)
    {
        // 原来的作者名 / UID / 平台匹配继续保留。
        if (ContainsHistorySearchText(item.UserName, keyword)
            || ContainsHistorySearchText(item.UserId, keyword)
            || ContainsHistorySearchText(item.Platform, keyword)
            || ContainsHistorySearchText(item.PlatformDisplayText, keyword))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(item.UserName)
            || !keyword.Any(IsAsciiLetter))
        {
            return false;
        }

        var normalizedKeyword = NormalizeHistoryPinyin(keyword);
        if (normalizedKeyword.Length == 0)
            return false;

        var pinyin = GetHistoryPinyinIndex(item.UserName);
        return pinyin.Full.Contains(normalizedKeyword, StringComparison.OrdinalIgnoreCase)
               || pinyin.NameFull.Contains(normalizedKeyword, StringComparison.OrdinalIgnoreCase)
               || pinyin.Initials.Contains(normalizedKeyword, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsHistorySearchText(string? value, string keyword)
        => !string.IsNullOrWhiteSpace(value)
           && value.Contains(keyword, StringComparison.OrdinalIgnoreCase);

    private HistoryPinyinIndex GetHistoryPinyinIndex(string userName)
    {
        if (_historyPinyinCache.TryGetValue(userName, out var cached))
            return cached;

        var full = TryGetHistoryPinyin(() => WordsHelper.GetPinyin(userName));
        var nameFull = TryGetHistoryPinyin(() => WordsHelper.GetPinyinForName(userName));
        var initials = TryGetHistoryPinyin(() => WordsHelper.GetFirstPinyin(userName));
        var result = new HistoryPinyinIndex(full, nameFull, initials);

        // 历史列表正常远小于这个数量；设置上限避免极端情况下缓存无限增长。
        if (_historyPinyinCache.Count >= 4096)
            _historyPinyinCache.Clear();

        _historyPinyinCache[userName] = result;
        return result;
    }

    private static string TryGetHistoryPinyin(Func<string> converter)
    {
        try
        {
            return NormalizeHistoryPinyin(converter());
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeHistoryPinyin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (IsAsciiLetter(ch) || ch is >= '0' and <= '9')
                builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }

    private static bool IsAsciiLetter(char ch)
        => (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z');

    private void HistoryPinyinDownloadHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RewireHistoryPinyinItemHandlers();
        QueueHistoryPinyinSearchRefresh();
    }

    private void RewireHistoryPinyinItemHandlers()
    {
        if (_historyPinyinViewModel is not { } viewModel)
            return;

        var current = viewModel.DownloadHistory.ToHashSet();

        foreach (var item in _historyPinyinObservedItems
                     .Where(item => !current.Contains(item))
                     .ToArray())
        {
            item.PropertyChanged -= HistoryPinyinItemPropertyChanged;
            _historyPinyinObservedItems.Remove(item);
        }

        foreach (var item in current)
        {
            if (_historyPinyinObservedItems.Add(item))
                item.PropertyChanged += HistoryPinyinItemPropertyChanged;
        }
    }

    private void HistoryPinyinItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadHistoryItem.UserName)
            or nameof(DownloadHistoryItem.UserId)
            or nameof(DownloadHistoryItem.Platform)
            or nameof(DownloadHistoryItem.PlatformDisplayText))
        {
            QueueHistoryPinyinSearchRefresh();
        }
    }

    private void HistoryPinyinFilteredHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_historyPinyinFilterApplying
            && !_historyDisplaySortApplying
            && !string.IsNullOrWhiteSpace(_historyPinyinSearchBox?.Text))
        {
            QueueHistoryPinyinSearchRefresh();
        }
    }

    private void HookHistoryPinyinFavoriteButton()
    {
        if (_historyPinyinFavoriteButtonHooked)
            return;

        if (_historyFavoritesButton is null)
        {
            if (_historyPinyinFavoriteHookAttempts++ < 6)
            {
                Dispatcher.UIThread.Post(
                    HookHistoryPinyinFavoriteButton,
                    DispatcherPriority.Background);
            }
            return;
        }

        _historyPinyinFavoriteButtonHooked = true;
        _historyFavoritesButton.Click += HistoryPinyinFavoriteFilterChanged;
    }

    private void HistoryPinyinFavoriteFilterChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => QueueHistoryPinyinSearchRefresh();

    private void HistoryPinyinLanguageChanged(object? sender, EventArgs e)
    {
        _historyPinyinCache.Clear();
        RefreshHistoryPinyinClearButton();
        QueueHistoryPinyinSearchRefresh();
    }

    private void HistoryPinyinWindowClosed(object? sender, EventArgs e)
    {
        if (_historyPinyinSearchBox is not null)
        {
            _historyPinyinSearchBox.TextChanged -= HistoryPinyinSearchTextChanged;
            _historyPinyinSearchBox.KeyDown -= HistorySearchBox_KeyDown;
            _historyPinyinSearchBox.LostFocus -= HistorySearchBox_LostFocus;
        }
        if (_historyPinyinClearButton is not null)
            _historyPinyinClearButton.Click -= HistoryPinyinClearButtonClick;
        if (_historySearchDropdownButton is not null)
            _historySearchDropdownButton.Click -= HistorySearchDropdownButton_Click;
        _historySearchDropdownMenu?.Close();
        if (_historyFavoritesButton is not null && _historyPinyinFavoriteButtonHooked)
            _historyFavoritesButton.Click -= HistoryPinyinFavoriteFilterChanged;

        if (_historyPinyinViewModel is { } viewModel)
        {
            viewModel.PropertyChanged -= HistoryPinyinViewModelPropertyChanged;
            viewModel.DownloadHistory.CollectionChanged -= HistoryPinyinDownloadHistoryChanged;
            viewModel.FilteredDownloadHistory.CollectionChanged -= HistoryPinyinFilteredHistoryChanged;
        }

        foreach (var item in _historyPinyinObservedItems)
            item.PropertyChanged -= HistoryPinyinItemPropertyChanged;
        _historyPinyinObservedItems.Clear();
        _historyPinyinCache.Clear();

        if (LocalizationService.Current is { } localization)
            localization.LanguageChanged -= HistoryPinyinLanguageChanged;

        Closed -= HistoryPinyinWindowClosed;
    }
}
