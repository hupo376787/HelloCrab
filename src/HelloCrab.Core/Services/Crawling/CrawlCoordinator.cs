using HelloCrab.Core.Services.Localization;
using System.Text.Json;
using System.Threading.Channels;
using HelloCrab.Core.Models;
using HelloCrab.Core.Services.Browser;
using HelloCrab.Core.Services.Downloading;
using HelloCrab.Core.Services.History;
using HelloCrab.Core.Services.Images;
using HelloCrab.Core.Sites;
using HelloCrab.Core.Utilities;

namespace HelloCrab.Core.Services.Crawling;

public sealed class CrawlCoordinator : IAsyncDisposable
{
    private readonly IBrowserAutomationService _browser;
    private readonly SiteAdapterRegistry _registry;
    private readonly MediaDownloadService _downloader;
    private readonly DownloadHistoryService _history;
    private readonly JsonDownloadIndex _index = new();
    private readonly object _downloadProgressGate = new();
    private readonly HashSet<string> _sessionSeen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _persistedAuthorMetadata = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string PlatformId, string UserId, string Folder)> _touchedAuthors = new(StringComparer.Ordinal);

    // 每一页（一次列表响应）中的所有作品处理完毕后，再按用户设置随机等待，
    // 然后才允许滚动或请求下一页。作品之间、详情解析前和媒体下载前均不等待。

    private const string CursorFetchScript = """
        async request => {
            const forbiddenHeaders = new Set([
                'accept-encoding', 'connection', 'content-length', 'cookie', 'host',
                'origin', 'referer', 'user-agent', ':authority', ':method', ':path', ':scheme'
            ]);
            const headers = new Headers();
            for (const [name, value] of Object.entries(request.headers || {})) {
                const lower = name.toLowerCase();
                if (forbiddenHeaders.has(lower) || lower.startsWith('sec-')) continue;
                try { headers.set(name, value); } catch { }
            }

            const method = String(request.method || (request.body ? 'POST' : 'GET')).toUpperCase();
            const options = {
                method,
                headers,
                credentials: 'include',
                cache: 'no-store',
                redirect: 'follow'
            };
            if (request.body && method !== 'GET' && method !== 'HEAD')
                options.body = request.body;

            const response = await fetch(request.url, options);
            const text = await response.text();
            return {
                ok: response.ok,
                status: response.status,
                url: response.url,
                bodyLength: text.length,
                preview: response.ok ? '' : text.slice(0, 180)
            };
        }
        """;

    private CancellationTokenSource? _captureCts;
    private Channel<CapturedResponse>? _channel;
    private ISiteAdapter? _activeAdapter;
    private string _downloadRoot = string.Empty;
    private string _platformDownloadRoot = string.Empty;
    private string _capturePageUrl = string.Empty;
    private CrawlerDownloadOptions _downloadOptions = new();
    private int _responseCount;
    private int? _totalWorkCount;
    private int _discoveredCount;
    private int _downloadedCount;
    private int _skippedCount;
    private int _failedCount;
    private int _processingCount;
    private int _parsedResponseCount;
    private long _responseVersion;
    private bool? _hasMore;
    private string? _nextCursor;
    private BrowserRequestSnapshot? _lastPaginationRequest;
    private string? _douyinProfileResponseUrl;
    private DateTimeOffset _lastResponseAt;
    private DateTimeOffset _lastNewWorkAt;
    private string? _currentWork;
    private int _consecutiveCompletedDuplicates;
    private bool _duplicateStopRequested;
    private string? _currentAuthorId;
    private string? _currentAuthorName;
    private string? _currentAuthorAvatarUrl;
    private string? _currentAuthorDirectory;
    private string? _currentCoverUrl;
    private string? _currentSourceUrl;
    private bool _isDownloading;
    private bool _isDownloadIndeterminate;
    private double _downloadProgressPercent;
    private string? _downloadProgressText;
    private string? _sessionAuthorId;
    private string? _sessionAuthorName;
    private Guid? _personDetectionSessionId;

    public CrawlCoordinator(
        IBrowserAutomationService browser,
        SiteAdapterRegistry registry,
        MediaDownloadService downloader,
        DownloadHistoryService history)
    {
        _browser = browser;
        _registry = registry;
        _downloader = downloader;
        _history = history;
        _downloader.Log += (_, message) => RaiseLog(message);
        _downloader.ProgressChanged += OnDownloadProgressChanged;
    }

    public bool IsCapturing => _captureCts is not null;

    public event EventHandler<string>? Log;
    public event EventHandler<CrawlProgressSnapshot>? ProgressChanged;
    public event EventHandler<string>? Completed;

    public async Task<CrawlSessionResult> StartAsync(
        string platformId,
        string downloadRoot,
        CrawlerDownloadOptions downloadOptions,
        CancellationToken cancellationToken = default)
    {
        if (_captureCts is not null)
            throw new InvalidOperationException(RuntimeLocalization.Get("Error.Crawl.AlreadyRunning", "采集任务已经在运行。"));

        var adapter = _registry.GetRequired(platformId);

        if (!_browser.IsStarted)
            throw new InvalidOperationException(RuntimeLocalization.Get("Error.Browser.NotStarted", "请先打开浏览器。"));

        var foregroundPageUrl = await _browser.SelectForegroundPageAsync(cancellationToken);
        if (!adapter.CanHandlePage(foregroundPageUrl))
            throw new InvalidOperationException(RuntimeLocalization.Get("Error.Crawl.WrongAuthorPage", "当前活动标签页不是该平台的作者主页。请切换到作者主页标签，再点击开始采集。"));

        Directory.CreateDirectory(downloadRoot);
        ResetState();

        _activeAdapter = adapter;
        _capturePageUrl = foregroundPageUrl;
        _downloadRoot = downloadRoot;
        _platformDownloadRoot = Path.Combine(
            downloadRoot,
            PlatformFolderHelper.GetFolderName(platformId));
        Directory.CreateDirectory(_platformDownloadRoot);
        _downloadOptions = downloadOptions;
        _downloader.BeginDownloadSession(downloadOptions.DownloadSpeedLimitMBps);
        if (downloadOptions.EnablePersonDetection)
        {
            _personDetectionSessionId = Guid.NewGuid();
            _downloader.BeginPersonDetectionSession(_personDetectionSessionId.Value);
        }

        _captureCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _channel = Channel.CreateUnbounded<CapturedResponse>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _browser.ResponseReceived += OnBrowserResponse;

        var token = _captureCts.Token;
        var completionMessage = RuntimeLocalization.Get("Status.CaptureCompleted", "采集完成");
        var personDetectionTicket = PersonDetectionSessionTicket.Empty(Guid.Empty);
        string? completedAuthorId = null;
        string? completedAuthorName = null;
        string? completedAuthorFolder = null;
        var completedDownloadedCount = 0;

        RaiseLog(RuntimeLocalization.Format("Log.Crawl.Start", "开始采集：{0}", adapter.DisplayName));
        RaiseLog(RuntimeLocalization.Get("Log.Crawl.RefreshHome", "将刷新当前作者主页，以重新触发第一页作品接口。"));
        PublishProgress();

        var consumerTask = ConsumeResponsesAsync(_channel.Reader, token);
        var pageLocked = false;
        try
        {
            await _browser.SetCaptureLockAsync(true, token);
            pageLocked = true;
            _capturePageUrl = _browser.CurrentUrl;
            if (!adapter.CanHandlePage(_capturePageUrl))
            {
                throw new InvalidOperationException(
                    RuntimeLocalization.Get("Error.Crawl.LockedWrongPage", "锁定的当前标签页不是该平台的作者主页。请停止后切换到作者主页标签再试。"));
            }

            RaiseLog(RuntimeLocalization.Format("Log.Crawl.CurrentTab", "当前采集标签页：{0}", _capturePageUrl));
            RaiseLog(RuntimeLocalization.Get("Log.Crawl.TabLocked", "采集标签页已锁定：页面操作、新标签页和误导航将被阻止。"));
            await TryRecoverDouyinTotalWorkCountAsync(adapter, token);
            await _browser.ReloadAsync(token);
            RaiseLog(RuntimeLocalization.Get("Log.Crawl.WaitFirstPage", "等待第一页作品接口和作品列表完成加载……"));
            await WaitForResponseOrNewWorkAsync(0, 0, TimeSpan.FromSeconds(20), token);
            if (!_totalWorkCount.HasValue)
                await TryRecoverDouyinTotalWorkCountAsync(adapter, token);
            await WaitUntilPipelineIdleAsync(token);
            completionMessage = await RunScrollLoopAsync(adapter, token);
            _channel.Writer.TryComplete();
            await consumerTask;
            Completed?.Invoke(this, completionMessage);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _channel.Writer.TryComplete();
            try { await consumerTask; } catch (OperationCanceledException) { }
            completionMessage = RuntimeLocalization.Get("Status.CaptureStopped", "采集已停止");
            Completed?.Invoke(this, completionMessage);
        }
        catch (OperationCanceledException ex)
        {
            try { _captureCts?.Cancel(); } catch (ObjectDisposedException) { }
            _channel.Writer.TryComplete();
            try { await consumerTask; } catch (OperationCanceledException) { }
            throw new InvalidOperationException(
                RuntimeLocalization.Get(
                    "Error.Browser.OperationCanceled",
                    "浏览器操作被意外取消。请确认采集期间作者页面没有被关闭，或重新打开作者主页后再试。"),
                ex);
        }
        catch
        {
            try { _captureCts?.Cancel(); } catch (ObjectDisposedException) { }
            _channel.Writer.TryComplete();
            try
            {
                await consumerTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (ChannelClosedException)
            {
            }

            throw;
        }
        finally
        {
            completedAuthorId = _sessionAuthorId ?? _currentAuthorId;
            completedAuthorName = _sessionAuthorName ?? _currentAuthorName;
            completedAuthorFolder = _currentAuthorDirectory;
            if (string.IsNullOrWhiteSpace(completedAuthorFolder))
                completedAuthorFolder = _touchedAuthors.Values.FirstOrDefault().Folder;
            completedDownloadedCount = _downloadedCount;

            if (_personDetectionSessionId.HasValue)
            {
                personDetectionTicket = _downloader.CompletePersonDetectionSession(
                    _personDetectionSessionId.Value);
                if (personDetectionTicket.PendingCount > 0)
                {
                    RaiseLog(RuntimeLocalization.Format("Log.Person.DownloadStageDone", "作者资源下载阶段已完成，后台仍有 {0} 张图片等待人像检测。现在可以开始采集其他作者。", personDetectionTicket.PendingCount));
                }
            }

            try
            {
                await RefreshTouchedAuthorStatsAsync();
            }
            catch (Exception ex)
            {
                RaiseLog(RuntimeLocalization.Format("Log.History.UpdateStatsFailed", "更新下载历史统计失败：{0}", ex.Message));
            }

            if (pageLocked)
            {
                try
                {
                    await _browser.SetCaptureLockAsync(false, CancellationToken.None);
                    RaiseLog(RuntimeLocalization.Get("Log.Crawl.TabUnlocked", "采集标签页已解锁。"));
                }
                catch (Exception ex)
                {
                    RaiseLog(RuntimeLocalization.Format("Log.Crawl.UnlockFailed", "解除标签页锁定失败：{0}", ex.Message));
                }
            }

            CleanupCapture();
            PublishProgress();
        }

        return new CrawlSessionResult(
            platformId,
            completionMessage,
            completedAuthorId,
            completedAuthorName,
            completedAuthorFolder,
            completedDownloadedCount,
            downloadOptions.EnablePersonDetection,
            personDetectionTicket);
    }

    public Task<PersonDetectionSessionResult> RecoverPendingPersonDetectionAsync(
        string downloadRoot,
        double confidence,
        CancellationToken cancellationToken = default)
        => _downloader.RecoverPendingPersonDetectionAsync(
            downloadRoot,
            confidence,
            cancellationToken);

    public void Stop() => _captureCts?.Cancel();

    private async void OnBrowserResponse(object? sender, BrowserResponseReceivedEventArgs response)
    {
        var adapter = _activeAdapter;
        var channel = _channel;
        var cts = _captureCts;
        if (adapter is null || channel is null || cts is null || cts.IsCancellationRequested)
            return;

        try
        {
            if (!adapter.IsTargetResponse(
                    response.Url,
                    response.ResourceType,
                    response.StatusCode,
                    response.RequestPostData))
                return;

            if (adapter.Id.Equals("douyin", StringComparison.OrdinalIgnoreCase)
                && response.Url.Contains(
                    "/aweme/v1/web/user/profile/other",
                    StringComparison.OrdinalIgnoreCase))
            {
                _douyinProfileResponseUrl = response.Url;
            }

            var text = await response.ReadBodyAsync(cts.Token);
            if (string.IsNullOrWhiteSpace(text))
                return;

            var totalWorkCount = adapter.TryReadTotalWorkCount(
                response.Url,
                text,
                _capturePageUrl,
                response.RequestPostData);
            if (totalWorkCount.HasValue)
                SetTotalWorkCount(totalWorkCount.Value);

            if (adapter.TryHandleAuxiliaryResponse(
                    response.Url,
                    text,
                    _capturePageUrl,
                    response.RequestPostData,
                    out var auxiliaryDiagnostic))
            {
                if (!string.IsNullOrWhiteSpace(auxiliaryDiagnostic))
                    RaiseLog(auxiliaryDiagnostic);
                PublishProgress();
                return;
            }

            Interlocked.Increment(ref _responseCount);
            Interlocked.Increment(ref _responseVersion);
            _lastResponseAt = DateTimeOffset.Now;
            await channel.Writer.WriteAsync(
                new CapturedResponse(
                    response.Url,
                    text,
                    response.PageUrl,
                    response.RequestPostData,
                    new BrowserRequestSnapshot(
                        response.Url,
                        response.RequestMethod,
                        response.RequestPostData,
                        response.RequestHeaders)),
                cts.Token);
            RaiseLog(
                RuntimeLocalization.Format(
                    "Log.Crawl.ResponseCaptured",
                    "捕获作品响应：第 {0} 页",
                    _responseCount)
                + Environment.NewLine
                + $"URL: {response.Url}");
            PublishProgress();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ChannelClosedException)
        {
        }
        catch (Exception ex)
        {
            RaiseLog(RuntimeLocalization.Format("Log.Crawl.ResponseReadFailed", "读取接口响应失败：{0}", ex.Message));
        }
    }

    private async Task ConsumeResponsesAsync(ChannelReader<CapturedResponse> reader, CancellationToken cancellationToken)
    {
        await foreach (var captured in reader.ReadAllAsync(cancellationToken))
        {
            if (_duplicateStopRequested)
                break;

            var adapter = _activeAdapter;
            if (adapter is null)
                continue;

            Interlocked.Increment(ref _processingCount);
            PublishProgress();
            try
            {
                ParsedWorkBatch batch;
                try
                {
                    batch = adapter.ParseResponse(
                        captured.ResponseUrl,
                        captured.Json,
                        _capturePageUrl,
                        captured.RequestBody);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _parsedResponseCount);
                    RaiseLog(RuntimeLocalization.Format("Log.Crawl.ResponseParseFailed", "解析作品响应失败：{0}", ex.Message));
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(batch.Diagnostic))
                    RaiseLog(batch.Diagnostic);

                if (batch.TotalWorkCount.HasValue)
                    _totalWorkCount = batch.TotalWorkCount;

                if (batch.HasMore.HasValue)
                    _hasMore = batch.HasMore;
                if (batch.HasMore.HasValue || batch.Cursor is not null)
                    _nextCursor = batch.Cursor;
                if (batch.HasMore.HasValue || batch.Cursor is not null || batch.Works.Count > 0)
                    _lastPaginationRequest = captured.Request;
                Interlocked.Increment(ref _parsedResponseCount);

                foreach (var listedWork in batch.Works)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var work = listedWork;

                    try
                    {
                        work = await adapter.EnrichWorkMetadataAsync(
                            work,
                            _browser,
                            cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        RaiseLog(RuntimeLocalization.Format("Log.Crawl.AuthorProfileFallback", "补充作者资料失败，将继续使用列表信息：{0}", ex.Message));
                    }

                    if (_sessionAuthorId is null)
                    {
                        _sessionAuthorId = work.AuthorId;
                        _sessionAuthorName = work.AuthorName;
                        RaiseLog(RuntimeLocalization.Format("Log.Crawl.AuthorBound", "本次采集已绑定作者：{0}（UID {1}）", work.AuthorName, work.AuthorId));
                    }
                    else if (!string.Equals(_sessionAuthorId, work.AuthorId, StringComparison.Ordinal))
                    {
                        RaiseLog(RuntimeLocalization.Format(
                            "Log.Crawl.OtherAuthorBlocked",
                            "已阻止其他作者作品进入下载队列：{0}（UID {1}），当前目标为 {2}（UID {3}）。",
                            work.AuthorName, work.AuthorId, _sessionAuthorName, _sessionAuthorId));
                        continue;
                    }

                    var sessionKey = $"{work.PlatformId}:{work.WorkId}";
                    if (!_sessionSeen.Add(sessionKey))
                        continue;

                    var completionKey = JsonDownloadIndex.BuildKey(work, _downloadOptions);

                    Interlocked.Increment(ref _discoveredCount);
                    _lastNewWorkAt = DateTimeOffset.Now;
                    _currentWork = FormatCurrentWork(work);
                    var authorFolder = GetAuthorFolder(_platformDownloadRoot, work);
                    _currentAuthorId = work.AuthorId;
                    _currentAuthorName = work.AuthorName;
                    _currentAuthorAvatarUrl = work.AuthorAvatarUrl;
                    _currentAuthorDirectory = authorFolder;
                    _currentCoverUrl = work.Assets
                        .FirstOrDefault(x => x.Type == MediaAssetType.Cover)?
                        .CandidateUrls.FirstOrDefault();
                    _currentSourceUrl = work.SourceUrl;
                    PublishProgress();

                    if (await _index.IsCompletedAsync(
                            authorFolder,
                            work,
                            _downloadOptions,
                            cancellationToken))
                    {
                        await RegisterTouchedAuthorAsync(work, authorFolder);

                        if (RegisterCompletedDuplicate(work.WorkId))
                            break;
                        continue;
                    }

                    try
                    {
                        var resolvedWork = await adapter.ResolveWorkAsync(
                            work,
                            _browser,
                            cancellationToken);
                        if (resolvedWork is null)
                        {
                            _consecutiveCompletedDuplicates = 0;
                            Interlocked.Increment(ref _failedCount);
                            RaiseLog(RuntimeLocalization.Format("Log.Crawl.NoMediaDetail", "作品详情未返回有效媒体，跳过：{0}", work.WorkId));
                            PublishProgress();
                            continue;
                        }

                        if (!string.Equals(
                                resolvedWork.AuthorId,
                                _sessionAuthorId,
                                StringComparison.Ordinal))
                        {
                            _consecutiveCompletedDuplicates = 0;
                            Interlocked.Increment(ref _failedCount);
                            RaiseLog(RuntimeLocalization.Format(
                                "Log.Crawl.DetailAuthorMismatch",
                                "作品详情作者不一致，已阻止下载：{0}，详情作者 UID {1}，目标 UID {2}。",
                                resolvedWork.WorkId, resolvedWork.AuthorId, _sessionAuthorId));
                            PublishProgress();
                            continue;
                        }

                        if (!resolvedWork.Assets.Any(asset =>
                                asset.Type is MediaAssetType.Video or MediaAssetType.Image))
                        {
                            _consecutiveCompletedDuplicates = 0;
                            Interlocked.Increment(ref _failedCount);
                            RaiseLog(RuntimeLocalization.Format("Log.Crawl.DetailNoMedia", "作品详情中没有可下载的视频或图片：{0}", resolvedWork.WorkId));
                            PublishProgress();
                            continue;
                        }

                        work = resolvedWork;
                        _currentWork = FormatCurrentWork(work);
                        _currentAuthorName = work.AuthorName;
                        _currentAuthorAvatarUrl = work.AuthorAvatarUrl;
                        _currentCoverUrl = work.Assets
                            .FirstOrDefault(x => x.Type == MediaAssetType.Cover)?
                            .CandidateUrls.FirstOrDefault();
                        _currentSourceUrl = work.SourceUrl;
                        PublishProgress();
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _consecutiveCompletedDuplicates = 0;
                        Interlocked.Increment(ref _failedCount);
                        RaiseLog(RuntimeLocalization.Format("Log.Crawl.DetailReadFailed", "读取作品详情失败 {0}：{1}", work.WorkId, ex.Message));
                        PublishProgress();
                        continue;
                    }

                    await RegisterTouchedAuthorAsync(work, authorFolder);

                    try
                    {
                        var downloadResult = await _downloader.DownloadWorkAsync(
                            work,
                            authorFolder,
                            _downloadOptions,
                            _personDetectionSessionId,
                            cancellationToken);
                        await _index.MarkCompletedAsync(authorFolder, completionKey, cancellationToken);
                        if (downloadResult.AllSelectedOutputsAlreadyExisted)
                        {
                            if (RegisterCompletedDuplicate(work.WorkId))
                                break;
                        }
                        else
                        {
                            _consecutiveCompletedDuplicates = 0;
                            Interlocked.Increment(ref _downloadedCount);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _consecutiveCompletedDuplicates = 0;
                        Interlocked.Increment(ref _failedCount);
                        RaiseLog(RuntimeLocalization.Format("Log.Crawl.WorkDownloadFailed", "作品下载失败 {0}：{1}", work.WorkId, ex.Message));
                    }
                    finally
                    {
                        ResetDownloadProgress();
                        PublishProgress();
                    }
                }

                if (batch.Works.Count > 0 && !_duplicateStopRequested && _hasMore != false)
                {
                    const int minimumSeconds = 2;
                    var maximumSeconds = Math.Max(minimumSeconds, _downloadOptions.PageDelaySeconds);
                    var pageDelaySeconds = maximumSeconds == minimumSeconds
                        ? minimumSeconds
                        : Random.Shared.NextInt64(minimumSeconds, (long)maximumSeconds + 1);
                    RaiseLog(RuntimeLocalization.Format("Log.Crawl.PageDoneDelay", "本页 {0} 个作品处理完成，随机等待 {1} 秒后加载下一页。", batch.Works.Count, pageDelaySeconds));
                    await DelaySecondsAsync(pageDelaySeconds, cancellationToken);
                }
            }
            finally
            {
                _currentWork = null;
                Interlocked.Decrement(ref _processingCount);
                PublishProgress();
            }
        }
    }

    private async Task<string> RunScrollLoopAsync(ISiteAdapter adapter, CancellationToken cancellationToken)
    {
        const int regularStagnantLimit = 10;
        const int bottomStagnantLimit = 3;
        var stagnantRounds = 0;
        var bottomStagnantRounds = 0;
        var lastHeight = 0d;
        var firstResponseDeadline = DateTimeOffset.Now.AddSeconds(25);
        var directPaginationDisabled = false;
        var directPaginationAnnounced = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            await WaitUntilPipelineIdleAsync(cancellationToken);

            if (_duplicateStopRequested)
                return RuntimeLocalization.Format("Completion.DuplicateThreshold", "已连续发现 {0} 个历史作品，达到设置阈值，采集完成。", _consecutiveCompletedDuplicates);

            if (_responseCount == 0 && DateTimeOffset.Now > firstResponseDeadline)
                return RuntimeLocalization.Get("Completion.NoResponse", "未捕获到作品接口。请确认已登录、当前是作者作品主页，并检查网页是否能正常加载。");

            if (_hasMore == false && DateTimeOffset.Now - _lastResponseAt > TimeSpan.FromSeconds(3))
                return RuntimeLocalization.Get("Completion.NoMore", "接口已返回无更多作品，采集完成。");

            if (!directPaginationDisabled)
            {
                var directOutcome = await TryFetchCursorPageAsync(adapter, cancellationToken);
                if (directOutcome == CursorFetchOutcome.Success)
                {
                    if (!directPaginationAnnounced)
                    {
                        directPaginationAnnounced = true;
                        RaiseLog(RuntimeLocalization.Get(
                            "Log.Crawl.CursorFetchEnabled",
                            "游标直连分页已启用：后续页将跳过页面渲染，直接复用当前登录态请求作品接口。"));
                    }

                    stagnantRounds = 0;
                    bottomStagnantRounds = 0;
                    continue;
                }

                if (directOutcome == CursorFetchOutcome.Failed)
                {
                    directPaginationDisabled = true;
                    RaiseLog(RuntimeLocalization.Get(
                        "Log.Crawl.CursorFetchFallback",
                        "游标直连请求未通过接口校验，可能需要动态签名；本次采集已自动回退页面滚动。"));
                }
            }

            var beforeVersion = Interlocked.Read(ref _responseVersion);
            var beforeDiscovered = _discoveredCount;
            var before = await adapter.GetScrollStateAsync(_browser, cancellationToken);
            await adapter.ScrollNextAsync(_browser, cancellationToken);

            // 已经滚到页面底部时，不再按普通分页等待 18 秒。先立即读取一次滚动状态，
            // 底部采用 3 秒快速观察窗口；若连续多次仍无接口、DOM 或页面高度变化即可结束。
            var afterScroll = await adapter.GetScrollStateAsync(_browser, cancellationToken);
            var fastBottomCheck = afterScroll.IsNearBottom();
            var receivedSomething = await WaitForResponseOrNewWorkAsync(
                beforeVersion,
                beforeDiscovered,
                fastBottomCheck ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(18),
                cancellationToken);

            var after = await adapter.GetScrollStateAsync(_browser, cancellationToken);
            var heightGrew = after.DocumentHeight > Math.Max(lastHeight, before.DocumentHeight) + 20;
            var positionMoved = after.ScrollY > before.ScrollY + 5;
            var domWorksGrew = after.WorkItemCount > before.WorkItemCount;
            lastHeight = Math.Max(lastHeight, after.DocumentHeight);

            if (receivedSomething || heightGrew || domWorksGrew)
            {
                stagnantRounds = 0;
                bottomStagnantRounds = 0;
                continue;
            }

            if (positionMoved && !after.IsNearBottom())
            {
                stagnantRounds = 0;
                bottomStagnantRounds = 0;
                RaiseLog(RuntimeLocalization.Format("Log.Crawl.Scrolled", "页面已向下滚动：{0}，{1}->{2}/{3}，继续寻找下一页触发点。", after.ContainerName, $"{before.ScrollY:0}", $"{after.ScrollY:0}", $"{after.MaxScrollTop:0}"));
                await Task.Delay(600, cancellationToken);
                continue;
            }

            var atBottom = after.IsNearBottom();
            if (atBottom)
            {
                bottomStagnantRounds++;
                stagnantRounds = 0;
            }
            else
            {
                stagnantRounds++;
                bottomStagnantRounds = 0;
            }

            var currentRounds = atBottom ? bottomStagnantRounds : stagnantRounds;
            var currentLimit = atBottom ? bottomStagnantLimit : regularStagnantLimit;
            var noNewContentLog = RuntimeLocalization.Format(
                "Log.Crawl.NoNewContent",
                "本轮滚动没有新增内容（{0}/10）：容器={1}，位置={2}->{3}/{4}，页面作品节点={5}->{6}，是否到底={7}",
                currentRounds, after.ContainerName, $"{before.ScrollY:0}", $"{after.ScrollY:0}",
                $"{after.MaxScrollTop:0}", before.WorkItemCount, after.WorkItemCount, atBottom);
            if (currentLimit != regularStagnantLimit)
            {
                noNewContentLog = noNewContentLog.Replace(
                    $"/{regularStagnantLimit}",
                    $"/{currentLimit}",
                    StringComparison.Ordinal);
            }
            RaiseLog(noNewContentLog);

            if (atBottom && bottomStagnantRounds >= bottomStagnantLimit)
            {
                return RuntimeLocalization.Get("Completion.PageBottom", "页面已到底部并连续多轮无新增作品，已自动判断采集结束。");
            }

            await Task.Delay(atBottom ? 500 : 1_500, cancellationToken);
        }

        return RuntimeLocalization.Get("Status.CaptureStopped", "采集已停止");
    }

    private async Task<bool> WaitForParsedResponseAsync(
        int beforeParsedCount,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _parsedResponseCount) > beforeParsedCount)
                return true;
            await Task.Delay(50, cancellationToken);
        }

        return Volatile.Read(ref _parsedResponseCount) > beforeParsedCount;
    }

    private static async Task DelaySecondsAsync(long seconds, CancellationToken cancellationToken)
    {
        while (seconds > 0)
        {
            var chunkSeconds = Math.Min(seconds, 86_400);
            await Task.Delay(TimeSpan.FromSeconds(chunkSeconds), cancellationToken);
            seconds -= chunkSeconds;
        }
    }

    private async Task<CursorFetchOutcome> TryFetchCursorPageAsync(
        ISiteAdapter adapter,
        CancellationToken cancellationToken)
    {
        var previousRequest = _lastPaginationRequest;
        var previousCursor = _nextCursor;
        if (previousRequest is null
            || string.IsNullOrWhiteSpace(previousCursor)
            || _hasMore == false
            || !adapter.TryCreateCursorRequest(previousRequest, previousCursor, out var nextRequest))
        {
            return CursorFetchOutcome.NotAvailable;
        }

        var beforeVersion = Interlocked.Read(ref _responseVersion);
        var beforeParsedCount = Volatile.Read(ref _parsedResponseCount);
        var beforeDiscovered = _discoveredCount;
        var previousHasMore = _hasMore;

        try
        {
            RaiseLog(
                RuntimeLocalization.Format(
                    "Log.Crawl.CursorFetchPage",
                    "正在使用游标直接请求下一页：预计第 {0} 页",
                    _responseCount + 1)
                + Environment.NewLine
                + $"URL: {nextRequest.Url}");
            var result = await _browser.EvaluatePageAsync(
                CursorFetchScript,
                nextRequest,
                cancellationToken);

            var ok = result.ValueKind == JsonValueKind.Object
                     && result.TryGetProperty("ok", out var okElement)
                     && okElement.ValueKind == JsonValueKind.True;
            if (!ok)
            {
                var status = result.ValueKind == JsonValueKind.Object
                             && result.TryGetProperty("status", out var statusElement)
                    ? statusElement.ToString()
                    : "unknown";
                RaiseLog(RuntimeLocalization.Format(
                    "Log.Crawl.CursorFetchHttpFailed",
                    "游标直连接口返回 HTTP {0}。",
                    status));
                return CursorFetchOutcome.Failed;
            }

            var responseArrived = await WaitForResponseOrNewWorkAsync(
                beforeVersion,
                beforeDiscovered,
                TimeSpan.FromSeconds(12),
                cancellationToken);
            var responseParsed = responseArrived
                                 && await WaitForParsedResponseAsync(
                                     beforeParsedCount,
                                     TimeSpan.FromSeconds(12),
                                     cancellationToken);
            if (responseParsed)
                await WaitUntilPipelineIdleAsync(cancellationToken);

            var cursorAdvanced = !string.IsNullOrWhiteSpace(_nextCursor)
                                 && !string.Equals(
                                     _nextCursor,
                                     previousCursor,
                                     StringComparison.Ordinal);
            var reachedEnd = _hasMore == false;
            var discoveredNewWork = _discoveredCount > beforeDiscovered;
            if (responseParsed && (cursorAdvanced || reachedEnd || discoveredNewWork))
                return CursorFetchOutcome.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RaiseLog(RuntimeLocalization.Format(
                "Log.Crawl.CursorFetchError",
                "游标直连请求失败：{0}",
                ex.Message));
        }

        _nextCursor = previousCursor;
        _hasMore = previousHasMore;
        _lastPaginationRequest = previousRequest;
        return CursorFetchOutcome.Failed;
    }

    private async Task WaitUntilPipelineIdleAsync(CancellationToken cancellationToken)
    {
        while (Volatile.Read(ref _processingCount) > 0)
            await Task.Delay(250, cancellationToken);
    }

    private async Task<bool> WaitForResponseOrNewWorkAsync(
        long beforeVersion,
        int beforeDiscovered,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Read(ref _responseVersion) > beforeVersion || _discoveredCount > beforeDiscovered)
                return true;
            await Task.Delay(250, cancellationToken);
        }

        return false;
    }

    private void ResetState()
    {
        _sessionSeen.Clear();
        _persistedAuthorMetadata.Clear();
        _touchedAuthors.Clear();
        _responseCount = 0;
        _totalWorkCount = null;
        _discoveredCount = 0;
        _downloadedCount = 0;
        _skippedCount = 0;
        _failedCount = 0;
        _processingCount = 0;
        _parsedResponseCount = 0;
        _responseVersion = 0;
        _hasMore = null;
        _nextCursor = null;
        _lastPaginationRequest = null;
        _douyinProfileResponseUrl = null;
        _currentWork = null;
        _lastResponseAt = DateTimeOffset.Now;
        _lastNewWorkAt = DateTimeOffset.Now;
        _consecutiveCompletedDuplicates = 0;
        _duplicateStopRequested = false;
        _currentAuthorId = null;
        _currentAuthorName = null;
        _currentAuthorAvatarUrl = null;
        _currentAuthorDirectory = null;
        _currentCoverUrl = null;
        _currentSourceUrl = null;
        ResetDownloadProgress();
        _sessionAuthorId = null;
        _sessionAuthorName = null;
        _personDetectionSessionId = null;
        _capturePageUrl = string.Empty;
        _platformDownloadRoot = string.Empty;
    }

    private void CleanupCapture()
    {
        _browser.ResponseReceived -= OnBrowserResponse;
        _activeAdapter = null;
        _sessionAuthorId = null;
        _sessionAuthorName = null;
        _personDetectionSessionId = null;
        _capturePageUrl = string.Empty;
        _downloadOptions = new CrawlerDownloadOptions();
        _channel = null;
        _captureCts?.Dispose();
        _captureCts = null;
    }

    private void PublishProgress()
    {
        bool isDownloading;
        bool isDownloadIndeterminate;
        double downloadProgressPercent;
        string? downloadProgressText;
        lock (_downloadProgressGate)
        {
            isDownloading = _isDownloading;
            isDownloadIndeterminate = _isDownloadIndeterminate;
            downloadProgressPercent = _downloadProgressPercent;
            downloadProgressText = _downloadProgressText;
        }

        ProgressChanged?.Invoke(this, new CrawlProgressSnapshot(
            _responseCount,
            _totalWorkCount,
            _discoveredCount,
            _downloadedCount,
            _skippedCount,
            _failedCount,
            _currentWork,
            Volatile.Read(ref _processingCount) > 0,
            _currentAuthorId,
            _currentAuthorName,
            _currentAuthorAvatarUrl,
            _currentAuthorDirectory,
            _currentCoverUrl,
            _currentSourceUrl)
        {
            IsDownloading = isDownloading,
            IsDownloadIndeterminate = isDownloadIndeterminate,
            DownloadProgressPercent = downloadProgressPercent,
            DownloadProgressText = downloadProgressText
        });
    }

    private async Task TryRecoverDouyinTotalWorkCountAsync(
        ISiteAdapter adapter,
        CancellationToken cancellationToken)
    {
        if (_totalWorkCount.HasValue
            || !adapter.Id.Equals("douyin", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var pageCountResult = await _browser.EvaluatePageAsync(
                """
                () => {
                    const parseCount = value => {
                        const text = String(value || '').trim().replaceAll(',', '');
                        const match = text.match(/^(\d+(?:\.\d+)?)(万|w)?$/i);
                        if (!match)
                            return null;
                        const number = Number(match[1]);
                        if (!Number.isFinite(number))
                            return null;
                        return Math.round(number * (match[2] ? 10000 : 1));
                    };

                    for (const element of document.querySelectorAll('span, a, div')) {
                        if (element.childElementCount !== 0)
                            continue;
                        const match = (element.textContent || '').trim().match(/^作品\s*([\d,.]+(?:万|w)?)/i);
                        if (!match)
                            continue;
                        const count = parseCount(match[1]);
                        if (count !== null)
                            return count;
                    }

                    const bodyMatch = (document.body?.innerText || '')
                        .match(/(?:^|\n)\s*作品\s*([\d,.]+(?:万|w)?)/i);
                    if (bodyMatch) {
                        const count = parseCount(bodyMatch[1]);
                        if (count !== null)
                            return count;
                    }

                    const pathParts = location.pathname.split('/').filter(Boolean);
                    const secUserId = pathParts[0] === 'user' && pathParts.length > 1
                        ? decodeURIComponent(pathParts[1])
                        : '';
                    if (secUserId) {
                        for (const script of document.scripts) {
                            const text = script.textContent || '';
                            let index = text.indexOf(secUserId);
                            while (index >= 0) {
                                const nearby = text.slice(Math.max(0, index - 4000), index + secUserId.length + 4000);
                                const match = nearby.match(/(?:\\?["'])aweme_count(?:\\?["'])\s*:\s*(\d+)/)
                                    || nearby.match(/(?:\\?["'])awemeCount(?:\\?["'])\s*:\s*(\d+)/);
                                if (match)
                                    return Number(match[1]);
                                index = text.indexOf(secUserId, index + secUserId.length);
                            }
                        }
                    }

                    return null;
                }
                """,
                cancellationToken);
            if (pageCountResult.ValueKind == JsonValueKind.Number
                && pageCountResult.TryGetInt32(out var pageCount)
                && pageCount >= 0)
            {
                SetTotalWorkCount(pageCount);
                PublishProgress();
                return;
            }

            var resourceUrl = _douyinProfileResponseUrl;
            if (string.IsNullOrWhiteSpace(resourceUrl))
            {
                var resourceUrlResult = await _browser.EvaluatePageAsync(
                    """
                    () => {
                        const matches = performance
                            .getEntriesByType('resource')
                            .map(entry => String(entry.name || ''))
                            .filter(url => url.includes('/aweme/v1/web/user/profile/other'));
                        return matches.length === 0 ? '' : matches[matches.length - 1];
                    }
                    """,
                    cancellationToken);
                resourceUrl = resourceUrlResult.ValueKind == JsonValueKind.String
                    ? resourceUrlResult.GetString()
                    : null;
            }

            if (string.IsNullOrWhiteSpace(resourceUrl))
                return;

            var responseJson = await _browser.FetchTextAsync(resourceUrl, cancellationToken);
            var totalWorkCount = adapter.TryReadTotalWorkCount(
                resourceUrl,
                responseJson,
                _capturePageUrl,
                requestBody: null);
            if (totalWorkCount.HasValue)
            {
                SetTotalWorkCount(totalWorkCount.Value);
                PublishProgress();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RaiseLog(RuntimeLocalization.Format(
                "Log.Crawl.TotalWorkCountRecoveryFailed",
                "补读抖音作者作品数失败，将继续正常采集：{0}",
                ex.Message));
        }
    }

    private void SetTotalWorkCount(int totalWorkCount)
    {
        if (_totalWorkCount == totalWorkCount)
            return;

        _totalWorkCount = totalWorkCount;
        RaiseLog(RuntimeLocalization.Format(
            "Log.Crawl.TotalWorkCountRead",
            "已读取作者作品数：{0}",
            totalWorkCount));
    }

    private void OnDownloadProgressChanged(object? sender, MediaTransferProgress progress)
    {
        lock (_downloadProgressGate)
        {
            if (!progress.IsActive)
            {
                _isDownloading = false;
                _isDownloadIndeterminate = false;
                _downloadProgressPercent = 0;
                _downloadProgressText = null;
            }
            else
            {
                _isDownloading = true;
                _isDownloadIndeterminate = !progress.Percent.HasValue;
                _downloadProgressPercent = Math.Clamp(progress.Percent ?? 0, 0, 100);
                _downloadProgressText = FormatDownloadProgress(progress);
            }
        }

        PublishProgress();
    }

    private void ResetDownloadProgress()
    {
        lock (_downloadProgressGate)
        {
            _isDownloading = false;
            _isDownloadIndeterminate = false;
            _downloadProgressPercent = 0;
            _downloadProgressText = null;
        }
    }

    private static string FormatDownloadProgress(MediaTransferProgress progress)
    {
        var parts = new List<string> { progress.FileName };
        if (!string.IsNullOrWhiteSpace(progress.Stage))
            parts.Add(progress.Stage);

        if (progress.Percent.HasValue)
            parts.Add($"{progress.Percent.Value:0.0}%");

        if (progress.TotalBytes is > 0)
        {
            parts.Add($"{FormatBytes(progress.BytesReceived)} / {FormatBytes(progress.TotalBytes.Value)}");
        }
        else if (progress.BytesReceived > 0)
        {
            parts.Add(FormatBytes(progress.BytesReceived));
        }

        if (progress.BytesPerSecond > 0)
            parts.Add($"{FormatBytes((long)progress.BytesPerSecond)}/s");

        return string.Join(" · ", parts);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var value = Math.Max(0, bytes);
        var unitIndex = 0;
        var displayValue = (double)value;
        while (displayValue >= 1024 && unitIndex < units.Length - 1)
        {
            displayValue /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{displayValue:0} {units[unitIndex]}"
            : $"{displayValue:0.##} {units[unitIndex]}";
    }

    private async Task RegisterTouchedAuthorAsync(
        WorkItem work,
        string authorFolder)
    {
        var touchedAuthorKey = $"{work.PlatformId}:{work.AuthorId}";
        var firstTouch = _touchedAuthors.TryAdd(
            touchedAuthorKey,
            (work.PlatformId, work.AuthorId, authorFolder));
        var metadataFingerprint = string.Join(
            "\n",
            work.AuthorName,
            work.AuthorAvatarUrl ?? string.Empty,
            work.AuthorPageUrl ?? work.SourceUrl,
            authorFolder);
        var metadataChanged = !_persistedAuthorMetadata.TryGetValue(
                                  touchedAuthorKey,
                                  out var persistedFingerprint)
                              || !persistedFingerprint.Equals(
                                  metadataFingerprint,
                                  StringComparison.Ordinal);

        if (!firstTouch && !metadataChanged)
            return;

        try
        {
            await _history.UpsertAuthorMetadataAsync(
                work,
                authorFolder,
                _downloadOptions.UpdateAuthorNickname
                && Path.GetFileName(authorFolder).Equals(
                    FileNameHelper.BuildAuthorFolderName(work.AuthorName, work.AuthorId),
                    StringComparison.Ordinal),
                CancellationToken.None);
            _persistedAuthorMetadata[touchedAuthorKey] = metadataFingerprint;
        }
        catch (Exception ex)
        {
            RaiseLog(RuntimeLocalization.Format("Log.History.RegisterFailed", "登记作者下载历史失败：{0}", ex.Message));
        }
    }

    private async Task RefreshTouchedAuthorStatsAsync()
    {
        foreach (var author in _touchedAuthors.Values)
        {
            await _history.RefreshAuthorStatsAsync(
                author.PlatformId,
                author.UserId,
                author.Folder,
                CancellationToken.None);
        }
    }

    private static string FormatCurrentWork(WorkItem work)
        => string.IsNullOrWhiteSpace(work.Description)
            ? work.AuthorName
            : $"{work.AuthorName} - {work.Description}";

    private string GetAuthorFolder(string downloadRoot, WorkItem work)
    {
        if (string.Equals(_currentAuthorId, work.AuthorId, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(_currentAuthorDirectory))
        {
            return _currentAuthorDirectory;
        }

        var preferredFolder = Path.Combine(
            downloadRoot,
            FileNameHelper.BuildAuthorFolderName(work.AuthorName, work.AuthorId));
        var resolution = AuthorFolderResolver.ResolveDetailed(
            downloadRoot,
            work.AuthorName,
            work.AuthorId,
            _downloadOptions.UpdateAuthorNickname);
        if (resolution.Renamed)
        {
            RaiseLog(RuntimeLocalization.Format(
                "Log.Download.AuthorFolderRenamed",
                "检测到作者昵称变化，已将下载目录重命名：{0} → {1}",
                resolution.PreviousFolderPath ?? string.Empty,
                resolution.FolderPath));
        }
        else if (!string.IsNullOrWhiteSpace(resolution.RenameError))
        {
            RaiseLog(RuntimeLocalization.Format(
                "Log.Download.AuthorFolderRenameFailed",
                "更新作者目录名称失败，将继续使用旧目录并保留历史昵称：{0}",
                resolution.RenameError));
        }
        else if (!resolution.FolderPath.Equals(preferredFolder, StringComparison.Ordinal))
        {
            RaiseLog(RuntimeLocalization.Format(
                "Log.Download.ExistingAuthorFolderReused",
                "已按作者 ID 找到原下载目录，将继续使用：{0}",
                resolution.FolderPath));
        }

        return resolution.FolderPath;
    }

    private bool RegisterCompletedDuplicate(string workId)
    {
        Interlocked.Increment(ref _skippedCount);
        _consecutiveCompletedDuplicates++;
        RaiseLog(RuntimeLocalization.Format(
            "Log.Download.DuplicateSkipped",
            "已下载过，跳过：{0}（连续重复 {1}）",
            workId,
            _consecutiveCompletedDuplicates));
        PublishProgress();

        if (!_downloadOptions.StopOnDuplicateThreshold
            || _consecutiveCompletedDuplicates < Math.Max(1, _downloadOptions.DuplicateStopThreshold))
        {
            return false;
        }

        _duplicateStopRequested = true;
        RaiseLog(RuntimeLocalization.Format(
            "Log.Crawl.DuplicateThresholdReached",
            "已连续发现 {0} 个历史作品，达到停止阈值。",
            _consecutiveCompletedDuplicates));
        return true;
    }

    private void RaiseLog(string message)
        => Log?.Invoke(this, $"[{DateTime.Now:HH:mm:ss}] {message}");

    public async ValueTask DisposeAsync()
    {
        Stop();
        CleanupCapture();
        _downloader.ProgressChanged -= OnDownloadProgressChanged;
        await _downloader.DisposeAsync();
    }

    private sealed record CapturedResponse(
        string ResponseUrl,
        string Json,
        string PageUrl,
        string? RequestBody,
        BrowserRequestSnapshot Request);

    private enum CursorFetchOutcome
    {
        NotAvailable,
        Success,
        Failed
    }
}
