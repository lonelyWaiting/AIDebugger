using System;
using System.Collections.Generic;

namespace VSDebuggerMCP.Ipc
{
    internal sealed class LongRunningDebugOperationLease : IDisposable
    {
        private LongRunningDebugOperationGate _owner;
        internal object Token { get; set; }

        internal LongRunningDebugOperationLease(LongRunningDebugOperationGate owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            LongRunningDebugOperationGate owner = System.Threading.Interlocked.Exchange(ref _owner, null);
            owner?.Release(Token);
        }
    }

    /// <summary>
    /// Coordinates one split-phase continue/wait/capture operation without holding
    /// the debugger actor during its event wait.
    /// </summary>
    internal sealed class LongRunningDebugOperationGate
    {
        private readonly object _sync = new object();
        private ActiveOperation _active;
        private readonly HashSet<object> _admittedCommands = new HashSet<object>();

        internal bool TryAcquire(string requestId, string method, out LongRunningDebugOperationLease lease)
        {
            lock (_sync)
            {
                if (_active != null || _admittedCommands.Count != 0)
                {
                    lease = null;
                    return false;
                }
                object token = new object();
                _active = new ActiveOperation
                {
                    RequestId = requestId,
                    Method = method,
                    Token = token,
                };
                lease = new LongRunningDebugOperationLease(this) { Token = token };
                return true;
            }
        }

        internal bool TryAcquireCommand(
            string method,
            string kind,
            out LongRunningDebugOperationLease lease,
            out string activeMethod)
        {
            lock (_sync)
            {
                activeMethod = _active?.Method;
                if (!string.Equals(kind, "command", StringComparison.Ordinal) ||
                    IsInterruptCommand(method))
                {
                    lease = null;
                    return true;
                }
                if (_active != null)
                {
                    lease = null;
                    return false;
                }
                object token = new object();
                _admittedCommands.Add(token);
                lease = new LongRunningDebugOperationLease(this) { Token = token };
                return true;
            }
        }

        internal bool TryGetConflict(string method, string kind, out string activeMethod)
        {
            lock (_sync)
            {
                activeMethod = _active?.Method;
                if (_active == null || !string.Equals(kind, "command", StringComparison.Ordinal))
                    return false;
                return !IsInterruptCommand(method);
            }
        }

        private static bool IsInterruptCommand(string method) =>
            string.Equals(method, "break", StringComparison.Ordinal) ||
            string.Equals(method, "stop", StringComparison.Ordinal) ||
            string.Equals(method, "detach", StringComparison.Ordinal) ||
            string.Equals(method, "cancel_request", StringComparison.Ordinal);

        internal void Release(object token)
        {
            lock (_sync)
            {
                if (_active != null && ReferenceEquals(_active.Token, token))
                    _active = null;
                else
                    _admittedCommands.Remove(token);
            }
        }

        private sealed class ActiveOperation
        {
            internal string RequestId { get; set; }
            internal string Method { get; set; }
            internal object Token { get; set; }
        }
    }
}
