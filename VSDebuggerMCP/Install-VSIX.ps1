param(
    [string]$VsixPath = "",
    [ValidateSet("all", "vs2022", "vs2026")]
    [string]$Target = "all",
    [switch]$AllowDowngrade,
    [string[]]$RollbackVsixPath = @(),
    [string[]]$RollbackVsixSha256 = @()
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.IO.Compression.FileSystem

$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if ($currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "AIDebugger VSIX installation is per-user and must not run from an elevated PowerShell process. Reopen a normal non-administrator terminal."
}

$MaxVsixPayloadEntryBytes = 256MB
$MaxVsixPayloadTotalBytes = 2GB
$MaxVsixPayloadEntries = 100000
$WindowsReservedNames = @("con", "prn", "aux", "nul") +
    @(1..9 | ForEach-Object { "com$_" }) +
    @(1..9 | ForEach-Object { "lpt$_" })

function Get-ExistingPathAttributes {
    param([string]$path)
    try { return [System.IO.File]::GetAttributes([System.IO.Path]::GetFullPath($path)) }
    catch [System.IO.FileNotFoundException] { return $null }
    catch [System.IO.DirectoryNotFoundException] { return $null }
}

function Assert-PlainDirectoryTree {
    param([string]$path)
    $current = [System.IO.Path]::GetFullPath($path).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    $root = [System.IO.Path]::GetPathRoot($current)
    while ($true) {
        $attributes = Get-ExistingPathAttributes -path $current
        if ($null -eq $attributes -or
            -not ($attributes -band [System.IO.FileAttributes]::Directory) -or
            ($attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
            throw "Directory is missing, not a directory, or a reparse point: $current"
        }
        if ([string]::Equals($current, $root, [StringComparison]::OrdinalIgnoreCase) -or
            [string]::Equals($current, $root.TrimEnd([System.IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) {
            return
        }
        $current = Split-Path -Parent $current
        if ([string]::IsNullOrWhiteSpace($current)) { throw "Directory escaped its filesystem root: $path" }
    }
}

function Ensure-PlainDirectoryTree {
    param([string]$path)
    $full = [System.IO.Path]::GetFullPath($path).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    $root = [System.IO.Path]::GetPathRoot($full)
    $pending = New-Object System.Collections.Generic.Stack[string]
    $current = $full
    while (-not [string]::Equals($current, $root, [StringComparison]::OrdinalIgnoreCase) -and
           -not [string]::Equals($current, $root.TrimEnd([System.IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) {
        $pending.Push((Split-Path -Leaf $current))
        $current = Split-Path -Parent $current
        if ([string]::IsNullOrWhiteSpace($current)) { throw "Directory escaped its filesystem root: $path" }
    }
    Assert-PlainDirectoryTree -path $root
    $current = $root
    while ($pending.Count -gt 0) {
        $current = [System.IO.Path]::GetFullPath((Join-Path $current $pending.Pop()))
        $attributes = Get-ExistingPathAttributes -path $current
        if ($null -eq $attributes) {
            [System.IO.Directory]::CreateDirectory($current) | Out-Null
            $attributes = Get-ExistingPathAttributes -path $current
        }
        if ($null -eq $attributes -or
            -not ($attributes -band [System.IO.FileAttributes]::Directory) -or
            ($attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
            throw "Directory is not a plain directory: $current"
        }
    }
    Assert-PlainDirectoryTree -path $full
    return $full
}

function Assert-SafeFinalEntry {
    param([string]$path, [switch]$RequireFile, [switch]$RequireDirectory)
    $full = [System.IO.Path]::GetFullPath($path)
    Assert-PlainDirectoryTree -path (Split-Path -Parent $full)
    $attributes = Get-ExistingPathAttributes -path $full
    if ($null -eq $attributes) {
        if ($RequireFile -or $RequireDirectory) { throw "Required path is missing: $full" }
        return
    }
    if ($attributes -band [System.IO.FileAttributes]::ReparsePoint) {
        throw "Path must not be a reparse point: $full"
    }
    if ($RequireFile -and ($attributes -band [System.IO.FileAttributes]::Directory)) {
        throw "Path must be a file: $full"
    }
    if ($RequireDirectory -and -not ($attributes -band [System.IO.FileAttributes]::Directory)) {
        throw "Path must be a directory: $full"
    }
}

function Assert-PlainOwnedTree {
    param([string]$path)
    Assert-SafeFinalEntry -path $path -RequireDirectory
    $pending = New-Object System.Collections.Generic.Stack[string]
    $pending.Push([System.IO.Path]::GetFullPath($path))
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        foreach ($entry in [System.IO.Directory]::EnumerateFileSystemEntries($current)) {
            $attributes = [System.IO.File]::GetAttributes($entry)
            if ($attributes -band [System.IO.FileAttributes]::ReparsePoint) {
                throw "Owned deployment tree contains a reparse point: $entry"
            }
            if ($attributes -band [System.IO.FileAttributes]::Directory) { $pending.Push($entry) }
        }
    }
}

function Remove-PlainOwnedTree {
    param([string]$path)
    $attributes = Get-ExistingPathAttributes -path $path
    if ($null -eq $attributes) { return }
    Assert-PlainOwnedTree -path $path
    Remove-Item -LiteralPath $path -Recurse -Force
}

function Get-WindowsCanonicalRelativePath {
    param([string]$relativeName)
    if ([string]::IsNullOrWhiteSpace($relativeName) -or $relativeName.Contains("\")) {
        throw "Archive path is empty or uses a backslash: $relativeName"
    }
    $canonical = @()
    foreach ($component in $relativeName.Split('/')) {
        if ([string]::IsNullOrWhiteSpace($component) -or $component -eq "." -or $component -eq ".." -or
            $component.EndsWith(".") -or $component.EndsWith(" ") -or
            $component -match '[\x00-\x1f<>:"/\\|?*]') {
            throw "Archive path has a Windows-unsafe component: $relativeName"
        }
        $stem = $component.Split('.')[0].ToLowerInvariant()
        if ($WindowsReservedNames -contains $stem) { throw "Archive path uses a reserved Windows name: $relativeName" }
        $canonical += $component.ToLowerInvariant()
    }
    return ($canonical -join '/')
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

$splitVs2022Path = Join-Path $scriptDir "bin\Release\VSDebuggerMCP.VS2022.vsix"
$splitVs2026Path = Join-Path $scriptDir "bin\Release\VSDebuggerMCP.VS2026.vsix"
$explicitVsixPath = ""

if (-not [string]::IsNullOrWhiteSpace($VsixPath)) {
    $explicitVsixPath = [System.IO.Path]::GetFullPath($VsixPath)
    if (-not (Test-Path $explicitVsixPath)) {
        throw "VSIX not found: $explicitVsixPath"
    }
}

if ([string]::IsNullOrWhiteSpace($explicitVsixPath)) {
    if ($Target -eq "vs2022" -and -not (Test-Path $splitVs2022Path)) {
        throw "VS2022 package not found: $splitVs2022Path"
    }

    if ($Target -eq "vs2026" -and -not (Test-Path $splitVs2026Path)) {
        throw "VS2026 package not found: $splitVs2026Path"
    }

    if ($Target -eq "all") {
        if (-not (Test-Path $splitVs2022Path)) {
            throw "VS2022 package not found: $splitVs2022Path"
        }

        if (-not (Test-Path $splitVs2026Path)) {
            throw "VS2026 package not found: $splitVs2026Path"
        }
    }
}

$deploymentLock = $null
$deploymentLockRoot = Ensure-PlainDirectoryTree -path (Join-Path $env:USERPROFILE ".ai-debugger")
$deploymentLockPath = Join-Path $deploymentLockRoot ".vs-deployment.lock"
Assert-SafeFinalEntry -path $deploymentLockPath
$lockDeadline = [DateTime]::UtcNow.AddSeconds(30)
while ($null -eq $deploymentLock) {
    try {
        $deploymentLock = [System.IO.File]::Open(
            $deploymentLockPath,
            [System.IO.FileMode]::OpenOrCreate,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::None)
    }
    catch [System.IO.IOException] {
        if ([DateTime]::UtcNow -ge $lockDeadline) {
            throw "Timed out waiting for another AIDebugger VSIX/runtime transaction"
        }
        Start-Sleep -Milliseconds 100
    }
}
$deploymentLock.SetLength(0)
$lockBytes = [System.Text.Encoding]::UTF8.GetBytes("pid=$PID utc=$([DateTime]::UtcNow.ToString('O'))")
$deploymentLock.Write($lockBytes, 0, $lockBytes.Length)
$deploymentLock.Flush($true)

try {
$vswherePath = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswherePath)) {
    throw "vswhere not found: $vswherePath"
}

$instancesJson = & $vswherePath -all -format json
$instances = @()
if (-not [string]::IsNullOrWhiteSpace($instancesJson)) {
    $instances = $instancesJson | ConvertFrom-Json
}

if (-not $instances) {
    throw "No Visual Studio instances were found."
}

$candidates = @($instances | Where-Object {
    $_.isComplete -and $_.isLaunchable -and $_.installationPath -and $_.instanceId -and $_.installationVersion
})

switch ($Target) {
    "vs2022" {
        $candidates = @($candidates | Where-Object { $_.installationVersion.StartsWith("17.") })
    }
    "vs2026" {
        $candidates = @($candidates | Where-Object { $_.installationVersion.StartsWith("18.") })
    }
    default {
        $candidates = @($candidates | Where-Object {
            $_.installationVersion.StartsWith("17.") -or $_.installationVersion.StartsWith("18.")
        })
    }
}

if (-not $candidates) {
    throw "No matching Visual Studio instances found for target '$Target'."
}

Write-Host "Target: $Target"
if (-not [string]::IsNullOrWhiteSpace($explicitVsixPath)) {
    Write-Host "VSIX: $explicitVsixPath"
}

function Get-PackageForInstance {
    param([object]$instance)

    if (-not [string]::IsNullOrWhiteSpace($explicitVsixPath)) {
        return $explicitVsixPath
    }

    if ($instance.installationVersion.StartsWith("17.")) {
        return $splitVs2022Path
    }

    if ($instance.installationVersion.StartsWith("18.")) {
        return $splitVs2026Path
    }

    throw "Unsupported Visual Studio version '$($instance.installationVersion)' for instance '$($instance.displayName)'"
}

foreach ($process in @(Get-Process devenv -ErrorAction SilentlyContinue)) {
    try {
        $processPath = [System.IO.Path]::GetFullPath($process.Path)
        foreach ($instance in $candidates) {
            $installationRoot = [System.IO.Path]::GetFullPath([string]$instance.installationPath).TrimEnd('\')
            if ($processPath.StartsWith($installationRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
                throw "Close Visual Studio instance '$($instance.displayName)' (PID $($process.Id)) before starting a transactional extension install"
            }
        }
    }
    catch [System.ComponentModel.Win32Exception] {
        throw "Unable to prove that target Visual Studio instances are closed"
    }
}

function Get-VsixPackageIdentity {
    param([string]$packagePath)
    $archive = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        $entry = $archive.GetEntry("extension.vsixmanifest")
        if ($null -eq $entry) { throw "VSIX is missing extension.vsixmanifest: $packagePath" }
        $stream = $entry.Open()
        try {
            $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8, $true, 4096, $true)
            try { [xml]$manifest = $reader.ReadToEnd() }
            finally { $reader.Dispose() }
        }
        finally { $stream.Dispose() }
        $identity = $manifest.PackageManifest.Metadata.Identity
        $version = [string]$identity.Version
        $id = [string]$identity.Id
        if ($version -notmatch '^\d+(?:\.\d+){2,3}$') {
            throw "VSIX has an invalid package version '$version': $packagePath"
        }
        if ([string]::IsNullOrWhiteSpace($id)) { throw "VSIX has no extension identity: $packagePath" }
        $targetRanges = @($manifest.PackageManifest.Installation.InstallationTarget | ForEach-Object { [string]$_.Version })
        if ($targetRanges.Count -eq 0 -or @($targetRanges | Where-Object { $_ -notmatch '^[\[\(]\d+(?:\.\d+){0,3},\d+(?:\.\d+){0,3}[\]\)]$' }).Count -gt 0) {
            throw "VSIX has no valid Visual Studio InstallationTarget version range: $packagePath"
        }
        return [PSCustomObject]@{
            Id = $id
            Version = [Version](Get-NormalizedBundleVersion -versionText $version)
            TargetRanges = $targetRanges
        }
    }
    finally { $archive.Dispose() }
}

function Test-VersionInVsixRange {
    param([Version]$version, [string]$range)
    if ($range -notmatch '^([\[\(])(\d+(?:\.\d+){0,3}),(\d+(?:\.\d+){0,3})([\]\)])$') { return $false }
    $minimum = [Version]$Matches[2]
    $maximum = [Version]$Matches[3]
    $minimumAccepted = if ($Matches[1] -eq '[') { $version -ge $minimum } else { $version -gt $minimum }
    $maximumAccepted = if ($Matches[4] -eq ']') { $version -le $maximum } else { $version -lt $maximum }
    return $minimumAccepted -and $maximumAccepted
}

function Test-VsixSupportsInstance {
    param([object]$identity, [object]$instance)
    $instanceVersion = [Version](Get-NormalizedBundleVersion -versionText ([string]$instance.installationVersion))
    return @($identity.TargetRanges | Where-Object { Test-VersionInVsixRange -version $instanceVersion -range $_ }).Count -gt 0
}

function Get-VsixPackageVersion {
    param([string]$packagePath)
    return (Get-VsixPackageIdentity -packagePath $packagePath).Version.ToString()
}

function Get-InstalledVsixInfo {
    param([object]$instance, [string]$extensionId)
    $roots = @()
    $profileRoot = Join-Path $env:LOCALAPPDATA "Microsoft\VisualStudio"
    if (Test-Path -LiteralPath $profileRoot -PathType Container) {
        $roots += @(Get-ChildItem -LiteralPath $profileRoot -Directory | Where-Object {
            $_.Name.EndsWith("_" + [string]$instance.instanceId, [StringComparison]::OrdinalIgnoreCase)
        } | ForEach-Object { Join-Path $_.FullName "Extensions" })
    }
    $roots += (Join-Path ([string]$instance.installationPath) "Common7\IDE\Extensions")
    $matches = @()
    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
        foreach ($manifestPath in @(Get-ChildItem -LiteralPath $root -Filter "extension.vsixmanifest" -File -Recurse -ErrorAction SilentlyContinue)) {
            if ($manifestPath.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { continue }
            try {
                [xml]$manifest = Get-Content -LiteralPath $manifestPath.FullName -Raw -Encoding UTF8
                $identity = $manifest.PackageManifest.Metadata.Identity
                if ([string]::Equals([string]$identity.Id, $extensionId, [StringComparison]::OrdinalIgnoreCase)) {
                    $extensionRoot = [System.IO.Path]::GetFullPath($manifestPath.Directory.FullName)
                    $matches += [PSCustomObject]@{
                        Version = [Version](Get-NormalizedBundleVersion -versionText ([string]$identity.Version))
                        ExtensionRoot = $extensionRoot
                        ManifestPath = [System.IO.Path]::GetFullPath($manifestPath.FullName)
                    }
                }
            }
            catch { }
        }
    }
    $vsregedit = Join-Path ([string]$instance.installationPath) "Common7\IDE\vsregedit.exe"
    Assert-SafeFinalEntry -path $vsregedit -RequireFile
    $registrationLines = @(& $vsregedit enum ([string]$instance.installationPath) HKCU "ExtensionManager\EnabledExtensions" 2>$null)
    $enabled = @()
    if ($LASTEXITCODE -eq 0) {
        foreach ($line in $registrationLines) {
            if ($line -match '^\s+(.+),(\d+(?:\.\d+){2,3})\s+string\s+(.+?)\s*$' -and
                [string]::Equals($Matches[1], $extensionId, [StringComparison]::OrdinalIgnoreCase)) {
                $enabled += [PSCustomObject]@{
                    Version = [Version](Get-NormalizedBundleVersion -versionText $Matches[2])
                    ExtensionRoot = [System.IO.Path]::GetFullPath($Matches[3].TrimEnd('\'))
                }
            }
        }
    }
    if ($enabled.Count -gt 1) {
        throw "Visual Studio reports multiple enabled registrations for extension '$extensionId'"
    }
    if ($enabled.Count -eq 1) {
        $registered = $enabled[0]
        $diskMatches = @($matches | Where-Object {
            $_.Version -eq $registered.Version -and
            [string]::Equals($_.ExtensionRoot, $registered.ExtensionRoot, [StringComparison]::OrdinalIgnoreCase)
        })
        if ($diskMatches.Count -ne 1) {
            throw "The enabled Visual Studio extension registration does not identify exactly one matching on-disk package"
        }
        $diskMatches[0] | Add-Member -NotePropertyName EnabledState -NotePropertyValue "enabled"
        return $diskMatches[0]
    }
    if ($matches.Count -eq 0) { return $null }
    if ($matches.Count -ne 1) {
        throw "Visual Studio has multiple unregistered/disabled copies of extension '$extensionId'; pre-install state is ambiguous"
    }
    $matches[0] | Add-Member -NotePropertyName EnabledState -NotePropertyValue "disabled"
    return $matches[0]
}

function Get-InstalledVsixVersion {
    param([object]$instance, [string]$extensionId)
    $info = Get-InstalledVsixInfo -instance $instance -extensionId $extensionId
    if ($null -eq $info) { return $null }
    return $info.Version
}

function Protect-InstallerStagingDirectory {
    param([string]$path)
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $security = New-Object System.Security.AccessControl.DirectorySecurity
    $security.SetOwner($identity.User)
    $security.SetAccessRuleProtection($true, $false)
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        $identity.User,
        [System.Security.AccessControl.FileSystemRights]::FullControl,
        [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [System.Security.AccessControl.InheritanceFlags]::ObjectInherit,
        [System.Security.AccessControl.PropagationFlags]::None,
        [System.Security.AccessControl.AccessControlType]::Allow)
    $security.AddAccessRule($rule)
    [System.IO.Directory]::SetAccessControl($path, $security)

    $principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
    if ($principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
        $icacls = Join-Path $env:SystemRoot "System32\icacls.exe"
        & $icacls $path /setintegritylevel '(OI)(CI)H' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Unable to apply a high-integrity label to rollback staging" }
    }
}

function Prepare-AuthorizedRollbackPackages {
    param([string[]]$paths, [string[]]$hashes)
    if ($paths.Count -ne $hashes.Count) {
        throw "RollbackVsixPath and RollbackVsixSha256 must contain the same number of entries"
    }
    $root = Ensure-PlainDirectoryTree -path (Join-Path ([System.IO.Path]::GetTempPath()) ("aidb-authorized-rollback-" + [Guid]::NewGuid().ToString("N")))
    Protect-InstallerStagingDirectory -path $root
    $packages = @()
    try {
        for ($index = 0; $index -lt $paths.Count; $index++) {
            $source = [System.IO.Path]::GetFullPath($paths[$index])
            $expectedHash = ([string]$hashes[$index]).ToLowerInvariant()
            if ($expectedHash -notmatch '^[0-9a-f]{64}$') { throw "Rollback SHA-256 must contain exactly 64 hexadecimal characters" }
            Assert-SafeFinalEntry -path $source -RequireFile
            if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expectedHash) {
                throw "Explicit rollback VSIX hash does not match: $source"
            }
            $destination = Join-Path $root ([Guid]::NewGuid().ToString("N") + ".vsix")
            Copy-Item -LiteralPath $source -Destination $destination
            Assert-SafeFinalEntry -path $destination -RequireFile
            if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expectedHash) {
                throw "Authorized rollback VSIX changed while staging"
            }
            $packageIdentity = Get-VsixPackageIdentity -packagePath $destination
            $packages += [PSCustomObject]@{
                Path = $destination
                Hash = $expectedHash
                Identity = $packageIdentity
            }
        }
        return [PSCustomObject]@{ Root = $root; Packages = $packages }
    }
    catch {
        Remove-PlainOwnedTree -path $root
        throw
    }
}

function Add-VsixSnapshotToStaging {
    param([string]$packagePath, [string]$stagingRoot)
    $source = [System.IO.Path]::GetFullPath($packagePath)
    Assert-SafeFinalEntry -path $source -RequireFile
    $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    $sourceIdentity = Get-VsixPackageIdentity -packagePath $source
    $destination = Join-Path $stagingRoot ([Guid]::NewGuid().ToString("N") + ".candidate.vsix")
    Copy-Item -LiteralPath $source -Destination $destination
    Assert-SafeFinalEntry -path $destination -RequireFile
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() -cne $sourceHash) {
        throw "Candidate VSIX changed while copying into protected staging"
    }
    $stagedIdentity = Get-VsixPackageIdentity -packagePath $destination
    if (-not [string]::Equals($sourceIdentity.Id, $stagedIdentity.Id, [StringComparison]::OrdinalIgnoreCase) -or
        $sourceIdentity.Version -ne $stagedIdentity.Version) {
        throw "Candidate VSIX identity changed while copying into protected staging"
    }
    return $destination
}

function Test-InstalledVsixMatchesPackage {
    param([object]$installedInfo, [string]$packagePath)
    if ($null -eq $installedInfo) { return $false }
    $installedRoot = [System.IO.Path]::GetFullPath([string]$installedInfo.ExtensionRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    Assert-PlainOwnedTree -path $installedRoot
    $archive = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        $expectedFiles = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) {
            $relative = $entry.FullName.Replace("\", "/")
            if ($relative.EndsWith("/") -or $relative -eq "[Content_Types].xml" -or
                $relative.StartsWith("_rels/") -or $relative.StartsWith("package/") -or
                $relative.EndsWith("signature.p7s", [StringComparison]::OrdinalIgnoreCase)) { continue }
            [void](Get-WindowsCanonicalRelativePath -relativeName $relative)
            [void]$expectedFiles.Add($relative)
            $installedPath = [System.IO.Path]::GetFullPath((Join-Path $installedRoot $relative.Replace("/", "\")))
            if (-not $installedPath.StartsWith($installedRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { return $false }
            if (-not (Test-Path -LiteralPath $installedPath -PathType Leaf)) { return $false }
            $entryStream = $entry.Open()
            $sha = [System.Security.Cryptography.SHA256]::Create()
            try { $expected = ([BitConverter]::ToString($sha.ComputeHash($entryStream))).Replace("-", "").ToLowerInvariant() }
            finally { $sha.Dispose(); $entryStream.Dispose() }
            if ((Get-FileHash -LiteralPath $installedPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expected) { return $false }
        }
        $actualFiles = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach ($file in Get-ChildItem -LiteralPath $installedRoot -File -Recurse -Force) {
            if ($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { return $false }
            $relative = $file.FullName.Substring($installedRoot.Length + 1).Replace("\", "/")
            [void]$actualFiles.Add($relative)
        }
        return $actualFiles.SetEquals($expectedFiles)
    }
    finally { $archive.Dispose() }
}

function ConvertTo-WindowsCommandLineArgument {
    param([string]$value)
    if ($null -eq $value) { return '""' }
    if ($value.Length -gt 0 -and $value -notmatch '[\s"]') { return $value }
    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append('"')
    $slashes = 0
    foreach ($character in $value.ToCharArray()) {
        if ($character -eq '\') { $slashes++; continue }
        if ($character -eq '"') {
            [void]$builder.Append(('\' * ($slashes * 2 + 1)))
            [void]$builder.Append('"')
        } else {
            if ($slashes -gt 0) { [void]$builder.Append(('\' * $slashes)) }
            [void]$builder.Append($character)
        }
        $slashes = 0
    }
    if ($slashes -gt 0) { [void]$builder.Append(('\' * ($slashes * 2))) }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Invoke-VsixInstaller {
    param([string]$installer, [string[]]$arguments)
    $argumentLine = (@($arguments | ForEach-Object { ConvertTo-WindowsCommandLineArgument -value ([string]$_) }) -join ' ')
    return Start-Process -FilePath $installer -ArgumentList $argumentLine -Wait -PassThru
}

function Set-VsixEnabledState {
    param([object]$instance, [string]$extensionId, [object]$installedInfo, [string]$enabledState)
    if ($null -eq $installedInfo) { throw "Cannot restore extension state without an installed package" }
    $vsregedit = Join-Path ([string]$instance.installationPath) "Common7\IDE\vsregedit.exe"
    $valueName = "$extensionId,$($installedInfo.Version)"
    if ($enabledState -eq "enabled") {
        $rootValue = [string]$installedInfo.ExtensionRoot
        if (-not $rootValue.EndsWith('\')) { $rootValue += '\' }
        & $vsregedit set ([string]$instance.installationPath) HKCU "ExtensionManager\EnabledExtensions" $valueName string $rootValue | Out-Null
    } else {
        & $vsregedit remove ([string]$instance.installationPath) HKCU "ExtensionManager\EnabledExtensions" $valueName | Out-Null
    }
    if ($LASTEXITCODE -ne 0 -and $enabledState -eq "enabled") {
        throw "Unable to restore enabled state for extension '$extensionId'"
    }
}

function Restore-VsixInstallPlans {
    param([object[]]$plans)
    $rollbackFailures = @()
    $reversePlans = @($plans)
    [Array]::Reverse($reversePlans)
    foreach ($plan in $reversePlans) {
        $instance = $plan.Instance
        $args = if ($null -ne $plan.InstalledInfo) {
            @("/quiet", "/force", "/instanceIds:$($instance.instanceId)", [string]$plan.RollbackPackage)
        } else {
            @("/quiet", "/instanceIds:$($instance.instanceId)", "/uninstall:$($plan.ExtensionId)")
        }
        try {
            $proc = Invoke-VsixInstaller -installer $plan.Installer -arguments $args
            if ($proc.ExitCode -ne 0) { throw "rollback installer exit code $($proc.ExitCode)" }
            $restored = Get-InstalledVsixInfo -instance $instance -extensionId $plan.ExtensionId
            if ($null -ne $plan.InstalledInfo) {
                Set-VsixEnabledState -instance $instance -extensionId $plan.ExtensionId -installedInfo $restored -enabledState $plan.InstalledInfo.EnabledState
                $restored = Get-InstalledVsixInfo -instance $instance -extensionId $plan.ExtensionId
            }
            if ($null -eq $plan.InstalledInfo) {
                if ($null -ne $restored) { throw "new extension remained installed after rollback" }
            } elseif ($null -eq $restored -or $restored.Version -ne $plan.InstalledInfo.Version) {
                throw "restored extension version did not match $($plan.InstalledInfo.Version)"
            } elseif (-not (Test-InstalledVsixMatchesPackage -installedInfo $restored -packagePath $plan.RollbackPackage)) {
                throw "restored extension payload did not match the explicitly authorized rollback VSIX"
            }
            Write-Host "Rolled back $($instance.displayName) to its pre-install extension state."
        }
        catch {
            $rollbackFailures += "$($instance.displayName): $($_.Exception.Message)"
        }
    }
    return $rollbackFailures
}

function Get-NormalizedBundleVersion {
    param([string]$versionText)
    $version = [Version]$versionText
    $build = if ($version.Build -lt 0) { 0 } else { $version.Build }
    $revision = if ($version.Revision -lt 0) { 0 } else { $version.Revision }
    return "$($version.Major).$($version.Minor).$build.$revision"
}

function Test-IsAllowedVsPayloadPath {
    param([string]$relativeName)
    $normalized = $relativeName.Replace("\", "/")
    $rootFiles = @(
        "bootstrap.py", "debug_method_manifest.json", "output_models.py", "requirements.txt",
        "tool_context.py", "tool_runtime.py", "vs_bridge.py", "vs_errors.py", "vs_session.py",
        "vs_mcp_app.py", "vs_tool_registry.py", "vs_trust.py", "vs_launcher.py", "vs_server.py"
    )
    if ($rootFiles -contains $normalized) { return $true }
    $toolModules = @(
        "tool_modules/__init__.py", "tool_modules/capability_tools.py",
        "tool_modules/vs_instance_tools.py", "tool_modules/snapshot_tools.py",
        "tool_modules/native_managed_tools.py", "tool_modules/control_tools.py",
        "tool_modules/breakpoint_tools.py", "tool_modules/memory_tools.py"
    )
    if ($toolModules -contains $normalized) { return $true }
    if ($normalized -match '^vendor/wheels/[^/]+\.whl$' -or $normalized -eq 'vendor/wheels/manifest.json') { return $true }
    return $false
}

function Prepare-McpServerFromPackage {
    param([string]$packagePath)

    Assert-PlainDirectoryTree -path $env:USERPROFILE
    $aidbRoot = Ensure-PlainDirectoryTree -path (Join-Path $env:USERPROFILE ".ai-debugger")
    $userMcpRoot = [System.IO.Path]::GetFullPath((Join-Path $aidbRoot "mcp_server_vs"))
    if (-not $userMcpRoot.StartsWith($aidbRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to deploy outside the AIDebugger user directory: $userMcpRoot"
    }

    $deploymentId = [Guid]::NewGuid().ToString("N")
    $stagingRoot = [System.IO.Path]::GetFullPath((Join-Path $aidbRoot ("mcp_server_vs.staging." + $deploymentId)))
    $backupRoot = [System.IO.Path]::GetFullPath((Join-Path $aidbRoot ("mcp_server_vs.backup." + $deploymentId)))
    $prepared = $false
    $packageVersion = Get-VsixPackageVersion -packagePath $packagePath

    try {
        Ensure-PlainDirectoryTree -path $stagingRoot | Out-Null

        $zip = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
        try {
            $payloadEntries = @($zip.Entries | Where-Object {
                $name = $_.FullName.Replace("\", "/")
                $name.StartsWith("mcp_server/") -and -not $name.EndsWith("/")
            })

            if ($payloadEntries.Count -eq 0) {
                throw "No mcp_server payload found in package: $packagePath"
            }

            if ($payloadEntries.Count -gt $MaxVsixPayloadEntries) {
                throw "VSIX MCP payload has too many entries"
            }
            $plan = @()
            $seenCanonical = @{}
            [long]$totalPayloadBytes = 0
            foreach ($entry in $payloadEntries) {
                if ($entry.FullName.Contains("\")) { throw "VSIX MCP payload path uses a backslash: $($entry.FullName)" }
                $normalizedName = $entry.FullName
                $relativeName = $normalizedName.Substring("mcp_server/".Length)
                if ([string]::IsNullOrWhiteSpace($relativeName) -or
                    $relativeName.StartsWith("/") -or
                    $relativeName.Split('/') -contains "..") {
                    throw "Unsafe MCP payload path in VSIX: $normalizedName"
                }
                if (-not (Test-IsAllowedVsPayloadPath -relativeName $relativeName)) {
                    throw "Unexpected file in VS MCP payload: $normalizedName"
                }
                $canonical = Get-WindowsCanonicalRelativePath -relativeName $relativeName
                if ($seenCanonical.ContainsKey($canonical)) {
                    throw "Duplicate or aliased VS MCP payload path: $normalizedName"
                }
                $seenCanonical[$canonical] = $true
                if ($entry.Length -lt 0 -or $entry.Length -gt $MaxVsixPayloadEntryBytes) {
                    throw "VS MCP payload entry exceeds its size budget: $normalizedName"
                }
                $totalPayloadBytes += $entry.Length
                if ($totalPayloadBytes -gt $MaxVsixPayloadTotalBytes) {
                    throw "VSIX MCP payload exceeds its total extraction budget"
                }

                $destFile = [System.IO.Path]::GetFullPath((Join-Path $stagingRoot $relativeName.Replace("/", "\")))
                if (-not $destFile.StartsWith($stagingRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "MCP payload escaped the staging directory: $normalizedName"
                }
                $plan += [PSCustomObject]@{ Entry = $entry; Destination = $destFile; RelativeName = $relativeName }
            }

            foreach ($item in $plan) {
                $entry = $item.Entry
                $destFile = $item.Destination
                $normalizedName = "mcp_server/" + $item.RelativeName
                $destDir = Ensure-PlainDirectoryTree -path (Split-Path -Parent $destFile)
                Assert-SafeFinalEntry -path $destFile
                $entryStream = $entry.Open()
                $fileStream = New-Object System.IO.FileStream($destFile, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
                try {
                    $buffer = New-Object byte[] 1048576
                    [long]$copied = 0
                    while (($read = $entryStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                        $copied += $read
                        if ($copied -gt $entry.Length -or $copied -gt $MaxVsixPayloadEntryBytes) {
                            throw "VS MCP payload entry expanded beyond its declared size: $normalizedName"
                        }
                        $fileStream.Write($buffer, 0, $read)
                    }
                    if ($copied -ne $entry.Length) { throw "VS MCP payload entry size mismatch: $normalizedName" }
                    $fileStream.Flush($true)
                }
                finally {
                    $fileStream.Dispose()
                    $entryStream.Dispose()
                }
                Assert-SafeFinalEntry -path $destFile -RequireFile
            }
        }
        finally {
            $zip.Dispose()
        }

        $extractedServerPath = Join-Path $stagingRoot "vs_server.py"
        if (-not (Test-Path $extractedServerPath)) {
            throw "Extracted VS payload is missing vs_server.py"
        }

        $manifestPath = Join-Path $stagingRoot "vendor\wheels\manifest.json"
        if (-not (Test-Path $manifestPath)) {
            throw "Extracted VS payload is missing the wheel manifest"
        }
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $manifestWheels = @($manifest.wheels)
        if ($manifestWheels.Count -eq 0) {
            throw "Wheel manifest must declare at least one wheel"
        }
        $declaredWheelNames = @{}
        foreach ($wheel in $manifestWheels) {
            if ([string]::IsNullOrWhiteSpace([string]$wheel.file) -or
                [string]::IsNullOrWhiteSpace([string]$wheel.sha256)) {
                throw "Wheel manifest entries require non-empty file and sha256 values"
            }
            $declaredWheelNames[([string]$wheel.file).Replace("\", "/")] = $true
            $wheelPath = [System.IO.Path]::GetFullPath((Join-Path $stagingRoot ([string]$wheel.file).Replace("/", "\")))
            if (-not $wheelPath.StartsWith($stagingRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
                -not (Test-Path -LiteralPath $wheelPath)) {
                throw "Wheel manifest references a missing or unsafe file: $($wheel.file)"
            }
            $actualHash = (Get-FileHash -LiteralPath $wheelPath -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actualHash -ne ([string]$wheel.sha256).ToLowerInvariant()) {
                throw "Wheel hash mismatch: $($wheel.file)"
            }
        }
        $stagedWheelRoot = Join-Path $stagingRoot "vendor\wheels"
        foreach ($wheelFile in @(Get-ChildItem -LiteralPath $stagedWheelRoot -Filter "*.whl" -File)) {
            $relativeWheel = "vendor/wheels/$($wheelFile.Name)"
            if (-not $declaredWheelNames.ContainsKey($relativeWheel)) {
                throw "Staged wheel is not declared in manifest: $relativeWheel"
            }
        }

        $prepared = $true
        Write-Host "Validated staged MCP server payload: $stagingRoot"
        return [PSCustomObject]@{
            StagingRoot = $stagingRoot
            UserMcpRoot = $userMcpRoot
            BackupRoot = $backupRoot
            PackagePath = $packagePath
            PackageVersion = $packageVersion
        }
    }
    catch {
        throw
    }
    finally {
        if (-not $prepared -and (Test-Path -LiteralPath $stagingRoot)) {
            Remove-PlainOwnedTree -path $stagingRoot
        }
    }
}

function Get-LauncherVersion {
    param([string]$path)
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return -1 }
    $content = Get-Content -LiteralPath $path -Raw -Encoding UTF8
    if ($content -match '(?m)^LAUNCHER_VERSION\s*=\s*(\d+)\s*$') { return [int]$Matches[1] }
    return -1
}

function Install-StableMcpLauncher {
    param([string]$versionedPayloadRoot, [switch]$AllowDowngrade)
    $source = Join-Path $versionedPayloadRoot "vs_launcher.py"
    Assert-SafeFinalEntry -path $source -RequireFile
    $bundleRoot = Split-Path -Parent $versionedPayloadRoot
    $target = Join-Path $bundleRoot "vs_server.py"
    Assert-SafeFinalEntry -path $target
    $sourceVersion = Get-LauncherVersion -path $source
    $targetVersion = Get-LauncherVersion -path $target
    if ($sourceVersion -lt 0) { throw "Bundled VS MCP launcher has no valid version marker" }
    $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    $targetHash = if (Test-Path -LiteralPath $target) { (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash } else { "" }
    if ($targetVersion -eq $sourceVersion -and $targetHash -eq $sourceHash) { return }
    if (-not $AllowDowngrade -and $targetVersion -gt $sourceVersion) {
        Write-Host "Preserved newer stable MCP launcher version $targetVersion."
        return
    }

    $temp = Join-Path $bundleRoot (".vs_server.launcher." + [Guid]::NewGuid().ToString("N") + ".tmp")
    try {
        Assert-SafeFinalEntry -path $temp
        [System.IO.File]::WriteAllText($temp, (Get-Content -LiteralPath $source -Raw -Encoding UTF8), (New-Object System.Text.UTF8Encoding($false)))
        Assert-SafeFinalEntry -path $temp -RequireFile
        if ((Get-LauncherVersion -path $temp) -ne $sourceVersion -or
            (Get-FileHash -LiteralPath $temp -Algorithm SHA256).Hash -ne $sourceHash) {
            throw "Staged VS MCP launcher failed validation"
        }
        Assert-SafeFinalEntry -path $target
        if (Test-Path -LiteralPath $target) { [System.IO.File]::Replace($temp, $target, $null) }
        else { [System.IO.File]::Move($temp, $target) }
    }
    finally {
        if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force }
    }
    Assert-SafeFinalEntry -path $target -RequireFile
    if ((Get-LauncherVersion -path $target) -ne $sourceVersion -or
        (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $sourceHash) {
        throw "Stable VS MCP launcher failed post-write validation"
    }
}

function Get-ValidatedBundleVersion {
    param([string]$versionedPayloadRoot)
    Assert-PlainOwnedTree -path $versionedPayloadRoot
    $bundleName = Split-Path -Leaf $versionedPayloadRoot
    if ($bundleName -notmatch '^[0-9_]+_[0-9a-f]{16}$') { throw "Invalid MCP bundle identity: $bundleName" }
    $versionPath = Join-Path $versionedPayloadRoot ".bundle-version"
    $signaturePath = Join-Path $versionedPayloadRoot ".bundle-signature"
    Assert-SafeFinalEntry -path $versionPath -RequireFile
    Assert-SafeFinalEntry -path $signaturePath -RequireFile
    $versionText = (Get-Content -LiteralPath $versionPath -Raw -Encoding UTF8).Trim()
    $signature = (Get-Content -LiteralPath $signaturePath -Raw -Encoding UTF8).Trim().ToLowerInvariant()
    $parsedVersion = $null
    if (-not [Version]::TryParse($versionText, [ref]$parsedVersion) -or $signature -notmatch '^[0-9a-f]{64}$') {
        throw "MCP bundle metadata is invalid: $bundleName"
    }
    $normalizedVersion = Get-NormalizedBundleVersion -versionText ($parsedVersion.ToString())
    $expectedName = $normalizedVersion.Replace('.', '_') + "_" + $signature.Substring(0, 16)
    if ($bundleName -cne $expectedName) { throw "MCP bundle identity does not match its metadata: $bundleName" }
    foreach ($file in @(Get-ChildItem -LiteralPath $versionedPayloadRoot -File -Recurse)) {
        $relative = $file.FullName.Substring($versionedPayloadRoot.Length).TrimStart("\").Replace("\", "/")
        if ($relative -in @(".bundle-version", ".bundle-signature")) { continue }
        if (-not (Test-IsAllowedVsPayloadPath -relativeName $relative)) {
            throw "Unexpected file in deployed VS MCP payload: $relative"
        }
    }
    if ((Get-CanonicalPayloadFingerprint -root $versionedPayloadRoot) -cne $signature) {
        throw "MCP bundle signature validation failed: $bundleName"
    }
    return $parsedVersion
}

function Set-ActiveMcpBundle {
    param([string]$versionedPayloadRoot, [switch]$AllowDowngrade)
    $bundleRoot = Split-Path -Parent $versionedPayloadRoot
    $bundleName = Split-Path -Leaf $versionedPayloadRoot
    $candidateVersion = Get-ValidatedBundleVersion -versionedPayloadRoot $versionedPayloadRoot
    $marker = Join-Path $bundleRoot ".active-bundle"
    Assert-SafeFinalEntry -path $marker
    if (Test-Path -LiteralPath $marker) {
        $activeName = (Get-Content -LiteralPath $marker -Raw -Encoding UTF8).Trim()
        if ($activeName -match '^[0-9_]+_[0-9a-f]{16}$') {
            $activeRoot = [System.IO.Path]::GetFullPath((Join-Path $bundleRoot $activeName))
            if ([string]::Equals((Split-Path -Parent $activeRoot), [System.IO.Path]::GetFullPath($bundleRoot), [StringComparison]::OrdinalIgnoreCase)) {
                try {
                    $activeVersion = Get-ValidatedBundleVersion -versionedPayloadRoot $activeRoot
                    if (-not $AllowDowngrade -and $candidateVersion -lt $activeVersion) {
                        Write-Host "Preserved newer active MCP payload $activeName; pass -AllowDowngrade to activate $bundleName."
                        return $activeRoot
                    }
                }
                catch {
                    Write-Warning "Existing active MCP marker is invalid; replacing it with the validated candidate."
                }
            }
        }
    }
    $temp = Join-Path $bundleRoot (".active-bundle." + [Guid]::NewGuid().ToString("N") + ".tmp")
    try {
        Assert-SafeFinalEntry -path $marker
        Assert-SafeFinalEntry -path $temp
        [System.IO.File]::WriteAllText($temp, $bundleName, (New-Object System.Text.UTF8Encoding($false)))
        Assert-SafeFinalEntry -path $temp -RequireFile
        Assert-SafeFinalEntry -path $marker
        if (Test-Path -LiteralPath $marker) { [System.IO.File]::Replace($temp, $marker, $null) }
        else { [System.IO.File]::Move($temp, $marker) }
    }
    finally {
        if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force }
    }
    Assert-SafeFinalEntry -path $marker -RequireFile
    if ((Get-Content -LiteralPath $marker -Raw -Encoding UTF8).Trim() -cne $bundleName) {
        throw "Active MCP bundle marker failed post-write validation"
    }
    return $versionedPayloadRoot
}

function Remove-StaleMcpBundles {
    param([string]$bundleRoot)
    $root = [System.IO.Path]::GetFullPath($bundleRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    $marker = Join-Path $root ".active-bundle"
    Assert-SafeFinalEntry -path $marker
    $activeName = if (Test-Path -LiteralPath $marker) { (Get-Content -LiteralPath $marker -Raw -Encoding UTF8).Trim() } else { "" }
    $inactive = @(Get-ChildItem -LiteralPath $root -Directory | Where-Object {
        $_.Name -match '^[0-9_]+_[0-9a-f]{16}$' -and
        $_.Name -ne $activeName -and
        -not ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint)
    } | Sort-Object LastWriteTimeUtc -Descending)
    $cutoff = [DateTime]::UtcNow.AddDays(-30)
    foreach ($directory in @($inactive | Select-Object -Skip 3)) {
        if ($directory.LastWriteTimeUtc -ge $cutoff) { continue }
        $full = [System.IO.Path]::GetFullPath($directory.FullName).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
        if ([string]::Equals((Split-Path -Parent $full), $root, [StringComparison]::OrdinalIgnoreCase)) {
            try { Remove-PlainOwnedTree -path $full }
            catch { }
        }
    }
}

function Activate-McpServerDeployment {
    param([object]$deployment, [switch]$AllowDowngrade)

    $stagingRoot = [string]$deployment.StagingRoot
    $userMcpRoot = [string]$deployment.UserMcpRoot
    $backupRoot = [string]$deployment.BackupRoot
    $bundleRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $userMcpRoot)).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    $activeMarker = Join-Path $bundleRoot ".active-bundle"
    $stableLauncher = Join-Path $bundleRoot "vs_server.py"
    Assert-SafeFinalEntry -path $activeMarker
    Assert-SafeFinalEntry -path $stableLauncher
    $previousMarker = if (Test-Path -LiteralPath $activeMarker) { [System.IO.File]::ReadAllBytes($activeMarker) } else { $null }
    $previousLauncher = if (Test-Path -LiteralPath $stableLauncher) { [System.IO.File]::ReadAllBytes($stableLauncher) } else { $null }
    $activated = $false
    try {
        Assert-PlainOwnedTree -path $stagingRoot
        Assert-SafeFinalEntry -path (Join-Path $stagingRoot "vs_server.py") -RequireFile
        Assert-PlainDirectoryTree -path $env:USERPROFILE
        $aidbRoot = [System.IO.Path]::GetFullPath((Join-Path $env:USERPROFILE ".ai-debugger")).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
        if (-not $bundleRoot.StartsWith($aidbRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to create an MCP bundle root outside the AIDebugger user directory: $bundleRoot"
        }
        Assert-PlainDirectoryTree -path $aidbRoot
        Ensure-PlainDirectoryTree -path $bundleRoot | Out-Null
        Assert-SafeFinalEntry -path $backupRoot
        if (Test-Path -LiteralPath $userMcpRoot) {
            Assert-PlainOwnedTree -path $userMcpRoot
            Move-Item -LiteralPath $userMcpRoot -Destination $backupRoot
            Assert-PlainOwnedTree -path $backupRoot
        }
        Move-Item -LiteralPath $stagingRoot -Destination $userMcpRoot
        Assert-PlainOwnedTree -path $userMcpRoot
        $activePayloadRoot = Set-ActiveMcpBundle -versionedPayloadRoot $userMcpRoot -AllowDowngrade:$AllowDowngrade
        Install-StableMcpLauncher -versionedPayloadRoot $activePayloadRoot -AllowDowngrade:$AllowDowngrade
        Remove-StaleMcpBundles -bundleRoot $bundleRoot
        $activated = $true
        Write-Host "Activated MCP server payload: $userMcpRoot"
    }
    catch {
        $activationFailure = $_
        $runtimeRollbackErrors = @()
        foreach ($restore in @(
            [PSCustomObject]@{ Path = $activeMarker; Bytes = $previousMarker },
            [PSCustomObject]@{ Path = $stableLauncher; Bytes = $previousLauncher }
        )) {
            try {
                Assert-SafeFinalEntry -path $restore.Path
                if ($null -eq $restore.Bytes) {
                    if (Test-Path -LiteralPath $restore.Path) { Remove-Item -LiteralPath $restore.Path -Force }
                } else {
                    $restoreTemp = $restore.Path + ".restore." + [Guid]::NewGuid().ToString("N") + ".tmp"
                    try {
                        Assert-SafeFinalEntry -path $restoreTemp
                        [System.IO.File]::WriteAllBytes($restoreTemp, $restore.Bytes)
                        Assert-SafeFinalEntry -path $restore.Path
                        if (Test-Path -LiteralPath $restore.Path) { [System.IO.File]::Replace($restoreTemp, $restore.Path, $null) }
                        else { [System.IO.File]::Move($restoreTemp, $restore.Path) }
                    }
                    finally {
                        if (Test-Path -LiteralPath $restoreTemp) { Remove-Item -LiteralPath $restoreTemp -Force }
                    }
                }
            }
            catch { $runtimeRollbackErrors += "$($restore.Path): $($_.Exception.Message)" }
        }
        try {
            if (-not $activated -and (Test-Path -LiteralPath $userMcpRoot)) {
                Remove-PlainOwnedTree -path $userMcpRoot
            }
        }
        catch { $runtimeRollbackErrors += "candidate payload: $($_.Exception.Message)" }
        try {
            if (-not (Test-Path -LiteralPath $userMcpRoot) -and (Test-Path -LiteralPath $backupRoot)) {
                Assert-PlainOwnedTree -path $backupRoot
                Move-Item -LiteralPath $backupRoot -Destination $userMcpRoot
            }
        }
        catch { $runtimeRollbackErrors += "backup payload: $($_.Exception.Message)" }
        if ($runtimeRollbackErrors.Count -gt 0) {
            throw "MCP runtime activation failed and runtime rollback incomplete. Activation: $($activationFailure.Exception.Message); rollback: $($runtimeRollbackErrors -join '; ')"
        }
        throw $activationFailure
    }
    finally {
        if (Test-Path -LiteralPath $stagingRoot) {
            Remove-PlainOwnedTree -path $stagingRoot
        }
        if ($activated -and (Test-Path -LiteralPath $backupRoot)) {
            Remove-PlainOwnedTree -path $backupRoot
        }
    }
}

function Remove-PreparedDeployments {
    param([object[]]$deployments)
    foreach ($deployment in @($deployments)) {
        if ($null -ne $deployment -and (Test-Path -LiteralPath $deployment.StagingRoot)) {
            Remove-PlainOwnedTree -path $deployment.StagingRoot
        }
    }
}

function Get-CanonicalPayloadFingerprint {
    param([string]$root)
    $records = @()
    foreach ($file in @(Get-ChildItem -LiteralPath $root -File -Recurse)) {
        $relative = $file.FullName.Substring($root.Length).TrimStart("\").Replace("\", "/")
        if ($relative.Split('/') -contains "__pycache__") { continue }
        if ($relative -in @(".bundle-version", ".bundle-signature")) { continue }
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $records += "$relative`:$hash"
    }
    $orderedRecords = [string[]]$records
    [Array]::Sort($orderedRecords, [StringComparer]::Ordinal)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes(($orderedRecords -join "`n"))
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Get-PreparedPayloadFingerprint {
    param([object]$deployment)
    return Get-CanonicalPayloadFingerprint -root ([string]$deployment.StagingRoot)
}

$signatureFixture = Join-Path (Split-Path -Parent $scriptDir) "tests\fixtures\VsBundleSignature"
if ((Get-CanonicalPayloadFingerprint -root $signatureFixture) -ne "3fd3d920468408e15d6356ba7cd450c20903e500e1385b431ecac5223db6b79e") {
    throw "PowerShell VS bundle signature algorithm does not match the shared cross-language fixture"
}

$packageSet = @{}
foreach ($instance in $candidates) {
    $packagePath = Get-PackageForInstance -instance $instance
    if (-not (Test-Path $packagePath)) {
        throw "VSIX not found: $packagePath"
    }
    $packageSet[$packagePath] = $true
}

$failures = @()
$installPlans = @()
$authorizedRollback = Prepare-AuthorizedRollbackPackages -paths $RollbackVsixPath -hashes $RollbackVsixSha256
$candidateSnapshots = @{}
$stagedPackageSet = @{}
try {
    foreach ($originalPackage in @($packageSet.Keys)) {
        $originalFullPath = [System.IO.Path]::GetFullPath($originalPackage)
        $snapshot = Add-VsixSnapshotToStaging -packagePath $originalFullPath -stagingRoot $authorizedRollback.Root
        $candidateSnapshots[$originalFullPath] = $snapshot
        $stagedPackageSet[$snapshot] = $true
    }
    $packageSet = $stagedPackageSet
}
catch {
    Remove-PlainOwnedTree -path $authorizedRollback.Root
    throw
}
foreach ($instance in $candidates) {
    $installer = Join-Path $instance.installationPath "Common7\IDE\VSIXInstaller.exe"
    if (-not (Test-Path $installer)) {
        $failures += "${instance.displayName}: VSIXInstaller.exe not found"
        continue
    }

    $originalPackageForInstance = [System.IO.Path]::GetFullPath((Get-PackageForInstance -instance $instance))
    $packageForInstance = $candidateSnapshots[$originalPackageForInstance]
    if ([string]::IsNullOrWhiteSpace($packageForInstance) -or -not (Test-Path $packageForInstance)) {
        $failures += "$($instance.displayName): protected VSIX snapshot not found ($originalPackageForInstance)"
        continue
    }

    $candidateIdentity = Get-VsixPackageIdentity -packagePath $packageForInstance
    if (-not (Test-VsixSupportsInstance -identity $candidateIdentity -instance $instance)) {
        $failures += "$($instance.displayName): candidate VSIX does not target Visual Studio $($instance.installationVersion)"
        continue
    }
    $installedInfo = Get-InstalledVsixInfo -instance $instance -extensionId $candidateIdentity.Id
    $installedVersion = if ($null -eq $installedInfo) { $null } else { $installedInfo.Version }
    if (-not $AllowDowngrade -and $null -ne $installedVersion -and $candidateIdentity.Version -lt $installedVersion) {
        $failures += "$($instance.displayName): refusing VSIX downgrade from $installedVersion to $($candidateIdentity.Version); pass -AllowDowngrade to downgrade extension and runtime together"
        continue
    }

    try {
        $rollbackPackage = $null
        if ($null -ne $installedInfo) {
            $rollbackMatches = @($authorizedRollback.Packages | Where-Object {
                [string]::Equals($_.Identity.Id, $candidateIdentity.Id, [StringComparison]::OrdinalIgnoreCase) -and
                $_.Identity.Version -eq $installedInfo.Version -and
                (Test-VsixSupportsInstance -identity $_.Identity -instance $instance)
            })
            if ($rollbackMatches.Count -ne 1) {
                throw "expected exactly one rollback VSIX compatible with this Visual Studio instance, found $($rollbackMatches.Count)"
            }
            $rollbackPackage = [string]$rollbackMatches[0].Path
            if (-not (Test-InstalledVsixMatchesPackage -installedInfo $installedInfo -packagePath $rollbackPackage)) {
                throw "the authorized rollback VSIX does not exactly match the registered pre-install extension files"
            }
        }
        if ($null -ne $installedInfo -and [string]::IsNullOrEmpty($rollbackPackage)) {
            throw "the exact original VSIX for installed version $($installedInfo.Version) was not explicitly authorized. Supply -RollbackVsixPath and its independently recorded -RollbackVsixSha256, or uninstall the old extension before this transactional install"
        }
        $installPlans += [PSCustomObject]@{
            Instance = $instance
            Installer = $installer
            Package = $packageForInstance
            ExtensionId = $candidateIdentity.Id
            CandidateVersion = $candidateIdentity.Version
            InstalledVersion = $installedVersion
            InstalledInfo = $installedInfo
            RollbackPackage = $rollbackPackage
        }
    }
    catch {
        $failures += "$($instance.displayName): could not prepare rollback state: $($_.Exception.Message)"
    }
}

# Do not mutate either the VS installation or the shared Python payload unless
# every target has passed preflight. This prevents a deterministic missing-tool
# failure from producing a mixed deployment.
if ($failures.Count -gt 0) {
    Remove-PlainOwnedTree -path $authorizedRollback.Root
    Write-Error ("Install preflight failed for: " + ($failures -join "; "))
    exit 1
}

# Phase 1: fully extract, hash-check, and retain a staged payload before any VS
# instance is changed. Split VS2022/VS2026 packages must carry identical runtime
# payloads because all installed instances share one MCP entrypoint.
$preparedDeployments = @()
try {
    foreach ($packagePath in $packageSet.Keys) {
        $preparedDeployments += Prepare-McpServerFromPackage -packagePath $packagePath
    }
    $payloadFingerprints = @($preparedDeployments | ForEach-Object {
        Get-PreparedPayloadFingerprint -deployment $_
    } | Select-Object -Unique)
    if ($payloadFingerprints.Count -ne 1) {
        throw "VSIX packages contain different MCP payloads; refusing a mixed shared deployment"
    }
    $packageVersions = @($preparedDeployments | ForEach-Object { [string]$_.PackageVersion } | Select-Object -Unique)
    if ($packageVersions.Count -ne 1) {
        throw "VSIX packages have different versions; refusing a mixed shared deployment"
    }
    $bundleVersion = Get-NormalizedBundleVersion -versionText $packageVersions[0]
    $bundleIdentity = $bundleVersion.Replace('.', '_') + "_" + $payloadFingerprints[0].Substring(0, 16)
    $versionedPayloadRoot = [System.IO.Path]::GetFullPath((Join-Path $env:USERPROFILE ".ai-debugger\mcp_server_vs\$bundleIdentity"))
    $existingBundleRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $versionedPayloadRoot))
    $existingMarker = Join-Path $existingBundleRoot ".active-bundle"
    if (Test-Path -LiteralPath $existingBundleRoot -PathType Container) {
        Assert-SafeFinalEntry -path $existingMarker
    }
    if (Test-Path -LiteralPath $existingMarker -PathType Leaf) {
        $existingName = (Get-Content -LiteralPath $existingMarker -Raw -Encoding UTF8).Trim()
        if ($existingName -notmatch '^[0-9_]+_[0-9a-f]{16}$') {
            throw "Existing active MCP bundle marker is invalid"
        }
        $existingRoot = [System.IO.Path]::GetFullPath((Join-Path $existingBundleRoot $existingName))
        if (-not [string]::Equals((Split-Path -Parent $existingRoot), $existingBundleRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Existing active MCP bundle marker escaped its bundle root"
        }
        $existingVersion = Get-ValidatedBundleVersion -versionedPayloadRoot $existingRoot
        $candidateBundleVersion = [Version]$bundleVersion
        if (-not $AllowDowngrade -and $candidateBundleVersion -lt $existingVersion) {
            throw "Refusing to install VSIX $candidateBundleVersion while a newer MCP runtime $existingVersion is active; pass -AllowDowngrade to downgrade extension and runtime atomically"
        }
    }
    foreach ($deployment in $preparedDeployments) {
        [System.IO.File]::WriteAllText((Join-Path $deployment.StagingRoot ".bundle-version"), $bundleVersion, (New-Object System.Text.UTF8Encoding($false)))
        [System.IO.File]::WriteAllText((Join-Path $deployment.StagingRoot ".bundle-signature"), $payloadFingerprints[0], (New-Object System.Text.UTF8Encoding($false)))
        $deployment.UserMcpRoot = $versionedPayloadRoot
        $deployment.BackupRoot = $versionedPayloadRoot + ".backup." + [Guid]::NewGuid().ToString("N")
    }
}
catch {
    $preflightError = $_
    try { Remove-PreparedDeployments -deployments $preparedDeployments }
    catch { Write-Warning "Could not remove prepared deployment after preflight failure: $($_.Exception.Message)" }
    try { Remove-PlainOwnedTree -path $authorizedRollback.Root }
    catch { Write-Warning "Could not remove authorized rollback staging after preflight failure: $($_.Exception.Message)" }
    throw $preflightError
}

$attemptedPlans = @()
try {
    foreach ($plan in $installPlans) {
        $instance = $plan.Instance
        $installer = $plan.Installer
        $packageForInstance = $plan.Package

        Write-Host "Installing to $($instance.displayName) ($($instance.instanceId))"
        Write-Host "  Package: $packageForInstance"

        $args = @(
            "/quiet",
            "/force",
            "/instanceIds:$($instance.instanceId)",
            $packageForInstance
        )

        $attemptedPlans += $plan
        $proc = Invoke-VsixInstaller -installer $installer -arguments $args
        if ($proc.ExitCode -ne 0) {
            $failures += "$($instance.displayName): installer exit code $($proc.ExitCode)"
            break
        }
        $installedCandidate = Get-InstalledVsixInfo -instance $instance -extensionId $plan.ExtensionId
        if ($null -eq $installedCandidate -or $installedCandidate.Version -ne $plan.CandidateVersion -or
            -not (Test-InstalledVsixMatchesPackage -installedInfo $installedCandidate -packagePath $plan.Package)) {
            $failures += "$($instance.displayName): VSIXInstaller did not produce the exact candidate package state"
            break
        }
        if ($null -ne $plan.InstalledInfo) {
            Set-VsixEnabledState -instance $instance -extensionId $plan.ExtensionId -installedInfo $installedCandidate -enabledState $plan.InstalledInfo.EnabledState
            $verifiedState = Get-InstalledVsixInfo -instance $instance -extensionId $plan.ExtensionId
            if ($verifiedState.EnabledState -ne $plan.InstalledInfo.EnabledState) {
                $failures += "$($instance.displayName): extension enabled/disabled state could not be preserved"
                break
            }
        }
    }
}
catch {
    $failures += "VSIXInstaller launch or wait failed: $($_.Exception.Message)"
}

if ($failures.Count -gt 0) {
    $rollbackFailures = Restore-VsixInstallPlans -plans $attemptedPlans
    $cleanupFailures = @()
    try { Remove-PreparedDeployments -deployments $preparedDeployments }
    catch { $cleanupFailures += "prepared deployment cleanup: $($_.Exception.Message)" }
    try { Remove-PlainOwnedTree -path $authorizedRollback.Root }
    catch { $cleanupFailures += "authorized rollback cleanup: $($_.Exception.Message)" }
    if ($rollbackFailures.Count -gt 0 -or $cleanupFailures.Count -gt 0) {
        $repairDetails = @($rollbackFailures) + @($cleanupFailures)
        Write-Error ("VSIX installation failed and rollback or cleanup was incomplete. The shared MCP runtime was not changed. Manual repair is required: " + ($repairDetails -join "; "))
    } else {
        Write-Error ("VSIX installation failed; every attempted instance was restored and the shared MCP runtime was not changed: " + ($failures -join "; "))
    }
    exit 1
}

try {
    Activate-McpServerDeployment -deployment $preparedDeployments[0] -AllowDowngrade:$AllowDowngrade
}
catch {
    $activationError = $_.Exception.Message
    $rollbackFailures = Restore-VsixInstallPlans -plans $attemptedPlans
    $cleanupFailures = @()
    try { Remove-PreparedDeployments -deployments $preparedDeployments }
    catch { $cleanupFailures += "prepared deployment cleanup: $($_.Exception.Message)" }
    try { Remove-PlainOwnedTree -path $authorizedRollback.Root }
    catch { $cleanupFailures += "authorized rollback cleanup: $($_.Exception.Message)" }
    if ($rollbackFailures.Count -gt 0 -or $cleanupFailures.Count -gt 0 -or $activationError -like "*runtime rollback incomplete*") {
        $repairDetails = @($rollbackFailures) + @($cleanupFailures)
        Write-Error ("MCP runtime activation failed after VSIX installation and rollback or cleanup was incomplete. Manual repair is required. Activation: $activationError; details: " + ($repairDetails -join "; "))
    } else {
        Write-Error "MCP runtime activation failed; every VSIX instance was restored: $activationError"
    }
    exit 1
}

# Runtime activation is committed when Activate-McpServerDeployment returns.
# Cleanup after that point must never reinterpret a successful commit as a
# transaction failure or roll the already-committed VSIX state back.
$postCommitCleanupWarnings = @()
try { Remove-PreparedDeployments -deployments @($preparedDeployments | Select-Object -Skip 1) }
catch { $postCommitCleanupWarnings += "duplicate prepared deployment cleanup: $($_.Exception.Message)" }
try { Remove-PlainOwnedTree -path $authorizedRollback.Root }
catch { $postCommitCleanupWarnings += "authorized rollback cleanup: $($_.Exception.Message)" }
foreach ($cleanupWarning in $postCommitCleanupWarnings) {
    Write-Warning "Install committed, but $cleanupWarning"
}
Write-Host "Install completed for $($candidates.Count) instance(s)."
exit 0
}
finally {
    if ($null -ne $deploymentLock) { $deploymentLock.Dispose() }
}
