namespace HelloCrab.Core.Services.Platform;

public interface IPlatformShellService
{
    void OpenFolder(string path);
    void OpenUrl(string url);
}
