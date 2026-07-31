using System.Runtime.InteropServices;

namespace ColorPicker.Services;

/// <summary>
/// All Win32 interop used by the app lives here so the rest of the code
/// stays free of raw P/Invoke declarations.
///
/// Covered APIs:
///   user32.dll  RegisterHotKey / UnregisterHotKey  — system-wide hotkeys
///   user32.dll  SetLayeredWindowAttributes         — invisible-but-clickable picking overlay
///   user32.dll  GetSystemMetrics                   — virtual-screen bounds (physical px)
///   user32.dll  GetWindowLong / SetWindowLong       — WS_EX_TRANSPARENT (click-through widget)
///   user32.dll  GetDC / ReleaseDC                  — screen device context
///   gdi32.dll   GetPixel                           — pixel colour sampling
///   user32.dll  GetCursorPos                       — cursor position
///   user32.dll  SetWindowPos                       — DPI-exact window placement
///   user32.dll  MonitorFromPoint / GetMonitorInfo  — monitor work-area clamping
///
/// Note: there is deliberately no SetWindowsHookEx here. Global hooks are what
/// endpoint-protection heuristics look for (they resemble keyloggers); the app
/// sees input only through the transient picking overlay window instead.
/// </summary>
public static class Win32Api
{
    // =====================================================================
    //  Constants
    // =====================================================================

    /// <summary>Window message posted when a registered hotkey is pressed.</summary>
    public const int WmHotkey = 0x0312;

    // Mouse messages delivered to the picking overlay window.
    public const int WmMouseMove = 0x0200;
    public const int WmLButtonDown = 0x0201;
    public const int WmRButtonDown = 0x0204;

    // =====================================================================
    //  Window styles / layered-window flags
    // =====================================================================

    /// <summary>SetWindowLong index for the extended window style.</summary>
    public const int GwlExStyle = -20;

    /// <summary>WS_EX_TRANSPARENT: window is transparent to hit-testing (clicks pass through).</summary>
    public const int WsExTransparent = 0x00000020;

    /// <summary>WS_EX_LAYERED: window supports per-window alpha (required for SetLayeredWindowAttributes).</summary>
    public const int WsExLayered = 0x00080000;

    /// <summary>WS_EX_TOOLWINDOW: no taskbar button, no Alt+Tab entry.</summary>
    public const int WsExToolWindow = 0x00000080;

    /// <summary>WS_EX_TOPMOST: window stays above non-topmost windows.</summary>
    public const int WsExTopmost = 0x00000008;

    /// <summary>WS_EX_NOACTIVATE: window never takes the keyboard focus (no focus stealing).</summary>
    public const int WsExNoActivate = 0x08000000;

    /// <summary>SetLayeredWindowAttributes flag: interpret the alpha parameter.</summary>
    public const uint LwaAlpha = 0x00000002;

    // GetSystemMetrics indices for the virtual screen (bounding box of all monitors).
    // A PerMonitorV2-aware process gets unscaled physical pixels for these.
    public const int SmXVirtualScreen = 76;
    public const int SmYVirtualScreen = 77;
    public const int SmCXVirtualScreen = 78;
    public const int SmCYVirtualScreen = 79;

    /// <summary>Return value of GetPixel when sampling fails (CLR_INVALID).</summary>
    public const uint ClrInvalid = 0xFFFFFFFF;

    /// <summary>SetWindowPos: don't resize (we only move the widget).</summary>
    public const uint SwpNoSize = 0x0001;

    /// <summary>SetWindowPos: don't activate the window (no focus stealing).</summary>
    public const uint SwpNoActivate = 0x0010;

    /// <summary>SetWindowPos: show the window (sets WS_VISIBLE).</summary>
    public const uint SwpShowWindow = 0x0040;

    /// <summary>MonitorFromPoint flag: return the nearest monitor.</summary>
    public const uint MonitorDefaultToNearest = 2;

    /// <summary>
    /// Modifiers for <see cref="RegisterHotKey"/>. Values match the Win32 MOD_* flags;
    /// <see cref="NoRepeat"/> stops the hotkey from auto-repeating while held.
    /// </summary>
    [Flags]
    public enum HotkeyModifiers : uint
    {
        None = 0,
        Alt = 0x0001,
        Control = 0x0002,
        Shift = 0x0004,
        Win = 0x0008,
        NoRepeat = 0x4000,
    }

    // =====================================================================
    //  Structs / delegates
    // =====================================================================

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    /// <summary>
    /// Header of a device-independent bitmap description, laid out exactly as
    /// the win32 <c>BITMAPINFOHEADER</c> so <see cref="CreateDIBSection"/> can
    /// hand us a pointer to raw pixel data (no marshalling per pixel).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint BiSize;
        public int BiWidth;
        public int BiHeight;      // negative → top-down rows, matching screen order
        public ushort BiPlanes;
        public ushort BiBitCount;
        public uint BiCompression;
        public uint BiSizeImage;
        public int BiXPelsPerMeter;
        public int BiYPelsPerMeter;
        public uint BiClrUsed;
        public uint BiClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER BmiHeader;
        public uint BmiColors;    // first palette entry; unused for 32-bit BI_RGB
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int CbSize;
        public RECT RcMonitor;
        public RECT RcWork;       // work area (excludes taskbar)
        public uint DwFlags;
    }

    // =====================================================================
    //  user32.dll
    // =====================================================================

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>
    /// Makes a WS_EX_LAYERED window fully transparent (alpha = 0) while keeping it
    /// hit-testable, so it can sit over the whole screen, swallow clicks and stay
    /// completely invisible — the pixel underneath remains readable via GetPixel.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    /// <summary>Size of the given screen in pixels (virtualized unless the process is DPI-aware).</summary>
    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>Screen DC for the entire (virtual) desktop when hWnd is IntPtr.Zero.</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    /// <summary>CreateDIBSection usage flag: colour table holds RGB triples.</summary>
    public const uint DibRgbColors = 0;

    /// <summary>
    /// Allocates a bitmap backed by directly addressable memory and returns a
    /// pointer to its bits. Used by the magnifier lens: one blit into this
    /// bitmap plus one bulk copy yields a whole square of screen pixels at once.
    /// </summary>
    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage,
                                                 out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

    // =====================================================================
    //  gdi32.dll
    // =====================================================================

    /// <summary>
    /// Returns the colour of a single pixel (COLORREF 0x00BBGGRR) or
    /// <see cref="ClrInvalid"/> when the pixel cannot be read.
    /// </summary>
    [DllImport("gdi32.dll")]
    public static extern uint GetPixel(IntPtr hdc, int nXPos, int nYPos);

    /// <summary>Creates a memory device context compatible with the given DC.</summary>
    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    /// <summary>Creates a bitmap compatible with the given DC.</summary>
    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    /// <summary>Selects an object into a DC; returns the previously selected object.</summary>
    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hdc);

    /// <summary>Raster-operation code for a plain source copy (what CopyFromScreen uses).</summary>
    public const uint SrcCopy = 0x00CC0020;

    /// <summary>
    /// Copies a rectangle between device contexts. This is the workhorse behind
    /// the 1×1 sampling blit: screen DC → memory DC, then GetPixel on the memory
    /// DC reads the copied value back.
    /// </summary>
    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
                                     IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);

    // =====================================================================
    //  kernel32.dll
    // =====================================================================

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern IntPtr GetModuleHandleW(string lpModuleName);

    // =====================================================================
    //  Small helpers built on top of the raw imports
    // =====================================================================

    /// <summary>
    /// Moves a topmost, non-activating window to an exact position expressed in
    /// *physical* screen pixels. Using SetWindowPos instead of WPF's Left/Top keeps
    /// placement deterministic under Per-Monitor-V2 DPI (WPF would otherwise convert
    /// device-independent units using the DPI of whichever monitor the window is on).
    /// </summary>
    public static void MoveTopmostNoActivate(IntPtr hwnd, int x, int y)
        => SetWindowPos(hwnd, new IntPtr(-1) /* HWND_TOPMOST */, x, y, 0, 0, SwpNoSize | SwpNoActivate);

    /// <summary>
    /// Positions, sizes and <b>shows</b> a topmost, non-activating window at an
    /// exact rectangle expressed in *physical* screen pixels (used to lay the
    /// picking overlay over the whole virtual screen). HwndSource creates its
    /// window hidden, so the SWP_SHOWWINDOW flag is what actually makes the
    /// overlay visible — without it the window is never hit-tested and would
    /// receive no mouse input at all.
    /// </summary>
    public static void PlaceTopmostNoActivate(IntPtr hwnd, int x, int y, int width, int height)
        => SetWindowPos(hwnd, new IntPtr(-1) /* HWND_TOPMOST */, x, y, width, height, SwpNoActivate | SwpShowWindow);

    /// <summary>
    /// Adds WS_EX_TRANSPARENT so the window no longer takes part in hit-testing:
    /// clicks fall through to whatever is underneath. Used by the floating widget
    /// so it can never eat the click the picker is waiting for.
    /// </summary>
    public static void MakeClickThrough(IntPtr hwnd)
        => SetWindowLong(hwnd, GwlExStyle, GetWindowLong(hwnd, GwlExStyle) | WsExTransparent);

    /// <summary>
    /// Work area (monitor bounds minus taskbar/docks) of the monitor that contains
    /// the given point. Used to keep the floating widget fully on-screen.
    /// </summary>
    public static RECT GetWorkAreaForPoint(int x, int y)
    {
        IntPtr monitor = MonitorFromPoint(new POINT { X = x, Y = y }, MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero)
        {
            var info = new MONITORINFO { CbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                return info.RcWork;
            }
        }

        // Degenerate fallback: caller clamps into a 1×1 area, so at minimum the
        // window stays reachable instead of flying off into negative space.
        return new RECT { Left = 0, Top = 0, Right = 1, Bottom = 1 };
    }
}
