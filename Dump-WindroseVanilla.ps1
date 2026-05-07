<#
.SYNOPSIS
    Extracts the vanilla InventoryItems JSONs straight out of the game's
    encrypted pak file.

.DESCRIPTION
    The Windrose main pak (`pakchunk0-WindowsServer.pak` on a dedicated
    server, `pakchunk0-Windows.pak` on the game client) is AES-encrypted
    with the public Windrose game key. With that key, repak can list and
    unpack the contents like any other UE pak.

    Inside the pak the InventoryItem DataAssets are stored as plain JSON
    files at:

        R5\Plugins\R5BusinessRules\Content\InventoryItems\<Category>\*.json

    This script uses `repak unpack -i ...` with the include filter set to
    that path so we get exactly the InventoryItems and nothing else
    (the full pak has ~13800 entries, we only want ~1097). The result is
    a clean Sources\Vanilla\ tree that is the single source of truth for
    Build-AllStackVariations.ps1.

    The AES key is hardcoded -- it is the public game key, not a secret;
    every Windrose-related modding tool uses the same value.

.PARAMETER VanillaPak
    Path to the game pak (`pakchunk0-WindowsServer.pak` for the dedicated
    server, or `pakchunk0-Windows.pak` from the game client).
    Default: $cfg.Game.VanillaPak (config.psd1).

.PARAMETER OutDir
    Target directory for the extracted tree. Default: $cfg.Paths.Vanilla
    (normally Sources\Vanilla).

.PARAMETER RepakExe
    Path to repak.exe. Default: $cfg.Tools.RepakExe.

.PARAMETER Clean
    Empties OutDir before extracting. Useful to ensure no stale files
    from an older pak remain.

.PARAMETER Force
    Pass --force to repak so existing files in OutDir are overwritten
    without prompting.

.PARAMETER DryRun
    Print the planned repak invocation without executing it.

.EXAMPLE
    .\Dump-WindroseVanilla.ps1
    # Default paths from config.psd1: extract into Sources\Vanilla\.

.EXAMPLE
    .\Dump-WindroseVanilla.ps1 -Clean -Force
    # Wipe Sources\Vanilla\ first, then unpack everything fresh.

.EXAMPLE
    .\Dump-WindroseVanilla.ps1 -VanillaPak 'C:\Games\Windrose\R5\Content\Paks\pakchunk0-Windows.pak'
    # Use the client pak instead of the server pak.
#>

[CmdletBinding()]
param(
    [string]$VanillaPak,

    [string]$OutDir,

    [string]$RepakExe,

    [switch]$Clean,

    [switch]$Force,

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

# --- Load config ----------------------------------------------------------
$cfg = & (Join-Path $PSScriptRoot '_config.ps1')

if (-not $VanillaPak) { $VanillaPak = [string]$cfg.Game.VanillaPak }
if (-not $OutDir)     { $OutDir     = [string]$cfg.Paths.Vanilla }
if (-not $RepakExe)   { $RepakExe   = [string]$cfg.Tools.RepakExe }

# Public game encryption key (NOT a secret -- used by every Windrose
# modding tool, e.g. WindrosePlus' IniConfigParser.ps1).
$WindroseAesKey = '0x5F430BF9FEF2B0B91B7C79C313BDAF291BA076A1DAB5045974186333AA16CFAE'

# Pak-internal prefix we want to extract. Everything else stays in the pak.
$IncludePath = 'R5/Plugins/R5BusinessRules/Content/InventoryItems'

function Write-Step($msg)  { Write-Host "==> $msg"      -ForegroundColor Cyan }
function Write-OK($msg)    { Write-Host "    [OK] $msg" -ForegroundColor Green }
function Write-Warn2($msg) { Write-Host "    [!]  $msg" -ForegroundColor Yellow }
function Write-Err2($msg)  { Write-Host "    [X]  $msg" -ForegroundColor Red }

# --- 1) Validation --------------------------------------------------------
Write-Step 'Checking prerequisites'

if (-not $VanillaPak -or $VanillaPak.Trim() -eq '') {
    throw "VanillaPak is not set. Configure Game.VanillaPak in config.psd1 or pass -VanillaPak."
}
if (-not (Test-Path -LiteralPath $VanillaPak -PathType Leaf)) {
    throw "VanillaPak not found: $VanillaPak"
}
$VanillaPak = (Resolve-Path -LiteralPath $VanillaPak).Path
Write-OK "VanillaPak: $VanillaPak"

if (-not (Test-Path -LiteralPath $RepakExe -PathType Leaf)) {
    throw "repak.exe not found: $RepakExe"
}
$RepakExe = (Resolve-Path -LiteralPath $RepakExe).Path
Write-OK "RepakExe:   $RepakExe"

if (-not $OutDir -or $OutDir.Trim() -eq '') {
    $OutDir = Join-Path $PSScriptRoot 'Sources\Vanilla'
}
if (-not (Test-Path -LiteralPath $OutDir)) {
    if (-not $DryRun) {
        New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
    }
}
$resolved = Resolve-Path -LiteralPath $OutDir -ErrorAction SilentlyContinue
if ($resolved) { $OutDir = $resolved.Path }
else           { $OutDir = [System.IO.Path]::GetFullPath($OutDir) }
Write-OK "OutDir:     $OutDir"

# --- 2) Optional clean ----------------------------------------------------
if ($Clean) {
    Write-Step 'Clean: emptying OutDir'
    if ($DryRun) {
        Write-Warn2 'DryRun -> OutDir left unchanged'
    } else {
        Get-ChildItem -LiteralPath $OutDir -Force -ErrorAction SilentlyContinue |
            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
        Write-OK 'OutDir emptied'
    }
}

# --- 3) Run repak unpack --------------------------------------------------
Write-Step 'Unpacking InventoryItems from pak'

$unpackArgs = @(
    '--aes-key', $WindroseAesKey,
    'unpack',
    '-i', $IncludePath,
    '-o', $OutDir
)
if ($Force) { $unpackArgs += '-f' }
$unpackArgs += $VanillaPak

# Don't echo the AES key (technically public, but no need to spam it)
$display = @(
    '--aes-key', '<hidden>',
    'unpack',
    '-i', $IncludePath,
    '-o', $OutDir
)
if ($Force) { $display += '-f' }
$display += $VanillaPak

Write-Host "    repak $($display -join ' ')" -ForegroundColor DarkGray

if ($DryRun) {
    Write-Warn2 'DryRun -> repak not invoked'
    Write-Host ''
    Write-Step 'Done (DryRun)'
    return
}

& $RepakExe @unpackArgs
if ($LASTEXITCODE -ne 0) {
    throw "repak unpack failed (exit $LASTEXITCODE)"
}

# --- 4) Stats -------------------------------------------------------------
Write-Step 'Statistics'

$invRoot = Join-Path $OutDir 'R5\Plugins\R5BusinessRules\Content\InventoryItems'
if (-not (Test-Path -LiteralPath $invRoot)) {
    Write-Warn2 "Expected directory not produced: $invRoot"
} else {
    $allJson = Get-ChildItem -LiteralPath $invRoot -Recurse -File -Filter '*.json'
    Write-OK ("{0} JSON files extracted" -f $allJson.Count)

    # Top-level category counts (and 'other' for files directly under InventoryItems\)
    $byCategory = [ordered]@{}
    foreach ($f in $allJson) {
        $rel = $f.FullName.Substring($invRoot.Length).TrimStart('\','/')
        $segs = $rel -split '[\\/]+'
        $cat  = if ($segs.Count -ge 2) { $segs[0] } else { '(other)' }
        if (-not $byCategory.Contains($cat)) { $byCategory[$cat] = 0 }
        $byCategory[$cat]++
    }
    foreach ($k in $byCategory.Keys) {
        Write-Host ("    {0,-15} {1,6}" -f $k, $byCategory[$k]) -ForegroundColor DarkGray
    }
}

Write-Host ''
Write-Step 'Done'
Write-OK "Vanilla source ready: $OutDir"
Write-Host ''
Write-Host '    Next step: build the variants with' -ForegroundColor DarkGray
Write-Host '        .\Build-AllStackVariations.ps1' -ForegroundColor DarkGray
