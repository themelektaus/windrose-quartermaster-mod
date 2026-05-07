# Library: wraps repak.exe to pack a folder into a Windrose mod _P.pak.
# Public API surface lives in Build-WindroseMod.ps1 -- see there for
# user-facing docs. Returns the absolute path of the produced .pak.

if ($script:__WindrosePackLoaded) { return }
$script:__WindrosePackLoaded = $true

function Invoke-WindroseModPack {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Source,
        [string]$Name,
        [string]$OutDir,
        # Pak format defaults match every Windrose mod examined so far:
        # MountPoint '../../../' lands pak entries on the game root, V8B
        # is the format used by the current Windrose build (UE5.6).
        [string]$MountPoint = '../../../',
        [ValidateSet('V0','V1','V2','V3','V4','V5','V6','V7','V8A','V8B','V9','V10','V11')]
        [string]$Version = 'V8B',
        [string]$RepakExe,
        [switch]$Force,
        [switch]$DryRun
    )

    if (-not $OutDir -or $OutDir.Trim() -eq '') {
        $OutDir = (Get-WindrosePaths).Builds
    }

    Write-Step 'Checking prerequisites'

    # Auto-resolve repak.exe (download on first use). Skipped
    # in DryRun -- a dry run should never trigger a network call.
    if (-not $RepakExe -or $RepakExe.Trim() -eq '') {
        if ($DryRun) {
            $RepakExe = '<auto>'
        } else {
            $RepakExe = Get-RepakExe
        }
    } elseif (-not (Test-Path -LiteralPath $RepakExe)) {
        throw "repak.exe not found: $RepakExe"
    }
    Write-OK "repak.exe: $RepakExe"

    $sourceFull = (Resolve-Path -LiteralPath $Source).Path
    if (-not (Test-Path -LiteralPath $sourceFull -PathType Container)) {
        throw "Source is not a folder: $sourceFull"
    }

    $allFiles  = Get-ChildItem -LiteralPath $sourceFull -Recurse -File
    $fileCount = ($allFiles | Measure-Object).Count
    if ($fileCount -eq 0) {
        throw "Source folder is empty: $sourceFull"
    }
    Write-OK "Source: $sourceFull ($fileCount files)"

    if (-not $Name -or $Name.Trim() -eq '') {
        $Name = Split-Path -Leaf $sourceFull
    }
    if ($Name -notmatch '_P$') {
        $Name = $Name + '_P'
        Write-Warn2 "Name doesn't end in _P -> auto-corrected to '$Name'"
    }

    Initialize-Directory -Path $OutDir -DryRun:$DryRun
    $OutDir = Resolve-FullPath $OutDir

    $outPak = Join-Path $OutDir ($Name + '.pak')
    Write-OK "Output: $outPak"

    if ((Test-Path -LiteralPath $outPak) -and -not $Force -and -not $DryRun) {
        throw "Output already exists: $outPak  (use -Force to overwrite)"
    }

    Write-Step 'Building .pak'

    $packArgs = @(
        'pack',
        '--mount-point', $MountPoint,
        '--version',     $Version,
        $sourceFull,
        $outPak
    )

    Write-Host "    repak $($packArgs -join ' ')" -ForegroundColor DarkGray

    if ($DryRun) {
        Write-Warn2 'DryRun -> no pack performed'
    } else {
        & $RepakExe @packArgs
        if ($LASTEXITCODE -ne 0) { throw "repak pack failed (exit $LASTEXITCODE)" }
        if (-not (Test-Path -LiteralPath $outPak)) {
            throw "Pak was not created: $outPak"
        }
        $sizeKB = [math]::Round((Get-Item -LiteralPath $outPak).Length / 1KB, 1)
        Write-OK ("Pak built: {0}  ({1} KB)" -f $outPak, $sizeKB)
    }

    if (-not $DryRun) {
        Write-Step 'Verifying pak'
        $info = & $RepakExe info $outPak 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Err2 'repak info failed'
        }
        $info | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
    }

    Write-Host ''
    Write-Step 'Done'
    Write-OK "Pak: $outPak"
    if ($DryRun) { Write-Warn2 'DryRun active -> nothing was actually written' }

    return $outPak
}
