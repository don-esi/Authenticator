Add-Type -AssemblyName System.Drawing
$assets = Split-Path -Parent $MyInvocation.MyCommand.Path
New-Item -ItemType Directory -Force -Path $assets | Out-Null

function Draw-Shield([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    $pad = [int]($size * 0.08)
    $left = $pad
    $top = $pad
    $right = $size - $pad
    $bottom = $size - $pad
    $midX = ($left + $right) / 2.0

    $pts = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($midX, $top),
        [System.Drawing.PointF]::new($right, $top + ($size * 0.08)),
        [System.Drawing.PointF]::new($right, $top + ($size * 0.42)),
        [System.Drawing.PointF]::new($midX, $bottom),
        [System.Drawing.PointF]::new($left, $top + ($size * 0.42)),
        [System.Drawing.PointF]::new($left, $top + ($size * 0.08))
    )

    $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0, 120, 212))
    $g.FillPolygon($brush, $pts)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 0, 90, 158), [Math]::Max(1.0, $size * 0.045))
    $g.DrawPolygon($pen, $pts)

    $cx = $midX
    $bodyW = ($right - $left) * 0.34
    $bodyH = ($bottom - $top) * 0.28
    $bodyX = $cx - $bodyW / 2
    $bodyY = $top + ($size * 0.40)
    $lockTop = $bodyY - ($size * 0.12)
    $shackleR = $bodyW * 0.28
    $stroke = [Math]::Max(1.5, $size * 0.055)

    $pen2 = New-Object System.Drawing.Pen ([System.Drawing.Color]::White, $stroke)
    $pen2.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen2.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $shack = New-Object System.Drawing.RectangleF ($cx - $shackleR), $lockTop, ($shackleR * 2), ($shackleR * 2.1)
    $g.DrawArc($pen2, $shack, 200, 140)

    $radius = [Math]::Max(2.0, $size * 0.04)
    $body = New-Object System.Drawing.Drawing2D.GraphicsPath
    $body.AddArc($bodyX, $bodyY, $radius * 2, $radius * 2, 180, 90)
    $body.AddArc(($bodyX + $bodyW - $radius * 2), $bodyY, $radius * 2, $radius * 2, 270, 90)
    $body.AddArc(($bodyX + $bodyW - $radius * 2), ($bodyY + $bodyH - $radius * 2), $radius * 2, $radius * 2, 0, 90)
    $body.AddArc($bodyX, ($bodyY + $bodyH - $radius * 2), $radius * 2, $radius * 2, 90, 90)
    $body.CloseFigure()
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $g.FillPath($white, $body)

    $holeR = [Math]::Max(1.2, $size * 0.035)
    $holeCy = $bodyY + $bodyH * 0.38
    $blue = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0, 120, 212))
    $g.FillEllipse($blue, ($cx - $holeR), ($holeCy - $holeR), ($holeR * 2), ($holeR * 2))
    $stemW = $holeR * 0.85
    $g.FillRectangle($blue, ($cx - $stemW / 2), $holeCy, $stemW, ($bodyH * 0.38))

    $g.Dispose(); $brush.Dispose(); $pen.Dispose(); $pen2.Dispose(); $body.Dispose(); $white.Dispose(); $blue.Dispose()
    return $bmp
}

function Write-Ico([string]$path, [int[]]$sizes) {
    $pngs = New-Object System.Collections.Generic.List[byte[]]
    foreach ($size in $sizes) {
        $bmp = Draw-Shield $size
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngs.Add($ms.ToArray())
        $preview = Join-Path $assets ("shield-{0}.png" -f $size)
        [System.IO.File]::WriteAllBytes($preview, $ms.ToArray())
        $bmp.Dispose(); $ms.Dispose()
    }

    $fs = [System.IO.File]::Create($path)
    $bw = New-Object System.IO.BinaryWriter $fs
    $bw.Write([uint16]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]$sizes.Length)
    $offset = 6 + (16 * $sizes.Length)
    for ($i = 0; $i -lt $sizes.Length; $i++) {
        $size = $sizes[$i]
        $bw.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
        $bw.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
        $bw.Write([byte]0)
        $bw.Write([byte]0)
        $bw.Write([uint16]1)
        $bw.Write([uint16]32)
        $bw.Write([int]$pngs[$i].Length)
        $bw.Write([int]$offset)
        $offset += $pngs[$i].Length
    }
    foreach ($p in $pngs) { $bw.Write($p) }
    $bw.Flush()
    $fs.Close()
}

$ico = Join-Path $assets 'app.ico'
Write-Ico $ico @(16, 32, 48, 256)
Copy-Item $ico (Join-Path (Split-Path $assets -Parent) 'app.ico') -Force
Get-Item $ico | Format-List FullName, Length
Get-ChildItem $assets -Filter 'shield-*.png' | Select-Object Name, Length
