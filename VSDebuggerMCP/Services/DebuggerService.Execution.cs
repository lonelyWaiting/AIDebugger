using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using EnvDTE;
using EnvDTE80;
using EnvDTE100;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VSDebuggerMCP.Protocol;
using VSDebuggerMCP.Services;

namespace VSDebuggerMCP
{
    public partial class DebuggerService
    {
        // Process lookup resolves the debugger automation handles once per snapshot and
        // caches the result briefly: reading debugger.LocalProcesses again for every process
        // costs about 200 ms, which turned a ~600 process scan into minutes and made every
        // attach look like a hang.
        // Items captured per bounded UI-thread slice; see CaptureLocalProcessSnapshot.
        private const int LocalProcessCaptureBatchSize = 32;
        private const int LocalProcessSnapshotCacheMs = 10000;
        private const int LocalProcessSnapshotTimeoutMs = 30000;
        private readonly object _localProcessSnapshotLock = new object();
        private LocalProcessSnapshot _cachedLocalProcessSnapshot;
        private int _cachedLocalProcessCount = -1;
        private DateTime _cachedLocalProcessSnapshotUtc = DateTime.MinValue;
        #region Debug Control

        /// <summary>
        /// Attach the Visual Studio debugger to a local process by PID or executable name.
        /// </summary>
        public ProcessDebugResult AttachProcess(int processId = 0, string processName = null)
        {
            LocalProcessLookup target = FindLocalProcessSliced(processId, processName);
            if (!target.Found)
            {
                var notFound = new ProcessDebugResult
                {
                    Action = "AttachProcess",
                    ProcessId = processId,
                    ProcessName = processName,
                    Success = false,
                    ErrorCode = "TARGET_NOT_FOUND",
                    Message = target.Message,
                };
                TryPopulateDebugStatus(notFound);
                return notFound;
            }

            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new ProcessDebugResult
                {
                    Action = "AttachProcess",
                    ProcessId = processId,
                    ProcessName = processName,
                    ProcessStartTimeUtcTicks = target.ProcessStartTimeUtcTicks,
                };

                try
                {
                    IntPtr identityHandle = OpenVerifiedLocalProcess(debugger, target, out EnvDTE.Process targetProcess);
                    if (identityHandle == IntPtr.Zero || targetProcess == null)
                    {
                        throw new StaleDebugSessionException(
                            $"Process {target.ProcessId} changed before Visual Studio could attach.");
                    }

                    try
                    {
                        ClearDumpSessionState();
                        result.CommandIssued = true;
                        try
                        {
                            CommandEffectExecutor.Issue(
                                "debugger.attach_process",
                                targetProcess.Attach);
                        }
                        finally
                        {
                            MarkDebugSessionTransitionCommandIssued("attach_process");
                        }
                    }
                    finally
                    {
                        CloseHandle(identityHandle);
                    }
                    result.Success = true;
                    result.AttachCompleted = true;
                    result.Outcome = "attached_existing_process";
                    result.ProcessId = target.ProcessId;
                    result.ProcessName = target.ProcessName;
                    result.Message = $"Attached to process {target.ProcessName} ({target.ProcessId})";
                    try { result.Status = BuildDebugStatus(debugger); } catch { }
                    RecordTrustedLiveProcessIdentity(
                        target.ProcessId,
                        target.ProcessStartTimeUtcTicks,
                        result.Status);
                }
                catch (Exception ex)
                {
                    RethrowRequestControlFlow(ex);
                    result.Success = false;
                    result.ErrorCode = "OUTCOME_UNKNOWN";
                    result.Outcome = result.CommandIssued
                        ? "attach_existing_process_outcome_unknown"
                        : "attach_existing_process_not_started";
                    result.Message = ex.Message;
                    TryPopulateDebugStatus(result, debugger);
                }

                return result;
            }, HeavyMainThreadTimeoutMs);
        }

        /// <summary>
        /// Start debugging with the current VS debug profile, or launch an executable and attach to it.
        /// </summary>
        public ProcessDebugResult StartDebugging(string executablePath = null, string workingDirectory = null, string arguments = null, bool attach = true)
        {
            string normalizedDumpPath = null;
            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                string candidatePath = System.IO.Path.GetFullPath(executablePath);
                if (!IsDumpFilePath(candidatePath))
                {
                    return StartExecutableAndOptionallyAttach(candidatePath, workingDirectory, arguments, attach);
                }
                if (!System.IO.File.Exists(candidatePath))
                {
                    return new ProcessDebugResult
                    {
                        Action = "StartDebugging",
                        Success = false,
                        ErrorCode = "TARGET_NOT_FOUND",
                        ExecutablePath = candidatePath,
                        Message = $"Dump file not found: {candidatePath}"
                    };
                }
                normalizedDumpPath = candidatePath;
            }

            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new ProcessDebugResult
                {
                    Action = "StartDebugging",
                    ExecutablePath = executablePath,
                    WorkingDirectory = workingDirectory,
                    Arguments = arguments
                };

                try
                {
                    if (string.IsNullOrWhiteSpace(executablePath))
                    {
                        if (debugger.CurrentMode != dbgDebugMode.dbgDesignMode)
                        {
                            result.Success = false;
                            result.Message = "A debug session is already active";
                            TryPopulateDebugStatus(result, debugger);
                            return result;
                        }

                        if (TryStartActiveDumpDebugging(debugger, result))
                        {
                            return result;
                        }

                        ClearDumpSessionState();
                        result.CommandIssued = true;
                        try
                        {
                            CommandEffectExecutor.Issue(
                                "debugger.start",
                                () =>
                                {
                                    ThreadHelper.ThrowIfNotOnUIThread();
                                    debugger.Go(false);
                                });
                        }
                        finally
                        {
                            MarkDebugSessionTransitionCommandIssued("start_debugging");
                        }
                        result.Success = true;
                        result.Outcome = "debug_start_requested";
                        result.Message = "Started debugging using the current Visual Studio startup configuration";
                        TryPopulateDebugStatus(result, debugger);
                        return result;
                    }

                    if (!string.IsNullOrEmpty(normalizedDumpPath))
                    {
                        return StartDumpDebuggingFromPath(debugger, result, normalizedDumpPath);
                    }

                    throw new InvalidOperationException("Executable launch must be dispatched outside the Visual Studio UI thread");
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.ErrorCode = "OUTCOME_UNKNOWN";
                    result.Outcome = result.CommandIssued
                        ? "debug_start_outcome_unknown"
                        : "debug_start_not_started";
                    result.Message = ex.Message;
                    TryPopulateDebugStatus(result, debugger);
                }

                return result;
            }, HeavyMainThreadTimeoutMs);
        }

        private ProcessDebugResult StartExecutableAndOptionallyAttach(
            string executablePath,
            string workingDirectory,
            string arguments,
            bool attach)
        {
            var result = new ProcessDebugResult
            {
                Action = "StartDebugging",
                ExecutablePath = executablePath,
                Arguments = arguments,
            };

            if (!System.IO.File.Exists(executablePath))
            {
                result.Success = false;
                result.ErrorCode = "TARGET_NOT_FOUND";
                result.Message = $"Executable not found: {executablePath}";
                return result;
            }

            string normalizedWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? System.IO.Path.GetDirectoryName(executablePath)
                : System.IO.Path.GetFullPath(workingDirectory);
            result.WorkingDirectory = normalizedWorkingDirectory;
            if (!string.IsNullOrWhiteSpace(normalizedWorkingDirectory) &&
                !System.IO.Directory.Exists(normalizedWorkingDirectory))
            {
                result.Success = false;
                result.ErrorCode = "TARGET_NOT_FOUND";
                result.Message = $"Working directory not found: {normalizedWorkingDirectory}";
                return result;
            }

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments ?? string.Empty,
                WorkingDirectory = normalizedWorkingDirectory ?? string.Empty,
                UseShellExecute = false,
            };
            IntPtr launchedProcessIdentityHandle = IntPtr.Zero;
            using (var process = CommandEffectExecutor.Issue(
                "process.start",
                () => System.Diagnostics.Process.Start(startInfo)))
            {
                if (process == null)
                {
                    result.Success = false;
                    result.Message = "Failed to start process";
                    return result;
                }

                PostLaunchCaptureResult<Tuple<int, string, long>> launchCapture =
                    ProcessLaunchBoundary.CaptureAfterStart(
                        process,
                        () =>
                        {
                            result.CommandIssued = true;
                            result.ProcessStarted = true;
                        },
                        launched =>
                        {
                            // Id is the minimum immutable identity needed for every
                            // later attach step. Other metadata remains best-effort.
                            int processId = launched.Id;
                            string processName;
                            long processStartTimeUtcTicks = 0;
                            try { processName = launched.ProcessName; }
                            catch { processName = System.IO.Path.GetFileNameWithoutExtension(executablePath); }
                            try { processStartTimeUtcTicks = launched.StartTime.ToUniversalTime().Ticks; }
                            catch { }
                            return Tuple.Create(processId, processName, processStartTimeUtcTicks);
                        });
                if (launchCapture.CaptureError != null)
                {
                    result.Success = false;
                    result.AttachCompleted = false;
                    result.ProcessName = System.IO.Path.GetFileNameWithoutExtension(executablePath);
                    result.ErrorCode = "PROCESS_STARTED_IDENTITY_UNAVAILABLE";
                    result.Outcome = "process_started_identity_unavailable";
                    result.Message =
                        $"The executable was started, but its process identity could not be captured: {launchCapture.CaptureError.Message}. Do not start it again blindly.";
                    TryPopulateDebugStatus(result);
                    return result;
                }
                result.ProcessId = launchCapture.Value.Item1;
                result.ProcessName = launchCapture.Value.Item2;
                result.ProcessStartTimeUtcTicks = launchCapture.Value.Item3;
                if (!attach)
                {
                    result.Success = true;
                    result.Outcome = "process_started_without_attach";
                    result.Message = $"Started process {result.ProcessName} ({result.ProcessId}) without attaching";
                    TryPopulateDebugStatus(result);
                    return result;
                }

                launchedProcessIdentityHandle = OpenVerifiedProcessHandle(
                    result.ProcessId,
                    result.ProcessStartTimeUtcTicks);
                if (launchedProcessIdentityHandle == IntPtr.Zero)
                {
                    result.Success = false;
                    result.AttachCompleted = false;
                    result.ErrorCode = "ATTACH_TARGET_STALE";
                    result.Outcome = "process_started_attach_not_completed";
                    result.Message = $"Started process {result.ProcessName} ({result.ProcessId}), but its immutable process identity could not be retained for attach.";
                    TryPopulateDebugStatus(result);
                    return result;
                }
            }

            try
            {
                LocalProcessLookup target;
                try
                {
                    target = WaitForLocalProcessOffUiThread(
                        result.ProcessId,
                        result.ProcessStartTimeUtcTicks,
                        5000);
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.AttachCompleted = false;
                    result.ErrorCode = "ATTACH_DISCOVERY_FAILED";
                    result.Outcome = "process_started_attach_not_completed";
                    result.Message = $"Started process {result.ProcessName} ({result.ProcessId}), but attach discovery failed: {ex.Message}";
                    TryPopulateDebugStatus(result);
                    return result;
                }
                if (target == null || !target.Found)
                {
                    result.Success = false;
                    result.AttachCompleted = false;
                    result.ErrorCode = "ATTACH_DISCOVERY_FAILED";
                    result.Outcome = "process_started_attach_not_completed";
                    result.Message = $"Started process {result.ProcessName} ({result.ProcessId}) but Visual Studio did not expose the same process identity for attach";
                    TryPopulateDebugStatus(result);
                    return result;
                }

                try
                {
                    return ExecuteOnMainThread(() =>
                    {
                        ThreadHelper.ThrowIfNotOnUIThread();
                        // Reuse the lookup's resolved automation object when present (the
                        // by-name fast path leaves Index at 0), otherwise fall back to the
                        // captured index; both are re-verified through the OS handle.
                        IntPtr finalIdentityHandle = OpenVerifiedLocalProcess(
                            Debugger,
                            target,
                            out EnvDTE.Process targetProcess);
                        if (finalIdentityHandle == IntPtr.Zero || targetProcess == null)
                        {
                            result.Success = false;
                            result.AttachCompleted = false;
                            result.ErrorCode = "ATTACH_TARGET_STALE";
                            result.Outcome = "process_started_attach_not_completed";
                            result.Message = $"Started process {result.ProcessName} ({result.ProcessId}), but its Visual Studio attach target changed.";
                            try { result.Status = BuildDebugStatus(Debugger); } catch { }
                            return result;
                        }
                        try
                        {
                            ClearDumpSessionState();
                            try
                            {
                                try
                                {
                                    CommandEffectExecutor.Issue(
                                        "debugger.attach_started_process",
                                        targetProcess.Attach);
                                }
                                finally
                                {
                                    MarkDebugSessionTransitionCommandIssued("start_executable_attach");
                                }
                            }
                            catch (Exception ex)
                            {
                                result.Success = false;
                                result.AttachCompleted = false;
                                result.ErrorCode = "OUTCOME_UNKNOWN";
                                result.Outcome = "process_started_attach_outcome_unknown";
                                result.Message = $"Started process {result.ProcessName} ({result.ProcessId}); attach was attempted but its outcome is unknown: {ex.Message}";
                                try { result.Status = BuildDebugStatus(Debugger); } catch { }
                                return result;
                            }
                        }
                        finally
                        {
                            CloseHandle(finalIdentityHandle);
                        }
                        result.Success = true;
                        result.AttachCompleted = true;
                        result.Outcome = "process_started_and_attached";
                        result.ProcessName = target.ProcessName;
                        result.Message = $"Started and attached to process {target.ProcessName} ({target.ProcessId})";
                        try { result.Status = BuildDebugStatus(Debugger); } catch { }
                        RecordTrustedLiveProcessIdentity(
                            target.ProcessId,
                            target.ProcessStartTimeUtcTicks,
                            result.Status);
                        return result;
                    }, HeavyMainThreadTimeoutMs);
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.AttachCompleted = false;
                    result.ErrorCode = "ATTACH_DISPATCH_FAILED";
                    result.Outcome = "process_started_attach_not_completed";
                    result.Message = $"Started process {result.ProcessName} ({result.ProcessId}), but attach dispatch did not complete: {ex.Message}";
                    TryPopulateDebugStatus(result);
                    return result;
                }
            }
            finally
            {
                if (launchedProcessIdentityHandle != IntPtr.Zero)
                {
                    CloseHandle(launchedProcessIdentityHandle);
                }
            }
        }

        [SuppressMessage("Usage", "VSTHRD010", Justification = "Uses the supplied DTE only when already on the UI thread; otherwise calls the self-marshalling status entrypoint.")]
        private void TryPopulateDebugStatus(ProcessDebugResult result, Debugger5 debugger = null)
        {
            if (result == null) return;
            try
            {
                result.Status = ThreadHelper.CheckAccess() && debugger != null
                    ? BuildDebugStatus(debugger)
                    : GetDebugStatus();
            }
            catch { }
        }

        private ProcessDebugResult StartDumpDebuggingFromPath(Debugger5 debugger, ProcessDebugResult result, string dumpPath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            result.ExecutablePath = dumpPath;

            try
            {
                BeginPendingDumpSession(dumpPath);
                result.UiNavigationIssued = true;
                CommandEffectExecutor.Issue(
                    "visual_studio.open_dump_document",
                    () => OpenAndVerifyTargetDocument(dumpPath));
                if (!TryStartActiveDumpDebuggingCommand(
                    out string startError,
                    out bool commandIssued,
                    out bool outcomeUnknown))
                {
                    result.CommandIssued = commandIssued;
                    if (commandIssued)
                        MarkDebugSessionTransitionCommandIssued("start_dump_debugging");
                    if (!outcomeUnknown) ClearDumpSessionState();
                    result.Success = false;
                    result.ErrorCode = outcomeUnknown ? "OUTCOME_UNKNOWN" : "DEBUG_OPERATION_FAILED";
                    result.Message = startError;
                    result.Outcome = outcomeUnknown
                        ? "dump_debug_start_outcome_unknown"
                        : result.UiNavigationIssued && !commandIssued
                            ? "ui_prepared_command_not_issued"
                            : "dump_debug_command_rejected";
                    TryPopulateDebugStatus(result, debugger);
                    return result;
                }

                result.CommandIssued = commandIssued;
                if (commandIssued)
                    MarkDebugSessionTransitionCommandIssued("start_dump_debugging");
                result.Success = true;
                result.Outcome = "dump_debug_start_requested";
                result.Capability = "official_vs_dump_command";
                result.Behavior = "debug_engine_selection_is_controlled_by_visual_studio";
                result.Message = "Requested Visual Studio to start debugging the dump file";
                TryPopulateDebugStatus(result, debugger);
            }
            catch (Exception ex)
            {
                if (!result.CommandIssued) ClearDumpSessionState();
                result.Success = false;
                result.ErrorCode = "OUTCOME_UNKNOWN";
                result.Outcome = result.CommandIssued || result.UiNavigationIssued
                    ? "dump_debug_start_outcome_unknown"
                    : "dump_debug_start_not_started";
                result.Message = ex.Message;
                TryPopulateDebugStatus(result, debugger);
            }

            return result;
        }

        private void OpenAndVerifyTargetDocument(string expectedPath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var window = _dte.ItemOperations.OpenFile(expectedPath);
            var document = window?.Document;
            if (window == null || document == null)
            {
                throw new InvalidOperationException("Visual Studio did not open the requested document.");
            }
            window.Activate();
            string openedPath = System.IO.Path.GetFullPath(document.FullName);
            string activePath = System.IO.Path.GetFullPath(_dte.ActiveDocument?.FullName ?? string.Empty);
            if (!string.Equals(openedPath, expectedPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(activePath, expectedPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Visual Studio did not activate the exact requested document; the debugger command was not issued.");
            }
        }

        private bool TryStartActiveDumpDebugging(Debugger5 debugger, ProcessDebugResult result)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string activeDocumentPath = null;
            try
            {
                activeDocumentPath = _dte?.ActiveDocument?.FullName;
            }
            catch
            {
                // ActiveDocument can be unavailable while VS is changing views.
            }

            if (!IsDumpFilePath(activeDocumentPath))
            {
                return false;
            }

            BeginPendingDumpSession(activeDocumentPath);
            result.ExecutablePath = activeDocumentPath;

            try
            {
                if (!TryStartActiveDumpDebuggingCommand(
                    out string startError,
                    out bool commandIssued,
                    out bool outcomeUnknown))
                {
                    result.CommandIssued = commandIssued;
                    if (commandIssued)
                        MarkDebugSessionTransitionCommandIssued("start_active_dump_debugging");
                    if (!outcomeUnknown) ClearDumpSessionState();
                    result.Success = false;
                    result.ErrorCode = outcomeUnknown ? "OUTCOME_UNKNOWN" : "DEBUG_OPERATION_FAILED";
                    result.Message = startError;
                    result.Outcome = outcomeUnknown
                        ? "dump_debug_start_outcome_unknown"
                        : "dump_debug_command_rejected";
                    TryPopulateDebugStatus(result, debugger);
                    return true;
                }

                result.CommandIssued = commandIssued;
                if (commandIssued)
                    MarkDebugSessionTransitionCommandIssued("start_active_dump_debugging");
                result.Success = true;
                result.Outcome = "dump_debug_start_requested";
                result.Capability = "official_vs_dump_command";
                result.Behavior = "debug_engine_selection_is_controlled_by_visual_studio";
                result.Message = "Requested Visual Studio to start debugging the active dump file";
                TryPopulateDebugStatus(result, debugger);
            }
            catch (Exception ex)
            {
                if (!result.CommandIssued) ClearDumpSessionState();
                result.Success = false;
                result.ErrorCode = "OUTCOME_UNKNOWN";
                result.Outcome = result.CommandIssued ? "dump_debug_start_outcome_unknown" : "dump_debug_start_not_started";
                result.Message = ex.Message;
                TryPopulateDebugStatus(result, debugger);
            }

            return true;
        }

        private bool TryStartActiveDumpDebuggingCommand(
            out string error,
            out bool commandIssued,
            out bool outcomeUnknown)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            error = null;
            commandIssued = false;
            outcomeUnknown = false;
            bool dispatchAttempted = false;

            try
            {
                var uiShell = ServiceProvider.GlobalProvider.GetService(typeof(SVsUIShell)) as IVsUIShell;
                if (uiShell == null)
                {
                    error = "Visual Studio shell service is unavailable";
                    return false;
                }

                Guid commandSet = VisualStudioDebuggerCommandSet;
                object argument = null;
                dispatchAttempted = true;
                int hr = CommandEffectExecutor.Issue(
                    "debugger.start_dump",
                    () =>
                    {
                        ThreadHelper.ThrowIfNotOnUIThread();
                        return uiShell.PostExecCommand(
                            ref commandSet,
                            DebugDumpFileCommandId,
                            0,
                            ref argument);
                    });
                if (hr < 0)
                {
                    error = $"Visual Studio rejected the official dump debugging command (HRESULT 0x{hr:X8})";
                    return false;
                }
                commandIssued = true;
                return true;
            }
            catch (Exception ex)
            {
                commandIssued = dispatchAttempted;
                outcomeUnknown = dispatchAttempted;
                error = "The official Visual Studio dump debugging command failed";
                _log($"Dump debugging command failed: {ex.GetType().Name}");
                return false;
            }
        }

        private bool IsDumpFilePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string extension = null;
            try
            {
                extension = System.IO.Path.GetExtension(path);
            }
            catch
            {
                return false;
            }

            return string.Equals(extension, ".dmp", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".mdmp", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".hdmp", StringComparison.OrdinalIgnoreCase);
        }

        [SuppressMessage("Usage", "VSTHRD010", Justification = "Reads one LocalProcesses entry per bounded UI slice and never carries a COM object across threads.")]
        private LocalProcessLookup FindLocalProcessSliced(int processId, string processName)
        {
            if (processId <= 0 && string.IsNullOrWhiteSpace(processName))
            {
                return new LocalProcessLookup { Message = "Either process_id or process_name is required" };
            }

            // A PID (with its process name derived from it) is the common case and resolves
            // with a single automation lookup; only a miss walks every local process.
            if (processId > 0)
            {
                string candidateName = TryGetProcessImageFileName(processId);
                if (!string.IsNullOrWhiteSpace(candidateName))
                {
                    LocalProcessLookup fast = ExecuteOnMainThread(() =>
                    {
                        ThreadHelper.ThrowIfNotOnUIThread();
                        try
                        {
                            EnvDTE.Process process = Debugger?.LocalProcesses?.Item(candidateName);
                            if (process == null || process.ProcessID != processId) return null;
                            return new LocalProcessLookup
                            {
                                Found = true,
                                ProcessId = processId,
                                ProcessName = process.Name,
                                ProcessStartTimeUtcTicks = TryGetProcessStartTimeUtcTicks(processId),
                                Process = process,
                            };
                        }
                        catch
                        {
                            return null;
                        }
                    }, DefaultMainThreadTimeoutMs);
                    if (fast != null) return fast;
                }
            }

            if (processId > 0)
            {
                LocalProcessLookup match = FindLocalProcessByPid(processId);
                return match ?? new LocalProcessLookup
                {
                    Message = $"Process with PID {processId} was not found in Visual Studio local processes",
                };
            }

            // Only a name needs the whole collection, to tell a single match from a duplicate.
            // The attach step re-verifies the chosen process through its OS handle, so one
            // snapshot is enough to pick a candidate here.
            LocalProcessSnapshot snapshot = CaptureLocalProcessSnapshot();
            List<LocalProcessIdentity> matches = snapshot.Items
                .Where(item => ProcessNameMatches(item.Name, processName))
                .ToList();
            if (matches.Count == 1)
            {
                // The snapshot only decides which process is meant; the attach target is then
                // resolved by PID, because a position in this collection does not survive the
                // Visual Studio startup churn that follows the capture.
                LocalProcessLookup match = FindLocalProcessByPid(matches[0].ProcessId);
                if (match != null)
                {
                    return match;
                }
                return new LocalProcessLookup
                {
                    Message = $"Process '{matches[0].Name}' ({matches[0].ProcessId}) exited before it could be attached",
                };
            }

            if (matches.Count > 1)
            {
                string details = string.Join(", ", matches.Take(16)
                    .Select(item => $"{item.Name} ({item.ProcessId})")
                    .ToArray());
                if (matches.Count > 16) details += $", ... {matches.Count - 16} more";
                return new LocalProcessLookup
                {
                    Message = $"Multiple processes named '{processName}' found. Retry with process_id. Matches: {details}",
                };
            }
            return new LocalProcessLookup
            {
                Message = $"Process named '{processName}' was not found in Visual Studio local processes",
            };
        }

        /// <summary>
        /// Resolve one local process by PID, in the same bounded slices the snapshot uses.
        ///
        /// The collection is a live view: Visual Studio starts and stops processes while it is
        /// enumerated, so a position captured by an earlier walk can point at a different
        /// process by the time the debugger attaches, and a name is not unique either (dozens
        /// of cmd.exe or conhost.exe share one). A PID is neither, and the caller still
        /// re-verifies the OS identity of the process it finally opens.
        /// </summary>
        private LocalProcessLookup FindLocalProcessByPid(int processId)
        {
            if (processId <= 0)
            {
                return null;
            }

            EnvDTE.Processes localProcesses = null;
            int count = ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                localProcesses = Debugger?.LocalProcesses;
                return localProcesses?.Count ?? 0;
            });
            if (localProcesses == null)
            {
                return null;
            }

            for (int first = 1; first <= count; first += LocalProcessCaptureBatchSize)
            {
                DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                int batchFirst = first;
                int batchLast = Math.Min(count, first + LocalProcessCaptureBatchSize - 1);
                LocalProcessLookup found = ExecuteOnMainThread(
                    () => FindLocalProcessInRange(localProcesses, processId, batchFirst, batchLast),
                    LocalProcessSnapshotTimeoutMs);
                if (found != null)
                {
                    return found;
                }
            }
            return null;
        }

        [SuppressMessage("Usage", "VSTHRD010", Justification = "Reads bounded LocalProcesses entries inside one UI slice.")]
        private static LocalProcessLookup FindLocalProcessInRange(
            EnvDTE.Processes localProcesses,
            int processId,
            int firstIndex,
            int lastIndex)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (localProcesses == null)
            {
                return null;
            }
            for (int index = firstIndex; index <= lastIndex; index++)
            {
                try
                {
                    EnvDTE.Process process = localProcesses.Item(index);
                    if (process != null && process.ProcessID == processId)
                    {
                        return new LocalProcessLookup
                        {
                            Found = true,
                            ProcessId = processId,
                            ProcessName = process.Name,
                            ProcessStartTimeUtcTicks = TryGetProcessStartTimeUtcTicks(processId),
                            Process = process,
                        };
                    }
                }
                catch
                {
                    // An entry whose process exited mid-scan is not addressable; keep scanning.
                }
            }
            return null;
        }

        private LocalProcessSnapshot CaptureLocalProcessSnapshot()
        {
            // The collection object is resolved once per snapshot and reused by every slice.
            // Resolving it per item costs ~200 ms through the automation layer, which turned a
            // ~600 process snapshot into minutes and made every attach look like a hang. The
            // capture stays sliced (bounded items per UI-thread hop) so the UI thread is never
            // monopolised and cancellation/deadline checks still run between slices.
            EnvDTE.Processes localProcesses = null;
            int initialCount = ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                localProcesses = Debugger?.LocalProcesses;
                return localProcesses?.Count ?? 0;
            });
            if (localProcesses == null)
            {
                throw new StaleDebugSessionException("Visual Studio local processes are unavailable.");
            }

            _log($"local process snapshot: count={initialCount}; enumerating");
            LocalProcessSnapshot cached = GetCachedLocalProcessSnapshot(initialCount);
            if (cached != null)
            {
                return cached;
            }

            var snapshot = new LocalProcessSnapshot();
            for (int first = 1; first <= initialCount; first += LocalProcessCaptureBatchSize)
            {
                DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                int batchFirst = first;
                int batchLast = Math.Min(initialCount, first + LocalProcessCaptureBatchSize - 1);
                List<LocalProcessIdentity> batch = ExecuteOnMainThread(
                    () => CaptureLocalProcessIdentityRange(localProcesses, batchFirst, batchLast),
                    LocalProcessSnapshotTimeoutMs);
                if (batch == null || batch.Count != batchLast - batchFirst + 1)
                {
                    throw new StaleDebugSessionException(
                        "Visual Studio local processes changed while being enumerated.");
                }
                snapshot.Items.AddRange(batch);
            }

            int finalCount = ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return localProcesses.Count;
            });
            if (finalCount != initialCount)
            {
                throw new StaleDebugSessionException(
                    "Visual Studio local process count changed while being enumerated.");
            }
            _log($"local process snapshot: complete ({initialCount} processes)");
            CacheLocalProcessSnapshot(snapshot, initialCount);
            return snapshot;
        }

        [SuppressMessage("Usage", "VSTHRD010", Justification = "Reads bounded LocalProcesses entries inside one UI slice.")]
        private static List<LocalProcessIdentity> CaptureLocalProcessIdentityRange(
            EnvDTE.Processes localProcesses,
            int firstIndex,
            int lastIndex)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var items = new List<LocalProcessIdentity>(Math.Max(0, lastIndex - firstIndex + 1));
            for (int index = firstIndex; index <= lastIndex; index++)
            {
                items.Add(CaptureLocalProcessIdentity(localProcesses, index));
            }
            return items;
        }

        [SuppressMessage("Usage", "VSTHRD010", Justification = "Reads one LocalProcesses entry inside a bounded UI slice.")]
        private static LocalProcessIdentity CaptureLocalProcessIdentity(EnvDTE.Processes localProcesses, int index)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                EnvDTE.Process process = localProcesses?.Item(index);
                int processId = process?.ProcessID ?? 0;
                long processStartTimeUtcTicks = TryGetProcessStartTimeUtcTicks(processId);
                return process == null
                    ? null
                    : new LocalProcessIdentity
                    {
                        ProcessId = processId,
                        Name = process.Name,
                        ProcessStartTimeUtcTicks = processStartTimeUtcTicks,
                    };
            }
            catch
            {
                return null;
            }
        }

        private LocalProcessSnapshot GetCachedLocalProcessSnapshot(int currentCount)
        {
            lock (_localProcessSnapshotLock)
            {
                if (_cachedLocalProcessSnapshot == null || _cachedLocalProcessCount != currentCount ||
                    (DateTime.UtcNow - _cachedLocalProcessSnapshotUtc).TotalMilliseconds > LocalProcessSnapshotCacheMs)
                {
                    return null;
                }
                return _cachedLocalProcessSnapshot;
            }
        }

        private void CacheLocalProcessSnapshot(LocalProcessSnapshot snapshot, int count)
        {
            lock (_localProcessSnapshotLock)
            {
                _cachedLocalProcessSnapshot = snapshot;
                _cachedLocalProcessCount = count;
                _cachedLocalProcessSnapshotUtc = DateTime.UtcNow;
            }
        }

        private static string TryGetProcessImageFileName(int processId)
        {
            try
            {
                using (System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(processId))
                {
                    string name = process.ProcessName;
                    return string.IsNullOrWhiteSpace(name) ? null : name + ".exe";
                }
            }
            catch
            {
                return null;
            }
        }

        private IntPtr OpenVerifiedLocalProcess(
            Debugger5 debugger,
            LocalProcessLookup lookup,
            out EnvDTE.Process verifiedProcess)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            verifiedProcess = null;
            if (lookup == null) return IntPtr.Zero;
            try
            {
                // Every discovery path resolves the automation object for the PID off the UI
                // thread. A position in the collection is not reusable here: the collection is
                // live, and re-adding it as a fallback only reintroduced the reordering bug.
                EnvDTE.Process process = lookup.Process;
                if (process == null || process.ProcessID != lookup.ProcessId)
                {
                    return IntPtr.Zero;
                }
                IntPtr handle = OpenVerifiedProcessHandle(lookup.ProcessId, lookup.ProcessStartTimeUtcTicks);
                if (handle != IntPtr.Zero)
                {
                    verifiedProcess = process;
                }
                return handle;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        private static IntPtr OpenVerifiedProcessHandle(int processId, long expectedProcessStartTimeUtcTicks)
        {
            if (processId <= 0 || expectedProcessStartTimeUtcTicks <= 0)
            {
                return IntPtr.Zero;
            }
            IntPtr handle = OpenProcess(
                PROCESS_QUERY_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION,
                false,
                processId);
            if (handle == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }
            if (!TryGetProcessStartTimeUtcTicks(handle, out long actualTicks) ||
                actualTicks != expectedProcessStartTimeUtcTicks)
            {
                CloseHandle(handle);
                return IntPtr.Zero;
            }
            return handle;
        }

        private static long TryGetProcessStartTimeUtcTicks(int processId)
        {
            IntPtr handle = OpenProcess(
                PROCESS_QUERY_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION,
                false,
                processId);
            if (handle == IntPtr.Zero)
            {
                return 0;
            }
            try
            {
                return TryGetProcessStartTimeUtcTicks(handle, out long ticks) ? ticks : 0;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private static bool TryGetProcessStartTimeUtcTicks(IntPtr processHandle, out long ticks)
        {
            ticks = 0;
            if (!GetProcessTimes(
                processHandle,
                out System.Runtime.InteropServices.ComTypes.FILETIME creation,
                out _,
                out _,
                out _))
            {
                return false;
            }
            long fileTime = ((long)creation.dwHighDateTime << 32) | (uint)creation.dwLowDateTime;
            try
            {
                ticks = DateTime.FromFileTimeUtc(fileTime).Ticks;
                return ticks > 0;
            }
            catch
            {
                return false;
            }
        }

        [SuppressMessage("Usage", "VSTHRD010", Justification = "Local process lookup self-marshals one collection item per UI slice.")]
        private LocalProcessLookup WaitForLocalProcessOffUiThread(
            int processId,
            long expectedProcessStartTimeUtcTicks,
            int timeoutMs)
        {
            if (ThreadHelper.CheckAccess())
            {
                throw new InvalidOperationException("Process discovery waits are not allowed on the Visual Studio UI thread");
            }

            int effectiveTimeoutMs = DebuggerRequestContext.RemainingMilliseconds(timeoutMs);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < effectiveTimeoutMs)
            {
                DebuggerRequestContext.CancellationToken.ThrowIfCancellationRequested();
                LocalProcessLookup process = FindLocalProcessSliced(processId, null);
                if (process.Found &&
                    process.ProcessStartTimeUtcTicks == expectedProcessStartTimeUtcTicks)
                {
                    return process;
                }

                int remaining = effectiveTimeoutMs - (int)Math.Min(effectiveTimeoutMs, sw.ElapsedMilliseconds);
                if (remaining > 0)
                {
                    if (DebuggerRequestContext.CancellationToken.WaitHandle.WaitOne(Math.Min(250, remaining)))
                    {
                        DebuggerRequestContext.CancellationToken.ThrowIfCancellationRequested();
                    }
                }
            }

            return null;
        }

        private sealed class LocalProcessSnapshot
        {
            internal List<LocalProcessIdentity> Items { get; } = new List<LocalProcessIdentity>();
        }

        private sealed class LocalProcessIdentity
        {
            internal int ProcessId { get; set; }
            internal string Name { get; set; }
            internal long ProcessStartTimeUtcTicks { get; set; }
        }

        private sealed class LocalProcessLookup
        {
            internal bool Found { get; set; }
            // The automation object for the PID, resolved on the UI thread by the discovery
            // step and reused by the attach step.
            internal EnvDTE.Process Process { get; set; }
            internal int ProcessId { get; set; }
            internal string ProcessName { get; set; }
            internal long ProcessStartTimeUtcTicks { get; set; }
            internal string Message { get; set; }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessTimes(
            IntPtr processHandle,
            out System.Runtime.InteropServices.ComTypes.FILETIME creationTime,
            out System.Runtime.InteropServices.ComTypes.FILETIME exitTime,
            out System.Runtime.InteropServices.ComTypes.FILETIME kernelTime,
            out System.Runtime.InteropServices.ComTypes.FILETIME userTime);

        private static bool ProcessNameMatches(string candidateName, string requestedName)
        {
            if (string.IsNullOrWhiteSpace(candidateName) || string.IsNullOrWhiteSpace(requestedName))
            {
                return false;
            }

            string candidateFileName = System.IO.Path.GetFileName(candidateName);
            string requestedFileName = System.IO.Path.GetFileName(requestedName);
            string candidateWithoutExtension = System.IO.Path.GetFileNameWithoutExtension(candidateFileName);
            string requestedWithoutExtension = System.IO.Path.GetFileNameWithoutExtension(requestedFileName);

            return string.Equals(candidateFileName, requestedFileName, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(candidateWithoutExtension, requestedWithoutExtension, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Continue execution (F5).
        /// </summary>
        public DebugControlResult Continue()
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new DebugControlResult { Action = "Continue" };

                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    result.Success = false;
                    result.Message = "Not in break mode";
                    return result;
                }

                try
                {
                    result.CommandIssued = true;
                    try
                    {
                        CommandEffectExecutor.Issue(
                            "debugger.continue",
                            () =>
                            {
                                ThreadHelper.ThrowIfNotOnUIThread();
                                debugger.Go(false);
                            }); // false = don't wait for break
                    }
                    finally
                    {
                        MarkExecutionStateCommandIssued("continue");
                    }
                    result.Success = true;
                    result.Outcome = "continued";
                    result.Message = "Execution continued";
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.ErrorCode = "OUTCOME_UNKNOWN";
                    result.Outcome = result.CommandIssued ? "continue_outcome_unknown" : "continue_not_started";
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Continues execution if needed, waits for the debugger to stop again, and captures a debug bundle.
        /// </summary>
        private static T ExecuteLongWaitPhase<T>(
            Func<T> operation,
            Func<Func<object>, object> phaseExecutor)
        {
            return phaseExecutor == null
                ? operation()
                : (T)phaseExecutor(() => operation());
        }

        [SuppressMessage("Usage", "VSTHRD010", Justification = "Composes self-marshalling debugger operations with an off-UI event wait.")]
        public ContinueAndWaitResult ContinueAndWaitForBreak(
            List<string> expressions = null,
            bool expandChildren = false,
            int maxDepth = 2,
            int outputTailLines = 80,
            int timeoutMs = 120000,
            int pollIntervalMs = 200,
            bool includeLocals = false,
            bool includeModules = false,
            bool includeRegisters = false,
            bool includeOutput = false,
            bool includeThreads = true,
            bool includeException = true,
            bool threadIncludeFrames = true,
            int threadMaxFramesPerThread = -1,
            int threadMaxNodes = -1,
            int threadBudgetMs = -1,
            Func<Func<object>, object> phaseExecutor = null,
            bool captureBundle = true)
        {
            int normalizedTimeoutMs = Math.Max(1000, Math.Min(timeoutMs, 30 * 60 * 1000));
            int normalizedPollIntervalMs = Math.Max(50, Math.Min(pollIntervalMs, 1000));
            var initialEvent = _sessionTracker.GetStopEventSnapshot();
            var initialStatus = ExecuteLongWaitPhase(GetDebugStatus, phaseExecutor);
            var result = new ContinueAndWaitResult
            {
                Action = "ContinueAndWaitForBreak",
                WaitTimeoutMs = normalizedTimeoutMs,
                PollIntervalMs = normalizedPollIntervalMs,
                Status = initialStatus,
                StopReason = initialStatus?.StopReason ?? "not_paused"
            };

            if (initialStatus == null || !initialStatus.IsDebugging)
            {
                result.Success = false;
                result.ErrorCode = "NOT_DEBUGGING";
                result.Message = "No active debug session";
                return result;
            }

            if (initialStatus.IsPaused)
            {
                var continueResult = ExecuteLongWaitPhase(Continue, phaseExecutor);
                result.ContinueOperation = continueResult;
                result.IssuedContinue = continueResult.CommandIssued;
                if (!continueResult.Success)
                {
                    result.Success = false;
                    result.ErrorCode = continueResult.ErrorCode;
                    result.Message = continueResult.Message;
                    RebaseRequestToCurrentDebuggerIdentity();
                    result.Status = ExecuteLongWaitPhase(GetDebugStatus, phaseExecutor);
                    result.StopReason = result.Status?.StopReason ?? "not_paused";
                    return result;
                }
            }

            int effectiveWaitMs = DebuggerRequestContext.RemainingMilliseconds(normalizedTimeoutMs);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            StopEventSnapshot stopEvent = _sessionTracker.WaitForBreakOrDesign(
                initialEvent.Generation,
                initialEvent.BreakSequence,
                effectiveWaitMs,
                DebuggerRequestContext.CancellationToken);
            result.WaitedMs = (int)Math.Min(sw.ElapsedMilliseconds, int.MaxValue);

            if (stopEvent != null && string.Equals(stopEvent.Mode, "design", StringComparison.Ordinal))
            {
                DebuggerRequestContext.RebaseDebuggerIdentity(
                    stopEvent.Generation,
                    stopEvent.BreakSequence,
                    stopEvent.ContextEpoch);
                result.Status = ExecuteLongWaitPhase(GetDebugStatus, phaseExecutor);
                result.StopReason = "not_paused";
                result.Success = false;
                result.ErrorCode = "DEBUG_SESSION_ENDED";
                result.Message = "Debug session ended before hitting a breakpoint";
                return result;
            }

            if (stopEvent != null && stopEvent.Generation != initialEvent.Generation)
            {
                DebuggerRequestContext.RebaseDebuggerIdentity(
                    stopEvent.Generation,
                    stopEvent.BreakSequence,
                    stopEvent.ContextEpoch);
                result.Status = ExecuteLongWaitPhase(GetDebugStatus, phaseExecutor);
                result.StopReason = result.Status?.StopReason ?? "not_paused";
                result.Success = false;
                result.ErrorCode = "STALE_DEBUG_SESSION";
                result.Message = "Debug session changed while waiting; no context was captured from the replacement session";
                return result;
            }

            if (stopEvent != null && string.Equals(stopEvent.Mode, "break", StringComparison.Ordinal))
            {
                DebuggerRequestContext.RebaseDebuggerIdentity(
                    stopEvent.Generation,
                    stopEvent.BreakSequence,
                    stopEvent.ContextEpoch);
                if (!captureBundle)
                {
                    result.Success = true;
                    result.BreakDetected = true;
                    result.TimedOut = false;
                    result.Status = ExecuteLongWaitPhase(GetDebugStatus, phaseExecutor);
                    result.StopReason = result.Status?.StopReason ?? stopEvent.StopReason ?? "pause_unknown";
                    result.Message = "Debug stop detected";
                    return result;
                }
                var bundle = ExecuteLongWaitPhase(() => CaptureDebugBundle(
                        expressions,
                        expandChildren,
                        maxDepth,
                        outputTailLines,
                        includeLocals,
                        includeModules,
                        includeRegisters,
                        includeOutput,
                        includeThreads,
                        includeException,
                        threadIncludeFrames,
                        threadMaxFramesPerThread,
                        threadMaxNodes,
                        threadBudgetMs),
                    phaseExecutor);
                result.Success = true;
                result.BreakDetected = true;
                result.TimedOut = false;
                result.Bundle = bundle;
                result.Status = bundle?.Status ?? ExecuteLongWaitPhase(GetDebugStatus, phaseExecutor);
                result.StopReason = bundle?.StopReason ?? stopEvent.StopReason ?? "pause_unknown";
                result.Message = "Debug stop detected and bundle captured";
                return result;
            }

            RebaseRequestToCurrentDebuggerIdentity();
            result.Status = ExecuteLongWaitPhase(GetDebugStatus, phaseExecutor);
            result.StopReason = result.Status?.StopReason ?? "not_paused";
            result.Success = false;
            result.TimedOut = true;
            result.ErrorCode = "DEBUG_WAIT_TIMEOUT";
            result.Message = "Timed out waiting for debugger to enter break mode";
            return result;
        }

        /// <summary>
        /// Step into (F11).
        /// </summary>
        [SuppressMessage("Usage", "VSTHRD010", Justification = "Marshals the EnvDTE step and waits for the debugger event off the UI thread.")]
        public DebugControlResult StepInto(int waitTimeoutMs = 2000)
        {
            return ExecuteStepAndCapture("StepInto", "Stepped into", waitTimeoutMs);
        }

        /// <summary>
        /// Step over (F10).
        /// </summary>
        [SuppressMessage("Usage", "VSTHRD010", Justification = "Marshals the EnvDTE step and waits for the debugger event off the UI thread.")]
        public DebugControlResult StepOver(int waitTimeoutMs = 2000)
        {
            return ExecuteStepAndCapture("StepOver", "Stepped over", waitTimeoutMs);
        }

        /// <summary>
        /// Step out (Shift+F11).
        /// </summary>
        [SuppressMessage("Usage", "VSTHRD010", Justification = "Marshals the EnvDTE step and waits for the debugger event off the UI thread.")]
        public DebugControlResult StepOut(int waitTimeoutMs = 2000)
        {
            return ExecuteStepAndCapture("StepOut", "Stepped out", waitTimeoutMs);
        }

        private DebugControlResult ExecuteStepAndCapture(
            string action,
            string successMessage,
            int waitTimeoutMs)
        {
            StopEventSnapshot before = _sessionTracker.GetStopEventSnapshot();
            var result = ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var issued = new DebugControlResult { Action = action };
                if (debugger == null || debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    issued.Success = false;
                    issued.Message = "Not in break mode";
                    return issued;
                }

                issued.CommandIssued = true;
                try
                {
                    try
                    {
                        CommandEffectExecutor.Issue("debugger.step:" + action, () =>
                        {
                            ThreadHelper.ThrowIfNotOnUIThread();
                            switch (action)
                            {
                                case "StepInto":
                                    debugger.StepInto(false);
                                    break;
                                case "StepOver":
                                    debugger.StepOver(false);
                                    break;
                                case "StepOut":
                                    debugger.StepOut(false);
                                    break;
                                default:
                                    throw new InvalidOperationException("Unknown debugger step action: " + action);
                            }
                        });
                    }
                    finally
                    {
                        MarkExecutionStateCommandIssued("step:" + action);
                    }
                    issued.Success = true;
                    issued.Outcome = "step_issued";
                    issued.Message = successMessage;
                }
                catch (Exception ex)
                {
                    issued.Success = false;
                    issued.ErrorCode = "OUTCOME_UNKNOWN";
                    issued.Outcome = "step_outcome_unknown";
                    issued.Message = ex.Message;
                }
                return issued;
            });

            if (!result.Success)
            {
                return result;
            }

            try
            {
                int normalizedWaitTimeoutMs = Math.Max(100, Math.Min(waitTimeoutMs, 300000));
                int waitMs = DebuggerRequestContext.RemainingMilliseconds(normalizedWaitTimeoutMs);
                StopEventSnapshot stop = _sessionTracker.WaitForBreakOrDesign(
                    before.Generation,
                    before.BreakSequence,
                    waitMs,
                    DebuggerRequestContext.CancellationToken);
                if (stop != null && string.Equals(stop.Mode, "design", StringComparison.Ordinal))
                {
                    DebuggerRequestContext.RebaseDebuggerIdentity(
                        stop.Generation,
                        stop.BreakSequence,
                        stop.ContextEpoch);
                    result.Success = false;
                    result.ErrorCode = "DEBUG_SESSION_ENDED";
                    result.Outcome = "step_issued_session_ended";
                    result.Message = successMessage + "; the debug session ended before the next stop";
                    return result;
                }
                if (stop != null && stop.Generation != before.Generation)
                {
                    DebuggerRequestContext.RebaseDebuggerIdentity(
                        stop.Generation,
                        stop.BreakSequence,
                        stop.ContextEpoch);
                    result.Success = false;
                    result.ErrorCode = "STALE_DEBUG_SESSION";
                    result.Outcome = "step_issued_session_changed";
                    result.Message = successMessage + "; the debug session changed before follow-up capture";
                    return result;
                }
                if (stop == null || !string.Equals(stop.Mode, "break", StringComparison.Ordinal))
                {
                    result.Outcome = "step_issued_break_not_observed";
                    result.Message = successMessage + "; the next break event was not observed before the deadline";
                    return result;
                }

                DebuggerRequestContext.RebaseDebuggerIdentity(
                    stop.Generation,
                    stop.BreakSequence,
                    stop.ContextEpoch);

                result.NewLocation = ExecuteOnMainThread(() =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    EnvDTE.StackFrame frame = Debugger?.CurrentStackFrame;
                    return CreateSourceLocation(frame);
                });
                result.Outcome = "step_completed";
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorCode = "OUTCOME_UNKNOWN";
                result.Outcome = "step_issued_post_capture_failed";
                result.Message = $"{successMessage} was issued, but follow-up capture failed: {ex.Message}";
                return result;
            }
        }

        /// <summary>
        /// Break execution (Ctrl+Alt+Break).
        /// </summary>
        public DebugControlResult Break()
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new DebugControlResult { Action = "Break" };

                if (debugger.CurrentMode != dbgDebugMode.dbgRunMode)
                {
                    result.Success = false;
                    result.Message = "Not running";
                    return result;
                }

                try
                {
                    result.CommandIssued = true;
                    try
                    {
                        CommandEffectExecutor.Issue(
                            "debugger.break",
                            () =>
                            {
                                ThreadHelper.ThrowIfNotOnUIThread();
                                debugger.Break(false);
                            });
                    }
                    finally
                    {
                        MarkExecutionStateCommandIssued("break");
                    }
                    result.Success = true;
                    result.Outcome = "break_requested";
                    result.Message = "Execution broken";
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.ErrorCode = "OUTCOME_UNKNOWN";
                    result.Outcome = result.CommandIssued ? "break_outcome_unknown" : "break_not_started";
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Stop debugging (Shift+F5).
        /// </summary>
        public DebugControlResult Stop()
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new DebugControlResult { Action = "Stop" };

                if (debugger.CurrentMode == dbgDebugMode.dbgDesignMode)
                {
                    result.Success = false;
                    result.Message = "Not debugging";
                    return result;
                }

                try
                {
                    result.CommandIssued = true;
                    try
                    {
                        CommandEffectExecutor.Issue(
                            "debugger.stop",
                            () =>
                            {
                                ThreadHelper.ThrowIfNotOnUIThread();
                                debugger.Stop(false);
                            });
                    }
                    finally
                    {
                        MarkDebugSessionTransitionCommandIssued("stop");
                    }
                    result.Success = true;
                    result.Outcome = "stopped";
                    result.Message = "Debugging stopped";
                    ClearDumpSessionState();
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.ErrorCode = "OUTCOME_UNKNOWN";
                    result.Outcome = result.CommandIssued ? "stop_outcome_unknown" : "stop_not_started";
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Detach the debugger from the current process without terminating it.
        /// </summary>
        public DebugControlResult Detach()
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new DebugControlResult { Action = "Detach" };

                if (debugger == null || debugger.CurrentMode == dbgDebugMode.dbgDesignMode)
                {
                    result.Success = false;
                    result.Message = "Not debugging";
                    return result;
                }

                var process = debugger.CurrentProcess;
                if (process == null)
                {
                    result.Success = false;
                    result.Message = "No current debugged process";
                    return result;
                }

                try
                {
                    string processName = process.Name;
                    int processId = process.ProcessID;
                    result.CommandIssued = true;
                    try
                    {
                        CommandEffectExecutor.Issue(
                            "debugger.detach_process",
                            () =>
                            {
                                ThreadHelper.ThrowIfNotOnUIThread();
                                process.Detach(false);
                            });
                    }
                    finally
                    {
                        MarkDebugSessionTransitionCommandIssued("detach");
                    }
                    result.Success = true;
                    result.Outcome = "detached";
                    result.Message = $"Debugger detached from {processName} ({processId})";
                    ClearDumpSessionState();
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.ErrorCode = "OUTCOME_UNKNOWN";
                    result.Outcome = result.CommandIssued ? "detach_outcome_unknown" : "detach_not_started";
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Run to cursor location.
        /// </summary>
        public DebugControlResult RunToCursor(string filePath, int lineNumber)
        {
            var validation = ValidateTargetLocation(filePath, lineNumber);
            if (!validation.IsValid)
            {
                return new DebugControlResult
                {
                    Action = "RunToCursor",
                    Success = false,
                    Message = validation.ErrorMessage
                };
            }
            filePath = System.IO.Path.GetFullPath(filePath);

            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new DebugControlResult { Action = "RunToCursor" };

                try
                {
                    result.UiNavigationIssued = true;
                    CommandEffectExecutor.Issue(
                        "visual_studio.position_command_target",
                        () => PositionDebuggerCommandTarget(filePath, lineNumber));

                    result.CommandIssued = true;
                    try
                    {
                        CommandEffectExecutor.Issue(
                            "debugger.run_to_cursor",
                            () =>
                            {
                                ThreadHelper.ThrowIfNotOnUIThread();
                                debugger.RunToCursor(false);
                            });
                    }
                    finally
                    {
                        MarkExecutionStateCommandIssued("run_to_cursor");
                    }
                    result.Success = true;
                    result.Outcome = "run_to_cursor_requested";
                    result.Message = $"Running to {filePath}:{lineNumber}";
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.ErrorCode = "OUTCOME_UNKNOWN";
                    result.Outcome = result.CommandIssued || result.UiNavigationIssued
                        ? "run_to_cursor_outcome_unknown"
                        : "run_to_cursor_not_started";
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Set next statement (move instruction pointer).
        /// </summary>
        public DebugControlResult SetNextStatement(string filePath, int lineNumber)
        {
            var validation = ValidateTargetLocation(filePath, lineNumber);
            if (!validation.IsValid)
            {
                return new DebugControlResult
                {
                    Action = "SetNextStatement",
                    Success = false,
                    Message = validation.ErrorMessage
                };
            }
            filePath = System.IO.Path.GetFullPath(filePath);

            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new DebugControlResult { Action = "SetNextStatement" };

                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    result.Success = false;
                    result.Message = "Not in break mode";
                    return result;
                }

                try
                {
                    result.UiNavigationIssued = true;
                    CommandEffectExecutor.Issue(
                        "visual_studio.position_command_target",
                        () => PositionDebuggerCommandTarget(filePath, lineNumber));

                    result.CommandIssued = true;
                    try
                    {
                        CommandEffectExecutor.Issue(
                            "debugger.set_next_statement",
                            debugger.SetNextStatement);
                    }
                    finally
                    {
                        // EnvDTE may throw after the engine has already moved the
                        // instruction pointer. Invalidate every old frame handle once
                        // dispatch begins, regardless of the returned HRESULT.
                        _sessionTracker.InvalidateContext("set_next_statement");
                        RebaseRequestToCurrentDebuggerIdentity();
                    }
                    result.Success = true;
                    result.Outcome = "next_statement_set";
                    result.Message = $"Next statement set to {filePath}:{lineNumber}";
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.ErrorCode = "OUTCOME_UNKNOWN";
                    result.Outcome = result.CommandIssued || result.UiNavigationIssued
                        ? "set_next_statement_outcome_unknown"
                        : "set_next_statement_not_started";
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        private void PositionDebuggerCommandTarget(string filePath, int lineNumber)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var window = _dte.ItemOperations.OpenFile(filePath);
            var document = window?.Document;
            if (window == null || document == null)
            {
                throw new InvalidOperationException("Visual Studio did not open the requested source document.");
            }
            string openedPath;
            try
            {
                openedPath = System.IO.Path.GetFullPath(document.FullName);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Visual Studio returned an invalid source document path.", ex);
            }
            if (!string.Equals(openedPath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Visual Studio opened '{openedPath}' instead of the requested target '{filePath}'.");
            }
            var textDocument = document.Object("TextDocument") as EnvDTE.TextDocument;
            if (textDocument == null)
            {
                throw new InvalidOperationException("The requested source document has no text editor buffer.");
            }
            window.Activate();
            var editPoint = textDocument.StartPoint.CreateEditPoint();
            editPoint.MoveToLineAndOffset(lineNumber, 1);
            textDocument.Selection.MoveToPoint(editPoint);
            if (textDocument.Selection.ActivePoint == null ||
                textDocument.Selection.ActivePoint.Line != lineNumber ||
                !string.Equals(
                    System.IO.Path.GetFullPath(window.Document?.FullName ?? string.Empty),
                    filePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Visual Studio did not activate the exact requested source line.");
            }
        }

        /// <summary>
        /// Validate that the target file and line are valid execution targets.
        /// </summary>
        private (bool IsValid, string ErrorMessage) ValidateTargetLocation(string filePath, int lineNumber)
        {
            SourceLocationPreflight source = InspectSourceLocation(filePath, lineNumber, requireExecutableText: true);
            if (!source.IsValid)
            {
                return (false, source.ErrorMessage);
            }
            string targetLine = source.TargetLine.Trim();

            // Best-effort precheck for lines that VS will normally reject as execution points.
            string extension = System.IO.Path.GetExtension(filePath).ToLowerInvariant();

            // C# / C++ style comments
            if (targetLine.StartsWith("//"))
            {
                return (false, $"Line {lineNumber} is a comment - cannot set execution point on comment line");
            }

            // Check for opening/closing braces only
            if (targetLine == "{" || targetLine == "}" || targetLine == "{}" ||
                targetLine == "};")
            {
                // Allow brace-only lines - they can be valid execution points in some cases
                // Just warn but don't reject
            }

            // C# specific: check for using directives (not using declarations/statements), namespace declarations, etc.
            if (extension == ".cs")
            {
                // Only reject top-level "using Namespace;" directives, not "using var x = ..." declarations
                if (targetLine.StartsWith("using ") && targetLine.EndsWith(";") && !targetLine.Contains("=") && !targetLine.Contains("("))
                {
                    return (false, $"Line {lineNumber} is a using directive - cannot set execution point on using statement");
                }

                if (targetLine.StartsWith("namespace "))
                {
                    return (false, $"Line {lineNumber} is a namespace declaration - cannot set execution point on namespace");
                }
            }

            return (true, null);
        }

        #endregion
    }
}
