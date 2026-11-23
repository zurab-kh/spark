# Renders the Spark logo (same geometry as spark-logo.svg) to PNGs and a multi-size spark.ico.
# Usage: powershell -File tools/branding/make-icon.ps1 -OutDir src/Strela.App/Assets
param([Parameter(Mandatory)][string]$OutDir)

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null

function Render([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.InterpolationMode = 'HighQualityBicubic'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 100.0
    # Small sizes: less margin so the mark stays legible in the tray.
    $inset = if ($size -le 24) { 0 } else { 2 }
    $box = New-Object System.Drawing.RectangleF ($inset * $s), ($inset * $s), ((100 - 2 * $inset) * $s), ((100 - 2 * $inset) * $s)
    $r = 22 * $s; $d = 2 * $r
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($box.X, $box.Y, $d, $d, 180, 90)
    $path.AddArc($box.Right - $d, $box.Y, $d, $d, 270, 90)
    $path.AddArc($box.Right - $d, $box.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($box.X, $box.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $box, ([System.Drawing.Color]::Black), ([System.Drawing.Color]::Black), 45.0
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend 3
    $blend.Colors = @([System.Drawing.ColorTranslator]::FromHtml('#FFB300'), [System.Drawing.ColorTranslator]::FromHtml('#FF6A1A'), [System.Drawing.ColorTranslator]::FromHtml('#FF2D7A'))
    $blend.Positions = @(0.0, 0.5, 1.0)
    $brush.InterpolationColors = $blend
    $g.FillPath($brush, $path)
    # Bolt: M58 14 L27 55 L47 55 L40 86 L73 42 L53 42 Z (bigger on tiny sizes)
    $k = if ($size -le 24) { 1.12 } else { 1.0 }
    $pts = @(@(58,14),@(27,55),@(47,55),@(40,86),@(73,42),@(53,42)) | ForEach-Object {
        New-Object System.Drawing.PointF ((50 + ($_[0] - 50) * $k) * $s), ((50 + ($_[1] - 50) * $k) * $s)
    }
    $g.FillPolygon([System.Drawing.Brushes]::White, [System.Drawing.PointF[]]$pts)
    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 48, 64, 128, 256
$pngs = @{}
foreach ($sz in $sizes) {
    $bmp = Render $sz
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs[$sz] = $ms.ToArray()
    if ($sz -eq 256) { $bmp.Save((Join-Path $OutDir 'spark-256.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
}

# ICO with PNG-compressed entries (supported since Windows Vista).
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($sz in $sizes) {
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$sz].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$sz].Length
}
foreach ($sz in $sizes) { $w.Write($pngs[$sz]) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'spark.ico'), $out.ToArray())
"wrote spark.ico and spark-256.png to $OutDir"
