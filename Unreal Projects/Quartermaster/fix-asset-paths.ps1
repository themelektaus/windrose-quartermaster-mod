#requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$ContentRoot = Join-Path $PSScriptRoot 'Content'

if (-not (Test-Path $ContentRoot)) {
    throw "Content folder not found: $ContentRoot"
}

Write-Host "ContentRoot: $ContentRoot"
Write-Host "DryRun: $DryRun"
Write-Host ""

# Extracts all unique /Game/... strings from raw file bytes.
# UE FName entries are stored as length-prefixed ASCII or UTF-16; we just scan
# byte-by-byte for the printable ASCII run that starts with "/Game/".
function Get-PackagePathCandidates {
    param([string]$Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $len = $bytes.Length
    $candidates = New-Object System.Collections.Generic.HashSet[string]
    $prefix = [System.Text.Encoding]::ASCII.GetBytes('/Game/')
    $prefixLen = $prefix.Length

    for ($i = 0; $i -le $len - $prefixLen; $i++) {
        # Quick prefix match
        $match = $true
        for ($k = 0; $k -lt $prefixLen; $k++) {
            if ($bytes[$i + $k] -ne $prefix[$k]) { $match = $false; break }
        }
        if (-not $match) { continue }

        # Greedy walk - collect printable ASCII path chars (letters, digits, '_', '-', '/', '.')
        $sb = New-Object System.Text.StringBuilder
        for ($j = $i; $j -lt $len; $j++) {
            $b = $bytes[$j]
            if (($b -ge 0x30 -and $b -le 0x39) -or                  # 0-9
                ($b -ge 0x41 -and $b -le 0x5A) -or                  # A-Z
                ($b -ge 0x61 -and $b -le 0x7A) -or                  # a-z
                $b -eq 0x2F -or $b -eq 0x5F -or                     # /, _
                $b -eq 0x2D -or $b -eq 0x2E) {                      # -, .
                [void]$sb.Append([char]$b)
            } else {
                break
            }
        }
        $s = $sb.ToString()
        # Require at least one segment after /Game/ (so "/Game/" alone is skipped)
        if ($s.Length -gt 6 -and $s -notmatch '/$') {
            [void]$candidates.Add($s)
        }
    }
    return $candidates
}

# Given the asset's filename stem and the candidate /Game/... strings,
# pick the one whose last segment exactly matches a "renamed-back" stem.
# Strategy differs slightly between .umap (level files - own path usually NOT
# under /Items/, but cross-refs to assets are) and .uasset (own path matches
# its type prefix M_/T_/SM_/MI_).
function Select-BestCandidate {
    param(
        [string]$Stem,
        [string]$Ext,
        [System.Collections.Generic.HashSet[string]]$Candidates
    )

    if ($Candidates.Count -eq 0) { return $null }

    # Normalize: strip duplicate-suffix form "Path/Name.Name" -> "Path/Name"
    $norm = New-Object System.Collections.Generic.HashSet[string]
    foreach ($c in $Candidates) {
        $last = $c.Split('/')[-1]
        if ($last -match '^([^.]+)\.\1$') {
            [void]$norm.Add($c.Substring(0, $c.Length - $matches[1].Length - 1))
        } else {
            [void]$norm.Add($c)
        }
    }

    $isMap = ($Ext -ieq '.umap')

    $scored = @()
    foreach ($c in $norm) {
        $lastSeg = $c.Split('/')[-1]
        $stripped = $lastSeg -replace 'Qm', ''
        $stemPrefix = ($Stem -split '_', 2)[0]
        $lastPrefix = ($lastSeg -split '_', 2)[0]

        $score = 0
        if ($lastSeg -ceq $Stem) { $score += 10000 }
        if ($stripped -ceq $Stem) { $score += 5000 }

        if ($isMap) {
            # Maps: own path is usually NOT under /Items/. Cross-refs to item
            # assets all live under /Items/. Penalize /Items/ heavily and
            # prefer shorter paths (maps live at higher folder levels).
            if ($c -notmatch '/Items/') { $score += 2000 }
            $score -= $c.Length        # shorter = closer to map root
        } else {
            # Assets: own path's last segment shares its type prefix
            # (M_/SM_/T_/MI_/...) with the filename stem.
            if ($stemPrefix -ceq $lastPrefix) { $score += 1000 }
            $score += $c.Length        # longer = more specific path
        }

        $scored += [PSCustomObject]@{ Path = $c; LastSeg = $lastSeg; Score = $score }
    }

    $best = $scored | Sort-Object -Property Score -Descending | Select-Object -First 1
    return $best.Path
}

# Map every .uasset/.umap to its target path
$assets = Get-ChildItem -Path $ContentRoot -Recurse -File -Include '*.uasset', '*.umap' |
    Where-Object { $_.FullName -notmatch '\\(Collections|Developers)\\' }

$plan = @()
$problems = @()

foreach ($asset in $assets) {
    $stem = [System.IO.Path]::GetFileNameWithoutExtension($asset.Name)
    $candidates = Get-PackagePathCandidates -Path $asset.FullName

    if ($candidates.Count -eq 0) {
        $problems += "NO_PACKAGEPATH: $($asset.FullName)"
        continue
    }

    $packagePath = Select-BestCandidate -Stem $stem -Ext $asset.Extension -Candidates $candidates
    if (-not $packagePath) {
        $problems += "NO_CANDIDATE: $($asset.FullName) - candidates: $(($candidates -join ', '))"
        continue
    }

    # /Game/Quartermaster/Items/QmOtter/M_QmOtter_01 -> Content\Quartermaster\Items\QmOtter\M_QmOtter_01.<ext>
    $relPath = $packagePath -replace '^/Game/', '' -replace '/', '\'
    $targetFile = Join-Path $ContentRoot "$relPath$($asset.Extension)"

    $plan += [PSCustomObject]@{
        Source       = $asset.FullName
        Target       = $targetFile
        PackagePath  = $packagePath
        Stem         = $stem
        TargetStem   = [System.IO.Path]::GetFileNameWithoutExtension($targetFile)
    }
}

Write-Host "Planned moves ($($plan.Count)):"
foreach ($p in $plan) {
    $srcRel = $p.Source.Substring($ContentRoot.Length + 1)
    $tgtRel = $p.Target.Substring($ContentRoot.Length + 1)
    Write-Host ("  {0,-60} -> {1}" -f $srcRel, $tgtRel)
}

if ($problems.Count -gt 0) {
    Write-Host ""
    Write-Host "Problems (will NOT move these):" -ForegroundColor Yellow
    foreach ($p in $problems) { Write-Host "  $p" -ForegroundColor Yellow }
}

if ($DryRun) {
    Write-Host ""
    Write-Host "DryRun: nothing was moved." -ForegroundColor Cyan
    return
}

Write-Host ""
Write-Host "Executing moves..." -ForegroundColor Green

# Sibling extensions that travel with the .uasset (.uexp, .ubulk, .ufont, ...).
# In UE5 single-file format these often don't exist, but we handle them defensively.
$siblingExts = @('.uexp', '.ubulk', '.ufont', '.uptnl')

$moved = 0
$skipped = 0
foreach ($p in $plan) {
    if ($p.Source -ieq $p.Target) {
        $skipped++
        continue
    }

    $targetDir = Split-Path -Parent $p.Target
    if (-not (Test-Path $targetDir)) {
        New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
    }

    # Move main file
    if (Test-Path $p.Target) {
        Write-Host "  SKIP (target exists): $($p.Target)" -ForegroundColor Yellow
        $skipped++
        continue
    }
    Move-Item -LiteralPath $p.Source -Destination $p.Target
    Write-Host "  moved: $($p.Source.Substring($ContentRoot.Length + 1)) -> $($p.Target.Substring($ContentRoot.Length + 1))"

    # Move siblings sharing the same stem
    $srcDir = Split-Path -Parent $p.Source
    foreach ($ext in $siblingExts) {
        $sibSrc = Join-Path $srcDir "$($p.Stem)$ext"
        if (Test-Path $sibSrc) {
            $sibTgt = Join-Path $targetDir "$($p.TargetStem)$ext"
            Move-Item -LiteralPath $sibSrc -Destination $sibTgt
            Write-Host "    sibling: $ext"
        }
    }

    $moved++
}

Write-Host ""
Write-Host "Moved: $moved, Skipped: $skipped" -ForegroundColor Green

# Remove now-empty source folders (Otter, Painting, Wieselburger, ...)
Write-Host ""
Write-Host "Cleaning up empty folders..." -ForegroundColor Green
$removedDirs = 0
# Walk depth-first so we can prune leaves first
Get-ChildItem -Path $ContentRoot -Recurse -Directory |
    Sort-Object FullName -Descending |
    ForEach-Object {
        if (-not (Get-ChildItem -Path $_.FullName -Force | Where-Object { $_.Name -notin @('.', '..') })) {
            Write-Host "  removed: $($_.FullName.Substring($ContentRoot.Length + 1))"
            Remove-Item -LiteralPath $_.FullName -Force
            $removedDirs++
        }
    }
Write-Host "Removed empty dirs: $removedDirs" -ForegroundColor Green
