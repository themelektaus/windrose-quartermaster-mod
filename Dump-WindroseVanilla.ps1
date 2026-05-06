<#
.SYNOPSIS
    Reorganises the flat JSON dumps produced by the UE4SS mod
    `VanillaItemDumper` into a directory tree that can be used directly
    as a mod source.

.DESCRIPTION
    The UE4SS Lua mod cannot create directories at runtime
    (os.execute / io.popen deadlock in this embedding), so it writes its
    dumps *flat*, side-by-side, encoding the original path via the
    separator `___`:

        R5___Plugins___R5BusinessRules___Content___InventoryItems___Ammo___DA_AID_X.json

    This script reverses that:

        R5\Plugins\R5BusinessRules\Content\InventoryItems\Ammo\DA_AID_X.json

    and places the result under -OutDir. That gives you a clean vanilla
    snapshot source that you can diff your own mods against, or use
    directly as a source for `Build-WindroseMod.ps1`.

.PARAMETER DumpsDir
    Source folder with the flat `R5___*.json` dumps. Default: $cfg.Paths.Dumps
    (config.psd1) -- handy when you copy/symlink the dump folder from the
    running server slot before invoking the script.

.PARAMETER OutDir
    Target folder for the reconstructed tree. Default: $cfg.Paths.Vanilla
    (config.psd1, normally Sources\Vanilla).

.PARAMETER Clean
    Deletes the contents of -OutDir before writing the new tree.
    Useful to make sure no stale files from an older dump run remain.

.PARAMETER Force
    Overwrites individual existing target files without asking.
    Without -Force the script aborts as soon as the first file collides.

.PARAMETER Filter
    Optional wildcard filter applied to the reconstructed tree path
    (e.g. `*Ammo*` or `R5\Plugins\*Cannonball*`). Only matching files are
    taken over. Useful when you only want a specific area.

.PARAMETER PathSeparator
    Separator the Lua mod uses to encode paths. Normally read from
    `_manifest.json`; only set this if the manifest is missing.

.PARAMETER DryRun
    Only show what would happen, do not write anything.

.EXAMPLE
    .\Dump-WindroseVanilla.ps1
    # Default (from config.psd1): $cfg.Paths.Dumps -> $cfg.Paths.Vanilla

.EXAMPLE
    .\Dump-WindroseVanilla.ps1 -Clean -Force
    # Empty OutDir first, then write everything fresh

.EXAMPLE
    .\Dump-WindroseVanilla.ps1 -Filter '*Cannonball*' -OutDir .\Sources\CannonOnly
    # Reorganise only Cannonball items into a separate folder
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

# --- Load config ----------------------------------------------------------
$cfg = & (Join-Path $PSScriptRoot '_config.ps1')
if (-not $DumpsDir) { $DumpsDir = [string]$cfg.Paths.Dumps }

function Write-Step($msg)  { Write-Host "==> $msg"          -ForegroundColor Cyan }
function Write-OK($msg)    { Write-Host "    [OK] $msg"     -ForegroundColor Green }
function Write-Warn2($msg) { Write-Host "    [!]  $msg"     -ForegroundColor Yellow }
function Write-Err2($msg)  { Write-Host "    [X]  $msg"     -ForegroundColor Red }

# --- 1) Validation --------------------------------------------------------
Write-Step 'Checking prerequisites'

if (-not (Test-Path -LiteralPath $DumpsDir -PathType Container)) {
    throw "DumpsDir does not exist: $DumpsDir"
}
$DumpsDir = (Resolve-Path -LiteralPath $DumpsDir).Path
Write-OK "DumpsDir: $DumpsDir"

if (-not $OutDir -or $OutDir.Trim() -eq '') {
    $OutDir = [string]$cfg.Paths.Vanilla
    if (-not $OutDir) {
        $OutDir = Join-Path $PSScriptRoot 'Sources\Vanilla'
    }
}
# Create target folder if missing, then normalise
if (-not (Test-Path -LiteralPath $OutDir)) {
    if (-not $DryRun) {
        New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
    }
}
$resolved = Resolve-Path -LiteralPath $OutDir -ErrorAction SilentlyContinue
if ($resolved) { $OutDir = $resolved.Path }
else           { $OutDir = [System.IO.Path]::GetFullPath($OutDir) }
Write-OK "OutDir:   $OutDir"

# --- 2) Read manifest (optional, informational) ---------------------------
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
        Write-Warn2 "Manifest could not be read: $($_.Exception.Message)"
    }
} else {
    Write-Warn2 "_manifest.json missing -> proceeding purely file-based"
}
if (-not $PathSeparator -or $PathSeparator -eq '') {
    $PathSeparator = '___'
    Write-Warn2 "PathSeparator not from manifest -> default '$PathSeparator'"
}
Write-OK "PathSeparator: '$PathSeparator'"

# --- 3) Collect source files ---------------------------------------------
Write-Step 'Collecting source files'

# Only *.json files with the tree prefix; the manifest and any probe files
# are deliberately ignored. The prefix is everything up to the first
# separator in the first sample -- we expect "R5", but if someone extends
# the structure it will be picked up here.
$allJson = Get-ChildItem -LiteralPath $DumpsDir -File -Filter '*.json'
$flat = @($allJson | Where-Object { $_.Name -like ("*" + $PathSeparator + "*") })

if ($flat.Count -eq 0) {
    throw "No flat dump files with separator '$PathSeparator' found in $DumpsDir."
}
Write-OK ("{0} flat dump files found ({1} JSON files in total)" -f $flat.Count, $allJson.Count)

# --- 4) Derive tree path --------------------------------------------------
function ConvertTo-TreePath {
    param([string]$FlatBase, [string]$Sep)
    # Replace the encoded separator with real directory separators.
    # We use "\" because the script is Windows-specific.
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
    Write-OK ("{0} files match filter '{1}'" -f $plan.Count, $Filter)
}
if ($plan.Count -eq 0) {
    throw 'No files left after filtering.'
}

# --- 5) Collisions / Clean -----------------------------------------------
if ($Clean) {
    Write-Step 'Clean: emptying OutDir contents'
    if (-not $DryRun) {
        Get-ChildItem -LiteralPath $OutDir -Force -ErrorAction SilentlyContinue |
            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
        Write-OK 'OutDir emptied'
    } else {
        Write-Warn2 'DryRun -> OutDir left unchanged'
    }
}

if (-not $Clean -and -not $Force) {
    $collisions = @($plan | Where-Object { Test-Path -LiteralPath $_.Dest })
    if ($collisions.Count -gt 0) {
        Write-Err2 ("{0} target files already exist, example: {1}" -f `
            $collisions.Count, $collisions[0].Dest)
        throw 'Aborted: collisions found. Use -Force to overwrite, or -Clean to wipe first.'
    }
}

# --- 6) Write -------------------------------------------------------------
Write-Step 'Reorganising files'

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
Write-Step 'Statistics'

# Determine the top-level category under ...\InventoryItems\<CAT>\... if
# the path matches the pattern. Otherwise we fall back to the segment
# right before the filename so we still have something to count.
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

# --- 8) Summary -----------------------------------------------------------
Write-Host ''
Write-Step 'Done'
Write-OK ("{0} files written" -f $writeCount)
Write-OK ("Target: {0}" -f $OutDir)
if ($Filter)      { Write-OK ("Filter active: {0}" -f $Filter) }
if ($DryRun)      { Write-Warn2 'DryRun active -> nothing was actually written' }
