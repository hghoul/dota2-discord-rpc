# Генерирует иконку программы (app.ico) и картинки мастера установки.
# Оригинальный рисунок: шестигранный щит с огненным градиентом и золотой окантовкой, скрещённые клинки
# и светящийся камень-«индикатор статуса» в цветах Discord. На маленьких размерах рисунок упрощается.
# Использование: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$icoPath = Join-Path $root 'src\Dota2RPC\Resources\app.ico'
$assets = Join-Path $root 'installer\assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null

function C([int]$a, [int]$r, [int]$g, [int]$b) { [Drawing.Color]::FromArgb($a, $r, $g, $b) }

# Шестигранник «остриём вверх» со скруглёнными углами
function New-Hex([float]$cx, [float]$cy, [float]$radius, [float]$round) {
    $pts = @()
    for ($i = 0; $i -lt 6; $i++) {
        $a = [Math]::PI / 180 * (60 * $i - 90)
        $pts += New-Object Drawing.PointF(($cx + $radius * [Math]::Cos($a)), ($cy + $radius * [Math]::Sin($a)))
    }
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    for ($i = 0; $i -lt 6; $i++) {
        $prev = $pts[($i + 5) % 6]; $cur = $pts[$i]; $next = $pts[($i + 1) % 6]
        $t = $round / $radius
        $a1 = New-Object Drawing.PointF(($cur.X + ($prev.X - $cur.X) * $t), ($cur.Y + ($prev.Y - $cur.Y) * $t))
        $a2 = New-Object Drawing.PointF(($cur.X + ($next.X - $cur.X) * $t), ($cur.Y + ($next.Y - $cur.Y) * $t))
        if ($i -gt 0) { $p.AddLine($p.GetLastPoint(), $a1) }
        $p.AddBezier($a1, $cur, $cur, $a2)
    }
    $p.CloseFigure()
    return $p
}

# Клинок в локальных координатах (остриём вверх, длина 1), затем поворот и масштаб
function New-Blade([float]$cx, [float]$cy, [float]$len, [float]$angle) {
    $m = New-Object Drawing.Drawing2D.Matrix
    $m.Translate($cx, $cy); $m.Rotate($angle); $m.Scale($len, $len)
    $blade = New-Object Drawing.Drawing2D.GraphicsPath
    $blade.AddPolygon([Drawing.PointF[]]@(
        (New-Object Drawing.PointF(0, -0.50)),
        (New-Object Drawing.PointF(0.055, -0.36)),
        (New-Object Drawing.PointF(0.045, 0.16)),
        (New-Object Drawing.PointF(-0.045, 0.16)),
        (New-Object Drawing.PointF(-0.055, -0.36))))
    $blade.Transform($m)
    $hilt = New-Object Drawing.Drawing2D.GraphicsPath
    $hilt.AddRectangle((New-Object Drawing.RectangleF(-0.13, 0.16, 0.26, 0.045)))  # гарда
    $hilt.AddRectangle((New-Object Drawing.RectangleF(-0.025, 0.205, 0.05, 0.15))) # рукоять
    $hilt.AddEllipse(-0.04, 0.345, 0.08, 0.08)                                      # навершие
    $hilt.Transform($m)
    return @($blade, $hilt)
}

function Draw-Emblem([Drawing.Graphics]$g, [float]$x, [float]$y, [float]$s) {
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $small = $s -lt 30
    $cx = $x + $s / 2; $cy = $y + $s / 2
    $R = $s * 0.49

    # Внешняя золотая окантовка
    $outer = New-Hex $cx $cy $R ($R * 0.16)
    $rimRect = New-Object Drawing.RectangleF($x, $y, $s, $s)
    $rim = New-Object Drawing.Drawing2D.LinearGradientBrush($rimRect, (C 255 255 226 150), (C 255 176 112 32), 70.0)
    $g.FillPath($rim, $outer)

    # Тело щита: огненный градиент сверху вниз
    $inner = New-Hex $cx $cy ($R * ($(if ($small) { 0.86 } else { 0.90 }))) ($R * 0.14)
    $body = New-Object Drawing.Drawing2D.LinearGradientBrush($rimRect, (C 255 255 120 60), (C 255 70 8 40), 90.0)
    $blend = New-Object Drawing.Drawing2D.ColorBlend
    $blend.Colors = [Drawing.Color[]]@((C 255 255 140 66), (C 255 214 38 52), (C 255 110 10 52), (C 255 34 6 30))
    $blend.Positions = [single[]]@(0.0, 0.38, 0.72, 1.0)
    $body.InterpolationColors = $blend
    $g.FillPath($body, $inner)

    if (-not $small) {
        # Мягкое свечение в центре
        $glowPath = New-Object Drawing.Drawing2D.GraphicsPath
        $glowPath.AddEllipse(($cx - $R * 0.75), ($cy - $R * 0.75), ($R * 1.5), ($R * 1.5))
        $glow = New-Object Drawing.Drawing2D.PathGradientBrush($glowPath)
        $glow.CenterColor = C 150 255 196 92
        $glow.SurroundColors = [Drawing.Color[]]@((C 0 255 120 40))
        $g.SetClip($inner); $g.FillPath($glow, $glowPath); $g.ResetClip()

        # Блик в верхней части
        $shineRect = New-Object Drawing.RectangleF($x, $y, $s, ($s * 0.5))
        $shine = New-Object Drawing.Drawing2D.LinearGradientBrush($shineRect, (C 60 255 255 255), (C 0 255 255 255), 90.0)
        $g.SetClip($inner); $g.FillRectangle($shine, $shineRect); $g.ResetClip()
    }

    # Скрещённые клинки
    $len = $s * $(if ($small) { 0.80 } else { 0.76 })
    $steel = New-Object Drawing.Drawing2D.LinearGradientBrush($rimRect, (C 255 255 255 255), (C 255 255 196 110), 90.0)
    $edge = New-Object Drawing.Pen((C 200 40 6 24), [Math]::Max(1.0, $s / 70))
    foreach ($angle in @(-40, 40)) {
        $parts = New-Blade $cx ($cy + $s * 0.02) $len $angle
        foreach ($part in $parts) {
            if (-not $small) { $g.DrawPath($edge, $part) }
            $g.FillPath($steel, $part)
        }
    }

    # Камень-индикатор в перекрестье (цвета Discord)
    $gem = $s * $(if ($small) { 0.24 } else { 0.15 })
    $gx = $cx - $gem / 2; $gy = $cy + $s * 0.02 - $gem / 2
    $ring = $s * $(if ($small) { 0.03 } else { 0.022 })
    $g.FillEllipse((New-Object Drawing.SolidBrush((C 255 30 8 30))), ($gx - $ring), ($gy - $ring), ($gem + 2 * $ring), ($gem + 2 * $ring))
    $gemRect = New-Object Drawing.RectangleF($gx, $gy, $gem, $gem)
    $gemBrush = New-Object Drawing.Drawing2D.LinearGradientBrush($gemRect, (C 255 120 190 255), (C 255 88 101 242), 60.0)
    $g.FillEllipse($gemBrush, $gemRect)
    if (-not $small) {
        $g.FillEllipse((New-Object Drawing.SolidBrush((C 220 255 255 255))), ($gx + $gem * 0.22), ($gy + $gem * 0.18), ($gem * 0.28), ($gem * 0.22))
    }
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
    $bg = New-Object Drawing.Drawing2D.LinearGradientBrush($rect, (C 255 42 10 30), (C 255 10 8 18), 90.0)
    $g.FillRectangle($bg, $rect)
    $glow = New-Object Drawing.Drawing2D.GraphicsPath
    $glow.AddEllipse((-$w * 0.6), ($h * 0.05), ($w * 2.2), ($h * 0.7))
    $pgb = New-Object Drawing.Drawing2D.PathGradientBrush($glow)
    $pgb.CenterColor = C 120 230 70 40
    $pgb.SurroundColors = [Drawing.Color[]]@((C 0 230 70 40))
    $g.FillPath($pgb, $glow)
    $s = [int]($w * 0.66)
    Draw-Emblem $g (($w - $s) / 2) ($h * 0.2) $s
    $font = New-Object Drawing.Font('Segoe UI Semibold', ($w * 0.085), [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object Drawing.StringFormat
    $fmt.Alignment = 'Center'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.DrawString("Dota 2`nDiscord Rich Presence", $font, [Drawing.Brushes]::White, (New-Object Drawing.RectangleF(0, ($h * 0.2 + $s + $h * 0.05), $w, ($h * 0.3))), $fmt)
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

# PNG для README и предпросмотра
[IO.File]::WriteAllBytes((Join-Path $assets 'icon-256.png'), (New-EmblemPng 256))
foreach ($p in 16, 32, 48) { [IO.File]::WriteAllBytes((Join-Path $assets "icon-$p.png"), (New-EmblemPng $p)) }
Write-Host "Картинки мастера: $assets"
