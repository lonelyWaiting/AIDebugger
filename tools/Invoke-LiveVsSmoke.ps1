param(
    [string]$RootSuffix = "AIDebuggerCI",
    [string]$Configuration = "Release",
    # Internal: set when the High-IL host re-launches this script as the non-admin smoke user.
    [switch]$AsSmokeUser,
    # Absolute python.exe — scheduled-task users do not inherit the job PATH from setup-python.
    [string]$PythonPath = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

function Test-HighIntegrityProcess {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    foreach ($group in $identity.Groups) {
        if ($group.Value -eq "S-1-16-12288") { return $true }
    }
    return $false
}

function Ensure-LiveSmokeUser {
    $userName = "aidbsmoke"
    # Local CI-only account; password is not a secret (ephemeral runner / disposable local user).
    $password = "Aidb!Smoke_CI_7e3c"
    $secure = ConvertTo-SecureString $password -AsPlainText -Force
    if (Get-LocalUser -Name $userName -ErrorAction SilentlyContinue) {
        Set-LocalUser -Name $userName -Password $secure
    } else {
        New-LocalUser -Name $userName -Password $secure -PasswordNeverExpires -UserMayNotChangePassword |
            Out-Null
        Add-LocalGroupMember -Group "Users" -Member $userName -ErrorAction SilentlyContinue
    }
    Remove-LocalGroupMember -Group "Administrators" -Member $userName -ErrorAction SilentlyContinue
    return @{ UserName = $userName; Password = $password }
}

function Grant-SmokeUserRepoAccess {
    param([Parameter(Mandatory = $true)][string]$UserName)
    $grant = "${UserName}:(OI)(CI)M"
    $paths = @(
        $repoRoot,
        (Join-Path $repoRoot "tests"),
        (Join-Path $repoRoot "VSDebuggerMCP\bin"),
        (Join-Path $repoRoot "mcp_server"),
        (Join-Path $repoRoot "tools")
    )
    foreach ($path in $paths) {
        if (Test-Path -LiteralPath $path) {
            & icacls $path /grant $grant /T /C /Q | Out-Null
        }
    }
}

function Resolve-SmokePython {
    param([string]$Preferred)
    if (-not [string]::IsNullOrWhiteSpace($Preferred) -and (Test-Path -LiteralPath $Preferred)) {
        return (Resolve-Path -LiteralPath $Preferred).Path
    }
    $command = Get-Command python -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw "python.exe was not found on PATH; live VS smoke requires a Python 3.10+ x64 runtime"
    }
    return $command.Source
}

function Invoke-AsNonAdminSmokeUser {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][string]$RootSuffix,
        [Parameter(Mandatory = $true)][string]$Configuration,
        [Parameter(Mandatory = $true)][string]$PythonPath
    )

    $cred = Ensure-LiveSmokeUser
    Grant-SmokeUserRepoAccess -UserName $cred.UserName
    # setup-python installs into a per-job location; grant RX so the smoke user can execute it.
    $pythonDir = Split-Path -Parent $PythonPath
    if (Test-Path -LiteralPath $pythonDir) {
        & icacls $pythonDir /grant "$($cred.UserName):(OI)(CI)RX" /T /C /Q | Out-Null
    }

    $taskName = "AIDebuggerLiveSmoke_" + ([guid]::NewGuid().ToString("N").Substring(0, 8))
    $outDir = Join-Path $env:TEMP $taskName
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    # Smoke user must write the exit/log files the host waits on.
    & icacls $outDir /grant "$($cred.UserName):(OI)(CI)M" /T /C /Q | Out-Null

    $exitFile = Join-Path $outDir "exitcode.txt"
    $logFile = Join-Path $outDir "stdout.txt"
    $wrapper = Join-Path $outDir "run.ps1"
    $wrapperBody = @"
`$ErrorActionPreference = 'Continue'
`$code = 1
try {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File '$ScriptPath' -RootSuffix '$RootSuffix' -Configuration '$Configuration' -AsSmokeUser -PythonPath '$PythonPath' *> '$logFile'
    `$code = `$LASTEXITCODE
    if (`$null -eq `$code) { `$code = 1 }
} catch {
    `$_ | Out-File -FilePath '$logFile' -Append -Encoding utf8
    `$code = 1
}
Set-Content -LiteralPath '$exitFile' -Value `$code -Encoding ASCII
"@
    Set-Content -LiteralPath $wrapper -Value $wrapperBody -Encoding UTF8

    try {
        $action = New-ScheduledTaskAction `
            -Execute "powershell.exe" `
            -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$wrapper`"" `
            -WorkingDirectory $repoRoot
        Register-ScheduledTask `
            -TaskName $taskName `
            -Action $action `
            -User $cred.UserName `
            -Password $cred.Password `
            -RunLevel Limited `
            -Force | Out-Null
        Start-ScheduledTask -TaskName $taskName

        $deadline = [DateTime]::UtcNow.AddMinutes(25)
        while (-not (Test-Path -LiteralPath $exitFile) -and [DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Seconds 2
        }
        if (Test-Path -LiteralPath $logFile) {
            Write-Host "--- live smoke (non-admin) log ---"
            Get-Content -LiteralPath $logFile | Write-Host
        }
        if (-not (Test-Path -LiteralPath $exitFile)) {
            throw "Live VS smoke scheduled task '$taskName' timed out without an exit code"
        }
        $code = [int]((Get-Content -LiteralPath $exitFile -Raw).Trim())
        if ($code -ne 0) {
            throw "Live VS smoke failed under non-admin user '$($cred.UserName)' with exit code $code"
        }
    }
    finally {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $outDir -Recurse -Force -ErrorAction SilentlyContinue
        # Leave the local user in place on reused self-hosted runners; GHA VMs are ephemeral.
    }
}

# GitHub-hosted Windows runners run High IL with UAC disabled. AIDebugger refuses to start
# its MCP server from elevated devenv, and same-user de-elevation (runas /trustlevel) does not
# drop the Mandatory Integrity Level. Re-launch the whole smoke as a non-admin local user.
if (-not $AsSmokeUser -and (Test-HighIntegrityProcess)) {
    $hostPython = Resolve-SmokePython -Preferred $PythonPath
    Write-Host "Host process is High integrity; re-launching live VS smoke as a non-admin local user."
    Write-Host "Smoke Python: $hostPython"
    Invoke-AsNonAdminSmokeUser -ScriptPath $PSCommandPath -RootSuffix $RootSuffix -Configuration $Configuration -PythonPath $hostPython
    return
}

Write-Host ("Live VS smoke identity: {0}; highIntegrity={1}" -f (
    [System.Security.Principal.WindowsIdentity]::GetCurrent().Name),
    (Test-HighIntegrityProcess))
if (Test-HighIntegrityProcess) {
    throw "Live VS smoke is still High integrity after the non-admin re-launch; MCP server start would be refused"
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
# Match mcp_server.vs_launch.LAUNCH_REQUEST_LIFETIME_SECONDS (300s). Cold Exp Instance
# startup on CI frequently exceeds a 2-minute launch window.
$expiresUnixMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() + 300000

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
    $python = Resolve-SmokePython -Preferred $PythonPath
    & $python (Join-Path $repoRoot "tests\vs_integration\run_live_vs_smoke.py")
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
