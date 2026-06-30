using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using EnvDTE;
using EnvDTE80;
using EnvDTE100;
using Microsoft.VisualStudio.Shell;
using VSDebuggerMCP.Protocol;

namespace VSDebuggerMCP
{
    /// <summary>
    /// Service that wraps Visual Studio debugger APIs to provide debugging information.
    /// </summary>
    public class DebuggerService
    {
        private readonly DTE2 _dte;
        private readonly Action<string> _log;
        private readonly DisassemblyService _disasmService;
        private readonly Dictionary<string, string> _sourceFileByClassCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _sourceFileByClassMissCache = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private string _sourceLookupSolutionKey = string.Empty;
        private int _selectedStackFrameIndex;
        private string _activeDumpFilePath;
        private const int SnapshotMaxAttempts = 2;
        private const int SnapshotRetryDelayMs = 40;
        private const int ExpressionEvaluationTimeoutMs = 1500;
        private const int ExpressionBatchBudgetMs = 30000;
        private const int ExpressionExpansionBudgetMs = 2500;
        private const int ExpressionExpansionMaxNodes = 128;
        private const int ExpressionExpansionMaxDepth = 4;
        private const int LocalsExpansionBudgetMs = 1500;
        private const int LocalsExpansionMaxNodes = 128;
        private const int LocalsExpansionMaxDepth = 4;
        private const int DefaultMainThreadTimeoutMs = 10000;
        private const int HeavyMainThreadTimeoutMs = 60000;
        private const int DebugBundleMainThreadTimeoutMs = 120000;
        private const int ThreadEnumerationBudgetMs = 2500;
        private const int ThreadFrameExpansionMaxNodes = 128;
        private const int ThreadFramesMaxPerThread = 16;
        private const int SourceLookupCacheMaxEntries = 1024;
        private const int BreakpointBindWaitMs = 250;
        private const int BreakpointBindPollMs = 25;
        private const int BreakpointProbeMaxDistance = 25;
        private const int DumpDebugStartTimeoutMs = 3000;
        private const int DumpDebugStartPollMs = 100;
        private static readonly TimeSpan SourceLookupMissCacheTtl = TimeSpan.FromSeconds(30);

        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint MEM_COMMIT = 0x1000;
        private const uint PAGE_NOACCESS = 0x01;
        private const uint PAGE_READONLY = 0x02;
        private const uint PAGE_READWRITE = 0x04;
        private const uint PAGE_WRITECOPY = 0x08;
        private const uint PAGE_EXECUTE_READ = 0x20;
        private const uint PAGE_EXECUTE_READWRITE = 0x40;
        private const uint PAGE_EXECUTE_WRITECOPY = 0x80;
        private const uint PAGE_GUARD = 0x100;

        public DebuggerService(DTE2 dte, Action<string> log)
        {
            _dte = dte ?? throw new ArgumentNullException(nameof(dte));
            _log = log ?? ((s) => { });
            _disasmService = new DisassemblyService();
        }

        private Debugger5 Debugger
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return _dte.Debugger as Debugger5;
            }
        }

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
            }

            if (result.IsPaused && debugger.CurrentStackFrame != null)
            {
                var frame = debugger.CurrentStackFrame;
                var lineInfo = GetFrameLineNumberEx(frame);
                result.CurrentLocation = new LocationInfo
                {
                    FilePath = GetFrameFileName(frame),
                    FunctionName = frame.FunctionName,
                    LineNumber = lineInfo.LineNumber,
                    ColumnNumber = 1
                };
            }

            var stopReason = DetermineStopReason(debugger);
            result.StopReason = stopReason.Reason;
            result.StopReasonRaw = stopReason.RawReason;

            return result;
        }

        private (string Reason, string RawReason) DetermineStopReason(Debugger5 debugger)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (debugger == null || debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
            {
                return ("not_paused", null);
            }

            string rawReason = TryReadDebuggerBreakReason(debugger);
            string mappedReason = MapStopReason(rawReason);
            if (!string.Equals(mappedReason, "pause_unknown", StringComparison.OrdinalIgnoreCase))
            {
                return (mappedReason, rawReason);
            }

            if (TryHasBreakpointHit(debugger))
            {
                return ("breakpoint", rawReason);
            }

            if (TryHasCurrentException(debugger))
            {
                return ("exception", rawReason);
            }

            return ("pause_unknown", rawReason);
        }

        private string TryReadDebuggerBreakReason(Debugger5 debugger)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var debuggerType = debugger.GetType();
                var property = debuggerType.GetProperty("BreakReason") ??
                               debuggerType.GetProperty("CurrentBreakReason") ??
                               debuggerType.GetProperty("CurrentReason");
                if (property == null)
                {
                    return null;
                }

                var value = property.GetValue(debugger, null);
                return value?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private string MapStopReason(string rawReason)
        {
            if (string.IsNullOrWhiteSpace(rawReason))
            {
                return "pause_unknown";
            }

            string normalized = rawReason.Trim().ToLowerInvariant();
            if (normalized.Contains("exception"))
            {
                return "exception";
            }

            if (normalized.Contains("step"))
            {
                return "step";
            }

            if (normalized.Contains("breakpoint") || normalized.Contains("tracepoint"))
            {
                return "breakpoint";
            }

            if (normalized.Contains("user") || normalized.Contains("pause") || normalized.Contains("break"))
            {
                return "user_break";
            }

            return "pause_unknown";
        }

        private bool TryHasBreakpointHit(Debugger5 debugger)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var property = debugger.GetType().GetProperty("BreakpointLastHit");
                if (property == null)
                {
                    return false;
                }

                return property.GetValue(debugger, null) != null;
            }
            catch
            {
                return false;
            }
        }

        private bool TryHasCurrentException(Debugger5 debugger)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var exceptionExpr = debugger.GetExpression("$exception", false, ExpressionEvaluationTimeoutMs);
                return exceptionExpr != null && exceptionExpr.IsValidValue;
            }
            catch
            {
                return false;
            }
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

        public CallStackResult GetCallStack()
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var bestEffort = new CallStackResult
                {
                    SnapshotStatus = "unavailable",
                    Capability = "best_effort_frame_metadata"
                };

                for (int attempt = 1; attempt <= SnapshotMaxAttempts; attempt++)
                {
                    if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                    {
                        bestEffort.SnapshotAttempts = attempt;
                        bestEffort.SnapshotStatus = "unavailable";
                        return bestEffort;
                    }

                    try
                    {
                        string tokenBefore = CreateDebuggerSnapshotToken(debugger);
                        var snapshot = BuildCallStackResult(debugger);
                        string tokenAfter = CreateDebuggerSnapshotToken(debugger);
                        bool stable = !string.IsNullOrEmpty(tokenBefore) && tokenBefore == tokenAfter;

                        snapshot.SnapshotStable = stable;
                        snapshot.SnapshotAttempts = attempt;
                        snapshot.SnapshotStatus = stable
                            ? "ok"
                            : (snapshot.Frames.Count > 0 ? "unstable" : "unavailable");

                        bestEffort = snapshot;
                        if (stable)
                        {
                            return snapshot;
                        }
                    }
                    catch (Exception ex)
                    {
                        _log($"GetCallStack snapshot attempt {attempt} failed: {ex.Message}");
                        bestEffort.SnapshotAttempts = attempt;
                        if (bestEffort.Frames.Count > 0)
                        {
                            bestEffort.SnapshotStatus = "unstable";
                        }
                    }

                    if (attempt < SnapshotMaxAttempts)
                    {
                        System.Threading.Thread.Sleep(SnapshotRetryDelayMs);
                    }
                }

                return bestEffort;
            });
        }

        private CallStackResult BuildCallStackResult(Debugger5 debugger)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new CallStackResult
            {
                Capability = "best_effort_frame_metadata"
            };

            var currentThread = debugger.CurrentThread;
            if (currentThread == null)
            {
                return result;
            }

            result.ThreadId = currentThread.ID;
            result.ThreadName = currentThread.Name ?? $"Thread {currentThread.ID}";

            int index = 0;
            const int maxArgumentFrames = 5;
            try
            {
                foreach (EnvDTE.StackFrame frame in currentThread.StackFrames)
                {
                    try
                    {
                        var lineInfo = GetFrameLineNumberEx(frame, index);
                        var frameInfo = new StackFrameInfo
                        {
                            Index = index,
                            FunctionName = frame.FunctionName,
                            FilePath = GetFrameFileName(frame, index),
                            LineNumber = lineInfo.LineNumber,
                            LineNumberReliable = lineInfo.IsReliable,
                            ColumnNumber = 1,
                            Module = frame.Module,
                            Language = frame.Language,
                            ReturnType = frame.ReturnType
                        };

                        if (index < maxArgumentFrames)
                        {
                            try
                            {
                                var args = new List<VariableInfo>();
                                foreach (EnvDTE.Expression arg in frame.Arguments)
                                {
                                    args.Add(new VariableInfo
                                    {
                                        Name = arg.Name,
                                        Type = arg.Type,
                                        Value = arg.Value,
                                        HasChildren = false,
                                        IsReadOnly = true
                                    });
                                }
                                if (args.Count > 0)
                                {
                                    frameInfo.Arguments = args;
                                }
                            }
                            catch
                            {
                                // Arguments may not be available for this frame
                            }
                        }

                        result.Frames.Add(frameInfo);
                        index++;
                    }
                    catch
                    {
                        // Skip frames that fail to materialize.
                    }
                }
            }
            catch
            {
                // Stack frame enumeration can fail for some engines/threads.
            }

            return result;
        }

        #endregion

        #region Locals

        public LocalsResult GetLocals(int maxDepth = 2)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return BuildLocalsResult(Debugger, maxDepth);
            });
        }

        private LocalsResult BuildLocalsResult(Debugger5 debugger, int maxDepth)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new LocalsResult();
            result.FrameIndex = _selectedStackFrameIndex;

            if (debugger == null || debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
            {
                return result;
            }

            var currentFrame = debugger.CurrentStackFrame;
            if (currentFrame == null)
            {
                return result;
            }

            int normalizedMaxDepth = Math.Max(0, Math.Min(maxDepth, LocalsExpansionMaxDepth));
            var budget = new VariableExpansionBudget(LocalsExpansionMaxNodes, LocalsExpansionBudgetMs);

            foreach (EnvDTE.Expression local in currentFrame.Locals)
            {
                if (!budget.CanContinue())
                {
                    break;
                }

                var localInfo = CreateVariableInfo(local, 0, normalizedMaxDepth, budget);
                if (localInfo != null)
                {
                    result.Variables.Add(localInfo);
                }
            }

            try
            {
                foreach (EnvDTE.Expression arg in currentFrame.Arguments)
                {
                    if (!budget.CanContinue())
                    {
                        break;
                    }

                    var varInfo = CreateVariableInfo(arg, 0, normalizedMaxDepth, budget);
                    if (varInfo != null)
                    {
                        varInfo.Name = $"[arg] {varInfo.Name}";
                        result.Variables.Add(varInfo);
                    }
                }
            }
            catch
            {
                // Arguments may not be available
            }

            if (budget.IsExceeded)
            {
                result.Partial = true;
                result.Error = budget.Reason;
            }

            return result;
        }

        private sealed class VariableExpansionBudget
        {
            private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
            private readonly int _maxNodes;
            private readonly int _maxElapsedMs;
            private int _nodes;

            public bool IsExceeded { get; private set; }
            public string Reason { get; private set; }

            public VariableExpansionBudget(int maxNodes, int maxElapsedMs)
            {
                _maxNodes = maxNodes;
                _maxElapsedMs = maxElapsedMs;
            }

            public bool TryEnterNode()
            {
                if (IsExceeded)
                {
                    return false;
                }

                if (_nodes >= _maxNodes)
                {
                    MarkExceeded($"Expansion truncated after {_maxNodes} nodes");
                    return false;
                }

                if (_stopwatch.ElapsedMilliseconds >= _maxElapsedMs)
                {
                    MarkExceeded($"Expansion truncated after {_maxElapsedMs}ms");
                    return false;
                }

                _nodes++;
                return true;
            }

            public bool CanContinue()
            {
                if (IsExceeded)
                {
                    return false;
                }

                if (_nodes >= _maxNodes)
                {
                    MarkExceeded($"Expansion truncated after {_maxNodes} nodes");
                    return false;
                }

                if (_stopwatch.ElapsedMilliseconds >= _maxElapsedMs)
                {
                    MarkExceeded($"Expansion truncated after {_maxElapsedMs}ms");
                    return false;
                }

                return true;
            }

            public void MarkExceeded(string reason)
            {
                IsExceeded = true;
                Reason = reason;
            }
        }

        private VariableInfo CreateVariableInfo(EnvDTE.Expression expr, int currentDepth, int maxDepth, VariableExpansionBudget budget = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (budget != null && !budget.TryEnterNode())
            {
                return null;
            }

            bool hasChildren = false;
            if (currentDepth < maxDepth && (budget == null || budget.CanContinue()))
            {
                try
                {
                    hasChildren = expr.DataMembers?.Count > 0;
                }
                catch
                {
                    hasChildren = false;
                }
            }

            var info = new VariableInfo
            {
                Name = expr.Name,
                Type = expr.Type,
                Value = expr.Value,
                HasChildren = hasChildren,
                IsReadOnly = expr.IsValidValue == false
            };

            if (info.HasChildren && currentDepth < maxDepth)
            {
                info.Children = new List<VariableInfo>();
                try
                {
                    foreach (EnvDTE.Expression child in expr.DataMembers)
                    {
                        if (budget != null && !budget.CanContinue())
                        {
                            break;
                        }

                        var childInfo = CreateVariableInfo(child, currentDepth + 1, maxDepth, budget);
                        if (childInfo != null)
                        {
                            info.Children.Add(childInfo);
                        }
                    }
                }
                catch
                {
                    // Children may not be accessible
                }
            }

            return info;
        }

        #endregion

        #region Threads

        public ThreadsResult GetThreads(bool includeFrames = true, int maxFramesPerThread = -1, int maxNodes = -1, int budgetMs = -1)
        {
            int normalizedMaxFramesPerThread = NormalizeThreadFrameLimit(maxFramesPerThread);
            int normalizedMaxNodes = NormalizeThreadNodeLimit(maxNodes);
            int normalizedBudgetMs = NormalizeThreadBudgetMs(budgetMs);
            int mainThreadTimeoutMs = ComputeThreadMainThreadTimeoutMs(normalizedBudgetMs);

            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var bestEffort = new ThreadsResult
                {
                    SnapshotStatus = "unavailable",
                    Capability = "best_effort_thread_metadata"
                };

                for (int attempt = 1; attempt <= SnapshotMaxAttempts; attempt++)
                {
                    if (debugger.CurrentMode == dbgDebugMode.dbgDesignMode)
                    {
                        bestEffort.SnapshotAttempts = attempt;
                        bestEffort.SnapshotStatus = "unavailable";
                        return bestEffort;
                    }

                    try
                    {
                        string tokenBefore = CreateDebuggerSnapshotToken(debugger);
                        var snapshot = BuildThreadsResult(debugger, includeFrames, normalizedMaxFramesPerThread, normalizedMaxNodes, normalizedBudgetMs);
                        string tokenAfter = CreateDebuggerSnapshotToken(debugger);
                        bool stable = !string.IsNullOrEmpty(tokenBefore) && tokenBefore == tokenAfter;

                        snapshot.SnapshotStable = stable;
                        snapshot.SnapshotAttempts = attempt;
                        snapshot.SnapshotStatus = DetermineThreadsSnapshotStatus(snapshot, stable);

                        bestEffort = snapshot;
                        if (stable)
                        {
                            return snapshot;
                        }
                    }
                    catch (Exception ex)
                    {
                        _log($"GetThreads snapshot attempt {attempt} failed: {ex.Message}");
                        bestEffort.SnapshotAttempts = attempt;
                        if (bestEffort.Threads.Count > 0)
                        {
                            bestEffort.SnapshotStatus = "unstable";
                        }
                    }

                    if (attempt < SnapshotMaxAttempts)
                    {
                        System.Threading.Thread.Sleep(SnapshotRetryDelayMs);
                    }
                }

                return bestEffort;
            }, mainThreadTimeoutMs);
        }

        private ThreadsResult BuildThreadsResult(Debugger5 debugger, bool includeFrames = true, int maxFramesPerThread = ThreadFramesMaxPerThread, int maxNodes = ThreadFrameExpansionMaxNodes, int budgetMs = ThreadEnumerationBudgetMs)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new ThreadsResult
            {
                Capability = "best_effort_thread_metadata"
            };
            var currentThread = debugger.CurrentThread;
            result.CurrentThreadId = currentThread?.ID ?? 0;
            var budget = new VariableExpansionBudget(maxNodes, budgetMs);
            var partialReasons = new List<string>();
            bool truncated = false;
            string truncatedReason = null;

            EnvDTE.Program currentProgram = null;
            try
            {
                currentProgram = debugger.CurrentProgram;
            }
            catch (Exception ex)
            {
                result.Partial = true;
                result.Error = $"Current debug program unavailable: {ex.Message}";
                return result;
            }

            if (currentProgram == null)
            {
                result.Error = "Current debug program unavailable";
                return result;
            }

            try
            {
                foreach (EnvDTE.Thread thread in currentProgram.Threads)
                {
                    if (!budget.CanContinue())
                    {
                        break;
                    }

                    var threadInfo = new ThreadInfo
                    {
                        Id = thread.ID,
                        Name = thread.Name ?? $"Thread {thread.ID}",
                        IsCurrent = currentThread != null && thread.ID == currentThread.ID,
                        IsFrozen = thread.IsFrozen,
                        Priority = thread.Priority,
                        SuspendCount = thread.SuspendCount
                    };

                    try
                    {
                        var stackFrames = thread.StackFrames;
                        int stackFrameCount = stackFrames.Count;
                        if (stackFrameCount > 0)
                        {
                            EnvDTE.StackFrame topFrame = null;
                            for (int i = 1; i <= stackFrameCount; i++)
                            {
                                try
                                {
                                    var candidateFrame = stackFrames.Item(i) as EnvDTE.StackFrame;
                                    if (candidateFrame != null && !IsSyntheticExecutionPointFrame(candidateFrame))
                                    {
                                        topFrame = candidateFrame;
                                        break;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    AddPartialReason(partialReasons, $"thread {thread.ID} top frame {i}: {ex.Message}");
                                }
                            }

                            if (topFrame != null)
                            {
                                var lineNum = GetFrameLineNumber(topFrame);
                                threadInfo.Location = $"{topFrame.FunctionName} ({GetFrameFileName(topFrame)}:{lineNum})";
                            }

                            if (includeFrames)
                            {
                                var framesList = new List<StackFrameInfo>();
                                int frameIndex = 0;
                                foreach (EnvDTE.StackFrame frame in stackFrames)
                                {
                                    if (!budget.TryEnterNode())
                                    {
                                        break;
                                    }

                                    if (frameIndex >= maxFramesPerThread)
                                    {
                                        truncated = true;
                                        truncatedReason = $"Thread frame enumeration truncated after {maxFramesPerThread} frames per thread";
                                        break;
                                    }

                                    try
                                    {
                                        int currentFrameIndex = frameIndex++;
                                        int sourceFallbackFrameIndex = threadInfo.IsCurrent ? currentFrameIndex : -1;
                                        var lineInfo = GetFrameLineNumberEx(frame, sourceFallbackFrameIndex);
                                        framesList.Add(new StackFrameInfo
                                        {
                                            Index = currentFrameIndex,
                                            FunctionName = frame.FunctionName,
                                            FilePath = GetFrameFileName(frame, sourceFallbackFrameIndex),
                                            LineNumber = lineInfo.LineNumber,
                                            LineNumberReliable = lineInfo.IsReliable,
                                            ColumnNumber = 1,
                                            Module = frame.Module,
                                            Language = frame.Language,
                                            ReturnType = frame.ReturnType
                                        });
                                    }
                                    catch
                                    {
                                        AddPartialReason(partialReasons, $"thread {thread.ID} frame {frameIndex}: unavailable");
                                    }
                                }
                                if (framesList.Count > 0)
                                {
                                    threadInfo.Frames = framesList;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        threadInfo.Location = "Unknown";
                        AddPartialReason(partialReasons, $"thread {thread.ID}: {ex.Message}");
                    }

                    result.Threads.Add(threadInfo);

                    if (budget.IsExceeded)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                // Thread enumeration can fail transiently while engine updates.
                result.Partial = true;
                result.Error = ex.Message;
            }

            if (budget.IsExceeded)
            {
                result.Partial = true;
                result.Error = budget.Reason;
            }
            else if (truncated)
            {
                result.Partial = true;
                result.Error = truncatedReason;
            }
            else if (partialReasons.Count > 0)
            {
                result.Partial = true;
                result.Error = string.Join("; ", partialReasons);
            }

            return result;
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

        private int ComputeThreadMainThreadTimeoutMs(int normalizedBudgetMs)
        {
            return Math.Max(HeavyMainThreadTimeoutMs, Math.Min(DebugBundleMainThreadTimeoutMs, normalizedBudgetMs + 5000));
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
                return int.MaxValue;
            }

            return Math.Min(maxFramesPerThread, 4096);
        }

        private int NormalizeThreadNodeLimit(int maxNodes)
        {
            if (maxNodes < 0)
            {
                return ThreadFrameExpansionMaxNodes;
            }

            if (maxNodes == 0)
            {
                return int.MaxValue;
            }

            return Math.Min(maxNodes, 100000);
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

        #endregion

        #region Modules

        public ModulesResult GetModules()
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return BuildModulesResult(Debugger);
            }, HeavyMainThreadTimeoutMs);
        }

        private ModulesResult BuildModulesResult(Debugger5 debugger)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new ModulesResult
            {
                Capability = "multi_source_best_effort"
            };

            if (debugger == null || debugger.CurrentMode == dbgDebugMode.dbgDesignMode)
            {
                result.Error = "Not in debug mode";
                return result;
            }

            try
            {
                // Method 1: Try to get modules from stack frames (most reliable)
                var moduleSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var moduleInfoDict = new Dictionary<string, ModuleInfo>(StringComparer.OrdinalIgnoreCase);

                // Collect modules from all threads' stack frames
                try
                {
                    foreach (EnvDTE.Thread thread in debugger.CurrentProgram.Threads)
                    {
                        try
                        {
                            foreach (EnvDTE.StackFrame frame in thread.StackFrames)
                            {
                                string modulePath = frame.Module;
                                if (!string.IsNullOrEmpty(modulePath) && !moduleSet.Contains(modulePath))
                                {
                                    moduleSet.Add(modulePath);
                                    var moduleInfo = CreateModuleInfoFromPath(modulePath, debugger);
                                    moduleInfoDict[modulePath] = moduleInfo;
                                }
                            }
                        }
                        catch
                        {
                            // Some threads may not have accessible stack frames
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log($"Error collecting modules from stack frames: {ex.Message}");
                }

                // Method 2: Try to enumerate modules via PEB (Process Environment Block) for native processes
                try
                {
                    var additionalModules = GetModulesFromPEB(debugger);
                    foreach (var mod in additionalModules)
                    {
                        MergeModuleInfo(moduleSet, moduleInfoDict, mod, addIfMissing: true);
                    }
                }
                catch (Exception ex)
                {
                    _log($"Error getting modules from PEB: {ex.Message}");
                }

                // Method 3: Active dump files carry an authoritative module list.
                // EnvDTE often reports no module base addresses when VS symbol loading is disabled.
                try
                {
                    if (TryGetActiveDumpFilePath(out string dumpPath))
                    {
                        bool is64Bit = Is64BitProcess(debugger);
                        foreach (var mod in GetModulesFromMinidump(dumpPath, is64Bit))
                        {
                            MergeModuleInfo(moduleSet, moduleInfoDict, mod, addIfMissing: true);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log($"Error getting modules from minidump: {ex.Message}");
                }

                // Method 4: Try common system DLLs that are usually loaded
                TryAddCommonSystemModules(debugger, moduleSet, moduleInfoDict);

                int order = 0;
                var sortedModules = moduleInfoDict.Values
                    .OrderBy(m => m.IsSystemModule)
                    .ThenBy(m => m.Name)
                    .ToList();

                foreach (var mod in sortedModules)
                {
                    mod.Order = order++;
                    result.Modules.Add(mod);
                }

                result.TotalCount = result.Modules.Count;

                if (result.Modules.Count == 0)
                {
                    result.Error = "No modules found. The process may not have loaded any modules yet or module enumeration is not supported for this debug target.";
                }
            }
            catch (Exception ex)
            {
                _log($"Error getting modules: {ex.Message}");
                result.Error = $"Error enumerating modules: {ex.Message}";
            }

            return result;
        }

        private ModuleInfo CreateModuleInfoFromPath(string modulePath, Debugger5 debugger)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var info = new ModuleInfo
            {
                Path = modulePath,
                Name = System.IO.Path.GetFileName(modulePath),
                SymbolsLoaded = false,
                IsSystemModule = IsSystemModule(modulePath)
            };

            // Try to get additional info via expression evaluation
            try
            {
                // Try to get module base address
                string safeModuleName = info.Name.Replace(".", "_").Replace("-", "_");
                
                // Try GetModuleHandle to get base address
                var handleExpr = debugger.GetExpression($"(void*)GetModuleHandleA(\"{EscapeForExpression(info.Name)}\")");
                if (handleExpr != null && handleExpr.IsValidValue && handleExpr.Value != "0x0000000000000000" && handleExpr.Value != "0x00000000")
                {
                    info.Address = handleExpr.Value;

                    // Try to get module size from PE header
                    try
                    {
                        var dosHeader = debugger.GetExpression($"*(short*){handleExpr.Value}");
                        if (dosHeader != null && dosHeader.IsValidValue)
                        {
                            // Check for MZ signature
                            string dosVal = dosHeader.Value.Trim();
                            // MZ = 0x5A4D
                            if (dosVal == "23117" || dosVal == "0x5a4d" || dosVal.ToLower() == "0x5a4d")
                            {
                                // Get e_lfanew (offset to PE header) at offset 0x3C
                                var lfanewExpr = debugger.GetExpression($"*(int*)({handleExpr.Value} + 0x3C)");
                                if (lfanewExpr != null && lfanewExpr.IsValidValue)
                                {
                                    int lfanew;
                                    if (int.TryParse(lfanewExpr.Value, out lfanew))
                                    {
                                        // SizeOfImage is at PE header + 0x50 (for 64-bit) or + 0x50 (same offset)
                                        var sizeExpr = debugger.GetExpression($"*(unsigned int*)({handleExpr.Value} + {lfanew} + 0x50)");
                                        if (sizeExpr != null && sizeExpr.IsValidValue)
                                        {
                                            uint size;
                                            if (uint.TryParse(sizeExpr.Value, out size))
                                            {
                                                info.Size = FormatSize(size);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _log($"[GetModuleDetails] Error getting module info: {ex.Message}");
                    }
                }

                // Check if symbols are loaded by trying to resolve a symbol
                try
                {
                    // A simple heuristic: if we can see function names in the stack, symbols are likely loaded
                    info.SymbolsLoaded = !string.IsNullOrEmpty(info.Address);
                }
                catch (Exception ex)
                {
                    _log($"[GetModuleDetails] Error checking symbols: {ex.Message}");
                }

                // Determine if 64-bit
                info.Is64Bit = Is64BitProcess(debugger);

                // Try to get version info
                info.Version = GetModuleVersion(modulePath);
            }
            catch (Exception ex)
            {
                _log($"Error getting module info for {modulePath}: {ex.Message}");
            }

            return info;
        }

        private List<ModuleInfo> GetModulesFromPEB(Debugger5 debugger)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var modules = new List<ModuleInfo>();

            try
            {
                // Detect if 64-bit process
                var ripExpr = debugger.GetExpression("@rip");
                bool is64Bit = ripExpr != null && ripExpr.IsValidValue;

                // For 64-bit process: PEB is at gs:[0x60]
                // For 32-bit process: PEB is at fs:[0x30]
                
                // Try to get PEB address
                EnvDTE.Expression pebExpr = null;
                
                if (is64Bit)
                {
                    pebExpr = debugger.GetExpression("(void*)__readgsqword(0x60)");
                }
                
                if (pebExpr == null || !pebExpr.IsValidValue || pebExpr.Value == "0x0000000000000000")
                {
                    // Try 32-bit
                    pebExpr = debugger.GetExpression("(void*)__readfsdword(0x30)");
                    is64Bit = false;
                }

                if (pebExpr == null || !pebExpr.IsValidValue)
                {
                    // Fallback: try @$peb pseudo-register
                    pebExpr = debugger.GetExpression("@$peb");
                }

                if (pebExpr != null && pebExpr.IsValidValue && 
                    pebExpr.Value != "0x0000000000000000" && pebExpr.Value != "0x00000000")
                {
                    // Get Ldr (PEB_LDR_DATA) - offset 0x18 for 64-bit, 0x0C for 32-bit
                    int ldrOffset = is64Bit ? 0x18 : 0x0C;
                    var ldrExpr = debugger.GetExpression($"*(void**)({pebExpr.Value} + {ldrOffset})");

                    if (ldrExpr != null && ldrExpr.IsValidValue &&
                        ldrExpr.Value != "0x0000000000000000" && ldrExpr.Value != "0x00000000")
                    {
                        // Walk InMemoryOrderModuleList
                        // InMemoryOrderModuleList is at offset 0x20 for 64-bit, 0x14 for 32-bit within PEB_LDR_DATA
                        int listOffset = is64Bit ? 0x20 : 0x14;
                        
                        // Get list head (Flink pointer)
                        var listHeadExpr = debugger.GetExpression($"*(void**)({ldrExpr.Value} + {listOffset})");
                        
                        if (listHeadExpr != null && listHeadExpr.IsValidValue &&
                            listHeadExpr.Value != "0x0000000000000000" && listHeadExpr.Value != "0x00000000")
                        {
                            ulong listHead = ParseAddress(listHeadExpr.Value);
                            ulong currentEntry = listHead;
                            int maxModules = 500; // Safety limit
                            int count = 0;
                            
                            while (count < maxModules)
                            {
                                try
                                {
                                    // LDR_DATA_TABLE_ENTRY offsets (relative to InMemoryOrderLinks):
                                    // 64-bit: DllBase at -0x10, FullDllName at +0x40, BaseDllName at +0x50
                                    // 32-bit: DllBase at -0x08, FullDllName at +0x28, BaseDllName at +0x30
                                    
                                    int dllBaseOffset = is64Bit ? -0x10 : -0x08;
                                    int fullDllNameOffset = is64Bit ? 0x40 : 0x28;
                                    
                                    // Get DllBase
                                    var dllBaseExpr = debugger.GetExpression($"*(void**)(0x{currentEntry:X} + {dllBaseOffset})");
                                    
                                    if (dllBaseExpr != null && dllBaseExpr.IsValidValue &&
                                        dllBaseExpr.Value != "0x0000000000000000" && dllBaseExpr.Value != "0x00000000")
                                    {
                                        string dllBase = dllBaseExpr.Value;
                                        
                                        // Try to get DLL name using GetModuleFileNameA-like approach
                                        // Since we have the base address, we can read the PE header to get the name
                                        string moduleName = GetModuleNameFromBase(debugger, dllBase, is64Bit);
                                        
                                        if (!string.IsNullOrEmpty(moduleName))
                                        {
                                            string systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
                                            string modulePath = moduleName;
                                            
                                            // Construct full path for system modules
                                            if (!modulePath.Contains("\\") && !modulePath.Contains("/"))
                                            {
                                                string system32Path = System.IO.Path.Combine(systemRoot, "System32", moduleName);
                                                if (System.IO.File.Exists(system32Path))
                                                {
                                                    modulePath = system32Path;
                                                }
                                            }
                                            
                                            modules.Add(new ModuleInfo
                                            {
                                                Name = System.IO.Path.GetFileName(moduleName),
                                                Path = modulePath,
                                                Address = dllBase,
                                                SymbolsLoaded = false,
                                                IsSystemModule = IsSystemModule(modulePath),
                                                Is64Bit = is64Bit
                                            });
                                        }
                                    }
                                    
                                    // Get next entry (Flink)
                                    var flinkExpr = debugger.GetExpression($"*(void**)(0x{currentEntry:X})");
                                    if (flinkExpr == null || !flinkExpr.IsValidValue)
                                        break;
                                    
                                    ulong nextEntry = ParseAddress(flinkExpr.Value);
                                    
                                    // Check if we've looped back to the head
                                    if (nextEntry == listHead || nextEntry == 0)
                                        break;
                                    
                                    currentEntry = nextEntry;
                                    count++;
                                }
                                catch (Exception ex)
                                {
                                    _log($"PEB module walk error at entry {count}: {ex.Message}");
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log($"PEB enumeration error: {ex.Message}");
            }

            return modules;
        }
        
        /// <summary>
        /// Try to get module name from its base address by reading PE export directory.
        /// </summary>
        private string GetModuleNameFromBase(Debugger5 debugger, string baseAddress, bool is64Bit)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            
            try
            {
                // Read DOS header - check MZ signature
                var mzExpr = debugger.GetExpression($"*(unsigned short*)({baseAddress})");
                if (mzExpr == null || !mzExpr.IsValidValue)
                    return null;
                
                string mzVal = mzExpr.Value.Trim();
                // MZ = 0x5A4D = 23117
                if (mzVal != "23117" && mzVal.ToLower() != "0x5a4d")
                    return null;
                
                // Read e_lfanew (offset to PE header) at offset 0x3C
                var lfanewExpr = debugger.GetExpression($"*(unsigned int*)({baseAddress} + 0x3C)");
                if (lfanewExpr == null || !lfanewExpr.IsValidValue)
                    return null;
                
                uint? lfanewValue = ParseUInt(lfanewExpr.Value);
                if (!lfanewValue.HasValue)
                    return null;
                uint lfanew = lfanewValue.Value;
                if (lfanew == 0 || lfanew > 0x1000)
                    return null;
                
                // Export directory RVA is at PE + 0x88 (64-bit) or PE + 0x78 (32-bit)
                int exportDirRvaOffset = is64Bit ? 0x88 : 0x78;
                var exportRvaExpr = debugger.GetExpression($"*(unsigned int*)({baseAddress} + {lfanew} + {exportDirRvaOffset})");
                if (exportRvaExpr == null || !exportRvaExpr.IsValidValue)
                    return null;
                
                uint? exportRvaValue = ParseUInt(exportRvaExpr.Value);
                if (!exportRvaValue.HasValue)
                    return null;
                uint exportRva = exportRvaValue.Value;
                if (exportRva == 0)
                    return null;
                
                // Export directory has Name RVA at offset 0x0C
                var nameRvaExpr = debugger.GetExpression($"*(unsigned int*)({baseAddress} + {exportRva} + 0x0C)");
                if (nameRvaExpr == null || !nameRvaExpr.IsValidValue)
                    return null;
                
                uint? nameRvaValue = ParseUInt(nameRvaExpr.Value);
                if (!nameRvaValue.HasValue)
                    return null;
                uint nameRva = nameRvaValue.Value;
                if (nameRva == 0)
                    return null;
                
                // Read the name string using bulk memory read instead of char-by-char IPC
                const int maxNameLen = 256;
                ulong nameAddress;
                try
                {
                    var nameAddrExpr = debugger.GetExpression($"(unsigned __int64)({baseAddress} + {nameRva})");
                    if (nameAddrExpr == null || !nameAddrExpr.IsValidValue)
                        return null;
                    nameAddress = ParseAddress(nameAddrExpr.Value);
                    if (nameAddress == 0)
                        return null;
                }
                catch
                {
                    return null;
                }
                
                byte[] nameBytes = ReadMemoryBlockOptimized(debugger, nameAddress, maxNameLen);
                if (nameBytes == null || nameBytes.Length == 0)
                    return null;
                
                // Find null terminator
                int nameLen = Array.IndexOf(nameBytes, (byte)0);
                if (nameLen < 0) nameLen = nameBytes.Length;
                if (nameLen == 0) return null;
                
                string name = System.Text.Encoding.ASCII.GetString(nameBytes, 0, nameLen);
                return string.IsNullOrEmpty(name) ? null : name;
            }
            catch (Exception ex)
            {
                _log($"GetModuleNameFromBase error: {ex.Message}");
                return null;
            }
        }

        private void TryAddCommonSystemModules(Debugger5 debugger, HashSet<string> moduleSet, Dictionary<string, ModuleInfo> moduleInfoDict)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Common system DLLs that are almost always loaded
            string[] commonDlls = new[]
            {
                "ntdll.dll",
                "kernel32.dll",
                "kernelbase.dll",
                "user32.dll",
                "gdi32.dll",
                "advapi32.dll",
                "msvcrt.dll",
                "ucrtbase.dll",
                "vcruntime140.dll",
                "msvcp140.dll"
            };

            foreach (var dllName in commonDlls)
            {
                try
                {
                    var handleExpr = debugger.GetExpression($"(void*)GetModuleHandleA(\"{dllName}\")");
                    if (handleExpr != null && handleExpr.IsValidValue &&
                        handleExpr.Value != "0x0000000000000000" && handleExpr.Value != "0x00000000" &&
                        handleExpr.Value != "0" && !handleExpr.Value.Contains("error"))
                    {
                        // Module is loaded, try to get its full path
                        string fullPath = GetModuleFullPath(debugger, handleExpr.Value, dllName);
                        
                        if (!moduleSet.Contains(fullPath) && !moduleSet.Contains(dllName))
                        {
                            moduleSet.Add(fullPath);
                            var info = new ModuleInfo
                            {
                                Name = dllName,
                                Path = fullPath,
                                Address = handleExpr.Value,
                                SymbolsLoaded = false,
                                IsSystemModule = true,
                                Is64Bit = Is64BitProcess(debugger)
                            };
                            moduleInfoDict[fullPath] = info;
                        }
                    }
                }
                catch
                {
                    // Module not loaded or error - skip
                }
            }
        }

        private string GetModuleFullPath(Debugger5 debugger, string baseAddress, string moduleName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Try to get full path using GetModuleFileNameA
            // This requires a buffer which is complex in expression evaluation
            // For now, return a constructed path for system modules
            
            string systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            
            if (moduleName.ToLower() == "ntdll.dll")
            {
                return System.IO.Path.Combine(systemRoot, "System32", moduleName);
            }
            
            // Check if it's in System32
            string system32Path = System.IO.Path.Combine(systemRoot, "System32", moduleName);
            if (System.IO.File.Exists(system32Path))
            {
                return system32Path;
            }

            // Check SysWOW64 for 32-bit modules on 64-bit OS
            string sysWow64Path = System.IO.Path.Combine(systemRoot, "SysWOW64", moduleName);
            if (System.IO.File.Exists(sysWow64Path))
            {
                return sysWow64Path;
            }

            return moduleName;
        }

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

        private bool Is64BitProcess(Debugger5 debugger)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                // Check if @rip register exists (64-bit) vs @eip (32-bit)
                var ripExpr = debugger.GetExpression("@rip");
                return ripExpr != null && ripExpr.IsValidValue;
            }
            catch
            {
                return Environment.Is64BitOperatingSystem;
            }
        }

        private string GetModuleVersion(string modulePath)
        {
            try
            {
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
                _log($"[GetModuleVersion] Error: {ex.Message}");
            }
            return null;
        }

        private string FormatSize(uint bytes)
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

        private string EscapeForExpression(string str)
        {
            return str.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        #endregion

        #region Exception

        public ExceptionResult GetException()
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return BuildExceptionResult(Debugger);
            }, HeavyMainThreadTimeoutMs);
        }

        private ExceptionResult BuildExceptionResult(Debugger5 debugger)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new ExceptionResult
            {
                HasException = false,
                Capability = "managed_exception_best_effort"
            };

            if (debugger == null || debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
            {
                return result;
            }

            try
            {
                var exceptionExpr = debugger.GetExpression("$exception");
                if (exceptionExpr != null && exceptionExpr.IsValidValue)
                {
                    result.HasException = true;
                    result.Type = exceptionExpr.Type;
                    result.Message = GetExpressionMemberValue(exceptionExpr, "Message");
                    result.StackTrace = GetExpressionMemberValue(exceptionExpr, "StackTrace");
                    result.Source = GetExpressionMemberValue(exceptionExpr, "Source");
                    result.HResult = GetExpressionMemberValue(exceptionExpr, "HResult");

                    const int maxInnerDepth = 10;
                    var currentExpr = exceptionExpr;
                    var currentResult = result;
                    for (int depth = 0; depth < maxInnerDepth; depth++)
                    {
                        var innerExpr = GetExpressionMember(currentExpr, "InnerException");
                        if (innerExpr == null || !innerExpr.IsValidValue || innerExpr.Value == "null")
                            break;
                        var innerResult = new ExceptionResult
                        {
                            HasException = true,
                            Type = innerExpr.Type,
                            Message = GetExpressionMemberValue(innerExpr, "Message"),
                            StackTrace = GetExpressionMemberValue(innerExpr, "StackTrace"),
                            Source = GetExpressionMemberValue(innerExpr, "Source"),
                            HResult = GetExpressionMemberValue(innerExpr, "HResult"),
                            Capability = "managed_exception_best_effort"
                        };
                        currentResult.InnerException = innerResult;
                        currentResult = innerResult;
                        currentExpr = innerExpr;
                    }
                }
            }
            catch (Exception ex)
            {
                _log($"Error getting exception: {ex.Message}");
            }

            return result;
        }

        private string GetExpressionMemberValue(EnvDTE.Expression expr, string memberName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                foreach (EnvDTE.Expression member in expr.DataMembers)
                {
                    if (member.Name == memberName)
                    {
                        return member.Value?.Trim('"') ?? "";
                    }
                }
            }
            catch (Exception ex)
            {
                _log($"[GetExpressionMemberValue] Error accessing member '{memberName}': {ex.Message}");
            }
            return "";
        }

        private EnvDTE.Expression GetExpressionMember(EnvDTE.Expression expr, string memberName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                foreach (EnvDTE.Expression member in expr.DataMembers)
                {
                    if (member.Name == memberName)
                    {
                        return member;
                    }
                }
            }
            catch (Exception ex)
            {
                _log($"[GetExpressionMember] Error accessing member '{memberName}': {ex.Message}");
            }
            return null;
        }

        #endregion

        #region Expression Evaluation

        public EvaluateResult EvaluateExpression(string expression, bool expandChildren = false, int maxDepth = 2, bool allowSideEffects = false)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return EvaluateExpressionCore(Debugger, expression, expandChildren, maxDepth, allowSideEffects);
            });
        }

        public BatchEvaluateResult EvaluateExpressionsBatch(List<string> expressions, bool expandChildren = false, int maxDepth = 2, bool allowSideEffects = false)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var normalizedExpressions = NormalizeExpressions(expressions);
                var snapshotId = Guid.NewGuid().ToString("N");
                var capturedAt = DateTime.UtcNow.ToString("o");
                var bestEffort = new BatchEvaluateResult
                {
                    SnapshotId = snapshotId,
                    CapturedAt = capturedAt,
                    RequestedCount = normalizedExpressions.Count,
                    SnapshotStatus = "unavailable",
                    Partial = true
                };

                if (normalizedExpressions.Count == 0)
                {
                    bestEffort.Error = "No expressions provided";
                    return bestEffort;
                }

                if (debugger == null || debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    bestEffort.Error = "Not in break mode";
                    return bestEffort;
                }

                for (int attempt = 1; attempt <= SnapshotMaxAttempts; attempt++)
                {
                    if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                    {
                        bestEffort.SnapshotAttempts = attempt;
                        bestEffort.SnapshotStatus = "unavailable";
                        bestEffort.Error = "Not in break mode";
                        return bestEffort;
                    }

                    try
                    {
                        string tokenBefore = CreateDebuggerSnapshotToken(debugger);
                        var snapshot = BuildBatchEvaluateResult(
                            debugger,
                            normalizedExpressions,
                            expandChildren,
                            maxDepth,
                            allowSideEffects,
                            snapshotId,
                            capturedAt);
                        string tokenAfter = CreateDebuggerSnapshotToken(debugger);
                        bool stable = !string.IsNullOrEmpty(tokenBefore) && tokenBefore == tokenAfter;

                        snapshot.SnapshotStable = stable;
                        snapshot.SnapshotAttempts = attempt;
                        snapshot.SnapshotStatus = stable
                            ? (snapshot.Partial ? "partial" : "ok")
                            : "unstable";
                        bestEffort = snapshot;
                        if (stable)
                        {
                            return snapshot;
                        }
                    }
                    catch (Exception ex)
                    {
                        _log($"EvaluateExpressionsBatch snapshot attempt {attempt} failed: {ex.Message}");
                        bestEffort.SnapshotAttempts = attempt;
                        if (bestEffort.Results.Count > 0)
                        {
                            bestEffort.SnapshotStatus = "unstable";
                        }
                    }

                    if (attempt < SnapshotMaxAttempts)
                    {
                        System.Threading.Thread.Sleep(SnapshotRetryDelayMs);
                    }
                }

                return bestEffort;
            }, HeavyMainThreadTimeoutMs);
        }

        private BatchEvaluateResult BuildBatchEvaluateResult(
            Debugger5 debugger,
            IReadOnlyList<string> expressions,
            bool expandChildren,
            int maxDepth,
            bool allowSideEffects,
            string snapshotId,
            string capturedAt)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new BatchEvaluateResult
            {
                SnapshotId = snapshotId,
                CapturedAt = capturedAt,
                RequestedCount = expressions.Count
            };
            var stopwatch = Stopwatch.StartNew();
            bool budgetExceeded = false;
            string budgetExceededMessage = $"Skipped because expression batch budget exceeded {ExpressionBatchBudgetMs}ms";

            foreach (var expression in expressions)
            {
                if (stopwatch.ElapsedMilliseconds >= ExpressionBatchBudgetMs)
                {
                    budgetExceeded = true;
                    result.Results.Add(new EvaluateResult
                    {
                        Expression = expression,
                        IsValid = false,
                        ErrorMessage = budgetExceededMessage
                    });
                    continue;
                }

                var evalResult = EvaluateExpressionCore(debugger, expression, expandChildren, maxDepth, allowSideEffects);
                result.Results.Add(evalResult);
            }

            result.SuccessCount = result.Results.Count(r => r.IsValid);
            result.FailedCount = result.Results.Count - result.SuccessCount;
            result.Partial = result.FailedCount > 0 || result.Results.Any(r => r.ErrorMessage != null && r.ErrorMessage.Contains("Expansion truncated"));
            if (budgetExceeded)
            {
                result.Error = $"Expression batch budget exceeded {ExpressionBatchBudgetMs}ms";
            }
            return result;
        }

        private void MergeModuleInfo(
            HashSet<string> moduleSet,
            Dictionary<string, ModuleInfo> moduleInfoDict,
            ModuleInfo moduleInfo,
            bool addIfMissing)
        {
            if (moduleInfo == null || string.IsNullOrEmpty(moduleInfo.Path))
            {
                return;
            }

            if (!moduleSet.Contains(moduleInfo.Path))
            {
                if (!addIfMissing)
                {
                    return;
                }

                moduleSet.Add(moduleInfo.Path);
                moduleInfoDict[moduleInfo.Path] = moduleInfo;
                return;
            }

            var existing = moduleInfoDict[moduleInfo.Path];
            if (string.IsNullOrEmpty(existing.Address) && !string.IsNullOrEmpty(moduleInfo.Address))
            {
                existing.Address = moduleInfo.Address;
            }
            if (string.IsNullOrEmpty(existing.Size) && !string.IsNullOrEmpty(moduleInfo.Size))
            {
                existing.Size = moduleInfo.Size;
            }
            if (string.IsNullOrEmpty(existing.SymbolPath) && !string.IsNullOrEmpty(moduleInfo.SymbolPath))
            {
                existing.SymbolPath = moduleInfo.SymbolPath;
            }
            if (string.IsNullOrEmpty(existing.Timestamp) && !string.IsNullOrEmpty(moduleInfo.Timestamp))
            {
                existing.Timestamp = moduleInfo.Timestamp;
            }
            if (string.IsNullOrEmpty(existing.Version) && !string.IsNullOrEmpty(moduleInfo.Version))
            {
                existing.Version = moduleInfo.Version;
            }
            existing.Is64Bit = existing.Is64Bit || moduleInfo.Is64Bit;
        }

        private bool TryGetActiveDumpFilePath(out string dumpPath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            dumpPath = null;
            try
            {
                var activePath = _dte?.ActiveDocument?.FullName;
                if (IsDumpFilePath(activePath) && System.IO.File.Exists(activePath))
                {
                    dumpPath = System.IO.Path.GetFullPath(activePath);
                    _activeDumpFilePath = dumpPath;
                    return true;
                }

                if (IsDumpFilePath(_activeDumpFilePath) && System.IO.File.Exists(_activeDumpFilePath))
                {
                    dumpPath = _activeDumpFilePath;
                    return true;
                }
            }
            catch (Exception ex)
            {
                _log($"[TryGetActiveDumpFilePath] Error reading active document: {ex.Message}");
            }

            return false;
        }

        private List<ModuleInfo> GetModulesFromMinidump(string dumpPath, bool is64Bit)
        {
            var modules = new List<ModuleInfo>();
            if (string.IsNullOrWhiteSpace(dumpPath) || !System.IO.File.Exists(dumpPath))
            {
                return modules;
            }

            using (var stream = new System.IO.FileStream(dumpPath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
            using (var reader = new System.IO.BinaryReader(stream, Encoding.UTF8))
            {
                if (stream.Length < 32)
                {
                    return modules;
                }

                uint signature = reader.ReadUInt32();
                if (signature != 0x504D444D) // "MDMP"
                {
                    return modules;
                }

                reader.ReadUInt32(); // version
                uint streamCount = reader.ReadUInt32();
                uint directoryRva = reader.ReadUInt32();
                reader.ReadUInt32(); // checksum
                reader.ReadUInt32(); // timestamp
                reader.ReadUInt64(); // flags

                if (streamCount > 4096 || directoryRva == 0 || directoryRva >= stream.Length)
                {
                    return modules;
                }

                uint moduleListRva = 0;
                uint moduleListSize = 0;
                stream.Position = directoryRva;
                for (uint i = 0; i < streamCount; i++)
                {
                    if (stream.Position + 12 > stream.Length)
                    {
                        return modules;
                    }

                    uint streamType = reader.ReadUInt32();
                    uint dataSize = reader.ReadUInt32();
                    uint rva = reader.ReadUInt32();
                    if (streamType == 4) // ModuleListStream
                    {
                        moduleListSize = dataSize;
                        moduleListRva = rva;
                    }
                }

                if (moduleListRva == 0 || moduleListRva >= stream.Length || moduleListSize < 4)
                {
                    return modules;
                }

                stream.Position = moduleListRva;
                uint moduleCount = reader.ReadUInt32();
                uint maxModulesBySize = (moduleListSize - 4) / 108;
                moduleCount = Math.Min(moduleCount, maxModulesBySize);
                moduleCount = Math.Min(moduleCount, 100000);

                for (uint i = 0; i < moduleCount; i++)
                {
                    if (stream.Position + 108 > stream.Length)
                    {
                        break;
                    }

                    ulong baseOfImage = reader.ReadUInt64();
                    uint sizeOfImage = reader.ReadUInt32();
                    reader.ReadUInt32(); // checksum
                    uint timeDateStamp = reader.ReadUInt32();
                    uint moduleNameRva = reader.ReadUInt32();
                    stream.Position += 52; // VS_FIXEDFILEINFO
                    uint cvRecordSize = reader.ReadUInt32();
                    uint cvRecordRva = reader.ReadUInt32();
                    stream.Position += 8; // MiscRecord
                    stream.Position += 16; // Reserved0/Reserved1

                    string modulePath = ReadMinidumpString(reader, moduleNameRva);
                    if (string.IsNullOrWhiteSpace(modulePath))
                    {
                        continue;
                    }

                    modules.Add(new ModuleInfo
                    {
                        Path = modulePath,
                        Name = SafeGetFileName(modulePath),
                        Address = $"0x{baseOfImage:X16}",
                        Size = FormatSize(sizeOfImage),
                        SymbolPath = ReadMinidumpCodeViewPdbPath(reader, cvRecordRva, cvRecordSize),
                        SymbolsLoaded = false,
                        IsSystemModule = IsSystemModule(modulePath),
                        Is64Bit = is64Bit,
                        Timestamp = FormatPeTimestamp(timeDateStamp),
                        Version = GetModuleVersion(modulePath)
                    });
                }
            }

            return modules;
        }

        private string ReadMinidumpString(System.IO.BinaryReader reader, uint rva)
        {
            if (rva == 0 || rva >= reader.BaseStream.Length)
            {
                return null;
            }

            long previousPosition = reader.BaseStream.Position;
            try
            {
                reader.BaseStream.Position = rva;
                uint byteLength = reader.ReadUInt32();
                if (byteLength == 0 || byteLength > 64 * 1024 || reader.BaseStream.Position + byteLength > reader.BaseStream.Length)
                {
                    return null;
                }

                return Encoding.Unicode.GetString(reader.ReadBytes((int)byteLength));
            }
            catch
            {
                return null;
            }
            finally
            {
                reader.BaseStream.Position = previousPosition;
            }
        }

        private string ReadMinidumpCodeViewPdbPath(System.IO.BinaryReader reader, uint rva, uint size)
        {
            if (rva == 0 || size < 24 || size > 4096 || rva >= reader.BaseStream.Length || (ulong)rva + size > (ulong)reader.BaseStream.Length)
            {
                return null;
            }

            long previousPosition = reader.BaseStream.Position;
            try
            {
                reader.BaseStream.Position = rva;
                var bytes = reader.ReadBytes((int)size);
                if (bytes.Length < 24 || bytes[0] != (byte)'R' || bytes[1] != (byte)'S' || bytes[2] != (byte)'D' || bytes[3] != (byte)'S')
                {
                    return null;
                }

                int end = Array.IndexOf(bytes, (byte)0, 24);
                if (end < 0)
                {
                    end = bytes.Length;
                }

                return Encoding.UTF8.GetString(bytes, 24, end - 24);
            }
            catch
            {
                return null;
            }
            finally
            {
                reader.BaseStream.Position = previousPosition;
            }
        }

        private string SafeGetFileName(string path)
        {
            try
            {
                return System.IO.Path.GetFileName(path);
            }
            catch
            {
                return path;
            }
        }

        private EvaluateResult EvaluateExpressionCore(Debugger5 debugger, string expression, bool expandChildren, int maxDepth, bool allowSideEffects = false)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new EvaluateResult { Expression = expression };

            if (debugger == null || debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
            {
                result.IsValid = false;
                result.ErrorMessage = "Not in break mode";
                return result;
            }

            try
            {
                var expr = debugger.GetExpression(expression, false, ExpressionEvaluationTimeoutMs);
                result.IsValid = expr.IsValidValue;
                result.Value = expr.Value;
                result.Type = expr.Type;

                if (!result.IsValid)
                {
                    result.ErrorMessage = expr.Value;
                }

                if (result.IsValid && IsNumericType(expr.Type))
                {
                    try
                    {
                        var hexExpr = debugger.GetExpression($"(void*){expression}", false, ExpressionEvaluationTimeoutMs);
                        if (hexExpr.IsValidValue)
                        {
                            result.HexValue = hexExpr.Value;
                        }
                    }
                    catch (Exception ex)
                    {
                        _log($"[EvaluateExpression] Error getting hex value for '{expression}': {ex.Message}");
                    }
                }

                if (expandChildren)
                {
                    int normalizedMaxDepth = Math.Max(0, Math.Min(maxDepth, ExpressionExpansionMaxDepth));
                    var budget = new VariableExpansionBudget(ExpressionExpansionMaxNodes, ExpressionExpansionBudgetMs);
                    result.HasChildren = expr.DataMembers?.Count > 0;
                    if (result.HasChildren)
                    {
                        result.Children = new List<VariableInfo>();
                        foreach (EnvDTE.Expression child in expr.DataMembers)
                        {
                            if (!budget.CanContinue())
                            {
                                break;
                            }

                            var childInfo = CreateVariableInfo(child, 0, normalizedMaxDepth, budget);
                            if (childInfo != null)
                            {
                                result.Children.Add(childInfo);
                            }
                        }

                        if (budget.IsExceeded && string.IsNullOrEmpty(result.ErrorMessage))
                        {
                            result.ErrorMessage = budget.Reason;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                result.IsValid = false;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        private bool IsNumericType(string type)
        {
            if (string.IsNullOrEmpty(type))
            {
                return false;
            }

            // Strip pointer/reference/array suffixes and whitespace for matching
            string cleanType = type.TrimEnd(' ', '*', '&', '[', ']');

            var numericTypes = new HashSet<string>(StringComparer.Ordinal)
            {
                "int", "uint", "long", "ulong", "short", "ushort", "byte", "sbyte",
                "IntPtr", "UIntPtr", "nint", "nuint",
                "System.Int32", "System.UInt32", "System.Int64", "System.UInt64",
                "System.Int16", "System.UInt16", "System.Byte", "System.SByte",
                "System.IntPtr", "System.UIntPtr"
            };
            return numericTypes.Contains(cleanType);
        }

        private List<string> NormalizeExpressions(IEnumerable<string> expressions)
        {
            var result = new List<string>();
            if (expressions == null)
            {
                return result;
            }

            foreach (var expression in expressions)
            {
                if (string.IsNullOrWhiteSpace(expression))
                {
                    continue;
                }

                result.Add(expression.Trim());
                if (result.Count >= 100)
                {
                    break;
                }
            }

            return result;
        }

        #endregion

        #region Memory

        public MemoryResult ReadMemory(string address, int size = 256)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                int requestedSize = Math.Max(1, Math.Min(size, 4096));
                var bestEffort = CreateUnavailableMemoryResult(address, requestedSize);

                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    return bestEffort;
                }

                for (int attempt = 1; attempt <= SnapshotMaxAttempts; attempt++)
                {
                    if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                    {
                        bestEffort.Status = "unavailable";
                        return bestEffort;
                    }

                    try
                    {
                        string tokenBefore = CreateDebuggerSnapshotToken(debugger);
                        var snapshot = ReadMemoryInternal(debugger, address, requestedSize);
                        string tokenAfter = CreateDebuggerSnapshotToken(debugger);
                        bool stable = !string.IsNullOrEmpty(tokenBefore) && tokenBefore == tokenAfter;

                        if (stable)
                        {
                            return snapshot;
                        }

                        bestEffort = snapshot;
                        if (bestEffort.BytesRead > 0)
                        {
                            bestEffort.Status = "unstable";
                        }
                    }
                    catch (Exception ex)
                    {
                        _log($"ReadMemory snapshot attempt {attempt} failed: {ex.Message}");
                    }

                    if (attempt < SnapshotMaxAttempts)
                    {
                        System.Threading.Thread.Sleep(SnapshotRetryDelayMs);
                    }
                }

                return bestEffort;
            });
        }

        private MemoryResult CreateUnavailableMemoryResult(string address, int requestedSize)
        {
            return new MemoryResult
            {
                Address = address,
                Status = "unavailable",
                RequestedSize = requestedSize,
                Size = 0,
                BytesRead = 0,
                FailedReads = requestedSize
            };
        }

        private MemoryResult ReadMemoryInternal(Debugger5 debugger, string address, int requestedSize)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = CreateUnavailableMemoryResult(address, requestedSize);

            try
            {
                var addrExpr = debugger.GetExpression(address);
                if (addrExpr == null || !addrExpr.IsValidValue)
                {
                    return result;
                }

                ulong baseAddress = ParseAddress(addrExpr.Value);
                bool is64Bit = Is64BitProcess(debugger);
                result.Address = is64Bit ? $"0x{baseAddress:X16}" : $"0x{baseAddress:X8}";

                byte[] memory = ReadProcessMemoryContiguous(debugger, baseAddress, requestedSize);
                
                // Validate that ReadProcessMemoryContiguous returned correct data
                // This is important for minidump debugging where the process ID might
                // correspond to a different or non-existent process
                if (memory != null && memory.Length >= 4)
                {
                    if (!ValidateMemoryWithExpression(debugger, baseAddress, memory))
                    {
                        _log($"ReadMemoryInternal: Process memory validation failed, falling back to expression-based reading");
                        memory = null; // Force fallback to ReadMemoryBlockOptimized
                    }
                }
                
                if ((memory == null || memory.Length == 0) && requestedSize > 0)
                {
                    memory = ReadMemoryBlockOptimized(debugger, baseAddress, requestedSize);
                }
                if (memory == null || memory.Length == 0)
                {
                    return result;
                }

                result.Bytes = memory.ToList();
                result.Size = memory.Length;
                result.BytesRead = memory.Length;
                result.FailedReads = Math.Max(0, requestedSize - memory.Length);
                result.Partial = result.FailedReads > 0;
                result.Status = result.Partial ? "partial" : "ok";

                var hexBuilder = new StringBuilder();
                var asciiBuilder = new StringBuilder();
                var formattedBuilder = new StringBuilder();
                const int bytesPerLine = 16;

                for (int offset = 0; offset < memory.Length; offset += bytesPerLine)
                {
                    int count = Math.Min(bytesPerLine, memory.Length - offset);
                    var lineHex = new StringBuilder();
                    var lineAscii = new StringBuilder();

                    for (int i = 0; i < count; i++)
                    {
                        byte b = memory[offset + i];
                        lineHex.AppendFormat("{0:X2} ", b);
                        lineAscii.Append(b >= 32 && b < 127 ? (char)b : '.');
                    }

                    string lineHexText = lineHex.ToString().TrimEnd();
                    hexBuilder.Append(lineHexText);
                    if (offset + count < memory.Length)
                    {
                        hexBuilder.Append(' ');
                    }
                    asciiBuilder.Append(lineAscii.ToString());
                    formattedBuilder.AppendFormat(
                        "{0}+{1:X4}: {2} | {3}\n",
                        result.Address,
                        offset,
                        lineHexText,
                        lineAscii.ToString());
                }

                result.HexDump = hexBuilder.ToString();
                result.AsciiView = asciiBuilder.ToString();
                result.FormattedView = formattedBuilder.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                _log($"Error reading memory: {ex.Message}");
            }

            return result;
        }

        #endregion

        #region Disassembly

        /// <summary>
        /// Get disassembly around the current instruction pointer using the Iced library.
        /// Returns properly decoded x86/x64 assembly instructions.
        /// </summary>
        public DisassemblyResult GetDisassembly(int linesBefore = 20, int linesAfter = 20)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new DisassemblyResult();

                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    result.Error = "Not in break mode";
                    return result;
                }

                try
                {
                    var frame = debugger.CurrentStackFrame;
                    if (frame != null)
                    {
                        result.FunctionName = frame.FunctionName;
                        result.SourceFile = GetFrameFileName(frame);

                        // Determine if 64-bit by checking for RIP register
                        var ripExpr = debugger.GetExpression("@rip");
                        bool is64Bit = ripExpr.IsValidValue;
                        var ipExpr = is64Bit ? ripExpr : debugger.GetExpression("@eip");
                        result.Is64Bit = is64Bit;

                        if (ipExpr.IsValidValue)
                        {
                            ulong currentIP = ParseAddress(ipExpr.Value);
                            result.CurrentAddress = is64Bit
                                ? $"0x{currentIP:X16}"
                                : $"0x{currentIP:X8}";

                            // Calculate bytes to read
                            // x86/x64 instructions average ~4 bytes, max 15 bytes
                            // Read extra to ensure we have enough data for proper disassembly
                            int bytesBeforeIP = linesBefore * 8;
                            int bytesAfterIP = linesAfter * 8;
                            int totalBytes = bytesBeforeIP + bytesAfterIP;

                            ulong startAddress = currentIP > (ulong)bytesBeforeIP
                                ? currentIP - (ulong)bytesBeforeIP
                                : 0;

                            // Read memory block using optimized batch reading
                            byte[] codeBytes = ReadMemoryBlockOptimized(debugger, startAddress, totalBytes);

                            // Fallback: if reading from calculated start failed, try reading from current IP
                            ulong actualStartAddress = startAddress;
                            if ((codeBytes == null || codeBytes.Length == 0) && startAddress != currentIP)
                            {
                                _log($"GetDisassembly: Failed to read from {startAddress:X}, trying from current IP {currentIP:X}");
                                actualStartAddress = currentIP;
                                codeBytes = ReadMemoryBlockOptimized(debugger, currentIP, bytesAfterIP);
                            }

                            if (codeBytes != null && codeBytes.Length > 0)
                            {
                                // Disassemble using Iced
                                var instructions = _disasmService.Disassemble(
                                    codeBytes, actualStartAddress, is64Bit, linesBefore + linesAfter + 50);

                                // Find current IP index
                                int currentIndex = -1;
                                for (int i = 0; i < instructions.Count; i++)
                                {
                                    if (instructions[i].Address == currentIP)
                                    {
                                        currentIndex = i;
                                        break;
                                    }
                                    if (instructions[i].Address > currentIP && i > 0)
                                    {
                                        currentIndex = i - 1;
                                        break;
                                    }
                                }

                                if (currentIndex == -1 && instructions.Count > 0)
                                {
                                    // Current IP not found exactly, use first instruction if we started from currentIP
                                    currentIndex = (actualStartAddress == currentIP) ? 0 : instructions.Count / 2;
                                }

                                // Extract instructions around current IP
                                int startIndex = Math.Max(0, currentIndex - linesBefore);
                                int endIndex = Math.Min(instructions.Count, currentIndex + linesAfter + 1);

                                // If we fell back to reading from currentIP, adjust startIndex
                                if (actualStartAddress == currentIP)
                                {
                                    startIndex = 0;
                                }

                                int sourceLine = GetFrameLineNumber(frame);

                                for (int i = startIndex; i < endIndex; i++)
                                {
                                    var inst = instructions[i];
                                    bool isCurrent = inst.Address == currentIP;

                                    result.Instructions.Add(new DisassemblyInstruction
                                    {
                                        Address = inst.AddressHex,
                                        Opcode = inst.Bytes,
                                        Instruction = inst.FullInstruction,
                                        IsCurrent = isCurrent,
                                        SourceLine = isCurrent ? (int?)sourceLine : null,
                                        Mnemonic = inst.Mnemonic,
                                        Operands = inst.Operands,
                                        IsCall = inst.IsCall,
                                        IsJump = inst.IsJump,
                                        IsReturn = inst.IsReturn,
                                        BranchTarget = inst.BranchTarget?.ToString("X"),
                                        Length = inst.Length
                                    });
                                }

                                result.TotalInstructions = result.Instructions.Count;
                            }
                            else
                            {
                                result.Error = "Could not read memory at current instruction pointer. This may occur when debugging optimized code without symbols.";
                            }
                        }
                        else
                        {
                            result.Error = "Could not determine instruction pointer";
                        }
                    }
                    else
                    {
                        result.Error = "No current stack frame";
                    }
                }
                catch (Exception ex)
                {
                    _log($"Error getting disassembly: {ex.Message}");
                    result.Error = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Get disassembly starting at a specific address using the Iced library.
        /// Automatically handles thread context issues by trying to switch to user code thread if needed.
        /// </summary>
        public DisassemblyResult GetDisassemblyAt(string address, int count = 50)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new DisassemblyResult();

                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    result.Error = "Not in break mode";
                    return result;
                }

                // Save current thread in case we need to restore
                int originalThreadId = debugger.CurrentThread?.ID ?? 0;
                bool switchedThread = false;

                try
                {
                    // Try to execute first
                    result = ExecuteDisassemblyAt(debugger, address, count, result);
                    
                    // If first attempt failed (has error or no instructions), try switching to user code thread
                    if (!string.IsNullOrEmpty(result.Error) || result.Instructions.Count == 0)
                    {
                        string firstError = result.Error ?? "No instructions retrieved";
                        _log($"GetDisassemblyAt first attempt failed: {firstError}");

                        var userThread = FindUserCodeThread(debugger);
                        if (userThread != null && userThread.ID != originalThreadId)
                        {
                            _log($"Switching from thread {originalThreadId} to user code thread {userThread.ID}");
                            debugger.CurrentThread = userThread;
                            switchedThread = true;

                            // Reset result and retry
                            result = new DisassemblyResult();
                            result = ExecuteDisassemblyAt(debugger, address, count, result);
                            
                            // If retry also failed, restore the original error
                            if (!string.IsNullOrEmpty(result.Error) || result.Instructions.Count == 0)
                            {
                                _log($"GetDisassemblyAt retry also failed: {result.Error}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log($"GetDisassemblyAt exception: {ex.Message}");
                    result.Error = ex.Message;
                }
                finally
                {
                    // Restore original thread if we switched
                    if (switchedThread && originalThreadId > 0)
                    {
                        try
                        {
                            foreach (EnvDTE.Thread thread in debugger.CurrentProgram.Threads)
                            {
                                if (thread.ID == originalThreadId)
                                {
                                    debugger.CurrentThread = thread;
                                    break;
                                }
                            }
                        }
                        catch (Exception restoreEx)
                        {
                            _log($"Failed to restore original thread: {restoreEx.Message}");
                        }
                    }
                }

                return result;
            });
        }

        /// <summary>
        /// Internal method to execute disassembly at a specific address.
        /// </summary>
        private DisassemblyResult ExecuteDisassemblyAt(Debugger5 debugger, string address, int count, DisassemblyResult result)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Evaluate address expression
            var addrExpr = debugger.GetExpression(address);
            if (!addrExpr.IsValidValue)
            {
                result.Error = $"Invalid address expression: {address}";
                return result;
            }

            ulong baseAddr = ParseAddress(addrExpr.Value);

            // Determine if 64-bit
            var ripExpr = debugger.GetExpression("@rip");
            bool is64Bit = ripExpr != null && ripExpr.IsValidValue;
            result.Is64Bit = is64Bit;

            result.CurrentAddress = is64Bit
                ? $"0x{baseAddr:X16}"
                : $"0x{baseAddr:X8}";

            // Read enough bytes for the requested instruction count
            // Average instruction is ~4 bytes, max is 15, so read count * 8 bytes
            int bytesToRead = count * 8;

            byte[] codeBytes = ReadMemoryBlockOptimized(debugger, baseAddr, bytesToRead);

            if (codeBytes != null && codeBytes.Length > 0)
            {
                // Disassemble using Iced
                var instructions = _disasmService.Disassemble(codeBytes, baseAddr, is64Bit, count);

                // Get current IP to mark current instruction
                var ipExpr = is64Bit ? ripExpr : debugger.GetExpression("@eip");
                ulong currentIP = ipExpr != null && ipExpr.IsValidValue
                    ? ParseAddress(ipExpr.Value)
                    : 0;

                foreach (var inst in instructions)
                {
                    bool isCurrent = inst.Address == currentIP;

                    result.Instructions.Add(new DisassemblyInstruction
                    {
                        Address = inst.AddressHex,
                        Opcode = inst.Bytes,
                        Instruction = inst.FullInstruction,
                        IsCurrent = isCurrent,
                        Mnemonic = inst.Mnemonic,
                        Operands = inst.Operands,
                        IsCall = inst.IsCall,
                        IsJump = inst.IsJump,
                        IsReturn = inst.IsReturn,
                        BranchTarget = inst.BranchTarget?.ToString("X"),
                        Length = inst.Length
                    });
                }

                result.TotalInstructions = result.Instructions.Count;
            }
            else
            {
                result.Error = $"Could not read memory at {address}";
            }

            return result;
        }

        /// <summary>
        /// Find a thread that is in user code context (not system DLL).
        /// Prefers threads in user's exe/dll over system threads.
        /// </summary>
        private EnvDTE.Thread FindUserCodeThread(Debugger5 debugger)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            EnvDTE.Thread bestThread = null;
            bool foundUserCode = false;

            try
            {
                foreach (EnvDTE.Thread thread in debugger.CurrentProgram.Threads)
                {
                    try
                    {
                        if (thread.StackFrames.Count > 0)
                        {
                            var topFrame = thread.StackFrames.Item(1) as EnvDTE.StackFrame;
                            if (topFrame != null)
                            {
                                string modulePath = GetFrameFileName(topFrame);
                                
                                // Check if this is user code (not system DLL)
                                bool isUserCode = !string.IsNullOrEmpty(modulePath) &&
                                    !modulePath.StartsWith("C:\\Windows", StringComparison.OrdinalIgnoreCase) &&
                                    modulePath.IndexOf("\\System32\\", StringComparison.OrdinalIgnoreCase) < 0 &&
                                    modulePath.IndexOf("\\SysWOW64\\", StringComparison.OrdinalIgnoreCase) < 0;

                                if (isUserCode && !foundUserCode)
                                {
                                    bestThread = thread;
                                    foundUserCode = true;
                                }
                                else if (!foundUserCode && bestThread == null)
                                {
                                    // Keep first thread as fallback
                                    bestThread = thread;
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Skip threads we can't inspect
                    }
                }
            }
            catch (Exception ex)
            {
                _log($"FindUserCodeThread error: {ex.Message}");
            }

            return bestThread;
        }

        /// <summary>
        /// Validates that memory read via ReadProcessMemory matches what the debugger's
        /// expression evaluator returns. This is important for minidump debugging where
        /// the ProcessID might not correspond to an actual running process.
        /// </summary>
        private bool ValidateMemoryWithExpression(Debugger5 debugger, ulong baseAddress, byte[] memory)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (memory == null || memory.Length < 4)
            {
                return true; // Cannot validate, assume valid
            }

            try
            {
                // Read first DWORD via expression evaluation
                var expr = debugger.GetExpression($"*(unsigned int*)(0x{baseAddress:X})");
                if (expr == null || !expr.IsValidValue)
                {
                    return true; // Cannot validate, assume valid
                }

                uint? expectedValue = ParseUInt(expr.Value);
                if (expectedValue == null)
                {
                    return true; // Cannot validate, assume valid
                }

                // Extract first 4 bytes from memory as little-endian uint32
                uint actualValue = BitConverter.ToUInt32(memory, 0);

                // Compare the values
                if (actualValue != expectedValue.Value)
                {
                    _log($"ValidateMemoryWithExpression: Mismatch at 0x{baseAddress:X} - expected 0x{expectedValue.Value:X8}, got 0x{actualValue:X8}");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _log($"ValidateMemoryWithExpression: Error validating memory at 0x{baseAddress:X}: {ex.Message}");
                return true; // On error, assume valid to avoid breaking existing functionality
            }
        }

        /// <summary>
        /// Optimized memory reading - reads in larger chunks to reduce IPC calls.
        /// Uses 4-byte reads instead of single-byte reads for better performance.
        /// </summary>
        private byte[] ReadMemoryBlockOptimized(EnvDTE.Debugger debugger, ulong address, int size)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new List<byte>();

            // Read in 4-byte chunks (DWORD at a time)
            for (int offset = 0; offset < size; offset += 4)
            {
                try
                {
                    var expr = debugger.GetExpression($"*(unsigned int*)(0x{address + (ulong)offset:X})");

                    if (!expr.IsValidValue)
                        break;

                    uint? value = ParseUInt(expr.Value);
                    if (!value.HasValue)
                        break;

                    var bytes = BitConverter.GetBytes(value.Value);

                    // Only add the bytes we need (handle end of range)
                    int bytesNeeded = Math.Min(4, size - offset);
                    for (int b = 0; b < bytesNeeded; b++)
                        result.Add(bytes[b]);
                }
                catch (Exception ex)
                {
                    _log($"ReadMemoryBlockOptimized: Error at offset {offset}: {ex.Message}");
                    break;
                }
            }

            return result.ToArray();
        }

        /// <summary>
        /// Parse unsigned int from debugger expression value.
        /// </summary>
        private uint? ParseUInt(string value)
        {
            if (string.IsNullOrEmpty(value))
                return null;

            value = value.Trim();

            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (uint.TryParse(value.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out uint hex))
                {
                    return hex;
                }
                return null;
            }

            if (uint.TryParse(value, out uint result))
                return result;

            return null;
        }

        /// <summary>
        /// Parse an address string to a ulong value.
        /// Handles both hex (with 0x prefix) and decimal formats.
        /// </summary>
        private ulong ParseAddress(string addrStr)
        {
            if (string.IsNullOrEmpty(addrStr))
                return 0;

            addrStr = addrStr.Trim();

            // Handle hex with 0x prefix
            if (addrStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                string hexPart = addrStr.Substring(2);
                // Handle addresses with extra info like "0x7ff712b45a94 {Module!Function}"
                int spaceIdx = hexPart.IndexOf(' ');
                if (spaceIdx > 0)
                    hexPart = hexPart.Substring(0, spaceIdx);
                
                if (ulong.TryParse(hexPart, System.Globalization.NumberStyles.HexNumber, null, out ulong hexAddr))
                {
                    return hexAddr;
                }
            }

            // Try to parse as decimal first (VS debugger often returns decimal values)
            if (ulong.TryParse(addrStr, out ulong decAddr))
            {
                return decAddr;
            }

            // Fallback: try to parse as pure hex without prefix (for edge cases)
            if (ulong.TryParse(addrStr, System.Globalization.NumberStyles.HexNumber, null, out ulong fallbackAddr))
            {
                return fallbackAddr;
            }

            return 0;
        }

        private string CreateDebuggerSnapshotToken(Debugger5 debugger)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (debugger == null || debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
            {
                return string.Empty;
            }

            int processId = 0;
            int threadId = 0;
            string functionName = "";
            string moduleName = "";

            try
            {
                processId = debugger.CurrentProcess?.ProcessID ?? 0;
            }
            catch { }

            try
            {
                threadId = debugger.CurrentThread?.ID ?? 0;
            }
            catch { }

            try
            {
                var frame = debugger.CurrentStackFrame;
                if (frame != null)
                {
                    functionName = frame.FunctionName ?? "";
                    moduleName = frame.Module ?? "";
                }
            }
            catch { }

            return $"{processId}:{threadId}:{functionName}:{moduleName}";
        }

        private byte[] ReadProcessMemoryContiguous(Debugger5 debugger, ulong startAddress, int requestedSize)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (debugger == null || requestedSize <= 0)
            {
                return Array.Empty<byte>();
            }

            int processId;
            try
            {
                processId = debugger.CurrentProcess?.ProcessID ?? 0;
            }
            catch
            {
                return Array.Empty<byte>();
            }

            if (processId <= 0)
            {
                return Array.Empty<byte>();
            }

            uint access = PROCESS_VM_READ | PROCESS_QUERY_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION;
            IntPtr processHandle = OpenProcess(access, false, processId);
            if (processHandle == IntPtr.Zero)
            {
                return Array.Empty<byte>();
            }

            try
            {
                var bytes = new List<byte>(requestedSize);
                int offset = 0;
                UIntPtr mbiSize = (UIntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION));

                while (offset < requestedSize)
                {
                    ulong currentAddress = startAddress + (ulong)offset;
                    if (currentAddress > long.MaxValue)
                    {
                        break;
                    }

                    MEMORY_BASIC_INFORMATION mbi;
                    IntPtr queryResult = VirtualQueryEx(processHandle, new IntPtr((long)currentAddress), out mbi, mbiSize);
                    if (queryResult == IntPtr.Zero)
                    {
                        break;
                    }

                    long regionSize = mbi.RegionSize.ToInt64();
                    if (regionSize <= 0)
                    {
                        break;
                    }

                    ulong regionBase = unchecked((ulong)mbi.BaseAddress.ToInt64());
                    ulong regionEnd = regionBase + (ulong)regionSize;
                    if (currentAddress < regionBase || currentAddress >= regionEnd)
                    {
                        break;
                    }

                    int chunkSize = (int)Math.Min((ulong)(requestedSize - offset), regionEnd - currentAddress);
                    if (chunkSize <= 0)
                    {
                        break;
                    }

                    if (!IsReadableMemoryProtection(mbi.State, mbi.Protect))
                    {
                        break;
                    }

                    var chunk = new byte[chunkSize];
                    IntPtr bytesReadPtr;
                    bool success = ReadProcessMemory(
                        processHandle,
                        new IntPtr((long)currentAddress),
                        chunk,
                        (UIntPtr)chunkSize,
                        out bytesReadPtr);

                    if (!success)
                    {
                        break;
                    }

                    int bytesRead = (int)Math.Min(chunkSize, Math.Max(0, bytesReadPtr.ToInt64()));
                    if (bytesRead <= 0)
                    {
                        break;
                    }

                    for (int i = 0; i < bytesRead; i++)
                    {
                        bytes.Add(chunk[i]);
                    }

                    offset += bytesRead;
                    if (bytesRead < chunkSize)
                    {
                        break;
                    }
                }

                return bytes.ToArray();
            }
            finally
            {
                CloseHandle(processHandle);
            }
        }

        private bool IsReadableMemoryProtection(uint state, uint protect)
        {
            if (state != MEM_COMMIT)
            {
                return false;
            }

            if ((protect & PAGE_GUARD) != 0 || (protect & PAGE_NOACCESS) != 0)
            {
                return false;
            }

            uint readableMask =
                PAGE_READONLY |
                PAGE_READWRITE |
                PAGE_WRITECOPY |
                PAGE_EXECUTE_READ |
                PAGE_EXECUTE_READWRITE |
                PAGE_EXECUTE_WRITECOPY;
            return (protect & readableMask) != 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public IntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadProcessMemory(
            IntPtr hProcess,
            IntPtr lpBaseAddress,
            [Out] byte[] lpBuffer,
            UIntPtr nSize,
            out IntPtr lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualQueryEx(
            IntPtr hProcess,
            IntPtr lpAddress,
            out MEMORY_BASIC_INFORMATION lpBuffer,
            UIntPtr dwLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        #endregion

        #region Build Errors

        public BuildErrorsResult GetBuildErrors(bool errorsOnly = false, bool warningsOnly = false)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var result = new BuildErrorsResult();

                try
                {
                    var errorList = _dte.ToolWindows.ErrorList;
                    var errorItems = errorList.ErrorItems;

                    for (int i = 1; i <= errorItems.Count; i++)
                    {
                        var item = errorItems.Item(i);
                        bool isError = item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelHigh;
                        bool isWarning = item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelMedium ||
                                        item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelLow;

                        if (errorsOnly && !isError) continue;
                        if (warningsOnly && !isWarning) continue;

                        if (isError) result.ErrorCount++;
                        if (isWarning) result.WarningCount++;

                        // Extract error code from description
                        string errorCode = ExtractErrorCode(item.Description);

                        result.Items.Add(new BuildErrorItem
                        {
                            Severity = isError ? "Error" : "Warning",
                            Code = errorCode,
                            Description = item.Description,
                            FilePath = item.FileName,
                            Line = item.Line,
                            Column = item.Column,
                            Project = item.Project
                        });
                    }
                }
                catch (Exception ex)
                {
                    _log($"Error getting build errors: {ex.Message}");
                }

                return result;
            });
        }

        /// <summary>
        /// Extract error code from error description.
        /// Handles formats like:
        /// - "CS0001: Description..."
        /// - "error CS0001: Description..."
        /// - "warning CA1234: Description..."
        /// - "C1234: Description..."
        /// </summary>
        /// <summary>
        /// Known error code prefixes for Visual Studio and common analyzers.
        /// </summary>
        private static readonly string[] KnownErrorCodePrefixes = new[]
        {
            "CS",    // C# compiler errors
            "CA",    // Code Analysis warnings
            "IDE",   // IDE analyzers
            "BC",    // Visual Basic compiler
            "NU",    // NuGet errors
            "SA",    // StyleCop analyzers
            "RCS",   // Roslynator analyzers
            "S",     // SonarQube analyzers
            "DOC",   // Documentation analyzers
            "C",     // C/C++ compiler errors
            "LNK",   // Linker errors
            "MSB",   // MSBuild errors
            "NETSDK" // .NET SDK errors
        };

        private string ExtractErrorCode(string description)
        {
            if (string.IsNullOrEmpty(description))
                return "";

            // Method 1: Try to match at the beginning with optional error/warning prefix
            var match = Regex.Match(
                description,
                @"^(?:error\s+|warning\s+)?([A-Z]{1,6}\d{4,5}):",
                RegexOptions.IgnoreCase);

            if (match.Success)
            {
                string code = match.Groups[1].Value.ToUpperInvariant();
                // Verify it starts with a known prefix
                if (IsKnownErrorCode(code))
                {
                    return code;
                }
            }

            // Method 2: Search for known prefixes specifically (more reliable)
            foreach (var prefix in KnownErrorCodePrefixes)
            {
                var prefixMatch = Regex.Match(
                    description,
                    $@"\b({Regex.Escape(prefix)}\d{{4,5}})\b",
                    RegexOptions.IgnoreCase);

                if (prefixMatch.Success)
                {
                    return prefixMatch.Groups[1].Value.ToUpperInvariant();
                }
            }

            // Method 3: Fallback - try any code-like pattern at the beginning
            match = Regex.Match(
                description,
                @"^(?:error\s+|warning\s+)?([A-Z]{1,3}\d{4,5}):",
                RegexOptions.IgnoreCase);

            if (match.Success)
            {
                return match.Groups[1].Value.ToUpperInvariant();
            }

            return "";
        }

        /// <summary>
        /// Check if the given code starts with a known error code prefix.
        /// </summary>
        private bool IsKnownErrorCode(string code)
        {
            if (string.IsNullOrEmpty(code))
                return false;

            foreach (var prefix in KnownErrorCodePrefixes)
            {
                if (code.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        #endregion

        #region Full Context

        public FullContextResult GetFullContext(
            bool threadIncludeFrames = true,
            int threadMaxFramesPerThread = -1,
            int threadMaxNodes = -1,
            int threadBudgetMs = -1)
        {
            int normalizedThreadMaxFramesPerThread = NormalizeThreadFrameLimit(threadMaxFramesPerThread);
            int normalizedThreadMaxNodes = NormalizeThreadNodeLimit(threadMaxNodes);
            int normalizedThreadBudgetMs = NormalizeThreadBudgetMs(threadBudgetMs);
            int mainThreadTimeoutMs = Math.Max(HeavyMainThreadTimeoutMs, ComputeThreadMainThreadTimeoutMs(normalizedThreadBudgetMs));

            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var fullContext = new FullContextResult();
                
                // Build all results in a single UI thread acquisition for atomicity
                try { fullContext.Status = BuildDebugStatus(debugger); }
                catch (Exception ex) { _log($"GetFullContext: Status failed: {ex.Message}"); }
                
                try
                {
                    if (debugger?.CurrentMode == dbgDebugMode.dbgBreakMode)
                    {
                        fullContext.Callstack = BuildCallStackResult(debugger);
                        fullContext.Locals = BuildLocalsResult(debugger, 2);
                        fullContext.Threads = BuildThreadsResult(
                            debugger,
                            threadIncludeFrames,
                            normalizedThreadMaxFramesPerThread,
                            normalizedThreadMaxNodes,
                            normalizedThreadBudgetMs);
                        fullContext.Threads.SnapshotAttempts = 1;
                        fullContext.Threads.SnapshotStable = true;
                        fullContext.Threads.SnapshotStatus = DetermineThreadsSnapshotStatus(fullContext.Threads, true);
                    }
                }
                catch (Exception ex) { _log($"GetFullContext: Debug data failed: {ex.Message}"); }
                
                try { fullContext.Exception = BuildExceptionResult(debugger); }
                catch (Exception ex) { _log($"GetFullContext: Exception failed: {ex.Message}"); }
                
                try { fullContext.Modules = BuildModulesResult(debugger); }
                catch (Exception ex) { _log($"GetFullContext: Modules failed: {ex.Message}"); }

                return fullContext;
            }, mainThreadTimeoutMs);
        }

        public DebugBundleResult CaptureDebugBundle(
            List<string> expressions = null,
            bool expandChildren = false,
            int maxDepth = 2,
            int outputTailLines = 80,
            bool includeLocals = false,
            bool includeModules = false,
            bool includeRegisters = false,
            bool includeOutput = false,
            bool includeThreads = true,
            bool includeException = true,
            bool allowSideEffects = false,
            bool threadIncludeFrames = true,
            int threadMaxFramesPerThread = -1,
            int threadMaxNodes = -1,
            int threadBudgetMs = -1)
        {
            int normalizedThreadMaxFramesPerThread = NormalizeThreadFrameLimit(threadMaxFramesPerThread);
            int normalizedThreadMaxNodes = NormalizeThreadNodeLimit(threadMaxNodes);
            int normalizedThreadBudgetMs = NormalizeThreadBudgetMs(threadBudgetMs);

            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var normalizedExpressions = NormalizeExpressions(expressions);
                int normalizedDepth = Math.Max(0, Math.Min(maxDepth, 4));
                int normalizedTailLines = Math.Max(0, Math.Min(outputTailLines, 500));
                string snapshotId = Guid.NewGuid().ToString("N");
                string capturedAt = DateTime.UtcNow.ToString("o");
                var bestEffort = new DebugBundleResult
                {
                    SnapshotId = snapshotId,
                    CapturedAt = capturedAt,
                    SnapshotStatus = "unavailable",
                    Partial = true,
                    IncludedComponents = BuildIncludedDebugBundleComponents(includeLocals, includeModules, includeRegisters, includeOutput, includeThreads, includeException, normalizedExpressions.Count),
                    OmittedComponents = BuildOmittedDebugBundleComponents(includeLocals, includeModules, includeRegisters, includeOutput, includeThreads, includeException)
                };

                if (debugger == null)
                {
                    bestEffort.Error = "Debugger unavailable";
                    bestEffort.Status = BuildDebugStatus(debugger);
                    bestEffort.StopReason = bestEffort.Status?.StopReason ?? "not_paused";
                    bestEffort.Expressions = BuildUnavailableBatchEvaluateResult(snapshotId, capturedAt, normalizedExpressions.Count, "Debugger unavailable");
                    return bestEffort;
                }

                for (int attempt = 1; attempt <= SnapshotMaxAttempts; attempt++)
                {
                    if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                    {
                        var status = BuildDebugStatus(debugger);
                        bestEffort.Status = status;
                        bestEffort.StopReason = status.StopReason;
                        bestEffort.SnapshotAttempts = attempt;
                        bestEffort.SnapshotStatus = "unavailable";
                        bestEffort.Error = "Not in break mode";
                        bestEffort.Expressions = BuildUnavailableBatchEvaluateResult(snapshotId, capturedAt, normalizedExpressions.Count, "Not in break mode");
                        return bestEffort;
                    }

                    try
                    {
                        string tokenBefore = CreateDebuggerSnapshotToken(debugger);
                        var snapshot = BuildDebugBundleResult(
                            debugger,
                            snapshotId,
                            capturedAt,
                            normalizedExpressions,
                            expandChildren,
                            normalizedDepth,
                            normalizedTailLines,
                            includeLocals,
                            includeModules,
                            includeRegisters,
                            includeOutput,
                            includeThreads,
                            includeException,
                            allowSideEffects,
                            threadIncludeFrames,
                            normalizedThreadMaxFramesPerThread,
                            normalizedThreadMaxNodes,
                            normalizedThreadBudgetMs);
                        string tokenAfter = CreateDebuggerSnapshotToken(debugger);
                        bool stable = !string.IsNullOrEmpty(tokenBefore) && tokenBefore == tokenAfter;

                        snapshot.SnapshotStable = stable;
                        snapshot.SnapshotAttempts = attempt;
                        snapshot.SnapshotStatus = stable
                            ? (snapshot.Partial ? "partial" : "ok")
                            : "unstable";
                        if (snapshot.Expressions != null)
                        {
                            snapshot.Expressions.SnapshotStable = stable;
                            snapshot.Expressions.SnapshotAttempts = attempt;
                            snapshot.Expressions.SnapshotStatus = stable
                                ? (snapshot.Expressions.Partial ? "partial" : "ok")
                                : "unstable";
                        }
                        bestEffort = snapshot;
                        if (stable)
                        {
                            return snapshot;
                        }
                    }
                    catch (Exception ex)
                    {
                        _log($"CaptureDebugBundle snapshot attempt {attempt} failed: {ex.Message}");
                        bestEffort.SnapshotAttempts = attempt;
                        if (bestEffort.Status != null)
                        {
                            bestEffort.SnapshotStatus = "unstable";
                        }
                    }

                    if (attempt < SnapshotMaxAttempts)
                    {
                        System.Threading.Thread.Sleep(SnapshotRetryDelayMs);
                    }
                }

                return bestEffort;
            }, DebugBundleMainThreadTimeoutMs);
        }

        private DebugBundleResult BuildDebugBundleResult(
            Debugger5 debugger,
            string snapshotId,
            string capturedAt,
            IReadOnlyList<string> expressions,
            bool expandChildren,
            int maxDepth,
            int outputTailLines,
            bool includeLocals,
            bool includeModules,
            bool includeRegisters,
            bool includeOutput,
            bool includeThreads,
            bool includeException,
            bool allowSideEffects,
            bool threadIncludeFrames,
            int threadMaxFramesPerThread,
            int threadMaxNodes,
            int threadBudgetMs)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var bundle = new DebugBundleResult
            {
                SnapshotId = snapshotId,
                CapturedAt = capturedAt,
                IncludedComponents = BuildIncludedDebugBundleComponents(includeLocals, includeModules, includeRegisters, includeOutput, includeThreads, includeException, expressions.Count),
                OmittedComponents = BuildOmittedDebugBundleComponents(includeLocals, includeModules, includeRegisters, includeOutput, includeThreads, includeException)
            };
            var componentErrors = new List<string>();

            bundle.Status = BuildDebugStatus(debugger);
            bundle.StopReason = bundle.Status?.StopReason ?? "not_paused";

            try
            {
                bundle.Callstack = BuildCallStackResult(debugger);
                bundle.Callstack.SnapshotAttempts = 1;
                bundle.Callstack.SnapshotStable = true;
                bundle.Callstack.SnapshotStatus = bundle.Callstack.Frames.Count > 0 ? "ok" : "unavailable";
            }
            catch (Exception ex)
            {
                componentErrors.Add($"callstack: {ex.Message}");
                bundle.Callstack = new CallStackResult
                {
                    Capability = "best_effort_frame_metadata",
                    SnapshotStatus = "unavailable"
                };
            }

            if (includeLocals)
            {
                try
                {
                    bundle.Locals = BuildLocalsResult(debugger, maxDepth);
                    if (!string.IsNullOrEmpty(bundle.Locals.Error))
                    {
                        componentErrors.Add($"locals: {bundle.Locals.Error}");
                    }
                }
                catch (Exception ex)
                {
                    componentErrors.Add($"locals: {ex.Message}");
                    bundle.Locals = new LocalsResult
                    {
                        Partial = true,
                        Error = ex.Message
                    };
                }
            }

            if (includeException)
            {
                try
                {
                    bundle.Exception = BuildExceptionResult(debugger);
                }
                catch (Exception ex)
                {
                    componentErrors.Add($"exception: {ex.Message}");
                    bundle.Exception = new ExceptionResult
                    {
                        Capability = "managed_exception_best_effort",
                        HasException = false
                    };
                }
            }

            if (includeThreads)
            {
                try
                {
                    bundle.Threads = BuildThreadsResult(
                        debugger,
                        threadIncludeFrames,
                        threadMaxFramesPerThread,
                        threadMaxNodes,
                        threadBudgetMs);
                    bundle.Threads.SnapshotAttempts = 1;
                    bundle.Threads.SnapshotStable = true;
                    bundle.Threads.SnapshotStatus = DetermineThreadsSnapshotStatus(bundle.Threads, true);
                }
                catch (Exception ex)
                {
                    componentErrors.Add($"threads: {ex.Message}");
                    bundle.Threads = new ThreadsResult
                    {
                        Capability = "best_effort_thread_metadata",
                        SnapshotStatus = "unavailable"
                    };
                }
            }

            if (includeModules)
            {
                try
                {
                    bundle.Modules = BuildModulesResult(debugger);
                    if (!string.IsNullOrEmpty(bundle.Modules.Error))
                    {
                        componentErrors.Add($"modules: {bundle.Modules.Error}");
                    }
                }
                catch (Exception ex)
                {
                    componentErrors.Add($"modules: {ex.Message}");
                    bundle.Modules = new ModulesResult
                    {
                        Capability = "multi_source_best_effort",
                        Error = ex.Message
                    };
                }
            }

            if (includeRegisters)
            {
                try
                {
                    bundle.Registers = BuildRegistersResult(debugger);
                    if (!string.IsNullOrEmpty(bundle.Registers.Error))
                    {
                        componentErrors.Add($"registers: {bundle.Registers.Error}");
                    }
                }
                catch (Exception ex)
                {
                    componentErrors.Add($"registers: {ex.Message}");
                    bundle.Registers = new RegistersResult
                    {
                        Capability = "x86_x64_general_segment_sse_best_effort",
                        Error = ex.Message
                    };
                }
            }

            if (includeOutput)
            {
                try
                {
                    bundle.Output = BuildOutputWindowResult("Debug", outputTailLines);
                    if (!string.IsNullOrEmpty(bundle.Output.Error))
                    {
                        componentErrors.Add($"output: {bundle.Output.Error}");
                    }
                }
                catch (Exception ex)
                {
                    componentErrors.Add($"output: {ex.Message}");
                    bundle.Output = new OutputWindowResult
                    {
                        Pane = "Debug",
                        Capability = "multi_pane_output",
                        Error = ex.Message
                    };
                }
            }

            try
            {
                bundle.Expressions = expressions.Count > 0
                    ? BuildBatchEvaluateResult(debugger, expressions, expandChildren, maxDepth, allowSideEffects, snapshotId, capturedAt)
                    : new BatchEvaluateResult
                    {
                        SnapshotId = snapshotId,
                        CapturedAt = capturedAt,
                        SnapshotStable = true,
                        SnapshotAttempts = 1,
                        SnapshotStatus = "ok",
                        RequestedCount = 0,
                        SuccessCount = 0,
                        FailedCount = 0,
                        Partial = false
                    };
                if (!string.IsNullOrEmpty(bundle.Expressions.Error))
                {
                    componentErrors.Add($"expressions: {bundle.Expressions.Error}");
                }
            }
            catch (Exception ex)
            {
                componentErrors.Add($"expressions: {ex.Message}");
                bundle.Expressions = BuildUnavailableBatchEvaluateResult(snapshotId, capturedAt, expressions.Count, ex.Message);
            }

            if (componentErrors.Count > 0)
            {
                bundle.Error = string.Join("; ", componentErrors);
            }
            bundle.Partial = IsDebugBundlePartial(bundle);

            return bundle;
        }

        private List<string> BuildIncludedDebugBundleComponents(
            bool includeLocals,
            bool includeModules,
            bool includeRegisters,
            bool includeOutput,
            bool includeThreads,
            bool includeException,
            int expressionCount)
        {
            var components = new List<string> { "status", "callstack" };
            if (includeException) components.Add("exception");
            if (includeThreads) components.Add("threads");
            if (includeLocals) components.Add("locals");
            if (includeModules) components.Add("modules");
            if (includeRegisters) components.Add("registers");
            if (includeOutput) components.Add("output");
            if (expressionCount > 0) components.Add("expressions");
            return components;
        }

        private List<string> BuildOmittedDebugBundleComponents(
            bool includeLocals,
            bool includeModules,
            bool includeRegisters,
            bool includeOutput,
            bool includeThreads,
            bool includeException)
        {
            var components = new List<string>();
            if (!includeException) components.Add("exception");
            if (!includeThreads) components.Add("threads");
            if (!includeLocals) components.Add("locals");
            if (!includeModules) components.Add("modules");
            if (!includeRegisters) components.Add("registers");
            if (!includeOutput) components.Add("output");
            return components;
        }

        private bool BundleIncludes(DebugBundleResult bundle, string component)
        {
            return bundle.IncludedComponents != null &&
                bundle.IncludedComponents.Contains(component, StringComparer.OrdinalIgnoreCase);
        }

        private BatchEvaluateResult BuildUnavailableBatchEvaluateResult(string snapshotId, string capturedAt, int requestedCount, string error)
        {
            return new BatchEvaluateResult
            {
                SnapshotId = snapshotId,
                CapturedAt = capturedAt,
                RequestedCount = requestedCount,
                SuccessCount = 0,
                FailedCount = requestedCount,
                Partial = requestedCount > 0,
                SnapshotStatus = "unavailable",
                Error = error
            };
        }

        private bool IsDebugBundlePartial(DebugBundleResult bundle)
        {
            if (bundle == null)
            {
                return true;
            }

            if (!string.IsNullOrEmpty(bundle.Error))
            {
                return true;
            }

            if (bundle.Callstack == null || bundle.Callstack.SnapshotStatus != "ok")
            {
                return true;
            }

            if (BundleIncludes(bundle, "threads") &&
                (bundle.Threads == null || bundle.Threads.SnapshotStatus != "ok" || bundle.Threads.Partial))
            {
                return true;
            }

            if (BundleIncludes(bundle, "locals") && (bundle.Locals == null || bundle.Locals.Partial))
            {
                return true;
            }

            if ((BundleIncludes(bundle, "modules") && bundle.Modules?.Error != null) ||
                (BundleIncludes(bundle, "registers") && bundle.Registers?.Error != null) ||
                (BundleIncludes(bundle, "output") && bundle.Output?.Error != null))
            {
                return true;
            }

            if (BundleIncludes(bundle, "expressions") &&
                bundle.Expressions != null &&
                (bundle.Expressions.Partial || bundle.Expressions.FailedCount > 0))
            {
                return true;
            }

            return false;
        }

        #endregion

        #region Instance Info

        /// <summary>
        /// Gets information about this Visual Studio instance for multi-instance support.
        /// </summary>
        public InstanceInfoResult GetInstanceInfo(string pipeName)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                
                var result = new InstanceInfoResult
                {
                    ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id,
                    PipeName = pipeName,
                    IsDebugging = debugger.CurrentMode != dbgDebugMode.dbgDesignMode,
                    IsPaused = debugger.CurrentMode == dbgDebugMode.dbgBreakMode,
                    DebugMode = GetDebugModeString(debugger.CurrentMode)
                };

                // Get solution info
                try
                {
                    if (_dte.Solution != null && !string.IsNullOrEmpty(_dte.Solution.FullName))
                    {
                        result.SolutionPath = _dte.Solution.FullName;
                        result.SolutionName = System.IO.Path.GetFileNameWithoutExtension(_dte.Solution.FullName);
                    }
                    else
                    {
                        result.SolutionName = "(No solution open)";
                    }
                }
                catch
                {
                    result.SolutionName = "(Unknown)";
                }

                // Get current process name if debugging
                if (result.IsDebugging && debugger.CurrentProcess != null)
                {
                    try
                    {
                        result.CurrentProcess = debugger.CurrentProcess.Name;
                    }
                    catch (Exception ex)
                    {
                        _log($"[GetInstanceInfo] Error getting current process: {ex.Message}");
                    }
                }

                // Get VS version
                try
                {
                    result.VsVersion = _dte.Version;
                }
                catch (Exception ex)
                {
                    _log($"[GetInstanceInfo] Error getting VS version: {ex.Message}");
                }

                return result;
            });
        }

        #endregion

        #region Debug Control

        /// <summary>
        /// Attach the Visual Studio debugger to a local process by PID or executable name.
        /// </summary>
        public ProcessDebugResult AttachProcess(int processId = 0, string processName = null)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new ProcessDebugResult
                {
                    Action = "AttachProcess",
                    ProcessId = processId,
                    ProcessName = processName
                };

                try
                {
                    var target = FindLocalProcess(debugger, processId, processName);
                    if (target == null)
                    {
                        result.Success = false;
                        result.Message = BuildAttachProcessNotFoundMessage(debugger, processId, processName);
                        return result;
                    }

                    _activeDumpFilePath = null;
                    target.Attach();
                    result.Success = true;
                    result.ProcessId = target.ProcessID;
                    result.ProcessName = target.Name;
                    result.Message = $"Attached to process {target.Name} ({target.ProcessID})";
                    result.Status = BuildDebugStatus(debugger);
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Message = ex.Message;
                    result.Status = BuildDebugStatus(debugger);
                }

                return result;
            }, HeavyMainThreadTimeoutMs);
        }

        /// <summary>
        /// Start debugging with the current VS debug profile, or launch an executable and attach to it.
        /// </summary>
        public ProcessDebugResult StartDebugging(string executablePath = null, string workingDirectory = null, string arguments = null, bool attach = true)
        {
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
                            result.Status = BuildDebugStatus(debugger);
                            return result;
                        }

                        if (TryStartActiveDumpDebugging(debugger, result))
                        {
                            return result;
                        }

                        _activeDumpFilePath = null;
                        debugger.Go(false);
                        result.Success = true;
                        result.Message = "Started debugging using the current Visual Studio startup configuration";
                        result.Status = BuildDebugStatus(debugger);
                        return result;
                    }

                    string normalizedExe = System.IO.Path.GetFullPath(executablePath);
                    if (IsDumpFilePath(normalizedExe))
                    {
                        return StartDumpDebuggingFromPath(debugger, result, normalizedExe);
                    }

                    if (!System.IO.File.Exists(normalizedExe))
                    {
                        result.Success = false;
                        result.Message = $"Executable not found: {normalizedExe}";
                        result.Status = BuildDebugStatus(debugger);
                        return result;
                    }

                    string normalizedWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                        ? System.IO.Path.GetDirectoryName(normalizedExe)
                        : System.IO.Path.GetFullPath(workingDirectory);

                    if (!string.IsNullOrWhiteSpace(normalizedWorkingDirectory) &&
                        !System.IO.Directory.Exists(normalizedWorkingDirectory))
                    {
                        result.Success = false;
                        result.Message = $"Working directory not found: {normalizedWorkingDirectory}";
                        result.Status = BuildDebugStatus(debugger);
                        return result;
                    }

                    _activeDumpFilePath = null;
                    var startInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = normalizedExe,
                        Arguments = arguments ?? string.Empty,
                        WorkingDirectory = normalizedWorkingDirectory ?? string.Empty,
                        UseShellExecute = false
                    };

                    var process = System.Diagnostics.Process.Start(startInfo);
                    if (process == null)
                    {
                        result.Success = false;
                        result.Message = "Failed to start process";
                        result.Status = BuildDebugStatus(debugger);
                        return result;
                    }

                    result.ProcessId = process.Id;
                    result.ProcessName = process.ProcessName;
                    result.ExecutablePath = normalizedExe;
                    result.WorkingDirectory = normalizedWorkingDirectory;

                    if (!attach)
                    {
                        result.Success = true;
                        result.Message = $"Started process {process.ProcessName} ({process.Id}) without attaching";
                        result.Status = BuildDebugStatus(debugger);
                        return result;
                    }

                    var target = WaitForLocalProcess(debugger, process.Id, 5000);
                    if (target == null)
                    {
                        result.Success = false;
                        result.Message = $"Started process {process.ProcessName} ({process.Id}) but Visual Studio did not expose it for attach";
                        result.Status = BuildDebugStatus(debugger);
                        return result;
                    }

                    target.Attach();
                    result.Success = true;
                    result.ProcessName = target.Name;
                    result.Message = $"Started and attached to process {target.Name} ({target.ProcessID})";
                    result.Status = BuildDebugStatus(debugger);
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Message = ex.Message;
                    result.Status = BuildDebugStatus(debugger);
                }

                return result;
            }, HeavyMainThreadTimeoutMs);
        }

        private ProcessDebugResult StartDumpDebuggingFromPath(Debugger5 debugger, ProcessDebugResult result, string dumpPath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            result.ExecutablePath = dumpPath;

            if (!System.IO.File.Exists(dumpPath))
            {
                result.Success = false;
                result.Message = $"Dump file not found: {dumpPath}";
                result.Status = BuildDebugStatus(debugger);
                return result;
            }

            try
            {
                _activeDumpFilePath = dumpPath;
                _dte.ItemOperations.OpenFile(dumpPath);
                if (!TryStartActiveDumpNativeOnlyDebugging(out string startError))
                {
                    _activeDumpFilePath = null;
                    result.Success = false;
                    result.Message = startError;
                    result.Status = BuildDebugStatus(debugger);
                    return result;
                }

                result.Success = true;
                result.Message = "Requested Visual Studio to start native debugging for dump file";
                result.Status = BuildDebugStatus(debugger);
            }
            catch (Exception ex)
            {
                _activeDumpFilePath = null;
                result.Success = false;
                result.Message = ex.Message;
                result.Status = BuildDebugStatus(debugger);
            }

            return result;
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

            if (!IsDumpFilePath(activeDocumentPath) || !System.IO.File.Exists(activeDocumentPath))
            {
                return false;
            }

            activeDocumentPath = System.IO.Path.GetFullPath(activeDocumentPath);
            _activeDumpFilePath = activeDocumentPath;
            result.ExecutablePath = activeDocumentPath;

            try
            {
                if (!TryStartActiveDumpNativeOnlyDebugging(out string startError))
                {
                    _activeDumpFilePath = null;
                    result.Success = false;
                    result.Message = startError;
                    result.Status = BuildDebugStatus(debugger);
                    return true;
                }

                result.Success = true;
                result.Message = "Requested Visual Studio to start native debugging for active dump file";
                result.Status = BuildDebugStatus(debugger);
            }
            catch (Exception ex)
            {
                _activeDumpFilePath = null;
                result.Success = false;
                result.Message = ex.Message;
                result.Status = BuildDebugStatus(debugger);
            }

            return true;
        }

        private bool TryStartActiveDumpNativeOnlyDebugging(out string error)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            error = null;

            try
            {
                var result = ThreadHelper.JoinableTaskFactory.Run(async () =>
                {
                    Exception lastException = null;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < DumpDebugStartTimeoutMs)
                    {
                        await System.Threading.Tasks.Task.Delay(DumpDebugStartPollMs);
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        try
                        {
                            if (TryInvokeVisualStudioControl("Debug with Native Only"))
                            {
                                return Tuple.Create(true, (string)null);
                            }
                        }
                        catch (Exception ex)
                        {
                            lastException = ex;
                        }
                    }

                    string message = lastException != null
                        ? $"Failed to invoke Debug with Native Only: {lastException.Message}"
                        : "Failed to find Visual Studio control: Debug with Native Only";
                    _log(message);
                    return Tuple.Create(false, message);
                });
                error = result.Item2;
                return result.Item1;
            }
            catch (AggregateException ae)
            {
                error = ae.InnerException?.Message ?? ae.Message;
                _log($"Failed to invoke Debug with Native Only: {error}");
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                _log($"Failed to invoke Debug with Native Only: {error}");
                return false;
            }
        }

        private bool TryInvokeVisualStudioControl(string automationName)
        {
            var mainWindowHandle = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            if (mainWindowHandle == IntPtr.Zero)
            {
                return false;
            }

            var root = AutomationElement.FromHandle(mainWindowHandle);
            if (root == null)
            {
                return false;
            }

            var element = root.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, automationName));
            if (element == null)
            {
                return false;
            }

            object pattern;
            if (element.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
            {
                ((InvokePattern)pattern).Invoke();
                return true;
            }

            return false;
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

        private EnvDTE.Process FindLocalProcess(Debugger5 debugger, int processId, string processName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (debugger == null)
            {
                return null;
            }

            if (processId <= 0 && string.IsNullOrWhiteSpace(processName))
            {
                return null;
            }

            var nameMatches = new List<EnvDTE.Process>();
            foreach (EnvDTE.Process process in debugger.LocalProcesses)
            {
                if (processId > 0 && process.ProcessID == processId)
                {
                    return process;
                }

                if (processId <= 0 && ProcessNameMatches(process.Name, processName))
                {
                    nameMatches.Add(process);
                }
            }

            return nameMatches.Count == 1 ? nameMatches[0] : null;
        }

        private EnvDTE.Process WaitForLocalProcess(Debugger5 debugger, int processId, int timeoutMs)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                var process = FindLocalProcess(debugger, processId, null);
                if (process != null)
                {
                    return process;
                }

                System.Threading.Thread.Sleep(100);
            }

            return null;
        }

        private string BuildAttachProcessNotFoundMessage(Debugger5 debugger, int processId, string processName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (processId > 0)
            {
                return $"Process with PID {processId} was not found in Visual Studio local processes";
            }

            if (string.IsNullOrWhiteSpace(processName))
            {
                return "Either process_id or process_name is required";
            }

            var matches = new List<string>();
            try
            {
                foreach (EnvDTE.Process process in debugger.LocalProcesses)
                {
                    if (ProcessNameMatches(process.Name, processName))
                    {
                        matches.Add($"{process.Name} ({process.ProcessID})");
                    }
                }
            }
            catch
            {
                // Best-effort diagnostic only.
            }

            if (matches.Count > 1)
            {
                return $"Multiple processes named '{processName}' found. Retry with process_id. Matches: {string.Join(", ", matches.ToArray())}";
            }

            return $"Process named '{processName}' was not found in Visual Studio local processes";
        }

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
                    debugger.Go(false); // false = don't wait for break
                    result.Success = true;
                    result.Message = "Execution continued";
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Continues execution if needed, waits for the debugger to stop again, and captures a debug bundle.
        /// </summary>
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
            bool allowSideEffects = false,
            bool threadIncludeFrames = true,
            int threadMaxFramesPerThread = -1,
            int threadMaxNodes = -1,
            int threadBudgetMs = -1)
        {
            int normalizedTimeoutMs = Math.Max(1000, Math.Min(timeoutMs, 30 * 60 * 1000));
            int normalizedPollIntervalMs = Math.Max(50, Math.Min(pollIntervalMs, 1000));
            var initialStatus = GetDebugStatus();
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
                result.Message = "No active debug session";
                return result;
            }

            if (initialStatus.IsPaused)
            {
                var continueResult = Continue();
                result.IssuedContinue = continueResult.Success;
                if (!continueResult.Success)
                {
                    result.Success = false;
                    result.Message = continueResult.Message;
                    result.Status = GetDebugStatus();
                    result.StopReason = result.Status?.StopReason ?? "not_paused";
                    return result;
                }
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < normalizedTimeoutMs)
            {
                var status = GetDebugStatus();
                result.Status = status;
                result.StopReason = status?.StopReason ?? "not_paused";
                result.WaitedMs = (int)Math.Min(sw.ElapsedMilliseconds, int.MaxValue);

                if (status == null || !status.IsDebugging)
                {
                    result.Success = false;
                    result.Message = "Debug session ended before hitting a breakpoint";
                    return result;
                }

                if (status.IsPaused)
                {
                    var bundle = CaptureDebugBundle(
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
                        allowSideEffects,
                        threadIncludeFrames,
                        threadMaxFramesPerThread,
                        threadMaxNodes,
                        threadBudgetMs);
                    result.Success = true;
                    result.BreakDetected = true;
                    result.TimedOut = false;
                    result.Bundle = bundle;
                    result.Status = bundle?.Status ?? status;
                    result.StopReason = bundle?.StopReason ?? result.Status?.StopReason ?? "not_paused";
                    result.Message = "Debug stop detected and bundle captured";
                    result.WaitedMs = (int)Math.Min(sw.ElapsedMilliseconds, int.MaxValue);
                    return result;
                }

                System.Threading.Thread.Sleep(normalizedPollIntervalMs);
            }

            result.Status = GetDebugStatus();
            result.StopReason = result.Status?.StopReason ?? "not_paused";
            result.WaitedMs = normalizedTimeoutMs;
            result.Success = false;
            result.TimedOut = true;
            result.Message = "Timed out waiting for debugger to enter break mode";
            return result;
        }

        /// <summary>
        /// Step into (F11).
        /// </summary>
        public DebugControlResult StepInto()
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new DebugControlResult { Action = "StepInto" };

                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    result.Success = false;
                    result.Message = "Not in break mode";
                    return result;
                }

                try
                {
                    debugger.StepInto(false);
                    result.Success = true;
                    result.Message = "Stepped into";
                    
                    // Wait for debugger to re-enter break mode before reading location
                    if (WaitForBreakMode(debugger, 2000) && debugger.CurrentStackFrame != null)
                    {
                        result.NewLocation = new LocationInfo
                        {
                            FunctionName = debugger.CurrentStackFrame.FunctionName,
                            FilePath = GetFrameFileName(debugger.CurrentStackFrame),
                            LineNumber = GetFrameLineNumber(debugger.CurrentStackFrame)
                        };
                    }
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Step over (F10).
        /// </summary>
        public DebugControlResult StepOver()
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new DebugControlResult { Action = "StepOver" };

                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    result.Success = false;
                    result.Message = "Not in break mode";
                    return result;
                }

                try
                {
                    debugger.StepOver(false);
                    result.Success = true;
                    result.Message = "Stepped over";
                    
                    if (WaitForBreakMode(debugger, 2000) && debugger.CurrentStackFrame != null)
                    {
                        result.NewLocation = new LocationInfo
                        {
                            FunctionName = debugger.CurrentStackFrame.FunctionName,
                            FilePath = GetFrameFileName(debugger.CurrentStackFrame),
                            LineNumber = GetFrameLineNumber(debugger.CurrentStackFrame)
                        };
                    }
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Step out (Shift+F11).
        /// </summary>
        public DebugControlResult StepOut()
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new DebugControlResult { Action = "StepOut" };

                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    result.Success = false;
                    result.Message = "Not in break mode";
                    return result;
                }

                try
                {
                    debugger.StepOut(false);
                    result.Success = true;
                    result.Message = "Stepped out";
                    
                    if (WaitForBreakMode(debugger, 2000) && debugger.CurrentStackFrame != null)
                    {
                        result.NewLocation = new LocationInfo
                        {
                            FunctionName = debugger.CurrentStackFrame.FunctionName,
                            FilePath = GetFrameFileName(debugger.CurrentStackFrame),
                            LineNumber = GetFrameLineNumber(debugger.CurrentStackFrame)
                        };
                    }
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Message = ex.Message;
                }

                return result;
            });
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
                    debugger.Break(false);
                    result.Success = true;
                    result.Message = "Execution broken";
                }
                catch (Exception ex)
                {
                    result.Success = false;
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
                    debugger.Stop(false);
                    result.Success = true;
                    result.Message = "Debugging stopped";
                    _activeDumpFilePath = null;
                }
                catch (Exception ex)
                {
                    result.Success = false;
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
                    process.Detach(false);
                    result.Success = true;
                    result.Message = $"Debugger detached from {processName} ({processId})";
                    _activeDumpFilePath = null;
                }
                catch (Exception ex)
                {
                    result.Success = false;
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
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new DebugControlResult { Action = "RunToCursor" };

                // Validate target location
                var validation = ValidateTargetLocation(filePath, lineNumber);
                if (!validation.IsValid)
                {
                    result.Success = false;
                    result.Message = validation.ErrorMessage;
                    return result;
                }

                try
                {
                    // Open the file and set cursor
                    var window = _dte.ItemOperations.OpenFile(filePath);
                    if (window != null)
                    {
                        var textDoc = window.Document?.Object("TextDocument") as EnvDTE.TextDocument;
                        if (textDoc != null)
                        {
                            var editPoint = textDoc.StartPoint.CreateEditPoint();
                            editPoint.MoveToLineAndOffset(lineNumber, 1);
                            textDoc.Selection.MoveToPoint(editPoint);
                        }
                    }

                    debugger.RunToCursor(false);
                    result.Success = true;
                    result.Message = $"Running to {filePath}:{lineNumber}";
                }
                catch (Exception ex)
                {
                    result.Success = false;
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

                // Validate target location
                var validation = ValidateTargetLocation(filePath, lineNumber);
                if (!validation.IsValid)
                {
                    result.Success = false;
                    result.Message = validation.ErrorMessage;
                    return result;
                }

                try
                {
                    // Open the file and set cursor position
                    var window = _dte.ItemOperations.OpenFile(filePath);
                    if (window != null)
                    {
                        var textDoc = window.Document?.Object("TextDocument") as EnvDTE.TextDocument;
                        if (textDoc != null)
                        {
                            var editPoint = textDoc.StartPoint.CreateEditPoint();
                            editPoint.MoveToLineAndOffset(lineNumber, 1);
                            textDoc.Selection.MoveToPoint(editPoint);
                        }
                    }

                    debugger.SetNextStatement();
                    result.Success = true;
                    result.Message = $"Next statement set to {filePath}:{lineNumber}";
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Validate that the target file and line are valid execution targets.
        /// </summary>
        private (bool IsValid, string ErrorMessage) ValidateTargetLocation(string filePath, int lineNumber)
        {
            // Check if file exists
            if (string.IsNullOrEmpty(filePath))
            {
                return (false, "File path is required");
            }

            if (!System.IO.File.Exists(filePath))
            {
                return (false, $"File not found: {filePath}");
            }

            // Read file lines
            string[] lines;
            try
            {
                lines = System.IO.File.ReadAllLines(filePath);
            }
            catch (Exception ex)
            {
                return (false, $"Cannot read file: {ex.Message}");
            }

            // Check line number validity
            if (lineNumber < 1)
            {
                return (false, "Line number must be >= 1");
            }

            if (lineNumber > lines.Length)
            {
                return (false, $"Line {lineNumber} is beyond end of file (file has {lines.Length} lines)");
            }

            // Get the target line content
            string targetLine = lines[lineNumber - 1].Trim();

            // Check for empty line
            if (string.IsNullOrEmpty(targetLine))
            {
                return (false, $"Line {lineNumber} is empty - cannot set execution point on empty line");
            }

            // Check for common non-executable patterns
            // Note: These are heuristics and may have false positives/negatives
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

        #region Breakpoints

        /// <summary>
        /// Get all breakpoints.
        /// </summary>
        public BreakpointsResult GetBreakpoints()
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new BreakpointsResult();

                try
                {
                    foreach (EnvDTE.Breakpoint bp in debugger.Breakpoints)
                    {
                        result.Breakpoints.Add(BuildBreakpointInfo(bp, debugger));
                    }
                    result.TotalCount = result.Breakpoints.Count;
                }
                catch (Exception ex)
                {
                    result.Error = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Set a breakpoint at a file location.
        /// </summary>
        public BreakpointOperationResult SetBreakpoint(
            string filePath,
            int lineNumber,
            string condition = null,
            int hitCount = 0,
            string hitCountType = null,
            string conditionType = null)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new BreakpointOperationResult { Operation = "Set" };
                string normalizedCondition = NormalizeBreakpointCondition(condition);
                string breakpointLanguage = GetBreakpointLanguage(filePath);
                var breakpointConditionType = ParseBreakpointConditionType(conditionType);
                var breakpointHitCountType = ParseHitCountType(hitCountType, hitCount);
                int normalizedHitCount = Math.Max(0, hitCount);

                try
                {
                    var locationValidation = ValidateBreakpointLocation(filePath, lineNumber);
                    if (!locationValidation.IsValid)
                    {
                        result.Success = false;
                        result.Message = locationValidation.ErrorMessage;
                        return result;
                    }

                    var breakpoint = AddFileBreakpoint(
                        debugger,
                        filePath,
                        lineNumber,
                        normalizedCondition,
                        breakpointLanguage,
                        breakpointConditionType,
                        normalizedHitCount,
                        breakpointHitCountType);
                    if (breakpoint == null)
                    {
                        result.Success = false;
                        result.Message = "Failed to set breakpoint";
                        return result;
                    }

                    WaitForBreakpointBinding(debugger, breakpoint);
                    var breakpointInfo = BuildBreakpointInfo(breakpoint, debugger, filePath, lineNumber, normalizedCondition);

                    if (!breakpointInfo.ConditionApplied)
                    {
                        SafeDeleteBreakpoint(breakpoint);
                        result.Success = false;
                        result.Message = $"Breakpoint condition was not applied at {filePath}:{lineNumber}";
                        return result;
                    }

                    if (ShouldSearchNearbyBindableLine(debugger, breakpointInfo))
                    {
                        var reboundBreakpoint = TryFindNearbyBoundBreakpoint(
                            debugger,
                            filePath,
                            lineNumber,
                            normalizedCondition,
                            breakpointLanguage,
                            breakpointConditionType,
                            normalizedHitCount,
                            breakpointHitCountType);
                        if (reboundBreakpoint == null)
                        {
                            result.Success = false;
                            result.BreakpointInfo = breakpointInfo;
                            result.Message = $"Breakpoint at {filePath}:{lineNumber} is still pending because no nearby executable code is bound in the current session";
                            return result;
                        }

                        SafeDeleteBreakpoint(breakpoint);
                        breakpoint = reboundBreakpoint;
                        WaitForBreakpointBinding(debugger, breakpoint);
                        breakpointInfo = BuildBreakpointInfo(breakpoint, debugger, filePath, lineNumber, normalizedCondition);
                    }

                    result.Success = true;
                    result.BreakpointInfo = breakpointInfo;
                    result.Message = BuildSetBreakpointMessage(breakpointInfo, filePath, lineNumber);
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Set a breakpoint at a function.
        /// </summary>
        public BreakpointOperationResult SetFunctionBreakpoint(
            string functionName,
            string condition = null,
            int hitCount = 0,
            string hitCountType = null,
            string conditionType = null)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new BreakpointOperationResult { Operation = "SetFunction" };
                string normalizedCondition = NormalizeBreakpointCondition(condition);
                var breakpointConditionType = ParseBreakpointConditionType(conditionType);
                var breakpointHitCountType = ParseHitCountType(hitCountType, hitCount);
                int normalizedHitCount = Math.Max(0, hitCount);

                try
                {
                    EnvDTE.Breakpoints bps = debugger.Breakpoints.Add(
                        functionName,
                        "",
                        0,
                        0,
                        normalizedCondition ?? string.Empty,
                        breakpointConditionType,
                        string.Empty,
                        string.Empty,
                        1,
                        string.Empty,
                        normalizedHitCount,
                        breakpointHitCountType);

                    if (bps != null && bps.Count > 0)
                    {
                        var bp = bps.Item(1);
                        var breakpointInfo = BuildBreakpointInfo(bp, debugger, null, 0, normalizedCondition);
                        if (!breakpointInfo.ConditionApplied)
                        {
                            SafeDeleteBreakpoint(bp);
                            result.Success = false;
                            result.Message = $"Function breakpoint condition was not applied on {functionName}";
                            return result;
                        }

                        result.Success = true;
                        result.Message = $"Function breakpoint set on {functionName}";
                        result.BreakpointInfo = breakpointInfo;
                    }
                    else
                    {
                        result.Success = false;
                        result.Message = "Failed to set function breakpoint";
                    }
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Set a native data breakpoint/watchpoint for an expression or address.
        /// </summary>
        public BreakpointOperationResult SetDataBreakpoint(
            string dataExpression,
            int byteCount = 1,
            string condition = null,
            int hitCount = 0,
            string hitCountType = null,
            string conditionType = null)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new BreakpointOperationResult { Operation = "SetData" };
                string normalizedExpression = string.IsNullOrWhiteSpace(dataExpression) ? null : dataExpression.Trim();
                string normalizedCondition = NormalizeBreakpointCondition(condition);
                int normalizedByteCount = Math.Max(1, Math.Min(byteCount, 1024));
                int normalizedHitCount = Math.Max(0, hitCount);
                var breakpointConditionType = ParseBreakpointConditionType(conditionType);
                var breakpointHitCountType = ParseHitCountType(hitCountType, hitCount);

                if (string.IsNullOrEmpty(normalizedExpression))
                {
                    result.Success = false;
                    result.Message = "data_expression is required";
                    return result;
                }

                try
                {
                    EnvDTE.Breakpoints bps = debugger.Breakpoints.Add(
                        string.Empty,
                        string.Empty,
                        0,
                        0,
                        normalizedCondition ?? string.Empty,
                        breakpointConditionType,
                        string.Empty,
                        normalizedExpression,
                        normalizedByteCount,
                        string.Empty,
                        normalizedHitCount,
                        breakpointHitCountType);

                    if (bps == null || bps.Count == 0)
                    {
                        result.Success = false;
                        result.Message = "Failed to set data breakpoint";
                        return result;
                    }

                    var breakpoint = bps.Item(1);
                    var breakpointInfo = BuildBreakpointInfo(breakpoint, debugger, null, 0, normalizedCondition);
                    if (!breakpointInfo.ConditionApplied)
                    {
                        SafeDeleteBreakpoint(breakpoint);
                        result.Success = false;
                        result.Message = $"Data breakpoint condition was not applied for {normalizedExpression}";
                        return result;
                    }

                    result.Success = true;
                    result.Message = $"Data breakpoint set for {normalizedExpression} ({normalizedByteCount} byte(s))";
                    result.BreakpointInfo = breakpointInfo;
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Delete a breakpoint.
        /// </summary>
        public BreakpointOperationResult DeleteBreakpoint(string filePath, int lineNumber)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new BreakpointOperationResult { Operation = "Delete" };

                try
                {
                    bool deleted = false;
                    foreach (EnvDTE.Breakpoint bp in debugger.Breakpoints)
                    {
                        if (string.Equals(bp.File, filePath, StringComparison.OrdinalIgnoreCase) && bp.FileLine == lineNumber)
                        {
                            bp.Delete();
                            deleted = true;
                            break;
                        }
                    }

                    result.Success = deleted;
                    result.Message = deleted ? $"Breakpoint deleted at {filePath}:{lineNumber}" : "Breakpoint not found";
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Delete all breakpoints.
        /// </summary>
        public BreakpointOperationResult DeleteAllBreakpoints()
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new BreakpointOperationResult { Operation = "DeleteAll" };

                try
                {
                    int count = debugger.Breakpoints.Count;
                    // Collect breakpoints first to avoid modifying collection during enumeration
                    var breakpointsToDelete = new List<EnvDTE.Breakpoint>();
                    foreach (EnvDTE.Breakpoint bp in debugger.Breakpoints)
                    {
                        breakpointsToDelete.Add(bp);
                    }
                    foreach (var bp in breakpointsToDelete)
                    {
                        bp.Delete();
                    }

                    result.Success = true;
                    result.Message = $"Deleted {count} breakpoints";
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Enable or disable a breakpoint.
        /// </summary>
        public BreakpointOperationResult EnableBreakpoint(string filePath, int lineNumber, bool enabled)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new BreakpointOperationResult { Operation = enabled ? "Enable" : "Disable" };

                try
                {
                    bool found = false;
                    foreach (EnvDTE.Breakpoint bp in debugger.Breakpoints)
                    {
                        if (string.Equals(bp.File, filePath, StringComparison.OrdinalIgnoreCase) && bp.FileLine == lineNumber)
                        {
                            bp.Enabled = enabled;
                            found = true;
                            break;
                        }
                    }

                    result.Success = found;
                    result.Message = found ? 
                        $"Breakpoint {(enabled ? "enabled" : "disabled")} at {filePath}:{lineNumber}" : 
                        "Breakpoint not found";
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        private BreakpointInfo BuildBreakpointInfo(EnvDTE.Breakpoint breakpoint, Debugger5 debugger, string requestedFilePath = null, int requestedLineNumber = 0, string requestedCondition = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var boundChildren = GetBreakpointChildren(breakpoint);
            var resolvedBreakpoint = GetResolvedBreakpoint(breakpoint, boundChildren);

            return new BreakpointInfo
            {
                Name = breakpoint.Name,
                FilePath = breakpoint.File,
                LineNumber = breakpoint.FileLine,
                Column = breakpoint.FileColumn,
                FunctionName = breakpoint.FunctionName,
                Enabled = breakpoint.Enabled,
                Condition = breakpoint.Condition,
                ConditionType = GetConditionTypeString(breakpoint),
                HitCount = breakpoint.CurrentHits,
                HitCountTarget = breakpoint.HitCountTarget,
                HitCountType = GetHitCountTypeString(breakpoint),
                BreakpointType = GetBreakpointTypeString(breakpoint.LocationType),
                Tag = breakpoint.Tag,
                RequestedFilePath = string.IsNullOrEmpty(requestedFilePath) ? null : requestedFilePath,
                RequestedLineNumber = requestedLineNumber,
                RequestedCondition = requestedCondition,
                ResolvedFilePath = resolvedBreakpoint?.File,
                ResolvedLineNumber = resolvedBreakpoint?.FileLine ?? 0,
                ResolvedColumn = resolvedBreakpoint?.FileColumn ?? 0,
                ResolvedFunctionName = resolvedBreakpoint?.FunctionName,
                BindingStatus = GetBreakpointBindingStatus(debugger, boundChildren.Count),
                BoundChildrenCount = boundChildren.Count,
                IsBound = boundChildren.Count > 0,
                ConditionApplied = IsConditionApplied(requestedCondition, breakpoint.Condition)
            };
        }

        private List<EnvDTE.Breakpoint> GetBreakpointChildren(EnvDTE.Breakpoint breakpoint)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var children = new List<EnvDTE.Breakpoint>();
            if (breakpoint == null)
            {
                return children;
            }

            EnvDTE.Breakpoints breakpointChildren = null;
            try
            {
                breakpointChildren = breakpoint.Children;
            }
            catch
            {
                return children;
            }

            if (breakpointChildren == null)
            {
                return children;
            }

            try
            {
                for (int i = 1; i <= breakpointChildren.Count; i++)
                {
                    var child = breakpointChildren.Item(i);
                    if (child != null)
                    {
                        children.Add(child);
                    }
                }
            }
            catch (Exception ex)
            {
                _log($"[GetBreakpointChildren] Error enumerating children: {ex.Message}");
            }

            return children;
        }

        private EnvDTE.Breakpoint GetResolvedBreakpoint(EnvDTE.Breakpoint breakpoint, List<EnvDTE.Breakpoint> children)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (children != null && children.Count > 0)
            {
                return children[0];
            }

            return breakpoint;
        }

        private string GetBreakpointBindingStatus(Debugger5 debugger, int boundChildrenCount)
        {
            if (boundChildrenCount > 0)
            {
                return boundChildrenCount > 1 ? "bound_multiple" : "bound";
            }

            if (debugger == null)
            {
                return "unknown";
            }

            try
            {
                return debugger.CurrentMode == dbgDebugMode.dbgDesignMode ? "pending" : "unbound";
            }
            catch
            {
                return "unknown";
            }
        }

        private bool IsConditionApplied(string requestedCondition, string actualCondition)
        {
            string normalizedRequested = NormalizeBreakpointCondition(requestedCondition);
            if (string.IsNullOrEmpty(normalizedRequested))
            {
                return true;
            }

            string normalizedActual = NormalizeBreakpointCondition(actualCondition);
            return string.Equals(normalizedRequested, normalizedActual, StringComparison.Ordinal);
        }

        private string NormalizeBreakpointCondition(string condition)
        {
            return string.IsNullOrWhiteSpace(condition) ? null : condition.Trim();
        }

        private dbgBreakpointConditionType ParseBreakpointConditionType(string conditionType)
        {
            if (string.IsNullOrWhiteSpace(conditionType))
            {
                return dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue;
            }

            string normalized = conditionType.Trim().Replace("-", "_").ToLowerInvariant();
            switch (normalized)
            {
                case "changed":
                case "when_changed":
                case "whenchanged":
                    return dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenChanged;
                case "true":
                case "when_true":
                case "whentrue":
                default:
                    return dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue;
            }
        }

        private dbgHitCountType ParseHitCountType(string hitCountType, int hitCount)
        {
            if (hitCount <= 0)
            {
                return dbgHitCountType.dbgHitCountTypeNone;
            }

            if (string.IsNullOrWhiteSpace(hitCountType))
            {
                return dbgHitCountType.dbgHitCountTypeEqual;
            }

            string normalized = hitCountType.Trim().Replace("-", "_").ToLowerInvariant();
            switch (normalized)
            {
                case "greater_or_equal":
                case "greater_than_or_equal":
                case "greater_equal":
                case ">=":
                    return dbgHitCountType.dbgHitCountTypeGreaterOrEqual;
                case "multiple":
                case "multiple_of":
                    return dbgHitCountType.dbgHitCountTypeMultiple;
                case "equal":
                case "equals":
                case "==":
                default:
                    return dbgHitCountType.dbgHitCountTypeEqual;
            }
        }

        private string GetConditionTypeString(EnvDTE.Breakpoint breakpoint)
        {
            try
            {
                return breakpoint.ConditionType.ToString();
            }
            catch
            {
                return null;
            }
        }

        private string GetHitCountTypeString(EnvDTE.Breakpoint breakpoint)
        {
            try
            {
                return breakpoint.HitCountType.ToString();
            }
            catch
            {
                return null;
            }
        }

        private EnvDTE.Breakpoint AddFileBreakpoint(
            Debugger5 debugger,
            string filePath,
            int lineNumber,
            string condition,
            string language,
            dbgBreakpointConditionType conditionType,
            int hitCount,
            dbgHitCountType hitCountType)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            EnvDTE.Breakpoints breakpoints = debugger.Breakpoints.Add(
                string.Empty,
                filePath,
                lineNumber,
                1,
                condition ?? string.Empty,
                conditionType,
                language ?? string.Empty,
                string.Empty,
                1,
                string.Empty,
                Math.Max(0, hitCount),
                hitCountType);

            return breakpoints != null && breakpoints.Count > 0 ? breakpoints.Item(1) : null;
        }

        private string GetBreakpointLanguage(string filePath)
        {
            string extension = System.IO.Path.GetExtension(filePath)?.ToLowerInvariant();
            switch (extension)
            {
                case ".cs":
                case ".csx":
                    return "C#";
                case ".vb":
                    return "Basic";
                case ".fs":
                    return "F#";
                case ".c":
                case ".cc":
                case ".cpp":
                case ".cxx":
                case ".h":
                case ".hpp":
                case ".hxx":
                case ".inl":
                    return "C++";
                default:
                    return null;
            }
        }

        private bool WaitForBreakpointBinding(Debugger5 debugger, EnvDTE.Breakpoint breakpoint)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (breakpoint == null || debugger == null)
            {
                return false;
            }

            try
            {
                if (debugger.CurrentMode == dbgDebugMode.dbgDesignMode)
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < BreakpointBindWaitMs)
            {
                if (GetBreakpointChildren(breakpoint).Count > 0)
                {
                    return true;
                }

                System.Threading.Thread.Sleep(BreakpointBindPollMs);
            }

            return false;
        }

        private bool ShouldSearchNearbyBindableLine(Debugger5 debugger, BreakpointInfo breakpointInfo)
        {
            if (breakpointInfo == null || breakpointInfo.IsBound || debugger == null)
            {
                return false;
            }

            try
            {
                return debugger.CurrentMode != dbgDebugMode.dbgDesignMode &&
                       !string.IsNullOrEmpty(breakpointInfo.FilePath) &&
                       breakpointInfo.LineNumber > 0 &&
                       IsLikelyNonBindableBreakpointLine(breakpointInfo.RequestedFilePath ?? breakpointInfo.FilePath,
                           breakpointInfo.RequestedLineNumber > 0 ? breakpointInfo.RequestedLineNumber : breakpointInfo.LineNumber);
            }
            catch
            {
                return false;
            }
        }

        private EnvDTE.Breakpoint TryFindNearbyBoundBreakpoint(
            Debugger5 debugger,
            string filePath,
            int requestedLineNumber,
            string condition,
            string language,
            dbgBreakpointConditionType conditionType,
            int hitCount,
            dbgHitCountType hitCountType)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            int lineCount = GetFileLineCount(filePath);
            if (lineCount <= 0)
            {
                return null;
            }

            foreach (int candidateLine in EnumerateNearbyBreakpointLines(requestedLineNumber, lineCount))
            {
                var candidateBreakpoint = AddFileBreakpoint(
                    debugger,
                    filePath,
                    candidateLine,
                    condition,
                    language,
                    conditionType,
                    hitCount,
                    hitCountType);
                if (candidateBreakpoint == null)
                {
                    continue;
                }

                WaitForBreakpointBinding(debugger, candidateBreakpoint);
                if (GetBreakpointChildren(candidateBreakpoint).Count > 0)
                {
                    return candidateBreakpoint;
                }

                SafeDeleteBreakpoint(candidateBreakpoint);
            }

            return null;
        }

        private IEnumerable<int> EnumerateNearbyBreakpointLines(int requestedLineNumber, int lineCount)
        {
            if (requestedLineNumber < 1 || lineCount < 1)
            {
                yield break;
            }

            for (int distance = 1; distance <= BreakpointProbeMaxDistance; distance++)
            {
                int forwardLine = requestedLineNumber + distance;
                if (forwardLine >= 1 && forwardLine <= lineCount)
                {
                    yield return forwardLine;
                }

                int backwardLine = requestedLineNumber - distance;
                if (backwardLine >= 1 && backwardLine <= lineCount)
                {
                    yield return backwardLine;
                }
            }
        }

        private int GetFileLineCount(string filePath)
        {
            try
            {
                return System.IO.File.ReadAllLines(filePath).Length;
            }
            catch
            {
                return 0;
            }
        }

        private bool IsLikelyNonBindableBreakpointLine(string filePath, int lineNumber)
        {
            try
            {
                var lines = System.IO.File.ReadAllLines(filePath);
                if (lineNumber < 1 || lineNumber > lines.Length)
                {
                    return false;
                }

                string trimmedLine = (lines[lineNumber - 1] ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(trimmedLine))
                {
                    return true;
                }

                if (trimmedLine.StartsWith("//", StringComparison.Ordinal) ||
                    trimmedLine.StartsWith("/*", StringComparison.Ordinal) ||
                    trimmedLine.StartsWith("*", StringComparison.Ordinal) ||
                    trimmedLine.StartsWith("*/", StringComparison.Ordinal) ||
                    trimmedLine.StartsWith("#", StringComparison.Ordinal))
                {
                    return true;
                }

                if (trimmedLine == "{" || trimmedLine == "}" || trimmedLine == "{}" || trimmedLine == "};")
                {
                    return true;
                }

                string extension = System.IO.Path.GetExtension(filePath)?.ToLowerInvariant();
                if (extension == ".cs")
                {
                    if (trimmedLine.StartsWith("using ", StringComparison.Ordinal) &&
                        trimmedLine.EndsWith(";", StringComparison.Ordinal) &&
                        !trimmedLine.Contains("=") &&
                        !trimmedLine.Contains("("))
                    {
                        return true;
                    }

                    if (trimmedLine.StartsWith("namespace ", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                if ((extension == ".c" || extension == ".cc" || extension == ".cpp" || extension == ".cxx" ||
                     extension == ".h" || extension == ".hpp" || extension == ".hxx") &&
                    trimmedLine.StartsWith("using namespace ", StringComparison.Ordinal))
                {
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private (bool IsValid, string ErrorMessage) ValidateBreakpointLocation(string filePath, int lineNumber)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return (false, "File path is required");
            }

            if (!System.IO.File.Exists(filePath))
            {
                return (false, $"File not found: {filePath}");
            }

            int lineCount = GetFileLineCount(filePath);
            if (lineCount <= 0)
            {
                return (false, $"Cannot read file: {filePath}");
            }

            if (lineNumber < 1)
            {
                return (false, "Line number must be >= 1");
            }

            if (lineNumber > lineCount)
            {
                return (false, $"Line {lineNumber} is beyond end of file (file has {lineCount} lines)");
            }

            return (true, null);
        }

        private void SafeDeleteBreakpoint(EnvDTE.Breakpoint breakpoint)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (breakpoint == null)
            {
                return;
            }

            try
            {
                breakpoint.Delete();
            }
            catch (Exception ex)
            {
                _log($"[SafeDeleteBreakpoint] Failed to delete breakpoint: {ex.Message}");
            }
        }

        private string BuildSetBreakpointMessage(BreakpointInfo breakpointInfo, string requestedFilePath, int requestedLineNumber)
        {
            if (breakpointInfo == null)
            {
                return $"Breakpoint set at {requestedFilePath}:{requestedLineNumber}";
            }

            if (breakpointInfo.BindingStatus == "pending")
            {
                return $"Breakpoint created at {breakpointInfo.FilePath}:{breakpointInfo.LineNumber} and is pending binding";
            }

            if (breakpointInfo.IsBound &&
                !string.IsNullOrEmpty(breakpointInfo.ResolvedFilePath) &&
                breakpointInfo.ResolvedLineNumber > 0 &&
                (breakpointInfo.ResolvedLineNumber != requestedLineNumber ||
                 !string.Equals(breakpointInfo.ResolvedFilePath, requestedFilePath, StringComparison.OrdinalIgnoreCase)))
            {
                return $"Breakpoint requested at {requestedFilePath}:{requestedLineNumber} and bound to {breakpointInfo.ResolvedFilePath}:{breakpointInfo.ResolvedLineNumber}";
            }

            if (breakpointInfo.LineNumber != requestedLineNumber)
            {
                return $"Breakpoint moved from {requestedFilePath}:{requestedLineNumber} to {breakpointInfo.FilePath}:{breakpointInfo.LineNumber}";
            }

            return $"Breakpoint set at {breakpointInfo.FilePath}:{breakpointInfo.LineNumber}";
        }

        private string GetBreakpointTypeString(dbgBreakpointLocationType type)
        {
            switch (type)
            {
                case dbgBreakpointLocationType.dbgBreakpointLocationTypeFile: return "File";
                case dbgBreakpointLocationType.dbgBreakpointLocationTypeFunction: return "Function";
                case dbgBreakpointLocationType.dbgBreakpointLocationTypeAddress: return "Address";
                case dbgBreakpointLocationType.dbgBreakpointLocationTypeData: return "Data";
                default: return "Unknown";
            }
        }

        #endregion

        #region Registers

        /// <summary>
        /// Get CPU registers.
        /// </summary>
        public RegistersResult GetRegisters()
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return BuildRegistersResult(Debugger);
            });
        }

        private RegistersResult BuildRegistersResult(Debugger5 debugger)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new RegistersResult
            {
                Capability = "x86_x64_general_segment_sse_best_effort",
                AvailableCategories = new List<string> { "General Purpose", "Instruction Pointer", "Stack", "Flags", "Segment", "SSE" }
            };

            if (debugger == null || debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
            {
                result.Error = "Not in break mode";
                return result;
            }

            try
            {
                bool is64Bit = Is64BitProcess(debugger);
                result.Is64Bit = is64Bit;

                if (is64Bit)
                {
                    string[] regs64 = { "rax", "rbx", "rcx", "rdx", "rsi", "rdi", "rbp", "rsp", 
                                       "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15",
                                       "rip", "rflags" };
                    foreach (var reg in regs64)
                    {
                        var expr = debugger.GetExpression($"@{reg}");
                        if (expr != null && expr.IsValidValue)
                        {
                            result.Registers.Add(new RegisterInfo
                            {
                                Name = reg.ToUpper(),
                                Value = expr.Value,
                                HexValue = FormatAsHex(expr.Value),
                                Category = GetRegisterCategory(reg)
                            });
                        }
                    }
                }
                else
                {
                    string[] regs32 = { "eax", "ebx", "ecx", "edx", "esi", "edi", "ebp", "esp", "eip", "eflags" };
                    foreach (var reg in regs32)
                    {
                        var expr = debugger.GetExpression($"@{reg}");
                        if (expr != null && expr.IsValidValue)
                        {
                            result.Registers.Add(new RegisterInfo
                            {
                                Name = reg.ToUpper(),
                                Value = expr.Value,
                                HexValue = FormatAsHex(expr.Value),
                                Category = GetRegisterCategory(reg)
                            });
                        }
                    }
                }

                string[] segRegs = { "cs", "ds", "es", "fs", "gs", "ss" };
                foreach (var reg in segRegs)
                {
                    var expr = debugger.GetExpression($"@{reg}");
                    if (expr != null && expr.IsValidValue)
                    {
                        result.Registers.Add(new RegisterInfo
                        {
                            Name = reg.ToUpper(),
                            Value = expr.Value,
                            HexValue = FormatAsHex(expr.Value),
                            Category = "Segment"
                        });
                    }
                }

                for (int i = 0; i < 16; i++)
                {
                    var expr = debugger.GetExpression($"@xmm{i}");
                    if (expr != null && expr.IsValidValue)
                    {
                        result.Registers.Add(new RegisterInfo
                        {
                            Name = $"XMM{i}",
                            Value = expr.Value,
                            Category = "SSE"
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }

            return result;
        }

        private string GetRegisterCategory(string reg)
        {
            reg = reg.ToLower();
            if (reg.Contains("ip") || reg.Contains("rip") || reg.Contains("eip"))
                return "Instruction Pointer";
            if (reg.Contains("sp") || reg.Contains("bp"))
                return "Stack";
            if (reg.Contains("flags"))
                return "Flags";
            if (reg.StartsWith("r") && reg.Length <= 3)
                return "General Purpose";
            return "General Purpose";
        }

        private string FormatAsHex(string value)
        {
            // Try to format the value as hex
            if (value.StartsWith("0x") || value.StartsWith("0X"))
                return value;
            
            if (long.TryParse(value, out long num))
            {
                return $"0x{num:X}";
            }
            return value;
        }

        #endregion

        #region Watch

        /// <summary>
        /// Add a watch expression.
        /// </summary>
        public WatchResult AddWatch(string expression)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new WatchResult
                {
                    Expression = expression,
                    Behavior = "persisted_watch"
                };

                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    result.Error = "Not in break mode";
                    return result;
                }

                try
                {
                    var expr = debugger.GetExpression(expression);
                    result.Value = expr.Value;
                    result.Type = expr.Type;
                    result.IsValid = expr.IsValidValue;
                    
                    if (!result.IsValid)
                    {
                        result.Error = expr.Value; // Error message is in Value when invalid
                    }

                    // Add to watch window
                    _dte.ExecuteCommand("Debug.AddWatch", expression);
                }
                catch (Exception ex)
                {
                    result.Error = ex.Message;
                    result.IsValid = false;
                }

                return result;
            });
        }

        #endregion

        #region Memory Search

        /// <summary>
        /// Search for a pattern in memory using optimized batch reading.
        /// Performance: Reads memory in chunks instead of byte-by-byte, reducing IPC calls from O(n) to O(n/4).
        /// Automatically handles thread context issues by trying to switch to user code thread if needed.
        /// </summary>
        public MemorySearchResult SearchMemory(string startAddress, string pattern, int maxResults = 100, int searchSize = 0x10000, string patternFormat = "auto", long startOffset = 0)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var bestEffort = CreateUnavailableMemorySearchResult(pattern, searchSize);

                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    return bestEffort;
                }

                for (int attempt = 1; attempt <= SnapshotMaxAttempts; attempt++)
                {
                    if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                    {
                        bestEffort.Status = "unavailable";
                        return bestEffort;
                    }

                    try
                    {
                        string tokenBefore = CreateDebuggerSnapshotToken(debugger);
                        var snapshot = ExecuteSearchMemoryWithThreadFallback(
                            debugger,
                            startAddress,
                            pattern,
                            maxResults,
                            searchSize,
                            patternFormat,
                            startOffset);
                        string tokenAfter = CreateDebuggerSnapshotToken(debugger);
                        bool stable = !string.IsNullOrEmpty(tokenBefore) && tokenBefore == tokenAfter;

                        if (stable)
                        {
                            return snapshot;
                        }

                        bestEffort = snapshot;
                        if (bestEffort.BytesSearched > 0)
                        {
                            bestEffort.Status = "unstable";
                        }
                    }
                    catch (Exception ex)
                    {
                        _log($"SearchMemory snapshot attempt {attempt} failed: {ex.Message}");
                    }

                    if (attempt < SnapshotMaxAttempts)
                    {
                        System.Threading.Thread.Sleep(SnapshotRetryDelayMs);
                    }
                }

                return bestEffort;
            });
        }

        private MemorySearchResult CreateUnavailableMemorySearchResult(string pattern, int searchSize)
        {
            return new MemorySearchResult
            {
                Pattern = pattern,
                Status = "unavailable",
                RequestedBytes = Math.Max(1, Math.Min(searchSize, 0x100000)),
                FailedReads = Math.Max(1, Math.Min(searchSize, 0x100000))
            };
        }

        private MemorySearchResult ExecuteSearchMemoryWithThreadFallback(
            Debugger5 debugger,
            string startAddress,
            string pattern,
            int maxResults,
            int searchSize,
            string patternFormat,
            long startOffset)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = CreateUnavailableMemorySearchResult(pattern, searchSize);

            // Save current thread in case we need to restore.
            int originalThreadId = debugger.CurrentThread?.ID ?? 0;
            bool switchedThread = false;

            try
            {
                result = ExecuteSearchMemory(debugger, startAddress, pattern, maxResults, searchSize, patternFormat, startOffset, result);

                // If first attempt could not obtain bytes, try switching to a user-code thread once.
                if (result.BytesSearched == 0)
                {
                    var userThread = FindUserCodeThread(debugger);
                    if (userThread != null && userThread.ID != originalThreadId)
                    {
                        _log($"SearchMemory: Switching from thread {originalThreadId} to user code thread {userThread.ID}");
                        debugger.CurrentThread = userThread;
                        switchedThread = true;

                        result = ExecuteSearchMemory(
                            debugger,
                            startAddress,
                            pattern,
                            maxResults,
                            searchSize,
                            patternFormat,
                            startOffset,
                            CreateUnavailableMemorySearchResult(pattern, searchSize));
                    }
                }
            }
            finally
            {
                if (switchedThread && originalThreadId > 0)
                {
                    try
                    {
                        foreach (EnvDTE.Thread thread in debugger.CurrentProgram.Threads)
                        {
                            if (thread.ID == originalThreadId)
                            {
                                debugger.CurrentThread = thread;
                                break;
                            }
                        }
                    }
                    catch (Exception restoreEx)
                    {
                        _log($"Failed to restore original thread: {restoreEx.Message}");
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Internal method to execute memory search.
        /// </summary>
        private MemorySearchResult ExecuteSearchMemory(Debugger5 debugger, string startAddress, string pattern, int maxResults, int searchSize, string patternFormat, long startOffset, MemorySearchResult result)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            int requestedBytes = Math.Max(1, Math.Min(searchSize, 0x100000));
            result.Pattern = pattern;
            result.RequestedBytes = requestedBytes;
            result.Matches.Clear();
            result.TotalFound = 0;
            result.BytesSearched = 0;
            result.FailedReads = requestedBytes;
            result.Partial = false;
            result.Status = "unavailable";

            if (string.IsNullOrWhiteSpace(pattern))
            {
                result.Error = "Pattern cannot be empty";
                return result;
            }

            // Parse pattern (hex bytes like "4D 5A" or string)
            byte[] patternBytes;
            string normalizedPatternFormat = (patternFormat ?? "auto").Trim().ToLowerInvariant();
            bool forceHex = normalizedPatternFormat == "hex" || normalizedPatternFormat == "bytes";
            bool forceText = normalizedPatternFormat == "text" || normalizedPatternFormat == "ascii" || normalizedPatternFormat == "string";
            bool looksLikeSpacedHex = pattern.Contains(" ") &&
                pattern.Replace(" ", "").All(c => "0123456789ABCDEFabcdef".Contains(c));

            if (forceHex || (!forceText && looksLikeSpacedHex))
            {
                // Hex bytes. In auto mode this requires spaces, so "41" remains literal ASCII text.
                var hexBytes = pattern.Replace(" ", "").Trim();
                if (hexBytes.Length % 2 != 0)
                {
                    result.Error = "Hex pattern must contain complete byte pairs";
                    return result;
                }
                patternBytes = Enumerable.Range(0, hexBytes.Length / 2)
                    .Select(i => Convert.ToByte(hexBytes.Substring(i * 2, 2), 16))
                    .ToArray();
            }
            else
            {
                // String pattern (ASCII): treat non-space-separated input as literal text.
                patternBytes = System.Text.Encoding.ASCII.GetBytes(pattern);
            }

            if (patternBytes.Length == 0)
            {
                return result;
            }

            result.PatternBytes = patternBytes.Select(b => b.ToString("X2")).ToArray();

            // Get start address
            var addrExpr = debugger.GetExpression(startAddress);
            if (addrExpr == null || !addrExpr.IsValidValue)
            {
                return result;
            }

            ulong addr = ParseAddress(addrExpr.Value);
            ulong byteOffset = startOffset > 0 ? (ulong)startOffset : 0UL;
            if (byteOffset > 0)
            {
                if (addr > ulong.MaxValue - byteOffset)
                {
                    result.Error = "Start offset overflows resolved address";
                    return result;
                }
                addr += byteOffset;
            }

            byte[] memoryBlock = ReadProcessMemoryContiguous(debugger, addr, requestedBytes);
            if ((memoryBlock == null || memoryBlock.Length == 0) && requestedBytes > 0)
            {
                memoryBlock = ReadMemoryBlockOptimized(debugger, addr, requestedBytes);
            }

            if (memoryBlock == null || memoryBlock.Length == 0)
            {
                result.FailedReads = requestedBytes;
                return result;
            }

            result.BytesSearched = memoryBlock.Length;
            result.FailedReads = Math.Max(0, requestedBytes - memoryBlock.Length);
            result.Partial = result.FailedReads > 0;
            result.Status = result.Partial ? "partial" : "ok";

            // Search for pattern in the local memory block
            int found = 0;
            int searchLimit = memoryBlock.Length - patternBytes.Length + 1;
            int nextSearchOffset = -1;

            // Determine if 64-bit once, not in the loop
            var ripExpr = debugger.GetExpression("@rip");
            bool is64Bit = ripExpr != null && ripExpr.IsValidValue;

            for (int i = 0; i < searchLimit; i++)
            {
                bool match = true;

                // Check if pattern matches at this position
                for (int j = 0; j < patternBytes.Length; j++)
                {
                    if (memoryBlock[i + j] != patternBytes[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    string matchAddr = is64Bit
                        ? $"0x{addr + (ulong)i:X16}"
                        : $"0x{addr + (ulong)i:X8}";

                    result.Matches.Add(matchAddr);
                    found++;
                    if (found >= maxResults)
                    {
                        nextSearchOffset = i + 1;
                        break;
                    }
                }
            }

            result.TotalFound = found;
            if (nextSearchOffset >= 0 && nextSearchOffset < searchLimit)
            {
                ulong nextAddress = addr + (ulong)nextSearchOffset;
                result.Truncated = true;
                result.Partial = true;
                result.Status = "partial";
                result.NextStartAddress = is64Bit ? $"0x{nextAddress:X16}" : $"0x{nextAddress:X8}";
                result.Error = "Search stopped after reaching maxResults";
            }

            return result;
        }

        #endregion

        #region Output Window

        /// <summary>
        /// Get content from a Visual Studio Output Window pane.
        /// </summary>
        /// <param name="pane">The name of the output pane (e.g., "Debug", "Build", "General"). If empty, defaults to "Debug".</param>
        /// <param name="tailLines">Number of lines to return from the end. 0 means all lines.</param>
        public OutputWindowResult GetOutputWindow(string pane = "Debug", int tailLines = 50)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return BuildOutputWindowResult(pane, tailLines);
            });
        }

        private OutputWindowResult BuildOutputWindowResult(string pane, int tailLines)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new OutputWindowResult
            {
                Pane = pane,
                Capability = "multi_pane_output",
                RequestedLines = tailLines
            };

            try
            {
                var outputWindow = _dte.ToolWindows.OutputWindow;
                if (outputWindow == null)
                {
                    result.Error = "Output window not available";
                    return result;
                }

                foreach (OutputWindowPane p in outputWindow.OutputWindowPanes)
                {
                    try
                    {
                        result.AvailablePanes.Add(p.Name);
                    }
                    catch { }
                }

                OutputWindowPane targetPane = null;
                try
                {
                    targetPane = outputWindow.OutputWindowPanes.Item(pane);
                }
                catch
                {
                    foreach (OutputWindowPane p in outputWindow.OutputWindowPanes)
                    {
                        try
                        {
                            if (p.Name.Equals(pane, StringComparison.OrdinalIgnoreCase))
                            {
                                targetPane = p;
                                break;
                            }
                        }
                        catch { }
                    }
                }

                if (targetPane == null)
                {
                    result.Error = $"Output pane '{pane}' not found. Available panes: {string.Join(", ", result.AvailablePanes)}";
                    return result;
                }

                result.Pane = targetPane.Name;
                var textDocument = targetPane.TextDocument;
                if (textDocument == null)
                {
                    result.Error = "Could not access output pane text";
                    return result;
                }

                var startPoint = textDocument.StartPoint.CreateEditPoint();
                var endPoint = textDocument.EndPoint.CreateEditPoint();
                string fullContent = startPoint.GetText(endPoint);

                if (string.IsNullOrEmpty(fullContent))
                {
                    result.Content = "";
                    result.LineCount = 0;
                    result.Truncated = false;
                    return result;
                }

                var lines = fullContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                result.LineCount = lines.Length;

                if (tailLines > 0 && lines.Length > tailLines)
                {
                    var tailArray = new string[tailLines];
                    Array.Copy(lines, lines.Length - tailLines, tailArray, 0, tailLines);
                    result.Content = string.Join("\n", tailArray);
                    result.Truncated = true;
                }
                else
                {
                    result.Content = fullContent;
                    result.Truncated = false;
                }
            }
            catch (Exception ex)
            {
                _log($"GetOutputWindow error: {ex.Message}");
                result.Error = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Search for a pattern in a Visual Studio Output Window pane.
        /// </summary>
        /// <param name="pattern">The pattern to search for (string or regex)</param>
        /// <param name="pane">The name of the output pane</param>
        /// <param name="isRegex">Whether the pattern is a regular expression</param>
        /// <param name="contextLines">Number of context lines before and after each match</param>
        /// <param name="maxMatches">Maximum number of matches to return</param>
        public SearchOutputResult SearchOutput(string pattern, string pane = "Debug", bool isRegex = false, int contextLines = 2, int maxMatches = 20)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var result = new SearchOutputResult
                {
                    Pane = pane,
                    Capability = "multi_pane_output",
                    Pattern = pattern,
                    IsRegex = isRegex
                };

                try
                {
                    if (string.IsNullOrEmpty(pattern))
                    {
                        result.Error = "Pattern cannot be empty";
                        return result;
                    }

                    // Get the Output window
                    var outputWindow = _dte.ToolWindows.OutputWindow;
                    if (outputWindow == null)
                    {
                        result.Error = "Output window not available";
                        return result;
                    }

                    // Find the requested pane
                    OutputWindowPane targetPane = null;
                    try
                    {
                        targetPane = outputWindow.OutputWindowPanes.Item(pane);
                    }
                    catch
                    {
                        foreach (OutputWindowPane p in outputWindow.OutputWindowPanes)
                        {
                            try
                            {
                                if (p.Name.Equals(pane, StringComparison.OrdinalIgnoreCase))
                                {
                                    targetPane = p;
                                    break;
                                }
                            }
                            catch { }
                        }
                    }

                    if (targetPane == null)
                    {
                        result.Error = $"Output pane '{pane}' not found";
                        return result;
                    }

                    result.Pane = targetPane.Name;

                    // Get the text
                    var textDocument = targetPane.TextDocument;
                    if (textDocument == null)
                    {
                        result.Error = "Could not access output pane text";
                        return result;
                    }

                    var startPoint = textDocument.StartPoint.CreateEditPoint();
                    var endPoint = textDocument.EndPoint.CreateEditPoint();
                    string fullContent = startPoint.GetText(endPoint);

                    if (string.IsNullOrEmpty(fullContent))
                    {
                        result.TotalMatches = 0;
                        return result;
                    }

                    // Split into lines
                    var lines = fullContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

                    // Prepare regex or simple matcher
                    Regex regex = null;
                    if (isRegex)
                    {
                        try
                        {
                            regex = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));
                        }
                        catch (Exception ex)
                        {
                            result.Error = $"Invalid regex pattern: {ex.Message}";
                            return result;
                        }
                    }

                    // Search for matches
                    int totalMatches = 0;
                    for (int i = 0; i < lines.Length; i++)
                    {
                        bool isMatch = false;
                        if (isRegex)
                        {
                            isMatch = regex.IsMatch(lines[i]);
                        }
                        else
                        {
                            isMatch = lines[i].IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
                        }

                        if (isMatch)
                        {
                            totalMatches++;

                            if (result.Matches.Count < maxMatches)
                            {
                                var match = new OutputMatch
                                {
                                    LineNumber = i + 1, // 1-based line number
                                    Line = lines[i]
                                };

                                // Add context lines
                                int contextStart = Math.Max(0, i - contextLines);
                                int contextEnd = Math.Min(lines.Length - 1, i + contextLines);
                                for (int j = contextStart; j <= contextEnd; j++)
                                {
                                    if (j != i) // Don't include the match line in context
                                    {
                                        match.Context.Add($"L{j + 1}: {lines[j]}");
                                    }
                                }

                                result.Matches.Add(match);
                            }
                        }
                    }

                    result.TotalMatches = totalMatches;
                    result.Truncated = totalMatches > maxMatches;
                }
                catch (Exception ex)
                {
                    _log($"SearchOutput error: {ex.Message}");
                    result.Error = ex.Message;
                }

                return result;
            });
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
                            debugger.CurrentStackFrame = frame;
                            _selectedStackFrameIndex = frameIndex;
                            result.Success = true;
                            result.Message = $"Switched to frame {frameIndex}: {frame.FunctionName}";
                            result.NewLocation = new LocationInfo
                            {
                                FunctionName = frame.FunctionName,
                                FilePath = GetFrameFileName(frame),
                                LineNumber = GetFrameLineNumber(frame)
                            };
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
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Switch to a specific thread.
        /// </summary>
        public DebugControlResult SwitchThread(int threadId)
        {
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
                    bool found = false;
                    foreach (EnvDTE.Thread thread in debugger.CurrentProgram.Threads)
                    {
                        if (thread.ID == threadId)
                        {
                            debugger.CurrentThread = thread;
                            _selectedStackFrameIndex = 0;
                            found = true;
                            result.Success = true;
                            result.Message = $"Switched to thread {threadId}";
                            
                            if (thread.StackFrames.Count > 0)
                            {
                                var topFrame = thread.StackFrames.Item(1) as EnvDTE.StackFrame;
                                if (topFrame != null)
                                {
                                    result.NewLocation = new LocationInfo
                                    {
                                        FunctionName = topFrame.FunctionName,
                                        FilePath = GetFrameFileName(topFrame),
                                        LineNumber = GetFrameLineNumber(topFrame)
                                    };
                                }
                            }
                            break;
                        }
                    }

                    if (!found)
                    {
                        result.Success = false;
                        result.Message = $"Thread {threadId} not found";
                    }
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Gets the file name from a stack frame safely - IMPROVED VERSION.
        /// Handles both native and managed code.
        /// </summary>
        private string GetFrameFileName(EnvDTE.StackFrame frame, int frameIndex = -1)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (IsSyntheticExecutionPointFrame(frame))
                {
                    return frame.Module ?? "";
                }

                string language = frame.Language ?? "";
                bool isManagedCode = language == "C#" || language == "VB" || language == "F#" || language == "Basic";
                bool isCurrentOrSelectedFrame = IsCurrentOrSelectedStackFrame(frame, frameIndex);
                
                // Method 1: For managed code, try to find source file through solution search
                if (isManagedCode)
                {
                    try
                    {
                        // Try to find source file by class name (more reliable than ActiveDocument)
                        var parts = ParseFunctionName(frame.FunctionName);
                        if (!string.IsNullOrEmpty(parts.TypeName) || !string.IsNullOrEmpty(parts.ClassName))
                        {
                            var filePath = FindSourceFileByClassName(parts.TypeName, parts.ClassName);
                            if (!string.IsNullOrEmpty(filePath))
                            {
                                return filePath;
                            }
                        }

                        // Fallback: use ActiveDocument only if this is the current frame 
                        // and the document matches the function name
                        if (isCurrentOrSelectedFrame)
                        {
                            if (TryGetActiveSourceDocumentPath(out string activeSourcePath))
                            {
                                return activeSourcePath;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _log($"Error finding managed source file: {ex.Message}");
                    }
                }

                // Method 2: For native code, try __FILE__ macro
                if (language == "C++" || language == "C" || !isManagedCode)
                {
                    // Native dump debugging can expose the active source document in VS while
                    // EnvDTE stack frames still report only the module path.
                    if (isCurrentOrSelectedFrame && TryGetActiveSourceDocumentPath(out string activeSourcePath))
                    {
                        return activeSourcePath;
                    }

                    try
                    {
                        var debugger = Debugger;
                        if (debugger != null)
                        {
                            var fileExpr = debugger.GetExpression("__FILE__");
                            if (fileExpr != null && fileExpr.IsValidValue)
                            {
                                string filePath = fileExpr.Value.Trim('"');
                                if (!string.IsNullOrEmpty(filePath) && System.IO.File.Exists(filePath))
                                {
                                    return filePath;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _log($"[GetFrameFileName] Error using __FILE__ macro: {ex.Message}");
                    }
                }

                // Method 3: Try to get from frame's module path and search for corresponding source
                try
                {
                    string modulePath = frame.Module;
                    if (!string.IsNullOrEmpty(modulePath))
                    {
                        // For .NET assemblies, try to find matching source file in solution
                        string moduleDir = System.IO.Path.GetDirectoryName(modulePath);
                        string moduleName = System.IO.Path.GetFileNameWithoutExtension(modulePath);
                        
                        // Check if there's a PDB file nearby
                        string pdbPath = System.IO.Path.ChangeExtension(modulePath, ".pdb");
                        if (System.IO.File.Exists(pdbPath))
                        {
                            // PDB exists, VS should be able to find source
                            _log($"PDB found at {pdbPath}, source lookup may succeed");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log($"[GetFrameFileName] Error checking module path: {ex.Message}");
                }

                // Fallback: Return module name
                return frame.Module ?? "";
            }
            catch (Exception ex)
            {
                _log($"Error getting frame file name: {ex.Message}");
                return "";
            }
        }

        /// <summary>
        /// Gets the line number from a stack frame safely - IMPROVED VERSION.
        /// Returns a tuple with line number and whether it's reliable (from the current frame).
        /// </summary>
        private (int LineNumber, bool IsReliable) GetFrameLineNumberEx(EnvDTE.StackFrame frame, int frameIndex = -1)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (IsSyntheticExecutionPointFrame(frame))
                {
                    return (0, false);
                }

                var debugger = Debugger;
                bool isCurrentFrame = IsCurrentOrSelectedStackFrame(frame, frameIndex);
                string language = frame.Language ?? "";
                bool isManagedCode = language == "C#" || language == "VB" || language == "F#" || language == "Basic";

                // Method 1: For managed code current frame, use the debugger's current statement position
                if (isManagedCode && isCurrentFrame)
                {
                    try
                    {
                        // Use DTE's debugger to get the actual execution line, not the cursor position
                        if (debugger != null)
                        {
                            // The debugger.CurrentStackFrame is the authoritative source for the execution line.
                            // We attempt to read it via the context below (Method 3 fallback).
                            // ActiveDocument cursor position is NOT reliable 鈥?user may have scrolled/clicked elsewhere.
                        }
                    }
                    catch (Exception ex)
                    {
                        _log($"[GetFrameLineNumberEx] Error using active document: {ex.Message}");
                    }
                }

                // Method 2: For native code current frame, try __LINE__ macro
                if ((language == "C++" || language == "C") && isCurrentFrame)
                {
                    try
                    {
                        if (debugger != null)
                        {
                            var lineExpr = debugger.GetExpression("__LINE__");
                            if (lineExpr != null && lineExpr.IsValidValue)
                            {
                                if (int.TryParse(lineExpr.Value, out int lineNum) && lineNum > 0)
                                {
                                    return (lineNum, true);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _log($"[GetFrameLineNumberEx] Error using __LINE__ macro: {ex.Message}");
                    }
                }

                // Native dump frames often reject __LINE__ even though VS has opened the exact
                // source location. Use the active editor position only for the current frame.
                if (isCurrentFrame && TryGetActiveSourceLineNumber(out int activeLineNumber))
                {
                    return (activeLineNumber, true);
                }

                // Method 3: For non-current frames, try to find open document for this file
                // and check if there's debug info available
                try
                {
                    string filePath = GetFrameFileName(frame);
                    if (!string.IsNullOrEmpty(filePath) && System.IO.File.Exists(filePath))
                    {
                        // Check if this file is currently open in an editor
                        foreach (Document doc in _dte.Documents)
                        {
                            try
                            {
                                if (doc.FullName.Equals(filePath, StringComparison.OrdinalIgnoreCase))
                                {
                                    // File is open but we can't reliably get line for non-current frames
                                    // Return 0 with unreliable flag
                                    break;
                                }
                            }
                            catch (Exception ex)
                            {
                                _log($"[GetFrameLineNumberEx] Error checking document: {ex.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log($"[GetFrameLineNumberEx] Error checking open documents: {ex.Message}");
                }

                // For non-current frames, we cannot reliably determine line number
                // without using lower-level debugging APIs (IDebugStackFrame2, etc.)
                return (0, false);
            }
            catch (Exception ex)
            {
                _log($"[GetFrameLineNumberEx] Unexpected error: {ex.Message}");
                return (0, false);
            }
        }

        /// <summary>
        /// Gets the line number from a stack frame safely - simple version for backward compatibility.
        /// </summary>
        private int GetFrameLineNumber(EnvDTE.StackFrame frame)
        {
            return GetFrameLineNumberEx(frame).LineNumber;
        }

        private bool IsCurrentOrSelectedStackFrame(EnvDTE.StackFrame frame, int frameIndex)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (IsSyntheticExecutionPointFrame(frame))
            {
                return false;
            }

            if (frameIndex >= 0 && frameIndex == _selectedStackFrameIndex)
            {
                return true;
            }

            try
            {
                var debugger = Debugger;
                if (debugger?.CurrentStackFrame == null)
                {
                    return false;
                }

                var currentFrame = debugger.CurrentStackFrame;
                if (currentFrame == frame)
                {
                    return true;
                }

                return string.Equals(currentFrame.FunctionName, frame.FunctionName, StringComparison.Ordinal) &&
                       string.Equals(currentFrame.Module, frame.Module, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(currentFrame.Language, frame.Language, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private bool IsSyntheticExecutionPointFrame(EnvDTE.StackFrame frame)
        {
            try
            {
                return (frame?.FunctionName ?? string.Empty).StartsWith("[Execution point when dump was captured", StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private bool TryGetActiveSourceDocumentPath(out string filePath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            filePath = null;
            try
            {
                var activeDoc = _dte.ActiveDocument;
                if (activeDoc == null)
                {
                    return false;
                }

                var fullName = activeDoc.FullName;
                if (IsSourceFilePath(fullName) && System.IO.File.Exists(fullName))
                {
                    filePath = fullName;
                    return true;
                }
            }
            catch (Exception ex)
            {
                _log($"[TryGetActiveSourceDocumentPath] Error reading active document: {ex.Message}");
            }

            return false;
        }

        private bool TryGetActiveSourceLineNumber(out int lineNumber)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            lineNumber = 0;
            try
            {
                if (!TryGetActiveSourceDocumentPath(out _))
                {
                    return false;
                }

                var selection = _dte.ActiveDocument?.Selection as TextSelection;
                var line = selection?.ActivePoint?.Line ?? 0;
                if (line > 0)
                {
                    lineNumber = line;
                    return true;
                }
            }
            catch (Exception ex)
            {
                _log($"[TryGetActiveSourceLineNumber] Error reading active source line: {ex.Message}");
            }

            return false;
        }

        private bool IsSourceFilePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string extension;
            try
            {
                extension = System.IO.Path.GetExtension(path);
            }
            catch
            {
                return false;
            }

            switch ((extension ?? string.Empty).ToLowerInvariant())
            {
                case ".c":
                case ".cc":
                case ".cpp":
                case ".cxx":
                case ".h":
                case ".hh":
                case ".hpp":
                case ".hxx":
                case ".inl":
                case ".ipp":
                case ".cs":
                case ".vb":
                case ".fs":
                case ".usf":
                case ".ush":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Parse function name to extract class and method.
        /// Handles C# style (Namespace.Class.Method) and C++ style (Namespace::Class::Method)
        /// </summary>
        private (string TypeName, string ClassName, string MethodName) ParseFunctionName(string functionName)
        {
            if (string.IsNullOrEmpty(functionName))
                return (null, null, null);

            // Remove generic type parameters using stack-based approach to handle nested generics
            // e.g., "Dictionary<string, List<int>>" -> "Dictionary"
            functionName = RemoveGenericParameters(functionName);

            // Remove parameter list if present (also handle nested parentheses)
            functionName = RemoveMethodParameters(functionName);

            // Handle C# style: Namespace.Class.Method or Class.Method
            // Handle C++ style: Namespace::Class::Method
            string separator = functionName.Contains("::") ? "::" : ".";
            var parts = functionName.Split(new[] { separator }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 2)
            {
                var className = parts[parts.Length - 2].Trim();
                var methodName = parts[parts.Length - 1].Trim();
                var typeName = string.Join(".", parts.Take(parts.Length - 1)).Trim();
                return (
                    string.IsNullOrEmpty(typeName) ? null : typeName,
                    string.IsNullOrEmpty(className) ? null : className,
                    string.IsNullOrEmpty(methodName) ? null : methodName
                );
            }

            var singleName = parts.Length > 0 ? parts[0].Trim() : null;
            if (string.IsNullOrEmpty(singleName))
            {
                return (null, null, null);
            }

            return (singleName, singleName, null);
        }

        /// <summary>
        /// Remove generic type parameters from a string, handling nested generics.
        /// e.g., "Dictionary&lt;string, List&lt;int&gt;&gt;" becomes "Dictionary"
        /// </summary>
        private string RemoveGenericParameters(string input)
        {
            if (string.IsNullOrEmpty(input) || !input.Contains("<"))
                return input;

            var result = new StringBuilder();
            int depth = 0;

            foreach (char c in input)
            {
                if (c == '<')
                {
                    depth++;
                }
                else if (c == '>')
                {
                    depth--;
                }
                else if (depth == 0)
                {
                    result.Append(c);
                }
            }

            return result.ToString();
        }

        /// <summary>
        /// Remove method parameters from a string, handling nested parentheses.
        /// e.g., "Method(int x, Func&lt;int, int&gt;)" becomes "Method"
        /// </summary>
        private string RemoveMethodParameters(string input)
        {
            if (string.IsNullOrEmpty(input) || !input.Contains("("))
                return input;

            int parenIndex = input.IndexOf('(');
            if (parenIndex <= 0)
                return input;

            // Just take everything before the first '('
            return input.Substring(0, parenIndex).Trim();
        }

        /// <summary>
        /// Find source file by searching solution for class definition.
        /// </summary>
        private string FindSourceFileByClassName(string typeName, string className)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var normalizedTypeName = NormalizeTypeLookupName(typeName);
            var normalizedClassName = NormalizeTypeLookupName(className);
            if (string.IsNullOrEmpty(normalizedClassName) && !string.IsNullOrEmpty(normalizedTypeName))
            {
                var lastSegment = normalizedTypeName.Split('.').LastOrDefault();
                normalizedClassName = NormalizeTypeLookupName(lastSegment);
            }

            var cacheKeys = BuildSourceLookupKeys(normalizedTypeName, normalizedClassName);
            if (cacheKeys.Count == 0)
            {
                return null;
            }

            try
            {
                if (_dte.Solution == null)
                {
                    return null;
                }

                EnsureSourceLookupCacheFresh();

                foreach (var key in cacheKeys)
                {
                    if (TryGetSourceLookupHit(key, out string cachedPath))
                    {
                        return cachedPath;
                    }
                }

                if (cacheKeys.All(IsSourceLookupMissCached))
                {
                    return null;
                }

                foreach (Project project in _dte.Solution.Projects)
                {
                    try
                    {
                        var filePath = SearchProjectForClass(project.ProjectItems, normalizedTypeName, normalizedClassName);
                        if (!string.IsNullOrEmpty(filePath))
                        {
                            RecordSourceLookupHit(cacheKeys, filePath);
                            return filePath;
                        }
                    }
                    catch (Exception ex)
                    {
                        _log($"[FindSourceFileByClassName] Error searching project '{project?.Name}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _log($"Error finding source file by class name: {ex.Message}");
            }

            RecordSourceLookupMiss(cacheKeys);
            return null;
        }

        private List<string> BuildSourceLookupKeys(string typeName, string className)
        {
            var keys = new List<string>();
            if (!string.IsNullOrEmpty(typeName))
            {
                keys.Add(typeName);
            }

            if (!string.IsNullOrEmpty(className) &&
                !keys.Any(k => string.Equals(k, className, StringComparison.OrdinalIgnoreCase)))
            {
                keys.Add(className);
            }

            return keys;
        }

        private void EnsureSourceLookupCacheFresh()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string solutionKey = _dte.Solution?.FullName ?? string.Empty;
            if (!string.Equals(solutionKey, _sourceLookupSolutionKey, StringComparison.OrdinalIgnoreCase))
            {
                _sourceLookupSolutionKey = solutionKey;
                _sourceFileByClassCache.Clear();
                _sourceFileByClassMissCache.Clear();
                return;
            }

            PruneSourceLookupMissCache();
        }

        private void PruneSourceLookupMissCache()
        {
            var now = DateTime.UtcNow;
            var expiredKeys = _sourceFileByClassMissCache
                .Where(entry => entry.Value <= now)
                .Select(entry => entry.Key)
                .ToList();

            foreach (var expiredKey in expiredKeys)
            {
                _sourceFileByClassMissCache.Remove(expiredKey);
            }
        }

        private bool TryGetSourceLookupHit(string cacheKey, out string cachedPath)
        {
            cachedPath = null;
            if (string.IsNullOrEmpty(cacheKey))
            {
                return false;
            }

            if (_sourceFileByClassCache.TryGetValue(cacheKey, out string path))
            {
                if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                {
                    cachedPath = path;
                    return true;
                }

                _sourceFileByClassCache.Remove(cacheKey);
            }

            return false;
        }

        private bool IsSourceLookupMissCached(string cacheKey)
        {
            if (string.IsNullOrEmpty(cacheKey))
            {
                return false;
            }

            if (_sourceFileByClassMissCache.TryGetValue(cacheKey, out DateTime expiresAt))
            {
                if (expiresAt > DateTime.UtcNow)
                {
                    return true;
                }

                _sourceFileByClassMissCache.Remove(cacheKey);
            }

            return false;
        }

        private void RecordSourceLookupHit(IEnumerable<string> cacheKeys, string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return;
            }

            foreach (var key in cacheKeys)
            {
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                if (_sourceFileByClassCache.Count >= SourceLookupCacheMaxEntries &&
                    !_sourceFileByClassCache.ContainsKey(key))
                {
                    _sourceFileByClassCache.Clear();
                }

                _sourceFileByClassCache[key] = filePath;
                _sourceFileByClassMissCache.Remove(key);
            }
        }

        private void RecordSourceLookupMiss(IEnumerable<string> cacheKeys)
        {
            var expiresAt = DateTime.UtcNow.Add(SourceLookupMissCacheTtl);
            foreach (var key in cacheKeys)
            {
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                if (_sourceFileByClassMissCache.Count >= SourceLookupCacheMaxEntries &&
                    !_sourceFileByClassMissCache.ContainsKey(key))
                {
                    PruneSourceLookupMissCache();
                    if (_sourceFileByClassMissCache.Count >= SourceLookupCacheMaxEntries)
                    {
                        _sourceFileByClassMissCache.Clear();
                    }
                }

                _sourceFileByClassMissCache[key] = expiresAt;
            }
        }

        private string NormalizeTypeLookupName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var normalized = name.Trim();
            if (normalized.StartsWith("global::", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring("global::".Length);
            }

            normalized = normalized.Replace("::", ".").Replace("+", ".");
            normalized = normalized.Trim('.');
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        /// <summary>
        /// Search project items recursively for a class definition.
        /// </summary>
        private string SearchProjectForClass(ProjectItems items, string typeName, string className)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (items == null) return null;

            foreach (ProjectItem item in items)
            {
                try
                {
                    // Check if this is a code file
                    if (item.FileCodeModel != null)
                    {
                        if (ContainsClass(item.FileCodeModel.CodeElements, typeName, className))
                        {
                            try
                            {
                                return item.FileNames[1]; // 1-indexed
                            }
                            catch
                            {
                                return item.FileNames[0];
                            }
                        }
                    }

                    // Recurse into nested items (folders, etc.)
                    if (item.ProjectItems != null && item.ProjectItems.Count > 0)
                    {
                        var result = SearchProjectForClass(item.ProjectItems, typeName, className);
                        if (!string.IsNullOrEmpty(result))
                        {
                            return result;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log($"[SearchProjectForClass] Error processing item: {ex.Message}");
                }
            }
            return null;
        }

        /// <summary>
        /// Check if code elements contain a class with the specified name.
        /// </summary>
        private bool ContainsClass(CodeElements elements, string typeName, string className)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (elements == null) return false;

            try
            {
                foreach (CodeElement element in elements)
                {
                    try
                    {
                        bool isType = element.Kind == vsCMElement.vsCMElementClass ||
                                      element.Kind == vsCMElement.vsCMElementStruct;
                        if (isType && MatchesCodeElementType(element, typeName, className))
                        {
                            return true;
                        }

                        if (isType)
                        {
                            var nestedMembers = SafeReadCodeElements(() => element.Children);
                            if (nestedMembers != null && ContainsClass(nestedMembers, typeName, className))
                            {
                                return true;
                            }
                        }

                        if (element.Kind == vsCMElement.vsCMElementNamespace)
                        {
                            var ns = element as CodeNamespace;
                            if (ns != null && ContainsClass(ns.Members, typeName, className))
                            {
                                return true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _log($"[ContainsClass] Error checking element: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _log($"[ContainsClass] Error iterating elements: {ex.Message}");
            }
            return false;
        }

        private bool MatchesCodeElementType(CodeElement element, string targetTypeName, string targetClassName)
        {
            string elementName = NormalizeTypeLookupName(SafeReadCodeElementName(() => element.Name));
            if (!string.IsNullOrEmpty(targetClassName) &&
                string.Equals(elementName, targetClassName, StringComparison.OrdinalIgnoreCase))
            {
                // If caller only knows the short class name, this is enough.
                if (string.IsNullOrEmpty(targetTypeName))
                {
                    return true;
                }
            }

            string elementFullName = NormalizeTypeLookupName(SafeReadCodeElementName(() => element.FullName));
            if (!string.IsNullOrEmpty(targetTypeName) &&
                !string.IsNullOrEmpty(elementFullName) &&
                string.Equals(elementFullName, targetTypeName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Some code models omit namespace in FullName; allow short-type fallback only
            // when the requested type itself has no namespace.
            if (!string.IsNullOrEmpty(targetTypeName) &&
                targetTypeName.IndexOf('.') < 0 &&
                !string.IsNullOrEmpty(elementName) &&
                string.Equals(elementName, targetTypeName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private string SafeReadCodeElementName(Func<string> reader)
        {
            try
            {
                return reader();
            }
            catch
            {
                return null;
            }
        }

        private CodeElements SafeReadCodeElements(Func<CodeElements> reader)
        {
            try
            {
                return reader();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Waits for the debugger to re-enter break mode after a step operation.
        /// Returns true if break mode was reached within the timeout.
        /// </summary>
        private bool WaitForBreakMode(Debugger5 debugger, int timeoutMs)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    if (debugger.CurrentMode == dbgDebugMode.dbgBreakMode)
                        return true;
                }
                catch
                {
                    // Debugger state may be in transition
                }
                System.Threading.Thread.Sleep(10);
            }
            return false;
        }

        private T ExecuteOnMainThread<T>(Func<T> action, int timeoutMs = DefaultMainThreadTimeoutMs)
        {
            // If already on UI thread, execute directly to avoid deadlock
            if (ThreadHelper.CheckAccess())
            {
                return action();
            }

            T result = default(T);
            Exception capturedException = null;
            
            try
            {
                // Use a timeout to prevent infinite blocking
                var task = System.Threading.Tasks.Task.Run(() =>
                {
                    ThreadHelper.JoinableTaskFactory.Run(async () =>
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        try
                        {
                            result = action();
                        }
                        catch (Exception ex)
                        {
                            capturedException = ex;
                        }
                    });
                });

                if (!task.Wait(TimeSpan.FromMilliseconds(timeoutMs)))
                {
                    _log($"ExecuteOnMainThread: Timeout waiting for UI thread after {timeoutMs}ms");
                    throw new TimeoutException($"Timeout waiting for VS UI thread after {timeoutMs}ms. Is Visual Studio responsive?");
                }

                if (capturedException != null)
                {
                    throw capturedException;
                }
            }
            catch (AggregateException ae)
            {
                _log($"ExecuteOnMainThread: AggregateException - {ae.InnerException?.Message ?? ae.Message}");
                throw ae.InnerException ?? ae;
            }
            catch (Exception ex)
            {
                _log($"ExecuteOnMainThread: Exception - {ex.Message}");
                throw;
            }
            
            return result;
        }

        #endregion
    }
}
