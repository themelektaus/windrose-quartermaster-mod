<#
.SYNOPSIS
    Reorganisiert die flachen JSON-Dumps des UE4SS-Mods `VanillaItemDumper`
    in einen Verzeichnisbaum, der direkt als Mod-Source verwendet werden kann.

.DESCRIPTION
    Der UE4SS-Lua-Mod kann zur Laufzeit keine Verzeichnisse anlegen
    (os.execute / io.popen deadlocken in der Embedding-Variante), deshalb
    schreibt er seine Dumps *flach* nebeneinander und kodiert den
    urspruenglichen Pfad ueber den Trenner `___`:

        R5___Plugins___R5BusinessRules___Content___InventoryItems___Ammo___DA_AID_X.json

    Dieses Script dreht das wieder um:

        R5\Plugins\R5BusinessRules\Content\InventoryItems\Ammo\DA_AID_X.json

    und legt das Ergebnis unter -OutDir ab. Damit hast du eine saubere
    Vanilla-Snapshot-Quelle, gegen die du eigene Mods diffen oder die du
    direkt als Source fuer `Build-WindroseMod.ps1` nutzen kannst.

.PARAMETER DumpsDir
    Quell-Ordner mit den flachen `R5___*.json`-Dumps. Default: $cfg.Paths.Dumps
    (config.psd1) -- praktisch, wenn du den Dump-Ordner aus dem laufenden
    Server-Slot dort hin kopierst / symlinkst, bevor du das Script startest.

.PARAMETER OutDir
    Ziel-Ordner fuer den rekonstruierten Tree. Default: $cfg.Paths.Vanilla
    (config.psd1, normalerweise Sources\Vanilla).

.PARAMETER Clean
    Loescht den Inhalt von -OutDir komplett, bevor der neue Tree geschrieben wird.
    Sinnvoll, wenn du sicherstellen willst, dass keine veralteten Files
    aus einem aelteren Dump-Run hinten ueberbleiben.

.PARAMETER Force
    Ueberschreibt einzelne bereits vorhandene Ziel-Dateien ohne Rueckfrage.
    Ohne -Force wird das Script abbrechen, sobald die erste Datei kollidiert.

.PARAMETER Filter
    Optionaler Wildcard-Filter auf den rekonstruierten Tree-Pfad
    (z.B. `*Ammo*` oder `R5\Plugins\*Cannonball*`). Nur passende Dateien
    werden uebernommen. Hilft, wenn du gezielt nur einen Bereich willst.

.PARAMETER PathSeparator
    Trenner, mit dem der Lua-Mod Pfade kodiert. Wird normalerweise aus
    `_manifest.json` gelesen; nur setzen, wenn das Manifest fehlt.

.PARAMETER DryRun
    Zeigt nur, was passieren wuerde, ohne zu schreiben.

.EXAMPLE
    .\Dump-WindroseVanilla.ps1
    # Default (aus config.psd1): $cfg.Paths.Dumps -> $cfg.Paths.Vanilla

.EXAMPLE
    .\Dump-WindroseVanilla.ps1 -Clean -Force
    # OutDir vorher leeren und alles neu schreiben

.EXAMPLE
    .\Dump-WindroseVanilla.ps1 -Filter '*Cannonball*' -OutDir .\Sources\CannonOnly
    # Nur Cannonball-Items in einen separaten Ordner reorganisieren
#>

[CmdletBinding()]
param(
    [string]$DumpsDir,

    [string]$OutDir,

    [switch]$Clean,

    [switch]$Force,

    [string]$Filter,

    [string]$PathSeparator,

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

# --- Config laden ---------------------------------------------------------
$cfg = & (Join-Path $PSScriptRoot '_config.ps1')
if (-not $DumpsDir) { $DumpsDir = [string]$cfg.Paths.Dumps }

function Write-Step($msg)  { Write-Host "==> $msg"          -ForegroundColor Cyan }
function Write-OK($msg)    { Write-Host "    [OK] $msg"     -ForegroundColor Green }
function Write-Warn2($msg) { Write-Host "    [!]  $msg"     -ForegroundColor Yellow }
function Write-Err2($msg)  { Write-Host "    [X]  $msg"     -ForegroundColor Red }

# --- 1) Validierung -------------------------------------------------------
Write-Step 'Pruefe Voraussetzungen'

if (-not (Test-Path -LiteralPath $DumpsDir -PathType Container)) {
    throw "DumpsDir existiert nicht: $DumpsDir"
}
$DumpsDir = (Resolve-Path -LiteralPath $DumpsDir).Path
Write-OK "DumpsDir: $DumpsDir"

if (-not $OutDir -or $OutDir.Trim() -eq '') {
    $OutDir = [string]$cfg.Paths.Vanilla
    if (-not $OutDir) {
        $OutDir = Join-Path $PSScriptRoot 'Sources\Vanilla'
    }
}
# Ziel-Ordner ggf. anlegen, dann normalisieren
if (-not (Test-Path -LiteralPath $OutDir)) {
    if (-not $DryRun) {
        New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
    }
}
$resolved = Resolve-Path -LiteralPath $OutDir -ErrorAction SilentlyContinue
if ($resolved) { $OutDir = $resolved.Path }
else           { $OutDir = [System.IO.Path]::GetFullPath($OutDir) }
Write-OK "OutDir:   $OutDir"

# --- 2) Manifest lesen (optional, informativ) -----------------------------
$ManifestPath = Join-Path $DumpsDir '_manifest.json'
$Manifest = $null
if (Test-Path -LiteralPath $ManifestPath) {
    try {
        $Manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
        $stamp = $Manifest.timestamp
        $found = $Manifest.total_found
        $dumped = $Manifest.dumped
        $failed = $Manifest.failed
        Write-OK ("Manifest: tool={0}, dumped={1}/{2} (failed={3}), timestamp={4}" -f `
            $Manifest.tool, $dumped, $found, $failed, $stamp)
        if (-not $PathSeparator -and $Manifest.path_separator) {
            $PathSeparator = [string]$Manifest.path_separator
        }
    } catch {
        Write-Warn2 "Manifest konnte nicht gelesen werden: $($_.Exception.Message)"
    }
} else {
    Write-Warn2 "_manifest.json fehlt -> rein dateibasiert weiter"
}
if (-not $PathSeparator -or $PathSeparator -eq '') {
    $PathSeparator = '___'
    Write-Warn2 "PathSeparator nicht aus Manifest -> Default '$PathSeparator'"
}
Write-OK "PathSeparator: '$PathSeparator'"

# --- 3) Quell-Files einsammeln --------------------------------------------
Write-Step 'Sammle Quell-Dateien'

# Nur *.json mit dem Tree-Praefix; das Manifest und etwaige Probe-Files
# werden bewusst ignoriert. Das Praefix ist alles bis zum ersten Separator
# im ersten Sample -- wir erwarten "R5", aber falls jemand die Struktur
# erweitert, wird das hier mitgenommen.
$allJson = Get-ChildItem -LiteralPath $DumpsDir -File -Filter '*.json'
$flat = @($allJson | Where-Object { $_.Name -like ("*" + $PathSeparator + "*") })

if ($flat.Count -eq 0) {
    throw "Keine flachen Dump-Files mit Separator '$PathSeparator' in $DumpsDir gefunden."
}
Write-OK ("{0} flache Dump-Files gefunden ({1} JSON-Files insgesamt)" -f $flat.Count, $allJson.Count)

# --- 4) Tree-Pfad ableiten ------------------------------------------------
function ConvertTo-TreePath {
    param([string]$FlatBase, [string]$Sep)
    # Ersetzt den kodierten Separator durch echte Verzeichnis-Trenner.
    # Wir nutzen "\" weil das Script Windows-spezifisch ist.
    return ($FlatBase -replace [regex]::Escape($Sep), '\')
}

$plan = New-Object System.Collections.Generic.List[Object]
foreach ($f in $flat) {
    $base = [System.IO.Path]::GetFileNameWithoutExtension($f.Name)
    $tree = ConvertTo-TreePath -FlatBase $base -Sep $PathSeparator
    $rel  = $tree + '.json'
    if ($Filter -and ($tree -notlike $Filter) -and ($rel -notlike $Filter)) {
        continue
    }
    $dest = Join-Path $OutDir $rel
    $plan.Add([PSCustomObject]@{
        Source = $f.FullName
        Rel    = $rel
        Dest   = $dest
    }) | Out-Null
}

if ($Filter) {
    Write-OK ("{0} Files passen zum Filter '{1}'" -f $plan.Count, $Filter)
}
if ($plan.Count -eq 0) {
    throw 'Nach Filterung sind keine Files mehr uebrig.'
}

# --- 5) Kollisionen / Clean ----------------------------------------------
if ($Clean) {
    Write-Step 'Clean: leere OutDir-Inhalt'
    if (-not $DryRun) {
        Get-ChildItem -LiteralPath $OutDir -Force -ErrorAction SilentlyContinue |
            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
        Write-OK 'OutDir geleert'
    } else {
        Write-Warn2 'DryRun -> OutDir bleibt unveraendert'
    }
}

if (-not $Clean -and -not $Force) {
    $collisions = @($plan | Where-Object { Test-Path -LiteralPath $_.Dest })
    if ($collisions.Count -gt 0) {
        Write-Err2 ("{0} Ziel-Dateien existieren bereits, Beispiel: {1}" -f `
            $collisions.Count, $collisions[0].Dest)
        throw 'Abbruch: Kollisionen gefunden. Mit -Force ueberschreiben oder -Clean vorher leeren.'
    }
}

# --- 6) Schreiben ---------------------------------------------------------
Write-Step 'Reorganisiere Files'

$writeCount = 0
$skipCount  = 0
$dirs       = New-Object System.Collections.Generic.HashSet[string]

foreach ($p in $plan) {
    $destDir = Split-Path -Parent $p.Dest
    if ($destDir -and -not $dirs.Contains($destDir)) {
        if (-not (Test-Path -LiteralPath $destDir)) {
            if (-not $DryRun) {
                New-Item -ItemType Directory -Path $destDir -Force | Out-Null
            }
        }
        [void]$dirs.Add($destDir)
    }

    if ($DryRun) {
        $writeCount++
        continue
    }

    Copy-Item -LiteralPath $p.Source -Destination $p.Dest -Force
    $writeCount++
}

# --- 7) Stats -------------------------------------------------------------
Write-Step 'Statistik'

# Top-Level-Kategorie unter ...\InventoryItems\<KAT>\... bestimmen, falls
# der Pfad das Pattern hat. Sonst nehmen wir das vorletzte Segment vor dem
# Filenamen, damit wir trotzdem etwas Zaehlbares haben.
$byCategory = @{}
foreach ($p in $plan) {
    $segs = $p.Rel -split '\\'
    $cat  = '(other)'
    for ($i = 0; $i -lt $segs.Count; $i++) {
        if ($segs[$i] -eq 'InventoryItems' -and ($i + 1) -lt ($segs.Count - 1)) {
            $cat = $segs[$i + 1]
            break
        }
    }
    if (-not $byCategory.ContainsKey($cat)) { $byCategory[$cat] = 0 }
    $byCategory[$cat]++
}
$byCategory.GetEnumerator() | Sort-Object Name | ForEach-Object {
    Write-Host ("    {0,-25} {1,6}" -f $_.Key, $_.Value) -ForegroundColor DarkGray
}

# --- 8) Zusammenfassung ---------------------------------------------------
Write-Host ''
Write-Step 'Fertig'
Write-OK ("{0} Files geschrieben" -f $writeCount)
Write-OK ("Ziel: {0}" -f $OutDir)
if ($Filter)      { Write-OK ("Filter aktiv: {0}" -f $Filter) }
if ($DryRun)      { Write-Warn2 'DryRun aktiv -> nichts wurde wirklich geschrieben' }
