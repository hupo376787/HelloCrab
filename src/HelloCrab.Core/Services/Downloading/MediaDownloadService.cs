using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Diagnostics;
using HelloCrab.Core.Models;
using HelloCrab.Core.Services.Browser;
using HelloCrab.Core.Services.Images;
using HelloCrab.Core.Services.Media;
using HelloCrab.Core.Utilities;

namespace HelloCrab.Core.Services.Downloading;

public sealed class MediaDownloadService : IAsyncDisposable
{
    private readonly IBrowserAutomationService _browser;
    private readonly HttpClient _httpClient;
    private readonly IMediaProcessor _mediaProcessor;
    private readonly PersonDetectionQueueService _personDetectionQueue;
    private readonly DownloadRateLimiter _downloadRateLimiter = new();
    private int _timestampWarningLogged;
    private int _ffmpegUnavailableWarningLogged;
    private int _hlsBrowserFallbackLogged;

    public MediaDownloadService(
        IBrowserAutomationService browser,
        IMediaProcessor mediaProcessor,
        IPersonImageDetector personImageDetector)
    {
        _browser = browser;
        _mediaProcessor = mediaProcessor;
        _personDetectionQueue = new PersonDetectionQueueService(personImageDetector);
        _personDetectionQueue.Log += (_, message) => RaiseLog(message);
        _httpClient = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            UseProxy = true,
            Proxy = HttpClient.DefaultProxy
        })
        {
            Timeout = TimeSpan.FromMinutes(30)
        };
    }

    public event EventHandler<string>? Log;
    public event EventHandler<MediaTransferProgress>? ProgressChanged;

    public void BeginDownloadSession(decimal speedLimitMBps)
        => _downloadRateLimiter.SetLimit(speedLimitMBps, resetSchedule: true);

    public void BeginPersonDetectionSession(Guid sessionId)
        => _personDetectionQueue.BeginSession(sessionId);

    public PersonDetectionSessionTicket CompletePersonDetectionSession(Guid sessionId)
        => _personDetectionQueue.CompleteSession(sessionId);

    public Task<PersonDetectionSessionResult> RecoverPendingPersonDetectionAsync(
        string downloadRoot,
        CancellationToken cancellationToken = default)
        => _personDetectionQueue.RecoverPendingFilesAsync(downloadRoot, cancellationToken);

    public async Task DownloadWorkAsync(
        WorkItem work,
        string downloadRoot,
        CrawlerDownloadOptions options,
        Guid? personDetectionSessionId,
        CancellationToken cancellationToken)
    {
        _downloadRateLimiter.SetLimit(options.DownloadSpeedLimitMBps);
        var mediaReferer = work.MediaRefererUrl ?? work.SourceUrl;
        var browserContext = await _browser.GetDownloadContextAsync(cancellationToken);
        if ((work.PlatformId.Equals("xiaohongshu", StringComparison.OrdinalIgnoreCase)
             || work.PlatformId.Equals("weibo", StringComparison.OrdinalIgnoreCase)
             || work.PlatformId.Equals("meipian", StringComparison.OrdinalIgnoreCase)
             || work.PlatformId.Equals("instagram", StringComparison.OrdinalIgnoreCase)
             || work.PlatformId.Equals("bilibili", StringComparison.OrdinalIgnoreCase)
             || work.PlatformId.Equals("tiktok", StringComparison.OrdinalIgnoreCase)
             || work.PlatformId.Equals("pinterest", StringComparison.OrdinalIgnoreCase))
            && Uri.TryCreate(mediaReferer, UriKind.Absolute, out _))
        {
            // 小红书、微博、美篇、Instagram、TikTok、Pinterest 和哔哩哔哩都使用单条作品详情地址作为媒体 Referer。
            var originalContext = browserContext;
            browserContext = new BrowserDownloadContext(
                originalContext.UserAgent,
                mediaReferer,
                (url, token) => originalContext.GetCookiesAsync(url, token));
        }

        // 使用接口中的 author.uid，不再截短 UID。
        var authorFolderName = FileNameHelper.BuildAuthorFolderName(work.AuthorName, work.AuthorId);
        var authorFolder = Path.Combine(downloadRoot, authorFolderName);
        Directory.CreateDirectory(authorFolder);

        var publishedAt = work.CreateTime > 0
            ? DateTimeOffset.FromUnixTimeSeconds(work.CreateTime)
            : DateTimeOffset.Now;
        var localPublishedAt = publishedAt.ToLocalTime();
        var baseName = FileNameHelper.BuildWorkBaseName(
            localPublishedAt,
            work.Description,
            work.WorkId,
            options.IncludeWorkId);

        var primaryAssets = work.Assets
            .Where(x => x.Type is MediaAssetType.Video or MediaAssetType.Image)
            .OrderBy(x => x.Index)
            .ToArray();
        var cover = work.Assets.FirstOrDefault(x => x.Type == MediaAssetType.Cover);
        var music = work.Assets.FirstOrDefault(x => x.Type == MediaAssetType.Music);
        var requiresDashAudioMerge =
            work.PlatformId.Equals("bilibili", StringComparison.OrdinalIgnoreCase)
            && primaryAssets.Any(x => x.Type == MediaAssetType.Video)
            && music is not null;

        if (requiresDashAudioMerge)
        {
            var selectedVideo = primaryAssets.First(x => x.Type == MediaAssetType.Video);
            var resolution = selectedVideo.Width > 0 && selectedVideo.Height > 0
                ? $"{selectedVideo.Width}x{selectedVideo.Height}"
                : "未知分辨率";
            var codec = string.IsNullOrWhiteSpace(selectedVideo.Codec)
                ? "未知编码"
                : selectedVideo.Codec;
            RaiseLog($"哔哩哔哩已选择最高画质 DASH：{resolution}，{codec}，下载后自动合并音频。");
        }

        if (primaryAssets.Length == 0)
            throw new InvalidOperationException("作品中没有可下载的视频或图片资源。");

        // 单个视频保持原来的无序号文件名；图片作品以及多媒体/混合轮播统一追加 _01、_02。
        var appendSequence = primaryAssets.Length > 1
                             || primaryAssets.Any(x => x.Type == MediaAssetType.Image);
        var sequence = 1;
        var musicRetainedDuringRepair = false;
        foreach (var asset in primaryAssets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var suffix = appendSequence ? $"_{sequence:00}" : string.Empty;
            var targetWithoutExtension = Path.Combine(authorFolder, baseName + suffix);

            if (asset.Type == MediaAssetType.Image)
            {
                var imagePath = await DownloadAssetAsync(
                    asset,
                    targetWithoutExtension,
                    browserContext,
                    publishedAt,
                    cancellationToken,
                    stageForPersonDetection: options.EnablePersonDetection);
                QueuePersonDetection(
                    imagePath,
                    options.EnablePersonDetection,
                    personDetectionSessionId);
            }
            else
            {
                var videoPath = await DownloadAssetAsync(
                    asset,
                    targetWithoutExtension,
                    browserContext,
                    publishedAt,
                    cancellationToken);

                if (options.CheckVideoAudio || requiresDashAudioMerge)
                {
                    var retained = await EnsureVideoHasAudioAsync(
                        videoPath,
                        music,
                        options.DownloadMusic && !musicRetainedDuringRepair
                            ? Path.Combine(authorFolder, baseName + "_music")
                            : null,
                        browserContext,
                        publishedAt,
                        cancellationToken,
                        requireAudioMerge: requiresDashAudioMerge);
                    musicRetainedDuringRepair |= retained;
                }
            }

            sequence++;
        }

        if (options.DownloadCover)
        {
            if (cover is not null)
            {
                var coverPath = await DownloadAssetAsync(
                    cover,
                    Path.Combine(authorFolder, baseName + "_cover"),
                    browserContext,
                    publishedAt,
                    cancellationToken,
                    stageForPersonDetection: options.EnablePersonDetection);
                QueuePersonDetection(
                    coverPath,
                    options.EnablePersonDetection,
                    personDetectionSessionId);
            }
            else
            {
                RaiseLog($"作品未提供封面地址：{work.WorkId}");
            }
        }

        if (options.DownloadMusic && !musicRetainedDuringRepair)
        {
            if (music is not null)
            {
                await DownloadAssetAsync(
                    music,
                    Path.Combine(authorFolder, baseName + "_music"),
                    browserContext,
                    publishedAt,
                    cancellationToken);
            }
            else
            {
                RaiseLog($"作品未提供背景音乐地址：{work.WorkId}");
            }
        }

    }

    private void QueuePersonDetection(
        string imagePath,
        bool enabled,
        Guid? sessionId)
    {
        if (!enabled)
            return;

        if (!imagePath.EndsWith(
                PersonDetectionQueueService.PendingSuffix,
                StringComparison.OrdinalIgnoreCase))
        {
            RaiseLog($"人像检测文件未进入 .pending 状态，已按普通图片保留：{Path.GetFileName(imagePath)}");
            return;
        }

        var finalPath = PersonDetectionQueueService.GetFinalPath(imagePath);
        if (!sessionId.HasValue)
        {
            RestorePendingImage(imagePath, finalPath);
            RaiseLog($"人像检测会话不存在，已恢复并保留图片：{Path.GetFileName(finalPath)}");
            return;
        }

        try
        {
            _personDetectionQueue.Enqueue(sessionId.Value, imagePath, finalPath);
            RaiseLog($"已加入后台人像检测队列：{Path.GetFileName(finalPath)}");
        }
        catch (Exception ex)
        {
            RestorePendingImage(imagePath, finalPath);
            RaiseLog(
                $"加入人像检测队列失败，已恢复并保留图片：{Path.GetFileName(finalPath)}；{ex.Message}");
        }
    }

    private static void RestorePendingImage(string pendingPath, string finalPath)
    {
        try
        {
            if (File.Exists(pendingPath))
                PersonDetectionQueueService.PromotePendingFile(pendingPath, finalPath);
        }
        catch
        {
            // 恢复失败时保留 .pending 文件，程序下次启动会自动重新检测。
        }
    }

    private async Task<string> DownloadAssetAsync(
        MediaAsset asset,
        string targetWithoutExtension,
        BrowserDownloadContext browserContext,
        DateTimeOffset publishedAt,
        CancellationToken cancellationToken,
        bool applyTimestamp = true,
        string? completionLabel = null,
        bool stageForPersonDetection = false)
    {
        Exception? lastError = null;
        foreach (var rawUrl in asset.CandidateUrls)
        {
            var url = WebUtility.HtmlDecode(rawUrl);

            if (asset.Type == MediaAssetType.Video && IsHlsUrl(url))
            {
                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        return await DownloadHlsAssetAsync(
                            url,
                            targetWithoutExtension,
                            browserContext,
                            publishedAt,
                            cancellationToken,
                            applyTimestamp,
                            completionLabel);
                    }
                    catch (FileNotFoundException ex)
                    {
                        // 未安装 FFmpeg 时不重复等待，直接尝试该作品的下一个 MP4 候选地址。
                        lastError = ex;
                        RaiseLog(ex.Message);
                        break;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        lastError = ex;
                        RaiseLog($"HLS 视频下载失败，第 {attempt}/3 次：{ex.Message}");
                        if (attempt < 3)
                            await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
                    }
                }

                continue;
            }

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var request = await CreateRequestAsync(url, browserContext, cancellationToken);
                    using var response = await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);
                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

                    ValidateContentType(asset.Type, response.Content.Headers.ContentType);
                    var extension = ResolveExtension(asset.Type, response.Content.Headers.ContentType, url);
                    var finalTargetPath = targetWithoutExtension + extension;
                    var targetPath = stageForPersonDetection
                        ? finalTargetPath + PersonDetectionQueueService.PendingSuffix
                        : finalTargetPath;
                    var partPath = targetPath + ".part";

                    if (stageForPersonDetection)
                    {
                        if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 0)
                        {
                            if (applyTimestamp)
                                ApplyPublishedTimestamp(targetPath, publishedAt);
                            RaiseLog($"待检测图片已存在，重新加入队列：{Path.GetFileName(finalTargetPath)}");
                            return targetPath;
                        }

                        if (File.Exists(finalTargetPath) && new FileInfo(finalTargetPath).Length > 0)
                        {
                            File.Move(finalTargetPath, targetPath, overwrite: true);
                            if (applyTimestamp)
                                ApplyPublishedTimestamp(targetPath, publishedAt);
                            RaiseLog($"已有图片已转入后台人像检测：{Path.GetFileName(finalTargetPath)}");
                            return targetPath;
                        }
                    }
                    else if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 0)
                    {
                        if (applyTimestamp)
                            ApplyPublishedTimestamp(targetPath, publishedAt);
                        RaiseLog($"文件已存在，跳过：{Path.GetFileName(targetPath)}");
                        return targetPath;
                    }

                    var expectedLength = response.Content.Headers.ContentLength;
                    await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
                    await using (var output = new FileStream(
                        partPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        1024 * 128,
                        FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        if (asset.Type is MediaAssetType.Video or MediaAssetType.Music)
                        {
                            await CopyStreamWithProgressAsync(
                                input,
                                output,
                                Path.GetFileName(finalTargetPath),
                                asset.Type,
                                expectedLength,
                                cancellationToken);
                        }
                        else
                        {
                            await CopyStreamAsync(
                                input,
                                output,
                                onBytesWritten: null,
                                cancellationToken: cancellationToken);
                        }
                        await output.FlushAsync(cancellationToken);
                    }

                    if (!File.Exists(partPath) || new FileInfo(partPath).Length == 0)
                        throw new IOException("下载结果为空文件。");

                    var actualLength = new FileInfo(partPath).Length;
                    if (expectedLength.HasValue
                        && expectedLength.Value > 0
                        && actualLength != expectedLength.Value)
                    {
                        throw new IOException(
                            $"下载文件长度不完整：期望 {expectedLength.Value}，实际 {actualLength}。");
                    }

                    File.Move(partPath, targetPath, true);
                    if (applyTimestamp)
                        ApplyPublishedTimestamp(targetPath, publishedAt);
                    var completedFileName = stageForPersonDetection
                        ? Path.GetFileName(finalTargetPath)
                        : Path.GetFileName(targetPath);
                    var completedLabel = stageForPersonDetection
                        ? "图片下载完成，等待后台人像检测"
                        : completionLabel ?? "下载完成";
                    RaiseLog($"{completedLabel}：{completedFileName}");
                    if (asset.Type is MediaAssetType.Video or MediaAssetType.Music)
                        ClearTransferProgress(completedFileName, asset.Type);
                    return targetPath;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (asset.Type is MediaAssetType.Video or MediaAssetType.Music)
                        ClearTransferProgress(Path.GetFileName(targetWithoutExtension), asset.Type);
                    lastError = ex;
                    RaiseLog($"下载失败，第 {attempt}/3 次：{ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
                }
            }
        }

        throw new IOException("所有候选下载地址均失败。", lastError);
    }

    private async Task<string> DownloadHlsAssetAsync(
        string playlistUrl,
        string targetWithoutExtension,
        BrowserDownloadContext browserContext,
        DateTimeOffset publishedAt,
        CancellationToken cancellationToken,
        bool applyTimestamp,
        string? completionLabel)
    {
        var targetPath = targetWithoutExtension + ".mp4";
        var displayFileName = Path.GetFileName(targetPath);
        if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 0)
        {
            if (applyTimestamp)
                ApplyPublishedTimestamp(targetPath, publishedAt);
            RaiseLog($"文件已存在，跳过：{Path.GetFileName(targetPath)}");
            return targetPath;
        }

        var partPath = targetWithoutExtension + ".hls.part.mp4";
        TryDeleteTemporaryFile(partPath);

        var cookies = await browserContext.GetCookiesAsync(playlistUrl, cancellationToken);
        var cookieHeader = cookies.Count == 0
            ? null
            : string.Join("; ", cookies.Select(static cookie => $"{cookie.Name}={cookie.Value}"));
        var parentDirectory = Path.GetDirectoryName(targetWithoutExtension);
        if (string.IsNullOrWhiteSpace(parentDirectory))
            parentDirectory = Directory.GetCurrentDirectory();
        var cacheDirectory = Path.Combine(parentDirectory, $".hls-cache-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(cacheDirectory);
            RaiseLog($"检测到 Pinterest HLS 视频，正在由程序下载播放列表和分片：{Path.GetFileName(targetPath)}");
            ReportTransferProgress(new MediaTransferProgress(
                true,
                displayFileName,
                MediaAssetType.Video,
                0,
                null,
                0,
                null,
                "正在读取 HLS 播放列表"));

            try
            {
                var localPlaylistPath = await MaterializeHlsPlaylistAsync(
                    playlistUrl,
                    cacheDirectory,
                    browserContext,
                    displayFileName,
                    cancellationToken);

                RaiseLog($"Pinterest HLS 分片下载完成，正在通过 FFmpeg 本地合并：{Path.GetFileName(targetPath)}");
                ReportTransferProgress(new MediaTransferProgress(
                    true,
                    displayFileName,
                    MediaAssetType.Video,
                    0,
                    null,
                    0,
                    null,
                    "HLS 分片下载完成，正在合并 MP4"));
                await _mediaProcessor.DownloadHlsAsync(
                    localPlaylistPath,
                    partPath,
                    userAgent: null,
                    referer: null,
                    cookieHeader: null,
                    cancellationToken: cancellationToken);
            }
            catch (FileNotFoundException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 某些非常规 HLS（例如外置音轨或特殊加密）无法完整本地化时，
                // 保留 FFmpeg 直连作为兼容回退。正常情况下 Pinterest 会走上面的
                // HttpClient 下载路径，从而继承系统代理，不依赖 FFmpeg 的网络栈。
                TryDeleteTemporaryFile(partPath);
                RaiseLog($"程序下载 HLS 分片或本地合并失败，尝试 FFmpeg 直连：{ex.Message}");
                ReportTransferProgress(new MediaTransferProgress(
                    true,
                    displayFileName,
                    MediaAssetType.Video,
                    0,
                    null,
                    0,
                    null,
                    "正在通过 FFmpeg 直连下载 HLS"));
                await _mediaProcessor.DownloadHlsAsync(
                    playlistUrl,
                    partPath,
                    browserContext.UserAgent,
                    browserContext.Referer,
                    cookieHeader,
                    cancellationToken);
            }

            if (!File.Exists(partPath) || new FileInfo(partPath).Length == 0)
                throw new IOException("HLS 下载结果为空文件。");

            File.Move(partPath, targetPath, overwrite: true);
            if (applyTimestamp)
                ApplyPublishedTimestamp(targetPath, publishedAt);
            RaiseLog($"{completionLabel ?? "下载完成"}：{Path.GetFileName(targetPath)}");
            ClearTransferProgress(displayFileName, MediaAssetType.Video);
            return targetPath;
        }
        catch
        {
            ClearTransferProgress(displayFileName, MediaAssetType.Video);
            TryDeleteTemporaryFile(partPath);
            throw;
        }
        finally
        {
            TryDeleteTemporaryDirectory(cacheDirectory);
        }
    }

    private async Task<string> MaterializeHlsPlaylistAsync(
        string playlistUrl,
        string cacheDirectory,
        BrowserDownloadContext browserContext,
        string displayFileName,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(WebUtility.HtmlDecode(playlistUrl), UriKind.Absolute, out var currentPlaylistUri))
            throw new InvalidOperationException("Pinterest HLS 播放列表地址无效。");

        // 主播放列表可能再次指向子播放列表，最多递归四层，防止异常循环。
        for (var depth = 0; depth < 4; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var playlistText = await DownloadHlsTextAsync(
                currentPlaylistUri,
                browserContext,
                cancellationToken);
            if (!playlistText.Contains("#EXTM3U", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Pinterest HLS 地址未返回有效的 M3U8 播放列表。");

            var bestVariant = SelectBestHlsVariant(playlistText, currentPlaylistUri);
            if (bestVariant is not null)
            {
                currentPlaylistUri = bestVariant.Uri;
                var resolution = bestVariant.Width > 0 && bestVariant.Height > 0
                    ? $"{bestVariant.Width}x{bestVariant.Height}"
                    : "未知分辨率";
                RaiseLog($"Pinterest HLS 已选择最高画质播放列表：{resolution}，带宽 {bestVariant.Bandwidth}。");
                continue;
            }

            return await MaterializeHlsMediaPlaylistAsync(
                playlistText,
                currentPlaylistUri,
                cacheDirectory,
                browserContext,
                displayFileName,
                cancellationToken);
        }

        throw new IOException("Pinterest HLS 主播放列表嵌套层级过深。");
    }

    private async Task<string> DownloadHlsTextAsync(
        Uri playlistUri,
        BrowserDownloadContext browserContext,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = await CreateRequestAsync(
                playlistUri.AbsoluteUri,
                browserContext,
                cancellationToken);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"HLS 播放列表 HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ShouldUseBrowserHlsFallback(ex, cancellationToken))
        {
            var bytes = await FetchHlsBytesThroughBrowserAsync(
                playlistUri.AbsoluteUri,
                browserContext,
                cancellationToken);
            return Encoding.UTF8.GetString(bytes);
        }
    }

    private async Task<string> MaterializeHlsMediaPlaylistAsync(
        string playlistText,
        Uri playlistUri,
        string cacheDirectory,
        BrowserDownloadContext browserContext,
        string displayFileName,
        CancellationToken cancellationToken)
    {
        var lines = playlistText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var rewrittenLines = new string[lines.Length];
        var resourcesByUrl = new Dictionary<string, HlsLocalResource>(StringComparer.Ordinal);
        var resourceSequence = 0;

        string RegisterResource(string rawReference, string prefix, string fallbackExtension)
        {
            var decodedReference = WebUtility.HtmlDecode(rawReference.Trim());
            if (decodedReference.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return decodedReference;

            if (!Uri.TryCreate(playlistUri, decodedReference, out var absoluteUri))
                throw new IOException($"无法解析 HLS 资源地址：{decodedReference}");

            var absoluteUrl = absoluteUri.AbsoluteUri;
            if (resourcesByUrl.TryGetValue(absoluteUrl, out var existing))
                return existing.LocalFileName;

            var extension = ResolveHlsResourceExtension(absoluteUri, fallbackExtension);
            var localFileName = $"{prefix}_{resourceSequence++:D5}{extension}";
            var localPath = Path.Combine(cacheDirectory, localFileName);
            resourcesByUrl.Add(
                absoluteUrl,
                new HlsLocalResource(absoluteUrl, localFileName, localPath));
            return localFileName;
        }

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0)
            {
                rewrittenLines[index] = string.Empty;
                continue;
            }

            if (line.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase))
            {
                var method = ReadHlsAttribute(line, "METHOD");
                var keyUri = ReadHlsAttribute(line, "URI");
                if (!string.Equals(method, "NONE", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(keyUri))
                {
                    var localKey = RegisterResource(keyUri, "key", ".key");
                    line = ReplaceHlsUriAttribute(line, localKey);
                }

                rewrittenLines[index] = line;
                continue;
            }

            if (line.StartsWith("#EXT-X-MAP:", StringComparison.OrdinalIgnoreCase))
            {
                var mapUri = ReadHlsAttribute(line, "URI");
                if (!string.IsNullOrWhiteSpace(mapUri))
                {
                    var localMap = RegisterResource(mapUri, "init", ".mp4");
                    line = ReplaceHlsUriAttribute(line, localMap);
                }

                rewrittenLines[index] = line;
                continue;
            }

            if (line.StartsWith('#'))
            {
                rewrittenLines[index] = line;
                continue;
            }

            rewrittenLines[index] = RegisterResource(line, "segment", ".ts");
        }

        var resources = resourcesByUrl.Values.ToArray();
        var progressTracker = new HlsProgressTracker(
            resources.Length,
            displayFileName,
            ReportTransferProgress);
        progressTracker.Report(force: true);
        using var concurrency = new SemaphoreSlim(6, 6);
        var downloadTasks = resources.Select(async resource =>
        {
            await concurrency.WaitAsync(cancellationToken);
            try
            {
                await DownloadHlsResourceAsync(
                    resource,
                    browserContext,
                    progressTracker,
                    cancellationToken);
            }
            finally
            {
                concurrency.Release();
            }
        });
        await Task.WhenAll(downloadTasks);

        var localPlaylistPath = Path.Combine(cacheDirectory, "local.m3u8");
        await File.WriteAllLinesAsync(
            localPlaylistPath,
            rewrittenLines,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
        RaiseLog($"Pinterest HLS 已下载 {resources.Length} 个本地资源分片。");
        return localPlaylistPath;
    }

    private async Task DownloadHlsResourceAsync(
        HlsLocalResource resource,
        BrowserDownloadContext browserContext,
        HlsProgressTracker progressTracker,
        CancellationToken cancellationToken)
    {
        var partPath = resource.LocalPath + ".part";
        TryDeleteTemporaryFile(partPath);

        try
        {
            try
            {
                using var request = await CreateRequestAsync(
                    resource.RemoteUrl,
                    browserContext,
                    cancellationToken);
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException(
                        $"HLS 分片 HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = new FileStream(
                    partPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 128,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await CopyStreamAsync(
                    input,
                    output,
                    progressTracker.AddBytes,
                    cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            catch (Exception ex) when (ShouldUseBrowserHlsFallback(ex, cancellationToken))
            {
                TryDeleteTemporaryFile(partPath);
                var bytes = await FetchHlsBytesThroughBrowserAsync(
                    resource.RemoteUrl,
                    browserContext,
                    cancellationToken);
                await _downloadRateLimiter.WaitAsync(bytes.Length, cancellationToken);
                await File.WriteAllBytesAsync(partPath, bytes, cancellationToken);
                progressTracker.AddBytes(bytes.Length);
            }

            if (!File.Exists(partPath) || new FileInfo(partPath).Length == 0)
                throw new IOException($"HLS 分片下载结果为空：{resource.RemoteUrl}");

            File.Move(partPath, resource.LocalPath, overwrite: true);
            progressTracker.CompletePart();
        }
        catch
        {
            TryDeleteTemporaryFile(partPath);
            throw;
        }
    }

    private async Task<byte[]> FetchHlsBytesThroughBrowserAsync(
        string url,
        BrowserDownloadContext browserContext,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _hlsBrowserFallbackLogged, 1) == 0)
        {
            RaiseLog("HLS 系统网络请求失败，已切换到浏览器网络通道继续下载。");
        }

        return await _browser.FetchBytesAsync(
            url,
            browserContext.Referer,
            cancellationToken);
    }

    private static bool ShouldUseBrowserHlsFallback(
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested
            || exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return exception is HttpRequestException
               or IOException
               or TaskCanceledException;
    }

    private static HlsVariant? SelectBestHlsVariant(string playlistText, Uri playlistUri)
    {
        var lines = playlistText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var variants = new List<HlsVariant>();

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (!line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
                continue;

            string? variantReference = null;
            for (var next = index + 1; next < lines.Length; next++)
            {
                var candidate = lines[next].Trim();
                if (candidate.Length == 0)
                    continue;
                if (candidate.StartsWith('#'))
                    break;
                variantReference = candidate;
                break;
            }

            if (string.IsNullOrWhiteSpace(variantReference)
                || !Uri.TryCreate(playlistUri, WebUtility.HtmlDecode(variantReference), out var variantUri))
            {
                continue;
            }

            var bandwidth = ParseHlsLongAttribute(line, "AVERAGE-BANDWIDTH");
            if (bandwidth <= 0)
                bandwidth = ParseHlsLongAttribute(line, "BANDWIDTH");
            var (width, height) = ParseHlsResolution(ReadHlsAttribute(line, "RESOLUTION"));
            variants.Add(new HlsVariant(variantUri, width, height, bandwidth));
        }

        return variants
            .OrderByDescending(static variant => (long)variant.Width * variant.Height)
            .ThenByDescending(static variant => variant.Bandwidth)
            .FirstOrDefault();
    }

    private static string? ReadHlsAttribute(string line, string attributeName)
    {
        var marker = attributeName + "=";
        var markerIndex = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return null;

        var valueStart = markerIndex + marker.Length;
        if (valueStart >= line.Length)
            return null;

        if (line[valueStart] == '"')
        {
            var closingQuote = line.IndexOf('"', valueStart + 1);
            return closingQuote > valueStart
                ? line[(valueStart + 1)..closingQuote]
                : null;
        }

        var commaIndex = line.IndexOf(',', valueStart);
        var valueEnd = commaIndex >= 0 ? commaIndex : line.Length;
        return line[valueStart..valueEnd].Trim();
    }

    private static long ParseHlsLongAttribute(string line, string attributeName)
        => long.TryParse(ReadHlsAttribute(line, attributeName), out var value) ? value : 0;

    private static (int Width, int Height) ParseHlsResolution(string? resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution))
            return (0, 0);

        var parts = resolution.Split('x', 'X');
        return parts.Length == 2
               && int.TryParse(parts[0], out var width)
               && int.TryParse(parts[1], out var height)
            ? (width, height)
            : (0, 0);
    }

    private static string ReplaceHlsUriAttribute(string line, string localFileName)
    {
        const string marker = "URI=";
        var markerIndex = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return line;

        var valueStart = markerIndex + marker.Length;
        if (valueStart >= line.Length)
            return line;

        int valueEnd;
        if (line[valueStart] == '"')
        {
            valueEnd = line.IndexOf('"', valueStart + 1);
            if (valueEnd < 0)
                return line;
            return line[..valueStart] + '"' + localFileName + line[valueEnd..];
        }

        valueEnd = line.IndexOf(',', valueStart);
        if (valueEnd < 0)
            valueEnd = line.Length;
        return line[..valueStart] + '"' + localFileName + '"' + line[valueEnd..];
    }

    private static string ResolveHlsResourceExtension(Uri uri, string fallbackExtension)
    {
        var extension = Path.GetExtension(uri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(extension)
            || extension.Length > 10
            || extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return fallbackExtension;
        }

        return extension.ToLowerInvariant();
    }

    private sealed record HlsLocalResource(
        string RemoteUrl,
        string LocalFileName,
        string LocalPath);

    private sealed record HlsVariant(
        Uri Uri,
        int Width,
        int Height,
        long Bandwidth);

    private async Task<bool> EnsureVideoHasAudioAsync(
        string videoPath,
        MediaAsset? music,
        string? retainedMusicTargetWithoutExtension,
        BrowserDownloadContext browserContext,
        DateTimeOffset publishedAt,
        CancellationToken cancellationToken,
        bool requireAudioMerge = false)
    {
        bool hasAudio;
        try
        {
            hasAudio = await _mediaProcessor.HasAudioStreamAsync(videoPath, cancellationToken);
        }
        catch (FileNotFoundException ex)
        {
            if (requireAudioMerge)
            {
                throw new FileNotFoundException(
                    "哔哩哔哩 DASH 音视频必须使用 FFmpeg 合并，但当前未找到 ffmpeg/ffprobe。" +
                    "请先完成程序内的 FFmpeg 安装后重试。",
                    ex.FileName,
                    ex);
            }

            if (Interlocked.Exchange(ref _ffmpegUnavailableWarningLogged, 1) == 0)
                RaiseLog(ex.Message);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RaiseLog($"无法检查视频音轨，保留原视频：{Path.GetFileName(videoPath)}；{ex.Message}");
            return false;
        }

        if (hasAudio)
            return false;

        RaiseLog($"检测到无音轨视频，准备下载临时音频并合并：{Path.GetFileName(videoPath)}");
        if (music is null || music.CandidateUrls.Count == 0)
        {
            if (requireAudioMerge)
                throw new IOException("哔哩哔哩 DASH 数据中没有可用音频流，无法生成完整视频。");

            RaiseLog("该作品没有可用的音乐地址，无法为无声视频补充音频。");
            return false;
        }

        var directory = Path.GetDirectoryName(videoPath)
                        ?? throw new IOException("无法确定视频所在目录。");
        var videoExtension = Path.GetExtension(videoPath);
        var uniqueId = Guid.NewGuid().ToString("N");
        var temporaryAudioBase = Path.Combine(directory, $".smc-audio-{uniqueId}");
        var muxedPath = Path.Combine(directory, $".smc-mux-{uniqueId}{videoExtension}");

        string? temporaryAudioPath = null;
        var retainedMusic = false;
        try
        {
            temporaryAudioPath = await DownloadAssetAsync(
                music,
                temporaryAudioBase,
                browserContext,
                publishedAt,
                cancellationToken,
                applyTimestamp: false,
                completionLabel: "临时音频下载完成");

            if (!await _mediaProcessor.HasAudioStreamAsync(temporaryAudioPath, cancellationToken))
                throw new IOException("下载的临时音乐文件中没有可用音频轨。");

            // 开启“下载背景音乐”时，直接复用本次临时音频，不再重复请求网络。
            if (!string.IsNullOrWhiteSpace(retainedMusicTargetWithoutExtension))
            {
                retainedMusic = RetainTemporaryMusic(
                    temporaryAudioPath,
                    retainedMusicTargetWithoutExtension,
                    publishedAt);
            }

            var videoFileName = Path.GetFileName(videoPath);
            ReportTransferProgress(new MediaTransferProgress(
                true,
                videoFileName,
                MediaAssetType.Video,
                0,
                null,
                0,
                null,
                "正在合并音视频"));
            try
            {
                await _mediaProcessor.MergeVideoAndAudioAsync(
                    videoPath,
                    temporaryAudioPath,
                    muxedPath,
                    cancellationToken);
            }
            finally
            {
                ClearTransferProgress(videoFileName, MediaAssetType.Video);
            }

            if (!await _mediaProcessor.HasAudioStreamAsync(muxedPath, cancellationToken))
                throw new IOException("合并后的文件仍未检测到音频轨。");

            File.Move(muxedPath, videoPath, overwrite: true);
            ApplyPublishedTimestamp(videoPath, publishedAt);
            RaiseLog($"无声视频已补充音频并合并完成：{Path.GetFileName(videoPath)}");
            return retainedMusic;
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryAudioPath);
            TryDeleteTemporaryFile(muxedPath);
        }
    }

    private bool RetainTemporaryMusic(
        string temporaryAudioPath,
        string targetWithoutExtension,
        DateTimeOffset publishedAt)
    {
        var targetPath = targetWithoutExtension + Path.GetExtension(temporaryAudioPath);
        if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 0)
        {
            ApplyPublishedTimestamp(targetPath, publishedAt);
            RaiseLog($"背景音乐已存在，跳过：{Path.GetFileName(targetPath)}");
            return true;
        }

        File.Copy(temporaryAudioPath, targetPath, overwrite: true);
        ApplyPublishedTimestamp(targetPath, publishedAt);
        RaiseLog($"背景音乐已保存：{Path.GetFileName(targetPath)}");
        return true;
    }

    private static void TryDeleteTemporaryFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 临时文件清理失败不覆盖原始下载/合并异常。
        }
    }

    private static void TryDeleteTemporaryDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // HLS 缓存目录清理失败不覆盖原始下载/合并异常。
        }
    }

    private static async Task<HttpRequestMessage> CreateRequestAsync(
        string url,
        BrowserDownloadContext browserContext,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(browserContext.UserAgent);
        request.Headers.Referrer = Uri.TryCreate(browserContext.Referer, UriKind.Absolute, out var referer)
            ? referer
            : null;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        request.Headers.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.7");

        if (Uri.TryCreate(browserContext.Referer, UriKind.Absolute, out var sourceReferer)
            && (sourceReferer.Host.Equals("bilibili.com", StringComparison.OrdinalIgnoreCase)
                || sourceReferer.Host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase)))
        {
            // B站 DASH CDN 的网页播放器请求通常携带 Referer 与 Range。
            // 从 0 开始请求完整资源，服务端返回 200 或 206 都属于成功响应。
            request.Headers.Range = new RangeHeaderValue(0, null);
        }

        if (Uri.TryCreate(browserContext.Referer, UriKind.Absolute, out sourceReferer)
            && (sourceReferer.Host.Equals("pinterest.com", StringComparison.OrdinalIgnoreCase)
                || sourceReferer.Host.EndsWith(".pinterest.com", StringComparison.OrdinalIgnoreCase)))
        {
            request.Headers.TryAddWithoutValidation(
                "Origin",
                sourceReferer.GetLeftPart(UriPartial.Authority));
        }

        var cookies = await browserContext.GetCookiesAsync(url, cancellationToken);
        if (cookies.Count > 0)
        {
            request.Headers.TryAddWithoutValidation(
                "Cookie",
                string.Join("; ", cookies.Select(x => $"{x.Name}={x.Value}")));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return request;
    }

    private static void ValidateContentType(MediaAssetType type, MediaTypeHeaderValue? contentType)
    {
        var mediaType = contentType?.MediaType?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(mediaType))
            return;

        if (mediaType.StartsWith("text/", StringComparison.Ordinal)
            || mediaType.Contains("json", StringComparison.Ordinal))
        {
            throw new IOException($"资源返回了非媒体内容：{mediaType}");
        }

        if ((type is MediaAssetType.Image or MediaAssetType.Cover)
            && !mediaType.StartsWith("image/", StringComparison.Ordinal)
            && mediaType != "application/octet-stream")
        {
            throw new IOException($"图片资源 Content-Type 异常：{mediaType}");
        }

        if (type == MediaAssetType.Music
            && !mediaType.StartsWith("audio/", StringComparison.Ordinal)
            && !mediaType.StartsWith("video/", StringComparison.Ordinal)
            && mediaType != "application/octet-stream")
        {
            throw new IOException($"音乐资源 Content-Type 异常：{mediaType}");
        }
    }

    private static bool IsHlsUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        var decoded = WebUtility.HtmlDecode(url);
        return decoded.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
               || decoded.Contains("application/vnd.apple.mpegurl", StringComparison.OrdinalIgnoreCase)
               || decoded.Contains("application/x-mpegurl", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveExtension(
        MediaAssetType type,
        MediaTypeHeaderValue? contentType,
        string sourceUrl)
    {
        var mediaType = contentType?.MediaType?.ToLowerInvariant();
        if (type == MediaAssetType.Video)
        {
            return mediaType switch
            {
                "video/webm" => ".webm",
                "video/quicktime" => ".mov",
                _ => ".mp4"
            };
        }

        if (type == MediaAssetType.Music)
        {
            return mediaType switch
            {
                "audio/mpeg" => ".mp3",
                "audio/mp4" or "video/mp4" => ".m4a",
                "audio/aac" => ".aac",
                "audio/ogg" or "application/ogg" => ".ogg",
                "audio/wav" or "audio/x-wav" => ".wav",
                "audio/webm" => ".webm",
                _ => ResolveAudioExtensionFromUrl(sourceUrl) ?? ".mp3"
            };
        }

        return mediaType switch
        {
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "image/avif" => ".avif",
            "image/heic" or "image/heic-sequence" => ".heic",
            "image/heif" or "image/heif-sequence" => ".heif",
            _ => ResolveImageExtensionFromUrl(sourceUrl) ?? ".jpg"
        };
    }

    private static string? ResolveImageExtensionFromUrl(string sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
            return null;

        var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        return extension switch
        {
            ".jpg" or ".jpeg" => ".jpg",
            ".png" or ".webp" or ".gif" or ".avif" or ".heic" or ".heif" => extension,
            _ => null
        };
    }

    private static string? ResolveAudioExtensionFromUrl(string sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
            return null;

        var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        if (extension is ".mp3" or ".m4a" or ".aac" or ".ogg" or ".wav" or ".webm")
            return extension;
        if (extension == ".m4s")
            return ".m4a";

        var query = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var pair in query)
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2 || !parts[0].Equals("mime_type", StringComparison.OrdinalIgnoreCase))
                continue;

            var mime = Uri.UnescapeDataString(parts[1]).Replace('_', '/').ToLowerInvariant();
            return mime switch
            {
                "audio/mpeg" => ".mp3",
                "audio/mp4" => ".m4a",
                "audio/aac" => ".aac",
                "audio/ogg" => ".ogg",
                _ => null
            };
        }

        return null;
    }

    private void ApplyPublishedTimestamp(string path, DateTimeOffset publishedAt)
    {
        var utc = publishedAt.UtcDateTime;
        var errors = new List<string>();

        TrySet(() => File.SetCreationTimeUtc(path, utc), "创建时间", errors);
        TrySet(() => File.SetLastWriteTimeUtc(path, utc), "修改时间", errors);
        TrySet(() => File.SetLastAccessTimeUtc(path, utc), "访问时间", errors);

        // 某些 Linux 文件系统不支持修改 birth time。只提示一次，不影响下载任务。
        if (errors.Count > 0 && Interlocked.Exchange(ref _timestampWarningLogged, 1) == 0)
        {
            RaiseLog($"部分文件时间属性无法设置（当前文件系统可能不支持）：{string.Join("；", errors)}");
        }
    }

    private static void TrySet(Action action, string name, ICollection<string> errors)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            errors.Add($"{name}: {ex.Message}");
        }
    }

    private async Task CopyStreamWithProgressAsync(
        Stream input,
        Stream output,
        string fileName,
        MediaAssetType assetType,
        long? totalBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 128];
        var stopwatch = Stopwatch.StartNew();
        var lastReportAt = TimeSpan.Zero;
        long bytesReceived = 0;

        ReportTransferProgress(new MediaTransferProgress(
            true,
            fileName,
            assetType,
            0,
            totalBytes,
            0,
            totalBytes is > 0 ? 0d : null,
            "正在下载"));

        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                break;

            await _downloadRateLimiter.WaitAsync(read, cancellationToken);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            bytesReceived += read;

            var elapsed = stopwatch.Elapsed;
            var completed = totalBytes is > 0 && bytesReceived >= totalBytes.Value;
            if (!completed && elapsed - lastReportAt < TimeSpan.FromMilliseconds(250))
                continue;

            lastReportAt = elapsed;
            var speed = elapsed.TotalSeconds > 0
                ? bytesReceived / elapsed.TotalSeconds
                : 0;
            double? percent = totalBytes is > 0
                ? Math.Clamp(bytesReceived * 100d / totalBytes.Value, 0, 100)
                : null;

            ReportTransferProgress(new MediaTransferProgress(
                true,
                fileName,
                assetType,
                bytesReceived,
                totalBytes,
                speed,
                percent,
                "正在下载"));
        }

        var finalElapsed = stopwatch.Elapsed;
        ReportTransferProgress(new MediaTransferProgress(
            true,
            fileName,
            assetType,
            bytesReceived,
            totalBytes,
            finalElapsed.TotalSeconds > 0 ? bytesReceived / finalElapsed.TotalSeconds : 0,
            totalBytes is > 0 ? 100d : null,
            "下载完成"));
    }

    private async Task CopyStreamAsync(
        Stream input,
        Stream output,
        Action<int>? onBytesWritten,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 128];
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                break;

            await _downloadRateLimiter.WaitAsync(read, cancellationToken);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            onBytesWritten?.Invoke(read);
        }
    }

    private void ReportTransferProgress(MediaTransferProgress progress)
        => ProgressChanged?.Invoke(this, progress);

    private void ClearTransferProgress(string fileName, MediaAssetType assetType)
        => ReportTransferProgress(new MediaTransferProgress(
            false,
            fileName,
            assetType,
            0,
            null,
            0,
            null));

    /// <summary>
    /// 当前采集任务共享的总下载速率限制器。并发作品和 HLS 分片共同预约带宽，
    /// 避免每个并发请求都单独达到设置值而导致总速度成倍增加。
    /// </summary>
    private sealed class DownloadRateLimiter
    {
        private readonly object _gate = new();
        private double _bytesPerSecond;
        private long _nextAvailableTimestamp;

        public void SetLimit(decimal megabytesPerSecond, bool resetSchedule = false)
        {
            var normalized = Math.Clamp(megabytesPerSecond, 0m, 10000m);
            var bytesPerSecond = normalized <= 0
                ? 0d
                : (double)normalized * 1024d * 1024d;

            lock (_gate)
            {
                if (!resetSchedule
                    && Math.Abs(_bytesPerSecond - bytesPerSecond) < 0.5d)
                {
                    return;
                }

                _bytesPerSecond = bytesPerSecond;
                _nextAvailableTimestamp = Stopwatch.GetTimestamp();
            }
        }

        public async ValueTask WaitAsync(
            int byteCount,
            CancellationToken cancellationToken)
        {
            if (byteCount <= 0)
                return;

            long delayTicks;
            lock (_gate)
            {
                if (_bytesPerSecond <= 0)
                    return;

                var now = Stopwatch.GetTimestamp();
                var start = Math.Max(now, _nextAvailableTimestamp);
                var reservationTicks = Math.Max(
                    1L,
                    (long)Math.Ceiling(byteCount * Stopwatch.Frequency / _bytesPerSecond));
                _nextAvailableTimestamp = start + reservationTicks;
                delayTicks = start - now;
            }

            if (delayTicks <= 0)
                return;

            var delay = TimeSpan.FromSeconds(
                delayTicks / (double)Stopwatch.Frequency);
            await Task.Delay(delay, cancellationToken);
        }
    }

    private sealed class HlsProgressTracker
    {
        private readonly int _totalParts;
        private readonly string _fileName;
        private readonly Action<MediaTransferProgress> _report;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private long _bytesReceived;
        private int _completedParts;
        private long _lastReportTimestamp;

        public HlsProgressTracker(
            int totalParts,
            string fileName,
            Action<MediaTransferProgress> report)
        {
            _totalParts = Math.Max(0, totalParts);
            _fileName = fileName;
            _report = report;
        }

        public void AddBytes(int count)
        {
            if (count <= 0)
                return;

            Interlocked.Add(ref _bytesReceived, count);
            Report();
        }

        public void CompletePart()
        {
            var completed = Interlocked.Increment(ref _completedParts);
            Report(force: _totalParts > 0 && completed >= _totalParts);
        }

        public void Report(bool force = false)
        {
            var now = Stopwatch.GetTimestamp();
            var previous = Interlocked.Read(ref _lastReportTimestamp);
            var minimumTicks = Stopwatch.Frequency / 4;
            if (!force && previous != 0 && now - previous < minimumTicks)
                return;

            if (Interlocked.CompareExchange(ref _lastReportTimestamp, now, previous) != previous)
                return;

            var bytes = Interlocked.Read(ref _bytesReceived);
            var completed = Math.Min(
                Math.Max(0, Volatile.Read(ref _completedParts)),
                _totalParts);
            var elapsedSeconds = Math.Max(0.001, _stopwatch.Elapsed.TotalSeconds);
            double? percent = _totalParts > 0
                ? completed * 100d / _totalParts
                : null;

            _report(new MediaTransferProgress(
                true,
                _fileName,
                MediaAssetType.Video,
                bytes,
                null,
                bytes / elapsedSeconds,
                percent,
                _totalParts > 0
                    ? $"HLS 分片 {completed}/{_totalParts}"
                    : "正在下载 HLS 分片",
                completed,
                _totalParts));
        }
    }

    private void RaiseLog(string message) => Log?.Invoke(this, message);

    public async ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        await _personDetectionQueue.DisposeAsync();
    }
}
