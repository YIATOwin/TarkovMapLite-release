# Rasterizes every bundled map SVG into per-floor PNGs for TarkovMapLite.
#
# Uses a headless Edge/Chrome so the native tool never needs an SVG parser and
# the output matches the browser pixel for pixel.
#
#   powershell -File tools\rasterize-maps.ps1 [-Scale 2] [-MaxEdge 2600]
#
# NOTE: keep this file ASCII-only. Windows PowerShell 5.1 reads a BOM-less
# script as ANSI, and non-ASCII comments end up mangling subsequent statements.

param(
    [double]$MaxScale = 24.0,
    [int]$MaxEdge = 4000,
    [string]$Only = ""
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$srcDir = Join-Path $repo 'TarkovMonitor\wwwroot\maps'
$outDir = Join-Path $repo 'TarkovMapLite\Assets\maps'
$tmpDir = Join-Path $env:TEMP 'tm-rasterize'

New-Item -ItemType Directory -Force -Path $outDir, $tmpDir | Out-Null

$edge = @(
    'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe',
    'C:\Program Files\Microsoft\Edge\Application\msedge.exe',
    'C:\Program Files\Google\Chrome\Application\chrome.exe'
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'no Edge/Chrome found for rasterization' }

$pack = Get-Content (Join-Path $srcDir 'maps.json') -Raw | ConvertFrom-Json

function Get-ScopedStyles {
    param([string]$raw, [string]$svgId, [object[]]$layers, [string]$activeLayer)
    $sb = [System.Text.StringBuilder]::new()
    $m = [regex]::Match($raw, '<style[^>]*>([\s\S]*?)</style>')
    if ($m.Success) {
        foreach ($block in $m.Groups[1].Value.Split('}')) {
            $brace = $block.IndexOf('{')
            if ($brace -lt 0) { continue }
            $sel = $block.Substring(0, $brace).Trim()
            $body = $block.Substring($brace + 1).Trim()
            if (-not $sel) { continue }
            $scoped = ($sel.Split(',') | ForEach-Object { "#$svgId " + $_.Trim() }) -join ', '
            [void]$sb.Append($scoped).Append('{').Append($body).Append('}')
        }
    }
    # ground is visible only for the base image; each floor image shows just that floor
    $groundVisible = [string]::IsNullOrEmpty($activeLayer)
    # 地面分组的 id 每张 SVG 不一样：海关是 Ground_Level，工厂是 Ground_Floor。
    # 写死一个 id 会静默失效（CSS 匹配不到就不隐藏），所以这里现探。
    $groundId = ""
    if ($raw -match '<g\s+[^>]*id="(Ground[^"]*)"') { $groundId = $Matches[1] }
    if ($groundId) {
        [void]$sb.Append("#$svgId #$groundId{display:" + $(if ($groundVisible) { 'inline' } else { 'none' }) + '}')
    }
    foreach ($layer in $layers) {
        if (-not $layer.svgLayer) { continue }
        $visible = ($layer.svgLayer -eq $activeLayer)
        [void]$sb.Append("#$svgId #$($layer.svgLayer){display:" + $(if ($visible) { 'inline' } else { 'none' }) + '}')
    }
    $sb.ToString()
}

$index = @()
foreach ($map in $pack.maps) {
    if ($Only -and $map.svg -notlike "*$Only*") { continue }
    $svgPath = Join-Path $srcDir $map.svg
    if (-not (Test-Path $svgPath)) { Write-Warning "missing svg: $svgPath"; continue }
    $raw = Get-Content $svgPath -Raw
    $rootMatch = [regex]::Match($raw, '<svg[^>]*>')
    if (-not $rootMatch.Success) { Write-Warning "no <svg> in $($map.svg)"; continue }
    $idMatch = [regex]::Match($rootMatch.Value, 'id="([^"]+)"')
    $svgId = if ($idMatch.Success) { $idMatch.Groups[1].Value } else { 'map-svg' }

    $vb = @([double]$map.viewBox[0], [double]$map.viewBox[1], [double]$map.viewBox[2], [double]$map.viewBox[3])
    # Small maps (Factory viewBox is only 131x141) look blurry if rasterized at a
    # fixed 2x: at fit zoom the app upscales them 4x or more. So aim every map at
    # roughly MaxEdge pixels on its long edge, with MaxScale as a safety cap.
    $scale = [Math]::Min($MaxScale, $MaxEdge / [Math]::Max($vb[2], $vb[3]))
    $w = [int][Math]::Round($vb[2] * $scale)
    $h = [int][Math]::Round($vb[3] * $scale)
    if ($w -le 1 -or $h -le 1) {
        throw "bad size for $($map.key): viewBox=$($vb -join ',') scale=$scale"
    }
    Write-Host ("  [{0}] viewBox={1}x{2} scale={3:F2} -> {4}x{5}" -f $map.key, $vb[2], $vb[3], $scale, $w, $h)

    $mapOut = Join-Path $outDir $map.key
    New-Item -ItemType Directory -Force -Path $mapOut | Out-Null

    # base (ground only) plus one image per real floor layer
    $variants = @('base')
    foreach ($layer in $map.layers) {
        if ($layer.svgLayer -and $variants -notcontains $layer.svgLayer) { $variants += $layer.svgLayer }
    }

    foreach ($variant in $variants) {
        $active = if ($variant -eq 'base') { '' } else { $variant }
        $styles = Get-ScopedStyles -raw $raw -svgId $svgId -layers $map.layers -activeLayer $active

        $markup = $raw
        $styleMatch = [regex]::Match($raw, '<style[^>]*>[\s\S]*?</style>')
        if ($styleMatch.Success) {
            $markup = $markup.Replace($styleMatch.Value, "<style>$styles</style>")
        } else {
            $markup = $markup.Replace($rootMatch.Value, $rootMatch.Value + "<style>$styles</style>")
        }
        # size the svg explicitly; the source files no longer carry width/height
        $sized = $rootMatch.Value.Insert($rootMatch.Value.Length - 1,
            " width=`"$w`" height=`"$h`" style=`"display:block`"")
        $markup = $markup.Replace($rootMatch.Value, $sized)

        $htmlPath = Join-Path $tmpDir "$($map.key)-$variant.html"
        # transparent background: the app dims the ground and composites floors itself
        $html = "<html><head><meta charset=`"utf-8`"></head><body style=`"margin:0;background:transparent`">$markup</body></html>"
        [IO.File]::WriteAllText($htmlPath, $html, [Text.UTF8Encoding]::new($false))

        $png = Join-Path $mapOut "$variant.png"
        if (Test-Path $png) { Remove-Item $png -Force }
        # Edge prints harmless stderr noise; do not let it become a terminating error
        $ErrorActionPreference = 'Continue'
        & $edge --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 `
                --default-background-color=00000000 `
                --window-size="$w,$h" --screenshot="$png" "file:///$($htmlPath.Replace('\','/'))" 2>$null | Out-Null
        $ErrorActionPreference = 'Stop'
        if (-not (Test-Path $png)) { Write-Warning "render failed: $($map.key)/$variant"; continue }
        $kb = [int]((Get-Item $png).Length / 1KB)
        Write-Host ("  {0,-20} {1,-18} {2,5}x{3,-5} {4,5} KB" -f $map.key, $variant, $w, $h, $kb)
    }

    $index += [pscustomobject]@{
        key      = $map.key
        svg      = $map.svg
        width    = $w
        height   = $h
        variants = $variants
    }
}

$indexPath = Join-Path $outDir 'index.json'
$index | ConvertTo-Json -Depth 5 | Set-Content -Path $indexPath -Encoding UTF8
$total = (Get-ChildItem $outDir -Recurse -Filter *.png | Measure-Object Length -Sum).Sum
Write-Host ''
Write-Host ("done: {0} maps, {1} images, {2} MB" -f $index.Count,
    (Get-ChildItem $outDir -Recurse -Filter *.png).Count, [Math]::Round($total / 1MB, 1))
