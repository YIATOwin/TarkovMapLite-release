# Downloads a tarkov.dev XYZ tile pyramid and stitches it into one PNG per layer.
#
# tarkov.dev serves the raster maps as tiles:
#   https://assets.tarkov.dev/maps/labs_v4/1st/{z}/{x}/{y}.png
# Blank tiles are tiny (about 100 bytes), so occupancy is detected by size.
#
#   powershell -File tools\stitch-tiles.ps1 -Map labyrinth -Layer main -Zoom 6
#   powershell -File tools\stitch-tiles.ps1 -Map labs -Prefix labs_v4 -Layer 1st -Zoom 6 -Out assets\maps\the-lab
#
# Output: <Out>\base.png (or <Out>\<Layer>.png when -LayerName is given)
#
# ASCII only: PowerShell 5.1 reads BOM-less scripts as ANSI.

param(
    [Parameter(Mandatory = $true)][string]$Map,        # path segment, e.g. labs_v4 or labyrinth
    [Parameter(Mandatory = $true)][string]$Layer,      # sub folder, e.g. 1st or main
    [int]$Zoom = 6,
    [int]$MaxTile = 24,
    [int]$BlankSize = 400,                             # tiles smaller than this are empty
    [string]$Out = "",
    [string]$LayerName = "",
    [int]$TileSize = 256
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
if (-not $Out) { $Out = Join-Path $root "TarkovMapLite\Assets\maps\$Map" }
New-Item -ItemType Directory -Force -Path $Out | Out-Null

$baseUrl = "https://assets.tarkov.dev/maps/$Map/$Layer/$Zoom"
$tmp = Join-Path $env:TEMP ("tiles-" + $Map + "-" + $Layer + "-" + $Zoom)
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

$script:decoded = @{}
function Test-HasPixels([string]$path) {
    if ($script:decoded.ContainsKey($path)) { return $script:decoded[$path] }
    $result = $false
    try {
        $img = [System.Drawing.Image]::FromFile($path)
        $bmp = New-Object System.Drawing.Bitmap $img
        $img.Dispose()
        for ($yy = 0; $yy -lt $bmp.Height; $yy += 8) {
            for ($xx = 0; $xx -lt $bmp.Width; $xx += 8) {
                if ($bmp.GetPixel($xx, $yy).A -gt 8) { $result = $true; break }
            }
            if ($result) { break }
        }
        $bmp.Dispose()
    } catch {
        $result = $false
    }
    $script:decoded[$path] = $result
    return $result
}
Write-Host "scanning $Map/$Layer at zoom $Zoom ..."
$found = @{}
$minX = [int]::MaxValue; $maxX = [int]::MinValue
$minY = [int]::MaxValue; $maxY = [int]::MinValue
for ($y = 0; $y -le $MaxTile; $y++) {
    for ($x = 0; $x -le $MaxTile; $x++) {
        $file = Join-Path $tmp "$x-$y.png"
        if (-not (Test-Path $file)) {
            & curl.exe -s --max-time 30 -o $file "$baseUrl/$x/$y.png" 2>$null
        }
        if (-not (Test-Path $file)) { continue }
        if (-not (Test-HasPixels $file)) { continue }
        $found["$x-$y"] = $file
        if ($x -lt $minX) { $minX = $x }
        if ($x -gt $maxX) { $maxX = $x }
        if ($y -lt $minY) { $minY = $y }
        if ($y -gt $maxY) { $maxY = $y }
    }
}

if ($found.Count -eq 0) {
    Write-Warning "no tiles found for $Map/$Layer at zoom $Zoom"
    return
}

$cols = $maxX - $minX + 1
$rows = $maxY - $minY + 1
$width = $cols * $TileSize
$height = $rows * $TileSize
Write-Host ("tiles={0} grid={1}x{2} image={3}x{4}" -f $found.Count, $cols, $rows, $width, $height)

$canvas = New-Object System.Drawing.Bitmap $width, $height
$g = [System.Drawing.Graphics]::FromImage($canvas)
$g.Clear([System.Drawing.Color]::Transparent)
$g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
foreach ($key in $found.Keys) {
    $parts = $key.Split('-')
    $x = [int]$parts[0]
    $y = [int]$parts[1]
    $img = [System.Drawing.Image]::FromFile($found[$key])
    $dest = New-Object System.Drawing.Rectangle (($x - $minX) * $TileSize), (($y - $minY) * $TileSize), $img.Width, $img.Height
    $g.DrawImage($img, $dest)
    $img.Dispose()
}
$g.Dispose()

$name = if ($LayerName) { "$LayerName.png" } else { 'base.png' }
$target = Join-Path $Out $name
$canvas.Save($target, [System.Drawing.Imaging.ImageFormat]::Png)
$canvas.Dispose()

$kb = [math]::Round((Get-Item $target).Length / 1KB, 0)
Write-Host ("saved {0} ({1} KB)  grid origin x={2} y={3}" -f $target, $kb, $minX, $minY)
