using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HelloCrab.Core.Services.Localization;
using HelloCrab.Core.ViewModels;

namespace HelloCrab.Core.Views;

public partial class MainWindow
{
    private Button? _batchSkipButton;
    private int _batchSkipEnsureAttempts;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Opened -= BatchSkipWindow_Opened;
        Opened += BatchSkipWindow_Opened;
    }

    private void BatchSkipWindow_Opened(object? sender, EventArgs e)
    {
        // MainWindow.BatchCapture 会在 OnOpened 返回后以 Loaded 优先级创建批量按钮。
        // 此处使用更低的 Background 优先级，确保原按钮先完成插入。
        Dispatcher.UIThread.Post(EnsureBatchSkipButton, DispatcherPriority.Background);
    }

    private void EnsureBatchSkipButton()
    {
        if (_batchSkipButton is not null)
            return;

        if (_batchCaptureButton is null
            || _batchCaptureViewModel is null
            || _batchCaptureButton.Parent is not Panel parent)
        {
            if (++_batchSkipEnsureAttempts <= 20)
                Dispatcher.UIThread.Post(EnsureBatchSkipButton, DispatcherPriority.Background);
            return;
        }

        var buttonIndex = parent.Children.IndexOf(_batchCaptureButton);
        if (buttonIndex < 0)
            return;

        var row = new Grid
        {
            ColumnSpacing = 8,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });

        parent.Children.RemoveAt(buttonIndex);
        Grid.SetColumn(_batchCaptureButton, 0);
        // 默认没有批量任务时横跨左右两列，占满整行。
        Grid.SetColumnSpan(_batchCaptureButton, 2);
        row.Children.Add(_batchCaptureButton);

        // 只使用 sectionAction，与“停止采集”保持相同背景和按钮样式。
        var skipButton = new Button
        {
            Classes = { "sectionAction" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsVisible = false,
            IsEnabled = false
        };
        skipButton.Click += BatchSkipButton_Click;
        Grid.SetColumn(skipButton, 1);
        row.Children.Add(skipButton);

        parent.Children.Insert(buttonIndex, row);
        _batchSkipButton = skipButton;

        _batchCaptureViewModel.PropertyChanged += BatchSkipViewModel_PropertyChanged;
        Closed += BatchSkipWindow_Closed;
        if (LocalizationService.Current is { } localization)
            localization.LanguageChanged += OnBatchSkipLanguageChanged;

        RefreshBatchSkipLocalizedText();
        UpdateBatchSkipButtonState(_batchCaptureViewModel);
    }

    private void BatchSkipButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_batchCaptureViewModel?.SkipCurrentManualBatchAuthor() == true)
            UpdateBatchSkipButtonState(_batchCaptureViewModel);
    }

    private void BatchSkipViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MainWindowViewModel viewModel
            || e.PropertyName is not (
                nameof(MainWindowViewModel.IsManualBatchRunning)
                or nameof(MainWindowViewModel.IsCapturing)
                or nameof(MainWindowViewModel.IsManualBatchSkipRequested)))
        {
            return;
        }

        UpdateBatchSkipButtonState(viewModel);
    }

    private void UpdateBatchSkipButtonState(MainWindowViewModel viewModel)
    {
        if (_batchSkipButton is null || _batchCaptureButton is null)
            return;

        var isBatchRunning = viewModel.IsManualBatchRunning;

        // 未运行时主按钮占满两列；批量运行后缩为左半宽，右侧显示跳过按钮。
        Grid.SetColumnSpan(_batchCaptureButton, isBatchRunning ? 1 : 2);
        _batchSkipButton.IsVisible = isBatchRunning;
        _batchSkipButton.IsEnabled = isBatchRunning
                                     && viewModel.IsCapturing
                                     && !viewModel.IsManualBatchSkipRequested;
    }

    private void OnBatchSkipLanguageChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(RefreshBatchSkipLocalizedText);

    private void RefreshBatchSkipLocalizedText()
    {
        if (_batchSkipButton is null)
            return;

        var localization = LocalizationService.Current;
        var languageCode = localization?.CurrentLanguageCode ?? "zh-CN";
        var fallback = languageCode.StartsWith("ja", StringComparison.OrdinalIgnoreCase)
            ? "次の作者へ"
            : languageCode.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                ? "Next author"
                : "跳到下一个作者";

        _batchSkipButton.Content = localization?.Get("Batch.SkipButton", fallback) ?? fallback;
    }

    private void BatchSkipWindow_Closed(object? sender, EventArgs e)
    {
        Opened -= BatchSkipWindow_Opened;
        Closed -= BatchSkipWindow_Closed;

        if (_batchSkipButton is not null)
            _batchSkipButton.Click -= BatchSkipButton_Click;
        if (_batchCaptureViewModel is not null)
            _batchCaptureViewModel.PropertyChanged -= BatchSkipViewModel_PropertyChanged;
        if (LocalizationService.Current is { } localization)
            localization.LanguageChanged -= OnBatchSkipLanguageChanged;
    }
}
