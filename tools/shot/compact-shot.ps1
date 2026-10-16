param([string]$OutPath)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Ui {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
[Ui]::SetProcessDPIAware() | Out-Null
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName System.Windows.Forms

$proc = Get-Process AIRadio -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { Write-Output "NO_WINDOW"; exit 1 }
$hwnd = $proc.MainWindowHandle

# find and invoke the compact toggle button by automation name
$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
$nameCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "简洁模式")
$btn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nameCond)
if (-not $btn) { Write-Output "NO_COMPACT_BUTTON"; exit 1 }
$invoke = $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
$invoke.Invoke()
Write-Output "INVOKED compact toggle"
Start-Sleep -Seconds 3

# fresh rect after shrink
$rect = New-Object Ui+RECT
[Ui]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
$w = $rect.Right - $rect.Left; $h = $rect.Bottom - $rect.Top
Write-Output ("NEW_RECT: " + $rect.Left + "," + $rect.Top + " " + $w + "x" + $h)

# verify compact UI via UIA: dump first texts
$root2 = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
$texts = $root2.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
$i = 0
foreach ($t in $texts) {
  $n = $t.Current.Name
  if ($n -and $n.Trim().Length -gt 0 -and $n -notmatch '^[\.\s]+$') { Write-Output ("TEXT: " + $n); $i++; if ($i -ge 10) { break } }
}

$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$g.Dispose()
$bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output ("SAVED: " + $OutPath)

# restore standard mode via Esc key (needs foreground)
[Ui]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 600
[System.Windows.Forms.SendKeys]::SendWait("{ESC}")
Start-Sleep -Milliseconds 800
Write-Output "ESC_SENT"
