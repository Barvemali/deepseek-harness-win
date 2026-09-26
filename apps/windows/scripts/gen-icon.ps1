# Generates DshShell/Assets/AppIcon.ico (16/32/256 PNG frames in one ICO).
# Run from anywhere: pwsh apps/windows/scripts/gen-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$outDir = Join-Path $PSScriptRoot '..\DshShell\Assets'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$out = Join-Path $outDir 'AppIcon.ico'

function New-DshPng {
    param([int]$Size)
    $bmp = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded-square background, gradient deep blue -> teal.
    $rect = New-Object System.Drawing.Rectangle 1, 1, ($Size - 2), ($Size - 2)
    $d = [int]($Size * 0.48)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (
        $rect,
        [System.Drawing.Color]::FromArgb(255, 37, 99, 235),
        [System.Drawing.Color]::FromArgb(255, 14, 165, 233),
        45
    )
    $g.FillPath($brush, $path)

    # Terminal prompt chevron ">" plus an underscore cursor, in white.
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White, [single]($Size * 0.13))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $w = $Size * 0.42
    $h = $Size * 0.28
    $cx = $Size / 2
    $cy = $Size / 2
    $points = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new([single]($cx - $w / 2), [single]($cy - $h / 2)),
        [System.Drawing.PointF]::new([single]($cx + $w / 2), [single]$cy),
        [System.Drawing.PointF]::new([single]($cx - $w / 2), [single]($cy + $h / 2))
    )
    $g.DrawLines($pen, $points)
    $g.DrawLine($pen, [single]($cx - $w * 0.30), [single]($cy + $h * 0.68), [single]($cx + $w * 0.30), [single]($cy + $h * 0.68))

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $g.Dispose()
    $bmp.Dispose()
    $brush.Dispose()
    $pen.Dispose()
    $path.Dispose()
    $ms.Dispose()
    return [byte[]]$bytes
}

$sizes = @(16, 32, 256)
$images = [System.Collections.Generic.List[byte[]]]::new()
foreach ($s in $sizes) {
    $png = [byte[]](New-DshPng $s)
    if ($png.Length -lt 100) {
        throw "PNG frame for ${s}px is unexpectedly small ($($png.Length) bytes)"
    }
    $images.Add($png)
    Write-Host "frame ${s}px: $($png.Length) bytes"
}

$count = $images.Count
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $ms
$bw.Write([uint16]0)   # reserved
$bw.Write([uint16]1)   # type: icon
$bw.Write([uint16]$count)
$offset = 6 + 16 * $count
for ($i = 0; $i -lt $count; $i++) {
    $s = $sizes[$i]
    $w = if ($s -ge 256) { 0 } else { $s }  # 0 means 256
    $bw.Write([byte]$w)
    $bw.Write([byte]$w)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([uint16]1)  # planes
    $bw.Write([uint16]32) # bpp
    $bw.Write([uint32]$images[$i].Length)
    $bw.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) {
    $bw.Write($img)
}
$bw.Flush()
[System.IO.File]::WriteAllBytes($out, $ms.ToArray())
Write-Host "wrote $out ($((Get-Item $out).Length) bytes, $count frames)"
