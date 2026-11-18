using Foundation;

namespace Laveyana.Audio.Maui;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	/// <summary>Builds the MAUI host used by the Apple application.</summary>
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
