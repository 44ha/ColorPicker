using System.Windows;
using System.Windows.Threading;

namespace ColorPicker.Services;

/// <summary>Arguments for <see cref="PickingController.ColorPicked"/>.</summary>
public sealed class ColorPickedEventArgs : EventArgs
{
    public ColorPickedEventArgs(Models.ColorInfo color, bool copiedToClipboard)
    {
        Color = color;
        CopiedToClipboard = copiedToClipboard;
    }

    public Models.ColorInfo Color { get; }

    /// <summary>False when the clipboard was busy — the UI can tell the user to copy from the card instead.</summary>
    public bool CopiedToClipboard { get; }
}

/// <summary>
/// Orchestrates "picking mode":
///
///   1. shows a transparent full-screen overlay
///      (<see cref="PickingOverlayService"/>) that captures the cursor locally —
///      no SetWindowsHookEx / global input hook is installed, which keeps the
///      process out of keylogger-heuristic territory,
///   2. shows the transparent floating widget at the cursor
///      (<see cref="FloatingWidgetWindow"/>) and live-updates it as the cursor
///      moves (sampling through <see cref="PixelCaptureService"/>), including a
///      magnified nearest-neighbour view of the pixels under the cursor so the
///      exact pixel can be aimed at, not just guessed,
///   3. on a left click: samples the exact clicked pixel, copies the HEX value
///      to the clipboard, flashes a "Copied!" toast and raises
///      <see cref="ColorPicked"/> so the dashboard can record history,
///   4. on a right click / Esc / hotkey toggle: cancels silently.
///
/// The overlay only exists while picking runs and is destroyed the moment it
/// ends, so between picks the app observes no input whatsoever.
/// </summary>
public sealed class PickingController : IDisposable
{
    /// <summary>Minimum gap between two colour samples while the cursor moves (ms).</summary>
    private const int SampleIntervalMs = 15;

    private readonly PickingOverlayService _overlay;
    private readonly PixelCaptureService _pixelCapture;
    private readonly ClipboardService _clipboard;
    private readonly ColorConverterService _converter;

    private FloatingWidgetWindow? _widget;
    private Models.ColorInfo? _lastColor;
    private long _lastSampleTick;
    private bool _subscribed;

    /// <summary>Scratch buffer for the magnifier lens; grown once, reused every frame.</summary>
    private byte[] _lensPixels = Array.Empty<byte>();

    public PickingController(
        PickingOverlayService overlay,
        PixelCaptureService pixelCapture,
        ClipboardService clipboard,
        ColorConverterService converter)
    {
        _overlay = overlay;
        _pixelCapture = pixelCapture;
        _clipboard = clipboard;
        _converter = converter;
    }

    public bool IsPicking { get; private set; }

    /// <summary>Raised whenever picking starts or ends.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised after a successful pick (on the UI thread).</summary>
    public event EventHandler<ColorPickedEventArgs>? ColorPicked;

    public void Start()
    {
        if (IsPicking) return;

        // Bring the overlay up first: if it can't be created we fail before
        // anything is shown, so a failed start never leaves a stray widget.
        _overlay.Start(); // may throw Win32Exception — callers surface it in the status bar

        Win32Api.GetCursorPos(out var cursor);

        // Show the widget immediately with whatever is under the cursor right now.
        _lastColor = Sample(cursor.X, cursor.Y) ?? _converter.FromRgb(0x1D, 0x4E, 0xD8);
        _lastSampleTick = Environment.TickCount64;

        _widget = new FloatingWidgetWindow();
        _widget.UpdateColor(_lastColor);
        _widget.Show();
        _widget.MoveTo(cursor.X, cursor.Y);
        UpdateLens(cursor.X, cursor.Y);

        if (!_subscribed)
        {
            _overlay.MouseMoved += OnMouseMoved;
            _overlay.LeftButtonDown += OnLeftButtonDown;
            _overlay.RightButtonDown += OnRightButtonDown;
            _subscribed = true;
        }

        IsPicking = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Ends picking mode without capturing anything.</summary>
    public void Cancel()
    {
        if (!IsPicking) return;
        EndPicking();
    }

    // ---------------------------------------------------------------------
    //  Overlay input handlers (already on the UI thread)
    // ---------------------------------------------------------------------

    private void OnMouseMoved(int x, int y)
    {
        if (!IsPicking) return;

        long now = Environment.TickCount64;
        if (now - _lastSampleTick < SampleIntervalMs) return; // mouse events can arrive far faster than we can sample
        _lastSampleTick = now;

        _widget?.MoveTo(x, y);

        var color = Sample(x, y);
        if (color is not null)
        {
            _lastColor = color;
            _widget?.UpdateColor(color);
        }

        UpdateLens(x, y);
    }

    /// <summary>
    /// Feeds the magnifier lens with a square of real screen pixels centred on
    /// the cursor. Independent of the colour sample: the lens must keep moving
    /// even on the frames where a sample fails (protected surfaces), because the
    /// user is aiming with it.
    /// </summary>
    private void UpdateLens(int x, int y)
    {
        if (_widget is null) return;

        int size = _widget.PatchSize;
        int needed = size * size * 4;
        if (_lensPixels.Length < needed)
        {
            _lensPixels = new byte[needed];
        }

        if (_pixelCapture.TryCapturePatch(x, y, size, _lensPixels))
        {
            _widget.UpdateMagnifier(_lensPixels);
        }
    }

    private void OnLeftButtonDown(int x, int y)
    {
        if (!IsPicking) return;

        // Finish on the dispatcher rather than inside the overlay's window
        // procedure so we never close a window while it is processing its own
        // mouse message.
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => CompletePick(x, y)));
    }

    private void OnRightButtonDown(int x, int y)
    {
        if (!IsPicking) return;
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Cancel));
    }

    // ---------------------------------------------------------------------

    private void CompletePick(int x, int y)
    {
        // Tear down FIRST: closing the widget (and the overlay) before sampling
        // guarantees the pixel we read is the screen itself — never our own HUD.
        // The widget follows the cursor with a small lag and can be clamped over
        // it near screen edges; sampling while it was still up made picks return
        // the widget's colour instead of the content underneath.
        EndPicking();

        var color = Sample(x, y) ?? _lastColor;
        if (color is null) return;

        bool copied = _clipboard.TrySetText(color.Hex);
        if (copied)
        {
            ShowCopiedToast(x, y, color);
        }

        ColorPicked?.Invoke(this, new ColorPickedEventArgs(color, copied));
    }

    private void EndPicking()
    {
        if (_subscribed)
        {
            _overlay.MouseMoved -= OnMouseMoved;
            _overlay.LeftButtonDown -= OnLeftButtonDown;
            _overlay.RightButtonDown -= OnRightButtonDown;
            _subscribed = false;
        }

        _overlay.Stop();

        if (_widget is not null)
        {
            _widget.Close();
            _widget = null;
        }

        IsPicking = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reads the pixel at the given physical screen coordinates.</summary>
    private Models.ColorInfo? Sample(int x, int y)
        => _pixelCapture.TryGetPixel(x, y, out byte r, out byte g, out byte b)
            ? _converter.FromRgb(r, g, b, x, y)
            : null;

    private static void ShowCopiedToast(int x, int y, Models.ColorInfo color)
    {
        var toast = new ToastWindow(color);
        toast.Show();
        toast.MoveTo(x, y);
        toast.PlayAndClose();
    }

    public void Dispose()
    {
        if (IsPicking)
        {
            EndPicking();
        }
        else
        {
            _overlay.Stop();
        }
    }
}
