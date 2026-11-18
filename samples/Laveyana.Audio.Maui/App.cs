#if !WINDOWS
using Microsoft.Maui.ApplicationModel;
#endif

namespace Laveyana.Audio.Maui;

public sealed class App : Application
{
#if !WINDOWS
    /// <summary>Applies the LavPulse theme on platforms where the native host does not set it.</summary>
    public App() => UserAppTheme = AppTheme.Dark;
#endif

    /// <summary>Creates a mixer window and binds its playback and import lifetime to the window.</summary>
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var page = new MainPage();
        var window = new Window(page) { Title = "LavPulse" };
#if WINDOWS
        window.Width = 1120;
        window.Height = 790;
#endif
        window.Stopped += (_, _) => page.StopPlayback();
        window.Destroying += async (_, _) => await page.CloseAsync();
        return window;
    }
}
