using System.Text.Json;
using HelloCrab.Core.Services.Browser;
using HelloCrab.Desktop.Chromium;
using Microsoft.Playwright;

namespace HelloCrab.Desktop.Playwright;

public sealed class PlaywrightBrowserService : IBrowserAutomationService
{
    private const string CaptureLockElementId = "__social_media_crawler_capture_lock__";
    private static readonly TimeSpan AuthenticationPollInterval = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly PlaywrightChromiumInstaller _chromiumInstaller;
    private readonly HashSet<IPage> _attachedPages = new();

    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private IPage? _page;
    private IPage? _lockedPage;
    private string? _lockedUrl;
    private string _currentUrl = string.Empty;
    private string _targetUrl = string.Empty;
    private bool _captureLockEnabled;
    private bool _restoringLockedPage;
    private bool _requestedHeadless;
    private bool _actualHeadless;
    private bool _loginRecoveryActive;
    private CancellationTokenSource? _authenticationMonitorCts;
    private long _authenticationMonitorGeneration;

    public PlaywrightBrowserService(PlaywrightChromiumInstaller chromiumInstaller)
    {
        _chromiumInstaller = chromiumInstaller;
    }

    public bool IsStarted => _context is not null;
    public bool IsHeadless => _context is not null && _actualHeadless;
    public bool IsLoginRecoveryActive => _loginRecoveryActive;
    public string CurrentUrl => _currentUrl;
    public string PreferredChromiumInstallDirectory => _chromiumInstaller.PreferredInstallDirectory;

    private IPage? ActivePage => _captureLockEnabled && _lockedPage is not null && !_lockedPage.IsClosed
        ? _lockedPage
        : _page;

    public event EventHandler<BrowserStateChangedEventArgs>? StateChanged;
    public event EventHandler<BrowserResponseReceivedEventArgs>? ResponseReceived;

    public Task<int> InstallChromiumAsync(
        IProgress<ChromiumInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => _chromiumInstaller.InstallAsync(progress, cancellationToken);

    public Task<string?> FindInstalledChromiumPathAsync(
        CancellationToken cancellationToken = default)
        => _chromiumInstaller.FindInstalledExecutablePathAsync(cancellationToken);

    public async Task StartAsync(
        string initialUrl,
        bool headless,
        CancellationToken cancellationToken = default)
    {
        var normalizedUrl = NormalizeUrl(initialUrl);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            StopAuthenticationMonitorCore();
            _requestedHeadless = headless;
            _loginRecoveryActive = false;
            _targetUrl = normalizedUrl;

            await EnsureContextCoreAsync(headless, cancellationToken);
            await NavigateCoreAsync(
                normalizedUrl,
                headless ? "无头浏览器已启动" : "浏览器已启动，请扫码登录并进入作者主页",
                cancellationToken);

            if (headless && await IsLoginRequiredAsync(GetActivePage(), cancellationToken))
                await EnterVisibleLoginRecoveryCoreAsync(cancellationToken);

            StartAuthenticationMonitorCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task NavigateAsync(string url, CancellationToken cancellationToken = default)
    {
        var normalizedUrl = NormalizeUrl(url);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_context is null)
                throw new InvalidOperationException("浏览器尚未启动。");

            _targetUrl = normalizedUrl;
            await NavigateCoreAsync(normalizedUrl, "页面已打开", cancellationToken);

            if (_requestedHeadless
                && _actualHeadless
                && await IsLoginRequiredAsync(GetActivePage(), cancellationToken))
            {
                await EnterVisibleLoginRecoveryCoreAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> SelectForegroundPageAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var page = await ResolveForegroundPageAsync(cancellationToken);
            AttachPage(page);
            _page = page;
            UpdateUrl(page.Url, "已选择当前活动标签页");
            return page.Url;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> FetchTextAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target)
            || target.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("请输入有效的 HTTP 或 HTTPS URL。", nameof(url));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var page = GetActivePage();
        if (page.IsClosed)
            throw new InvalidOperationException("当前采集页面已经关闭。");

        try
        {
            // 在已经登录的作者主页上下文中执行同源 fetch。这样会自动携带 Chromium
            // 当前 Cookie、User-Agent 和站点运行时环境，不需要在 .NET 侧重放小红书
            // 的动态签名请求，也不会改变或关闭被锁定的作者主页标签页。
            var task = page.EvaluateAsync<string>(
                """
                async url => {
                    const response = await fetch(url, {
                        method: 'GET',
                        credentials: 'include',
                        redirect: 'follow',
                        cache: 'no-store',
                        headers: {
                            'Accept': 'text/html,application/xhtml+xml,application/json;q=0.9,*/*;q=0.8'
                        }
                    });

                    const text = await response.text();
                    if (!response.ok)
                        throw new Error(`HTTP ${response.status} ${response.statusText}: ${text.slice(0, 180)}`);
                    return text;
                }
                """,
                target.ToString());

            var text = await task.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return text;
        }
        catch (PlaywrightException ex) when (IsTargetClosedError(ex.Message))
        {
            throw new InvalidOperationException(
                "读取作品详情时浏览器页面被关闭，请重新打开作者主页后再试。",
                ex);
        }
    }

    public async Task<byte[]> FetchBytesAsync(
        string url,
        string? referer,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target)
            || target.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("请输入有效的 HTTP 或 HTTPS URL。", nameof(url));
        }

        var context = _context ?? throw new InvalidOperationException("浏览器尚未启动。");
        cancellationToken.ThrowIfCancellationRequested();

        var headers = new Dictionary<string, string>
        {
            ["Accept"] = "*/*"
        };
        if (Uri.TryCreate(referer, UriKind.Absolute, out var refererUri))
        {
            headers["Referer"] = refererUri.AbsoluteUri;
            headers["Origin"] = refererUri.GetLeftPart(UriPartial.Authority);
        }

        var response = await context.APIRequest.GetAsync(
                target.AbsoluteUri,
                new()
                {
                    Headers = headers,
                    FailOnStatusCode = false,
                    MaxRetries = 2,
                    Timeout = 0
                })
            .WaitAsync(cancellationToken);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!response.Ok)
            {
                var responseText = await response.TextAsync();
                throw new HttpRequestException(
                    $"浏览器请求 HTTP {response.Status} {response.StatusText}: " +
                    responseText[..Math.Min(responseText.Length, 180)]);
            }

            return await response.BodyAsync().WaitAsync(cancellationToken);
        }
        finally
        {
            await response.DisposeAsync();
        }
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        var page = GetActivePage();
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await page.ReloadAsync(new PageReloadOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60_000
            });
        }
        catch (PlaywrightException ex) when (IsTargetClosedError(ex.Message))
        {
            throw new InvalidOperationException(
                "刷新作者主页时浏览器页面被关闭。请重新打开作者主页后再开始采集。",
                ex);
        }
        catch (PlaywrightException ex) when (!page.IsClosed && IsRecoverableNavigationInterruption(ex.Message))
        {
            // 快手页面刷新后可能立即由前端路由接管导航，Playwright 会报告 ERR_ABORTED 或
            // navigation interrupted，但页面本身仍然有效。等待新导航稳定后继续捕获即可。
            try
            {
                await page.WaitForLoadStateAsync(
                    LoadState.DOMContentLoaded,
                    new PageWaitForLoadStateOptions { Timeout = 15_000 });
            }
            catch
            {
                // 页面可能已经完成加载，或仍在后台请求；后续接口捕获会继续判断。
            }

            RaiseState(true, page.Url, "页面刷新由站点脚本接管，已继续等待作品接口");
        }

        if (page.IsClosed)
            throw new InvalidOperationException("作者主页在刷新过程中被关闭，请重新打开后再试。");

        if (_captureLockEnabled)
            await ApplyCaptureLockOverlayAsync(page);
        UpdateUrl(page.Url, "作者主页已刷新，开始捕获第一页数据");
    }

    public async Task SetCaptureLockAsync(bool isLocked, CancellationToken cancellationToken = default)
    {
        // 登录恢复监测也可能重启 Chromium 上下文。锁定/解锁必须与该流程使用同一把锁，
        // 否则点击“开始采集”的瞬间可能刚好关闭当前页面，随后出现 TargetClosedException。
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (isLocked)
            {
                // 用户可能同时打开多个标签页。采集开始时应锁定 Chromium 当前真正
                // 位于前台的标签页，而不是持久化会话中的第一个页面或最后创建的页面。
                var page = await ResolveForegroundPageAsync(cancellationToken);
                AttachPage(page);
                _page = page;
                _captureLockEnabled = true;
                _lockedPage = page;
                _lockedUrl = page.Url;
                await ApplyCaptureLockOverlayAsync(page);
                UpdateUrl(page.Url, "采集标签页已锁定，停止采集后可继续操作");
                return;
            }

            var lockedPage = _lockedPage;
            _captureLockEnabled = false;
            _lockedPage = null;
            _lockedUrl = null;
            _restoringLockedPage = false;

            if (lockedPage is not null && !lockedPage.IsClosed)
            {
                try
                {
                    await lockedPage.EvaluateAsync("""
                        () => {
                            const cleanup = window.__smcCaptureLockCleanup;
                            if (typeof cleanup === 'function') cleanup();
                            document.getElementById('__social_media_crawler_capture_lock__')?.remove();
                        }
                        """);
                }
                catch
                {
                    // 页面可能正在跳转或已经关闭，解除锁定时无需阻断退出流程。
                }
            }

            var active = _page is not null && !_page.IsClosed
                ? _page
                : _context?.Pages.LastOrDefault(candidate => !candidate.IsClosed);
            if (active is not null)
            {
                _page = active;
                UpdateUrl(active.Url, "采集标签页已解锁");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BrowserDownloadContext> GetDownloadContextAsync(CancellationToken cancellationToken = default)
    {
        var context = _context ?? throw new InvalidOperationException("浏览器尚未启动。");
        var page = GetActivePage();
        cancellationToken.ThrowIfCancellationRequested();
        var userAgent = await page.EvaluateAsync<string>("() => navigator.userAgent");
        return new BrowserDownloadContext(
            userAgent,
            page.Url,
            async (url, token) =>
            {
                token.ThrowIfCancellationRequested();
                var cookies = await context.CookiesAsync(new[] { url });
                token.ThrowIfCancellationRequested();
                return cookies.Select(cookie => new BrowserCookie(cookie.Name, cookie.Value)).ToArray();
            });
    }

    public async Task<JsonElement> EvaluatePageAsync(
        string expression,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await GetActivePage().EvaluateAsync<JsonElement>(expression);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public async Task<JsonElement> EvaluatePageAsync(
        string expression,
        object? argument,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await GetActivePage().EvaluateAsync<JsonElement>(expression, argument);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public async Task MoveMouseAsync(double x, double y, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await GetActivePage().Mouse.MoveAsync((float)x, (float)y);
    }

    public async Task WheelAsync(double deltaX, double deltaY, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await GetActivePage().Mouse.WheelAsync((float)deltaX, (float)deltaY);
    }

    public async Task PressKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await GetActivePage().Keyboard.PressAsync(key);
    }

    private async Task EnsureContextCoreAsync(bool headless, CancellationToken cancellationToken)
    {
        if (_context is not null && _actualHeadless == headless)
        {
            _page ??= _context.Pages.LastOrDefault(x => !x.IsClosed) ?? await _context.NewPageAsync();
            AttachPage(_page);
            return;
        }

        await RestartContextCoreAsync(headless, cancellationToken);
    }

    private async Task RestartContextCoreAsync(bool headless, CancellationToken cancellationToken)
    {
        await CloseContextCoreAsync();
        cancellationToken.ThrowIfCancellationRequested();
        await LaunchContextCoreAsync(headless);
    }

    private async Task LaunchContextCoreAsync(bool headless)
    {
        // 明确指定浏览器可执行文件：优先程序目录，找不到再回退到
        // Playwright 原来的用户缓存目录。这样不依赖全局环境变量。
        var chromiumExecutablePath = await _chromiumInstaller
            .FindInstalledExecutablePathAsync(CancellationToken.None);
        if (string.IsNullOrWhiteSpace(chromiumExecutablePath))
        {
            throw new InvalidOperationException(
                "未找到 Chromium。请先点击“安装 Chromium”。程序会优先安装到程序目录。"
            );
        }

        _playwright ??= await Microsoft.Playwright.Playwright.CreateAsync();

        // 使用程序目录中的持久化 profile，使登录状态随便携版程序一起移动。
        var userDataDir = Path.Combine(AppContext.BaseDirectory, "browser-profile");
        TryMigrateLegacyBrowserProfile(userDataDir);

        try
        {
            Directory.CreateDirectory(userDataDir);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"无法在程序目录创建浏览器数据目录：{userDataDir}。请将 HelloCrab 放到当前用户可写的目录。",
                ex);
        }

        var args = new List<string>
        {
            "--disable-background-timer-throttling",
            "--disable-backgrounding-occluded-windows",
            "--disable-renderer-backgrounding"
        };
        if (!headless)
            args.Insert(0, "--start-maximized");

        _context = await _playwright.Chromium.LaunchPersistentContextAsync(
            userDataDir,
            new BrowserTypeLaunchPersistentContextOptions
            {
                Headless = headless,
                ExecutablePath = chromiumExecutablePath,
                Locale = "zh-CN",
                AcceptDownloads = true,
                ViewportSize = headless
                    ? new ViewportSize { Width = 1440, Height = 900 }
                    : ViewportSize.NoViewport,
                Args = args
            });

        _actualHeadless = headless;
        _attachedPages.Clear();

        var context = _context ?? throw new InvalidOperationException("浏览器上下文创建失败。");
        context.Page += (_, page) => AttachPage(page);
        context.Close += (_, _) => HandleContextClosed(context);

        _page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
        AttachPage(_page);
        RaiseState(true, _targetUrl, "已使用 Playwright Chromium");
    }

    private static void TryMigrateLegacyBrowserProfile(string destinationDirectory)
    {
        try
        {
            if (Directory.Exists(destinationDirectory)
                && Directory.EnumerateFileSystemEntries(destinationDirectory).Any())
            {
                return;
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
                return;

            var legacyDirectory = Path.Combine(localAppData, "HelloCrab", "browser-profile");
            if (!Directory.Exists(legacyDirectory)
                || string.Equals(
                    Path.GetFullPath(legacyDirectory),
                    Path.GetFullPath(destinationDirectory),
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                return;
            }

            Directory.CreateDirectory(destinationDirectory);

            foreach (var directory in Directory.EnumerateDirectories(
                         legacyDirectory,
                         "*",
                         SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(legacyDirectory, directory);
                Directory.CreateDirectory(Path.Combine(destinationDirectory, relativePath));
            }

            foreach (var file in Directory.EnumerateFiles(
                         legacyDirectory,
                         "*",
                         SearchOption.AllDirectories))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Equals("SingletonLock", StringComparison.OrdinalIgnoreCase)
                    || fileName.Equals("SingletonCookie", StringComparison.OrdinalIgnoreCase)
                    || fileName.Equals("SingletonSocket", StringComparison.OrdinalIgnoreCase)
                    || fileName.Equals("DevToolsActivePort", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(legacyDirectory, file);
                var targetPath = Path.Combine(destinationDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

                try
                {
                    File.Copy(file, targetPath, overwrite: false);
                }
                catch (IOException)
                {
                    // 目标文件已存在，或旧 profile 中存在临时占用文件时直接跳过。
                }
                catch (UnauthorizedAccessException)
                {
                    // 个别浏览器临时文件不可复制时不阻止使用新的便携 profile。
                }
            }
        }
        catch
        {
            // 旧目录迁移失败不阻止浏览器启动；Playwright 会在程序目录创建新的 profile。
        }
    }

    private async Task CloseContextCoreAsync()
    {
        var context = _context;
        _context = null;
        _page = null;
        _lockedPage = null;
        _lockedUrl = null;
        _captureLockEnabled = false;
        _restoringLockedPage = false;
        _attachedPages.Clear();

        if (context is null)
            return;

        try
        {
            await context.CloseAsync();
        }
        catch
        {
            // 上下文可能已由用户关闭。
        }
    }

    private void HandleContextClosed(IBrowserContext context)
    {
        if (!ReferenceEquals(_context, context))
            return;

        _context = null;
        _page = null;
        _lockedPage = null;
        _lockedUrl = null;
        _captureLockEnabled = false;
        _attachedPages.Clear();
        StopAuthenticationMonitorCore();
        RaiseState(false, string.Empty, "浏览器已关闭");
    }

    private async Task NavigateCoreAsync(
        string requestedUrl,
        string message,
        CancellationToken cancellationToken)
    {
        var page = GetActivePage();
        cancellationToken.ThrowIfCancellationRequested();

        var response = await page.GotoAsync(requestedUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 60_000
        });

        cancellationToken.ThrowIfCancellationRequested();
        var resolvedTarget = ResolveTargetUrl(requestedUrl, response, page.Url);
        if (!string.IsNullOrWhiteSpace(resolvedTarget))
            _targetUrl = resolvedTarget;

        UpdateUrl(_targetUrl, message);
    }

    private async Task EnterVisibleLoginRecoveryCoreAsync(CancellationToken cancellationToken)
    {
        if (!_requestedHeadless || _loginRecoveryActive)
            return;

        _loginRecoveryActive = true;
        var targetUrl = _targetUrl;
        RaiseState(
            true,
            targetUrl,
            "检测到登录失效，正在关闭无头浏览器并切换为显示模式扫码登录");

        await RestartContextCoreAsync(false, cancellationToken);
        await NavigateCoreAsync(targetUrl, "已切换显示模式，请扫码登录", cancellationToken);
        RaiseState(
            true,
            targetUrl,
            "请在显示的 Chromium 中扫码登录；登录成功后将自动恢复无头模式并返回目标 URL");
    }

    private async Task ResumeHeadlessAfterLoginCoreAsync(CancellationToken cancellationToken)
    {
        if (!_requestedHeadless || !_loginRecoveryActive)
            return;

        var targetUrl = _targetUrl;
        RaiseState(true, targetUrl, "检测到登录成功，正在恢复无头模式并返回目标 URL");

        await RestartContextCoreAsync(true, cancellationToken);
        await NavigateCoreAsync(targetUrl, "已恢复无头模式并返回目标 URL", cancellationToken);

        if (await IsLoginRequiredAsync(GetActivePage(), cancellationToken))
        {
            await RestartContextCoreAsync(false, cancellationToken);
            await NavigateCoreAsync(targetUrl, "登录状态尚未生效，请继续扫码登录", cancellationToken);
            RaiseState(true, targetUrl, "登录状态尚未生效，请继续在显示窗口中完成登录");
            return;
        }

        _loginRecoveryActive = false;
        RaiseState(true, _targetUrl, "登录成功，已恢复无头模式");
    }

    private void StartAuthenticationMonitorCore()
    {
        StopAuthenticationMonitorCore();
        if (!_requestedHeadless || _context is null)
            return;

        var generation = Interlocked.Increment(ref _authenticationMonitorGeneration);
        var cts = new CancellationTokenSource();
        _authenticationMonitorCts = cts;
        _ = MonitorAuthenticationAsync(generation, cts.Token);
    }

    private void StopAuthenticationMonitorCore()
    {
        Interlocked.Increment(ref _authenticationMonitorGeneration);
        var cts = Interlocked.Exchange(ref _authenticationMonitorCts, null);
        if (cts is null)
            return;

        try
        {
            cts.Cancel();
        }
        finally
        {
            cts.Dispose();
        }
    }

    private async Task MonitorAuthenticationAsync(long generation, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(AuthenticationPollInterval, cancellationToken);
                if (generation != Interlocked.Read(ref _authenticationMonitorGeneration))
                    return;

                // 采集期间禁止登录监测重启浏览器上下文。快手作者页可能常驻“登录后查看更多”
                // 提示，但作品接口仍能正常加载；旧逻辑会误判登录失效并关闭采集页面。
                if (_captureLockEnabled)
                    continue;

                var page = ActivePage;
                if (!_requestedHeadless || page is null || page.IsClosed)
                    continue;

                if (_actualHeadless && !_loginRecoveryActive)
                {
                    if (!await IsLoginRequiredAsync(page, cancellationToken))
                        continue;

                    await _gate.WaitAsync(cancellationToken);
                    try
                    {
                        if (generation == Interlocked.Read(ref _authenticationMonitorGeneration)
                            && _requestedHeadless
                            && _actualHeadless
                            && !_loginRecoveryActive
                            && !_captureLockEnabled)
                        {
                            await EnterVisibleLoginRecoveryCoreAsync(cancellationToken);
                        }
                    }
                    finally
                    {
                        _gate.Release();
                    }
                }
                else if (!_actualHeadless && _loginRecoveryActive)
                {
                    if (!await IsLoginCompletedAsync(page, cancellationToken))
                        continue;

                    await _gate.WaitAsync(cancellationToken);
                    try
                    {
                        if (generation == Interlocked.Read(ref _authenticationMonitorGeneration)
                            && _requestedHeadless
                            && !_actualHeadless
                            && _loginRecoveryActive
                            && !_captureLockEnabled)
                        {
                            await ResumeHeadlessAfterLoginCoreAsync(cancellationToken);
                        }
                    }
                    finally
                    {
                        _gate.Release();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 模式变化或程序退出时正常结束。
        }
        catch (Exception ex)
        {
            RaiseState(
                _context is not null,
                _targetUrl,
                $"登录状态监测失败：{ex.Message}");
        }
    }

    private async Task<bool> IsLoginRequiredAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (page.IsClosed)
            return false;

        // 明确跳转到登录地址时，所有平台都可以进入可视化登录恢复。
        if (IsLikelyLoginUrl(page.Url))
            return true;

        // 当前自动登录判定只对抖音使用会话 Cookie 和页面提示。
        // 快手作者页即使能正常浏览，也可能显示“登录后查看更多”等常驻文案，
        // 不能据此重启浏览器，否则采集中的页面会被关闭。
        if (!IsDouyinUrl(_targetUrl) && !IsDouyinUrl(page.Url))
            return false;

        if (!await HasDouyinSessionCookieAsync(cancellationToken))
            return true;

        return await HasVisibleLoginPromptAsync(page);
    }

    private async Task<bool> IsLoginCompletedAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (page.IsClosed || IsLikelyLoginUrl(page.Url))
            return false;

        if (!IsDouyinUrl(_targetUrl) && !IsDouyinUrl(page.Url))
        {
            // 非抖音平台只有在真正进入登录地址时才会触发恢复；离开登录地址即认为完成。
            return true;
        }

        if (!await HasDouyinSessionCookieAsync(cancellationToken))
            return false;

        return !await HasVisibleLoginPromptAsync(page);
    }

    private async Task<bool> HasDouyinSessionCookieAsync(CancellationToken cancellationToken)
    {
        var context = _context;
        if (context is null)
            return false;

        cancellationToken.ThrowIfCancellationRequested();
        var cookies = await context.CookiesAsync(new[] { "https://www.douyin.com/" });
        cancellationToken.ThrowIfCancellationRequested();

        return cookies.Any(cookie =>
            cookie.Name.Equals("sessionid", StringComparison.OrdinalIgnoreCase)
            || cookie.Name.Equals("sessionid_ss", StringComparison.OrdinalIgnoreCase)
            || cookie.Name.Equals("sid_guard", StringComparison.OrdinalIgnoreCase)
            || cookie.Name.Equals("sid_tt", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<bool> HasVisibleLoginPromptAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<bool>("""
                () => {
                    const isVisible = element => {
                        if (!(element instanceof Element)) return false;
                        const style = getComputedStyle(element);
                        if (style.display === 'none' || style.visibility === 'hidden' || Number(style.opacity) === 0)
                            return false;
                        const rect = element.getBoundingClientRect();
                        return rect.width > 40 && rect.height > 30;
                    };

                    const strongTexts = [
                        '扫码登录', '二维码登录', '验证码登录', '请先登录',
                        '登录后即可', '登录后查看', '登录后继续'
                    ];

                    const candidates = [
                        ...document.querySelectorAll(
                            'iframe[src*="passport"], iframe[src*="login"], [class*="login-modal" i], ' +
                            '[class*="login-dialog" i], [data-e2e*="login" i], [id*="login" i], ' +
                            'img[src*="qrcode" i], canvas'
                        )
                    ].filter(isVisible);

                    for (const element of candidates) {
                        const text = (element.innerText || element.getAttribute?.('aria-label') || '').trim();
                        if (strongTexts.some(value => text.includes(value))) return true;
                        if (element.matches('iframe[src*="passport"], iframe[src*="login"]')) return true;
                        if (element.matches('img[src*="qrcode" i]')) return true;
                    }

                    const visibleTextElements = [
                        ...document.querySelectorAll('dialog, [role=\"dialog\"], section, aside, button, p, span, h1, h2, h3')
                    ].filter(isVisible).slice(0, 2500);

                    return visibleTextElements.some(element => {
                        const text = (element.innerText || element.textContent || '').trim();
                        return text.length > 0
                            && text.length <= 160
                            && strongTexts.some(value => text.includes(value));
                    });
                }
                """);
        }
        catch
        {
            // 页面切换中的瞬时脚本失败不直接判定为登录失效。
            return false;
        }
    }

    private async Task<IPage> ResolveForegroundPageAsync(CancellationToken cancellationToken)
    {
        var context = _context ?? throw new InvalidOperationException("浏览器尚未启动。");
        var pages = context.Pages.Where(page => !page.IsClosed).ToArray();
        if (pages.Length == 0)
            throw new InvalidOperationException("当前没有可锁定的浏览器页面。");

        // Chromium 只有当前选中的标签页同时具备 document.hasFocus()。从后往前检查，
        // 在极短的标签切换过程中也优先选择最近创建/激活的页面。
        foreach (var page in pages.Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await page.EvaluateAsync<bool>("() => document.hasFocus()"))
                    return page;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (PlaywrightException ex) when (IsTargetClosedError(ex.Message))
            {
            }
            catch
            {
                // 页面正在导航时脚本可能暂时不可执行，继续检查其他页面。
            }
        }

        // 某些页面在导航初期 hasFocus() 暂时为 false，使用 visibilityState 作为第二层判断。
        foreach (var page in pages.Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await page.EvaluateAsync<bool>("() => document.visibilityState === 'visible'"))
                    return page;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (PlaywrightException ex) when (IsTargetClosedError(ex.Message))
            {
            }
            catch
            {
            }
        }

        // 无法执行脚本时维持最近由服务记录的页面，最后才退回最后一个存活标签页。
        return _page is not null && !_page.IsClosed
            ? _page
            : pages[^1];
    }

    private IPage GetActivePage()
        => ActivePage ?? throw new InvalidOperationException("浏览器尚未启动或当前没有可用页面。");

    private void AttachPage(IPage page)
    {
        if (_captureLockEnabled && _lockedPage is not null && !ReferenceEquals(page, _lockedPage))
        {
            _ = CloseUnexpectedPageAsync(page);
            return;
        }

        if (!_attachedPages.Add(page))
        {
            if (!_captureLockEnabled)
                _page = page;
            return;
        }

        if (!_captureLockEnabled)
            _page = page;

        page.Response += (_, response) => OnPageResponse(page, response);
        page.FrameNavigated += (_, frame) => _ = HandleFrameNavigatedAsync(page, frame);
        page.DOMContentLoaded += (_, _) =>
        {
            if (_captureLockEnabled && ReferenceEquals(page, _lockedPage))
                _ = ApplyCaptureLockOverlayAsync(page);
        };
        page.Close += (_, _) =>
        {
            _attachedPages.Remove(page);
            if (ReferenceEquals(_lockedPage, page))
            {
                _captureLockEnabled = false;
                _lockedPage = null;
                _lockedUrl = null;
                RaiseState(
                    _context is not null,
                    string.Empty,
                    "采集标签页已被关闭，请停止采集后重新打开作者主页");
            }

            if (ReferenceEquals(_page, page))
                _page = _context?.Pages.LastOrDefault(x => !x.IsClosed);
        };

        if (!string.IsNullOrWhiteSpace(page.Url)
            && !page.Url.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
        {
            UpdateUrl(_loginRecoveryActive ? _targetUrl : page.Url, "已连接浏览器页面");
        }
    }

    private void OnPageResponse(IPage page, IResponse response)
    {
        // 持久化 Chromium profile 可能恢复多个旧标签页。此前每个已附加页面的
        // /aweme/post 响应都会被转发给采集器，因此后台旧标签页也可能把其他作者
        // 的作品送入下载队列。采集期间只允许锁定标签页产生采集响应；未采集时
        // 也只转发当前活动页，彻底隔离其他标签页的网络请求。
        if (_captureLockEnabled)
        {
            if (_lockedPage is null || !ReferenceEquals(page, _lockedPage))
                return;
        }
        else if (_page is not null && !ReferenceEquals(page, _page))
        {
            return;
        }

        var responsePageUrl = page.Url;
        ResponseReceived?.Invoke(this, new BrowserResponseReceivedEventArgs(
            response.Url,
            response.Request.ResourceType,
            response.Status,
            responsePageUrl,
            response.Request.PostData,
            async cancellationToken =>
            {
                // response.TextAsync 本身不支持 CancellationToken。停止采集或页面关闭时先返回空，
                // 避免为了检查取消状态主动抛出大量 TaskCanceledException。
                if (cancellationToken.IsCancellationRequested || page.IsClosed)
                    return string.Empty;

                try
                {
                    var text = await response.TextAsync();
                    return cancellationToken.IsCancellationRequested ? string.Empty : text;
                }
                catch (PlaywrightException ex) when (
                    page.IsClosed
                    || cancellationToken.IsCancellationRequested
                    || IsTargetClosedError(ex.Message))
                {
                    return string.Empty;
                }
            }));
    }

    private async Task HandleFrameNavigatedAsync(IPage page, IFrame frame)
    {
        if (!ReferenceEquals(frame, page.MainFrame))
            return;

        var currentUrl = page.Url;
        if (_requestedHeadless
            && _actualHeadless
            && !_loginRecoveryActive
            && !_captureLockEnabled
            && IsLikelyLoginUrl(currentUrl))
        {
            _ = TriggerVisibleLoginRecoveryAsync();
            return;
        }

        if (_loginRecoveryActive)
        {
            RaiseState(true, _targetUrl, "显示模式登录窗口已打开，等待扫码登录");
            return;
        }

        if (_captureLockEnabled && ReferenceEquals(page, _lockedPage))
        {
            if (!IsSameAuthorLocation(currentUrl, _lockedUrl))
            {
                await RestoreLockedPageAsync(page, currentUrl);
                return;
            }

            UpdateUrl(currentUrl, "采集标签页已刷新");
            await ApplyCaptureLockOverlayAsync(page);
            return;
        }

        if (IsUsefulTargetUrl(currentUrl))
            _targetUrl = currentUrl;
        UpdateUrl(currentUrl, "页面已切换");
    }

    private async Task TriggerVisibleLoginRecoveryAsync()
    {
        try
        {
            await _gate.WaitAsync();
            try
            {
                if (_requestedHeadless && _actualHeadless && !_loginRecoveryActive)
                    await EnterVisibleLoginRecoveryCoreAsync(CancellationToken.None);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            RaiseState(_context is not null, _targetUrl, $"切换扫码登录模式失败：{ex.Message}");
        }
    }

    private async Task RestoreLockedPageAsync(IPage page, string unexpectedUrl)
    {
        if (_restoringLockedPage || !_captureLockEnabled || string.IsNullOrWhiteSpace(_lockedUrl))
            return;

        _restoringLockedPage = true;
        try
        {
            RaiseState(true, unexpectedUrl, "检测到采集标签页被误操作，正在恢复作者主页");
            await Task.Delay(150);
            if (!_captureLockEnabled || page.IsClosed || string.IsNullOrWhiteSpace(_lockedUrl))
                return;

            await page.GotoAsync(_lockedUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60_000
            });
            await ApplyCaptureLockOverlayAsync(page);
            UpdateUrl(page.Url, "已恢复并重新锁定作者主页");
        }
        catch (Exception ex)
        {
            RaiseState(true, page.IsClosed ? string.Empty : page.Url, $"恢复作者主页失败：{ex.Message}");
        }
        finally
        {
            _restoringLockedPage = false;
        }
    }

    private async Task CloseUnexpectedPageAsync(IPage page)
    {
        try
        {
            if (!page.IsClosed)
                await page.CloseAsync();
            RaiseState(true, _lockedPage?.Url ?? _currentUrl, "采集期间已阻止打开新的标签页");
        }
        catch
        {
            // 浏览器可能已经自行关闭该页面。
        }
    }

    private static string ResolveTargetUrl(string requestedUrl, IResponse? response, string pageUrl)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(pageUrl))
            candidates.Add(pageUrl);

        for (var request = response?.Request; request is not null; request = request.RedirectedFrom)
            candidates.Add(request.Url);

        candidates.Add(requestedUrl);

        return candidates.FirstOrDefault(IsUsefulTargetUrl) ?? requestedUrl;
    }

    private static string NormalizeUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("URL 不能为空。", nameof(value));

        var normalized = value.Trim();
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            normalized = "https://" + normalized.TrimStart('/');
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out uri))
                throw new ArgumentException("请输入有效的 HTTP 或 HTTPS URL。", nameof(value));
        }

        if (uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("仅支持 HTTP 或 HTTPS URL。", nameof(value));

        return uri.ToString();
    }

    private static bool IsUsefulTargetUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || url.Equals("about:blank", StringComparison.OrdinalIgnoreCase)
            || IsLikelyLoginUrl(url))
        {
            return false;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
               && uri.Scheme is "http" or "https";
    }

    private static bool IsLikelyLoginUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        return uri.Host.Contains("passport", StringComparison.OrdinalIgnoreCase)
               || uri.Host.Contains("sso", StringComparison.OrdinalIgnoreCase)
               || uri.AbsolutePath.Contains("/login", StringComparison.OrdinalIgnoreCase)
               || uri.AbsolutePath.Contains("/signin", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDouyinUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && (uri.Host.Equals("douyin.com", StringComparison.OrdinalIgnoreCase)
               || uri.Host.EndsWith(".douyin.com", StringComparison.OrdinalIgnoreCase));


    private static bool IsTargetClosedError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        // Microsoft.Playwright.TargetClosedException 在当前 Playwright .NET 版本中并非公开类型，
        // 因此只能捕获公开的 PlaywrightException，再按官方错误文本识别页面/上下文关闭。
        return message.Contains("Target page, context or browser has been closed", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Target closed", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Page closed", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Browser has been closed", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Context closed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRecoverableNavigationInterruption(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.Contains("ERR_ABORTED", StringComparison.OrdinalIgnoreCase)
               || message.Contains("interrupted by another navigation", StringComparison.OrdinalIgnoreCase)
               || message.Contains("navigation interrupted", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Navigation to", StringComparison.OrdinalIgnoreCase)
                  && message.Contains("interrupted", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameAuthorLocation(string? currentUrl, string? lockedUrl)
    {
        if (string.IsNullOrWhiteSpace(currentUrl) || string.IsNullOrWhiteSpace(lockedUrl))
            return false;

        if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var current)
            || !Uri.TryCreate(lockedUrl, UriKind.Absolute, out var locked))
        {
            return string.Equals(currentUrl, lockedUrl, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(current.Scheme, locked.Scheme, StringComparison.OrdinalIgnoreCase)
               && string.Equals(current.Host, locked.Host, StringComparison.OrdinalIgnoreCase)
               && string.Equals(
                   current.AbsolutePath.TrimEnd('/'),
                   locked.AbsolutePath.TrimEnd('/'),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static async Task ApplyCaptureLockOverlayAsync(IPage page)
    {
        if (page.IsClosed)
            return;

        try
        {
            await page.EvaluateAsync($$"""
                () => {
                    const elementId = '{{CaptureLockElementId}}';
                    const eventNames = [
                        'click', 'dblclick', 'mousedown', 'mouseup', 'pointerdown', 'pointerup',
                        'touchstart', 'touchmove', 'wheel', 'contextmenu', 'keydown', 'keypress',
                        'keyup', 'dragstart', 'drop'
                    ];

                    const createOverlay = () => {
                        let overlay = document.getElementById(elementId);
                        if (!overlay) {
                            overlay = document.createElement('div');
                            overlay.id = elementId;
                            overlay.tabIndex = 0;
                            overlay.innerHTML = `
                                <div style="min-width:320px;max-width:520px;padding:24px 28px;border-radius:16px;
                                            background:rgba(18,22,36,.92);border:1px solid rgba(255,255,255,.22);
                                            box-shadow:0 20px 70px rgba(0,0,0,.48);color:white;text-align:center;
                                            font-family:system-ui,-apple-system,'Segoe UI',sans-serif;">
                                    <div style="font-size:32px;line-height:1;margin-bottom:14px;">🔒</div>
                                    <div style="font-size:18px;font-weight:700;margin-bottom:8px;">采集标签页已锁定</div>
                                    <div style="font-size:13px;line-height:1.7;color:rgba(255,255,255,.76);">
                                        正在自动滚动和下载，请勿操作此页面。<br>
                                        回到采集软件点击“停止采集”后即可解锁。
                                    </div>
                                </div>`;
                            Object.assign(overlay.style, {
                                position: 'fixed', inset: '0', zIndex: '2147483647',
                                display: 'flex', alignItems: 'center', justifyContent: 'center',
                                background: 'rgba(4,7,15,.28)', backdropFilter: 'blur(2px)',
                                cursor: 'not-allowed', pointerEvents: 'auto'
                            });
                            (document.body || document.documentElement).appendChild(overlay);
                        }
                        if (window.__smcAllowAutomationInput !== true) {
                            try { overlay.focus({ preventScroll: true }); } catch { overlay.focus(); }
                        }
                    };

                    if (typeof window.__smcCaptureLockCleanup === 'function') {
                        window.__smcCaptureLockCleanup();
                    }

                    const prevent = event => {
                        if (window.__smcAllowAutomationInput === true) return;
                        event.preventDefault();
                        event.stopPropagation();
                        event.stopImmediatePropagation();
                    };
                    eventNames.forEach(name => document.addEventListener(name, prevent, true));
                    createOverlay();

                    const observer = new MutationObserver(createOverlay);
                    observer.observe(document.documentElement, { childList: true, subtree: true });
                    window.__smcCaptureLockCleanup = () => {
                        observer.disconnect();
                        eventNames.forEach(name => document.removeEventListener(name, prevent, true));
                        document.getElementById(elementId)?.remove();
                        delete window.__smcCaptureLockCleanup;
                        delete window.__smcAllowAutomationInput;
                    };
                }
                """);
        }
        catch
        {
            // 导航过程中的瞬时执行失败会在 DOMContentLoaded 后再次注入。
        }
    }

    private void UpdateUrl(string url, string message)
    {
        _currentUrl = url;
        RaiseState(_context is not null, url, message);
    }

    private void RaiseState(bool isStarted, string url, string message)
        => StateChanged?.Invoke(
            this,
            new BrowserStateChangedEventArgs(
                isStarted,
                url,
                message,
                _actualHeadless,
                _loginRecoveryActive));

    public async ValueTask DisposeAsync()
    {
        StopAuthenticationMonitorCore();
        _requestedHeadless = false;

        await _gate.WaitAsync();
        try
        {
            try
            {
                await SetCaptureLockAsync(false);
            }
            catch
            {
                // 页面可能已经关闭。
            }

            await CloseContextCoreAsync();
            _playwright?.Dispose();
            _playwright = null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
