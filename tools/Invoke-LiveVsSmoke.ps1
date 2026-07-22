param(
    [string]$RootSuffix = "AIDebuggerCI",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
public static class AIDebuggerRot
{
    [DllImport("ole32.dll")] private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable table);
    [DllImport("ole32.dll")] private static extern int CreateBindCtx(int reserved, out IBindCtx context);
    public static object GetVisualStudioDte(int processId)
    {
        IRunningObjectTable table;
        IBindCtx context;
        if (GetRunningObjectTable(0, out table) != 0 || CreateBindCtx(0, out context) != 0) return null;
        IEnumMoniker iterator;
        table.EnumRunning(out iterator);
        IMoniker[] current = new IMoniker[1];
        while (iterator.Next(1, current, IntPtr.Zero) == 0)
        {
            string name;
            current[0].GetDisplayName(context, null, out name);
            if (name != null && name.StartsWith("!VisualStudio.DTE.", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(":" + processId, StringComparison.Ordinal))
            {
                object instance;
                table.GetObject(current[0], out instance);
                return instance;
            }
        }
        return null;
    }
}
"@
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
$knownVsPids = @(Get-Process -Name devenv -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
$devenvProcess = $null
$testVsProcessId = $null
$nonceBytes = New-Object byte[] 32
$nonceGenerator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try { $nonceGenerator.GetBytes($nonceBytes) } finally { $nonceGenerator.Dispose() }
$automationNonce = ([BitConverter]::ToString($nonceBytes)).Replace("-", "").ToLowerInvariant()
$previousAutomationNonce = $env:AIDB_LIVE_SMOKE_NONCE
$previousAutomationRootSuffix = $env:AIDB_LIVE_SMOKE_ROOT_SUFFIX
$env:AIDB_LIVE_SMOKE_NONCE = $automationNonce
$env:AIDB_LIVE_SMOKE_ROOT_SUFFIX = $RootSuffix
try {
    $devenvProcess = Start-Process -FilePath $devenv -ArgumentList @(
        "/RootSuffix", $RootSuffix,
        "/Embedding",
        "/Log", $activityLog
    ) -PassThru -WindowStyle Hidden
    if ($null -eq $previousAutomationNonce) {
        Remove-Item Env:AIDB_LIVE_SMOKE_NONCE -ErrorAction SilentlyContinue
    } else {
        $env:AIDB_LIVE_SMOKE_NONCE = $previousAutomationNonce
    }
    if ($null -eq $previousAutomationRootSuffix) {
        Remove-Item Env:AIDB_LIVE_SMOKE_ROOT_SUFFIX -ErrorAction SilentlyContinue
    } else {
        $env:AIDB_LIVE_SMOKE_ROOT_SUFFIX = $previousAutomationRootSuffix
    }
    $testVsProcessId = $devenvProcess.Id
    $readyDeadline = [DateTime]::UtcNow.AddSeconds(60)
    $dte = $null
    while ([DateTime]::UtcNow -lt $readyDeadline) {
        if ($devenvProcess.HasExited) { throw "Visual Studio exited before registering DTE for $RootSuffix" }
        $dte = [AIDebuggerRot]::GetVisualStudioDte($testVsProcessId)
        if ($dte) { break }
        Start-Sleep -Milliseconds 500
    }
    if (!$dte) { throw "Visual Studio DTE did not register for $RootSuffix" }
    $dte.Solution.Open($solution)
    $commandDeadline = [DateTime]::UtcNow.AddSeconds(45)
    $startCommand = $null
    while ([DateTime]::UtcNow -lt $commandDeadline) {
        try { $startCommand = $dte.Commands.Item("AIDebugger.StartAutomationTestServer", 0) }
        catch { $startCommand = $null }
        if ($startCommand) { break }
        Start-Sleep -Milliseconds 500
    }
    if (!$startCommand) { throw "AIDebugger command table was not registered in $RootSuffix" }
    $dte.ExecuteCommand("AIDebugger.StartAutomationTestServer", $automationNonce)
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
    if ($env:AIDB_LIVE_SMOKE_NONCE -eq $automationNonce) {
        if ($null -eq $previousAutomationNonce) {
            Remove-Item Env:AIDB_LIVE_SMOKE_NONCE -ErrorAction SilentlyContinue
        } else {
            $env:AIDB_LIVE_SMOKE_NONCE = $previousAutomationNonce
        }
    }
    if ($env:AIDB_LIVE_SMOKE_ROOT_SUFFIX -eq $RootSuffix) {
        if ($null -eq $previousAutomationRootSuffix) {
            Remove-Item Env:AIDB_LIVE_SMOKE_ROOT_SUFFIX -ErrorAction SilentlyContinue
        } else {
            $env:AIDB_LIVE_SMOKE_ROOT_SUFFIX = $previousAutomationRootSuffix
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
