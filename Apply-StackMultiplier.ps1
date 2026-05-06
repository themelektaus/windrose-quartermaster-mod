<#
.SYNOPSIS
    Multipliziert MaxCountInSlot in allen JSONs eines Mod-Source-Ordners
    und loescht Files, die nicht stackbar sind (Stack <= 1) oder kein
    MaxCountInSlot-Feld haben.

.DESCRIPTION
    Walker fuer Windrose-Stack-Mods.

    Pro JSON:
      - liest "MaxCountInSlot": <n>
      - n <= 1                -> Datei loeschen (bleibt Vanilla)
      - n >  1 (oder == 0)    -> n * Multiplier (ge-cap-t auf -Cap)
                                und JSON neu schreiben (Tabs/Reihenfolge bleiben)

    Schreibt eine kompakte Statistik am Ende.

.PARAMETER Source
    Pflicht. Pfad zum Mod-Source-Ordner (in dem die JSONs liegen).

.PARAMETER Multiplier
    Faktor fuer MaxCountInSlot. Default: 4.

.PARAMETER Cap
    Maximalwert. Default: 39996 (Stack-Mod x4 nutzt diesen als Cap).
    Setze 0 fuer "kein Cap".

.PARAMETER KeepUnchanged
    Schalter. Falls gesetzt, werden Files mit Stack <= 1 nicht geloescht,
    sondern unveraendert behalten. Default: Files werden geloescht.

.PARAMETER ExcludePath
    Liste von Pfad-Substrings (Wildcards erlaubt). Files, deren relativer
    Pfad eines dieser Muster enthaelt, werden komplett ignoriert (geloescht
    bzw. uebersprungen, je nach -KeepUnchanged).
    Default: '*\Tests\*' (Dev-Test-Items rauswerfen)

.PARAMETER Minimal
    Schalter. Ersetzt das gesamte JSON durch ein Minimal-Schema, das nur
    "$type", "InventoryItemGppData.MaxCountInSlot" und "NativeClass" enthaelt.
    Vermeidet Loader-Fehler durch problematische Vanilla-Dump-Felder
    (Enum-Zahlen, leere Default-Structs in Arrays etc.) und vertraut auf
    Property-Override im R5BL-Loader.

.PARAMETER VanillaSource
    Optionaler Pfad zu einem zweiten Source-Tree mit den Vanilla-JSONs
    (z.B. .\Sources\Vanilla). Wenn gesetzt, wird der Multiplier auf den Vanilla-
    Wert angewendet (statt auf den Source-Wert). Nuetzlich, wenn -Source
    bereits modifizierte Werte enthaelt (z.B. aus -FromPak einer fremden
    Stack-Mod) und du echte Vanilla*Multiplier-Werte willst.

    Lookup: gleicher relativer Pfad in -VanillaSource. Fehlende Vanilla-
    Counterparts werden geloggt und uebersprungen.

.PARAMETER AbsoluteValue
    Optionaler Festwert. Wenn gesetzt (>0), wird MaxCountInSlot fuer alle
    stackbaren Items (Vanilla-Stack > 1) auf diesen Wert gesetzt, statt zu
    multiplizieren. -Multiplier wird dann ignoriert. -Cap wird ebenfalls
    nicht mehr angewendet, weil der Wert ja direkt vorgegeben ist.

    Use case: flache "alle Stacks = 999" Mods.

.PARAMETER DryRun
    Nur zeigen, was passieren wuerde, nichts schreiben/loeschen.

.EXAMPLE
    .\Apply-StackMultiplier.ps1 -Source .\Sources\StackSize_x4 -Multiplier 4

.EXAMPLE
    .\Apply-StackMultiplier.ps1 -Source .\Sources\Test -Multiplier 10 -DryRun

.EXAMPLE
    .\Apply-StackMultiplier.ps1 -Source .\Sources\StackSize_999 -VanillaSource .\Sources\Vanilla -AbsoluteValue 999
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Source,

    [int]$Multiplier = 4,

    [int]$Cap = 39996,

    [switch]$KeepUnchanged,

    [string[]]$ExcludePath = @('*\Tests\*'),

    [switch]$Minimal,

    [string]$VanillaSource,

    [int]$AbsoluteValue = 0,

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

function Write-Step($msg)  { Write-Host "==> $msg"          -ForegroundColor Cyan }
function Write-OK($msg)    { Write-Host "    [OK] $msg"     -ForegroundColor Green }
function Write-Warn2($msg) { Write-Host "    [!]  $msg"     -ForegroundColor Yellow }

if ($Multiplier -lt 1) {
    throw "Multiplier muss >= 1 sein (war: $Multiplier)"
}
if ($AbsoluteValue -lt 0) {
    throw "AbsoluteValue darf nicht negativ sein (war: $AbsoluteValue)"
}
$IsAbsolute = ($AbsoluteValue -gt 0)

$SourceFull = (Resolve-Path -LiteralPath $Source).Path
if (-not (Test-Path -LiteralPath $SourceFull -PathType Container)) {
    throw "Source ist kein Ordner: $SourceFull"
}

$VanillaSourceFull = $null
if ($VanillaSource) {
    $VanillaSourceFull = (Resolve-Path -LiteralPath $VanillaSource).Path
    if (-not (Test-Path -LiteralPath $VanillaSourceFull -PathType Container)) {
        throw "VanillaSource ist kein Ordner: $VanillaSourceFull"
    }
}

Write-Step "Apply Stack Multiplier"
Write-OK "Source     : $SourceFull"
if ($VanillaSourceFull) {
    Write-OK "Vanilla    : $VanillaSourceFull"
    Write-OK "Mode       : Vanilla-merge (multiplier applied to vanilla values)"
}
if ($IsAbsolute) {
    Write-OK "AbsoluteVal: $AbsoluteValue (Multiplier/Cap ignoriert)"
} else {
    Write-OK "Multiplier : x$Multiplier"
    if ($Cap -gt 0) { Write-OK "Cap        : $Cap" } else { Write-OK "Cap        : (none)" }
}
if ($KeepUnchanged) { Write-OK "Cleanup    : keep unchanged files" } else { Write-OK "Cleanup    : delete unchanged files (Stack<=1)" }
if ($Minimal)       { Write-OK "Schema     : minimal (Type+MaxCountInSlot+NativeClass only)" } else { Write-OK "Schema     : full (preserve all original fields)" }
if ($DryRun) { Write-Warn2 "DryRun aktiv -> nichts wird geschrieben/geloescht" }

$files = Get-ChildItem -LiteralPath $SourceFull -Recurse -File -Filter '*.json'
Write-OK "Gefunden   : $($files.Count) JSON-Datei(en)"
if ($ExcludePath -and $ExcludePath.Count -gt 0) {
    Write-OK ("ExcludePath: {0}" -f ($ExcludePath -join ', '))
}

$modified  = 0
$deleted   = 0
$kept      = 0
$skipped   = 0
$capped    = 0
$excluded  = 0
$noVanilla = 0

foreach ($f in $files) {
    # ExcludePath-Pruefung (gegen relativen Pfad)
    $rel = $f.FullName.Substring($SourceFull.Length).TrimStart('\')
    $isExcluded = $false
    foreach ($pat in $ExcludePath) {
        if ($rel -like $pat) { $isExcluded = $true; break }
    }
    if ($isExcluded) {
        if ($KeepUnchanged) {
            $kept++
        } else {
            if (-not $DryRun) {
                Remove-Item -LiteralPath $f.FullName -Force
            }
            $excluded++
        }
        continue
    }

    # UTF-8 explizit, sonst frisst Windows PowerShell 5.1 Default-ANSI alle
    # Nicht-ASCII-Zeichen (z.B. kyrillische Item-Namen) und schreibt sie als
    # Mojibake zurueck.
    $content = [System.IO.File]::ReadAllText($f.FullName, [System.Text.Encoding]::UTF8)

    if ($content -match '("MaxCountInSlot"\s*:\s*)(\d+)') {
        $sourceVal = [int]$matches[2]

        # Vanilla-Merge: Basis-Wert kommt aus VanillaSource, nicht aus Source
        if ($VanillaSourceFull) {
            $vanillaPath = Join-Path $VanillaSourceFull $rel
            if (-not (Test-Path -LiteralPath $vanillaPath)) {
                # Kein Vanilla-Counterpart -> File ist Stack-Mod-spezifisch
                # (z.B. Items, die der Stack-Mod-Autor bewusst stackbar gemacht hat).
                # Wir loeschen es, weil wir keine echte Vanilla-Basis haben.
                if (-not $DryRun) {
                    Remove-Item -LiteralPath $f.FullName -Force
                }
                $noVanilla++
                continue
            }
            $vanillaContent = [System.IO.File]::ReadAllText($vanillaPath, [System.Text.Encoding]::UTF8)
            if ($vanillaContent -match '"MaxCountInSlot"\s*:\s*(\d+)') {
                $oldVal = [int]$matches[1]
            } else {
                # Vanilla hat kein MaxCountInSlot -> nicht modifizierbar
                if (-not $DryRun) {
                    Remove-Item -LiteralPath $f.FullName -Force
                }
                $noVanilla++
                continue
            }
        } else {
            $oldVal = $sourceVal
        }

        if ($oldVal -le 1) {
            # Nicht stackbar -> loeschen oder behalten
            if ($KeepUnchanged) {
                $kept++
            } else {
                if (-not $DryRun) {
                    Remove-Item -LiteralPath $f.FullName -Force
                }
                $deleted++
            }
            continue
        }

        if ($IsAbsolute) {
            $newVal = $AbsoluteValue
        } else {
            $newVal = $oldVal * $Multiplier
            if ($Cap -gt 0 -and $newVal -gt $Cap) {
                $newVal = $Cap
                $capped++
            }
        }

        if ($newVal -eq $sourceVal -and -not $Minimal) {
            # Source hat schon den richtigen Wert (z.B. wenn FromPak schon x4 war)
            $kept++
            continue
        }

        if ($Minimal) {
            # NativeClass aus Original uebernehmen (Default falls nicht gefunden)
            $nativeClass = "/Script/CoreUObject.Class'/Script/R5BusinessRules.R5BLInventoryItem'"
            if ($content -match '"NativeClass"\s*:\s*"([^"]+)"') {
                $nativeClass = $matches[1]
            }
            # $type aus Original uebernehmen (Default R5BLInventoryItem)
            $typeName = 'R5BLInventoryItem'
            if ($content -match '"\$type"\s*:\s*"([^"]+)"') {
                $typeName = $matches[1]
            }

            # Minimal-JSON mit Tabs (Stack-Mod-Style), CRLF damit es zum Original passt
            $newContent = @"
{
`t"`$type": "$typeName",
`t"InventoryItemGppData": {
`t`t"MaxCountInSlot": $newVal
`t},
`t"NativeClass": "$($nativeClass.Replace('\','\\').Replace('"','\"'))"
}
"@
        } else {
            $newContent = [regex]::Replace(
                $content,
                '("MaxCountInSlot"\s*:\s*)\d+',
                { param($m) $m.Groups[1].Value + $newVal.ToString() },
                1   # nur ersten Match ersetzen
            )
        }

        if (-not $DryRun) {
            # UTF-8 ohne BOM, LF-Zeilenenden bleiben so wie sie sind
            $utf8 = New-Object System.Text.UTF8Encoding($false)
            [System.IO.File]::WriteAllText($f.FullName, $newContent, $utf8)
        }
        $modified++
    } else {
        # Kein MaxCountInSlot-Feld -> loeschen (passt nicht ins Schema)
        if ($KeepUnchanged) {
            $kept++
        } else {
            if (-not $DryRun) {
                Remove-Item -LiteralPath $f.FullName -Force
            }
            $skipped++
        }
    }
}

# Leere Verzeichnisse aufraeumen
if (-not $DryRun -and -not $KeepUnchanged) {
    # Mehrfach durchlaufen, weil leere Parents erst nach dem Kind-Loeschen leer werden
    for ($i = 0; $i -lt 10; $i++) {
        $emptyDirs = Get-ChildItem -LiteralPath $SourceFull -Recurse -Directory |
            Where-Object { @(Get-ChildItem -LiteralPath $_.FullName -Force).Count -eq 0 }
        if (-not $emptyDirs) { break }
        $emptyDirs | Remove-Item -Force -Recurse
    }
}

Write-Host ""
Write-Step "Fertig"
Write-OK ("Modifiziert        : {0}" -f $modified)
if ($capped -gt 0) {
    Write-OK ("  davon ge-cap-t   : {0} (auf {1})" -f $capped, $Cap)
}
if ($KeepUnchanged) {
    Write-OK ("Unveraendert       : {0}" -f $kept)
} else {
    Write-OK ("Geloescht (Stack=1): {0}" -f $deleted)
    Write-OK ("Geloescht (no MCS) : {0}" -f $skipped)
    Write-OK ("Geloescht (excl.)  : {0}" -f $excluded)
    if ($VanillaSourceFull) {
        Write-OK ("Geloescht (no van.): {0}" -f $noVanilla)
    }
    Write-OK ("Behalten unveraend.: {0}" -f $kept)
}
if ($DryRun) {
    Write-Warn2 "DryRun aktiv -> nichts wurde wirklich geschrieben/geloescht"
}
