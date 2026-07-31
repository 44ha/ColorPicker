# READ-ONLY diagnosis of caption-button hit testing.
# Only reads window rects / UIA rects and sends WM_NCHITTEST to Chroma's own hwnd.
# It never moves the cursor, never injects input, never touches other apps.

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class H
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

    public static string Desc(IntPtr h)
    {
        if (h == IntPtr.Zero) return "<none>";
        var sb = new StringBuilder(512);
        GetWindowText(h, sb, 512);
        uint pid; GetWindowThreadProcessId(h, out pid);
        return "pid=" + pid + " title='" + sb + "'";
    }
}
"@

# physical pixels, same space the PerMonitorV2 app reports its window in
if (-not [H]::SetProcessDpiAwarenessContext([IntPtr](-4))) { [void][H]::SetProcessDPIAware() }

function HitName([int]$code) {
    switch ($code) {
        1  { 'HTCLIENT   <- WPF receives it' }
        2  { 'HTCAPTION  <- Windows swallows it (drag only)' }
        -1 { 'HTNOWHERE/TRANSPARENT' }
        10 { 'HTLEFT' } 11 { 'HTRIGHT' } 12 { 'HTTOP' } 13 { 'HTTOPLEFT' }
        14 { 'HTTOPRIGHT' } 15 { 'HTBOTTOM' } 16 { 'HTBOTTOMLEFT' } 17 { 'HTBOTTOMRIGHT' }
        99 { '<no answer>' }
        default { "code=$code" }
    }
}

$exe = 'C:\Users\User\Desktop\ColorPicker\bin\Debug\net8.0-windows\ColorPicker.exe'
$proc = Get-Process -Name ColorPicker -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) {
    $proc = Start-Process -FilePath $exe -PassThru
    Start-Sleep -Milliseconds 1800
    $proc.Refresh()
}
$hwnd = $proc.MainWindowHandle
Write-Output ("app pid=" + $proc.Id + " hwnd=0x" + $hwnd.ToInt64().ToString("X"))
if ($hwnd -eq [IntPtr]::Zero) { Write-Output 'no main window - abort'; exit 1 }

$r = New-Object H+RECT
[void][H]::GetWindowRect($hwnd, [ref]$r)
$W = $r.Right; $B = $r.Bottom
$R = $r; $L = $r.Left; $T = $r.Top
Write-Output ("window rect L,T,R,B = $L,$T,$W,$B")

function Probe([int]$x, [int]$y, [string]$label) {
    # WM_NCHITTEST: LOWORD/HIWORD of lParam = coordinates. Try screen first, then client.
    $lpScreen = [IntPtr](($y -shl 16) -bor ($x -band 0xFFFF))
    $cx = $x - $L; $cy = $y - $T
    $lpClient = [IntPtr](($cy -shl 16) -bor ($cx -band 0xFFFF))
    $s = [H]::SendMessage($hwnd, 0x0084, [IntPtr]::Zero, $lpScreen)
    $c = [H]::SendMessage($hwnd, 0x0084, [IntPtr]::Zero, $lpClient)
    $pt = New-Object H+POINT; $pt.X = $x; $pt.Y = $y
    $under = [H]::Desc([H]::WindowFromPoint($pt))
    Write-Output ("  {0,-24} ({1},{2})  screen-> {3,-34} client-> {4,-20} | topmost: {5}" -f `
        $label, $x, $y, (HitName ([int]$s)), (HitName ([int]$c)), $under)
}

Write-Output '--- WM_NCHITTEST (sent only to our own hwnd) ---'
Probe ($L + [int](($W - $L) / 2)) ($T + 300) 'client area'
Probe ($L + 3) ($T + 300) 'left resize border'
Probe ($W - 250) ($T + 26) 'caption away from buttons'
Probe ($W - 24)  ($T + 26) 'CLOSE button centre'
Probe ($W - 72)  ($T + 26) 'MAXIMIZE button centre'
Probe ($W - 120) ($T + 26) 'MINIMIZE button centre'

Write-Output '--- UIA buttons (authoritative element rects) ---'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$proc.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if ($win) {
    $btnCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    foreach ($b in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)) {
        $rc = $b.Current.BoundingRectangle
        $bc = New-Object System.Windows.Automation.Rect
        # Probe the hit-test at the exact element centre (read-only).
        $cx = [int]($rc.X + $rc.Width / 2)
        $cy = [int]($rc.Y + $rc.Height / 2)
        $s = [H]::SendMessage($hwnd, 0x0084, [IntPtr]::Zero,
              [IntPtr](($cy -shl 16) -bor ($cx -band 0xFFFF)))
        Write-Output ("  '{0,-18}' centre=({1},{2}) size={3}x{4}  nchittest={5}" -f `
            ($b.Current.Name -replace '[^\x20-\x7E]', '.'), $cx, $cy,
            [int]$rc.Width, [int]$rc.Height, (HitName ([int]$s)))
    }
} else {
    Write-Output '  (UIA could not find the window)'
}

Write-Output 'done (no input was injected)'
