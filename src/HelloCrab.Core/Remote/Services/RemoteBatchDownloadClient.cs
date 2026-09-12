using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HelloCrab.Core.Contracts;

namespace HelloCrab.Core.Remote.Services;

/// <summary>
/// Browser / Android / iOS 向桌面主机提交批量下载文本。
/// 请求体使用 text/plain，避免为一段大文本额外引入 JSON DTO，
/// 响应继续使用现有 RemoteCommandResult 源生成元数据。
/// </summary>
internal static class RemoteBatchDownloadClient
{
    private const string TokenHeader = "X-SMC-Token";

    public static async Task<RemoteCommandResult> SubmitAsync(
        string serverAddress,
        string accessToken,
        string content,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(
                serverAddress.Trim().TrimEnd('/') + "/",
                UriKind.Absolute,
                out var baseAddress)
            || baseAddress.Scheme is not ("http" or "https"))
        {
            return RemoteCommandResult.Fail("主机地址必须是 http:// 或 https:// 地址。");
        }

        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(baseAddress, "api/batch-download"));

        if (!string.IsNullOrWhiteSpace(accessToken))
            request.Headers.TryAddWithoutValidation(TokenHeader, accessToken.Trim());

        request.Content = new StringContent(
            content ?? string.Empty,
            Encoding.UTF8,
            "text/plain");

        try
        {
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            RemoteCommandResult? result = null;
            try
            {
                result = await response.Content.ReadFromJsonAsync(
                    RemoteJsonContext.Default.RemoteCommandResult,
                    cancellationToken);
            }
            catch (JsonException)
            {
            }
            catch (NotSupportedException)
            {
            }

            result ??= RemoteCommandResult.Fail(
                $"桌面主机返回 HTTP {(int)response.StatusCode} {response.ReasonPhrase}。");

            if (!response.IsSuccessStatusCode)
                result.Success = false;

            if (response.StatusCode == HttpStatusCode.Unauthorized
                && string.IsNullOrWhiteSpace(result.Message))
            {
                result.Message = "远程访问令牌不正确。";
            }

            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RemoteCommandResult.Fail("连接桌面主机超时，请确认远程服务器已开启。");
        }
        catch (HttpRequestException ex)
        {
            return RemoteCommandResult.Fail($"无法访问桌面主机：{ex.Message}");
        }
    }
}
