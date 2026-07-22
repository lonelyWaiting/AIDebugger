using System.Threading;

namespace VSDebuggerMCP.Services
{
    /// <summary>
    /// Atomically arbitrates the only irreversible boundary of a queued UI call.
    /// Exactly one of start or cancel-before-start can win.
    /// </summary>
    internal sealed class UiOperationAdmission
    {
        private const int Queued = 0;
        private const int StartedValue = 1;
        private const int CancelledValue = 2;
        private int _state = Queued;

        internal bool Started => Volatile.Read(ref _state) == StartedValue;
        internal bool TryStart() =>
            Interlocked.CompareExchange(ref _state, StartedValue, Queued) == Queued;
        internal bool TryCancelBeforeStart() =>
            Interlocked.CompareExchange(ref _state, CancelledValue, Queued) == Queued;
    }
}
