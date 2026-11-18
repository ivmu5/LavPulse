using System.Runtime.InteropServices;

namespace Laveyana.Audio.Maui.Controls;

/// <summary>Removes the non-client frame while retaining native window movement and resizing.</summary>
internal sealed class WindowsClientFrame : IDisposable
{
    private const uint WindowNonClientCalculateSize = 0x0083;
    private const uint WindowHitTest = 0x0084;
    private const uint WindowNonClientActivate = 0x0086;
    private const uint NoSize = 0x0001;
    private const uint NoMove = 0x0002;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;
    private const uint FrameChanged = 0x0020;
    private const uint NearestMonitor = 2;
    private const uint DwmBorderColor = 34;
    private const uint DwmColorNone = 0xFFFFFFFE;
    private const uint DwmNonClientRenderingPolicy = 2;
    private const uint DwmNonClientRenderingDisabled = 1;
    private const int ResizeEdge = 6;

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

    /// <summary>Clears the compositor border and forces a client-size recalculation without moving the window.</summary>
    public void Refresh()
    {
        if (!_installed || IsFullscreen) return;
        var margins = new FrameMargins();
        DwmExtendFrameIntoClientArea(_hwnd, in margins);
        uint border = DwmColorNone;
        if (DwmSetWindowAttribute(_hwnd, DwmBorderColor, in border, sizeof(uint)) < 0)
        {
            // Older Windows versions lack a border-color attribute.
            uint policy = DwmNonClientRenderingDisabled;
            DwmSetWindowAttribute(_hwnd, DwmNonClientRenderingPolicy, in policy, sizeof(uint));
        }
        SetWindowPos(_hwnd, 0, 0, 0, 0, 0, NoSize | NoMove | NoZOrder | NoActivate | FrameChanged);
    }

    private nint HandleMessage(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (!IsFullscreen)
        {
            switch (message)
            {
                case WindowNonClientCalculateSize when wParam != 0 && lParam != 0 && !IsIconic(hwnd):
                    // NCCALCSIZE_PARAMS starts with the proposed window rectangle. Give it all to the client.
                    if (IsZoomed(hwnd)) FitMaximizedClient(lParam);
                    return 0;
                case WindowNonClientActivate when !IsIconic(hwnd):
                    return DefSubclassProc(hwnd, message, wParam, -1);
                case WindowHitTest when !IsIconic(hwnd):
                    if (HitTest(hwnd, lParam) is { } hit) return (nint)hit;
                    break;
            }
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private static void FitMaximizedClient(nint rectangle)
    {
        var proposed = Marshal.PtrToStructure<NativeRectangle>(rectangle);
        var monitor = MonitorFromRect(in proposed, NearestMonitor);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) return;

        // Maximized windows extend their invisible resize edges beyond the work area.
        var client = Intersect(proposed, info.WorkArea);
        if (client.Right > client.Left && client.Bottom > client.Top)
        {
            Marshal.StructureToPtr(client, rectangle, false);
        }
    }

    private WindowHit? HitTest(nint hwnd, nint lParam)
    {
        long coordinates = lParam.ToInt64();
        var point = new NativePoint { X = unchecked((short)coordinates), Y = unchecked((short)(coordinates >> 16)) };
        if (!ScreenToClient(hwnd, ref point) || !GetClientRect(hwnd, out var client)) return null;
        uint dpi = GetDpiForWindow(hwnd);
        double scale = dpi == 0 ? 1 : dpi / 96d;
        return ClassifyHit(point.X / scale, point.Y / scale, client.Right / scale, client.Bottom / scale,
            _captionHeight, _toolbarWidth, !IsZoomed(hwnd));
    }

    internal static WindowHit? ClassifyHit(double x, double y, double width, double height,
        double captionHeight, double toolbarWidth, bool resizable)
    {
        if (x < 0 || x >= width || y < 0 || y >= height) return null;
        if (resizable)
        {
            bool left = x < ResizeEdge;
            bool right = x >= width - ResizeEdge;
            if (y < ResizeEdge) return left ? WindowHit.TopLeft : right ? WindowHit.TopRight : WindowHit.Top;
            if (y >= height - ResizeEdge) return left ? WindowHit.BottomLeft : right ? WindowHit.BottomRight : WindowHit.Bottom;
            if (left) return WindowHit.Left;
            if (right) return WindowHit.Right;
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
        Client = 1, Caption = 2, Left = 10, Right = 11, Top = 12, TopLeft = 13, TopRight = 14,
        Bottom = 15, BottomLeft = 16, BottomRight = 17
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
