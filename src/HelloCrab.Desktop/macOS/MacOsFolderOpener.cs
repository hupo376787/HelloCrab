using System.Diagnostics;
using HelloCrab.Desktop.Platform;

namespace HelloCrab.Desktop.macOS;

internal sealed class MacOsFolderOpener : IPlatformFolderOpener
{
    public void OpenFolder(string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "open",
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(path);
        Process.Start(startInfo);
    }
}
