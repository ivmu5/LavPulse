namespace Laveyana.Audio.Maui.WinUI;

/// <summary>Lets the WinUI compiler generate the entry point and XAML metadata for the Windows host.</summary>
public partial class App : MauiWinUIApplication
{
    public App()
    {
        // Register identity before the first window or media session is created.
        WindowsMediaIdentity.Initialize();
        InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
