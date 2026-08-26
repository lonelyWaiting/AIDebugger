using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VSDebuggerMCP.Ipc
{
    internal enum CommandReservationStatus
    {
        ReservedByCaller,
        Cached,
        Conflict,
        InProgress,
        ResultEvicted,
        CapacityExceeded,
    }

    internal sealed class CommandReservationLease
    {
        internal string RequestId { get; set; }
        internal string Fingerprint { get; set; }
        internal object Token { get; set; }
    }

    /// <summary>
    /// Keeps server-instance-scoped at-most-once tombstones keyed by request id.
    /// Response bodies are bounded and may be evicted independently. Completed
    /// tombstones are retained for the configured command-acceptance window;
    /// while that window is active, capacity exhaustion rejects new commands
    /// rather than risking a duplicate side effect.
    /// </summary>
    internal sealed class CommandResultStore
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Queue<string> _responseOrder = new Queue<string>();
        private readonly int _responseCapacity;
        private readonly int _tombstoneCapacity;
        private readonly long _responseByteCapacity;
        private readonly TimeSpan _responseTtl;
        private readonly TimeSpan _tombstoneTtl;
        private readonly Func<DateTime> _utcNow;
        private int _retainedResponseCount;
        private long _retainedResponseBytes;

        internal CommandResultStore(
            int responseCapacity,
            TimeSpan responseTtl,
            Func<DateTime> utcNow = null,
            int tombstoneCapacity = 65536,
            long responseByteCapacity = 32L * 1024L * 1024L,
            TimeSpan? tombstoneTtl = null)
        {
            if (responseCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(responseCapacity));
            if (responseTtl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(responseTtl));
            if (tombstoneCapacity < responseCapacity) throw new ArgumentOutOfRangeException(nameof(tombstoneCapacity));
            if (responseByteCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(responseByteCapacity));
            TimeSpan resolvedTombstoneTtl = tombstoneTtl ?? TimeSpan.FromHours(24);
            if (resolvedTombstoneTtl < responseTtl)
                throw new ArgumentOutOfRangeException(nameof(tombstoneTtl));
            _responseCapacity = responseCapacity;
            _responseTtl = responseTtl;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _tombstoneCapacity = tombstoneCapacity;
            _responseByteCapacity = responseByteCapacity;
            _tombstoneTtl = resolvedTombstoneTtl;
        }

        internal CommandReservationStatus ReserveOrLookup(
            string requestId,
            string fingerprint,
            out string response,
            out CommandReservationLease lease)
        {
            lock (_sync)
            {
                PruneResponsesLocked();
                PruneTombstonesLocked();
                lease = null;
                if (_entries.TryGetValue(requestId, out Entry entry))
                {
                    if (!string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal))
                    {
                        response = null;
                        return CommandReservationStatus.Conflict;
                    }
                    if (entry.CompletedUtc == default(DateTime))
                    {
                        response = null;
                        return CommandReservationStatus.InProgress;
                    }
                    response = entry.Response;
                    return response == null
                        ? CommandReservationStatus.ResultEvicted
                        : CommandReservationStatus.Cached;
                }

                response = null;
                if (_entries.Count >= _tombstoneCapacity)
                {
                    return CommandReservationStatus.CapacityExceeded;
                }
                object token = new object();
                _entries.Add(requestId, new Entry
                {
                    RequestId = requestId,
                    Fingerprint = fingerprint,
                    CreatedUtc = _utcNow(),
                    CompletedUtc = default(DateTime),
                    LeaseToken = token,
                });
                lease = new CommandReservationLease
                {
                    RequestId = requestId,
                    Fingerprint = fingerprint,
                    Token = token,
                };
                return CommandReservationStatus.ReservedByCaller;
            }
        }

        internal void Complete(CommandReservationLease lease, string response)
        {
            if (lease == null) throw new ArgumentNullException(nameof(lease));
            lock (_sync)
            {
                if (!_entries.TryGetValue(lease.RequestId, out Entry entry))
                {
                    throw new InvalidOperationException("Command request id was not reserved before completion");
                }
                if (!string.Equals(entry.Fingerprint, lease.Fingerprint, StringComparison.Ordinal) ||
                    !ReferenceEquals(entry.LeaseToken, lease.Token))
                {
                    throw new InvalidOperationException("Command reservation lease does not own this request id");
                }
                if (entry.CompletedUtc != default(DateTime)) return;
                long responseBytes = response == null ? 0 : Encoding.UTF8.GetByteCount(response);
                entry.Response = responseBytes <= _responseByteCapacity ? response : null;
                entry.ResponseBytes = entry.Response == null ? 0 : responseBytes;
                entry.CompletedUtc = _utcNow();
                entry.LeaseToken = null;
                if (entry.Response != null)
                {
                    _retainedResponseCount++;
                    _retainedResponseBytes += entry.ResponseBytes;
                }
                _responseOrder.Enqueue(lease.RequestId);
                PruneResponsesLocked();
            }
        }

        internal void ReleaseReservation(CommandReservationLease lease)
        {
            if (lease == null) return;
            lock (_sync)
            {
                if (_entries.TryGetValue(lease.RequestId, out Entry entry) &&
                    entry.CompletedUtc == default(DateTime) &&
                    string.Equals(entry.Fingerprint, lease.Fingerprint, StringComparison.Ordinal) &&
                    ReferenceEquals(entry.LeaseToken, lease.Token))
                {
                    _entries.Remove(lease.RequestId);
                }
            }
        }

        internal bool TryGetById(
            string requestId,
            out string response,
            out DateTime completedUtc,
            out bool resultEvicted)
        {
            lock (_sync)
            {
                PruneResponsesLocked();
                PruneTombstonesLocked();
                _entries.TryGetValue(requestId, out Entry entry);
                if (entry != null && entry.CompletedUtc != default(DateTime))
                {
                    response = entry.Response;
                    completedUtc = entry.CompletedUtc;
                    resultEvicted = entry.Response == null;
                    return true;
                }
            }
            response = null;
            completedUtc = default(DateTime);
            resultEvicted = false;
            return false;
        }

        private void PruneResponsesLocked()
        {
            DateTime cutoff = _utcNow() - _responseTtl;
            while (_responseOrder.Count > 0)
            {
                string oldestId = _responseOrder.Peek();
                if (!_entries.TryGetValue(oldestId, out Entry oldest) || oldest.Response == null)
                {
                    _responseOrder.Dequeue();
                    continue;
                }
                if (_retainedResponseCount <= _responseCapacity &&
                    _retainedResponseBytes <= _responseByteCapacity &&
                    oldest.CompletedUtc >= cutoff) break;
                _responseOrder.Dequeue();
                oldest.Response = null;
                _retainedResponseCount--;
                _retainedResponseBytes -= oldest.ResponseBytes;
                oldest.ResponseBytes = 0;
            }
        }

        private void PruneTombstonesLocked()
        {
            DateTime cutoff = _utcNow() - _tombstoneTtl;
            string[] expired = _entries
                .Where(pair =>
                    pair.Value.CompletedUtc != default(DateTime) &&
                    pair.Value.CompletedUtc < cutoff)
                .Select(pair => pair.Key)
                .ToArray();
            foreach (string requestId in expired)
            {
                Entry entry = _entries[requestId];
                if (entry.Response != null)
                {
                    _retainedResponseCount--;
                    _retainedResponseBytes -= entry.ResponseBytes;
                }
                _entries.Remove(requestId);
            }
        }

        internal CommandLedgerSnapshot Snapshot()
        {
            lock (_sync)
            {
                PruneResponsesLocked();
                PruneTombstonesLocked();
                DateTime now = _utcNow();
                DateTime? oldest = _entries.Count == 0
                    ? (DateTime?)null
                    : _entries.Values.Min(entry => entry.CreatedUtc);
                return new CommandLedgerSnapshot
                {
                    TombstoneCount = _entries.Count,
                    TombstoneCapacity = _tombstoneCapacity,
                    RetainedResponseCount = _retainedResponseCount,
                    RetainedResponseBytes = _retainedResponseBytes,
                    OldestEntryAgeMs = oldest.HasValue
                        ? Math.Max(0L, (long)(now - oldest.Value).TotalMilliseconds)
                        : 0L,
                };
            }
        }

        private sealed class Entry
        {
            internal string RequestId { get; set; }
            internal string Fingerprint { get; set; }
            internal DateTime CreatedUtc { get; set; }
            internal string Response { get; set; }
            internal DateTime CompletedUtc { get; set; }
            internal long ResponseBytes { get; set; }
            internal object LeaseToken { get; set; }
        }
    }

    internal sealed class CommandLedgerSnapshot
    {
        internal int TombstoneCount { get; set; }
        internal int TombstoneCapacity { get; set; }
        internal int RetainedResponseCount { get; set; }
        internal long RetainedResponseBytes { get; set; }
        internal long OldestEntryAgeMs { get; set; }
    }
}
