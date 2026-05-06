<#
.SYNOPSIS
    Builds a _P.pak for Windrose from a source folder. Can optionally
    initialise a new mod source from the vanilla snapshot or from an
    existing pak.

.DESCRIPTION
    Two modes via -Action:

      -Action Build  (default): Wrapper around repak.exe. Expects a source
        folder whose contents will be mounted "over" the original files in
        the game. The pak name always ends in "_P" (patch marker) so the
        game loads it as an override. The finished pak lands in -OutDir and
        is NOT installed automatically -- copy it yourself into the
        respective ~mods folder (server or client).

      -Action Init: Creates a new mod source folder by copying/unpacking
        files from Sources\Vanilla\ (default) or from an existing .pak
        (-FromPak). Optionally filtered via -Filter (filename glob) or
        -Categories (list of InventoryItems sub-folders).

    Typical folder structure beneath -Source (for Build):
        R5\Plugins\R5BusinessRules\Content\InventoryItems\Ammo\*.json
        R5\Plugins\R5BusinessRules\Content\InventoryItems\Consumables\...

    Default mount point is `../../../` (matches every Windrose mod
    examined so far, e.g. Stack_Size_Changes_x04_P.pak). See config.psd1
    for details.

.PARAMETER Action
    Build (default) builds a .pak. Init initialises a mod source folder.

.PARAMETER Source
    Required. Build: path to the source folder with the mod files.
    Init:  path to the NEW source folder (will be created; must be
    empty/non-existent or -Force given).

.PARAMETER Name
    [Build] Base name of the pak (without extension). "_P" is appended
    automatically if not already present. Default: name of the source folder.

.PARAMETER OutDir
    [Build] Where the pak is written. Default: $cfg.Paths.Builds (config.psd1).

.PARAMETER MountPoint
    [Build] Mount point inside the pak. Default: $cfg.Pak.MountPoint ('../../../').

.PARAMETER Version
    [Build] Pak format version. Default: $cfg.Pak.Version ('V8B').

.PARAMETER RepakExe
    Path to repak.exe. Default: $cfg.Tools.RepakExe.
    Also required for -Action Init -FromPak.

.PARAMETER VanillaDir
    [Init] Source folder with the vanilla snapshot.
    Default: $cfg.Paths.Vanilla (= output of Dump-WindroseVanilla.ps1).

.PARAMETER FromPak
    [Init] Instead of unpacking from VanillaDir, unpack from this .pak (e.g.
    an existing mod as a template). Mutually exclusive with -VanillaDir.

.PARAMETER Filter
    [Init] Glob pattern (one or more) to filter on the filename level,
    e.g. '*Cannonball*'. Multiple patterns are OR-combined.

.PARAMETER Categories
    [Init] List of InventoryItems sub-folders (e.g. Ammo,Consumables).
    If set, only files under R5\...\InventoryItems\<Cat>\... are kept.

.PARAMETER Force
    Build: Overwrites an existing output file without asking.
    Init:  Allows writing into a non-empty target folder (merges; existing
    files are overwritten).

.PARAMETER DryRun
    Only show what would happen, without packing, unpacking, or copying.

.EXAMPLE
    .\Build-WindroseMod.ps1 -Source .\Sources\MyStackMod -Name MyStackMod

.EXAMPLE
    .\Build-WindroseMod.ps1 -Source .\Sources\MyStackMod -Force

.EXAMPLE
    # New mod source from vanilla, only Cannonball items
    .\Build-WindroseMod.ps1 -Action Init -Source .\Sources\MyAmmoMod -Filter '*Cannonball*'

.EXAMPLE
    # New mod source from vanilla, only Ammo + Consumables
    .\Build-WindroseMod.ps1 -Action Init -Source .\Sources\MyMod -Categories Ammo,Consumables

.EXAMPLE
    # New mod source from an existing pak (as template)
    .\Build-WindroseMod.ps1 -Action Init -Source .\Sources\MyMod -FromPak 'C:\path\to\some_P.pak'
#>

[CmdletBinding()]
param(
    [ValidateSet('Build','Init')]
    [string]$Action = 'Build',

    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Source,

    [string]$Name,

    [string]$OutDir,

    # Defaults from config.psd1 (Tools.RepakExe, Pak.MountPoint, Pak.Version).
    # Explicitly set parameters take precedence.
    [string]$MountPoint,

    [ValidateSet('V0','V1','V2','V3','V4','V5','V6','V7','V8A','V8B','V9','V10','V11','')]
    [string]$Version = '',

    [string]$RepakExe,

    # --- Init parameters ---
    [string]$VanillaDir,

    [string]$FromPak,

    [string[]]$Filter,

    [string[]]$Categories,

    [switch]$Force,

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

# --- Load config ----------------------------------------------------------
$cfg = & (Join-Path $PSScriptRoot '_config.ps1')

# Pull in defaults from config when not explicitly set
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
    Write-Step 'Init: create mod source folder'

    # --- Param validation ------------------------------------------------
    if ($FromPak -and $VanillaDir) {
        throw '-FromPak and -VanillaDir cannot be set at the same time.'
    }

    # Default VanillaDir from config
    if (-not $FromPak -and (-not $VanillaDir -or $VanillaDir.Trim() -eq '')) {
        $VanillaDir = [string]$cfg.Paths.Vanilla
        if (-not $VanillaDir) {
            $VanillaDir = Join-Path $PSScriptRoot 'Sources\Vanilla'
        }
    }

    # Normalise target folder path
    $TargetFull = $Source
    if (-not [System.IO.Path]::IsPathRooted($TargetFull)) {
        $TargetFull = [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $TargetFull))
    } else {
        $TargetFull = [System.IO.Path]::GetFullPath($TargetFull)
    }
    Write-OK "Target: $TargetFull"

    if (Test-Path -LiteralPath $TargetFull -PathType Leaf) {
        throw "Target exists as a file (not a folder): $TargetFull"
    }

    if (Test-Path -LiteralPath $TargetFull -PathType Container) {
        $existingCount = (Get-ChildItem -LiteralPath $TargetFull -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count
        if ($existingCount -gt 0 -and -not $Force) {
            throw "Target folder is not empty ($existingCount files): $TargetFull  (use -Force to merge)"
        }
        if ($existingCount -gt 0) {
            Write-Warn2 "Target folder already contains $existingCount file(s) -> merging (-Force)"
        }
    }

    # --- Prepare source --------------------------------------------------
    $tempUnpackDir = $null
    $sourceRoot    = $null
    $sourceLabel   = $null

    if ($FromPak) {
        if (-not (Test-Path -LiteralPath $FromPak -PathType Leaf)) {
            throw "FromPak not found: $FromPak"
        }
        if (-not (Test-Path -LiteralPath $RepakExe)) {
            throw "repak.exe is required for -FromPak, not found: $RepakExe"
        }
        $sourceLabel = "FromPak: $FromPak"
        Write-OK $sourceLabel

        # Unpack into temp, then copy selectively
        $tempUnpackDir = Join-Path ([System.IO.Path]::GetTempPath()) ("WindroseModInit_" + [System.Guid]::NewGuid().ToString('N'))
        if ($DryRun) {
            Write-Warn2 "DryRun -> would unpack into temp: $tempUnpackDir"
            # In DryRun we can't know the file list -> dummy display
            $sourceRoot = $null
        } else {
            New-Item -ItemType Directory -Path $tempUnpackDir -Force | Out-Null
            $unpackArgs = @('unpack', $FromPak, '-o', $tempUnpackDir)
            Write-Host "    repak $($unpackArgs -join ' ')" -ForegroundColor DarkGray
            & $RepakExe @unpackArgs
            if ($LASTEXITCODE -ne 0) {
                throw "repak unpack failed (exit $LASTEXITCODE)"
            }
            $sourceRoot = $tempUnpackDir
        }
    }
    else {
        if (-not (Test-Path -LiteralPath $VanillaDir -PathType Container)) {
            throw "VanillaDir not found: $VanillaDir  (run Dump-WindroseVanilla.ps1 first?)"
        }
        $VanillaDir = (Resolve-Path -LiteralPath $VanillaDir).Path
        $sourceLabel = "VanillaDir: $VanillaDir"
        Write-OK $sourceLabel
        $sourceRoot = $VanillaDir
    }

    # --- Build filter logic ----------------------------------------------
    # Categories -> matched against relative path segments
    $categorySet = $null
    if ($Categories -and $Categories.Count -gt 0) {
        $categorySet = @{}
        foreach ($c in $Categories) {
            if ($c -and $c.Trim() -ne '') { $categorySet[$c.Trim()] = $true }
        }
        Write-OK "Category filter: $($categorySet.Keys -join ', ')"
    }

    if ($Filter -and $Filter.Count -gt 0) {
        Write-OK "Filename filter: $($Filter -join ', ')"
    }

    function Test-MatchesFilters {
        param(
            [string]$RelativePath,
            [string]$FileName
        )

        # Filename filter (OR-combined)
        if ($script:Filter -and $script:Filter.Count -gt 0) {
            $matched = $false
            foreach ($pat in $script:Filter) {
                if ($FileName -like $pat) { $matched = $true; break }
            }
            if (-not $matched) { return $false }
        }

        # Category filter (only for paths under InventoryItems\<Cat>\)
        if ($script:categorySet) {
            # expected path: R5\Plugins\R5BusinessRules\Content\InventoryItems\<Cat>\...
            if ($RelativePath -match '(?i)\\InventoryItems\\([^\\]+)\\') {
                $cat = $matches[1]
                if (-not $script:categorySet.ContainsKey($cat)) { return $false }
            } else {
                # Path not under InventoryItems\<Cat> -> exclude when category filter active
                return $false
            }
        }

        return $true
    }

    # --- Collect file list -----------------------------------------------
    $filesToCopy = @()
    if (-not $DryRun -or $sourceRoot) {
        if ($sourceRoot) {
            Write-Host "    Scanning $sourceRoot ..." -ForegroundColor DarkGray
            $allFiles = Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -ErrorAction SilentlyContinue
            foreach ($f in $allFiles) {
                # Skip _manifest.json (from VanillaItemDumper)
                if ($f.Name -eq '_manifest.json') { continue }
                # Don't pass _INIT.txt etc. through
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
        throw 'No files to copy (filter too restrictive?).'
    }

    Write-OK ("Filtered: {0} file(s) will be taken over" -f $filesToCopy.Count)

    # --- Preview (DryRun or just a few examples) -------------------------
    $sampleN = [Math]::Min(5, $filesToCopy.Count)
    if ($sampleN -gt 0) {
        Write-Host '    Examples:' -ForegroundColor DarkGray
        for ($i = 0; $i -lt $sampleN; $i++) {
            Write-Host ("      {0}" -f $filesToCopy[$i].Rel) -ForegroundColor DarkGray
        }
        if ($filesToCopy.Count -gt $sampleN) {
            Write-Host ("      ... ({0} more)" -f ($filesToCopy.Count - $sampleN)) -ForegroundColor DarkGray
        }
    }

    if ($DryRun) {
        Write-Warn2 'DryRun -> no copy performed'
        if ($tempUnpackDir -and (Test-Path -LiteralPath $tempUnpackDir)) {
            Remove-Item -LiteralPath $tempUnpackDir -Recurse -Force -ErrorAction SilentlyContinue
        }
        Write-Host ''
        Write-Step 'Init: done (DryRun)'
        return
    }

    # --- Create target folder + copy -------------------------------------
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
    Write-OK "$copied file(s) copied to $TargetFull"

    # --- Write _INIT.txt marker ------------------------------------------
    $initInfo = @"
# Windrose Mod Source -- initialized by Build-WindroseMod.ps1
date     : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
source   : $sourceLabel
filter   : $(if ($Filter)     { $Filter     -join ', ' } else { '(none)' })
category : $(if ($Categories) { $Categories -join ', ' } else { '(none)' })
files    : $copied

Notes:
  - This folder contains a copy of the source JSONs as a starting point
    for your own mod. Edit the values, then build with
        .\Build-WindroseMod.ps1 -Source <THIS FOLDER>
    You have to copy the resulting _P.pak into the ~mods folder of your
    server or game client yourself.
  - If you only modify a few items, delete the unchanged JSONs from this
    folder -- anything not in the _P.pak keeps its vanilla value.
"@
    Set-Content -LiteralPath (Join-Path $TargetFull '_INIT.txt') -Value $initInfo -Encoding UTF8
    Write-OK '_INIT.txt written'

    # --- Cleanup ---------------------------------------------------------
    if ($tempUnpackDir -and (Test-Path -LiteralPath $tempUnpackDir)) {
        Remove-Item -LiteralPath $tempUnpackDir -Recurse -Force -ErrorAction SilentlyContinue
        Write-OK 'Temp unpack directory deleted'
    }

    Write-Host ''
    Write-Step 'Init: done'
    Write-OK "Mod source: $TargetFull"
    Write-OK "Next step: edit values in $TargetFull, then"
    Write-Host ("    .\Build-WindroseMod.ps1 -Source `"$TargetFull`"") -ForegroundColor DarkGray
    return
}

# =========================================================================
#  ACTION: Build (default)
# =========================================================================

# --- 1) Validation --------------------------------------------------------
Write-Step 'Checking prerequisites'

if (-not (Test-Path -LiteralPath $RepakExe)) {
    throw "repak.exe not found: $RepakExe"
}
Write-OK "repak.exe: $RepakExe"

$SourceFull = (Resolve-Path -LiteralPath $Source).Path
if (-not (Test-Path -LiteralPath $SourceFull -PathType Container)) {
    throw "Source is not a folder: $SourceFull"
}

# Meta files that may live in the source folder (e.g. _INIT.txt from -Action
# Init) but should NOT be packed into the final pak. Temporarily moved
# aside before packing.
$IgnoredMetaFiles = @('_INIT.txt')

$allFiles  = Get-ChildItem -LiteralPath $SourceFull -Recurse -File
$payload   = $allFiles | Where-Object { $IgnoredMetaFiles -notcontains $_.Name }
$fileCount = ($payload | Measure-Object).Count
$skipped   = ($allFiles | Measure-Object).Count - $fileCount
if ($fileCount -eq 0) {
    throw "Source folder is empty (or contains only meta files): $SourceFull"
}
if ($skipped -gt 0) {
    Write-OK "Source: $SourceFull ($fileCount pak files, $skipped meta file(s) ignored)"
} else {
    Write-OK "Source: $SourceFull ($fileCount files)"
}

if (-not $Name -or $Name.Trim() -eq '') {
    $Name = Split-Path -Leaf $SourceFull
}
# Ensure the name ends with "_P" (patch marker)
if ($Name -notmatch '_P$') {
    $Name = $Name + '_P'
    Write-Warn2 "Name doesn't end in _P -> auto-corrected to '$Name'"
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
    # In DryRun the folder may not exist yet -> normalise the path manually
    $OutDir = [System.IO.Path]::GetFullPath($OutDir)
}

$OutPak = Join-Path $OutDir ($Name + '.pak')
Write-OK "Output: $OutPak"

if ((Test-Path -LiteralPath $OutPak) -and -not $Force -and -not $DryRun) {
    throw "Output already exists: $OutPak  (use -Force to overwrite)"
}

# --- 2) Build pak ---------------------------------------------------------
Write-Step 'Building .pak'

$packArgs = @(
    'pack',
    '--mount-point', $MountPoint,
    '--version',     $Version,
    $SourceFull,
    $OutPak
)

Write-Host "    repak $($packArgs -join ' ')" -ForegroundColor DarkGray

if ($DryRun) {
    Write-Warn2 'DryRun -> no pack performed'
} else {
    # Move meta files temporarily into the user temp dir (NOT inside the
    # source folder, otherwise repak would pack the backup file as well!)
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
            Write-Host "    (meta '$(Split-Path -Leaf $metaPath)' temporarily hidden)" -ForegroundColor DarkGray
        }
    }

    try {
        & $RepakExe @packArgs
        if ($LASTEXITCODE -ne 0) { throw "repak pack failed (exit $LASTEXITCODE)" }
        if (-not (Test-Path -LiteralPath $OutPak)) {
            throw "Pak was not created: $OutPak"
        }
        $sizeKB = [math]::Round((Get-Item -LiteralPath $OutPak).Length / 1KB, 1)
        Write-OK ("Pak built: {0}  ({1} KB)" -f $OutPak, $sizeKB)
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

# --- 3) Optional: verify --------------------------------------------------
if (-not $DryRun) {
    Write-Step 'Verifying pak'
    $info = & $RepakExe info $OutPak 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Err2 'repak info failed'
        $info | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
    } else {
        $info | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
    }
}

# --- 4) Summary -----------------------------------------------------------
Write-Host ''
Write-Step 'Done'
Write-OK "Pak: $OutPak"
Write-Host ''
Write-Host '    Next step: copy the pak into your server/client ~mods folder, e.g.' -ForegroundColor DarkGray
Write-Host ("    Copy-Item `"$OutPak`" '<PATH>\R5\Content\Paks\~mods\' -Force") -ForegroundColor DarkGray
if ($DryRun) { Write-Warn2 'DryRun active -> nothing was actually written' }
