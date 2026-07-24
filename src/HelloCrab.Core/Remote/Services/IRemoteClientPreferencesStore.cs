namespace HelloCrab.Core.Remote.Services;

/// <summary>
/// Stores settings that belong to the remote controller itself rather than to the desktop crawler.
/// </summary>
public interface IRemoteClientPreferencesStore
{
    RemoteClientPreferences Load();

    void Save(RemoteClientPreferences preferences);
}

public sealed class RemoteClientPreferences
{
    public string ServerAddress { get; init; } = string.Empty;

    public string AccessToken { get; init; } = string.Empty;

    public bool IsDarkTheme { get; init; } = true;
}
