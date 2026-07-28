using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using HelloCrab.Core.Models;
using HelloCrab.Core.Services.Browser;
using HelloCrab.Core.Services.Media;
using HelloCrab.Core.Utilities;
using YoutubeExplode;
using YoutubeExplode.Channels;
using YoutubeExplode.Common;
using YoutubeExplode.Videos.Streams;

namespace HelloCrab.Core.Sites.YouTube;

/// <summary>
/// YouTube 公开频道视频适配器。
///
/// 频道上传列表和作品元数据由 YoutubeExplode 获取；下载时分别选择最高质量的
/// video-only 与 audio-only 流，再复用 HelloCrab 已有的 FFmpeg 处理器进行封装。
/// </summary>
public sealed class YouTubeSiteAdapter : ISiteAdapter, ISiteManagedDownloadAdapter
{
    private static readonly TimeSpan DuplicateDocumentWindow = TimeSpan.FromSeconds(30);
    private static readonly HttpClient ImageHttpClient = CreateImageHttpClient();

    private readonly IMediaProcessor _mediaProcessor;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentChannelDocuments =
        new(StringComparer.OrdinalIgnoreCase);

    public YouTubeSiteAdapter(IMediaProcessor mediaProcessor)
    {
        _mediaProcessor = mediaProcessor;
    }

    public string Id => "youtube";
    public string DisplayName => "YouTube";
    public string HomeUrl => "https://www.youtube.com/";

    public bool CanHandlePage(string pageUrl)
        => TryGetChannelRootUrl(pageUrl, out _);

    public bool IsTargetResponse(
        string responseUrl,
        string resourceType,
        int statusCode,
        string? requestBody)
        => statusCode is >= 200 and < 300
           && resourceType.Equals("document", StringComparison.OrdinalIgnoreCase)
           && TryGetChannelRootUrl(responseUrl, out _);

    public ParsedWorkBatch ParseResponse(
        string responseUrl,
        string responseJson,
        string pageUrl,
        string? requestBody)
    {
        if (!TryGetChannelRootUrl(pageUrl, out var channelRootUrl)
            && !TryGetChannelRootUrl(responseUrl, out channelRootUrl))
        {
            return new ParsedWorkBatch(
                Array.Empty<WorkItem>(),
                false,
                null,
                "当前页面不是可识别的 YouTube 频道主页。");
        }

        var now = DateTimeOffset.UtcNow;
        if (_recentChannelDocuments.TryGetValue(channelRootUrl, out var previous)
            && now - previous < DuplicateDocumentWindow)
        {
            return new ParsedWorkBatch(
                Array.Empty<WorkItem>(),
                false,
                null,
                "已忽略同一次页面刷新产生的重复 YouTube 频道文档。");
        }
        _recentChannelDocuments[channelRootUrl] = now;

        try
        {
            using var youtube = new YoutubeClient();
            var channel = ResolveChannelAsync(
                    youtube,
                    channelRootUrl,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            var channelId = channel.Id.ToString();
            var channelName = channel.Title;
            var channelAvatarUrl = channel.Thumbnails.LastOrDefault()?.Url;
            var videos = youtube.Channels
                .GetUploadsAsync(channel.Id)
                .CollectAsync()
                .GetAwaiter()
                .GetResult();

            var works = new List<WorkItem>(videos.Count);
            var rejectedCount = 0;
            foreach (var video in videos)
            {
                var authorId = video.Author.ChannelId.ToString();
                if (!authorId.Equals(channelId, StringComparison.Ordinal))
                {
                    rejectedCount++;
                    continue;
                }

                var videoId = video.Id.ToString();
                var coverUrl = video.Thumbnails.LastOrDefault()?.Url;
                works.Add(new WorkItem(
                    Id,
                    videoId,
                    channelId,
                    string.IsNullOrWhiteSpace(video.Author.ChannelTitle)
                        ? channelName
                        : video.Author.ChannelTitle,
                    channelAvatarUrl,
                    string.IsNullOrWhiteSpace(video.Title) ? videoId : video.Title,
                    0,
                    BuildPlaceholderAssets(videoId, coverUrl),
                    video.Url)
                {
                    AuthorPageUrl = $"https://www.youtube.com/channel/{channelId}/videos",
                    MediaRefererUrl = video.Url,
                    RequiresDetailResolution = true
                });
            }

            var diagnostic =
                $"YouTube 频道 {channelName}（{channelId}）读取到 {works.Count} 个公开视频。";
            if (rejectedCount > 0)
                diagnostic += $" 已过滤 {rejectedCount} 个作者不一致的条目。";

            return new ParsedWorkBatch(
                works,
                false,
                null,
                diagnostic,
                rejectedCount);
        }
        catch (Exception ex)
        {
            return new ParsedWorkBatch(
                Array.Empty<WorkItem>(),
                false,
                null,
                $"读取 YouTube 频道视频失败：{ex.Message}");
        }
    }

    public async Task<WorkItem?> ResolveWorkAsync(
        WorkItem work,
        IBrowserAutomationService browser,
        CancellationToken cancellationToken)
    {
        using var youtube = new YoutubeClient();
        var video = await youtube.Videos.GetAsync(work.WorkId, cancellationToken);
        var authorId = video.Author.ChannelId.ToString();
        if (!authorId.Equals(work.AuthorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"YouTube 视频作者不一致：视频作者 {authorId}，目标频道 {work.AuthorId}。");
        }

        var coverUrl = video.Thumbnails.LastOrDefault()?.Url
                       ?? work.Assets
                           .FirstOrDefault(asset => asset.Type == MediaAssetType.Cover)?
                           .CandidateUrls.FirstOrDefault();
        var sourceUrl = video.Url;
        return work with
        {
            AuthorName = string.IsNullOrWhiteSpace(video.Author.ChannelTitle)
                ? work.AuthorName
                : video.Author.ChannelTitle,
            Description = string.IsNullOrWhiteSpace(video.Title)
                ? work.Description
                : video.Title,
            CreateTime = video.UploadDate.ToUnixTimeSeconds(),
            Assets = BuildPlaceholderAssets(work.WorkId, coverUrl),
            SourceUrl = sourceUrl,
            MediaRefererUrl = sourceUrl,
            RequiresDetailResolution = false
        };
    }

    public Task ScrollNextAsync(
        IBrowserAutomationService browser,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    public async Task<PageScrollState> GetScrollStateAsync(
        IBrowserAutomationService browser,
        CancellationToken cancellationToken)
    {
        var state = await browser.EvaluatePageAsync(
            """
            () => {
                const root = document.scrollingElement || document.documentElement;
                return {
                    scrollY: root?.scrollTop || window.scrollY || 0,
                    viewportHeight: window.innerHeight || 0,
                    documentHeight: root?.scrollHeight || document.documentElement.scrollHeight || 0,
                    workItemCount: document.querySelectorAll('ytd-rich-item-renderer, ytd-grid-video-renderer').length
                };
            }
            """,
            cancellationToken);

        return new PageScrollState(
            ReadDouble(state, "scrollY"),
            ReadDouble(state, "viewportHeight"),
            ReadDouble(state, "documentHeight"),
            "YouTube document",
            ReadInt32(state, "workItemCount"));
    }

    public async Task DownloadWorkAsync(
        WorkItem work,
        string platformDownloadRoot,
        CrawlerDownloadOptions options,
        Action<string> log,
        Action<MediaTransferProgress> reportProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentException.ThrowIfNullOrWhiteSpace(platformDownloadRoot);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(reportProgress);

        var authorFolder = Path.Combine(
            platformDownloadRoot,
            FileNameHelper.BuildAuthorFolderName(work.AuthorName, work.AuthorId));
        Directory.CreateDirectory(authorFolder);

        var publishedAt = work.CreateTime > 0
            ? DateTimeOffset.FromUnixTimeSeconds(work.CreateTime)
            : DateTimeOffset.Now;
        var baseName = FileNameHelper.BuildWorkBaseName(
            publishedAt.ToLocalTime(),
            work.Description,
            work.WorkId,
            options.IncludeWorkId);

        using var youtube = new YoutubeClient();
        var manifest = await youtube.Videos.Streams.GetManifestAsync(
            work.WorkId,
            cancellationToken);
        var videoOnlyStreams = manifest.GetVideoOnlyStreams().ToArray();
        var audioOnlyStreams = manifest.GetAudioOnlyStreams().ToArray();

        if (videoOnlyStreams.Length == 0 || audioOnlyStreams.Length == 0)
        {
            await DownloadMuxedFallbackAsync(
                youtube,
                manifest,
                work,
                authorFolder,
                baseName,
                publishedAt,
                options,
                log,
                reportProgress,
                cancellationToken);
            return;
        }

        var videoStream = videoOnlyStreams.GetWithHighestVideoQuality();
        var matchingAudioStreams = audioOnlyStreams
            .Where(stream => stream.Container == videoStream.Container)
            .Cast<IStreamInfo>()
            .ToArray();
        var mp4AudioStreams = audioOnlyStreams
            .Where(stream => stream.Container == Container.Mp4)
            .Cast<IStreamInfo>()
            .ToArray();
        var audioCandidates = matchingAudioStreams.Length > 0
            ? matchingAudioStreams
            : mp4AudioStreams.Length > 0
                ? mp4AudioStreams
                : audioOnlyStreams.Cast<IStreamInfo>().ToArray();
        var audioStream = audioCandidates.GetWithHighestBitrate();

        var outputExtension = GetContainerExtension(videoStream.Container);
        var finalOutputPath = Path.Combine(authorFolder, baseName + outputExtension);
        if (IsUsableFile(finalOutputPath))
        {
            ApplyPublishedTimestamp(finalOutputPath, publishedAt);
            log($"文件已存在，跳过：{Path.GetFileName(finalOutputPath)}");
            await DownloadCoverIfRequestedAsync(
                work,
                authorFolder,
                baseName,
                publishedAt,
                options,
                log,
                cancellationToken);
            return;
        }

        var tempPrefix = Path.Combine(authorFolder, $".{baseName}.{Guid.NewGuid():N}");
        var tempVideoPath = tempPrefix + ".video" + GetContainerExtension(videoStream.Container);
        var tempAudioPath = tempPrefix + ".audio" + GetContainerExtension(audioStream.Container);
        var mergedPartPath = tempPrefix + ".merged" + outputExtension;
        var displayFileName = Path.GetFileName(finalOutputPath);

        try
        {
            log(
                $"YouTube 已选择最高画质视频流 {videoStream.VideoQuality.Label}，" +
                $"将与最高码率音频流合并：{work.Description}");

            await youtube.Videos.Streams.DownloadAsync(
                videoStream,
                tempVideoPath,
                new InlineProgress<double>(value =>
                    ReportDownloadProgress(
                        reportProgress,
                        displayFileName,
                        Math.Clamp(value * 55, 0, 55),
                        "正在下载 YouTube 视频流")),
                cancellationToken);

            await youtube.Videos.Streams.DownloadAsync(
                audioStream,
                tempAudioPath,
                new InlineProgress<double>(value =>
                    ReportDownloadProgress(
                        reportProgress,
                        displayFileName,
                        55 + Math.Clamp(value * 35, 0, 35),
                        "正在下载 YouTube 音频流")),
                cancellationToken);

            ReportDownloadProgress(
                reportProgress,
                displayFileName,
                92,
                "正在通过 FFmpeg 合并音视频");
            await _mediaProcessor.MergeVideoAndAudioAsync(
                tempVideoPath,
                tempAudioPath,
                mergedPartPath,
                cancellationToken);

            if (!IsUsableFile(mergedPartPath))
                throw new IOException("FFmpeg 合并后的 YouTube 文件为空。");

            File.Move(mergedPartPath, finalOutputPath, true);
            ApplyPublishedTimestamp(finalOutputPath, publishedAt);
            log($"下载完成：{displayFileName}");
        }
        finally
        {
            TryDelete(tempVideoPath);
            TryDelete(tempAudioPath);
            TryDelete(mergedPartPath);
            ClearProgress(reportProgress, displayFileName);
        }

        await DownloadCoverIfRequestedAsync(
            work,
            authorFolder,
            baseName,
            publishedAt,
            options,
            log,
            cancellationToken);
    }

    private static async Task DownloadMuxedFallbackAsync(
        YoutubeClient youtube,
        StreamManifest manifest,
        WorkItem work,
        string authorFolder,
        string baseName,
        DateTimeOffset publishedAt,
        CrawlerDownloadOptions options,
        Action<string> log,
        Action<MediaTransferProgress> reportProgress,
        CancellationToken cancellationToken)
    {
        var muxedStreams = manifest.GetMuxedStreams().ToArray();
        if (muxedStreams.Length == 0)
            throw new InvalidOperationException("YouTube 未返回可用的视频流或音频流。");

        var muxed = muxedStreams.GetWithHighestVideoQuality();
        var extension = GetContainerExtension(muxed.Container);
        var finalPath = Path.Combine(authorFolder, baseName + extension);
        if (IsUsableFile(finalPath))
        {
            ApplyPublishedTimestamp(finalPath, publishedAt);
            log($"文件已存在，跳过：{Path.GetFileName(finalPath)}");
            await DownloadCoverIfRequestedAsync(
                work,
                authorFolder,
                baseName,
                publishedAt,
                options,
                log,
                cancellationToken);
            return;
        }

        var partPath = Path.Combine(
            authorFolder,
            $".{baseName}.{Guid.NewGuid():N}.part{extension}");
        var displayFileName = Path.GetFileName(finalPath);
        try
        {
            log($"YouTube 仅返回兼容合并流，正在下载：{work.Description}");
            await youtube.Videos.Streams.DownloadAsync(
                muxed,
                partPath,
                new InlineProgress<double>(value =>
                    ReportDownloadProgress(
                        reportProgress,
                        displayFileName,
                        Math.Clamp(value * 100, 0, 100),
                        "正在下载 YouTube 视频")),
                cancellationToken);
            File.Move(partPath, finalPath, true);
            ApplyPublishedTimestamp(finalPath, publishedAt);
            log($"下载完成：{displayFileName}");
        }
        finally
        {
            TryDelete(partPath);
            ClearProgress(reportProgress, displayFileName);
        }

        await DownloadCoverIfRequestedAsync(
            work,
            authorFolder,
            baseName,
            publishedAt,
            options,
            log,
            cancellationToken);
    }

    private static async Task<Channel> ResolveChannelAsync(
        YoutubeClient youtube,
        string channelRootUrl,
        CancellationToken cancellationToken)
    {
        var path = new Uri(channelRootUrl).AbsolutePath;
        if (path.StartsWith("/@", StringComparison.OrdinalIgnoreCase))
            return await youtube.Channels.GetByHandleAsync(channelRootUrl, cancellationToken);
        if (path.StartsWith("/c/", StringComparison.OrdinalIgnoreCase))
            return await youtube.Channels.GetBySlugAsync(channelRootUrl, cancellationToken);
        if (path.StartsWith("/user/", StringComparison.OrdinalIgnoreCase))
            return await youtube.Channels.GetByUserAsync(channelRootUrl, cancellationToken);
        return await youtube.Channels.GetAsync(channelRootUrl, cancellationToken);
    }

    private static bool TryGetChannelRootUrl(string pageUrl, out string channelRootUrl)
    {
        channelRootUrl = string.Empty;
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri)
            || !(uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
                 || uri.Host.Equals("www.youtube.com", StringComparison.OrdinalIgnoreCase)
                 || uri.Host.Equals("m.youtube.com", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
            return false;

        string rootPath;
        if (segments[0].StartsWith('@'))
        {
            rootPath = "/" + segments[0];
        }
        else if (segments.Length >= 2
                 && (segments[0].Equals("channel", StringComparison.OrdinalIgnoreCase)
                     || segments[0].Equals("c", StringComparison.OrdinalIgnoreCase)
                     || segments[0].Equals("user", StringComparison.OrdinalIgnoreCase)))
        {
            rootPath = $"/{segments[0]}/{segments[1]}";
        }
        else
        {
            return false;
        }

        channelRootUrl = "https://www.youtube.com" + rootPath;
        return true;
    }

    private static IReadOnlyList<MediaAsset> BuildPlaceholderAssets(
        string videoId,
        string? coverUrl)
    {
        var assets = new List<MediaAsset>
        {
            // 实际流由 ISiteManagedDownloadAdapter 下载；占位项用于复用通用的
            // 作品有效性检查和完成索引，不会作为普通 HTTP 地址访问。
            new(MediaAssetType.Video, 0, new[] { $"youtube://{videoId}" })
        };
        if (!string.IsNullOrWhiteSpace(coverUrl))
            assets.Add(new MediaAsset(MediaAssetType.Cover, 0, new[] { coverUrl }));
        return assets;
    }

    private static async Task DownloadCoverIfRequestedAsync(
        WorkItem work,
        string authorFolder,
        string baseName,
        DateTimeOffset publishedAt,
        CrawlerDownloadOptions options,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        if (!options.DownloadCover)
            return;

        var coverUrl = work.Assets
            .FirstOrDefault(asset => asset.Type == MediaAssetType.Cover)?
            .CandidateUrls.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(coverUrl))
        {
            log($"作品未提供封面地址：{work.WorkId}");
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, coverUrl);
        request.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/150 Safari/537.36");
        request.Headers.Referrer = new Uri(work.SourceUrl);
        using var response = await ImageHttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var extension = ResolveImageExtension(
            response.Content.Headers.ContentType,
            coverUrl);
        var coverPath = Path.Combine(authorFolder, baseName + "_cover" + extension);
        if (IsUsableFile(coverPath))
        {
            ApplyPublishedTimestamp(coverPath, publishedAt);
            log($"封面已存在，跳过：{Path.GetFileName(coverPath)}");
            return;
        }

        var partPath = coverPath + ".part";
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(
                partPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
            File.Move(partPath, coverPath, true);
            ApplyPublishedTimestamp(coverPath, publishedAt);
            log($"封面下载完成：{Path.GetFileName(coverPath)}");
        }
        finally
        {
            TryDelete(partPath);
        }
    }

    private static HttpClient CreateImageHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            UseProxy = true,
            Proxy = HttpClient.DefaultProxy
        })
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("image/avif"));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("image/webp"));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("image/*"));
        return client;
    }

    private static string ResolveImageExtension(
        MediaTypeHeaderValue? contentType,
        string url)
    {
        var mediaType = contentType?.MediaType?.ToLowerInvariant();
        if (mediaType == "image/png") return ".png";
        if (mediaType == "image/webp") return ".webp";
        if (mediaType == "image/gif") return ".gif";
        if (mediaType is "image/avif" or "image/avif-sequence") return ".avif";

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
            if (extension is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".avif")
                return extension;
        }

        return ".jpg";
    }

    private static string GetContainerExtension(Container container)
        => container == Container.WebM
            ? ".webm"
            : container == Container.Tgpp
                ? ".3gp"
                : ".mp4";

    private static void ReportDownloadProgress(
        Action<MediaTransferProgress> reportProgress,
        string fileName,
        double percent,
        string stage)
        => reportProgress(new MediaTransferProgress(
            true,
            fileName,
            MediaAssetType.Video,
            0,
            null,
            0,
            Math.Clamp(percent, 0, 100),
            stage));

    private static void ClearProgress(
        Action<MediaTransferProgress> reportProgress,
        string fileName)
        => reportProgress(new MediaTransferProgress(
            false,
            fileName,
            MediaAssetType.Video,
            0,
            null,
            0,
            null));

    private static bool IsUsableFile(string path)
        => File.Exists(path) && new FileInfo(path).Length > 0;

    private static void ApplyPublishedTimestamp(string path, DateTimeOffset publishedAt)
    {
        try
        {
            var local = publishedAt.ToLocalTime().DateTime;
            File.SetLastWriteTime(path, local);
            File.SetCreationTime(path, local);
        }
        catch
        {
            // 时间戳失败不影响已下载文件。
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 临时文件清理由下一次下载覆盖。
        }
    }

    private static double ReadDouble(System.Text.Json.JsonElement element, string name)
        => element.ValueKind == System.Text.Json.JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.TryGetDouble(out var number)
            ? number
            : 0;

    private static int ReadInt32(System.Text.Json.JsonElement element, string name)
        => element.ValueKind == System.Text.Json.JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.TryGetInt32(out var number)
            ? number
            : 0;

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
