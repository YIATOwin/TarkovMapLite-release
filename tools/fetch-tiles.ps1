# Downloads a tarkov.dev tile layer and stitches it into one PNG, using the
# projection parameters straight from tarkov-dev's maps.json.
#
# Why this replaces the old scan-every-tile approach: the config already tells
# us the world bounds, the affine transform and the zoom range, so the occupied
# tile rectangle can be computed instead of probed. That turns a 625-request
# scan into exactly the tiles we need, fetched in parallel.
#
#   powershell -File tools\fetch-tiles.ps1 -Entry woods -Zoom 4 -DryRun
#   powershell -File tools\fetch-tiles.ps1 -Entry woods -Zoom 4 -Out .\TarkovMapLite\Assets\maps\woods-tiles
#
# -DryRun only prints the numbers you need for maps.json (bounds + viewBox).
# Without -DryRun it downloads, stitches, saves base.png and prints the same.
#
# ASCII only: PowerShell 5.1 reads BOM-less scripts as ANSI.

param(
    [Parameter(Mandatory = $true)][string]$Entry,     # normalizedName, e.g. woods
    [int]$Zoom = 4,
    [string]$Layer = "",                              # maps[].key, defaults to $Entry
    [string]$TilePath = "",                           # override the layer's own tilePath (floor layers)
    [string]$Out = "",
    [string]$LayerName = "",                          # "" -> base.png
    [string]$ConfigUrl = "https://raw.githubusercontent.com/the-hideout/tarkov-dev/main/src/data/maps.json",
    [string]$Config = "",
    [int]$Parallel = 16,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# --- config ----------------------------------------------------------------

if (-not $Config) {
    $Config = Join-Path $env:TEMP "tarkov-dev-maps.json"
    if (-not (Test-Path $Config)) {
        Write-Host "fetching $ConfigUrl ..."
        & curl.exe -s --max-time 60 -o $Config $ConfigUrl
    }
}
$all = Get-Content $Config -Raw | ConvertFrom-Json
$map = $all | Where-Object { $_.normalizedName -eq $Entry } | Select-Object -First 1
if (-not $map) { throw "no map entry '$Entry' in $Config" }
if (-not $Layer) { $Layer = $Entry }
$l = $map.maps | Where-Object { $_.key -eq $Layer } | Select-Object -First 1
if (-not $l) { throw "no layer '$Layer' under '$Entry'" }
if ($TilePath) {
    # Floor layers share the map's projection and grid; only the tile folder changes.
    $l.tilePath = $TilePath
} elseif (-not $l.tilePath) {
    throw "layer '$Layer' has no tilePath"
}
if ($Zoom -lt $l.minZoom -or $Zoom -gt $l.maxZoom) {
    Write-Warning ("zoom {0} outside the declared {1}..{2}; continuing anyway" -f $Zoom, $l.minZoom, $l.maxZoom)
}

$t = $l.transform          # [scaleX, marginX, scaleZ, marginZ]
$rot = [double]$l.coordinateRotation
$bounds = $l.bounds        # two opposite world corners

function ConvertTo-Pixel([double]$x, [double]$z) {
    $r = $rot * [math]::PI / 180.0
    $lng = $x * [math]::Cos($r) - $z * [math]::Sin($r)
    $lat = $x * [math]::Sin($r) + $z * [math]::Cos($r)
    return @(($t[0] * $lng + $t[1]), (-$t[2] * $lat + $t[3]))
}

function ConvertTo-World([double]$px, [double]$py) {
    $r = $rot * [math]::PI / 180.0
    $lng = ($px - $t[1]) / $t[0]
    $lat = ($t[3] - $py) / $t[2]
    return @(($lng * [math]::Cos($r) + $lat * [math]::Sin($r)),
             (-$lng * [math]::Sin($r) + $lat * [math]::Cos($r)))
}

# --- tile rectangle --------------------------------------------------------

$x0 = [double]$bounds[0][0]; $z0 = [double]$bounds[0][1]
$x1 = [double]$bounds[1][0]; $z1 = [double]$bounds[1][1]
$pixelMinX = [double]::MaxValue; $pixelMinY = [double]::MaxValue
$pixelMaxX = [double]::MinValue; $pixelMaxY = [double]::MinValue
foreach ($corner in @(@($x0, $z0), @($x1, $z1), @($x0, $z1), @($x1, $z0))) {
    $p = ConvertTo-Pixel $corner[0] $corner[1]
    if ($p[0] -lt $pixelMinX) { $pixelMinX = $p[0] }
    if ($p[0] -gt $pixelMaxX) { $pixelMaxX = $p[0] }
    if ($p[1] -lt $pixelMinY) { $pixelMinY = $p[1] }
    if ($p[1] -gt $pixelMaxY) { $pixelMaxY = $p[1] }
}

$scale = [math]::Pow(2, $Zoom)
$tileSize = 256
$minTileX = [int][math]::Floor($pixelMinX * $scale / $tileSize)
$minTileY = [int][math]::Floor($pixelMinY * $scale / $tileSize)
$maxTileX = [int][math]::Floor(($pixelMaxX * $scale - 1e-6) / $tileSize)
$maxTileY = [int][math]::Floor(($pixelMaxY * $scale - 1e-6) / $tileSize)
$cols = $maxTileX - $minTileX + 1
$rows = $maxTileY - $minTileY + 1
$width = $cols * $tileSize
$height = $rows * $tileSize

# The stitched image covers the whole tile rectangle, so the bounds we hand to
# the app must be that rectangle expressed back in world coordinates.
$offX = $minTileX * $tileSize
$offY = $minTileY * $tileSize
$worldCorners = @(
    (ConvertTo-World ($offX / $scale) ($offY / $scale)),
    (ConvertTo-World (($offX + $width) / $scale) ($offY / $scale)),
    (ConvertTo-World (($offX + $width) / $scale) (($offY + $height) / $scale)),
    (ConvertTo-World ($offX / $scale) (($offY + $height) / $scale))
)

Write-Host ("{0}/{1}  zoom={2} rot={3}" -f $Entry, $Layer, $Zoom, $rot)
Write-Host ("tiles x {0}..{1}  y {2}..{3}  grid {4}x{5}  image {6}x{7}" -f `
    $minTileX, $maxTileX, $minTileY, $maxTileY, $cols, $rows, $width, $height)
$mapEntry = '  "bounds": [' + (($worldCorners | ForEach-Object { '[' + $_[0].ToString('F2') + ',' + $_[1].ToString('F2') + ']' }) -join ', ') + ']'
Write-Host "maps.json:"
Write-Host $mapEntry
Write-Host ("  `"viewBox`": [0, 0, {0}, {1}]," -f $width, $height)

if ($DryRun) { return }

# --- download --------------------------------------------------------------

$root = Split-Path -Parent $PSScriptRoot
if (-not $Out) { $Out = Join-Path $root ("TarkovMapLite\Assets\maps\" + (Split-Path $Entry -Leaf) + "-tiles") }
New-Item -ItemType Directory -Force -Path $Out | Out-Null

$stamp = ($l.tilePath -replace '[^A-Za-z0-9]', '_') + "_z$Zoom"
$tmp = Join-Path $env:TEMP ("tiles-" + $stamp)
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

$cfg = Join-Path $tmp "curl.cfg"
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('silent')
$lines.Add('show-error')
# Bare file names plus --output-dir: a Windows path inside the config file would
# be read as an escape sequence by curl (C:\Users -> C:Users) and every write
# would fail with "client returned ERROR on write".
$lines.Add('output-dir = "' + $tmp.Replace('\', '/') + '"')
$todo = 0
for ($y = $minTileY; $y -le $maxTileY; $y++) {
    for ($x = $minTileX; $x -le $maxTileX; $x++) {
        $file = Join-Path $tmp "$x-$y.png"
        if ((Test-Path $file) -and (Get-Item $file).Length -gt 0) { continue }
        $url = $l.tilePath.Replace('{z}', $Zoom).Replace('{x}', $x).Replace('{y}', $y)
        $lines.Add('url = "' + $url + '"')
        $lines.Add('output = "' + "$x-$y.png" + '"')
        $todo++
    }
}
Write-Host ("downloading {0} of {1} tiles (parallel {2}) ..." -f $todo, ($cols * $rows), $Parallel)
if ($todo -gt 0) {
    Set-Content -Path $cfg -Value $lines -Encoding ASCII
    & curl.exe --parallel --parallel-max $Parallel --config $cfg
}

$missing = 0
for ($y = $minTileY; $y -le $maxTileY; $y++) {
    for ($x = $minTileX; $x -le $maxTileX; $x++) {
        $file = Join-Path $tmp "$x-$y.png"
        if (-not (Test-Path $file)) { $missing++ }
    }
}
if ($missing -gt 0) {
    Write-Warning ("{0} of {1} tiles are missing; the stitched image will have holes" -f $missing, ($cols * $rows))
}

# --- stitch ----------------------------------------------------------------

$canvas = New-Object System.Drawing.Bitmap $width, $height
$g = [System.Drawing.Graphics]::FromImage($canvas)
$g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
$used = 0
for ($y = $minTileY; $y -le $maxTileY; $y++) {
    for ($x = $minTileX; $x -le $maxTileX; $x++) {
        $file = Join-Path $tmp "$x-$y.png"
        if (-not (Test-Path $file)) { continue }
        try {
            $img = [System.Drawing.Image]::FromFile($file)
        } catch {
            continue
        }
        $dest = New-Object System.Drawing.Rectangle (($x - $minTileX) * $tileSize), (($y - $minTileY) * $tileSize), $tileSize, $tileSize
        $g.DrawImage($img, $dest)
        $img.Dispose()
        $used++
    }
}
$g.Dispose()

$name = if ($LayerName) { "$LayerName.png" } else { 'base.png' }
$target = Join-Path $Out $name
$canvas.Save($target, [System.Drawing.Imaging.ImageFormat]::Png)
$canvas.Dispose()

$kb = [math]::Round((Get-Item $target).Length / 1KB, 0)
Write-Host ("drew {0} tiles -> {1} ({2} KB)" -f $used, $target, $kb)
