param(
    [string]$SourcePath = (Join-Path $PSScriptRoot '..\..\AIRadio.Desktop\Assets\airadio.png'),
    [string]$IconPath = (Join-Path $PSScriptRoot '..\..\AIRadio.Desktop\Assets\airadio.ico'),
    [string]$PreviewPath = (Join-Path $PSScriptRoot '..\..\docs\branding\app-icon-preview.png')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Resize-Icon([System.Drawing.Image]$Source, [int]$Size) {
    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $attributes = [System.Drawing.Imaging.ImageAttributes]::new()
    try {
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
        $graphics.DrawImage($Source, [System.Drawing.Rectangle]::new(0, 0, $Size, $Size),
            0, 0, $Source.Width, $Source.Height, [System.Drawing.GraphicsUnit]::Pixel, $attributes)
    }
    catch { $bitmap.Dispose(); throw }
    finally { $attributes.Dispose(); $graphics.Dispose() }
    return $bitmap
}

$source = [System.Drawing.Bitmap]::FromFile((Resolve-Path -LiteralPath $SourcePath))
try {
    if ($source.Width -ne $source.Height -or $source.Width -lt 256) {
        throw 'The source icon must be square and at least 256 pixels wide.'
    }

    # PNG-compressed frames retain alpha at every Windows DPI and shell icon size.
    $sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
    $frames = [System.Collections.Generic.List[byte[]]]::new()
    foreach ($size in $sizes) {
        $bitmap = Resize-Icon $source $size
        $stream = [System.IO.MemoryStream]::new()
        try {
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $frames.Add($stream.ToArray())
        }
        finally { $stream.Dispose(); $bitmap.Dispose() }
    }

    $iconStream = [System.IO.MemoryStream]::new()
    $writer = [System.IO.BinaryWriter]::new($iconStream)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($index = 0; $index -lt $sizes.Count; $index++) {
            $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$index].Length)
            $writer.Write([uint32]$offset)
            $offset += $frames[$index].Length
        }
        foreach ($frame in $frames) { $writer.Write($frame) }
        $writer.Flush()
        [System.IO.File]::WriteAllBytes([System.IO.Path]::GetFullPath($IconPath), $iconStream.ToArray())
    }
    finally { $writer.Dispose(); $iconStream.Dispose() }

    $previewDirectory = Split-Path -Parent $PreviewPath
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetFullPath($previewDirectory)) | Out-Null
    $preview = [System.Drawing.Bitmap]::new(960, 460)
    $graphics = [System.Drawing.Graphics]::FromImage($preview)
    $titleFont = [System.Drawing.Font]::new('Segoe UI', 22)
    $labelFont = [System.Drawing.Font]::new('Segoe UI', 11)
    $muted = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#B8BAC8'))
    $lightLabel = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#4F5265'))
    $lightBackground = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#F4F4F8'))
    try {
        $graphics.Clear([System.Drawing.ColorTranslator]::FromHtml('#181922'))
        $graphics.DrawString('TuanRadio FM', $titleFont, [System.Drawing.Brushes]::White, 28, 20)
        $graphics.DrawString('Retro radio / mint spectrum / tuned to you', $labelFont, $muted, 30, 63)
        $large = Resize-Icon $source 256
        try { $graphics.DrawImageUnscaled($large, 20, 145) }
        finally { $large.Dispose() }
        $graphics.FillRectangle($lightBackground, 300, 266, 640, 178)
        $graphics.DrawString('DARK / actual pixel sizes', $labelFont, $muted, 320, 104)
        $graphics.DrawString('LIGHT / actual pixel sizes', $labelFont, $lightLabel, 320, 279)
        foreach ($row in @(0, 1)) {
            $x = 325
            $centerY = if ($row -eq 0) { 186 } else { 361 }
            foreach ($size in @(16, 24, 32, 48, 64, 96)) {
                $bitmap = Resize-Icon $source $size
                try { $graphics.DrawImageUnscaled($bitmap, $x, [int]($centerY - $size / 2)) }
                finally { $bitmap.Dispose() }
                $brush = if ($row -eq 0) { $muted } else { $lightLabel }
                $graphics.DrawString("$size px", $labelFont, $brush, $x, $centerY + 56)
                $x += [Math]::Max($size + 30, 72)
            }
        }
        $preview.Save([System.IO.Path]::GetFullPath($PreviewPath), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $lightBackground.Dispose(); $lightLabel.Dispose(); $muted.Dispose()
        $labelFont.Dispose(); $titleFont.Dispose(); $graphics.Dispose(); $preview.Dispose()
    }

    Write-Output "Icon: $([System.IO.Path]::GetFullPath($IconPath)) ($($sizes -join ', ') px)"
    Write-Output "Preview: $([System.IO.Path]::GetFullPath($PreviewPath))"
}
finally { $source.Dispose() }
