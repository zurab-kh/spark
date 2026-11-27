# Renders the Spark logo (same geometry as spark-logo.svg: a white S with lightning-bolt tips on a storm-night tile)
# to every icon the apps use, so all of them come from one drawing:
#   src/Strela.App/Assets/spark.ico, spark-256.png   - Windows exe, window, tray (16-24 px full bleed), empty state
#   src/Spark.Mac/Assets/spark-256.png               - Mac empty state and toast
#   src/Spark.Mac/Assets/tray-template.png           - Mac menu bar template (black S on transparency)
#   src/Spark.Mac/macos/spark.icns                   - Mac app icon on the macOS grid (824 px tile in 1024, shadow)
#   browser-extension/icons/icon-16/32/48/128.png
# Usage: powershell -File tools/branding/make-icon.ps1
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')

function C([string]$hex, [int]$alpha = 255) { [System.Drawing.Color]::FromArgb($alpha, [System.Drawing.ColorTranslator]::FromHtml($hex)) }
function P([double]$x, [double]$y) { New-Object System.Drawing.PointF ([float]$x), ([float]$y) }

function RoundRect([float]$x, [float]$y, [float]$w, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath; $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $w - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $w - $d, $d, $d, 90, 90)
    $p.CloseFigure(); $p
}

# Tile background in 0..100 units: deep blue night, a soft glow behind the letter, a few stars.
function DrawTile($g, [float]$inset, [bool]$stars) {
    $clip = RoundRect $inset $inset (100 - 2 * $inset) 22
    $g.SetClip($clip)
    $sky = New-Object System.Drawing.Drawing2D.GraphicsPath; $sky.AddEllipse(-25, -30, 150, 150)
    $b = New-Object System.Drawing.Drawing2D.PathGradientBrush $sky
    $b.CenterPoint = P 50 45
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend 3
    $blend.Colors = @((C '#0A0726'), (C '#1E1466'), (C '#4B3BFF')); $blend.Positions = @(0.0, 0.45, 1.0)
    $b.InterpolationColors = $blend
    $g.FillRectangle($b, 0, 0, 100, 100)
    $halo = New-Object System.Drawing.Drawing2D.GraphicsPath; $halo.AddEllipse(6, 6, 88, 88)
    $glow = New-Object System.Drawing.Drawing2D.PathGradientBrush $halo
    $glow.CenterColor = C '#9FD8FF' 150; $glow.SurroundColors = @(C '#9FD8FF' 0)
    $g.FillEllipse($glow, 6, 6, 88, 88)
    if ($stars) {
        foreach ($s in @(@(20, 18, 1.2, 200), @(82, 30, 1.0, 150), @(14, 64, 0.9, 130), @(86, 80, 1.3, 180))) {
            $g.FillEllipse((New-Object System.Drawing.SolidBrush (C '#FFFFFF' $s[3])), $s[0] - $s[2], $s[1] - $s[2], 2 * $s[2], 2 * $s[2])
        }
    }
    $g.ResetClip()
}

# The S: one stroke of two curves joined by a short straight spine, both ends sharpened into bolt tips.
function DrawS($g, $brush) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddBezier((P 72 25), (P 64 17), (P 40 15), (P 32 27))
    $p.AddBezier((P 32 27), (P 26 37), (P 34 45), (P 48 48))
    $p.AddLine((P 48 48), (P 58 50))
    $p.AddBezier((P 58 50), (P 72 53), (P 76 64), (P 70 74))
    $p.AddBezier((P 70 74), (P 62 86), (P 38 86), (P 28 76))
    $pen = New-Object System.Drawing.Pen $brush, 15
    $pen.LineJoin = 'Miter'; $pen.MiterLimit = 10
    $g.DrawPath($pen, $p)
    $g.FillPolygon($brush, [System.Drawing.PointF[]]@((P 64 13), (P 88 10), (P 76 30), (P 68 24)))
    $g.FillPolygon($brush, [System.Drawing.PointF[]]@((P 34 70), (P 34 90), (P 10 88), (P 24 70)))
}

function NewCanvas([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.InterpolationMode = 'HighQualityBicubic'
    $g.Clear([System.Drawing.Color]::Transparent)
    return $bmp, $g
}

# Square icon: tray sizes (<= 24 px) fill the whole square and skip the stars, which would only be noise there.
function RenderIcon([int]$size) {
    $bmp, $g = NewCanvas $size
    $g.ScaleTransform($size / 100.0, $size / 100.0)
    DrawTile $g ($(if ($size -le 24) { 0 } else { 2 })) ($size -ge 48)
    DrawS $g ([System.Drawing.Brushes]::White)
    $g.Dispose(); $bmp
}

function PngBytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    , $ms.ToArray()   # the comma keeps it a byte[]: PowerShell would unroll it into object[]
}

function SavePng($bmp, [string]$path) {
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    [System.IO.File]::WriteAllBytes($path, (PngBytes $bmp))
}

# ---- Windows: spark.ico (PNG entries, Vista+) and spark-256.png --------------------------------------------------
$sizes = 16, 20, 24, 32, 48, 64, 128, 256
$pngs = @{}
foreach ($sz in $sizes) { $pngs[$sz] = PngBytes (RenderIcon $sz) }
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
[System.IO.File]::WriteAllBytes((Join-Path $root 'src\Strela.App\Assets\spark.ico'), $out.ToArray())
foreach ($dir in 'src\Strela.App\Assets', 'src\Spark.Mac\Assets') {
    [System.IO.File]::WriteAllBytes((Join-Path $root "$dir\spark-256.png"), $pngs[256])
}

# ---- Browser extension ---------------------------------------------------------------------------------------------
foreach ($sz in 16, 32, 48, 128) { SavePng (RenderIcon $sz) (Join-Path $root "browser-extension\icons\icon-$sz.png") }

# ---- Mac menu bar template: the S alone in black, a little larger than on the tile ---------------------------------
$tray, $g = NewCanvas 64
$g.TranslateTransform(32, 32); $g.ScaleTransform(0.64 * 1.12, 0.64 * 1.12); $g.TranslateTransform(-50, -50)
DrawS $g ([System.Drawing.Brushes]::Black)
$g.Dispose()
SavePng $tray (Join-Path $root 'src\Spark.Mac\Assets\tray-template.png')

# ---- Mac app icon: 824 px tile on a 1024 canvas (macOS icon grid) with a soft drop shadow ---------------------------
$master, $g = NewCanvas 1024
foreach ($i in 1..14) {   # layered translucent tiles fake a blurred shadow below the tile
    $grow = 15 - $i
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(7, 0, 0, 0))),
        (RoundRect (100 - $grow) (112 - $grow) (824 + 2 * $grow) (185 + $grow)))
}
$g.TranslateTransform(100, 100); $g.ScaleTransform(8.24, 8.24)
DrawTile $g 0 $true
DrawS $g ([System.Drawing.Brushes]::White)
$g.Dispose()
$chunks = New-Object System.IO.MemoryStream
$cw = New-Object System.IO.BinaryWriter $chunks
function BigEndian([uint32]$v) { $b = [BitConverter]::GetBytes($v); [Array]::Reverse($b); , $b }
# PNG-encoded entries: ic11 32 (16@2x), ic12 64 (32@2x), ic07 128, ic13 256 (128@2x), ic08 256, ic14 512 (256@2x),
# ic09 512, ic10 1024 (512@2x).
foreach ($e in @(@('ic11', 32), @('ic12', 64), @('ic07', 128), @('ic13', 256), @('ic08', 256), @('ic14', 512), @('ic09', 512), @('ic10', 1024))) {
    $small = New-Object System.Drawing.Bitmap $master, $e[1], $e[1]
    $data = PngBytes $small
    $cw.Write([Text.Encoding]::ASCII.GetBytes($e[0])); $cw.Write((BigEndian ($data.Length + 8))); $cw.Write($data)
}
$cw.Flush()
$icns = New-Object System.IO.MemoryStream
$iw = New-Object System.IO.BinaryWriter $icns
$iw.Write([Text.Encoding]::ASCII.GetBytes('icns')); $iw.Write((BigEndian ($chunks.Length + 8))); $iw.Write($chunks.ToArray())
$iw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $root 'src\Spark.Mac\macos\spark.icns'), $icns.ToArray())

"wrote spark.ico, spark-256.png (Windows, Mac), tray-template.png, spark.icns and extension icons"
