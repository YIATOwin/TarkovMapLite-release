# Builds Assets/doors.json: locked doors (with the name of the key that opens
# them) and power switches, so the app can draw them offline.
#
# Source: json.tarkov.dev /pve/maps
#   maps[].locks[]    { lockType: "door", key: <itemId>, needsPower, position }
#   maps[].switches[] { id, name (internal asset name, e.g. switch_00409_power),
#                       position }
# Key names come from /pve/items_zh, which is a flat translation dictionary:
#   "5913915886f774123603c392 Name" -> "Military base checkpoint key" (zh)
# Do NOT ConvertFrom-Json that file: it has keys that differ only in case
# ("Front_plate" / "front_plate") and PowerShell throws DuplicateKeysInJsonString.
# A flat regex over "key":"value" pairs is enough and safe.
#
# Switch names stay in their internal form here on purpose: there is no Chinese
# translation for them anywhere in the API, and this script must stay pure ASCII
# (PS 5.1 reads BOM-less UTF-8 as ANSI, so a CJK literal would break parsing).
# The app maps them to readable labels in MapDoors.cs.
#
# Output (world coordinates, rounded to 0.1 m):
#   { "customs": { "doors":    [[x, z, "<key name>", powered, y], ...],
#                  "switches": [[x, z, "<internal switch name>", y], ...] } }
#
# NOTE: extracts[].switches[] is NOT usable as "which extracts does this switch
# open". On customs all 28 extracts reference the very same switch id, so the
# field carries no information. Do not resurrect that idea.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

$root = Split-Path -Parent $PSScriptRoot
$outFile = Join-Path $root 'Assets\doors.json'

function Get-Json([string] $url) {
    $client = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromMinutes(3)
    try {
        return $client.GetStringAsync($url).GetAwaiter().GetResult()
    }
    finally {
        $client.Dispose()
    }
}

function Round1([double] $v) { return [Math]::Round($v, 1) }

Write-Host 'Fetching maps...'
$mapsText = Get-Json 'https://json.tarkov.dev/pve/maps'
$mapsDoc = $mapsText | ConvertFrom-Json
$maps = if ($mapsDoc.data) { $mapsDoc.data.maps } else { $mapsDoc.maps }

Write-Host 'Fetching item translations...'
$zh = Get-Json 'https://json.tarkov.dev/pve/items_zh'
$names = @{}
foreach ($m in [regex]::Matches($zh, '"([^"]{1,80})":"([^"]*)"')) {
    $names[$m.Groups[1].Value] = $m.Groups[2].Value
}
Write-Host ("  translations: {0}" -f $names.Count)

$result = [ordered]@{}
$totalDoors = 0
$totalSwitches = 0
$missingNames = 0

foreach ($entry in $maps.PSObject.Properties) {
    $map = $entry.Value
    $key = "$($map.normalizedName)"
    if ([string]::IsNullOrWhiteSpace($key)) { continue }

    $doors = @()
    foreach ($lock in @($map.locks)) {
        if ($null -eq $lock) { continue }
        if ("$($lock.lockType)" -ne 'door') { continue }
        if ($null -eq $lock.position) { continue }
        $keyId = "$($lock.key)"
        $keyName = $names["$keyId Name"]
        if ([string]::IsNullOrWhiteSpace($keyName)) {
            $missingNames++
            $keyName = $keyId
        }
        $powered = if ($lock.needsPower -eq $true) { 1 } else { 0 }
        $height = if ($null -ne $lock.position.y) { Round1 $lock.position.y } else { $null }
        $doors += , @((Round1 $lock.position.x), (Round1 $lock.position.z), $keyName, $powered, $height)
    }

    $switches = @()
    foreach ($sw in @($map.switches)) {
        if ($null -eq $sw -or $null -eq $sw.position) { continue }
        $height = if ($null -ne $sw.position.y) { Round1 $sw.position.y } else { $null }
        $switches += , @((Round1 $sw.position.x), (Round1 $sw.position.z), "$($sw.name)", $height)
    }

    if ($doors.Count -eq 0 -and $switches.Count -eq 0) { continue }
    $result[$key] = [ordered]@{}
    if ($doors.Count -gt 0) { $result[$key]['doors'] = $doors }
    if ($switches.Count -gt 0) { $result[$key]['switches'] = $switches }
    $totalDoors += $doors.Count
    $totalSwitches += $switches.Count
    Write-Host ("  {0,-22} doors={1,-4} switches={2}" -f $key, $doors.Count, $switches.Count)
}

$json = $result | ConvertTo-Json -Depth 6 -Compress
[IO.File]::WriteAllText($outFile, $json, (New-Object System.Text.UTF8Encoding($false)))
$size = [Math]::Round((Get-Item $outFile).Length / 1KB, 1)
Write-Host ""
Write-Host ("Wrote {0} ({1} KB): {2} doors, {3} switches, {4} missing key names" -f `
    $outFile, $size, $totalDoors, $totalSwitches, $missingNames)
