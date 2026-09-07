using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HelloCrab.Core.Models;
using HelloCrab.Core.Services.Localization;
using HelloCrab.Core.ViewModels;

namespace HelloCrab.Core.Views;

public partial class MainWindow
{
    private const string HistoryMultiSelectHookClass = "helloCrabHistoryMultiSelectHook";
    private const string HistorySelectionMarkerClass = "helloCrabHistorySelectionMarker";
    private const string HistoryBatchMenuClass = "helloCrabHistoryBatchMenu";
    private const string HistoryBatchCopyClass = "helloCrabHistoryBatchCopy";
    private const string HistoryBatchFolderClass = "helloCrabHistoryBatchFolder";
    private const string HistoryBatchRecollectClass = "helloCrabHistoryBatchRecollect";
    private const string HistoryBatchFavoriteClass = "helloCrabHistoryBatchFavorite";
    private const string HistoryBatchRemoveClass = "helloCrabHistoryBatchRemove";

    private static readonly IDisposable HistoryMultiSelectDataContextHandler =
        StyledElement.DataContextProperty.Changed.AddClassHandler<MainWindow>((window, _) =>
            Dispatcher.UIThread.Post(
                window.InstallHistoryMultiSelect,
                DispatcherPriority.Loaded));

    private static readonly IDisposable HistoryMultiSelectItemLoadedHandler =
        Control.LoadedEvent.AddClassHandler<Border>((border, _) =>
        {
            if (!border.Classes.Contains("historyItem"))
                return;

            if (TopLevel.GetTopLevel(border) is MainWindow window)
                window.EnsureHistoryMultiSelectItem(border);
        });

    private DownloadHistoryItem? _historySelectionAnchorItem;
    private IReadOnlyList<DownloadHistoryItem> _pendingBatchHistoryDeleteItems = Array.Empty<DownloadHistoryItem>();
    private bool _historyMultiSelectInstalled;

    private void InstallHistoryMultiSelect()
    {
        if (_historyMultiSelectInstalled)
            return;

        _historyMultiSelectInstalled = true;
        HistoryList.SelectionMode = SelectionMode.Multiple;

        HistoryList.AddHandler(
            PointerPressedEvent,
            HistoryMultiSelect_PointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        HistoryList.SelectionChanged += HistoryMultiSelect_SelectionChanged;
        HistoryDeleteOverlay.AddHandler(
            Button.ClickEvent,
            HistoryBatchDeleteOverlay_ButtonClick,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        Closed += HistoryMultiSelectWindowClosed;

        Dispatcher.UIThread.Post(RefreshHistorySelectionMarkers, DispatcherPriority.Background);
    }

    private void HistoryMultiSelect_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount > 1)
            return;

        var item = FindHistoryItemFromPointerSource(e.Source, out var favoriteButtonHit);
        if (item is null || favoriteButtonHit)
            return;

        var point = e.GetCurrentPoint(HistoryList);
        if (point.Properties.IsRightButtonPressed)
        {
            // Windows 资源管理器式行为：右键已选项目保留整组选择；
            // 右键未选项目时只选中当前项目，避免误操作之前的选择。
            if (!IsHistoryItemSelected(item))
                SelectOnlyHistoryItem(item);

            _historySelectionAnchorItem = item;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
            return;

        var selectedItems = HistoryList.SelectedItems;
        if (selectedItems is null)
            return;

        var hasCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var hasShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (hasShift)
        {
            SelectHistoryRange(item, additive: hasCtrl);
            return;
        }

        if (hasCtrl)
        {
            if (selectedItems.Contains(item))
                selectedItems.Remove(item);
            else
                selectedItems.Add(item);

            _historySelectionAnchorItem = item;
            return;
        }

        SelectOnlyHistoryItem(item);
        _historySelectionAnchorItem = item;
    }

    private DownloadHistoryItem? FindHistoryItemFromPointerSource(
        object? source,
        out bool favoriteButtonHit)
    {
        favoriteButtonHit = false;
        DownloadHistoryItem? historyItem = null;
        var current = source as Control;

        while (current is not null && !ReferenceEquals(current, HistoryList))
        {
            if (current is Button button
                && button.Classes.Contains(HistoryFavoriteItemButtonClass))
            {
                favoriteButtonHit = true;
                return null;
            }

            if (historyItem is null && current.DataContext is DownloadHistoryItem item)
                historyItem = item;

            current = current.Parent as Control;
        }

        return historyItem;
    }

    private void SelectOnlyHistoryItem(DownloadHistoryItem item)
    {
        var selectedItems = HistoryList.SelectedItems;
        if (selectedItems is null)
            return;

        if (selectedItems.Count == 1 && selectedItems.Contains(item))
            return;

        selectedItems.Clear();
        selectedItems.Add(item);
    }

    private void SelectHistoryRange(DownloadHistoryItem clickedItem, bool additive)
    {
        if (DataContext is not MainWindowViewModel viewModel
            || HistoryList.SelectedItems is not { } selectedItems)
        {
            return;
        }

        var clickedIndex = viewModel.FilteredDownloadHistory.IndexOf(clickedItem);
        if (clickedIndex < 0)
            return;

        var anchor = _historySelectionAnchorItem;
        var anchorIndex = anchor is null
            ? -1
            : viewModel.FilteredDownloadHistory.IndexOf(anchor);
        if (anchorIndex < 0)
        {
            anchor = HistoryList.SelectedItem as DownloadHistoryItem ?? clickedItem;
            anchorIndex = viewModel.FilteredDownloadHistory.IndexOf(anchor);
        }

        if (anchorIndex < 0)
        {
            anchor = clickedItem;
            anchorIndex = clickedIndex;
        }

        _historySelectionAnchorItem ??= anchor;

        if (!additive)
            selectedItems.Clear();

        var first = Math.Min(anchorIndex, clickedIndex);
        var last = Math.Max(anchorIndex, clickedIndex);
        for (var index = first; index <= last; index++)
        {
            var item = viewModel.FilteredDownloadHistory[index];
            if (!selectedItems.Contains(item))
                selectedItems.Add(item);
        }
    }

    private bool IsHistoryItemSelected(DownloadHistoryItem item)
        => HistoryList.SelectedItems?.Contains(item) == true;

    private IReadOnlyList<DownloadHistoryItem> GetSelectedHistoryItems(
        DownloadHistoryItem contextItem)
    {
        var selectedItems = HistoryList.SelectedItems;
        if (selectedItems is null
            || selectedItems.Count == 0
            || !selectedItems.Contains(contextItem))
        {
            return new[] { contextItem };
        }

        if (DataContext is MainWindowViewModel viewModel)
        {
            var ordered = viewModel.FilteredDownloadHistory
                .Where(selectedItems.Contains)
                .ToArray();
            if (ordered.Length > 0)
                return ordered;
        }

        return selectedItems.OfType<DownloadHistoryItem>().ToArray();
    }

    private void HistoryMultiSelect_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        => Dispatcher.UIThread.Post(RefreshHistorySelectionMarkers, DispatcherPriority.Render);

    private void EnsureHistoryMultiSelectItem(Border historyItemBorder)
    {
        if (!historyItemBorder.Classes.Contains(HistoryMultiSelectHookClass))
        {
            historyItemBorder.Classes.Add(HistoryMultiSelectHookClass);
            historyItemBorder.DataContextChanged += (_, _) =>
                Dispatcher.UIThread.Post(
                    () => UpdateHistorySelectionMarker(historyItemBorder),
                    DispatcherPriority.Render);

            if (historyItemBorder.ContextMenu is { } menu)
            {
                menu.Opened += (_, _) =>
                    ConfigureHistoryBatchContextMenu(
                        menu,
                        historyItemBorder.DataContext as DownloadHistoryItem);
            }
        }

        if (historyItemBorder.Child is Grid itemGrid
            && !itemGrid.Children
                .OfType<Border>()
                .Any(border => border.Classes.Contains(HistorySelectionMarkerClass)))
        {
            var marker = new Border
            {
                Margin = new Thickness(-6),
                BorderBrush = new SolidColorBrush(Color.Parse("#B87C3AED")),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(8),
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false,
                IsVisible = false
            };
            marker.Classes.Add(HistorySelectionMarkerClass);
            Grid.SetColumn(marker, 0);
            Grid.SetColumnSpan(marker, 3);
            itemGrid.Children.Add(marker);
        }

        UpdateHistorySelectionMarker(historyItemBorder);
    }

    private void RefreshHistorySelectionMarkers()
    {
        foreach (var border in HistoryList
                     .GetVisualDescendants()
                     .OfType<Border>()
                     .Where(border => border.Classes.Contains("historyItem")))
        {
            UpdateHistorySelectionMarker(border);
        }
    }

    private void UpdateHistorySelectionMarker(Border historyItemBorder)
    {
        if (historyItemBorder.Child is not Grid itemGrid)
            return;

        var marker = itemGrid.Children
            .OfType<Border>()
            .FirstOrDefault(border => border.Classes.Contains(HistorySelectionMarkerClass));
        if (marker is null)
            return;

        marker.IsVisible = historyItemBorder.DataContext is DownloadHistoryItem item
                           && IsHistoryItemSelected(item);
    }

    private void ConfigureHistoryBatchContextMenu(
        ContextMenu menu,
        DownloadHistoryItem? contextItem)
    {
        if (contextItem is null)
            return;

        var selectedItems = GetSelectedHistoryItems(contextItem);
        var isBatch = selectedItems.Count > 1;
        EnsureHistoryBatchMenuItems(menu);

        foreach (var control in menu.Items.OfType<Control>())
        {
            var isBatchControl = control.Classes.Contains(HistoryBatchMenuClass);
            control.IsVisible = isBatch ? isBatchControl : !isBatchControl;
        }

        if (!isBatch)
            return;

        var count = selectedItems.Count;
        ConfigureBatchMenuItem(
            menu,
            HistoryBatchCopyClass,
            contextItem,
            string.Format(
                HistoryBatchText("复制所选作者链接（{0}）", "Copy selected author URLs ({0})", "選択した作者 URL をコピー（{0}）"),
                count));
        ConfigureBatchMenuItem(
            menu,
            HistoryBatchFolderClass,
            contextItem,
            string.Format(
                HistoryBatchText("打开所选文件夹（{0}）", "Open selected folders ({0})", "選択したフォルダーを開く（{0}）"),
                count));
        ConfigureBatchMenuItem(
            menu,
            HistoryBatchRecollectClass,
            contextItem,
            string.Format(
                HistoryBatchText("重新采集所选（{0}）", "Recollect selected ({0})", "選択項目を再収集（{0}）"),
                count));

        var makeFavorite = selectedItems.Any(item => !IsHistoryFavorite(item));
        ConfigureBatchMenuItem(
            menu,
            HistoryBatchFavoriteClass,
            contextItem,
            string.Format(
                makeFavorite
                    ? HistoryBatchText("收藏所选作者（{0}）", "Add selected authors to favorites ({0})", "選択した作者をお気に入りに追加（{0}）")
                    : HistoryBatchText("取消收藏所选（{0}）", "Remove selected from favorites ({0})", "選択項目のお気に入りを解除（{0}）"),
                count));
        ConfigureBatchMenuItem(
            menu,
            HistoryBatchRemoveClass,
            contextItem,
            string.Format(
                HistoryBatchText("移除所选（{0}）", "Remove selected ({0})", "選択項目を削除（{0}）"),
                count));
    }

    private void EnsureHistoryBatchMenuItems(ContextMenu menu)
    {
        if (menu.Items
            .OfType<MenuItem>()
            .Any(item => item.Classes.Contains(HistoryBatchCopyClass)))
        {
            return;
        }

        var copy = CreateHistoryBatchMenuItem(HistoryBatchCopyClass, HistoryBatchCopyUrls_Click);
        var folders = CreateHistoryBatchMenuItem(HistoryBatchFolderClass, HistoryBatchOpenFolders_Click);
        var recollect = CreateHistoryBatchMenuItem(HistoryBatchRecollectClass, HistoryBatchRecollect_Click);
        var favorite = CreateHistoryBatchMenuItem(HistoryBatchFavoriteClass, HistoryBatchFavorite_Click);
        var remove = CreateHistoryBatchMenuItem(HistoryBatchRemoveClass, HistoryBatchRemove_Click);
        var separator = new Separator();
        separator.Classes.Add(HistoryBatchMenuClass);

        menu.Items.Add(copy);
        menu.Items.Add(folders);
        menu.Items.Add(recollect);
        menu.Items.Add(favorite);
        menu.Items.Add(separator);
        menu.Items.Add(remove);
    }

    private static MenuItem CreateHistoryBatchMenuItem(
        string actionClass,
        EventHandler<RoutedEventArgs> clickHandler)
    {
        var item = new MenuItem();
        item.Classes.Add(HistoryBatchMenuClass);
        item.Classes.Add(actionClass);
        item.Click += clickHandler;
        return item;
    }

    private static void ConfigureBatchMenuItem(
        ContextMenu menu,
        string actionClass,
        DownloadHistoryItem contextItem,
        string header)
    {
        var menuItem = menu.Items
            .OfType<MenuItem>()
            .FirstOrDefault(item => item.Classes.Contains(actionClass));
        if (menuItem is null)
            return;

        menuItem.Tag = contextItem;
        menuItem.Header = header;
        menuItem.IsVisible = true;
    }

    private async void HistoryBatchCopyUrls_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: DownloadHistoryItem contextItem }
            || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var urls = GetSelectedHistoryItems(contextItem)
            .Select(item => item.OriginalUrl?.Trim())
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (urls.Length == 0)
        {
            viewModel.AddRemoteLog(HistoryBatchText(
                "所选作者没有可复制的主页地址。",
                "The selected authors do not have copyable home URLs.",
                "選択した作者にはコピー可能なホーム URL がありません。"));
            return;
        }

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard
                            ?? throw new InvalidOperationException(
                                HistoryBatchText(
                                    "系统剪贴板不可用。",
                                    "The system clipboard is unavailable.",
                                    "システムのクリップボードを利用できません。"));
            await clipboard.SetTextAsync(string.Join(Environment.NewLine, urls));
            viewModel.AddRemoteLog(string.Format(
                HistoryBatchText(
                    "已复制 {0} 个作者链接。",
                    "Copied {0} author URLs.",
                    "作者 URL を {0} 件コピーしました。"),
                urls.Length));
        }
        catch (Exception ex)
        {
            viewModel.AddRemoteLog(string.Format(
                HistoryBatchText(
                    "批量复制作者链接失败：{0}",
                    "Failed to copy author URLs: {0}",
                    "作者 URL の一括コピーに失敗しました：{0}"),
                ex.Message));
        }
    }

    private void HistoryBatchOpenFolders_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: DownloadHistoryItem contextItem }
            || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var items = GetSelectedHistoryItems(contextItem);
        foreach (var item in items)
            viewModel.OpenHistoryFolder(item);

        viewModel.AddRemoteLog(string.Format(
            HistoryBatchText(
                "已请求打开 {0} 个作者文件夹。",
                "Requested opening {0} author folders.",
                "作者フォルダー {0} 件を開くよう要求しました。"),
            items.Count));
    }

    private async void HistoryBatchRecollect_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: DownloadHistoryItem contextItem }
            || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var urls = GetSelectedHistoryItems(contextItem)
            .Select(item => item.OriginalUrl?.Trim())
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (urls.Length == 0)
        {
            viewModel.AddRemoteLog(HistoryBatchText(
                "所选作者没有可用于重新采集的地址。",
                "The selected authors do not have URLs that can be recollected.",
                "選択した作者には再収集に使用できる URL がありません。"));
            return;
        }

        await viewModel.StartManualBatchCaptureAsync(string.Join(Environment.NewLine, urls));
    }

    private async void HistoryBatchFavorite_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: DownloadHistoryItem contextItem })
            return;

        var items = GetSelectedHistoryItems(contextItem);
        var makeFavorite = items.Any(item => !IsHistoryFavorite(item));
        var changed = 0;
        foreach (var item in items)
        {
            var key = BuildHistoryFavoriteKey(item);
            if (string.IsNullOrWhiteSpace(key))
                continue;

            if (makeFavorite)
            {
                if (_historyFavoriteKeys.Add(key))
                    changed++;
            }
            else if (_historyFavoriteKeys.Remove(key))
            {
                changed++;
            }
        }

        if (changed == 0)
            return;

        ApplyHistoryFavoriteFilter();
        RefreshHistoryFavoriteItemButtons();
        _historyFavoritesViewModel?.AddRemoteLog(string.Format(
            makeFavorite
                ? HistoryBatchText(
                    "已批量收藏 {0} 位作者。",
                    "Added {0} authors to favorites.",
                    "作者 {0} 人をお気に入りに追加しました。")
                : HistoryBatchText(
                    "已批量取消收藏 {0} 位作者。",
                    "Removed {0} authors from favorites.",
                    "作者 {0} 人のお気に入りを解除しました。"),
            changed));

        try
        {
            await _historyFavoritesStore.SaveAsync(_historyFavoriteKeys.ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _historyFavoritesViewModel?.AddRemoteLog(string.Format(
                HistoryBatchText(
                    "保存收藏列表失败：{0}",
                    "Failed to save favorites: {0}",
                    "お気に入り一覧の保存に失敗しました：{0}"),
                ex.Message));
        }
    }

    private void HistoryBatchRemove_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: DownloadHistoryItem contextItem })
            return;

        var items = GetSelectedHistoryItems(contextItem);
        if (items.Count < 2)
            return;

        EndHistoryDrag(saveOrder: false);
        _pendingHistoryDeleteItem = null;
        _pendingBatchHistoryDeleteItems = items.ToArray();

        var names = items
            .Take(4)
            .Select(item => string.IsNullOrWhiteSpace(item.UserName) ? item.UserId : item.UserName)
            .ToArray();
        var namePreview = string.Join("、", names);
        if (items.Count > names.Length)
            namePreview += HistoryBatchText(" 等", " and others", " ほか");

        HistoryDeleteAuthorText.Text = string.Format(
            HistoryBatchText(
                "已选择 {0} 位作者：{1}",
                "Selected {0} authors: {1}",
                "作者を {0} 人選択：{1}"),
            items.Count,
            namePreview);
        HistoryDeletePathText.Text = string.Format(
            HistoryBatchText(
                "将批量处理 {0} 位作者。选择“同时删除磁盘文件”会删除每位作者对应的下载目录。",
                "This will process {0} authors in bulk. Choosing “delete disk files too” will delete each author's download folder.",
                "{0} 人の作者を一括処理します。「ディスク上のファイルも削除」を選ぶと、各作者のダウンロードフォルダーも削除されます。"),
            items.Count);
        HistoryDeleteOverlay.IsVisible = true;
    }

    private void HistoryBatchDeleteOverlay_ButtonClick(object? sender, RoutedEventArgs e)
    {
        if (_pendingBatchHistoryDeleteItems.Count < 2
            || e.Source is not Button button)
        {
            return;
        }

        var column = Grid.GetColumn(button);
        if (column is < 0 or > 2)
            return;

        e.Handled = true;
        var items = _pendingBatchHistoryDeleteItems.ToArray();
        _pendingBatchHistoryDeleteItems = Array.Empty<DownloadHistoryItem>();
        CloseHistoryDeleteOverlay();

        if (column == 0)
            return;

        _ = RemoveSelectedHistoryAsync(items, deleteDiskFiles: column == 2);
    }

    private async Task RemoveSelectedHistoryAsync(
        IReadOnlyList<DownloadHistoryItem> items,
        bool deleteDiskFiles)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        foreach (var item in items)
            await viewModel.RemoveHistoryAsync(item, deleteDiskFiles);

        viewModel.AddRemoteLog(string.Format(
            deleteDiskFiles
                ? HistoryBatchText(
                    "已完成 {0} 位作者的批量移除，并请求删除对应磁盘目录。",
                    "Finished removing {0} authors and requested deletion of their disk folders.",
                    "作者 {0} 人の一括削除と対応するディスクフォルダーの削除が完了しました。")
                : HistoryBatchText(
                    "已完成 {0} 位作者的批量历史移除。",
                    "Finished removing {0} authors from history.",
                    "作者 {0} 人を履歴から一括削除しました。"),
            items.Count));
    }

    private static string HistoryBatchText(string zhCn, string enUs, string jaJp)
    {
        var code = LocalizationService.Current?.CurrentLanguageCode ?? "zh-CN";
        if (code.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            return enUs;
        if (code.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
            return jaJp;
        return zhCn;
    }

    private void HistoryMultiSelectWindowClosed(object? sender, EventArgs e)
    {
        HistoryList.RemoveHandler(
            PointerPressedEvent,
            HistoryMultiSelect_PointerPressed);
        HistoryList.SelectionChanged -= HistoryMultiSelect_SelectionChanged;
        HistoryDeleteOverlay.RemoveHandler(
            Button.ClickEvent,
            HistoryBatchDeleteOverlay_ButtonClick);
        Closed -= HistoryMultiSelectWindowClosed;
        _pendingBatchHistoryDeleteItems = Array.Empty<DownloadHistoryItem>();
        _historyMultiSelectInstalled = false;
    }
}
