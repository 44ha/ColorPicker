using System.Windows.Interop;

namespace ColorPicker.Services;

/// <summary>
/// Registers system-wide ("global") hotkeys with <c>RegisterHotKey</c>.
///
/// The hotkey is attached to a hidden message-only window created with
/// <see cref="HwndSource"/>, which lives on the WPF UI thread's message pump —
/// so the hotkey keeps working no matter which application is focused and even
/// while our main window is minimized.
///
/// Pressed hotkeys surface as the <see cref="HotkeyPressed"/> event carrying
/// the caller-chosen hotkey id.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;

    private readonly HwndSource _messageWindow;
    private bool _disposed;

    /// <summary>Raised (on the UI thread) whenever any registered hotkey fires.</summary>
    public event Action<int>? HotkeyPressed;

    public HotkeyService()
    {
        // A tiny invisible top-level window whose only job is to receive WM_HOTKEY.
        var parameters = new HwndSourceParameters("ChromaHotkeyMessageWindow")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP
        };

        _messageWindow = new HwndSource(parameters);
        _messageWindow.AddHook(WndProc);
    }

    /// <summary>Handle of the message window the hotkeys are registered against.</summary>
    public IntPtr Handle => _messageWindow.Handle;

    /// <summary>
    /// Registers a hotkey. Returns false when another application already owns
    /// the combination (Windows reserves hotkeys per-session).
    /// </summary>
    public bool Register(int id, Win32Api.HotkeyModifiers modifiers, uint virtualKey)
        => Win32Api.RegisterHotKey(Handle, id, (uint)modifiers, virtualKey);

    /// <summary>Unregisters a hotkey id. Safe to call for ids that were never registered.</summary>
    public void Unregister(int id)
        => Win32Api.UnregisterHotKey(Handle, id);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            // wParam carries the id passed to RegisterHotKey.
            HotkeyPressed?.Invoke(wParam.ToInt32());
            handled = true;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _messageWindow.RemoveHook(WndProc);
        _messageWindow.Dispose();
        GC.SuppressFinalize(this);
    }
}
