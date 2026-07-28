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
using HelloCrab.Core.Sites.X;
using HelloCrab.Core.Sites.YouTube;
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
    private readonly SemaphoreSlim _remoteApiOperationGate = new(1, 1);
    private RemoteApiHostService? _remoteApiHost;
    private MainWindowViewModel? _viewModel;
    private GyanFfmpegInstallerService? _ffmpegInstaller;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var browser = new PlaywrightBrowserService(new PlaywrightChromiumInstaller());
            var mediaProcessor = new FfmpegMediaService();
            var ffmpegInstaller = _ffmpegInstaller = new GyanFfmpegInstallerService();
            var platformShell = new PlatformShellService();
            var adapters = new SiteAdapterRegistry(new ISiteAdapter[]
            {
                new BilibiliSiteAdapter(),
                new DouyinSiteAdapter(),
                new InstagramSiteAdapter(),
                new TikTokSiteAdapter(),
                new PinterestSiteAdapter(),
                new KuaishouSiteAdapter(),
                new XiaohongshuSiteAdapter(),
                new WeiboSiteAdapter(),
                new XSiteAdapter(),
                new MeipianSiteAdapter(),
                new YouTubeSiteAdapter(mediaProcessor)
            });
            var personImageDetector = new YoloPersonImageDetector();
            var downloader = new MediaDownloadService(browser, mediaProcessor, personImageDetector);
            var historyService = new DownloadHistoryService();
            var imageCache = new ImageCacheService();
            var settingsService = new SettingsService();
            var localization = new LocalizationService();
            var coordinator = new CrawlCoordinator(browser, adapters, downloader, historyService);
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

            _viewModel = viewModel;
            _remoteApiHost = new RemoteApiHostService(viewModel);
            viewModel.RemoteApiEnabledChanged += ViewModel_RemoteApiEnabledChanged;
            viewModel.RemoteApiPortChanged += ViewModel_RemoteApiPortChanged;

            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel
            };

            _ = ApplyRemoteServerStateAsync(viewModel.RemoteApiEnabled);
            desktop.Exit += Desktop_Exit;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ViewModel_RemoteApiEnabledChanged(object? sender, bool enabled)
        => _ = ApplyRemoteServerStateAsync(enabled);

    private void ViewModel_RemoteApiPortChanged(object? sender, int port)
    {
        if (_viewModel?.RemoteApiEnabled == true)
            _ = RestartRemoteServerAsync();
    }

    private async Task ApplyRemoteServerStateAsync(bool enabled)
    {
        await _remoteApiOperationGate.WaitAsync();
        try
        {
            if (_remoteApiHost is null)
                return;

            await _remoteApiHost.SetEnabledAsync(enabled);
        }
        catch (Exception ex)
        {
            _viewModel?.AddRemoteLog($"切换远程控制服务器失败：{ex.Message}");
            _viewModel?.SetRemoteApiStatus($"启动失败：{ex.Message}");
        }
        finally
        {
            _remoteApiOperationGate.Release();
        }
    }

    private async Task RestartRemoteServerAsync()
    {
        await _remoteApiOperationGate.WaitAsync();
        try
        {
            var oldHost = _remoteApiHost;
            _remoteApiHost = null;
            if (oldHost is not null)
                await oldHost.DisposeAsync();

            if (_viewModel is null || !_viewModel.RemoteApiEnabled)
                return;

            _remoteApiHost = new RemoteApiHostService(_viewModel);
            await _remoteApiHost.SetEnabledAsync(true);
        }
        catch (Exception ex)
        {
            _viewModel?.AddRemoteLog($"切换远程端口失败：{ex.Message}");
            _viewModel?.SetRemoteApiStatus($"启动失败：{ex.Message}");
        }
        finally
        {
            _remoteApiOperationGate.Release();
        }
    }

    private async void Desktop_Exit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.RemoteApiEnabledChanged -= ViewModel_RemoteApiEnabledChanged;
            _viewModel.RemoteApiPortChanged -= ViewModel_RemoteApiPortChanged;
        }

        await _remoteApiOperationGate.WaitAsync();
        try
        {
            if (_remoteApiHost is not null)
                await _remoteApiHost.DisposeAsync();
        }
        finally
        {
            _remoteApiOperationGate.Release();
            _remoteApiOperationGate.Dispose();
        }

        _ffmpegInstaller?.Dispose();
        _ffmpegInstaller = null;
    }
}
