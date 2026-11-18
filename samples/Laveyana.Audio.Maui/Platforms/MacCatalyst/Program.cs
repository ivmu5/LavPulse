using ObjCRuntime;
using UIKit;

namespace Laveyana.Audio.Maui;

public class Program
{
	// This is the main entry point of the application.
	/// <summary>Enters the UIKit application loop with the platform delegate.</summary>
	static void Main(string[] args)
	{
		// if you want to use a different Application Delegate class from "AppDelegate"
		// you can specify it here.
		UIApplication.Main(args, null, typeof(AppDelegate));
	}
}
