using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HelloCrab.Core.Contracts;

namespace HelloCrab.Core.Remote.Services;

/// <summary>
/// Android、iOS 与 Browser 共用的桌面主机 HTTP 客户端。
///
/// 不使用 HttpClient.BaseAddress 或 DefaultRequestHeaders 保存连接设置：
/// HttpClient 一旦发出过请求，再修改这些属性会抛出
/// net_http_operation_started。连接地址和令牌改为原子替换的配置快照，
/// 每次请求根据快照创建 HttpRequestMessage，因此首次连接失败后可直接
/// 修改令牌或地址重新连接。
/// </summary>
public sealed class RemoteCrawlerClient : IDisposable
{
    private const string TokenHeader = "X-SMC-Token";

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private ConnectionOptions? _options;

    public void Configure(string serverAddress, string token)
    {
        if (!Uri.TryCreate(serverAddress.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var baseAddress)
            || baseAddress.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("服务器地址必须是 http:// 或 https:// 地址。", nameof(serverAddress));
        }

        if ((OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
            && IsLoopbackHost(baseAddress.Host))
        {
            throw new ArgumentException(
                "手机端不能使用 127.0.0.1、::1 或 localhost；请填写桌面端“远程控制服务器”状态中显示的局域网地址。",
                nameof(serverAddress));
        }

        // 不修改已启动 HttpClient 的 BaseAddress/DefaultRequestHeaders。
        // Volatile 写入后，新请求会立即使用新的地址和令牌；已在途请求继续
        // 使用其创建时的配置，不会被重新连接操作破坏。
        Volatile.Write(
            ref _options,
            new ConnectionOptions(baseAddress, token.Trim()));
    }

    public async Task<RemoteHealthDto> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "api/health", includeToken: false);
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync(
                   RemoteJsonContext.Default.RemoteHealthDto,
                   cancellationToken)
               ?? throw new InvalidOperationException("服务器没有返回健康状态。");
    }

    public async Task<RemoteCrawlerSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "api/snapshot");
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync(
                   RemoteJsonContext.Default.RemoteCrawlerSnapshot,
                   cancellationToken)
               ?? throw new InvalidOperationException("服务器没有返回采集状态。");
    }

    public async Task<byte[]?> GetCurrentCoverAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "api/current-cover");
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task<byte[]?> GetHistoryAvatarAsync(
        int historyId,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Get,
            $"api/history/{historyId}/avatar");
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task<RemoteCommandResult> ExecuteActionAsync(
        string action,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Post,
            $"api/actions/{Uri.EscapeDataString(action)}");
        using var response = await SendAsync(request, cancellationToken);
        var result = await ReadCommandResultAsync(response, cancellationToken);

        if (!response.IsSuccessStatusCode && result.Success)
            result.Success = false;

        return result;
    }

    public async Task<RemoteCommandResult> UpdateSettingsAsync(
        RemoteSettingsDto settings,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Put, "api/settings");
        request.Content = JsonContent.Create(
            settings,
            RemoteJsonContext.Default.RemoteSettingsDto);

        using var response = await SendAsync(request, cancellationToken);
        return await ReadCommandResultAsync(response, cancellationToken);
    }

    private HttpRequestMessage CreateRequest(
        HttpMethod method,
        string relativePath,
        bool includeToken = true)
    {
        var options = Volatile.Read(ref _options)
                      ?? throw new InvalidOperationException("请先填写主机地址并点击连接。");

        var request = new HttpRequestMessage(
            method,
            new Uri(options.BaseAddress, relativePath));

        if (includeToken && !string.IsNullOrWhiteSpace(options.Token))
            request.Headers.TryAddWithoutValidation(TokenHeader, options.Token);

        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("连接桌面主机超时，请确认远程服务器已开启且地址、端口正确。");
        }
        catch (HttpRequestException ex)
        {
            throw new HttpRequestException(BuildNetworkErrorMessage(request.RequestUri, ex), ex);
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var result = await TryReadCommandResultAsync(response, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new UnauthorizedAccessException(
                string.IsNullOrWhiteSpace(result?.Message)
                    ? "远程访问令牌不正确，请重新复制桌面端显示的完整令牌。"
                    : result.Message);
        }

        throw new HttpRequestException(
            string.IsNullOrWhiteSpace(result?.Message)
                ? $"桌面主机返回 HTTP {(int)response.StatusCode} {response.ReasonPhrase}。"
                : result.Message,
            inner: null,
            response.StatusCode);
    }

    private static async Task<RemoteCommandResult> ReadCommandResultAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var result = await TryReadCommandResultAsync(response, cancellationToken)
                     ?? RemoteCommandResult.Fail(
                         $"桌面主机返回 HTTP {(int)response.StatusCode} {response.ReasonPhrase}。");

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            result.Success = false;
            if (string.IsNullOrWhiteSpace(result.Message))
                result.Message = "远程访问令牌不正确。";
        }

        return result;
    }

    private static async Task<RemoteCommandResult?> TryReadCommandResultAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync(
                RemoteJsonContext.Default.RemoteCommandResult,
                cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsLoopbackHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        return IPAddress.TryParse(host, out var address)
               && IPAddress.IsLoopback(address);
    }

    private static string BuildNetworkErrorMessage(Uri? uri, HttpRequestException exception)
    {
        var target = uri is null ? "桌面主机" : $"桌面主机 {uri.GetLeftPart(UriPartial.Authority)}";
        var original = exception.Message;

        // Browser/WASM 的 fetch 在 CORS、混合内容、连接被拒绝等场景中
        // 经常只返回 Failed to fetch 或资源键，统一给出可操作的提示。
        if (original.Contains("Failed to fetch", StringComparison.OrdinalIgnoreCase)
            || original.Contains("TypeError", StringComparison.OrdinalIgnoreCase)
            || original.Contains("net_http", StringComparison.OrdinalIgnoreCase))
        {
            return $"无法访问{target}。请确认桌面端已开启“远程控制服务器”；同一台电脑填写 http://127.0.0.1:端口，手机或其他电脑必须填写桌面端显示的局域网 IP；若网页以 HTTPS 打开，请改用 HTTP 页面或为主机 API 配置 HTTPS。";
        }

        return $"无法访问{target}：{original}";
    }

    public void Dispose() => _httpClient.Dispose();

    private sealed record ConnectionOptions(Uri BaseAddress, string Token);
}
