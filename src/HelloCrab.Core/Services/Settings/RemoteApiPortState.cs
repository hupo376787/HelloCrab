namespace HelloCrab.Core.Services.Settings;

/// <summary>
/// 保存当前进程实际使用的远程 API 端口。
/// SettingsService 负责从 settings.json 初始化，并在每次保存设置时写回。
/// </summary>
public static class RemoteApiPortState
{
    private static int _current = 5088;

    public static int Current => Volatile.Read(ref _current);

    public static int Normalize(int value)
        => Math.Clamp(value, 1024, 65535);

    public static void Set(int value)
        => Volatile.Write(ref _current, Normalize(value));
}
