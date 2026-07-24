using HelloCrab.Core.Models;
using HelloCrab.Core.Services.Browser;

namespace HelloCrab.Core.Sites;

public interface ISiteAdapter
{
    string Id { get; }
    string DisplayName { get; }
    string HomeUrl { get; }

    bool CanHandlePage(string pageUrl);
    bool IsTargetResponse(string responseUrl, string resourceType, int statusCode, string? requestBody);

    /// <summary>
    /// 处理不直接产生作品列表、但会补充作者资料等信息的辅助接口响应。
    /// 返回 true 表示该响应已被消费，不再放入作品处理队列。
    /// </summary>
    bool TryHandleAuxiliaryResponse(
        string responseUrl,
        string responseJson,
        string pageUrl,
        string? requestBody,
        out string? diagnostic)
    {
        diagnostic = null;
        return false;
    }

    ParsedWorkBatch ParseResponse(string responseUrl, string responseJson, string pageUrl, string? requestBody);

    /// <summary>
    /// 在判断历史完成索引之前补充作者名、头像等轻量元数据。
    /// 不应在这里请求作品详情或媒体地址，避免已下载作品产生额外请求。
    /// </summary>
    Task<WorkItem> EnrichWorkMetadataAsync(
        WorkItem work,
        IBrowserAutomationService browser,
        CancellationToken cancellationToken)
        => Task.FromResult(work);

    /// <summary>
    /// 对列表接口中只有作品 ID/令牌、尚未包含真实媒体地址的作品执行详情补全。
    /// 大多数平台的列表响应已经包含下载地址，因此默认原样返回。
    /// </summary>
    Task<WorkItem?> ResolveWorkAsync(
        WorkItem work,
        IBrowserAutomationService browser,
        CancellationToken cancellationToken)
        => Task.FromResult<WorkItem?>(work);

    Task ScrollNextAsync(IBrowserAutomationService browser, CancellationToken cancellationToken);
    Task<PageScrollState> GetScrollStateAsync(IBrowserAutomationService browser, CancellationToken cancellationToken);
}

public sealed record PageScrollState(
    double ScrollY,
    double ViewportHeight,
    double DocumentHeight,
    string ContainerName = "document",
    int WorkItemCount = 0)
{
    public double MaxScrollTop => Math.Max(0, DocumentHeight - ViewportHeight);

    public bool IsNearBottom(double tolerance = 120)
        => ScrollY + ViewportHeight >= DocumentHeight - tolerance;
}
