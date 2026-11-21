namespace Laveyana.LavPulse.WinUI;

/// <summary>Loads the WinUI XAML resources for the Windows host.</summary>
public partial class App : MauiWinUIApplication
{
    internal const string PreviousExecutableName = "Laveyana.Audio.Maui.exe";

    public App()
    {
        // Register identity before the first window or media session is created.
        var executable = Environment.ProcessPath;
        if (executable is not null &&
            !Path.GetFileName(executable).Equals($"{typeof(App).Assembly.GetName().Name}.exe", StringComparison.OrdinalIgnoreCase))
            executable = null;

        WindowsMediaIdentity.Initialize(executable);
        if (executable is not null) WindowsFileAssociation.Initialize(executable);
        InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
