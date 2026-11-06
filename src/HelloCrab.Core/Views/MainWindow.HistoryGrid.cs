using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HelloCrab.Core.Services.Localization;
using HelloCrab.Core.Utilities;
using HelloCrab.Core.ViewModels;

namespace HelloCrab.Core.Views;

public partial class MainWindow
{
    private const int HistoryGridInitialBatchSize = 120;
    private const int HistoryGridNextBatchSize = 60;
    private readonly ObservableCollection<HelloCrab.Core.Models.DownloadHistoryItem> _historyGridItems = new();
    private MainWindowViewModel? _historyGridViewModel;
    private bool _isHistoryGridExpanded;
    private bool _historyGridRefreshQueued;
    private bool _historyGridLoadQueued;
    private int _historyGridSizeMode = 1;
    private double _historyGridItemWidth = 315;

    private void InitializeHistoryGrid()
    {
        HistoryGridMediumButton.IsChecked = true;
        UpdateHistoryGridToggleIcon();
        HistoryList.AddHandler(ScrollViewer.ScrollChangedEvent, HistoryGridList_ScrollChanged);
        DataContextChanged += HistoryGridDataContextChanged;
        if (LocalizationService.Current is { } localization)
            localization.LanguageChanged += HistoryGridLanguageChanged;
        Closed += HistoryGridWindowClosed;
    }

    private void HistoryGridDataContextChanged(object? sender, EventArgs e)
    {
        if (_historyGridViewModel is not null)
        {
            _historyGridViewModel.PropertyChanged -= HistoryGridViewModelPropertyChanged;
            _historyGridViewModel.FilteredDownloadHistory.CollectionChanged -= HistoryGridFilteredHistoryChanged;
            _historyGridViewModel.SetHistoryGridItemsSource(null);
        }
        _historyGridViewModel = DataContext as MainWindowViewModel;
        if (_historyGridViewModel is not null)
        {
            _historyGridViewModel.PropertyChanged += HistoryGridViewModelPropertyChanged;
            _historyGridViewModel.FilteredDownloadHistory.CollectionChanged += HistoryGridFilteredHistoryChanged;
        }
        if (_historyGridViewModel?.IsHistoryVisible != true)
            SetHistoryGridExpanded(false);
        UpdateHistoryGridToggleIcon();
    }

    private void HistoryGridViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsHistoryVisible)
            && _historyGridViewModel?.IsHistoryVisible != true)
        {
            SetHistoryGridExpanded(false);
        }
    }

    private void HistoryGridToggleButton_Click(object? sender, RoutedEventArgs e)
        => SetHistoryGridExpanded(!_isHistoryGridExpanded);

    private void SetHistoryGridExpanded(bool expanded)
    {
        if (_isHistoryGridExpanded == expanded)
            return;

        _isHistoryGridExpanded = expanded;
        if (expanded && _historyGridViewModel is { } viewModel)
        {
            HistoryCollectionSynchronizer.Sync(
                _historyGridItems,
                viewModel.FilteredDownloadHistory.Take(HistoryGridInitialBatchSize).ToArray());
            viewModel.SetHistoryGridItemsSource(_historyGridItems);
        }
        else if (!expanded)
        {
            _historyGridViewModel?.SetHistoryGridItemsSource(null);
            _historyGridItems.Clear();
        }

        MainWorkspace.IsVisible = !expanded;
        Grid.SetColumn(HistoryPanel, expanded ? 0 : 1);
        Grid.SetColumnSpan(HistoryPanel, expanded ? 2 : 1);
        HistoryPanel.Width = expanded ? double.NaN : 390;
        HistoryGridSizeControls.IsVisible = expanded;
        HistoryList.Margin = expanded ? new Thickness(0) : new Thickness(0, 0, -18, 0);

        if (expanded)
        {
            HistoryList.Classes.Add("historyGridList");
            HistoryList.ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                ItemWidth = _historyGridItemWidth
            });
            UpdateHistoryGridItemWidth();
        }
        else
        {
            HistoryList.Classes.Remove("historyGridList");
            HistoryList.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
        }

        UpdateHistoryGridToggleIcon();
        RefreshHistoryFavoriteItemButtons();
        Dispatcher.UIThread.Post(UpdateHistoryGridItemWidth, DispatcherPriority.Loaded);
        Dispatcher.UIThread.Post(RefreshHistoryFavoriteItemButtons, DispatcherPriority.Loaded);
    }

    private void HistoryGridSmallButton_Click(object? sender, RoutedEventArgs e)
        => SetHistoryGridSizeMode(0);

    private void HistoryGridMediumButton_Click(object? sender, RoutedEventArgs e)
        => SetHistoryGridSizeMode(1);

    private void HistoryGridLargeButton_Click(object? sender, RoutedEventArgs e)
        => SetHistoryGridSizeMode(2);

    private void SetHistoryGridSizeMode(int mode)
    {
        _historyGridSizeMode = mode;
        HistoryGridSmallButton.IsChecked = mode == 0;
        HistoryGridMediumButton.IsChecked = mode == 1;
        HistoryGridLargeButton.IsChecked = mode == 2;
        HistoryList.Classes.Set("smallGrid", mode == 0 && _isHistoryGridExpanded);
        HistoryList.Classes.Set("largeGrid", mode == 2 && _isHistoryGridExpanded);
        RefreshHistoryFavoriteItemButtons();
        UpdateHistoryGridItemWidth();
    }

    private void HistoryGridFilteredHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_isHistoryGridExpanded || _historyGridRefreshQueued)
            return;

        _historyGridRefreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _historyGridRefreshQueued = false;
            if (!_isHistoryGridExpanded || _historyGridViewModel is not { } viewModel)
                return;

            var visibleCount = Math.Max(HistoryGridInitialBatchSize, _historyGridItems.Count);
            HistoryCollectionSynchronizer.Sync(
                _historyGridItems,
                viewModel.FilteredDownloadHistory.Take(visibleCount).ToArray());
        }, DispatcherPriority.Background);
    }

    private void HistoryGridList_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (!_isHistoryGridExpanded)
            return;

        var scrollViewer = e.Source as ScrollViewer
                           ?? HistoryList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scrollViewer is null
            || _historyGridViewModel is not { } viewModel
            || _historyGridItems.Count >= viewModel.FilteredDownloadHistory.Count)
        {
            return;
        }

        var remaining = scrollViewer.Extent.Height
                        - scrollViewer.Offset.Y
                        - scrollViewer.Viewport.Height;
        if (remaining > scrollViewer.Viewport.Height * 0.35 || _historyGridLoadQueued)
            return;

        _historyGridLoadQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _historyGridLoadQueued = false;
            if (!_isHistoryGridExpanded || _historyGridViewModel is not { } currentViewModel)
                return;

            var source = currentViewModel.FilteredDownloadHistory;
            var nextCount = Math.Min(source.Count, _historyGridItems.Count + HistoryGridNextBatchSize);
            for (var index = _historyGridItems.Count; index < nextCount; index++)
                _historyGridItems.Add(source[index]);
        }, DispatcherPriority.Background);
    }

    private void HistoryList_SizeChanged(object? sender, SizeChangedEventArgs e)
        => UpdateHistoryGridItemWidth();

    private void UpdateHistoryGridItemWidth()
    {
        if (!_isHistoryGridExpanded || HistoryList.Bounds.Width <= 0)
            return;

        var usableWidth = Math.Max(300, HistoryList.Bounds.Width - 16);
        var mediumColumns = Math.Clamp((int)Math.Floor(usableWidth / 315), 1, 6);
        var columns = _historyGridSizeMode == 0
            ? Math.Min(mediumColumns * 2, 12)
            : mediumColumns;
        _historyGridItemWidth = Math.Floor(usableWidth / columns * 10) / 10;

        foreach (var panel in HistoryList.GetVisualDescendants().OfType<WrapPanel>())
            panel.ItemWidth = _historyGridItemWidth;
    }

    private void UpdateHistoryGridToggleIcon()
    {
        HistoryGridTogglePath.Data = Geometry.Parse(_isHistoryGridExpanded
            ? "M2,7 L7,7 L7,2 M16,7 L11,7 L11,2 M2,11 L7,11 L7,16 M16,11 L11,11 L11,16"
            : "M7,2 L2,2 L2,7 M11,2 L16,2 L16,7 M2,11 L2,16 L7,16 M16,11 L16,16 L11,16");
        ToolTip.SetTip(HistoryGridToggleButton, _isHistoryGridExpanded
            ? HistoryGridText("History.GridRestore", "恢复历史列表侧栏")
            : HistoryGridText("History.GridExpand", "展开下载历史"));
    }

    private static string HistoryGridText(string key, string fallback)
        => LocalizationService.Current?.Get(key, fallback) ?? fallback;

    private void HistoryGridLanguageChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(UpdateHistoryGridToggleIcon);

    private void HistoryGridWindowClosed(object? sender, EventArgs e)
    {
        if (_historyGridViewModel is not null)
        {
            _historyGridViewModel.PropertyChanged -= HistoryGridViewModelPropertyChanged;
            _historyGridViewModel.FilteredDownloadHistory.CollectionChanged -= HistoryGridFilteredHistoryChanged;
            _historyGridViewModel.SetHistoryGridItemsSource(null);
        }
        HistoryList.RemoveHandler(ScrollViewer.ScrollChangedEvent, HistoryGridList_ScrollChanged);
        if (LocalizationService.Current is { } localization)
            localization.LanguageChanged -= HistoryGridLanguageChanged;
    }
}
