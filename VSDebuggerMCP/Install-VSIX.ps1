param(
    [string]$VsixPath = "",
    [ValidateSet("all", "vs2022", "vs2026")]
    [string]$Target = "all"
)

$ErrorActionPreference = "Stop"

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

$failures = @()
foreach ($instance in $candidates) {
    $installer = Join-Path $instance.installationPath "Common7\IDE\VSIXInstaller.exe"
    if (-not (Test-Path $installer)) {
        $failures += "${instance.displayName}: VSIXInstaller.exe not found"
        continue
    }

    $packageForInstance = Get-PackageForInstance -instance $instance
    if (-not (Test-Path $packageForInstance)) {
        $failures += "$($instance.displayName): VSIX not found ($packageForInstance)"
        continue
    }

    Write-Host "Installing to $($instance.displayName) ($($instance.instanceId))"
    Write-Host "  Package: $packageForInstance"

    $args = @(
        "/quiet",
        "/force",
        "/instanceIds:$($instance.instanceId)",
        $packageForInstance
    )

    $proc = Start-Process -FilePath $installer -ArgumentList $args -Wait -PassThru
    if ($proc.ExitCode -ne 0) {
        $failures += "$($instance.displayName): installer exit code $($proc.ExitCode)"
    }
}

if ($failures.Count -gt 0) {
    Write-Error ("Install failed for: " + ($failures -join "; "))
    exit 1
}

Write-Host "Install completed for $($candidates.Count) instance(s)."
exit 0
