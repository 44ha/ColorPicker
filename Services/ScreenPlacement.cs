using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ColorPicker.Services;

/// <summary>
/// Positions the floating widget / toast windows next to the cursor using
/// physical-pixel coordinates (see Win32Api.MoveTopmostNoActivate for why we
/// use SetWindowPos rather than Window.Left/Top under Per-Monitor-V2 DPI),
/// flipping and clamping so they never spill off the current monitor.
/// </summary>
public static class ScreenPlacement
{
    /// <summary>
    /// Moves <paramref name="window"/> so its top-left corner sits
    /// (<paramref name="offsetX"/>, <paramref name="offsetY"/>) pixels away from
    /// the anchor point, flipping to the opposite side and clamping to the
    /// monitor's work area when there is not enough room.
    ///
    /// The widget must never end up covering the anchor point: the anchor is
    /// the pixel being colour-sampled, and a window sitting on top of it would
    /// make <c>GetPixel</c> read the widget instead of the content underneath
    /// (i.e. "wrong colour" picks near screen edges). The four quadrants around
    /// the anchor are tried in order — clamped into the work area — and the
    /// first one that leaves the anchor (plus a small margin) uncovered wins.
    /// </summary>
    public static void PlaceNear(Window window, int anchorX, int anchorY, int offsetX = 26, int offsetY = 30)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();

        // Window size in physical pixels (ActualWidth/Height are DIU).
        var dpi = VisualTreeHelper.GetDpi(window);
        int width = Math.Max(1, (int)(window.ActualWidth * dpi.DpiScaleX));
        int height = Math.Max(1, (int)(window.ActualHeight * dpi.DpiScaleY));

        var work = Win32Api.GetWorkAreaForPoint(anchorX, anchorY);
        int maxX = Math.Max(work.Left, work.Right - width);
        int maxY = Math.Max(work.Top, work.Bottom - height);

        // Candidate corners: right/below, left/below, right/above, left/above.
        var candidates = new (int x, int y)[]
        {
            (anchorX + offsetX, anchorY + offsetY),
            (anchorX - offsetX - width, anchorY + offsetY),
            (anchorX + offsetX, anchorY - offsetY - height),
            (anchorX - offsetX - width, anchorY - offsetY - height),
        };

        const int margin = 8; // clearance kept between the anchor and the widget
        int bestX = work.Left, bestY = work.Top;
        int bestDistance = int.MaxValue;

        foreach (var (cx, cy) in candidates)
        {
            int x = Math.Clamp(cx, work.Left, maxX);
            int y = Math.Clamp(cy, work.Top, maxY);

            if (!CoversAnchor(x, y, width, height, anchorX, anchorY, margin))
            {
                bestX = x;
                bestY = y;
                break;
            }

            // Fallback bookkeeping: keep the candidate that puts the anchor
            // farthest outside the widget, in case every quadrant overlaps
            // (only possible on a pathologically small work area).
            int distance = DistanceOutside(x, y, width, height, anchorX, anchorY);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestX = x;
                bestY = y;
            }
        }

        Win32Api.MoveTopmostNoActivate(hwnd, bestX, bestY);
    }

    private static bool CoversAnchor(int x, int y, int width, int height, int anchorX, int anchorY, int margin)
        => anchorX >= x - margin && anchorX <= x + width + margin
        && anchorY >= y - margin && anchorY <= y + height + margin;

    /// <summary>How far the anchor is from the widget's border (0 = inside).</summary>
    private static int DistanceOutside(int x, int y, int width, int height, int anchorX, int anchorY)
    {
        int dx = Math.Max(x - anchorX, anchorX - (x + width));
        int dy = Math.Max(y - anchorY, anchorY - (y + height));
        return Math.Max(dx, dy);
    }
}
