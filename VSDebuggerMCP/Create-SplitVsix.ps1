param(
    [Parameter(Mandatory = $true)]
    [string]$BaseVsixPath,

    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.IO.Compression.FileSystem

function Resolve-FullPath {
    param([string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $Path))
}

$baseVsixFullPath = Resolve-FullPath -Path $BaseVsixPath
if (-not (Test-Path $baseVsixFullPath)) {
    throw "Base VSIX not found: $baseVsixFullPath"
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Split-Path -Parent $baseVsixFullPath
}

$outputDirectoryFullPath = Resolve-FullPath -Path $OutputDirectory
if (-not (Test-Path $outputDirectoryFullPath)) {
    New-Item -ItemType Directory -Path $outputDirectoryFullPath | Out-Null
}

$targets = @(
    "Microsoft.VisualStudio.Community",
    "Microsoft.VisualStudio.Pro",
    "Microsoft.VisualStudio.Enterprise"
)

function New-ZipFromDirectoryWithForwardSlashes {
    param(
        [string]$SourceDirectory,
        [string]$DestinationZip
    )

    if (Test-Path $DestinationZip) {
        Remove-Item -Path $DestinationZip -Force
    }

    $archive = [System.IO.Compression.ZipFile]::Open($DestinationZip, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $baseLength = $SourceDirectory.Length
        if (-not $SourceDirectory.EndsWith("\\") -and -not $SourceDirectory.EndsWith("/")) {
            $baseLength += 1
        }

        Get-ChildItem -Path $SourceDirectory -Recurse -File | ForEach-Object {
            $relativePath = $_.FullName.Substring($baseLength)
            $entryName = $relativePath.Replace("\", "/")

            $entry = $archive.CreateEntry($entryName, [System.IO.Compression.CompressionLevel]::Optimal)
            $entryStream = $entry.Open()
            $fileStream = [System.IO.File]::OpenRead($_.FullName)
            try {
                $fileStream.CopyTo($entryStream)
            }
            finally {
                $fileStream.Dispose()
                $entryStream.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

function New-SplitVsix {
    param(
        [string]$PackageSuffix,
        [string]$VersionRange
    )

    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("AIDebuggerVSIX_" + [Guid]::NewGuid().ToString("N"))
    $outputFile = Join-Path $outputDirectoryFullPath ("VSDebuggerMCP.{0}.vsix" -f $PackageSuffix)

    try {
        [System.IO.Compression.ZipFile]::ExtractToDirectory($baseVsixFullPath, $tempDir)

        $manifestPath = Join-Path $tempDir "extension.vsixmanifest"
        [xml]$manifest = Get-Content -Path $manifestPath

        $nsUri = "http://schemas.microsoft.com/developer/vsx-schema/2011"
        $nsManager = New-Object System.Xml.XmlNamespaceManager($manifest.NameTable)
        $nsManager.AddNamespace("vsx", $nsUri)

        $installation = $manifest.SelectSingleNode("/vsx:PackageManifest/vsx:Installation", $nsManager)
        if ($null -eq $installation) {
            throw "Installation node not found in manifest."
        }

        @($installation.SelectNodes("vsx:InstallationTarget", $nsManager)) | ForEach-Object {
            $installation.RemoveChild($_) | Out-Null
        }

        foreach ($id in $targets) {
            $targetNode = $manifest.CreateElement("InstallationTarget", $nsUri)
            $targetNode.SetAttribute("Id", $id)
            $targetNode.SetAttribute("Version", $VersionRange)

            $archNode = $manifest.CreateElement("ProductArchitecture", $nsUri)
            $archNode.InnerText = "amd64"
            $targetNode.AppendChild($archNode) | Out-Null

            $installation.AppendChild($targetNode) | Out-Null
        }

        $manifest.Save($manifestPath)

        New-ZipFromDirectoryWithForwardSlashes -SourceDirectory $tempDir -DestinationZip $outputFile

        Write-Host "Created: $outputFile"
    }
    finally {
        if (Test-Path $tempDir) {
            Remove-Item -Path $tempDir -Recurse -Force
        }
    }
}

New-SplitVsix -PackageSuffix "VS2022" -VersionRange "[17.0,18.0)"
New-SplitVsix -PackageSuffix "VS2026" -VersionRange "[18.0,20.0)"
