using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
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

        var startButton = this.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(button => ReferenceEquals(button.Command, viewModel.StartCaptureCommand));
        if (startButton?.Parent is not Grid actionGrid
            || actionGrid.Parent is not StackPanel capturePanel)
        {
            Dispatcher.UIThread.Post(EnsureBatchCaptureControls, DispatcherPriority.Background);
            return;
        }

        var button = new Button
        {
            Classes = { "primary", "sectionAction" },
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
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
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
                "导入 TXT 文本文件，每行一个作者地址；允许空行。程序会按文件顺序逐个采集并自动下载。")
                ?? "导入 TXT 文本文件，每行一个作者地址；允许空行。程序会按文件顺序逐个采集并自动下载。";
        }
    }

    private async void BatchCaptureButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel
            || !viewModel.CanStartManualBatchCapture)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LocalizationService.Current?.Get(
                "Batch.SelectFileDialog",
                "选择批量采集地址文本文件") ?? "选择批量采集地址文本文件",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Text files")
                {
                    Patterns = new[] { "*.txt" },
                    MimeTypes = new[] { "text/plain" }
                }
            }
        });

        if (files.Count == 0)
            return;

        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            var content = await reader.ReadToEndAsync();
            await viewModel.StartManualBatchCaptureAsync(content);
        }
        catch (Exception ex)
        {
            var template = LocalizationService.Current?.Get(
                "Batch.FileReadFailed",
                "读取批量地址文件失败：{0}") ?? "读取批量地址文件失败：{0}";
            viewModel.AddRemoteLog(string.Format(template, ex.Message));
        }
    }
}
