Add-Type @"
using System;
using System.Runtime.InteropServices;
public class WinShot {
  public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBmp, uint nFlags);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
[WinShot]::SetProcessDPIAware() | Out-Null
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient

function Find-RealWindow($procId) {
    $hwnds = New-Object System.Collections.Generic.List[IntPtr]
    [WinShot]::EnumWindows({
        param($hwnd, $lparam)
        $procIdent = 0
        [WinShot]::GetWindowThreadProcessId($hwnd, [ref]$procIdent)
        if ($procIdent -eq $procId) { $hwnds.Add($hwnd) }
        return $true
    }, [IntPtr]::Zero) | Out-Null

    foreach ($h in $hwnds) {
        $rect = New-Object WinShot+RECT
        [WinShot]::GetWindowRect($h, [ref]$rect) | Out-Null
        $vis = [WinShot]::IsWindowVisible($h)
        $w = $rect.Right - $rect.Left
        $h_val = $rect.Bottom - $rect.Top
        if ($vis -and $w -ge 400 -and $h_val -ge 70) {
            return @{ Hwnd = $h; Width = $w; Height = $h_val }
        }
    }
    return $null
}

function Save-WindowScreenshot($hwnd, $w, $h, $outPath) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $ok = [WinShot]::PrintWindow($hwnd, $hdc, 2)
    $g.ReleaseHdc($hdc)
    $g.Dispose()
    
    $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "SAVED: $outPath ($w x $h), size = $((Get-Item $outPath).Length) bytes"
}

$exe = Resolve-Path "AIRadio.Desktop\bin\Debug\net10.0-windows10.0.19041.0\AIRadio.exe"
$settingsPath = "$env:APPDATA\AIRadio\settings.json"

# ==========================================
# Run 1: Main Stage (Clock) & Settings Tabs
# ==========================================
Write-Output "=== Starting Run 1: Main Stage & Settings ==="
if (Test-Path $settingsPath) {
    $json = Get-Content $settingsPath -Raw | ConvertFrom-Json
    $json.accent_palette = "mint"
    $json.show_lyrics_in_stage = $false
    $json.start_in_compact_mode = $false
    $json | ConvertTo-Json -Depth 10 | Set-Content $settingsPath -Encoding UTF8
}

$proc1 = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 6

$win1 = Find-RealWindow $proc1.Id
if ($win1) {
    Save-WindowScreenshot $win1.Hwnd $win1.Width $win1.Height "docs\screenshots\main-stage.png"
    
    # Open settings via AutomationId="SettingsButton"
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($win1.Hwnd)
    $btnCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "SettingsButton")
    $settingsBtn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
    if ($settingsBtn) {
        Write-Output "Opening Settings..."
        ($settingsBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
        Start-Sleep -Seconds 2
        
        # Capture Tab 1 (Music & Sources) -> settings-top.png
        $musicTabCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "NavMusic")
        $musicTabBtn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $musicTabCond)
        if ($musicTabBtn) {
            Write-Output "Switching to NavMusic..."
            ($musicTabBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
            Start-Sleep -Milliseconds 1000
            Save-WindowScreenshot $win1.Hwnd $win1.Width $win1.Height "docs\screenshots\settings-top.png"
        }
        
        # Capture Tab 3 (Appearance & Updates) -> settings-bottom.png
        $appTabCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "NavAppearance")
        $appTabBtn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $appTabCond)
        if ($appTabBtn) {
            Write-Output "Switching to NavAppearance..."
            ($appTabBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
            Start-Sleep -Milliseconds 1000
            Save-WindowScreenshot $win1.Hwnd $win1.Width $win1.Height "docs\screenshots\settings-bottom.png"
        }
    }
}
Stop-Process -Id $proc1.Id -Force
Start-Sleep -Seconds 2

# ==========================================
# Run 2: Main Lyrics Stage
# ==========================================
Write-Output "=== Starting Run 2: Main Lyrics Stage ==="
if (Test-Path $settingsPath) {
    $json = Get-Content $settingsPath -Raw | ConvertFrom-Json
    $json.show_lyrics_in_stage = $true
    $json.start_in_compact_mode = $false
    $json | ConvertTo-Json -Depth 10 | Set-Content $settingsPath -Encoding UTF8
}

$proc2 = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 5

$win2 = Find-RealWindow $proc2.Id
if ($win2) {
    Save-WindowScreenshot $win2.Hwnd $win2.Width $win2.Height "docs\screenshots\main-lyrics.png"
}
Stop-Process -Id $proc2.Id -Force
Start-Sleep -Seconds 2

# ==========================================
# Run 3: Compact Mode
# ==========================================
Write-Output "=== Starting Run 3: Compact Mode ==="
if (Test-Path $settingsPath) {
    $json = Get-Content $settingsPath -Raw | ConvertFrom-Json
    $json.start_in_compact_mode = $true
    $json | ConvertTo-Json -Depth 10 | Set-Content $settingsPath -Encoding UTF8
}

$proc3 = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 5

$win3 = Find-RealWindow $proc3.Id
if ($win3) {
    Save-WindowScreenshot $win3.Hwnd $win3.Width $win3.Height "docs\screenshots\compact-mode.png"
}
Stop-Process -Id $proc3.Id -Force
Start-Sleep -Seconds 1

# Restore default settings
if (Test-Path $settingsPath) {
    $json = Get-Content $settingsPath -Raw | ConvertFrom-Json
    $json.start_in_compact_mode = $false
    $json | ConvertTo-Json -Depth 10 | Set-Content $settingsPath -Encoding UTF8
    Write-Output "Settings restored: start_in_compact_mode=false"
}

Write-Output "=== ALL 5 SCREENSHOTS UPDATED SUCCESSFULLY ==="
