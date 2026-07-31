# Verifies the magnifier lens.
#
# SAFETY: part 1 only *reads* the screen (BitBlt + GetPixel, the same reads the
# app performs). Part 2 sends messages exclusively to Chroma's own windows
# (found by their own titles) and invokes UIA elements of its own process, and
# cancels with a right-click so the clipboard is never touched. Nothing moves
# the cursor and nothing is injected into the global input stream.

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class L
{
    [StructLayout(LayoutKind.Sequential)] public struct BITMAPINFOHEADER {
        public uint BiSize; public int BiWidth; public int BiHeight;
        public ushort BiPlanes; public ushort BiBitCount; public uint BiCompression;
        public uint BiSizeImage; public int BiXPelsPerMeter; public int BiYPelsPerMeter;
        public uint BiClrUsed; public uint BiClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] public struct BITMAPINFO {
        public BITMAPINFOHEADER BmiHeader; public uint BmiColors;
    }

    [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr d, int dx, int dy, int w, int h, IntPtr s, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] public static extern uint GetPixel(IntPtr hdc, int x, int y);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

    // Same code path PixelCaptureService.TryCapturePatch uses.
    public static string Report(int x, int y, int size)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return "no screen DC";
        IntPtr dc = CreateCompatibleDC(screen);
        var bi = new BITMAPINFO();
        bi.BmiHeader = new BITMAPINFOHEADER {
            BiSize = 40, BiWidth = size, BiHeight = -size,
            BiPlanes = 1, BiBitCount = 32, BiCompression = 0
        };
        IntPtr bits = IntPtr.Zero;
        IntPtr bmp = CreateDIBSection(dc, ref bi, 0, out bits, IntPtr.Zero, 0);
        IntPtr old = SelectObject(dc, bmp);
        int half = size / 2;
        bool blit = BitBlt(dc, 0, 0, size, size, screen, x - half, y - half, 0x00CC0020);

        byte[] buf = new byte[size * size * 4];
        if (blit && bits != IntPtr.Zero) Marshal.Copy(bits, buf, 0, size * size * 4);

        uint g = GetPixel(screen, x, y);
        SelectObject(dc, old); DeleteObject(bmp); DeleteDC(dc); ReleaseDC(IntPtr.Zero, screen);

        int ci = (half * size + half) * 4;
        int b0 = buf[ci], b1 = buf[ci + 1], b2 = buf[ci + 2];
        int gr = (int)((g >> 16) & 0xFF), gg = (int)((g >> 8) & 0xFF), gb = (int)(g & 0xFF);

        string orderAsRgb = string.Format("bytes[0..2] as RGB=({0},{1},{2})", b0, b1, b2);
        string orderAsBgr = string.Format("bytes[0..2] as BGR=({0},{1},{2})", b2, b1, b0);
        string gp = string.Format("GetPixel RGB=({0},{1},{2})", gr, gg, gb);
        bool rgbMatches = b0 == gr && b1 == gg && b2 == gb;
        bool bgrMatches = b2 == gr && b1 == gg && b0 == gb;
        string verdict = rgbMatches ? "  -> BYTES ARE RGB ORDER" : (bgrMatches ? "  -> BYTES ARE BGR ORDER" : "  -> NEITHER (real content difference)");
        return orderAsRgb + " | " + orderAsBgr + " | " + gp + verdict;
    }

    public static void Aim(IntPtr h, int x, int y)
    {
        IntPtr lp = (IntPtr)((y << 16) | (x & 0xFFFF));
        SendMessage(h, 0x0200, IntPtr.Zero, lp);   // WM_MOUSEMOVE
        SendMessage(h, 0x0204, (IntPtr)1, lp);     // WM_RBUTTONDOWN
        SendMessage(h, 0x0205, IntPtr.Zero, lp);   // WM_RBUTTONUP
    }
}
"@

if (-not [L]::SetProcessDpiAwarenessContext([IntPtr](-4))) { [void][L]::SetProcessDPIAware() }

$fail = 0
function Check([string]$label, [bool]$ok) {
    if ($ok) { Write-Output ("  [PASS] " + $label) } else { $script:fail++; Write-Output ("  [FAIL] " + $label) }
}

$proc = Get-Process -Name ColorPicker -ErrorAction SilentlyContinue
if (-not $proc) { Write-Output 'app not running - start it first'; exit 1 }

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$proc.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
$btnCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Button)

# A saturated point (the accent-blue button) is what settles channel order:
# on a near-grey pixel an R/B swap is invisible, on blue it is unmissable.
$accent = $null
foreach ($b in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)) {
    if ($b.Current.Name -like '*Pick a color*') { $accent = $b; break }
}
$points = @(@(100,100), @(400,300), @(900,600))
if ($accent) {
    $rc = $accent.Current.BoundingRectangle
    $points += ,@([int]($rc.X + $rc.Width / 2), [int]($rc.Y + $rc.Height / 2))
    Write-Output ('accent test point from the blue button: ' + [int]($rc.X + $rc.Width / 2) + ',' + [int]($rc.Y + $rc.Height / 2))
}

Write-Output ''
Write-Output '--- 1. lens capture: byte order and agreement with GetPixel (5 reads each) ---'
foreach ($p in $points) {
    $reports = @()
    for ($i = 0; $i -lt 5; $i++) { $reports += [L]::Report($p[0], $p[1], 19) }
    Write-Output ("  point " + $p[0] + "," + $p[1] + ":")
    foreach ($r in ($reports | Select-Object -Unique)) { Write-Output ("    " + $r) }
    $consistent = (@($reports | Select-Object -Unique).Count -eq 1)
    Check ("  stable across 5 reads at " + $p[0] + "," + $p[1]) $consistent
}

$last = $reports[-1]
Check '  captured bytes agree with GetPixel as BGR (what Bgra32 expects)' ($last -like '*BYTES ARE BGR ORDER*')

Write-Output ''
Write-Output '--- 2. picking mode with the lens starts and cancels cleanly ---'
$pickBtn = $null
foreach ($b in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)) {
    if ($b.Current.Name -like '*Pick a color*') { $pickBtn = $b; break }
}
$pattern = $pickBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
$pattern.Invoke()
Start-Sleep -Milliseconds 700

$widget = [L]::FindWindow($null, 'ChromaWidget')
Check 'widget (with the lens) appears when picking starts' ([L]::IsWindow($widget))
$overlay = [L]::FindWindow($null, 'ChromaPickingOverlay')
Check 'picking overlay appears' ([L]::IsWindow($overlay))

# Sweep the lens over a few positions (exercises the off-screen clamp too),
# then cancel with a right-click so the clipboard is never modified.
[void][L]::Aim($overlay, 700, 400)
[void][L]::Aim($overlay, 10, 10)
[void][L]::Aim($overlay, 1600, 1000)
Start-Sleep -Milliseconds 400
Check 'right-click through the overlay cancels picking' (-not [L]::IsWindow($widget))
Check 'overlay is destroyed after cancelling' (-not [L]::IsWindow($overlay))
$proc.Refresh()
Check 'app process still running' (-not $proc.HasExited)

Write-Output ''
if ($fail -gt 0) { Write-Output ("RESULT: " + $fail + " FAILED"); exit 1 } else { Write-Output 'RESULT: all checks passed' }
