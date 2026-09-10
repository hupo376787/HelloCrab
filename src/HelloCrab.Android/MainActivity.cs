using Android.App;
using Android.Content.PM;
using Android.OS;
using Avalonia.Android;
using HelloCrab.Core.Remote.Views;

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
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        BackRequested += MainActivity_BackRequested;
    }

    private void MainActivity_BackRequested(object? sender, AndroidBackRequestedEventArgs e)
    {
        if (Content is RemoteMainView view && view.TryHandleSystemBack())
            e.Handled = true;
    }

    protected override void OnDestroy()
    {
        BackRequested -= MainActivity_BackRequested;
        base.OnDestroy();
    }
}
