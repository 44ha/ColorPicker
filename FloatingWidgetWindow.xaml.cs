using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ColorPicker.Models;
using ColorPicker.Services;

namespace ColorPicker;

/// <summary>
/// Code-behind for the floating picking widget. It is a transient, presentation-only
/// view: the <see cref="PickingController"/> pushes colours and captured pixels into
/// it and asks it to follow the cursor, so no view model is needed here.
///
/// The lens on the left is a magnified, nearest-neighbour view of the real pixels
/// under the cursor: <see cref="Zoom"/> physical pixels per magnified pixel, with the
/// centre one outlined so the user can see exactly which pixel the pick will read.
/// </summary>
public partial class FloatingWidgetWindow : Window
{
    /// <summary>Physical screen pixels shown per magnified pixel (~9× zoom).</summary>
    public const double Zoom = 9.0;

    /// <summary>Lens diameter in DIPs, matching the Width/Height in the XAML.</summary>
    private const double LensDipSize = 92;

    /// <summary>Patch size used for the first frames, before the window knows its DPI.</summary>
    private const int FallbackPatchSize = 19;

    private readonly SolidColorBrush _ringBrush = new(Color.FromArgb(0x66, 0x1D, 0x4E, 0xD8));
    private WriteableBitmap? _magnifier;
    private int _patchSize;

    public FloatingWidgetWindow()
    {
        InitializeComponent();

        // The widget is a passive HUD with nothing to click, and it can end up
        // covering the cursor when it is clamped against a screen edge — if it
        // ate the click, the pick would silently do nothing. WS_EX_TRANSPARENT
        // takes it out of hit-testing, so the click falls through to the
        // full-screen picking overlay waiting underneath.
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            Win32Api.MakeClickThrough(hwnd);
        };

        // The ring tracks the sampled colour so the lens reads as one element.
        LensBorder.BorderBrush = _ringBrush;
    }

    /// <summary>
    /// Odd number of physical pixels captured per frame. Odd so the captured
    /// square always has a true centre pixel, which is the one being picked.
    /// </summary>
    public int PatchSize => _patchSize > 0 ? _patchSize : FallbackPatchSize;

    /// <summary>Updates the swatch ring and the stacked value labels with a new sample.</summary>
    public void UpdateColor(ColorInfo color)
    {
        _ringBrush.Color = Color.FromArgb(0x66, color.R, color.G, color.B);

        HexValue.Text = color.Hex;
        RgbValue.Text = color.Rgb;
        HslValue.Text = color.Hsl;
    }

    /// <summary>
    /// Blits a captured square of screen pixels (top-down BGRA, produced by
    /// <see cref="PixelCaptureService.TryCapturePatch"/>) into the lens.
    /// </summary>
    public void UpdateMagnifier(byte[] bgra)
    {
        int size = PatchSize;
        if (bgra.Length < size * size * 4) return;

        if (_magnifier is null || _magnifier.PixelWidth != size)
        {
            _magnifier = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
            Magnifier.Source = _magnifier;
        }

        _magnifier.WritePixels(new Int32Rect(0, 0, size, size), bgra, size * 4, 0);
    }

    /// <summary>Moves the widget next to the given physical screen coordinates.</summary>
    public void MoveTo(int screenX, int screenY)
        => ScreenPlacement.PlaceNear(this, screenX, screenY, offsetX: 26, offsetY: 30);

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        RefreshPatchSize();
    }

    /// <summary>
    /// Recomputes the captured patch from the lens size and the DPI of the
    /// monitor the widget is currently on, so the zoom stays constant in
    /// physical pixels on mixed-DPI setups (the process is PerMonitorV2 aware).
    /// </summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        RefreshPatchSize();
    }

    private void RefreshPatchSize()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        double lensDip = Magnifier.ActualWidth > 0 ? Magnifier.ActualWidth : LensDipSize;

        int size = (int)Math.Round(lensDip * dpi.DpiScaleX / Zoom);
        size = Math.Clamp(size, 9, 99);
        if ((size & 1) == 0) size++; // odd → a true centre pixel
        _patchSize = size;

        // One source pixel in DIPs: the centre outline then covers exactly the
        // pixel the pick will read, on any DPI.
        double marker = Zoom / dpi.DpiScaleX;
        CenterPixel.Width = marker;
        CenterPixel.Height = marker;
    }
}
