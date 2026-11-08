# Builds Assets/markers.json from json.tarkov.dev /pve/maps.
#
# Extracts the static map content tarkov.dev draws on its interactive map:
# loose loot, containers, hazards (minefields), spawn points, stationary
# weapons. Coordinates are rounded to 0.1 m to keep the file small.
#
#   powershell -File tools\build-markers.ps1
#   powershell -File tools\build-markers.ps1 -Source path\to\maps.json
#
# NOTE: keep this file ASCII-only. Windows PowerShell 5.1 reads a BOM-less
# script as ANSI, and non-ASCII comments corrupt later statements.

param(
    [string]$Source = "",
    [string]$OutFile = ""
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $OutFile) { $OutFile = Join-Path $repo 'Assets\markers.json' }

if (-not $Source) {
    $Source = Join-Path $env:TEMP 'jd_maps_full.json'
    if (-not (Test-Path $Source)) {
        Write-Host 'downloading /pve/maps ...'
        & curl.exe -s --max-time 300 -o $Source 'https://json.tarkov.dev/pve/maps'
    }
}

Write-Host "reading $Source"
$json = Get-Content $Source -Raw -Encoding UTF8 | ConvertFrom-Json
$maps = $json.data.maps


$wanted = @(
    'factory','customs','woods','lighthouse','shoreline','reserve',
    'interchange','streets-of-tarkov','ground-zero','terminal',
    'night-factory','infected-factory','ground-zero-21','ground-zero-tutorial',
    'the-labyrinth','the-lab','the-lab-dark','infected-lab'
)

function Round1([double]$v) { return [math]::Round($v, 1) }

$result = [ordered]@{}
$result['generated'] = (Get-Date).ToString('s')

$mapsOut = [ordered]@{}
foreach ($prop in $maps.PSObject.Properties) {
    $m = $prop.Value
    $key = $m.normalizedName
    if ($wanted -notcontains $key) { continue }

    $entry = [ordered]@{}

    $loose = @()
    foreach ($point in $m.lootLoose) {
        if ($null -eq $point.position) { continue }
        $loose += ,@((Round1 $point.position.x), (Round1 $point.position.z))
    }
    if ($loose.Count -gt 0) { $entry['loose'] = $loose }

    $containers = @()
    foreach ($point in $m.lootContainers) {
        if ($null -eq $point.position) { continue }
        $kindId = "$($point.lootContainer)"
        $kindNode = $json.data.lootContainers.$kindId
        $kind = if ($kindNode) { $kindNode.normalizedName } else { "container" }
        $containers += ,@((Round1 $point.position.x), (Round1 $point.position.z), $kind)
    }
    if ($containers.Count -gt 0) { $entry['containers'] = $containers }

    # hazards: minefields have an outline polygon, other types only a point
    $hazardPoints = @()
    $hazardAreas = @()
    foreach ($h in $m.hazards) {
        $type = if ($h.hazardType) { $h.hazardType } else { $h.name }
        if ($h.outline -and $h.outline.Count -ge 3) {
            $ring = @()
            foreach ($p in $h.outline) {
                if ($null -eq $p) { continue }
                $ring += ,@((Round1 $p.x), (Round1 $p.z))
            }
            if ($ring.Count -ge 3) {
                $hazardAreas += ,([ordered]@{ t = $type; p = $ring })
            }
        }
        elseif ($h.position) {
            $hazardPoints += ,([ordered]@{ t = $type; x = (Round1 $h.position.x); z = (Round1 $h.position.z) })
        }
    }
    if ($hazardPoints.Count -gt 0) { $entry['hazards'] = $hazardPoints }
    if ($hazardAreas.Count -gt 0) { $entry['hazardAreas'] = $hazardAreas }

    # spawns: keep only the category that matters for "where can players/bots appear"
    $spawns = @()
    foreach ($sp in $m.spawns) {
        if ($null -eq $sp.position) { continue }
        $cats = @()
        foreach ($c in $sp.categories) {
            $name = "$c"
            if ($name -eq 'player') { $cats += 'p' }
            elseif ($name -like 'bot*') { $cats += 'b' }
            elseif ($name -eq 'boss') { $cats += 'o' }
            elseif ($name -eq 'sniper') { $cats += 's' }
        }
        if ($cats.Count -eq 0) { continue }
        $spawns += ,@((Round1 $sp.position.x), (Round1 $sp.position.z), (-join ($cats | Select-Object -Unique)))
    }
    if ($spawns.Count -gt 0) { $entry['spawns'] = $spawns }

    $statics = @()
    foreach ($sw in $m.stationaryWeapons) {
        if ($null -eq $sw.position) { continue }
        $statics += ,@((Round1 $sw.position.x), (Round1 $sw.position.z))
    }
    if ($statics.Count -gt 0) { $entry['statics'] = $statics }

    if ($entry.Count -gt 0) { $mapsOut[$key] = $entry }

    Write-Host ("  {0,-20} loose={1,-5} containers={2,-5} hazards={3,-4} spawns={4,-5} statics={5}" -f `
        $key, $loose.Count, $containers.Count, ($hazardPoints.Count + $hazardAreas.Count), $spawns.Count, $statics.Count)
}

$result['maps'] = $mapsOut

# ConvertTo-Json is slow and pretty-prints; build compact JSON by hand for size
$sb = New-Object System.Text.StringBuilder
[void]$sb.Append('{"generated":"').Append($result['generated']).Append('","maps":{')
$firstMap = $true
foreach ($mapKey in $mapsOut.Keys) {
    if (-not $firstMap) { [void]$sb.Append(',') }
    $firstMap = $false
    [void]$sb.Append('"').Append($mapKey).Append('":{')
    $entry = $mapsOut[$mapKey]
    $firstKey = $true
    foreach ($k in $entry.Keys) {
        if (-not $firstKey) { [void]$sb.Append(',') }
        $firstKey = $false
        [void]$sb.Append('"').Append($k).Append('":')
        $value = $entry[$k]
        if ($k -eq 'hazards' -or $k -eq 'hazardAreas') {
            [void]$sb.Append((ConvertTo-Json $value -Compress -Depth 6))
        }
        else {
            # [[x,z],...] or [[x,z,"cats"],...]
            [void]$sb.Append('[')
            $firstRow = $true
            foreach ($row in $value) {
                if (-not $firstRow) { [void]$sb.Append(',') }
                $firstRow = $false
                [void]$sb.Append('[')
                for ($i = 0; $i -lt $row.Count; $i++) {
                    if ($i -gt 0) { [void]$sb.Append(',') }
                    $v = $row[$i]
                    if ($v -is [string]) { [void]$sb.Append('"').Append($v).Append('"') }
                    else { [void]$sb.Append(($v.ToString([System.Globalization.CultureInfo]::InvariantCulture))) }
                }
                [void]$sb.Append(']')
            }
            [void]$sb.Append(']')
        }
    }
    [void]$sb.Append('}')
}
[void]$sb.Append('}}')

$dir = Split-Path -Parent $OutFile
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
[IO.File]::WriteAllText($OutFile, $sb.ToString(), (New-Object Text.UTF8Encoding($false)))

$size = [math]::Round((Get-Item $OutFile).Length / 1KB, 0)
Write-Host ("done: {0} maps, {1} KB -> {2}" -f $mapsOut.Count, $size, $OutFile)
