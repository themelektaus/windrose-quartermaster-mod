<#
.SYNOPSIS
    Multiplies MaxCountInSlot in every JSON of a mod source folder and
    deletes files that are not stackable (Stack <= 1) or have no
    MaxCountInSlot field.

.DESCRIPTION
    Walker for Windrose stack mods.

    Per JSON:
      - reads "MaxCountInSlot": <n>
      - n <= 1                -> delete file (stays vanilla)
      - n >  1 (or == 0)      -> n * Multiplier (capped at -Cap)
                                 and rewrite the JSON (tabs/order preserved)

    Writes a compact summary at the end.

.PARAMETER Source
    Required. Path to the mod source folder containing the JSONs.

.PARAMETER Multiplier
    Factor for MaxCountInSlot. Default: 4.

.PARAMETER Cap
    Maximum value. Default: 39996 (the Stack-mod x4 uses this as a cap).
    Set 0 for "no cap".

.PARAMETER KeepUnchanged
    Switch. If set, files with Stack <= 1 are kept unchanged instead of
    being deleted. Default: files are deleted.

.PARAMETER ExcludePath
    List of path substrings (wildcards allowed). Files whose relative
    path matches any of these patterns are ignored entirely (deleted or
    skipped, depending on -KeepUnchanged).
    Default: '*\Tests\*' (drop dev/test items)

.PARAMETER Minimal
    Switch. Replaces the entire JSON with a minimal schema containing only
    "$type", "InventoryItemGppData.MaxCountInSlot" and "NativeClass".
    Avoids loader errors caused by problematic vanilla-dump fields (number
    enums, empty default structs in arrays, etc.) and relies on property
    overrides in the R5BL loader.

.PARAMETER VanillaSource
    Optional path to a second source tree containing the vanilla JSONs
    (e.g. .\Sources\Vanilla). When set, the multiplier is applied to the
    vanilla value instead of the source value. Useful when -Source already
    contains modified values (e.g. from -FromPak of someone else's stack
    mod) and you want true vanilla*multiplier values.

    Lookup: same relative path inside -VanillaSource. Missing vanilla
    counterparts are logged and skipped.

.PARAMETER AbsoluteValue
    Optional fixed value. When set (>0), MaxCountInSlot is set to this
    value for every stackable item (vanilla stack > 1) instead of being
    multiplied. -Multiplier is then ignored. -Cap is also no longer
    applied because the value is given directly.

    Use case: flat "all stacks = 999" mods.

.PARAMETER DryRun
    Only show what would happen, do not write/delete anything.

.EXAMPLE
    .\Apply-StackMultiplier.ps1 -Source .\Sources\StackSize_x4 -Multiplier 4

.EXAMPLE
    .\Apply-StackMultiplier.ps1 -Source .\Sources\Test -Multiplier 10 -DryRun

.EXAMPLE
    .\Apply-StackMultiplier.ps1 -Source .\Sources\StackSize_999 -VanillaSource .\Sources\Vanilla -AbsoluteValue 999
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Source,

    [int]$Multiplier = 4,

    [int]$Cap = 39996,

    [switch]$KeepUnchanged,

    [string[]]$ExcludePath = @('*\Tests\*'),

    [switch]$Minimal,

    [string]$VanillaSource,

    [int]$AbsoluteValue = 0,

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

function Write-Step($msg)  { Write-Host "==> $msg"          -ForegroundColor Cyan }
function Write-OK($msg)    { Write-Host "    [OK] $msg"     -ForegroundColor Green }
function Write-Warn2($msg) { Write-Host "    [!]  $msg"     -ForegroundColor Yellow }

if ($Multiplier -lt 1) {
    throw "Multiplier must be >= 1 (was: $Multiplier)"
}
if ($AbsoluteValue -lt 0) {
    throw "AbsoluteValue must not be negative (was: $AbsoluteValue)"
}
$IsAbsolute = ($AbsoluteValue -gt 0)

$SourceFull = (Resolve-Path -LiteralPath $Source).Path
if (-not (Test-Path -LiteralPath $SourceFull -PathType Container)) {
    throw "Source is not a folder: $SourceFull"
}

$VanillaSourceFull = $null
if ($VanillaSource) {
    $VanillaSourceFull = (Resolve-Path -LiteralPath $VanillaSource).Path
    if (-not (Test-Path -LiteralPath $VanillaSourceFull -PathType Container)) {
        throw "VanillaSource is not a folder: $VanillaSourceFull"
    }
}

Write-Step "Apply Stack Multiplier"
Write-OK "Source     : $SourceFull"
if ($VanillaSourceFull) {
    Write-OK "Vanilla    : $VanillaSourceFull"
    Write-OK "Mode       : Vanilla-merge (multiplier applied to vanilla values)"
}
if ($IsAbsolute) {
    Write-OK "AbsoluteVal: $AbsoluteValue (Multiplier/Cap ignored)"
} else {
    Write-OK "Multiplier : x$Multiplier"
    if ($Cap -gt 0) { Write-OK "Cap        : $Cap" } else { Write-OK "Cap        : (none)" }
}
if ($KeepUnchanged) { Write-OK "Cleanup    : keep unchanged files" } else { Write-OK "Cleanup    : delete unchanged files (Stack<=1)" }
if ($Minimal)       { Write-OK "Schema     : minimal (Type+MaxCountInSlot+NativeClass only)" } else { Write-OK "Schema     : full (preserve all original fields)" }
if ($DryRun) { Write-Warn2 "DryRun active -> nothing will be written/deleted" }

$files = Get-ChildItem -LiteralPath $SourceFull -Recurse -File -Filter '*.json'
Write-OK "Found      : $($files.Count) JSON file(s)"
if ($ExcludePath -and $ExcludePath.Count -gt 0) {
    Write-OK ("ExcludePath: {0}" -f ($ExcludePath -join ', '))
}

$modified  = 0
$deleted   = 0
$kept      = 0
$skipped   = 0
$capped    = 0
$excluded  = 0
$noVanilla = 0

foreach ($f in $files) {
    # ExcludePath check (against relative path)
    $rel = $f.FullName.Substring($SourceFull.Length).TrimStart('\')
    $isExcluded = $false
    foreach ($pat in $ExcludePath) {
        if ($rel -like $pat) { $isExcluded = $true; break }
    }
    if ($isExcluded) {
        if ($KeepUnchanged) {
            $kept++
        } else {
            if (-not $DryRun) {
                Remove-Item -LiteralPath $f.FullName -Force
            }
            $excluded++
        }
        continue
    }

    # Read explicitly as UTF-8, otherwise Windows PowerShell 5.1's default
    # ANSI codepage will mangle non-ASCII characters (e.g. Cyrillic item
    # names) and re-write them as mojibake.
    $content = [System.IO.File]::ReadAllText($f.FullName, [System.Text.Encoding]::UTF8)

    if ($content -match '("MaxCountInSlot"\s*:\s*)(\d+)') {
        $sourceVal = [int]$matches[2]

        # Vanilla merge: base value comes from VanillaSource, not from Source
        if ($VanillaSourceFull) {
            $vanillaPath = Join-Path $VanillaSourceFull $rel
            if (-not (Test-Path -LiteralPath $vanillaPath)) {
                # No vanilla counterpart -> file is stack-mod-specific
                # (e.g. items the stack-mod author deliberately made stackable).
                # Delete it because we have no real vanilla baseline.
                if (-not $DryRun) {
                    Remove-Item -LiteralPath $f.FullName -Force
                }
                $noVanilla++
                continue
            }
            $vanillaContent = [System.IO.File]::ReadAllText($vanillaPath, [System.Text.Encoding]::UTF8)
            if ($vanillaContent -match '"MaxCountInSlot"\s*:\s*(\d+)') {
                $oldVal = [int]$matches[1]
            } else {
                # Vanilla has no MaxCountInSlot -> not modifiable
                if (-not $DryRun) {
                    Remove-Item -LiteralPath $f.FullName -Force
                }
                $noVanilla++
                continue
            }
        } else {
            $oldVal = $sourceVal
        }

        if ($oldVal -le 1) {
            # Not stackable -> delete or keep
            if ($KeepUnchanged) {
                $kept++
            } else {
                if (-not $DryRun) {
                    Remove-Item -LiteralPath $f.FullName -Force
                }
                $deleted++
            }
            continue
        }

        if ($IsAbsolute) {
            $newVal = $AbsoluteValue
        } else {
            $newVal = $oldVal * $Multiplier
            if ($Cap -gt 0 -and $newVal -gt $Cap) {
                $newVal = $Cap
                $capped++
            }
        }

        if ($newVal -eq $sourceVal -and -not $Minimal) {
            # Source already has the correct value (e.g. when FromPak was already x4)
            $kept++
            continue
        }

        if ($Minimal) {
            # Carry over NativeClass from the original (default if not found)
            $nativeClass = "/Script/CoreUObject.Class'/Script/R5BusinessRules.R5BLInventoryItem'"
            if ($content -match '"NativeClass"\s*:\s*"([^"]+)"') {
                $nativeClass = $matches[1]
            }
            # Carry over $type from the original (default R5BLInventoryItem)
            $typeName = 'R5BLInventoryItem'
            if ($content -match '"\$type"\s*:\s*"([^"]+)"') {
                $typeName = $matches[1]
            }

            # Minimal JSON with tabs (Stack-mod style), CRLF to match the original
            $newContent = @"
{
`t"`$type": "$typeName",
`t"InventoryItemGppData": {
`t`t"MaxCountInSlot": $newVal
`t},
`t"NativeClass": "$($nativeClass.Replace('\','\\').Replace('"','\"'))"
}
"@
        } else {
            $newContent = [regex]::Replace(
                $content,
                '("MaxCountInSlot"\s*:\s*)\d+',
                { param($m) $m.Groups[1].Value + $newVal.ToString() },
                1   # only replace the first match
            )
        }

        if (-not $DryRun) {
            # UTF-8 without BOM, LF line endings stay as they are
            $utf8 = New-Object System.Text.UTF8Encoding($false)
            [System.IO.File]::WriteAllText($f.FullName, $newContent, $utf8)
        }
        $modified++
    } else {
        # No MaxCountInSlot field -> delete (doesn't fit the schema)
        if ($KeepUnchanged) {
            $kept++
        } else {
            if (-not $DryRun) {
                Remove-Item -LiteralPath $f.FullName -Force
            }
            $skipped++
        }
    }
}

# Clean up empty directories
if (-not $DryRun -and -not $KeepUnchanged) {
    # Loop multiple times because empty parents only become empty after
    # their children are deleted.
    for ($i = 0; $i -lt 10; $i++) {
        $emptyDirs = Get-ChildItem -LiteralPath $SourceFull -Recurse -Directory |
            Where-Object { @(Get-ChildItem -LiteralPath $_.FullName -Force).Count -eq 0 }
        if (-not $emptyDirs) { break }
        $emptyDirs | Remove-Item -Force -Recurse
    }
}

Write-Host ""
Write-Step "Done"
Write-OK ("Modified           : {0}" -f $modified)
if ($capped -gt 0) {
    Write-OK ("  of which capped  : {0} (to {1})" -f $capped, $Cap)
}
if ($KeepUnchanged) {
    Write-OK ("Unchanged          : {0}" -f $kept)
} else {
    Write-OK ("Deleted (Stack=1)  : {0}" -f $deleted)
    Write-OK ("Deleted (no MCS)   : {0}" -f $skipped)
    Write-OK ("Deleted (excl.)    : {0}" -f $excluded)
    if ($VanillaSourceFull) {
        Write-OK ("Deleted (no van.)  : {0}" -f $noVanilla)
    }
    Write-OK ("Kept unchanged     : {0}" -f $kept)
}
if ($DryRun) {
    Write-Warn2 "DryRun active -> nothing was actually written/deleted"
}
