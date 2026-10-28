# Converts an image (png/jpg/bmp/ico) into a multi-size .ico for the exe
# and a 256x256 .png for the in-app logo. Called from Logo.targets during build.
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$OutIco,
    [Parameter(Mandatory = $true)][string]$OutPng
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutIco) | Out-Null
$ext = [IO.Path]::GetExtension($Source).ToLowerInvariant()

# Load without locking the source file.
if ($ext -eq '.ico') {
    $icon = New-Object System.Drawing.Icon($Source, 256, 256)
    $image = $icon.ToBitmap()
    $icon.Dispose()
} else {
    $bytes = [IO.File]::ReadAllBytes($Source)
    $stream = New-Object IO.MemoryStream(, $bytes)
    $image = [System.Drawing.Image]::FromStream($stream)
}

function Render-Square([System.Drawing.Image]$img, [int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    # Keep aspect ratio, center inside the square.
    $scale = [Math]::Min($size / $img.Width, $size / $img.Height)
    $w = [int][Math]::Round($img.Width * $scale)
    $h = [int][Math]::Round($img.Height * $scale)
    $g.DrawImage($img, [int](($size - $w) / 2), [int](($size - $h) / 2), $w, $h)
    $g.Dispose()
    return $bmp
}

function To-PngBytes([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return , $ms.ToArray()
}

$big = Render-Square $image 256
$big.Save($OutPng, [System.Drawing.Imaging.ImageFormat]::Png)
$big.Dispose()

if ($ext -eq '.ico') {
    Copy-Item -LiteralPath $Source -Destination $OutIco -Force
} else {
    $sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
    $frames = New-Object 'System.Collections.Generic.List[byte[]]'
    foreach ($s in $sizes) {
        $bmp = Render-Square $image $s
        $frames.Add((To-PngBytes $bmp))
        $bmp.Dispose()
    }

    $fs = [IO.File]::Create($OutIco)
    $bw = New-Object IO.BinaryWriter($fs)
    $bw.Write([UInt16]0)               # reserved
    $bw.Write([UInt16]1)               # type: icon
    $bw.Write([UInt16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
        $bw.Write([byte]$dim)          # width
        $bw.Write([byte]$dim)          # height
        $bw.Write([byte]0)             # palette
        $bw.Write([byte]0)             # reserved
        $bw.Write([UInt16]1)           # color planes
        $bw.Write([UInt16]32)          # bits per pixel
        $bw.Write([UInt32]$frames[$i].Length)
        $bw.Write([UInt32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($f in $frames) { $bw.Write($f) }
    $bw.Dispose()
    $fs.Dispose()
}

$image.Dispose()
Write-Host "Icon: $OutIco"
