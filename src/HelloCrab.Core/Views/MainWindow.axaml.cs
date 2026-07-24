using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using HelloCrab.Core.Models;
using HelloCrab.Core.Services.Localization;
using HelloCrab.Core.ViewModels;

namespace HelloCrab.Core.Views;

public partial class MainWindow : Window
{
    private DownloadHistoryItem? _draggedHistoryItem;
    private DownloadHistoryItem? _pendingHistoryDeleteItem;
    private Point _dragStartPoint;
    private bool _isHistoryDragging;
    private Border? _dragGhost;
    private IPointer? _historyDragPointer;
    private ObservableCollection<string>? _subscribedLogs;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        Closing += MainWindow_Closing;
        Closed += OnClosed;
        Opened += (_, _) => UpdateWindowFrame();
        PropertyChanged += MainWindow_PropertyChanged;
        DataContextChanged += MainWindow_DataContextChanged;
        Deactivated += (_, _) => EndHistoryDrag(saveOrder: true);

        // ListBox/ScrollViewer 可能把 PointerMoved 标记为已处理，使用 handledEventsToo
        // 确保历史拖动在整个窗口范围内都能持续收到移动和释放事件。
        AddHandler(
            PointerMovedEvent,
            Window_PointerMoved,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        AddHandler(
            PointerReleasedEvent,
            Window_PointerReleased,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
    }


    private void MainWindow_DataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedLogs is not null)
            _subscribedLogs.CollectionChanged -= Logs_CollectionChanged;

        _subscribedLogs = (DataContext as MainWindowViewModel)?.Logs;
        if (_subscribedLogs is not null)
        {
            _subscribedLogs.CollectionChanged += Logs_CollectionChanged;
            ScrollLogToTop();
        }
    }

    private void Logs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is not (NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset))
            return;

        // 新项目插入集合头部后，ScrollViewer 默认会尝试保留原锚点，
        // 因而 Offset 会随着日志累积逐渐向下。这里在布局前后各归零一次。
        Dispatcher.UIThread.Post(ScrollLogToTop, DispatcherPriority.Render);
        Dispatcher.UIThread.Post(ScrollLogToTop, DispatcherPriority.Background);
    }

    private void ScrollLogToTop()
    {
        if (_subscribedLogs is null || _subscribedLogs.Count == 0)
            return;

        var offset = LogScrollViewer.Offset;
        if (offset.Y != 0)
            LogScrollViewer.Offset = new Vector(offset.X, 0);
    }


    private void MainWindow_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty)
            UpdateWindowFrame();
    }

    private void UpdateWindowFrame()
    {
        var isMaximized = WindowState == WindowState.Maximized;
        WindowFrame.Margin = isMaximized ? new Thickness(0) : new Thickness(1);
        WindowFrame.CornerRadius = isMaximized ? new CornerRadius(0) : new CornerRadius(8);
        WindowFrame.BorderThickness = isMaximized ? new Thickness(0) : new Thickness(1);
        ResizeHandles.IsVisible = !isMaximized && CanResize;
    }

    private void ResizeHandle_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (WindowState != WindowState.Normal
            || !CanResize
            || sender is not Control { Tag: string edgeName })
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        var edge = edgeName switch
        {
            "NorthWest" => WindowEdge.NorthWest,
            "North" => WindowEdge.North,
            "NorthEast" => WindowEdge.NorthEast,
            "West" => WindowEdge.West,
            "East" => WindowEdge.East,
            "SouthWest" => WindowEdge.SouthWest,
            "South" => WindowEdge.South,
            "SouthEast" => WindowEdge.SouthEast,
            _ => (WindowEdge?)null
        };

        if (edge.HasValue)
        {
            BeginResizeDrag(edge.Value, e);
            e.Handled = true;
        }
    }

    private async void SelectFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = LocalizationService.Current?.Get("Download.SelectFolderDialog", "选择下载目录") ?? "选择下载目录",
            AllowMultiple = false
        });

        if (folders.Count > 0 && DataContext is MainWindowViewModel viewModel)
            viewModel.DownloadRoot = folders[0].Path.LocalPath;
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        // BeginMoveDrag 之前先处理双击，否则第二次按下会继续进入拖动，
        // 自定义标题栏就无法像系统标题栏一样最大化/还原。
        if (e.ClickCount >= 2)
        {
            ToggleMaximizeRestore();
            e.Handled = true;
            return;
        }

        BeginMoveDrag(e);
        e.Handled = true;
    }

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object? sender, RoutedEventArgs e)
        => ToggleMaximizeRestore();

    private void ToggleMaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        UpdateWindowFrame();
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
        => ShowCloseConfirmation();

    private void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose)
            return;

        e.Cancel = true;
        ShowCloseConfirmation();
    }

    private void ShowCloseConfirmation()
    {
        StopCaptureConfirmOverlay.IsVisible = false;
        CloseConfirmOverlay.IsVisible = true;
    }

    private void StopCaptureButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel
            || !viewModel.StopCaptureCommand.CanExecute(null))
        {
            return;
        }

        CloseConfirmOverlay.IsVisible = false;
        StopCaptureConfirmOverlay.IsVisible = true;
    }

    private void StopCaptureCancelButton_Click(object? sender, RoutedEventArgs e)
    {
        StopCaptureConfirmOverlay.IsVisible = false;
    }

    private void StopCaptureConfirmButton_Click(object? sender, RoutedEventArgs e)
    {
        StopCaptureConfirmOverlay.IsVisible = false;

        if (DataContext is MainWindowViewModel viewModel
            && viewModel.StopCaptureCommand.CanExecute(null))
        {
            viewModel.StopCaptureCommand.Execute(null);
        }
    }

    private void CloseCancelButton_Click(object? sender, RoutedEventArgs e)
    {
        CloseConfirmOverlay.IsVisible = false;
    }

    private void CloseConfirmButton_Click(object? sender, RoutedEventArgs e)
    {
        _allowClose = true;
        CloseConfirmOverlay.IsVisible = false;
        Close();
    }

    private void HistoryItem_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: DownloadHistoryItem item })
            return;

        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        _draggedHistoryItem = item;
        _dragStartPoint = e.GetPosition(this);
        _isHistoryDragging = false;
        _historyDragPointer = e.Pointer;

        // 捕获到窗口后，即使指针离开原列表项，仍能持续拖动和在释放时保存顺序。
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void MoveDraggedHistoryItem(Point pointerInHistoryList)
    {
        if (_draggedHistoryItem is null
            || DataContext is not MainWindowViewModel viewModel
            || viewModel.FilteredDownloadHistory.Count < 2)
        {
            return;
        }

        var targetIndex = -1;
        var realizedContainers = HistoryList.GetRealizedContainers();
        foreach (var container in realizedContainers)
        {
            var topLeft = container.TranslatePoint(default, HistoryList);
            if (topLeft is null)
                continue;

            var centerY = topLeft.Value.Y + container.Bounds.Height / 2d;
            if (pointerInHistoryList.Y <= centerY)
            {
                targetIndex = HistoryList.IndexFromContainer(container);
                break;
            }
        }

        // 指针位于最后一个已实现项目下方时，移动到列表末尾。
        if (targetIndex < 0)
            targetIndex = viewModel.FilteredDownloadHistory.Count - 1;

        viewModel.MoveHistoryItemPreview(_draggedHistoryItem, targetIndex);
    }

    private void Window_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggedHistoryItem is null)
            return;

        var currentPoint = e.GetCurrentPoint(this);
        if (!currentPoint.Properties.IsLeftButtonPressed)
        {
            EndHistoryDrag(saveOrder: _isHistoryDragging);
            return;
        }

        var position = e.GetPosition(this);
        if (!_isHistoryDragging)
        {
            var delta = position - _dragStartPoint;
            if (Math.Abs(delta.X) < 8 && Math.Abs(delta.Y) < 8)
                return;

            _isHistoryDragging = true;
            _dragGhost = CreateDragGhost(_draggedHistoryItem);
            DragOverlay.Children.Add(_dragGhost);
        }

        if (_dragGhost is not null)
        {
            var overlayPosition = e.GetPosition(DragOverlay);
            Canvas.SetLeft(_dragGhost, overlayPosition.X + 18);
            Canvas.SetTop(_dragGhost, overlayPosition.Y + 18);
        }

        MoveDraggedHistoryItem(e.GetPosition(HistoryList));
        e.Handled = true;
    }

    private void Window_PointerReleased(object? sender, PointerReleasedEventArgs e)
        => EndHistoryDrag(saveOrder: _isHistoryDragging);

    private void EndHistoryDrag(bool saveOrder)
    {
        if (_dragGhost is not null)
            DragOverlay.Children.Remove(_dragGhost);

        _historyDragPointer?.Capture(null);

        _dragGhost = null;
        _draggedHistoryItem = null;
        _historyDragPointer = null;
        _isHistoryDragging = false;

        if (saveOrder && DataContext is MainWindowViewModel viewModel)
            _ = viewModel.PersistHistoryOrderAsync();
    }

    private static Border CreateDragGhost(DownloadHistoryItem item)
    {
        var avatarFallback = new TextBlock
        {
            Text = LocalizationService.Current?.Get("History.AvatarFallback", "人") ?? "人",
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#7C3AED")),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        var avatar = new Image
        {
            Source = item.AvatarImage,
            Width = 46,
            Height = 46,
            Stretch = Stretch.UniformToFill
        };

        var avatarGrid = new Grid();
        avatarGrid.Children.Add(avatarFallback);
        avatarGrid.Children.Add(avatar);

        var avatarBorder = new Border
        {
            Width = 48,
            Height = 48,
            CornerRadius = new CornerRadius(16),
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.Parse("#22FFFFFF")),
            Child = avatarGrid
        };

        var texts = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = item.UserName,
                    FontSize = 14,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Brushes.White,
                    MaxWidth = 220,
                    TextTrimming = TextTrimming.CharacterEllipsis
                },
                new TextBlock
                {
                    Text = item.UidText,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.Parse("#DFFFFFFF")),
                    MaxWidth = 220,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            }
        };

        var content = new Grid
        {
            ColumnSpacing = 10
        };
        content.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(48)
        });
        content.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        content.Children.Add(avatarBorder);
        Grid.SetColumn(texts, 1);
        content.Children.Add(texts);

        return new Border
        {
            Width = 310,
            Padding = new Thickness(11),
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.Parse("#E6242A40")),
            BorderBrush = new SolidColorBrush(Color.Parse("#66FFFFFF")),
            BorderThickness = new Thickness(1),
            Opacity = 0.94,
            Child = content
        };
    }

    private async void HistoryOpenHome_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: DownloadHistoryItem item }
            && DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.OpenHistoryHomeAsync(item);
        }
    }

    private void HistoryOpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: DownloadHistoryItem item }
            && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.OpenHistoryFolder(item);
        }
    }

    private async void HistoryRecollect_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: DownloadHistoryItem item }
            && DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.RecollectHistoryAsync(item);
        }
    }

    private void HistoryRemove_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: DownloadHistoryItem item }
            || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        EndHistoryDrag(saveOrder: false);
        _pendingHistoryDeleteItem = item;
        HistoryDeleteAuthorText.Text = LocalizationService.Current?.Format("Dialog.Delete.Author", item.UserName, item.UserId)
                                      ?? $"作者：{item.UserName}（UID：{item.UserId}）";

        try
        {
            HistoryDeletePathText.Text = LocalizationService.Current?.Format("Dialog.Delete.Path", viewModel.GetHistoryFolderPath(item))
                                         ?? $"磁盘目录：{viewModel.GetHistoryFolderPath(item)}";
        }
        catch (Exception ex)
        {
            HistoryDeletePathText.Text = LocalizationService.Current?.Format("Dialog.Delete.PathError", ex.Message)
                                         ?? $"磁盘目录解析失败：{ex.Message}";
        }

        HistoryDeleteOverlay.IsVisible = true;
    }

    private void HistoryDeleteCancelButton_Click(object? sender, RoutedEventArgs e)
        => CloseHistoryDeleteOverlay();

    private async void HistoryDeleteHistoryOnlyButton_Click(object? sender, RoutedEventArgs e)
        => await CompleteHistoryDeleteAsync(deleteDiskFiles: false);

    private async void HistoryDeleteFilesButton_Click(object? sender, RoutedEventArgs e)
        => await CompleteHistoryDeleteAsync(deleteDiskFiles: true);

    private async Task CompleteHistoryDeleteAsync(bool deleteDiskFiles)
    {
        var item = _pendingHistoryDeleteItem;
        CloseHistoryDeleteOverlay();

        if (item is not null && DataContext is MainWindowViewModel viewModel)
            await viewModel.RemoveHistoryAsync(item, deleteDiskFiles);
    }

    private void CloseHistoryDeleteOverlay()
    {
        HistoryDeleteOverlay.IsVisible = false;
        _pendingHistoryDeleteItem = null;
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        if (_subscribedLogs is not null)
            _subscribedLogs.CollectionChanged -= Logs_CollectionChanged;

        EndHistoryDrag(saveOrder: false);

        if (DataContext is MainWindowViewModel viewModel)
            await viewModel.DisposeAsync();
    }
}
