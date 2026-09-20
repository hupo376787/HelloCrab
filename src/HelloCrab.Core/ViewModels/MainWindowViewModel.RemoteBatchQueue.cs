using Avalonia.Threading;
using HelloCrab.Core.Contracts;
using HelloCrab.Core.Models;

namespace HelloCrab.Core.ViewModels;

/// <summary>
/// 远程控制端提交的批量下载队列。
/// 新提交在已有手工批量任务运行时不会被拒绝，而是追加到队尾；
/// 当前批次结束后自动继续处理新增文本。
/// </summary>
public sealed partial class MainWindowViewModel
{
    private readonly object _remoteBatchQueueGate = new();
    private readonly Queue<string> _remoteBatchPendingLines = new();
    private bool _remoteBatchQueueWorkerRunning;

    /// <summary>
    /// 下载历史中的“重新采集”在桌面端已有任务时复用同一批量队列。
    /// 返回 true 表示本次操作已经被队列接管；false 表示当前空闲，应立即重新采集。
    /// </summary>
    private bool TryQueueHistoryRecollectIfNeeded(DownloadHistoryItem item)
    {
        bool shouldQueue;
        lock (_remoteBatchQueueGate)
        {
            shouldQueue = !CanStartManualBatchCapture
                          || _remoteBatchQueueWorkerRunning
                          || _remoteBatchPendingLines.Count > 0;
        }

        if (!shouldQueue)
            return false;

        var url = ExtractFirstUrl(item.OriginalUrl);
        if (string.IsNullOrWhiteSpace(url))
        {
            AddLocalizedLog("Log.History.HomeUrlMissing", item.UserName);
            return true;
        }

        bool startWorker;
        int pendingCount;
        lock (_remoteBatchQueueGate)
        {
            _remoteBatchPendingLines.Enqueue(url);
            pendingCount = _remoteBatchPendingLines.Count;

            startWorker = !_remoteBatchQueueWorkerRunning;
            if (startWorker)
                _remoteBatchQueueWorkerRunning = true;
        }

        AddLog(BatchLocalizedText(
            "Log.History.RecollectQueued",
            "作者“{0}”的重新采集任务已进入队列，当前有 {1} 条任务等待处理；当前任务完成后会自动开始。",
            "Re-collection for author “{0}” was added to the queue. {1} task(s) are waiting and it will start automatically after the current task finishes.",
            "作者「{0}」の再収集タスクをキューに追加しました。現在 {1} 件待機中で、現在のタスク完了後に自動で開始します。",
            item.UserName,
            pendingCount));

        if (startWorker)
        {
            Dispatcher.UIThread.Post(
                () => _ = RunRemoteBatchQueueAsync(),
                DispatcherPriority.Background);
        }

        return true;
    }

    public RemoteCommandResult QueueRemoteManualBatchCapture(string? content)
    {
        var lines = (content ?? string.Empty)
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        if (lines.Length == 0)
            return RemoteCommandResult.Fail("批量下载文本中没有有效内容。\n请输入作者主页地址或包含网址的分享文本。");

        bool startWorker;
        bool appended;
        bool waitForHost;

        lock (_remoteBatchQueueGate)
        {
            appended = IsManualBatchRunning
                       || _remoteBatchQueueWorkerRunning
                       || _remoteBatchPendingLines.Count > 0;
            waitForHost = !CanStartManualBatchCapture;

            foreach (var line in lines)
                _remoteBatchPendingLines.Enqueue(line);

            startWorker = !_remoteBatchQueueWorkerRunning;
            if (startWorker)
                _remoteBatchQueueWorkerRunning = true;
        }

        var message = appended
            ? $"已追加 {lines.Length} 条到桌面端批量下载任务队列。"
            : waitForHost
                ? $"已接收 {lines.Length} 条批量下载任务；桌面端空闲后会自动开始。"
                : $"已接收 {lines.Length} 条批量下载任务，桌面端开始处理。";

        AddLog(message);

        if (startWorker)
        {
            Dispatcher.UIThread.Post(
                () => _ = RunRemoteBatchQueueAsync(),
                DispatcherPriority.Background);
        }

        return RemoteCommandResult.Ok(message);
    }

    private async Task RunRemoteBatchQueueAsync()
    {
        try
        {
            while (!_isDisposed)
            {
                if (!CanStartManualBatchCapture)
                {
                    await Task.Delay(250);
                    continue;
                }

                string[] nextLines;
                lock (_remoteBatchQueueGate)
                {
                    if (_remoteBatchPendingLines.Count == 0)
                    {
                        _remoteBatchQueueWorkerRunning = false;
                        return;
                    }

                    nextLines = _remoteBatchPendingLines.ToArray();
                    _remoteBatchPendingLines.Clear();
                }

                try
                {
                    await StartManualBatchCaptureAsync(
                        string.Join(Environment.NewLine, nextLines));
                }
                catch (Exception ex)
                {
                    AddLog($"远程批量下载任务执行失败：{ex.Message}");
                }
            }
        }
        finally
        {
            bool restart;
            lock (_remoteBatchQueueGate)
            {
                _remoteBatchQueueWorkerRunning = false;
                restart = !_isDisposed && _remoteBatchPendingLines.Count > 0;
                if (restart)
                    _remoteBatchQueueWorkerRunning = true;
            }

            if (restart)
            {
                Dispatcher.UIThread.Post(
                    () => _ = RunRemoteBatchQueueAsync(),
                    DispatcherPriority.Background);
            }
        }
    }
}
