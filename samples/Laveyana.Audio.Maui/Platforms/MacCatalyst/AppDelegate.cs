using Foundation;
using Laveyana.Audio.Maui.Tracks;
using UIKit;

namespace Laveyana.Audio.Maui;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    public override bool OpenUrl(UIApplication application, NSUrl url, NSDictionary options) =>
        PlatformTrackActivation.Open(url) || base.OpenUrl(application, url, options);

	/// <summary>Builds the MAUI host used by the Apple application.</summary>
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
