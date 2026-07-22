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
        #region Debug Process Navigation

        [SuppressMessage("Usage", "VSTHRD010", Justification = "Each DebuggedProcesses COM access is executed inside a bounded ExecuteOnMainThread callback.")]
        public DebugProcessesResult GetDebugProcesses(
            int startIndex = 0,
            int maxNodes = -1,
            int budgetMs = -1)
        {
            if (startIndex < 0) throw new ArgumentOutOfRangeException(nameof(startIndex));
            int normalizedMaxNodes = Math.Max(1, NormalizeCollectionNodeLimit(maxNodes));
            int normalizedBudgetMs = NormalizeCollectionBudgetMs(budgetMs);
            var result = new DebugProcessesResult
            {
                Capability = "same_debug_session_process_selection",
                Behavior = "visual_studio_debugged_processes_only",
                StartIndex = startIndex,
                NextIndex = startIndex,
                MaxNodes = normalizedMaxNodes,
                BudgetMs = normalizedBudgetMs,
                SnapshotStatus = "unavailable",
            };

            DebugProcessCollectionHeader header = ExecuteOnMainThread(CaptureDebugProcessCollectionHeader);
            result.CurrentProcessId = header.CurrentProcessId;
            result.TotalAvailable = header.Count;
            if (!header.Available)
            {
                result.Error = header.Error;
                return result;
            }

            var stopwatch = Stopwatch.StartNew();
            for (int offset = startIndex; offset < header.Count; offset++)
            {
                DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                if (stopwatch.ElapsedMilliseconds >= normalizedBudgetMs ||
                    result.Processes.Count >= normalizedMaxNodes)
                {
                    result.Partial = true;
                    result.Error = AppendError(
                        result.Error,
                        stopwatch.ElapsedMilliseconds >= normalizedBudgetMs
                            ? $"Debug process snapshot exceeded its {normalizedBudgetMs}ms budget."
                            : $"Debug process snapshot reached maxNodes={normalizedMaxNodes}.");
                    break;
                }

                int collectionIndex = offset + 1;
                DebugProcessCapture capture;
                try
                {
                    capture = ExecuteOnMainThread(
                        () => CaptureDebugProcess(collectionIndex, header.CurrentProcessId),
                        DefaultMainThreadTimeoutMs);
                }
                catch (Exception ex)
                {
                    RethrowRequestControlFlow(ex);
                    result.Partial = true;
                    result.Error = AppendError(result.Error, $"process index {offset}: {ex.Message}");
                    result.NextIndex = offset + 1;
                    continue;
                }

                if (capture?.Process == null)
                {
                    result.Partial = true;
                    result.Error = AppendError(
                        result.Error,
                        capture?.Error ?? $"process index {offset}: unavailable");
                }
                else
                {
                    result.Processes.Add(capture.Process);
                }
                result.NextIndex = offset + 1;
            }

            result.HasMore = result.NextIndex < header.Count;
            if (result.HasMore)
            {
                result.Partial = true;
                result.Error = AppendError(
                    result.Error,
                    $"Continue at start_index={result.NextIndex}.");
            }

            DebugProcessCollectionHeader finalHeader = ExecuteOnMainThread(CaptureDebugProcessCollectionHeader);
            result.SnapshotStable = finalHeader.Available &&
                finalHeader.Count == header.Count &&
                finalHeader.CurrentProcessId == header.CurrentProcessId &&
                string.Equals(finalHeader.SnapshotToken, header.SnapshotToken, StringComparison.Ordinal);
            if (!result.SnapshotStable)
            {
                result.Partial = true;
                result.Error = AppendError(
                    result.Error,
                    "The debug process collection or current debugger context changed during capture.");
            }
            result.SnapshotStatus = result.SnapshotStable
                ? (result.Partial ? "partial" : "ok")
                : (result.Processes.Count > 0 ? "unstable" : "unavailable");
            return result;
        }

        /// <summary>
        /// Switch the current Visual Studio process without attaching a new target.
        /// </summary>
        [SuppressMessage("Usage", "VSTHRD010", Justification = "Process lookup is sliced and the final CurrentProcess assignment runs on the UI thread.")]
        public ProcessDebugResult SwitchProcess(int processId, int processLookupBudgetMs = -1)
        {
            int processIndex;
            try
            {
                processIndex = FindDebuggedProcessIndexByIdSliced(processId, processLookupBudgetMs);
            }
            catch (DebugContextNotFoundException ex)
            {
                return CreateProcessSelectionResult(
                    processId,
                    false,
                    "DEBUG_CONTEXT_NOT_FOUND",
                    "switch_process_not_started",
                    ex.Message);
            }

            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = CreateProcessSelectionResult(
                    processId,
                    false,
                    null,
                    "switch_process_not_started",
                    string.Empty);

                if (debugger == null || debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    result.ErrorCode = "NOT_PAUSED";
                    result.Message = "Process selection requires Visual Studio break mode.";
                    return result;
                }

                try
                {
                    EnvDTE.Process targetProcess = debugger.DebuggedProcesses?.Item(processIndex);
                    if (targetProcess == null || targetProcess.ProcessID != processId)
                    {
                        throw new StaleDebugContextException(
                            $"Debugged process {processId} changed before it could be selected.");
                    }

                    result.ProcessName = targetProcess.Name ?? $"Process {processId}";
                    if (debugger.CurrentProcess?.ProcessID == processId)
                    {
                        result.Success = true;
                        result.Outcome = "process_already_selected";
                        result.Message = $"Process {result.ProcessName} ({processId}) is already selected.";
                        result.Status = BuildDebugStatus(debugger);
                        return result;
                    }

                    result.CommandIssued = true;
                    DebuggerRequestContext.MarkSideEffectIssued();
                    debugger.CurrentProcess = targetProcess;
                    _sessionTracker.InvalidateContext("switch_process");
                    ObserveDebugContext(debugger);
                    RebaseRequestToCurrentDebuggerIdentity();

                    EnvDTE.Process selectedProcess = debugger.CurrentProcess;
                    if (selectedProcess == null || selectedProcess.ProcessID != processId)
                    {
                        result.ErrorCode = "OUTCOME_UNKNOWN";
                        result.Outcome = "switch_process_outcome_unknown";
                        result.Message = $"Visual Studio accepted process {processId}, but did not confirm it as the current process.";
                        return result;
                    }

                    result.Success = true;
                    result.Outcome = "process_selected";
                    result.Message = $"Switched to process {result.ProcessName} ({processId}).";
                    result.Status = BuildDebugStatus(debugger);
                }
                catch (Exception ex)
                {
                    RethrowRequestControlFlow(ex);
                    result.Success = false;
                    result.ErrorCode = result.CommandIssued ? "OUTCOME_UNKNOWN" : "DEBUG_CONTEXT_NOT_FOUND";
                    result.Outcome = result.CommandIssued
                        ? "switch_process_outcome_unknown"
                        : "switch_process_not_started";
                    result.Message = ex.Message;
                }
                return result;
            });
        }

        private ProcessDebugResult CreateProcessSelectionResult(
            int processId,
            bool success,
            string errorCode,
            string outcome,
            string message)
        {
            return new ProcessDebugResult
            {
                Action = "SwitchProcess",
                Capability = "same_debug_session_process_selection",
                Behavior = "select_existing_visual_studio_debugged_process",
                ProcessId = processId,
                Success = success,
                ErrorCode = errorCode,
                Outcome = outcome,
                Message = message,
            };
        }

        [SuppressMessage("Usage", "VSTHRD010", Justification = "Reads one DebuggedProcesses identity per bounded UI slice.")]
        private int FindDebuggedProcessIndexByIdSliced(int processId, int budgetMs)
        {
            if (processId <= 0)
                throw new DebugContextNotFoundException("A positive process id is required.");

            int normalizedBudgetMs = NormalizeCollectionBudgetMs(budgetMs);
            DebugProcessCollectionHeader header = ExecuteOnMainThread(CaptureDebugProcessCollectionHeader);
            if (!header.Available || !header.IsPaused)
            {
                throw new DebugContextNotFoundException(
                    header.Error ?? "Debug process selection is unavailable outside break mode.");
            }

            var stopwatch = Stopwatch.StartNew();
            for (int index = 1; index <= header.Count; index++)
            {
                DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                if (stopwatch.ElapsedMilliseconds >= normalizedBudgetMs)
                {
                    throw new ResourceLimitExceededException(
                        $"Process {processId} lookup exceeded its {normalizedBudgetMs}ms budget; " +
                        "retry with a larger process_lookup_budget_ms.");
                }

                int capturedIndex = index;
                int capturedProcessId = ExecuteOnMainThread(
                    () => CaptureDebugProcessId(capturedIndex),
                    DefaultMainThreadTimeoutMs);
                if (capturedProcessId == processId) return index;
            }

            DebugProcessCollectionHeader finalHeader = ExecuteOnMainThread(CaptureDebugProcessCollectionHeader);
            if (!finalHeader.Available || finalHeader.Count != header.Count ||
                !string.Equals(finalHeader.SnapshotToken, header.SnapshotToken, StringComparison.Ordinal))
            {
                throw new StaleDebugContextException(
                    "The debug process collection changed while locating the requested process.");
            }
            throw new DebugContextNotFoundException(
                $"Process {processId} is not attached to the current Visual Studio debug session.");
        }

        private DebugProcessCollectionHeader CaptureDebugProcessCollectionHeader()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var debugger = Debugger;
            if (debugger == null || debugger.CurrentMode == dbgDebugMode.dbgDesignMode ||
                debugger.DebuggedProcesses == null)
            {
                return new DebugProcessCollectionHeader
                {
                    Error = "No active Visual Studio debug session or debugged process collection.",
                };
            }

            return new DebugProcessCollectionHeader
            {
                Available = true,
                IsPaused = debugger.CurrentMode == dbgDebugMode.dbgBreakMode,
                Count = debugger.DebuggedProcesses.Count,
                CurrentProcessId = debugger.CurrentProcess?.ProcessID ?? 0,
                SnapshotToken = CreateDebuggerSnapshotToken(debugger),
            };
        }

        private DebugProcessCapture CaptureDebugProcess(int collectionIndex, int currentProcessId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                EnvDTE.Process process = Debugger?.DebuggedProcesses?.Item(collectionIndex);
                if (process == null) return new DebugProcessCapture { Error = "Debugged process unavailable." };
                int processId = process.ProcessID;
                int programCount = 0;
                try { programCount = process.Programs?.Count ?? 0; } catch { }
                return new DebugProcessCapture
                {
                    Process = new DebugProcessInfo
                    {
                        ProcessId = processId,
                        Name = process.Name ?? $"Process {processId}",
                        IsCurrent = processId == currentProcessId,
                        ProgramCount = programCount,
                    },
                };
            }
            catch (Exception ex)
            {
                return new DebugProcessCapture { Error = ex.Message };
            }
        }

        private int CaptureDebugProcessId(int collectionIndex)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { return Debugger?.DebuggedProcesses?.Item(collectionIndex)?.ProcessID ?? 0; }
            catch { return 0; }
        }

        private sealed class DebugProcessCollectionHeader
        {
            internal bool Available { get; set; }
            internal bool IsPaused { get; set; }
            internal int Count { get; set; }
            internal int CurrentProcessId { get; set; }
            internal string SnapshotToken { get; set; }
            internal string Error { get; set; }
        }

        private sealed class DebugProcessCapture
        {
            internal DebugProcessInfo Process { get; set; }
            internal string Error { get; set; }
        }

        #endregion

        #region Stack Frame Navigation

        /// <summary>
        /// Switch to a specific stack frame.
        /// </summary>
        public DebugControlResult SwitchStackFrame(int frameIndex)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new DebugControlResult { Action = "SwitchStackFrame" };

                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    result.Success = false;
                    result.Message = "Not in break mode";
                    return result;
                }

                try
                {
                    var thread = debugger.CurrentThread;
                    if (thread != null && frameIndex < thread.StackFrames.Count)
                    {
                        // Stack frames are 1-indexed in DTE
                        var frame = thread.StackFrames.Item(frameIndex + 1) as EnvDTE.StackFrame;
                        if (frame != null)
                        {
                            result.CommandIssued = true;
                            DebuggerRequestContext.MarkSideEffectIssued();
                            debugger.CurrentStackFrame = frame;
                            _sessionTracker.InvalidateContext("switch_stack_frame");
                            RebaseRequestToCurrentDebuggerIdentity();
                            result.Success = true;
                            result.Outcome = "stack_frame_selected";
                            result.Message = $"Switched to frame {frameIndex}: {frame.FunctionName}";
                            result.NewLocation = CreateSourceLocation(frame);
                        }
                        else
                        {
                            result.Success = false;
                            result.Message = "Invalid frame";
                        }
                    }
                    else
                    {
                        result.Success = false;
                        result.Message = $"Frame index {frameIndex} out of range";
                    }
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.ErrorCode = "OUTCOME_UNKNOWN";
                    result.Outcome = result.CommandIssued ? "switch_stack_frame_outcome_unknown" : "switch_stack_frame_not_started";
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Switch to a specific thread.
        /// </summary>
        public DebugControlResult SwitchThread(int threadId, int threadLookupBudgetMs = -1)
        {
            int threadIndex;
            try
            {
                threadIndex = FindThreadIndexByIdSliced(threadId, threadLookupBudgetMs);
            }
            catch (DebugContextNotFoundException ex)
            {
                return new DebugControlResult
                {
                    Action = "SwitchThread",
                    Success = false,
                    ErrorCode = "DEBUG_CONTEXT_NOT_FOUND",
                    Message = ex.Message,
                };
            }

            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new DebugControlResult { Action = "SwitchThread" };

                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    result.Success = false;
                    result.Message = "Not in break mode";
                    return result;
                }

                try
                {
                    EnvDTE.Thread thread = debugger.CurrentProgram?.Threads?.Item(threadIndex);
                    if (thread == null || thread.ID != threadId)
                    {
                        throw new StaleDebugContextException(
                            $"Thread {threadId} changed before it could be selected.");
                    }

                    result.CommandIssued = true;
                    DebuggerRequestContext.MarkSideEffectIssued();
                    debugger.CurrentThread = thread;
                    _sessionTracker.InvalidateContext("switch_thread");
                    RebaseRequestToCurrentDebuggerIdentity();
                    result.Success = true;
                    result.Outcome = "thread_selected";
                    result.Message = $"Switched to thread {threadId}";

                    if (thread.StackFrames.Count > 0)
                    {
                        var topFrame = thread.StackFrames.Item(1) as EnvDTE.StackFrame;
                        if (topFrame != null)
                        {
                            result.NewLocation = CreateSourceLocation(topFrame);
                        }
                    }
                }
                catch (Exception ex)
                {
                    RethrowRequestControlFlow(ex);
                    result.Success = false;
                    result.ErrorCode = "OUTCOME_UNKNOWN";
                    result.Outcome = result.CommandIssued ? "switch_thread_outcome_unknown" : "switch_thread_not_started";
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        #endregion
    }
}
