using System;

namespace VSDebuggerMCP.Services
{
    internal sealed class UiOperationLease
    {
        internal object Token { get; set; }
    }

    internal sealed class DispatcherHealthSnapshot
    {
        internal string State { get; set; }
        internal string ActiveRequestId { get; set; }
        internal string ActiveMethod { get; set; }
        internal DateTime? DispatchStartedUtc { get; set; }
        internal DateTime? UiAdmissionUtc { get; set; }
        internal DateTime? PoisonedUtc { get; set; }
        internal long ElapsedMs { get; set; }
        internal int PoisonAfterMs { get; set; }
        internal bool SideEffectIssued { get; set; }
        internal string EffectOperation { get; set; }
        internal string EffectStage { get; set; }
    }

    /// <summary>
    /// Process-wide lease for Visual Studio UI/COM ownership. It intentionally
    /// survives MCP server disable/enable cycles, so a replacement server cannot
    /// enter DTE while an operation from the previous server is still unresolved.
    /// </summary>
    internal sealed class UiOperationCircuit
    {
        private const int DefaultPoisonAfterMs = 120000;
        private const int MinimumPoisonAfterMs = 1000;
        private const int MaximumPoisonAfterMs = 30 * 60 * 1000;
        private readonly object _sync = new object();
        private readonly Func<DateTime> _utcNow;
        private readonly int _poisonAfterMs;
        private ActiveOperation _active;

        internal static UiOperationCircuit ProcessWide { get; } =
            new UiOperationCircuit(ResolvePoisonAfterMs(), () => DateTime.UtcNow);

        internal UiOperationCircuit(int poisonAfterMs, Func<DateTime> utcNow)
        {
            if (poisonAfterMs < MinimumPoisonAfterMs || poisonAfterMs > MaximumPoisonAfterMs)
                throw new ArgumentOutOfRangeException(nameof(poisonAfterMs));
            _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
            _poisonAfterMs = poisonAfterMs;
        }

        internal bool TryAcquire(
            string requestId,
            string method,
            out UiOperationLease lease,
            out DispatcherHealthSnapshot conflict)
        {
            lock (_sync)
            {
                DateTime now = _utcNow();
                ApplyPoisonLocked(now);
                if (_active != null)
                {
                    lease = null;
                    conflict = SnapshotLocked(now);
                    return false;
                }

                object token = new object();
                _active = new ActiveOperation
                {
                    Token = token,
                    RequestId = requestId,
                    Method = method,
                    DispatchStartedUtc = now,
                    EffectStage = "none",
                };
                lease = new UiOperationLease { Token = token };
                conflict = null;
                return true;
            }
        }

        internal void MarkUiAdmitted(UiOperationLease lease)
        {
            lock (_sync)
            {
                ActiveOperation active = GetOwnedLocked(lease);
                if (!active.UiAdmissionUtc.HasValue) active.UiAdmissionUtc = _utcNow();
            }
        }

        internal void MarkOutcomeUnknown(UiOperationLease lease)
        {
            lock (_sync)
            {
                ActiveOperation active = GetOwnedLocked(lease);
                active.OutcomeUnknown = true;
                ApplyPoisonLocked(_utcNow());
            }
        }

        internal void RecordEffectStage(string requestId, string operation, string stage)
        {
            lock (_sync)
            {
                if (_active == null ||
                    (!string.IsNullOrEmpty(requestId) &&
                     !string.Equals(_active.RequestId, requestId, StringComparison.Ordinal)))
                {
                    return;
                }
                _active.SideEffectIssued = true;
                if (!string.IsNullOrWhiteSpace(operation)) _active.EffectOperation = operation;
                if (!string.IsNullOrWhiteSpace(stage)) _active.EffectStage = stage;
            }
        }

        internal void Release(UiOperationLease lease)
        {
            if (lease == null) return;
            lock (_sync)
            {
                if (_active != null && ReferenceEquals(_active.Token, lease.Token))
                {
                    _active = null;
                }
            }
        }

        internal DispatcherHealthSnapshot Snapshot()
        {
            lock (_sync)
            {
                DateTime now = _utcNow();
                ApplyPoisonLocked(now);
                return SnapshotLocked(now);
            }
        }

        private ActiveOperation GetOwnedLocked(UiOperationLease lease)
        {
            if (lease == null || _active == null || !ReferenceEquals(_active.Token, lease.Token))
                throw new InvalidOperationException("The UI operation lease is no longer active.");
            return _active;
        }

        private void ApplyPoisonLocked(DateTime now)
        {
            if (_active == null || _active.PoisonedUtc.HasValue || !_active.OutcomeUnknown ||
                !_active.UiAdmissionUtc.HasValue)
            {
                return;
            }
            if ((now - _active.UiAdmissionUtc.Value).TotalMilliseconds >= _poisonAfterMs)
            {
                _active.PoisonedUtc = now;
            }
        }

        private DispatcherHealthSnapshot SnapshotLocked(DateTime now)
        {
            if (_active == null)
            {
                return new DispatcherHealthSnapshot
                {
                    State = "healthy",
                    PoisonAfterMs = _poisonAfterMs,
                };
            }

            DateTime elapsedStart = _active.UiAdmissionUtc ?? _active.DispatchStartedUtc;
            return new DispatcherHealthSnapshot
            {
                State = _active.PoisonedUtc.HasValue
                    ? "poisoned"
                    : (_active.OutcomeUnknown
                        ? "draining_unknown"
                        : (_active.UiAdmissionUtc.HasValue ? "executing" : "dispatching")),
                ActiveRequestId = _active.RequestId,
                ActiveMethod = _active.Method,
                DispatchStartedUtc = _active.DispatchStartedUtc,
                UiAdmissionUtc = _active.UiAdmissionUtc,
                PoisonedUtc = _active.PoisonedUtc,
                ElapsedMs = Math.Max(0L, (long)(now - elapsedStart).TotalMilliseconds),
                PoisonAfterMs = _poisonAfterMs,
                SideEffectIssued = _active.SideEffectIssued,
                EffectOperation = _active.EffectOperation,
                EffectStage = _active.EffectStage,
            };
        }

        private static int ResolvePoisonAfterMs()
        {
            string configured = Environment.GetEnvironmentVariable("AIDB_UI_OPERATION_POISON_AFTER_MS");
            if (int.TryParse(configured, out int value) &&
                value >= MinimumPoisonAfterMs && value <= MaximumPoisonAfterMs)
            {
                return value;
            }
            return DefaultPoisonAfterMs;
        }

        private sealed class ActiveOperation
        {
            internal object Token { get; set; }
            internal string RequestId { get; set; }
            internal string Method { get; set; }
            internal DateTime DispatchStartedUtc { get; set; }
            internal DateTime? UiAdmissionUtc { get; set; }
            internal bool OutcomeUnknown { get; set; }
            internal DateTime? PoisonedUtc { get; set; }
            internal bool SideEffectIssued { get; set; }
            internal string EffectOperation { get; set; }
            internal string EffectStage { get; set; }
        }
    }
}
