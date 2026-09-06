using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using HelloCrab.Core.Models;
using HelloCrab.Core.Services.Browser;
using HelloCrab.Core.Services.Localization;

namespace HelloCrab.Core.Sites.Weibo;

/// <summary>
/// 在原微博解析器外补充更稳健的游标翻页和接口诊断。
/// Cookie 仍由当前 Playwright 页面上下文通过 credentials=include 自动携带；
/// 这里只保证 page/since_id 组合完整，并把微博分页响应的关键字段写入运行日志。
/// </summary>
public sealed class WeiboReliableSiteAdapter : ISiteAdapter
{
    private readonly ISiteAdapter _inner = new WeiboSiteAdapter();
    private readonly ConcurrentDictionary<string, int> _responseStatuses = new(StringComparer.Ordinal);

    public string Id => _inner.Id;
    public string DisplayName => _inner.DisplayName;
    public string HomeUrl => _inner.HomeUrl;

    public bool CanHandlePage(string pageUrl) => _inner.CanHandlePage(pageUrl);

    public bool IsTargetResponse(
        string responseUrl,
        string resourceType,
        int statusCode,
        string? requestBody)
    {
        if ((!resourceType.Equals("xhr", StringComparison.OrdinalIgnoreCase)
             && !resourceType.Equals("fetch", StringComparison.OrdinalIgnoreCase))
            || !Uri.TryCreate(responseUrl, UriKind.Absolute, out var uri)
            || !IsWeiboHost(uri.Host)
            || !uri.AbsolutePath.TrimEnd('/').Equals(
                "/ajax/statuses/mymblog",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 3xx 只属于中间跳转，不作为作品页响应处理；2xx 以及 4xx/5xx 都保留，
        // 这样直连分页被风控拒绝时也能打印状态码和响应正文摘要。
        if (statusCode is >= 300 and < 400)
            return false;

        _responseStatuses[responseUrl] = statusCode;
        return true;
    }

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
        if (!_responseStatuses.TryRemove(responseUrl, out var statusCode))
            statusCode = 200;

        var summary = InspectResponse(responseJson);
        var technicalDiagnostic = BuildResponseDiagnostic(
            responseUrl,
            responseJson,
            statusCode,
            summary);

        if (statusCode is < 200 or >= 300)
        {
            return new ParsedWorkBatch(
                Array.Empty<WorkItem>(),
                null,
                null,
                technicalDiagnostic);
        }

        try
        {
            var batch = _inner.ParseResponse(
                responseUrl,
                responseJson,
                pageUrl,
                requestBody);

            return batch with
            {
                Diagnostic = JoinDiagnostics(technicalDiagnostic, batch.Diagnostic)
            };
        }
        catch (JsonException)
        {
            return new ParsedWorkBatch(
                Array.Empty<WorkItem>(),
                null,
                null,
                technicalDiagnostic);
        }
    }

    public bool TryCreateCursorRequest(
        BrowserRequestSnapshot previousRequest,
        string cursor,
        out BrowserPageRequest nextRequest)
    {
        if (!_inner.TryCreateCursorRequest(previousRequest, cursor, out var candidate))
        {
            nextRequest = null!;
            return false;
        }

        // 微博 mymblog 翻页需要 page 与 since_id 配合。原请求若带 page=1，
        // 现有逻辑会自然递增到 page=2；若第一页 URL 根本没有 page，则补 page=2。
        // 对空值、非数字或异常的 page 也统一修正为第二页，避免重复返回第一页。
        if (TryReadPositivePage(candidate.Url, out var page) && page >= 2)
        {
            nextRequest = candidate;
            return true;
        }

        var candidateSnapshot = new BrowserRequestSnapshot(
            candidate.Url,
            candidate.Method,
            candidate.Body,
            candidate.Headers);

        if (CursorRequestRewriter.TrySetQueryParameter(
                candidateSnapshot,
                "page",
                "2",
                out var correctedRequest))
        {
            nextRequest = correctedRequest;
            return true;
        }

        nextRequest = candidate;
        return true;
    }

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

    public Task ScrollNextAsync(
        IBrowserAutomationService browser,
        CancellationToken cancellationToken)
        => _inner.ScrollNextAsync(browser, cancellationToken);

    public Task<PageScrollState> GetScrollStateAsync(
        IBrowserAutomationService browser,
        CancellationToken cancellationToken)
        => _inner.GetScrollStateAsync(browser, cancellationToken);

    private static WeiboResponseSummary InspectResponse(string responseJson)
    {
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object)
            {
                return new WeiboResponseSummary(null, null, false);
            }

            int? listCount = data.TryGetProperty("list", out var list)
                             && list.ValueKind == JsonValueKind.Array
                ? list.GetArrayLength()
                : null;

            string? sinceId = null;
            if (data.TryGetProperty("since_id", out var sinceIdElement))
            {
                sinceId = sinceIdElement.ValueKind switch
                {
                    JsonValueKind.String => sinceIdElement.GetString(),
                    JsonValueKind.Number => sinceIdElement.GetRawText(),
                    _ => null
                };
            }

            return new WeiboResponseSummary(listCount, sinceId, true);
        }
        catch (JsonException)
        {
            return new WeiboResponseSummary(null, null, false);
        }
    }

    private static string BuildResponseDiagnostic(
        string responseUrl,
        string responseJson,
        int statusCode,
        WeiboResponseSummary summary)
    {
        var page = ReadQueryValue(responseUrl, "page") ?? "(missing)";
        var status = statusCode.ToString(CultureInfo.InvariantCulture);
        var bodyBytes = Encoding.UTF8.GetByteCount(responseJson);
        var listCount = summary.ListCount?.ToString(CultureInfo.InvariantCulture) ?? "?";
        var sinceId = string.IsNullOrWhiteSpace(summary.SinceId)
            ? "(empty)"
            : summary.SinceId;

        var diagnostic = RuntimeLocalization.Format(
            "Weibo.ResponseDiagnostic",
            "微博接口响应：HTTP {0}，page={1}，响应 {2} 字节，list={3}，since_id={4}。",
            status,
            page,
            bodyBytes,
            listCount,
            sinceId);

        if (statusCode is < 200 or >= 300 || !summary.HasDataObject || !summary.ListCount.HasValue)
        {
            diagnostic += " " + RuntimeLocalization.Format(
                "Weibo.ResponsePreview",
                "内容前180字符：{0}",
                BuildPreview(responseJson));
        }

        return diagnostic;
    }

    private static string BuildPreview(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "(empty)";

        var compact = text
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return compact.Length <= 180 ? compact : compact[..180];
    }

    private static string? ReadQueryValue(string url, string name)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var rawName = separator >= 0 ? pair[..separator] : pair;
            if (!string.Equals(
                    WebUtility.UrlDecode(rawName.Replace('+', ' ')),
                    name,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rawValue = separator >= 0 ? pair[(separator + 1)..] : string.Empty;
            return WebUtility.UrlDecode(rawValue.Replace('+', ' '));
        }

        return null;
    }

    private static bool TryReadPositivePage(string url, out long page)
        => long.TryParse(
               ReadQueryValue(url, "page"),
               NumberStyles.Integer,
               CultureInfo.InvariantCulture,
               out page)
           && page > 0;

    private static bool IsWeiboHost(string host)
        => host.Equals("weibo.com", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".weibo.com", StringComparison.OrdinalIgnoreCase);

    private static string JoinDiagnostics(string first, string? second)
        => string.IsNullOrWhiteSpace(second)
            ? first
            : $"{first} {second}";

    private sealed record WeiboResponseSummary(
        int? ListCount,
        string? SinceId,
        bool HasDataObject);
}
