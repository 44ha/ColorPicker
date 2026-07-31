using System.Runtime.InteropServices;
using System.Windows;

namespace ColorPicker.Services;

/// <summary>
/// Thin, exception-safe wrapper around the WPF clipboard.
///
/// The clipboard is a shared COM resource: another process can hold it open,
/// which makes SetText throw intermittently. We retry once before giving up
/// and report success/failure instead of crashing the UI thread.
/// </summary>
public sealed class ClipboardService
{
    /// <summary>Puts text on the clipboard. Returns false when the clipboard was busy/unavailable.</summary>
    public bool TrySetText(string text)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(40); // someone else had the clipboard open — brief pause and retry
            }
            catch (InvalidOperationException)
            {
                Thread.Sleep(40);
            }
        }

        return false;
    }
}
