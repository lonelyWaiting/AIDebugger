using System;
using System.Collections.Generic;
using System.Text;

namespace VSDebuggerMCP.Ipc
{
    /// <summary>
    /// Short-lived recovery journal for query responses that finish after the
    /// transport deadline. Unlike the command ledger it has no at-most-once
    /// semantics; entries are bounded by TTL, count, and UTF-8 bytes.
    /// </summary>
    internal sealed class QueryResultStore
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Queue<string> _order = new Queue<string>();
        private readonly int _capacity;
        private readonly long _byteCapacity;
        private readonly TimeSpan _ttl;
        private readonly Func<DateTime> _utcNow;
        private long _retainedBytes;

        internal QueryResultStore(
            int capacity,
            long byteCapacity,
            TimeSpan ttl,
            Func<DateTime> utcNow = null)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (byteCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(byteCapacity));
            if (ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl));
            _capacity = capacity;
            _byteCapacity = byteCapacity;
            _ttl = ttl;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        internal void Complete(string requestId, string response)
        {
            if (string.IsNullOrWhiteSpace(requestId) || response == null) return;
            long bytes = Encoding.UTF8.GetByteCount(response);
            if (bytes > _byteCapacity) return;
            lock (_sync)
            {
                PruneLocked();
                if (_entries.TryGetValue(requestId, out Entry existing))
                {
                    _retainedBytes -= existing.ResponseBytes;
                }
                else
                {
                    _order.Enqueue(requestId);
                }
                _entries[requestId] = new Entry
                {
                    Response = response,
                    ResponseBytes = bytes,
                    CompletedUtc = _utcNow(),
                };
                _retainedBytes += bytes;
                PruneLocked();
            }
        }

        internal bool TryGet(
            string requestId,
            out string response,
            out DateTime completedUtc)
        {
            lock (_sync)
            {
                PruneLocked();
                if (_entries.TryGetValue(requestId, out Entry entry))
                {
                    response = entry.Response;
                    completedUtc = entry.CompletedUtc;
                    return true;
                }
            }
            response = null;
            completedUtc = default(DateTime);
            return false;
        }

        internal QueryJournalSnapshot Snapshot()
        {
            lock (_sync)
            {
                PruneLocked();
                return new QueryJournalSnapshot
                {
                    EntryCount = _entries.Count,
                    EntryCapacity = _capacity,
                    RetainedBytes = _retainedBytes,
                    ByteCapacity = _byteCapacity,
                };
            }
        }

        private void PruneLocked()
        {
            DateTime cutoff = _utcNow() - _ttl;
            while (_order.Count > 0)
            {
                string oldestId = _order.Peek();
                if (!_entries.TryGetValue(oldestId, out Entry oldest))
                {
                    _order.Dequeue();
                    continue;
                }
                if (_entries.Count <= _capacity &&
                    _retainedBytes <= _byteCapacity &&
                    oldest.CompletedUtc >= cutoff)
                {
                    break;
                }
                _order.Dequeue();
                _entries.Remove(oldestId);
                _retainedBytes -= oldest.ResponseBytes;
            }
        }

        private sealed class Entry
        {
            internal string Response { get; set; }
            internal long ResponseBytes { get; set; }
            internal DateTime CompletedUtc { get; set; }
        }
    }

    internal sealed class QueryJournalSnapshot
    {
        internal int EntryCount { get; set; }
        internal int EntryCapacity { get; set; }
        internal long RetainedBytes { get; set; }
        internal long ByteCapacity { get; set; }
    }
}
