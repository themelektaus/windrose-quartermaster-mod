<#
.SYNOPSIS
    Laedt die Windrose-Modding-Config aus config.psd1 (mit Fallback auf
    config.example.psd1) und gibt das Hashtable zurueck.

.DESCRIPTION
    Wird von allen Modding-Skripten dot-sourced bzw. aufgerufen:
        $cfg = & "$PSScriptRoot\_config.ps1"
        $RepakExe = $cfg.Tools.RepakExe

    Sucht primaer nach `config.psd1` neben dem Skript. Falls nicht vorhanden,
    wird `config.example.psd1` verwendet (mit Warnung) -- das ist nur fuer
    DryRun/Help nuetzlich; richtige Builds brauchen eine echte config.psd1.

    Validiert, dass die Pflicht-Sektionen vorhanden sind, fuellt fehlende
    Path-Defaults aus dem Modding-Root (= Skript-Verzeichnis) auf und loest
    relative Path-Eintraege gegen das Modding-Root auf.
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
    Write-Warning "config.psd1 nicht gefunden -- nutze config.example.psd1 als Fallback. Kopiere und passe an: cp config.example.psd1 config.psd1"
    $cfg = Import-PowerShellDataFile -LiteralPath $examplePath
}
else {
    throw "Weder config.psd1 noch config.example.psd1 in $ScriptDir gefunden."
}

# Pflicht-Sektionen sicherstellen
foreach ($k in 'Paths','Tools','Pak','References') {
    if (-not $cfg.ContainsKey($k)) { $cfg[$k] = @{} }
}

# Default-Pfade relativ zum Modding-Root (= ScriptDir) ableiten.
# Wenn in config.psd1 ein eigener Wert steht, wird dieser respektiert;
# relative Pfade werden gegen ScriptDir aufgeloest, absolute bleiben absolut.
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
        # Relativer Pfad in der Config -> gegen ScriptDir aufloesen
        $cfg.Paths[$k] = [System.IO.Path]::GetFullPath((Join-Path $ScriptDir $val))
    }
    else {
        # Bereits absolut
        $cfg.Paths[$k] = [System.IO.Path]::GetFullPath($val)
    }
}

return $cfg
