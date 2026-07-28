using HelloCrab.Core.Models;

namespace HelloCrab.Core.Sites;

/// <summary>
/// 少数平台不能把媒体流当作普通 HTTP 文件直接下载时，可由站点适配器接管单条作品下载。
/// 其他站点无需实现，仍使用通用 MediaDownloadService。
/// </summary>
public interface ISiteManagedDownloadAdapter
{
    Task DownloadWorkAsync(
        WorkItem work,
        string platformDownloadRoot,
        CrawlerDownloadOptions options,
        Action<string> log,
        Action<MediaTransferProgress> reportProgress,
        CancellationToken cancellationToken);
}
