# Read-only diagnostic: invoke the pick button, then dump every top-level
# window the app owns (title + class + visibility) so we can see what happened.
# Sends no input anywhere; only UIA on Chroma's own process.

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class W
{
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);

    public static string[] WindowsOf(uint targetPid)
    {
        var result = new List<string>();
        EnumWindows((h, l) =>
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == targetPid)
            {
                var t = new StringBuilder(512); GetWindowText(h, t, 512);
                var c = new StringBuilder(512); GetClassName(h, c, 512);
                result.Add(string.Format("hwnd=0x{0:X} visible={1,-5} class='{2}' title='{3}'",
                    h.ToInt64(), IsWindowVisible(h), c, t));
            }
            return true;
        }, IntPtr.Zero);
        return result.ToArray();
    }
}
"@

$proc = Get-Process -Name ColorPicker -ErrorAction SilentlyContinue
if (-not $proc) { Write-Output 'app not running'; exit 1 }
$pidApp = $proc.Id
Write-Output ("app pid=" + $pidApp)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$proc.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
$btnCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Button)

$btn = $null
foreach ($b in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)) {
    if ($b.Current.Name -like '*Pick a color*') { $btn = $b; break }
}
if (-not $btn) { Write-Output 'no pick button'; exit 1 }

Write-Output ('invoking: ' + $btn.Current.Name)
$btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 900

Write-Output '--- top-level windows of the app after Invoke ---'
foreach ($line in [W]::WindowsOf($pidApp)) { Write-Output ('  ' + $line) }

$proc.Refresh()
Write-Output ('exited=' + $proc.HasExited + '  responding=' + $proc.Responding)
Write-Output '--- app log ---'
$log = 'C:\Users\User\Desktop\ColorPicker\bin\Debug\net8.0-windows\app-run.log'
if (Test-Path $log) { Get-Content $log } else { Write-Output '(no log)' }
