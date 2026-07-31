using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ColorPicker.Services;

/// <summary>
/// Samples the colour of a single screen pixel.
///
/// Primary path: <c>BitBlt</c> a 1×1 rectangle of the desktop into a small
/// reusable memory DC and read the copied value back. This is the same
/// strategy PowerToys' colour picker uses (<c>CopyFromScreen</c> is a thin
/// wrapper over exactly this blit): it goes through GDI's normal blit path,
/// which is the reliable way to read composed content — hardware-accelerated
/// browser pages, DirectComposition surfaces — on setups where a direct
/// <c>GetPixel</c> on the screen DC fails or returns stale data. The blit is
/// done with raw gdi32 imports so the app keeps zero NuGet dependencies
/// (no System.Drawing.Common).
///
/// Fallback path: the classic <c>GetDC(NULL) → GetPixel → ReleaseDC</c> dance,
/// used when the blit itself is unavailable — the two are verified to return
/// identical values on normal content.
///
/// All coordinates are *physical* screen pixels (the process is
/// Per-Monitor-V2 DPI aware — see app.manifest), the same space the picking
/// overlay's mouse messages and <c>GetCursorPos</c> deliver, so multi-monitor
/// and high-DPI setups sample exactly the pixel under the cursor.
/// </summary>
public sealed class PixelCaptureService : IDisposable
{
    // Reusable 1×1 memory DC + bitmap so steady-state sampling allocates nothing.
    private IntPtr _memDc = IntPtr.Zero;
    private IntPtr _memBitmap = IntPtr.Zero;
    private IntPtr _oldBitmap = IntPtr.Zero;

    // Second, separately sized set used by the magnifier lens (a square patch
    // of screen rather than a single pixel). Kept apart from the 1×1 set so the
    // hot single-pixel path never has to reason about bitmap dimensions.
    private IntPtr _patchDc = IntPtr.Zero;
    private IntPtr _patchBitmap = IntPtr.Zero;
    private IntPtr _patchBits = IntPtr.Zero;   // raw pixel data of the DIB section
    private IntPtr _patchOldBitmap = IntPtr.Zero;
    private int _patchSize;
    private bool _disposed;

    /// <summary>
    /// Tries to read the 8-bit RGB components of the pixel at the given
    /// *physical* screen coordinates. Returns false when the pixel is unreadable
    /// (protected/UAC surfaces, secure desktop, off-screen coordinates, ...).
    /// </summary>
    public bool TryGetPixel(int x, int y, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (_disposed) return false;

        // ---- Path 1: 1×1 BitBlt into a memory DC (PowerToys-style) ----------
        if (TryBlitPixel(x, y, out uint blitted))
        {
            r = (byte)(blitted & 0xFF);
            g = (byte)((blitted >> 8) & 0xFF);
            b = (byte)((blitted >> 16) & 0xFF);
            return true;
        }

        // ---- Path 2: direct GetPixel on the desktop DC ----------------------
        IntPtr hdc = Win32Api.GetDC(IntPtr.Zero); // NULL → whole desktop DC
        if (hdc == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            // GetPixel returns COLORREF 0x00BBGGRR — the LOW byte is red and
            // the HIGH byte is blue (same order as the blit path above reads it;
            // getting these backwards silently swaps the two channels).
            uint pixel = Win32Api.GetPixel(hdc, x, y);
            if (pixel == Win32Api.ClrInvalid)
            {
                return false;
            }

            r = (byte)(pixel & 0xFF);
            g = (byte)((pixel >> 8) & 0xFF);
            b = (byte)((pixel >> 16) & 0xFF);
            return true;
        }
        finally
        {
            // Always release the DC — GetDC without a matching ReleaseDC leaks.
            Win32Api.ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    /// <summary>
    /// Blits the single screen pixel at (<paramref name="x"/>, <paramref name="y"/>)
    /// into the cached memory DC and returns its COLORREF. False when the blit
    /// path is unavailable or the copy failed.
    /// </summary>
    private bool TryBlitPixel(int x, int y, out uint pixel)
    {
        pixel = 0;

        IntPtr hdcScreen = Win32Api.GetDC(IntPtr.Zero);
        if (hdcScreen == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (!EnsureMemoryDc(hdcScreen))
            {
                return false;
            }

            if (!Win32Api.BitBlt(_memDc, 0, 0, 1, 1, hdcScreen, x, y, Win32Api.SrcCopy))
            {
                return false;
            }

            pixel = Win32Api.GetPixel(_memDc, 0, 0);
            return pixel != Win32Api.ClrInvalid;
        }
        finally
        {
            Win32Api.ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    /// <summary>
    /// Copies a square of <paramref name="size"/>&#xD7;<paramref name="size"/>
    /// physical screen pixels centred on (<paramref name="x"/>, <paramref name="y"/>)
    /// into <paramref name="bgra"/> as top-down 32-bit BGRA rows (the layout
    /// Bgra32 expects, so the buffer can go straight into a WriteableBitmap).
    ///
    /// One <c>BitBlt</c> plus one bulk copy per frame, no allocation: this runs
    /// on every mouse move while the magnifier lens is on screen.
    ///
    /// Partly off-screen patches are handled by blitting only the visible
    /// intersection — the rest of the buffer keeps the previous frame instead of
    /// going blank, so the lens never flickers at a screen edge. The centre
    /// pixel is always inside the screen while the cursor is, and that is the
    /// only pixel that ever gets picked.
    /// </summary>
    public bool TryCapturePatch(int x, int y, int size, byte[] bgra)
    {
        if (_disposed || size < 3 || bgra.Length < size * size * 4) return false;

        IntPtr hdcScreen = Win32Api.GetDC(IntPtr.Zero);
        if (hdcScreen == IntPtr.Zero) return false;

        try
        {
            if (!EnsurePatchDc(hdcScreen, size)) return false;

            int half = size / 2;
            int left = x - half;
            int top = y - half;

            int vx = Win32Api.GetSystemMetrics(Win32Api.SmXVirtualScreen);
            int vy = Win32Api.GetSystemMetrics(Win32Api.SmYVirtualScreen);
            int vw = Win32Api.GetSystemMetrics(Win32Api.SmCXVirtualScreen);
            int vh = Win32Api.GetSystemMetrics(Win32Api.SmCYVirtualScreen);

            int srcLeft = Math.Max(left, vx);
            int srcTop = Math.Max(top, vy);
            int srcRight = Math.Min(left + size, vx + vw);
            int srcBottom = Math.Min(top + size, vy + vh);
            if (srcRight <= srcLeft || srcBottom <= srcTop) return false; // fully off-screen

            if (!Win32Api.BitBlt(_patchDc, srcLeft - left, srcTop - top,
                                 srcRight - srcLeft, srcBottom - srcTop,
                                 hdcScreen, srcLeft, srcTop, Win32Api.SrcCopy))
            {
                return false;
            }

            Marshal.Copy(_patchBits, bgra, 0, size * size * 4);
            return true;
        }
        finally
        {
            Win32Api.ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    /// <summary>Lazily creates (and sizes) the DC + DIB section used for lens blits.</summary>
    private bool EnsurePatchDc(IntPtr hdcScreen, int size)
    {
        if (_patchDc != IntPtr.Zero && _patchSize == size)
        {
            return true;
        }

        DestroyPatchResources();

        IntPtr dc = Win32Api.CreateCompatibleDC(hdcScreen);
        if (dc == IntPtr.Zero) return false;

        var info = new Win32Api.BITMAPINFO
        {
            BmiHeader = new Win32Api.BITMAPINFOHEADER
            {
                BiSize = (uint)Marshal.SizeOf<Win32Api.BITMAPINFOHEADER>(),
                BiWidth = size,
                BiHeight = -size,      // negative → rows top-down, same order as the screen
                BiPlanes = 1,
                BiBitCount = 32,
                BiCompression = 0,     // BI_RGB: 32-bit rows are BGRA, alpha byte ignored
            },
        };

        IntPtr bitmap = Win32Api.CreateDIBSection(dc, ref info, Win32Api.DibRgbColors, out IntPtr bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
        {
            Win32Api.DeleteDC(dc);
            return false;
        }

        _patchOldBitmap = Win32Api.SelectObject(dc, bitmap);
        _patchDc = dc;
        _patchBitmap = bitmap;
        _patchBits = bits;
        _patchSize = size;
        return true;
    }

    private void DestroyPatchResources()
    {
        if (_patchDc == IntPtr.Zero) return;

        // Deselect before deleting (GDI requirement), then drop both objects.
        if (_patchOldBitmap != IntPtr.Zero)
        {
            Win32Api.SelectObject(_patchDc, _patchOldBitmap);
        }
        if (_patchBitmap != IntPtr.Zero)
        {
            Win32Api.DeleteObject(_patchBitmap);
        }
        Win32Api.DeleteDC(_patchDc);

        _patchDc = IntPtr.Zero;
        _patchBitmap = IntPtr.Zero;
        _patchBits = IntPtr.Zero;
        _patchOldBitmap = IntPtr.Zero;
        _patchSize = 0;
    }

    /// <summary>Lazily creates the 1×1 memory DC + bitmap used for every blit.</summary>
    private bool EnsureMemoryDc(IntPtr hdcScreen)
    {
        if (_memDc != IntPtr.Zero)
        {
            return true;
        }

        IntPtr dc = Win32Api.CreateCompatibleDC(hdcScreen);
        if (dc == IntPtr.Zero)
        {
            return false;
        }

        IntPtr bitmap = Win32Api.CreateCompatibleBitmap(hdcScreen, 1, 1);
        if (bitmap == IntPtr.Zero)
        {
            Win32Api.DeleteDC(dc);
            return false;
        }

        _oldBitmap = Win32Api.SelectObject(dc, bitmap);
        _memDc = dc;
        _memBitmap = bitmap;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_memDc != IntPtr.Zero)
        {
            // Deselect the bitmap before deleting it (GDI requirement), then
            // tear both objects down so nothing leaks on shutdown.
            if (_oldBitmap != IntPtr.Zero)
            {
                Win32Api.SelectObject(_memDc, _oldBitmap);
            }
            if (_memBitmap != IntPtr.Zero)
            {
                Win32Api.DeleteObject(_memBitmap);
            }
            Win32Api.DeleteDC(_memDc);
            _memDc = IntPtr.Zero;
            _memBitmap = IntPtr.Zero;
        }

        DestroyPatchResources();

        GC.SuppressFinalize(this);
    }
}
