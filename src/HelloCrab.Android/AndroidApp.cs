using Android.App;
using Android.Runtime;
using Avalonia.Android;
using HelloCrab.Core.Remote;

namespace HelloCrab.Android;

[Application]
public sealed class AndroidApp : AvaloniaAndroidApplication<RemoteApp>
{
    protected AndroidApp(IntPtr javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }
}
