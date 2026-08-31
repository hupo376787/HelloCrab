using System.Text.Json;

namespace HelloCrab.Core.Services.Browser;

public interface IBrowserAutomationService : IAsyncDisposable
{
    bool IsStarted { get; }
    bool IsHeadless { get; }
    bool IsLoginRecoveryActive { get; }
    string CurrentUrl { get; }
    string PreferredChromiumInstallDirectory { get; }

    event EventHandler<BrowserStateChangedEventArgs>? StateChanged;
    event EventHandler<BrowserResponseReceivedEventArgs>? ResponseReceived;

    Task<int> InstallChromiumAsync(
        IProgress<ChromiumInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<string?> FindInstalledChromiumPathAsync(CancellationToken cancellationToken = default);
    Task StartAsync(
        string initialUrl,
        bool headless,
        CancellationToken cancellationToken = default);
    Task NavigateAsync(string url, CancellationToken cancellationToken = default);
    Task<string> SelectForegroundPageAsync(CancellationToken cancellationToken = default);
    Task<string> FetchTextAsync(string url, CancellationToken cancellationToken = default);
    Task<byte[]> FetchBytesAsync(
        string url,
        string? referer,
        CancellationToken cancellationToken = default);
    Task ReloadAsync(CancellationToken cancellationToken = default);
    Task SetCaptureLockAsync(bool isLocked, CancellationToken cancellationToken = default);
    Task<BrowserDownloadContext> GetDownloadContextAsync(CancellationToken cancellationToken = default);

    Task<JsonElement> EvaluatePageAsync(
        string expression,
        CancellationToken cancellationToken = default);

    Task<JsonElement> EvaluatePageAsync(
        string expression,
        object? argument,
        CancellationToken cancellationToken = default);

    Task MoveMouseAsync(double x, double y, CancellationToken cancellationToken = default);
    Task WheelAsync(double deltaX, double deltaY, CancellationToken cancellationToken = default);
    Task PressKeyAsync(string key, CancellationToken cancellationToken = default);
}

public sealed record BrowserStateChangedEventArgs(
    bool IsStarted,
    string CurrentUrl,
    string Message,
    bool IsHeadless,
    bool IsLoginRecoveryActive);

public sealed class BrowserResponseReceivedEventArgs : EventArgs
{
    private readonly Func<CancellationToken, Task<string>> _readBodyAsync;

    public BrowserResponseReceivedEventArgs(
        string url,
        string resourceType,
        int statusCode,
        string pageUrl,
        string requestMethod,
        string? requestPostData,
        IReadOnlyDictionary<string, string> requestHeaders,
        Func<CancellationToken, Task<string>> readBodyAsync)
    {
        Url = url;
        ResourceType = resourceType;
        StatusCode = statusCode;
        PageUrl = pageUrl;
        RequestMethod = requestMethod;
        RequestPostData = requestPostData;
        RequestHeaders = requestHeaders;
        _readBodyAsync = readBodyAsync;
    }

    public string Url { get; }
    public string ResourceType { get; }
    public int StatusCode { get; }
    public string PageUrl { get; }
    public string RequestMethod { get; }
    public string? RequestPostData { get; }
    public IReadOnlyDictionary<string, string> RequestHeaders { get; }

    public Task<string> ReadBodyAsync(CancellationToken cancellationToken = default)
        => _readBodyAsync(cancellationToken);
}

/// <summary>
/// 从已成功返回的浏览器网络请求中保存的分页请求模板。
/// Cookie、User-Agent 等受浏览器控制的请求头不需要显式保存；再次 fetch 时会
/// 由当前页面上下文自动附带。这里保留业务接口需要的普通请求头。
/// </summary>
public sealed record BrowserRequestSnapshot(
    string Url,
    string Method,
    string? Body,
    IReadOnlyDictionary<string, string> Headers);

/// <summary>使用当前登录页面上下文发出的下一页接口请求。</summary>
public sealed record BrowserPageRequest(
    string Url,
    string Method,
    string? Body,
    IReadOnlyDictionary<string, string> Headers);

public sealed class BrowserDownloadContext
{
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<BrowserCookie>>> _getCookiesAsync;

    public BrowserDownloadContext(
        string userAgent,
        string referer,
        Func<string, CancellationToken, Task<IReadOnlyList<BrowserCookie>>> getCookiesAsync)
    {
        UserAgent = userAgent;
        Referer = referer;
        _getCookiesAsync = getCookiesAsync;
    }

    public string UserAgent { get; }
    public string Referer { get; }

    public Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(
        string url,
        CancellationToken cancellationToken = default)
        => _getCookiesAsync(url, cancellationToken);
}

public sealed record BrowserCookie(string Name, string Value);


/// <summary>
/// Playwright Chromium 安装器报告的当前组件下载进度。
/// Percent 为空时表示正在准备、解析或解压，无法计算确定百分比。
/// </summary>
public sealed record ChromiumInstallProgress(
    double? Percent,
    string Stage,
    string? Detail = null);
