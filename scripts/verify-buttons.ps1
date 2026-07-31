# Verifies the caption buttons on the fixed build.
#
# SAFETY: every message is sent to Chroma's own hwnd with SendMessage and every
# state call targets Chroma's own process. Nothing moves the cursor and nothing
# is injected into the global input stream, so nothing here can click another
# app. The two halves of a real click are exercised separately and both must be
# true for a physical click to work:
#   1. WM_NCHITTEST says HTCLIENT   -> Windows routes the mouse into the client
#                                      area instead of treating it as drag area
#   2. WM_LBUTTONDOWN + WM_LBUTTONUP at that point -> WPF hit-tests the button
#                                      and fires its Click handler

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class V
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
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);

    public static string Desc(IntPtr h)
    {
        if (h == IntPtr.Zero) return "<none>";
        var sb = new StringBuilder(512);
        GetWindowText(h, sb, 512);
        uint p; GetWindowThreadProcessId(h, out p);
        return "pid=" + p + " title='" + sb + "'";
    }

    // Client coordinates, as WPF expects them on a routed mouse message.
    public static void MoveDownUpClient(IntPtr h, int cx, int cy)
    {
        IntPtr lp = (IntPtr)((cy << 16) | (cx & 0xFFFF));
        SendMessage(h, 0x0200, IntPtr.Zero, lp);     // WM_MOUSEMOVE (hover state)
        SendMessage(h, 0x0201, (IntPtr)0x0001, lp);  // WM_LBUTTONDOWN, MK_LBUTTON
        SendMessage(h, 0x0202, IntPtr.Zero, lp);     // WM_LBUTTONUP
    }
}
"@

if (-not [V]::SetProcessDpiAwarenessContext([IntPtr](-4))) { [void][V]::SetProcessDPIAware() }

function HitName([int]$code) {
    switch ($code) {
        1  { 'HTCLIENT' }  2  { 'HTCAPTION' }  -1 { 'HTNOWHERE' }
        10 { 'HTLEFT' } 11 { 'HTRIGHT' } 12 { 'HTTOP' } 13 { 'HTTOPLEFT' }
        14 { 'HTTOPRIGHT' } 15 { 'HTBOTTOM' } 16 { 'HTBOTTOMLEFT' } 17 { 'HTBOTTOMRIGHT' }
        default { "code=$code" }
    }
}

$script:pass = 0; $script:fail = 0
function Check([string]$label, [bool]$ok) {
    if ($ok) { $script:pass++; Write-Output ("  [PASS] " + $label) }
    else     { $script:fail++; Write-Output ("  [FAIL] " + $label) }
}

function Start-App {
    $p = Get-Process -Name ColorPicker -ErrorAction SilentlyContinue
    if ($p) { return $p }
    $p = Start-Process -FilePath 'C:\Users\User\Desktop\ColorPicker\bin\Debug\net8.0-windows\ColorPicker.exe' -PassThru
    Start-Sleep -Milliseconds 1800
    $p.Refresh()
    return $p
}

$proc = Start-App
$hwnd = $proc.MainWindowHandle
Write-Output ("app pid=" + $proc.Id + " hwnd=0x" + $hwnd.ToInt64().ToString("X"))
if ($hwnd -eq [IntPtr]::Zero) { Write-Output 'no main window'; exit 1 }

# UIA gives the true on-screen button centres; GetWindowRect covers the rest.
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$proc.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
$btnCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Button)

$caption = @()
foreach ($b in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)) {
    $rc = $b.Current.BoundingRectangle
    if ($rc.Height -ge 40 -and ($rc.Y + $rc.Height) -lt 220) {
        $caption += [pscustomobject]@{
            Name = ($b.Current.Name -replace '[^\x20-\x7E]', '.')
            X    = [int]($rc.X + $rc.Width / 2)
            Y    = [int]($rc.Y + $rc.Height / 2)
        }
    }
}
$caption = @($caption | Sort-Object X)
Write-Output ('caption buttons found: ' + (($caption | ForEach-Object { "'$($_.Name)'" }) -join ' '))
if ($caption.Count -ne 3) { Write-Output 'expected 3 caption buttons'; exit 1 }
$min = $caption[0]; $max = $caption[1]; $close = $caption[2]

$r = New-Object V+RECT
function HitTest([int]$x, [int]$y) {
    return [int][V]::SendMessage($hwnd, 0x0084, [IntPtr]::Zero,
               [IntPtr](($y -shl 16) -bor ($x -band 0xFFFF)))
}

Write-Output ''
Write-Output '--- 1. hit testing (what Windows does with a real click) ---'
foreach ($b in @($min, $max, $close)) {
    $code = HitTest $b.X $b.Y
    Check ("'" + $b.Name + "' centre is HTCLIENT, not HTCAPTION") ((HitName $code) -eq 'HTCLIENT')
}
Check 'title bar background is client space (drag now done by DragMove)' `
    ((HitName (HitTest ($close.X - 250) $close.Y)) -eq 'HTCLIENT')
[void][V]::GetWindowRect($hwnd, [ref]$r)
Check 'left edge still resizes (HTLEFT)' ((HitName (HitTest ($r.Left + 3) ([int](($r.Top + $r.Bottom) / 2)))) -eq 'HTLEFT')

# The WPF client area is inset from the window rect by the chrome frame, so
# screen -> client needs the real client origin, not the window's top-left.
$origin = New-Object V+POINT; $origin.X = 0; $origin.Y = 0
[void][V]::ClientToScreen($hwnd, [ref]$origin)
$cr = New-Object V+RECT
[void][V]::GetClientRect($hwnd, [ref]$cr)
Write-Output ("client origin on screen = " + $origin.X + "," + $origin.Y +
    "  client size = " + $cr.Right + "x" + $cr.Bottom)
function Click([object]$btn) {
    [V]::MoveDownUpClient($hwnd, $btn.X - $origin.X, $btn.Y - $origin.Y)
}

Write-Output ''
Write-Output '--- 2. MINIMIZE button ---'
Click $min
Start-Sleep -Milliseconds 400
Check 'clicking minimize minimizes the window' ([V]::IsIconic($hwnd))
[void][V]::ShowWindow($hwnd, 9); Start-Sleep -Milliseconds 300   # SW_RESTORE
Check 'window restored for the next test' (-not [V]::IsIconic($hwnd))

Write-Output ''
Write-Output '--- 3. MAXIMIZE button ---'
[void][V]::ShowWindow($hwnd, 3); Start-Sleep -Milliseconds 400   # SW_MAXIMIZE
Check 'window can be maximized (baseline)' ([V]::IsZoomed($hwnd))
[void][V]::GetWindowRect($hwnd, [ref]$r)
Check 'maximized caption stays HTCLIENT' ((HitName (HitTest $max.X $max.Y)) -eq 'HTCLIENT')
Click $max
Start-Sleep -Milliseconds 400
Check 'clicking maximize restores the window' (-not [V]::IsZoomed($hwnd))

Write-Output ''
Write-Output '--- 4. CLOSE button ---'
Click $close
Start-Sleep -Milliseconds 800
Check 'clicking close exits the app' ((-not [V]::IsWindow($hwnd)) -and
    (-not (Get-Process -Name ColorPicker -ErrorAction SilentlyContinue)))

Write-Output ''
Write-Output ("RESULT: " + $script:pass + " passed, " + $script:fail + " failed")
if ($script:fail -gt 0) { exit 1 } else { exit 0 }
