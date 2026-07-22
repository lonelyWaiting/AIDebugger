param(
    [string]$WheelsDirectory = "",
    [switch]$VerifyOnly
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($WheelsDirectory)) {
    $WheelsDirectory = Join-Path $repoRoot "mcp_server\vendor\wheels"
}
$resolvedWheels = [System.IO.Path]::GetFullPath($WheelsDirectory)
if (-not (Test-Path -LiteralPath $resolvedWheels -PathType Container)) {
    throw "Wheel directory does not exist: $resolvedWheels"
}

$pythonTags = @("cp310", "cp311", "cp312", "cp313", "cp314")
foreach ($distribution in @("pydantic_core", "cffi", "pywin32")) {
    foreach ($tag in $pythonTags) {
        $pattern = "$distribution-*-$tag-$tag-win_amd64.whl"
        if (-not (Get-ChildItem -LiteralPath $resolvedWheels -Filter $pattern -File)) {
            throw "Missing required wheel: $pattern"
        }
    }
}
foreach ($tag in $pythonTags) {
    $pattern = "rpds_py-*-$tag-$tag-win_amd64.whl"
    if (-not (Get-ChildItem -LiteralPath $resolvedWheels -Filter $pattern -File)) {
        throw "Missing required wheel: $pattern"
    }
}

$entries = foreach ($wheel in Get-ChildItem -LiteralPath $resolvedWheels -Filter "*.whl" -File | Sort-Object Name) {
    [ordered]@{
        file = "vendor/wheels/$($wheel.Name)"
        sha256 = (Get-FileHash -LiteralPath $wheel.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$manifestPath = Join-Path $resolvedWheels "manifest.json"
if ($VerifyOnly) {
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Wheel manifest does not exist: $manifestPath"
    }
    $existing = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($existing.platform -ne "win_amd64") { throw "Wheel manifest platform must be win_amd64" }
    if ((@($existing.python) -join ",") -ne ($pythonTags -join ",")) {
        throw "Wheel manifest Python coverage does not match the required tags"
    }
    $declared = @{}
    foreach ($entry in @($existing.wheels)) {
        if ($declared.ContainsKey($entry.file)) { throw "Duplicate wheel manifest entry: $($entry.file)" }
        $declared[$entry.file] = ([string]$entry.sha256).ToLowerInvariant()
    }
    if ($declared.Count -ne @($entries).Count) {
        throw "Wheel manifest declares $($declared.Count) files but $(@($entries).Count) wheels are present"
    }
    foreach ($entry in @($entries)) {
        if (-not $declared.ContainsKey($entry.file) -or $declared[$entry.file] -ne $entry.sha256) {
            throw "Wheel manifest hash mismatch: $($entry.file)"
        }
    }
    Write-Host "Verified $manifestPath with $(@($entries).Count) wheels."
    return
}
$manifest = [ordered]@{
    platform = "win_amd64"
    python = $pythonTags
    generatedAt = [DateTime]::UtcNow.ToString("yyyy-MM-dd")
    wheels = @($entries)
}
$temporaryPath = "$manifestPath.$([Guid]::NewGuid().ToString('N')).tmp"
$backupPath = "$manifestPath.$([Guid]::NewGuid().ToString('N')).bak"
try {
    $json = $manifest | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText($temporaryPath, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $manifestPath) {
        [System.IO.File]::Replace($temporaryPath, $manifestPath, $backupPath)
    }
    else {
        [System.IO.File]::Move($temporaryPath, $manifestPath)
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryPath) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
    if (Test-Path -LiteralPath $backupPath) {
        Remove-Item -LiteralPath $backupPath -Force
    }
}

Write-Host "Updated $manifestPath with $($entries.Count) wheels."
