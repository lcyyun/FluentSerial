# Generates Assets/app.ico (multi-size, 32bpp BMP entries) and Assets/app-icon.png.
# FluentSerial icon: a deep-blue rounded square with a native console prompt.
param(
    [string]$AssetsDir = (Join-Path $PSScriptRoot '..\native\FluentSerial\Assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$AssetsDir = [System.IO.Path]::GetFullPath($AssetsDir)
New-Item -ItemType Directory -Force -Path $AssetsDir | Out-Null

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)

        $rect = New-Object System.Drawing.RectangleF(0, 0, $size, $size)
        $radius = [single]($size * 0.22)
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = $radius * 2
        $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
        $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
        $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
        $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
        $path.CloseFigure()

        $top = [System.Drawing.ColorTranslator]::FromHtml('#3B7291')
        $bottom = [System.Drawing.ColorTranslator]::FromHtml('#274F66')
        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $top, $bottom, 90.0)
        $g.FillPath($brush, $path)
        $brush.Dispose()

        # Subtle inner highlight along the top edge.
        $penWidth = [single][Math]::Max(1, $size * 0.012)
        $highlight = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(70, 255, 255, 255), $penWidth)
        $g.DrawPath($highlight, $path)
        $highlight.Dispose()

        # Prompt chevron: ">"
        $chevron = New-Object System.Drawing.Drawing2D.GraphicsPath
        $x0 = [single]($size * 0.255)
        $x1 = [single]($size * 0.43)
        $x2 = [single]($size * 0.255)
        $y0 = [single]($size * 0.345)
        $y1 = [single]($size * 0.5)
        $y2 = [single]($size * 0.655)
        $chevron.AddLine($x0, $y0, $x1, $y1)
        $chevron.AddLine($x1, $y1, $x2, $y2)
        $stroke = [Math]::Max(1.6, $size * 0.088)
        $white = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(245, 255, 255, 255), [single]$stroke)
        $white.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $white.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $white.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $g.DrawPath($white, $chevron)
        $white.Dispose()
        $chevron.Dispose()

        # Cursor bar: "_"
        $cursorPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(245, 255, 255, 255), [single]$stroke)
        $cursorPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $cursorPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $g.DrawLine($cursorPen,
            [single]($size * 0.505), [single]($size * 0.655),
            [single]($size * 0.70), [single]($size * 0.655))
        $cursorPen.Dispose()

        # Tiny status dot for the "live port" cue.
        if ($size -ge 32) {
            $dot = [single]($size * 0.17)
            $dotBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(235, 158, 207, 166))
            $g.FillEllipse($dotBrush, [single]($size * 0.705), [single]($size * 0.215), $dot, $dot)
            $dotBrush.Dispose()
        }
    }
    finally {
        $g.Dispose()
    }
    return $bmp
}

# --- PNG (title bar / docs) -------------------------------------------------
$png = New-IconBitmap 256
$pngPath = Join-Path $AssetsDir 'app-icon.png'
$png.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
$png.Dispose()

# --- ICO with uncompressed 32bpp BMP frames --------------------------------
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @()

foreach ($size in $sizes) {
    $bmp = New-IconBitmap $size
    try {
        $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
        $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $stride = $data.Stride
            $xor = New-Object byte[] ($size * $size * 4)
            $row = New-Object byte[] ([Math]::Abs($stride))
            for ($y = 0; $y -lt $size; $y++) {
                # BMP frames are bottom-up.
                [System.Runtime.InteropServices.Marshal]::Copy([IntPtr]($data.Scan0.ToInt64() + $y * $stride), $row, 0, $row.Length)
                $destRow = $size - 1 - $y
                for ($x = 0; $x -lt $size; $x++) {
                    $src = $x * 4
                    $dst = ($destRow * $size + $x) * 4
                    $xor[$dst + 0] = $row[$src + 0] # B
                    $xor[$dst + 1] = $row[$src + 1] # G
                    $xor[$dst + 2] = $row[$src + 2] # R
                    $xor[$dst + 3] = $row[$src + 3] # A
                }
            }
        }
        finally {
            $bmp.UnlockBits($data)
        }
    }
    finally {
        $bmp.Dispose()
    }

    $andRowBytes = [int]([Math]::Ceiling($size / 32.0) * 4)
    $andMask = New-Object byte[] ($andRowBytes * $size)

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([uint32]40)              # biSize
    $bw.Write([int32]$size)            # biWidth
    $bw.Write([int32]($size * 2))      # biHeight (XOR + AND)
    $bw.Write([uint16]1)               # biPlanes
    $bw.Write([uint16]32)              # biBitCount
    $bw.Write([uint32]0)               # biCompression BI_RGB
    $bw.Write([uint32]($xor.Length + $andMask.Length))
    $bw.Write([int32]0); $bw.Write([int32]0)
    $bw.Write([uint32]0); $bw.Write([uint32]0)
    $bw.Write($xor)
    $bw.Write($andMask)
    $bw.Flush()
    $frames += [pscustomobject]@{ Size = $size; Bytes = $ms.ToArray() }
    $bw.Dispose()
    $ms.Dispose()
}

$icoPath = Join-Path $AssetsDir 'app.ico'
$fs = [System.IO.File]::Create($icoPath)
$writer = New-Object System.IO.BinaryWriter($fs)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)
    $offset = 6 + (16 * $frames.Count)
    foreach ($frame in $frames) {
        $dim = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dim)
        $writer.Write([byte]$dim)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write($frame.Bytes) }
}
finally {
    $writer.Dispose()
    $fs.Dispose()
}

Write-Host "Generated $icoPath ($($frames.Count) sizes) and $pngPath"
