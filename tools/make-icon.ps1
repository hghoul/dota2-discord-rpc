# Генерирует иконку программы (app.ico) и картинки мастера установки.
# Оригинальный рисунок: тёмно-красный значок с «D2» и индикатором статуса Discord.
# Использование: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$icoPath = Join-Path $root 'src\Dota2RPC\Resources\app.ico'
$assets = Join-Path $root 'installer\assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Min($r * 2, [Math]::Min($w, $h))
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Draw-Emblem([Drawing.Graphics]$g, [float]$x, [float]$y, [float]$s) {
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.PixelOffsetMode = 'HighQuality'

    # Фон: скруглённый квадрат с диагональным градиентом
    $pad = [Math]::Max(0.5, $s * 0.03)
    $rect = New-Object Drawing.RectangleF(($x + $pad), ($y + $pad), ($s - 2 * $pad), ($s - 2 * $pad))
    $path = New-RoundRect $rect.X $rect.Y $rect.Width $rect.Height ($s * 0.22)
    $grad = New-Object Drawing.Drawing2D.LinearGradientBrush($rect, [Drawing.Color]::FromArgb(236, 84, 58), [Drawing.Color]::FromArgb(110, 20, 18), 45.0)
    $g.FillPath($grad, $path)

    # Лёгкий блик сверху
    $shine = New-Object Drawing.Drawing2D.LinearGradientBrush($rect, [Drawing.Color]::FromArgb(60, 255, 255, 255), [Drawing.Color]::FromArgb(0, 255, 255, 255), 90.0)
    $g.FillPath($shine, $path)
    if ($s -ge 32) {
        $pen = New-Object Drawing.Pen([Drawing.Color]::FromArgb(70, 0, 0, 0), [Math]::Max(1, $s / 64))
        $g.DrawPath($pen, $path)
    }

    # Текст «D2»
    $fontSize = $s * 0.40
    $font = New-Object Drawing.Font('Segoe UI Black', $fontSize, [Drawing.FontStyle]::Bold, [Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object Drawing.StringFormat
    $fmt.Alignment = 'Center'
    $fmt.LineAlignment = 'Center'
    $textRect = New-Object Drawing.RectangleF($x, ($y - $s * 0.03), $s, $s)
    $shadow = New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(90, 0, 0, 0))
    $off = [Math]::Max(1, $s * 0.02)
    $g.DrawString('D2', $font, $shadow, (New-Object Drawing.RectangleF(($x + $off), ($y - $s * 0.03 + $off), $s, $s)), $fmt)
    $g.DrawString('D2', $font, [Drawing.Brushes]::White, $textRect, $fmt)

    # Индикатор «в сети» (как статус в Discord) в правом нижнем углу
    $dot = $s * 0.30
    $dx = $x + $s - $dot - $s * 0.02
    $dy = $y + $s - $dot - $s * 0.02
    $ring = $s * 0.05
    $g.FillEllipse((New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(24, 26, 32))), ($dx - $ring), ($dy - $ring), ($dot + 2 * $ring), ($dot + 2 * $ring))
    $g.FillEllipse((New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(88, 101, 242))), $dx, $dy, $dot, $dot)
    $inner = $dot * 0.42
    $g.FillEllipse([Drawing.Brushes]::White, ($dx + ($dot - $inner) / 2), ($dy + ($dot - $inner) / 2), $inner, $inner)
}

function New-EmblemPng([int]$size) {
    $bmp = New-Object Drawing.Bitmap($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.Clear([Drawing.Color]::Transparent)
    Draw-Emblem $g 0 0 $size
    $g.Dispose()
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

# ICO с PNG-кадрами (поддерживается Windows Vista+)
$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$images = @()
foreach ($s in $sizes) { $images += , (New-EmblemPng $s) }

$fs = [IO.File]::Create($icoPath)
$bw = New-Object IO.BinaryWriter($fs)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$images[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Close()
Write-Host "Иконка: $icoPath"

# Картинки мастера Inno Setup (BMP 24 бит): большая слева и маленькая в шапке, 100% и 200%
function Save-WizardImage([int]$w, [int]$h, [string]$file) {
    $bmp = New-Object Drawing.Bitmap($w, $h, [Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $rect = New-Object Drawing.Rectangle(0, 0, $w, $h)
    $bg = New-Object Drawing.Drawing2D.LinearGradientBrush($rect, [Drawing.Color]::FromArgb(30, 16, 18), [Drawing.Color]::FromArgb(12, 14, 18), 90.0)
    $g.FillRectangle($bg, $rect)
    $glow = New-Object Drawing.Drawing2D.GraphicsPath
    $glow.AddEllipse((-$w * 0.6), ($h * 0.05), ($w * 2.2), ($h * 0.7))
    $pgb = New-Object Drawing.Drawing2D.PathGradientBrush($glow)
    $pgb.CenterColor = [Drawing.Color]::FromArgb(110, 200, 50, 40)
    $pgb.SurroundColors = @([Drawing.Color]::FromArgb(0, 200, 50, 40))
    $g.FillPath($pgb, $glow)
    $s = [int]($w * 0.62)
    Draw-Emblem $g (($w - $s) / 2) ($h * 0.22) $s
    $font = New-Object Drawing.Font('Segoe UI Semibold', ($w * 0.085), [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object Drawing.StringFormat
    $fmt.Alignment = 'Center'
    $g.DrawString("Dota 2`nDiscord Rich Presence", $font, [Drawing.Brushes]::White, (New-Object Drawing.RectangleF(0, ($h * 0.22 + $s + $h * 0.05), $w, ($h * 0.3))), $fmt)
    $g.Dispose()
    $bmp.Save($file, [Drawing.Imaging.ImageFormat]::Bmp)
    $bmp.Dispose()
}

function Save-SmallImage([int]$s, [string]$file) {
    $bmp = New-Object Drawing.Bitmap($s, $s, [Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.Clear([Drawing.Color]::White)
    Draw-Emblem $g 0 0 $s
    $g.Dispose()
    $bmp.Save($file, [Drawing.Imaging.ImageFormat]::Bmp)
    $bmp.Dispose()
}

Save-WizardImage 164 314 (Join-Path $assets 'wizard.bmp')
Save-WizardImage 328 628 (Join-Path $assets 'wizard-200.bmp')
Save-SmallImage 55 (Join-Path $assets 'wizard-small.bmp')
Save-SmallImage 110 (Join-Path $assets 'wizard-small-200.bmp')

# PNG для README
[IO.File]::WriteAllBytes((Join-Path $assets 'icon-256.png'), (New-EmblemPng 256))
Write-Host "Картинки мастера: $assets"
