<#
.SYNOPSIS
    Builds a _P.pak for Windrose from a source folder.

.DESCRIPTION
    Thin wrapper around Invoke-WindroseModPack (Library\Pack.ps1).

    Wraps repak.exe. Expects a source folder whose contents will be mounted
    "over" the original files in the game. The pak name always ends in "_P"
    (patch marker) so the game loads it as an override. The finished pak
    lands in -OutDir and is NOT installed automatically -- copy it yourself
    into the respective ~mods folder.

    Typical folder structure beneath -Source:
        R5\Plugins\R5BusinessRules\Content\InventoryItems\Ammo\*.json
        R5\Plugins\R5BusinessRules\Content\InventoryItems\Consumables\...

    Default mount point is `../../../` (matches every Windrose mod
    examined so far).

.PARAMETER Source
    Required. Path to the source folder with the mod files.

.PARAMETER Name
    Base name of the pak (without extension). "_P" is appended automatically
    if not already present. Default: name of the source folder.

.PARAMETER OutDir
    Where the pak is written. Default: .\Builds.

.PARAMETER MountPoint
    Mount point inside the pak. Default: '../../../'.

.PARAMETER Version
    Pak format version. Default: 'V8B'.

.PARAMETER RepakExe
    Path to repak.exe. Default: auto-downloaded to repak.exe on
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

. (Join-Path $PSScriptRoot 'Library\Common.ps1')
. (Join-Path $PSScriptRoot 'Library\Pack.ps1')

# Library defaults (Builds folder, MountPoint '../../../', Version 'V8B',
# repak.exe auto-download) kick in when the corresponding parameter is
# left empty. Only forward the values the caller actually set.
$packArgs = @{
    Source = $Source
    Name   = $Name
    Force  = $Force
    DryRun = $DryRun
}
if ($OutDir     -and $OutDir.Trim()     -ne '') { $packArgs.OutDir     = $OutDir     }
if ($MountPoint -and $MountPoint.Trim() -ne '') { $packArgs.MountPoint = $MountPoint }
if ($Version    -and $Version.Trim()    -ne '') { $packArgs.Version    = $Version    }
if ($RepakExe   -and $RepakExe.Trim()   -ne '') { $packArgs.RepakExe   = $RepakExe   }

[void](Invoke-WindroseModPack @packArgs)
