<#
.SYNOPSIS
    Builds a _P.pak for Windrose from a source folder.

.DESCRIPTION
    Wrapper around repak.exe. Expects a source folder whose contents will
    be mounted "over" the original files in the game. The pak name always
    ends in "_P" (patch marker) so the game loads it as an override. The
    finished pak lands in -OutDir and is NOT installed automatically --
    copy it yourself into the respective ~mods folder (server or client).

    Typical folder structure beneath -Source:
        R5\Plugins\R5BusinessRules\Content\InventoryItems\Ammo\*.json
        R5\Plugins\R5BusinessRules\Content\InventoryItems\Consumables\...

    Default mount point is `../../../` (matches every Windrose mod
    examined so far). See config.psd1 for details.

.PARAMETER Source
    Required. Path to the source folder with the mod files.

.PARAMETER Name
    Base name of the pak (without extension). "_P" is appended
    automatically if not already present. Default: name of the source folder.

.PARAMETER OutDir
    Where the pak is written. Default: $cfg.Paths.Builds (config.psd1).

.PARAMETER MountPoint
    Mount point inside the pak. Default: $cfg.Pak.MountPoint ('../../../').

.PARAMETER Version
    Pak format version. Default: $cfg.Pak.Version ('V8B').

.PARAMETER RepakExe
    Path to repak.exe. Default: $cfg.Tools.RepakExe.

.PARAMETER Force
    Overwrites an existing output file without asking.

.PARAMETER DryRun
    Only show what would happen, without packing.

.EXAMPLE
    .\Build-WindroseMod.ps1 -Source .\Sources\MyStackMod -Name MyStackMod

.EXAMPLE
    .\Build-WindroseMod.ps1 -Source .\Sources\MyStackMod -Force
#>

[CmdletBinding()]
param(
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

$allFiles  = Get-ChildItem -LiteralPath $SourceFull -Recurse -File
$fileCount = ($allFiles | Measure-Object).Count
if ($fileCount -eq 0) {
    throw "Source folder is empty: $SourceFull"
}
Write-OK "Source: $SourceFull ($fileCount files)"

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
    & $RepakExe @packArgs
    if ($LASTEXITCODE -ne 0) { throw "repak pack failed (exit $LASTEXITCODE)" }
    if (-not (Test-Path -LiteralPath $OutPak)) {
        throw "Pak was not created: $OutPak"
    }
    $sizeKB = [math]::Round((Get-Item -LiteralPath $OutPak).Length / 1KB, 1)
    Write-OK ("Pak built: {0}  ({1} KB)" -f $OutPak, $sizeKB)
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
