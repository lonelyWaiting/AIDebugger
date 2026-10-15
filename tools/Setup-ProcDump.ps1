param([string]$DependencyRoot = "")
$ErrorActionPreference = "Stop"
if (!$DependencyRoot) { $DependencyRoot = Join-Path $PSScriptRoot "..\.codex\dependencies\procdump\12.01" }
$DependencyRoot = [IO.Path]::GetFullPath($DependencyRoot)
[IO.Directory]::CreateDirectory($DependencyRoot) | Out-Null
$url = "https://download.sysinternals.com/files/Procdump.zip"
$zipPath = Join-Path $DependencyRoot "Procdump.zip"
Invoke-WebRequest -Uri $url -OutFile $zipPath
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    foreach ($entry in $archive.Entries) {
        if ($entry.FullName -notin @("procdump.exe", "procdump64.exe", "procdump64a.exe", "Eula.txt")) {
            throw "Unexpected official archive entry: $($entry.FullName)"
        }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $DependencyRoot $entry.FullName), $true)
    }
} finally { $archive.Dispose() }
$exe = Join-Path $DependencyRoot "procdump64.exe"
$signature = Get-AuthenticodeSignature -LiteralPath $exe
if ($signature.Status -ne "Valid" -or $signature.SignerCertificate.Subject -notmatch "O=Microsoft Corporation") {
    throw "ProcDump Microsoft Authenticode verification failed: $($signature.Status) $($signature.SignerCertificate.Subject)"
}
$manifest = [ordered]@{
    sourceUrl = $url
    documentationUrl = "https://learn.microsoft.com/en-us/sysinternals/downloads/procdump"
    zipSha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
    executable = $exe
    executableSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion
    signatureStatus = $signature.Status.ToString()
    signer = $signature.SignerCertificate.Subject
    signerThumbprint = $signature.SignerCertificate.Thumbprint
    globalDebuggerRegistration = $false
}
$json = $manifest | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText((Join-Path $DependencyRoot "manifest.json"), $json, (New-Object Text.UTF8Encoding($false)))
$json
