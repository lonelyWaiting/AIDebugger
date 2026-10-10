param(
    [string]$Configuration = "Release",
    [string]$ExpectedVersion,
    [switch]$RequireSignature
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if ($ExpectedVersion) {
    if ($ExpectedVersion -cnotmatch '^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)$') {
        throw "ExpectedVersion must be a canonical three-part numeric version"
    }
    $expectedVersion = $ExpectedVersion
}
else {
    $versionOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "Stamp-Version.ps1") -DryRun -Target VS
    if ($LASTEXITCODE -ne 0 -or ($versionOutput -join "`n") -notmatch 'Stamp-Version:\s+([0-9]+(?:\.[0-9]+){2,3})') {
        throw "Unable to determine the expected VSIX version"
    }
    $expectedVersion = $Matches[1]
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName WindowsBase
$packages = @(
    Join-Path $repoRoot "VSDebuggerMCP\bin\$Configuration\VSDebuggerMCP.VS2022.vsix"
    Join-Path $repoRoot "VSDebuggerMCP\bin\$Configuration\VSDebuggerMCP.VS2026.vsix"
)
$expectedTargets = @{
    "VSDebuggerMCP.VS2022.vsix" = "[17.0,18.0)"
    "VSDebuggerMCP.VS2026.vsix" = "[18.0,20.0)"
}
$expectedSkus = @("Microsoft.VisualStudio.Community", "Microsoft.VisualStudio.Enterprise", "Microsoft.VisualStudio.Pro")
$payloadFingerprints = @{}

function Read-ZipText($entry) {
    $stream = $entry.Open()
    try {
        $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8, $true, 4096, $true)
        try { return $reader.ReadToEnd() }
        finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Get-NormalizedFourPartVersion([string]$value) {
    $version = [Version]$value
    $build = if ($version.Build -lt 0) { 0 } else { $version.Build }
    $revision = if ($version.Revision -lt 0) { 0 } else { $version.Revision }
    return "$($version.Major).$($version.Minor).$build.$revision"
}

function Test-IsAllowedVsPayloadPath([string]$relativeName) {
    $normalized = $relativeName.Replace("\", "/")
    $rootFiles = @(
        "bootstrap.py", "debug_method_manifest.json", "output_models.py", "requirements.txt",
        "tool_context.py", "tool_runtime.py", "vs_bridge.py", "vs_errors.py", "vs_session.py",
        "vs_mcp_app.py", "vs_tool_registry.py", "vs_trust.py", "vs_launch.py", "vs_launcher.py",
        "vs_server.py"
    )
    if ($rootFiles -contains $normalized) { return $true }
    $toolModules = @(
        "tool_modules/__init__.py", "tool_modules/capability_tools.py",
        "tool_modules/vs_instance_tools.py", "tool_modules/snapshot_tools.py",
        "tool_modules/native_managed_tools.py", "tool_modules/control_tools.py",
        "tool_modules/breakpoint_tools.py", "tool_modules/memory_tools.py",
        "tool_modules/multi_vs_tools.py"
    )
    if ($toolModules -contains $normalized) { return $true }
    if ($normalized -match '^vendor/wheels/[^/]+\.whl$' -or $normalized -eq 'vendor/wheels/manifest.json') { return $true }
    return $false
}

function Get-CanonicalDirectoryFingerprint([string]$root) {
    $records = @()
    foreach ($file in @(Get-ChildItem -LiteralPath $root -File -Recurse)) {
        $relative = $file.FullName.Substring($root.Length).TrimStart("\").Replace("\", "/")
        if ($relative.Split('/') -contains "__pycache__") { continue }
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $records += "$relative`:$hash"
    }
    $ordered = [string[]]$records
    [Array]::Sort($ordered, [StringComparer]::Ordinal)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes(($ordered -join "`n"))))).Replace("-", "").ToLowerInvariant()
    }
    finally { $sha.Dispose() }
}

$signatureFixture = Join-Path $repoRoot "tests\fixtures\VsBundleSignature"
if ((Get-CanonicalDirectoryFingerprint $signatureFixture) -ne "3fd3d920468408e15d6356ba7cd450c20903e500e1385b431ecac5223db6b79e") {
    throw "VS bundle signature algorithm does not match the shared cross-language fixture"
}

function Assert-VsixSignature([string]$path) {
    $package = [System.IO.Packaging.Package]::Open(
        $path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read)
    try {
        $manager = New-Object System.IO.Packaging.PackageDigitalSignatureManager($package)
        if ($manager.Signatures.Count -eq 0) {
            throw "$path contains no OPC digital signature"
        }
        $verifyResult = $manager.VerifySignatures($false)
        if ($verifyResult -ne [System.IO.Packaging.VerifyResult]::Success) {
            throw "$path has an invalid OPC signature: $verifyResult"
        }
        foreach ($signature in $manager.Signatures) {
            $chainStatus = [System.IO.Packaging.PackageDigitalSignatureManager]::VerifyCertificate($signature.Signer)
            if ($chainStatus -ne [System.Security.Cryptography.X509Certificates.X509ChainStatusFlags]::NoError) {
                throw "$path signer certificate validation failed: $chainStatus"
            }
        }
    }
    finally {
        $package.Close()
    }
}

foreach ($package in $packages) {
    $fullPackage = [System.IO.Path]::GetFullPath($package)
    if (-not (Test-Path -LiteralPath $fullPackage -PathType Leaf)) {
        throw "Required VSIX is missing: $fullPackage"
    }

    # PackageDigitalSignatureManager opens the OPC package exclusively. Verify
    # the signature before ZipFile.OpenRead acquires its own handle.
    if ($RequireSignature) { Assert-VsixSignature $fullPackage }

    $archive = [System.IO.Compression.ZipFile]::OpenRead($fullPackage)
    try {
        $entries = @{}
        foreach ($entry in $archive.Entries) {
            if ($entries.ContainsKey($entry.FullName)) { throw "Duplicate VSIX entry: $($entry.FullName)" }
            $entries[$entry.FullName] = $entry
        }
        foreach ($required in @(
            "extension.vsixmanifest",
            "VSDebuggerMCP.dll",
            "ManagedInspectionHelper.exe",
            "Mono.Cecil.dll",
            "Iced.dll",
            "mcp_server/vs_server.py",
            "mcp_server/vs_launcher.py",
            "mcp_server/vs_bridge.py",
            "mcp_server/vs_errors.py",
            "mcp_server/vs_trust.py",
            "mcp_server/vs_launch.py",
            "mcp_server/debug_method_manifest.json",
            "mcp_server/vendor/wheels/manifest.json"
        )) {
            if (-not $entries.ContainsKey($required)) { throw "$fullPackage is missing $required" }
        }
        foreach ($forbidden in @(
            "mcp_server/server.py",
            "mcp_server/rider_bridge.py",
            "mcp_server/cursor_allowlist_sync.py"
        )) {
            if ($entries.ContainsKey($forbidden)) { throw "$fullPackage contains forbidden VS runtime file $forbidden" }
        }
        foreach ($entryName in @($entries.Keys)) {
            if ($entryName.StartsWith("mcp_server/") -and -not $entryName.EndsWith("/")) {
                $relativeName = $entryName.Substring("mcp_server/".Length)
                if (-not (Test-IsAllowedVsPayloadPath $relativeName)) {
                    throw "$fullPackage contains unexpected VS runtime file $entryName"
                }
            }
        }
        [xml]$vsixManifest = Read-ZipText $entries["extension.vsixmanifest"]
        $identity = $vsixManifest.PackageManifest.Metadata.Identity
        if ([string]$identity.Version -ne $expectedVersion) {
            throw "$fullPackage version $($identity.Version) does not match $expectedVersion"
        }
        $assemblyTemp = Join-Path ([System.IO.Path]::GetTempPath()) ("aidb-vsix-assembly-" + [Guid]::NewGuid().ToString("N") + ".dll")
        try {
            $input = $entries["VSDebuggerMCP.dll"].Open()
            try {
                $output = [System.IO.File]::Create($assemblyTemp)
                try { $input.CopyTo($output) }
                finally { $output.Dispose() }
            }
            finally { $input.Dispose() }
            $assemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($assemblyTemp).Version.ToString()
        }
        finally {
            if (Test-Path -LiteralPath $assemblyTemp) { Remove-Item -LiteralPath $assemblyTemp -Force }
        }
        $expectedAssemblyVersion = Get-NormalizedFourPartVersion ([string]$identity.Version)
        if ($assemblyVersion -ne $expectedAssemblyVersion) {
            throw "$fullPackage assembly version $assemblyVersion does not match manifest bundle version $expectedAssemblyVersion"
        }
        if ([string]$vsixManifest.PackageManifest.Metadata.MoreInfo -ne "https://github.com/lonelyWaiting/AIDebugger") {
            throw "$fullPackage has an invalid MoreInfo repository URL"
        }

        $namespace = New-Object System.Xml.XmlNamespaceManager($vsixManifest.NameTable)
        $namespace.AddNamespace("vsx", "http://schemas.microsoft.com/developer/vsx-schema/2011")
        $targets = @($vsixManifest.SelectNodes("/vsx:PackageManifest/vsx:Installation/vsx:InstallationTarget", $namespace))
        $expectedRange = $expectedTargets[[System.IO.Path]::GetFileName($fullPackage)]
        if ([string]::IsNullOrWhiteSpace($expectedRange) -or $targets.Count -ne 3) {
            throw "$fullPackage has an invalid InstallationTarget set"
        }
        $actualSkus = @()
        foreach ($target in $targets) {
            $actualSkus += [string]$target.Id
            if ([string]$target.Version -ne $expectedRange) {
                throw "$fullPackage target $($target.Id) has range $($target.Version), expected $expectedRange"
            }
            $architectures = @($target.SelectNodes("vsx:ProductArchitecture", $namespace))
            if ($architectures.Count -ne 1 -or [string]$architectures[0].InnerText -ne "amd64") {
                throw "$fullPackage target $($target.Id) must declare exactly ProductArchitecture=amd64"
            }
        }
        if ((($actualSkus | Sort-Object) -join "|") -ne (($expectedSkus | Sort-Object) -join "|")) {
            throw "$fullPackage has unexpected Visual Studio SKU targets: $($actualSkus -join ', ')"
        }

        $wheelManifest = (Read-ZipText $entries["mcp_server/vendor/wheels/manifest.json"]) | ConvertFrom-Json
        if ($wheelManifest.platform -ne "win_amd64") { throw "$fullPackage has an invalid wheel platform" }
        $declaredWheels = @{}
        foreach ($wheel in @($wheelManifest.wheels)) {
            $entryName = "mcp_server/" + ([string]$wheel.file).Replace('\', '/')
            if ($entryName -notmatch '^mcp_server/vendor/wheels/[^/]+\.whl$' -or $declaredWheels.ContainsKey($entryName)) {
                throw "$fullPackage has an invalid or duplicate wheel declaration: $entryName"
            }
            $declaredWheels[$entryName] = $true
            if (-not $entries.ContainsKey($entryName)) { throw "$fullPackage is missing declared wheel $entryName" }
            $stream = $entries[$entryName].Open()
            try {
                $sha256 = [System.Security.Cryptography.SHA256]::Create()
                try {
                    $actual = ([BitConverter]::ToString($sha256.ComputeHash($stream))).Replace("-", "").ToLowerInvariant()
                }
                finally { $sha256.Dispose() }
            }
            finally { $stream.Dispose() }
            if ($actual -ne ([string]$wheel.sha256).ToLowerInvariant()) {
                throw "$fullPackage contains a wheel hash mismatch: $entryName"
            }
        }
        $packagedWheels = @($entries.Keys | Where-Object { $_ -match '^mcp_server/vendor/wheels/[^/]+\.whl$' })
        if ($packagedWheels.Count -ne $declaredWheels.Count) {
            throw "$fullPackage wheel manifest does not exactly match packaged wheels"
        }
        foreach ($entryName in $packagedWheels) {
            if (-not $declaredWheels.ContainsKey($entryName)) { throw "$fullPackage contains undeclared wheel $entryName" }
        }

        $payloadRecords = @()
        foreach ($entryName in @($entries.Keys | Sort-Object)) {
            if ($entryName -eq "extension.vsixmanifest" -or
                $entryName -eq "[Content_Types].xml" -or
                $entryName -eq "_rels/.rels" -or
                $entryName.StartsWith("_xmlsignatures/")) {
                continue
            }
            $stream = $entries[$entryName].Open()
            try {
                $sha256 = [System.Security.Cryptography.SHA256]::Create()
                try { $entryHash = ([BitConverter]::ToString($sha256.ComputeHash($stream))).Replace("-", "").ToLowerInvariant() }
                finally { $sha256.Dispose() }
            }
            finally { $stream.Dispose() }
            $payloadRecords += "$entryName`:$entryHash"
        }
        $payloadBytes = [System.Text.Encoding]::UTF8.GetBytes(($payloadRecords -join "`n"))
        $payloadSha = [System.Security.Cryptography.SHA256]::Create()
        try { $payloadFingerprints[[System.IO.Path]::GetFileName($fullPackage)] = ([BitConverter]::ToString($payloadSha.ComputeHash($payloadBytes))).Replace("-", "").ToLowerInvariant() }
        finally { $payloadSha.Dispose() }
    }
    finally {
        $archive.Dispose()
    }
    Write-Host "Verified $fullPackage (version $expectedVersion)."
}

if (($payloadFingerprints.Values | Select-Object -Unique).Count -ne 1) {
    throw "VS2022 and VS2026 packages contain different non-manifest payloads"
}
