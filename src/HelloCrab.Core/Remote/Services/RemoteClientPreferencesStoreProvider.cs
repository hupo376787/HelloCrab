namespace HelloCrab.Core.Remote.Services;

public static class RemoteClientPreferencesStoreProvider
{
    private static IRemoteClientPreferencesStore _current = new FileRemoteClientPreferencesStore();

    public static IRemoteClientPreferencesStore Current
    {
        get => _current;
        set => _current = value ?? throw new ArgumentNullException(nameof(value));
    }
}
