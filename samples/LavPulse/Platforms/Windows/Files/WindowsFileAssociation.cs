using Laveyana.Audio.Playback;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Laveyana.LavPulse.WinUI;

/// <summary>Registers the unpackaged player as an available MP3 handler for the current Windows user.</summary>
internal static class WindowsFileAssociation
{
    private const string ProgId = "Laveyana.LavPulse.MP3";
    private const string Capabilities = @"Software\Laveyana\LavPulse\Capabilities";

    internal static void Initialize(string executable)
    {
        string fileName = Path.GetFileName(executable);

        try
        {
            string command = $"\"{executable}\" --open \"%1\"";
            using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
            using var type = classes.CreateSubKey(ProgId);
            type.SetValue(string.Empty, "LavPulse Audio");
            WriteDefault(type, "DefaultIcon", $"\"{executable}\",0");
            WriteDefault(type, @"shell\open\command", command);
            foreach (string extension in TrackFile.SupportedExtensions)
            {
                using var openWith = classes.CreateSubKey(extension + @"\OpenWithProgids");
                openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            }

            using var app = classes.CreateSubKey($@"Applications\{fileName}");
            app.SetValue("FriendlyAppName", "LavPulse");
            WriteDefault(app, @"shell\open\command", command);
            using var supported = app.CreateSubKey("SupportedTypes");
            foreach (string extension in TrackFile.SupportedExtensions)
                supported.SetValue(extension, string.Empty);

            // Keep existing Open With selections working through the executable rename.
            using var previous = classes.OpenSubKey($@"Applications\{App.PreviousExecutableName}", writable: true);
            if (previous?.GetValue("FriendlyAppName") is "LavPulse")
                WriteDefault(previous, @"shell\open\command", command);

            using var capabilities = Registry.CurrentUser.CreateSubKey(Capabilities);
            capabilities.SetValue("ApplicationName", "LavPulse");
            capabilities.SetValue("ApplicationDescription", "LavPulse music player");
            using var associations = capabilities.CreateSubKey("FileAssociations");
            foreach (string extension in TrackFile.SupportedExtensions)
                associations.SetValue(extension, ProgId);
            using var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications");
            registered.SetValue("LavPulse", Capabilities);
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"LavPulse file associations: {ex}");
        }
    }

    private static void WriteDefault(RegistryKey parent, string path, string value)
    {
        using var key = parent.CreateSubKey(path);
        key.SetValue(string.Empty, value);
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
