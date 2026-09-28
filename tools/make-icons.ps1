# Generates the multi-resolution .ico files in src/EasyBlackout/Assets.
# Usage: powershell -ExecutionPolicy Bypass -File tools/make-icons.ps1
Add-Type -AssemblyName System.Drawing

$assets = Join-Path $PSScriptRoot '..\src\EasyBlackout\Assets'
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-Frame([int]$size, [bool]$active) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [float]$size
    $pad = [Math]::Max(0.5, $s * 0.03)
    $rect = New-RoundedRect $pad $pad ($s - 2 * $pad) ($s - 2 * $pad) ($s * 0.22)

    if ($active) {
        $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 8, 8, 12))
        $g.FillPath($bg, $rect)
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 139, 124, 255)), ([Math]::Max(1.0, $s * 0.06))
        $g.DrawPath($pen, $rect)
        $moon = [System.Drawing.Color]::FromArgb(255, 139, 124, 255)
        $cut = [System.Drawing.Color]::FromArgb(255, 8, 8, 12)
    } else {
        $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $s, $s), ([System.Drawing.Color]::FromArgb(255, 124, 92, 255)), ([System.Drawing.Color]::FromArgb(255, 72, 52, 212))
        $g.FillPath($bg, $rect)
        $moon = [System.Drawing.Color]::White
        $cut = $null
    }

    # Crescent: a disc with an offset disc removed.
    $r = $s * 0.30
    $cx = $s * 0.50; $cy = $s * 0.50
    $disc = New-Object System.Drawing.Drawing2D.GraphicsPath
    $disc.AddEllipse($cx - $r, $cy - $r, 2 * $r, 2 * $r)
    $bite = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bite.AddEllipse($cx - $r + $s * 0.17, $cy - $r - $s * 0.10, 2 * $r, 2 * $r)
    $region = New-Object System.Drawing.Region $disc
    $region.Exclude($bite)
    $g.FillRegion((New-Object System.Drawing.SolidBrush $moon), $region)

    $g.Dispose()
    return $bmp
}

function Write-Ico([string]$path, [bool]$active) {
    $pngs = foreach ($size in $sizes) {
        $bmp = New-Frame $size $active
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        , $ms.ToArray()
    }

    $out = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $out
    $w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([UInt16]1); $w.Write([UInt16]32)
        $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$offset)
        $offset += $pngs[$i].Length
    }
    foreach ($png in $pngs) { $w.Write($png) }
    $w.Flush()
    [System.IO.File]::WriteAllBytes($path, $out.ToArray())
}

New-Item -ItemType Directory -Force $assets | Out-Null
Write-Ico (Join-Path $assets 'EasyBlackout.ico') $false
Write-Ico (Join-Path $assets 'EasyBlackoutActive.ico') $true
# Also a PNG for the in-app header.
$logo = New-Frame 128 $false
$logo.Save((Join-Path $assets 'Logo.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$logo.Dispose()
Write-Host "Icons written to $assets"
