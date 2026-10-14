param(
    [string]$RootSuffix = "AIDebuggerCI",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

function Test-HighIntegrityProcess {
    # WindowsIdentity.Groups omits the Mandatory Label SID. Match ProcessIntegrityValidator.
    $aidbTypeDef = @"
using System;
using System.Runtime.InteropServices;
public static class AidbTokenIntegrity {
    const int TokenIntegrityLevel = 25;
    const uint TokenQuery = 0x0008;
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthority);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool IsValidSid(IntPtr sid);
    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
    public static bool IsHighOrAbove() {
        IntPtr token;
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token)) return false;
        try {
            int needed;
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out needed);
            if (needed <= 0) return false;
            IntPtr buffer = Marshal.AllocHGlobal(needed);
            try {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, needed, out needed)) return false;
                IntPtr sid = Marshal.ReadIntPtr(buffer);
                if (sid == IntPtr.Zero || !IsValidSid(sid)) return false;
                IntPtr countPointer = GetSidSubAuthorityCount(sid);
                if (countPointer == IntPtr.Zero) return false;
                byte subAuthorityCount = Marshal.ReadByte(countPointer);
                if (subAuthorityCount == 0) return false;
                IntPtr ridPointer = GetSidSubAuthority(sid, (uint)(subAuthorityCount - 1));
                if (ridPointer == IntPtr.Zero) return false;
                return unchecked((uint)Marshal.ReadInt32(ridPointer)) >= 0x3000u;
            } finally { Marshal.FreeHGlobal(buffer); }
        } finally { CloseHandle(token); }
    }
}
"@
    if (-not ("AidbTokenIntegrity" -as [type])) {
        Add-Type -TypeDefinition $aidbTypeDef -ErrorAction Stop
    }
    return [AidbTokenIntegrity]::IsHighOrAbove()
}

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
# Match mcp_server.vs_launch.LAUNCH_REQUEST_LIFETIME_SECONDS (300s).
$expiresUnixMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() + 300000

$previousLaunchId = $env:AIDB_LAUNCH_ID
$previousLaunchCapabilities = $env:AIDB_LAUNCH_CAPABILITIES
$previousLaunchExpires = $env:AIDB_LAUNCH_EXPIRES_UNIX_MS
$previousAllowElevated = $env:AIDB_ALLOW_ELEVATED_SERVER
$env:AIDB_LAUNCH_ID = $launchId
$env:AIDB_LAUNCH_CAPABILITIES = ($launchCapabilities -join ",")
$env:AIDB_LAUNCH_EXPIRES_UNIX_MS = "$expiresUnixMs"
# GitHub-hosted Windows runners are High IL with UAC disabled; runas /trustlevel and
# non-admin session-0 tasks cannot reliably host devenv. Opt into the CI-only elevated
# server path that the VSIX recognizes via AIDB_ALLOW_ELEVATED_SERVER=1.
if (Test-HighIntegrityProcess) {
    Write-Host "Host process is High integrity; setting AIDB_ALLOW_ELEVATED_SERVER=1 for CI live smoke."
    $env:AIDB_ALLOW_ELEVATED_SERVER = "1"
}

try {
    # Do not wait for DTE/ROT. On headless CI, devenv without /Command parks on the
    # Start Page and never finishes startup. Use the production launcher contract:
    # /Command + AIDB_LAUNCH_* (+ AIDB_ALLOW_ELEVATED_SERVER on High-IL hosts).
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
        @{ Name = "AIDB_LAUNCH_EXPIRES_UNIX_MS"; Previous = $previousLaunchExpires; Current = "$expiresUnixMs" },
        @{ Name = "AIDB_ALLOW_ELEVATED_SERVER"; Previous = $previousAllowElevated; Current = "1" }
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
