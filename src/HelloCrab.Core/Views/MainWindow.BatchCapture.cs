using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
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
        var editor = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            PlaceholderText = localization?.Get(
                "Batch.Dialog.Placeholder",
                "在这里粘贴或编辑作者地址，每行一条。允许包含分享文案，程序会自动提取每行中的第一个网址。")
                ?? "在这里粘贴或编辑作者地址，每行一条。允许包含分享文案，程序会自动提取每行中的第一个网址。",
            MinHeight = 300,
            Padding = new Thickness(12)
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(editor, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(editor, ScrollBarVisibility.Auto);

        var title = new TextBlock
        {
            Text = localization?.Get("Batch.Dialog.Title", "批量采集") ?? "批量采集",
            FontSize = 22,
            FontWeight = FontWeight.SemiBold
        };
        var description = new TextBlock
        {
            Text = localization?.Get(
                "Batch.Dialog.Description",
                "请检查并编辑待采集文本。点击“确定”解析当前文本，或点击“导入外部txt”读取文件并立即开始解析。")
                ?? "请检查并编辑待采集文本。点击“确定”解析当前文本，或点击“导入外部txt”读取文件并立即开始解析。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#8B95A7"))
        };
        var header = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                title,
                description
            }
        };

        var importButton = new Button
        {
            Content = localization?.Get("Batch.Dialog.Import", "导入外部txt") ?? "导入外部txt",
            MinWidth = 132,
            Height = 40,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        var confirmButton = new Button
        {
            Content = localization?.Get("Batch.Dialog.Confirm", "确定") ?? "确定",
            MinWidth = 100,
            Height = 40,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse("#7C3AED")),
            Foreground = Brushes.White,
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
        contentGrid.Children.Add(header);
        Grid.SetRow(editor, 1);
        contentGrid.Children.Add(editor);
        Grid.SetRow(buttons, 2);
        contentGrid.Children.Add(buttons);

        var dialog = new Window
        {
            Title = localization?.Get("Batch.Dialog.Title", "批量采集") ?? "批量采集",
            Width = 760,
            Height = 560,
            MinWidth = 560,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true,
            ShowInTaskbar = false,
            Content = new Border
            {
                Padding = new Thickness(24),
                Child = contentGrid
            }
        };

        dialog.Opened += (_, _) => editor.Focus();
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
                    new FilePickerFileType("Text files")
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
