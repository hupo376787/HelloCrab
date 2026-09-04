using System.Net;
using System.Text.Json;

namespace HelloCrab.Core.ViewModels;

public sealed partial class MainWindowViewModel
{
    private static readonly HttpClient WorkCountPushPlusHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    internal async Task SendWorkCountExceededPushPlusAsync(int? actualTotalWorkCount)
    {
        var token = (PushPlusToken ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(token))
            return;

        var summary = actualTotalWorkCount is > 500
            ? WorkCountPushPlusText(
                $"作品数超过500（实际解析到{actualTotalWorkCount.Value}个）",
                $"Work count exceeds 500 (actually parsed: {actualTotalWorkCount.Value})",
                $"作品数が500を超えました（実際の解析数：{actualTotalWorkCount.Value}件）")
            : WorkCountPushPlusText(
                "作品数超过500",
                "Work count exceeds 500",
                "作品数が500を超えました");

        var authorName = string.IsNullOrWhiteSpace(CurrentAuthorName)
            ? WorkCountPushPlusText("未知作者", "Unknown author", "不明な作者")
            : CurrentAuthorName.Trim();
        var title = $"HelloCrab({authorName}){summary}";
        var content = WebUtility.HtmlEncode(summary);
        var requestUrl =
            "http://www.pushplus.plus/send" +
            $"?token={Uri.EscapeDataString(token)}" +
            $"&title={Uri.EscapeDataString(title)}" +
            $"&content={Uri.EscapeDataString(content)}";

        try
        {
            using var response = await WorkCountPushPlusHttpClient.GetAsync(
                requestUrl,
                CancellationToken.None);
            var responseText = await response.Content.ReadAsStringAsync(CancellationToken.None);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(WorkCountPushPlusText(
                    $"PushPlus HTTP {(int)response.StatusCode} {response.ReasonPhrase}：{TrimWorkCountPushPlusResponse(responseText)}",
                    $"PushPlus HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {TrimWorkCountPushPlusResponse(responseText)}",
                    $"PushPlus HTTP {(int)response.StatusCode} {response.ReasonPhrase}：{TrimWorkCountPushPlusResponse(responseText)}"));
            }

            if (TryReadWorkCountPushPlusError(responseText, out var error))
                throw new InvalidOperationException(error);

            AddLog(WorkCountPushPlusText(
                $"PushPlus 作品数提醒已发送：{summary}",
                $"PushPlus work-count alert sent: {summary}",
                $"PushPlus 作品数通知を送信しました：{summary}"));
        }
        catch (Exception ex)
        {
            // 作品数提醒失败不影响当前采集和下载任务。
            AddLog(WorkCountPushPlusText(
                $"PushPlus 作品数提醒发送失败：{ex.Message}",
                $"Failed to send PushPlus work-count alert: {ex.Message}",
                $"PushPlus 作品数通知の送信に失敗しました：{ex.Message}"));
        }
    }

    private string WorkCountPushPlusText(string zhCn, string enUs, string jaJp)
    {
        var code = _localization.CurrentLanguageCode;
        if (code.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            return enUs;
        if (code.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
            return jaJp;
        return zhCn;
    }

    private string TryReadWorkCountPushPlusErrorMessage(JsonElement root)
    {
        if (root.TryGetProperty("msg", out var msgElement))
            return msgElement.ToString();
        if (root.TryGetProperty("message", out var messageElement))
            return messageElement.ToString();
        return WorkCountPushPlusText("未知错误", "Unknown error", "不明なエラー");
    }

    private bool TryReadWorkCountPushPlusError(string json, out string error)
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

            var message = TryReadWorkCountPushPlusErrorMessage(root);
            error = WorkCountPushPlusText(
                $"PushPlus 返回失败（code={code}）：{message}",
                $"PushPlus returned an error (code={code}): {message}",
                $"PushPlus がエラーを返しました（code={code}）：{message}");
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string TrimWorkCountPushPlusResponse(string value)
    {
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 240 ? normalized : normalized[..240] + "…";
    }
}
