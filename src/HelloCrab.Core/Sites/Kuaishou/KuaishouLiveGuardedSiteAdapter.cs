using System.Net;
using HelloCrab.Core.Models;
using HelloCrab.Core.Services.Browser;

namespace HelloCrab.Core.Sites.Kuaishou;

/// <summary>
/// 为快手 Live 作者页增加分页卡死保护。
///
/// 某些情况下页面滚动会不断请求同一个 pcursor，并反复返回同一批作品；
/// 接口本身仍标记 hasMore=true，导致采集器把这些重复响应当成“仍有进展”。
/// 连续 5 次收到相同请求游标且作品批次也完全相同时，按已到末页处理。
/// </summary>
public sealed class KuaishouLiveGuardedSiteAdapter : ISiteAdapter
{
    private const int RepeatedPageStopThreshold = 5;

    private readonly KuaishouSiteAdapter _inner = new();
    private string? _lastRequestCursor;
    private string? _lastBatchSignature;
    private int _samePageResponseCount;
    private bool _repeatedPageEndDetected;

    public string Id => _inner.Id;
    public string DisplayName => _inner.DisplayName;
    public string HomeUrl => _inner.HomeUrl;

    public bool CanHandlePage(string pageUrl)
        => _inner.CanHandlePage(pageUrl);

    public bool IsTargetResponse(
        string responseUrl,
        string resourceType,
        int statusCode,
        string? requestBody)
        => _inner.IsTargetResponse(responseUrl, resourceType, statusCode, requestBody);

    public bool TryHandleAuxiliaryResponse(
        string responseUrl,
        string responseJson,
        string pageUrl,
        string? requestBody,
        out string? diagnostic)
        => _inner.TryHandleAuxiliaryResponse(
            responseUrl,
            responseJson,
            pageUrl,
            requestBody,
            out diagnostic);

    public int? TryReadTotalWorkCount(
        string responseUrl,
        string responseJson,
        string pageUrl,
        string? requestBody)
        => _inner.TryReadTotalWorkCount(
            responseUrl,
            responseJson,
            pageUrl,
            requestBody);

    public ParsedWorkBatch ParseResponse(
        string responseUrl,
        string responseJson,
        string pageUrl,
        string? requestBody)
    {
        var batch = _inner.ParseResponse(responseUrl, responseJson, pageUrl, requestBody);

        if (!TryReadLivePublicCursor(responseUrl, out var requestCursor))
        {
            ResetRepeatedPageGuard();
            return batch;
        }

        var batchSignature = BuildBatchSignature(batch);
        if (string.Equals(requestCursor, _lastRequestCursor, StringComparison.Ordinal)
            && string.Equals(batchSignature, _lastBatchSignature, StringComparison.Ordinal))
        {
            _samePageResponseCount++;
        }
        else
        {
            _lastRequestCursor = requestCursor;
            _lastBatchSignature = batchSignature;
            _samePageResponseCount = 1;
            _repeatedPageEndDetected = false;
        }

        if (_samePageResponseCount < RepeatedPageStopThreshold)
            return batch;

        _repeatedPageEndDetected = true;
        return batch with { HasMore = false };
    }

    public bool TryCreateCursorRequest(
        BrowserRequestSnapshot previousRequest,
        string cursor,
        out BrowserPageRequest nextRequest)
        => _inner.TryCreateCursorRequest(previousRequest, cursor, out nextRequest);

    public Task<WorkItem> EnrichWorkMetadataAsync(
        WorkItem work,
        IBrowserAutomationService browser,
        CancellationToken cancellationToken)
        => _inner.EnrichWorkMetadataAsync(work, browser, cancellationToken);

    public Task<WorkItem?> ResolveWorkAsync(
        WorkItem work,
        IBrowserAutomationService browser,
        CancellationToken cancellationToken)
        => _inner.ResolveWorkAsync(work, browser, cancellationToken);

    public async Task ScrollNextAsync(
        IBrowserAutomationService browser,
        CancellationToken cancellationToken)
    {
        if (!_repeatedPageEndDetected)
        {
            await _inner.ScrollNextAsync(browser, cancellationToken);
            return;
        }

        // CrawlCoordinator 对 hasMore=false 会等待短暂静默后结束。
        // 此处不再继续触发滚动请求，并留出静默窗口，避免重复响应刷新计时器。
        await Task.Delay(TimeSpan.FromMilliseconds(3_200), cancellationToken);
    }

    public async Task<PageScrollState> GetScrollStateAsync(
        IBrowserAutomationService browser,
        CancellationToken cancellationToken)
    {
        var state = await _inner.GetScrollStateAsync(browser, cancellationToken);
        if (!_repeatedPageEndDetected)
            return state;

        // 已确认接口卡在同一页时，将状态标记为“接近底部”，让通用滚动循环使用短观察窗口。
        return state with { ScrollY = state.MaxScrollTop };
    }

    private static bool TryReadLivePublicCursor(string responseUrl, out string cursor)
    {
        cursor = string.Empty;
        if (!Uri.TryCreate(responseUrl, UriKind.Absolute, out var uri)
            || !uri.Host.Equals("live.kuaishou.com", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.Equals("/live_api/profile/public", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var encodedName = separator >= 0 ? pair[..separator] : pair;
            var name = WebUtility.UrlDecode(encodedName);
            if (!name.Equals("pcursor", StringComparison.OrdinalIgnoreCase))
                continue;

            var encodedValue = separator >= 0 ? pair[(separator + 1)..] : string.Empty;
            cursor = WebUtility.UrlDecode(encodedValue).Trim();
            return !string.IsNullOrWhiteSpace(cursor);
        }

        return false;
    }

    private static string BuildBatchSignature(ParsedWorkBatch batch)
    {
        if (batch.Works.Count == 0)
            return "<empty>";

        return string.Join(
            '\u001f',
            batch.Works
                .Select(work => work.WorkId)
                .OrderBy(workId => workId, StringComparer.Ordinal));
    }

    private void ResetRepeatedPageGuard()
    {
        _lastRequestCursor = null;
        _lastBatchSignature = null;
        _samePageResponseCount = 0;
        _repeatedPageEndDetected = false;
    }
}
