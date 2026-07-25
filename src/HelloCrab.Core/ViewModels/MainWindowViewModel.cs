using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HelloCrab.Core.Models;
using HelloCrab.Core.Services.Browser;
using HelloCrab.Core.Services.Crawling;
using HelloCrab.Core.Services.Downloading;
using HelloCrab.Core.Services.History;
using HelloCrab.Core.Services.Images;
using HelloCrab.Core.Services.Logging;
using HelloCrab.Core.Services.Localization;
using HelloCrab.Core.Services.Media;
using HelloCrab.Core.Services.Settings;
using HelloCrab.Core.Services.Notifications;
using HelloCrab.Core.Services.Platform;
using HelloCrab.Core.Sites;
using HelloCrab.Core.Utilities;
using HelloCrab.Core.Contracts;

namespace HelloCrab.Core.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IBrowserAutomationService _browser;
    private readonly CrawlCoordinator _coordinator;
    private readonly DownloadHistoryService _historyService;
    private readonly ImageCacheService _imageCache;
    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localization;
    private readonly IPlatformShellService _platformShell;
    private readonly IFfmpegInstallerService _ffmpegInstaller;
    private readonly IPersonImageDetector _personImageDetector;
    private readonly PushPlusNotificationService _pushPlusNotification = new();
    private readonly DailyFileLogWriter _dailyFileLogWriter = new();
    private readonly object _backgroundFinalizationGate = new();
    private readonly HashSet<Task> _backgroundFinalizationTasks = new();
    private bool _isBusy;
    private bool _isInstallingChromium;
    private bool _isChromiumInstallProgressVisible;
    private bool _isChromiumInstallProgressIndeterminate;
    private double _chromiumInstallProgressPercent;
    private string _chromiumInstallProgressText = string.Empty;
    private bool _isInstallingFfmpeg;
    private bool _isFfmpegInstallProgressVisible;
    private bool _isFfmpegInstallProgressIndeterminate;
    private double _ffmpegInstallProgressPercent;
    private string _ffmpegInstallProgressText = string.Empty;
    private bool _isClearingImageCache;
    private string _ffmpegInstallStatusText = string.Empty;
    private string _personDetectionModelStatusText = string.Empty;
    private bool _isCapturing;
    private string _statusText = "准备就绪";
    private string _currentUrl = "尚未打开浏览器";
    private bool _isHeadlessMode;
    private string _browserModeStatusText = "显示模式：使用系统窗口尺寸（NoViewport）";
    private string _downloadRoot;
    private PlatformOption _selectedPlatform;
    private LanguageOption _selectedLanguage;
    private int _responseCount;
    private int _discoveredCount;
    private int _downloadedCount;
    private int _skippedCount;
    private int _failedCount;
    private string _currentWork = "-";
    private bool _isDownloadProgressVisible;
    private bool _isDownloadProgressIndeterminate;
    private double _downloadProgressPercent;
    private string _downloadProgressText = string.Empty;
    private bool _includeWorkId;
    private bool _downloadCover;
    private bool _downloadMusic;
    private decimal _downloadSpeedLimitMBps;
    private bool _checkVideoAudio;
    private bool _enablePersonDetection;
    private double _personDetectionConfidence = 0.60;
    private bool _stopOnDuplicateThreshold = true;
    private int _duplicateStopThreshold = 20;
    private string _pushPlusToken = string.Empty;
    private string? _lastCoordinatorCompletionMessage;
    private int _currentTaskDownloadedCount;
    private string? _currentTaskAuthorId;
    private string? _activeCapturePlatformId;
    private HashSet<string> _authorsKnownBeforeCurrentTask = new(StringComparer.OrdinalIgnoreCase);
    private bool _isDarkTheme;
    private bool _isHistoryVisible;
    private string _historySearchText = string.Empty;
    private string? _currentAuthorDirectory;
    private string? _currentAuthorName;
    private string? _currentAuthorId;
    private string? _currentAuthorAvatarUrl;
    private IImage? _currentAuthorAvatarImage;
    private long _authorAvatarRequestVersion;
    private string? _currentCoverUrl;
    private IImage? _currentCoverImage;
    private long _coverRequestVersion;
    private CancellationTokenSource? _settingsSaveCts;
    private bool _isApplyingSettings;
    private bool _isDisposed;
    private bool _remoteApiEnabled;
    private readonly int _remoteApiPort;
    private string _remoteApiToken;
    private string _remoteApiTokenDraft;
    private string _remoteApiStatusText = "远程服务器未启动";

    public MainWindowViewModel(
        IBrowserAutomationService browser,
        CrawlCoordinator coordinator,
        SiteAdapterRegistry registry,
        DownloadHistoryService historyService,
        ImageCacheService imageCache,
        SettingsService settingsService,
        LocalizationService localization,
        IPlatformShellService platformShell,
        IFfmpegInstallerService ffmpegInstaller,
        IPersonImageDetector personImageDetector)
    {
        _browser = browser;
        _coordinator = coordinator;
        _historyService = historyService;
        _imageCache = imageCache;
        _settingsService = settingsService;
        _localization = localization;
        _platformShell = platformShell;
        _ffmpegInstaller = ffmpegInstaller;
        _personImageDetector = personImageDetector;
        Platforms = registry.Platforms;

        var settings = _settingsService.Load();
        _localization.Apply(settings.LanguageCode);
        RefreshPlatformDisplayNames();
        _selectedLanguage = _localization.Languages.FirstOrDefault(language =>
                                language.Code.Equals(_localization.CurrentLanguageCode, StringComparison.OrdinalIgnoreCase))
                            ?? _localization.Languages.First();
        _statusText = _localization.Get("Status.Ready", "准备就绪");
        _currentUrl = _localization.Get("Status.BrowserNotOpened", "尚未打开浏览器");
        _isApplyingSettings = true;
        _selectedPlatform = Platforms.FirstOrDefault(x =>
                                x.Id.Equals(settings.SelectedPlatformId, StringComparison.OrdinalIgnoreCase))
                            ?? Platforms.First();
        _isHeadlessMode = settings.HeadlessMode;
        _currentUrl = ResolveInitialBrowserUrl(settings.LastBrowserUrl, _selectedPlatform, _isHeadlessMode);
        _browserModeStatusText = GetBrowserModeStatusText(_isHeadlessMode, false);
        _downloadRoot = ResolveDownloadRoot(settings.DownloadRoot);
        _includeWorkId = settings.IncludeWorkId;
        _downloadCover = settings.DownloadCover;
        _downloadMusic = settings.DownloadMusic;
        _downloadSpeedLimitMBps = Math.Clamp(settings.DownloadSpeedLimitMBps, 0m, 10000m);
        _checkVideoAudio = settings.CheckVideoAudio;
        _enablePersonDetection = settings.EnablePersonDetection;
        _personDetectionConfidence = Math.Clamp(settings.PersonDetectionConfidence, 0.10, 0.95);
        _stopOnDuplicateThreshold = settings.StopOnDuplicateThreshold;
        _duplicateStopThreshold = Math.Clamp(settings.DuplicateStopThreshold, 1, 10000);
        _pushPlusToken = settings.PushPlusToken ?? string.Empty;
        _isDarkTheme = settings.Theme.Equals("Dark", StringComparison.OrdinalIgnoreCase);
        _remoteApiEnabled = settings.RemoteApiEnabled;
        _remoteApiPort = Math.Clamp(settings.RemoteApiPort, 1024, 65535);
        _remoteApiToken = settings.RemoteApiToken;
        _remoteApiTokenDraft = _remoteApiToken;
        _isHistoryVisible = false;
        _ffmpegInstallStatusText = _ffmpegInstaller.GetStatusText();
        _personDetectionModelStatusText = BuildPersonDetectionModelStatusText(
            _personImageDetector.GetModelInfo());
        _isApplyingSettings = false;
        ApplyTheme();

        OpenBrowserCommand = new AsyncRelayCommand(OpenBrowserAsync, () => !IsBusy && !IsCapturing);
        InstallChromiumCommand = new AsyncRelayCommand(InstallChromiumAsync, () => !IsBusy && !IsCapturing);
        InstallFfmpegCommand = new AsyncRelayCommand(
            InstallFfmpegAsync,
            () => _ffmpegInstaller.IsSupported && !IsBusy && !IsCapturing && !_isInstallingFfmpeg);
        ClearImageCacheCommand = new AsyncRelayCommand(
            ClearImageCacheAsync,
            () => !IsBusy && !IsCapturing && !_isClearingImageCache);
        StartCaptureCommand = new AsyncRelayCommand(
            StartCaptureAsync,
            CanStartCapture);
        StopCaptureCommand = new RelayCommand(StopCapture, () => IsCapturing || IsScheduledBatchRunning);
        OpenDownloadFolderCommand = new RelayCommand(OpenDownloadFolder);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        ToggleHistoryCommand = new RelayCommand(ToggleHistory);
        ApplyRemoteApiTokenCommand = new RelayCommand(ApplyRemoteApiToken);
        ReloadLanguagesCommand = new RelayCommand(ReloadLanguages);
        InitializeScheduledDownloadFeature(settings.LanguageCode);

        _browser.StateChanged += OnBrowserStateChanged;
        _coordinator.Log += OnCoordinatorLog;
        _coordinator.ProgressChanged += OnCoordinatorProgressChanged;
        _coordinator.Completed += OnCoordinatorCompleted;
        _historyService.HistoryChanged += OnHistoryChanged;

        AddLog(_localization.Get("Status.Ready", "准备就绪"));
        _ = InitializeRuntimeComponentStatusAsync();
        _ = InitializeHistoryAsync();
        _scheduledDownloadInitializationTask = InitializeScheduledDownloadAsync();
        _ = RecoverPendingPersonDetectionAsync();
        QueueSettingsSave();
    }

    public IReadOnlyList<PlatformOption> Platforms { get; }
    public ObservableCollection<string> Logs { get; } = new();
    public ObservableCollection<DownloadHistoryItem> DownloadHistory { get; } = new();
    public ObservableCollection<DownloadHistoryItem> FilteredDownloadHistory { get; } = new();

    public IAsyncRelayCommand OpenBrowserCommand { get; }
    public IAsyncRelayCommand InstallChromiumCommand { get; }
    public IAsyncRelayCommand InstallFfmpegCommand { get; }
    public IAsyncRelayCommand ClearImageCacheCommand { get; }
    public IAsyncRelayCommand StartCaptureCommand { get; }
    public IRelayCommand StopCaptureCommand { get; }
    public IRelayCommand OpenDownloadFolderCommand { get; }
    public IRelayCommand ToggleThemeCommand { get; }
    public IRelayCommand ToggleHistoryCommand { get; }
    public IRelayCommand ApplyRemoteApiTokenCommand { get; }
    public IRelayCommand ReloadLanguagesCommand { get; }

    public IReadOnlyList<LanguageOption> LanguageOptions => _localization.Languages;

    public LanguageOption SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (value is null || value.Code.Equals(_selectedLanguage?.Code, StringComparison.OrdinalIgnoreCase))
                return;
            var browserWasPlaceholder = IsBrowserUrlPlaceholder(CurrentUrl);
            var statusWasReady = StatusText.Equals(
                _localization.Get("Status.Ready", "准备就绪"),
                StringComparison.Ordinal);
            if (!SetProperty(ref _selectedLanguage, value))
                return;
            _localization.Apply(value.Code);
            ApplyScheduledDownloadCulture(value.Code);
            if (browserWasPlaceholder)
                CurrentUrl = _localization.Get("Status.BrowserNotOpened", "尚未打开浏览器");
            if (statusWasReady)
                StatusText = _localization.Get("Status.Ready", "准备就绪");
            RefreshLocalizedUi();
            QueueSettingsSave();
        }
    }

    public string LanguageDirectoryText
        => _localization.Format("Settings.Language.Directory", _localization.LanguageDirectory);

    public PlatformOption SelectedPlatform
    {
        get => _selectedPlatform;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedPlatform))
                return;

            var previous = _selectedPlatform;
            if (!SetProperty(ref _selectedPlatform, value))
                return;

            if (IsHeadlessMode
                && !_browser.IsStarted
                && ShouldReplaceUrlForPlatformChange(CurrentUrl, previous))
            {
                CurrentUrl = value.HomeUrl;
            }

            OnPropertyChanged(nameof(IsCurrentUrlReadOnly));
            OnPropertyChanged(nameof(OpenBrowserButtonText));
            BrowserModeStatusText = GetBrowserModeStatusText(IsHeadlessMode, _browser.IsLoginRecoveryActive);
            QueueSettingsSave();
            RefreshCommands();
        }
    }

    public string DownloadRoot
    {
        get => _downloadRoot;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value)
                ? GetDefaultDownloadRoot()
                : value.Trim();
            if (SetProperty(ref _downloadRoot, normalized))
                QueueSettingsSave();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanChangeBrowserMode));
                RefreshCommands();
            }
        }
    }

    public string InstallChromiumButtonText => _isInstallingChromium
        ? _localization.Get("Browser.InstallingChromium", "正在安装 Chromium…")
        : _localization.Get("Browser.InstallChromium", "安装 Chromium");

    public bool IsChromiumInstallProgressVisible
    {
        get => _isChromiumInstallProgressVisible;
        private set => SetProperty(ref _isChromiumInstallProgressVisible, value);
    }

    public bool IsChromiumInstallProgressIndeterminate
    {
        get => _isChromiumInstallProgressIndeterminate;
        private set => SetProperty(ref _isChromiumInstallProgressIndeterminate, value);
    }

    public double ChromiumInstallProgressPercent
    {
        get => _chromiumInstallProgressPercent;
        private set => SetProperty(ref _chromiumInstallProgressPercent, value);
    }

    public string ChromiumInstallProgressText
    {
        get => _chromiumInstallProgressText;
        private set => SetProperty(ref _chromiumInstallProgressText, value);
    }

    public string InstallFfmpegButtonText => _isInstallingFfmpeg
        ? _localization.Get("Download.DownloadingFfmpeg", "正在下载 FFmpeg…")
        : _ffmpegInstaller.IsInstalled
            ? _localization.Get("Download.RedownloadFfmpeg", "重新下载 FFmpeg")
            : _localization.Get("Download.DownloadFfmpeg", "下载 FFmpeg");

    public bool IsFfmpegInstallProgressVisible
    {
        get => _isFfmpegInstallProgressVisible;
        private set => SetProperty(ref _isFfmpegInstallProgressVisible, value);
    }

    public bool IsFfmpegInstallProgressIndeterminate
    {
        get => _isFfmpegInstallProgressIndeterminate;
        private set => SetProperty(ref _isFfmpegInstallProgressIndeterminate, value);
    }

    public double FfmpegInstallProgressPercent
    {
        get => _ffmpegInstallProgressPercent;
        private set => SetProperty(ref _ffmpegInstallProgressPercent, value);
    }

    public string FfmpegInstallProgressText
    {
        get => _ffmpegInstallProgressText;
        private set => SetProperty(ref _ffmpegInstallProgressText, value);
    }

    public string FfmpegInstallStatusText
    {
        get => _ffmpegInstallStatusText;
        private set => SetProperty(ref _ffmpegInstallStatusText, value);
    }

    public bool IsFfmpegAutoInstallSupported => _ffmpegInstaller.IsSupported;

    public string PersonDetectionModelStatusText
    {
        get => _personDetectionModelStatusText;
        private set => SetProperty(ref _personDetectionModelStatusText, value);
    }

    public string ImageCachePathText => _localization.Format("Status.ImageCachePath", _imageCache.CacheDirectory);

    public string ClearImageCacheButtonText => _isClearingImageCache
        ? _localization.Get("Download.ClearingCache", "正在清理…")
        : _localization.Get("Download.ClearCache", "清空图片缓存");

    public bool IsCapturing
    {
        get => _isCapturing;
        private set
        {
            if (SetProperty(ref _isCapturing, value))
            {
                OnPropertyChanged(nameof(IsCurrentUrlReadOnly));
                OnPropertyChanged(nameof(CanChangeBrowserMode));
                OnPropertyChanged(nameof(CanStopCurrentTask));
                RefreshCommands();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value) && !string.IsNullOrWhiteSpace(value))
                AddLog(value);
        }
    }
    public string CurrentUrl
    {
        get => _currentUrl;
        set
        {
            var normalized = value ?? string.Empty;
            if (SetProperty(ref _currentUrl, normalized))
                QueueSettingsSave();
        }
    }

    public bool IsHeadlessMode
    {
        get => _isHeadlessMode;
        set
        {
            if (!SetProperty(ref _isHeadlessMode, value))
                return;

            if (value && IsBrowserUrlPlaceholder(CurrentUrl))
                CurrentUrl = SelectedPlatform.HomeUrl;

            BrowserModeStatusText = GetBrowserModeStatusText(value, false);
            OnPropertyChanged(nameof(IsCurrentUrlReadOnly));
            OnPropertyChanged(nameof(OpenBrowserButtonText));
            QueueSettingsSave();
        }
    }

    public bool IsCurrentUrlReadOnly => IsCapturing || !IsHeadlessMode;
    public bool CanChangeBrowserMode => !IsBusy && !IsCapturing;
    public string OpenBrowserButtonText => IsHeadlessMode
        ? _localization.Get("Browser.OpenHeadless", "打开无头浏览器")
        : _localization.Get("Browser.Open", "打开浏览器");

    public string BrowserModeStatusText
    {
        get => _browserModeStatusText;
        private set => SetProperty(ref _browserModeStatusText, value);
    }
    public int ResponseCount { get => _responseCount; private set => SetProperty(ref _responseCount, value); }
    public int DiscoveredCount { get => _discoveredCount; private set => SetProperty(ref _discoveredCount, value); }
    public int DownloadedCount { get => _downloadedCount; private set => SetProperty(ref _downloadedCount, value); }
    public int SkippedCount { get => _skippedCount; private set => SetProperty(ref _skippedCount, value); }
    public int FailedCount { get => _failedCount; private set => SetProperty(ref _failedCount, value); }
    public string CurrentWork { get => _currentWork; private set => SetProperty(ref _currentWork, value); }
    public bool IsDownloadProgressVisible { get => _isDownloadProgressVisible; private set => SetProperty(ref _isDownloadProgressVisible, value); }
    public bool IsDownloadProgressIndeterminate { get => _isDownloadProgressIndeterminate; private set => SetProperty(ref _isDownloadProgressIndeterminate, value); }
    public double DownloadProgressPercent { get => _downloadProgressPercent; private set => SetProperty(ref _downloadProgressPercent, value); }
    public string DownloadProgressText { get => _downloadProgressText; private set => SetProperty(ref _downloadProgressText, value); }

    public bool IncludeWorkId
    {
        get => _includeWorkId;
        set
        {
            if (SetProperty(ref _includeWorkId, value))
                QueueSettingsSave();
        }
    }

    public bool DownloadCover
    {
        get => _downloadCover;
        set
        {
            if (SetProperty(ref _downloadCover, value))
                QueueSettingsSave();
        }
    }

    public bool DownloadMusic
    {
        get => _downloadMusic;
        set
        {
            if (SetProperty(ref _downloadMusic, value))
                QueueSettingsSave();
        }
    }

    public decimal DownloadSpeedLimitMBps
    {
        get => _downloadSpeedLimitMBps;
        set
        {
            var normalized = Math.Clamp(value, 0m, 10000m);
            if (SetProperty(ref _downloadSpeedLimitMBps, normalized))
                QueueSettingsSave();
        }
    }

    public bool CheckVideoAudio
    {
        get => _checkVideoAudio;
        set
        {
            if (SetProperty(ref _checkVideoAudio, value))
                QueueSettingsSave();
        }
    }

    public bool EnablePersonDetection
    {
        get => _enablePersonDetection;
        set
        {
            if (!SetProperty(ref _enablePersonDetection, value))
                return;

            QueueSettingsSave();
            var modelInfo = RefreshPersonDetectionModelStatus();
            if (value)
                AddPersonDetectionModelLog(modelInfo);
        }
    }

    public double PersonDetectionConfidence
    {
        get => _personDetectionConfidence;
        set
        {
            var normalized = Math.Round(Math.Clamp(value, 0.10, 0.95), 2);
            if (!SetProperty(ref _personDetectionConfidence, normalized))
                return;

            OnPropertyChanged(nameof(PersonDetectionConfidenceText));
            QueueSettingsSave();
        }
    }

    public string PersonDetectionConfidenceText
        => $"{PersonDetectionConfidence * 100:0}%";

    public bool StopOnDuplicateThreshold
    {
        get => _stopOnDuplicateThreshold;
        set
        {
            if (SetProperty(ref _stopOnDuplicateThreshold, value))
                QueueSettingsSave();
        }
    }

    public int DuplicateStopThreshold
    {
        get => _duplicateStopThreshold;
        set
        {
            var normalized = Math.Clamp(value, 1, 10000);
            if (SetProperty(ref _duplicateStopThreshold, normalized))
                QueueSettingsSave();
        }
    }

    public string PushPlusToken
    {
        get => _pushPlusToken;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (SetProperty(ref _pushPlusToken, normalized))
                QueueSettingsSave();
        }
    }


    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        private set
        {
            if (SetProperty(ref _isDarkTheme, value))
            {
                OnPropertyChanged(nameof(ThemeButtonText));
                OnPropertyChanged(nameof(ThemeIcon));
                QueueSettingsSave();
            }
        }
    }

    public bool IsHistoryVisible
    {
        get => _isHistoryVisible;
        private set
        {
            if (SetProperty(ref _isHistoryVisible, value))
                OnPropertyChanged(nameof(HistoryButtonText));
        }
    }

    public string ThemeButtonText => IsDarkTheme
        ? _localization.Get("Theme.ToLight", "切换到亮色主题")
        : _localization.Get("Theme.ToDark", "切换到暗色主题");
    public string ThemeIcon => IsDarkTheme ? "🔆" : "🌙";
    public string HistoryButtonText => IsHistoryVisible
        ? _localization.Get("History.Hide", "隐藏下载历史")
        : _localization.Get("History.Show", "显示下载历史");

    public string HistorySearchText
    {
        get => _historySearchText;
        set
        {
            var normalized = value ?? string.Empty;
            if (!SetProperty(ref _historySearchText, normalized))
                return;

            OnPropertyChanged(nameof(IsHistorySearchActive));
            RefreshFilteredDownloadHistory();
        }
    }

    public bool IsHistorySearchActive => !string.IsNullOrWhiteSpace(HistorySearchText);
    public string HistorySearchPlaceholderText => _localization.Get(
        "History.SearchPlaceholder",
        "请输入作者名字，id，平台");
    public bool IsBrowserStarted => _browser.IsStarted;
    public event EventHandler<bool>? RemoteApiEnabledChanged;

    public bool RemoteApiEnabled
    {
        get => _remoteApiEnabled;
        set
        {
            if (!SetProperty(ref _remoteApiEnabled, value))
                return;

            QueueSettingsSave();
            RemoteApiEnabledChanged?.Invoke(this, value);
        }
    }

    public int RemoteApiPort => _remoteApiPort;
    public string RemoteApiToken => _remoteApiToken;

    public string RemoteApiTokenDraft
    {
        get => _remoteApiTokenDraft;
        set => SetProperty(ref _remoteApiTokenDraft, value ?? string.Empty);
    }

    public string RemoteApiStatusText
    {
        get => _remoteApiStatusText;
        private set => SetProperty(ref _remoteApiStatusText, value);
    }
    public string? CurrentAuthorDirectory { get => _currentAuthorDirectory; private set => SetProperty(ref _currentAuthorDirectory, value); }
    public string? CurrentAuthorName { get => _currentAuthorName; private set => SetProperty(ref _currentAuthorName, value); }
    public string? CurrentAuthorId { get => _currentAuthorId; private set => SetProperty(ref _currentAuthorId, value); }
    public IImage? CurrentAuthorAvatarImage { get => _currentAuthorAvatarImage; private set => SetProperty(ref _currentAuthorAvatarImage, value); }
    public IImage? CurrentCoverImage { get => _currentCoverImage; private set => SetProperty(ref _currentCoverImage, value); }

    private async Task InitializeRuntimeComponentStatusAsync()
    {
        try
        {
            var chromiumPath = await _browser.FindInstalledChromiumPathAsync(CancellationToken.None);
            var ffmpegInfo = _ffmpegInstaller.GetToolInfo();
            var modelInfo = _personImageDetector.GetModelInfo();

            Ui(() =>
            {
                PersonDetectionModelStatusText = BuildPersonDetectionModelStatusText(modelInfo);

                AddLog(string.IsNullOrWhiteSpace(chromiumPath)
                    ? "未找到 Chromium 浏览器。首次使用请点击“安装 Chromium”。"
                    : $"已找到 Chromium 浏览器，位置：{chromiumPath}");

                AddLog(ffmpegInfo.IsFound
                    ? $"已找到 FFmpeg，位置：{ffmpegInfo.FfmpegPath}；ffprobe：{ffmpegInfo.FfprobePath}"
                    : "未找到 FFmpeg。开启视频音轨检测前可点击“下载 FFmpeg”。");

                AddPersonDetectionModelLog(modelInfo);
            });
        }
        catch (Exception ex)
        {
            Ui(() => AddLog($"检查 Chromium、FFmpeg 和 YOLO 组件失败：{ex.Message}"));
        }
    }

    private async Task InitializeHistoryAsync()
    {
        try
        {
            var items = await _historyService.LoadAsync();
            Ui(() => SyncHistory(items));
            await LoadHistoryAvatarsAsync(items);
            Ui(() => AddLog($"已加载 {items.Count} 位作者的下载历史。"));
        }
        catch (Exception ex)
        {
            Ui(() => AddLog($"加载下载历史失败：{ex.Message}"));
        }
    }

    private async Task OpenBrowserAsync()
    {
        IsBusy = true;
        try
        {
            var targetUrl = IsHeadlessMode
                ? NormalizeBrowserUrl(CurrentUrl)
                : SelectedPlatform.HomeUrl;

            CurrentUrl = targetUrl;
            StatusText = IsHeadlessMode ? "正在启动无头浏览器…" : "正在启动浏览器…";
            await _browser.StartAsync(targetUrl, IsHeadlessMode);

            if (_browser.IsLoginRecoveryActive)
            {
                StatusText = "登录已失效，请在显示的浏览器中完成登录";
            }
            else if (_browser.IsHeadless)
            {
                StatusText = "无头浏览器已打开并导航到目标 URL";
            }
            else
            {
                StatusText = "请在打开的浏览器中登录，然后进入作者主页";
            }

            AddLog($"浏览器登录状态保存在程序目录：{Path.Combine(AppContext.BaseDirectory, "browser-profile")}。");
        }
        catch (Exception ex)
        {
            StatusText = "浏览器启动失败";
            AddLog($"打开浏览器失败：{ex.Message}");
            AddLog($"首次运行请点击“安装 Chromium”。程序会安装到：{_browser.PreferredChromiumInstallDirectory}；若该目录没有浏览器，再兼容查找系统原有的 Playwright 缓存目录。");
        }
        finally
        {
            IsBusy = false;
            RefreshCommands();
        }
    }

    private async Task InstallChromiumAsync()
    {
        IsBusy = true;
        _isInstallingChromium = true;
        OnPropertyChanged(nameof(InstallChromiumButtonText));

        IsChromiumInstallProgressVisible = true;
        IsChromiumInstallProgressIndeterminate = true;
        ChromiumInstallProgressPercent = 0;
        ChromiumInstallProgressText = _localization.Get(
            "Browser.ChromiumDownloadPreparing",
            "正在准备 Chromium 下载…");

        var startedAt = DateTimeOffset.Now;
        var progress = new Progress<ChromiumInstallProgress>(item =>
        {
            IsChromiumInstallProgressIndeterminate = item.Percent is null;
            if (item.Percent is { } percent)
                ChromiumInstallProgressPercent = Math.Clamp(percent, 0d, 100d);

            ChromiumInstallProgressText = BuildChromiumInstallProgressText(item);
        });

        try
        {
            StatusText = "正在安装 Playwright Chromium…";
            AddLog($"开始下载并安装 Chromium 到程序目录：{_browser.PreferredChromiumInstallDirectory}。界面会实时显示当前组件的下载百分比。");

            var exitCode = await _browser.InstallChromiumAsync(progress);
            var elapsed = DateTimeOffset.Now - startedAt;

            if (exitCode == 0)
            {
                IsChromiumInstallProgressIndeterminate = false;
                ChromiumInstallProgressPercent = 100;
                ChromiumInstallProgressText = _localization.Get(
                    "Browser.ChromiumDownloadComplete",
                    "Chromium 下载并安装完成");
                StatusText = "Chromium 下载并安装完成";
                AddLog($"Chromium 已安装到程序目录，用时 {FormatElapsed(elapsed)}。现在可以点击“打开浏览器”。");
                var chromiumPath = await _browser.FindInstalledChromiumPathAsync(CancellationToken.None);
                if (!string.IsNullOrWhiteSpace(chromiumPath))
                {
                    AddLog($"已找到 Chromium 浏览器，位置：{chromiumPath}");
                    if (IsPathInsideDirectory(
                        chromiumPath,
                        _browser.PreferredChromiumInstallDirectory))
                    {
                        AddLog("当前正在使用程序目录中的便携 Chromium。关闭 HelloCrab 后，可以删除用户目录下旧的 ms-playwright 缓存。");
                    }
                }
            }
            else
            {
                ChromiumInstallProgressText = _localization.Format(
                    "Browser.ChromiumDownloadFailed",
                    exitCode);
                StatusText = $"Chromium 安装失败，退出码 {exitCode}";
                AddLog("安装程序已经结束，安装按钮已恢复，可以检查网络后重新尝试。");
            }
        }
        catch (Exception ex)
        {
            ChromiumInstallProgressText = _localization.Get(
                "Browser.ChromiumDownloadFailedGeneral",
                "Chromium 下载或安装失败");
            StatusText = "Chromium 安装失败";
            AddLog($"安装失败：{ex.Message}");
            AddLog("安装按钮已恢复，可以重新尝试。");
        }
        finally
        {
            _isInstallingChromium = false;
            OnPropertyChanged(nameof(InstallChromiumButtonText));
            IsBusy = false;

            // 保留最终状态一小段时间，让用户能看到 100% 或失败信息。
            await Task.Delay(1200);
            IsChromiumInstallProgressVisible = false;

            // AsyncRelayCommand 自身执行状态结束前仍可能保持禁用，显式刷新一次。
            Dispatcher.UIThread.Post(RefreshCommands, DispatcherPriority.Background);
        }
    }

    private string BuildChromiumInstallProgressText(ChromiumInstallProgress progress)
    {
        var stage = string.IsNullOrWhiteSpace(progress.Stage)
            ? "Chromium"
            : progress.Stage.Trim();
        var detail = BuildChromiumInstallProgressDetail(progress.Detail);

        if (progress.Percent is { } percent)
        {
            var percentage = $"{Math.Clamp(percent, 0d, 100d):0}%";
            return string.IsNullOrWhiteSpace(detail)
                ? $"{stage}：{percentage}"
                : $"{stage}：{percentage} · {detail}";
        }

        var status = string.IsNullOrWhiteSpace(detail)
            ? _localization.Get("Browser.ChromiumDownloadPreparing", "正在准备下载…")
            : detail;
        return $"{stage}：{status}";
    }

    private string BuildChromiumInstallProgressDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return string.Empty;

        if (detail.StartsWith("size:", StringComparison.OrdinalIgnoreCase))
        {
            return _localization.Format(
                "Browser.ChromiumDownloadTotalSize",
                detail[5..]);
        }

        return detail switch
        {
            "extracting" => _localization.Get(
                "Browser.ChromiumDownloadExtracting",
                "正在解压…"),
            "finalizing" => _localization.Get(
                "Browser.ChromiumDownloadFinalizing",
                "下载完成，正在整理文件…"),
            _ => detail
        };
    }

    private static bool IsPathInsideDirectory(string path, string directory)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullDirectory = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(
                fullDirectory,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string BuildFfmpegInstallProgressText(FfmpegInstallProgress progress)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(progress.Message))
            parts.Add(progress.Message.Trim().TrimEnd('。', '…'));

        if (progress.Percentage is { } percentage)
            parts.Add($"{percentage}%");

        if (progress.TotalBytes is > 0)
        {
            parts.Add(
                $"{FormatFileSize(progress.BytesReceived)} / " +
                FormatFileSize(progress.TotalBytes.Value));
        }
        else if (progress.BytesReceived > 0)
        {
            parts.Add(FormatFileSize(progress.BytesReceived));
        }

        if (progress.BytesPerSecond > 0)
            parts.Add($"{FormatFileSize((long)progress.BytesPerSecond)}/s");

        return string.Join(" · ", parts);
    }

    private async Task InstallFfmpegAsync()
    {
        IsBusy = true;
        _isInstallingFfmpeg = true;
        OnPropertyChanged(nameof(InstallFfmpegButtonText));
        RefreshCommands();

        IsFfmpegInstallProgressVisible = true;
        IsFfmpegInstallProgressIndeterminate = true;
        FfmpegInstallProgressPercent = 0;
        FfmpegInstallProgressText = "正在准备 FFmpeg 下载…";

        try
        {
            StatusText = "正在后台下载并安装 FFmpeg…";
            AddLog("开始访问 gyan.dev FFmpeg Windows 构建页，并下载 release essentials ZIP。界面会实时显示下载百分比、大小和速度。");

            var progress = new Progress<FfmpegInstallProgress>(item =>
            {
                var isDownloading = item.BytesReceived > 0;
                IsFfmpegInstallProgressIndeterminate =
                    !isDownloading || item.Percentage is null;

                if (item.Percentage is { } percentage)
                {
                    FfmpegInstallProgressPercent =
                        Math.Clamp(percentage, 0d, 100d);
                }

                FfmpegInstallProgressText =
                    BuildFfmpegInstallProgressText(item);
                FfmpegInstallStatusText = item.Message;
            });

            var result = await _ffmpegInstaller.InstallAsync(progress);
            IsFfmpegInstallProgressIndeterminate = false;
            FfmpegInstallProgressPercent = 100;
            FfmpegInstallProgressText = "FFmpeg 下载并安装完成";
            FfmpegInstallStatusText = _ffmpegInstaller.GetStatusText();
            StatusText = "FFmpeg 下载并安装完成";
            AddLog($"ffmpeg.exe：{result.FfmpegPath}");
            AddLog($"ffprobe.exe：{result.FfprobePath}");
            AddLog("无需重启程序；后续开启视频音轨检测时会直接使用新安装的工具。");
        }
        catch (OperationCanceledException)
        {
            FfmpegInstallProgressText = "FFmpeg 下载已取消";
            StatusText = "FFmpeg 下载已取消";
            FfmpegInstallStatusText = _ffmpegInstaller.GetStatusText();
        }
        catch (Exception ex)
        {
            FfmpegInstallProgressText = $"FFmpeg 下载或安装失败：{ex.Message}";
            StatusText = "FFmpeg 下载或安装失败";
            FfmpegInstallStatusText = $"安装失败：{ex.Message}";
            AddLog($"FFmpeg 安装失败：{ex.Message}");
        }
        finally
        {
            _isInstallingFfmpeg = false;
            OnPropertyChanged(nameof(InstallFfmpegButtonText));
            IsBusy = false;

            // 保留最终状态片刻，避免完成或失败信息一闪而过。
            await Task.Delay(1200);
            IsFfmpegInstallProgressVisible = false;
            Dispatcher.UIThread.Post(RefreshCommands, DispatcherPriority.Background);
        }
    }

    private async Task ClearImageCacheAsync()
    {
        if (_isClearingImageCache)
            return;

        _isClearingImageCache = true;
        OnPropertyChanged(nameof(ClearImageCacheButtonText));
        IsBusy = true;
        RefreshCommands();

        try
        {
            StatusText = "正在清理图片缓存…";
            var result = await _imageCache.ClearDiskCacheAsync();
            var releasedText = FormatFileSize(result.ReleasedBytes);

            if (result.FailedFileCount == 0)
            {
                StatusText = result.DeletedFileCount == 0
                    ? "图片缓存已经是空的"
                    : $"图片缓存已清空：删除 {result.DeletedFileCount} 个文件，释放 {releasedText}";
            }
            else
            {
                StatusText =
                    $"图片缓存已部分清理：删除 {result.DeletedFileCount} 个文件，" +
                    $"释放 {releasedText}，{result.FailedFileCount} 个文件删除失败";
            }

            AddLog($"图片缓存目录：{result.CacheDirectory}");
            AddLog("清理图片缓存不会删除已下载作品、History.json、settings.json 或浏览器登录状态。");
        }
        catch (Exception ex)
        {
            StatusText = "清理图片缓存失败";
            AddLog($"清理图片缓存失败：{ex.Message}");
        }
        finally
        {
            _isClearingImageCache = false;
            OnPropertyChanged(nameof(ClearImageCacheButtonText));
            IsBusy = false;
            Dispatcher.UIThread.Post(RefreshCommands, DispatcherPriority.Background);
        }
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalMinutes >= 1)
            return $"{(int)elapsed.TotalMinutes} 分 {elapsed.Seconds} 秒";

        return $"{Math.Max(1, (int)elapsed.TotalSeconds)} 秒";
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";

        var value = (double)bytes;
        var units = new[] { "KB", "MB", "GB", "TB" };
        var unitIndex = -1;
        do
        {
            value /= 1024;
            unitIndex++;
        } while (value >= 1024 && unitIndex < units.Length - 1);

        return $"{value:0.##} {units[unitIndex]}";
    }

    private async Task StartCaptureAsync()
    {
        if (_browser.IsLoginRecoveryActive)
        {
            StatusText = "请先完成扫码登录";
            return;
        }

        if (EnablePersonDetection)
            AddPersonDetectionModelLog(RefreshPersonDetectionModelStatus());

        ClearCurrentCover();
        _lastCoordinatorCompletionMessage = null;
        _currentTaskDownloadedCount = 0;
        _currentTaskAuthorId = null;
        var capturePlatformId = SelectedPlatform.Id;
        var pushPlusTokenSnapshot = PushPlusToken;
        _activeCapturePlatformId = capturePlatformId;

        HashSet<string> authorsKnownBeforeTask;
        try
        {
            var historyBeforeTask = await _historyService.LoadAsync(CancellationToken.None);
            authorsKnownBeforeTask = historyBeforeTask
                .Select(item => BuildAuthorHistoryKey(item.Platform, item.UserId))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            authorsKnownBeforeTask = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddLog($"读取下载历史失败，本次完成通知按新下载处理：{ex.Message}");
        }

        IsCapturing = true;
        StatusText = "正在采集…";
        AddLog("点击开始采集。程序会自动刷新作者主页、下载当前批次，然后继续滚动。");
        if (DownloadSpeedLimitMBps > 0)
            AddLog($"已启用下载速度限制：{DownloadSpeedLimitMBps:0.##} MB/s（所有作品媒体共享该上限）。");
        if (EnablePersonDetection)
            AddLog($"人像检测置信度：{PersonDetectionConfidenceText}。");
        try
        {
            var options = new CrawlerDownloadOptions(
                IncludeWorkId,
                DownloadCover,
                DownloadMusic,
                CheckVideoAudio,
                EnablePersonDetection,
                StopOnDuplicateThreshold,
                DuplicateStopThreshold,
                DownloadSpeedLimitMBps,
                PersonDetectionConfidence);
            var result = await _coordinator.StartAsync(
                capturePlatformId,
                DownloadRoot,
                options);

            var isUpdate = !string.IsNullOrWhiteSpace(result.AuthorId)
                           && authorsKnownBeforeTask.Contains(
                               BuildAuthorHistoryKey(capturePlatformId, result.AuthorId));
            var finalizationTask = FinalizeCaptureAsync(
                result,
                pushPlusTokenSnapshot,
                isUpdate);

            if (result.PersonDetectionEnabled
                && result.PersonDetection.PendingCount > 0
                && !result.PersonDetection.Completion.IsCompleted)
            {
                AddLog(
                    $"下载线程已释放；{result.PersonDetection.PendingCount} 张图片正在后台排队检测。" +
                    "现在可以切换作者并开始下一次采集。");
                TrackBackgroundFinalization(finalizationTask);
            }
            else
            {
                await finalizationTask;
            }
        }
        catch (Exception ex)
        {
            StatusText = "采集失败";
            AddLog($"采集失败（{ex.GetType().Name}）：{ex.Message}");
            if (ex.InnerException is not null
                && !string.Equals(ex.InnerException.Message, ex.Message, StringComparison.Ordinal))
            {
                AddLog($"内部异常（{ex.InnerException.GetType().Name}）：{ex.InnerException.Message}");
            }
        }
        finally
        {
            IsCapturing = false;
            CurrentWork = "-";
            ClearCurrentAuthorAvatar();
            ClearCurrentCover();
            RefreshCommands();
        }
    }

    private void StopCapture()
    {
        StatusText = "正在停止…";
        if (IsScheduledBatchRunning)
        {
            _scheduledBatchCts?.Cancel();
            AddLog(_localization.Get(
                "Schedule.Log.CancelRequested",
                "已请求停止定时自动下载任务；当前作者停止后不会继续处理后续历史任务。"));
        }
        _coordinator.Stop();
    }

    private void ToggleTheme() => SetTheme(!IsDarkTheme);

    public void SetTheme(bool isDark)
    {
        IsDarkTheme = isDark;
        ApplyTheme();
    }

    public void AddRemoteLog(string message) => Ui(() => AddLog(message));


    private void ApplyRemoteApiToken()
    {
        var token = (RemoteApiTokenDraft ?? string.Empty).Trim();

        if (token.Length is < 4 or > 64)
        {
            AddRemoteLog("访问令牌保存失败：请输入 4–64 位字符。");
            return;
        }

        if (!Regex.IsMatch(token, @"^[A-Za-z0-9._@-]+$"))
        {
            AddRemoteLog("访问令牌保存失败：仅支持英文字母、数字以及 . _ @ -。");
            return;
        }

        if (string.Equals(_remoteApiToken, token, StringComparison.Ordinal))
        {
            RemoteApiTokenDraft = token;
            AddRemoteLog("访问令牌没有变化。");
            return;
        }

        _remoteApiToken = token;
        RemoteApiTokenDraft = token;
        OnPropertyChanged(nameof(RemoteApiToken));
        QueueSettingsSave();
        AddRemoteLog("远程访问令牌已更新；网页和手机端需要使用新令牌重新连接。");
    }

    public void SetRemoteApiStatus(string message)
        => Ui(() => RemoteApiStatusText = message);

    public RemoteCrawlerSnapshot CreateRemoteSnapshot()
    {
        return new RemoteCrawlerSnapshot
        {
            ServerTime = DateTimeOffset.Now,
            IsBusy = IsBusy,
            IsCapturing = IsCapturing,
            IsBrowserStarted = _browser.IsStarted,
            StatusText = StatusText,
            CurrentUrl = CurrentUrl,
            CurrentWork = CurrentWork,
            IsDownloading = IsDownloadProgressVisible,
            IsDownloadIndeterminate = IsDownloadProgressIndeterminate,
            DownloadProgressPercent = DownloadProgressPercent,
            DownloadProgressText = DownloadProgressText,
            CurrentCoverUrl = IsCapturing ? _currentCoverUrl ?? string.Empty : string.Empty,
            CurrentAuthorName = CurrentAuthorName,
            CurrentAuthorId = CurrentAuthorId,
            CurrentAuthorDirectory = CurrentAuthorDirectory,
            ResponseCount = ResponseCount,
            DiscoveredCount = DiscoveredCount,
            DownloadedCount = DownloadedCount,
            SkippedCount = SkippedCount,
            FailedCount = FailedCount,
            Settings = new RemoteSettingsDto
            {
                Theme = IsDarkTheme ? "Dark" : "Light",
                SelectedPlatformId = SelectedPlatform.Id,
                HeadlessMode = IsHeadlessMode,
                BrowserUrl = CurrentUrl,
                DownloadRoot = DownloadRoot,
                IncludeWorkId = IncludeWorkId,
                DownloadCover = DownloadCover,
                DownloadMusic = DownloadMusic,
                CheckVideoAudio = CheckVideoAudio,
                EnablePersonDetection = EnablePersonDetection,
                PersonDetectionConfidence = PersonDetectionConfidence,
                StopOnDuplicateThreshold = StopOnDuplicateThreshold,
                DuplicateStopThreshold = DuplicateStopThreshold
            },
            Logs = Logs.Take(150).ToList(),
            History = DownloadHistory.Select(item => new RemoteHistoryItemDto
            {
                Id = item.Id,
                Platform = item.Platform,
                UserId = item.UserId,
                UserName = item.UserName,
                OriginalUrl = item.OriginalUrl,
                FolderPath = item.FolderPath,
                HeadUrl = item.HeadUrl,
                ItemsCount = item.ItemsCount,
                ItemsSize = item.ItemsSize,
                UpdatedAt = item.UpdatedAt
            }).ToList()
        };
    }


    public byte[]? CreateRemoteHistoryAvatarPng(int historyId)
    {
        var item = DownloadHistory.FirstOrDefault(history => history.Id == historyId);
        if (item?.AvatarImage is not Bitmap bitmap)
            return null;

        using var output = new MemoryStream();
        bitmap.Save(output, new PngBitmapEncoderOptions());
        return output.ToArray();
    }

    public string? GetRemoteHistoryAvatarUrl(int historyId)
        => DownloadHistory
            .FirstOrDefault(history => history.Id == historyId)?.HeadUrl;

    public async Task<byte[]?> DownloadRemoteHistoryAvatarPngAsync(
        string? headUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(headUrl))
            return null;

        var bitmap = await _imageCache.LoadAsync(headUrl, cancellationToken);
        if (bitmap is null)
            return null;

        using var output = new MemoryStream();
        bitmap.Save(output, new PngBitmapEncoderOptions());
        return output.ToArray();
    }

    public byte[]? CreateRemoteCoverPng()
    {
        if (!IsCapturing || CurrentCoverImage is not Bitmap bitmap)
            return null;

        using var output = new MemoryStream();
        bitmap.Save(output, new PngBitmapEncoderOptions());
        return output.ToArray();
    }

    private void ClearCurrentCover()
    {
        Interlocked.Increment(ref _coverRequestVersion);
        _currentCoverUrl = null;
        CurrentCoverImage = null;
    }

    private void ClearCurrentAuthorAvatar()
    {
        Interlocked.Increment(ref _authorAvatarRequestVersion);
        _currentAuthorAvatarUrl = null;
        CurrentAuthorAvatarImage = null;
    }

    public async Task ApplyRemoteSettingsAsync(
        RemoteSettingsDto settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // 此方法由远程 API 切换到 UI 线程后调用。批量赋值期间暂停各属性的
        // 延迟保存，确保桌面界面一次性刷新，然后把同一份最终状态立即写入 JSON。
        _isApplyingSettings = true;
        try
        {
            var platform = Platforms.FirstOrDefault(item =>
                item.Id.Equals(settings.SelectedPlatformId, StringComparison.OrdinalIgnoreCase));
            if (platform is not null)
                SelectedPlatform = platform;

            IsHeadlessMode = settings.HeadlessMode;
            CurrentUrl = string.IsNullOrWhiteSpace(settings.BrowserUrl)
                ? SelectedPlatform.HomeUrl
                : settings.BrowserUrl.Trim();
            DownloadRoot = settings.DownloadRoot;
            IncludeWorkId = settings.IncludeWorkId;
            DownloadCover = settings.DownloadCover;
            DownloadMusic = settings.DownloadMusic;
            CheckVideoAudio = settings.CheckVideoAudio;
            EnablePersonDetection = settings.EnablePersonDetection;
            PersonDetectionConfidence = settings.PersonDetectionConfidence;
            StopOnDuplicateThreshold = settings.StopOnDuplicateThreshold;
            DuplicateStopThreshold = settings.DuplicateStopThreshold;
            SetTheme(settings.Theme.Equals("Dark", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _isApplyingSettings = false;
        }

        var pending = Interlocked.Exchange(ref _settingsSaveCts, null);
        pending?.Cancel();
        await _settingsService.SaveAsync(CreateSettingsSnapshot(), cancellationToken);
        AddLog("远程端设置已同步到桌面界面并保存到 settings.json。");
    }

    private void ApplyTheme()
    {
        if (Avalonia.Application.Current is { } app)
            app.RequestedThemeVariant = IsDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    private void ToggleHistory() => IsHistoryVisible = !IsHistoryVisible;

    private void OpenDownloadFolder()
    {
        try
        {
            var path = !string.IsNullOrWhiteSpace(CurrentAuthorDirectory)
                ? CurrentAuthorDirectory
                : DownloadRoot;
            _platformShell.OpenFolder(path);
        }
        catch (Exception ex)
        {
            AddLog($"打开目录失败：{ex.Message}");
        }
    }

    public async Task OpenHistoryHomeAsync(DownloadHistoryItem item)
    {
        if (IsCapturing || IsBusy)
        {
            AddLog("当前作者正在采集或下载。为避免切换页面打断当前任务，请先停止或等待任务完成后再查看其他作者主页。");
            return;
        }

        var url = ExtractFirstUrl(item.OriginalUrl);
        if (string.IsNullOrWhiteSpace(url))
        {
            AddLog($"历史记录中没有可用的作者主页地址：{item.UserName}");
            return;
        }

        try
        {
            await _browser.StartAsync(url, IsHeadlessMode);
            AddLog($"已打开作者主页：{item.UserName}");
        }
        catch (Exception ex)
        {
            AddLog($"打开作者主页失败：{ex.Message}");
        }
    }

    public void OpenHistoryFolder(DownloadHistoryItem item)
    {
        try
        {
            var path = item.FolderPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                path = Path.Combine(
                    DownloadRoot,
                    PlatformFolderHelper.GetFolderName(item.Platform),
                    FileNameHelper.BuildAuthorFolderName(item.UserName, item.UserId));
            }
            _platformShell.OpenFolder(path);
        }
        catch (Exception ex)
        {
            AddLog($"打开作者文件夹失败：{ex.Message}");
        }
    }

    public async Task RecollectHistoryAsync(DownloadHistoryItem item)
    {
        if (IsCapturing || IsBusy)
        {
            AddLog("当前已有任务运行，不能同时重新采集历史作者。");
            return;
        }

        var platform = Platforms.FirstOrDefault(x =>
            x.Id.Equals(item.Platform, StringComparison.OrdinalIgnoreCase)
            || (x.Id.Equals("douyin", StringComparison.OrdinalIgnoreCase)
                && item.Platform.Equals("Douyin", StringComparison.OrdinalIgnoreCase))
            || (x.Id.Equals("instagram", StringComparison.OrdinalIgnoreCase)
                && item.Platform.Equals("Instagram", StringComparison.OrdinalIgnoreCase))
            || (x.Id.Equals("tiktok", StringComparison.OrdinalIgnoreCase)
                && item.Platform.Equals("TikTok", StringComparison.OrdinalIgnoreCase))
            || (x.Id.Equals("pinterest", StringComparison.OrdinalIgnoreCase)
                && item.Platform.Equals("Pinterest", StringComparison.OrdinalIgnoreCase))
            || (x.Id.Equals("kuaishou", StringComparison.OrdinalIgnoreCase)
                && item.Platform.Equals("Kuaishou", StringComparison.OrdinalIgnoreCase))
            || (x.Id.Equals("weibo", StringComparison.OrdinalIgnoreCase)
                && item.Platform.Equals("Weibo", StringComparison.OrdinalIgnoreCase))
            || (x.Id.Equals("meipian", StringComparison.OrdinalIgnoreCase)
                && item.Platform.Equals("Meipian", StringComparison.OrdinalIgnoreCase)));
        if (platform is not null)
            SelectedPlatform = platform;

        await OpenHistoryHomeAsync(item);
        if (_browser.IsStarted)
            await StartCaptureAsync();
    }

    public string GetHistoryFolderPath(DownloadHistoryItem item)
    {
        var path = item.FolderPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(
                DownloadRoot,
                PlatformFolderHelper.GetFolderName(item.Platform),
                FileNameHelper.BuildAuthorFolderName(item.UserName, item.UserId));
        }

        return Path.GetFullPath(path);
    }

    public async Task RemoveHistoryAsync(DownloadHistoryItem item, bool deleteDiskFiles = false)
    {
        try
        {
            string? authorFolder = null;
            var diskFolderDeleted = false;
            var diskFolderMissing = false;

            if (deleteDiskFiles)
            {
                authorFolder = GetHistoryFolderPath(item);
                if ((IsCapturing || IsBusy)
                    && !string.IsNullOrWhiteSpace(CurrentAuthorDirectory)
                    && PathsEqual(authorFolder, CurrentAuthorDirectory))
                {
                    throw new InvalidOperationException("当前作者正在采集或下载，请先停止任务后再删除磁盘文件。");
                }

                EnsureSafeAuthorFolderForDeletion(authorFolder);
                if (Directory.Exists(authorFolder))
                {
                    Directory.Delete(authorFolder, recursive: true);
                    diskFolderDeleted = true;
                }
                else
                {
                    diskFolderMissing = true;
                }
            }

            await _historyService.RemoveAsync(item.Id);

            if (diskFolderDeleted)
            {
                AddLog($"已移除作者历史并删除磁盘文件：{item.UserName}（{authorFolder}）。");
            }
            else if (diskFolderMissing)
            {
                AddLog($"已移除作者历史：{item.UserName}。磁盘目录不存在：{authorFolder}。");
            }
            else
            {
                AddLog($"已从下载历史移除：{item.UserName}。作者文件未删除。");
            }
        }
        catch (Exception ex)
        {
            AddLog($"移除历史记录失败：{ex.Message}");
        }
    }

    private void EnsureSafeAuthorFolderForDeletion(string authorFolder)
    {
        var fullPath = Path.GetFullPath(authorFolder);
        var pathRoot = Path.GetPathRoot(fullPath);
        var appRoot = Path.GetFullPath(AppContext.BaseDirectory);
        var currentDownloadRoot = Path.GetFullPath(DownloadRoot);
        var leafName = Path.GetFileName(fullPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));

        if (string.IsNullOrWhiteSpace(fullPath)
            || string.IsNullOrWhiteSpace(leafName)
            || (!string.IsNullOrWhiteSpace(pathRoot) && PathsEqual(fullPath, pathRoot))
            || PathsEqual(fullPath, appRoot)
            || PathsEqual(fullPath, currentDownloadRoot))
        {
            throw new InvalidOperationException("为避免误删，不能删除磁盘根目录、程序目录或下载根目录。");
        }
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            NormalizePath(left),
            NormalizePath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var pathRoot = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrWhiteSpace(pathRoot)
            && string.Equals(
                fullPath,
                pathRoot,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return pathRoot;
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public void MoveHistoryItemPreview(DownloadHistoryItem source, int targetIndex)
    {
        var sourceIndex = DownloadHistory.IndexOf(source);
        var filteredSourceIndex = FilteredDownloadHistory.IndexOf(source);
        if (sourceIndex < 0
            || filteredSourceIndex < 0
            || DownloadHistory.Count == 0
            || FilteredDownloadHistory.Count == 0)
        {
            return;
        }

        var filteredTargetIndex = Math.Clamp(targetIndex, 0, FilteredDownloadHistory.Count - 1);
        var targetItem = FilteredDownloadHistory[filteredTargetIndex];
        var fullTargetIndex = DownloadHistory.IndexOf(targetItem);
        if (fullTargetIndex < 0 || sourceIndex == fullTargetIndex)
            return;

        // 搜索时按照当前可见结果排序，同时保留未匹配项目在完整历史中的相对顺序。
        DownloadHistory.Move(sourceIndex, fullTargetIndex);
        if (filteredSourceIndex != filteredTargetIndex)
            FilteredDownloadHistory.Move(filteredSourceIndex, filteredTargetIndex);

        // 预览顺序变化时立即同步 SortOrder，释放鼠标后持久化到 exe 根目录的 History.json。
        for (var index = 0; index < DownloadHistory.Count; index++)
            DownloadHistory[index].SortOrder = index;
    }

    public async Task PersistHistoryOrderAsync()
    {
        try
        {
            await _historyService.SetOrderAsync(DownloadHistory.Select(x => x.Id).ToArray());
        }
        catch (Exception ex)
        {
            AddLog($"保存历史排序失败：{ex.Message}");
        }
    }

    private void OnBrowserStateChanged(object? sender, BrowserStateChangedEventArgs e)
    {
        Ui(() =>
        {

            if (!string.IsNullOrWhiteSpace(e.CurrentUrl))
            {
                CurrentUrl = e.CurrentUrl;
                SelectPlatformForUrl(e.CurrentUrl);
            }
            else if (!e.IsStarted && !IsHeadlessMode)
            {
                CurrentUrl = _localization.Get("Status.BrowserNotOpened", "尚未打开浏览器");
            }

            BrowserModeStatusText = GetBrowserModeStatusText(e.IsHeadless, e.IsLoginRecoveryActive);

            if (e.IsLoginRecoveryActive && IsCapturing)
            {
                AddLog("登录状态失效，正在停止当前采集并切换到显示模式扫码登录。");
                _coordinator.Stop();
            }

            if (IsCapturing)
                AddLog(e.Message);
            else
                StatusText = e.Message;
            RefreshCommands();
        });
    }

    private void OnCoordinatorLog(object? sender, string message)
        => Ui(() => AddLog(message));

    private void OnCoordinatorProgressChanged(object? sender, CrawlProgressSnapshot progress)
    {
        // 通知所需的数据直接在事件线程保存，避免 UI Dispatcher 队列尚未刷新时，
        // StartCaptureAsync 已经进入通知判断而误认为本次没有下载或没有作者。
        _currentTaskDownloadedCount = progress.DownloadedCount;
        if (!string.IsNullOrWhiteSpace(progress.CurrentAuthorId))
            _currentTaskAuthorId = progress.CurrentAuthorId;

        Ui(() => ApplyProgress(progress));
    }

    private void OnCoordinatorCompleted(object? sender, string message)
    {
        _lastCoordinatorCompletionMessage = message;
        Ui(() =>
        {
            StatusText = message;
            AddLog(message);
        });
    }

    private async Task FinalizeCaptureAsync(
        CrawlSessionResult result,
        string pushPlusToken,
        bool isUpdate)
    {
        try
        {
            var detectionResult = await result.PersonDetection.Completion;
            if (result.PersonDetectionEnabled)
            {
                if (detectionResult.CanceledCount > 0)
                {
                    AddLog(
                        $"后台人像检测已中断，仍有 {detectionResult.CanceledCount} 张 .pending 图片，" +
                        "程序下次启动会继续处理。");
                }
                else
                {
                    AddLog(
                        $"作者后台人像检测完成：排队 {detectionResult.QueuedCount} 张，" +
                        $"保留 {detectionResult.KeptCount} 张，删除 {detectionResult.DeletedCount} 张，" +
                        $"检测失败保留 {detectionResult.DetectionFailureCount} 张。 ");
                }
            }

            if (!string.IsNullOrWhiteSpace(result.AuthorId)
                && !string.IsNullOrWhiteSpace(result.AuthorFolder))
            {
                await _historyService.RefreshAuthorStatsAsync(
                    result.PlatformId,
                    result.AuthorId,
                    result.AuthorFolder,
                    CancellationToken.None);
            }

            if (ShouldSendPushPlusNotification(result, pushPlusToken, detectionResult))
            {
                await SendPushPlusDownloadCompletedAsync(
                    result,
                    pushPlusToken,
                    isUpdate);
            }
        }
        catch (Exception ex)
        {
            // 后台收尾失败不能影响已经开始的下一位作者采集。
            AddLog($"作者下载后台收尾失败：{ex.Message}");
        }
    }

    private static bool ShouldSendPushPlusNotification(
        CrawlSessionResult result,
        string pushPlusToken,
        PersonDetectionSessionResult detectionResult)
        => result.DownloadedWorkCount > 0
           && !string.IsNullOrWhiteSpace(pushPlusToken)
           && detectionResult.CanceledCount == 0
           && !string.Equals(
               result.CompletionMessage,
               "采集已停止",
               StringComparison.Ordinal);

    private async Task SendPushPlusDownloadCompletedAsync(
        CrawlSessionResult result,
        string pushPlusToken,
        bool isUpdate)
    {
        if (string.IsNullOrWhiteSpace(result.AuthorId))
        {
            AddLog("PushPlus 通知未发送：本次任务没有识别到作者 UID。");
            return;
        }

        try
        {
            var history = await _historyService.FindAuthorAsync(
                result.PlatformId,
                result.AuthorId,
                CancellationToken.None);
            if (history is null)
            {
                AddLog("PushPlus 通知未发送：History.json 中没有找到本次作者记录。");
                return;
            }

            await _pushPlusNotification.SendDownloadCompletedAsync(
                pushPlusToken,
                history,
                result.DownloadedWorkCount,
                isUpdate,
                CancellationToken.None);
            AddLog($"PushPlus 微信通知已发送：{history.UserName}（UID {history.UserId}）");
        }
        catch (Exception ex)
        {
            // 通知失败不能把已经完成的下载任务改成“采集失败”。
            AddLog($"PushPlus 微信通知发送失败：{ex.Message}");
        }
    }

    private void TrackBackgroundFinalization(Task task)
    {
        lock (_backgroundFinalizationGate)
            _backgroundFinalizationTasks.Add(task);

        _ = task.ContinueWith(
            completedTask =>
            {
                lock (_backgroundFinalizationGate)
                    _backgroundFinalizationTasks.Remove(completedTask);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RecoverPendingPersonDetectionAsync()
    {
        try
        {
            await _coordinator.RecoverPendingPersonDetectionAsync(
                DownloadRoot,
                PersonDetectionConfidence,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            AddLog($"恢复遗留人像检测任务失败：{ex.Message}");
        }
    }

    private void OnHistoryChanged(object? sender, IReadOnlyList<DownloadHistoryItem> items)
    {
        Ui(() =>
        {
            SyncHistory(items);
            _ = LoadHistoryAvatarsAsync(DownloadHistory.ToArray());
        });
    }

    private void ApplyProgress(CrawlProgressSnapshot progress)
    {
        ResponseCount = progress.ResponseCount;
        DiscoveredCount = progress.DiscoveredCount;
        DownloadedCount = progress.DownloadedCount;
        SkippedCount = progress.SkippedCount;
        FailedCount = progress.FailedCount;
        CurrentWork = string.IsNullOrWhiteSpace(progress.CurrentWork) ? "-" : progress.CurrentWork;
        IsDownloadProgressVisible = progress.IsDownloading;
        IsDownloadProgressIndeterminate = progress.IsDownloadIndeterminate;
        DownloadProgressPercent = progress.DownloadProgressPercent;
        DownloadProgressText = progress.DownloadProgressText ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(progress.CurrentAuthorDirectory))
            CurrentAuthorDirectory = progress.CurrentAuthorDirectory;
        if (!string.IsNullOrWhiteSpace(progress.CurrentAuthorName))
            CurrentAuthorName = progress.CurrentAuthorName;
        if (!string.IsNullOrWhiteSpace(progress.CurrentAuthorId))
            CurrentAuthorId = progress.CurrentAuthorId;
        if (!string.IsNullOrWhiteSpace(progress.CurrentAuthorAvatarUrl)
            && !progress.CurrentAuthorAvatarUrl.Equals(_currentAuthorAvatarUrl, StringComparison.Ordinal))
        {
            _currentAuthorAvatarUrl = progress.CurrentAuthorAvatarUrl;
            _ = LoadCurrentAuthorAvatarAsync(progress.CurrentAuthorAvatarUrl);
        }

        if (!string.IsNullOrWhiteSpace(progress.CurrentCoverUrl)
            && !progress.CurrentCoverUrl.Equals(_currentCoverUrl, StringComparison.Ordinal))
        {
            _currentCoverUrl = progress.CurrentCoverUrl;
            _ = LoadCurrentCoverAsync(progress.CurrentCoverUrl);
        }
    }

    private async Task LoadCurrentCoverAsync(string url)
    {
        var requestVersion = Interlocked.Increment(ref _coverRequestVersion);
        var image = await _imageCache.LoadAsync(url);
        if (requestVersion != Interlocked.Read(ref _coverRequestVersion) || image is null)
            return;

        Ui(() => CurrentCoverImage = image);
    }

    private async Task LoadCurrentAuthorAvatarAsync(string url)
    {
        var requestVersion = Interlocked.Increment(ref _authorAvatarRequestVersion);
        var image = await _imageCache.LoadAsync(url);
        if (requestVersion != Interlocked.Read(ref _authorAvatarRequestVersion) || image is null)
            return;

        Ui(() => CurrentAuthorAvatarImage = image);
    }

    private async Task LoadHistoryAvatarsAsync(IEnumerable<DownloadHistoryItem> items)
    {
        foreach (var item in items)
        {
            if (item.AvatarImage is not null || string.IsNullOrWhiteSpace(item.HeadUrl))
                continue;

            var image = await _imageCache.LoadAsync(item.HeadUrl);
            if (image is not null)
                Ui(() => item.AvatarImage = image);
        }
    }

    private void SyncHistory(IReadOnlyList<DownloadHistoryItem> items)
    {
        var ordered = items.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToArray();
        var validIds = ordered.Select(x => x.Id).ToHashSet();

        for (var index = DownloadHistory.Count - 1; index >= 0; index--)
        {
            if (!validIds.Contains(DownloadHistory[index].Id))
                DownloadHistory.RemoveAt(index);
        }

        for (var targetIndex = 0; targetIndex < ordered.Length; targetIndex++)
        {
            var incoming = ordered[targetIndex];
            var existing = DownloadHistory.FirstOrDefault(x => x.Id == incoming.Id);
            if (existing is null)
            {
                DownloadHistory.Insert(Math.Min(targetIndex, DownloadHistory.Count), incoming);
                continue;
            }

            CopyHistoryFields(existing, incoming);
            var currentIndex = DownloadHistory.IndexOf(existing);
            if (currentIndex != targetIndex)
                DownloadHistory.Move(currentIndex, targetIndex);
        }

        RefreshFilteredDownloadHistory();
    }

    private void RefreshFilteredDownloadHistory()
    {
        var keywords = HistorySearchText
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var matches = DownloadHistory
            .Where(item => keywords.Length == 0 || keywords.All(keyword => HistoryItemMatchesSearch(item, keyword)))
            .ToArray();

        FilteredDownloadHistory.Clear();
        foreach (var item in matches)
            FilteredDownloadHistory.Add(item);
    }

    private static bool HistoryItemMatchesSearch(DownloadHistoryItem item, string keyword)
    {
        return ContainsSearchKeyword(item.UserName, keyword)
               || ContainsSearchKeyword(item.UserId, keyword)
               || ContainsSearchKeyword(item.Platform, keyword)
               || ContainsSearchKeyword(item.PlatformDisplayText, keyword);
    }

    private static bool ContainsSearchKeyword(string? value, string keyword)
        => !string.IsNullOrWhiteSpace(value)
           && value.Contains(keyword, StringComparison.OrdinalIgnoreCase);

    private static void CopyHistoryFields(DownloadHistoryItem target, DownloadHistoryItem source)
    {
        var oldHeadUrl = target.HeadUrl;
        target.Platform = source.Platform;
        target.HeadUrl = source.HeadUrl;
        target.UserId = source.UserId;
        target.UserName = source.UserName;
        target.OriginalUrl = source.OriginalUrl;
        target.UpdatedAt = source.UpdatedAt;
        target.IsChecked = source.IsChecked;
        target.ItemsCount = source.ItemsCount;
        target.ItemsSize = source.ItemsSize;
        target.FolderPath = source.FolderPath;
        target.SortOrder = source.SortOrder;

        if (!oldHeadUrl.Equals(source.HeadUrl, StringComparison.Ordinal))
            target.AvatarImage = null;
    }

    private PersonDetectionModelInfo RefreshPersonDetectionModelStatus()
    {
        var modelInfo = _personImageDetector.GetModelInfo();
        PersonDetectionModelStatusText = BuildPersonDetectionModelStatusText(modelInfo);
        return modelInfo;
    }

    private static string BuildPersonDetectionModelStatusText(PersonDetectionModelInfo modelInfo)
        => modelInfo.IsFound
            ? $"已发现 YOLO 模型：{modelInfo.ModelName}\n位置：{modelInfo.ModelPath}"
            : "未发现 YOLO 模型。请将 person-detection.onnx、yolo11.onnx，或 yolo11 后带任意一个字母的 ONNX 模型放入程序根目录的 Models 文件夹。";

    private void AddPersonDetectionModelLog(PersonDetectionModelInfo modelInfo)
    {
        AddLog(modelInfo.IsFound
            ? $"已找到 YOLO 模型：{modelInfo.ModelName}，位置：{modelInfo.ModelPath}"
            : "未找到 YOLO 模型。人像检测开启时将跳过检测并保留图片；请检查程序根目录的 Models 文件夹。");
    }

    private void AddLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        // 状态文字现在直接进入日志；相邻的同一消息不重复插入。
        if (Logs.Count > 0 && string.Equals(Logs[0], message, StringComparison.Ordinal))
            return;

        _dailyFileLogWriter.Write(message);
        Logs.Insert(0, message);
        while (Logs.Count > 500)
            Logs.RemoveAt(Logs.Count - 1);
    }

    private bool CanStartCapture()
    {
        if (IsBusy || IsCapturing)
            return false;

        return _browser.IsStarted && !_browser.IsLoginRecoveryActive;
    }

    private void RefreshCommands()
    {
        OpenBrowserCommand.NotifyCanExecuteChanged();
        InstallChromiumCommand.NotifyCanExecuteChanged();
        InstallFfmpegCommand.NotifyCanExecuteChanged();
        ClearImageCacheCommand.NotifyCanExecuteChanged();
        StartCaptureCommand.NotifyCanExecuteChanged();
        StopCaptureCommand.NotifyCanExecuteChanged();
        OpenScheduledDownloadEditorCommand?.NotifyCanExecuteChanged();
    }

    private void SelectPlatformForUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var currentUri))
            return;

        var platform = Platforms.FirstOrDefault(option =>
        {
            if (!Uri.TryCreate(option.HomeUrl, UriKind.Absolute, out var homeUri))
                return false;

            return HostsBelongToSamePlatform(currentUri.Host, homeUri.Host);
        });

        if (platform is not null && !ReferenceEquals(platform, SelectedPlatform))
            SelectedPlatform = platform;
    }

    private static bool HostsBelongToSamePlatform(string firstHost, string secondHost)
    {
        var first = firstHost.Trim('.').ToLowerInvariant();
        var second = secondHost.Trim('.').ToLowerInvariant();
        if (first == second || first.EndsWith('.' + second) || second.EndsWith('.' + first))
            return true;

        static string RootDomain(string host)
        {
            var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? string.Join('.', parts[^2], parts[^1]) : host;
        }

        return RootDomain(first) == RootDomain(second);
    }

    private string NormalizeBrowserUrl(string? text)
    {
        var extracted = ExtractFirstUrl(text);
        if (!string.IsNullOrWhiteSpace(extracted))
            return extracted;

        var candidate = text?.Trim();
        if (string.IsNullOrWhiteSpace(candidate) || IsBrowserUrlPlaceholder(candidate))
            throw new InvalidOperationException("无头模式下请在“当前页面”文本框输入目标 URL。");

        if (!candidate.Contains("://", StringComparison.Ordinal))
            candidate = "https://" + candidate.TrimStart('/');

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("请输入有效的 HTTP 或 HTTPS URL。");
        }

        return uri.ToString();
    }

    private string ResolveInitialBrowserUrl(
        string? savedUrl,
        PlatformOption platform,
        bool headless)
    {
        if (!string.IsNullOrWhiteSpace(savedUrl) && !IsBrowserUrlPlaceholder(savedUrl))
            return savedUrl;


        return headless ? platform.HomeUrl : _localization.Get("Status.BrowserNotOpened", "尚未打开浏览器");
    }

    private bool ShouldReplaceUrlForPlatformChange(string currentUrl, PlatformOption previousPlatform)
        => IsBrowserUrlPlaceholder(currentUrl)
           || currentUrl.Equals(previousPlatform.HomeUrl, StringComparison.OrdinalIgnoreCase);

    private bool IsBrowserUrlPlaceholder(string? value)
        => string.IsNullOrWhiteSpace(value)
           || value.Equals("尚未打开浏览器", StringComparison.Ordinal)
           || value.Equals(_localization.Get("Status.BrowserNotOpened", "尚未打开浏览器"), StringComparison.Ordinal);

    private string GetBrowserModeStatusText(bool isHeadless, bool isLoginRecoveryActive)
    {
        if (isLoginRecoveryActive)
            return _localization.Get("Status.BrowserLoginRecovery", "临时显示模式：等待扫码登录；成功后自动恢复无头 1440×900");

        return isHeadless
            ? _localization.Get("Status.BrowserHeadless", "无头模式：固定视口 1440×900")
            : _localization.Get("Status.BrowserVisible", "显示模式：使用 NoViewport，跟随浏览器窗口尺寸");
    }

    private static string BuildAuthorHistoryKey(string? platform, string? userId)
        => $"{platform?.Trim()}:{userId?.Trim()}";

    private string ResolveDownloadRoot(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return GetDefaultDownloadRoot();

        try
        {
            Directory.CreateDirectory(configuredPath);
            return Path.GetFullPath(configuredPath);
        }
        catch
        {
            return GetDefaultDownloadRoot();
        }
    }

    private void ReloadLanguages()
    {
        var browserWasPlaceholder = IsBrowserUrlPlaceholder(CurrentUrl);
        var statusWasReady = StatusText.Equals(
            _localization.Get("Status.Ready", "准备就绪"),
            StringComparison.Ordinal);
        _localization.Reload();
        ApplyScheduledDownloadCulture(_localization.CurrentLanguageCode);
        OnPropertyChanged(nameof(LanguageOptions));
        _selectedLanguage = _localization.Languages.FirstOrDefault(language =>
                                language.Code.Equals(_localization.CurrentLanguageCode, StringComparison.OrdinalIgnoreCase))
                            ?? _localization.Languages.First();
        OnPropertyChanged(nameof(SelectedLanguage));
        if (browserWasPlaceholder)
            CurrentUrl = _localization.Get("Status.BrowserNotOpened", "尚未打开浏览器");
        if (statusWasReady)
            StatusText = _localization.Get("Status.Ready", "准备就绪");
        RefreshLocalizedUi();
        AddLog(_localization.Format("Status.LanguageReloaded", _localization.Languages.Count));
        QueueSettingsSave();
    }

    private void RefreshPlatformDisplayNames()
    {
        foreach (var platform in Platforms)
        {
            platform.SetDisplayName(_localization.Get(
                $"Platform.{platform.Id}",
                platform.OriginalDisplayName));
        }
    }

    private void RefreshLocalizedUi()
    {
        RefreshPlatformDisplayNames();
        BrowserModeStatusText = GetBrowserModeStatusText(IsHeadlessMode, _browser.IsLoginRecoveryActive);
        OnPropertyChanged(nameof(LanguageDirectoryText));
        OnPropertyChanged(nameof(InstallChromiumButtonText));
        OnPropertyChanged(nameof(InstallFfmpegButtonText));
        OnPropertyChanged(nameof(ClearImageCacheButtonText));
        OnPropertyChanged(nameof(ImageCachePathText));
        OnPropertyChanged(nameof(OpenBrowserButtonText));
        OnPropertyChanged(nameof(ThemeButtonText));
        OnPropertyChanged(nameof(HistoryButtonText));
        OnPropertyChanged(nameof(HistorySearchPlaceholderText));
        RefreshScheduledDownloadLocalizedText();
        foreach (var item in DownloadHistory)
            item.RefreshLocalizedText();
        RefreshFilteredDownloadHistory();
    }

    private AppSettings CreateSettingsSnapshot()
        => new()
        {
            Theme = IsDarkTheme ? "Dark" : "Light",
            LanguageCode = SelectedLanguage.Code,
            SelectedPlatformId = SelectedPlatform.Id,
            HeadlessMode = IsHeadlessMode,
            LastBrowserUrl = CurrentUrl,
            DownloadRoot = DownloadRoot,
            IncludeWorkId = IncludeWorkId,
            DownloadCover = DownloadCover,
            DownloadMusic = DownloadMusic,
            DownloadSpeedLimitMBps = DownloadSpeedLimitMBps,
            CheckVideoAudio = CheckVideoAudio,
            EnablePersonDetection = EnablePersonDetection,
            PersonDetectionConfidence = PersonDetectionConfidence,
            StopOnDuplicateThreshold = StopOnDuplicateThreshold,
            DuplicateStopThreshold = DuplicateStopThreshold,
            PushPlusToken = PushPlusToken,
            RemoteApiEnabled = RemoteApiEnabled,
            RemoteApiPort = RemoteApiPort,
            RemoteApiToken = RemoteApiToken
        };

    private void QueueSettingsSave()
    {
        if (_isApplyingSettings || _isDisposed)
            return;

        var next = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _settingsSaveCts, next);
        previous?.Cancel();
        _ = SaveSettingsAfterDelayAsync(next);
    }

    private async Task SaveSettingsAfterDelayAsync(CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(250, source.Token);
            await _settingsService.SaveAsync(CreateSettingsSnapshot(), source.Token);
        }
        catch (OperationCanceledException)
        {
            // 用户连续修改多个设置时，只保存最后一次结果。
        }
        catch (Exception ex)
        {
            Ui(() => AddLog($"保存 settings.json 失败：{ex.Message}"));
        }
        finally
        {
            Interlocked.CompareExchange(ref _settingsSaveCts, null, source);
            source.Dispose();
        }
    }

    private async Task FlushSettingsAsync()
    {
        var pending = Interlocked.Exchange(ref _settingsSaveCts, null);
        pending?.Cancel();
        await _settingsService.SaveAsync(CreateSettingsSnapshot());
    }

    private static string GetDefaultDownloadRoot()
    {
        var preferred = Path.Combine(AppContext.BaseDirectory, "Download");
        try
        {
            Directory.CreateDirectory(preferred);
            return preferred;
        }
        catch
        {
            // 某些系统将程序安装目录设为只读；此时回退到用户下载目录，避免启动失败。
            var fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads",
                "HelloCrab");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    private static string? ExtractFirstUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (Uri.TryCreate(text.Trim(), UriKind.Absolute, out var direct)
            && direct.Scheme is "http" or "https")
        {
            return direct.ToString();
        }

        var match = Regex.Match(text, @"https?://[^\s]+", RegexOptions.IgnoreCase);
        return match.Success
            ? match.Value.TrimEnd('。', '，', ',', '.', '；', ';', ')', '）', ']', '】')
            : null;
    }

    private static void Ui(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    public async ValueTask DisposeAsync()
    {
        _isDisposed = true;
        await DisposeScheduledDownloadFeatureAsync();
        try
        {
            await FlushSettingsAsync();
        }
        catch (Exception ex)
        {
            AddLog($"保存 settings.json 失败：{ex.Message}");
        }

        _browser.StateChanged -= OnBrowserStateChanged;
        _coordinator.Log -= OnCoordinatorLog;
        _coordinator.ProgressChanged -= OnCoordinatorProgressChanged;
        _coordinator.Completed -= OnCoordinatorCompleted;
        _historyService.HistoryChanged -= OnHistoryChanged;
        await _coordinator.DisposeAsync();

        Task[] backgroundTasks;
        lock (_backgroundFinalizationGate)
            backgroundTasks = _backgroundFinalizationTasks.ToArray();
        if (backgroundTasks.Length > 0)
            await Task.WhenAll(backgroundTasks);

        await _browser.DisposeAsync();
        _pushPlusNotification.Dispose();
        _imageCache.Dispose();
    }
}
