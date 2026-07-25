using System.Collections.Concurrent;
using System.Threading.Channels;

namespace HelloCrab.Core.Services.Images;

/// <summary>
/// Stores only file paths in an unbounded background queue. Image bytes are never held in the
/// queue, so fast downloads do not consume large amounts of memory and are not blocked by YOLO.
/// </summary>
public sealed class PersonDetectionQueueService : IAsyncDisposable
{
    public const string PendingSuffix = ".pending";

    private readonly IPersonImageDetector _detector;
    private readonly Channel<PersonDetectionJob> _channel;
    private readonly ConcurrentDictionary<Guid, SessionState> _sessions = new();
    private readonly ConcurrentDictionary<string, PathWorkState> _pathStates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Task _workerTask;
    private int _disposed;

    public PersonDetectionQueueService(IPersonImageDetector detector)
    {
        _detector = detector;
        _channel = Channel.CreateUnbounded<PersonDetectionJob>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _workerTask = Task.Run(() => WorkerLoopAsync(_shutdownCts.Token));
    }

    public event EventHandler<string>? Log;

    public void BeginSession(Guid sessionId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_sessions.TryAdd(sessionId, new SessionState(sessionId)))
            throw new InvalidOperationException($"人像检测会话已存在：{sessionId}");
    }

    /// <summary>
    /// Adds a pending image to the background queue. This method does not wait for inference.
    /// The same physical pending file is detected only once, even if recovery and a new capture
    /// discover it at the same time; every attached session still waits for the shared result.
    /// </summary>
    public void Enqueue(
        Guid sessionId,
        string pendingPath,
        string finalPath,
        double confidence)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_sessions.TryGetValue(sessionId, out var session))
            throw new InvalidOperationException($"未找到人像检测会话：{sessionId}");

        if (string.IsNullOrWhiteSpace(pendingPath)
            || !pendingPath.EndsWith(PendingSuffix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("待检测文件必须以 .pending 结尾。", nameof(pendingPath));
        }

        var normalizedPendingPath = Path.GetFullPath(pendingPath);
        var normalizedFinalPath = Path.GetFullPath(finalPath);
        session.AddPending();

        var candidate = new PathWorkState(
            new PersonDetectionJob(
                normalizedPendingPath,
                normalizedFinalPath,
                Math.Clamp(confidence, 0.10, 0.95)));
        var sharedState = _pathStates.GetOrAdd(normalizedPendingPath, candidate);
        _ = ObservePathForSessionAsync(session, sharedState.Completion.Task);

        if (!ReferenceEquals(candidate, sharedState))
            return;

        if (_channel.Writer.TryWrite(candidate.Job))
            return;

        var rejected = PersonDetectionFileResult.CreateCanceled(
            "人像检测队列已关闭，待检测文件将保留为 .pending，程序下次启动时会继续处理。");
        candidate.Completion.TrySetResult(rejected);
        _pathStates.TryRemove(normalizedPendingPath, out _);
    }

    public PersonDetectionSessionTicket CompleteSession(Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return PersonDetectionSessionTicket.Empty(sessionId);

        var snapshot = session.MarkDownloadsCompleted();
        _ = snapshot.Completion.ContinueWith(
            completedTask => _sessions.TryRemove(sessionId, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return snapshot;
    }

    /// <summary>
    /// Requeues unfinished .pending files left by a previous abnormal exit. Recovery runs in the
    /// same background queue and does not block a new author download.
    /// </summary>
    public async Task<PersonDetectionSessionResult> RecoverPendingFilesAsync(
        string downloadRoot,
        double confidence = 0.60,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(downloadRoot) || !Directory.Exists(downloadRoot))
            return PersonDetectionSessionResult.Empty(Guid.Empty);

        string[] pendingFiles;
        try
        {
            pendingFiles = await Task.Run(
                () => Directory.EnumerateFiles(
                        downloadRoot,
                        "*" + PendingSuffix,
                        SearchOption.AllDirectories)
                    .ToArray(),
                cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RaiseLog($"扫描遗留待检测图片失败：{ex.Message}");
            return PersonDetectionSessionResult.Empty(Guid.Empty);
        }

        if (pendingFiles.Length == 0)
            return PersonDetectionSessionResult.Empty(Guid.Empty);

        var sessionId = Guid.NewGuid();
        BeginSession(sessionId);
        var queued = 0;
        foreach (var pendingPath in pendingFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!pendingPath.EndsWith(PendingSuffix, StringComparison.OrdinalIgnoreCase))
                continue;

            var finalPath = pendingPath[..^PendingSuffix.Length];
            Enqueue(sessionId, pendingPath, finalPath, confidence);
            queued++;
        }

        RaiseLog($"发现 {queued} 张上次未完成的人像检测图片，已恢复到后台队列。");
        var ticket = CompleteSession(sessionId);
        var result = await ticket.Completion;
        RaiseLog(
            $"遗留人像检测处理完成：保留 {result.KeptCount} 张，删除 {result.DeletedCount} 张，" +
            $"检测失败保留 {result.DetectionFailureCount} 张。 ");
        return result;
    }

    private async Task ObservePathForSessionAsync(
        SessionState session,
        Task<PersonDetectionFileResult> completion)
    {
        PersonDetectionFileResult result;
        try
        {
            result = await completion.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = PersonDetectionFileResult.CreateFailed(ex.Message);
        }

        session.CompleteOne(result);
    }

    private async Task WorkerLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var job in _channel.Reader.ReadAllAsync(cancellationToken))
            {
                var key = Path.GetFullPath(job.PendingPath);
                if (!_pathStates.TryGetValue(key, out var state))
                    continue;

                PersonDetectionFileResult result;
                try
                {
                    result = await ProcessJobAsync(job, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    result = PersonDetectionFileResult.CreateCanceled(
                        "程序正在退出，待检测文件保持为 .pending，下次启动继续处理。");
                }
                catch (Exception ex)
                {
                    result = await FailSafeKeepAsync(job, ex.Message);
                }

                state.Completion.TrySetResult(result);
                _pathStates.TryRemove(key, out _);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            var canceled = PersonDetectionFileResult.CreateCanceled(
                "人像检测队列已停止，待检测文件保持为 .pending。");
            foreach (var pair in _pathStates)
            {
                pair.Value.Completion.TrySetResult(canceled);
                _pathStates.TryRemove(pair.Key, out _);
            }
        }
    }

    private async Task<PersonDetectionFileResult> ProcessJobAsync(
        PersonDetectionJob job,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(job.PendingPath))
        {
            if (File.Exists(job.FinalPath))
                return PersonDetectionFileResult.CreateKept();

            return PersonDetectionFileResult.CreateFailed("待检测图片和最终图片均不存在。");
        }

        var result = await _detector.DetectAsync(
            job.PendingPath,
            job.Confidence,
            RaiseLog,
            cancellationToken);

        if (!result.DetectionSucceeded)
        {
            return await FailSafeKeepAsync(
                job,
                result.ErrorMessage ?? "未知检测错误");
        }

        if (result.ContainsPerson)
        {
            PromotePendingFile(job.PendingPath, job.FinalPath);
            RaiseLog($"检测到人物，已保留图片：{Path.GetFileName(job.FinalPath)}");
            return PersonDetectionFileResult.CreateKept();
        }

        try
        {
            File.Delete(job.PendingPath);
            RaiseLog($"未检测到人物，已删除图片：{Path.GetFileName(job.FinalPath)}");
            return PersonDetectionFileResult.CreateDeleted();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                PromotePendingFile(job.PendingPath, job.FinalPath);
                RaiseLog(
                    $"未检测到人物，但删除图片失败；为避免遗留待处理文件，已恢复并保留图片：" +
                    $"{Path.GetFileName(job.FinalPath)}；{ex.Message}");
                return PersonDetectionFileResult.CreateDetectionFailed(ex.Message);
            }
            catch (Exception restoreEx) when (restoreEx is IOException or UnauthorizedAccessException)
            {
                RaiseLog(
                    $"未检测到人物，但删除和恢复文件名均失败，文件仍保留为 .pending：" +
                    $"{Path.GetFileName(job.PendingPath)}；{restoreEx.Message}");
                return PersonDetectionFileResult.CreateFailed(restoreEx.Message);
            }
        }
    }

    private Task<PersonDetectionFileResult> FailSafeKeepAsync(
        PersonDetectionJob job,
        string errorMessage)
    {
        try
        {
            if (File.Exists(job.PendingPath))
                PromotePendingFile(job.PendingPath, job.FinalPath);

            RaiseLog(
                $"人像检测失败，为避免误删已保留图片：{Path.GetFileName(job.FinalPath)}；" +
                errorMessage);
            return Task.FromResult(PersonDetectionFileResult.CreateDetectionFailed(errorMessage));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RaiseLog(
                $"人像检测失败且恢复最终文件名失败，文件仍保留为 .pending：" +
                $"{Path.GetFileName(job.PendingPath)}；{ex.Message}");
            return Task.FromResult(PersonDetectionFileResult.CreateFailed(ex.Message));
        }
    }

    public static string GetFinalPath(string pendingPath)
    {
        if (!pendingPath.EndsWith(PendingSuffix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("路径不是 .pending 文件。", nameof(pendingPath));
        return pendingPath[..^PendingSuffix.Length];
    }

    public static void PromotePendingFile(string pendingPath, string finalPath)
    {
        var directory = Path.GetDirectoryName(finalPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        File.Move(pendingPath, finalPath, overwrite: true);
    }

    private void RaiseLog(string message) => Log?.Invoke(this, message);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _channel.Writer.TryComplete();
        _shutdownCts.Cancel();
        try
        {
            await _workerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        foreach (var session in _sessions.Values)
            session.ForceCompleteAsCanceled();
        _sessions.Clear();

        await _detector.DisposeAsync();
        _shutdownCts.Dispose();
    }

    private sealed record PersonDetectionJob(
        string PendingPath,
        string FinalPath,
        double Confidence);

    private sealed class PathWorkState
    {
        public PathWorkState(PersonDetectionJob job)
        {
            Job = job;
            Completion = new TaskCompletionSource<PersonDetectionFileResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public PersonDetectionJob Job { get; }
        public TaskCompletionSource<PersonDetectionFileResult> Completion { get; }
    }

    private sealed class SessionState
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource<PersonDetectionSessionResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _totalQueued;
        private int _pending;
        private int _kept;
        private int _deleted;
        private int _detectionFailures;
        private int _failed;
        private int _canceled;
        private bool _downloadsCompleted;

        public SessionState(Guid sessionId) => SessionId = sessionId;

        public Guid SessionId { get; }

        public void AddPending()
        {
            lock (_gate)
            {
                if (_downloadsCompleted)
                    throw new InvalidOperationException("下载阶段已经结束，不能继续加入人像检测任务。");
                _totalQueued++;
                _pending++;
            }
        }

        public void CompleteOne(PersonDetectionFileResult result)
        {
            PersonDetectionSessionResult? completed = null;
            lock (_gate)
            {
                if (result.WasCanceled)
                    _canceled++;
                else if (result.DetectionFailed)
                    _detectionFailures++;
                else if (result.Deleted)
                    _deleted++;
                else if (result.Kept)
                    _kept++;
                else
                    _failed++;

                if (_pending > 0)
                    _pending--;
                completed = TryCreateCompletedResultNoLock();
            }

            if (completed is not null)
                _completion.TrySetResult(completed);
        }

        public PersonDetectionSessionTicket MarkDownloadsCompleted()
        {
            PersonDetectionSessionResult? completed;
            PersonDetectionSessionTicket ticket;
            lock (_gate)
            {
                _downloadsCompleted = true;
                completed = TryCreateCompletedResultNoLock();
                ticket = new PersonDetectionSessionTicket(
                    SessionId,
                    _totalQueued,
                    _pending,
                    _completion.Task);
            }

            if (completed is not null)
                _completion.TrySetResult(completed);
            return ticket;
        }

        public void ForceCompleteAsCanceled()
        {
            PersonDetectionSessionResult result;
            lock (_gate)
            {
                _canceled += _pending;
                _pending = 0;
                _downloadsCompleted = true;
                result = CreateResultNoLock();
            }
            _completion.TrySetResult(result);
        }

        private PersonDetectionSessionResult? TryCreateCompletedResultNoLock()
            => _downloadsCompleted && _pending == 0 ? CreateResultNoLock() : null;

        private PersonDetectionSessionResult CreateResultNoLock()
            => new(
                SessionId,
                _totalQueued,
                _kept,
                _deleted,
                _detectionFailures,
                _failed,
                _canceled);
    }

    private sealed record PersonDetectionFileResult(
        bool Kept,
        bool Deleted,
        bool DetectionFailed,
        bool WasCanceled,
        string? ErrorMessage)
    {
        public static PersonDetectionFileResult CreateKept() => new(true, false, false, false, null);
        public static PersonDetectionFileResult CreateDeleted() => new(false, true, false, false, null);
        public static PersonDetectionFileResult CreateDetectionFailed(string error) =>
            new(true, false, true, false, error);
        public static PersonDetectionFileResult CreateFailed(string error) =>
            new(false, false, false, false, error);
        public static PersonDetectionFileResult CreateCanceled(string error) =>
            new(false, false, false, true, error);
    }
}

public sealed record PersonDetectionSessionTicket(
    Guid SessionId,
    int QueuedCount,
    int PendingCount,
    Task<PersonDetectionSessionResult> Completion)
{
    public static PersonDetectionSessionTicket Empty(Guid sessionId)
    {
        var result = PersonDetectionSessionResult.Empty(sessionId);
        return new PersonDetectionSessionTicket(
            sessionId,
            0,
            0,
            Task.FromResult(result));
    }
}

public sealed record PersonDetectionSessionResult(
    Guid SessionId,
    int QueuedCount,
    int KeptCount,
    int DeletedCount,
    int DetectionFailureCount,
    int FailedCount,
    int CanceledCount)
{
    public static PersonDetectionSessionResult Empty(Guid sessionId)
        => new(sessionId, 0, 0, 0, 0, 0, 0);
}
