<#
.SYNOPSIS
    Baut aus einem Quell-Ordner ein _P.pak fuer Windrose. Kann optional
    eine neue Mod-Source aus dem Vanilla-Snapshot oder einem existierenden
    Pak initialisieren.

.DESCRIPTION
    Zwei Modi via -Action:

      -Action Build  (Default): Wrapper um repak.exe. Erwartet einen Quell-Ordner,
        dessen Inhalt im Spiel "ueber" den Original-Dateien gemountet wird. Der
        Pak-Name endet immer auf "_P" (Patch-Marker), damit das Spiel ihn als
        Override laedt. Das fertige Pak landet in -OutDir und wird NICHT
        automatisch installiert -- kopiere es selbst in den jeweiligen
        ~mods-Ordner (Server oder Client).

      -Action Init: Erzeugt einen neuen Mod-Quell-Ordner indem Files aus
        Sources\Vanilla\ (Default) oder einem existierenden .pak (-FromPak)
        kopiert/entpackt werden. Optional gefiltert via -Filter (Glob auf Filename)
        oder -Categories (Liste von InventoryItems-Unterordnern).

    Typische Ordnerstruktur unterhalb von -Source (fuer Build):
        R5\Plugins\R5BusinessRules\Content\InventoryItems\Ammo\*.json
        R5\Plugins\R5BusinessRules\Content\InventoryItems\Consumables\...

    Default Mount-Point ist `../../../` (passt zu allen bisher untersuchten
    Windrose-Mods, z.B. Stack_Size_Changes_x04_P.pak). Siehe config.psd1
    fuer Details.

.PARAMETER Action
    Build (Default) baut ein .pak. Init initialisiert einen Mod-Quell-Ordner.

.PARAMETER Source
    Pflicht. Build: Pfad zum Quell-Ordner mit den Mod-Dateien.
    Init:  Pfad zum NEUEN Quell-Ordner (wird erzeugt, muss leer/nicht-existent sein
    oder -Force gesetzt).

.PARAMETER Name
    [Build] Basisname des Paks (ohne Endung). "_P" wird automatisch angehaengt,
    falls noch nicht vorhanden. Default: Name des Quell-Ordners.

.PARAMETER OutDir
    [Build] Wohin das Pak geschrieben wird. Default: $cfg.Paths.Builds (config.psd1).

.PARAMETER MountPoint
    [Build] Mount-Point im Pak. Default: $cfg.Pak.MountPoint ('../../../').

.PARAMETER Version
    [Build] Pak-Format-Version. Default: $cfg.Pak.Version ('V8B').

.PARAMETER RepakExe
    Pfad zu repak.exe. Default: $cfg.Tools.RepakExe.
    Wird auch fuer -Action Init -FromPak benoetigt.

.PARAMETER VanillaDir
    [Init] Quell-Ordner mit dem Vanilla-Snapshot.
    Default: $cfg.Paths.Vanilla (= Output von Dump-WindroseVanilla.ps1).

.PARAMETER FromPak
    [Init] Statt aus VanillaDir wird aus diesem .pak entpackt (z.B. eine bestehende
    Mod als Template). Schliesst -VanillaDir aus.

.PARAMETER Filter
    [Init] Glob-Pattern (eines oder mehrere) zum Filtern auf Filename-Ebene,
    z.B. '*Cannonball*'. Mehrere Pattern werden ODER-verknuepft.

.PARAMETER Categories
    [Init] Liste von InventoryItems-Unterordnern (z.B. Ammo,Consumables).
    Wenn gesetzt, werden nur Dateien unter R5\...\InventoryItems\<Cat>\... uebernommen.

.PARAMETER Force
    Build: Ueberschreibt eine vorhandene Output-Datei ohne Rueckfrage.
    Init:  Erlaubt das Schreiben in einen nicht-leeren Ziel-Ordner (mergt; vorhandene
    Dateien werden ueberschrieben).

.PARAMETER DryRun
    Zeigt nur was passieren wuerde, ohne zu packen, zu entpacken oder zu kopieren.

.EXAMPLE
    .\Build-WindroseMod.ps1 -Source .\Sources\MyStackMod -Name MyStackMod

.EXAMPLE
    .\Build-WindroseMod.ps1 -Source .\Sources\MyStackMod -Force

.EXAMPLE
    # Neue Mod-Source aus Vanilla, nur Cannonball-Items
    .\Build-WindroseMod.ps1 -Action Init -Source .\Sources\MyAmmoMod -Filter '*Cannonball*'

.EXAMPLE
    # Neue Mod-Source aus Vanilla, nur Ammo + Consumables
    .\Build-WindroseMod.ps1 -Action Init -Source .\Sources\MyMod -Categories Ammo,Consumables

.EXAMPLE
    # Neue Mod-Source aus existierendem Pak (als Template, Pfad aus Config)
    .\Build-WindroseMod.ps1 -Action Init -Source .\Sources\MyMod -FromPak $cfg.References.StackModX4
#>

[CmdletBinding()]
param(
    [ValidateSet('Build','Init')]
    [string]$Action = 'Build',

    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Source,

    [string]$Name,

    [string]$OutDir,

    # Defaults aus config.psd1 (Tools.RepakExe, Pak.MountPoint, Pak.Version).
    # Explizit gesetzte Parameter haben Vorrang.
    [string]$MountPoint,

    [ValidateSet('V0','V1','V2','V3','V4','V5','V6','V7','V8A','V8B','V9','V10','V11','')]
    [string]$Version = '',

    [string]$RepakExe,

    # --- Init-Parameter ---
    [string]$VanillaDir,

    [string]$FromPak,

    [string[]]$Filter,

    [string[]]$Categories,

    [switch]$Force,

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

# --- Config laden ---------------------------------------------------------
$cfg = & (Join-Path $PSScriptRoot '_config.ps1')

# Defaults aus Config nachziehen, wenn nicht explizit gesetzt
if (-not $MountPoint) { $MountPoint = [string]$cfg.Pak.MountPoint }
if (-not $Version)    { $Version    = [string]$cfg.Pak.Version }
if (-not $RepakExe)   { $RepakExe   = [string]$cfg.Tools.RepakExe }

function Write-Step($msg)  { Write-Host "==> $msg"          -ForegroundColor Cyan }
function Write-OK($msg)    { Write-Host "    [OK] $msg"     -ForegroundColor Green }
function Write-Warn2($msg) { Write-Host "    [!]  $msg"     -ForegroundColor Yellow }
function Write-Err2($msg)  { Write-Host "    [X]  $msg"     -ForegroundColor Red }

# =========================================================================
#  ACTION: Init
# =========================================================================
if ($Action -eq 'Init') {
    Write-Step 'Init: Mod-Quellordner anlegen'

    # --- Param-Validierung -----------------------------------------------
    if ($FromPak -and $VanillaDir) {
        throw '-FromPak und -VanillaDir koennen nicht gleichzeitig gesetzt werden.'
    }

    # Default VanillaDir aus Config
    if (-not $FromPak -and (-not $VanillaDir -or $VanillaDir.Trim() -eq '')) {
        $VanillaDir = [string]$cfg.Paths.Vanilla
        if (-not $VanillaDir) {
            $VanillaDir = Join-Path $PSScriptRoot 'Sources\Vanilla'
        }
    }

    # Ziel-Ordner-Pfad normalisieren
    $TargetFull = $Source
    if (-not [System.IO.Path]::IsPathRooted($TargetFull)) {
        $TargetFull = [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $TargetFull))
    } else {
        $TargetFull = [System.IO.Path]::GetFullPath($TargetFull)
    }
    Write-OK "Ziel: $TargetFull"

    if (Test-Path -LiteralPath $TargetFull -PathType Leaf) {
        throw "Ziel existiert als Datei (nicht als Ordner): $TargetFull"
    }

    if (Test-Path -LiteralPath $TargetFull -PathType Container) {
        $existingCount = (Get-ChildItem -LiteralPath $TargetFull -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count
        if ($existingCount -gt 0 -and -not $Force) {
            throw "Ziel-Ordner ist nicht leer ($existingCount Dateien): $TargetFull  (mit -Force mergen)"
        }
        if ($existingCount -gt 0) {
            Write-Warn2 "Ziel-Ordner enthaelt bereits $existingCount Datei(en) -> wird gemergt (-Force)"
        }
    }

    # --- Quelle vorbereiten ----------------------------------------------
    $tempUnpackDir = $null
    $sourceRoot    = $null
    $sourceLabel   = $null

    if ($FromPak) {
        if (-not (Test-Path -LiteralPath $FromPak -PathType Leaf)) {
            throw "FromPak nicht gefunden: $FromPak"
        }
        if (-not (Test-Path -LiteralPath $RepakExe)) {
            throw "repak.exe wird fuer -FromPak benoetigt, nicht gefunden: $RepakExe"
        }
        $sourceLabel = "FromPak: $FromPak"
        Write-OK $sourceLabel

        # In Temp entpacken, dann selektiv kopieren
        $tempUnpackDir = Join-Path ([System.IO.Path]::GetTempPath()) ("WindroseModInit_" + [System.Guid]::NewGuid().ToString('N'))
        if ($DryRun) {
            Write-Warn2 "DryRun -> wuerde nach Temp entpacken: $tempUnpackDir"
            # Im DryRun koennen wir die Dateiliste nicht kennen -> Dummy-Anzeige
            $sourceRoot = $null
        } else {
            New-Item -ItemType Directory -Path $tempUnpackDir -Force | Out-Null
            $unpackArgs = @('unpack', $FromPak, '-o', $tempUnpackDir)
            Write-Host "    repak $($unpackArgs -join ' ')" -ForegroundColor DarkGray
            & $RepakExe @unpackArgs
            if ($LASTEXITCODE -ne 0) {
                throw "repak unpack fehlgeschlagen (exit $LASTEXITCODE)"
            }
            $sourceRoot = $tempUnpackDir
        }
    }
    else {
        if (-not (Test-Path -LiteralPath $VanillaDir -PathType Container)) {
            throw "VanillaDir nicht gefunden: $VanillaDir  (zuerst Dump-WindroseVanilla.ps1 ausfuehren?)"
        }
        $VanillaDir = (Resolve-Path -LiteralPath $VanillaDir).Path
        $sourceLabel = "VanillaDir: $VanillaDir"
        Write-OK $sourceLabel
        $sourceRoot = $VanillaDir
    }

    # --- Filter-Logik bauen ----------------------------------------------
    # Categories -> wir matchen auf den relativen Pfad-Segmenten
    $categorySet = $null
    if ($Categories -and $Categories.Count -gt 0) {
        $categorySet = @{}
        foreach ($c in $Categories) {
            if ($c -and $c.Trim() -ne '') { $categorySet[$c.Trim()] = $true }
        }
        Write-OK "Kategorien-Filter: $($categorySet.Keys -join ', ')"
    }

    if ($Filter -and $Filter.Count -gt 0) {
        Write-OK "Filename-Filter: $($Filter -join ', ')"
    }

    function Test-MatchesFilters {
        param(
            [string]$RelativePath,
            [string]$FileName
        )

        # Filename-Filter (ODER-verknuepft)
        if ($script:Filter -and $script:Filter.Count -gt 0) {
            $matched = $false
            foreach ($pat in $script:Filter) {
                if ($FileName -like $pat) { $matched = $true; break }
            }
            if (-not $matched) { return $false }
        }

        # Kategorien-Filter (nur fuer Pfade die InventoryItems\<Cat>\ haben)
        if ($script:categorySet) {
            # erwarteter Pfad: R5\Plugins\R5BusinessRules\Content\InventoryItems\<Cat>\...
            if ($RelativePath -match '(?i)\\InventoryItems\\([^\\]+)\\') {
                $cat = $matches[1]
                if (-not $script:categorySet.ContainsKey($cat)) { return $false }
            } else {
                # Pfad nicht unter InventoryItems\<Cat> -> bei aktivem Categorie-Filter ausschliessen
                return $false
            }
        }

        return $true
    }

    # --- Datei-Liste sammeln ---------------------------------------------
    $filesToCopy = @()
    if (-not $DryRun -or $sourceRoot) {
        if ($sourceRoot) {
            Write-Host "    Scanne $sourceRoot ..." -ForegroundColor DarkGray
            $allFiles = Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -ErrorAction SilentlyContinue
            foreach ($f in $allFiles) {
                # _manifest.json (von VanillaItemDumper) auslassen
                if ($f.Name -eq '_manifest.json') { continue }
                # _INIT.txt o.ae. nicht weiterreichen
                if ($f.Name -eq '_INIT.txt') { continue }

                $rel = $f.FullName.Substring($sourceRoot.Length).TrimStart('\','/')
                if (Test-MatchesFilters -RelativePath $rel -FileName $f.Name) {
                    $filesToCopy += [pscustomobject]@{
                        Source = $f.FullName
                        Rel    = $rel
                    }
                }
            }
        }
    }

    if (-not $DryRun -and $filesToCopy.Count -eq 0) {
        if ($tempUnpackDir -and (Test-Path -LiteralPath $tempUnpackDir)) {
            Remove-Item -LiteralPath $tempUnpackDir -Recurse -Force -ErrorAction SilentlyContinue
        }
        throw 'Keine Dateien zum Kopieren gefunden (Filter zu restriktiv?).'
    }

    Write-OK ("Gefiltert: {0} Datei(en) werden uebernommen" -f $filesToCopy.Count)

    # --- Vorschau (DryRun oder generell ein paar Beispiele) ---------------
    $sampleN = [Math]::Min(5, $filesToCopy.Count)
    if ($sampleN -gt 0) {
        Write-Host '    Beispiele:' -ForegroundColor DarkGray
        for ($i = 0; $i -lt $sampleN; $i++) {
            Write-Host ("      {0}" -f $filesToCopy[$i].Rel) -ForegroundColor DarkGray
        }
        if ($filesToCopy.Count -gt $sampleN) {
            Write-Host ("      ... ({0} weitere)" -f ($filesToCopy.Count - $sampleN)) -ForegroundColor DarkGray
        }
    }

    if ($DryRun) {
        Write-Warn2 'DryRun -> kein Kopieren ausgefuehrt'
        if ($tempUnpackDir -and (Test-Path -LiteralPath $tempUnpackDir)) {
            Remove-Item -LiteralPath $tempUnpackDir -Recurse -Force -ErrorAction SilentlyContinue
        }
        Write-Host ''
        Write-Step 'Init: Fertig (DryRun)'
        return
    }

    # --- Ziel-Ordner anlegen + kopieren ----------------------------------
    if (-not (Test-Path -LiteralPath $TargetFull)) {
        New-Item -ItemType Directory -Path $TargetFull -Force | Out-Null
    }

    $copied = 0
    foreach ($entry in $filesToCopy) {
        $destPath = Join-Path $TargetFull $entry.Rel
        $destDir  = Split-Path -Parent $destPath
        if (-not (Test-Path -LiteralPath $destDir)) {
            New-Item -ItemType Directory -Path $destDir -Force | Out-Null
        }
        Copy-Item -LiteralPath $entry.Source -Destination $destPath -Force
        $copied++
    }
    Write-OK "$copied Datei(en) kopiert nach $TargetFull"

    # --- _INIT.txt Marker schreiben --------------------------------------
    $initInfo = @"
# Windrose Mod Source -- initialized by Build-WindroseMod.ps1
date     : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
source   : $sourceLabel
filter   : $(if ($Filter)     { $Filter     -join ', ' } else { '(none)' })
category : $(if ($Categories) { $Categories -join ', ' } else { '(none)' })
files    : $copied

Hinweis:
  - Dieser Ordner enthaelt eine Kopie der Quell-JSONs als Ausgangspunkt
    fuer eine eigene Mod. Editiere die Werte und baue dann mit
        .\Build-WindroseMod.ps1 -Source <DIESER ORDNER>
    Das fertige _P.pak musst du selbst in den ~mods-Ordner deines Servers
    bzw. Spiel-Clients kopieren.
  - Wenn du nur einzelne Items modifizierst, loesche die unveraenderten
    JSONs aus diesem Ordner -- alles was nicht im _P.pak liegt, behaelt
    seinen Vanilla-Wert.
"@
    Set-Content -LiteralPath (Join-Path $TargetFull '_INIT.txt') -Value $initInfo -Encoding UTF8
    Write-OK '_INIT.txt geschrieben'

    # --- Cleanup ----------------------------------------------------------
    if ($tempUnpackDir -and (Test-Path -LiteralPath $tempUnpackDir)) {
        Remove-Item -LiteralPath $tempUnpackDir -Recurse -Force -ErrorAction SilentlyContinue
        Write-OK 'Temp-Unpack-Verzeichnis geloescht'
    }

    Write-Host ''
    Write-Step 'Init: Fertig'
    Write-OK "Mod-Source: $TargetFull"
    Write-OK "Naechster Schritt: Werte in $TargetFull editieren, dann"
    Write-Host ("    .\Build-WindroseMod.ps1 -Source `"$TargetFull`"") -ForegroundColor DarkGray
    return
}

# =========================================================================
#  ACTION: Build (Default)
# =========================================================================

# --- 1) Validierung -------------------------------------------------------
Write-Step 'Pruefe Voraussetzungen'

if (-not (Test-Path -LiteralPath $RepakExe)) {
    throw "repak.exe nicht gefunden: $RepakExe"
}
Write-OK "repak.exe: $RepakExe"

$SourceFull = (Resolve-Path -LiteralPath $Source).Path
if (-not (Test-Path -LiteralPath $SourceFull -PathType Container)) {
    throw "Source ist kein Ordner: $SourceFull"
}

# Meta-Files, die im Quell-Ordner liegen koennen (z.B. _INIT.txt von -Action Init),
# aber nicht ins finale Pak gehoeren. Werden vor dem Pack temporaer weggemoved.
$IgnoredMetaFiles = @('_INIT.txt')

$allFiles  = Get-ChildItem -LiteralPath $SourceFull -Recurse -File
$payload   = $allFiles | Where-Object { $IgnoredMetaFiles -notcontains $_.Name }
$fileCount = ($payload | Measure-Object).Count
$skipped   = ($allFiles | Measure-Object).Count - $fileCount
if ($fileCount -eq 0) {
    throw "Source-Ordner ist leer (oder enthaelt nur Meta-Files): $SourceFull"
}
if ($skipped -gt 0) {
    Write-OK "Source: $SourceFull ($fileCount Pak-Dateien, $skipped Meta-File(s) ignoriert)"
} else {
    Write-OK "Source: $SourceFull ($fileCount Dateien)"
}

if (-not $Name -or $Name.Trim() -eq '') {
    $Name = Split-Path -Leaf $SourceFull
}
# Sicherstellen, dass Name auf "_P" endet (Patch-Marker)
if ($Name -notmatch '_P$') {
    $Name = $Name + '_P'
    Write-Warn2 "Name endet nicht auf _P -> automatisch zu '$Name' korrigiert"
}

if (-not $OutDir -or $OutDir.Trim() -eq '') {
    $OutDir = [string]$cfg.Paths.Builds
    if (-not $OutDir) {
        $OutDir = Join-Path $PSScriptRoot 'Builds'
    }
}
if (-not (Test-Path -LiteralPath $OutDir)) {
    if (-not $DryRun) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
}
$resolved = Resolve-Path -LiteralPath $OutDir -ErrorAction SilentlyContinue
if ($resolved) {
    $OutDir = $resolved.Path
} else {
    # Im DryRun existiert der Ordner ggf. noch nicht -> Pfad selbst normalisieren
    $OutDir = [System.IO.Path]::GetFullPath($OutDir)
}

$OutPak = Join-Path $OutDir ($Name + '.pak')
Write-OK "Output: $OutPak"

if ((Test-Path -LiteralPath $OutPak) -and -not $Force -and -not $DryRun) {
    throw "Output existiert bereits: $OutPak  (mit -Force ueberschreiben)"
}

# --- 2) Pak bauen ---------------------------------------------------------
Write-Step 'Baue .pak'

$packArgs = @(
    'pack',
    '--mount-point', $MountPoint,
    '--version',     $Version,
    $SourceFull,
    $OutPak
)

Write-Host "    repak $($packArgs -join ' ')" -ForegroundColor DarkGray

if ($DryRun) {
    Write-Warn2 'DryRun -> kein Pack ausgefuehrt'
} else {
    # Meta-Files temporaer ins User-Temp wegmoven (NICHT im Source-Ordner,
    # sonst packt repak die Backup-Datei mit ein!)
    $metaBackups   = @()
    $metaStashRoot = $null
    $metaList = foreach ($metaName in $IgnoredMetaFiles) {
        $metaPath = Join-Path $SourceFull $metaName
        if (Test-Path -LiteralPath $metaPath -PathType Leaf) { $metaPath }
    }
    if ($metaList) {
        $metaStashRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("WindroseModBuildMeta_" + [System.Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $metaStashRoot -Force | Out-Null
        foreach ($metaPath in $metaList) {
            $tmpPath = Join-Path $metaStashRoot (Split-Path -Leaf $metaPath)
            Move-Item -LiteralPath $metaPath -Destination $tmpPath -Force
            $metaBackups += [pscustomobject]@{ Orig = $metaPath; Tmp = $tmpPath }
            Write-Host "    (meta '$(Split-Path -Leaf $metaPath)' temporaer ausgeblendet)" -ForegroundColor DarkGray
        }
    }

    try {
        & $RepakExe @packArgs
        if ($LASTEXITCODE -ne 0) { throw "repak pack fehlgeschlagen (exit $LASTEXITCODE)" }
        if (-not (Test-Path -LiteralPath $OutPak)) {
            throw "Pak wurde nicht erzeugt: $OutPak"
        }
        $sizeKB = [math]::Round((Get-Item -LiteralPath $OutPak).Length / 1KB, 1)
        Write-OK ("Pak gebaut: {0}  ({1} KB)" -f $OutPak, $sizeKB)
    } finally {
        foreach ($b in $metaBackups) {
            if (Test-Path -LiteralPath $b.Tmp -PathType Leaf) {
                Move-Item -LiteralPath $b.Tmp -Destination $b.Orig -Force
            }
        }
        if ($metaStashRoot -and (Test-Path -LiteralPath $metaStashRoot)) {
            Remove-Item -LiteralPath $metaStashRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

# --- 3) Optional: Verify --------------------------------------------------
if (-not $DryRun) {
    Write-Step 'Verifiziere Pak'
    $info = & $RepakExe info $OutPak 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Err2 'repak info fehlgeschlagen'
        $info | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
    } else {
        $info | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
    }
}

# --- 4) Zusammenfassung ---------------------------------------------------
Write-Host ''
Write-Step 'Fertig'
Write-OK "Pak: $OutPak"
Write-Host ''
Write-Host '    Naechster Schritt: Pak in den ~mods-Ordner deines Servers/Clients kopieren, z.B.' -ForegroundColor DarkGray
Write-Host ("    Copy-Item `"$OutPak`" '<PFAD>\R5\Content\Paks\~mods\' -Force") -ForegroundColor DarkGray
if ($DryRun) { Write-Warn2 'DryRun aktiv -> nichts wurde wirklich geschrieben' }
