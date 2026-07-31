Add-Type -AssemblyName UIAutomationClient
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class Q
{
    public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int m);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr WindowFromPoint(POINT p);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    public static extern int GetWindowLong(IntPtr h, int i);

    public static string DescribeAt(int x, int y)
    {
        IntPtr h = WindowFromPoint(new POINT { X = x, Y = y });
        uint pid; GetWindowThreadProcessId(h, out pid);
        var sb = new System.Text.StringBuilder(256); GetWindowText(h, sb, 256);
        int ex = GetWindowLong(h, -20);
        return string.Format("hwnd=0x{0:X} pid={1} topmost={2} exstyle=0x{3:X8} title='{4}'",
            h.ToInt64(), pid, ((ex & 0x00000008) != 0), ex, sb.ToString());
    }

    public static string DescribeWindow(IntPtr h)
    {
        uint pid; GetWindowThreadProcessId(h, out pid);
        var sb = new System.Text.StringBuilder(256); GetWindowText(h, sb, 256);
        int ex = GetWindowLong(h, -20);
        return string.Format("hwnd=0x{0:X} pid={1} topmost={2} exstyle=0x{3:X8} title='{4}'",
            h.ToInt64(), pid, ((ex & 0x00000008) != 0), ex, sb.ToString());
    }
}
"@

$proc = Get-Process ColorPicker -ErrorAction Stop
$hwnd = $proc.MainWindowHandle
$pid_ = [uint32]$proc.Id
Write-Output ('app pid: ' + $pid_)

$rChroma = New-Object Q+RECT
[void][Q]::GetWindowRect($hwnd, [ref]$rChroma)
$clL=$rChroma.L; $clT=$rChroma.T; $clR=$rChroma.R; $clB=$rChroma.B
Write-Output ('chroma rect (L,T,R,B)=(' + $rChroma.L + ',' + $rChroma.T + ',' + $rChroma.R + ',' + $rChroma.B + ')')

# Where caption buttons sit: UIA earlier placed them at screen y=125-126, but that
# assumed window at (0,0) with a 52-height caption. Instead, recompute from the real
# chroma rect. Caption buttons are inside the caption band at bottom of it, right side.
# The buttons have Height 65 (CaptionButtonStyle)?? Actually CaptionButtonStyle Height=52,
# but with the WindowChrome CaptionHeight=52 they're 52 px tall. The buttons are placed
# at the right edge with width 48 each, so: close at R-96..R-48, maximize at R-144..R-96,
# minimize at R-192..R-144? No — ordering in xaml is minimize,maximize,close left-to-right,
# so minimize at R-192..R-144?? Actually XAML order is minimize, maximize, close, so:
#   minimize: R-144..R-96
#   maximize: R-96..R-48
#   close:    R-48..R
# Each button Width=48 in CaptionButtonStyle, so close = R-48..R, maximize = R-96..R-48,
# minimize = R-144..R-96. CaptionHeight=52 so y = T .. T+52. UIA saw them at y=126 on
# screen -> that means T=74? We'll probe both theories: caption region and buttons.

function ProbeCaptionRegion {
    param([int]$L, [int]$T, [int]$R, [int]$B)
    $bx = $R - 24
    Write-Output '--- probing caption button pixels (close ~ R-24, along caption band) ---'
    foreach ($by in @(0,5,10,15,20,25,28,30,32,34,36,38,40,42,44,46,48,50,52,55,60,65)) {
        $y = $T + $by
        $info = [Q]::DescribeAt($bx, $y)
        Write-Output ('  (' + $bx + ',' + $y + ') -> ' + $info)
    }
    # rect of whatever owns close-like point
    $pt = New-Object Q+POINT
    $pt.X = $bx; $pt.Y = $T + 46
    $owner = [Q]::WindowFromPoint($pt)
    $ownerR = New-Object Q+RECT
    [void][Q]::GetWindowRect($owner, [ref]$ownerR)
    $ownerInfo = [Q]::DescribeAt($bx, $T+46)
    Write-Output '--- owner of close-like point ---'
    Write-Output '  ' + $ownerInfo
    Write-Output ('  owner rect (L,T,R,B)=(' + $ownerR.L + ',' + $ownerR.T + ',' + $ownerR.R + ',' + $ownerR.B + ')')
    $sb = New-Object System.Text.StringBuilder(256)
    [void][Q]::GetWindowText($owner, $sb, 256)
    Write-Output '  owner title: ' + $sb.ToString()
}

ProbeCaptionRegion $rChroma.L $rChroma.T $rChroma.R $rChroma.B
exit 0
