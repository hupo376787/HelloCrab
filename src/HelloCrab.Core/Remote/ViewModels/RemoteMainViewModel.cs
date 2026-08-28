using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HelloCrab.Core.Contracts;
using HelloCrab.Core.Remote.Services;

namespace HelloCrab.Core.Remote.ViewModels;

public sealed class RemoteMainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly RemoteCrawlerClient _client;
    private readonly IRemoteClientPreferencesStore _clientPreferencesStore;
    private CancellationTokenSource? _pollingCts;
    private string _serverAddress = OperatingSystem.IsAndroid() || OperatingSystem.IsIOS()
        ? string.Empty
        : "http://127.0.0.1:5088";
    private string _accessToken = string.Empty;
    private bool _isAccessTokenVisible;
    private string _connectionText = "尚未连接";
    private bool _isConnected;
    private bool _isConnecting;
    private bool _hostIsBusy;
    private bool _isCapturing;
    private bool _isBrowserStarted;
    private string _statusText = "-";
    private string _currentUrl = "-";
    private string _currentWork = "-";
    private bool _isDownloadProgressVisible;
    private bool _isDownloadProgressIndeterminate;
    private double _downloadProgressPercent;
    private string _downloadProgressText = string.Empty;
    private string _currentAuthor = "-";
    private int _responseCount;
    private int _discoveredCount;
    private int _downloadedCount;
    private int _skippedCount;
    private int _failedCount;
    private string _theme = "Dark";
    private string _selectedPlatformId = "douyin";
    private bool _headlessMode;
    private string _browserUrl = string.Empty;
    private string _downloadRoot = string.Empty;
    private bool _downloadVideo = true;
    private bool _downloadImage = true;
    private bool _includeWorkId;
    private bool _downloadCover;
    private bool _downloadMusic;
    private bool _downloadLivePhoto;
    private bool _checkVideoAudio;
    private bool _enablePersonDetection;
    private double _personDetectionConfidence = 0.60;
    private bool _stopOnDuplicateThreshold = true;
    private int _duplicateStopThreshold = 20;
    private bool _applyingSnapshot;
    private bool _settingsDirty;
    private string _settingsSyncText = "连接桌面客户端后会自动加载 settings.json。";
    private bool _isRemoteDarkTheme = true;
    private IImage? _currentCoverImage;
    private string? _loadedCoverKey;
    private string? _loadingCoverKey;
    private long _coverRequestVersion;
    private readonly HashSet<int> _loadingHistoryAvatarIds = new();
    private readonly Dictionary<int, DateTimeOffset> _historyAvatarRetryAfter = new();
    private bool _isUserScrolling;
    private RemoteCrawlerSnapshot? _pendingVisualSnapshot;

    public RemoteMainViewModel(RemoteCrawlerClient client)
    {
        _client = client;
        _clientPreferencesStore = RemoteClientPreferencesStoreProvider.Current;
        LoadClientPreferences();

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsConnecting);
        InstallChromiumCommand = new AsyncRelayCommand(() => ExecuteActionAsync("install-chromium"), CanRunHostAction);
        OpenBrowserCommand = new AsyncRelayCommand(() => ExecuteActionAsync("open-browser"), CanRunHostAction);
        StartCaptureCommand = new AsyncRelayCommand(() => ExecuteActionAsync("start"), () => IsConnected && IsBrowserStarted && !HostIsBusy && !IsCapturing);
        StopCaptureCommand = new AsyncRelayCommand(() => ExecuteActionAsync("stop"), () => IsConnected && IsCapturing);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync, () => IsConnected && !IsConnecting);
        ToggleRemoteThemeCommand = new RelayCommand(ToggleRemoteTheme);
        ToggleAccessTokenVisibilityCommand = new RelayCommand(ToggleAccessTokenVisibility);
        ApplyRemoteTheme();
    }

    public bool IsNativeMobileClient => OperatingSystem.IsAndroid() || OperatingSystem.IsIOS();

    public bool IsBrowserClient => !IsNativeMobileClient;

    public IReadOnlyList<string> ThemeOptions { get; } = new[] { "Light", "Dark" };
    public IReadOnlyList<string> PlatformOptions { get; } = new[] { "douyin", "tiktok", "kuaishou", "xiaohongshu", "weibo", "meipian", "instagram", "bilibili" };
    public ObservableCollection<string> Logs { get; } = new();
    public ObservableCollection<RemoteHistoryItemViewModel> History { get; } = new();

    public IAsyncRelayCommand ConnectCommand { get; }
    public IAsyncRelayCommand InstallChromiumCommand { get; }
    public IAsyncRelayCommand OpenBrowserCommand { get; }
    public IAsyncRelayCommand StartCaptureCommand { get; }
    public IAsyncRelayCommand StopCaptureCommand { get; }
    public IAsyncRelayCommand SaveSettingsCommand { get; }
    public IRelayCommand ToggleRemoteThemeCommand { get; }
    public IRelayCommand ToggleAccessTokenVisibilityCommand { get; }

    public string ServerAddress { get => _serverAddress; set => SetProperty(ref _serverAddress, value); }
    public string AccessToken { get => _accessToken; set => SetProperty(ref _accessToken, value); }

    public bool IsAccessTokenVisible
    {
        get => _isAccessTokenVisible;
        private set
        {
            if (SetProperty(ref _isAccessTokenVisible, value))
            {
                OnPropertyChanged(nameof(IsAccessTokenHidden));
                OnPropertyChanged(nameof(AccessTokenVisibilityToolTip));
            }
        }
    }

    public bool IsAccessTokenHidden => !IsAccessTokenVisible;

    public string AccessTokenVisibilityToolTip => IsAccessTokenVisible
        ? "隐藏访问令牌"
        : "显示访问令牌";
    public string ConnectionText { get => _connectionText; private set => SetProperty(ref _connectionText, value); }
    public string SettingsSyncText { get => _settingsSyncText; private set => SetProperty(ref _settingsSyncText, value); }
    public string ConnectionHint => OperatingSystem.IsAndroid() || OperatingSystem.IsIOS()
        ? "手机端不能使用 127.0.0.1 或 localhost，请填写桌面端“远程控制服务器”状态中显示的局域网地址。"
        : "手机浏览器访问时同样不能使用 127.0.0.1，请填写桌面端显示的局域网地址。";

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
                RefreshCommands();
        }
    }

    public bool IsConnecting
    {
        get => _isConnecting;
        private set
        {
            if (SetProperty(ref _isConnecting, value))
                RefreshCommands();
        }
    }

    public bool HostIsBusy
    {
        get => _hostIsBusy;
        private set
        {
            if (SetProperty(ref _hostIsBusy, value))
                RefreshCommands();
        }
    }

    public bool IsCapturing
    {
        get => _isCapturing;
        private set
        {
            if (SetProperty(ref _isCapturing, value))
            {
                RefreshCommands();
                OnPropertyChanged(nameof(HasCurrentCoverBackground));
                OnPropertyChanged(nameof(IsGradientBackgroundVisible));
                OnPropertyChanged(nameof(ShowDecorativeGlows));
            }
        }
    }

    public bool IsBrowserStarted
    {
        get => _isBrowserStarted;
        private set
        {
            if (SetProperty(ref _isBrowserStarted, value))
                RefreshCommands();
        }
    }

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string CurrentUrl { get => _currentUrl; private set => SetProperty(ref _currentUrl, value); }
    public string CurrentWork { get => _currentWork; private set => SetProperty(ref _currentWork, value); }
    public bool IsDownloadProgressVisible { get => _isDownloadProgressVisible; private set => SetProperty(ref _isDownloadProgressVisible, value); }
    public bool IsDownloadProgressIndeterminate { get => _isDownloadProgressIndeterminate; private set => SetProperty(ref _isDownloadProgressIndeterminate, value); }
    public double DownloadProgressPercent { get => _downloadProgressPercent; private set => SetProperty(ref _downloadProgressPercent, value); }
    public string DownloadProgressText { get => _downloadProgressText; private set => SetProperty(ref _downloadProgressText, value); }
    public string CurrentAuthor { get => _currentAuthor; private set => SetProperty(ref _currentAuthor, value); }
    public int ResponseCount { get => _responseCount; private set => SetProperty(ref _responseCount, value); }
    public int DiscoveredCount { get => _discoveredCount; private set => SetProperty(ref _discoveredCount, value); }
    public int DownloadedCount { get => _downloadedCount; private set => SetProperty(ref _downloadedCount, value); }
    public int SkippedCount { get => _skippedCount; private set => SetProperty(ref _skippedCount, value); }
    public int FailedCount { get => _failedCount; private set => SetProperty(ref _failedCount, value); }

    public IImage? CurrentCoverImage
    {
        get => _currentCoverImage;
        private set
        {
            if (SetProperty(ref _currentCoverImage, value))
            {
                OnPropertyChanged(nameof(HasCurrentCoverBackground));
                OnPropertyChanged(nameof(IsGradientBackgroundVisible));
                OnPropertyChanged(nameof(ShowDecorativeGlows));
            }
        }
    }

    // Android/iOS 上的大图全屏模糊会明显增加 GPU 合成与重绘开销。
    // 原生手机端固定使用渐变背景；Browser/Desktop 远程端仍保留作品封面背景。
    public bool HasCurrentCoverBackground
        => !IsNativeMobileClient && IsCapturing && CurrentCoverImage is not null;

    public bool IsGradientBackgroundVisible => !HasCurrentCoverBackground;

    public bool ShowDecorativeGlows
        => !IsNativeMobileClient && IsGradientBackgroundVisible;

    public bool IsRemoteDarkTheme
    {
        get => _isRemoteDarkTheme;
        private set
        {
            if (SetProperty(ref _isRemoteDarkTheme, value))
            {
                OnPropertyChanged(nameof(IsRemoteLightTheme));
                OnPropertyChanged(nameof(RemoteThemeButtonText));
            }
        }
    }

    public bool IsRemoteLightTheme => !IsRemoteDarkTheme;

    public string RemoteThemeButtonText => IsRemoteDarkTheme ? "亮色" : "暗色";

    public string Theme
    {
        get => _theme;
        set { if (SetProperty(ref _theme, value)) MarkSettingsDirty(); }
    }

    public string SelectedPlatformId
    {
        get => _selectedPlatformId;
        set { if (SetProperty(ref _selectedPlatformId, value)) MarkSettingsDirty(); }
    }

    public bool HeadlessMode
    {
        get => _headlessMode;
        set { if (SetProperty(ref _headlessMode, value)) MarkSettingsDirty(); }
    }

    public string BrowserUrl
    {
        get => _browserUrl;
        set { if (SetProperty(ref _browserUrl, value)) MarkSettingsDirty(); }
    }

    public string DownloadRoot
    {
        get => _downloadRoot;
        set { if (SetProperty(ref _downloadRoot, value)) MarkSettingsDirty(); }
    }

    public bool DownloadVideo
    {
        get => _downloadVideo;
        set { if (SetProperty(ref _downloadVideo, value)) MarkSettingsDirty(); }
    }

    public bool DownloadImage
    {
        get => _downloadImage;
        set { if (SetProperty(ref _downloadImage, value)) MarkSettingsDirty(); }
    }

    public bool IncludeWorkId
    {
        get => _includeWorkId;
        set { if (SetProperty(ref _includeWorkId, value)) MarkSettingsDirty(); }
    }

    public bool DownloadCover
    {
        get => _downloadCover;
        set { if (SetProperty(ref _downloadCover, value)) MarkSettingsDirty(); }
    }

    public bool DownloadMusic
    {
        get => _downloadMusic;
        set { if (SetProperty(ref _downloadMusic, value)) MarkSettingsDirty(); }
    }

    public bool DownloadLivePhoto
    {
        get => _downloadLivePhoto;
        set { if (SetProperty(ref _downloadLivePhoto, value)) MarkSettingsDirty(); }
    }

    public bool CheckVideoAudio
    {
        get => _checkVideoAudio;
        set { if (SetProperty(ref _checkVideoAudio, value)) MarkSettingsDirty(); }
    }

    public bool EnablePersonDetection
    {
        get => _enablePersonDetection;
        set { if (SetProperty(ref _enablePersonDetection, value)) MarkSettingsDirty(); }
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
            MarkSettingsDirty();
        }
    }

    public string PersonDetectionConfidenceText
        => $"{PersonDetectionConfidence * 100:0}%";

    public bool StopOnDuplicateThreshold
    {
        get => _stopOnDuplicateThreshold;
        set { if (SetProperty(ref _stopOnDuplicateThreshold, value)) MarkSettingsDirty(); }
    }

    public int DuplicateStopThreshold
    {
        get => _duplicateStopThreshold;
        set
        {
            var clamped = Math.Clamp(value, 1, 10000);
            if (SetProperty(ref _duplicateStopThreshold, clamped))
                MarkSettingsDirty();
        }
    }

    private bool CanRunHostAction() => IsConnected && !HostIsBusy && !IsCapturing;

    private async Task ConnectAsync()
    {
        IsConnecting = true;
        StopPolling();
        try
        {
            ServerAddress = ServerAddress.Trim();
            AccessToken = AccessToken.Trim();
            SaveClientPreferences();

            _client.Configure(ServerAddress, AccessToken);
            await _client.GetHealthAsync();
            var snapshot = await _client.GetSnapshotAsync();
            ApplySnapshot(snapshot, loadSettings: true);
            IsConnected = true;
            ConnectionText = "已连接桌面客户端 · 已自动加载桌面设置";
            StartPolling();
        }
        catch (Exception ex)
        {
            IsConnected = false;
            ConnectionText = $"连接失败：{FormatConnectionError(ex)}";
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            var snapshot = await _client.GetSnapshotAsync();
            ApplySnapshot(snapshot, loadSettings: true);
            IsConnected = true;
            ConnectionText = $"已连接 · 已同步桌面设置 · {snapshot.ServerTime:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            IsConnected = false;
            ConnectionText = $"连接中断：{ex.Message}";
            StopPolling();
        }
    }

    private async Task ExecuteActionAsync(string action)
    {
        try
        {
            var result = await _client.ExecuteActionAsync(action);
            ConnectionText = result.Message;
            await Task.Delay(250);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ConnectionText = $"操作失败：{ex.Message}";
        }
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            var result = await _client.UpdateSettingsAsync(new RemoteSettingsDto
            {
                Theme = Theme,
                SelectedPlatformId = SelectedPlatformId,
                HeadlessMode = HeadlessMode,
                BrowserUrl = BrowserUrl,
                DownloadRoot = DownloadRoot,
                DownloadVideo = DownloadVideo,
                DownloadImage = DownloadImage,
                IncludeWorkId = IncludeWorkId,
                DownloadCover = DownloadCover,
                DownloadMusic = DownloadMusic,
                DownloadLivePhoto = DownloadLivePhoto,
                CheckVideoAudio = CheckVideoAudio,
                EnablePersonDetection = EnablePersonDetection,
                PersonDetectionConfidence = PersonDetectionConfidence,
                StopOnDuplicateThreshold = StopOnDuplicateThreshold,
                DuplicateStopThreshold = DuplicateStopThreshold
            });
            await RefreshAsync();
            ConnectionText = string.IsNullOrWhiteSpace(result.Message)
                ? "设置已保存，桌面客户端界面与 settings.json 已同步。"
                : result.Message;
        }
        catch (Exception ex)
        {
            ConnectionText = $"保存失败：{ex.Message}";
        }
    }

    private void ApplySnapshot(RemoteCrawlerSnapshot snapshot, bool loadSettings)
    {
        _applyingSnapshot = true;
        try
        {
            HostIsBusy = snapshot.IsBusy;
            IsCapturing = snapshot.IsCapturing;
            IsBrowserStarted = snapshot.IsBrowserStarted;
            StatusText = snapshot.StatusText;
            CurrentUrl = string.IsNullOrWhiteSpace(snapshot.CurrentUrl) ? "-" : snapshot.CurrentUrl;
            CurrentWork = string.IsNullOrWhiteSpace(snapshot.CurrentWork) ? "-" : snapshot.CurrentWork;
            IsDownloadProgressVisible = snapshot.IsDownloading;
            IsDownloadProgressIndeterminate = snapshot.IsDownloadIndeterminate;
            DownloadProgressPercent = snapshot.DownloadProgressPercent;
            DownloadProgressText = snapshot.DownloadProgressText ?? string.Empty;
            CurrentAuthor = string.IsNullOrWhiteSpace(snapshot.CurrentAuthorName)
                ? "-"
                : $"{snapshot.CurrentAuthorName} · {snapshot.CurrentAuthorId}";
            ResponseCount = snapshot.ResponseCount;
            DiscoveredCount = snapshot.DiscoveredCount;
            DownloadedCount = snapshot.DownloadedCount;
            SkippedCount = snapshot.SkippedCount;
            FailedCount = snapshot.FailedCount;

            if (loadSettings || !_settingsDirty)
            {
                Theme = snapshot.Settings.Theme;
                SelectedPlatformId = snapshot.Settings.SelectedPlatformId;
                HeadlessMode = snapshot.Settings.HeadlessMode;
                BrowserUrl = snapshot.Settings.BrowserUrl;
                DownloadRoot = snapshot.Settings.DownloadRoot;
                DownloadVideo = snapshot.Settings.DownloadVideo;
                DownloadImage = snapshot.Settings.DownloadImage;
                IncludeWorkId = snapshot.Settings.IncludeWorkId;
                DownloadCover = snapshot.Settings.DownloadCover;
                DownloadMusic = snapshot.Settings.DownloadMusic;
                DownloadLivePhoto = snapshot.Settings.DownloadLivePhoto;
                CheckVideoAudio = snapshot.Settings.CheckVideoAudio;
                EnablePersonDetection = snapshot.Settings.EnablePersonDetection;
                PersonDetectionConfidence = snapshot.Settings.PersonDetectionConfidence;
                StopOnDuplicateThreshold = snapshot.Settings.StopOnDuplicateThreshold;
                DuplicateStopThreshold = snapshot.Settings.DuplicateStopThreshold;
                _settingsDirty = false;
                SettingsSyncText = $"已自动加载桌面设置 · {snapshot.ServerTime:HH:mm:ss}";
            }

            if (_isUserScrolling)
            {
                // 惯性滚动期间只更新轻量状态，暂缓日志、历史列表和封面图片的
                // 集合变更/解码，避免轮询刷新打断 Android 的滚动帧。
                _pendingVisualSnapshot = snapshot;
            }
            else
            {
                ApplyVisualSnapshot(snapshot);
            }

        }
        finally
        {
            _applyingSnapshot = false;
        }
    }


    /// <summary>
    /// 由视图在手指/惯性滚动开始和结束时调用。滚动期间保留最新快照，
    /// 停止滚动后一次性应用，避免连续重建可视树。
    /// </summary>
    public void SetUserScrolling(bool isScrolling)
    {
        if (_isUserScrolling == isScrolling)
            return;

        _isUserScrolling = isScrolling;
        if (isScrolling)
            return;

        var pending = _pendingVisualSnapshot;
        _pendingVisualSnapshot = null;
        if (pending is not null)
            ApplyVisualSnapshot(pending);
    }

    private void ApplyVisualSnapshot(RemoteCrawlerSnapshot snapshot)
    {
        UpdateCoverBackground(snapshot);
        SyncLogs(snapshot.Logs);
        SyncRemoteHistory(snapshot.History);
    }

    private void SyncLogs(IReadOnlyList<string> items)
    {
        // 快照没有变化时不触碰集合。旧实现每次轮询都 Clear + Add 150 条，
        // 会让 Android 在滚动过程中反复创建文本控件并重新布局。
        if (Logs.Count == items.Count && Logs.SequenceEqual(items))
            return;

        // 日志按“最新在前”排列。通常每次只新增少量内容，因此找到旧首项
        // 在新快照中的位置，只插入新增项，再裁掉超出快照的尾部。
        if (Logs.Count > 0)
        {
            var oldFirst = Logs[0];
            var overlapIndex = -1;
            for (var index = 0; index < items.Count; index++)
            {
                if (string.Equals(items[index], oldFirst, StringComparison.Ordinal))
                {
                    overlapIndex = index;
                    break;
                }
            }

            if (overlapIndex >= 0)
            {
                for (var index = overlapIndex - 1; index >= 0; index--)
                    Logs.Insert(0, items[index]);

                while (Logs.Count > items.Count)
                    Logs.RemoveAt(Logs.Count - 1);

                if (Logs.Count == items.Count && Logs.SequenceEqual(items))
                    return;
            }
        }

        // 首次连接、日志截断或重复文本导致无法可靠对齐时，再执行完整同步。
        Logs.Clear();
        foreach (var log in items)
            Logs.Add(log);
    }

    private void SyncRemoteHistory(IReadOnlyList<RemoteHistoryItemDto> items)
    {
        var validIds = items.Select(item => item.Id).ToHashSet();

        for (var index = History.Count - 1; index >= 0; index--)
        {
            var existing = History[index];
            if (validIds.Contains(existing.Id))
                continue;

            History.RemoveAt(index);
            _loadingHistoryAvatarIds.Remove(existing.Id);
            _historyAvatarRetryAfter.Remove(existing.Id);
            existing.Dispose();
        }

        for (var targetIndex = 0; targetIndex < items.Count; targetIndex++)
        {
            var source = items[targetIndex];
            var existing = History.FirstOrDefault(item => item.Id == source.Id);
            if (existing is null)
            {
                existing = new RemoteHistoryItemViewModel(source);
                History.Insert(Math.Min(targetIndex, History.Count), existing);
            }
            else
            {
                var avatarChanged = existing.UpdateFrom(source);
                if (avatarChanged)
                    _historyAvatarRetryAfter.Remove(existing.Id);

                var currentIndex = History.IndexOf(existing);
                if (currentIndex != targetIndex)
                    History.Move(currentIndex, targetIndex);
            }

            QueueHistoryAvatarLoad(existing);
        }
    }

    private void QueueHistoryAvatarLoad(RemoteHistoryItemViewModel item)
    {
        if (item.AvatarImage is not null
            || string.IsNullOrWhiteSpace(item.HeadUrl)
            || _loadingHistoryAvatarIds.Contains(item.Id))
        {
            return;
        }

        if (_historyAvatarRetryAfter.TryGetValue(item.Id, out var retryAfter)
            && retryAfter > DateTimeOffset.UtcNow)
        {
            return;
        }

        _loadingHistoryAvatarIds.Add(item.Id);
        _ = LoadHistoryAvatarAsync(item, item.AvatarKey);
    }

    private async Task LoadHistoryAvatarAsync(
        RemoteHistoryItemViewModel item,
        string expectedAvatarKey)
    {
        Bitmap? bitmap = null;
        try
        {
            var bytes = await _client.GetHistoryAvatarAsync(item.Id);
            if (bytes is not { Length: > 0 })
            {
                await UiAsync(() =>
                    _historyAvatarRetryAfter[item.Id] = DateTimeOffset.UtcNow.AddSeconds(15));
                return;
            }

            using var input = new MemoryStream(bytes, writable: false);
            bitmap = new Bitmap(input);

            await UiAsync(() =>
            {
                var avatar = bitmap;
                if (avatar is null)
                    return;

                if (!History.Contains(item)
                    || !string.Equals(item.AvatarKey, expectedAvatarKey, StringComparison.Ordinal))
                {
                    return;
                }

                item.SetAvatar(avatar);
                bitmap = null;
                _historyAvatarRetryAfter.Remove(item.Id);
            });
        }
        catch
        {
            // 头像仅用于显示。桌面端头像尚未缓存或网络暂时失败时，
            // 保留默认占位，并在稍后的状态轮询中自动重试。
            await UiAsync(() =>
                _historyAvatarRetryAfter[item.Id] = DateTimeOffset.UtcNow.AddSeconds(30));
        }
        finally
        {
            bitmap?.Dispose();
            await UiAsync(() =>
                _loadingHistoryAvatarIds.Remove(item.Id));
        }
    }


    private void UpdateCoverBackground(RemoteCrawlerSnapshot snapshot)
    {
        if (IsNativeMobileClient)
        {
            if (CurrentCoverImage is not null || _loadingCoverKey is not null || _loadedCoverKey is not null)
                ClearCoverBackground();
            return;
        }

        var desiredKey = snapshot.IsCapturing
            ? snapshot.CurrentCoverUrl?.Trim()
            : null;

        if (string.IsNullOrWhiteSpace(desiredKey))
        {
            ClearCoverBackground();
            return;
        }

        if (CurrentCoverImage is not null
            && string.Equals(_loadedCoverKey, desiredKey, StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(_loadingCoverKey, desiredKey, StringComparison.Ordinal))
            return;

        _loadingCoverKey = desiredKey;
        var requestVersion = Interlocked.Increment(ref _coverRequestVersion);
        _ = LoadCoverBackgroundAsync(desiredKey, requestVersion);
    }

    private async Task LoadCoverBackgroundAsync(string coverKey, long requestVersion)
    {
        try
        {
            var bytes = await _client.GetCurrentCoverAsync();
            if (bytes is not { Length: > 0 })
            {
                Ui(() =>
                {
                    if (requestVersion == Interlocked.Read(ref _coverRequestVersion))
                        _loadingCoverKey = null;
                });
                return;
            }

            using var input = new MemoryStream(bytes, writable: false);
            var bitmap = new Bitmap(input);
            Ui(() =>
            {
                if (requestVersion != Interlocked.Read(ref _coverRequestVersion)
                    || !IsCapturing)
                {
                    bitmap.Dispose();
                    return;
                }

                var old = CurrentCoverImage as IDisposable;
                CurrentCoverImage = bitmap;
                _loadedCoverKey = coverKey;
                _loadingCoverKey = null;
                old?.Dispose();
            });
        }
        catch
        {
            // 封面背景只是视觉增强；加载失败时保留渐变背景，并在下一次轮询重试。
            Ui(() =>
            {
                if (requestVersion == Interlocked.Read(ref _coverRequestVersion))
                    _loadingCoverKey = null;
            });
        }
    }

    private void ClearCoverBackground()
    {
        Interlocked.Increment(ref _coverRequestVersion);
        _loadingCoverKey = null;
        _loadedCoverKey = null;
        var old = CurrentCoverImage as IDisposable;
        CurrentCoverImage = null;
        old?.Dispose();
    }

    private void MarkSettingsDirty()
    {
        if (_applyingSnapshot)
            return;

        _settingsDirty = true;
        SettingsSyncText = "有未保存修改；保存后会同步桌面界面并写入 settings.json。";
    }

    private static string FormatConnectionError(Exception exception)
    {
        if (exception is UnauthorizedAccessException)
            return exception.Message;

        if (exception is TimeoutException)
            return exception.Message;

        if (exception is HttpRequestException)
            return exception.Message;

        if (exception.Message.Contains("net_http_operation_started", StringComparison.OrdinalIgnoreCase))
            return "连接配置已变化，请重新点击一次连接。";

        return exception.Message;
    }


    private void ToggleAccessTokenVisibility()
        => IsAccessTokenVisible = !IsAccessTokenVisible;

    private void ToggleRemoteTheme()
    {
        IsRemoteDarkTheme = !IsRemoteDarkTheme;
        ApplyRemoteTheme();
        SaveClientPreferences();
    }

    private void LoadClientPreferences()
    {
        try
        {
            var preferences = _clientPreferencesStore.Load();
            if (!string.IsNullOrWhiteSpace(preferences.ServerAddress))
                _serverAddress = preferences.ServerAddress.Trim();

            _accessToken = preferences.AccessToken ?? string.Empty;
            _isRemoteDarkTheme = preferences.IsDarkTheme;
        }
        catch
        {
            // Persistence must never prevent the remote controller from opening.
        }
    }

    private void SaveClientPreferences()
    {
        try
        {
            _clientPreferencesStore.Save(new RemoteClientPreferences
            {
                ServerAddress = ServerAddress.Trim(),
                AccessToken = AccessToken.Trim(),
                IsDarkTheme = IsRemoteDarkTheme
            });
        }
        catch
        {
            // The controller remains usable when localStorage/private app storage is unavailable.
        }
    }

    private void ApplyRemoteTheme()
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = IsRemoteDarkTheme
                ? ThemeVariant.Dark
                : ThemeVariant.Light;
        }
    }

    private void StartPolling()
    {
        StopPolling();
        _pollingCts = new CancellationTokenSource();
        _ = PollAsync(_pollingCts.Token);
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var interval = IsNativeMobileClient
                    ? TimeSpan.FromSeconds(2.5)
                    : TimeSpan.FromSeconds(1.5);
                await Task.Delay(interval, cancellationToken);
                var snapshot = await _client.GetSnapshotAsync(cancellationToken);
                Ui(() =>
                {
                    ApplySnapshot(snapshot, loadSettings: false);
                    IsConnected = true;
                    ConnectionText = $"已连接 · {snapshot.ServerTime:HH:mm:ss}";
                });
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Ui(() =>
                {
                    IsConnected = false;
                    ConnectionText = $"连接中断：{ex.Message}";
                });
                break;
            }
        }
    }

    private void StopPolling()
    {
        var old = Interlocked.Exchange(ref _pollingCts, null);
        old?.Cancel();
        old?.Dispose();
    }

    private void RefreshCommands()
    {
        ConnectCommand.NotifyCanExecuteChanged();
        InstallChromiumCommand.NotifyCanExecuteChanged();
        OpenBrowserCommand.NotifyCanExecuteChanged();
        StartCaptureCommand.NotifyCanExecuteChanged();
        StopCaptureCommand.NotifyCanExecuteChanged();
        SaveSettingsCommand.NotifyCanExecuteChanged();
    }

    private static void Ui(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    private static Task UiAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                action();
                completion.TrySetResult(true);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        return completion.Task;
    }

    public ValueTask DisposeAsync()
    {
        StopPolling();
        ClearCoverBackground();
        foreach (var item in History)
            item.Dispose();
        History.Clear();
        _loadingHistoryAvatarIds.Clear();
        _historyAvatarRetryAfter.Clear();
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
