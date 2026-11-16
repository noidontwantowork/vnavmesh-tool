#Requires -Version 5.1
<#
    Registers vnavmesh-tool and its two bundled vnavmesh builds as Dalamud dev plugins.
    Close FINAL FANTASY XIV before running this script.
#>
$ErrorActionPreference = 'Stop'

$pluginDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$configPath = Join-Path $env:APPDATA 'XIVLauncherCN\dalamudConfig.json'

$entries = @(
    @{ Path = (Join-Path $pluginDir 'vnavmesh-tool.dll'); Nickname = 'vnavmesh-tool' },
    @{ Path = (Join-Path $pluginDir 'Versions\global-1.2.3.15\vnavmesh.dll'); Nickname = 'vnavmesh Global' },
    @{ Path = (Join-Path $pluginDir 'Versions\cn-0.9.3.0\vnavmesh.dll'); Nickname = 'vnavmesh CN' }
)

if (-not (Test-Path -LiteralPath $configPath)) {
    Write-Host "dalamudConfig.json not found: $configPath" -ForegroundColor Red
    exit 1
}

$missing = $entries | Where-Object { -not (Test-Path -LiteralPath $_.Path) }
if ($missing) {
    Write-Host 'Missing files:' -ForegroundColor Red
    $missing | ForEach-Object { Write-Host ('  ' + $_.Path) }
    exit 1
}

if (Get-Process -Name ffxiv_dx11, ffxiv -ErrorAction SilentlyContinue) {
    Write-Host 'FINAL FANTASY XIV is running. Close the game, then run this script again.' -ForegroundColor Yellow
    exit 2
}

$raw = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8
$newline = if ($raw.Contains("`r`n")) { "`r`n" } else { "`n" }

$block = ''
$added = 0
foreach ($entry in $entries) {
    $escaped = $entry.Path.Replace('\', '\\')
    if ($raw.Contains($escaped)) { continue }

    $block += '      {' + $newline
    $block += '        "$type": "Dalamud.Configuration.DevPluginLocationSettings, Dalamud",' + $newline
    $block += '        "Path": "' + $escaped + '",' + $newline
    $block += '        "IsEnabled": true,' + $newline
    $block += '        "Nickname": "' + $entry.Nickname + '"' + $newline
    $block += '      },' + $newline
    $added++
}

if ($added -eq 0) {
    Write-Host 'All dev plugin locations are already registered. Nothing to do.' -ForegroundColor Green
    exit 0
}

$pattern = '("DevPluginLoadLocations"\s*:\s*\{[\s\S]{0,400}?"\$values"\s*:\s*\[)'
$match = [regex]::Match($raw, $pattern)
if (-not $match.Success) {
    Write-Host 'Could not locate DevPluginLoadLocations/$values in dalamudConfig.json.' -ForegroundColor Red
    exit 1
}

$insertAt = $match.Groups[1].Index + $match.Groups[1].Length
$newRaw = $raw.Substring(0, $insertAt) + $newline + $block + $raw.Substring($insertAt)

# Structural check. PowerShell 5.1 cannot round-trip this config because Dalamud stores
# dictionaries with keys that differ only by letter case, so only brace balance is checked there.
if ($PSVersionTable.PSVersion.Major -ge 6) {
    try {
        $null = $newRaw | ConvertFrom-Json -AsHashtable
    }
    catch {
        Write-Host ('Patched JSON failed to parse: ' + $_.Exception.Message) -ForegroundColor Red
        exit 1
    }
}
else {
    $open = ([regex]::Matches($newRaw, '\{')).Count
    $close = ([regex]::Matches($newRaw, '\}')).Count
    if ($open -ne $close) {
        Write-Host ("Brace count mismatch after patch: $open / $close") -ForegroundColor Red
        exit 1
    }
}

$backup = $configPath + '.bak-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
Copy-Item -LiteralPath $configPath -Destination $backup -Force

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($configPath, $newRaw, $utf8NoBom)

Write-Host "Registered $added dev plugin location(s)." -ForegroundColor Green
Write-Host "Backup: $backup"
Write-Host 'Start the game, then use /vnavmeshtool or /vnavmeshswitch.'
