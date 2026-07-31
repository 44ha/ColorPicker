using System.Globalization;
using ColorPicker.Models;

namespace ColorPicker.Services;

/// <summary>
/// Converts raw RGB values (or pasted hex strings) into a fully-populated
/// <see cref="ColorInfo"/> carrying every supported representation:
///
///   HEX        #00F2CC
///   HEX raw    00F2CC
///   RGB        0, 242, 204
///   RGBA       0, 242, 204, 255
///   HSL        171°, 100%, 47%
///   CMYK       100%, 0%, 16%, 5%
///
/// All math lives in <see cref="ColorInfo"/>; this service adds hex parsing
/// and acts as the single entry point the rest of the app talks to.
/// </summary>
public sealed class ColorConverterService
{
    /// <summary>Builds a converted colour from an8-bit RGB triple.</summary>
    public ColorInfo FromRgb(byte r, byte g, byte b, int x = -1, int y = -1)
        => ColorInfo.FromRgb(r, g, b, x, y);

    /// <summary>
    /// Tries to parse a user-typed hex string. Accepted forms:
    ///   F0F, #F0F, FF00FF, #FF00FF, 0xFF00FF, #FF00FFCC (alpha ignored —
    ///   the screen can only give us opaque pixels).
    /// </summary>
    public bool TryParseHex(string? text, out ColorInfo color)
    {
        color = null!;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string s = text.Trim();
        if (s.StartsWith('#'))
        {
            s = s[1..];
        }
        else if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            s = s[2..];
        }

        // Accept RGB or RGBA hex; alpha is dropped.
        if (s.Length is not (3 or 6 or 8) || !s.All(Uri.IsHexDigit))
        {
            return false;
        }

        if (s.Length == 3)
        {
            // "#FC0" shorthand → "#FFCC00"
            s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        }
        else if (s.Length == 8)
        {
            s = s[..6];
        }

        byte r = byte.Parse(s[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte g = byte.Parse(s[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte b = byte.Parse(s[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        color = ColorInfo.FromRgb(r, g, b);
        return true;
    }
}
