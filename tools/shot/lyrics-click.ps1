param([string]$OutPath, [switch]$NoToggleBack)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Ui {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
[Ui]::SetProcessDPIAware() | Out-Null
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient

$proc = Get-Process AIRadio -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { Write-Output "NO_WINDOW"; exit 1 }
$hwnd = $proc.MainWindowHandle
$rect = New-Object Ui+RECT
[Ui]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
$w = $rect.Right - $rect.Left; $h = $rect.Bottom - $rect.Top

$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
$texts = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
$clockEl = $null; $bestH = 0.0
foreach ($t in $texts) {
  $n = $t.Current.Name
  if ($n -and $n -match '^[0-9]{2}:[0-9]{2}$') {
    $r = $t.Current.BoundingRectangle
    if ($r.Height -gt $bestH) { $bestH = $r.Height; $clockEl = $t }
  }
}
if (-not $clockEl) { Write-Output "NO_CLOCK_ELEMENT"; exit 1 }
$r2 = $clockEl.Current.BoundingRectangle
$cx = [int]($r2.X + $r2.Width / 2); $cy = [int]($r2.Y + $r2.Height / 2)
Write-Output ("CLOCK_RECT: " + $r2.ToString() + " CLICK: " + $cx + "," + $cy)

[Ui]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 500
[Ui]::SetCursorPos($cx, $cy) | Out-Null
Start-Sleep -Milliseconds 300
[Ui]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 100
[Ui]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Seconds 3

# verify lyrics layer active: current-line text appears (center, large font) - dump texts again
$root2 = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
$texts2 = $root2.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
$found = $false
foreach ($t in $texts2) {
  $n = $t.Current.Name
  if ($n -and $n -match '^[0-9]{2}:[0-9]{2}$') { $found = $true }
}
if ($found) { Write-Output "WARN: clock text still present - lyrics may not be active" } else { Write-Output "LYRICS_ACTIVE (clock text gone)" }

$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$g.Dispose()
$bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output ("SAVED: " + $OutPath)

if (-not $NoToggleBack) {
  [Ui]::SetCursorPos($cx, $cy) | Out-Null
  Start-Sleep -Milliseconds 300
  [Ui]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 100
  [Ui]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 800
  Write-Output "TOGGLED_BACK"
}
