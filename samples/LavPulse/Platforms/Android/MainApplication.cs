using Android.App;
using Android.Runtime;

namespace Laveyana.LavPulse;

[Application]
public class MainApplication : MauiApplication
{
    public MainApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
    }

    /// <summary>Builds the MAUI host used by the Android application.</summary>
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
