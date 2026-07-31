namespace HelloCrab.Core.Services.Settings;

public static class LongFileNameState
{
    public const int StandardWorkBaseNameMaxLength = 170;
    public const int ExtendedWorkBaseNameMaxLength = 220;

    private static int _enabled;

    public static bool Enabled => Volatile.Read(ref _enabled) != 0;

    public static int CurrentWorkBaseNameMaxLength
        => Enabled
            ? ExtendedWorkBaseNameMaxLength
            : StandardWorkBaseNameMaxLength;

    public static void Set(bool enabled)
        => Volatile.Write(ref _enabled, enabled ? 1 : 0);
}
