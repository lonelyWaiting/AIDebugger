param(
    [string]$RootSuffix = "AIDebuggerCI",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
$installPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
if ([string]::IsNullOrWhiteSpace($installPath)) { throw "Visual Studio with MSBuild was not found" }
$devenv = Join-Path $installPath "Common7\IDE\devenv.exe"
$vsixInstaller = Join-Path $installPath "Common7\IDE\VSIXInstaller.exe"
$msbuild = Join-Path $installPath "MSBuild\Current\Bin\MSBuild.exe"
$createExpInstance = Join-Path $installPath "VSSDK\VisualStudioIntegration\Tools\Bin\CreateExpInstance.exe"
$installationVersion = [Version](& $vswhere -latest -products * -property installationVersion)
$instanceId = & $vswhere -latest -products * -property instanceId
$vsInstance = "$($installationVersion.Major).0_$instanceId"
$vsixName = if ($installationVersion.Major -ge 18) { "VSDebuggerMCP.VS2026.vsix" } else { "VSDebuggerMCP.VS2022.vsix" }
$vsix = Join-Path $repoRoot "VSDebuggerMCP\bin\$Configuration\$vsixName"
$solution = Join-Path $repoRoot "tests\vs_integration\AIDebugger.LiveSmoke.sln"
$activityLog = Join-Path $env:TEMP ("AIDebugger-" + $RootSuffix + "-ActivityLog.xml")
if (!(Test-Path -LiteralPath $vsix)) { throw "VSIX not found: $vsix" }
if (!(Test-Path -LiteralPath $createExpInstance)) { throw "VSSDK CreateExpInstance was not found: $createExpInstance" }
$runningVisualStudio = @(Get-Process -Name devenv -ErrorAction SilentlyContinue)
if ($runningVisualStudio.Count -ne 0) {
    throw "Live VS smoke requires all existing Visual Studio processes to be closed because devenv /Setup uses a machine-wide configuration lock"
}

& $msbuild $solution /t:Build /p:Configuration=Debug /v:minimal
if ($LASTEXITCODE -ne 0) { throw "Live-smoke debuggee build failed" }
& $createExpInstance /Reset "/VSInstance=$vsInstance" "/RootSuffix=$RootSuffix"
if ($LASTEXITCODE -ne 0) { throw "Experimental Instance reset failed for $RootSuffix" }
& $vsixInstaller /quiet "/rootSuffix:$RootSuffix" $vsix
if ($LASTEXITCODE -ne 0) { throw "VSIX installation into $RootSuffix failed" }
$configurationProcess = Start-Process -FilePath $devenv -ArgumentList @(
    "/RootSuffix", $RootSuffix,
    "/Setup"
) -PassThru -WindowStyle Hidden
$configurationDeadline = [DateTime]::UtcNow.AddMinutes(5)
while (!$configurationProcess.HasExited -and [DateTime]::UtcNow -lt $configurationDeadline) {
    $configurationProcess.WaitForExit(500) | Out-Null
}
if (!$configurationProcess.HasExited) {
    Stop-Process -Id $configurationProcess.Id -Force -ErrorAction SilentlyContinue
    throw "Visual Studio extension configuration refresh timed out for $RootSuffix"
}
if ($configurationProcess.ExitCode -ne 0) {
    throw "Visual Studio extension configuration refresh failed for $RootSuffix"
}

# Capability vocabulary must match DebugMethodCatalog / debug_method_manifest.json.
$launchCapabilities = ((Get-Content -LiteralPath (Join-Path $repoRoot "mcp_server\debug_method_manifest.json") -Raw -Encoding UTF8) |
    ConvertFrom-Json).methods.PSObject.Properties.Value |
    ForEach-Object { $_.capabilities } |
    Select-Object -Unique |
    Sort-Object
if (@($launchCapabilities).Count -eq 0) {
    throw "Unable to derive launch capabilities from debug_method_manifest.json"
}

$knownVsPids = @(Get-Process -Name devenv -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
$devenvProcess = $null
$testVsProcessId = $null
$launchIdBytes = New-Object byte[] 16
$nonceGenerator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try { $nonceGenerator.GetBytes($launchIdBytes) } finally { $nonceGenerator.Dispose() }
$launchId = ([BitConverter]::ToString($launchIdBytes)).Replace("-", "").ToLowerInvariant()
$expiresUnixMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() + 120000

$previousLaunchId = $env:AIDB_LAUNCH_ID
$previousLaunchCapabilities = $env:AIDB_LAUNCH_CAPABILITIES
$previousLaunchExpires = $env:AIDB_LAUNCH_EXPIRES_UNIX_MS
$env:AIDB_LAUNCH_ID = $launchId
$env:AIDB_LAUNCH_CAPABILITIES = ($launchCapabilities -join ",")
$env:AIDB_LAUNCH_EXPIRES_UNIX_MS = "$expiresUnixMs"

try {
    # Do not wait for DTE/ROT. On headless CI, devenv without /Command parks on the
    # Start Page and never finishes startup (rot stays empty). Use the same launcher
    # contract as mcp_server/vs_launch.py: /Command + AIDB_LAUNCH_* environment.
    # Match mcp_server.vs_launch.build_launch_arguments: RootSuffix, solution, then /Command.
    $devenvProcess = Start-Process -FilePath $devenv -ArgumentList @(
        "/RootSuffix", $RootSuffix,
        "/nosplash",
        "/Log", $activityLog,
        $solution,
        "/Command", "Tools.AIDebugger.StartFullAccessServer"
    ) -PassThru -WindowStyle Hidden
    $testVsProcessId = $devenvProcess.Id
    if ($devenvProcess.HasExited) {
        throw "Visual Studio exited immediately after launcher start for $RootSuffix"
    }

    # Clear launcher env from this process so later children cannot reuse it.
    foreach ($name in @("AIDB_LAUNCH_ID", "AIDB_LAUNCH_CAPABILITIES", "AIDB_LAUNCH_EXPIRES_UNIX_MS")) {
        Remove-Item -LiteralPath ("Env:" + $name) -ErrorAction SilentlyContinue
    }

    $env:AIDB_TEST_VS_PID = $testVsProcessId.ToString()
    $env:AIDB_PREFERRED_IDE = "vs"
    & python (Join-Path $repoRoot "tests\vs_integration\run_live_vs_smoke.py")
    if ($LASTEXITCODE -ne 0) { throw "Live Visual Studio smoke test failed; Activity Log: $activityLog" }
}
catch {
    if (Test-Path -LiteralPath $activityLog) {
        Write-Host "--- Visual Studio Activity Log tail ---"
        Get-Content -LiteralPath $activityLog -Tail 80 | Write-Host
    }
    throw
}
finally {
    foreach ($pair in @(
        @{ Name = "AIDB_LAUNCH_ID"; Previous = $previousLaunchId; Current = $launchId },
        @{ Name = "AIDB_LAUNCH_CAPABILITIES"; Previous = $previousLaunchCapabilities; Current = ($launchCapabilities -join ",") },
        @{ Name = "AIDB_LAUNCH_EXPIRES_UNIX_MS"; Previous = $previousLaunchExpires; Current = "$expiresUnixMs" }
    )) {
        if ((Get-Item -LiteralPath ("Env:" + $pair.Name) -ErrorAction SilentlyContinue).Value -eq $pair.Current) {
            if ($null -eq $pair.Previous) {
                Remove-Item -LiteralPath ("Env:" + $pair.Name) -ErrorAction SilentlyContinue
            } else {
                Set-Item -LiteralPath ("Env:" + $pair.Name) -Value $pair.Previous
            }
        }
    }
    if ($testVsProcessId -and $knownVsPids -notcontains $testVsProcessId) {
        Stop-Process -Id $testVsProcessId -Force -ErrorAction SilentlyContinue
    }
    if ($devenvProcess -and !$devenvProcess.HasExited -and $knownVsPids -notcontains $devenvProcess.Id) {
        Stop-Process -Id $devenvProcess.Id -Force -ErrorAction SilentlyContinue
        $devenvProcess.WaitForExit(10000) | Out-Null
    }
}
