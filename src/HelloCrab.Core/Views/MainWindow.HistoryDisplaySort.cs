using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using HelloCrab.Core.Models;
using HelloCrab.Core.Services.Localization;
using HelloCrab.Core.ViewModels;

namespace HelloCrab.Core.Views;

public partial class MainWindow
{
    private enum HistoryDisplaySortKind
    {
        Default,
        Name,
        UpdatedAt,
        Platform
    }

    private static readonly IDisposable HistoryDisplaySortDataContextHandler =
        StyledElement.DataContextProperty.Changed.AddClassHandler<MainWindow>((window, _) =>
            Dispatcher.UIThread.Post(
                window.EnsureHistoryDisplaySort,
                DispatcherPriority.Loaded));

    private readonly HashSet<DownloadHistoryItem> _historySortObservedItems = new();
    private MainWindowViewModel? _historySortViewModel;
    private Button? _historySortButton;
    private TextBlock? _historySortButtonLabel;
    private ContextMenu? _historySortMenu;
    private MenuItem? _historySortDefaultMenuItem;
    private MenuItem? _historySortNameMenuItem;
    private MenuItem? _historySortUpdatedAtMenuItem;
    private MenuItem? _historySortPlatformMenuItem;
    private MenuItem? _historySortAscendingMenuItem;
    private MenuItem? _historySortDescendingMenuItem;
    private HistoryDisplaySortKind _historyDisplaySortKind = HistoryDisplaySortKind.Default;
    private bool _historyDisplaySortAscending = true;
    private bool _historyDisplaySortApplying;
    private bool _historyDisplaySortInstalled;
    private long _historyDisplaySortVersion;

    private bool IsHistoryDisplaySortActive
        => _historyDisplaySortKind != HistoryDisplaySortKind.Default
           || !_historyDisplaySortAscending;

    private void EnsureHistoryDisplaySort()
    {
        if (_historyDisplaySortInstalled
            || DataContext is not MainWindowViewModel viewModel
            || HistoryList.Parent is not Grid historyGrid)
        {
            return;
        }

        _historyDisplaySortInstalled = true;
        _historySortViewModel = viewModel;

        _historySortDefaultMenuItem = CreateHistorySortMenuItem(
            () => SetHistoryDisplaySortKind(HistoryDisplaySortKind.Default));
        _historySortNameMenuItem = CreateHistorySortMenuItem(
            () => SetHistoryDisplaySortKind(HistoryDisplaySortKind.Name));
        _historySortUpdatedAtMenuItem = CreateHistorySortMenuItem(
            () => SetHistoryDisplaySortKind(HistoryDisplaySortKind.UpdatedAt));
        _historySortPlatformMenuItem = CreateHistorySortMenuItem(
            () => SetHistoryDisplaySortKind(HistoryDisplaySortKind.Platform));
        _historySortAscendingMenuItem = CreateHistorySortMenuItem(
            () => SetHistoryDisplaySortDirection(ascending: true));
        _historySortDescendingMenuItem = CreateHistorySortMenuItem(
            () => SetHistoryDisplaySortDirection(ascending: false));

        _historySortMenu = new ContextMenu
        {
            Items =
            {
                _historySortDefaultMenuItem,
                _historySortNameMenuItem,
                _historySortUpdatedAtMenuItem,
                _historySortPlatformMenuItem,
                new Separator(),
                _historySortAscendingMenuItem,
                _historySortDescendingMenuItem
            }
        };

        var icon = new TextBlock
        {
            Text = "⇅",
            FontSize = 17,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        _historySortButtonLabel = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeight.Medium,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        var chevron = new TextBlock
        {
            Text = "⌄",
            FontSize = 12,
            Margin = new Thickness(-1, -1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(icon);
        content.Children.Add(_historySortButtonLabel);
        content.Children.Add(chevron);

        _historySortButton = new Button
        {
            Width = 78,
            Height = 38,
            MinWidth = 78,
            MinHeight = 38,
            Margin = new Thickness(0, -5, 44, 0),
            Padding = new Thickness(4, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(7),
            Content = content,
            ContextMenu = _historySortMenu
        };
        _historySortButton.Click += HistorySortButton_Click;
        Grid.SetRow(_historySortButton, 0);
        historyGrid.Children.Add(_historySortButton);

        viewModel.DownloadHistory.CollectionChanged += HistoryDisplaySortDownloadHistoryChanged;
        viewModel.FilteredDownloadHistory.CollectionChanged += HistoryDisplaySortFilteredHistoryChanged;
        RewireHistoryDisplaySortItemHandlers();

        HistoryList.AddHandler(
            PointerPressedEvent,
            HistoryDisplaySort_BlockSortedDrag,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        if (LocalizationService.Current is { } localization)
            localization.LanguageChanged += HistoryDisplaySortLanguageChanged;
        Closed += HistoryDisplaySortWindowClosed;

        UpdateHistoryDisplaySortUi();
        QueueHistoryDisplaySort();
    }

    private static MenuItem CreateHistorySortMenuItem(Action action)
    {
        var item = new MenuItem();
        item.Click += (_, _) => action();
        return item;
    }

    private void HistorySortButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_historySortButton is null || _historySortMenu is null)
            return;

        UpdateHistoryDisplaySortUi();
        if (!_historySortMenu.IsOpen)
            _historySortMenu.Open(_historySortButton);
        e.Handled = true;
    }

    private void SetHistoryDisplaySortKind(HistoryDisplaySortKind kind)
    {
        if (_historyDisplaySortKind == kind)
            return;

        EndHistoryDrag(saveOrder: false);
        _historyDisplaySortKind = kind;
        UpdateHistoryDisplaySortUi();
        QueueHistoryDisplaySort();
    }

    private void SetHistoryDisplaySortDirection(bool ascending)
    {
        if (_historyDisplaySortAscending == ascending)
            return;

        EndHistoryDrag(saveOrder: false);
        _historyDisplaySortAscending = ascending;
        UpdateHistoryDisplaySortUi();
        QueueHistoryDisplaySort();
    }

    private void UpdateHistoryDisplaySortUi()
    {
        if (_historySortButtonLabel is not null)
            _historySortButtonLabel.Text = HistorySortText("排序", "Sort", "並べ替え");

        SetHistorySortMenuHeader(
            _historySortDefaultMenuItem,
            _historyDisplaySortKind == HistoryDisplaySortKind.Default,
            HistorySortText("默认排序", "Default order", "既定の順序"));
        SetHistorySortMenuHeader(
            _historySortNameMenuItem,
            _historyDisplaySortKind == HistoryDisplaySortKind.Name,
            HistorySortText("名称", "Name", "名前"));
        SetHistorySortMenuHeader(
            _historySortUpdatedAtMenuItem,
            _historyDisplaySortKind == HistoryDisplaySortKind.UpdatedAt,
            HistorySortText("更新日期", "Updated date", "更新日時"));
        SetHistorySortMenuHeader(
            _historySortPlatformMenuItem,
            _historyDisplaySortKind == HistoryDisplaySortKind.Platform,
            HistorySortText("平台", "Platform", "プラットフォーム"));
        SetHistorySortMenuHeader(
            _historySortAscendingMenuItem,
            _historyDisplaySortAscending,
            HistorySortText("递增", "Ascending", "昇順"));
        SetHistorySortMenuHeader(
            _historySortDescendingMenuItem,
            !_historyDisplaySortAscending,
            HistorySortText("递减", "Descending", "降順"));

        if (_historySortButton is not null)
        {
            var kindText = _historyDisplaySortKind switch
            {
                HistoryDisplaySortKind.Name => HistorySortText("名称", "Name", "名前"),
                HistoryDisplaySortKind.UpdatedAt => HistorySortText("更新日期", "Updated date", "更新日時"),
                HistoryDisplaySortKind.Platform => HistorySortText("平台", "Platform", "プラットフォーム"),
                _ => HistorySortText("默认排序", "Default order", "既定の順序")
            };
            var directionText = _historyDisplaySortAscending
                ? HistorySortText("递增", "Ascending", "昇順")
                : HistorySortText("递减", "Descending", "降順");
            ToolTip.SetTip(
                _historySortButton,
                string.Format(
                    HistorySortText("排序：{0} · {1}", "Sort: {0} · {1}", "並べ替え：{0} · {1}"),
                    kindText,
                    directionText));
        }
    }

    private static void SetHistorySortMenuHeader(MenuItem? item, bool selected, string text)
    {
        if (item is null)
            return;

        item.Header = selected ? $"•   {text}" : $"    {text}";
    }

    private void QueueHistoryDisplaySort()
    {
        if (_historySortViewModel is null)
            return;

        var version = Interlocked.Increment(ref _historyDisplaySortVersion);
        Dispatcher.UIThread.Post(
            () =>
            {
                if (version == Interlocked.Read(ref _historyDisplaySortVersion))
                    ApplyHistoryDisplaySort();
            },
            DispatcherPriority.Background);
    }

    private void ApplyHistoryDisplaySort()
    {
        if (_historyDisplaySortApplying
            || _historySortViewModel is not { } viewModel
            || viewModel.FilteredDownloadHistory.Count < 2)
        {
            return;
        }

        var defaultIndexes = viewModel.DownloadHistory
            .Select((item, index) => (item, index))
            .ToDictionary(pair => pair.item, pair => pair.index);
        var comparer = GetHistorySortStringComparer();
        var desired = viewModel.FilteredDownloadHistory.ToArray();

        Array.Sort(desired, (left, right) =>
            CompareHistoryDisplaySortItems(left, right, defaultIndexes, comparer));

        _historyDisplaySortApplying = true;
        try
        {
            for (var targetIndex = 0; targetIndex < desired.Length; targetIndex++)
            {
                var currentIndex = viewModel.FilteredDownloadHistory.IndexOf(desired[targetIndex]);
                if (currentIndex >= 0 && currentIndex != targetIndex)
                    viewModel.FilteredDownloadHistory.Move(currentIndex, targetIndex);
            }
        }
        finally
        {
            _historyDisplaySortApplying = false;
        }

        Dispatcher.UIThread.Post(RefreshHistorySelectionMarkers, DispatcherPriority.Render);
    }

    private int CompareHistoryDisplaySortItems(
        DownloadHistoryItem left,
        DownloadHistoryItem right,
        IReadOnlyDictionary<DownloadHistoryItem, int> defaultIndexes,
        StringComparer comparer)
    {
        var leftDefaultIndex = defaultIndexes.TryGetValue(left, out var leftIndex)
            ? leftIndex
            : int.MaxValue;
        var rightDefaultIndex = defaultIndexes.TryGetValue(right, out var rightIndex)
            ? rightIndex
            : int.MaxValue;

        var result = _historyDisplaySortKind switch
        {
            HistoryDisplaySortKind.Name => comparer.Compare(left.UserName, right.UserName),
            HistoryDisplaySortKind.UpdatedAt => left.UpdatedAt.CompareTo(right.UpdatedAt),
            HistoryDisplaySortKind.Platform => CompareHistoryPlatforms(left, right, comparer),
            _ => leftDefaultIndex.CompareTo(rightDefaultIndex)
        };

        if (result == 0 && _historyDisplaySortKind != HistoryDisplaySortKind.Default)
            result = leftDefaultIndex.CompareTo(rightDefaultIndex);

        return _historyDisplaySortAscending ? result : -result;
    }

    private static int CompareHistoryPlatforms(
        DownloadHistoryItem left,
        DownloadHistoryItem right,
        StringComparer comparer)
    {
        var result = comparer.Compare(left.PlatformDisplayText, right.PlatformDisplayText);
        if (result != 0)
            return result;

        return comparer.Compare(left.UserName, right.UserName);
    }

    private static StringComparer GetHistorySortStringComparer()
    {
        var code = LocalizationService.Current?.CurrentLanguageCode;
        if (!string.IsNullOrWhiteSpace(code))
        {
            try
            {
                return StringComparer.Create(CultureInfo.GetCultureInfo(code), ignoreCase: true);
            }
            catch (CultureNotFoundException)
            {
                // 语言包允许自定义区域代码；无对应 CultureInfo 时回退系统文化。
            }
        }

        return StringComparer.CurrentCultureIgnoreCase;
    }

    private void HistoryDisplaySortDownloadHistoryChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        RewireHistoryDisplaySortItemHandlers();
        QueueHistoryDisplaySort();
    }

    private void HistoryDisplaySortFilteredHistoryChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        if (!_historyDisplaySortApplying)
            QueueHistoryDisplaySort();
    }

    private void RewireHistoryDisplaySortItemHandlers()
    {
        if (_historySortViewModel is not { } viewModel)
            return;

        var current = viewModel.DownloadHistory.ToHashSet();
        foreach (var item in _historySortObservedItems
                     .Where(item => !current.Contains(item))
                     .ToArray())
        {
            item.PropertyChanged -= HistoryDisplaySortItemPropertyChanged;
            _historySortObservedItems.Remove(item);
        }

        foreach (var item in current)
        {
            if (_historySortObservedItems.Add(item))
                item.PropertyChanged += HistoryDisplaySortItemPropertyChanged;
        }
    }

    private void HistoryDisplaySortItemPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        var affectsSort = _historyDisplaySortKind switch
        {
            HistoryDisplaySortKind.Name => e.PropertyName == nameof(DownloadHistoryItem.UserName),
            HistoryDisplaySortKind.UpdatedAt => e.PropertyName == nameof(DownloadHistoryItem.UpdatedAt),
            HistoryDisplaySortKind.Platform => e.PropertyName is nameof(DownloadHistoryItem.Platform)
                or nameof(DownloadHistoryItem.PlatformDisplayText)
                or nameof(DownloadHistoryItem.UserName),
            _ => false
        };

        if (affectsSort)
            QueueHistoryDisplaySort();
    }

    private void HistoryDisplaySortLanguageChanged(object? sender, EventArgs e)
    {
        UpdateHistoryDisplaySortUi();
        QueueHistoryDisplaySort();
    }

    private void HistoryDisplaySort_BlockSortedDrag(
        object? sender,
        PointerPressedEventArgs e)
    {
        if (!IsHistoryDisplaySortActive || e.ClickCount != 1)
            return;

        var point = e.GetCurrentPoint(HistoryList);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        var item = FindHistoryItemFromPointerSource(e.Source, out var favoriteButtonHit);
        if (item is null || favoriteButtonHit)
            return;

        // 非默认显示排序时禁止拖动改写真实历史顺序；Ctrl/Shift 多选仍由
        // handledEventsToo 的多选处理器正常接收。
        e.Handled = true;
    }

    private void HistoryDisplaySortWindowClosed(object? sender, EventArgs e)
    {
        _historySortMenu?.Close();

        if (_historySortButton is not null)
            _historySortButton.Click -= HistorySortButton_Click;

        if (_historySortViewModel is { } viewModel)
        {
            viewModel.DownloadHistory.CollectionChanged -= HistoryDisplaySortDownloadHistoryChanged;
            viewModel.FilteredDownloadHistory.CollectionChanged -= HistoryDisplaySortFilteredHistoryChanged;
        }

        foreach (var item in _historySortObservedItems)
            item.PropertyChanged -= HistoryDisplaySortItemPropertyChanged;
        _historySortObservedItems.Clear();

        HistoryList.RemoveHandler(
            PointerPressedEvent,
            HistoryDisplaySort_BlockSortedDrag);

        if (LocalizationService.Current is { } localization)
            localization.LanguageChanged -= HistoryDisplaySortLanguageChanged;

        Closed -= HistoryDisplaySortWindowClosed;
    }

    private static string HistorySortText(string zhCn, string enUs, string jaJp)
    {
        var code = LocalizationService.Current?.CurrentLanguageCode ?? "zh-CN";
        if (code.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            return enUs;
        if (code.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
            return jaJp;
        return zhCn;
    }
}
