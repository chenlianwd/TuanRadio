Add-Type -AssemblyName UIAutomationClient
$proc = Get-Process AIRadio -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { Write-Output "NO_WINDOW"; exit 1 }
$root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
$closeCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "关闭")
$btn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $closeCond)
if ($btn) {
  ($btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
  Write-Output "CLOSE_INVOKED"
  Start-Sleep -Seconds 5
}
$p = Get-Process AIRadio -ErrorAction SilentlyContinue
if ($p) { Write-Output "STILL_RUNNING" } else { Write-Output "EXITED" }
