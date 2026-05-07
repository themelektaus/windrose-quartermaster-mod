# Library: extracts inventory item icons from the AES-encrypted game pak.
# Public API surface lives in Extract-Icons.ps1 -- see there for user-facing
# docs.
#
# Pipeline:
#   1. Walk Sources/Vanilla/.../*.json and pull the
#      InventoryItemUIData.ItemTexture path out of each one.
#   2. Build a manifest [{itemId, texturePath}, ...] and write it to a temp
#      file.
#   3. Hand the manifest to the C# IconExtractor (auto-built when missing),
#      together with the AES key, paks dir, and .usmap.
#   4. PNGs land under <OutDir>\<itemId>.png.

if ($script:__WindroseIconsLoaded) { return }
$script:__WindroseIconsLoaded = $true

# Same public game key used by Dump.ps1 (and by every Windrose modding tool).
$script:WindroseAesKey = '0x5F430BF9FEF2B0B91B7C79C313BDAF291BA076A1DAB5045974186333AA16CFAE'

function Invoke-WindroseIconExtract {
    [CmdletBinding()]
    param(
        # Folder with the per-item JSONs (e.g. Sources/Vanilla/.../InventoryItems/).
        # Defaults to <ModRoot>\Sources\Vanilla.
        [string]$Source,

        # Where the PNGs end up. Defaults to <ModRoot>\Icons.
        [string]$OutDir,

        # Pak folder for CUE4Parse to mount. Default: auto-detected via
        # Get-WindroseVanillaPak (Steam install).
        [string]$PaksDir,

        # UE5 mappings file. Default: auto-resolved via Get-WindroseUsmap.
        [string]$Usmap,

        # IconExtractor.exe override. Default: auto-built on first use.
        [string]$ExtractorExe,

        # CUE4Parse EGame value. Default: UE5_6 (verified via Banana test).
        [string]$GameVersion = 'UE5_6',

        [switch]$DryRun
    )

    Write-Step 'Checking prerequisites'

    if (-not $Source -or $Source.Trim() -eq '') {
        $Source = (Get-WindrosePaths).Vanilla
    }
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw @"
Source folder not found: $Source

Run Dump-WindroseVanilla.ps1 first to extract the vanilla JSONs.
"@
    }
    $Source = (Resolve-Path -LiteralPath $Source).Path
    Write-OK "Source:    $Source"

    if (-not $OutDir -or $OutDir.Trim() -eq '') {
        $OutDir = Join-Path (Get-WindrosePaths).Root 'Icons'
    }
    Initialize-Directory -Path $OutDir -DryRun:$DryRun
    $OutDir = Resolve-FullPath $OutDir
    Write-OK "OutDir:    $OutDir"

    # Auto-resolve PaksDir from the Steam-detected vanilla pak's folder.
    # DryRun stays offline (registry probe only).
    if (-not $PaksDir -or $PaksDir.Trim() -eq '') {
        if ($DryRun) {
            $PaksDir = '<auto>'
        } else {
            $vanillaPak = Get-WindroseVanillaPak
            $PaksDir    = Split-Path -Parent $vanillaPak
        }
    } elseif (-not (Test-Path -LiteralPath $PaksDir -PathType Container)) {
        throw "PaksDir not found: $PaksDir"
    }
    Write-OK "PaksDir:   $PaksDir"

    if (-not $Usmap -or $Usmap.Trim() -eq '') {
        if ($DryRun) {
            $Usmap = '<auto>'
        } else {
            $Usmap = Get-WindroseUsmap
        }
    } elseif (-not (Test-Path -LiteralPath $Usmap -PathType Leaf)) {
        throw "Usmap not found: $Usmap"
    }
    Write-OK "Usmap:     $Usmap"

    if (-not $ExtractorExe -or $ExtractorExe.Trim() -eq '') {
        if ($DryRun) {
            $ExtractorExe = '<auto>'
        } else {
            $ExtractorExe = Get-IconExtractorExe
        }
    } elseif (-not (Test-Path -LiteralPath $ExtractorExe -PathType Leaf)) {
        throw "IconExtractor.exe not found: $ExtractorExe"
    }
    Write-OK "Extractor: $ExtractorExe"

    # --- Build manifest from JSONs ----------------------------------------
    Write-Step 'Scanning JSONs for ItemTexture paths'

    $jsons = Get-ChildItem -LiteralPath $Source -Recurse -File -Filter '*.json'
    if (-not $jsons -or $jsons.Count -eq 0) {
        throw "No *.json files found under $Source"
    }

    $entries = New-Object System.Collections.Generic.List[object]
    $skipped = 0
    foreach ($f in $jsons) {
        try {
            $obj = Get-Content -LiteralPath $f.FullName -Raw | ConvertFrom-Json -ErrorAction Stop
        } catch {
            $skipped++
            continue
        }
        $tex = $null
        if ($obj.PSObject.Properties.Name -contains 'InventoryItemUIData') {
            $tex = [string]$obj.InventoryItemUIData.ItemTexture
        }
        if (-not $tex -or $tex -eq 'None' -or $tex.Trim() -eq '') {
            $skipped++
            continue
        }
        # itemId = JSON filename without extension (matches DA_CID_*/DA_EID_* etc).
        $entries.Add([pscustomobject]@{
            itemId       = [System.IO.Path]::GetFileNameWithoutExtension($f.Name)
            texturePath  = $tex
        }) | Out-Null
    }
    Write-OK ("Manifest entries: {0} (skipped {1} JSONs without ItemTexture)" -f $entries.Count, $skipped)

    if ($entries.Count -eq 0) {
        throw "No InventoryItemUIData.ItemTexture paths found under $Source"
    }

    # --- Invoke IconExtractor --------------------------------------------
    $manifestPath = Join-Path ([System.IO.Path]::GetTempPath()) ("windrose-icons-" + [guid]::NewGuid().ToString('N') + ".json")
    $manifestJson = ConvertTo-Json -InputObject $entries.ToArray() -Depth 4 -Compress
    Set-Content -LiteralPath $manifestPath -Value $manifestJson -Encoding UTF8

    Write-Step 'Running IconExtractor'

    $extractorArgs = @(
        '--paks-dir',     $PaksDir
        '--aes-key',      $script:WindroseAesKey
        '--manifest',     $manifestPath
        '--out-dir',      $OutDir
        '--usmap',        $Usmap
        '--game-version', $GameVersion
    )

    $display = @(
        '--paks-dir',     $PaksDir
        '--aes-key',      '<hidden>'
        '--manifest',     $manifestPath
        '--out-dir',      $OutDir
        '--usmap',        $Usmap
        '--game-version', $GameVersion
    )
    Write-Host "    IconExtractor.exe $($display -join ' ')" -ForegroundColor DarkGray

    if ($DryRun) {
        Write-Warn2 'DryRun -> IconExtractor not invoked'
        Remove-Item -LiteralPath $manifestPath -Force -ErrorAction SilentlyContinue
        Write-Host ''
        Write-Step 'Done (DryRun)'
        return
    }

    try {
        & $ExtractorExe @extractorArgs
        $exit = $LASTEXITCODE
    } finally {
        Remove-Item -LiteralPath $manifestPath -Force -ErrorAction SilentlyContinue
    }
    if ($exit -ne 0) {
        throw "IconExtractor failed (exit $exit)"
    }

    # --- Statistics -------------------------------------------------------
    Write-Step 'Statistics'
    $pngs = Get-ChildItem -LiteralPath $OutDir -Filter '*.png' -File -ErrorAction SilentlyContinue
    $pngCount = if ($pngs) { @($pngs).Count } else { 0 }
    $totalKB  = if ($pngs) { [math]::Round((($pngs | Measure-Object -Sum -Property Length).Sum) / 1KB, 1) } else { 0 }
    Write-OK ("{0} PNG files written ({1} KB total)" -f $pngCount, $totalKB)

    Write-Host ''
    Write-Step 'Done'
    Write-OK "Icons: $OutDir"
}
