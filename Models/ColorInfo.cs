using System.Globalization;

namespace ColorPicker.Models;

/// <summary>
/// An immutable, fully-converted snapshot of a picked colour.
///
/// A single instance holds every representation the UI needs
/// (HEX, HEX raw, RGB, RGBA, HSL and CMYK), pre-computed once when the
/// colour is captured so the dashboard rows are cheap to bind.
///
/// This model is deliberately free of any WPF types so the conversion
/// logic can be unit-tested (or reused) without a UI stack.
/// </summary>
public sealed class ColorInfo
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private ColorInfo(byte r, byte g, byte b, int x, int y, DateTime capturedAt)
    {
        R = r;
        G = g;
        B = b;
        X = x;
        Y = y;
        CapturedAt = capturedAt;

        // ---- HEX: "#00F2CC" --------------------------------------------
        Hex = string.Create(Inv, $"#{r:X2}{g:X2}{b:X2}");

        // ---- HEX raw (no '#'): "00F2CC" ---------------------------------
        HexRaw = $"{r:X2}{g:X2}{b:X2}";

        // ---- RGB: "0, 242, 204" ----------------------------------------
        Rgb = string.Join(", ", r.ToString(Inv), g.ToString(Inv), b.ToString(Inv));

        // ---- RGBA: "0, 242, 204, 255" (we always capture fully opaque) --
        Rgba = string.Join(", ", r.ToString(Inv), g.ToString(Inv), b.ToString(Inv), "255");

        // ---- HSL: "171°, 100%, 47%" ------------------------------------
        (double h, double s, double l) = RgbToHsl(r, g, b);
        Hsl = $"{Round(h)}°, {Round(s)}%, {Round(l)}%";

        // ---- CMYK: "100%, 0%, 16%, 5%" ---------------------------------
        (double c, double m, double yellow, double k) = RgbToCmyk(r, g, b);
        Cmyk = $"{Round(c)}%, {Round(m)}%, {Round(yellow)}%, {Round(k)}%";
    }

    public byte R { get; }
    public byte G { get; }
    public byte B { get; }

    /// <summary>Screen X (physical pixels) the colour was picked at; -1 when added manually.</summary>
    public int X { get; }

    /// <summary>Screen Y (physical pixels) the colour was picked at; -1 when added manually.</summary>
    public int Y { get; }

    public DateTime CapturedAt { get; }

    public string Hex { get; }
    public string HexRaw { get; }
    public string Rgb { get; }
    public string Rgba { get; }
    public string Hsl { get; }
    public string Cmyk { get; }

    /// <summary>Creates a colour from raw8-bit RGB components.</summary>
    public static ColorInfo FromRgb(byte r, byte g, byte b, int x = -1, int y = -1)
        => new(r, g, b, x, y, DateTime.Now);

    /// <summary>
    /// Standard RGB → HSL conversion (H in degrees, S/L in percent).
    /// </summary>
    private static (double H, double S, double L) RgbToHsl(byte r, byte g, byte b)
    {
        double r1 = r / 255d, g1 = g / 255d, b1 = b / 255d;
        double max = Math.Max(r1, Math.Max(g1, b1));
        double min = Math.Min(r1, Math.Min(g1, b1));
        double delta = max - min;

        double h = 0;
        if (delta > 0)
        {
            if (Math.Abs(max - r1) < double.Epsilon)      h = 60 * (((g1 - b1) / delta) % 6);
            else if (Math.Abs(max - g1) < double.Epsilon) h = 60 * ((b1 - r1) / delta + 2);
            else                                          h = 60 * ((r1 - g1) / delta + 4);
            if (h < 0) h += 360;
        }

        double l = (max + min) / 2;
        double s = delta < double.Epsilon
            ? 0
            : l > 0.5 ? delta / (2d - max - min) : delta / (max + min);

        return (h, s * 100, l * 100);
    }

    /// <summary>
    /// Standard RGB → CMYK conversion (percentages, K = 1 - max channel).
    /// </summary>
    private static (double C, double M, double Y, double K) RgbToCmyk(byte r, byte g, byte b)
    {
        double r1 = r / 255d, g1 = g / 255d, b1 = b / 255d;
        double k = 1 - Math.Max(r1, Math.Max(g1, b1));

        if (k >= 1 - 1e-9) return (0, 0, 0, 100); // pure black

        double c = (1 - r1 - k) / (1 - k);
        double m = (1 - g1 - k) / (1 - k);
        double y = (1 - b1 - k) / (1 - k);
        return (c * 100, m * 100, y * 100, k * 100);
    }

    /// <summary>Rounds a percentage for display, e.g. 47.45 → 47, 15.7 → 16.</summary>
    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
}
