using System.ComponentModel;
using System.Windows.Interop;

namespace ColorPicker.Services;

/// <summary>
/// The input surface used while picking mode is active.
///
/// This replaces the old <c>SetWindowsHookEx(WH_MOUSE_LL)</c> hook. A process that
/// installs global input hooks is exactly what endpoint-protection heuristics watch
/// for (it is how keyloggers behave), whereas an ordinary top-level window is
/// invisible to those heuristics. The window created here:
///
///   * covers the entire (virtual) screen, so it is the window under the cursor
///     on every monitor,
///   * is WS_POPUP + WS_EX_TOPMOST + WS_EX_TOOLWINDOW: borderless, always on top,
///     no taskbar button and no Alt+Tab entry,
///   * is WS_EX_NOACTIVATE: showing it (and clicking it) never steals keyboard
///     focus from the application the user is picking from,
///   * is WS_EX_LAYERED at alpha 1/255: visually invisible and invisible to
///     <c>GetPixel</c> — the screen underneath still reads back its real colour —
///     while remaining hit-testable. (Alpha 0 would look even cleaner, but
///     Windows documents that layered areas *whose alpha value is zero* "let the
///     mouse messages through", so an alpha-0 window never receives a single
///     mouse message — verified experimentally. Alpha 1 is the smallest value
///     that Windows still routes input to.)
///
/// Because the window owns the mouse messages, the click lands here instead of in
/// the application below and is simply never forwarded — the same "swallow" the
/// hook achieved by returning 1 from its callback.
///
/// The window only exists between <see cref="Start"/> and <see cref="Stop"/>:
/// outside picking mode the app observes no input at all. Events are raised on the
/// UI thread (HwndSource pumps through WPF's message loop), so subscribers may
/// touch UI directly.
/// </summary>
public sealed class PickingOverlayService : IDisposable
{
    private HwndSource? _source;
    private int _originX;
    private int _originY;
    private bool _disposed;

    /// <summary>Raised on mouse move with physical screen coordinates.</summary>
    public event Action<int, int>? MouseMoved;

    /// <summary>Raised when the left button is pressed anywhere on screen. The click is consumed.</summary>
    public event Action<int, int>? LeftButtonDown;

    /// <summary>Raised when the right button is pressed anywhere on screen. The click is consumed.</summary>
    public event Action<int, int>? RightButtonDown;

    public bool IsRunning => _source is not null;

    /// <summary>
    /// Creates the transparent full-screen overlay. Throws <see cref="Win32Exception"/>
    /// when it cannot be made invisible or positioned (callers surface that in the
    /// status bar).
    /// </summary>
    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PickingOverlayService));
        if (IsRunning) return;

        // Virtual screen = bounding box of every monitor. The process is
        // PerMonitorV2-aware (app.manifest), so GetSystemMetrics reports
        // unscaled physical pixels — the same space GetCursorPos and GetPixel use.
        int x = Win32Api.GetSystemMetrics(Win32Api.SmXVirtualScreen);
        int y = Win32Api.GetSystemMetrics(Win32Api.SmYVirtualScreen);
        int width = Win32Api.GetSystemMetrics(Win32Api.SmCXVirtualScreen);
        int height = Win32Api.GetSystemMetrics(Win32Api.SmCYVirtualScreen);

        if (width <= 0 || height <= 0)
        {
            throw new Win32Exception("Couldn't determine the virtual screen size.");
        }

        var parameters = new HwndSourceParameters("ChromaPickingOverlay", width, height)
        {
            PositionX = x,
            PositionY = y,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP
            ExtendedWindowStyle = Win32Api.WsExTopmost
                                | Win32Api.WsExToolWindow
                                | Win32Api.WsExLayered
                                | Win32Api.WsExNoActivate,

            // Required: HwndSource silently strips WS_EX_LAYERED from the extended
            // style it is given unless UsesPerPixelOpacity is set (then it keeps the
            // style and owns the window's opacity itself). Without this flag the
            // SetLayeredWindowAttributes call below fails with ERROR_INVALID_PARAMETER.
            // Nothing is ever rendered into this window; it only supplies the
            // layered style that the SetLayeredWindowAttributes call below needs.
            UsesPerPixelOpacity = true,
        };

        var source = new HwndSource(parameters);
        source.AddHook(WndProc);

        // Alpha 1, *not* 0 — the crucial detail:
        //
        //   * Windows routes mouse input around layered areas "whose alpha value
        //     is zero", so making the window invisible with alpha 0 would make it
        //     deaf: no move, no click, ever (measured — WindowFromPoint skips the
        //     window entirely and the widget stops following the cursor).
        //   * Alpha 1/255 is still visually nothing (an empty window surface
        //     blended at 1/255) and leaves the value GetPixel reads from the
        //     screen unchanged (also measured), while making this window the one
        //     under the cursor — so the click lands here and is swallowed instead
        //     of reaching the app below.
        const byte OverlayAlpha = 1;
        if (!Win32Api.SetLayeredWindowAttributes(source.Handle, 0, OverlayAlpha, Win32Api.LwaAlpha))
        {
            int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            source.Dispose();
            throw new Win32Exception(error, "Couldn't make the picking overlay transparent.");
        }

        // Exact physical-pixel placement over the whole virtual screen, raised
        // above every other topmost window (the floating widget included). This
        // also shows the window: HwndSource hands back a hidden hwnd, and an
        // invisible window is never hit-tested, so no input would reach it.
        Win32Api.PlaceTopmostNoActivate(source.Handle, x, y, width, height);

        _originX = x;
        _originY = y;
        _source = source;
    }

    /// <summary>Destroys the overlay window. Outside picking mode there is no input surface at all.</summary>
    public void Stop()
    {
        if (_source is null) return;

        _source.RemoveHook(WndProc);
        _source.Dispose(); // destroys the hwnd
        _source = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Win32Api.WmMouseMove:
                RaiseWithScreenPoint(MouseMoved, lParam);
                handled = true; // input stays local: the app below sees no hover
                break;

            case Win32Api.WmLButtonDown:
                RaiseWithScreenPoint(LeftButtonDown, lParam);
                handled = true; // consume the click: don't forward it to the app below
                break;

            case Win32Api.WmRButtonDown:
                RaiseWithScreenPoint(RightButtonDown, lParam);
                handled = true; // right-click = cancel, also swallowed
                break;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Converts the message's client coordinates into physical screen pixels.
    /// The overlay's client area starts at the virtual-screen origin, so adding
    /// the two back gives exactly what GetCursorPos / GetPixel expect.
    /// </summary>
    private void RaiseWithScreenPoint(Action<int, int>? handler, IntPtr lParam)
    {
        if (handler is null) return;

        int packed = lParam.ToInt32();
        int clientX = (short)(packed & 0xFFFF);
        int clientY = (short)((packed >> 16) & 0xFFFF);

        handler(_originX + clientX, _originY + clientY);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        GC.SuppressFinalize(this);
    }
}
