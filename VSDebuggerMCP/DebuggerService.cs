using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
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
    /// <summary>
    /// Service that wraps Visual Studio debugger APIs to provide debugging information.
    /// </summary>
    public partial class DebuggerService : IDisposable
    {
        private readonly DTE2 _dte;
        private readonly Action<string> _log;
        private readonly DebugSessionTracker _sessionTracker;
        private readonly object _trustedLiveProcessSync = new object();
        private int _trustedLiveProcessId;
        private long _trustedLiveProcessStartTimeUtcTicks;
        private long _trustedLiveDebugSessionGeneration;
        private int _pendingTrustedLiveProcessId;
        private long _pendingTrustedLiveProcessStartTimeUtcTicks;
        private long _pendingTrustedLiveSessionGeneration;
        private long _pendingTrustedLiveExpiresUtcTicks;
        private int _negativeTrustedLiveProbeProcessId;
        private long _negativeTrustedLiveProbeGeneration;
        private long _negativeTrustedLiveProbeExpiresUtcTicks;
        private static readonly long PendingTrustedLiveIdentityLifetimeTicks =
            TimeSpan.FromSeconds(15).Ticks;
        private static readonly long NegativeTrustedLiveProbeLifetimeTicks =
            TimeSpan.FromSeconds(2).Ticks;

        public long DebugSessionGeneration => _sessionTracker.Generation;
        public long BreakSequence => _sessionTracker.BreakSequence;
        public long ContextEpoch => _sessionTracker.ContextEpoch;

        internal void InvalidateUnsafeEvaluationContext(string method)
        {
            _sessionTracker.InvalidateContext("unsafe_evaluation:" + (method ?? "unknown"));
        }

        private void RecordTrustedLiveProcessIdentity(
            int processId,
            long processStartTimeUtcTicks,
            DebugStatusResult status)
        {
            if (processId <= 0 || processStartTimeUtcTicks <= 0)
            {
                return;
            }
            lock (_trustedLiveProcessSync)
            {
                // Attach is asynchronous in some engines. Retain the already-verified
                // immutable target briefly, then bind it only after DTE reports that exact
                // process as the active live debuggee.
                _pendingTrustedLiveProcessId = processId;
                _pendingTrustedLiveProcessStartTimeUtcTicks = processStartTimeUtcTicks;
                _pendingTrustedLiveSessionGeneration = _sessionTracker.Generation;
                _pendingTrustedLiveExpiresUtcTicks = DateTime.UtcNow.Ticks +
                    PendingTrustedLiveIdentityLifetimeTicks;
                _negativeTrustedLiveProbeProcessId = 0;
                _negativeTrustedLiveProbeGeneration = 0;
                _negativeTrustedLiveProbeExpiresUtcTicks = 0;
                if (status?.IsDebugging == true && status.CurrentProcessId == processId)
                {
                    BindTrustedLiveProcessIdentity(
                        processId,
                        processStartTimeUtcTicks,
                        status.DebugSessionGeneration);
                }
            }
        }

        private void BindTrustedLiveProcessIdentity(
            int processId,
            long processStartTimeUtcTicks,
            long debugSessionGeneration)
        {
            lock (_trustedLiveProcessSync)
            {
                _trustedLiveProcessId = processId;
                _trustedLiveProcessStartTimeUtcTicks = processStartTimeUtcTicks;
                _trustedLiveDebugSessionGeneration = debugSessionGeneration;
                _pendingTrustedLiveProcessId = 0;
                _pendingTrustedLiveProcessStartTimeUtcTicks = 0;
                _pendingTrustedLiveSessionGeneration = 0;
                _pendingTrustedLiveExpiresUtcTicks = 0;
                _negativeTrustedLiveProbeProcessId = 0;
                _negativeTrustedLiveProbeGeneration = 0;
                _negativeTrustedLiveProbeExpiresUtcTicks = 0;
            }
        }

        private void ClearTrustedLiveProcessIdentity(bool clearPending)
        {
            lock (_trustedLiveProcessSync)
            {
                _trustedLiveProcessId = 0;
                _trustedLiveProcessStartTimeUtcTicks = 0;
                _trustedLiveDebugSessionGeneration = 0;
                if (clearPending)
                {
                    _pendingTrustedLiveProcessId = 0;
                    _pendingTrustedLiveProcessStartTimeUtcTicks = 0;
                    _pendingTrustedLiveSessionGeneration = 0;
                    _pendingTrustedLiveExpiresUtcTicks = 0;
                }
            }
        }

        private void RefreshTrustedLiveProcessIdentity(Debugger5 debugger, int processId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            long generation = _sessionTracker.Generation;
            dbgDebugMode mode;
            try
            {
                mode = debugger?.CurrentMode ?? dbgDebugMode.dbgDesignMode;
            }
            catch
            {
                // This observer runs from UI-dispatch finally blocks. A transient COM
                // failure must never replace the operation's actual result or exception.
                ClearTrustedLiveProcessIdentity(clearPending: false);
                ExpirePendingTrustedLiveProcessIdentity();
                return;
            }
            if (mode == dbgDebugMode.dbgDesignMode || processId <= 0)
            {
                ClearTrustedLiveProcessIdentity(clearPending: false);
                ExpirePendingTrustedLiveProcessIdentity();
                return;
            }

            lock (_trustedLiveProcessSync)
            {
                if (_trustedLiveProcessId == processId &&
                    _trustedLiveProcessStartTimeUtcTicks > 0 &&
                    _trustedLiveDebugSessionGeneration == generation)
                {
                    return;
                }
            }

            long expectedPendingStartTime = 0;
            lock (_trustedLiveProcessSync)
            {
                bool pendingIsCurrent = _pendingTrustedLiveProcessId == processId &&
                    _pendingTrustedLiveProcessStartTimeUtcTicks > 0 &&
                    DateTime.UtcNow.Ticks <= _pendingTrustedLiveExpiresUtcTicks &&
                    (generation == _pendingTrustedLiveSessionGeneration ||
                     generation == _pendingTrustedLiveSessionGeneration + 1);
                if (pendingIsCurrent)
                {
                    expectedPendingStartTime = _pendingTrustedLiveProcessStartTimeUtcTicks;
                }
            }
            if (expectedPendingStartTime > 0)
            {
                long actualStartTime = TryGetProcessStartTimeUtcTicks(processId);
                if (actualStartTime == expectedPendingStartTime)
                {
                    BindTrustedLiveProcessIdentity(processId, actualStartTime, generation);
                    return;
                }
            }

            lock (_trustedLiveProcessSync)
            {
                if (_negativeTrustedLiveProbeProcessId == processId &&
                    _negativeTrustedLiveProbeGeneration == generation &&
                    DateTime.UtcNow.Ticks <= _negativeTrustedLiveProbeExpiresUtcTicks)
                {
                    return;
                }
            }

            // Never enumerate Debugger.LocalProcesses here. This observer runs in
            // request-finally UI-thread paths, and even a bounded COM enumeration can
            // monopolize the IDE. Only an MCP start/attach operation supplies the
            // pre-captured OS start time needed to bind a live process safely.
            // Sessions started outside MCP stay on debugger-engine-only access; this
            // also prevents a dump's recorded PID from being mistaken for a reused PID.
            ClearTrustedLiveProcessIdentity(clearPending: false);
            ExpirePendingTrustedLiveProcessIdentity();
            lock (_trustedLiveProcessSync)
            {
                _negativeTrustedLiveProbeProcessId = processId;
                _negativeTrustedLiveProbeGeneration = generation;
                _negativeTrustedLiveProbeExpiresUtcTicks = DateTime.UtcNow.Ticks +
                    NegativeTrustedLiveProbeLifetimeTicks;
            }
        }

        private void ExpirePendingTrustedLiveProcessIdentity()
        {
            lock (_trustedLiveProcessSync)
            {
                if (_pendingTrustedLiveExpiresUtcTicks > 0 &&
                    DateTime.UtcNow.Ticks > _pendingTrustedLiveExpiresUtcTicks)
                {
                    _pendingTrustedLiveProcessId = 0;
                    _pendingTrustedLiveProcessStartTimeUtcTicks = 0;
                    _pendingTrustedLiveSessionGeneration = 0;
                    _pendingTrustedLiveExpiresUtcTicks = 0;
                }
            }
        }

        private IntPtr OpenTrustedLiveProcessHandle(int processId, uint desiredAccess)
        {
            int trustedProcessId;
            long trustedStartTime;
            long trustedGeneration;
            lock (_trustedLiveProcessSync)
            {
                trustedProcessId = _trustedLiveProcessId;
                trustedStartTime = _trustedLiveProcessStartTimeUtcTicks;
                trustedGeneration = _trustedLiveDebugSessionGeneration;
            }
            if (processId <= 0 || processId != trustedProcessId ||
                trustedStartTime <= 0 || trustedGeneration != _sessionTracker.Generation)
            {
                return IntPtr.Zero;
            }
            IntPtr handle = OpenProcess(desiredAccess, false, processId);
            if (handle == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }
            if (!TryGetProcessStartTimeUtcTicks(handle, out long actualStartTime) ||
                actualStartTime != trustedStartTime)
            {
                CloseHandle(handle);
                return IntPtr.Zero;
            }
            return handle;
        }
        private readonly DisassemblyService _disasmService;
        private readonly BuildErrorService _buildErrorService;
        private readonly OutputWindowService _outputWindowService;
        private readonly IManagedInspectionService _managedInspectionService;
        private readonly VisualStudioSourceLocationAdapter _sourceLocationAdapter;
        private readonly IVisualStudioDebuggerAdapter _visualStudioAdapter;
        private readonly DebugProcessHandleStore _debugProcessHandles;
        private string _activeDumpFilePath;
        private long _activeDumpSessionGeneration;
        private string _pendingDumpFilePath;
        private long _pendingDumpStartGeneration;
        private const int SnapshotMaxAttempts = 1;
        private const int ExpressionEvaluationTimeoutMs = 1500;
        private const int ExpressionBatchBudgetMs = 30000;
        private const int ExpressionExpansionBudgetMs = 250;
        private const int ExpressionExpansionMaxNodes = 32;
        private const int ExpressionExpansionMaxDepth = 4;
        private const int LocalsExpansionBudgetMs = 250;
        private const int LocalsExpansionMaxNodes = 32;
        private const int LocalsExpansionMaxDepth = 4;
        private const int DefaultMainThreadTimeoutMs = 10000;
        private const int HeavyMainThreadTimeoutMs = 60000;
        private const int DebugBundleMainThreadTimeoutMs = 120000;
        private const int ThreadEnumerationBudgetMs = 2500;
        private const int ThreadFrameExpansionMaxNodes = 128;
        private const int ThreadFramesMaxPerThread = 16;
        private const int ThreadFramesHardMaxPerThread = 256;
        private const int ThreadFrameExpansionHardMaxNodes = 4096;
        private const int ModuleFrameScanDefault = 1024;
        private const int ModuleFrameScanHardLimit = 4096;
        private const int CollectionEnumerationBudgetMs = 2500;
        private const int CollectionExpansionMaxNodes = 256;
        private const int CollectionExpansionHardMaxNodes = 4096;
        private const int BreakpointProbeMaxDistance = 25;
        private static readonly Guid VisualStudioDebuggerCommandSet =
            new Guid("5289D302-2432-4761-8C45-051C64BD00C4");
        private const uint DebugDumpFileCommandId = 0x00000603;
        private const long SourcePreflightMaxBytes = 16L * 1024L * 1024L;
        private const string AddressBreakpointTagPrefix = "AIDB_ADDR=";
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
            ThreadHelper.ThrowIfNotOnUIThread();
            _dte = dte ?? throw new ArgumentNullException(nameof(dte));
            _log = log ?? ((s) => { });
            _disasmService = new DisassemblyService();
            _buildErrorService = new BuildErrorService(_dte, _log);
            _outputWindowService = new OutputWindowService(_dte, _log);
            _managedInspectionService = new ManagedInspectionService(_log);
            _sourceLocationAdapter = new VisualStudioSourceLocationAdapter();
            _visualStudioAdapter = new VisualStudioDebuggerAdapter();
            _debugProcessHandles = new DebugProcessHandleStore();
            _sessionTracker = new DebugSessionTracker(_dte, _log);
            ObserveDebugContext(Debugger);
        }

        private Debugger5 Debugger
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return _dte.Debugger as Debugger5;
            }
        }










        #region Helpers

        /// <summary>
        /// Gets the file name from a stack frame safely - IMPROVED VERSION.
        /// Handles both native and managed code.
        /// </summary>
        private string GetFrameFileName(EnvDTE.StackFrame frame, int frameIndex = -1)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return _sourceLocationAdapter.GetLocation(frame).FilePath;
        }

        /// <summary>
        /// EnvDTE.StackFrame has no authoritative line API. Cursor/editor positions are
        /// deliberately excluded because they are not execution state.
        /// </summary>
        private (int LineNumber, bool IsReliable) GetFrameLineNumberEx(EnvDTE.StackFrame frame, int frameIndex = -1)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            VisualStudioSourceLocation location = _sourceLocationAdapter.GetLocation(frame);
            return (location.LineNumber ?? 0, location.IsReliable);
        }

        private int GetFrameLineNumber(EnvDTE.StackFrame frame)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return GetFrameLineNumberEx(frame).LineNumber;
        }

        private SourceLocationPreflight InspectSourceLocation(
            string filePath,
            int lineNumber,
            bool requireExecutableText)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return SourceLocationPreflight.Invalid("File path is required");
            }
            if (lineNumber < 1)
            {
                return SourceLocationPreflight.Invalid("Line number must be >= 1");
            }

            string normalizedPath;
            try
            {
                normalizedPath = Path.GetFullPath(filePath);
                string root = Path.GetPathRoot(normalizedPath);
                if (string.IsNullOrEmpty(root) || normalizedPath.StartsWith(@"\\", StringComparison.Ordinal) ||
                    GetDriveType(root) != 3) // DRIVE_FIXED
                {
                    return SourceLocationPreflight.Invalid(
                        "Source preflight accepts local fixed-drive files only");
                }
                var info = new FileInfo(normalizedPath);
                if (!info.Exists)
                {
                    return SourceLocationPreflight.Invalid($"File not found: {normalizedPath}");
                }
                if (info.Length > SourcePreflightMaxBytes)
                {
                    return SourceLocationPreflight.Invalid(
                        $"Source file exceeds the {SourcePreflightMaxBytes}-byte preflight limit");
                }

                int lineCount = 0;
                string targetLine = null;
                using (var reader = new StreamReader(normalizedPath, Encoding.UTF8, true, 4096))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if ((lineCount & 0xFF) == 0)
                        {
                            DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                        }
                        lineCount++;
                        if (lineCount == lineNumber) targetLine = line;
                    }
                }
                if (lineNumber > lineCount)
                {
                    return SourceLocationPreflight.Invalid(
                        $"Line {lineNumber} is beyond end of file (file has {lineCount} lines)");
                }
                if (requireExecutableText && string.IsNullOrWhiteSpace(targetLine))
                {
                    return SourceLocationPreflight.Invalid(
                        $"Line {lineNumber} is empty - cannot set execution point on empty line");
                }
                return new SourceLocationPreflight
                {
                    IsValid = true,
                    FilePath = normalizedPath,
                    LineCount = lineCount,
                    TargetLine = targetLine ?? string.Empty,
                };
            }
            catch (Exception ex)
            {
                RethrowRequestControlFlow(ex);
                return SourceLocationPreflight.Invalid($"Cannot read file: {ex.Message}");
            }
        }

        private sealed class SourceLocationPreflight
        {
            internal bool IsValid { get; set; }
            internal string ErrorMessage { get; set; }
            internal string FilePath { get; set; }
            internal int LineCount { get; set; }
            internal string TargetLine { get; set; }

            internal static SourceLocationPreflight Invalid(string message)
            {
                return new SourceLocationPreflight { ErrorMessage = message };
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetDriveType(string rootPathName);

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

        private T ExecuteOnMainThread<T>(Func<T> action, int timeoutMs = DefaultMainThreadTimeoutMs)
        {
            int effectiveTimeoutMs = DebuggerRequestContext.RemainingMilliseconds(timeoutMs);
            try
            {
                return _visualStudioAdapter.ExecuteOnMainThread(() =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    ValidateExpectedDebuggerIdentity("before UI dispatch");
                    T result;
                    try
                    {
                        result = action();
                    }
                    finally
                    {
                        ObserveDebugContext(Debugger);
                        RecordRequestCompletionCandidate();
                    }
                    if (!DebuggerRequestContext.IsCommand)
                    {
                        DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                        ValidateExpectedDebuggerIdentity("while the query was executing");
                    }
                    return result;
                }, effectiveTimeoutMs, DebuggerRequestContext.CancellationToken);
            }
            catch (Exception ex)
            {
                _log($"ExecuteOnMainThread: Exception - {ex.GetType().Name}");
                throw;
            }
        }

        internal Task WaitForPendingUiOperationsAsync() => _visualStudioAdapter.WaitForIdleAsync();

        internal bool HasPendingUiOperations => _visualStudioAdapter.HasPendingOperations;

        internal DispatcherHealthResult GetDispatcherHealth()
        {
            DispatcherHealthSnapshot snapshot = _visualStudioAdapter.GetDispatcherHealth();
            return new DispatcherHealthResult
            {
                State = snapshot.State,
                ActiveRequestId = snapshot.ActiveRequestId,
                ActiveMethod = snapshot.ActiveMethod,
                DispatchStartedAtUtc = snapshot.DispatchStartedUtc?.ToString("O"),
                UiAdmittedAtUtc = snapshot.UiAdmissionUtc?.ToString("O"),
                PoisonedAtUtc = snapshot.PoisonedUtc?.ToString("O"),
                ElapsedMs = snapshot.ElapsedMs,
                PoisonAfterMs = snapshot.PoisonAfterMs,
                PendingUiOperations = _visualStudioAdapter.HasPendingOperations,
                SideEffectIssued = snapshot.SideEffectIssued,
                EffectOperation = snapshot.EffectOperation,
                EffectStage = snapshot.EffectStage,
                RecoveryAction = string.Equals(snapshot.State, "poisoned", StringComparison.Ordinal)
                    ? "restart_visual_studio"
                    : null,
            };
        }

        private void RecordRequestCompletionCandidate()
        {
            StopEventSnapshot current = _sessionTracker.GetStopEventSnapshot();
            DebuggerRequestContext.RecordCompletionCandidate(
                current.Generation,
                current.BreakSequence,
                current.ContextEpoch);
        }

        private void ValidateExpectedDebuggerIdentity(string phase)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ObserveDebugContext(Debugger);
            if (DebuggerRequestContext.RequiresStableDebugSession &&
                DebuggerRequestContext.ExpectedDebugSessionGeneration != _sessionTracker.Generation)
            {
                throw new StaleDebugSessionException(
                    $"Debug session generation changed from " +
                    $"{DebuggerRequestContext.ExpectedDebugSessionGeneration} to {_sessionTracker.Generation} {phase}.");
            }
            if (DebuggerRequestContext.RequiresStableDebugContext &&
                (DebuggerRequestContext.ExpectedBreakSequence != _sessionTracker.BreakSequence ||
                 DebuggerRequestContext.ExpectedContextEpoch != _sessionTracker.ContextEpoch))
            {
                throw new StaleDebugContextException(
                    $"Debug context changed from {DebuggerRequestContext.ExpectedBreakSequence}:" +
                    $"{DebuggerRequestContext.ExpectedContextEpoch} to {_sessionTracker.BreakSequence}:" +
                    $"{_sessionTracker.ContextEpoch} {phase}.");
            }
        }

        private static void RethrowRequestControlFlow(Exception exception)
        {
            if (exception is OperationCanceledException ||
                exception is TimeoutException ||
                exception is ResourceLimitExceededException ||
                exception is StaleDebugSessionException ||
                exception is StaleDebugContextException)
            {
                ExceptionDispatchInfo.Capture(exception).Throw();
            }
        }

        internal DebuggerIdentitySnapshot RebaseRequestToCurrentDebuggerIdentity()
        {
            StopEventSnapshot current = _sessionTracker.GetStopEventSnapshot();
            DebuggerRequestContext.RebaseDebuggerIdentity(
                current.Generation,
                current.BreakSequence,
                current.ContextEpoch);
            return new DebuggerIdentitySnapshot
            {
                DebugSessionGeneration = current.Generation,
                BreakSequence = current.BreakSequence,
                ContextEpoch = current.ContextEpoch,
            };
        }

        private void MarkExecutionStateCommandIssued(string reason)
        {
            _sessionTracker.InvalidateContext(reason);
            RebaseRequestToCurrentDebuggerIdentity();
        }

        private void MarkUnsafeEvaluationIssued()
        {
            DebuggerRequestContext.MarkUnsafeEvaluationIssued(() =>
            {
                InvalidateUnsafeEvaluationContext("evaluation");
                RebaseRequestToCurrentDebuggerIdentity();
            });
        }

        private void MarkDebugSessionTransitionCommandIssued(string reason)
        {
            _sessionTracker.InvalidateSessionTransition(reason);
            RebaseRequestToCurrentDebuggerIdentity();
        }

        internal DebuggerIdentitySnapshot CaptureCurrentDebuggerIdentity()
        {
            StopEventSnapshot current = _sessionTracker.GetStopEventSnapshot();
            return new DebuggerIdentitySnapshot
            {
                DebugSessionGeneration = current.Generation,
                BreakSequence = current.BreakSequence,
                ContextEpoch = current.ContextEpoch,
            };
        }

        private static int NormalizeCollectionNodeLimit(int maxNodes)
        {
            if (maxNodes < 0) return CollectionExpansionMaxNodes;
            if (maxNodes == 0) return CollectionExpansionHardMaxNodes;
            return Math.Min(maxNodes, CollectionExpansionHardMaxNodes);
        }

        private static int NormalizeCollectionBudgetMs(int budgetMs)
        {
            if (budgetMs < 0) return CollectionEnumerationBudgetMs;
            if (budgetMs == 0) return DebugBundleMainThreadTimeoutMs - 5000;
            return Math.Min(budgetMs, DebugBundleMainThreadTimeoutMs - 5000);
        }

        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _sessionTracker.Dispose();
        }

        #endregion
    }

}
