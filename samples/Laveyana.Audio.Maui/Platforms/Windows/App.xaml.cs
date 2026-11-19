namespace Laveyana.Audio.Maui.WinUI;

/// <summary>Loads the WinUI XAML resources for the Windows host.</summary>
public partial class App : MauiWinUIApplication
{
    public App()
    {
        // Register identity before the first window or media session is created.
        WindowsMediaIdentity.Initialize();
        WindowsFileAssociation.Initialize();
        InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
