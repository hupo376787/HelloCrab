using HelloCrab.Core.Models;

namespace HelloCrab.Core.ViewModels;

public sealed partial class MainWindowViewModel
{
    private CancellationTokenSource? _manualBatchCts;
    private bool _isManualBatchRunning;

    public bool IsManualBatchRunning
    {
        get => _isManualBatchRunning;
        private set
        {
            if (SetProperty(ref _isManualBatchRunning, value))
            {
                OnPropertyChanged(nameof(CanStopCurrentTask));
                OnPropertyChanged(nameof(CanStartManualBatchCapture));
                RefreshCommands();
            }
        }
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
            AddLog(_localization.Get("Batch.Log.Busy", "当前已有任务运行，无法开始批量采集。"));
            return;
        }

        var lines = (fileContent ?? string.Empty)
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        if (lines.Length == 0)
        {
            AddLog(_localization.Get("Batch.Log.Empty", "导入的文本文件没有有效地址；空行会被自动忽略。"));
            return;
        }

        using var cts = new CancellationTokenSource();
        _manualBatchCts = cts;
        using var stopRegistration = cts.Token.Register(_coordinator.Stop);

        IsManualBatchRunning = true;
        IsBusy = true;
        var completedCount = 0;
        var failedCount = 0;

        try
        {
            AddLog(_localization.Format("Batch.Log.Started", lines.Length));

            for (var index = 0; index < lines.Length; index++)
            {
                cts.Token.ThrowIfCancellationRequested();
                var sourceLine = lines[index];
                var url = ExtractFirstUrl(sourceLine);
                if (string.IsNullOrWhiteSpace(url))
                {
                    failedCount++;
                    AddLog(_localization.Format("Batch.Log.InvalidUrl", index + 1, sourceLine));
                    continue;
                }

                var platform = ResolvePlatformForBatchUrl(url);
                if (platform is null)
                {
                    failedCount++;
                    AddLog(_localization.Format("Batch.Log.UnsupportedUrl", index + 1, url));
                    continue;
                }

                try
                {
                    SelectedPlatform = platform;
                    CurrentUrl = url;
                    AddLog(_localization.Format(
                        "Batch.Log.ItemStarted",
                        index + 1,
                        lines.Length,
                        platform.DisplayName,
                        url));

                    await _browser.StartAsync(url, IsHeadlessMode, cts.Token);
                    if (_browser.IsLoginRecoveryActive)
                    {
                        AddLog(_localization.Format("Batch.Log.LoginRequired", index + 1, platform.DisplayName));
                        break;
                    }

                    await StartCaptureAsync();
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
                    AddLog(_localization.Format("Batch.Log.ItemFailed", index + 1, ex.Message));
                }
            }

            if (!cts.IsCancellationRequested)
            {
                AddLog(_localization.Format(
                    "Batch.Log.Completed",
                    completedCount,
                    failedCount,
                    lines.Length));
            }
        }
        catch (OperationCanceledException)
        {
            AddLog(_localization.Format("Batch.Log.Canceled", completedCount, lines.Length));
        }
        finally
        {
            _manualBatchCts = null;
            IsManualBatchRunning = false;
            IsBusy = false;
            RefreshCommands();
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

    public void CancelManualBatchCapture()
    {
        if (!IsManualBatchRunning)
            return;

        _manualBatchCts?.Cancel();
        AddLog(_localization.Get(
            "Batch.Log.CancelRequested",
            "已请求停止批量采集；当前作者停止后不会继续处理后续地址。"));
    }
}
