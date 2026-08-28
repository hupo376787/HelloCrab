using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HelloCrab.Core.Services.Localization;
using HelloCrab.Core.ViewModels;

namespace HelloCrab.Core.Views;

public partial class MainWindow
{
    // MainWindow 的主体 XAML 已经比较大；手动文件夹人像扫描属于桌面增强功能，
    // 与现有批量采集按钮一样在窗口加载后挂到对应设置区域，避免继续膨胀主 XAML。
    private static readonly IDisposable PersonFolderScanDataContextHandler =
        StyledElement.DataContextProperty.Changed.AddClassHandler<MainWindow>((window, _) =>
            Dispatcher.UIThread.Post(
                window.EnsurePersonFolderScanControls,
                DispatcherPriority.Loaded));

    private Button? _personFolderScanButton;
    private TextBlock? _personFolderScanStatus;
    private MainWindowViewModel? _personFolderScanViewModel;
    private int _personFolderScanInstallAttempts;

    private void EnsurePersonFolderScanControls()
    {
        if (_personFolderScanButton is not null
            || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var localization = LocalizationService.Current;
        var personDetectionText = localization?.Get(
                                      "Download.PersonDetection",
                                      "人像检测：删除不包含人物的图片")
                                  ?? "人像检测：删除不包含人物的图片";
        var personDetectionToggle = this.GetVisualDescendants()
            .OfType<CheckBox>()
            .FirstOrDefault(checkBox => string.Equals(
                checkBox.Content?.ToString(),
                personDetectionText,
                StringComparison.Ordinal));

        if (personDetectionToggle?.Parent is not StackPanel settingsPanel)
        {
            // DataContext 有时会早于最后一批设置控件完成可视树构建，短暂重试即可。
            if (_personFolderScanInstallAttempts++ < 4)
            {
                Dispatcher.UIThread.Post(
                    EnsurePersonFolderScanControls,
                    DispatcherPriority.Background);
            }
            return;
        }

        _personFolderScanInstallAttempts = 0;
        _personFolderScanViewModel = viewModel;
        viewModel.PropertyChanged += PersonFolderScanViewModel_PropertyChanged;
        if (localization is not null)
            localization.LanguageChanged += PersonFolderScanLanguageChanged;
        Closing += PersonFolderScanWindow_Closing;
        Closed += PersonFolderScanWindow_Closed;

        var scanPanel = new StackPanel
        {
            Spacing = 5,
            Margin = new Thickness(0, 2, 0, 0)
        };

        var scanButton = new Button
        {
            Classes = { "sectionAction" },
            MinWidth = 110,
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        scanButton.Click += PersonFolderScanButton_Click;

        var scanStatus = new TextBlock
        {
            Classes = { "caption" },
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };

        scanPanel.Children.Add(scanButton);
        scanPanel.Children.Add(scanStatus);

        // 人像检测是下载设置卡片的最后一组设置，追加到 StackPanel 尾部即位于该区域内部。
        settingsPanel.Children.Add(scanPanel);
        _personFolderScanButton = scanButton;
        _personFolderScanStatus = scanStatus;
        RefreshPersonFolderScanUi();
    }

    private void PersonFolderScanViewModel_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.IsBusy)
            or nameof(MainWindowViewModel.IsCapturing)
            or nameof(MainWindowViewModel.IsScheduledBatchRunning)
            or nameof(MainWindowViewModel.IsManualBatchRunning)
            or nameof(MainWindowViewModel.IsPersonFolderScanRunning)
            or nameof(MainWindowViewModel.PersonFolderScanStatusText))
        {
            Dispatcher.UIThread.Post(RefreshPersonFolderScanUi);
        }
    }

    private void PersonFolderScanLanguageChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(RefreshPersonFolderScanUi);

    private void RefreshPersonFolderScanUi()
    {
        if (_personFolderScanViewModel is not { } viewModel)
            return;

        if (_personFolderScanButton is not null)
        {
            _personFolderScanButton.Content = viewModel.IsPersonFolderScanRunning
                ? PersonFolderScanLocalizedText(
                    "PersonScan.ButtonRunning",
                    "扫描中…",
                    "Scanning…",
                    "スキャン中…")
                : PersonFolderScanLocalizedText(
                    "PersonScan.Button",
                    "扫描",
                    "Scan",
                    "スキャン");
            _personFolderScanButton.IsEnabled = viewModel.CanStartPersonFolderScan;
        }

        if (_personFolderScanStatus is not null)
        {
            _personFolderScanStatus.Text = string.IsNullOrWhiteSpace(viewModel.PersonFolderScanStatusText)
                ? PersonFolderScanLocalizedText(
                    "PersonScan.Description",
                    "选择文件夹后会递归扫描所有子文件夹，使用当前检测置信度；确认没有人物的图片会直接删除，检测失败的图片会保留。",
                    "Choose a folder to scan it and all subfolders using the current confidence threshold. Images confirmed to contain no person are deleted; images that fail detection are kept.",
                    "フォルダーとすべてのサブフォルダーを現在の信頼度で再帰的にスキャンします。人物がいないと確認された画像は削除し、検出に失敗した画像は保持します。")
                : viewModel.PersonFolderScanStatusText;
        }
    }

    private async void PersonFolderScanButton_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel
            || !viewModel.CanStartPersonFolderScan)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = PersonFolderScanLocalizedText(
                "PersonScan.FolderDialogTitle",
                "选择要进行人像扫描的文件夹",
                "Select a folder for person scanning",
                "人物スキャンするフォルダーを選択"),
            AllowMultiple = false
        });

        if (folders.Count == 0)
            return;

        // 文件夹选择期间任务状态可能发生变化；ViewModel 会再次检查，避免与下载并发启动。
        await viewModel.ScanPersonFolderAsync(folders[0].Path.LocalPath);
        RefreshPersonFolderScanUi();
    }

    private void PersonFolderScanWindow_Closing(
        object? sender,
        WindowClosingEventArgs e)
    {
        // 第一次点关闭按钮时主窗口会先把 e.Cancel 设为 true 并显示确认框，
        // 此时不取消扫描；用户真正确认退出后再停止当前检测。
        if (!e.Cancel)
            _personFolderScanViewModel?.CancelPersonFolderScan();
    }

    private void PersonFolderScanWindow_Closed(object? sender, EventArgs e)
    {
        if (_personFolderScanViewModel is not null)
            _personFolderScanViewModel.PropertyChanged -= PersonFolderScanViewModel_PropertyChanged;
        if (_personFolderScanButton is not null)
            _personFolderScanButton.Click -= PersonFolderScanButton_Click;
        if (LocalizationService.Current is { } localization)
            localization.LanguageChanged -= PersonFolderScanLanguageChanged;

        Closing -= PersonFolderScanWindow_Closing;
        Closed -= PersonFolderScanWindow_Closed;
    }

    private static string PersonFolderScanLocalizedText(
        string key,
        string zhCn,
        string enUs,
        string jaJp)
    {
        var localization = LocalizationService.Current;
        var code = localization?.CurrentLanguageCode ?? "zh-CN";
        var fallback = code.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? zhCn
            : code.StartsWith("ja", StringComparison.OrdinalIgnoreCase)
                ? jaJp
                : enUs;
        return localization?.Get(key, fallback) ?? fallback;
    }
}
