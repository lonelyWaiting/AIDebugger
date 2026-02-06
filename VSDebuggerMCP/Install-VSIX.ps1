param(
    [string]$VsixPath = "",
    [ValidateSet("all", "vs2022", "vs2026")]
    [string]$Target = "all"
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.IO.Compression.FileSystem

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

function Sync-McpServerFromPackage {
    param([string]$packagePath)

    $userMcpRoot = Join-Path $env:USERPROFILE ".ai-debugger\mcp_server"
    $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("AIDebuggerMcpPayload_" + [Guid]::NewGuid().ToString("N"))

    try {
        New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

        $zip = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
        try {
            $payloadEntries = @($zip.Entries | Where-Object {
                $name = $_.FullName.Replace("\", "/")
                $name.StartsWith("mcp_server/") -and -not $name.EndsWith("/")
            })

            if ($payloadEntries.Count -eq 0) {
                throw "No mcp_server payload found in package: $packagePath"
            }

            foreach ($entry in $payloadEntries) {
                $normalizedName = $entry.FullName.Replace("\", "/")
                $relativeName = $normalizedName.Substring("mcp_server/".Length).Replace("/", "\")
                $destFile = Join-Path $tempRoot (Join-Path "mcp_server" $relativeName)
                $destDir = Split-Path -Parent $destFile

                if (-not (Test-Path $destDir)) {
                    New-Item -ItemType Directory -Path $destDir -Force | Out-Null
                }

                $entryStream = $entry.Open()
                $fileStream = [System.IO.File]::Create($destFile)
                try {
                    $entryStream.CopyTo($fileStream)
                }
                finally {
                    $fileStream.Dispose()
                    $entryStream.Dispose()
                }
            }
        }
        finally {
            $zip.Dispose()
        }

        $extractedServerPath = Join-Path $tempRoot "mcp_server\server.py"
        if (-not (Test-Path $extractedServerPath)) {
            throw "Extracted payload is missing server.py"
        }

        if (Test-Path $userMcpRoot) {
            Remove-Item -Path $userMcpRoot -Recurse -Force
        }

        New-Item -ItemType Directory -Path (Split-Path -Parent $userMcpRoot) -Force | Out-Null
        Copy-Item -Path (Join-Path $tempRoot "mcp_server") -Destination $userMcpRoot -Recurse -Force

        Write-Host "Seeded MCP server payload: $userMcpRoot"
    }
    finally {
        if (Test-Path $tempRoot) {
            Remove-Item -Path $tempRoot -Recurse -Force
        }
    }
}

$packageSet = @{}
foreach ($instance in $candidates) {
    $packagePath = Get-PackageForInstance -instance $instance
    if (-not (Test-Path $packagePath)) {
        throw "VSIX not found: $packagePath"
    }
    $packageSet[$packagePath] = $true
}

foreach ($packagePath in $packageSet.Keys) {
    Sync-McpServerFromPackage -packagePath $packagePath
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
