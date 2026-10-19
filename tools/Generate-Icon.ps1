$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$iconPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'assets\KeyHold.ico'
function New-RoundedPath([single]$x, [single]$y, [single]$width, [single]$height, [single]$radius) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $diameter = $radius * 2
    $path.AddArc($x, $y, $diameter, $diameter, 180, 90)
    $path.AddArc($x + $width - $diameter, $y, $diameter, $diameter, 270, 90)
    $path.AddArc($x + $width - $diameter, $y + $height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($x, $y + $height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}
$sizes = @(16, 20, 24, 32, 40, 48, 64, 256)
$frames = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($size in $sizes) {
    $scale = [single]($size / 256.0)
    $bitmap = New-Object System.Drawing.Bitmap($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $blue = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0, 103, 192))
    $shadow = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0, 72, 145))
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $background = New-RoundedPath (8*$scale) (8*$scale) (240*$scale) (240*$scale) (52*$scale)
    $lower = New-RoundedPath (48*$scale) (48*$scale) (160*$scale) (172*$scale) (24*$scale)
    $key = New-RoundedPath (48*$scale) (38*$scale) (160*$scale) (172*$scale) (24*$scale)
    $graphics.FillPath($blue, $background); $graphics.FillPath($shadow, $lower); $graphics.FillPath($white, $key)
    $font = New-Object System.Drawing.Font('Segoe UI', (110*$scale), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = [System.Drawing.StringAlignment]::Center; $format.LineAlignment = [System.Drawing.StringAlignment]::Center
    $rect = New-Object System.Drawing.RectangleF((48*$scale), (34*$scale), (160*$scale), (172*$scale))
    $graphics.DrawString('K', $font, $blue, $rect, $format)
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames.Add($stream.ToArray())
    $stream.Dispose(); $format.Dispose(); $font.Dispose(); $key.Dispose(); $lower.Dispose(); $background.Dispose()
    $white.Dispose(); $shadow.Dispose(); $blue.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$output = [System.IO.File]::Create($iconPath)
$writer = New-Object System.IO.BinaryWriter($output)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = [byte]($sizes[$index] % 256)
        $writer.Write($dimension); $writer.Write($dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frames[$index].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$index].Length
    }
    foreach ($frame in $frames) { $writer.Write($frame) }
} finally { $writer.Dispose(); $output.Dispose() }
Write-Output "Generated $iconPath"
