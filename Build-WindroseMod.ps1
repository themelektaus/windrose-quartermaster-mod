<#
.SYNOPSIS
    Builds a _P.pak for Windrose from a source folder.

.DESCRIPTION
    Thin wrapper around Invoke-WindroseModPack (lib\Pack.ps1).

    Wraps repak.exe. Expects a source folder whose contents will be mounted
    "over" the original files in the game. The pak name always ends in "_P"
    (patch marker) so the game loads it as an override. The finished pak
    lands in -OutDir and is NOT installed automatically -- copy it yourself
    into the respective ~mods folder.

    Typical folder structure beneath -Source:
        R5\Plugins\R5BusinessRules\Content\InventoryItems\Ammo\*.json
        R5\Plugins\R5BusinessRules\Content\InventoryItems\Consumables\...

    Default mount point is `../../../` (matches every Windrose mod
    examined so far). See config.psd1 for details.

.PARAMETER Source
    Required. Path to the source folder with the mod files.

.PARAMETER Name
    Base name of the pak (without extension). "_P" is appended automatically
    if not already present. Default: name of the source folder.

.PARAMETER OutDir
    Where the pak is written. Default: $cfg.Paths.Builds (config.psd1).

.PARAMETER MountPoint
    Mount point inside the pak. Default: $cfg.Pak.MountPoint ('../../../').

.PARAMETER Version
    Pak format version. Default: $cfg.Pak.Version ('V8B').

.PARAMETER RepakExe
    Path to repak.exe. Default: auto-downloaded to lib\bin\repak.exe on
    first use (pinned v0.2.3, SHA256-verified). Override only if you want
    to use a system-installed repak.

.PARAMETER Force
    Overwrite an existing output file without asking.

.PARAMETER DryRun
    Only show what would happen.

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

    [string]$MountPoint,

    [ValidateSet('V0','V1','V2','V3','V4','V5','V6','V7','V8A','V8B','V9','V10','V11','')]
    [string]$Version = '',

    [string]$RepakExe,

    [switch]$Force,

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'lib\Common.ps1')
. (Join-Path $PSScriptRoot 'lib\Pack.ps1')

$cfg = Get-WindroseConfig -ModRoot $PSScriptRoot

# Pull defaults from config when not explicitly set. RepakExe is left empty
# when not provided -- the library auto-resolves it via Get-RepakExe (lazy
# download to lib\bin\ on first use).
$packArgs = @{
    Source     = $Source
    Name       = $Name
    OutDir     = (Use-Default $OutDir     ([string]$cfg.Paths.Builds))
    MountPoint = (Use-Default $MountPoint ([string]$cfg.Pak.MountPoint))
    Version    = (Use-Default $Version    ([string]$cfg.Pak.Version))
    Force      = $Force
    DryRun     = $DryRun
}
if ($RepakExe -and $RepakExe.Trim() -ne '') { $packArgs.RepakExe = $RepakExe }

[void](Invoke-WindroseModPack @packArgs)
