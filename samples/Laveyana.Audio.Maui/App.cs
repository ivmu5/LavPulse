#if !WINDOWS
using Microsoft.Maui.ApplicationModel;
#else
using Laveyana.Audio.Maui.Controls;
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
        WindowsWindowChrome.Configure(window);
        window.Created += (_, _) =>
        {
            if (window.Handler?.PlatformView is Microsoft.UI.Xaml.Window native)
            {
                native.VisibilityChanged += OnVisibilityChanged;
                window.Destroying += (_, _) => native.VisibilityChanged -= OnVisibilityChanged;
            }
        };

        void OnVisibilityChanged(object sender, Microsoft.UI.Xaml.WindowVisibilityChangedEventArgs args) =>
            page.SetWindowVisible(args.Visible);
#elif ANDROID
        window.Created += (_, _) =>
        {
            if (window.Handler?.PlatformView is MainActivity activity)
            {
                activity.PlayerVisibilityChanged += page.SetWindowVisible;
                activity.SetPlayerOnLockScreen(page.ShowOnLockScreen);
                page.SetWindowVisible(activity.IsPlayerVisible);
                window.Destroying += (_, _) => activity.PlayerVisibilityChanged -= page.SetWindowVisible;
            }
        };
#endif
        window.Destroying += async (_, _) => await page.CloseAsync();
        return window;
    }
}
