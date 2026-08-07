using System.Net;
using System.Reflection;
using System.Text.Json;
using HelloCrab.Core.Models;
using HelloCrab.Core.Services.Localization;

namespace HelloCrab.Core.Services.Notifications;

public sealed class PushPlusNotificationService : IDisposable
{
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public async Task SendDownloadCompletedAsync(
        string token,
        DownloadHistoryItem history,
        int downloadedWorkCount,
        bool isUpdate,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return;

        ArgumentNullException.ThrowIfNull(history);

        var nickName = string.IsNullOrWhiteSpace(history.UserName)
            ? RuntimeLocalization.Get("PushPlus.UnknownAuthor", "未知作者")
            : history.UserName.Trim();
        var uid = string.IsNullOrWhiteSpace(history.UserId)
            ? "unknown"
            : history.UserId.Trim();
        var parsedUrl = history.OriginalUrl?.Trim() ?? string.Empty;
        var headUrl = history.HeadUrl?.Trim() ?? string.Empty;
        var currentVersion = GetCurrentVersion();
        var normalizedCount = Math.Max(0, downloadedWorkCount);
        var completionSummary = isUpdate
            ? RuntimeLocalization.Format("PushPlus.Summary.Update", "下载完成，更新了 {0} 个作品", normalizedCount)
            : RuntimeLocalization.Format("PushPlus.Summary.New", "下载完成，共 {0} 个作品", normalizedCount);

        var title = $"HelloCrab({nickName}){completionSummary}";
        var linkHtml = $"<a href=\"{WebUtility.HtmlEncode(parsedUrl)}\">{WebUtility.HtmlEncode(parsedUrl)}</a>";
        var imageHtml = $"<br> <img src=\"{WebUtility.HtmlEncode(headUrl)}\">";
        var content = RuntimeLocalization.Format(
            "PushPlus.Body",
            "作者：{0}（{1}），{2}于 {3} {4}，当前程序版本 V{5}。{6}",
            WebUtility.HtmlEncode(nickName),
            WebUtility.HtmlEncode(uid),
            linkHtml,
            DateTime.Now.ToString("HH:mm:ss"),
            WebUtility.HtmlEncode(completionSummary),
            WebUtility.HtmlEncode(currentVersion),
            imageHtml);

        var requestUrl =
            "http://www.pushplus.plus/send" +
            $"?token={Uri.EscapeDataString(token.Trim())}" +
            $"&title={Uri.EscapeDataString(title)}" +
            $"&content={Uri.EscapeDataString(content)}";

        using var response = await _httpClient.GetAsync(requestUrl, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                RuntimeLocalization.Format(
                    "PushPlus.HttpFailed",
                    "PushPlus HTTP {0} {1}：{2}",
                    (int)response.StatusCode,
                    response.ReasonPhrase,
                    TrimResponse(responseText)));
        }

        if (TryReadPushPlusError(responseText, out var error))
            throw new InvalidOperationException(error);
    }

    private static bool TryReadPushPlusError(string json, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            var code = 0;
            if (root.TryGetProperty("code", out var codeElement))
            {
                if (codeElement.ValueKind == JsonValueKind.Number)
                    codeElement.TryGetInt32(out code);
                else
                    int.TryParse(codeElement.ToString(), out code);
            }

            if (code is 0 or 200)
                return false;

            var message = root.TryGetProperty("msg", out var msgElement)
                ? msgElement.ToString()
                : root.TryGetProperty("message", out var messageElement)
                    ? messageElement.ToString()
                    : RuntimeLocalization.Get("PushPlus.UnknownError", "未知错误");
            error = RuntimeLocalization.Format(
                    "PushPlus.ApiFailed",
                    "PushPlus 返回失败（code={0}）：{1}",
                    code,
                    message);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string GetCurrentVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version
                      ?? Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? "1.0.0" : version.ToString(3);
    }

    private static string TrimResponse(string value)
    {
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 240 ? normalized : normalized[..240] + "…";
    }

    public void Dispose() => _httpClient.Dispose();
}
