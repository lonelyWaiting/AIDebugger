using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace VSDebuggerMCP.Ipc
{
    /// <summary>
    /// Bounded FIFO actor that is the sole execution owner for mutable/live
    /// Visual Studio debugger requests. Transport listeners only validate and
    /// enqueue work; debugger state is never entered concurrently.
    /// </summary>
    internal sealed class DebugSessionActor : IDisposable
    {
        private readonly ConcurrentQueue<IWorkItem> _queue = new ConcurrentQueue<IWorkItem>();
        private readonly SemaphoreSlim _queuedItems = new SemaphoreSlim(0);
        private readonly SemaphoreSlim _availableSlots;
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private readonly Task _worker;
        private readonly object _lifecycleSync = new object();
        private readonly Action _beforeFinalAdmissionForTest;
        private int _stopped;
        private int _disposeRequested;
        private int _activeEnqueueEntrants;
        private TaskCompletionSource<bool> _enqueueEntrantsDrained;

        internal DebugSessionActor(int capacity) : this(capacity, null)
        {
        }

        internal DebugSessionActor(int capacity, Action beforeFinalAdmissionForTest)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _beforeFinalAdmissionForTest = beforeFinalAdmissionForTest;
            _availableSlots = new SemaphoreSlim(capacity, capacity);
            _worker = Task.Run(ProcessQueueAsync);
        }

        internal async Task<T> EnqueueAsync<T>(
            Func<T> operation,
            long deadlineUnixMs,
            CancellationToken cancellationToken)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            EnterEnqueue();
            try
            {
                return await EnqueueCoreAsync(operation, deadlineUnixMs, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                ExitEnqueue();
            }
        }

        private async Task<T> EnqueueCoreAsync<T>(
            Func<T> operation,
            long deadlineUnixMs,
            CancellationToken cancellationToken)
        {

            int waitMilliseconds = RemainingMilliseconds(deadlineUnixMs);
            bool admitted;
            using (var admissionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _shutdown.Token))
            {
                try
                {
                    admitted = waitMilliseconds > 0 &&
                        await _availableSlots.WaitAsync(waitMilliseconds, admissionCancellation.Token)
                            .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    _shutdown.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("The debugger actor is stopping.");
                }
            }
            if (!admitted)
            {
                throw new MainThreadDispatchTimeoutException(
                    "Request deadline expired while waiting for space in the debugger actor queue; the operation was not started.");
            }

            var item = new WorkItem<T>(operation, deadlineUnixMs, cancellationToken);
            _beforeFinalAdmissionForTest?.Invoke();
            lock (_lifecycleSync)
            {
                if (_stopped != 0)
                {
                    _availableSlots.Release();
                    throw new OperationCanceledException("The debugger actor is stopping.", cancellationToken);
                }

                _queue.Enqueue(item);
                _queuedItems.Release();
            }
            return await item.Completion.ConfigureAwait(false);
        }

        internal void Stop()
        {
            lock (_lifecycleSync)
            {
                if (_stopped != 0) return;
                _stopped = 1;
            }
            _shutdown.Cancel();
            _queuedItems.Release();
        }

        internal Task Completion => _worker;

        internal int QueueDepth => _queue.Count;

        private async Task ProcessQueueAsync()
        {
            try
            {
                while (true)
                {
                    await _queuedItems.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                    if (!_queue.TryDequeue(out IWorkItem item))
                    {
                        if (_shutdown.IsCancellationRequested) break;
                        continue;
                    }
                    _availableSlots.Release();
                    item.Execute();
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                // Normal shutdown.
            }
            finally
            {
                while (_queue.TryDequeue(out IWorkItem item))
                {
                    _availableSlots.Release();
                    item.CancelNotStarted();
                }
            }
        }

        private static int RemainingMilliseconds(long deadlineUnixMs)
        {
            if (deadlineUnixMs <= 0) return int.MaxValue;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long remaining = deadlineUnixMs - now;
            return remaining <= 0 ? 0 : (int)Math.Min(int.MaxValue, remaining);
        }

        private void ThrowIfStopped()
        {
            if (Volatile.Read(ref _stopped) != 0)
                throw new OperationCanceledException("The debugger actor is stopping.");
        }

        public void Dispose()
        {
            Task enqueueEntrantsCompletion;
            bool signalStop = false;
            lock (_lifecycleSync)
            {
                if (_disposeRequested != 0) return;
                _disposeRequested = 1;
                if (_stopped == 0)
                {
                    _stopped = 1;
                    signalStop = true;
                }
                if (_activeEnqueueEntrants == 0)
                {
                    enqueueEntrantsCompletion = Task.CompletedTask;
                }
                else
                {
                    _enqueueEntrantsDrained = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    enqueueEntrantsCompletion = _enqueueEntrantsDrained.Task;
                }
            }
            if (signalStop)
            {
                _shutdown.Cancel();
                _queuedItems.Release();
            }
            _ = Task.WhenAll(_worker, enqueueEntrantsCompletion).ContinueWith(
                _ =>
                {
                    _shutdown.Dispose();
                    _queuedItems.Dispose();
                    _availableSlots.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void EnterEnqueue()
        {
            lock (_lifecycleSync)
            {
                if (_stopped != 0 || _disposeRequested != 0)
                    throw new OperationCanceledException("The debugger actor is stopping.");
                _activeEnqueueEntrants++;
            }
        }

        private void ExitEnqueue()
        {
            TaskCompletionSource<bool> drained = null;
            lock (_lifecycleSync)
            {
                _activeEnqueueEntrants--;
                if (_activeEnqueueEntrants == 0)
                    drained = _enqueueEntrantsDrained;
            }
            drained?.TrySetResult(true);
        }

        private interface IWorkItem
        {
            void Execute();
            void CancelNotStarted();
        }

        private sealed class WorkItem<T> : IWorkItem
        {
            private readonly Func<T> _operation;
            private readonly long _deadlineUnixMs;
            private readonly CancellationToken _cancellationToken;
            private readonly TaskCompletionSource<T> _completion =
                new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            internal WorkItem(Func<T> operation, long deadlineUnixMs, CancellationToken cancellationToken)
            {
                _operation = operation;
                _deadlineUnixMs = deadlineUnixMs;
                _cancellationToken = cancellationToken;
            }

            internal Task<T> Completion => _completion.Task;

            public void Execute()
            {
                if (_cancellationToken.IsCancellationRequested)
                {
                    _completion.TrySetException(new RequestCancelledNotStartedException(
                        "Request was cancelled while queued and the debugger operation was not started."));
                    return;
                }
                if (RemainingMilliseconds(_deadlineUnixMs) <= 0)
                {
                    _completion.TrySetException(new MainThreadDispatchTimeoutException(
                        "Request deadline expired in the debugger actor queue; the operation was not started."));
                    return;
                }
                try
                {
                    _completion.TrySetResult(_operation());
                }
                catch (Exception ex)
                {
                    _completion.TrySetException(ex);
                }
            }

            public void CancelNotStarted()
            {
                _completion.TrySetException(new RequestCancelledNotStartedException(
                    "The debugger actor stopped before the request started."));
            }
        }
    }
}
