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
        #region Debug Status

        public DebugStatusResult GetDebugStatus()
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return BuildDebugStatus(Debugger);
            });
        }

        private DebugStatusResult BuildDebugStatus(Debugger5 debugger)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (debugger == null)
            {
                return new DebugStatusResult
                {
                    IsDebugging = false,
                    IsPaused = false,
                    DebugMode = "Unknown",
                    StopReason = "not_paused"
                };
            }

            var result = new DebugStatusResult
            {
                IsDebugging = debugger.CurrentMode != dbgDebugMode.dbgDesignMode,
                IsPaused = debugger.CurrentMode == dbgDebugMode.dbgBreakMode,
                DebugMode = GetDebugModeString(debugger.CurrentMode)
            };

            if (result.IsDebugging && debugger.CurrentProcess != null)
            {
                result.CurrentProcess = debugger.CurrentProcess.Name;
                result.CurrentProcessId = debugger.CurrentProcess.ProcessID;
            }

            if (result.IsPaused && debugger.CurrentStackFrame != null)
            {
                result.CurrentLocation = CreateSourceLocation(debugger.CurrentStackFrame);
            }

            ObserveDebugContext(debugger);
            var stopEvent = _sessionTracker.GetStopEventSnapshot();
            result.StopReason = result.IsPaused ? stopEvent.StopReason : "not_paused";
            result.StopReasonRaw = result.IsPaused ? stopEvent.StopReasonRaw : null;
            result.StopReasonSource = stopEvent.Source;
            result.StopReasonConfidence = result.IsPaused ? stopEvent.Confidence : "high";
            result.DebugSessionGeneration = stopEvent.Generation;
            result.BreakSequence = stopEvent.BreakSequence;
            result.ContextEpoch = stopEvent.ContextEpoch;

            return result;
        }

        internal void EnsureRequiredState(string requiredState)
        {
            if (string.IsNullOrEmpty(requiredState) || requiredState == "any") return;

            ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var mode = debugger?.CurrentMode ?? dbgDebugMode.dbgDesignMode;
                bool valid = requiredState == "paused"
                    ? mode == dbgDebugMode.dbgBreakMode
                    : (requiredState == "running"
                        ? mode == dbgDebugMode.dbgRunMode
                        : mode != dbgDebugMode.dbgDesignMode);
                if (!valid)
                {
                    string code = requiredState == "paused"
                        ? "NOT_PAUSED"
                        : (requiredState == "running" ? "NOT_RUNNING" : "NOT_DEBUGGING");
                    throw new DebuggerStateRequirementException(
                        code,
                        $"Method requires debugger state '{requiredState}', but Visual Studio is in '{GetDebugModeString(mode)}' mode.");
                }
                return true;
            });
        }

        private string GetDebugModeString(dbgDebugMode mode)
        {
            switch (mode)
            {
                case dbgDebugMode.dbgDesignMode: return "Design";
                case dbgDebugMode.dbgBreakMode: return "Break";
                case dbgDebugMode.dbgRunMode: return "Running";
                default: return "Unknown";
            }
        }

        #endregion

        #region Call Stack

        public CallStackResult GetCallStack(
            int startIndex = 0,
            int maxFrames = CallStackCaptureBudget.DefaultMaxFrames,
            int budgetMs = CallStackCaptureBudget.DefaultBudgetMs)
        {
            CallStackSliceHeader header = ExecuteOnMainThread(CaptureCallStackSliceHeader);
            int normalizedStart = Math.Max(0, startIndex);
            int normalizedMaxFrames = maxFrames == 0
                ? CallStackCaptureBudget.HardMaxFrames
                : Math.Max(1, Math.Min(maxFrames, CallStackCaptureBudget.HardMaxFrames));
            int requestedBudget = budgetMs == 0
                ? 120000
                : Math.Max(1, Math.Min(budgetMs, 120000));
            int normalizedBudgetMs = Math.Max(0, Math.Min(
                requestedBudget,
                DebuggerRequestContext.RemainingMilliseconds(requestedBudget) - 250));
            var result = new CallStackResult
            {
                Capability = "sliced_frame_snapshot",
                SnapshotAttempts = 1,
                SnapshotStatus = "unavailable",
                ThreadId = header.ThreadId,
                ThreadName = header.ThreadName,
                DebugSessionGeneration = header.StopEvent?.Generation ?? 0,
                BreakSequence = header.StopEvent?.BreakSequence ?? 0,
                ContextEpoch = header.StopEvent?.ContextEpoch ?? 0,
                ProcessId = header.StopEvent?.DebuggeeProcessId ?? 0,
                ExpectedFrameCount = header.FrameCount,
                Error = header.Error,
                StartIndex = normalizedStart,
                MaxFrames = normalizedMaxFrames,
                BudgetMs = normalizedBudgetMs,
                MaxResultBytes = CallStackCaptureBudget.MaxResultBytes,
            };
            if (!header.Available)
            {
                return result;
            }

            normalizedStart = Math.Min(normalizedStart, header.FrameCount);
            result.StartIndex = normalizedStart;
            int captureEnd = Math.Min(
                header.FrameCount,
                normalizedStart + normalizedMaxFrames);
            int nextIndex = normalizedStart;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var captureBudget = new CallStackCaptureBudget();
            for (int index = normalizedStart; index < captureEnd; index++)
            {
                DebuggerRequestContext.CancellationToken.ThrowIfCancellationRequested();
                if (stopwatch.ElapsedMilliseconds >= normalizedBudgetMs)
                {
                    result.Error = $"Call stack capture reached its {normalizedBudgetMs}ms page budget.";
                    break;
                }
                int capturedIndex = index;
                StackFrameInfo frame = ExecuteOnMainThread(
                    () => CaptureCallStackFrame(header.ThreadId, capturedIndex, header.StopEvent),
                    DefaultMainThreadTimeoutMs);
                if (frame == null)
                {
                    throw new StaleDebugContextException(
                        $"Call stack context changed while reading frame {index}");
                }
                if (!captureBudget.TryConsume(frame))
                {
                    result.Error = $"Call stack page reached its {CallStackCaptureBudget.MaxResultBytes}-byte result budget.";
                    break;
                }
                result.Frames.Add(frame);
                nextIndex = index + 1;
                if (string.Equals(frame.SourceLocationStatus, "unavailable", StringComparison.Ordinal) &&
                    string.Equals(frame.FunctionName, "<unavailable>", StringComparison.Ordinal))
                {
                    result.Partial = true;
                    result.Error = AppendError(
                        result.Error,
                        $"Call stack frame {index} was included as an unavailable placeholder");
                }
                else if (frame.Truncated)
                {
                    result.Partial = true;
                    result.Error = AppendError(
                        result.Error,
                        frame.Error ?? $"Call stack frame {index} contains unavailable metadata");
                }
            }
            result.CapturedFrameCount = result.Frames.Count;
            result.ScannedFrameCount = nextIndex - normalizedStart;
            result.NextIndex = nextIndex;
            result.HasMore = nextIndex < header.FrameCount;
            result.ResultBytes = captureBudget.ResultBytes;
            if (result.HasMore)
            {
                result.Partial = true;
                if (string.IsNullOrEmpty(result.Error))
                {
                    result.Error = $"Captured frames {normalizedStart} through {Math.Max(normalizedStart, nextIndex) - 1}; continue at startIndex={nextIndex}.";
                }
            }

            string tokenAfter = ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return CreateDebuggerSnapshotToken(Debugger);
            });
            bool contextStable = !string.IsNullOrEmpty(header.SnapshotToken) &&
                string.Equals(header.SnapshotToken, tokenAfter, StringComparison.Ordinal);
            result.SnapshotStable = contextStable;
            result.SnapshotStatus = result.Partial
                ? "partial"
                : result.SnapshotStable
                ? (result.Frames.Count > 0 ? "ok" : "unavailable")
                : (result.Frames.Count > 0 ? "unstable" : "unavailable");
            return result;
        }

        private CallStackSliceHeader CaptureCallStackSliceHeader()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var debugger = Debugger;
            if (debugger == null || debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
            {
                return new CallStackSliceHeader();
            }
            ObserveDebugContext(debugger);
            StopEventSnapshot stopEvent = _sessionTracker.GetStopEventSnapshot();
            bool hasDumpOwner = !string.IsNullOrEmpty(NormalizeExistingDumpPath(GetActiveDumpFilePathCandidate()));
            if (stopEvent.DebuggeeProcessId <= 0 && !hasDumpOwner)
            {
                return new CallStackSliceHeader
                {
                    StopEvent = stopEvent,
                    Error = "The current Visual Studio debuggee has no stable process identity; frame contexts are unavailable.",
                };
            }
            EnvDTE.Thread thread = debugger.CurrentThread;
            if (thread == null)
            {
                return new CallStackSliceHeader { StopEvent = stopEvent };
            }
            return new CallStackSliceHeader
            {
                Available = true,
                SnapshotToken = CreateDebuggerSnapshotToken(debugger),
                StopEvent = stopEvent,
                ThreadId = thread.ID,
                ThreadName = thread.Name ?? $"Thread {thread.ID}",
                FrameCount = thread.StackFrames?.Count ?? 0
            };
        }

        [SuppressMessage("Usage", "VSTHRD010", Justification = "Runs only as one bounded ExecuteOnMainThread call; field lambdas execute synchronously inside it.")]
        private StackFrameInfo CaptureCallStackFrame(
            int expectedThreadId,
            int frameIndex,
            StopEventSnapshot stopEvent)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EnvDTE.Thread thread = Debugger?.CurrentThread;
            if (thread == null || thread.ID != expectedThreadId)
            {
                throw new StaleDebugContextException(
                    "The selected debugger thread changed while capturing the call stack");
            }
            EnvDTE.StackFrame frame;
            try
            {
                frame = thread.StackFrames?.Item(frameIndex + 1) as EnvDTE.StackFrame;
            }
            catch (COMException)
            {
                frame = null;
            }
            if (frame == null)
            {
                return new StackFrameInfo
                {
                    Index = frameIndex,
                    Context = CreateFrameContext(stopEvent, expectedThreadId, frameIndex),
                    FunctionName = "<unavailable>",
                    SourceLocationStatus = "unavailable",
                    Truncated = true,
                };
            }
            var errors = new List<string>();
            VisualStudioSourceLocation sourceLocation;
            try
            {
                sourceLocation = _sourceLocationAdapter.GetLocation(frame) ??
                    VisualStudioSourceLocation.Unavailable;
            }
            catch (Exception)
            {
                sourceLocation = VisualStudioSourceLocation.Unavailable;
                errors.Add($"Call stack frame {frameIndex} source location is unavailable");
            }
            var result = new StackFrameInfo
            {
                Index = frameIndex,
                Context = CreateFrameContext(stopEvent, expectedThreadId, frameIndex),
                FunctionName = CaptureStackFrameString(
                    () => frame.FunctionName,
                    "function name",
                    frameIndex,
                    errors) ?? "<unavailable>",
                FilePath = sourceLocation.FilePath,
                LineNumber = sourceLocation.LineNumber,
                LineNumberReliable = sourceLocation.IsReliable ? true : (bool?)null,
                SourceLocationStatus = sourceLocation.Status ?? "unavailable",
                ColumnNumber = sourceLocation.ColumnNumber,
                Module = CaptureStackFrameString(() => frame.Module, "module", frameIndex, errors),
                Language = CaptureStackFrameString(() => frame.Language, "language", frameIndex, errors),
                ReturnType = CaptureStackFrameString(() => frame.ReturnType, "return type", frameIndex, errors),
            };
            if (errors.Count > 0)
            {
                result.Truncated = true;
                result.Error = string.Join("; ", errors);
            }
            return result;
        }

        private static string CaptureStackFrameString(
            Func<string> getter,
            string field,
            int frameIndex,
            List<string> errors)
        {
            try
            {
                return getter();
            }
            catch (Exception)
            {
                errors.Add($"Call stack frame {frameIndex} {field} is unavailable");
                return null;
            }
        }

        private sealed class CallStackSliceHeader
        {
            internal bool Available { get; set; }
            internal string SnapshotToken { get; set; }
            internal StopEventSnapshot StopEvent { get; set; }
            internal int ThreadId { get; set; }
            internal string ThreadName { get; set; }
            internal int FrameCount { get; set; }
            internal string Error { get; set; }
        }

        #endregion

        #region Locals

        [SuppressMessage("Usage", "VSTHRD010", Justification = "Delegates every EnvDTE access to individually bounded UI slices.")]
        public LocalsResult GetLocals(
            int maxDepth = 2,
            int threadId = 0,
            int frameIndex = 0,
            long debugSessionGeneration = 0,
            long breakSequence = 0,
            long contextEpoch = 0,
            int processId = 0,
            string frameId = null,
            int threadLookupBudgetMs = -1)
        {
            int selectedThreadIndex = threadId > 0
                ? FindThreadIndexByIdSliced(threadId, threadLookupBudgetMs)
                : 0;
            return CaptureLocalsSliced(
                maxDepth,
                threadId,
                frameIndex,
                debugSessionGeneration,
                breakSequence,
                contextEpoch,
                processId,
                frameId,
                selectedThreadIndex);
        }

        private DebugFrameContext CreateFrameContext(StopEventSnapshot stopEvent, int threadId, int frameIndex)
        {
            // A native dump has no live OS PID. Its bounded snapshot is owned by
            // the actual dump session, not a reusable live-process frame token.
            if (stopEvent.DebuggeeProcessId <= 0) return null;
            return new DebugFrameContext
            {
                DebugSessionGeneration = stopEvent.Generation,
                BreakSequence = stopEvent.BreakSequence,
                ContextEpoch = stopEvent.ContextEpoch,
                ProcessId = stopEvent.DebuggeeProcessId,
                ThreadId = threadId,
                FrameIndex = frameIndex,
                FrameId = $"{stopEvent.Generation}:{stopEvent.BreakSequence}:{stopEvent.ContextEpoch}:" +
                    $"{stopEvent.DebuggeeProcessId}:{threadId}:{frameIndex}",
            };
        }

        private void ObserveDebugContext(Debugger5 debugger)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            int processId = 0;
            int threadId = 0;
            string programIdentity = null;
            string frameIdentity = null;
            string mode = "unavailable";
            try { processId = debugger?.CurrentProcess?.ProcessID ?? 0; } catch { }
            try { programIdentity = debugger?.CurrentProgram?.Name; } catch { }
            try { threadId = debugger?.CurrentThread?.ID ?? 0; } catch { }
            try { mode = debugger?.CurrentMode.ToString() ?? "unavailable"; } catch { }
            try
            {
                EnvDTE.StackFrame frame = debugger?.CurrentStackFrame;
                if (frame != null)
                {
                    IntPtr unknown = IntPtr.Zero;
                    try
                    {
                        unknown = Marshal.GetIUnknownForObject(frame);
                        frameIdentity = unknown.ToInt64().ToString("X16");
                    }
                    finally
                    {
                        if (unknown != IntPtr.Zero) Marshal.Release(unknown);
                    }
                }
            }
            catch { }
            _sessionTracker.ObserveDebuggee(processId, programIdentity);
            _sessionTracker.ObserveDebuggerContext(
                $"{mode}:{processId}:{threadId}:{frameIdentity ?? string.Empty}");
            RefreshTrustedLiveProcessIdentity(debugger, processId);
        }

        private EnvDTE.Thread ResolveThread(Debugger5 debugger, int threadId, int threadIndex)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (threadId <= 0)
            {
                return debugger.CurrentThread;
            }

            if (debugger?.CurrentProgram?.Threads == null || threadIndex <= 0)
            {
                return null;
            }

            try
            {
                EnvDTE.Thread thread = debugger.CurrentProgram.Threads.Item(threadIndex);
                return thread != null && thread.ID == threadId ? thread : null;
            }
            catch
            {
                return null;
            }
        }

        [SuppressMessage("Usage", "VSTHRD010", Justification = "Reads one thread identity per bounded UI slice.")]
        private int FindThreadIndexByIdSliced(int threadId, int budgetMs = -1)
        {
            if (threadId <= 0)
            {
                return 0;
            }

            int normalizedBudgetMs = NormalizeCollectionBudgetMs(budgetMs);
            ThreadLookupHeader header = ExecuteOnMainThread(CaptureThreadLookupHeader);
            if (!header.Available)
            {
                throw new DebugContextNotFoundException(header.Error ?? "Thread collection unavailable.");
            }

            var stopwatch = Stopwatch.StartNew();
            int inspected = 0;
            for (int index = 1; index <= header.Count; index++)
            {
                DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                if (stopwatch.ElapsedMilliseconds >= normalizedBudgetMs)
                {
                    throw new ResourceLimitExceededException(
                        $"Thread {threadId} lookup exceeded its {normalizedBudgetMs}ms budget; " +
                        "retry with a larger thread_lookup_budget_ms.");
                }

                int capturedIndex = index;
                ThreadIdentityCapture identity = ExecuteOnMainThread(
                    () => CaptureThreadIdentity(capturedIndex),
                    DefaultMainThreadTimeoutMs);
                inspected++;
                if (identity?.ThreadId == threadId)
                {
                    return index;
                }
            }

            ThreadLookupHeader finalHeader = ExecuteOnMainThread(CaptureThreadLookupHeader);
            if (!finalHeader.Available || finalHeader.Count != header.Count ||
                !string.Equals(finalHeader.SnapshotToken, header.SnapshotToken, StringComparison.Ordinal))
            {
                throw new StaleDebugContextException("Thread collection changed while locating the requested thread.");
            }
            throw new DebugContextNotFoundException($"Thread {threadId} was not found in the current break context.");
        }

        private ThreadLookupHeader CaptureThreadLookupHeader()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var debugger = Debugger;
            var threads = debugger?.CurrentProgram?.Threads;
            if (debugger == null || debugger.CurrentMode != dbgDebugMode.dbgBreakMode || threads == null)
            {
                return new ThreadLookupHeader { Error = "Thread collection unavailable outside break mode." };
            }
            return new ThreadLookupHeader
            {
                Available = true,
                Count = threads.Count,
                SnapshotToken = CreateDebuggerSnapshotToken(debugger),
            };
        }

        private ThreadIdentityCapture CaptureThreadIdentity(int index)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                EnvDTE.Thread thread = Debugger?.CurrentProgram?.Threads?.Item(index);
                return thread == null
                    ? new ThreadIdentityCapture()
                    : new ThreadIdentityCapture { ThreadId = thread.ID };
            }
            catch
            {
                return new ThreadIdentityCapture();
            }
        }

        private sealed class ThreadLookupHeader
        {
            internal bool Available { get; set; }
            internal int Count { get; set; }
            internal string SnapshotToken { get; set; }
            internal string Error { get; set; }
        }

        private sealed class ThreadIdentityCapture
        {
            internal int ThreadId { get; set; }
        }

        private EnvDTE.StackFrame ResolveFrame(EnvDTE.Thread thread, int frameIndex)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (thread == null || frameIndex < 0 || frameIndex >= thread.StackFrames.Count)
            {
                return null;
            }
            return thread.StackFrames.Item(frameIndex + 1) as EnvDTE.StackFrame;
        }

        #endregion

        #region Threads

        [SuppressMessage("Usage", "VSTHRD010", Justification = "Each thread/frame COM access is executed inside a bounded ExecuteOnMainThread callback.")]
        public ThreadsResult GetThreads(
            bool includeFrames = true,
            int maxFramesPerThread = -1,
            int maxNodes = -1,
            int budgetMs = -1,
            int startThreadIndex = 1,
            int startFrameIndex = 0)
        {
            if (startThreadIndex < 1) throw new ArgumentOutOfRangeException(nameof(startThreadIndex));
            if (startFrameIndex < 0) throw new ArgumentOutOfRangeException(nameof(startFrameIndex));
            int normalizedMaxFramesPerThread = NormalizeThreadFrameLimit(maxFramesPerThread);
            int normalizedMaxNodes = Math.Max(1, NormalizeThreadNodeLimit(maxNodes));
            if (includeFrames && normalizedMaxNodes == 1)
                throw new ArgumentException(
                    "maxNodes must be at least 2 when includeFrames is enabled.",
                    nameof(maxNodes));
            int normalizedBudgetMs = NormalizeThreadBudgetMs(budgetMs);
            var result = new ThreadsResult
            {
                Capability = "sliced_thread_snapshot",
                SnapshotAttempts = 1,
                SnapshotStatus = "unavailable",
                StartThreadIndex = startThreadIndex,
                StartFrameIndex = startFrameIndex,
                NextThreadIndex = startThreadIndex,
                NextFrameIndex = startFrameIndex,
                MaxNodes = normalizedMaxNodes,
                BudgetMs = normalizedBudgetMs,
                MaxResultBytes = ThreadCaptureBudget.MaxResultBytes,
            };
            var partialReasons = new List<string>();
            var stopwatch = Stopwatch.StartNew();
            var captureBudget = new ThreadCaptureBudget();

            ThreadSnapshotHeader header = ExecuteOnMainThread(CaptureThreadSnapshotHeader);
            result.CurrentThreadId = header.CurrentThreadId;
            if (!header.Available)
            {
                result.Error = header.Error;
                return result;
            }

            int capturedNodes = 0;
            bool stoppedForPageBoundary = false;
            for (int threadIndex = startThreadIndex; threadIndex <= header.ThreadCount; threadIndex++)
            {
                DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                if (stopwatch.ElapsedMilliseconds >= normalizedBudgetMs || capturedNodes >= normalizedMaxNodes)
                {
                    stoppedForPageBoundary = true;
                    AddPartialReason(
                        partialReasons,
                        stopwatch.ElapsedMilliseconds >= normalizedBudgetMs
                            ? $"Thread snapshot budget exceeded {normalizedBudgetMs}ms"
                            : $"Thread snapshot reached maxNodes={normalizedMaxNodes}");
                    break;
                }

                ThreadMetadataCapture metadata;
                try
                {
                    int capturedIndex = threadIndex;
                    metadata = ExecuteOnMainThread(
                        () => CaptureThreadMetadata(capturedIndex, includeFrames),
                        DefaultMainThreadTimeoutMs);
                }
                catch (Exception ex)
                {
                    RethrowRequestControlFlow(ex);
                    result.Partial = true;
                    AddPartialReason(partialReasons, $"thread index {threadIndex}: {ex.Message}");
                    continue;
                }
                if (metadata?.Thread == null)
                {
                    result.Partial = true;
                    AddPartialReason(partialReasons, metadata?.Error ?? $"thread index {threadIndex}: unavailable");
                    result.NextThreadIndex = threadIndex + 1;
                    result.NextFrameIndex = 0;
                    continue;
                }

                ThreadInfo threadInfo = metadata.Thread;
                if (!string.IsNullOrEmpty(metadata.Error))
                {
                    result.Partial = true;
                    AddPartialReason(partialReasons, $"thread {threadInfo.Id}: {metadata.Error}");
                }
                if (!captureBudget.TryConsumeThreadHeader(threadInfo))
                {
                    stoppedForPageBoundary = true;
                    AddPartialReason(
                        partialReasons,
                        $"Thread snapshot reached {ThreadCaptureBudget.MaxResultBytes} bytes");
                    break;
                }
                capturedNodes++;
                if (threadInfo.Truncated)
                {
                    result.Partial = true;
                    AddPartialReason(partialReasons, $"thread {threadInfo.Id}: metadata was truncated");
                }

                int firstFrameIndex = includeFrames && threadIndex == startThreadIndex
                    ? startFrameIndex
                    : 0;
                int frameEndExclusive = includeFrames
                    ? Math.Min(metadata.FrameCount, firstFrameIndex + normalizedMaxFramesPerThread)
                    : firstFrameIndex;
                var frames = includeFrames ? new List<StackFrameInfo>() : null;
                int nextFrameIndex = firstFrameIndex;
                bool stoppedInsideThread = false;
                for (int frameIndex = firstFrameIndex; frameIndex < frameEndExclusive; frameIndex++)
                {
                    DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                    if (stopwatch.ElapsedMilliseconds >= normalizedBudgetMs ||
                        capturedNodes >= normalizedMaxNodes)
                    {
                        stoppedInsideThread = true;
                        break;
                    }

                    int capturedThreadIndex = threadIndex;
                    int capturedFrameIndex = frameIndex;
                    ThreadFrameCapture frameCapture;
                    try
                    {
                        frameCapture = ExecuteOnMainThread(
                            () => CaptureThreadFrame(
                                capturedThreadIndex,
                                threadInfo.Id,
                                capturedFrameIndex),
                            DefaultMainThreadTimeoutMs);
                    }
                    catch (Exception ex)
                    {
                        RethrowRequestControlFlow(ex);
                        result.Partial = true;
                        AddPartialReason(partialReasons, $"thread {threadInfo.Id} frame {frameIndex}: {ex.Message}");
                        nextFrameIndex = frameIndex + 1;
                        continue;
                    }

                    if (frameCapture?.Frame == null)
                    {
                        result.Partial = true;
                        AddPartialReason(
                            partialReasons,
                            frameCapture?.Error ?? $"thread {threadInfo.Id} frame {frameIndex}: unavailable");
                        nextFrameIndex = frameIndex + 1;
                        continue;
                    }
                    if (!captureBudget.TryConsumeFrame(frameCapture.Frame))
                    {
                        stoppedInsideThread = true;
                        AddPartialReason(
                            partialReasons,
                            $"Thread snapshot reached {ThreadCaptureBudget.MaxResultBytes} bytes");
                        break;
                    }
                    frames.Add(frameCapture.Frame);
                    capturedNodes++;
                    nextFrameIndex = frameIndex + 1;
                    if (frameCapture.Frame.Truncated)
                    {
                        result.Partial = true;
                        AddPartialReason(
                            partialReasons,
                            $"thread {threadInfo.Id} frame {frameIndex}: metadata was truncated");
                    }
                }

                if (frames != null && frames.Count > 0)
                {
                    threadInfo.Frames = frames;
                }
                result.Threads.Add(threadInfo);
                result.NextThreadIndex = threadIndex + 1;
                result.NextFrameIndex = 0;

                if (includeFrames && (stoppedInsideThread || nextFrameIndex < metadata.FrameCount))
                {
                    stoppedForPageBoundary = true;
                    result.NextThreadIndex = threadIndex;
                    result.NextFrameIndex = nextFrameIndex;
                    AddPartialReason(
                        partialReasons,
                        $"thread {threadInfo.Id}: page ended before frame {nextFrameIndex} of {metadata.FrameCount}");
                    break;
                }
            }

            result.CapturedNodes = capturedNodes;
            result.ResultBytes = captureBudget.ResultBytes;
            result.HasMore = stoppedForPageBoundary && result.NextThreadIndex <= header.ThreadCount;
            if (result.HasMore) result.Partial = true;

            ThreadSnapshotHeader finalHeader = ExecuteOnMainThread(CaptureThreadSnapshotHeader);
            bool observedBoundaryStable =
                finalHeader.Available &&
                !string.IsNullOrEmpty(header.SnapshotToken) &&
                string.Equals(header.SnapshotToken, finalHeader.SnapshotToken, StringComparison.Ordinal) &&
                header.ThreadCount == finalHeader.ThreadCount &&
                header.CurrentThreadId == finalHeader.CurrentThreadId &&
                SameStopEvent(header.StopEvent, finalHeader.StopEvent);
            // EnvDTE exposes no atomic thread-collection generation. Boundary checks
            // can establish best-effort consistency, never a strong stable snapshot.
            result.SnapshotStable = false;
            if (!observedBoundaryStable)
            {
                result.Partial = true;
                AddPartialReason(partialReasons, "debugger state changed while the sliced thread snapshot was captured");
            }
            if (string.IsNullOrEmpty(result.Error) && partialReasons.Count > 0)
            {
                result.Error = string.Join("; ", partialReasons);
            }
            result.SnapshotStatus = !observedBoundaryStable
                ? (result.Threads.Count > 0 ? "unstable" : "unavailable")
                : (result.Partial ? "partial" : (result.Threads.Count > 0 ? "best_effort" : "unavailable"));
            return result;
        }

        private static bool SameStopEvent(StopEventSnapshot left, StopEventSnapshot right)
        {
            return left != null && right != null &&
                left.Generation == right.Generation &&
                left.BreakSequence == right.BreakSequence &&
                left.ContextEpoch == right.ContextEpoch;
        }

        private ThreadSnapshotHeader CaptureThreadSnapshotHeader()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var debugger = Debugger;
            if (debugger == null || debugger.CurrentMode == dbgDebugMode.dbgDesignMode)
            {
                return new ThreadSnapshotHeader { Error = "Not in debug mode" };
            }
            try
            {
                var program = debugger.CurrentProgram;
                if (program == null)
                {
                    return new ThreadSnapshotHeader { Error = "Current debug program unavailable" };
                }
                return new ThreadSnapshotHeader
                {
                    Available = true,
                    SnapshotToken = CreateDebuggerSnapshotToken(debugger),
                    CurrentThreadId = debugger.CurrentThread?.ID ?? 0,
                    ThreadCount = program.Threads?.Count ?? 0,
                    StopEvent = _sessionTracker.GetStopEventSnapshot(),
                };
            }
            catch (Exception ex)
            {
                return new ThreadSnapshotHeader { Error = $"Current debug program unavailable: {ex.Message}" };
            }
        }


        private ThreadMetadataCapture CaptureThreadMetadata(int threadIndex, bool includeFrameCount = true)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var debugger = Debugger;
                var thread = debugger?.CurrentProgram?.Threads?.Item(threadIndex);
                if (thread == null)
                {
                    return new ThreadMetadataCapture { Error = $"thread index {threadIndex}: unavailable" };
                }
                // Keep the collection within this UI slice. A metadata-only
                // thread list needs its top location, not a full stack depth.
                EnvDTE.StackFrames stackFrames = null;
                int frameCount = 0;
                string frameCountError = null;
                if (includeFrameCount)
                {
                    ThreadCaptureBudget.TryCaptureFrameCount(() =>
                    {
                        ThreadHelper.ThrowIfNotOnUIThread();
                        stackFrames = thread.StackFrames;
                        if (stackFrames == null)
                            throw new InvalidOperationException("Stack frame collection unavailable");
                        return stackFrames.Count;
                    }, out frameCount, out frameCountError);
                }
                else try { stackFrames = thread.StackFrames; } catch { }
                string location = string.Empty;
                try
                {
                    var topFrame = stackFrames?.Item(1) as EnvDTE.StackFrame;
                    string functionName = topFrame?.FunctionName;
                    if (functionName != null && !IsSyntheticExecutionPointName(functionName))
                    {
                        location = functionName;
                    }
                }
                catch { }
                int threadId = thread.ID;
                var currentThread = debugger.CurrentThread;
                string name = ThreadCaptureBudget.BoundField(
                    thread.Name ?? $"Thread {threadId}",
                    out bool nameTruncated);
                string boundedLocation = ThreadCaptureBudget.BoundField(
                    location,
                    out bool locationTruncated);
                string priority = ThreadCaptureBudget.BoundField(
                    thread.Priority,
                    out bool priorityTruncated);
                return new ThreadMetadataCapture
                {
                    Thread = new ThreadInfo
                    {
                        Id = threadId,
                        Name = name,
                        IsCurrent = currentThread != null && threadId == currentThread.ID,
                        IsFrozen = thread.IsFrozen,
                        Location = boundedLocation,
                        Priority = priority,
                        SuspendCount = thread.SuspendCount,
                        Truncated = nameTruncated || locationTruncated || priorityTruncated,
                    },
                    FrameCount = frameCount,
                    Error = frameCountError
                };
            }
            catch (Exception ex)
            {
                return new ThreadMetadataCapture { Error = $"thread index {threadIndex}: {ex.Message}" };
            }
        }

        private ThreadFrameCapture CaptureThreadFrame(int threadIndex, int expectedThreadId, int frameIndex)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var thread = Debugger?.CurrentProgram?.Threads?.Item(threadIndex);
                if (thread == null || thread.ID != expectedThreadId)
                {
                    return new ThreadFrameCapture
                    {
                        Error = $"thread index {threadIndex} changed while snapshotting"
                    };
                }
                var frame = thread.StackFrames?.Item(frameIndex + 1) as EnvDTE.StackFrame;
                if (frame == null)
                {
                    return new ThreadFrameCapture { Error = $"frame {frameIndex}: unavailable" };
                }
                VisualStudioSourceLocation sourceLocation = _sourceLocationAdapter.GetLocation(frame);
                return new ThreadFrameCapture
                {
                    Frame = new StackFrameInfo
                    {
                        Index = frameIndex,
                        FunctionName = frame.FunctionName,
                        FilePath = sourceLocation.FilePath,
                        LineNumber = sourceLocation.LineNumber,
                        LineNumberReliable = sourceLocation.IsReliable ? true : (bool?)null,
                        SourceLocationStatus = sourceLocation.Status,
                        ColumnNumber = sourceLocation.ColumnNumber,
                        Module = frame.Module,
                        Language = frame.Language,
                        ReturnType = frame.ReturnType
                    }
                };
            }
            catch (Exception ex)
            {
                return new ThreadFrameCapture { Error = $"frame {frameIndex}: {ex.Message}" };
            }
        }

        private bool IsSyntheticExecutionPointName(string functionName)
        {
            return (functionName ?? string.Empty).StartsWith(
                "[Execution point when dump was captured",
                StringComparison.Ordinal);
        }

        private LocationInfo CreateSourceLocation(EnvDTE.StackFrame frame)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (frame == null) return null;

            VisualStudioSourceLocation source = _sourceLocationAdapter.GetLocation(frame);
            if (string.Equals(source.Status, "unavailable", StringComparison.Ordinal))
                return null;

            return new LocationInfo
            {
                FilePath = source.FilePath,
                FunctionName = frame.FunctionName,
                LineNumber = source.LineNumber,
                ColumnNumber = source.ColumnNumber,
                LineNumberReliable = source.IsReliable ? true : (bool?)null,
                SourceLocationStatus = source.Status,
            };
        }

        private sealed class ThreadSnapshotHeader
        {
            internal bool Available { get; set; }
            internal string Error { get; set; }
            internal string SnapshotToken { get; set; }
            internal int CurrentThreadId { get; set; }
            internal int ThreadCount { get; set; }
            internal StopEventSnapshot StopEvent { get; set; }
        }

        private sealed class ThreadMetadataCapture
        {
            internal ThreadInfo Thread { get; set; }
            internal int FrameCount { get; set; }
            internal string Error { get; set; }
        }

        private sealed class ThreadFrameCapture
        {
            internal StackFrameInfo Frame { get; set; }
            internal string Error { get; set; }
        }

        private void AddPartialReason(List<string> partialReasons, string reason)
        {
            if (partialReasons == null || string.IsNullOrWhiteSpace(reason))
            {
                return;
            }

            if (partialReasons.Count < 8)
            {
                partialReasons.Add(reason);
            }
            else if (partialReasons.Count == 8)
            {
                partialReasons.Add("additional thread/frame errors omitted");
            }
        }

        private string DetermineThreadsSnapshotStatus(ThreadsResult threads, bool stable)
        {
            if (threads == null)
            {
                return "unavailable";
            }

            if (!stable)
            {
                return threads.Threads.Count > 0 ? "unstable" : "unavailable";
            }

            if (threads.Partial)
            {
                return "partial";
            }

            return threads.Threads.Count > 0 ? "ok" : "unavailable";
        }

        private int NormalizeThreadFrameLimit(int maxFramesPerThread)
        {
            if (maxFramesPerThread < 0)
            {
                return ThreadFramesMaxPerThread;
            }

            if (maxFramesPerThread == 0)
            {
                return ThreadFramesHardMaxPerThread;
            }

            return Math.Min(maxFramesPerThread, ThreadFramesHardMaxPerThread);
        }

        private int NormalizeThreadNodeLimit(int maxNodes)
        {
            if (maxNodes < 0)
            {
                return ThreadFrameExpansionMaxNodes;
            }

            if (maxNodes == 0)
            {
                return ThreadFrameExpansionHardMaxNodes;
            }

            return Math.Min(maxNodes, ThreadFrameExpansionHardMaxNodes);
        }

        private int NormalizeThreadBudgetMs(int budgetMs)
        {
            if (budgetMs < 0)
            {
                return ThreadEnumerationBudgetMs;
            }

            if (budgetMs == 0)
            {
                return DebugBundleMainThreadTimeoutMs - 5000;
            }

            return Math.Min(budgetMs, DebugBundleMainThreadTimeoutMs - 5000);
        }

        private static int NormalizeModuleFrameScanLimit(int maxFramesScanned)
        {
            if (maxFramesScanned < 0) return ModuleFrameScanDefault;
            if (maxFramesScanned == 0) return ModuleFrameScanHardLimit;
            return Math.Min(maxFramesScanned, ModuleFrameScanHardLimit);
        }

        #endregion

        #region Modules

        private static readonly TimeSpan ModuleFallbackSnapshotTtl = TimeSpan.FromMinutes(2);
        private const int MaxModuleFallbackSnapshots = 8;
        private readonly object _moduleFallbackSnapshotsLock = new object();
        private readonly Dictionary<string, ModuleFallbackSnapshot> _moduleFallbackSnapshots =
            new Dictionary<string, ModuleFallbackSnapshot>(StringComparer.Ordinal);

        [SuppressMessage("Usage", "VSTHRD010", Justification = "Enumerates one DTE thread/frame per bounded UI slice and completes process/PE metadata off-thread.")]
        public ModulesResult GetModules(
            int maxNodes = -1,
            int budgetMs = -1,
            int startThreadIndex = 1,
            int startFrameIndex = 0,
            int startModuleIndex = 0,
            int maxFramesScanned = -1,
            string moduleSnapshotId = null,
            string scanContinuationToken = null)
        {
            if (startThreadIndex < 1) throw new ArgumentOutOfRangeException(nameof(startThreadIndex));
            if (startFrameIndex < 0) throw new ArgumentOutOfRangeException(nameof(startFrameIndex));
            if (startModuleIndex < 0) throw new ArgumentOutOfRangeException(nameof(startModuleIndex));
            if (startThreadIndex != 1 || startFrameIndex != 0)
                throw new ArgumentException(
                    "Stack-frame cursors are diagnostic only; continue module pages with moduleSnapshotId and nextModuleIndex.");
            if (!string.IsNullOrEmpty(moduleSnapshotId) && !string.IsNullOrEmpty(scanContinuationToken))
                throw new DebuggerParameterValidationException(
                    "moduleSnapshotId and scanContinuationToken are mutually exclusive.");
            int normalizedMaxNodes = NormalizeCollectionNodeLimit(maxNodes);
            int normalizedBudgetMs = NormalizeCollectionBudgetMs(budgetMs);
            int normalizedMaxFramesScanned = NormalizeModuleFrameScanLimit(maxFramesScanned);
            ModuleSliceHeader header = ExecuteOnMainThread(CaptureModuleSliceHeader);
            ModulesResult result = header.Capture.Result;
            result.MaxNodes = normalizedMaxNodes;
            result.MaxFramesScanned = normalizedMaxFramesScanned;
            result.BudgetMs = normalizedBudgetMs;
            result.StartThreadIndex = startThreadIndex;
            result.StartFrameIndex = startFrameIndex;
            result.StartModuleIndex = startModuleIndex;
            result.NextModuleIndex = startModuleIndex;
            result.MaxResultBytes = ModuleCaptureBudget.MaxResultBytes;
            header.Capture.StartModuleIndex = startModuleIndex;
            header.Capture.MaxModuleNodes = normalizedMaxNodes;
            header.Capture.ModuleBudget = new ModuleCaptureBudget();
            if (!header.Available)
            {
                result.SnapshotStatus = "unavailable";
                return result;
            }

            header.Capture.DumpPath = NormalizeExistingDumpPath(header.Capture.DumpPath);
            if (!string.IsNullOrEmpty(moduleSnapshotId))
            {
                if (!string.IsNullOrEmpty(header.Capture.DumpPath))
                    throw new StaleDebugContextException("The requested fallback module snapshot no longer matches the active dump source.");
                ModuleFallbackSnapshot cached = GetModuleFallbackSnapshot(moduleSnapshotId, header.SnapshotToken);
                ApplyModuleFallbackSnapshot(
                    cached,
                    result,
                    header.Capture,
                    startModuleIndex,
                    normalizedMaxNodes);
                result.SnapshotStable = false;
                result.SnapshotStatus = result.Partial ? "partial_best_effort" : "best_effort";
                return CompleteModuleCapture(header.Capture);
            }

            ModuleFallbackSnapshot scanSource = null;
            if (!string.IsNullOrEmpty(scanContinuationToken))
            {
                if (!string.IsNullOrEmpty(header.Capture.DumpPath))
                    throw new StaleDebugContextException("The fallback module scan no longer matches the active dump source.");
                scanSource = GetModuleFallbackSnapshot(scanContinuationToken, header.SnapshotToken);
                if (!scanSource.HasMoreFrames)
                    throw new DebuggerParameterValidationException(
                        "The module scan is already complete; page it with moduleSnapshotId.");
                if (scanSource.ThreadCount != header.ThreadCount)
                    throw new StaleDebugContextException(
                        "The debugger thread set changed after the fallback module snapshot was captured.");
            }
            if (scanSource == null && string.IsNullOrEmpty(header.Capture.DumpPath))
            {
                header.Capture.ProcessHandle = ExecuteOnMainThread(() =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    string currentToken = CreateDebuggerSnapshotToken(Debugger);
                    if (!string.Equals(header.SnapshotToken, currentToken, StringComparison.Ordinal) ||
                        header.Capture.ProcessId <= 0)
                    {
                        return IntPtr.Zero;
                    }
                    return OpenTrustedLiveProcessHandle(
                        header.Capture.ProcessId,
                        PROCESS_QUERY_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ);
                });
            }
            bool hasAuthoritativeModuleSource =
                scanSource == null &&
                (!string.IsNullOrEmpty(header.Capture.DumpPath) || header.Capture.ProcessHandle != IntPtr.Zero);

            var stopwatch = Stopwatch.StartNew();
            var boundedUniqueModules = scanSource == null
                ? new List<ModuleInfo>()
                : new List<ModuleInfo>(scanSource.Modules);
            var moduleSet = new HashSet<string>(
                boundedUniqueModules.Select(module => module.Path ?? module.Name),
                StringComparer.OrdinalIgnoreCase);
            var fallbackSnapshotBudget = new ModuleCaptureBudget();
            foreach (ModuleInfo existingModule in boundedUniqueModules)
            {
                if (!fallbackSnapshotBudget.TryConsume(existingModule))
                    throw new InvalidOperationException("Cached module snapshot exceeds its enforced result budget.");
            }
            var partialReasons = new List<string>();
            bool capturePartial = scanSource?.CapturePartial ?? false;
            string captureError = scanSource?.CaptureError;
            int scanStartThreadIndex = scanSource?.NextThreadIndex ?? 1;
            int scanStartFrameIndex = scanSource?.NextFrameIndex ?? 0;
            int lastCountedThreadIndex = scanSource?.LastCountedThreadIndex ?? 0;
            int continuationThreadId = scanSource?.ContinuationThreadId ?? 0;
            int continuationFrameCount = scanSource?.ContinuationFrameCount ?? 0;
            int framesScannedThisRequest = 0;
            result.ScannedStackFrames = scanSource?.ScannedStackFrames ?? 0;
            result.CapturedStackFrames = result.ScannedStackFrames;
            result.TotalStackFrames = scanSource?.TotalStackFrames ?? 0;
            result.StartThreadIndex = scanStartThreadIndex;
            result.StartFrameIndex = scanStartFrameIndex;
            bool stop = false;
            for (int threadIndex = scanStartThreadIndex;
                !hasAuthoritativeModuleSource && threadIndex <= header.ThreadCount && !stop;
                threadIndex++)
            {
                DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                if (stopwatch.ElapsedMilliseconds >= normalizedBudgetMs)
                {
                    result.Partial = result.Truncated = true;
                    result.HasMoreFrames = true;
                    result.NextThreadIndex = threadIndex;
                    result.NextFrameIndex = threadIndex == scanStartThreadIndex ? scanStartFrameIndex : 0;
                    break;
                }

                ThreadMetadataCapture metadata;
                try
                {
                    int capturedThreadIndex = threadIndex;
                    metadata = ExecuteOnMainThread(
                        () => CaptureThreadMetadata(capturedThreadIndex),
                        DefaultMainThreadTimeoutMs);
                }
                catch (Exception ex)
                {
                    RethrowRequestControlFlow(ex);
                    capturePartial = true;
                    AddPartialReason(partialReasons, $"module thread index {threadIndex}: {ex.Message}");
                    continue;
                }
                if (metadata?.Thread == null)
                {
                    capturePartial = true;
                    AddPartialReason(partialReasons, metadata?.Error ?? $"module thread index {threadIndex}: unavailable");
                    continue;
                }
                if (!string.IsNullOrEmpty(metadata.Error))
                {
                    capturePartial = true;
                    AddPartialReason(partialReasons, $"module thread {metadata.Thread.Id}: {metadata.Error}");
                }
                if (scanSource != null && threadIndex == scanStartThreadIndex && scanStartFrameIndex > 0 &&
                    (metadata.Thread.Id != continuationThreadId || metadata.FrameCount != continuationFrameCount))
                {
                    throw new StaleDebugContextException(
                        "The debugger stack changed after the fallback module snapshot was captured.");
                }

                if (threadIndex > lastCountedThreadIndex)
                {
                    result.TotalStackFrames += metadata.FrameCount;
                    lastCountedThreadIndex = threadIndex;
                }
                int firstFrameIndex = threadIndex == scanStartThreadIndex ? scanStartFrameIndex : 0;
                for (int frameIndex = firstFrameIndex; frameIndex < metadata.FrameCount; frameIndex++)
                {
                    DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                    if (framesScannedThisRequest >= normalizedMaxFramesScanned ||
                        stopwatch.ElapsedMilliseconds >= normalizedBudgetMs)
                    {
                        result.Partial = result.Truncated = true;
                        result.HasMoreFrames = true;
                        result.NextThreadIndex = threadIndex;
                        result.NextFrameIndex = frameIndex;
                        continuationThreadId = metadata.Thread.Id;
                        continuationFrameCount = metadata.FrameCount;
                        stop = true;
                        break;
                    }

                    ModuleFrameCapture frameCapture;
                    try
                    {
                        int capturedThreadIndex = threadIndex;
                        int capturedFrameIndex = frameIndex;
                        frameCapture = ExecuteOnMainThread(
                            () => CaptureModuleFrame(
                                capturedThreadIndex,
                                metadata.Thread.Id,
                                capturedFrameIndex),
                            DefaultMainThreadTimeoutMs);
                    }
                    catch (Exception ex)
                    {
                        RethrowRequestControlFlow(ex);
                        capturePartial = true;
                        AddPartialReason(partialReasons, $"module thread {metadata.Thread.Id} frame {frameIndex}: {ex.Message}");
                        continue;
                    }

                    result.ScannedStackFrames++;
                    framesScannedThisRequest++;
                    result.CapturedStackFrames = result.ScannedStackFrames;
                    if (!string.IsNullOrEmpty(frameCapture?.ModulePath))
                    {
                        var module = new ModuleInfo
                        {
                            Path = frameCapture.ModulePath,
                            Name = SafeGetFileName(frameCapture.ModulePath),
                            SymbolsLoaded = false,
                            IsSystemModule = IsSystemModule(frameCapture.ModulePath),
                        };
                        ModuleCaptureBudget.BoundFields(module);
                        string moduleKey = module.Path ?? module.Name;
                        if (!moduleSet.Add(moduleKey))
                        {
                            continue;
                        }
                        if (!fallbackSnapshotBudget.TryConsume(module))
                        {
                            result.Partial = result.Truncated = true;
                            result.HasMoreFrames = false;
                            capturePartial = true;
                            AddPartialReason(
                                partialReasons,
                                $"immutable module snapshot reached {ModuleCaptureBudget.MaxResultBytes} bytes");
                            stop = true;
                            break;
                        }
                        boundedUniqueModules.Add(module);
                    }
                    else if (!string.IsNullOrEmpty(frameCapture?.Error))
                    {
                        capturePartial = true;
                        AddPartialReason(partialReasons, frameCapture.Error);
                    }
                }
            }

            if (!result.HasMoreFrames)
            {
                result.NextThreadIndex = 0;
                result.NextFrameIndex = 0;
            }

            string tokenAfter = ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return CreateDebuggerSnapshotToken(Debugger);
            });
            bool dteSnapshotStable = !string.IsNullOrEmpty(header.SnapshotToken) &&
                string.Equals(header.SnapshotToken, tokenAfter, StringComparison.Ordinal);
            // Live process/dump module sources have no atomic generation token.
            // Do not overstate a multi-source capture as strongly stable.
            result.SnapshotStable = false;
            if (!dteSnapshotStable)
            {
                throw new StaleDebugContextException(
                    "The debugger context changed while the fallback module snapshot was captured.");
            }
            if (partialReasons.Count > 0)
                captureError = AppendError(captureError, string.Join("; ", partialReasons));
            result.SnapshotStatus = "best_effort";
            if (!hasAuthoritativeModuleSource)
            {
                var snapshot = new ModuleFallbackSnapshot
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DebugSnapshotToken = header.SnapshotToken,
                    ThreadCount = header.ThreadCount,
                    CreatedUtc = DateTime.UtcNow,
                    Modules = boundedUniqueModules,
                    ScannedStackFrames = result.ScannedStackFrames,
                    TotalStackFrames = result.TotalStackFrames,
                    MaxFramesScanned = normalizedMaxFramesScanned,
                    ScanStartThreadIndex = scanStartThreadIndex,
                    ScanStartFrameIndex = scanStartFrameIndex,
                    HasMoreFrames = result.HasMoreFrames,
                    NextThreadIndex = result.NextThreadIndex,
                    NextFrameIndex = result.NextFrameIndex,
                    LastCountedThreadIndex = lastCountedThreadIndex,
                    ContinuationThreadId = continuationThreadId,
                    ContinuationFrameCount = continuationFrameCount,
                    CapturePartial = capturePartial,
                    CaptureError = captureError,
                    TerminalTruncated = result.Truncated && !result.HasMoreFrames,
                };
                StoreModuleFallbackSnapshot(snapshot);
                ApplyModuleFallbackSnapshot(
                    snapshot,
                    result,
                    header.Capture,
                    startModuleIndex,
                    normalizedMaxNodes);
            }
            return CompleteModuleCapture(header.Capture);
        }

        private void StoreModuleFallbackSnapshot(ModuleFallbackSnapshot snapshot)
        {
            lock (_moduleFallbackSnapshotsLock)
            {
                DateTime cutoff = DateTime.UtcNow - ModuleFallbackSnapshotTtl;
                foreach (string expired in _moduleFallbackSnapshots
                    .Where(pair => pair.Value.CreatedUtc < cutoff)
                    .Select(pair => pair.Key)
                    .ToArray())
                {
                    _moduleFallbackSnapshots.Remove(expired);
                }
                while (_moduleFallbackSnapshots.Count >= MaxModuleFallbackSnapshots)
                {
                    string oldest = _moduleFallbackSnapshots
                        .OrderBy(pair => pair.Value.CreatedUtc)
                        .Select(pair => pair.Key)
                        .First();
                    _moduleFallbackSnapshots.Remove(oldest);
                }
                _moduleFallbackSnapshots[snapshot.Id] = snapshot;
            }
        }

        private ModuleFallbackSnapshot GetModuleFallbackSnapshot(string id, string currentDebugSnapshotToken)
        {
            ModuleFallbackSnapshot snapshot;
            lock (_moduleFallbackSnapshotsLock)
            {
                if (!_moduleFallbackSnapshots.TryGetValue(id, out snapshot) ||
                    DateTime.UtcNow - snapshot.CreatedUtc > ModuleFallbackSnapshotTtl)
                {
                    _moduleFallbackSnapshots.Remove(id ?? string.Empty);
                    throw new StaleDebugContextException("The module snapshot expired or does not exist; capture a new first page.");
                }
            }
            if (!string.Equals(
                snapshot.DebugSnapshotToken,
                currentDebugSnapshotToken,
                StringComparison.Ordinal))
            {
                throw new StaleDebugContextException("The debugger context changed after the module snapshot was captured.");
            }
            return snapshot;
        }

        private void ApplyModuleFallbackSnapshot(
            ModuleFallbackSnapshot snapshot,
            ModulesResult result,
            ModuleCapture capture,
            int startModuleIndex,
            int maxNodes)
        {
            if (startModuleIndex > snapshot.Modules.Count)
                throw new ArgumentOutOfRangeException(nameof(startModuleIndex));
            result.ModuleSnapshotId = snapshot.Id;
            result.ScanContinuationToken = snapshot.HasMoreFrames ? snapshot.Id : null;
            result.StartThreadIndex = snapshot.ScanStartThreadIndex;
            result.StartFrameIndex = snapshot.ScanStartFrameIndex;
            result.StartModuleIndex = startModuleIndex;
            result.TotalAvailable = snapshot.Modules.Count;
            result.UniqueModulesDiscovered = snapshot.Modules.Count;
            result.ScannedStackFrames = snapshot.ScannedStackFrames;
            result.CapturedStackFrames = snapshot.ScannedStackFrames;
            result.TotalStackFrames = snapshot.TotalStackFrames;
            result.MaxFramesScanned = snapshot.MaxFramesScanned;
            result.HasMoreFrames = snapshot.HasMoreFrames;
            result.NextThreadIndex = snapshot.NextThreadIndex;
            result.NextFrameIndex = snapshot.NextFrameIndex;
            result.Partial |= snapshot.CapturePartial || snapshot.TerminalTruncated || snapshot.HasMoreFrames;
            result.Truncated |= snapshot.TerminalTruncated || snapshot.HasMoreFrames;
            if (!string.Equals(result.Error, snapshot.CaptureError, StringComparison.Ordinal))
                result.Error = AppendError(result.Error, snapshot.CaptureError);
            if (snapshot.HasMoreFrames)
            {
                result.Error = AppendError(
                    result.Error,
                    "module stack discovery is incomplete; continue with scanContinuationToken");
            }
            capture.StartModuleIndex = startModuleIndex;
            ModuleSnapshotPage page = ModuleSnapshotPager.Build(
                snapshot.Modules,
                startModuleIndex,
                maxNodes,
                capture.ModuleBudget);
            result.Modules.AddRange(page.Modules);
            if (page.ResultBudgetExceeded)
            {
                result.Partial = result.Truncated = true;
                result.Error = AppendError(
                    result.Error,
                    $"module result reached {ModuleCaptureBudget.MaxResultBytes} bytes");
            }
            foreach (ModuleInfo module in page.Modules)
                if (module.Truncated) result.Partial = result.Truncated = true;
            result.ReturnedModuleCount = result.Modules.Count;
            result.NextModuleIndex = page.NextIndex;
            result.HasMoreModules = page.HasMore;
            if (result.HasMoreModules)
            {
                result.Partial = result.Truncated = true;
                result.Error = AppendError(
                    result.Error,
                    $"module page ended before unique module index {page.NextIndex} of {snapshot.Modules.Count}");
            }
        }

        private ModuleSliceHeader CaptureModuleSliceHeader()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new ModulesResult { Capability = "sliced_stack_frame_snapshot" };
            var capture = new ModuleCapture { Result = result };
            var debugger = Debugger;
            if (debugger == null || debugger.CurrentMode == dbgDebugMode.dbgDesignMode)
            {
                result.Error = "Not in debug mode";
                return new ModuleSliceHeader { Capture = capture };
            }
            var program = debugger.CurrentProgram;
            if (program == null)
            {
                result.Error = "Current debug program unavailable";
                return new ModuleSliceHeader { Capture = capture };
            }
            capture.ProcessId = debugger.CurrentProcess?.ProcessID ?? 0;
            capture.DumpPath = GetActiveDumpFilePathCandidate();
            capture.SnapshotToken = CreateDebuggerSnapshotToken(debugger);
            return new ModuleSliceHeader
            {
                Available = true,
                SnapshotToken = capture.SnapshotToken,
                ThreadCount = program.Threads?.Count ?? 0,
                Capture = capture,
            };
        }

        private ModuleFrameCapture CaptureModuleFrame(int threadIndex, int expectedThreadId, int frameIndex)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var thread = Debugger?.CurrentProgram?.Threads?.Item(threadIndex);
                if (thread == null || thread.ID != expectedThreadId)
                {
                    return new ModuleFrameCapture { Error = $"module thread index {threadIndex} changed while snapshotting" };
                }
                var frame = thread.StackFrames?.Item(frameIndex + 1) as EnvDTE.StackFrame;
                return frame == null
                    ? new ModuleFrameCapture { Error = $"module thread {expectedThreadId} frame {frameIndex}: unavailable" }
                    : new ModuleFrameCapture { ModulePath = frame.Module };
            }
            catch (Exception ex)
            {
                return new ModuleFrameCapture { Error = $"module thread {expectedThreadId} frame {frameIndex}: {ex.Message}" };
            }
        }

        private ModulesResult CompleteModuleCapture(ModuleCapture capture)
        {
            var result = capture?.Result ?? new ModulesResult();
            result.Capability = "multi_source_best_effort";
            capture = capture ?? new ModuleCapture { Result = result };
            capture.ModuleBudget = capture.ModuleBudget ?? new ModuleCaptureBudget();
            try
            {
                ModuleSourceCapture sourceCapture = null;
                if (!string.IsNullOrEmpty(capture.DumpPath))
                {
                    sourceCapture = GetModulesFromMinidump(
                        capture.DumpPath,
                        capture.StartModuleIndex,
                        capture.MaxModuleNodes,
                        capture.ModuleBudget);
                    if (sourceCapture.Architecture == TargetMachineArchitecture.Unknown)
                    {
                        result.Partial = true;
                        result.Error = AppendError(result.Error, "minidump architecture is unavailable");
                    }
                }
                else if (capture.ProcessHandle != IntPtr.Zero)
                {
                    TargetMachineArchitecture architecture = DetectProcessArchitecture(capture.ProcessHandle);
                    sourceCapture = GetModulesFromProcessHandle(
                        capture.ProcessHandle,
                        architecture,
                        capture.StartModuleIndex,
                        capture.MaxModuleNodes,
                        capture.ModuleBudget);
                }

                if (sourceCapture != null)
                {
                    // Dump/process sources are authoritative. Stack-frame modules were
                    // intentionally skipped so each source page has deterministic offsets.
                    result.Modules = sourceCapture.Modules;
                    result.TotalAvailable = sourceCapture.TotalCount;
                    result.UniqueModulesDiscovered = sourceCapture.TotalCount;
                    result.ReturnedModuleCount = sourceCapture.Modules.Count;
                    result.NextModuleIndex = sourceCapture.NextIndex;
                    result.HasMoreModules = sourceCapture.HasMore;
                    if (sourceCapture.Partial)
                    {
                        result.Partial = true;
                        result.Error = AppendError(result.Error, sourceCapture.Error);
                    }
                    if (sourceCapture.HasMore)
                    {
                        result.Partial = result.Truncated = true;
                        result.Error = AppendError(
                            result.Error,
                            $"module source page ended before index {sourceCapture.NextIndex} of {sourceCapture.TotalCount}");
                    }
                }
                else
                {
                    if (capture.ProcessId > 0)
                    {
                        result.Partial = true;
                        result.Error = AppendError(
                            result.Error,
                            "trusted live process identity is unavailable; bounded stack modules were returned");
                    }
                }

                int order = capture.StartModuleIndex;
                foreach (ModuleInfo module in result.Modules)
                {
                    DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                    module.Order = order++;
                    if (module.Truncated) result.Partial = result.Truncated = true;
                }
            }
            catch (Exception ex)
            {
                RethrowRequestControlFlow(ex);
                _log($"Background module enumeration failed: {ex.GetType().Name}");
                result.Partial = true;
                result.Error = AppendError(result.Error, $"module enumeration failed: {ex.Message}");
            }
            finally
            {
                if (capture.ProcessHandle != IntPtr.Zero)
                {
                    CloseHandle(capture.ProcessHandle);
                    capture.ProcessHandle = IntPtr.Zero;
                }
            }

            result.TotalCount = result.Modules.Count;
            result.ReturnedModuleCount = result.Modules.Count;
            result.ResultBytes = capture.ModuleBudget.ResultBytes;
            result.MaxResultBytes = ModuleCaptureBudget.MaxResultBytes;
            if (result.TotalCount == 0 && string.IsNullOrEmpty(result.Error))
            {
                result.Error = "No modules found for the active debug target.";
            }
            if (result.Partial && string.Equals(result.SnapshotStatus, "best_effort", StringComparison.Ordinal))
            {
                result.SnapshotStatus = "partial_best_effort";
            }
            return result;
        }

        private static string AppendError(string current, string next)
        {
            return string.IsNullOrWhiteSpace(current)
                ? next
                : string.IsNullOrWhiteSpace(next) ? current : $"{current}; {next}";
        }

        private sealed class ModuleSliceHeader
        {
            internal bool Available { get; set; }
            internal string SnapshotToken { get; set; }
            internal int ThreadCount { get; set; }
            internal ModuleCapture Capture { get; set; }
        }

        private sealed class ModuleFallbackSnapshot
        {
            internal string Id { get; set; }
            internal string DebugSnapshotToken { get; set; }
            internal int ThreadCount { get; set; }
            internal DateTime CreatedUtc { get; set; }
            internal List<ModuleInfo> Modules { get; set; }
            internal int ScannedStackFrames { get; set; }
            internal int TotalStackFrames { get; set; }
            internal int MaxFramesScanned { get; set; }
            internal int ScanStartThreadIndex { get; set; }
            internal int ScanStartFrameIndex { get; set; }
            internal bool HasMoreFrames { get; set; }
            internal int NextThreadIndex { get; set; }
            internal int NextFrameIndex { get; set; }
            internal int LastCountedThreadIndex { get; set; }
            internal int ContinuationThreadId { get; set; }
            internal int ContinuationFrameCount { get; set; }
            internal bool CapturePartial { get; set; }
            internal bool TerminalTruncated { get; set; }
            internal string CaptureError { get; set; }
        }

        private sealed class ModuleFrameCapture
        {
            internal string ModulePath { get; set; }
            internal string Error { get; set; }
        }

        private sealed class ModuleCapture
        {
            internal ModulesResult Result { get; set; }
            internal int ProcessId { get; set; }
            internal string DumpPath { get; set; }
            internal string SnapshotToken { get; set; }
            internal IntPtr ProcessHandle { get; set; }
            internal int StartModuleIndex { get; set; }
            internal int MaxModuleNodes { get; set; }
            internal ModuleCaptureBudget ModuleBudget { get; set; }
        }

        private sealed class ModuleSourceCapture
        {
            internal List<ModuleInfo> Modules { get; } = new List<ModuleInfo>();
            internal TargetMachineArchitecture Architecture { get; set; }
            internal bool? Is64Bit { get; set; }
            internal int TotalCount { get; set; }
            internal int NextIndex { get; set; }
            internal bool HasMore { get; set; }
            internal bool Partial { get; set; }
            internal string Error { get; set; }
        }

        private ModuleSourceCapture GetModulesFromProcessHandle(
            IntPtr processHandle,
            TargetMachineArchitecture architecture,
            int startIndex,
            int maxNodes,
            ModuleCaptureBudget budget)
        {
            bool? is64Bit = TargetArchitecturePolicy.Is64Bit(architecture);
            var capture = new ModuleSourceCapture
            {
                Architecture = architecture,
                Is64Bit = is64Bit,
            };
            if (processHandle == IntPtr.Zero)
            {
                return capture;
            }

            const uint ListModulesAll = 0x03;
            int capacity = 256;
            IntPtr[] moduleHandles;
            uint bytesNeeded;
            int reportedModuleCount = 0;
            while (true)
            {
                DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                moduleHandles = new IntPtr[capacity];
                uint bufferBytes = checked((uint)(moduleHandles.Length * IntPtr.Size));
                if (!EnumProcessModulesEx(
                    processHandle,
                    moduleHandles,
                    bufferBytes,
                    out bytesNeeded,
                    ListModulesAll))
                {
                    throw new InvalidOperationException(
                        $"EnumProcessModulesEx failed with Win32 error {Marshal.GetLastWin32Error()}.");
                }
                reportedModuleCount = checked((int)((bytesNeeded + (uint)IntPtr.Size - 1) / (uint)IntPtr.Size));
                if (reportedModuleCount <= capacity || capacity >= 4096)
                {
                    break;
                }
                capacity = Math.Min(4096, reportedModuleCount);
            }

            int enumeratedModuleCount = Math.Min(
                4096,
                Math.Min(moduleHandles.Length, checked((int)(bytesNeeded / (uint)IntPtr.Size))));
            capture.TotalCount = reportedModuleCount;
            capture.NextIndex = Math.Min(startIndex, enumeratedModuleCount);
            if (reportedModuleCount > enumeratedModuleCount)
            {
                capture.Partial = true;
                capture.Error = $"process reported {reportedModuleCount} modules, exceeding the bounded enumeration cap of {enumeratedModuleCount}";
            }
            for (int index = Math.Min(startIndex, enumeratedModuleCount); index < enumeratedModuleCount; index++)
            {
                DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                if (capture.Modules.Count >= maxNodes)
                {
                    capture.HasMore = true;
                    capture.NextIndex = index;
                    break;
                }
                IntPtr moduleHandle = moduleHandles[index];
                if (moduleHandle == IntPtr.Zero ||
                    !GetModuleInformation(
                        processHandle,
                        moduleHandle,
                        out ProcessModuleInformation moduleInformation,
                        (uint)Marshal.SizeOf(typeof(ProcessModuleInformation))))
                {
                    capture.Partial = true;
                    if (string.IsNullOrEmpty(capture.Error))
                        capture.Error = $"module index {index} metadata was unavailable; additional unavailable modules may be omitted";
                    capture.NextIndex = index + 1;
                    continue;
                }

                var pathBuilder = new StringBuilder(32768);
                uint pathLength = GetModuleFileNameEx(
                    processHandle,
                    moduleHandle,
                    pathBuilder,
                    pathBuilder.Capacity);
                string modulePath = pathLength > 0 ? pathBuilder.ToString() : null;
                ulong baseAddress = unchecked((ulong)moduleInformation.BaseOfDll.ToInt64());
                ulong sizeBytes = moduleInformation.SizeOfImage;
                var module = new ModuleInfo
                {
                    Name = string.IsNullOrEmpty(modulePath)
                        ? $"module_0x{baseAddress:X}"
                        : System.IO.Path.GetFileName(modulePath),
                    Path = modulePath,
                    Address = $"0x{baseAddress:X}",
                    SizeBytes = sizeBytes,
                    Size = FormatSize(sizeBytes),
                    SymbolsLoaded = false,
                    IsSystemModule = IsSystemModule(modulePath),
                    Architecture = TargetArchitecturePolicy.ToProtocolName(architecture),
                    Is64Bit = is64Bit,
                };
                if (!budget.TryConsume(module))
                {
                    capture.HasMore = true;
                    capture.NextIndex = index;
                    break;
                }
                capture.Modules.Add(module);
                capture.NextIndex = index + 1;
            }
            capture.HasMore = capture.HasMore || capture.NextIndex < enumeratedModuleCount;
            return capture;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessModuleInformation
        {
            internal IntPtr BaseOfDll;
            internal uint SizeOfImage;
            internal IntPtr EntryPoint;
        }

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumProcessModulesEx(
            IntPtr processHandle,
            [Out] IntPtr[] moduleHandles,
            uint bufferBytes,
            out uint bytesNeeded,
            uint filterFlag);

        [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetModuleFileNameEx(
            IntPtr processHandle,
            IntPtr moduleHandle,
            StringBuilder fileName,
            int size);

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetModuleInformation(
            IntPtr processHandle,
            IntPtr moduleHandle,
            out ProcessModuleInformation moduleInformation,
            uint size);

        private bool IsSystemModule(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            string lowerPath = path.ToLower();
            return lowerPath.Contains(@"\windows\") ||
                   lowerPath.Contains(@"\system32\") ||
                   lowerPath.Contains(@"\syswow64\") ||
                   lowerPath.Contains(@"\winsxs\");
        }

        [SuppressMessage("Usage", "VSTHRD010", Justification = "Captures only a dump path or trusted process handle on one bounded UI dispatch; probing occurs off-thread.")]
        private TargetMachineArchitecture DetectTargetArchitecture()
        {
            TargetArchitectureProbe probe = ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                Debugger5 debugger = Debugger;
                if (debugger == null || debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                    return new TargetArchitectureProbe();
                string dumpPath = NormalizeExistingDumpPath(GetActiveDumpFilePathCandidate());
                if (!string.IsNullOrEmpty(dumpPath))
                    return new TargetArchitectureProbe { DumpPath = dumpPath };
                int processId = 0;
                try { processId = debugger.CurrentProcess?.ProcessID ?? 0; } catch { }
                return new TargetArchitectureProbe
                {
                    ProcessHandle = OpenTrustedLiveProcessHandle(
                        processId,
                        PROCESS_QUERY_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION),
                    DebuggerArchitecture = DetectDebuggerX86Architecture(debugger),
                };
            });
            if (!string.IsNullOrEmpty(probe.DumpPath))
                return ReadMinidumpTargetArchitecture(probe.DumpPath);
            if (probe.ProcessHandle == IntPtr.Zero) return probe.DebuggerArchitecture;
            try
            {
                TargetMachineArchitecture processArchitecture =
                    DetectProcessArchitecture(probe.ProcessHandle);
                return processArchitecture == TargetMachineArchitecture.Unknown
                    ? probe.DebuggerArchitecture
                    : processArchitecture;
            }
            finally { CloseHandle(probe.ProcessHandle); }
        }

        private sealed class TargetArchitectureProbe
        {
            internal string DumpPath { get; set; }
            internal IntPtr ProcessHandle { get; set; }
            internal TargetMachineArchitecture DebuggerArchitecture { get; set; }
        }

        private static TargetMachineArchitecture DetectDebuggerX86Architecture(Debugger5 debugger)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                EnvDTE.Expression rip = debugger?.GetExpression("@rip", false, 250);
                if (rip?.IsValidValue == true) return TargetMachineArchitecture.X64;
            }
            catch { }
            try
            {
                EnvDTE.Expression eip = debugger?.GetExpression("@eip", false, 250);
                if (eip?.IsValidValue == true) return TargetMachineArchitecture.X86;
            }
            catch { }
            return TargetMachineArchitecture.Unknown;
        }

        private TargetMachineArchitecture DetectProcessArchitecture(IntPtr processHandle)
        {
            if (processHandle == IntPtr.Zero) return TargetMachineArchitecture.Unknown;
            try
            {
                if (IsWow64Process2(
                    processHandle,
                    out ushort processMachine,
                    out ushort nativeMachine))
                {
                    ushort effectiveMachine = processMachine == ImageFileMachineUnknown
                        ? nativeMachine
                        : processMachine;
                    return TargetArchitecturePolicy.FromImageFileMachine(effectiveMachine);
                }
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { return TargetMachineArchitecture.Unknown; }
            catch { return TargetMachineArchitecture.Unknown; }
            try
            {
                if (!IsWow64Process(processHandle, out bool isWow64))
                    return TargetMachineArchitecture.Unknown;
                if (isWow64) return TargetMachineArchitecture.X86;
                GetNativeSystemInfo(out NativeSystemInfo nativeSystemInfo);
                return TargetArchitecturePolicy.FromMinidumpProcessorArchitecture(
                    nativeSystemInfo.ProcessorArchitecture);
            }
            catch { return TargetMachineArchitecture.Unknown; }
        }

        private const ushort ImageFileMachineUnknown = 0x0000;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWow64Process2(
            IntPtr processHandle,
            out ushort processMachine,
            out ushort nativeMachine);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWow64Process(IntPtr processHandle, out bool wow64Process);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSystemInfo
        {
            internal ushort ProcessorArchitecture;
            internal ushort Reserved;
            internal uint PageSize;
            internal IntPtr MinimumApplicationAddress;
            internal IntPtr MaximumApplicationAddress;
            internal IntPtr ActiveProcessorMask;
            internal uint NumberOfProcessors;
            internal uint ProcessorType;
            internal uint AllocationGranularity;
            internal ushort ProcessorLevel;
            internal ushort ProcessorRevision;
        }

        [DllImport("kernel32.dll")]
        private static extern void GetNativeSystemInfo(out NativeSystemInfo systemInfo);

        private string GetModuleVersion(string modulePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(modulePath) ||
                    modulePath.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    return null;
                }
                if (System.IO.File.Exists(modulePath))
                {
                    var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(modulePath);
                    if (!string.IsNullOrEmpty(versionInfo.FileVersion))
                    {
                        return versionInfo.FileVersion;
                    }
                }
            }
            catch (Exception ex)
            {
                _log($"[GetModuleVersion] Error: {ex.GetType().Name}");
            }
            return null;
        }

        private string FormatSize(uint bytes)
        {
            return FormatSize((ulong)bytes);
        }

        private string FormatSize(ulong bytes)
        {
            if (bytes >= 1024 * 1024)
                return $"{bytes / (1024 * 1024.0):F2} MB";
            if (bytes >= 1024)
                return $"{bytes / 1024.0:F2} KB";
            return $"{bytes} bytes";
        }

        private string FormatPeTimestamp(uint timeDateStamp)
        {
            if (timeDateStamp == 0)
            {
                return null;
            }

            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(timeDateStamp).UtcDateTime.ToString("o");
            }
            catch
            {
                return $"0x{timeDateStamp:X8}";
            }
        }

        [SuppressMessage("Usage", "VSTHRD010", Justification = "Each exception expression is captured by a separate bounded UI dispatch.")]
        public ExceptionResult GetException()
        {
            var budget = new ExceptionCaptureBudget();
            var result = new ExceptionResult
            {
                Capability = "sliced_managed_exception_best_effort",
                Status = "failed",
                Partial = true,
                MaxCapturedBytes = ExceptionCaptureBudget.MaxSerializedBytes,
            };
            budget.TryEnterNode();
            ExceptionEvalValue root;
            try
            {
                root = ExecuteOnMainThread(() => CaptureExceptionExpression("$exception"));
            }
            catch (Exception ex)
            {
                RethrowRequestControlFlow(ex);
                budget.AddReason(ex.Message);
                return FinalizeExceptionCapture(result, budget);
            }
            if (root == null)
            {
                result.Status = "unavailable";
                budget.AddReason("Not in break mode");
                return FinalizeExceptionCapture(result, budget);
            }
            if (!root.IsValid)
            {
                result.Status = "failed";
                result.Partial = true;
                result.HasException = false;
                budget.AddReason("The debugger could not evaluate $exception" +
                    (string.IsNullOrWhiteSpace(root.Value) ? string.Empty : ": " + root.Value));
                return FinalizeExceptionCapture(result, budget);
            }
            if (string.Equals(root.Value, "null", StringComparison.OrdinalIgnoreCase))
            {
                result.Status = "none";
                result.Partial = false;
                result.HasException = false;
                return FinalizeExceptionCapture(result, budget);
            }

            result.Status = "ok";
            result.Partial = false;
            result.HasException = true;
            result.Type = budget.CaptureString(root.Type, "type", out bool rootTypeTruncated);
            result.Truncated |= rootTypeTruncated;
            string expressionPath = "$exception";
            ExceptionResult current = result;
            const int maximumDepth = 10;
            result.MaxDepth = maximumDepth;
            for (int depth = 0; depth < maximumDepth; depth++)
            {
                DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                if (budget.IsExceeded)
                {
                    result.HasMoreInnerExceptions = true;
                    break;
                }
                result.CapturedDepth = depth + 1;
                CaptureExceptionMember(current, expressionPath, "Message", value => current.Message = value, budget);
                CaptureExceptionMember(current, expressionPath, "StackTrace", value => current.StackTrace = value, budget);
                CaptureExceptionMember(current, expressionPath, "Source", value => current.Source = value, budget);
                CaptureExceptionMember(current, expressionPath, "HResult", value => current.HResult = value, budget);

                string innerPath = expressionPath + ".InnerException";
                ExceptionEvalValue inner;
                try
                {
                    inner = ExecuteOnMainThread(() => CaptureExceptionExpression(innerPath));
                }
                catch (Exception ex)
                {
                    RethrowRequestControlFlow(ex);
                    budget.AddReason(innerPath + ": " + ex.Message);
                    break;
                }
                if (inner == null)
                {
                    budget.AddReason(innerPath + ": debugger context became unavailable");
                    break;
                }
                if (!inner.IsValid)
                {
                    budget.AddReason(innerPath + ": expression could not be evaluated" +
                        (string.IsNullOrWhiteSpace(inner.Value) ? string.Empty : " (" + inner.Value + ")"));
                    break;
                }
                if (string.Equals(inner.Value, "null", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                if (depth == maximumDepth - 1)
                {
                    result.HasMoreInnerExceptions = true;
                    budget.AddReason($"Inner exception chain was truncated after {maximumDepth} nodes");
                    break;
                }
                if (!budget.TryEnterNode())
                {
                    result.HasMoreInnerExceptions = true;
                    break;
                }
                string innerType = budget.CaptureString(
                    inner.Type,
                    $"innerException[{depth + 1}].type",
                    out bool innerTypeTruncated);
                current.InnerException = new ExceptionResult
                {
                    Capability = result.Capability,
                    Status = "ok",
                    Partial = false,
                    HasException = true,
                    Type = innerType,
                    Truncated = innerTypeTruncated,
                    MaxCapturedBytes = ExceptionCaptureBudget.MaxSerializedBytes,
                };
                current = current.InnerException;
                expressionPath = innerPath;
            }
            return FinalizeExceptionCapture(result, budget);
        }

        private static ExceptionResult FinalizeExceptionCapture(
            ExceptionResult result,
            ExceptionCaptureBudget budget)
        {
            result.CapturedBytes = budget.SerializedBytes;
            result.MaxCapturedBytes = ExceptionCaptureBudget.MaxSerializedBytes;
            if (budget.IsIncomplete)
            {
                result.Partial = true;
                result.Truncated = true;
                result.Error = AppendError(result.Error, budget.CombinedReason);
            }
            return result;
        }

        [SuppressMessage("Usage", "VSTHRD010", Justification = "The member expression is captured by ExecuteOnMainThread.")]
        private void CaptureExceptionMember(
            ExceptionResult owner,
            string expressionPath,
            string memberName,
            Action<string> assign,
            ExceptionCaptureBudget budget)
        {
            string memberPath = expressionPath + "." + memberName;
            try
            {
                ExceptionEvalValue member = ExecuteOnMainThread(() => CaptureExceptionExpression(memberPath));
                if (member?.IsValid == true)
                {
                    string value = budget.CaptureString(
                        (member.Value ?? string.Empty).Trim('"'),
                        memberPath,
                        out bool truncated);
                    assign(value);
                    owner.Truncated |= truncated;
                    owner.Partial |= truncated;
                }
                else
                {
                    budget.AddReason(memberPath + ": expression could not be evaluated" +
                        (string.IsNullOrWhiteSpace(member?.Value) ? string.Empty : " (" + member.Value + ")"));
                    owner.Partial = true;
                }
            }
            catch (Exception ex)
            {
                RethrowRequestControlFlow(ex);
                budget.AddReason(memberPath + ": " + ex.Message);
                owner.Partial = true;
            }
        }

        private ExceptionEvalValue CaptureExceptionExpression(string expression)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var debugger = Debugger;
            if (debugger == null || debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
            {
                return null;
            }
            MarkUnsafeEvaluationIssued();
            EnvDTE.Expression value = debugger.GetExpression(expression, false, 500);
            return new ExceptionEvalValue
            {
                IsValid = value != null && value.IsValidValue,
                Value = value?.Value,
                Type = value?.Type
            };
        }

        private sealed class ExceptionEvalValue
        {
            internal bool IsValid { get; set; }
            internal string Value { get; set; }
            internal string Type { get; set; }
        }

        #endregion
    }
}
