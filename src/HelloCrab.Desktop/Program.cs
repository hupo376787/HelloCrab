using Avalonia;
using Avalonia.Media;
using System;

namespace HelloCrab.Desktop;

internal static class Program
{
    private static EarlyStartupSplash? _earlyStartupSplash;

    [STAThread]
    public static void Main(string[] args)
    {
        // Avalonia 自身初始化之前先显示一个极轻量的 Win32 启动占位窗口。
        // 这样 Windows 冷启动时，不会出现双击 exe 后数秒完全没有反馈的情况。
        _earlyStartupSplash = EarlyStartupSplash.TryStart();
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            HideEarlyStartupSplash();
        }
    }

    internal static bool HasEarlyStartupSplash => _earlyStartupSplash is not null;

    internal static DateTimeOffset? EarlyStartupSplashShownAt
        => _earlyStartupSplash?.ShownAt;

    internal static void UpdateEarlyStartupSplash(
        double percent,
        string status,
        string? detail = null)
        => _earlyStartupSplash?.SetProgress(percent, status, detail);

    internal static void HideEarlyStartupSplash()
        => Interlocked.Exchange(ref _earlyStartupSplash, null)?.Dispose();

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            // Register Emoji as a real Unicode-range fallback. A comma-separated FontFamily on a
            // TextBlock is not equivalent to FontManager fallback and can leave supplementary-plane
            // characters as missing-glyph boxes when the primary CJK font is selected.
            .With(CreateFontManagerOptions())
            .UsePlatformDetect()
            .LogToTrace();

    private static FontManagerOptions CreateFontManagerOptions()
    {
        var emojiFamily = OperatingSystem.IsWindows()
            ? "Segoe UI Emoji"
            : OperatingSystem.IsMacOS()
                ? "Apple Color Emoji"
                : "Noto Color Emoji";

        return new FontManagerOptions
        {
            FontFallbacks =
            [
                new FontFallback
                {
                    FontFamily = new FontFamily(emojiFamily),
                    // Miscellaneous Symbols, Dingbats and all modern pictographic blocks.
                    UnicodeRange = UnicodeRange.Parse("200D,20E3,2600-27BF,FE0E-FE0F,1F000-1FAFF")
                }
            ]
        };
    }
}
