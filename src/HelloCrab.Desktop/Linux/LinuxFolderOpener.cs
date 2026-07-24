using System.Diagnostics;
using HelloCrab.Desktop.Platform;

namespace HelloCrab.Desktop.Linux;

internal sealed class LinuxFolderOpener : IPlatformFolderOpener
{
    public void OpenFolder(string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "xdg-open",
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(path);
        Process.Start(startInfo);
    }
}
