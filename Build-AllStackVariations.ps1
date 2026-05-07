<#
.SYNOPSIS
    Builds all stack-size variants (multipliers x2..x10 + absolute
    999..9999) into the Builds folder.

.DESCRIPTION
    For each variant:
        1. Copy Sources\Vanilla\ -> Sources\StackSize_<v>\ (skip if already
           there; use -Force to overwrite)
        2. Apply stack multiplier (or absolute value)
        3. Pack -> .pak in <OutDir>

    The finished .pak files end up in <OutDir> and must be copied into the
    server/client ~mods folder yourself.

    Output: <OutDir>\StackSize_<name>_P.pak

    Source folders are kept (by default) so you can inspect/diff them. Use
    -CleanSources to delete them after a successful build.

    Run Dump-WindroseVanilla.ps1 once before this script to populate
    Sources\Vanilla\.

.PARAMETER Variants
    List of variants to build. Default: all 11.
    Allowed names: x2, x3, x4, x5, x6, x7, x8, x9, x10, 999, 9999

.PARAMETER VanillaSource
    Path to the vanilla snapshot. Default: .\Sources\Vanilla.

.PARAMETER SrcRoot
    Where the per-variant source folders are placed. Default: .\Sources.

.PARAMETER OutDir
    Where the finished .pak files end up. Default: .\Builds.

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
    .\Build-AllStackVariations.ps1 -Variants x4,x10,9999 -Force

.EXAMPLE
    # Build all, then clean up src folders
    .\Build-AllStackVariations.ps1 -CleanSources -Force
#>

[CmdletBinding()]
param(
    [string[]]$Variants = @('x2','x3','x4','x5','x6','x7','x8','x9','x10','999','9999'),

    [string]$VanillaSource,

    [string]$SrcRoot,

    [string]$OutDir,

    [switch]$Force,

    [switch]$CleanSources,

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Library\Common.ps1')
. (Join-Path $PSScriptRoot 'Library\Apply.ps1')
. (Join-Path $PSScriptRoot 'Library\Pack.ps1')

$paths = Get-WindrosePaths -ModRoot $PSScriptRoot

$VanillaSource = Use-Default $VanillaSource $paths.Vanilla
$SrcRoot       = Use-Default $SrcRoot       $paths.Sources
$OutDir        = Use-Default $OutDir        $paths.Builds

if (-not (Test-Path -LiteralPath $VanillaSource -PathType Container)) {
    throw @"
VanillaSource not found: $VanillaSource

Run Dump-WindroseVanilla.ps1 first to extract the vanilla JSONs from
the game pak.
"@
}
$VanillaSource = (Resolve-Path -LiteralPath $VanillaSource).Path

Initialize-Directory -Path $OutDir  -DryRun:$DryRun
Initialize-Directory -Path $SrcRoot -DryRun:$DryRun

# --- Variant spec parser -------------------------------------------------
function Resolve-Variant {
    param([string]$name)
    if ($name -match '^x(\d+)$') {
        return [pscustomobject]@{
            Name = $name; Mode = 'Multiplier'; Multiplier = [int]$matches[1]; Absolute = 0
        }
    }
    if ($name -match '^(\d+)$') {
        return [pscustomobject]@{
            Name = $name; Mode = 'Absolute'; Multiplier = 0; Absolute = [int]$matches[1]
        }
    }
    throw "Unknown variant: $name (allowed: 'xN' or 'N')"
}

# --- Header --------------------------------------------------------------
Write-Step "Build All Stack Variations"
Write-OK "Variants     : $($Variants -join ', ')"
Write-OK "VanillaSource: $VanillaSource"
Write-OK "SrcRoot      : $SrcRoot"
Write-OK "OutDir       : $OutDir"
if ($Force)        { Write-OK "Force        : yes (existing builds will be overwritten)" }
if ($CleanSources) { Write-OK "CleanSources : yes (src folders deleted after build)" }
if ($DryRun)       { Write-Warn2 "DryRun active -> nothing happens" }
Write-Host ""

# --- Per-variant loop ----------------------------------------------------
$results = @()
$idx = 0
$total = $Variants.Count

foreach ($vname in $Variants) {
    $idx++
    $v = Resolve-Variant $vname
    $modName = "StackSize_$($v.Name)"
    $srcPath = Join-Path $SrcRoot $modName
    $pakName = "${modName}_P.pak"
    $pakPath = Join-Path $OutDir $pakName

    Write-Host "================================================================" -ForegroundColor DarkCyan
    Write-Step "[$idx/$total] $modName  ($($v.Mode))"

    if ((Test-Path -LiteralPath $pakPath) -and -not $Force) {
        Write-Warn2 "Pak already exists, skipping (use -Force to overwrite): $pakPath"
        $results += [pscustomobject]@{ Name = $modName; Status = 'skipped'; Path = $pakPath }
        continue
    }

    try {
        # 1. Copy vanilla -> variant src folder
        $srcExists = Test-Path -LiteralPath $srcPath
        if ($srcExists -and $Force) {
            if ($DryRun) {
                Write-OK "DryRun: would remove existing src: $srcPath"
            } else {
                Remove-Item -LiteralPath $srcPath -Recurse -Force
                Write-OK "Removed existing src (Force): $srcPath"
            }
            $srcExists = $DryRun  # in DryRun pretend it's still there
        }
        if (-not $srcExists) {
            Write-Step "Copy Vanilla -> $srcPath"
            if (-not $DryRun) {
                Copy-Item -LiteralPath $VanillaSource -Destination $srcPath -Recurse -Force
            }
        } elseif (-not $Force) {
            Write-OK "Reusing existing src: $srcPath"
        }

        if ($DryRun) {
            Write-Warn2 "DryRun: skipping Apply/Pack"
            $results += [pscustomobject]@{ Name = $modName; Status = 'ok (dryrun)'; Path = $pakPath }
            continue
        }

        # 2. Apply -- direct library call, no sub-process
        Write-Step "Apply $($v.Mode) on $srcPath"
        if ($v.Mode -eq 'Multiplier') {
            Invoke-StackMultiplierApply -Source $srcPath -Multiplier $v.Multiplier -Cap 0 | Out-Null
        } else {
            Invoke-StackMultiplierApply -Source $srcPath -AbsoluteValue $v.Absolute | Out-Null
        }

        # 3. Pack -- direct library call (auto-downloads repak.exe on first use)
        Write-Step "Pack $srcPath -> $pakPath"
        Invoke-WindroseModPack `
            -Source $srcPath `
            -Name   $modName `
            -OutDir $OutDir `
            -Force:$Force | Out-Null

        # 4. Optional cleanup
        if ($CleanSources) {
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

# --- Summary -------------------------------------------------------------
Write-Host ""
Write-Step "Summary"
$results | ForEach-Object {
    $color = switch -Wildcard ($_.Status) {
        'ok'      { 'Green' }
        'skipped' { 'Yellow' }
        'error*'  { 'Red' }
        default   { 'Gray' }
    }
    Write-Host ("    {0,-22} {1}" -f $_.Name, $_.Status) -ForegroundColor $color
}

# @(...) wraps to enforce an array even when only one result matches
# (PS 5.1: a single-object pipeline doesn't have a usable .Count).
$okCount   = @($results | Where-Object Status -eq 'ok').Count
$skipCount = @($results | Where-Object Status -eq 'skipped').Count
$errCount  = @($results | Where-Object Status -like 'error*').Count
Write-Host ""
Write-OK "OK: $okCount  Skipped: $skipCount  Errors: $errCount"
if ($DryRun) {
    Write-Warn2 "DryRun active -> nothing was actually written"
}
