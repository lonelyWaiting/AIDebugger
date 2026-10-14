param(
    [string]$RootSuffix = "AIDebuggerCI",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
# ROT lookup follows Microsoft's documented VS 2017+ DTE launch pattern:
# https://learn.microsoft.com/en-us/visualstudio/extensibility/launch-visual-studio-dte
# Critical details: match EndsWith("VisualStudio.DTE.<major>.0:<pid>"), and swallow
# UnauthorizedAccessException while enumerating ROT entries (elevated monikers).
Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
public static class AIDebuggerRot
{
    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(uint reserved, out IBindCtx context);

    public static string[] ListVisualStudioDteNames()
    {
        IEnumerable<string> names;
        GetRunningObject(null, out names);
        List<string> filtered = new List<string>();
        foreach (string name in names)
        {
            if (name.IndexOf("VisualStudio.DTE", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                filtered.Add(name);
            }
        }
        return filtered.ToArray();
    }

    public static object GetVisualStudioDte(int majorVersion, int processId)
    {
        // Official sample: display name suffix is "VisualStudio.DTE.16.0:{pid}".
        // Full ROT names usually look like "!VisualStudio.DTE.16.0:{pid}".
        string suffix = "VisualStudio.DTE." + majorVersion + ".0:" + processId;
        IEnumerable<string> ignored;
        return GetRunningObject(suffix, out ignored);
    }

    private static object GetRunningObject(string displayNameSuffix, out IEnumerable<string> runningObjectDisplayNames)
    {
        IBindCtx bindContext;
        CreateBindCtx(0, out bindContext);
        IRunningObjectTable runningObjectTable;
        bindContext.GetRunningObjectTable(out runningObjectTable);
        IEnumMoniker monikerEnumerator;
        runningObjectTable.EnumRunning(out monikerEnumerator);

        object runningObject = null;
        List<string> names = new List<string>();
        IMoniker[] monikers = new IMoniker[1];
        IntPtr numberFetched = IntPtr.Zero;
        while (monikerEnumerator.Next(1, monikers, numberFetched) == 0)
        {
            string objectDisplayName = null;
            try
            {
                monikers[0].GetDisplayName(bindContext, null, out objectDisplayName);
            }
            catch (UnauthorizedAccessException)
            {
                // Some ROT objects require elevated permissions; keep scanning.
            }

            if (!string.IsNullOrWhiteSpace(objectDisplayName))
            {
                names.Add(objectDisplayName);
                if (displayNameSuffix != null &&
                    objectDisplayName.EndsWith(displayNameSuffix, StringComparison.Ordinal))
                {
                    runningObjectTable.GetObject(monikers[0], out runningObject);
                }
            }
        }

        runningObjectDisplayNames = names;
        return runningObject;
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
    # Launch like the Microsoft DTE sample: Process.Start(devenv) + poll ROT.
    # Avoid /Embedding here; it is not part of the documented launch path and has
    # been unreliable for Exp-hive automation on headless CI runners.
    $devenvProcess = Start-Process -FilePath $devenv -ArgumentList @(
        "/RootSuffix", $RootSuffix,
        "/nosplash",
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
    $dteMajor = [int]$installationVersion.Major
    # Microsoft sample waits up to 60s; cold VS 18 Exp hives on CI need more.
    $readyDeadline = [DateTime]::UtcNow.AddMinutes(3)
    $dte = $null
    while ([DateTime]::UtcNow -lt $readyDeadline) {
        if ($devenvProcess.HasExited) {
            $candidates = @(Get-Process -Name devenv -ErrorAction SilentlyContinue |
                Where-Object { $knownVsPids -notcontains $_.Id } |
                Select-Object -ExpandProperty Id)
            if ($candidates.Count -eq 1) {
                $testVsProcessId = [int]$candidates[0]
                $devenvProcess = Get-Process -Id $testVsProcessId
            } else {
                throw "Visual Studio exited before registering DTE for $RootSuffix"
            }
        }
        $dte = [AIDebuggerRot]::GetVisualStudioDte($dteMajor, $testVsProcessId)
        if ($dte) { break }
        foreach ($candidateId in @(Get-Process -Name devenv -ErrorAction SilentlyContinue |
            Where-Object { $knownVsPids -notcontains $_.Id } |
            Select-Object -ExpandProperty Id)) {
            if ([int]$candidateId -eq $testVsProcessId) { continue }
            $dte = [AIDebuggerRot]::GetVisualStudioDte($dteMajor, [int]$candidateId)
            if ($dte) {
                $testVsProcessId = [int]$candidateId
                break
            }
        }
        if ($dte) { break }
        Start-Sleep -Seconds 1
    }
    if (!$dte) {
        $rotNames = @([AIDebuggerRot]::ListVisualStudioDteNames())
        $devenvIds = @(Get-Process -Name devenv -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
        throw ("Visual Studio DTE did not register for $RootSuffix within 3 minutes " +
            "(expected suffix VisualStudio.DTE.$dteMajor.0:$testVsProcessId; " +
            "devenv=[$($devenvIds -join ', ')]; rot=[$($rotNames -join ', ')])")
    }
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
