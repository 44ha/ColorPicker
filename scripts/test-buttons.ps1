Add-Type -AssemblyName UIAutomationClient
Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class P
{
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int m);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] public static extern uint GetPixel(IntPtr hdc, int x, int y);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT p);
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    public static IntPtr WhichWindowAt(int x, int y)
    {
        IntPtr h = WindowFromPoint(new POINT { X = x, Y = y });
        uint pid; GetWindowThreadProcessId(h, out pid);
        return h;
    }

    public static string DescribeAt(int x, int y)
    {
        IntPtr h = WindowFromPoint(new POINT { X = x, Y = y });
        uint pid; GetWindowThreadProcessId(h, out pid);
        var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
        return string.Format("hwnd=0x{0:X} pid={1} topmost={2} title='{3}'",
            h.ToInt64(), pid, ((GetWindowLong(h, -20) & 0x00000008) != 0), sb.ToString());
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    static extern int GetWindowLong(IntPtr h, int i);

    public static bool IsIconic(IntPtr h) { return IsIconic(h); }  // no-op guard
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);

    public static void RectOf(IntPtr h, out RECT r) { r = new RECT(); GetWindowRect(h, out r); }
    public static string PidAtPoint(int x, int y)
    {
        IntPtr h = WindowFromPoint(new POINT { X = x, Y = y });
        uint p; GetWindowThreadProcessId(h, out p);
        var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
        return string.Format("hwnd=0x{0:X} pid={1} topmost={2} title='{3}' color={4:X6}",
            h.ToInt64(), p, ((GetWindowLong(h, -20) & 0x00000008) != 0), sb.ToString(),
            GetPixel(GetDC(IntPtr.Zero), x, y));
    }
}
"@
$proc = Get-Process ColorPicker -ErrorAction Stop
$hwnd = $proc.MainWindowHandle
$pid_ = [uint32]$proc.Id
Write-Output ('app pid: ' + $pid_)
$rOut = New-Object "System.Runtime.InteropServices.RECT[]" 1
[void][P]::RectOf($hwnd, [ref]$rOut[0])
$base = $rOut[0]
Write-Output ('chroma window rect (L,T,R,B): ' + $base.L + ',' + $base.T + ',' + $base.R + ',' + $base.B)

# Measure target click coordinates the user would actually click — caption area button perches.
# From UIA the caption buttons live around y=126 within the window, so map the window-relative
# y back to screen: the clicked point is at window-y = base.T + (126-base.T)?? -> use absolute.
# Earlier UIA saw caption buttons at y=125..126 on screen; verify now:
foreach ($yGuess in @(125, 126, 127, 128)) {
    $info = [P]::PidAtPoint(($base.R - 24), $yGuess)
    Write-Output ("click-point (R-24,$yGuess) -> $info")
}

# What window is actually covering (R-24,126)?
Write-Output '--- What covers the caption button when user clicks? ---'
$cover = [P]::WhichWindowAt($base.R - 24, 126)
$coverPid = 0
[void][P]::GetWindowThreadProcessId($cover, [ref]$coverPid)
if ($coverPid -ne $pid_) {
    Write-Output ("WARNING: point owned by pid $coverPid, NOT chroma ($pid_)")
    $tInfo = [P]::DescribeAt($base.R - 24, 126)
    Write-Output ('covering window description: ' + $tInfo)
} else {
    Write-Output 'point is owned by chroma'
}

# Rectangle of the covering window
$r2 = New-Object "System.Runtime.InteropServices.RECT[]" 1
$p2 = 0
[void][P]::GetWindowThreadProcessId($cover, [ref]$p2)
[P]::RectOf($cover, [ref]$r2)
Write-Output ('covering window rect (L,T,R,B): ' + $r2[0].L + ',' + $r2[0].T + ',' + $r2[0].R + ',' + $r2[0].B)
Write-Output ('covering window pid: ' + $p2)
$wsb = New-Object System.Text.StringBuilder(256)
[void][P]::GetWindowText($cover, $wsb, 256)
Write-Output ('covering window title: ' + $wsb.ToString())
exit 0
