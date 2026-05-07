<#
.SYNOPSIS
    Multiplies MaxCountInSlot in every JSON of a mod source folder and
    deletes files for items that are inherently non-stackable (equipment
    slots, NPCs, ship parts) or have no MaxCountInSlot field.

.DESCRIPTION
    Thin wrapper around Invoke-StackMultiplierApply (Library\Apply.ps1).

    Per JSON:
      - reads "MaxCountInSlot": <n>
      - n >  1                -> n * Multiplier (capped at -Cap)
      - n == 1 AND
          ItemClass == "Consumable", or
          ItemClass == "Default" AND Category == "Resource"
                              -> n * Multiplier (promoted from vanilla 1)
      - n == 1 otherwise      -> delete file (Armor / Weapon / Jewelry /
                                 Backpack / Tool / NPC / Ship / quest items)
      - field missing         -> delete file (doesn't fit the schema)

    The script expects -Source to already contain vanilla values (e.g. a
    fresh copy of Sources\Vanilla\ produced by Dump-WindroseVanilla.ps1).
    There is no separate vanilla baseline -- the source IS the baseline.

.PARAMETER Source
    Required. Path to the mod source folder containing the JSONs.

.PARAMETER Multiplier
    Factor for MaxCountInSlot. Default: 4.

.PARAMETER Cap
    Maximum value. Default: 0 (no cap).

.PARAMETER KeepUnchanged
    Switch. If set, non-stackable items (equipment, NPCs, etc.) are kept
    unchanged on disk instead of being deleted.

.PARAMETER ExcludePath
    List of path substrings (wildcards allowed). Files whose relative
    path matches any of these patterns are ignored entirely (deleted or
    skipped, depending on -KeepUnchanged).
    Default: '*\Tests\*'

.PARAMETER AbsoluteValue
    Optional fixed value. When set (>0), MaxCountInSlot is set to this
    value for every stackable item (including the promoted stack=1
    Consumables/Resources) instead of being multiplied. -Multiplier is
    then ignored.

.PARAMETER DryRun
    Only show what would happen.

.EXAMPLE
    .\Apply-StackMultiplier.ps1 -Source .\Sources\StackSize_x4 -Multiplier 4

.EXAMPLE
    .\Apply-StackMultiplier.ps1 -Source .\Sources\StackSize_999 -AbsoluteValue 999
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Source,

    [int]$Multiplier = 4,

    [int]$Cap = 0,

    [switch]$KeepUnchanged,

    [string[]]$ExcludePath = @('*\Tests\*'),

    [int]$AbsoluteValue = 0,

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Library\Common.ps1')
. (Join-Path $PSScriptRoot 'Library\Apply.ps1')

[void](Invoke-StackMultiplierApply @PSBoundParameters)
