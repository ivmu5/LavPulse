using Microsoft.Maui.ApplicationModel;
using Laveyana.Audio.Maui.Tracks;
#if WINDOWS
using Laveyana.Audio.Maui.Controls;
#endif

namespace Laveyana.Audio.Maui;

public sealed class App : Application
{
    /// <summary>Creates a mixer window and binds its playback and import lifetime to the window.</summary>
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var page = new MainPage();
        var window = new Window(page) { Title = "LavPulse" };
        void OnThemeChanged(object? sender, AppThemeChangedEventArgs args) => page.RefreshAppearance();
        RequestedThemeChanged += OnThemeChanged;
        window.Destroying += (_, _) => RequestedThemeChanged -= OnThemeChanged;
        window.Created += (_, _) => page.RefreshAppearance();
#if WINDOWS
        WindowsWindowChrome.Configure(window, page.RefreshAppearance);
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
#elif IOS || MACCATALYST
        window.Activated += (_, _) => page.SetWindowVisible(true);
        window.Deactivated += (_, _) => page.SetWindowVisible(false);
        window.Stopped += (_, _) => page.SetWindowVisible(false);
#endif
        void OnFilesRequested() => window.Dispatcher.Dispatch(() =>
        {
#if WINDOWS
            if (window.Handler?.PlatformView is Microsoft.UI.Xaml.Window native)
            {
                if (native.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter &&
                    presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
                    presenter.Restore();
                native.Activate();
            }
#endif
            page.ProcessSystemTrackRequests();
        });

        SystemTrackRequests.Received += OnFilesRequested;
        window.Activated += (_, _) => page.ActivateSystemTrackRequests();
        window.Destroying += (_, _) => SystemTrackRequests.Received -= OnFilesRequested;
        window.Destroying += async (_, _) => await page.CloseAsync();
        return window;
    }
}
