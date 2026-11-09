# Exports the place-name labels from tarkov-dev's maps.json into Assets/labels.json.
#
# The site draws these names on the map itself; they are not in the json.tarkov.dev
# API at all. Each label is [x, z] in world coordinates plus its text, which is
# exactly what MapLabels.cs needs to project it onto whichever drawing is active.
#
#   powershell -File tools\build-labels.ps1
#
# ASCII only: PowerShell 5.1 reads BOM-less scripts as ANSI.

param(
    [string]$ConfigUrl = "https://raw.githubusercontent.com/the-hideout/tarkov-dev/main/src/data/maps.json",
    [string]$Out = ""
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not $Out) { $Out = Join-Path $root 'TarkovMapLite\Assets\labels.json' }

$config = Join-Path $env:TEMP "tarkov-dev-maps-labels.json"
Write-Host "fetching $ConfigUrl ..."
& curl.exe -s --max-time 60 -o $config $ConfigUrl

$all = [IO.File]::ReadAllText($config, [Text.Encoding]::UTF8) | ConvertFrom-Json

$maps = [ordered]@{}
$total = 0
foreach ($entry in $all) {
    # Only the interactive (tile/svg) layer carries labels; the 2D/3D art has none.
    $layer = $entry.maps | Where-Object { $_.labels -and @($_.labels).Count -gt 0 } | Select-Object -First 1
    if (-not $layer) { continue }
    $list = @()
    foreach ($label in $layer.labels) {
        if (-not $label.text) { continue }
        $x = [double]$label.position[0]
        $z = [double]$label.position[1]
        $size = if ($label.size) { [int]$label.size } else { 70 }
        $list += [pscustomobject]@{ x = [math]::Round($x, 2); z = [math]::Round($z, 2); t = $label.text; s = $size }
    }
    if ($list.Count -eq 0) { continue }
    $maps[$entry.normalizedName] = $list
    $total += $list.Count
    Write-Host ("  {0,-18} {1}" -f $entry.normalizedName, $list.Count)
}

$document = [pscustomobject]@{ maps = $maps }
$json = $document | ConvertTo-Json -Depth 6 -Compress
[IO.File]::WriteAllText($Out, $json, (New-Object System.Text.UTF8Encoding($false)))

$kb = [math]::Round((Get-Item $Out).Length / 1KB, 1)
Write-Host ("saved {0} maps, {1} labels -> {2} ({3} KB)" -f $maps.Count, $total, $Out, $kb)
