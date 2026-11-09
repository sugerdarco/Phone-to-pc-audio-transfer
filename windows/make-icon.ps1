# Regenerates app.ico (PNG-compressed multi-size icon). Only needed if you change the design.
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 64, 256
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $k = $s / 256.0
    # rounded square background
    $r = 56 * $k; $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $w = $s - 1
    $path.AddArc(0, 0, 2*$r, 2*$r, 180, 90); $path.AddArc($w-2*$r, 0, 2*$r, 2*$r, 270, 90)
    $path.AddArc($w-2*$r, $w-2*$r, 2*$r, 2*$r, 0, 90); $path.AddArc(0, $w-2*$r, 2*$r, 2*$r, 90, 90); $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $s, $s), ([System.Drawing.Color]::FromArgb(255, 37, 99, 235)), ([System.Drawing.Color]::FromArgb(255, 13, 148, 136))
    $g.FillPath($brush, $path)
    $white = [System.Drawing.Color]::White
    # phone body
    $pen = New-Object System.Drawing.Pen $white, ([Math]::Max(1.5, 14 * $k))
    $g.DrawRectangle($pen, [single](46*$k), [single](52*$k), [single](70*$k), [single](152*$k))
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $white), [single](73*$k), [single](176*$k), [single](16*$k), [single](16*$k))
    # sound waves
    $wpen = New-Object System.Drawing.Pen $white, ([Math]::Max(1.5, 16 * $k))
    $wpen.StartCap = 'Round'; $wpen.EndCap = 'Round'
    foreach ($i in 0..2) {
        $rad = (34 + 34 * $i) * $k
        $cx = 120 * $k; $cy = 128 * $k
        $g.DrawArc($wpen, [single]($cx - $rad), [single]($cy - $rad), [single](2*$rad), [single](2*$rad), -45, 90)
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,($ms.ToArray())
    $bmp.Dispose()
}
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'app.ico'), $out.ToArray())
"app.ico written"
