using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using HelloCrab.Core.Services.Crawling;
using HelloCrab.Core.Services.Downloading;
using HelloCrab.Core.Services.History;
using HelloCrab.Core.Services.Images;
using HelloCrab.Core.Services.Localization;
using HelloCrab.Core.Services.Settings;
using HelloCrab.Core.Sites;
using HelloCrab.Core.Sites.Bilibili;
using HelloCrab.Core.Sites.Douyin;
using HelloCrab.Core.Sites.Instagram;
using HelloCrab.Core.Sites.Kuaishou;
using HelloCrab.Core.Sites.Meipian;
using HelloCrab.Core.Sites.Pinterest;
using HelloCrab.Core.Sites.TikTok;
using HelloCrab.Core.Sites.Xiaohongshu;
using HelloCrab.Core.Sites.Weibo;
using HelloCrab.Core.Utilities;
using HelloCrab.Core.ViewModels;
using HelloCrab.Core.Views;
using HelloCrab.Desktop.Playwright;
using HelloCrab.Desktop.Chromium;
using HelloCrab.Desktop.FFmpeg;
using HelloCrab.Desktop.Platform;
using HelloCrab.Desktop.Remote;
using HelloCrab.Desktop.AI;

namespace HelloCrab.Desktop;

public partial class App : Application
{
    private static readonly TimeSpan MinimumSplashDisplayTime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SplashCompletionHoldTime = TimeSpan.FromMilliseconds(120);

    private static readonly TimeSpan TrayDoubleClickThreshold = TimeSpan.FromMilliseconds(600);

    private RemoteApiHostService? _remoteApiHost;
    private MainWindowViewModel? _viewModel;
    private GyanFfmpegInstallerService? _ffmpegInstaller;
    private MainWindow? _mainWindow;
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _trayShowMainWindowItem;
    private NativeMenuItem? _trayExitProgramItem;
    private LocalizationService? _trayLocalization;
    private DateTimeOffset? _lastTrayClickAt;
    private bool _desktopStartupStarted;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var splash = new SplashWindow();
            var useNativeStartupSplash = Program.HasEarlyStartupSplash;
            if (useNativeStartupSplash)
            {
                // Windows 冷启动时，原生闪屏从进程入口一直保留到主窗口出现。
                // Avalonia 闪屏只作为生命周期占位窗口，不再显示第二个界面。
                splash.Opacity = 0;
                splash.ShowInTaskbar = false;
                splash.Topmost = false;
            }

            desktop.MainWindow = splash;
            desktop.Exit += Desktop_Exit;

            splash.Opened += async (_, _) =>
            {
                if (_desktopStartupStarted)
                    return;

                _desktopStartupStarted = true;

                DateTimeOffset splashShownAt;
                if (useNativeStartupSplash)
                {
                    splashShownAt = Program.EarlyStartupSplashShownAt ?? DateTimeOffset.UtcNow;
                }
                else
                {
                    await splash.WaitUntilPresentedAsync();
                    splashShownAt = DateTimeOffset.UtcNow;
                }

                await InitializeDesktopAsync(desktop, splash, splashShownAt);
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task InitializeDesktopAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        SplashWindow splash,
        DateTimeOffset splashShownAt)
    {
        try
        {
            SetStartupProgress(splash, 5, "正在启动 HelloCrab…", "准备应用运行环境");
            await Task.Yield();

            SetStartupProgress(splash, 15, "正在加载核心模块…", "初始化浏览器、媒体处理与系统服务");
            var browser = new PlaywrightBrowserService(new PlaywrightChromiumInstaller());
            var mediaProcessor = new FfmpegMediaService();
            var ffmpegInstaller = _ffmpegInstaller = new GyanFfmpegInstallerService();
            var platformShell = new PlatformShellService();

            SetStartupProgress(splash, 28, "正在加载平台模块…", "加载哔哩哔哩、抖音、快手、小红书、微博等平台适配器");
            var adapters = new SiteAdapterRegistry(new ISiteAdapter[]
            {
                new BilibiliSiteAdapter(),
                new DouyinLivePhotoSiteAdapter(),
                new InstagramSiteAdapter(),
                new TikTokSiteAdapter(),
                new PinterestSiteAdapter(),
                new KuaishouProfileFeedSiteAdapter(),
                new KuaishouLiveGuardedSiteAdapter(),
                new XiaohongshuSiteAdapter(),
                new WeiboReliableSiteAdapter(),
                new MeipianSiteAdapter()
            });

            SetStartupProgress(splash, 40, "正在初始化服务…", "准备下载、历史记录、图片缓存与 AI 模块");
            var personImageDetector = new YoloPersonImageDetector();
            var downloader = new MediaDownloadService(browser, mediaProcessor, personImageDetector);
            var historyService = new DownloadHistoryService();
            var imageCache = new ImageCacheService();
            var settingsService = new SettingsService();
            var localization = new LocalizationService();
            var coordinator = new CrawlCoordinator(browser, adapters, downloader, historyService);

            SetStartupProgress(splash, 50, "正在读取设置…", "恢复语言、主题、下载目录与平台配置");
            var viewModel = new MainWindowViewModel(
                browser,
                coordinator,
                adapters,
                historyService,
                imageCache,
                settingsService,
                localization,
                platformShell,
                ffmpegInstaller,
                personImageDetector);

            SetStartupProgress(splash, 56, "正在检查本地数据…", "检查旧版文件命名并完成必要迁移");
            // 临时启动迁移：统一历史下载文件末尾的“ 空格+序号”为当前“_序号”。
            LegacySequenceFileNameMigration.Run(viewModel.DownloadRoot);

            SetStartupProgress(splash, 62, "正在加载历史记录…", "读取已下载作者和作品统计");
            await viewModel.HistoryInitializationTask;
            SetStartupProgress(splash, 
                74,
                "历史记录加载完成",
                $"已加载 {viewModel.DownloadHistory.Count} 个作者记录");

            SetStartupProgress(splash, 78, "正在检查运行组件…", "检测 Chromium、FFmpeg 与人像识别模型");
            await viewModel.RuntimeComponentInitializationTask;

            SetStartupProgress(splash, 87, "正在加载定时任务…", "恢复定时自动下载配置");
            await viewModel.ScheduledDownloadInitializationTask;

            _viewModel = viewModel;
            _remoteApiHost = new RemoteApiHostService(viewModel);
            viewModel.RemoteApiEnabledChanged += ViewModel_RemoteApiEnabledChanged;

            SetStartupProgress(splash, 93, "正在启动后台服务…", "应用远程控制服务配置");
            await ApplyRemoteServerStateAsync(viewModel.RemoteApiEnabled);

            SetStartupProgress(splash, 97, "正在准备主界面…", "创建窗口并应用界面设置");
            var mainWindow = new MainWindow
            {
                DataContext = viewModel
            };
            InitializeTrayIcon(mainWindow, localization);

            SetStartupProgress(splash, 100, "启动完成", "HelloCrab 已准备就绪");

            // 先让闪屏真正显示满最短时长，再显示主窗口。
            // 之前主窗口会提前 Show() 并覆盖闪屏，因此虽然闪屏仍存在，
            // 用户实际上看不到它。
            var elapsed = DateTimeOffset.UtcNow - splashShownAt;
            var remaining = MinimumSplashDisplayTime - elapsed;
            var closeDelay = remaining > SplashCompletionHoldTime
                ? remaining
                : SplashCompletionHoldTime;
            await Task.Delay(closeDelay);

            desktop.MainWindow = mainWindow;
            mainWindow.Show();
            Program.HideEarlyStartupSplash();
            splash.Close();
        }
        catch (Exception ex)
        {
            // Windows 原生闪屏只负责正常启动。若初始化失败，则切回 Avalonia
            // 错误界面，保留详细异常与关闭按钮。
            Program.HideEarlyStartupSplash();
            splash.Opacity = 1;
            splash.ShowInTaskbar = true;
            splash.Topmost = true;
            splash.ShowFailure(ex.Message);
        }
    }

    private static void SetStartupProgress(
        SplashWindow splash,
        double percent,
        string status,
        string? detail = null)
    {
        splash.SetProgress(percent, status, detail);
        Program.UpdateEarlyStartupSplash(percent, status, detail);
    }

    private void InitializeTrayIcon(MainWindow mainWindow, LocalizationService localization)
    {
        _mainWindow = mainWindow;
        _trayLocalization = localization;

        try
        {
            var showItem = _trayShowMainWindowItem = new NativeMenuItem();
            var exitItem = _trayExitProgramItem = new NativeMenuItem();

            showItem.Click += TrayShowMainWindowItem_Click;
            exitItem.Click += TrayExitProgramItem_Click;

            var menu = new NativeMenu();
            menu.Add(showItem);
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(exitItem);

            // Desktop 项目的程序集名显式设置为 HelloCrab，而不是命名空间
            // HelloCrab.Desktop。avares URI 必须使用真实程序集名，否则 AssetLoader
            // 会抛异常，随后托盘初始化被 catch 回退成普通最小化。
            using var iconStream = AssetLoader.Open(
                new Uri("avares://HelloCrab/Assets/app-icon.ico"));

            _trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(iconStream),
                ToolTipText = "HelloCrab",
                Menu = menu,
                IsVisible = false
            };
            _trayIcon.Clicked += TrayIcon_Clicked;

            var trayIcons = new TrayIcons();
            trayIcons.Add(_trayIcon);
            TrayIcon.SetIcons(this, trayIcons);

            mainWindow.MinimizeToTrayRequested += MainWindow_MinimizeToTrayRequested;
            localization.LanguageChanged += TrayLocalization_LanguageChanged;
            UpdateTrayMenuText();
        }
        catch
        {
            // 托盘初始化失败不能影响主程序启动。按钮会退化为普通最小化。
            DisposeTrayIcon();
        }
    }

    private void MainWindow_MinimizeToTrayRequested(object? sender, EventArgs e)
    {
        if (_mainWindow is not { } mainWindow || _trayIcon is not { } trayIcon)
        {
            if (_mainWindow is { } fallbackWindow)
                fallbackWindow.WindowState = WindowState.Minimized;
            return;
        }

        _lastTrayClickAt = null;
        trayIcon.IsVisible = true;
        mainWindow.ShowInTaskbar = false;
        mainWindow.Hide();
    }

    private void TrayIcon_Clicked(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastTrayClickAt is { } last
            && now - last <= TrayDoubleClickThreshold)
        {
            _lastTrayClickAt = null;
            RestoreMainWindowFromTray();
            return;
        }

        _lastTrayClickAt = now;
    }

    private void TrayShowMainWindowItem_Click(object? sender, EventArgs e)
        => RestoreMainWindowFromTray();

    private void TrayExitProgramItem_Click(object? sender, EventArgs e)
    {
        RestoreMainWindowFromTray();

        // 与主窗口右上角关闭按钮完全复用同一个确认退出流程。
        _mainWindow?.RequestCloseConfirmation();
    }

    private void RestoreMainWindowFromTray()
    {
        if (_mainWindow is not { } mainWindow)
            return;

        mainWindow.ShowInTaskbar = true;
        if (!mainWindow.IsVisible)
            mainWindow.Show();

        if (mainWindow.WindowState == WindowState.Minimized)
            mainWindow.WindowState = WindowState.Normal;

        mainWindow.Activate();

        if (_trayIcon is not null)
            _trayIcon.IsVisible = false;

        _lastTrayClickAt = null;
    }

    private void TrayLocalization_LanguageChanged(object? sender, EventArgs e)
        => UpdateTrayMenuText();

    private void UpdateTrayMenuText()
    {
        var localization = _trayLocalization;
        if (_trayShowMainWindowItem is not null)
        {
            _trayShowMainWindowItem.Header = localization?.Get(
                "Tray.ShowMainWindow",
                "显示主界面") ?? "显示主界面";
        }

        if (_trayExitProgramItem is not null)
        {
            _trayExitProgramItem.Header = localization?.Get(
                "Tray.ExitProgram",
                "退出程序") ?? "退出程序";
        }
    }

    private void DisposeTrayIcon()
    {
        if (_mainWindow is not null)
            _mainWindow.MinimizeToTrayRequested -= MainWindow_MinimizeToTrayRequested;

        if (_trayLocalization is not null)
            _trayLocalization.LanguageChanged -= TrayLocalization_LanguageChanged;

        if (_trayShowMainWindowItem is not null)
            _trayShowMainWindowItem.Click -= TrayShowMainWindowItem_Click;

        if (_trayExitProgramItem is not null)
            _trayExitProgramItem.Click -= TrayExitProgramItem_Click;

        if (_trayIcon is not null)
        {
            _trayIcon.Clicked -= TrayIcon_Clicked;
            _trayIcon.IsVisible = false;
            _trayIcon.Dispose();
        }

        TrayIcon.SetIcons(this, null);

        _trayIcon = null;
        _trayShowMainWindowItem = null;
        _trayExitProgramItem = null;
        _trayLocalization = null;
        _mainWindow = null;
        _lastTrayClickAt = null;
    }

    private void ViewModel_RemoteApiEnabledChanged(object? sender, bool enabled)
        => _ = ApplyRemoteServerStateAsync(enabled);

    private async Task ApplyRemoteServerStateAsync(bool enabled)
    {
        if (_remoteApiHost is null)
            return;

        try
        {
            await _remoteApiHost.SetEnabledAsync(enabled);
        }
        catch (Exception ex)
        {
            _viewModel?.AddRemoteLocalizedLog("Remote.Log.ToggleFailed", ex.Message);
            _viewModel?.SetRemoteApiLocalizedStatus("Remote.Status.StartFailed", ex.Message);
        }
    }

    private async void Desktop_Exit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        DisposeTrayIcon();

        if (_viewModel is not null)
            _viewModel.RemoteApiEnabledChanged -= ViewModel_RemoteApiEnabledChanged;

        if (_remoteApiHost is not null)
            await _remoteApiHost.DisposeAsync();

        _ffmpegInstaller?.Dispose();
        _ffmpegInstaller = null;
    }
}
