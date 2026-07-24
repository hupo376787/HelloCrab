using System.Diagnostics;
using HelloCrab.Core.Services.Platform;
using HelloCrab.Desktop.Linux;
using HelloCrab.Desktop.macOS;
using HelloCrab.Desktop.Windows;

namespace HelloCrab.Desktop.Platform;

public sealed class PlatformShellService : IPlatformShellService
{
    private readonly IPlatformFolderOpener _folderOpener = CreateFolderOpener();

    public void OpenFolder(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(path);
        _folderOpener.OpenFolder(path);
    }


    public void OpenUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("只能打开 HTTP 或 HTTPS 地址。", nameof(url));
        }

        Process.Start(new ProcessStartInfo(uri.ToString())
        {
            UseShellExecute = true
        });
    }

    private static IPlatformFolderOpener CreateFolderOpener()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsFolderOpener();
        if (OperatingSystem.IsMacOS())
            return new MacOsFolderOpener();
        if (OperatingSystem.IsLinux())
            return new LinuxFolderOpener();

        throw new PlatformNotSupportedException("当前桌面系统不受支持。");
    }
}
