# Draws App Icon options for the Discord application (1024x1024 PNGs) into assets\icons, plus a
# preview sheet that shows each one large and at the tiny size Discord uses next to a name.
#   powershell -File tools\make-icon-options.ps1
Add-Type -AssemblyName System.Drawing
$dir = Join-Path $PSScriptRoot "..\assets\icons"
New-Item -ItemType Directory -Force $dir | Out-Null
$dir = (Resolve-Path $dir).Path
$S = 1024

function New-Canvas {
    $bmp = New-Object System.Drawing.Bitmap $S, $S
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    return $bmp, $g
}

function Color([int]$r, [int]$gr, [int]$b, [int]$a = 255) { [System.Drawing.Color]::FromArgb($a, $r, $gr, $b) }

# Full-bleed background with a soft glow in the middle, so it looks right cropped to a circle or a square.
function Fill-Background($g, $inner, $outer) {
    $g.Clear($outer)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddEllipse(-180, -180, $S + 360, $S + 360)
    $brush = New-Object System.Drawing.Drawing2D.PathGradientBrush $path
    $brush.CenterColor = $inner
    $brush.SurroundColors = @($outer)
    $g.FillRectangle($brush, 0, 0, $S, $S)
}

function New-Pen($color, [single]$width, [string]$cap = 'Round') {
    $pen = New-Object System.Drawing.Pen $color, $width
    $pen.StartCap = $cap; $pen.EndCap = $cap
    return $pen
}

# --- 1. Steering wheel: the classic sim-racing symbol ---------------------------------------
function Draw-Wheel {
    $bmp, $g = New-Canvas
    Fill-Background $g (Color 38 58 110) (Color 7 11 24)
    $cx = 512; $cy = 520; $r = 318
    $white = Color 240 243 250
    $red = Color 225 35 45
    # spokes: left, right (slightly lowered) and bottom
    $spoke = New-Pen $white 92
    $g.DrawLine($spoke, $cx, $cy + 30, $cx - $r + 20, $cy + 10)
    $g.DrawLine($spoke, $cx, $cy + 30, $cx + $r - 20, $cy + 10)
    $g.DrawLine($spoke, $cx, $cy + 30, $cx, $cy + $r - 20)
    # rim with the red centre marker at 12 o'clock
    $g.DrawEllipse((New-Pen $white 84), $cx - $r, $cy - $r, 2 * $r, 2 * $r)
    $g.DrawArc((New-Pen $red 84 'Flat'), $cx - $r, $cy - $r, 2 * $r, 2 * $r, -98, 16)
    # hub
    $hub = 128
    $g.FillEllipse((New-Object System.Drawing.SolidBrush (Color 14 22 46)), $cx - $hub, $cy + 30 - $hub, 2 * $hub, 2 * $hub)
    $g.DrawEllipse((New-Pen $white 22), $cx - $hub, $cy + 30 - $hub, 2 * $hub, 2 * $hub)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $red), $cx - 34, $cy + 30 - 34, 68, 68)
    $g.Dispose(); return $bmp
}

# --- 2. Waving chequered flag on racing red ---------------------------------------------------
function Draw-Flag {
    $bmp, $g = New-Canvas
    Fill-Background $g (Color 215 40 48) (Color 92 8 16)
    $x0 = 318; $x1 = 870; $y0 = 236; $y1 = 640; $cols = 7; $rows = 5
    $wave = { param($x) 46 * [Math]::Sin(($x - $x0) / ($x1 - $x0) * 2 * [Math]::PI * 0.95) }
    $black = New-Object System.Drawing.SolidBrush (Color 16 16 20)
    $white = New-Object System.Drawing.SolidBrush (Color 246 246 248)
    $cw = ($x1 - $x0) / $cols; $ch = ($y1 - $y0) / $rows
    for ($c = 0; $c -lt $cols; $c++) {
        $ax = $x0 + $c * $cw; $bx = $ax + $cw
        $ao = & $wave $ax; $bo = & $wave $bx
        for ($row = 0; $row -lt $rows; $row++) {
            $ay = $y0 + $row * $ch; $by = $ay + $ch
            $pts = [System.Drawing.PointF[]]@(
                (New-Object System.Drawing.PointF $ax, ($ay + $ao)), (New-Object System.Drawing.PointF $bx, ($ay + $bo)),
                (New-Object System.Drawing.PointF $bx, ($by + $bo)), (New-Object System.Drawing.PointF $ax, ($by + $ao)))
            $g.FillPolygon($(if (($c + $row) % 2 -eq 0) { $white } else { $black }), $pts)
        }
    }
    # pole
    $pole = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Rectangle 270, 200, 48, 640), (Color 250 250 252), (Color 170 172 180), 0
    $g.FillRectangle($pole, 272, 214, 46, 640)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush (Color 250 250 252)), 262, 172, 66, 66)
    $g.Dispose(); return $bmp
}

# --- 3. Rev counter with the needle in the red ------------------------------------------------
function Draw-Tach {
    $bmp, $g = New-Canvas
    Fill-Background $g (Color 40 46 62) (Color 8 9 14)
    $cx = 512; $cy = 560; $r = 330
    $white = Color 240 243 250
    $red = Color 230 38 46
    $rect = New-Object System.Drawing.RectangleF ($cx - $r), ($cy - $r), (2 * $r), (2 * $r)
    $g.DrawArc((New-Pen $white 34 'Flat'), $rect, 135, 210)
    $g.DrawArc((New-Pen $red 34 'Flat'), $rect, 345, 60)
    for ($i = 0; $i -le 10; $i++) {
        $a = (135 + $i * 27) * [Math]::PI / 180
        $len = if ($i % 2 -eq 0) { 78 } else { 46 }
        $pen = New-Pen $(if ($i -ge 8) { $red } else { $white }) $(if ($i % 2 -eq 0) { 22 } else { 14 }) 'Flat'
        $g.DrawLine($pen, $cx + ($r - 40) * [Math]::Cos($a), $cy + ($r - 40) * [Math]::Sin($a),
            $cx + ($r - 40 - $len) * [Math]::Cos($a), $cy + ($r - 40 - $len) * [Math]::Sin($a))
    }
    $n = 372 * [Math]::PI / 180
    $g.DrawLine((New-Pen $red 30), $cx, $cy, $cx + 285 * [Math]::Cos($n), $cy + 285 * [Math]::Sin($n))
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $white), $cx - 58, $cy - 58, 116, 116)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush (Color 20 22 30)), $cx - 26, $cy - 26, 52, 52)
    $g.Dispose(); return $bmp
}

$options = [ordered]@{
    "1-steering-wheel" = Draw-Wheel
    "2-checkered-flag" = Draw-Flag
    "3-rev-counter"    = Draw-Tach
}
foreach ($name in $options.Keys) { $options[$name].Save("$dir\$name.png", [System.Drawing.Imaging.ImageFormat]::Png) }
$badge = [System.Drawing.Image]::FromFile((Resolve-Path "$PSScriptRoot\..\assets\app-icon-1024.png").Path)
$options["4-ir-badge"] = $badge

# Preview: each option at 240 px, with a mock "name + tiny icon" row underneath at Discord's sizes.
$cell = 300; $sheet = New-Object System.Drawing.Bitmap ($cell * $options.Count), 430
$g = [System.Drawing.Graphics]::FromImage($sheet)
$g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.TextRenderingHint = 'AntiAliasGridFit'
$g.Clear((Color 49 51 56))
$label = New-Object System.Drawing.Font ("Segoe UI Semibold", 15)
$nameFont = New-Object System.Drawing.Font ("Segoe UI Semibold", 13)
$i = 0
foreach ($name in $options.Keys) {
    $x = $i * $cell + 30
    $g.DrawImage($options[$name], $x, 24, 240, 240)
    $g.DrawString(($name -replace '^\d-', '' -replace '-', ' '), $label, [System.Drawing.Brushes]::White, $x, 276)
    # tiny-size check, as a rounded mock of a member-list row
    $g.FillRectangle((New-Object System.Drawing.SolidBrush (Color 43 45 49)), $x, 318, 240, 86)
    $g.DrawString("YourName", $nameFont, (New-Object System.Drawing.SolidBrush (Color 242 243 245)), ($x + 10), 332)
    $g.DrawImage($options[$name], ($x + 128), 334, 20, 20)
    $g.DrawString("Playing iRacing", (New-Object System.Drawing.Font ("Segoe UI", 10)), (New-Object System.Drawing.SolidBrush (Color 181 186 193)), ($x + 10), 362)
    $g.DrawImage($options[$name], ($x + 196), 340, 36, 36)
    $i++
}
$g.Dispose()
$sheet.Save("$dir\preview.png", [System.Drawing.Imaging.ImageFormat]::Png)
$badge.Dispose()
"saved options and preview to $dir"
