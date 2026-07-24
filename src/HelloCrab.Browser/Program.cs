using System.Diagnostics;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Browser;
using Avalonia.Logging;
using HelloCrab.Core.Remote.Services;

[assembly: SupportedOSPlatform("browser")]

namespace HelloCrab.Browser;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        // WASM exceptions are written to the browser developer console.
        Trace.Listeners.Add(new ConsoleTraceListener());

        // Browser 使用 localStorage 记住远程主机地址、令牌和独立主题。
        RemoteClientPreferencesStoreProvider.Current =
            new BrowserRemoteClientPreferencesStore();

        // 优先使用浏览器标准的 WebGL 渲染；无法创建 WebGL 上下文时再回退到 Software2D。
        var browserOptions = new BrowserPlatformOptions
        {
            RenderingMode =
            [
                BrowserRenderingMode.WebGL2,
                BrowserRenderingMode.WebGL1,
                BrowserRenderingMode.Software2D
            ]
        };

        await BuildAvaloniaApp()
            // 必须直接调用字体包提供的扩展方法，而不是只写一个 avares URI。
            // 这样字体程序集会被 WASM 链接器保留，字体资源也会真正打进发布产物。
            // 思源黑体同时包含中文和拉丁字符，因此 Browser 端统一以它作为默认字体。
            .WithFont_SourceHanSansCN()
            .LogToTrace()
            .StartBrowserAppAsync("out", browserOptions);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<HelloCrab.Core.Remote.RemoteApp>();
}
