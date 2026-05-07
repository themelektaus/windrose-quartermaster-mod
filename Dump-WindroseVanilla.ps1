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
    Default: $cfg.Game.VanillaPak (config.psd1).

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

. (Join-Path $PSScriptRoot 'lib\Common.ps1')
. (Join-Path $PSScriptRoot 'lib\Dump.ps1')

$cfg = Get-WindroseConfig -ModRoot $PSScriptRoot

$vp = Use-Default $VanillaPak ([string]$cfg.Game.VanillaPak)
$od = Use-Default $OutDir     ([string]$cfg.Paths.Vanilla)

if (-not $vp) {
    throw "VanillaPak is not set. Configure Game.VanillaPak in config.psd1 or pass -VanillaPak."
}

# RepakExe is left empty when not provided -- the library auto-resolves it
# via Get-RepakExe (lazy download to lib\bin\ on first use).
$dumpArgs = @{
    VanillaPak = $vp
    OutDir     = $od
    Clean      = $Clean
    Force      = $Force
    DryRun     = $DryRun
}
if ($RepakExe -and $RepakExe.Trim() -ne '') { $dumpArgs.RepakExe = $RepakExe }

Invoke-WindroseVanillaDump @dumpArgs
