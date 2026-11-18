using System.Diagnostics;

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
                .Build();
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception.ToString(), "LavPulse startup");
            throw;
        }
    }
}
