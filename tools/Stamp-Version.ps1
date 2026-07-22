<#
.SYNOPSIS
    Computes a deterministic numeric version and stamps the selected project files.

.DESCRIPTION
    Exact semver tag vX.Y.Z -> X.Y.Z.
    Untagged commit N commits after the nearest semver tag -> X.Y.Z.N.
    Repositories without a reachable semver tag -> 0.0.0.<commit-count>.
    Git and stamping failures are fatal so a package cannot be published with a stale version.
#>
param(
    [string]$Version,
    [ValidateSet("All", "VS", "Rider")]
    [string]$Target = "All",
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$utf8NoBom = New-Object System.Text.UTF8Encoding $false

function Invoke-Git([string[]]$Arguments) {
    $output = & git @Arguments 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
    return ($output | Out-String).Trim()
}

function Write-FileAtomically([string]$Path, [string]$Content) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $temporaryPath = "$fullPath.$([Guid]::NewGuid().ToString('N')).tmp"
    $backupPath = "$fullPath.$([Guid]::NewGuid().ToString('N')).bak"
    try {
        [System.IO.File]::WriteAllText($temporaryPath, $Content, $utf8NoBom)
        if ([System.IO.File]::Exists($fullPath)) {
            [System.IO.File]::Replace($temporaryPath, $fullPath, $backupPath)
        }
        else {
            [System.IO.File]::Move($temporaryPath, $fullPath)
        }
    }
    finally {
        if ([System.IO.File]::Exists($temporaryPath)) { [System.IO.File]::Delete($temporaryPath) }
        if ([System.IO.File]::Exists($backupPath)) { [System.IO.File]::Delete($backupPath) }
    }
}

$repoRoot = Invoke-Git @("rev-parse", "--show-toplevel")
if (-not [System.IO.Directory]::Exists($repoRoot)) {
    throw "Git returned an invalid repository root: $repoRoot"
}

if (-not $Version) {
    $description = $null
    try {
        $description = Invoke-Git @("describe", "--tags", "--long", "--match", "v[0-9]*.[0-9]*.[0-9]*", "HEAD")
    }
    catch {
        $commitCount = Invoke-Git @("rev-list", "--count", "HEAD")
        if ($commitCount -notmatch '^\d+$') { throw "Git returned an invalid commit count: $commitCount" }
        $Version = "0.0.0.$commitCount"
    }

    if ($description) {
        if ($description -notmatch '^v?(\d+)\.(\d+)\.(\d+)-(\d+)-g[0-9a-f]+$') {
            throw "Nearest semver tag has an unsupported format: $description"
        }
        $baseVersion = "$($matches[1]).$($matches[2]).$($matches[3])"
        $distance = [int]$matches[4]
        $Version = if ($distance -eq 0) { $baseVersion } else { "$baseVersion.$distance" }
    }
}

if ($Version -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') {
    throw "Version must contain three or four numeric components: $Version"
}
$parts = @($Version.Split('.') | ForEach-Object { [int]$_ })
if ($parts | Where-Object { $_ -lt 0 -or $_ -gt 65535 }) {
    throw "Every version component must be between 0 and 65535: $Version"
}
$assemblyVersion = if ($parts.Count -eq 3) { "$Version.0" } else { $Version }

Write-Host "Stamp-Version: $Version (target=$Target)"
if ($DryRun) { return }

$changed = $false
if ($Target -in @("All", "VS")) {
    $manifestPath = Join-Path $repoRoot "VSDebuggerMCP\source.extension.vsixmanifest"
    $assemblyInfoPath = Join-Path $repoRoot "VSDebuggerMCP\Properties\AssemblyInfo.cs"
    foreach ($requiredPath in @($manifestPath, $assemblyInfoPath)) {
        if (-not [System.IO.File]::Exists($requiredPath)) { throw "Required version file is missing: $requiredPath" }
    }

    $manifest = [System.IO.File]::ReadAllText($manifestPath, $utf8NoBom)
    $updatedManifest = $manifest -creplace '(<Identity[^>]*Version=")[\d\.]+(")', "`${1}$Version`${2}"
    $expectedManifestVersion = 'Version="' + [Regex]::Escape($Version) + '"'
    if ($updatedManifest -eq $manifest -and $manifest -notmatch $expectedManifestVersion) {
        throw "VSIX manifest version field was not found"
    }
    if ($updatedManifest -ne $manifest) {
        Write-FileAtomically $manifestPath $updatedManifest
        $changed = $true
    }

    $assemblyInfo = [System.IO.File]::ReadAllText($assemblyInfoPath, $utf8NoBom)
    $updatedAssemblyInfo = $assemblyInfo -replace 'AssemblyVersion\("[\d\.]+"\)', "AssemblyVersion(`"$assemblyVersion`")"
    $updatedAssemblyInfo = $updatedAssemblyInfo -replace 'AssemblyFileVersion\("[\d\.]+"\)', "AssemblyFileVersion(`"$assemblyVersion`")"
    if ($updatedAssemblyInfo -eq $assemblyInfo -and $assemblyInfo -notmatch [Regex]::Escape($assemblyVersion)) {
        throw "Assembly version fields were not found"
    }
    if ($updatedAssemblyInfo -ne $assemblyInfo) {
        Write-FileAtomically $assemblyInfoPath $updatedAssemblyInfo
        $changed = $true
    }
}

if ($Target -in @("All", "Rider")) {
    $gradlePath = Join-Path $repoRoot "RiderDebuggerMCP\gradle.properties"
    if (-not [System.IO.File]::Exists($gradlePath)) { throw "Required version file is missing: $gradlePath" }
    $properties = [System.IO.File]::ReadAllText($gradlePath, $utf8NoBom)
    $updatedProperties = $properties -replace 'pluginVersion=[\d\.]+', "pluginVersion=$Version"
    if ($updatedProperties -eq $properties -and $properties -notmatch "pluginVersion=$([Regex]::Escape($Version))") {
        throw "Rider pluginVersion field was not found"
    }
    if ($updatedProperties -ne $properties) {
        Write-FileAtomically $gradlePath $updatedProperties
        $changed = $true
    }
}

if (-not $changed) { Write-Host "  All selected files already up-to-date." }
