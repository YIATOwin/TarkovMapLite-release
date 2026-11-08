# Composites a stitched tile layer over its SVG sibling.
#
# tarkov.dev cuts some shapes out of the satellite mosaic (the Interchange mall
# roof, for example) and draws them with vector art on top. On its own the tile
# layer therefore has transparent holes exactly where the most important
# building is. Both images are affine projections of the same world, so the
# mapping from SVG pixels to tile pixels is one affine matrix: build it from
# three world points, draw the SVG under the tiles, and the holes are filled
# while every opaque tile pixel still wins.
#
#   powershell -File tools\composite-tiles.ps1 -Key interchange-tiles
#
# ASCII only: PowerShell 5.1 reads BOM-less scripts as ANSI.

param(
    [Parameter(Mandatory = $true)][string]$Key,        # maps.json key of the tile map, e.g. interchange-tiles
    [string]$SvgKey = "",                              # key of the SVG sibling; defaults to the tile map's dataKey
    [string]$MapsJson = "",
    [string]$TilesDir = "",
    [string]$SvgFile = ""
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'TarkovMapLite\Assets\maps'
if (-not $MapsJson) { $MapsJson = Join-Path $assets 'maps.json' }

$doc = [IO.File]::ReadAllText($MapsJson, [Text.Encoding]::UTF8) | ConvertFrom-Json
$tile = $doc.maps | Where-Object { $_.key -eq $Key } | Select-Object -First 1
if (-not $tile) { throw "no map '$Key' in $MapsJson" }
if (-not $SvgKey) { $SvgKey = if ($tile.dataKey) { $tile.dataKey } else { $Key } }
$svg = $doc.maps | Where-Object { $_.key -eq $SvgKey } | Select-Object -First 1
if (-not $svg) { throw "no SVG map '$SvgKey'" }
if (-not $TilesDir) { $TilesDir = Join-Path $assets $Key }
if (-not $SvgFile) { $SvgFile = Join-Path $assets ($SvgKey + '\base.png') }

$tilePath = Join-Path $TilesDir 'base.png'
if (-not (Test-Path $tilePath)) { throw "missing $tilePath" }
if (-not (Test-Path $SvgFile)) { throw "missing $SvgFile" }

# World -> normalized viewBox position, per map entry.
function Make-Projector($map) {
    $t = $map.transform
    $rot = [double]$map.rotation * [math]::PI / 180.0
    $corners = @()
    foreach ($pair in $map.bounds) {
        $x = [double]$pair[0]; $z = [double]$pair[1]
        $lng = $x * [math]::Cos($rot) - $z * [math]::Sin($rot)
        $lat = $x * [math]::Sin($rot) + $z * [math]::Cos($rot)
        $corners += [pscustomobject]@{ X = ($t[0] * $lng + $t[1]); Y = (-$t[2] * $lat + $t[3]) }
    }
    $minX = ($corners | Measure-Object X -Minimum).Minimum
    $maxX = ($corners | Measure-Object X -Maximum).Maximum
    $minY = ($corners | Measure-Object Y -Minimum).Minimum
    $maxY = ($corners | Measure-Object Y -Maximum).Maximum
    $vbW = [double]$map.viewBox[2]; $vbH = [double]$map.viewBox[3]
    return @{
        Rot = $rot; T = $t; MinX = $minX; MaxX = $maxX; MinY = $minY; MaxY = $maxY; VbW = $vbW; VbH = $vbH
    }
}

function Project($p, [double]$x, [double]$z) {
    $lng = $x * [math]::Cos($p.Rot) - $z * [math]::Sin($p.Rot)
    $lat = $x * [math]::Sin($p.Rot) + $z * [math]::Cos($p.Rot)
    $px = $p.T[0] * $lng + $p.T[1]
    $py = -$p.T[2] * $lat + $p.T[3]
    $u = ($px - $p.MinX) / ($p.MaxX - $p.MinX) * $p.VbW
    $v = ($py - $p.MinY) / ($p.MaxY - $p.MinY) * $p.VbH
    return @($u, $v)
}

$svgImage = [System.Drawing.Image]::FromFile($SvgFile)
$tileImage = [System.Drawing.Image]::FromFile($tilePath)
$ps = Make-Projector $svg
$pt = Make-Projector $tile

# Three world points give the six unknowns of the affine map
#   tile = M * svg    (svg and tile pixels are both plain image pixels here)
$worldPoints = @(@(0.0, 0.0), @(200.0, 0.0), @(0.0, 200.0))
$src = @(); $dst = @()
foreach ($w in $worldPoints) {
    $a = Project $ps $w[0] $w[1]
    $b = Project $pt $w[0] $w[1]
    $src += [pscustomobject]@{
        X = $a[0] / $ps.VbW * $svgImage.Width
        Y = $a[1] / $ps.VbH * $svgImage.Height
    }
    $dst += [pscustomobject]@{
        X = $b[0] / $pt.VbW * $tileImage.Width
        Y = $b[1] / $pt.VbH * $tileImage.Height
    }
}

# Solve for A,B,C in  value = A*x + B*y + C  from three correspondences
# (Cramer's rule; plain scalars keep this readable in PowerShell 5.1).
function Solve-Plane([double]$x1, [double]$y1, [double]$v1,
                     [double]$x2, [double]$y2, [double]$v2,
                     [double]$x3, [double]$y3, [double]$v3) {
    $det = $x1 * ($y2 - $y3) - $y1 * ($x2 - $x3) + ($x2 * $y3 - $x3 * $y2)
    if ([math]::Abs($det) -lt 1e-12) { throw 'singular projection' }
    $a = ($v1 * ($y2 - $y3) - $y1 * ($v2 - $v3) + ($v2 * $y3 - $v3 * $y2)) / $det
    $b = ($x1 * ($v2 - $v3) - $v1 * ($x2 - $x3) + ($x2 * $v3 - $x3 * $v2)) / $det
    $c = ($x1 * ($y2 * $v3 - $y3 * $v2) - $y1 * ($x2 * $v3 - $x3 * $v2) + $v1 * ($x2 * $y3 - $x3 * $y2)) / $det
    return @($a, $b, $c)
}

$mx = Solve-Plane $src[0].X $src[0].Y $dst[0].X  $src[1].X $src[1].Y $dst[1].X  $src[2].X $src[2].Y $dst[2].X
$my = Solve-Plane $src[0].X $src[0].Y $dst[0].Y  $src[1].X $src[1].Y $dst[1].Y  $src[2].X $src[2].Y $dst[2].Y

$width = $tileImage.Width
$height = $tileImage.Height
$canvas = New-Object System.Drawing.Bitmap $width, $height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($canvas)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

# GDI+ Matrix(m11, m12, m21, m22, dx, dy) maps (x,y) to (x*m11 + y*m21 + dx, x*m12 + y*m22 + dy).
$matrix = New-Object System.Drawing.Drawing2D.Matrix $mx[0], $my[0], $mx[1], $my[1], $mx[2], $my[2]
$g.Transform = $matrix
$g.DrawImage($svgImage, (New-Object System.Drawing.RectangleF 0, 0, $svgImage.Width, $svgImage.Height))
$g.ResetTransform()

# Tiles on top: every opaque tile pixel wins, the holes let the SVG through.
$g.DrawImage($tileImage, 0, 0, $width, $height)
$g.Dispose()
$svgImage.Dispose()
$tileImage.Dispose()

$canvas.Save($tilePath, [System.Drawing.Imaging.ImageFormat]::Png)
$canvas.Dispose()

$kb = [math]::Round((Get-Item $tilePath).Length / 1KB, 0)
Write-Host ("composited {0} over {1} -> {2} ({3} KB)" -f $SvgKey, $Key, $tilePath, $kb)
