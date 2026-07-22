using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

namespace VSDebuggerMCP.Services
{
    internal interface IUiOperationTracker
    {
        bool HasPendingOperations { get; }
        Task WaitForIdleAsync();
    }

    internal interface IVisualStudioDebuggerAdapter : IUiOperationTracker
    {
        T ExecuteOnMainThread<T>(
            Func<T> action,
            int timeoutMs,
            CancellationToken requestCancellation);
    }

    /// <summary>
    /// Owns Visual Studio UI scheduling and the lifetime of every accepted COM
    /// operation. The debugger facade supplies domain/context validation inside
    /// the callback; transport and shutdown can reason about this tracker alone.
    /// </summary>
    internal sealed class VisualStudioDebuggerAdapter : IVisualStudioDebuggerAdapter
    {
        private readonly object _sync = new object();
        private readonly HashSet<Task> _pending = new HashSet<Task>();
        private int _operationCircuitOpen;

        public bool HasPendingOperations
        {
            get { lock (_sync) return _pending.Count != 0; }
        }

        public T ExecuteOnMainThread<T>(
            Func<T> action,
            int timeoutMs,
            CancellationToken requestCancellation)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (timeoutMs <= 0)
                throw new MainThreadDispatchTimeoutException(
                    "Request deadline expired before the UI operation was dispatched.");

            if (ThreadHelper.CheckAccess())
            {
                requestCancellation.ThrowIfCancellationRequested();
                return action();
            }
            if (Volatile.Read(ref _operationCircuitOpen) != 0)
                throw new MainThreadDispatchTimeoutException(
                    "A previous Visual Studio UI operation is still running; this request was not started.");

            var admission = new UiOperationAdmission();
            using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs)))
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                timeoutCts.Token,
                requestCancellation))
            {
                JoinableTask<T> operation = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(linkedCts.Token);
                    linkedCts.Token.ThrowIfCancellationRequested();
                    if (!admission.TryStart())
                    {
                        throw new OperationCanceledException(linkedCts.Token);
                    }
                    return action();
                });
                Task<T> task = operation.Task;
                Track(task);
                try
                {
                    if (!((IAsyncResult)task).AsyncWaitHandle.WaitOne(timeoutMs))
                    {
                        if (admission.TryCancelBeforeStart())
                        {
                            linkedCts.Cancel();
                            throw new MainThreadDispatchTimeoutException(
                                $"VS UI thread did not accept the operation within {timeoutMs}ms; the operation was not started.");
                        }
                        Interlocked.Exchange(ref _operationCircuitOpen, 1);
                    }

                    // Once accepted, the complete debugger invocation remains owned by
                    // the actor until the COM call becomes definitive. The transport has
                    // its own bounded wait and returns OUTCOME_UNKNOWN while this worker
                    // remains joined, so no request thread or VS shutdown path blocks.
                    return operation.Join();
                }
                catch (OperationCanceledException) when (
                    !admission.Started)
                {
                    admission.TryCancelBeforeStart();
                    if (requestCancellation.IsCancellationRequested)
                        throw new RequestCancelledNotStartedException(
                            "Request was cancelled before the Visual Studio UI operation started.");
                    throw new MainThreadDispatchTimeoutException(
                        $"VS UI thread did not accept the operation within {timeoutMs}ms; the operation was not started.");
                }
                finally
                {
                    if (task.IsCompleted)
                    {
                        Complete(task);
                    }
                    else
                    {
                        Task cleanup = task.ContinueWith(
                            Complete,
                            CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                    }
                }
            }
        }

        public Task WaitForIdleAsync()
        {
            lock (_sync)
            {
                if (_pending.Count == 0) return Task.CompletedTask;
                return Task.WhenAll(new List<Task>(_pending));
            }
        }

        private void Track(Task task)
        {
            lock (_sync) _pending.Add(task);
        }

        private void Complete(Task task)
        {
            if (task.IsFaulted)
            {
                AggregateException ignored = task.Exception;
            }
            lock (_sync) _pending.Remove(task);
            Interlocked.Exchange(ref _operationCircuitOpen, 0);
            try
            {
                task.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // A defensive second completion callback cannot retain a wait handle.
            }
        }
    }
}
