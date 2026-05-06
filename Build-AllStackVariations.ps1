<#
.SYNOPSIS
    Builds all stack-size variants (multipliers x2..x100 + absolute
    999..9999) into the Builds folder.

.DESCRIPTION
    For each variant:
        1. Build-WindroseMod.ps1 -Action Init (from the Stack-mod x4 pak as
           structural source)
        2. Apply-StackMultiplier.ps1 (with -VanillaSource, multiplies
           vanilla*N or sets an absolute value)
        3. Build-WindroseMod.ps1 (build)

    The finished .pak files end up in <OutDir> and must be copied into the
    server/client ~mods folder yourself.

    Output: <OutDir>\StackSize_<name>_P.pak

    Source folders are created at Sources\StackSize_<name>\ and kept (by
    default) so you can inspect/diff them. Use -CleanSources to delete them
    after a successful build.

.PARAMETER Variants
    List of variants to build. Default: all 11.
    Allowed names: x2, x3, x4, x5, x6, x7, x8, x9, x10, x100, 999, 9999
    (x4 is intentionally NOT excluded by default; it matches the currently
    deployed pak. Pass it explicitly if you want to rebuild it.)

.PARAMETER FromPak
    Source pak for the Init step (provides the full JSON schema with mesh
    paths, string enums, etc.). Required if you want to (re)build the per-variant
    Sources from a reference pak; otherwise the existing Sources/<variant>/
    folders are reused.

.PARAMETER VanillaSource
    Path to the vanilla dump (537 real vanilla MaxCountInSlot values).
    Default: $cfg.Paths.Vanilla (config.psd1).

.PARAMETER SrcRoot
    Where the per-variant source folders are placed.
    Default: $cfg.Paths.Sources (config.psd1).

.PARAMETER OutDir
    Where the finished .pak files end up.
    Default: $cfg.Paths.Builds (config.psd1).

.PARAMETER Force
    Overwrite existing src folders and builds.

.PARAMETER CleanSources
    Delete the variant's src folder after a successful build.

.PARAMETER DryRun
    Only show what would happen.

.EXAMPLE
    # Build all 11 variants
    .\Build-AllStackVariations.ps1

.EXAMPLE
    # Only selected variants, overwrite existing
    .\Build-AllStackVariations.ps1 -Variants x10,x100,9999 -Force

.EXAMPLE
    # Build all, then clean up src folders
    .\Build-AllStackVariations.ps1 -CleanSources -Force
#>

[CmdletBinding()]
param(
    [string[]]$Variants = @(
        'x2','x3','x4','x5','x6','x7','x8','x9','x10','x100',
        '999','9999'
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

# --- Load config ----------------------------------------------------------
$cfg = & (Join-Path $PSScriptRoot '_config.ps1')

if (-not $VanillaSource) { $VanillaSource = [string]$cfg.Paths.Vanilla }
if (-not $SrcRoot)       { $SrcRoot       = [string]$cfg.Paths.Sources }
if (-not $OutDir)        { $OutDir        = [string]$cfg.Paths.Builds }

function Write-Step($msg)  { Write-Host "==> $msg"          -ForegroundColor Cyan }
function Write-OK($msg)    { Write-Host "    [OK] $msg"     -ForegroundColor Green }
function Write-Warn2($msg) { Write-Host "    [!]  $msg"     -ForegroundColor Yellow }
function Write-Err2($msg)  { Write-Host "    [X]  $msg"     -ForegroundColor Red }

# Verify paths
$ScriptRoot   = Split-Path -Parent $MyInvocation.MyCommand.Path
$BuildScript  = Join-Path $ScriptRoot 'Build-WindroseMod.ps1'
$ApplyScript  = Join-Path $ScriptRoot 'Apply-StackMultiplier.ps1'

foreach ($p in @($BuildScript, $ApplyScript, $VanillaSource)) {
    if (-not (Test-Path -LiteralPath $p)) {
        throw "Path not found: $p"
    }
}
if ($FromPak -and -not (Test-Path -LiteralPath $FromPak)) {
    throw "Path not found: $FromPak"
}

if (-not (Test-Path -LiteralPath $OutDir)) {
    if (-not $DryRun) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
}
if (-not (Test-Path -LiteralPath $SrcRoot)) {
    if (-not $DryRun) { New-Item -ItemType Directory -Path $SrcRoot -Force | Out-Null }
}

# Derive variant spec: multiplier or absolute?
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
    throw "Unknown variant: $name (allowed: 'xN' or 'N')"
}

Write-Step "Build All Stack Variations"
Write-OK "Variants     : $($Variants -join ', ')"
Write-OK "FromPak      : $(if ($FromPak) { $FromPak } else { '<none -- reusing existing src folders>' })"
Write-OK "VanillaSource: $VanillaSource"
Write-OK "SrcRoot      : $SrcRoot"
Write-OK "OutDir       : $OutDir"
if ($Force)        { Write-OK "Force        : yes (existing builds will be overwritten)" }
if ($CleanSources) { Write-OK "CleanSources : yes (src folders deleted after build)" }
if ($DryRun)       { Write-Warn2 "DryRun active -> nothing happens" }
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

    # Skip when pak already exists and -Force was not given
    if ((Test-Path -LiteralPath $pakPath) -and -not $Force) {
        Write-Warn2 "Pak already exists, skipping (use -Force to overwrite): $pakPath"
        $results += [pscustomobject]@{ Name = $modName; Status = 'skipped'; Path = $pakPath }
        continue
    }

    try {
        # 1. Init (only when -FromPak was supplied; otherwise reuse existing src)
        if ($FromPak) {
            Write-Step "Init from FromPak -> $srcPath"
            $initArgs = @{
                Action = 'Init'
                Source = $srcPath
                FromPak = $FromPak
            }
            if ($Force) { $initArgs.Force = $true }
            if ($DryRun) { $initArgs.DryRun = $true }
            & $BuildScript @initArgs
            if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "Init failed (exit $LASTEXITCODE)" }
        }
        elseif (-not (Test-Path -LiteralPath $srcPath)) {
            throw "Source folder missing and no -FromPak supplied: $srcPath"
        }
        else {
            Write-OK "Reusing existing src (no -FromPak): $srcPath"
        }

        if ($DryRun) {
            # In DryRun nothing was unpacked after Init -> Apply/Build would
            # fail on an empty/non-existent path. End the simulation here.
            Write-Warn2 "DryRun: skipping Apply/Pack (no src present)"
            $results += [pscustomobject]@{ Name = $modName; Status = 'ok (dryrun)'; Path = $pakPath }
            continue
        }

        # 2. Apply
        Write-Step "Apply $($v.Mode) on $srcPath"
        $applyArgs = @{
            Source        = $srcPath
            VanillaSource = $VanillaSource
        }
        if ($v.Mode -eq 'Multiplier') {
            $applyArgs.Multiplier = $v.Multiplier
            $applyArgs.Cap        = 0   # no cap - user explicitly wants high values
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
            Write-Step "CleanSources: deleting $srcPath"
            Remove-Item -LiteralPath $srcPath -Recurse -Force
        }

        $results += [pscustomobject]@{ Name = $modName; Status = 'ok'; Path = $pakPath }
        Write-OK "$modName done"
    }
    catch {
        Write-Err2 "ERROR on $($modName): $_"
        $results += [pscustomobject]@{ Name = $modName; Status = "error: $_"; Path = $pakPath }
    }
}

Write-Host ""
Write-Step "Summary"
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
    Write-Warn2 "DryRun active -> nothing was actually written"
}
