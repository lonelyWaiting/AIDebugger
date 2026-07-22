using System;
using System.Threading;

namespace VSDebuggerMCP
{
    internal sealed class ActiveRequest
    {
        private readonly object _sync = new object();
        private bool _completed;
        private string _state = "queued";
        private DateTime? _startedUtc;
        private bool _sideEffectIssued;

        public CancellationTokenSource Cancellation { get; set; }
        public string Fingerprint { get; set; }
        public string Kind { get; set; }
        public DateTime EnqueuedUtc { get; set; }
        public CancellationToken Token => Cancellation.Token;

        public bool HasStarted
        {
            get { lock (_sync) return _startedUtc.HasValue; }
        }

        public bool SideEffectIssued
        {
            get { lock (_sync) return _sideEffectIssued; }
        }

        public void MarkSideEffectIssued()
        {
            lock (_sync)
            {
                _sideEffectIssued = true;
            }
        }

        public bool TryMarkRunning()
        {
            lock (_sync)
            {
                if (_completed || Cancellation.IsCancellationRequested) return false;
                _state = "running";
                _startedUtc = DateTime.UtcNow;
                return true;
            }
        }

        public bool TryCancel()
        {
            return TryCancel(null);
        }

        public bool TryCancel(Action beforeCancellation)
        {
            lock (_sync)
            {
                if (_completed) return false;
                _state = "cancellation_requested";
                if (Cancellation.IsCancellationRequested) return false;
                beforeCancellation?.Invoke();
                Cancellation.Cancel();
                return true;
            }
        }

        public bool TryCancelIfNotStarted()
        {
            lock (_sync)
            {
                if (_completed || _startedUtc.HasValue) return false;
                _state = "cancellation_requested";
                if (!Cancellation.IsCancellationRequested) Cancellation.Cancel();
                return true;
            }
        }

        public void MarkOutcomePending()
        {
            lock (_sync)
            {
                if (_completed || !_startedUtc.HasValue) return;
                _state = "outcome_pending";
            }
        }

        public ActiveRequestSnapshot Snapshot()
        {
            lock (_sync)
            {
                return new ActiveRequestSnapshot
                {
                    State = _state,
                    Kind = Kind,
                    EnqueuedUtc = EnqueuedUtc,
                    StartedUtc = _startedUtc,
                    CancellationRequested = Cancellation.IsCancellationRequested,
                    SideEffectIssued = _sideEffectIssued,
                };
            }
        }

        public void Complete()
        {
            lock (_sync)
            {
                if (_completed) return;
                _completed = true;
                _state = "completed";
                Cancellation.Dispose();
            }
        }
    }

    internal sealed class ActiveRequestSnapshot
    {
        public string State { get; set; }
        public string Kind { get; set; }
        public DateTime EnqueuedUtc { get; set; }
        public DateTime? StartedUtc { get; set; }
        public bool CancellationRequested { get; set; }
        public bool SideEffectIssued { get; set; }
    }
}
