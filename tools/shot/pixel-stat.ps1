param([string]$Path)
Add-Type -AssemblyName System.Drawing
$b = [System.Drawing.Bitmap]::FromFile($Path)
$colors = @{}
$bright = 0; $dark = 0; $total = 0
$rng = New-Object System.Random(42)
for ($i = 0; $i -lt 3000; $i++) {
  $x = $rng.Next($b.Width); $y = $rng.Next($b.Height)
  $c = $b.GetPixel($x, $y)
  $key = "$($c.R)/$($c.G)/$($c.B)"
  if (-not $colors.ContainsKey($key)) { $colors[$key] = 1 } else { $colors[$key]++ }
  $lum = 0.299*$c.R + 0.587*$c.G + 0.114*$c.B
  if ($lum -lt 60) { $dark++ } elseif ($lum -gt 180) { $bright++ }
  $total++
}
$b.Dispose()
$distinct = $colors.Count
$darkPct = [math]::Round(100.0 * $dark / $total, 1)
$brightPct = [math]::Round(100.0 * $bright / $total, 1)
Write-Output ("{0}: distinct={1} dark={2}% bright={3}% -> {4}" -f (Split-Path $Path -Leaf), $distinct, $darkPct, $brightPct, $(if ($distinct -gt 400 -and $brightPct -gt 25) { "PHOTO/LIKE" } else { "DARK-UI" }))
