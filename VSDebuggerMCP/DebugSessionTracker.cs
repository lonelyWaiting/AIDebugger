using System;
using System.Threading;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;

namespace VSDebuggerMCP
{
    internal sealed class DebugSessionTracker : IDisposable
    {
        private readonly object _sync = new object();
        private readonly DebuggerEvents _events;
        private readonly Action<string> _log;
        private long _generation = 1;
        private long _breakSequence;
        private long _contextEpoch = 1;
        private int _debuggeeProcessId;
        private string _programIdentity;
        private string _debuggerContextSignature;
        private string _mode = "design";
        private string _stopReason = "not_paused";
        private string _stopReasonRaw;
        private bool _disposed;

        public DebugSessionTracker(DTE2 dte, Action<string> log)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _log = log ?? (_ => { });
            _events = dte?.Events?.DebuggerEvents ??
                throw new InvalidOperationException("Visual Studio debugger events are unavailable");
            _events.OnEnterRunMode += OnEnterRunMode;
            _events.OnEnterBreakMode += OnEnterBreakMode;
            _events.OnEnterDesignMode += OnEnterDesignMode;

            dbgDebugMode currentMode = dte.Debugger.CurrentMode;
            _mode = currentMode == dbgDebugMode.dbgBreakMode
                ? "break"
                : currentMode == dbgDebugMode.dbgRunMode ? "run" : "design";
            _stopReason = currentMode == dbgDebugMode.dbgBreakMode ? "pause_unknown" : "not_paused";
            _stopReasonRaw = "initial_state";
        }

        public long Generation => Interlocked.Read(ref _generation);
        public long BreakSequence => Interlocked.Read(ref _breakSequence);
        public long ContextEpoch => Interlocked.Read(ref _contextEpoch);

        public void ObserveDebuggee(int processId, string programIdentity)
        {
            lock (_sync)
            {
                string normalizedProgram = programIdentity ?? string.Empty;
                bool hadIdentity = _debuggeeProcessId != 0 || !string.IsNullOrEmpty(_programIdentity);
                bool changed = _debuggeeProcessId != processId ||
                    !string.Equals(_programIdentity ?? string.Empty, normalizedProgram, StringComparison.Ordinal);
                if (hadIdentity && changed)
                {
                    Interlocked.Increment(ref _contextEpoch);
                }
                _debuggeeProcessId = processId;
                _programIdentity = normalizedProgram;
            }
        }

        public void ObserveDebuggerContext(string signature)
        {
            lock (_sync)
            {
                string normalized = signature ?? string.Empty;
                if (_debuggerContextSignature != null &&
                    !string.Equals(_debuggerContextSignature, normalized, StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _contextEpoch);
                    Monitor.PulseAll(_sync);
                }
                _debuggerContextSignature = normalized;
            }
        }

        public void InvalidateContext(string reason)
        {
            lock (_sync)
            {
                Interlocked.Increment(ref _contextEpoch);
                // stopReasonRaw is the last IDE debugger event and must not be
                // repurposed for internal context invalidation diagnostics.
                Monitor.PulseAll(_sync);
            }
        }

        public void InvalidateSessionTransition(string reason)
        {
            lock (_sync)
            {
                // Once a start/attach/stop/detach command has been dispatched, the
                // previous session identity is unsafe even if EnvDTE delivers its
                // mode event late or never delivers it.  A later event may advance
                // the generation again; monotonicity is intentional.
                Interlocked.Increment(ref _generation);
                Interlocked.Increment(ref _contextEpoch);
                _debuggeeProcessId = 0;
                _programIdentity = null;
                _debuggerContextSignature = null;
                Monitor.PulseAll(_sync);
            }
        }

        public StopEventSnapshot GetStopEventSnapshot()
        {
            lock (_sync)
            {
                return CreateSnapshotUnsafe();
            }
        }

        private void OnEnterRunMode(dbgEventReason reason)
        {
            Update("run", "not_paused", reason.ToString(), isBreak: false);
        }

        private void OnEnterBreakMode(dbgEventReason reason, ref dbgExecutionAction executionAction)
        {
            string raw = reason.ToString();
            Update("break", MapStopReason(raw), raw, isBreak: true);
        }

        private void OnEnterDesignMode(dbgEventReason reason)
        {
            Update("design", "not_paused", reason.ToString(), isBreak: false);
        }

        private void Update(string mode, string reason, string rawReason, bool isBreak)
        {
            lock (_sync)
            {
                bool enteringSession = string.Equals(_mode, "design", StringComparison.Ordinal) &&
                    !string.Equals(mode, "design", StringComparison.Ordinal);
                bool leavingSession = !string.Equals(_mode, "design", StringComparison.Ordinal) &&
                    string.Equals(mode, "design", StringComparison.Ordinal);
                _mode = mode;
                // The event advances the epoch itself. The previous mode's frame
                // identity cannot be compared with the first lazily observed frame
                // of this stop/run: doing so creates a second, spurious epoch.
                _debuggerContextSignature = null;
                _stopReason = reason;
                _stopReasonRaw = rawReason;
                if (enteringSession || leavingSession)
                {
                    Interlocked.Increment(ref _generation);
                }
                Interlocked.Increment(ref _contextEpoch);
                if (isBreak)
                {
                    Interlocked.Increment(ref _breakSequence);
                }
                Monitor.PulseAll(_sync);
            }
        }

        public StopEventSnapshot WaitForBreakOrDesign(
            long afterGeneration,
            long afterBreakSequence,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            using (cancellationToken.Register(() =>
            {
                lock (_sync)
                {
                    Monitor.PulseAll(_sync);
                }
            }))
            {
                lock (_sync)
                {
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        StopEventSnapshot current = CreateSnapshotUnsafe();
                        // A waiter is bound to exactly one debug-session generation.
                        // Never absorb a design->run transition into an in-flight
                        // continue/step: a later break may belong to another process.
                        if (current.Generation != afterGeneration)
                        {
                            return current;
                        }
                        if (current.BreakSequence > afterBreakSequence ||
                            string.Equals(current.Mode, "design", StringComparison.Ordinal))
                        {
                            if (string.Equals(current.Mode, "break", StringComparison.Ordinal) ||
                                string.Equals(current.Mode, "design", StringComparison.Ordinal))
                            {
                                return current;
                            }
                        }

                        int remainingMs = Math.Max(
                            0,
                            timeoutMs - (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds));
                        if (remainingMs == 0 || !Monitor.Wait(_sync, remainingMs))
                        {
                            return null;
                        }
                    }
                }
            }
        }

        private StopEventSnapshot CreateSnapshotUnsafe()
        {
            return new StopEventSnapshot
            {
                Generation = Generation,
                BreakSequence = BreakSequence,
                ContextEpoch = ContextEpoch,
                DebuggeeProcessId = _debuggeeProcessId,
                ProgramIdentity = _programIdentity,
                Mode = _mode,
                StopReason = _stopReason,
                StopReasonRaw = _stopReasonRaw,
                Source = "debugger_event",
                Confidence = string.Equals(_stopReason, "pause_unknown", StringComparison.Ordinal)
                    ? "low"
                    : "high",
            };
        }

        private static string MapStopReason(string rawReason)
        {
            switch (rawReason)
            {
                case "dbgEventReasonBreakpoint":
                    return "breakpoint";
                case "dbgEventReasonExceptionThrown":
                case "dbgEventReasonExceptionNotHandled":
                    return "exception";
                case "dbgEventReasonStep":
                    return "step";
                case "dbgEventReasonUserBreak":
                    return "user_break";
                default:
                    return "pause_unknown";
            }
        }

        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            try
            {
                _events.OnEnterRunMode -= OnEnterRunMode;
                _events.OnEnterBreakMode -= OnEnterBreakMode;
                _events.OnEnterDesignMode -= OnEnterDesignMode;
            }
            catch (Exception ex)
            {
                _log($"Failed to detach debugger event handlers: {ex.GetType().Name}");
            }
        }
    }

    internal sealed class StopEventSnapshot
    {
        public long Generation { get; set; }
        public long BreakSequence { get; set; }
        public long ContextEpoch { get; set; }
        public int DebuggeeProcessId { get; set; }
        public string ProgramIdentity { get; set; }
        public string Mode { get; set; }
        public string StopReason { get; set; }
        public string StopReasonRaw { get; set; }
        public string Source { get; set; }
        public string Confidence { get; set; }
    }
}
