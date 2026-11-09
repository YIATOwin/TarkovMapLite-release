# Shrinks a tile map: crops the transparent border and re-encodes the base image
# as JPEG, then rewrites the map's bounds/viewBox in maps.json.
#
# Why crop first: JPEG has no alpha, so the transparent border would turn into a
# black frame. Cropping to the opaque box fixes both the frame and the wasted
# pixels at once. Floor layers keep PNG (they must stay transparent over the
# base) but are cropped with the SAME rectangle so they still line up.
#
#   powershell -File tools\shrink-tiles.ps1 -Key customs-tiles
#   powershell -File tools\shrink-tiles.ps1 -Key all
#
# ASCII only: PowerShell 5.1 reads BOM-less scripts as ANSI.

param(
    [Parameter(Mandatory = $true)][string]$Key,
    [int]$Quality = 92,
    [string]$MapsJson = "",
    [string]$AssetsDir = "",
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
if (-not $AssetsDir) { $AssetsDir = Join-Path $root 'TarkovMapLite\Assets\maps' }
if (-not $MapsJson) { $MapsJson = Join-Path $AssetsDir 'maps.json' }

$doc = [IO.File]::ReadAllText($MapsJson, [Text.Encoding]::UTF8) | ConvertFrom-Json
$keys = if ($Key -eq 'all') {
    @($doc.maps | Where-Object { $_.variantOf } | ForEach-Object { $_.key })
} else {
    @($Key)
}

$jpegCodec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() |
    Where-Object { $_.MimeType -eq 'image/jpeg' } | Select-Object -First 1

function Get-OpaqueBox($path, [int]$step = 4) {
    $bmp = New-Object System.Drawing.Bitmap $path
    $rect = New-Object System.Drawing.Rectangle 0, 0, $bmp.Width, $bmp.Height
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $bytes = New-Object byte[] ($stride * $bmp.Height)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $bmp.UnlockBits($data)
    $width = $bmp.Width
    $height = $bmp.Height
    $bmp.Dispose()

    $minX = $width; $minY = $height; $maxX = -1; $maxY = -1
    for ($y = 0; $y -lt $height; $y += $step) {
        $row = $y * $stride
        for ($x = 0; $x -lt $width; $x += $step) {
            if ($bytes[$row + $x * 4 + 3] -gt 8) {
                if ($x -lt $minX) { $minX = $x }
                if ($x -gt $maxX) { $maxX = $x }
                if ($y -lt $minY) { $minY = $y }
                if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }
    if ($maxX -lt 0) {
        return $null
    }
    # snap outward to the sampling grid, then clamp
    $minX = [math]::Max(0, $minX - $step)
    $minY = [math]::Max(0, $minY - $step)
    $maxX = [math]::Min($width - 1, $maxX + $step)
    $maxY = [math]::Min($height - 1, $maxY + $step)
    return [pscustomobject]@{
        X = $minX; Y = $minY
        Width = $maxX - $minX + 1
        Height = $maxY - $minY + 1
        FullWidth = $width
        FullHeight = $height
    }
}

function Save-Crop($sourcePath, $targetPath, $box, [bool]$asJpeg) {
    $src = [System.Drawing.Image]::FromFile($sourcePath)
    $fmt = if ($asJpeg) { [System.Drawing.Imaging.PixelFormat]::Format24bppRgb } else { [System.Drawing.Imaging.PixelFormat]::Format32bppArgb }
    $canvas = New-Object System.Drawing.Bitmap $box.Width, $box.Height, $fmt
    $g = [System.Drawing.Graphics]::FromImage($canvas)
    if ($asJpeg) {
        # JPEG has no alpha: paint the app's background colour instead of black
        $g.Clear([System.Drawing.Color]::FromArgb(13, 15, 17))
    } else {
        $g.Clear([System.Drawing.Color]::Transparent)
    }
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $srcRect = New-Object System.Drawing.Rectangle $box.X, $box.Y, $box.Width, $box.Height
    $dstRect = New-Object System.Drawing.Rectangle 0, 0, $box.Width, $box.Height
    $g.DrawImage($src, $dstRect, $srcRect, [System.Drawing.GraphicsUnit]::Pixel)
    $g.Dispose()
    $src.Dispose()

    if ($asJpeg) {
        $params = New-Object System.Drawing.Imaging.EncoderParameters 1
        $params.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), $Quality
        $canvas.Save($targetPath, $jpegCodec, $params)
    } else {
        $canvas.Save($targetPath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    $canvas.Dispose()
}

# world <-> transform-pixel helpers (mirror of MapPack.ToPixel / its inverse)
function ConvertTo-Pixel($entry, [double]$x, [double]$z) {
    $t = $entry.transform
    $rot = [double]$entry.rotation * [math]::PI / 180.0
    $lng = $x * [math]::Cos($rot) - $z * [math]::Sin($rot)
    $lat = $x * [math]::Sin($rot) + $z * [math]::Cos($rot)
    return @(($t[0] * $lng + $t[1]), (-$t[2] * $lat + $t[3]))
}

function ConvertTo-World($entry, [double]$px, [double]$py) {
    $t = $entry.transform
    $rot = [double]$entry.rotation * [math]::PI / 180.0
    $lng = ($px - $t[1]) / $t[0]
    $lat = ($t[3] - $py) / $t[2]
    return @(($lng * [math]::Cos($rot) + $lat * [math]::Sin($rot)),
             (-$lng * [math]::Sin($rot) + $lat * [math]::Cos($rot)))
}

$totalBefore = 0
$totalAfter = 0
$updates = @()
$mapsText = [IO.File]::ReadAllText($MapsJson, [Text.Encoding]::UTF8)
foreach ($mapKey in $keys) {
    $entry = $doc.maps | Where-Object { $_.key -eq $mapKey } | Select-Object -First 1
    if (-not $entry) { throw "no map '$mapKey'" }
    $dir = Join-Path $AssetsDir $mapKey
    $basePath = Join-Path $dir 'base.png'
    if (-not (Test-Path $basePath)) { throw "missing $basePath" }

    $box = Get-OpaqueBox $basePath
    if (-not $box) { throw "$mapKey base is fully transparent" }

    $oldView = @([double]$entry.viewBox[2], [double]$entry.viewBox[3])
    # pixel rect of the old bounds, in transform units
    $corners = @()
    foreach ($pair in $entry.bounds) {
        $corners += ,(ConvertTo-Pixel $entry ([double]$pair[0]) ([double]$pair[1]))
    }
    $minX = ($corners | ForEach-Object { $_[0] } | Measure-Object -Minimum).Minimum
    $maxX = ($corners | ForEach-Object { $_[0] } | Measure-Object -Maximum).Maximum
    $minY = ($corners | ForEach-Object { $_[1] } | Measure-Object -Minimum).Minimum
    $maxY = ($corners | ForEach-Object { $_[1] } | Measure-Object -Maximum).Maximum

    # the crop rectangle expressed in transform units
    $nx0 = $minX + $box.X / $oldView[0] * ($maxX - $minX)
    $nx1 = $minX + ($box.X + $box.Width) / $oldView[0] * ($maxX - $minX)
    $ny0 = $minY + $box.Y / $oldView[1] * ($maxY - $minY)
    $ny1 = $minY + ($box.Y + $box.Height) / $oldView[1] * ($maxY - $minY)

    $worldCorners = @(
        (ConvertTo-World $entry $nx0 $ny0),
        (ConvertTo-World $entry $nx1 $ny0),
        (ConvertTo-World $entry $nx1 $ny1),
        (ConvertTo-World $entry $nx0 $ny1)
    )
    $boundsText = '[' + (($worldCorners | ForEach-Object { '[' + $_[0].ToString('F2') + ',' + $_[1].ToString('F2') + ']' }) -join ', ') + ']'

    $before = ((Get-ChildItem $dir -File | Measure-Object Length -Sum).Sum)
    Write-Host ("{0}: crop {1},{2} {3}x{4} (was {5}x{6})" -f `
        $mapKey, $box.X, $box.Y, $box.Width, $box.Height, $box.FullWidth, $box.FullHeight)

    if (-not $DryRun) {
        # base -> jpeg, floors -> png, all cropped with the same rectangle
        Save-Crop $basePath (Join-Path $dir 'base.jpg') $box $true
        foreach ($file in Get-ChildItem $dir -Filter '*.png' -File) {
            if ($file.Name -eq 'base.png') { continue }
            Save-Crop $file.FullName $file.FullName $box $false
        }
        Remove-Item $basePath -Force
        $updates += [pscustomobject]@{
            Key = $mapKey
            Bounds = $boundsText
            ViewBox = ('[0, 0, {0}, {1}]' -f $box.Width, $box.Height)
        }
    }

    $after = ((Get-ChildItem $dir -File | Measure-Object Length -Sum).Sum)
    $totalBefore += $before
    $totalAfter += $after
    Write-Host ("  {0:N1} MB -> {1:N1} MB" -f ($before / 1MB), ($after / 1MB))
}

if (-not $DryRun) {
    # Patch only the two fields, inside each map's own object. Rewriting the whole
    # document through ConvertTo-Json would re-escape everything and risk the rest.
    foreach ($update in $updates) {
        $start = $mapsText.IndexOf('{"key":"' + $update.Key + '"')
        if ($start -lt 0) { throw "cannot locate entry $($update.Key)" }
        $next = $mapsText.IndexOf('{"key":"', $start + 1)
        $length = if ($next -lt 0) { $mapsText.Length - $start } else { $next - $start }
        $body = $mapsText.Substring($start, $length)
        $patched = [regex]::Replace($body, '("bounds":)\[\[[^\]]*\](?:,\[[^\]]*\])*\]', ('${1}' + $update.Bounds))
        $patched = [regex]::Replace($patched, '("viewBox":)\[[^\]]*\]', ('${1}' + $update.ViewBox))
        if ($patched -eq $body) { throw "no bounds/viewBox replaced for $($update.Key)" }
        $mapsText = $mapsText.Substring(0, $start) + $patched + $mapsText.Substring($start + $length)
    }
    [IO.File]::WriteAllText($MapsJson, $mapsText, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host ("updated {0}" -f $MapsJson)
}
Write-Host ("total {0:N1} MB -> {1:N1} MB" -f ($totalBefore / 1MB), ($totalAfter / 1MB))
