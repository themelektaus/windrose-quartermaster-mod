<#
.SYNOPSIS
    Extracts the vanilla InventoryItems JSONs straight out of the game's
    encrypted pak file.

.DESCRIPTION
    Thin wrapper around Invoke-WindroseVanillaDump (lib\Dump.ps1).

    The Windrose main pak (`pakchunk0-WindowsServer.pak` on a dedicated
    server, `pakchunk0-Windows.pak` on the game client) is AES-encrypted
    with the public Windrose game key. With that key, repak can list and
    unpack the contents like any other UE pak.

    Inside the pak the InventoryItem DataAssets are stored as plain JSON
    files at:

        R5\Plugins\R5BusinessRules\Content\InventoryItems\<Category>\*.json

    Result: a clean Sources\Vanilla\ tree that is the single source of
    truth for Build-AllStackVariations.ps1.

.PARAMETER VanillaPak
    Path to the game pak (`pakchunk0-WindowsServer.pak` for the dedicated
    server, or `pakchunk0-Windows.pak` from the game client).
    Default: auto-detected by reading the Steam install path from the
    Windows registry and walking every Steam library listed in
    libraryfolders.vdf for a Windrose install. Override only if your pak
    lives somewhere Steam doesn't know about (e.g. a dedicated server).

.PARAMETER OutDir
    Target directory for the extracted tree. Default: $cfg.Paths.Vanilla
    (normally Sources\Vanilla).

.PARAMETER RepakExe
    Path to repak.exe. Default: auto-downloaded to lib\bin\repak.exe on
    first use (pinned v0.2.3, SHA256-verified). Override only if you want
    to use a system-installed repak.

.PARAMETER Clean
    Empties OutDir before extracting.

.PARAMETER Force
    Pass --force to repak so existing files are overwritten without
    prompting.

.PARAMETER DryRun
    Print the planned repak invocation without executing it.

.EXAMPLE
    .\Dump-WindroseVanilla.ps1
    # Auto-detects the Steam install of Windrose; extract into Sources\Vanilla\.

.EXAMPLE
    .\Dump-WindroseVanilla.ps1 -Clean -Force
    # Wipe Sources\Vanilla\ first, then unpack everything fresh.

.EXAMPLE
    .\Dump-WindroseVanilla.ps1 -VanillaPak 'E:\Windrose\Server\Nockalmeer\R5\Content\Paks\pakchunk0-WindowsServer.pak'
    # Point at a dedicated-server pak instead of the auto-detected Steam install.
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

. (Join-Path $PSScriptRoot 'lib\Common.ps1')
. (Join-Path $PSScriptRoot 'lib\Dump.ps1')

$cfg = Get-WindroseConfig -ModRoot $PSScriptRoot

$od = Use-Default $OutDir ([string]$cfg.Paths.Vanilla)

# VanillaPak and RepakExe are left empty when not provided -- the library
# auto-resolves them (Steam install lookup / lib\bin\repak.exe download).
$dumpArgs = @{
    OutDir = $od
    Clean  = $Clean
    Force  = $Force
    DryRun = $DryRun
}
if ($VanillaPak -and $VanillaPak.Trim() -ne '') { $dumpArgs.VanillaPak = $VanillaPak }
if ($RepakExe   -and $RepakExe.Trim()   -ne '') { $dumpArgs.RepakExe   = $RepakExe   }

Invoke-WindroseVanillaDump @dumpArgs
