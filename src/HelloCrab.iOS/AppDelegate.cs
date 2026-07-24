using Avalonia;
using Avalonia.iOS;
using Foundation;

namespace HelloCrab.iOS;

[Register("AppDelegate")]
public sealed class AppDelegate : AvaloniaAppDelegate<HelloCrab.Core.Remote.RemoteApp>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        // Use iOS system fonts and the platform fallback chain.
        => base.CustomizeAppBuilder(builder);
}
