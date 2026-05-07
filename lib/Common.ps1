# Shared helpers for the Windrose Stack Size build pipeline. Provides:
#   Write-Step / Write-OK / Write-Warn2 / Write-Err2   (consistent logging)
#   Get-WindroseConfig                                 (config.psd1 loader)
#   Get-RepakExe                                       (returns lib\bin\repak.exe,
#                                                       auto-downloads pinned
#                                                       v0.2.3 with SHA256 verify
#                                                       on first use)
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

# --- Config ---------------------------------------------------------------

# Loads config.psd1 (or example fallback). Validates required sections,
# fills in path defaults, resolves relative paths against $ModRoot.
# $ModRoot defaults to the parent of the lib\ folder this file lives in.
function Get-WindroseConfig {
    [CmdletBinding()]
    param(
        [string]$ModRoot
    )

    if (-not $ModRoot) {
        # lib\Common.ps1 -> parent is lib\, parent of that is the modding root
        $ModRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
    }

    $cfgPath     = Join-Path $ModRoot 'config.psd1'
    $examplePath = Join-Path $ModRoot 'config.example.psd1'

    if (Test-Path -LiteralPath $cfgPath) {
        $cfg = Import-PowerShellDataFile -LiteralPath $cfgPath
    }
    elseif (Test-Path -LiteralPath $examplePath) {
        Write-Warning "config.psd1 not found -- falling back to config.example.psd1. Copy and adjust: cp config.example.psd1 config.psd1"
        $cfg = Import-PowerShellDataFile -LiteralPath $examplePath
    }
    else {
        throw "Neither config.psd1 nor config.example.psd1 found in $ModRoot."
    }

    foreach ($k in 'Paths','Pak','Game') {
        if (-not $cfg.ContainsKey($k)) { $cfg[$k] = @{} }
    }

    $pathDefaults = [ordered]@{
        Sources = 'Sources'
        Vanilla = 'Sources\Vanilla'
        Builds  = 'Builds'
    }
    foreach ($k in $pathDefaults.Keys) {
        $val = [string]$cfg.Paths[$k]
        if (-not $val -or $val.Trim() -eq '') {
            $cfg.Paths[$k] = [System.IO.Path]::GetFullPath((Join-Path $ModRoot $pathDefaults[$k]))
        }
        elseif (-not [System.IO.Path]::IsPathRooted($val)) {
            $cfg.Paths[$k] = [System.IO.Path]::GetFullPath((Join-Path $ModRoot $val))
        }
        else {
            $cfg.Paths[$k] = [System.IO.Path]::GetFullPath($val)
        }
    }

    return $cfg
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

# Returns the absolute path to repak.exe under <ModRoot>\lib\bin\.
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

    $binDir   = Join-Path $ModRoot 'lib\bin'
    $repakExe = Join-Path $binDir 'repak.exe'

    if (Test-Path -LiteralPath $repakExe -PathType Leaf) {
        return $repakExe
    }

    Write-Step "repak.exe not present -- downloading v$($script:WindroseRepakVersion)"

    Initialize-Directory -Path $binDir

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
