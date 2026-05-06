<#
.SYNOPSIS
    Loads the Windrose modding config from config.psd1 (with fallback to
    config.example.psd1) and returns the hashtable.

.DESCRIPTION
    Dot-sourced or invoked by every modding script:
        $cfg = & "$PSScriptRoot\_config.ps1"
        $RepakExe = $cfg.Tools.RepakExe

    Looks primarily for `config.psd1` next to the script. If missing, falls
    back to `config.example.psd1` (with a warning) -- that's only useful for
    DryRun/help; real builds need an actual config.psd1.

    Validates that the required sections are present, fills in missing path
    defaults from the modding root (= script directory), and resolves
    relative path entries against the modding root.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$ScriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$cfgPath     = Join-Path $ScriptDir 'config.psd1'
$examplePath = Join-Path $ScriptDir 'config.example.psd1'

if (Test-Path -LiteralPath $cfgPath) {
    $cfg = Import-PowerShellDataFile -LiteralPath $cfgPath
}
elseif (Test-Path -LiteralPath $examplePath) {
    Write-Warning "config.psd1 not found -- falling back to config.example.psd1. Copy and adjust: cp config.example.psd1 config.psd1"
    $cfg = Import-PowerShellDataFile -LiteralPath $examplePath
}
else {
    throw "Neither config.psd1 nor config.example.psd1 found in $ScriptDir."
}

# Ensure required sections exist
foreach ($k in 'Paths','Tools','Pak','References') {
    if (-not $cfg.ContainsKey($k)) { $cfg[$k] = @{} }
}

# Derive default paths relative to the modding root (= ScriptDir).
# If config.psd1 has a custom value it is honoured; relative paths are
# resolved against ScriptDir, absolute paths stay absolute.
$pathDefaults = [ordered]@{
    Sources = 'Sources'
    Vanilla = 'Sources\Vanilla'
    Builds  = 'Builds'
    Dumps   = 'ue4ss-mods\VanillaItemDumper\Dumps'
}
foreach ($k in $pathDefaults.Keys) {
    $val = [string]$cfg.Paths[$k]
    if (-not $val -or $val.Trim() -eq '') {
        # Default
        $cfg.Paths[$k] = [System.IO.Path]::GetFullPath((Join-Path $ScriptDir $pathDefaults[$k]))
    }
    elseif (-not [System.IO.Path]::IsPathRooted($val)) {
        # Relative path in the config -> resolve against ScriptDir
        $cfg.Paths[$k] = [System.IO.Path]::GetFullPath((Join-Path $ScriptDir $val))
    }
    else {
        # Already absolute
        $cfg.Paths[$k] = [System.IO.Path]::GetFullPath($val)
    }
}

return $cfg
