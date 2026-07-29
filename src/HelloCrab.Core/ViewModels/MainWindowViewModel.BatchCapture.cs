using Avalonia.Media;
using Avalonia.Threading;
using HelloCrab.Core.Models;

namespace HelloCrab.Core.ViewModels;

public sealed partial class MainWindowViewModel
{
    private CancellationTokenSource? _manualBatchCts;
    private bool _isManualBatchRunning;
    private bool _isManualBatchSkipRequested;
    private string? _retainedAuthorName;
    private string? _retainedAuthorAvatarUrl;
    private IImage? _retainedAuthorAvatarImage;

    public bool IsManualBatchRunning
    {
        get => _isManualBatchRunning;
        private set
        {
            if (SetProperty(ref _isManualBatchRunning, value))
            {
                if (!value)
                    IsManualBatchSkipRequested = false;

                OnPropertyChanged(nameof(CanStopCurrentTask));
                OnPropertyChanged(nameof(CanStartManualBatchCapture));
                RefreshCommands();
            }
        }
    }

    public bool IsManualBatchSkipRequested
    {
        get => _isManualBatchSkipRequested;
        private set => SetProperty(ref _isManualBatchSkipRequested, value);
    }

    public bool CanStartManualBatchCapture
        => !IsBusy
           && !IsCapturing
           && !IsScheduledBatchRunning
           && !IsManualBatchRunning;

    public async Task StartManualBatchCaptureAsync(string? fileContent)
    {
        if (!CanStartManualBatchCapture)
        {
            AddLog(BatchText("Batch.Log.Busy", "当前已有任务运行，无法开始批量采集。"));
            return;
        }

        var lines = (fileContent ?? string.Empty)
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        if (lines.Length == 0)
        {
            AddLog(BatchText("Batch.Log.Empty", "导入的文本文件没有有效地址；空行会被自动忽略。"));
            return;
        }

        using var cts = new CancellationTokenSource();
        _manualBatchCts = cts;
        using var stopRegistration = cts.Token.Register(_coordinator.Stop);

        IsManualBatchRunning = true;
        IsManualBatchSkipRequested = false;
        IsBusy = true;
        ClearCurrentAuthorDisplayForNextCapture();
        var completedCount = 0;
        var failedCount = 0;

        try
        {
            AddLog(BatchText("Batch.Log.Started", "批量采集开始，共读取到 {0} 个非空地址。", lines.Length));

            for (var index = 0; index < lines.Length; index++)
            {
                cts.Token.ThrowIfCancellationRequested();
                var sourceLine = lines[index];
                var url = ExtractFirstUrl(sourceLine);
                if (string.IsNullOrWhiteSpace(url))
                {
                    failedCount++;
                    AddLog(BatchText(
                        "Batch.Log.InvalidUrl",
                        "批量第 {0} 行没有可用地址，已跳过：{1}",
                        index + 1,
                        sourceLine));
                    continue;
                }

                var platform = ResolvePlatformForBatchUrl(url);
                if (platform is null)
                {
                    failedCount++;
                    AddLog(BatchText(
                        "Batch.Log.UnsupportedUrl",
                        "批量第 {0} 项无法识别所属平台，已跳过：{1}",
                        index + 1,
                        url));
                    continue;
                }

                try
                {
                    SelectedPlatform = platform;
                    CurrentUrl = url;
                    AddLog(BatchText(
                        "Batch.Log.ItemStarted",
                        "批量任务 {0}/{1}：正在处理 {2}，地址：{3}",
                        index + 1,
                        lines.Length,
                        platform.DisplayName,
                        url));

                    await _browser.StartAsync(url, IsHeadlessMode, cts.Token);
                    if (_browser.IsLoginRecoveryActive)
                    {
                        AddLog(BatchText(
                            "Batch.Log.LoginRequired",
                            "批量第 {0} 项（{1}）需要重新登录，已暂停后续任务。",
                            index + 1,
                            platform.DisplayName));
                        break;
                    }

                    IsManualBatchSkipRequested = false;
                    await StartCaptureAsync();

                    // “停止全部”优先级高于“跳过当前作者”。若两者同时发生，必须结束批量任务。
                    cts.Token.ThrowIfCancellationRequested();

                    if (IsManualBatchSkipRequested)
                    {
                        IsManualBatchSkipRequested = false;
                        failedCount++;
                        AddLog(BatchLocalizedText(
                            "Batch.Log.ItemSkipped",
                            "批量第 {0} 项已跳过，立即继续下一位作者。",
                            "Batch item {0} was skipped. Moving to the next author immediately.",
                            "一括処理の第 {0} 件をスキップし、すぐに次の作者へ進みます。",
                            index + 1));
                        continue;
                    }

                    if (string.Equals(
                            _lastCoordinatorCompletionMessage,
                            "采集已停止",
                            StringComparison.Ordinal))
                    {
                        cts.Cancel();
                    }

                    cts.Token.ThrowIfCancellationRequested();
                    completedCount++;
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failedCount++;
                    AddLog(BatchText(
                        "Batch.Log.ItemFailed",
                        "批量第 {0} 项处理失败，继续下一项：{1}",
                        index + 1,
                        ex.Message));
                }
                finally
                {
                    IsManualBatchSkipRequested = false;
                    AddManualBatchLogSeparator();
                }
            }

            if (!cts.IsCancellationRequested)
            {
                AddLog(BatchText(
                    "Batch.Log.Completed",
                    "批量采集完成：成功处理 {0} 项，失败或跳过 {1} 项，共 {2} 项。",
                    completedCount,
                    failedCount,
                    lines.Length));
            }
        }
        catch (OperationCanceledException)
        {
            AddLog(BatchText(
                "Batch.Log.Canceled",
                "批量采集已停止：已处理 {0}/{1} 项。",
                completedCount,
                lines.Length));
        }
        finally
        {
            _manualBatchCts = null;
            IsManualBatchSkipRequested = false;

            // 先释放通用忙碌状态，再结束批量状态，确保最后一次属性通知
            // 读取到的是最终可用状态，而不是 IsBusy=true 的中间状态。
            IsBusy = false;
            IsManualBatchRunning = false;
            OnPropertyChanged(nameof(CanStartManualBatchCapture));
            OnPropertyChanged(nameof(CanStopCurrentTask));
            RefreshCommands();

            // 命令和绑定可能在当前异步调用栈结束后才重新求值，
            // 在 UI 队列尾部再刷新一次，确保两个开始按钮立即恢复。
            Dispatcher.UIThread.Post(() =>
            {
                OnPropertyChanged(nameof(CanStartManualBatchCapture));
                OnPropertyChanged(nameof(CanStopCurrentTask));
                RefreshCommands();
            }, DispatcherPriority.Background);
        }
    }

    private PlatformOption? ResolvePlatformForBatchUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var targetUri)
            || targetUri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        return Platforms.FirstOrDefault(option =>
        {
            if (!Uri.TryCreate(option.HomeUrl, UriKind.Absolute, out var homeUri))
                return false;

            return HostsBelongToSamePlatform(targetUri.Host, homeUri.Host);
        });
    }

    public bool SkipCurrentManualBatchAuthor()
    {
        if (!IsManualBatchRunning
            || !IsCapturing
            || IsManualBatchSkipRequested)
        {
            return false;
        }

        IsManualBatchSkipRequested = true;
        AddLog(BatchLocalizedText(
            "Batch.Log.SkipRequested",
            "已请求跳过当前作者；正在停止当前采集，完成清理后立即处理下一位作者。",
            "Skipping the current author. The next author will start as soon as cleanup finishes.",
            "現在の作者をスキップします。終了処理後、すぐに次の作者を開始します。"));
        _coordinator.Stop();
        return true;
    }

    public void CancelManualBatchCapture()
    {
        if (!IsManualBatchRunning
            || _manualBatchCts is not { } cts
            || cts.IsCancellationRequested)
        {
            return;
        }

        cts.Cancel();
        _coordinator.Stop();
        AddLog(BatchText(
            "Batch.Log.CancelRequested",
            "已请求停止批量采集；当前作者停止后不会继续处理后续地址。"));
    }

    private void AddManualBatchLogSeparator()
    {
        const string line = "────────────────────────────────────────────────────────────";
        AddLog(string.Join(Environment.NewLine, Enumerable.Repeat(line, 10)));
    }

    internal void ClearCurrentAuthorDisplayForNextCapture()
    {
        _retainedAuthorName = null;
        _retainedAuthorAvatarUrl = null;
        _retainedAuthorAvatarImage = null;
        ClearCurrentAuthorAvatar();
        CurrentAuthorName = null;
        CurrentAuthorId = null;
        CurrentAuthorDirectory = null;
    }

    internal void RememberCurrentAuthorDisplayBeforeCleanup()
    {
        _retainedAuthorName = CurrentAuthorName;
        _retainedAuthorAvatarUrl = _currentAuthorAvatarUrl;
        _retainedAuthorAvatarImage = CurrentAuthorAvatarImage;
    }

    internal void RestoreCurrentAuthorDisplayAfterCleanup()
    {
        if (string.IsNullOrWhiteSpace(_retainedAuthorName)
            && string.IsNullOrWhiteSpace(_retainedAuthorAvatarUrl)
            && _retainedAuthorAvatarImage is null)
        {
            return;
        }

        CurrentAuthorName = _retainedAuthorName;
        _currentAuthorAvatarUrl = _retainedAuthorAvatarUrl;

        if (_retainedAuthorAvatarImage is not null)
        {
            CurrentAuthorAvatarImage = _retainedAuthorAvatarImage;
            return;
        }

        if (!string.IsNullOrWhiteSpace(_retainedAuthorAvatarUrl))
            _ = LoadCurrentAuthorAvatarAsync(_retainedAuthorAvatarUrl);
    }

    private string BatchText(string key, string fallback, params object?[] arguments)
    {
        var template = _localization.Get(key, fallback);
        try
        {
            return arguments.Length == 0
                ? template
                : string.Format(template, arguments);
        }
        catch (FormatException)
        {
            return arguments.Length == 0
                ? fallback
                : string.Format(fallback, arguments);
        }
    }

    private string BatchLocalizedText(
        string key,
        string chineseFallback,
        string englishFallback,
        string japaneseFallback,
        params object?[] arguments)
    {
        var languageCode = _localization.CurrentLanguageCode;
        var fallback = languageCode.StartsWith("ja", StringComparison.OrdinalIgnoreCase)
            ? japaneseFallback
            : languageCode.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                ? englishFallback
                : chineseFallback;
        var template = _localization.Get(key, fallback);

        try
        {
            return arguments.Length == 0
                ? template
                : string.Format(template, arguments);
        }
        catch (FormatException)
        {
            return arguments.Length == 0
                ? fallback
                : string.Format(fallback, arguments);
        }
    }
}
