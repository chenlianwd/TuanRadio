param([string]$OutTop, [string]$OutBottom)
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

function Get-Md5([string]$path) {
  $md5 = [System.Security.Cryptography.MD5]::Create()
  $fs = [System.IO.File]::OpenRead($path)
  $hash = $md5.ComputeHash($fs)
  $fs.Close()
  return ([System.BitConverter]::ToString($hash) -replace '-', '')
}

function Capture-Window($hwnd, $path) {
  $rect = New-Object Ui+RECT
  [Ui]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
  $w = $rect.Right - $rect.Left; $h = $rect.Bottom - $rect.Top
  $bmp = New-Object System.Drawing.Bitmap($w, $h)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
  $g.Dispose()
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  return "$w x $h"
}

# capture with liveness canary: two shots 2.5s apart must differ, else stale frame -> retry
function Capture-Live($hwnd, $path, $tmpA, $tmpB) {
  for ($attempt = 1; $attempt -le 6; $attempt++) {
    Capture-Window $hwnd $tmpA | Out-Null
    Start-Sleep -Milliseconds 2500
    Capture-Window $hwnd $tmpB | Out-Null
    $ha = Get-Md5 $tmpA; $hb = Get-Md5 $tmpB
    if ($ha -ne $hb) {
      Copy-Item $tmpB $path -Force
      Write-Output ("LIVE-CAPTURE ok (attempt " + $attempt + ")")
      return $true
    }
    Write-Output ("STALE-FRAME attempt " + $attempt + ", waiting...")
    Start-Sleep -Seconds 4
  }
  return $false
}

$proc = Get-Process AIRadio -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { Write-Output "NO_WINDOW"; exit 1 }
$hwnd = $proc.MainWindowHandle
[Ui]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 600

# open settings
$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
$nameCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "设置")
$btn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nameCond)
if (-not $btn) { Write-Output "NO_SETTINGS_BUTTON"; exit 1 }
($btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
Write-Output "INVOKED settings"
Start-Sleep -Seconds 2
[Ui]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 400

# locate settings panel scrollviewer = scrollable ancestor of the AI service header text
$root2 = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
$aiCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "AI 服务")
$aiHeader = $root2.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $aiCond)
if (-not $aiHeader) { Write-Output "SETTINGS_NOT_OPEN"; exit 1 }
Write-Output "SETTINGS_OPEN confirmed (AI service header found)"

$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
$node = $aiHeader
$scroller = $null
for ($i = 0; $i -lt 15 -and $node; $i++) {
  $node = $walker.GetParent($node)
  if (-not $node) { break }
  try {
    if ($node.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)) { $scroller = $node; break }
  } catch { }
}
if (-not $scroller) { Write-Output "NO_SETTINGS_SCROLLVIEWER"; exit 1 }
$sp = $scroller.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
Write-Output ("SCROLLER found, vert=" + $sp.Current.VerticallyScrollable + " pct=" + $sp.Current.VerticalScrollPercent)

# top capture with canary
$tmpA = Join-Path $env:TEMP "shot-canary-a.png"
$tmpB = Join-Path $env:TEMP "shot-canary-b.png"
$ok = Capture-Live $hwnd $OutTop $tmpA $tmpB
if (-not $ok) { Write-Output "GAVE_UP_TOP"; exit 1 }

# scroll settings to bottom
$last = -1.0
for ($i = 0; $i -lt 12; $i++) {
  $sp.Scroll([System.Windows.Automation.ScrollAmount]::NoAmount, [System.Windows.Automation.ScrollAmount]::LargeIncrement)
  Start-Sleep -Milliseconds 700
  $pct = $sp.Current.VerticalScrollPercent
  if ([math]::Abs($pct - $last) -lt 0.5) { break }
  $last = $pct
  if ($pct -ge 99.5) { break }
}
Write-Output ("SCROLLED pct=" + $sp.Current.VerticalScrollPercent)

$ok2 = Capture-Live $hwnd $OutBottom $tmpA $tmpB
if (-not $ok2) { Write-Output "GAVE_UP_BOTTOM"; exit 1 }

# ensure bottom differs from top (scroll actually moved view)
$ht = Get-Md5 $OutTop; $hb = Get-Md5 $OutBottom
if ($ht -eq $hb) { Write-Output "WARN: top==bottom (scroll may not have moved)" } else { Write-Output "TOP_BOTTOM_DIFFER ok" }

# close settings
$closeCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "关闭设置")
$closeBtn = $root2.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $closeCond)
if ($closeBtn) { ($closeBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); Write-Output "CLOSED settings" }
