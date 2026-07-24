using Android.App;
using Android.Content.PM;
using Avalonia.Android;

namespace HelloCrab.Android;

[Activity(
    Label = "HelloCrab Remote",
    Theme = "@style/AppTheme",
    MainLauncher = true,
    Exported = true,
    ConfigurationChanges = ConfigChanges.Orientation
                           | ConfigChanges.ScreenSize
                           | ConfigChanges.UiMode)]
public sealed class MainActivity : AvaloniaMainActivity
{
}
