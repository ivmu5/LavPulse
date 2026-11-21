using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Laveyana.LavPulse.WinUI;

/// <summary>
/// Gives the unpackaged Windows app a stable identity and matching per-user Start Menu shortcut.
/// The shortcut is needed for Windows to resolve LavPulse instead of "Unknown application" in SMTC.
/// </summary>
internal static class WindowsMediaIdentity
{
    private const string AppUserModelId = "Laveyana.LavPulse.Mixer";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appId);

    /// <summary>Runs once, before WinUI creates an application window.</summary>
    internal static void Initialize(string? executable)
    {
        try
        {
            Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(AppUserModelId));
            if (executable is not null) EnsureUserShortcut(executable);
        }
        catch (Exception error)
        {
            // An unavailable shell shortcut must never stop music playback or app startup.
            Debug.WriteLine($"LavPulse media identity: {error}");
        }
    }

    /// <summary>Creates only LavPulse's own per-user shortcut; never modifies other shortcuts.</summary>
    private static void EnsureUserShortcut(string executable)
    {
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        if (string.IsNullOrWhiteSpace(programs)) return;
        string directory = Path.Combine(programs, "Laveyana");
        string target = Path.Combine(directory, "LavPulse.lnk");
        object instance = new ShellLink();
        try
        {
            var shortcut = (IShellLinkW)instance;
            var file = (IPersistFile)instance;
            if (File.Exists(target))
            {
                file.Load(target, 0);
                var previousPath = new StringBuilder(260);
                const uint rawPath = 4;
                shortcut.GetPath(previousPath, previousPath.Capacity, IntPtr.Zero, rawPath);
                if (!Path.GetFileName(previousPath.ToString()).Equals(App.PreviousExecutableName, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            else
            {
                Directory.CreateDirectory(directory);
            }

            shortcut.SetPath(executable);
            shortcut.SetWorkingDirectory(Path.GetDirectoryName(executable)!);
            shortcut.SetDescription("LavPulse — музыкальный проигрыватель");
            shortcut.SetIconLocation(executable, 0);

            var key = new PropertyKey(
                new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
            var value = new PropVariant
            {
                VariantType = 31, // VT_LPWSTR
                Value = Marshal.StringToCoTaskMemUni(AppUserModelId)
            };
            try
            {
                var properties = (IPropertyStore)instance;
                properties.SetValue(ref key, ref value);
                properties.Commit();
            }
            finally
            {
                PropVariantClear(ref value);
            }

            file.Save(target, true);
        }
        finally
        {
            Marshal.ReleaseComObject(instance);
        }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(ref PropVariant variant);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
        public PropertyKey(Guid formatId, uint propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public IntPtr Value;
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink { }

    // The complete IShellLinkW vtable must be kept in its native order.
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path,
            int maxPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int maxLength);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maxLength);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int maxLength);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out ushort hotkey);
        void SetHotkey(ushort hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath,
            int maxLength, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }
}
