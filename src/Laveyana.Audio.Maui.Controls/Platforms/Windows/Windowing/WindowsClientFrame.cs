using System.Runtime.InteropServices;

namespace Laveyana.Audio.Maui.Controls;

/// <summary>Provides the custom caption frame on systems that lack AppWindow title bar customization.</summary>
internal sealed class WindowsClientFrame : IDisposable
{
    private const uint WindowNonClientCalculateSize = 0x0083;
    private const uint WindowHitTest = 0x0084;
    private const uint NoSize = 0x0001;
    private const uint NoMove = 0x0002;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;
    private const uint FrameChanged = 0x0020;
    private const uint NearestMonitor = 2;
    private const uint DwmBorderColor = 34;
    private const int TopBorderPixels = 1;
    private const int TopResizeHeight = 6;
    private const int TopCornerWidth = 10;

    private readonly nint _hwnd;
    private readonly int _captionHeight;
    private readonly int _toolbarWidth;
    private readonly WindowSubclass _subclass;
    private bool _installed;

    public WindowsClientFrame(nint hwnd, int captionHeight, int toolbarWidth)
    {
        _hwnd = hwnd;
        _captionHeight = captionHeight;
        _toolbarWidth = toolbarWidth;
        _subclass = HandleMessage;
        _installed = hwnd != 0 && SetWindowSubclass(hwnd, _subclass, 1, 0);
    }

    public bool IsFullscreen { get; set; }

    /// <summary>Sets the border color and forces a client-size recalculation without moving the window.</summary>
    public void Refresh(Color color)
    {
        if (!_installed || IsFullscreen) return;
        var margins = new FrameMargins();
        DwmExtendFrameIntoClientArea(_hwnd, in margins);
        ApplyBorderColor(_hwnd, color);
        SetWindowPos(_hwnd, 0, 0, 0, 0, 0, NoSize | NoMove | NoZOrder | NoActivate | FrameChanged);
    }

    internal static void ApplyBorderColor(nint hwnd, Color color)
    {
        color.ToRgb(out byte red, out byte green, out byte blue);
        uint border = (uint)(red | green << 8 | blue << 16);
        DwmSetWindowAttribute(hwnd, DwmBorderColor, in border, sizeof(uint));
    }

    private nint HandleMessage(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (!IsFullscreen)
        {
            switch (message)
            {
                case WindowNonClientCalculateSize when lParam != 0 && !IsIconic(hwnd):
                    return CalculateClientRect(hwnd, wParam, lParam);
                case WindowHitTest when !IsIconic(hwnd):
                    nint nativeHit = DefWindowProc(hwnd, message, wParam, lParam);
                    if (nativeHit != (nint)WindowHit.Client && nativeHit != (nint)WindowHit.Caption)
                    {
                        return nativeHit; // Windows owns the resize edges and corners outside the client.
                    }
                    return HitTest(hwnd, lParam) is { } hit ? (nint)hit : nativeHit;
            }
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    /// <summary>Retains the system's side and bottom borders and replaces the top caption inset.</summary>
    private static nint CalculateClientRect(nint hwnd, nuint wParam, nint rectangle)
    {
        // Both WM_NCCALCSIZE forms place the proposed RECT first.
        var proposed = Marshal.PtrToStructure<NativeRectangle>(rectangle);
        if (IsZoomed(hwnd))
        {
            var maximized = FitMaximizedClient(proposed);
            Marshal.StructureToPtr(maximized, rectangle, false);
            return 0;
        }

        DefWindowProc(hwnd, WindowNonClientCalculateSize, wParam, rectangle);
        var native = Marshal.PtrToStructure<NativeRectangle>(rectangle);
        var client = KeepThinTopBorder(proposed, native);
        Marshal.StructureToPtr(client, rectangle, false);
        return 0;
    }

    internal static NativeRectangle KeepThinTopBorder(NativeRectangle window, NativeRectangle native)
    {
        native.Top = Math.Min(window.Top + TopBorderPixels, native.Bottom);
        return native;
    }

    private static NativeRectangle FitMaximizedClient(NativeRectangle proposed)
    {
        var monitor = MonitorFromRect(in proposed, NearestMonitor);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) return proposed;

        // Maximized windows extend their invisible resize edges beyond the work area.
        var client = Intersect(proposed, info.WorkArea);
        return client.Right > client.Left && client.Bottom > client.Top ? client : proposed;
    }

    private WindowHit? HitTest(nint hwnd, nint lParam)
    {
        long coordinates = lParam.ToInt64();
        var point = new NativePoint { X = unchecked((short)coordinates), Y = unchecked((short)(coordinates >> 16)) };
        if (!ScreenToClient(hwnd, ref point) || !GetClientRect(hwnd, out var client)) return null;
        uint dpi = GetDpiForWindow(hwnd);
        double scale = dpi == 0 ? 1 : dpi / 96d;
        return ClassifyHit(point.X / scale, point.Y / scale, client.Right / scale, client.Bottom / scale,
            _captionHeight, _toolbarWidth, IsZoomed(hwnd) ? 0 : TopResizeHeight - TopBorderPixels / scale);
    }

    internal static WindowHit? ClassifyHit(double x, double y, double width, double height,
        double captionHeight, double toolbarWidth, double topResizeHeight)
    {
        if (x < 0 || x >= width || y < 0 || y >= height) return null;
        // Extend the grab area into the caption without drawing a thicker top border.
        if (y < topResizeHeight)
        {
            if (x < TopCornerWidth) return WindowHit.TopLeft;
            if (x >= width - TopCornerWidth) return WindowHit.TopRight;
            return WindowHit.Top;
        }
        return y < captionHeight && x < width - toolbarWidth ? WindowHit.Caption : WindowHit.Client;
    }

    internal static NativeRectangle Intersect(NativeRectangle window, NativeRectangle work) => new()
    {
        Left = Math.Max(window.Left, work.Left),
        Top = Math.Max(window.Top, work.Top),
        Right = Math.Min(window.Right, work.Right),
        Bottom = Math.Min(window.Bottom, work.Bottom)
    };

    public void Dispose()
    {
        if (!_installed) return;
        RemoveWindowSubclass(_hwnd, _subclass, 1);
        _installed = false;
    }

    internal enum WindowHit
    {
        Client = 1, Caption = 2, Top = 12, TopLeft = 13, TopRight = 14
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRectangle Monitor;
        public NativeRectangle WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameMargins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
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

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW", ExactSpelling = true)]
    private static extern nint DefWindowProc(nint hwnd, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(nint hwnd, ref NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint hwnd, out NativeRectangle rectangle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromRect(in NativeRectangle rectangle, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, in FrameMargins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, uint attribute, in uint value, uint valueSize);
}
