using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using HelloCrab.Core.Models;
using HelloCrab.Core.Services.Scheduling;
using ScheduleEditor.Localization;
using ScheduleEditor.Models;
using ScheduleEditor.Services;
using ScheduleEditor.ViewModels;

namespace HelloCrab.Core.ViewModels;

public sealed partial class MainWindowViewModel
{
    // 定时下载固定在后台以无头浏览器运行，不受主界面手动采集模式影响。
    // 登录失效时，PlaywrightBrowserService 仍会自动临时切换到显示模式。
    private const bool ScheduledDownloadHeadless = true;

    private ScheduleLocalizationService _scheduledDownloadLocalization = null!;
    private JsonScheduleStore _scheduledDownloadStore = null!;
    private ScheduleManager _scheduledDownloadManager = null!;
    private Task? _scheduledDownloadInitializationTask;
    private CancellationTokenSource? _scheduledBatchCts;
    private bool _isScheduledDownloadEditorVisible;
    private bool _isScheduledDownloadReady;
    private bool _isScheduledBatchRunning;
    private string _scheduledDownloadStatusText = string.Empty;

    public ScheduleEditorViewModel ScheduledDownloadEditor { get; private set; } = null!;

    public IRelayCommand OpenScheduledDownloadEditorCommand { get; private set; } = null!;

    public bool IsScheduledDownloadEditorVisible
    {
        get => _isScheduledDownloadEditorVisible;
        private set => SetProperty(ref _isScheduledDownloadEditorVisible, value);
    }

    public bool IsScheduledBatchRunning
    {
        get => _isScheduledBatchRunning;
        private set
        {
            if (SetProperty(ref _isScheduledBatchRunning, value))
            {
                OnPropertyChanged(nameof(CanStopCurrentTask));
                RefreshCommands();
            }
        }
    }

    public bool CanStopCurrentTask => IsCapturing || IsScheduledBatchRunning;

    public string ScheduledDownloadStatusText
    {
        get => _scheduledDownloadStatusText;
        private set => SetProperty(ref _scheduledDownloadStatusText, value);
    }

    private void InitializeScheduledDownloadFeature(string? languageCode)
    {
        _scheduledDownloadLocalization = new ScheduleLocalizationService("en-US");
        TryLoadScheduledDownloadJapaneseLanguage();
        ApplyScheduledDownloadEditorTextOverrides();
        ApplyScheduledDownloadCulture(languageCode);

        var settingsDirectory = Path.GetDirectoryName(_settingsService.SettingsPath);
        if (string.IsNullOrWhiteSpace(settingsDirectory))
            settingsDirectory = AppContext.BaseDirectory;

        _scheduledDownloadStore = new JsonScheduleStore(
            Path.Combine(settingsDirectory, "scheduled-download.json"));
        _scheduledDownloadManager = new ScheduleManager(
            _scheduledDownloadStore,
            new ScheduledDownloadFluentScheduleService(),
            ExecuteScheduledHistoryDownloadsAsync,
            ownsScheduler: true);

        _scheduledDownloadManager.ScheduleChanged += OnScheduledDownloadScheduleChanged;
        _scheduledDownloadManager.ExecutionStarted += OnScheduledDownloadExecutionStarted;
        _scheduledDownloadManager.ExecutionCompleted += OnScheduledDownloadExecutionCompleted;
        _scheduledDownloadManager.ExecutionFailed += OnScheduledDownloadExecutionFailed;
        _scheduledDownloadManager.ExecutionSkipped += OnScheduledDownloadExecutionSkipped;

        ScheduledDownloadEditor = new ScheduleEditorViewModel(_scheduledDownloadLocalization)
        {
            SaveHandler = SaveScheduledDownloadOptionsAsync,
            CancelHandler = CancelScheduledDownloadEditorAsync
        };
        ScheduledDownloadEditor.SetModeVisibility(
            showEverySeconds: false,
            showEveryMinutes: true,
            showEveryHours: true,
            showDaily: true,
            showWeekly: true,
            showMonthly: true,
            showCron: true);

        OpenScheduledDownloadEditorCommand = new RelayCommand(
            OpenScheduledDownloadEditor,
            CanOpenScheduledDownloadEditor);
        ScheduledDownloadStatusText = _localization.Get(
            "Schedule.Status.Initializing",
            "正在读取定时设置…");
    }

    private async Task InitializeScheduledDownloadAsync()
    {
        try
        {
            var hadSavedConfiguration = File.Exists(_scheduledDownloadStore.FilePath);
            var options = await _scheduledDownloadManager.InitializeAsync(
                new ScheduleOptions
                {
                    IsEnabled = false,
                    RepeatType = ScheduleRepeatType.Daily,
                    ExecutionTime = new TimeSpan(9, 30, 0)
                });

            await RunOnUiThreadAsync(() =>
            {
                ScheduledDownloadEditor.Load(options);
                _isScheduledDownloadReady = true;
                RefreshScheduledDownloadStatus();
                RefreshCommands();

                if (options.IsEnabled && _scheduledDownloadManager.NextRun is { } nextRun)
                {
                    AddLog(_localization.Format(
                        "Schedule.Log.Restored",
                        FormatScheduledRunTime(nextRun)));
                }
                else if (hadSavedConfiguration)
                {
                    AddLog(_localization.Get(
                        "Schedule.Log.Disabled",
                        "已读取定时自动下载设置，当前计划未启用。"));
                }

                return Task.CompletedTask;
            });
        }
        catch (Exception ex)
        {
            await RunOnUiThreadAsync(() =>
            {
                _isScheduledDownloadReady = true;
                ScheduledDownloadStatusText = _localization.Get(
                    "Schedule.Status.Disabled",
                    "定时自动下载未启用");
                AddLog(_localization.Format(
                    "Schedule.Log.ExecutionFailed",
                    ex.Message));
                RefreshCommands();
                return Task.CompletedTask;
            });
        }
    }

    private bool CanOpenScheduledDownloadEditor()
        => _isScheduledDownloadReady
           && !IsBusy
           && !IsCapturing
           && !IsScheduledBatchRunning;

    private void OpenScheduledDownloadEditor()
    {
        if (!CanOpenScheduledDownloadEditor())
            return;

        if (_scheduledDownloadManager.CurrentOptions is { } options)
            ScheduledDownloadEditor.Load(options);

        IsScheduledDownloadEditorVisible = true;
    }

    private Task CancelScheduledDownloadEditorAsync()
    {
        IsScheduledDownloadEditorVisible = false;
        return Task.CompletedTask;
    }

    private async Task SaveScheduledDownloadOptionsAsync(ScheduleOptions options)
    {
        await _scheduledDownloadManager.SaveAndApplyAsync(options);
        await RunOnUiThreadAsync(() =>
        {
            IsScheduledDownloadEditorVisible = false;
            RefreshScheduledDownloadStatus();

            if (options.IsEnabled && _scheduledDownloadManager.NextRun is { } nextRun)
            {
                AddLog(_localization.Format(
                    "Schedule.Log.Saved",
                    FormatScheduledRunTime(nextRun)));
            }
            else
            {
                AddLog(_localization.Get(
                    "Schedule.Log.Disabled",
                    "定时自动下载已关闭。"));
            }

            return Task.CompletedTask;
        });
    }

    private Task ExecuteScheduledHistoryDownloadsAsync(CancellationToken cancellationToken)
        => RunOnUiThreadAsync(() => ExecuteScheduledHistoryDownloadsOnUiThreadAsync(cancellationToken));

    private async Task ExecuteScheduledHistoryDownloadsOnUiThreadAsync(
        CancellationToken schedulerCancellationToken)
    {
        if (IsBusy
            || IsCapturing
            || IsScheduledBatchRunning
            || IsScheduledDownloadEditorVisible)
        {
            AddLog(_localization.Get(
                "Schedule.Log.BatchBusy",
                "定时自动下载到点，但当前已有任务运行，本次计划已跳过。"));
            return;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            schedulerCancellationToken);
        _scheduledBatchCts = linkedCts;
        using var stopRegistration = linkedCts.Token.Register(_coordinator.Stop);

        // 在第一次异步读取历史列表之前先占用任务状态，避免用户恰好在
        // 调度触发与历史读取之间手动启动另一个采集任务。
        IsScheduledBatchRunning = true;
        IsBusy = true;
        ScheduledDownloadStatusText = _localization.Get(
            "Schedule.Status.Running",
            "定时自动下载正在运行…");

        var completedCount = 0;
        var totalCount = 0;
        var stoppedForLogin = false;
        var issues = new List<BatchIssue>();
        try
        {
            var history = (await _historyService.LoadAsync(linkedCts.Token))
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.Id)
                .ToArray();
            totalCount = history.Length;

            if (history.Length == 0)
            {
                AddLog(_localization.Get(
                    "Schedule.Log.NoHistory",
                    "定时自动下载已触发，但历史列表为空。"));
                return;
            }

            AddLog(_localization.Format(
                "Schedule.Log.BatchStarted",
                history.Length));
            AddLog(_localization.Get(
                "Schedule.Log.HeadlessMode",
                "定时自动下载默认使用无头模式后台运行；仅登录失效时会临时显示浏览器窗口。"));

            for (var index = 0; index < history.Length; index++)
            {
                var item = history[index];
                linkedCts.Token.ThrowIfCancellationRequested();

                var platform = ResolveHistoryPlatform(item.Platform);
                if (platform is null)
                {
                    issues.Add(new BatchIssue(
                        index + 1,
                        BuildBatchIssueTarget(item.UserName, item.OriginalUrl),
                        BatchLocalizedText(
                            "Batch.Issue.UnsupportedUrl",
                            $"无法识别平台“{item.Platform}”",
                            $"Platform “{item.Platform}” could not be recognized",
                            $"プラットフォーム「{item.Platform}」を認識できません")));
                    AddLog(_localization.Format(
                        "Schedule.Log.ItemUnknownPlatform",
                        item.UserName,
                        item.Platform));
                    continue;
                }

                var url = ExtractFirstUrl(item.OriginalUrl);
                if (string.IsNullOrWhiteSpace(url))
                {
                    issues.Add(new BatchIssue(
                        index + 1,
                        item.UserName,
                        BatchLocalizedText(
                            "Batch.Issue.InvalidUrl",
                            "没有可用地址",
                            "No valid URL",
                            "有効な URL がありません")));
                    AddLog(_localization.Format(
                        "Schedule.Log.ItemInvalidUrl",
                        item.UserName));
                    continue;
                }

                SelectedPlatform = platform;
                CurrentUrl = url;
                AddLog(_localization.Format(
                    "Schedule.Log.ItemStarted",
                    item.UserName,
                    index + 1,
                    history.Length));

                await _browser.StartAsync(
                    url,
                    ScheduledDownloadHeadless,
                    linkedCts.Token);
                if (_browser.IsLoginRecoveryActive)
                {
                    issues.Add(new BatchIssue(
                        index + 1,
                        BuildBatchIssueTarget(item.UserName, url),
                        BatchLocalizedText(
                            "Batch.Issue.LoginRequired",
                            "登录失效，当前项及后续任务已暂停",
                            "Login expired; this and remaining items were paused",
                            "ログイン切れのため、この項目と残りの処理を停止しました")));
                    AddLog(_localization.Format(
                        "Schedule.Log.LoginRequired",
                        item.UserName));
                    stoppedForLogin = true;
                    break;
                }

                await StartCaptureAsync();
                if (string.IsNullOrWhiteSpace(_lastCaptureErrorMessage))
                {
                    completedCount++;
                }
                else
                {
                    issues.Add(new BatchIssue(
                        index + 1,
                        BuildBatchIssueTarget(item.UserName, url),
                        _lastCaptureErrorMessage));
                }
                linkedCts.Token.ThrowIfCancellationRequested();
            }

            if (!stoppedForLogin)
            {
                AddLog(_localization.Format(
                    "Schedule.Log.BatchCompleted",
                    completedCount,
                    history.Length));
            }

            AddBatchIssueSummary(issues);
        }
        catch (OperationCanceledException)
        {
            AddLog(_localization.Format(
                "Schedule.Log.BatchCanceled",
                completedCount,
                totalCount));
            AddBatchIssueSummary(issues);
        }
        catch (Exception ex)
        {
            issues.Add(new BatchIssue(
                completedCount + issues.Count + 1,
                BuildBatchIssueTarget(CurrentAuthorName, CurrentUrl),
                ex.Message));
            AddLog(_localization.Format(
                "Schedule.Log.ExecutionFailed",
                ex.Message));
            AddBatchIssueSummary(issues);
            throw;
        }
        finally
        {
            _scheduledBatchCts = null;
            IsScheduledBatchRunning = false;
            IsBusy = false;
            RefreshScheduledDownloadStatus();
            RefreshCommands();
        }
    }

    private PlatformOption? ResolveHistoryPlatform(string? platformName)
    {
        var normalized = platformName?.Trim().ToLowerInvariant() switch
        {
            "xhs" => "xiaohongshu",
            "小红书" => "xiaohongshu",
            "抖音" => "douyin",
            "快手" => "kuaishou",
            "快手live" => "kuaishou-live",
            "快手 live" => "kuaishou-live",
            "快手网页版live" => "kuaishou-live",
            "微博" => "weibo",
            "美篇" => "meipian",
            _ => platformName?.Trim().ToLowerInvariant()
        };

        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : Platforms.FirstOrDefault(platform =>
                platform.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    private void OnScheduledDownloadScheduleChanged(object? sender, EventArgs e)
        => Ui(RefreshScheduledDownloadStatus);

    private void OnScheduledDownloadExecutionStarted(
        object? sender,
        ScheduleExecutionEventArgs e)
    {
        Ui(() =>
        {
            ScheduledDownloadStatusText = _localization.Get(
                "Schedule.Status.Running",
                "定时自动下载正在运行…");
        });
    }

    private void OnScheduledDownloadExecutionCompleted(
        object? sender,
        ScheduleExecutionEventArgs e)
    {
        Ui(() =>
        {
            RefreshScheduledDownloadStatus();
            if (_scheduledDownloadManager.NextRun is { } nextRun)
            {
                AddLog(_localization.Format(
                    "Schedule.Log.NextRun",
                    FormatScheduledRunTime(nextRun)));
            }
        });
    }

    private void OnScheduledDownloadExecutionFailed(
        object? sender,
        ScheduleExecutionEventArgs e)
    {
        Ui(() =>
        {
            RefreshScheduledDownloadStatus();
            AddLog(_localization.Format(
                "Schedule.Log.ExecutionFailed",
                e.Exception?.Message ?? "Unknown error"));
        });
    }

    private void OnScheduledDownloadExecutionSkipped(
        object? sender,
        ScheduleExecutionEventArgs e)
    {
        Ui(() => AddLog(_localization.Get(
            "Schedule.Log.OverlapSkipped",
            "上一次定时自动下载尚未结束，本次重复触发已跳过。")));
    }

    private void RefreshScheduledDownloadStatus()
    {
        if (IsScheduledBatchRunning)
        {
            ScheduledDownloadStatusText = _localization.Get(
                "Schedule.Status.Running",
                "定时自动下载正在运行…");
            return;
        }

        if (_scheduledDownloadManager?.NextRun is { } nextRun)
        {
            ScheduledDownloadStatusText = _localization.Format(
                "Schedule.Status.NextRun",
                FormatScheduledRunTime(nextRun));
            return;
        }

        ScheduledDownloadStatusText = _localization.Get(
            "Schedule.Status.Disabled",
            "定时自动下载未启用");
    }

    private void RefreshScheduledDownloadLocalizedText()
    {
        if (_scheduledDownloadManager is null)
            return;

        RefreshScheduledDownloadStatus();
    }

    private void ApplyScheduledDownloadCulture(string? languageCode)
    {
        if (_scheduledDownloadLocalization is null)
            return;

        var culture = languageCode?.Trim().ToLowerInvariant() switch
        {
            { } code when code.StartsWith("zh", StringComparison.Ordinal) => "zh-CN",
            { } code when code.StartsWith("ja", StringComparison.Ordinal) => "ja-JP",
            _ => "en-US"
        };

        if (_scheduledDownloadLocalization.AvailableLanguages.Any(language =>
                language.Culture.Equals(culture, StringComparison.OrdinalIgnoreCase)))
        {
            _scheduledDownloadLocalization.SetCulture(culture);
        }

        RefreshScheduledDownloadLocalizedText();
    }

    private void TryLoadScheduledDownloadJapaneseLanguage()
    {
        try
        {
            var path = Path.Combine(
                AppContext.BaseDirectory,
                "ScheduleLanguages",
                "ja-JP.json");
            string? json = File.Exists(path)
                ? File.ReadAllText(path)
                : null;

            if (string.IsNullOrWhiteSpace(json))
            {
                var assembly = typeof(MainWindowViewModel).Assembly;
                var resourceName = assembly.GetManifestResourceNames()
                    .FirstOrDefault(name =>
                        name.Contains(".ScheduleLanguages.", StringComparison.Ordinal)
                        && name.EndsWith(".ja-JP.json", StringComparison.OrdinalIgnoreCase));
                if (resourceName is not null)
                {
                    using var stream = assembly.GetManifestResourceStream(resourceName);
                    if (stream is not null)
                    {
                        using var reader = new StreamReader(stream);
                        json = reader.ReadToEnd();
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(json))
            {
                _scheduledDownloadLocalization.AddOrUpdateLanguagePackFromJson(
                    json,
                    setCurrent: false);
            }
        }
        catch
        {
            // 日语扩展包不可读时回退到组件内置英语，不阻止主程序启动。
        }
    }

    private void ApplyScheduledDownloadEditorTextOverrides()
    {
        _scheduledDownloadLocalization.AddOrUpdateLanguageOverridesFromJson(
            "zh-CN",
            """
            {
              "Title": "定时自动下载",
              "Description": "设置自动重新采集历史列表的执行时间。计划只在 HelloCrab 运行期间执行。"
            }
            """);
        _scheduledDownloadLocalization.AddOrUpdateLanguageOverridesFromJson(
            "en-US",
            """
            {
              "Title": "Scheduled automatic downloads",
              "Description": "Choose when to collect every author in the history list again. The schedule runs only while HelloCrab is open."
            }
            """);

        if (_scheduledDownloadLocalization.AvailableLanguages.Any(language =>
                language.Culture.Equals("ja-JP", StringComparison.OrdinalIgnoreCase)))
        {
            _scheduledDownloadLocalization.AddOrUpdateLanguageOverridesFromJson(
                "ja-JP",
                """
                {
                  "Title": "定期自動ダウンロード",
                  "Description": "履歴リストの全作者を再収集する実行時刻を設定します。予定は HelloCrab の起動中のみ実行されます。"
                }
                """);
        }
    }

    private static string FormatScheduledRunTime(DateTimeOffset value)
        => value.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");

    private static Task RunOnUiThreadAsync(Func<Task> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return action();

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await action();
                completion.TrySetResult(true);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        return completion.Task;
    }

    private async Task DisposeScheduledDownloadFeatureAsync()
    {
        if (_scheduledDownloadManager is null)
            return;

        _scheduledBatchCts?.Cancel();
        _coordinator.Stop();
        IsScheduledDownloadEditorVisible = false;

        if (_scheduledDownloadInitializationTask is not null)
        {
            try
            {
                await _scheduledDownloadInitializationTask;
            }
            catch
            {
                // 初始化方法本身会记录失败；退出流程不再重复抛出。
            }
        }

        _scheduledDownloadManager.ScheduleChanged -= OnScheduledDownloadScheduleChanged;
        _scheduledDownloadManager.ExecutionStarted -= OnScheduledDownloadExecutionStarted;
        _scheduledDownloadManager.ExecutionCompleted -= OnScheduledDownloadExecutionCompleted;
        _scheduledDownloadManager.ExecutionFailed -= OnScheduledDownloadExecutionFailed;
        _scheduledDownloadManager.ExecutionSkipped -= OnScheduledDownloadExecutionSkipped;

        await Task.Run(_scheduledDownloadManager.Dispose);
        ScheduledDownloadEditor?.Dispose();
    }
}
