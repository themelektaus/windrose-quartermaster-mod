<#
.SYNOPSIS
    Baut alle Stack-Size-Varianten (Multiplier x2..x100 + Absolute 999..999999)
    in den Builds-Ordner.

.DESCRIPTION
    Ruft pro Variante:
        1. Build-WindroseMod.ps1 -Action Init (aus Stack-Mod-x4-Pak als Strukturquelle)
        2. Apply-StackMultiplier.ps1 (mit -VanillaSource, multipliziert vanilla*N
           oder setzt absoluten Wert)
        3. Build-WindroseMod.ps1 (Build)

    Die fertigen .pak-Dateien landen in <OutDir> und muessen anschliessend
    selbst in den ~mods-Ordner des Servers/Clients kopiert werden.

    Output: <OutDir>\StackSize_<name>_P.pak

    Quellordner werden in Sources\StackSize_<name>\ erzeugt und stehen lassen
    (per default), damit du reinschauen/diffen kannst. Mit -CleanSources
    werden sie nach erfolgreichem Build geloescht.

.PARAMETER Variants
    Liste der zu bauenden Varianten. Default: alle 13.
    Erlaubte Namen: x2, x3, x4, x5, x6, x7, x8, x9, x10, x100, 999, 9999, 99999, 999999
    (x4 ist absichtlich nicht dabei, weil das das aktuell auf Nockalmeer
    laufende Pak ist - falls du es trotzdem willst, explizit angeben.)

.PARAMETER FromPak
    Quell-Pak fuer den Init-Schritt (liefert das volle JSON-Schema mit
    Mesh-Pfaden, String-Enums etc.). Default: $cfg.References.StackModX4 (config.psd1).

.PARAMETER VanillaSource
    Pfad zum Vanilla-Dump (537 echte Vanilla-MaxCountInSlot-Werte).
    Default: $cfg.Paths.Vanilla (config.psd1).

.PARAMETER SrcRoot
    Wo die Per-Variante-Source-Ordner abgelegt werden.
    Default: $cfg.Paths.Sources (config.psd1).

.PARAMETER OutDir
    Wo die fertigen .pak-Dateien landen.
    Default: $cfg.Paths.Builds (config.psd1).

.PARAMETER Force
    Vorhandene src-Ordner und Builds ueberschreiben.

.PARAMETER CleanSources
    Nach erfolgreichem Build den src-Ordner der Variante loeschen.

.PARAMETER DryRun
    Nur zeigen, was passieren wuerde.

.EXAMPLE
    # Alle 13 Variationen bauen
    .\Build-AllStackVariations.ps1

.EXAMPLE
    # Nur ausgewaehlte Variationen, vorhandene ueberschreiben
    .\Build-AllStackVariations.ps1 -Variants x10,x100,999999 -Force

.EXAMPLE
    # Alle bauen, src-Ordner danach aufraeumen
    .\Build-AllStackVariations.ps1 -CleanSources -Force
#>

[CmdletBinding()]
param(
    [string[]]$Variants = @(
        'x2','x3','x4','x5','x6','x7','x8','x9','x10','x100',
        '999','9999','99999','999999'
    ),

    [string]$FromPak,

    [string]$VanillaSource,

    [string]$SrcRoot,

    [string]$OutDir,

    [switch]$Force,

    [switch]$CleanSources,

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

# --- Config laden ---------------------------------------------------------
$cfg = & (Join-Path $PSScriptRoot '_config.ps1')

if (-not $FromPak)       { $FromPak       = [string]$cfg.References.StackModX4 }
if (-not $VanillaSource) { $VanillaSource = [string]$cfg.Paths.Vanilla }
if (-not $SrcRoot)       { $SrcRoot       = [string]$cfg.Paths.Sources }
if (-not $OutDir)        { $OutDir        = [string]$cfg.Paths.Builds }

function Write-Step($msg)  { Write-Host "==> $msg"          -ForegroundColor Cyan }
function Write-OK($msg)    { Write-Host "    [OK] $msg"     -ForegroundColor Green }
function Write-Warn2($msg) { Write-Host "    [!]  $msg"     -ForegroundColor Yellow }
function Write-Err2($msg)  { Write-Host "    [X]  $msg"     -ForegroundColor Red }

# Pfade verifizieren
$ScriptRoot   = Split-Path -Parent $MyInvocation.MyCommand.Path
$BuildScript  = Join-Path $ScriptRoot 'Build-WindroseMod.ps1'
$ApplyScript  = Join-Path $ScriptRoot 'Apply-StackMultiplier.ps1'

foreach ($p in @($BuildScript, $ApplyScript, $FromPak, $VanillaSource)) {
    if (-not (Test-Path -LiteralPath $p)) {
        throw "Pfad nicht gefunden: $p"
    }
}

if (-not (Test-Path -LiteralPath $OutDir)) {
    if (-not $DryRun) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
}
if (-not (Test-Path -LiteralPath $SrcRoot)) {
    if (-not $DryRun) { New-Item -ItemType Directory -Path $SrcRoot -Force | Out-Null }
}

# Variante-Spec ableiten: Multiplier oder Absolute?
function Resolve-Variant($name) {
    if ($name -match '^x(\d+)$') {
        return [pscustomobject]@{
            Name       = $name
            Mode       = 'Multiplier'
            Multiplier = [int]$matches[1]
            Absolute   = 0
        }
    }
    if ($name -match '^(\d+)$') {
        return [pscustomobject]@{
            Name       = $name
            Mode       = 'Absolute'
            Multiplier = 0
            Absolute   = [int]$matches[1]
        }
    }
    throw "Unbekannte Variante: $name (erlaubt: 'xN' oder 'N')"
}

Write-Step "Build All Stack Variations"
Write-OK "Variants     : $($Variants -join ', ')"
Write-OK "FromPak      : $FromPak"
Write-OK "VanillaSource: $VanillaSource"
Write-OK "SrcRoot      : $SrcRoot"
Write-OK "OutDir       : $OutDir"
if ($Force)        { Write-OK "Force        : ja (vorhandene werden ueberschrieben)" }
if ($CleanSources) { Write-OK "CleanSources : ja (src-Ordner werden nach Build geloescht)" }
if ($DryRun)       { Write-Warn2 "DryRun aktiv -> es passiert nichts" }
Write-Host ""

$results = @()
$idx = 0
$total = $Variants.Count

foreach ($vname in $Variants) {
    $idx++
    $v = Resolve-Variant $vname
    $modName  = "StackSize_$($v.Name)"
    $srcPath  = Join-Path $SrcRoot $modName
    $pakName  = "${modName}_P.pak"
    $pakPath  = Join-Path $OutDir $pakName

    Write-Host "================================================================" -ForegroundColor DarkCyan
    Write-Step "[$idx/$total] $modName  ($($v.Mode))"

    # Skip wenn Pak schon da und kein -Force
    if ((Test-Path -LiteralPath $pakPath) -and -not $Force) {
        Write-Warn2 "Pak existiert bereits, skip (mit -Force ueberschreiben): $pakPath"
        $results += [pscustomobject]@{ Name = $modName; Status = 'skipped'; Path = $pakPath }
        continue
    }

    try {
        # 1. Init
        Write-Step "Init aus FromPak -> $srcPath"
        $initArgs = @{
            Action = 'Init'
            Source = $srcPath
            FromPak = $FromPak
        }
        if ($Force) { $initArgs.Force = $true }
        if ($DryRun) { $initArgs.DryRun = $true }
        & $BuildScript @initArgs
        if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "Init failed (exit $LASTEXITCODE)" }

        if ($DryRun) {
            # Im DryRun ist nach Init nichts entpackt -> Apply/Build wuerden auf
            # leerem/nicht existentem Pfad fehlschlagen. Simulation ende hier.
            Write-Warn2 "DryRun: Apply/Pack uebersprungen (kein src vorhanden)"
            $results += [pscustomobject]@{ Name = $modName; Status = 'ok (dryrun)'; Path = $pakPath }
            continue
        }

        # 2. Apply
        Write-Step "Apply $($v.Mode) auf $srcPath"
        $applyArgs = @{
            Source        = $srcPath
            VanillaSource = $VanillaSource
        }
        if ($v.Mode -eq 'Multiplier') {
            $applyArgs.Multiplier = $v.Multiplier
            $applyArgs.Cap        = 0   # kein Cap - User will ja explizit hohe Werte
        } else {
            $applyArgs.AbsoluteValue = $v.Absolute
        }
        if ($DryRun) { $applyArgs.DryRun = $true }
        & $ApplyScript @applyArgs
        if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "Apply failed (exit $LASTEXITCODE)" }

        # 3. Build
        Write-Step "Pack $srcPath -> $pakPath"
        $buildArgs = @{
            Source = $srcPath
            Name   = $modName
            OutDir = $OutDir
        }
        if ($Force)  { $buildArgs.Force  = $true }
        if ($DryRun) { $buildArgs.DryRun = $true }
        & $BuildScript @buildArgs
        if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)" }

        # 4. Optional cleanup
        if ($CleanSources -and -not $DryRun) {
            Write-Step "CleanSources: loesche $srcPath"
            Remove-Item -LiteralPath $srcPath -Recurse -Force
        }

        $results += [pscustomobject]@{ Name = $modName; Status = 'ok'; Path = $pakPath }
        Write-OK "$modName fertig"
    }
    catch {
        Write-Err2 "FEHLER bei $($modName): $_"
        $results += [pscustomobject]@{ Name = $modName; Status = "error: $_"; Path = $pakPath }
    }
}

Write-Host ""
Write-Step "Zusammenfassung"
$results | ForEach-Object {
    $color = switch -Wildcard ($_.Status) {
        'ok'         { 'Green' }
        'skipped'    { 'Yellow' }
        'error*'     { 'Red' }
        default      { 'Gray' }
    }
    Write-Host ("    {0,-22} {1}" -f $_.Name, $_.Status) -ForegroundColor $color
}

$okCount   = ($results | Where-Object Status -eq 'ok').Count
$skipCount = ($results | Where-Object Status -eq 'skipped').Count
$errCount  = ($results | Where-Object Status -like 'error*').Count
Write-Host ""
Write-OK "OK: $okCount  Skipped: $skipCount  Errors: $errCount"
if ($DryRun) {
    Write-Warn2 "DryRun aktiv -> nichts wurde wirklich geschrieben"
}
