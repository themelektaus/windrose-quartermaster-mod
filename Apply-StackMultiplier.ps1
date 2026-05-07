<#
.SYNOPSIS
    Multiplies MaxCountInSlot in every JSON of a mod source folder and
    deletes files that are not stackable (Stack <= 1) or have no
    MaxCountInSlot field.

.DESCRIPTION
    Thin wrapper around Invoke-StackMultiplierApply (lib\Apply.ps1).

    Per JSON:
      - reads "MaxCountInSlot": <n>
      - n <= 1                -> delete file (stays vanilla)
      - n >  1 (or == 0)      -> n * Multiplier (capped at -Cap)
                                 and rewrite the JSON (tabs/order preserved)

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
    Switch. If set, files with Stack <= 1 are kept unchanged instead of
    being deleted.

.PARAMETER ExcludePath
    List of path substrings (wildcards allowed). Files whose relative
    path matches any of these patterns are ignored entirely (deleted or
    skipped, depending on -KeepUnchanged).
    Default: '*\Tests\*'

.PARAMETER AbsoluteValue
    Optional fixed value. When set (>0), MaxCountInSlot is set to this
    value for every stackable item (vanilla stack > 1) instead of being
    multiplied. -Multiplier is then ignored.

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

. (Join-Path $PSScriptRoot 'lib\Common.ps1')
. (Join-Path $PSScriptRoot 'lib\Apply.ps1')

[void](Invoke-StackMultiplierApply @PSBoundParameters)
