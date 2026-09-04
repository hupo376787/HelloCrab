using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
    private const string HistoryFavoriteItemButtonClass = "helloCrabHistoryFavoriteItemButton";

    // 历史项由 ListBox 虚拟化创建；每个 historyItem Border 实际加载时再补收藏按钮，
    // 不改变历史数据模型，也不复制已有收藏存储逻辑。
    private static readonly IDisposable HistoryFavoriteItemLoadedHandler =
        Control.LoadedEvent.AddClassHandler<Border>((border, _) =>
        {
            if (!border.Classes.Contains("historyItem"))
                return;

            if (TopLevel.GetTopLevel(border) is MainWindow window)
                window.EnsureHistoryFavoriteItemButton(border);
        });

    private bool _historyFavoriteItemHooksInstalled;

    private void EnsureHistoryFavoriteItemButton(Border historyItemBorder)
    {
        if (historyItemBorder.Child is not Grid itemGrid)
            return;

        var existingButton = itemGrid.Children
            .OfType<Button>()
            .FirstOrDefault(button => button.Classes.Contains(HistoryFavoriteItemButtonClass));
        if (existingButton is not null)
        {
            UpdateHistoryFavoriteItemButton(existingButton);
            EnsureHistoryFavoriteItemHooks();
            return;
        }

        // 给文字区域右侧留出收藏按钮空间，避免“最后下载”文字与星形按钮重叠。
        var contentPanel = itemGrid.Children
            .OfType<StackPanel>()
            .FirstOrDefault(panel => Grid.GetColumn(panel) == 1);
        if (contentPanel is not null && contentPanel.Margin.Right < 34)
        {
            contentPanel.Margin = new Thickness(
                contentPanel.Margin.Left,
                contentPanel.Margin.Top,
                34,
                contentPanel.Margin.Bottom);
        }

        var star = new TextBlock
        {
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };

        var button = new Button
        {
            Width = 28,
            Height = 28,
            MinWidth = 28,
            MinHeight = 28,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 2, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Content = star
        };
        button.Classes.Add(HistoryFavoriteItemButtonClass);

        // historyItem 的父 Border 本身监听 PointerPressed 来启动拖动。
        // 在按钮目标处先把按下事件标记为已处理，避免收藏点击被父级捕获成拖动。
        button.PointerPressed += (_, e) => e.Handled = true;
        button.Click += HistoryFavoriteItemButton_Click;
        button.PointerEntered += (_, _) => UpdateHistoryFavoriteItemButton(button);
        button.PropertyChanged += (_, e) =>
        {
            if (e.Property == StyledElement.DataContextProperty)
                UpdateHistoryFavoriteItemButton(button);
        };

        Grid.SetColumn(button, 1);
        itemGrid.Children.Add(button);
        UpdateHistoryFavoriteItemButton(button);
        EnsureHistoryFavoriteItemHooks();
    }

    private void EnsureHistoryFavoriteItemHooks()
    {
        if (_historyFavoriteItemHooksInstalled)
            return;

        _historyFavoriteItemHooksInstalled = true;

        // 右键菜单仍然复用原来的收藏命令。菜单关闭后刷新当前已实现的卡片，
        // 因此从右键菜单切换收藏时，卡片星形状态也会立即同步。
        HistoryList.AddHandler(
            InputElement.ContextRequestedEvent,
            HistoryFavoriteItemContextRequested,
            Avalonia.Interactivity.RoutingStrategies.Bubble,
            handledEventsToo: true);

        if (DataContext is MainWindowViewModel viewModel)
            viewModel.PropertyChanged += HistoryFavoriteItemViewModel_PropertyChanged;
        if (LocalizationService.Current is { } localization)
            localization.LanguageChanged += HistoryFavoriteItemLanguageChanged;
        Closed += HistoryFavoriteItemWindowClosed;
    }

    private async void HistoryFavoriteItemButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DownloadHistoryItem item } button)
            return;

        await ToggleHistoryFavoriteAsync(item);
        UpdateHistoryFavoriteItemButton(button);
        RefreshHistoryFavoriteItemButtons();
        e.Handled = true;
    }

    private void HistoryFavoriteItemContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var current = e.Source as Control;
        while (current is not null && !ReferenceEquals(current, HistoryList))
        {
            if (current.DataContext is DownloadHistoryItem
                && current.ContextMenu is { } menu)
            {
                EventHandler? closedHandler = null;
                closedHandler = (_, _) =>
                {
                    menu.Closed -= closedHandler;
                    Dispatcher.UIThread.Post(
                        RefreshHistoryFavoriteItemButtons,
                        DispatcherPriority.Background);
                };
                menu.Closed += closedHandler;
                return;
            }

            current = current.Parent as Control;
        }
    }

    private void RefreshHistoryFavoriteItemButtons()
    {
        foreach (var button in HistoryList
                     .GetVisualDescendants()
                     .OfType<Button>()
                     .Where(button => button.Classes.Contains(HistoryFavoriteItemButtonClass)))
        {
            UpdateHistoryFavoriteItemButton(button);
        }
    }

    private void UpdateHistoryFavoriteItemButton(Button button)
    {
        if (button.Content is not TextBlock star
            || button.DataContext is not DownloadHistoryItem item)
        {
            return;
        }

        var isFavorite = IsHistoryFavorite(item);
        star.Text = isFavorite ? "★" : "☆";

        var normalColor = DataContext is MainWindowViewModel { IsDarkTheme: true }
            ? "#D4D7E2"
            : "#6F7484";
        var color = Color.Parse(isFavorite ? "#FD6F71" : normalColor);
        if (star.Foreground is not SolidColorBrush brush || brush.Color != color)
            star.Foreground = new SolidColorBrush(color);

        ToolTip.SetTip(
            button,
            isFavorite
                ? FavoriteText("取消收藏", "Remove from favorites", "お気に入りを解除")
                : FavoriteText("收藏该作者", "Add this author to favorites", "この投稿者をお気に入りに追加"));
    }

    private void HistoryFavoriteItemViewModel_PropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsDarkTheme))
            Dispatcher.UIThread.Post(RefreshHistoryFavoriteItemButtons);
    }

    private void HistoryFavoriteItemLanguageChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(RefreshHistoryFavoriteItemButtons);

    private void HistoryFavoriteItemWindowClosed(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
            viewModel.PropertyChanged -= HistoryFavoriteItemViewModel_PropertyChanged;
        if (LocalizationService.Current is { } localization)
            localization.LanguageChanged -= HistoryFavoriteItemLanguageChanged;

        HistoryList.RemoveHandler(
            InputElement.ContextRequestedEvent,
            HistoryFavoriteItemContextRequested);
        Closed -= HistoryFavoriteItemWindowClosed;
        _historyFavoriteItemHooksInstalled = false;
    }
}
