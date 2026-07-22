using System;
using System.Collections.Generic;
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
    /// Keeps server-instance-lifetime at-most-once tombstones keyed by request id.
    /// Response bodies are bounded and may be evicted, but tombstones are never
    /// evicted: once the ledger is full, new commands are rejected until the
    /// server restarts rather than risking a duplicate side effect.
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
        private readonly Func<DateTime> _utcNow;
        private int _retainedResponseCount;
        private long _retainedResponseBytes;

        internal CommandResultStore(
            int responseCapacity,
            TimeSpan responseTtl,
            Func<DateTime> utcNow = null,
            int tombstoneCapacity = 65536,
            long responseByteCapacity = 32L * 1024L * 1024L)
        {
            if (responseCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(responseCapacity));
            if (responseTtl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(responseTtl));
            if (tombstoneCapacity < responseCapacity) throw new ArgumentOutOfRangeException(nameof(tombstoneCapacity));
            if (responseByteCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(responseByteCapacity));
            _responseCapacity = responseCapacity;
            _responseTtl = responseTtl;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _tombstoneCapacity = tombstoneCapacity;
            _responseByteCapacity = responseByteCapacity;
        }

        internal CommandReservationStatus ReserveOrLookup(
            string requestId,
            string fingerprint,
            out string response,
            out CommandReservationLease lease)
        {
            return ReserveOrLookup("test-client", requestId, fingerprint, out response, out lease);
        }

        internal CommandReservationStatus ReserveOrLookup(
            string clientLane,
            string requestId,
            string fingerprint,
            out string response,
            out CommandReservationLease lease)
        {
            if (string.IsNullOrWhiteSpace(clientLane)) throw new ArgumentException("Client lane is required", nameof(clientLane));
            lock (_sync)
            {
                PruneResponsesLocked();
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
                    ClientLane = clientLane,
                    RequestId = requestId,
                    Fingerprint = fingerprint,
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
            return TryGetById("test-client", requestId, out response, out completedUtc, out resultEvicted);
        }

        internal bool TryGetById(
            string clientLane,
            string requestId,
            out string response,
            out DateTime completedUtc,
            out bool resultEvicted)
        {
            lock (_sync)
            {
                PruneResponsesLocked();
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

        private sealed class Entry
        {
            internal string ClientLane { get; set; }
            internal string RequestId { get; set; }
            internal string Fingerprint { get; set; }
            internal string Response { get; set; }
            internal DateTime CompletedUtc { get; set; }
            internal long ResponseBytes { get; set; }
            internal object LeaseToken { get; set; }
        }
    }
}
