using System.Runtime.InteropServices;
using NativeWindow = Microsoft.UI.Xaml.Window;

namespace Laveyana.Audio.Maui;

internal static class WindowsMouseNavigation
{
    private const uint XButtonUp = 0x020C;
    private const uint NonClientXButtonUp = 0x00AC;
    private const uint AppCommand = 0x0319;
    private const int Back = 1;
    private const int Forward = 2;
    private const nuint SubclassId = 1;

    public static void Attach(NativeWindow window, Func<bool> back, Func<bool> forward)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        WindowSubclass callback = OnMessage;
        if (hwnd != 0 && SetWindowSubclass(hwnd, callback, SubclassId, 0))
            window.Closed += (_, _) => RemoveWindowSubclass(hwnd, callback, SubclassId);

        nint OnMessage(nint handle, uint message, nuint wParam, nint lParam, nuint id, nuint data)
        {
            // Child windows can forward the side buttons as browser navigation commands.
            int direction = message switch
            {
                XButtonUp or NonClientXButtonUp => (int)((wParam >> 16) & 0xffff),
                AppCommand => (int)(((nuint)lParam >> 16) & 0x0fff),
                _ => 0
            };
            if (direction is Back or Forward)
            {
                var navigate = direction == Back ? back : forward;
                if (window.DispatcherQueue.TryEnqueue(() => navigate())) return 1;
            }
            return DefSubclassProc(handle, message, wParam, lParam);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowSubclass(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hwnd, WindowSubclass callback, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hwnd, WindowSubclass callback, nuint id);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
}
