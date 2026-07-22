using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace VSDebuggerMCP.Services
{
    internal sealed class CancelableSingleFlight : IDisposable
    {
        private readonly object _sync = new object();
        private CancellationTokenSource _cancellation;
        private Task _task = Task.CompletedTask;
        private bool _disposed;

        public bool TryStart(Func<CancellationToken, Task> operation, out Task task)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            lock (_sync)
            {
                if (_disposed)
                {
                    task = _task;
                    return false;
                }
                if (!_task.IsCompleted)
                {
                    task = _task;
                    return false;
                }

                _cancellation?.Dispose();
                _cancellation = new CancellationTokenSource();
                try
                {
                    _task = operation(_cancellation.Token) ?? Task.CompletedTask;
                }
                catch (Exception exception)
                {
                    _task = Task.FromException(exception);
                }
                task = _task;
                return true;
            }
        }

        [SuppressMessage("Usage", "VSTHRD003", Justification = "The task is returned to the shutdown owner, which observes it with a bounded wait rather than synchronously joining it.")]
        public Task CancelAndGetTaskAsync()
        {
            lock (_sync)
            {
                _cancellation?.Cancel();
                return _task;
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                _cancellation?.Cancel();
                _cancellation?.Dispose();
                _cancellation = null;
            }
        }
    }
}
