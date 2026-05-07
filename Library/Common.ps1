# Shared helpers for the Windrose Stack Size build pipeline. Provides:
#   Write-Step / Write-OK / Write-Warn2 / Write-Err2   (consistent logging)
#   Get-WindrosePaths                                  (default Sources/Builds
#                                                       paths relative to the
#                                                       modding root)
#   Get-RepakExe                                       (returns repak.exe,
#                                                       auto-downloads pinned
#                                                       v0.2.3 with SHA256 verify
#                                                       on first use)
#   Get-WindroseVanillaPak                             (locates the game's
#                                                       pakchunk0 by reading
#                                                       the Steam install via
#                                                       registry +
#                                                       libraryfolders.vdf)
#   Resolve-FullPath                                   (Resolve-Path with
#                                                       fallback for paths
#                                                       that don't exist yet)
#   Initialize-Directory                               (mkdir-if-missing,
#                                                       DryRun-aware)
#   Use-Default                                        (return value or
#                                                       default if empty)
# Safe to dot-source repeatedly (idempotency guard below).

if ($script:__WindroseCommonLoaded) { return }
$script:__WindroseCommonLoaded = $true

# --- Logging --------------------------------------------------------------

function Write-Step  { param([string]$msg) Write-Host "==> $msg"      -ForegroundColor Cyan   }
function Write-OK    { param([string]$msg) Write-Host "    [OK] $msg" -ForegroundColor Green  }
function Write-Warn2 { param([string]$msg) Write-Host "    [!]  $msg" -ForegroundColor Yellow }
function Write-Err2  { param([string]$msg) Write-Host "    [X]  $msg" -ForegroundColor Red    }

# --- Default paths --------------------------------------------------------

# Returns the absolute Sources / Vanilla / Builds paths relative to the
# modding root (= parent of the Library\ folder this file lives in).
function Get-WindrosePaths {
    [CmdletBinding()]
    param([string]$ModRoot)

    if (-not $ModRoot) {
        $ModRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
    }

    return [pscustomobject]@{
        Root    = $ModRoot
        Sources = [System.IO.Path]::GetFullPath((Join-Path $ModRoot 'Sources'))
        Vanilla = [System.IO.Path]::GetFullPath((Join-Path $ModRoot 'Sources\Vanilla'))
        Builds  = [System.IO.Path]::GetFullPath((Join-Path $ModRoot 'Builds'))
    }
}

# --- Path helpers ---------------------------------------------------------

# Resolve-Path with fallback to GetFullPath for non-existing paths (DryRun-friendly).
function Resolve-FullPath {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)
    $r = Resolve-Path -LiteralPath $Path -ErrorAction SilentlyContinue
    if ($r) { return $r.Path }
    return [System.IO.Path]::GetFullPath($Path)
}

# mkdir-if-missing, no-op in DryRun.
function Initialize-Directory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [switch]$DryRun
    )
    if (-not (Test-Path -LiteralPath $Path)) {
        if (-not $DryRun) {
            New-Item -ItemType Directory -Path $Path -Force | Out-Null
        }
    }
}

# Returns $Value if non-empty (whitespace-trimmed), otherwise $Default.
function Use-Default {
    [CmdletBinding()]
    param([string]$Value, [string]$Default)
    if ($Value -and $Value.Trim() -ne '') { return $Value }
    return $Default
}

# --- repak.exe -----------------------------------------------------------

# Pinned tool version. Bump here to upgrade.
$script:WindroseRepakVersion = '0.2.3'
$script:WindroseRepakAsset   = 'repak_cli-x86_64-pc-windows-msvc.zip'

# Returns the absolute path to repak.exe.
# Downloads and verifies it on first use; subsequent calls return the cached
# path. The .sha256 file from the same release URL is used for verification
# (mirrors WindrosePlus' release workflow -- protects against download
# corruption, not active tampering).
function Get-RepakExe {
    [CmdletBinding()]
    param([string]$ModRoot)

    if (-not $ModRoot) {
        $ModRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
    }

    $repakExe = Join-Path $ModRoot 'repak.exe'

    if (Test-Path -LiteralPath $repakExe -PathType Leaf) {
        return $repakExe
    }

    Write-Step "repak.exe not present -- downloading v$($script:WindroseRepakVersion)"

    Initialize-Directory -Path $ModRoot

    $url    = "https://github.com/trumank/repak/releases/download/v$($script:WindroseRepakVersion)/$($script:WindroseRepakAsset)"
    $tmpDir = Join-Path ([System.IO.Path]::GetTempPath()) ("windrose-repak-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmpDir -Force | Out-Null

    try {
        $zipPath = Join-Path $tmpDir $script:WindroseRepakAsset
        $shaPath = "$zipPath.sha256"

        Write-Host "    URL: $url" -ForegroundColor DarkGray

        # PS 5.1 may default to SSLv3/TLS1.0 -- force TLS 1.2 for github.com.
        try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

        $oldProgress = $ProgressPreference
        $ProgressPreference = 'SilentlyContinue'
        try {
            Invoke-WebRequest -Uri $url           -OutFile $zipPath -UseBasicParsing
            Invoke-WebRequest -Uri ($url+'.sha256') -OutFile $shaPath -UseBasicParsing
        } finally {
            $ProgressPreference = $oldProgress
        }

        # The .sha256 file contains "<hash>  <filename>" (or just "<hash>").
        $expectedHash = (Get-Content -LiteralPath $shaPath -Raw).Trim().Split()[0].ToLower()
        $actualHash   = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLower()

        if ($actualHash -ne $expectedHash) {
            throw "SHA256 mismatch for $($script:WindroseRepakAsset).`n  Expected: $expectedHash`n  Actual:   $actualHash"
        }
        Write-OK "SHA256 verified"

        $extractDir = Join-Path $tmpDir 'extract'
        Expand-Archive -LiteralPath $zipPath -DestinationPath $extractDir -Force

        $extractedExe = Get-ChildItem -LiteralPath $extractDir -Recurse -Filter 'repak.exe' |
            Select-Object -First 1
        if (-not $extractedExe) {
            throw "repak.exe not found inside $($script:WindroseRepakAsset)"
        }

        Copy-Item -LiteralPath $extractedExe.FullName -Destination $repakExe -Force
        Write-OK "Installed: $repakExe (repak v$($script:WindroseRepakVersion))"
    }
    finally {
        Remove-Item -LiteralPath $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    return $repakExe
}

# --- Vanilla pak auto-detection -----------------------------------------

# Pak filenames we accept, in priority order. Steam installs ship the
# Windows variant; dedicated server installs ship the WindowsServer one.
# Either contains the encrypted InventoryItems JSONs we need.
$script:WindroseVanillaPakNames = @(
    'pakchunk0-Windows.pak',
    'pakchunk0-WindowsServer.pak'
)

# Reads the Steam install path from the registry. Tries the per-user
# (HKCU) hive first because that's where Steam writes when launched
# normally, and falls back to the machine-wide 32-bit hive (HKLM\WOW6432Node)
# that the Steam installer creates.
function Get-SteamInstallPath {
    $hkcu = Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -Name 'SteamPath' -ErrorAction SilentlyContinue
    if ($hkcu -and $hkcu.SteamPath) {
        return ([string]$hkcu.SteamPath).Replace('/', '\')
    }
    $hklm = Get-ItemProperty -Path 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam' -Name 'InstallPath' -ErrorAction SilentlyContinue
    if ($hklm -and $hklm.InstallPath) {
        return ([string]$hklm.InstallPath).Replace('/', '\')
    }
    return $null
}

# Parses Steam's libraryfolders.vdf and returns every library root path.
# We only care about the "path" entries; full VDF parsing isn't needed.
function Get-SteamLibraryPaths {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$SteamPath)

    $libs = @($SteamPath)
    $vdf  = Join-Path $SteamPath 'steamapps\libraryfolders.vdf'
    if (-not (Test-Path -LiteralPath $vdf)) { return $libs }

    foreach ($line in Get-Content -LiteralPath $vdf) {
        if ($line -match '^\s*"path"\s*"([^"]+)"') {
            $p = $matches[1] -replace '\\\\', '\'
            if ($libs -notcontains $p) { $libs += $p }
        }
    }
    return $libs
}

# Returns the absolute path to the Windrose vanilla pak. Resolution order:
#   1. <SteamLibrary>\steamapps\common\Windrose\R5\Content\Paks\<paknames>
#      across every library listed in libraryfolders.vdf
# Throws a descriptive error when nothing is found so the user knows where
# to point -VanillaPak manually.
function Get-WindroseVanillaPak {
    [CmdletBinding()]
    param()

    $steam = Get-SteamInstallPath
    if (-not $steam) {
        throw "Could not locate the Steam install (no SteamPath in HKCU and no InstallPath in HKLM\WOW6432Node\Valve\Steam). Pass -VanillaPak <path> to override, e.g. <ServerDir>\R5\Content\Paks\pakchunk0-WindowsServer.pak."
    }

    $libs = Get-SteamLibraryPaths -SteamPath $steam
    foreach ($lib in $libs) {
        $paksDir = Join-Path $lib 'steamapps\common\Windrose\R5\Content\Paks'
        if (-not (Test-Path -LiteralPath $paksDir -PathType Container)) { continue }
        foreach ($name in $script:WindroseVanillaPakNames) {
            $candidate = Join-Path $paksDir $name
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                return (Resolve-Path -LiteralPath $candidate).Path
            }
        }
    }

    $searched = ($libs | ForEach-Object { Join-Path $_ 'steamapps\common\Windrose\R5\Content\Paks' }) -join "`n  "
    throw @"
Could not find a Windrose vanilla pak under any Steam library.
Searched:
  $searched
Pass -VanillaPak <path> to point at a pak directly, e.g. a dedicated-server install.
"@
}
