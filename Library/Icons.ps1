# Library: extracts inventory item icons from the AES-encrypted game pak.
# Public API surface lives in Extract-Icons.ps1 -- see there for user-facing
# docs.
#
# Pipeline:
#   1. Walk Sources/Vanilla/.../*.json and pull, for each item:
#        - InventoryItemUIData.ItemTexture            (asset path)
#        - InventoryItemUIData.ItemName         FText (TableId + Key)
#        - InventoryItemUIData.ItemDescription  FText (TableId + Key)
#        - InventoryItemUIData.VanityText       FText (or empty string)
#        - InventoryItemUIData.EffectsDescriptions[]  list of FText
#        - InventoryItemUIData.SetEffectsDescriptions[]  list of structs with
#          nested Name/Description FText + ActivationCount + SetEffectTag.
#   2. Build a manifest [{itemId, texturePath, nameTable/Key, descTable/Key,
#      vanityTable/Key, effects[], setEffects[]}, ...] and write it to a temp
#      file.
#   3. Hand the manifest to the C# IconExtractor (auto-built when missing),
#      together with the AES key, paks dir, and .usmap.
#   4. PNGs land under <OutDir>\<itemId>.png. Localized title/description
#      sidecars land under <OutDir>\<itemId>.json (one per item, with all
#      shipped cultures merged).

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
    $withName = 0
    $withVanity = 0
    $withEffects = 0
    $withSetEffects = 0

    # Helpers (closures capturing nothing -> safe to inline).
    function Test-FTextRef ($node) {
        # Returns $true if $node looks like a usable {TableId, Key} pair.
        if ($null -eq $node) { return $false }
        if ($node -is [string]) { return $false }    # VanityText is sometimes ""
        if (-not $node.PSObject.Properties.Name -contains 'TableId') { return $false }
        $t = [string]$node.TableId
        $k = [string]$node.Key
        return ($t -and $k -and $t.Trim() -ne '' -and $k.Trim() -ne '')
    }

    foreach ($f in $jsons) {
        try {
            $obj = Get-Content -LiteralPath $f.FullName -Raw | ConvertFrom-Json -ErrorAction Stop
        } catch {
            $skipped++
            continue
        }
        $ui = $null
        if ($obj.PSObject.Properties.Name -contains 'InventoryItemUIData') {
            $ui = $obj.InventoryItemUIData
        }
        $tex = if ($ui) { [string]$ui.ItemTexture } else { $null }
        if (-not $tex -or $tex -eq 'None' -or $tex.Trim() -eq '') {
            $skipped++
            continue
        }

        # FText (TableId + Key) refs -- all fields are optional; we treat any
        # missing piece as "no value" and let the C# side skip the lookup.
        $nameTable = $null; $nameKey = $null
        $descTable = $null; $descKey = $null
        $vanityTable = $null; $vanityKey = $null
        $effects    = New-Object System.Collections.Generic.List[object]
        $setEffects = New-Object System.Collections.Generic.List[object]

        if ($ui) {
            if (Test-FTextRef $ui.ItemName) {
                $nameTable = [string]$ui.ItemName.TableId
                $nameKey   = [string]$ui.ItemName.Key
            }
            if (Test-FTextRef $ui.ItemDescription) {
                $descTable = [string]$ui.ItemDescription.TableId
                $descKey   = [string]$ui.ItemDescription.Key
            }
            # VanityText is polymorph: empty string OR FText object.
            if ($ui.PSObject.Properties.Name -contains 'VanityText' -and (Test-FTextRef $ui.VanityText)) {
                $vanityTable = [string]$ui.VanityText.TableId
                $vanityKey   = [string]$ui.VanityText.Key
            }
            # EffectsDescriptions: array of FText. Empty array is the common case.
            if ($ui.PSObject.Properties.Name -contains 'EffectsDescriptions' -and $ui.EffectsDescriptions) {
                foreach ($e in $ui.EffectsDescriptions) {
                    if (Test-FTextRef $e) {
                        $effects.Add([pscustomobject]@{
                            table = [string]$e.TableId
                            key   = [string]$e.Key
                        }) | Out-Null
                    }
                }
            }
            # SetEffectsDescriptions: array of structs with nested Name/Description
            # FText + ActivationCount + SetEffectTag. Only ~70 items have entries.
            if ($ui.PSObject.Properties.Name -contains 'SetEffectsDescriptions' -and $ui.SetEffectsDescriptions) {
                foreach ($s in $ui.SetEffectsDescriptions) {
                    $sn = $null
                    $sd = $null
                    if (Test-FTextRef $s.Name) {
                        $sn = [pscustomobject]@{ table = [string]$s.Name.TableId; key = [string]$s.Name.Key }
                    }
                    if (Test-FTextRef $s.Description) {
                        $sd = [pscustomobject]@{ table = [string]$s.Description.TableId; key = [string]$s.Description.Key }
                    }
                    if (-not $sn -and -not $sd) { continue }   # nothing localizable here

                    $tag = $null
                    if ($s.PSObject.Properties.Name -contains 'SetEffectTag' -and $s.SetEffectTag -and $s.SetEffectTag.PSObject.Properties.Name -contains 'TagName') {
                        $tn = [string]$s.SetEffectTag.TagName
                        if ($tn -and $tn -ne 'None') { $tag = $tn }
                    }
                    $count = 0
                    if ($s.PSObject.Properties.Name -contains 'ActivationCount') {
                        $count = [int]$s.ActivationCount
                    }
                    $setEffects.Add([pscustomobject]@{
                        nameTable       = if ($sn) { $sn.table } else { $null }
                        nameKey         = if ($sn) { $sn.key }   else { $null }
                        descTable       = if ($sd) { $sd.table } else { $null }
                        descKey         = if ($sd) { $sd.key }   else { $null }
                        setEffectTag    = $tag
                        activationCount = $count
                    }) | Out-Null
                }
            }
        }

        if ($nameTable -and $nameKey) { $withName++ }
        if ($vanityTable -and $vanityKey) { $withVanity++ }
        if ($effects.Count -gt 0) { $withEffects++ }
        if ($setEffects.Count -gt 0) { $withSetEffects++ }

        # itemId = JSON filename without extension (matches DA_CID_*/DA_EID_* etc).
        $entries.Add([pscustomobject]@{
            itemId      = [System.IO.Path]::GetFileNameWithoutExtension($f.Name)
            texturePath = $tex
            nameTable   = $nameTable
            nameKey     = $nameKey
            descTable   = $descTable
            descKey     = $descKey
            vanityTable = $vanityTable
            vanityKey   = $vanityKey
            effects     = $effects.ToArray()
            setEffects  = $setEffects.ToArray()
        }) | Out-Null
    }
    Write-OK ("Manifest entries: {0} (skipped {1} JSONs without ItemTexture)" -f $entries.Count, $skipped)
    Write-OK ("Localization keys: {0} name, {1} vanity, {2} effects, {3} set-effects" -f $withName, $withVanity, $withEffects, $withSetEffects)

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
    $pngKB    = if ($pngs) { [math]::Round((($pngs | Measure-Object -Sum -Property Length).Sum) / 1KB, 1) } else { 0 }
    Write-OK ("{0} PNG files written ({1} KB total)" -f $pngCount, $pngKB)

    $metas = Get-ChildItem -LiteralPath $OutDir -Filter '*.json' -File -ErrorAction SilentlyContinue
    $metaCount = if ($metas) { @($metas).Count } else { 0 }
    $metaKB    = if ($metas) { [math]::Round((($metas | Measure-Object -Sum -Property Length).Sum) / 1KB, 1) } else { 0 }
    if ($metaCount -gt 0) {
        Write-OK ("{0} metadata sidecars written ({1} KB total)" -f $metaCount, $metaKB)
    }

    Write-Host ''
    Write-Step 'Done'
    Write-OK "Icons: $OutDir"
}
