<#
.SYNOPSIS
    Extracts inventory item icons (PNG) from the encrypted Windrose game pak.

.DESCRIPTION
    Thin wrapper around Invoke-WindroseIconExtract (Library\Icons.ps1).

    For every JSON under -Source the script reads
    `InventoryItemUIData.ItemTexture`, hands the soft-object reference to a
    C# helper (IconExtractor.exe) that uses CUE4Parse to mount the AES-
    encrypted UE5 IoStore containers (.utoc/.ucas), decode the UTexture2D
    and write a PNG.

    First-run prerequisites the script handles automatically:
      * IconExtractor.exe              -- built via `dotnet publish` if missing
      * repak.exe                      -- not used here, see Dump script
      * Oodle / Detex native DLLs      -- fetched/extracted by IconExtractor itself

    What you have to do once, manually:
      * Generate a UE5 mappings file. In the running game press **Ctrl+Num6**
        (UE4SS Keybinds mod -> DumpUSMAP). Drop the produced
        `R5-<version>-<hash>.usmap` into the modding root.
      * Have the vanilla JSONs available -- run Dump-WindroseVanilla.ps1
        first.

.PARAMETER Source
    Folder containing the per-item JSONs. Default: .\Sources\Vanilla.

.PARAMETER OutDir
    Where the PNGs land (one per item). Default: .\Icons.

.PARAMETER PaksDir
    Folder with the game's pakchunk0*.pak/.ucas/.utoc. Default: auto-
    detected from the Steam install.

.PARAMETER Usmap
    Path to the UE5 .usmap file. Default: auto-detected (newest *.usmap
    in the modding root).

.PARAMETER ExtractorExe
    IconExtractor.exe override. Default: auto-built into
    Tools\IconExtractor\publish\IconExtractor.exe on first use.

.PARAMETER GameVersion
    CUE4Parse EGame value. Default: UE5_6 (the version Windrose ships).

.PARAMETER DryRun
    Print the planned invocation without running it.

.EXAMPLE
    .\Extract-Icons.ps1
    # Auto-detects everything; PNGs end up under .\Icons\.

.EXAMPLE
    .\Extract-Icons.ps1 -OutDir .\Builds\Icons -DryRun
    # Show what would happen without invoking IconExtractor.
#>

[CmdletBinding()]
param(
    [string]$Source,

    [string]$OutDir,

    [string]$PaksDir,

    [string]$Usmap,

    [string]$ExtractorExe,

    [string]$GameVersion = 'UE5_6',

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Library\Common.ps1')
. (Join-Path $PSScriptRoot 'Library\Icons.ps1')

# Library defaults (Sources\Vanilla\, .\Icons, Steam-detected paks, newest
# *.usmap in repo root, auto-built IconExtractor.exe) kick in when the
# corresponding parameter is left empty. Only forward what the caller set.
$iconArgs = @{
    GameVersion = $GameVersion
    DryRun      = $DryRun
}
if ($Source       -and $Source.Trim()       -ne '') { $iconArgs.Source       = $Source       }
if ($OutDir       -and $OutDir.Trim()       -ne '') { $iconArgs.OutDir       = $OutDir       }
if ($PaksDir      -and $PaksDir.Trim()      -ne '') { $iconArgs.PaksDir      = $PaksDir      }
if ($Usmap        -and $Usmap.Trim()        -ne '') { $iconArgs.Usmap        = $Usmap        }
if ($ExtractorExe -and $ExtractorExe.Trim() -ne '') { $iconArgs.ExtractorExe = $ExtractorExe }

Invoke-WindroseIconExtract @iconArgs
