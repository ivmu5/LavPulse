using System.Runtime.InteropServices;
using Laveyana.Audio.Maui.Tracks;
using Laveyana.Audio.Playback;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using Windows.Storage;

namespace Laveyana.Audio.Maui.WinUI;

/// <summary>Routes Explorer file activations to the existing player before creating a second window.</summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var player = AppInstance.FindOrRegisterForKey("Laveyana.LavPulse.Player");
        if (!player.IsCurrent)
        {
            AllowSetForegroundWindow(player.ProcessId);
            // Redirect off the startup STA while Windows App SDK delivers the activation.
            Task.Run(async () => await player.RedirectActivationToAsync(activation)).GetAwaiter().GetResult();
            return;
        }

        player.Activated += (_, incoming) => SystemTrackRequests.Open(ReadFiles(incoming));
        var initial = ReadFiles(activation);
        SystemTrackRequests.Open(initial.Length > 0 ? initial : ReadPaths(args));

        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            var dispatcher = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(dispatcher);
            new App();
        });
    }

    private static TrackFile[] ReadFiles(AppActivationArguments activation) => activation.Data switch
    {
        IFileActivatedEventArgs files => ReadPaths(files.Files.OfType<StorageFile>().Select(file => file.Path)),
        ILaunchActivatedEventArgs launch => ReadPaths(ParseCommandLine(launch.Arguments)),
        ICommandLineActivatedEventArgs command => ReadPaths(ParseCommandLine(command.Operation.Arguments)),
        _ => []
    };

    private static TrackFile[] ReadPaths(IEnumerable<string> paths) => paths
        .Where(path => Path.IsPathFullyQualified(path) && TrackFile.IsMp3(path))
        .Select(TrackFile.FromPath)
        .ToArray();

    private static string[] ParseCommandLine(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return [];
        // Give the parser a program name even when the activation carries only arguments.
        IntPtr argv = CommandLineToArgvW($"LavPulse {arguments}", out int count);
        if (argv == IntPtr.Zero) return [];
        try
        {
            var result = new string[count - 1];
            for (int i = 1; i < count; i++)
                result[i - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? string.Empty;
            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
