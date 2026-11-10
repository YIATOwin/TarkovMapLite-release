# Bakes the manual calibration offsets (Ctrl+arrows in the app) into maps.json.
#
# The app writes %LOCALAPPDATA%\TarkovMapLite\calibration.json like
#   { "reserve": [2, -32] }
# which means "add (2, -32) viewBox units to every projected point". Doing that
# at render time is fine for one person, but everyone else would still see the
# misaligned map - so this script folds the offset into the map's `bounds`
# and clears the calibration entry.
#
# Math: u = (px - minX) / (maxX - minX) * viewBoxW, so adding du is the same as
# moving the pixel window left by du * (maxX - minX) / viewBoxW. Shift both
# corners, convert back to world coordinates through the inverse transform, and
# write the result as the new bounds.
#
# Usage: powershell -File tools\bake-calibration.ps1 [-DryRun] [-Keep]
#   -DryRun  print the new bounds without touching files
#   -Keep    leave calibration.json untouched (default: remove the baked keys)
#
# MUST stay pure ASCII.

param(
    [switch]$DryRun,
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$mapsPath = Join-Path $root 'TarkovMapLite\Assets\maps\maps.json'
$calPath = Join-Path $env:LOCALAPPDATA 'TarkovMapLite\calibration.json'

if (-not (Test-Path $calPath)) {
    Write-Host "no calibration file: $calPath"
    exit 0
}

$cal = Get-Content $calPath -Raw -Encoding UTF8 | ConvertFrom-Json
$doc = Get-Content $mapsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$text = [IO.File]::ReadAllText($mapsPath)
$changed = 0

function Find-ArrayEnd([string] $s, [int] $start) {
    # start points at '['; return index just past the matching ']'
    $depth = 0
    for ($i = $start; $i -lt $s.Length; $i++) {
        $ch = $s[$i]
        if ($ch -eq '[') { $depth++ }
        elseif ($ch -eq ']') {
            $depth--
            if ($depth -eq 0) { return $i + 1 }
        }
    }
    throw 'unbalanced brackets'
}

foreach ($prop in $cal.PSObject.Properties) {
    $key = $prop.Name
    $dx = [double]$prop.Value[0]
    $dy = [double]$prop.Value[1]
    if ($dx -eq 0 -and $dy -eq 0) { continue }

    $map = $doc.maps | Where-Object { $_.key -eq $key } | Select-Object -First 1
    if (-not $map) { Write-Host "  skip $key (not in maps.json)"; continue }

    $rot = $map.rotation * [Math]::PI / 180
    $cos = [Math]::Cos($rot)
    $sin = [Math]::Sin($rot)
    $a = $map.transform[0]; $b = $map.transform[1]
    $c = -$map.transform[2]; $d = $map.transform[3]

    $pxs = @(); $pys = @()
    foreach ($bd in $map.bounds) {
        $ln = $bd[0] * $cos - $bd[1] * $sin
        $lt = $bd[0] * $sin + $bd[1] * $cos
        $pxs += ($a * $ln + $b)
        $pys += ($c * $lt + $d)
    }
    $minX = ($pxs | Measure-Object -Minimum).Minimum
    $maxX = ($pxs | Measure-Object -Maximum).Maximum
    $minY = ($pys | Measure-Object -Minimum).Minimum
    $maxY = ($pys | Measure-Object -Maximum).Maximum

    $shiftX = -$dx * ($maxX - $minX) / $map.viewBox[2]
    $shiftY = -$dy * ($maxY - $minY) / $map.viewBox[3]

    $n1 = @(($minX + $shiftX), ($minY + $shiftY))
    $n2 = @(($maxX + $shiftX), ($maxY + $shiftY))
    # pixel -> world (inverse of MapPack.ToPixel)
    $corners = @()
    foreach ($p in @($n1, $n2)) {
        $lng = ($p[0] - $b) / $a
        $lat = ($p[1] - $d) / $c
        $x = $lng * $cos + $lat * $sin
        $z = -$lng * $sin + $lat * $cos
        $corners += , @([Math]::Round($x, 2), [Math]::Round($z, 2))
    }
    $boundsJson = "[[" + $corners[0][0] + "," + $corners[0][1] + "],[" + $corners[1][0] + "," + $corners[1][1] + "]]"
    Write-Host ("  {0,-16} offset ({1},{2}) -> bounds {3}" -f $key, $dx, $dy, $boundsJson)

    if ($DryRun) { continue }

    # Locate this map's "bounds":[...] inside the raw text (keep the file formatting)
    $keyAt = $text.IndexOf('"key":"' + $key + '"')
    if ($keyAt -lt 0) { Write-Host "    (cannot locate key text)"; continue }
    $boundsAt = $text.IndexOf('"bounds":', $keyAt)
    if ($boundsAt -lt 0) { continue }
    $arrStart = $text.IndexOf('[', $boundsAt)
    $arrEnd = Find-ArrayEnd $text $arrStart
    $text = $text.Substring(0, $arrStart) + $boundsJson + $text.Substring($arrEnd)
    $changed++
}

if (-not $DryRun -and $changed -gt 0) {
    [IO.File]::WriteAllText($mapsPath, $text, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "updated $changed map(s) in maps.json"
    if (-not $Keep) {
        Remove-Item $calPath -Force
        Write-Host 'calibration.json removed (offsets are baked in now)'
    }
}
