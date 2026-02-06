<#
.SYNOPSIS
    Stamps version into project files based on git history.

.DESCRIPTION
    Computes a version string from git and updates:
      - VSDebuggerMCP/source.extension.vsixmanifest
      - VSDebuggerMCP/Properties/AssemblyInfo.cs
      - RiderDebuggerMCP/gradle.properties

    Version scheme:
      - Tagged commit (v1.2.3)  -> 1.2.3
      - Untagged commit         -> 1.1.<total commit count>

    Compatible with PowerShell 5.1+ and PowerShell 7+.

.PARAMETER Version
    Override computed version (e.g., "1.2.3"). Skips git-based computation.

.PARAMETER DryRun
    Only output computed version; do not modify files.
#>
param(
    [string]$Version,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$utf8NoBom = New-Object System.Text.UTF8Encoding $false

function Write-FileSafe([string]$Path, [string]$Content) {
    [System.IO.File]::WriteAllText($Path, $Content, $utf8NoBom)
}

# --- Locate repo root ---
$repoRoot = $null
try {
    $repoRoot = (& git rev-parse --show-toplevel 2>$null)
    if ($LASTEXITCODE -ne 0) { $repoRoot = $null }
} catch { }

if (-not $repoRoot) {
    Write-Host "Stamp-Version: not inside a git repository, skipping."
    exit 0
}
$repoRoot = $repoRoot.Trim() -replace '/', '\'

# --- Compute version ---
if (-not $Version) {
    # Try exact tag match on HEAD (e.g., v1.2.3)
    $tag = $null
    try {
        $tag = (& git describe --exact-match --tags HEAD 2>$null)
        if ($LASTEXITCODE -ne 0) { $tag = $null }
    } catch { }

    if ($tag -match '^v?(\d+\.\d+\.\d+)') {
        $Version = $matches[1]
    } else {
        $commitCount = $null
        try {
            $commitCount = (& git rev-list --count HEAD 2>$null)
            if ($LASTEXITCODE -ne 0) { $commitCount = $null }
        } catch { }

        if ($commitCount) {
            $Version = "1.1.$($commitCount.Trim())"
        } else {
            Write-Host "Stamp-Version: unable to determine version from git, skipping."
            exit 0
        }
    }
}

Write-Host "Stamp-Version: $Version"

if ($DryRun) {
    exit 0
}

# --- Ensure version has at least 3 parts (X.Y.Z) ---
$parts = $Version.Split(".")
if ($parts.Count -lt 3) { $Version = "$Version.0" }

# Assembly version needs 4 parts (X.Y.Z.W)
$parts = $Version.Split(".")
$assemblyVersion = if ($parts.Count -eq 3) { "$Version.0" } else { $Version }

$changed = $false

# --- Update source.extension.vsixmanifest ---
$manifestPath = Join-Path $repoRoot "VSDebuggerMCP\source.extension.vsixmanifest"
if (Test-Path $manifestPath) {
    $manifest = [System.IO.File]::ReadAllText($manifestPath, $utf8NoBom)
    $updated = $manifest -creplace '(<Identity[^>]*Version=")[\d\.]+(")', "`${1}$Version`${2}"
    if ($updated -ne $manifest) {
        Write-FileSafe $manifestPath $updated
        Write-Host "  Updated: source.extension.vsixmanifest -> $Version"
        $changed = $true
    }
}

# --- Update AssemblyInfo.cs ---
$assemblyInfoPath = Join-Path $repoRoot "VSDebuggerMCP\Properties\AssemblyInfo.cs"
if (Test-Path $assemblyInfoPath) {
    $asmInfo = [System.IO.File]::ReadAllText($assemblyInfoPath, $utf8NoBom)
    $updated = $asmInfo
    $updated = $updated -replace 'AssemblyVersion\("[\d\.]+"\)', "AssemblyVersion(`"$assemblyVersion`")"
    $updated = $updated -replace 'AssemblyFileVersion\("[\d\.]+"\)', "AssemblyFileVersion(`"$assemblyVersion`")"
    if ($updated -ne $asmInfo) {
        Write-FileSafe $assemblyInfoPath $updated
        Write-Host "  Updated: AssemblyInfo.cs -> $assemblyVersion"
        $changed = $true
    }
}

# --- Update gradle.properties ---
$gradlePropPath = Join-Path $repoRoot "RiderDebuggerMCP\gradle.properties"
if (Test-Path $gradlePropPath) {
    $props = [System.IO.File]::ReadAllText($gradlePropPath, $utf8NoBom)
    $updated = $props -replace 'pluginVersion=[\d\.]+', "pluginVersion=$Version"
    if ($updated -ne $props) {
        Write-FileSafe $gradlePropPath $updated
        Write-Host "  Updated: gradle.properties -> $Version"
        $changed = $true
    }
}

if (-not $changed) {
    Write-Host "  All files already up-to-date."
}
