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

    // 每一页（一次列表响应）中的所有作品处理完毕后，再随机等待 3–10 秒，
    // 然后才允许滚动或请求下一页。作品之间、详情解析前和媒体下载前均不等待。
    private const int PageDelayMinMilliseconds = 3_000;
    private const int PageDelayMaxMillisecondsExclusive = 10_001;

    private CancellationTokenSource? _captureCts;
    private Channel<CapturedResponse>? _channel;
    private ISiteAdapter? _activeAdapter;
    private string _downloadRoot = string.Empty;
    private string _platformDownloadRoot = string.Empty;
    private string _capturePageUrl = string.Empty;
    private CrawlerDownloadOptions _downloadOptions = new();
    private int _responseCount;
    private int _discoveredCount;
    private int _downloadedCount;
    private int _skippedCount;
    private int _failedCount;
    private int _processingCount;
    private int _parsedResponseCount;
    private long _responseVersion;
    private bool? _hasMore;
    private string? _nextCursor;
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
            throw new InvalidOperationException("采集任务已经在运行。");

        var adapter = _registry.GetRequired(platformId);

        if (!_browser.IsStarted)
            throw new InvalidOperationException("请先打开浏览器。");

        // Playwright 不会在用户切换 Chromium 标签页时自动更新 _page。点击开始采集时
        // 主动检测 document.hasFocus()/visibilityState，确保使用屏幕上当前选中的标签页。
        var foregroundPageUrl = await _browser.SelectForegroundPageAsync(cancellationToken);
        if (!adapter.CanHandlePage(foregroundPageUrl))
            throw new InvalidOperationException("当前活动标签页不是该平台的作者主页。请切换到作者主页标签，再点击开始采集。");

        Directory.CreateDirectory(downloadRoot);
        ResetState();

        _activeAdapter = adapter;
        // 整个采集会话始终绑定到点击“开始采集”时的作者主页。
        // 后续即使页面发生瞬时导航、旧请求延迟返回，也不能改变目标作者。
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
        var completionMessage = "采集完成";
        var personDetectionTicket = PersonDetectionSessionTicket.Empty(Guid.Empty);
        string? completedAuthorId = null;
        string? completedAuthorName = null;
        string? completedAuthorFolder = null;
        var completedDownloadedCount = 0;

        RaiseLog($"开始采集：{adapter.DisplayName}");
        RaiseLog("将刷新当前作者主页，以重新触发第一页作品接口。");
        PublishProgress();

        var consumerTask = ConsumeResponsesAsync(_channel.Reader, token);
        var pageLocked = false;
        try
        {
            await _browser.SetCaptureLockAsync(true, token);
            pageLocked = true;
            // 锁定完成后以浏览器服务最终选中的 URL 为准，防止点击按钮瞬间发生标签切换。
            _capturePageUrl = _browser.CurrentUrl;
            if (!adapter.CanHandlePage(_capturePageUrl))
            {
                throw new InvalidOperationException(
                    "锁定的当前标签页不是该平台的作者主页。请停止后切换到作者主页标签再试。");
            }

            RaiseLog($"当前采集标签页：{_capturePageUrl}");
            RaiseLog("采集标签页已锁定：页面操作、新标签页和误导航将被阻止。");
            await _browser.ReloadAsync(token);
            RaiseLog("等待第一页作品接口和作品列表完成加载……");
            await WaitForResponseOrNewWorkAsync(0, 0, TimeSpan.FromSeconds(20), token);
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
            completionMessage = "采集已停止";
            Completed?.Invoke(this, completionMessage);
        }
        catch (OperationCanceledException ex)
        {
            // Playwright 页面或上下文被关闭时，有些调用会以 TaskCanceledException 的形式结束。
            // 只有采集令牌真的被取消，才能视为用户点击“停止”；否则应保留为采集失败。
            try { _captureCts?.Cancel(); } catch (ObjectDisposedException) { }
            _channel.Writer.TryComplete();
            try { await consumerTask; } catch (OperationCanceledException) { }
            throw new InvalidOperationException(
                "浏览器操作被意外取消。请确认采集期间作者页面没有被关闭，或重新打开作者主页后再试。",
                ex);
        }
        catch
        {
            // 初始化、刷新或滚动阶段异常时也必须关闭响应通道并等待消费者退出。
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
                    RaiseLog(
                        $"作者资源下载阶段已完成，后台仍有 {personDetectionTicket.PendingCount} 张图片等待人像检测。" +
                        "现在可以开始采集其他作者。");
                }
            }

            try
            {
                await RefreshTouchedAuthorStatsAsync();
            }
            catch (Exception ex)
            {
                RaiseLog($"更新下载历史统计失败：{ex.Message}");
            }

            if (pageLocked)
            {
                try
                {
                    await _browser.SetCaptureLockAsync(false, CancellationToken.None);
                    RaiseLog("采集标签页已解锁。");
                }
                catch (Exception ex)
                {
                    RaiseLog($"解除标签页锁定失败：{ex.Message}");
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
        CancellationToken cancellationToken = default)
        => _downloader.RecoverPendingPersonDetectionAsync(downloadRoot, cancellationToken);

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

            var text = await response.ReadBodyAsync(cts.Token);
            if (string.IsNullOrWhiteSpace(text))
                return;

            // 作者资料等辅助响应必须在浏览器事件线程中立即解析。若与作品列表一起进入
            // 单消费者队列，它可能会被一整页视频详情和下载任务阻塞，导致首个作品写入
            // History.json 时头像仍为空。
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
                new CapturedResponse(response.Url, text, response.PageUrl, response.RequestPostData),
                cts.Token);
            RaiseLog($"捕获作品响应：第 {_responseCount} 页");
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
            RaiseLog($"读取接口响应失败：{ex.Message}");
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
                    RaiseLog($"解析作品响应失败：{ex.Message}");
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(batch.Diagnostic))
                    RaiseLog(batch.Diagnostic);

                if (batch.HasMore.HasValue)
                    _hasMore = batch.HasMore;
                _nextCursor = batch.Cursor;
                Interlocked.Increment(ref _parsedResponseCount);

                foreach (var listedWork in batch.Works)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var work = listedWork;

                    // 在历史完成索引判断之前补全作者头像等轻量资料。B 站的 acc/info
                    // 与作品列表可能并发返回，这一步只等待作者资料，不请求视频详情。
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
                        RaiseLog($"补充作者资料失败，将继续使用列表信息：{ex.Message}");
                    }

                    // 一次采集任务只允许一个作者。即使平台接口以后出现字段变化，
                    // 或某条异常数据绕过了站点适配器校验，也不能创建第二个作者目录。
                    if (_sessionAuthorId is null)
                    {
                        _sessionAuthorId = work.AuthorId;
                        _sessionAuthorName = work.AuthorName;
                        RaiseLog($"本次采集已绑定作者：{work.AuthorName}（UID {work.AuthorId}）");
                    }
                    else if (!string.Equals(_sessionAuthorId, work.AuthorId, StringComparison.Ordinal))
                    {
                        RaiseLog(
                            $"已阻止其他作者作品进入下载队列：{work.AuthorName}（UID {work.AuthorId}），" +
                            $"当前目标为 {_sessionAuthorName}（UID {_sessionAuthorId}）。");
                        continue;
                    }

                    var sessionKey = $"{work.PlatformId}:{work.WorkId}";
                    if (!_sessionSeen.Add(sessionKey))
                        continue;

                    // 完成索引包含作者 UID、命名模式和全部可选处理开关。
                    // 索引键不再携带 v3/v4 等版本前缀；旧索引会在首次读取时自动迁移。
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

                    if (await _index.IsCompletedAsync(authorFolder, completionKey, cancellationToken))
                    {
                        // 即使全部作品都已下载，也要把新捕获的作者头像写回历史记录。
                        await RegisterTouchedAuthorAsync(work, authorFolder);

                        Interlocked.Increment(ref _skippedCount);
                        _consecutiveCompletedDuplicates++;
                        RaiseLog($"已下载过，跳过：{work.WorkId}（连续重复 {_consecutiveCompletedDuplicates}）");
                        PublishProgress();

                        if (_downloadOptions.StopOnDuplicateThreshold
                            && _consecutiveCompletedDuplicates >= Math.Max(1, _downloadOptions.DuplicateStopThreshold))
                        {
                            _duplicateStopRequested = true;
                            RaiseLog($"已连续发现 {_consecutiveCompletedDuplicates} 个历史作品，达到停止阈值。 ");
                            break;
                        }
                        continue;
                    }

                    _consecutiveCompletedDuplicates = 0;

                    // 小红书和 Pinterest 的作者列表主要提供作品 ID；真实媒体地址
                    // 分别位于作品详情文档中。其他平台默认原样返回，不增加请求。
                    try
                    {
                        var resolvedWork = await adapter.ResolveWorkAsync(
                            work,
                            _browser,
                            cancellationToken);
                        if (resolvedWork is null)
                        {
                            Interlocked.Increment(ref _failedCount);
                            RaiseLog($"作品详情未返回有效媒体，跳过：{work.WorkId}");
                            PublishProgress();
                            continue;
                        }

                        if (!string.Equals(
                                resolvedWork.AuthorId,
                                _sessionAuthorId,
                                StringComparison.Ordinal))
                        {
                            Interlocked.Increment(ref _failedCount);
                            RaiseLog(
                                $"作品详情作者不一致，已阻止下载：{resolvedWork.WorkId}，" +
                                $"详情作者 UID {resolvedWork.AuthorId}，目标 UID {_sessionAuthorId}。");
                            PublishProgress();
                            continue;
                        }

                        if (!resolvedWork.Assets.Any(asset =>
                                asset.Type is MediaAssetType.Video or MediaAssetType.Image))
                        {
                            Interlocked.Increment(ref _failedCount);
                            RaiseLog($"作品详情中没有可下载的视频或图片：{resolvedWork.WorkId}");
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
                        Interlocked.Increment(ref _failedCount);
                        RaiseLog($"读取作品详情失败 {work.WorkId}：{ex.Message}");
                        PublishProgress();
                        continue;
                    }

                    // 在真正开始下载前就登记本次作者。若前面的作品因已下载而只登记了
                    // 列表元数据，则首个成功解析详情的作品会再更新一次完整作者资料。
                    await RegisterTouchedAuthorAsync(work, authorFolder);

                    try
                    {
                        await _downloader.DownloadWorkAsync(
                            work,
                            _platformDownloadRoot,
                            _downloadOptions,
                            _personDetectionSessionId,
                            cancellationToken);
                        await _index.MarkCompletedAsync(authorFolder, completionKey, cancellationToken);
                        Interlocked.Increment(ref _downloadedCount);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Interlocked.Increment(ref _failedCount);
                        RaiseLog($"作品下载失败 {work.WorkId}：{ex.Message}");
                    }
                    finally
                    {
                        ResetDownloadProgress();
                        PublishProgress();
                    }
                }

                // 本页全部作品（包括已下载跳过项和失败项）处理完成后再统一等待。
                // 把等待保留在 _processingCount > 0 的区间内，滚动线程会在这里等到结束，
                // 从而确保延时发生在“本页完成”和“加载下一页”之间，而不是作品之间。
                if (batch.Works.Count > 0 && !_duplicateStopRequested && _hasMore != false)
                {
                    var pageDelay = Random.Shared.Next(
                        PageDelayMinMilliseconds,
                        PageDelayMaxMillisecondsExclusive);
                    RaiseLog($"本页 {batch.Works.Count} 个作品处理完成，随机等待 {pageDelay / 1000d:0.0} 秒后加载下一页。");
                    await Task.Delay(pageDelay, cancellationToken);
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
        var stagnantRounds = 0;
        var lastHeight = 0d;
        var firstResponseDeadline = DateTimeOffset.Now.AddSeconds(25);

        while (!cancellationToken.IsCancellationRequested)
        {
            await WaitUntilPipelineIdleAsync(cancellationToken);

            if (_duplicateStopRequested)
                return $"已连续发现 {_consecutiveCompletedDuplicates} 个历史作品，达到设置阈值，采集完成。";

            if (_responseCount == 0 && DateTimeOffset.Now > firstResponseDeadline)
                return "未捕获到作品接口。请确认已登录、当前是作者作品主页，并检查网页是否能正常加载。";

            if (_hasMore == false && DateTimeOffset.Now - _lastResponseAt > TimeSpan.FromSeconds(3))
                return "接口已返回无更多作品，采集完成。";

            var beforeVersion = Interlocked.Read(ref _responseVersion);
            var beforeDiscovered = _discoveredCount;
            var before = await adapter.GetScrollStateAsync(_browser, cancellationToken);
            await adapter.ScrollNextAsync(_browser, cancellationToken);

            var receivedSomething = await WaitForResponseOrNewWorkAsync(
                beforeVersion,
                beforeDiscovered,
                TimeSpan.FromSeconds(18),
                cancellationToken);

            var after = await adapter.GetScrollStateAsync(_browser, cancellationToken);
            var heightGrew = after.DocumentHeight > Math.Max(lastHeight, before.DocumentHeight) + 20;
            var positionMoved = after.ScrollY > before.ScrollY + 5;
            var domWorksGrew = after.WorkItemCount > before.WorkItemCount;
            lastHeight = Math.Max(lastHeight, after.DocumentHeight);

            if (receivedSomething || heightGrew || domWorksGrew)
            {
                stagnantRounds = 0;
                continue;
            }

            // 滚动位置确实向下移动，但还没到加载触发点，不能算作“无新增”。
            if (positionMoved && !after.IsNearBottom())
            {
                stagnantRounds = 0;
                RaiseLog(
                    $"页面已向下滚动：{after.ContainerName}，" +
                    $"{before.ScrollY:0}->{after.ScrollY:0}/{after.MaxScrollTop:0}，继续寻找下一页触发点。");
                await Task.Delay(600, cancellationToken);
                continue;
            }

            stagnantRounds++;
            RaiseLog(
                $"本轮滚动没有新增内容（{stagnantRounds}/10）：" +
                $"容器={after.ContainerName}，位置={before.ScrollY:0}->{after.ScrollY:0}/{after.MaxScrollTop:0}，" +
                $"页面作品节点={before.WorkItemCount}->{after.WorkItemCount}，" +
                $"是否到底={after.IsNearBottom()}");

            if (stagnantRounds >= 10
                && after.IsNearBottom()
                && DateTimeOffset.Now - _lastResponseAt > TimeSpan.FromSeconds(45)
                && DateTimeOffset.Now - _lastNewWorkAt > TimeSpan.FromSeconds(45))
            {
                return "页面已到底部并连续多轮无新增作品，已自动判断采集结束。";
            }

            await Task.Delay(1_500, cancellationToken);
        }

        return "采集已停止";
    }

    private async Task WaitForParsedResponseAsync(
        int beforeParsedCount,
        CancellationToken cancellationToken)
    {
        while (Volatile.Read(ref _parsedResponseCount) <= beforeParsedCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(50, cancellationToken);
        }
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
        _discoveredCount = 0;
        _downloadedCount = 0;
        _skippedCount = 0;
        _failedCount = 0;
        _processingCount = 0;
        _parsedResponseCount = 0;
        _responseVersion = 0;
        _hasMore = null;
        _nextCursor = null;
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
            // 历史登记是很短的本地文件操作，不使用采集取消令牌，
            // 避免“停止”恰好发生时作者记录还没建立或头像尚未写入。
            await _history.UpsertDownloadedAuthorAsync(
                work,
                authorFolder,
                CancellationToken.None);
            _persistedAuthorMetadata[touchedAuthorKey] = metadataFingerprint;
        }
        catch (Exception ex)
        {
            // 历史写入失败不能阻断媒体下载；任务结束时还会再次刷新统计。
            RaiseLog($"登记作者下载历史失败：{ex.Message}");
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

    private static string GetAuthorFolder(string downloadRoot, WorkItem work)
        => Path.Combine(downloadRoot, FileNameHelper.BuildAuthorFolderName(work.AuthorName, work.AuthorId));

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
        string? RequestBody);
}
