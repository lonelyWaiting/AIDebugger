using System;
using System.Collections.Generic;

namespace VSDebuggerMCP.Services
{
    internal sealed class DebugProcessIdentity
    {
        internal int ProcessId { get; set; }
        internal long ProcessStartTimeUtcTicks { get; set; }
        internal long DebugSessionGeneration { get; set; }
    }

    /// <summary>
    /// Session-scoped opaque handles prevent a stale PID from selecting a newly
    /// created process after PID reuse. Handles are invalidated on generation
    /// changes and never resolve without immutable OS process start time.
    /// </summary>
    internal sealed class DebugProcessHandleStore
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, DebugProcessIdentity> _byHandle =
            new Dictionary<string, DebugProcessIdentity>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _byIdentity =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly int _capacity;
        private long _generation = -1;

        internal DebugProcessHandleStore(int capacity = 512)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
        }

        internal string GetOrCreate(
            int processId,
            long processStartTimeUtcTicks,
            long debugSessionGeneration)
        {
            if (processId <= 0 || processStartTimeUtcTicks <= 0 || debugSessionGeneration <= 0)
                return null;
            lock (_sync)
            {
                ResetGenerationLocked(debugSessionGeneration);
                string identityKey = processId + ":" + processStartTimeUtcTicks;
                if (_byIdentity.TryGetValue(identityKey, out string existing)) return existing;
                if (_byHandle.Count >= _capacity)
                {
                    _byHandle.Clear();
                    _byIdentity.Clear();
                }
                string handle = "dph_" + Guid.NewGuid().ToString("N");
                _byHandle.Add(handle, new DebugProcessIdentity
                {
                    ProcessId = processId,
                    ProcessStartTimeUtcTicks = processStartTimeUtcTicks,
                    DebugSessionGeneration = debugSessionGeneration,
                });
                _byIdentity.Add(identityKey, handle);
                return handle;
            }
        }

        internal bool TryResolve(
            string handle,
            long currentDebugSessionGeneration,
            out DebugProcessIdentity identity)
        {
            lock (_sync)
            {
                ResetGenerationLocked(currentDebugSessionGeneration);
                if (!string.IsNullOrWhiteSpace(handle) &&
                    _byHandle.TryGetValue(handle, out DebugProcessIdentity stored))
                {
                    identity = new DebugProcessIdentity
                    {
                        ProcessId = stored.ProcessId,
                        ProcessStartTimeUtcTicks = stored.ProcessStartTimeUtcTicks,
                        DebugSessionGeneration = stored.DebugSessionGeneration,
                    };
                    return true;
                }
            }
            identity = null;
            return false;
        }

        private void ResetGenerationLocked(long generation)
        {
            if (_generation == generation) return;
            _generation = generation;
            _byHandle.Clear();
            _byIdentity.Clear();
        }
    }
}
