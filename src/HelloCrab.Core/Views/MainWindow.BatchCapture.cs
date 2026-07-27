using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HelloCrab.Core.Services.Localization;
using HelloCrab.Core.ViewModels;

namespace HelloCrab.Core.Views;

public partial class MainWindow
{
    private Button? _batchCaptureButton;
    private TextBlock? _batchCaptureDescription;
    private Button? _autopilotButton;
    private Button? _batchStopButton;
    private Button? _batchStopConfirmButton;
    private MainWindowViewModel? _batchCaptureViewModel;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Dispatcher.UIThread.Post(EnsureBatchCaptureControls, DispatcherPriority.Loaded);
    }

    private void EnsureBatchCaptureControls()
    {
        if (_batchCaptureButton is not null
            || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var buttons = this.GetVisualDescendants()
            .OfType<Button>()
            .ToArray();
        var startButton = buttons.FirstOrDefault(button =>
            ReferenceEquals(button.Command, viewModel.StartCaptureCommand));
        if (startButton?.Parent is not Grid actionGrid
            || actionGrid.Parent is not StackPanel capturePanel)
        {
            Dispatcher.UIThread.Post(EnsureBatchCaptureControls, DispatcherPriority.Background);
            return;
        }

        _autopilotButton = buttons.FirstOrDefault(button =>
            ReferenceEquals(button.Command, viewModel.OpenScheduledDownloadEditorCommand));
        viewModel.ApplyAutopilotBranding();

        _batchStopButton = actionGrid.Children
            .OfType<Button>()
            .FirstOrDefault(button => !ReferenceEquals(button, startButton));
        if (_batchStopButton is not null)
            _batchStopButton.Click += BatchStopButton_Click;

        _batchStopConfirmButton = StopCaptureConfirmOverlay
            .GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(button => button.Classes.Contains("coral"));
        if (_batchStopConfirmButton is not null)
            _batchStopConfirmButton.Click += BatchStopConfirmButton_Click;

        _batchCaptureViewModel = viewModel;
        viewModel.PropertyChanged += BatchCaptureViewModel_PropertyChanged;
        Closed += BatchCaptureWindow_Closed;

        var button = new Button
        {
            Classes = { "coral", "sectionAction" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        button.Bind(
            IsEnabledProperty,
            new Binding(nameof(MainWindowViewModel.CanStartManualBatchCapture)));
        button.Click += BatchCaptureButton_Click;

        var description = new TextBlock
        {
            Classes = { "caption" },
            TextWrapping = TextWrapping.Wrap
        };

        var insertIndex = capturePanel.Children.IndexOf(actionGrid) + 1;
        capturePanel.Children.Insert(insertIndex, button);
        capturePanel.Children.Insert(insertIndex + 1, description);
        _batchCaptureButton = button;
        _batchCaptureDescription = description;
        RefreshBatchCaptureLocalizedText();

        if (LocalizationService.Current is { } localization)
            localization.LanguageChanged += OnBatchCaptureLanguageChanged;
    }

    private void BatchCaptureWindow_Closed(object? sender, EventArgs e)
    {
        if (_batchCaptureViewModel is not null)
            _batchCaptureViewModel.PropertyChanged -= BatchCaptureViewModel_PropertyChanged;
        if (_batchStopButton is not null)
            _batchStopButton.Click -= BatchStopButton_Click;
        if (_batchStopConfirmButton is not null)
            _batchStopConfirmButton.Click -= BatchStopConfirmButton_Click;
        if (LocalizationService.Current is { } localization)
            localization.LanguageChanged -= OnBatchCaptureLanguageChanged;
    }

    private void BatchCaptureViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.IsCapturing)
            || sender is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (viewModel.IsCapturing)
        {
            // 新一轮采集真正开始时再清空上一位作者。
            viewModel.ClearCurrentAuthorDisplayForNextCapture();
            return;
        }

        // MainWindowViewModel 现有 finally 会紧接着清空头像。先保存显示内容，
        // 等 finally 执行完后恢复，让用户在本次任务结束后仍能看到作者信息。
        viewModel.RememberCurrentAuthorDisplayBeforeCleanup();
        Dispatcher.UIThread.Post(
            viewModel.RestoreCurrentAuthorDisplayAfterCleanup,
            DispatcherPriority.Background);
    }

    private void BatchStopButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_batchCaptureViewModel is not { IsManualBatchRunning: true } viewModel)
            return;

        // 当前作者之间的短暂间隔里 IsCapturing=false，原 StopCaptureCommand
        // 不可执行；此时仍允许用户打开确认框并停止剩余批量任务。
        if (!viewModel.IsCapturing && !viewModel.IsScheduledBatchRunning)
            StopCaptureConfirmOverlay.IsVisible = true;
    }

    private void BatchStopConfirmButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_batchCaptureViewModel is { IsManualBatchRunning: true } viewModel)
            viewModel.CancelManualBatchCapture();
    }

    private void OnBatchCaptureLanguageChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(RefreshBatchCaptureLocalizedText);

    private void RefreshBatchCaptureLocalizedText()
    {
        var localization = LocalizationService.Current;
        if (_batchCaptureButton is not null)
        {
            _batchCaptureButton.Content = localization?.Get(
                "Batch.Button",
                "批量采集并自动下载") ?? "批量采集并自动下载";
        }

        if (_batchCaptureDescription is not null)
        {
            _batchCaptureDescription.Text = localization?.Get(
                "Batch.Description",
                "可直接粘贴或编辑文本，每行一个作者地址；也可以导入外部 TXT 文件。程序会按顺序逐个采集并自动下载。")
                ?? "可直接粘贴或编辑文本，每行一个作者地址；也可以导入外部 TXT 文件。程序会按顺序逐个采集并自动下载。";
        }

        if (_autopilotButton is not null)
            _autopilotButton.Content = "Autopilot";
    }

    private async void BatchCaptureButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel
            || !viewModel.CanStartManualBatchCapture)
        {
            return;
        }

        var content = await ShowBatchCaptureDialogAsync(viewModel);
        if (content is null)
            return;

        await viewModel.StartManualBatchCaptureAsync(content);
    }

    private async Task<string?> ShowBatchCaptureDialogAsync(MainWindowViewModel viewModel)
    {
        var localization = LocalizationService.Current;
        var isDark = viewModel.IsDarkTheme;
        var titleText = localization?.Get("Batch.Dialog.Title", "批量采集") ?? "批量采集";

        var textPrimary = new SolidColorBrush(Color.Parse(isDark ? "#F7F7FB" : "#202231"));
        var textSecondary = new SolidColorBrush(Color.Parse(isDark ? "#C3C7D4" : "#62687A"));
        var frameBackground = new SolidColorBrush(Color.Parse(isDark ? "#D91B1927" : "#E8FFFFFF"));
        var contentBackground = new SolidColorBrush(Color.Parse(isDark ? "#C9232131" : "#D9F8F7FC"));
        var borderBrush = new SolidColorBrush(Color.Parse(isDark ? "#55FFFFFF" : "#331B1D2A"));
        var titleTextBrush = new SolidColorBrush(Color.Parse(isDark ? "#FFF7F7FB" : "#FF4A355E"));
        var titleHintBrush = new SolidColorBrush(Color.Parse(isDark ? "#D9FFFFFF" : "#B85B4770"));

        // 浅色主题使用与主窗体接近的浅紫、浅粉、浅蓝半透明标题栏。
        // 深色主题保留较深的色调，避免和正文区域失去层次。
        var titleBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative)
        };
        if (isDark)
        {
            titleBrush.GradientStops.Add(new GradientStop(Color.Parse("#B55B21B6"), 0));
            titleBrush.GradientStops.Add(new GradientStop(Color.Parse("#B9BE185D"), 0.52));
            titleBrush.GradientStops.Add(new GradientStop(Color.Parse("#AD0369A1"), 1));
        }
        else
        {
            titleBrush.GradientStops.Add(new GradientStop(Color.Parse("#F3DECDF4"), 0));
            titleBrush.GradientStops.Add(new GradientStop(Color.Parse("#F3F0D3E5"), 0.52));
            titleBrush.GradientStops.Add(new GradientStop(Color.Parse("#F3D5E6F4"), 1));
        }

        var editor = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            PlaceholderText = localization?.Get(
                "Batch.Dialog.Placeholder",
                "在这里粘贴或编辑作者地址，每行一条。允许包含分享文案，程序会自动提取每行中的第一个网址。")
                ?? "在这里粘贴或编辑作者地址，每行一条。允许包含分享文案，程序会自动提取每行中的第一个网址。",
            MinHeight = 300,
            Padding = new Thickness(14),
            Background = Brushes.Transparent,
            Foreground = textPrimary,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10)
        };

        // Fluent TextBox 在悬停和获得焦点时会给模板内部边框设置独立背景，
        // 因此仅设置 Background=Transparent 不够，需要同时覆盖四种状态资源。
        editor.Resources["TextControlBackground"] = Brushes.Transparent;
        editor.Resources["TextControlBackgroundPointerOver"] = Brushes.Transparent;
        editor.Resources["TextControlBackgroundFocused"] = Brushes.Transparent;
        editor.Resources["TextControlBackgroundDisabled"] = Brushes.Transparent;

        ScrollViewer.SetHorizontalScrollBarVisibility(editor, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(editor, ScrollBarVisibility.Auto);

        var title = new TextBlock
        {
            Text = titleText,
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            Foreground = titleTextBrush
        };
        var titleHint = new TextBlock
        {
            Text = localization?.Get("Batch.Dialog.Subtitle", "HelloCrab · 批量采集")
                   ?? "HelloCrab · 批量采集",
            FontSize = 11,
            Foreground = titleHintBrush,
            Margin = new Thickness(0, 2, 0, 0)
        };
        var titlePanel = new StackPanel
        {
            Margin = new Thickness(18, 0, 0, 0),
            Spacing = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                title,
                titleHint
            }
        };

        var closeButton = new Button
        {
            Content = "×",
            Width = 44,
            Height = 40,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(0),
            FontSize = 22,
            Background = Brushes.Transparent,
            Foreground = titleTextBrush,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(7),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };

        Window? dialog = null;
        var dragRegion = new Border
        {
            Background = Brushes.Transparent,
            Child = titlePanel
        };
        dragRegion.PointerPressed += (_, pointerEvent) =>
        {
            if (dialog is null)
                return;

            var point = pointerEvent.GetCurrentPoint(dragRegion);
            if (!point.Properties.IsLeftButtonPressed)
                return;

            dialog.BeginMoveDrag(pointerEvent);
            pointerEvent.Handled = true;
        };

        var titleBarGrid = new Grid();
        titleBarGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        titleBarGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        titleBarGrid.Children.Add(dragRegion);
        Grid.SetColumn(closeButton, 1);
        titleBarGrid.Children.Add(closeButton);

        var titleBar = new Border
        {
            Height = 66,
            Background = titleBrush,
            Child = titleBarGrid
        };

        var description = new TextBlock
        {
            Text = localization?.Get(
                "Batch.Dialog.Description",
                "请检查并编辑待采集文本。点击“确定”解析当前文本，或点击“导入外部 TXT”读取文件并立即开始解析。")
                ?? "请检查并编辑待采集文本。点击“确定”解析当前文本，或点击“导入外部 TXT”读取文件并立即开始解析。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = textSecondary
        };

        var importButton = new Button
        {
            Content = localization?.Get("Batch.Dialog.Import", "导入外部 TXT") ?? "导入外部 TXT",
            MinWidth = 132,
            Height = 42,
            Padding = new Thickness(14, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse(isDark ? "#554A465D" : "#66FFFFFF")),
            Foreground = textPrimary,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8)
        };
        var confirmButton = new Button
        {
            Content = localization?.Get("Batch.Dialog.Confirm", "确定") ?? "确定",
            MinWidth = 100,
            Height = 42,
            Padding = new Thickness(14, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse("#FD6F71")),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            FontWeight = FontWeight.SemiBold
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 12,
            Children =
            {
                importButton,
                confirmButton
            }
        };

        var contentGrid = new Grid
        {
            RowSpacing = 16
        };
        contentGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        contentGrid.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
        contentGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        contentGrid.Children.Add(description);
        Grid.SetRow(editor, 1);
        contentGrid.Children.Add(editor);
        Grid.SetRow(buttons, 2);
        contentGrid.Children.Add(buttons);

        var contentBorder = new Border
        {
            Padding = new Thickness(24),
            Background = contentBackground,
            Child = contentGrid
        };

        var rootGrid = new Grid();
        rootGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        rootGrid.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
        rootGrid.Children.Add(titleBar);
        Grid.SetRow(contentBorder, 1);
        rootGrid.Children.Add(contentBorder);

        var frame = new Border
        {
            Margin = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            ClipToBounds = true,
            Background = frameBackground,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            Child = rootGrid
        };

        dialog = new Window
        {
            Title = titleText,
            Width = 780,
            Height = 580,
            MinWidth = 620,
            MinHeight = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
            WindowDecorations = WindowDecorations.None,
            ExtendClientAreaToDecorationsHint = true,
            Background = Brushes.Transparent,
            TransparencyLevelHint = new[]
            {
                WindowTransparencyLevel.AcrylicBlur,
                WindowTransparencyLevel.Blur,
                WindowTransparencyLevel.Transparent
            },
            Content = frame
        };

        dialog.Opened += (_, _) => editor.Focus();
        dialog.KeyDown += (_, keyEvent) =>
        {
            if (keyEvent.Key == Key.Escape)
                dialog.Close((string?)null);
        };
        closeButton.Click += (_, _) => dialog.Close((string?)null);
        confirmButton.Click += (_, _) => dialog.Close(editor.Text ?? string.Empty);
        importButton.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = localization?.Get(
                    "Batch.SelectFileDialog",
                    "选择批量采集地址文本文件") ?? "选择批量采集地址文本文件",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType(
                        localization?.Get("Batch.FileType.Text", "文本文件") ?? "文本文件")
                    {
                        Patterns = new[] { "*.txt" },
                        MimeTypes = new[] { "text/plain" }
                    }
                }
            });

            if (files.Count == 0)
                return;

            importButton.IsEnabled = false;
            confirmButton.IsEnabled = false;
            try
            {
                await using var stream = await files[0].OpenReadAsync();
                using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
                var importedContent = await reader.ReadToEndAsync();
                dialog.Close(importedContent);
            }
            catch (Exception ex)
            {
                var template = localization?.Get(
                    "Batch.FileReadFailed",
                    "读取批量地址文件失败：{0}") ?? "读取批量地址文件失败：{0}";
                viewModel.AddRemoteLog(string.Format(template, ex.Message));
                importButton.IsEnabled = true;
                confirmButton.IsEnabled = true;
            }
        };

        return await dialog.ShowDialog<string?>(this);
    }
}
