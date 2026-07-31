# Diagnose caption-button clicks: WM_NCHITTEST code + a real mouse click.
# HTCLIENT(1) = WPF gets the input; HTCAPTION(2) = Windows swallows it for drag.

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class D
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);

    public static string Hit(int x, int y)
    {
        IntPtr r = SendMessage(GetForegroundSafe(), 0x0084, IntPtr.Zero, (IntPtr)((y << 16) | (x & 0xFFFF)));
        return r.ToString();
    }
    static IntPtr _h;
    public static void SetH(IntPtr h) { _h = h; }
    static IntPtr GetForegroundSafe() { return _h; }
    public static string Desc(IntPtr h)
    {
        var sb = new StringBuilder(512);
        GetWindowText(h, sb, 512);
        uint pid; GetWindowThreadProcessId(h, out pid);
        return "hwnd=0x" + h.ToInt64().ToString("X") + " pid=" + pid + " title='" + sb + "'";
    }
}
"@

# physical-pixel coordinates for this process, so rects match the PerMonitorV2 app
if (-not [D]::SetProcessDpiAwarenessContext([IntPtr](-4))) { [void][D]::SetProcessDPIAware() }

function HitName([int]$code) {
    switch ([int]$code) {
        1  { 'HTCLIENT' }
        2  { 'HTCAPTION' }
        -1 { 'HTTRANSPARENT/NOWHERE' }
        10 { 'HTLEFT' } 11 { 'HTRIGHT' } 12 { 'HTTOP' } 13 { 'HTTOPLEFT' }
        14 { 'HTTOPRIGHT' } 15 { 'HTBOTTOM' } 16 { 'HTBOTTOMLEFT' } 17 { 'HTBOTTOMRIGHT' }
        default { "code=$code" }
    }
}

$exe = Join-Path $PSScriptRoot 'bin\Debug\net8.0-windows\ColorPicker.exe'
$proc = Start-Process -FilePath $exe -PassThru
Start-Sleep -Milliseconds 1800
$proc.Refresh()
$hwnd = $proc.MainWindowHandle
Write-Output ("app pid=" + $proc.Id + " hwnd=0x" + $hwnd.ToInt64().ToString("X"))

$r = New-Object D+RECT
[void][D]::GetWindowRect($hwnd, [ref]$r)
Write-Output ("window rect L,T,R,B = " + $r.Left + "," + $r.Top + "," + $r.Right + "," + $r.B)
$W = $r.Right; $T = $r.Top

[D]::SetH($hwnd)

# --- sanity probes: results only make sense if screen coords are right ---
function Probe([int]$x, [int]$y, [string]$label) {
    $code = [D]::SendMessage($hwnd, 0x0084, [IntPtr]::Zero, [IntPtr](($y -shl 16) -bor ($x -band 0xFFFF)))
    $pt = New-Object D+POINT; $pt.X = $x; $pt.Y = $y
    $over = [D]::WindowFromPoint($pt)
    Write-Output ("  {0,-28} ({1},{2}) -> {3}   |under: {4}" -f $label, $x, $y, (HitName $code), ([D]::Desc($over)))
}

Write-Output '--- WM_NCHITTEST probes (screen coords) ---'
Probe ($r.Left + 400) ($T + 300) 'client area (expect HTCLIENT)'
Probe ($r.Left + 3)   ($T + 300) 'left resize border (expect HTLEFT)'
Probe ($r.Left + 400) ($T + 26)  'caption, far from buttons'
Probe ($W - 24)       ($T + 26)  'close button area'
Probe ($W - 72)       ($T + 26)  'maximize button area'
Probe ($W - 120)      ($T + 26)  'minimize button area'

# --- enumerate real UIA buttons + rects ---
Write-Output '--- UIA buttons (name @ rect) ---'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if ($win) {
    $btnCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    $btns = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
    foreach ($b in $btns) {
        $rc = $b.Current.BoundingRectangle
        Write-Output ("  '{0}'  x={1} y={2} w={3} h={4}" -f $b.Current.Name, [int]$rc.X, [int]$rc.Y, [int]$rc.Width, [int]$rc.Height)
    }
}

# --- real hardware-style click on the MINIMIZE button ---
$minX = $W - 120; $minY = $T + 26
Write-Output ("--- SendInput click on minimize at (" + $minX + "," + $minY + ") ---")
$before = [D]::Desc($hwnd)
[void][D]::SetCursorPos($minX, $minY)
Start-Sleep -Milliseconds 120
[D]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)   # LEFTDOWN
Start-Sleep -Milliseconds 60
[D]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)   # LEFTUP
Start-Sleep -Milliseconds 700
[void][D]::GetWindowRect($hwnd, [ref]$r)
Write-Output ("after click: IsIconic=" + [D]::IsIconic($hwnd) + " IsZoomed=" + [D]::IsZoomed($hwnd) + " rect=" + $r.Left + "," + $r.Top + "," + $r.Right + "," + $r.B)
if ([D]::IsIconic($hwnd)) { [void][D]::ShowWindow($hwnd, 9) }  # SW_RESTORE

Write-Output 'done'
