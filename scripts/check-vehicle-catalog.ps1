[CmdletBinding()]
param([Parameter(Mandatory)][string]$GameRoot, [switch]$InspectOnly)

# Read-only audit of the installed F1 catalog, model bounds, fuel and bridge.
# Does not launch either application, load a DLL or change game files.
$ErrorActionPreference = 'Stop'
$game = (Resolve-Path -LiteralPath $GameRoot).Path
$repo = Split-Path -Parent $PSScriptRoot
$addon = Join-Path $game 'ixr_addons\zzzzzzzzzzzzzzz_pf-f1-item-spawner'
$catalogText = Get-Content -LiteralPath (Join-Path $addon 'scripts\pf_f1_item_catalog.script') -Raw
$block = [regex]::Match($catalogText, '(?s)local VEHICLE_SECTIONS = \{(.*?)\}').Groups[1].Value
$sections = @([regex]::Matches($block, '"(veh_[a-z0-9_]+)"') | ForEach-Object { $_.Groups[1].Value })
if ($sections.Count -eq 0 -or ($sections | Sort-Object -Unique).Count -ne $sections.Count) { throw 'Empty/duplicate F1 catalog' }
$labels = [xml](Get-Content -LiteralPath (Join-Path $addon 'configs\text\rus\st_pf_f1_item_spawner.xml') -Raw)
$definitions = @{}
$configFiles = @(Get-Item -LiteralPath (Join-Path $game 'gamedata\configs\extracontent.ltx')) +
    @(Get-ChildItem -LiteralPath (Join-Path $game 'gamedata\configs') -Filter 'mod_system_*.ltx' -File | Sort-Object Name)
foreach ($file in $configFiles) {
    $section = ''
    foreach ($line in Get-Content -LiteralPath $file.FullName) {
        if ($line -match '^!?\[([^\]]+)\]') { $section = $Matches[1] }
        if ($sections -contains $section) {
            if (-not $definitions.ContainsKey($section)) { $definitions[$section] = @{} }
            if ($line -match '^\s*(visual|class)\s*=\s*([^;]+)') { $definitions[$section][$Matches[1]] = $Matches[2].Trim() }
        }
    }
}
# Refuse to silently ignore an addon overriding the audited model/class.
foreach ($overlay in Get-ChildItem -LiteralPath (Join-Path $game 'ixr_addons') -Filter '*.ltx' -Recurse -File) {
    $section = ''
    foreach ($line in Get-Content -LiteralPath $overlay.FullName) {
        if ($line -match '^!?\[([^\]]+)\]') { $section = $Matches[1] }
        if ($sections -contains $section -and $line -match '^\s*(visual|class)\s*=') {
            throw "Vehicle overlay needs explicit review: $($overlay.FullName) [$section]"
        }
    }
}
$manifest = Get-Content -LiteralPath (Join-Path $repo 'game-addon\configs\pf_donation_actions.ltx') -Raw
$manifestBlock = [regex]::Match($manifest, '(?s)\[action:spawn_vehicle\](.*?)(?=\r?\n\[|\z)').Groups[1].Value
$choices = [regex]::Match($manifestBlock, '(?m)^param.vehicle = Choice\|\|\|([^|]+)').Groups[1].Value.Split(',')
$lua = Get-Content -LiteralPath (Join-Path $repo 'game-addon\scripts\pf_donation_action_vehicle.script') -Raw
$converter = Get-Content -LiteralPath (Join-Path $repo 'src\ChroniclesDonationBridge.App\Converters.cs') -Raw
$overlayRoots = @(Get-ChildItem -LiteralPath (Join-Path $game 'ixr_addons') -Directory)
$result = @()
foreach ($section in $sections) {
    $definition = $definitions[$section]
    if ($definition['class'] -ne 'C_NIVA') { throw "Invalid class: $section" }
    $visual = $definition['visual']
    foreach ($overlayRoot in $overlayRoots) {
        if (Test-Path -LiteralPath (Join-Path $overlayRoot.FullName "meshes\$visual")) {
            throw "Model overlay needs explicit review: $($overlayRoot.FullName) $visual"
        }
    }
    $model = (Resolve-Path -LiteralPath (Join-Path "$game\gamedata\meshes" $visual)).Path
    $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($model))
    try {
        $bounds = @(); $data = ''
        while ($reader.BaseStream.Position -lt $reader.BaseStream.Length) {
            $id = $reader.ReadUInt32(); $length = $reader.ReadUInt32(); $start = $reader.BaseStream.Position
            if ($start + $length -gt $reader.BaseStream.Length) { throw "Invalid OGF chunk: $model" }
            if ($id -eq 1) { $null = $reader.ReadUInt32(); $bounds = @(0..5 | ForEach-Object { [double]$reader.ReadSingle() }) }
            if ($id -eq 17) { $data = [Text.Encoding]::ASCII.GetString($reader.ReadBytes($length)) }
            $reader.BaseStream.Position = $start + $length
        }
    } finally { $reader.Dispose() }
    if ($bounds.Count -ne 6) { throw "Missing model bounds: $section" }
    if ($data -match '#include "(models\\vehicles\\[a-zA-Z0-9_]+\.ltx)"') {
        $data = Get-Content -LiteralPath (Join-Path "$game\gamedata\configs" $Matches[1]) -Raw
    }
    $tank = [regex]::Match($data, '(?m)^\s*fuel_tank\s*=\s*([\d.]+)').Groups[1].Value
    $profile = [regex]::Match($data, '(?m)^\s*fuel_profile\s*=\s*([^;\r\n]+)').Groups[1].Value.Trim()
    $legacy = -not $section.StartsWith('veh_dcp_')
    if ([double]::Parse($tank, [cultureinfo]::InvariantCulture) -lt 10) { throw "Tank below 10 litres: $section" }
    if (-not $legacy -and $profile -ne 'dcp_vehicle_fuel_v1') { throw "DCP fuel profile missing: $section" }
    $name = ($labels.string_table.string | Where-Object id -eq "pf_f1_vehicle_$section").text
    if (-not $name) { throw "Missing F1 label: $section" }
    $width = [math]::Ceiling(([math]::Max([math]::Abs($bounds[0]), [math]::Abs($bounds[3])) + 0.20) * 20) / 20
    $length = [math]::Ceiling(([math]::Max([math]::Abs($bounds[2]), [math]::Abs($bounds[5])) + 0.20) * 20) / 20
    $lift = [math]::Ceiling(([math]::Max([double]0, -$bounds[1]) + 0.05) * 20) / 20
    $height = [math]::Ceiling(($bounds[4] + $lift + 0.20) * 20) / 20
    if (-not $InspectOnly) {
        if (-not $converter.Contains('["' + $section + '"] = "' + $name + '"')) { throw "F1 label differs: $section" }
        $match = [regex]::Match($lua, '(?m)^\s*' + [regex]::Escape($section) + ' = \{ ([0-9.]+), ([0-9.]+), ([0-9.]+), ([0-9.]+), (true|false) \}')
        if (-not $match.Success) { throw "Missing Lua dimensions: $section" }
        $expected = @($width, $length, $lift, $height)
        for ($i = 0; $i -lt 4; $i++) {
            $actual = [double]::Parse($match.Groups[$i+1].Value, [cultureinfo]::InvariantCulture)
            if ([math]::Abs($actual - $expected[$i]) -gt 0.001) { throw "Model bounds changed: $section" }
        }
        if (($match.Groups[5].Value -eq 'true') -ne $legacy) { throw "Wrong legacy marker: $section" }
    }
    $result += [pscustomobject]@{section=$section; name=$name; width=$width; length=$length; lift=$lift; height=$height; legacy=$legacy; tank=$tank; visual=$visual}
}
if (-not $InspectOnly -and (Compare-Object ($sections | Sort-Object) ($choices | Sort-Object))) { throw 'Manifest differs from F1 catalog' }
$result | ConvertTo-Json -Depth 3
