using HelloCrab.Core.Models;

namespace HelloCrab.Core.Sites;

/// <summary>
/// 不依赖 Playwright 页面响应、可以直接通过站点 HTTP 接口分页获取作品的适配器。
/// </summary>
public interface IDirectSiteAdapter
{
    Task<DirectSiteResponse> FetchPageAsync(
        string pageUrl,
        string? cursor,
        CrawlerDownloadOptions options,
        CancellationToken cancellationToken);
}

public sealed record DirectSiteResponse(
    string ResponseUrl,
    string ResponseJson,
    string? RequestBody = null);
