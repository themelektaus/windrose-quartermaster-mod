# Library: applies a stack-size multiplier (or absolute value) to JSONs.
# Public API surface lives in Apply-StackMultiplier.ps1 -- see there for
# user-facing docs. Returns a [pscustomobject] with the run summary.

if ($script:__WindroseApplyLoaded) { return }
$script:__WindroseApplyLoaded = $true

function Invoke-StackMultiplierApply {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Source,
        [int]$Multiplier = 4,
        [int]$Cap = 0,
        [switch]$KeepUnchanged,
        [string[]]$ExcludePath = @('*\Tests\*'),
        [int]$AbsoluteValue = 0,
        [switch]$DryRun
    )

    if ($Multiplier -lt 1) {
        throw "Multiplier must be >= 1 (was: $Multiplier)"
    }
    if ($AbsoluteValue -lt 0) {
        throw "AbsoluteValue must not be negative (was: $AbsoluteValue)"
    }
    $isAbsolute = ($AbsoluteValue -gt 0)

    $sourceFull = (Resolve-Path -LiteralPath $Source).Path
    if (-not (Test-Path -LiteralPath $sourceFull -PathType Container)) {
        throw "Source is not a folder: $sourceFull"
    }

    Write-Step "Apply Stack Multiplier"
    Write-OK "Source     : $sourceFull"
    if ($isAbsolute) {
        Write-OK "AbsoluteVal: $AbsoluteValue (Multiplier/Cap ignored)"
    } else {
        Write-OK "Multiplier : x$Multiplier"
        if ($Cap -gt 0) { Write-OK "Cap        : $Cap" } else { Write-OK "Cap        : (none)" }
    }
    if ($KeepUnchanged) { Write-OK "Cleanup    : keep unchanged files" }
    else                { Write-OK "Cleanup    : delete unchanged files (Stack<=1)" }
    if ($DryRun)        { Write-Warn2 "DryRun active -> nothing will be written/deleted" }

    $files = Get-ChildItem -LiteralPath $sourceFull -Recurse -File -Filter '*.json'
    Write-OK "Found      : $($files.Count) JSON file(s)"
    if ($ExcludePath -and $ExcludePath.Count -gt 0) {
        Write-OK ("ExcludePath: {0}" -f ($ExcludePath -join ', '))
    }

    $modified = 0; $deleted = 0; $kept = 0; $skipped = 0; $capped = 0; $excluded = 0

    foreach ($f in $files) {
        $rel = $f.FullName.Substring($sourceFull.Length).TrimStart('\')
        $isExcluded = $false
        foreach ($pat in $ExcludePath) {
            if ($rel -like $pat) { $isExcluded = $true; break }
        }
        if ($isExcluded) {
            if ($KeepUnchanged) { $kept++ }
            else {
                if (-not $DryRun) { Remove-Item -LiteralPath $f.FullName -Force }
                $excluded++
            }
            continue
        }

        # Read explicitly as UTF-8, otherwise Windows PowerShell 5.1's default
        # ANSI codepage will mangle non-ASCII characters.
        $content = [System.IO.File]::ReadAllText($f.FullName, [System.Text.Encoding]::UTF8)

        if ($content -match '("MaxCountInSlot"\s*:\s*)(\d+)') {
            $oldVal = [int]$matches[2]

            if ($oldVal -le 1) {
                if ($KeepUnchanged) { $kept++ }
                else {
                    if (-not $DryRun) { Remove-Item -LiteralPath $f.FullName -Force }
                    $deleted++
                }
                continue
            }

            if ($isAbsolute) {
                $newVal = $AbsoluteValue
            } else {
                $newVal = $oldVal * $Multiplier
                if ($Cap -gt 0 -and $newVal -gt $Cap) {
                    $newVal = $Cap
                    $capped++
                }
            }

            if ($newVal -eq $oldVal) {
                $kept++
                continue
            }

            $newContent = [regex]::Replace(
                $content,
                '("MaxCountInSlot"\s*:\s*)\d+',
                { param($m) $m.Groups[1].Value + $newVal.ToString() },
                1
            )

            if (-not $DryRun) {
                $utf8 = New-Object System.Text.UTF8Encoding($false)
                [System.IO.File]::WriteAllText($f.FullName, $newContent, $utf8)
            }
            $modified++
        } else {
            # No MaxCountInSlot field -> delete (doesn't fit the schema)
            if ($KeepUnchanged) { $kept++ }
            else {
                if (-not $DryRun) { Remove-Item -LiteralPath $f.FullName -Force }
                $skipped++
            }
        }
    }

    # Clean up empty directories
    if (-not $DryRun -and -not $KeepUnchanged) {
        for ($i = 0; $i -lt 10; $i++) {
            $emptyDirs = Get-ChildItem -LiteralPath $sourceFull -Recurse -Directory |
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

    return [pscustomobject]@{
        Modified = $modified
        Deleted  = $deleted
        Skipped  = $skipped
        Excluded = $excluded
        Kept     = $kept
        Capped   = $capped
    }
}
