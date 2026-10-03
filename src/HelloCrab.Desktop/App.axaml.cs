using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
    private RemoteApiHostService? _remoteApiHost;
    private MainWindowViewModel? _viewModel;
    private GyanFfmpegInstallerService? _ffmpegInstaller;
    private bool _desktopStartupStarted;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var splash = new SplashWindow();
            desktop.MainWindow = splash;
            desktop.Exit += Desktop_Exit;

            splash.Opened += (_, _) =>
            {
                if (_desktopStartupStarted)
                    return;

                _desktopStartupStarted = true;
                _ = InitializeDesktopAsync(desktop, splash);
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task InitializeDesktopAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        SplashWindow splash)
    {
        try
        {
            splash.SetProgress(5, "正在启动 HelloCrab…", "准备应用运行环境");
            await Task.Yield();

            splash.SetProgress(15, "正在加载核心模块…", "初始化浏览器、媒体处理与系统服务");
            var browser = new PlaywrightBrowserService(new PlaywrightChromiumInstaller());
            var mediaProcessor = new FfmpegMediaService();
            var ffmpegInstaller = _ffmpegInstaller = new GyanFfmpegInstallerService();
            var platformShell = new PlatformShellService();

            splash.SetProgress(28, "正在加载平台模块…", "加载哔哩哔哩、抖音、快手、小红书、微博等平台适配器");
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

            splash.SetProgress(40, "正在初始化服务…", "准备下载、历史记录、图片缓存与 AI 模块");
            var personImageDetector = new YoloPersonImageDetector();
            var downloader = new MediaDownloadService(browser, mediaProcessor, personImageDetector);
            var historyService = new DownloadHistoryService();
            var imageCache = new ImageCacheService();
            var settingsService = new SettingsService();
            var localization = new LocalizationService();
            var coordinator = new CrawlCoordinator(browser, adapters, downloader, historyService);

            splash.SetProgress(50, "正在读取设置…", "恢复语言、主题、下载目录与平台配置");
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

            splash.SetProgress(56, "正在检查本地数据…", "检查旧版文件命名并完成必要迁移");
            // 临时启动迁移：统一历史下载文件末尾的“ 空格+序号”为当前“_序号”。
            LegacySequenceFileNameMigration.Run(viewModel.DownloadRoot);

            splash.SetProgress(62, "正在加载历史记录…", "读取已下载作者和作品统计");
            await viewModel.HistoryInitializationTask;
            splash.SetProgress(
                74,
                "历史记录加载完成",
                $"已加载 {viewModel.DownloadHistory.Count} 个作者记录");

            splash.SetProgress(78, "正在检查运行组件…", "检测 Chromium、FFmpeg 与人像识别模型");
            await viewModel.RuntimeComponentInitializationTask;

            splash.SetProgress(87, "正在加载定时任务…", "恢复定时自动下载配置");
            await viewModel.ScheduledDownloadInitializationTask;

            _viewModel = viewModel;
            _remoteApiHost = new RemoteApiHostService(viewModel);
            viewModel.RemoteApiEnabledChanged += ViewModel_RemoteApiEnabledChanged;

            splash.SetProgress(93, "正在启动后台服务…", "应用远程控制服务配置");
            await ApplyRemoteServerStateAsync(viewModel.RemoteApiEnabled);

            splash.SetProgress(97, "正在准备主界面…", "创建窗口并应用界面设置");
            var mainWindow = new MainWindow
            {
                DataContext = viewModel
            };

            desktop.MainWindow = mainWindow;
            mainWindow.Show();

            splash.SetProgress(100, "启动完成", "HelloCrab 已准备就绪");
            await Task.Delay(120);
            splash.Close();
        }
        catch (Exception ex)
        {
            splash.ShowFailure(ex.Message);
        }
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
        if (_viewModel is not null)
            _viewModel.RemoteApiEnabledChanged -= ViewModel_RemoteApiEnabledChanged;

        if (_remoteApiHost is not null)
            await _remoteApiHost.DisposeAsync();

        _ffmpegInstaller?.Dispose();
        _ffmpegInstaller = null;
    }
}
