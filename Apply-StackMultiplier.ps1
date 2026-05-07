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

    The script expects -Source to already contain vanilla values (e.g. a
    fresh copy of Sources\Vanilla\ produced by Dump-WindroseVanilla.ps1).
    There is no separate vanilla baseline -- the source IS the baseline.

    Writes a compact summary at the end.

.PARAMETER Source
    Required. Path to the mod source folder containing the JSONs.

.PARAMETER Multiplier
    Factor for MaxCountInSlot. Default: 4.

.PARAMETER Cap
    Maximum value. Default: 0 (no cap). The historical 39996 cap from
    Mod #26 is no longer applied by default.

.PARAMETER KeepUnchanged
    Switch. If set, files with Stack <= 1 are kept unchanged instead of
    being deleted. Default: files are deleted.

.PARAMETER ExcludePath
    List of path substrings (wildcards allowed). Files whose relative
    path matches any of these patterns are ignored entirely (deleted or
    skipped, depending on -KeepUnchanged).
    Default: '*\Tests\*' (drop dev/test items)

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

Write-Step "Apply Stack Multiplier"
Write-OK "Source     : $SourceFull"
if ($IsAbsolute) {
    Write-OK "AbsoluteVal: $AbsoluteValue (Multiplier/Cap ignored)"
} else {
    Write-OK "Multiplier : x$Multiplier"
    if ($Cap -gt 0) { Write-OK "Cap        : $Cap" } else { Write-OK "Cap        : (none)" }
}
if ($KeepUnchanged) { Write-OK "Cleanup    : keep unchanged files" } else { Write-OK "Cleanup    : delete unchanged files (Stack<=1)" }
if ($DryRun) { Write-Warn2 "DryRun active -> nothing will be written/deleted" }

$files = Get-ChildItem -LiteralPath $SourceFull -Recurse -File -Filter '*.json'
Write-OK "Found      : $($files.Count) JSON file(s)"
if ($ExcludePath -and $ExcludePath.Count -gt 0) {
    Write-OK ("ExcludePath: {0}" -f ($ExcludePath -join ', '))
}

$modified = 0
$deleted  = 0
$kept     = 0
$skipped  = 0
$capped   = 0
$excluded = 0

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
    # ANSI codepage will mangle non-ASCII characters and re-write them as
    # mojibake.
    $content = [System.IO.File]::ReadAllText($f.FullName, [System.Text.Encoding]::UTF8)

    if ($content -match '("MaxCountInSlot"\s*:\s*)(\d+)') {
        $oldVal = [int]$matches[2]

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

        if ($newVal -eq $oldVal) {
            # Nothing to change (e.g. Multiplier=1)
            $kept++
            continue
        }

        $newContent = [regex]::Replace(
            $content,
            '("MaxCountInSlot"\s*:\s*)\d+',
            { param($m) $m.Groups[1].Value + $newVal.ToString() },
            1   # only replace the first match
        )

        if (-not $DryRun) {
            # UTF-8 without BOM, line endings preserved
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
    Write-OK ("Kept unchanged     : {0}" -f $kept)
}
if ($DryRun) {
    Write-Warn2 "DryRun active -> nothing was actually written/deleted"
}
