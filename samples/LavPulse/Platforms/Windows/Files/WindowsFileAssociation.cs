using Laveyana.Audio.Playback;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Laveyana.LavPulse.WinUI;

/// <summary>Registers the unpackaged player as an available audio handler for the current Windows user.</summary>
internal static class WindowsFileAssociation
{
    // Keep the original ProgID so existing user defaults survive upgrades.
    private const string ProgId = "Laveyana.LavPulse.MP3";
    private const string RegisteredName = "LavPulse";
    private const string Capabilities = @"Software\Laveyana\LavPulse\Capabilities";

    internal static bool Initialize(string executable)
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
            capabilities.SetValue("ApplicationIcon", $"\"{executable}\",0");
            using var associations = capabilities.CreateSubKey("FileAssociations");
            foreach (string extension in TrackFile.SupportedExtensions)
                associations.SetValue(extension, ProgId);
            using var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications");
            registered.SetValue(RegisteredName, Capabilities);
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"LavPulse file associations: {ex}");
            return false;
        }
    }

    /// <summary>Refreshes the registration and opens the OS-owned consent UI.
    /// Never writes UserChoice or changes the current defaults.</summary>
    internal static async Task<string?> OpenDefaultAppsAsync()
    {
        string? executable = Environment.ProcessPath;
        if (executable is null ||
            !Path.GetFileName(executable).Equals(
                $"{typeof(App).Assembly.GetName().Name}.exe", StringComparison.OrdinalIgnoreCase))
            return "Запусти LavPulse из его EXE-файла для регистрации в Windows.";
        if (!Initialize(executable))
            return "Не удалось зарегистрировать LavPulse в Windows.";

        const string page = "ms-settings:defaultapps";
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            try
            {
                if (await global::Windows.System.Launcher.LaunchUriAsync(
                    new Uri(page + "?registeredAppUser=" + Uri.EscapeDataString(RegisteredName))))
                    return null;
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }
        return await global::Windows.System.Launcher.LaunchUriAsync(new Uri(page))
            ? null : "Не удалось открыть параметры приложений по умолчанию.";
    }

    private static void WriteDefault(RegistryKey parent, string path, string value)
    {
        using var key = parent.CreateSubKey(path);
        key.SetValue(string.Empty, value);
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
