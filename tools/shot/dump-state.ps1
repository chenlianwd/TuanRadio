Add-Type @"
using System.Runtime.InteropServices;
public class DpiHelper { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); }
"@
[DpiHelper]::SetProcessDPIAware() | Out-Null
Add-Type -AssemblyName UIAutomationClient
$proc = Get-Process AIRadio -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { Write-Output "NO_WINDOW"; exit 1 }
$root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
Write-Output ("RECT: " + $root.Current.BoundingRectangle.ToString())
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
$texts = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
foreach ($t in $texts) {
  $n = $t.Current.Name
  if ($n -and $n.Trim().Length -gt 0) { Write-Output ("TEXT: " + $n) }
}
$btnCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
$btns = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
foreach ($b in $btns) {
  $n = $b.Current.Name
  if ($n) { Write-Output ("BTN: " + $n) }
}
