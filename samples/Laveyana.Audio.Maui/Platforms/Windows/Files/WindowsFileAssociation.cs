using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Laveyana.Audio.Maui.WinUI;

/// <summary>Registers the unpackaged player as an available MP3 handler for the current Windows user.</summary>
internal static class WindowsFileAssociation
{
    private const string ProgId = "Laveyana.LavPulse.MP3";
    private const string Capabilities = @"Software\Laveyana\LavPulse\Capabilities";

    internal static void Initialize()
    {
        if (Environment.ProcessPath is not { } executable) return;
        string fileName = Path.GetFileName(executable);
        if (!fileName.Equals("Laveyana.Audio.Maui.exe", StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            string command = $"\"{executable}\" --open \"%1\"";
            using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
            using var type = classes.CreateSubKey(ProgId);
            type.SetValue(string.Empty, "MP3 Audio");
            WriteDefault(type, "DefaultIcon", $"\"{executable}\",0");
            WriteDefault(type, @"shell\open\command", command);
            using var openWith = classes.CreateSubKey(@".mp3\OpenWithProgids");
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);

            using var app = classes.CreateSubKey($@"Applications\{fileName}");
            app.SetValue("FriendlyAppName", "LavPulse");
            WriteDefault(app, @"shell\open\command", command);
            using var supported = app.CreateSubKey("SupportedTypes");
            supported.SetValue(".mp3", string.Empty);

            using var capabilities = Registry.CurrentUser.CreateSubKey(Capabilities);
            capabilities.SetValue("ApplicationName", "LavPulse");
            capabilities.SetValue("ApplicationDescription", "LavPulse music player");
            using var associations = capabilities.CreateSubKey("FileAssociations");
            associations.SetValue(".mp3", ProgId);
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
