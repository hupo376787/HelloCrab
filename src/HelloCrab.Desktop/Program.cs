using Avalonia;
using Avalonia.Media;
using System;

namespace HelloCrab.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
        => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

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
