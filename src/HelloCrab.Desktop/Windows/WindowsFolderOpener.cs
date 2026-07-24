using System.Diagnostics;
using HelloCrab.Desktop.Platform;

namespace HelloCrab.Desktop.Windows;

internal sealed class WindowsFolderOpener : IPlatformFolderOpener
{
    public void OpenFolder(string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add(path);
        Process.Start(startInfo);
    }
}
