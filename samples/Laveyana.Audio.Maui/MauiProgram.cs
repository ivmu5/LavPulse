using System.Diagnostics;
#if WINDOWS
using Laveyana.Audio.Maui.Controls;
using Microsoft.Maui.LifecycleEvents;
#endif

namespace Laveyana.Audio.Maui;

public static class MauiProgram
{
    /// <summary>Registers the application and its font, and traces the full exception if startup fails.</summary>
    public static MauiApp CreateMauiApp()
    {
        try
        {
            return MauiApp.CreateBuilder()
                .UseMauiApp<App>()
                .ConfigureFonts(fonts => fonts.AddFont("InterVariable.ttf", "Inter"))
#if WINDOWS
                .ConfigureLifecycleEvents(events => events.AddWindows(windows =>
                    windows.OnPlatformWindowSubclassed((window, _) => WindowsWindowChrome.PrepareNativeCaption(window))))
#endif
                .Build();
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception.ToString(), "LavPulse startup");
            throw;
        }
    }
}
