param(
    [Parameter(Mandatory = $true)]
    [string]$Tag,
    [string]$ExpectedCommit
)

$ErrorActionPreference = "Stop"
if ($Tag -cnotmatch '^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
    throw "Release tag must be exactly vMAJOR.MINOR.PATCH with canonical decimal components: $Tag"
}
$expectedVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3])"

$tagCommit = (& git rev-list -n 1 -- $Tag 2>$null | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $tagCommit -notmatch '^[0-9a-fA-F]{40}$') {
    throw "Unable to resolve release tag $Tag to a commit"
}
$headCommit = (& git rev-parse HEAD 2>$null | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $headCommit -notmatch '^[0-9a-fA-F]{40}$') {
    throw "Unable to resolve the checked-out commit"
}
if (-not [string]::Equals($tagCommit, $headCommit, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Release tag $Tag points to $tagCommit but the checkout is $headCommit"
}
if ($ExpectedCommit -and
    -not [string]::Equals($ExpectedCommit, $headCommit, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Workflow commit $ExpectedCommit does not match checked-out commit $headCommit"
}

$stampScript = Join-Path $PSScriptRoot "Stamp-Version.ps1"
$versionOutput = & $stampScript -DryRun -Target All
if (($versionOutput -join "`n") -notmatch 'Stamp-Version:\s+([0-9]+(?:\.[0-9]+){2,3})') {
    throw "Unable to determine the repository version for $Tag"
}
if (-not [string]::Equals($Matches[1], $expectedVersion, [StringComparison]::Ordinal)) {
    throw "Release tag version $expectedVersion does not match computed version $($Matches[1])"
}

Write-Output $expectedVersion
