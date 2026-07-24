using System.Runtime.InteropServices.JavaScript;
using HelloCrab.Core.Remote.Services;

namespace HelloCrab.Browser;

/// <summary>
/// Browser 端使用 localStorage 保存远程主机地址、访问令牌和主题。
/// JavaScript 函数由 wwwroot/remote-storage.js 在 main.js 启动 .NET 之前注册到 globalThis，
/// 因而不再依赖 JSHost.ImportAsync 的相对模块路径解析。
/// </summary>
internal sealed class BrowserRemoteClientPreferencesStore : IRemoteClientPreferencesStore
{
    private const string ServerKey = "HelloCrab.Remote.ServerAddress";
    private const string TokenKey = "HelloCrab.Remote.AccessToken";
    private const string ThemeKey = "HelloCrab.Remote.Theme";

    public RemoteClientPreferences Load()
    {
        try
        {
            return new RemoteClientPreferences
            {
                ServerAddress = BrowserStorageInterop.GetItem(ServerKey) ?? string.Empty,
                AccessToken = BrowserStorageInterop.GetItem(TokenKey) ?? string.Empty,
                IsDarkTheme = !string.Equals(
                    BrowserStorageInterop.GetItem(ThemeKey),
                    "Light",
                    StringComparison.OrdinalIgnoreCase)
            };
        }
        catch (Exception exception)
        {
            // localStorage 被浏览器策略禁用或脚本未能加载时，仍允许远程控制端启动。
            Console.Error.WriteLine($"Unable to load browser preferences: {exception}");
            return new RemoteClientPreferences();
        }
    }

    public void Save(RemoteClientPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        try
        {
            BrowserStorageInterop.SetItem(
                ServerKey,
                preferences.ServerAddress ?? string.Empty);
            BrowserStorageInterop.SetItem(
                TokenKey,
                preferences.AccessToken ?? string.Empty);
            BrowserStorageInterop.SetItem(
                ThemeKey,
                preferences.IsDarkTheme ? "Dark" : "Light");
        }
        catch (Exception exception)
        {
            // 私密浏览或受限浏览器可能禁止持久化。保存失败不应中断控制功能。
            Console.Error.WriteLine($"Unable to save browser preferences: {exception}");
        }
    }
}

internal static partial class BrowserStorageInterop
{
    [JSImport("globalThis.helloCrabRemoteStorageGetItem")]
    internal static partial string? GetItem(string key);

    [JSImport("globalThis.helloCrabRemoteStorageSetItem")]
    internal static partial void SetItem(string key, string value);
}
