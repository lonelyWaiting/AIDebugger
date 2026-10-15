<# Publish the current repo VS Python payload using the built extension's owning
   transactional bundle installer. Does not rebuild/install VSIX or restart VS/MCP. #>
param(
    [string]$ExtensionDll = "",
    [string]$ReceiptPath = ""
)
$ErrorActionPreference = "Stop"
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if (!$ExtensionDll) { $ExtensionDll = Join-Path $repo "VSDebuggerMCP\bin\Release\VSDebuggerMCP.dll" }
$ExtensionDll = [IO.Path]::GetFullPath($ExtensionDll)
if (!(Test-Path -LiteralPath $ExtensionDll -PathType Leaf)) { throw "Build the extension once before publishing its Python bundle: $ExtensionDll" }
$stage = Join-Path $repo (".codex-tmp\vs-python-publish\" + [Guid]::NewGuid().ToString("N"))
[IO.Directory]::CreateDirectory($stage) | Out-Null
$stagedDll = Join-Path $stage "VSDebuggerMCP.dll"
Copy-Item -LiteralPath $ExtensionDll -Destination $stagedDll
$assembly = [Reflection.Assembly]::LoadFrom($stagedDll)
$registration = $assembly.GetType("VSDebuggerMCP.McpClientRegistration", $true)
$privateStatic = [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Static
$copy = $registration.GetMethod("CopyVsBundleFiles", $privateStatic)
if (!$copy) { throw "Built extension lacks the owning VS bundle copier" }
$source = Join-Path $repo "mcp_server"
$payload = Join-Path $stage "mcp_server"
$copy.Invoke($null, [object[]]@([string]$source, [string]$payload)) | Out-Null
$signatureMethod = $registration.GetMethod("ComputeDirectorySignature", $privateStatic)
$expectedSignature = $signatureMethod.Invoke($null, [object[]]@([string]$payload))
$bundleRoot = Join-Path $env:USERPROFILE ".ai-debugger\mcp_server_vs"
$marker = Join-Path $bundleRoot ".active-bundle"
$before = if (Test-Path -LiteralPath $marker) { [IO.File]::ReadAllText($marker).Trim() } else { $null }
$entry = $registration.GetMethod("GetMcpServerPath").Invoke($null, @())
$active = [IO.File]::ReadAllText($marker).Trim()
$activeDir = Join-Path $bundleRoot $active
$actualSignature = $signatureMethod.Invoke($null, [object[]]@([string]$activeDir))
if ($actualSignature -ne $expectedSignature) { throw "Publisher did not activate the requested payload: expected $expectedSignature, actual $actualSignature" }
$receipt = [ordered]@{
    success = $true
    source = $source
    extensionDll = $ExtensionDll
    extensionDllSha256 = (Get-FileHash -LiteralPath $ExtensionDll -Algorithm SHA256).Hash
    assemblyVersion = $assembly.GetName().Version.ToString()
    previousActiveBundle = $before
    activeBundle = $active
    bundleSignature = $actualSignature
    stableEntry = $entry
    stage = $stage
    restarts = 0
}
$json = $receipt | ConvertTo-Json -Depth 5
if ($ReceiptPath) {
    $ReceiptPath = [IO.Path]::GetFullPath($ReceiptPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($ReceiptPath)) | Out-Null
    [IO.File]::WriteAllText($ReceiptPath, $json, (New-Object Text.UTF8Encoding($false)))
}
$json
