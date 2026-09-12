using Avalonia.Threading;
using HelloCrab.Core.Contracts;

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
