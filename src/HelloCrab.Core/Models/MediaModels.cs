using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;

namespace HelloCrab.Core.Models;

public enum MediaAssetType
{
    Video,
    Image,
    Cover,
    Music
}

public sealed record MediaAsset(
    MediaAssetType Type,
    int Index,
    IReadOnlyList<string> CandidateUrls,
    long Bitrate = 0,
    int Width = 0,
    int Height = 0,
    string? Codec = null);

public sealed record WorkItem(
    string PlatformId,
    string WorkId,
    string AuthorId,
    string AuthorName,
    string? AuthorAvatarUrl,
    string Description,
    long CreateTime,
    IReadOnlyList<MediaAsset> Assets,
    string SourceUrl)
{
    /// <summary>作者主页地址，用于历史记录重新采集；未设置时使用 SourceUrl。</summary>
    public string? AuthorPageUrl { get; init; }

    /// <summary>媒体下载请求的 Referer；未设置时使用 SourceUrl。</summary>
    public string? MediaRefererUrl { get; init; }

    /// <summary>
    /// 列表响应只给出封面、但作品类型表明可能还有视频时，由站点适配器补取详情。
    /// 默认 false，避免普通图片作品产生不必要的详情请求。
    /// </summary>
    public bool RequiresDetailResolution { get; init; }
}

public sealed record CrawlerDownloadOptions(
    bool IncludeWorkId = false,
    bool DownloadCover = false,
    bool DownloadMusic = false,
    bool CheckVideoAudio = false,
    bool EnablePersonDetection = false,
    bool StopOnDuplicateThreshold = true,
    int DuplicateStopThreshold = 20,
    decimal DownloadSpeedLimitMBps = 0,
    double PersonDetectionConfidence = 0.60);

public sealed record ParsedWorkBatch(
    IReadOnlyList<WorkItem> Works,
    bool? HasMore,
    string? Cursor,
    string? Diagnostic = null,
    int RejectedWorkCount = 0);

public sealed record CrawlProgressSnapshot(
    int ResponseCount,
    int DiscoveredCount,
    int DownloadedCount,
    int SkippedCount,
    int FailedCount,
    string? CurrentWork,
    bool IsProcessing,
    string? CurrentAuthorId,
    string? CurrentAuthorName,
    string? CurrentAuthorAvatarUrl,
    string? CurrentAuthorDirectory,
    string? CurrentCoverUrl,
    string? CurrentSourceUrl)
{
    public bool IsDownloading { get; init; }
    public bool IsDownloadIndeterminate { get; init; }
    public double DownloadProgressPercent { get; init; }
    public string? DownloadProgressText { get; init; }
}

public sealed record MediaTransferProgress(
    bool IsActive,
    string FileName,
    MediaAssetType AssetType,
    long BytesReceived,
    long? TotalBytes,
    double BytesPerSecond,
    double? Percent,
    string? Stage = null,
    int CompletedParts = 0,
    int TotalParts = 0);

public sealed class PlatformOption : ObservableObject
{
    private string _displayName;

    public PlatformOption(string id, string displayName, string homeUrl)
    {
        Id = id;
        OriginalDisplayName = displayName;
        _displayName = displayName;
        HomeUrl = homeUrl;
        Icon = LoadIcon(id);
    }

    public string Id { get; }
    public string OriginalDisplayName { get; }
    public string HomeUrl { get; }
    public IImage? Icon { get; }

    public string DisplayName
    {
        get => _displayName;
        private set => SetProperty(ref _displayName, value);
    }

    public void SetDisplayName(string displayName)
        => DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? OriginalDisplayName
            : displayName;

    public override string ToString() => DisplayName;

    private static IImage? LoadIcon(string platformId)
    {
        try
        {
            var iconUri = new Uri($"avares://HelloCrab.Core/Assets/Platforms/{platformId}.png");
            using var stream = AssetLoader.Open(iconUri);
            return new Bitmap(stream);
        }
        catch
        {
            // 自定义或新增平台暂未提供图标时，仍允许平台列表正常显示文字。
            return null;
        }
    }
}
