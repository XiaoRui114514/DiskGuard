# 生成应用图标 assets/DiskGuard.ico（多尺寸 PNG 压缩 ICO）
Add-Type -AssemblyName System.Drawing

$output = Join-Path $PSScriptRoot '..\assets\DiskGuard.ico'
$output = [System.IO.Path]::GetFullPath($output)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($output)) | Out-Null

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$payloads = @()

foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap -ArgumentList ([int]$size), ([int]$size), ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

    $pad = $size * 0.045
    $side = $size - ($pad * 2)
    $radius = $side * 0.24

    $shape = New-RoundedPath $pad $pad $side $side $radius
    $point1 = New-Object System.Drawing.PointF -ArgumentList ([float]0), ([float]0)
    $point2 = New-Object System.Drawing.PointF -ArgumentList ([float]$size), ([float]$size)
    $color1 = [System.Drawing.Color]::FromArgb(255, 59, 130, 246)
    $color2 = [System.Drawing.Color]::FromArgb(255, 23, 62, 160)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush -ArgumentList $point1, $point2, $color1, $color2
    $g.FillPath($brush, $shape)

    # 磁盘盘片
    $platterSize = $side * 0.56
    $platterX = $size / 2 - $platterSize / 2
    $platterY = $size / 2 - $platterSize / 2 - $side * 0.05
    $white = New-Object System.Drawing.SolidBrush -ArgumentList ([System.Drawing.Color]::White)
    $g.FillEllipse($white, $platterX, $platterY, $platterSize, $platterSize)

    $holeSize = $platterSize * 0.34
    $holeX = $size / 2 - $holeSize / 2
    $holeY = $platterY + $platterSize / 2 - $holeSize / 2
    $holeBrush = New-Object System.Drawing.SolidBrush -ArgumentList ([System.Drawing.Color]::FromArgb(255, 23, 62, 160))
    $g.FillEllipse($holeBrush, $holeX, $holeY, $holeSize, $holeSize)

    # 速度线条
    if ($size -ge 32) {
        $pen = New-Object System.Drawing.Pen -ArgumentList ([System.Drawing.Color]::FromArgb(230, 255, 255, 255)), ([float]($size * 0.055))
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $y1 = $platterY + $platterSize + $side * 0.10
        $g.DrawLine($pen, $size * 0.30, $y1, $size * 0.70, $y1)
        $pen.Dispose()
    }

    $g.Dispose()
    $brush.Dispose()
    $white.Dispose()
    $holeBrush.Dispose()
    $shape.Dispose()

    $stream = New-Object System.IO.MemoryStream
    $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $payloads += , $stream.ToArray()
    $stream.Dispose()
    $bmp.Dispose()
}

$file = [System.IO.File]::Create($output)
$writer = New-Object System.IO.BinaryWriter($file)
$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]$sizes.Count)

$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $data = $payloads[$i]
    $writer.Write([byte]$dimension)
    $writer.Write([byte]$dimension)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]$data.Length)
    $writer.Write([UInt32]$offset)
    $offset += $data.Length
}

foreach ($data in $payloads) { $writer.Write($data) }
$writer.Close()

Write-Host "已生成图标: $output"
