# Library: extracts vanilla InventoryItems from the AES-encrypted game pak.
# Public API surface lives in Dump-WindroseVanilla.ps1 -- see there for
# user-facing docs.

if ($script:__WindroseDumpLoaded) { return }
$script:__WindroseDumpLoaded = $true

# Public game encryption key (NOT a secret -- used by every Windrose
# modding tool, e.g. WindrosePlus' IniConfigParser.ps1).
$script:WindroseAesKey = '0x5F430BF9FEF2B0B91B7C79C313BDAF291BA076A1DAB5045974186333AA16CFAE'

# Pak-internal prefix we want to extract.
$script:WindroseInventoryItemsPath = 'R5/Plugins/R5BusinessRules/Content/InventoryItems'

function Invoke-WindroseVanillaDump {
    [CmdletBinding()]
    param(
        [string]$VanillaPak,
        [Parameter(Mandatory)][string]$OutDir,
        [string]$RepakExe,
        [switch]$Clean,
        [switch]$Force,
        [switch]$DryRun
    )

    Write-Step 'Checking prerequisites'

    # Auto-resolve VanillaPak via Steam registry + libraryfolders.vdf when
    # not supplied. DryRun stays offline-/IO-light (registry probe only,
    # no failure if Windrose is missing).
    if (-not $VanillaPak -or $VanillaPak.Trim() -eq '') {
        if ($DryRun) {
            $VanillaPak = '<auto>'
        } else {
            $VanillaPak = Get-WindroseVanillaPak
        }
    } elseif (-not (Test-Path -LiteralPath $VanillaPak -PathType Leaf)) {
        throw "VanillaPak not found: $VanillaPak"
    } else {
        $VanillaPak = (Resolve-Path -LiteralPath $VanillaPak).Path
    }
    Write-OK "VanillaPak: $VanillaPak"

    # Auto-resolve repak.exe (downloads to lib\bin\ on first use). Skipped
    # in DryRun -- a dry run should never trigger a network call.
    if (-not $RepakExe -or $RepakExe.Trim() -eq '') {
        if ($DryRun) {
            $RepakExe = '<auto>'
        } else {
            $RepakExe = Get-RepakExe
        }
    } elseif (-not (Test-Path -LiteralPath $RepakExe -PathType Leaf)) {
        throw "repak.exe not found: $RepakExe"
    }
    Write-OK "RepakExe:   $RepakExe"

    Initialize-Directory -Path $OutDir -DryRun:$DryRun
    $OutDir = Resolve-FullPath $OutDir
    Write-OK "OutDir:     $OutDir"

    if ($Clean) {
        Write-Step 'Clean: emptying OutDir'
        if ($DryRun) {
            Write-Warn2 'DryRun -> OutDir left unchanged'
        } else {
            Get-ChildItem -LiteralPath $OutDir -Force -ErrorAction SilentlyContinue |
                Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
            Write-OK 'OutDir emptied'
        }
    }

    Write-Step 'Unpacking InventoryItems from pak'

    $unpackArgs = @(
        '--aes-key', $script:WindroseAesKey,
        'unpack',
        '-i', $script:WindroseInventoryItemsPath,
        '-o', $OutDir
    )
    if ($Force) { $unpackArgs += '-f' }
    $unpackArgs += $VanillaPak

    $display = @(
        '--aes-key', '<hidden>',
        'unpack',
        '-i', $script:WindroseInventoryItemsPath,
        '-o', $OutDir
    )
    if ($Force) { $display += '-f' }
    $display += $VanillaPak
    Write-Host "    repak $($display -join ' ')" -ForegroundColor DarkGray

    if ($DryRun) {
        Write-Warn2 'DryRun -> repak not invoked'
        Write-Host ''
        Write-Step 'Done (DryRun)'
        return
    }

    & $RepakExe @unpackArgs
    if ($LASTEXITCODE -ne 0) {
        throw "repak unpack failed (exit $LASTEXITCODE)"
    }

    Write-Step 'Statistics'

    $invRoot = Join-Path $OutDir 'R5\Plugins\R5BusinessRules\Content\InventoryItems'
    if (-not (Test-Path -LiteralPath $invRoot)) {
        Write-Warn2 "Expected directory not produced: $invRoot"
    } else {
        $allJson = Get-ChildItem -LiteralPath $invRoot -Recurse -File -Filter '*.json'
        Write-OK ("{0} JSON files extracted" -f $allJson.Count)

        $byCategory = [ordered]@{}
        foreach ($f in $allJson) {
            $rel = $f.FullName.Substring($invRoot.Length).TrimStart('\','/')
            $segs = $rel -split '[\\/]+'
            $cat  = if ($segs.Count -ge 2) { $segs[0] } else { '(other)' }
            if (-not $byCategory.Contains($cat)) { $byCategory[$cat] = 0 }
            $byCategory[$cat]++
        }
        foreach ($k in $byCategory.Keys) {
            Write-Host ("    {0,-15} {1,6}" -f $k, $byCategory[$k]) -ForegroundColor DarkGray
        }
    }

    Write-Host ''
    Write-Step 'Done'
    Write-OK "Vanilla source ready: $OutDir"
    Write-Host ''
    Write-Host '    Next step: build the variants with' -ForegroundColor DarkGray
    Write-Host '        .\Build-AllStackVariations.ps1' -ForegroundColor DarkGray
}
