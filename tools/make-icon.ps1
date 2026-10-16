# Draws assets\app.ico (the exe's icon): the "iR" badge at 16-256 px, stored as PNG frames.
#   powershell -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot "..\assets\app.ico"

function New-Frame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'

    # rounded square with the same dark blue gradient as the status picture
    $r = [Math]::Max(2, [int]($size * 0.22))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $s = $size - 1
    $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90)
    $path.AddArc($s - $r * 2, 0, $r * 2, $r * 2, 270, 90)
    $path.AddArc($s - $r * 2, $s - $r * 2, $r * 2, $r * 2, 0, 90)
    $path.AddArc(0, $s - $r * 2, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 30, 52, 104)), ([System.Drawing.Color]::FromArgb(255, 8, 14, 30)), 60
    $g.FillPath($bg, $path)

    # checkered strip and red stripe along the bottom on the bigger sizes
    $g.SetClip($path)
    if ($size -ge 48) {
        $cell = [Math]::Max(2, [int]($size / 10))
        $top = [int]($size * 0.76)
        $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(230, 255, 255, 255))
        for ($row = 0; $row -lt 3; $row++) {
            for ($col = 0; $col * $cell -lt $size; $col++) {
                if (($row + $col) % 2 -eq 0) { $g.FillRectangle($white, $col * $cell, $top + $row * $cell, $cell, $cell) }
            }
        }
        $red = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 220, 38, 38))
        $g.FillRectangle($red, 0, $top - [int]($size * 0.07), $size, [Math]::Max(1, [int]($size * 0.035)))
    }
    $g.ResetClip()

    $fontSize = if ($size -ge 48) { $size * 0.46 } else { $size * 0.56 }
    $font = New-Object System.Drawing.Font ("Segoe UI Black", [single]$fontSize, [System.Drawing.FontStyle]::Italic, [System.Drawing.GraphicsUnit]::Pixel)
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = 'Center'
    $format.LineAlignment = 'Center'
    $textHeight = if ($size -ge 48) { $size * 0.72 } else { $size }
    $g.DrawString("iR", $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, 0, $size, $textHeight), $format)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$frames = $sizes | ForEach-Object { , (New-Frame $_) }

# ICO container: header, one 16-byte directory entry per frame, then the PNG data
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$frames[$i].Length); $w.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $w.Write($f) }
$w.Flush()
$assets = (Resolve-Path (Split-Path $out)).Path
[System.IO.File]::WriteAllBytes("$assets\app.ico", $ico.ToArray())
"saved $out ($($ico.Length) bytes)"

# The same badge at 1024 px, for the Discord application's App Icon.
[System.IO.File]::WriteAllBytes("$assets\app-icon-1024.png", (New-Frame 1024))
"saved $assets\app-icon-1024.png"
