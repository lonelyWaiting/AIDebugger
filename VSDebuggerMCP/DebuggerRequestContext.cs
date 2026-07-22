using System;
using System.Threading;

namespace VSDebuggerMCP
{
    internal static class DebuggerRequestContext
    {
        private static readonly AsyncLocal<RequestState> CurrentState = new AsyncLocal<RequestState>();

        public static long ExpectedDebugSessionGeneration => CurrentState.Value?.ExpectedDebugSessionGeneration ?? 0;
        public static bool RequiresStableDebugSession => CurrentState.Value?.RequiresStableDebugSession ?? false;
        public static long ExpectedBreakSequence => CurrentState.Value?.ExpectedBreakSequence ?? 0;
        public static long ExpectedContextEpoch => CurrentState.Value?.ExpectedContextEpoch ?? 0;
        public static bool RequiresStableDebugContext => CurrentState.Value?.RequiresStableDebugContext ?? false;
        public static CancellationToken CancellationToken => CurrentState.Value?.CancellationToken ?? System.Threading.CancellationToken.None;
        public static bool IsCommand => string.Equals(CurrentState.Value?.Kind, "command", StringComparison.Ordinal);
        public static string RequestId => CurrentState.Value?.RequestId;
        public static string Method => CurrentState.Value?.Method;
        public static bool MayEvaluateUserCode => CurrentState.Value?.MayEvaluateUserCode ?? false;
        public static bool SideEffectIssued => CurrentState.Value?.SideEffectIssued ?? false;
        public static string EffectOperation => CurrentState.Value?.EffectOperation;
        public static string EffectStage => CurrentState.Value?.EffectStage ?? "none";

        public static bool MarkUnsafeEvaluationIssued(Action invalidateContext)
        {
            RequestState state = CurrentState.Value;
            if (state == null || !state.MayEvaluateUserCode) return false;
            if (!state.UnsafeEvaluationContextInvalidated)
            {
                state.UnsafeEvaluationContextInvalidated = true;
                invalidateContext?.Invoke();
            }
            MarkSideEffectIssued("debugger.evaluate_user_code");
            return true;
        }

        internal static void MarkSideEffectIssued(string operation)
        {
            RequestState state = CurrentState.Value;
            if (state != null && string.Equals(state.Kind, "command", StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(operation))
                {
                    state.EffectOperation = operation;
                }
                state.EffectStage = "issued";
                if (!state.SideEffectIssued)
                {
                    state.SideEffectIssued = true;
                    state.OnSideEffectIssued?.Invoke();
                }
                state.OnEffectStageChanged?.Invoke(state.EffectOperation, state.EffectStage);
            }
        }

        internal static void MarkSideEffectConfirmed(string operation)
        {
            RequestState state = CurrentState.Value;
            if (state == null || !string.Equals(state.Kind, "command", StringComparison.Ordinal))
            {
                return;
            }
            if (!state.SideEffectIssued)
            {
                throw new InvalidOperationException(
                    "A command effect cannot be confirmed before it is issued.");
            }
            if (!string.IsNullOrWhiteSpace(operation))
            {
                state.EffectOperation = operation;
            }
            state.EffectStage = "confirmed";
            state.OnEffectStageChanged?.Invoke(state.EffectOperation, state.EffectStage);
        }

        public static DebuggerIdentitySnapshot CaptureIdentity()
        {
            RequestState state = CurrentState.Value;
            return state == null
                ? null
                : new DebuggerIdentitySnapshot
                {
                    DebugSessionGeneration = state.ExpectedDebugSessionGeneration,
                    BreakSequence = state.ExpectedBreakSequence,
                    ContextEpoch = state.ExpectedContextEpoch,
                };
        }

        public static void RecordCompletionCandidate(
            long debugSessionGeneration,
            long breakSequence,
            long contextEpoch)
        {
            RequestState state = CurrentState.Value;
            if (state == null) return;
            state.CompletionCandidate = new DebuggerIdentitySnapshot
            {
                DebugSessionGeneration = debugSessionGeneration,
                BreakSequence = breakSequence,
                ContextEpoch = contextEpoch,
            };
        }

        public static DebuggerIdentitySnapshot CaptureCompletionIdentity()
        {
            RequestState state = CurrentState.Value;
            if (state?.CompletionCandidate != null)
            {
                return new DebuggerIdentitySnapshot
                {
                    DebugSessionGeneration = state.CompletionCandidate.DebugSessionGeneration,
                    BreakSequence = state.CompletionCandidate.BreakSequence,
                    ContextEpoch = state.CompletionCandidate.ContextEpoch,
                };
            }
            return CaptureIdentity();
        }

        public static bool HasCompletionCandidate => CurrentState.Value?.CompletionCandidate != null;

        public static int RemainingMilliseconds(int fallbackMilliseconds)
        {
            RequestState state = CurrentState.Value;
            if (state == null || state.DeadlineUnixMs <= 0)
            {
                return fallbackMilliseconds;
            }

            long remaining = state.DeadlineUnixMs - UtcNowUnixMilliseconds();
            if (remaining <= 0)
            {
                return 0;
            }
            return (int)Math.Min(fallbackMilliseconds, Math.Min(int.MaxValue, remaining));
        }

        public static void ThrowIfCancellationOrDeadlineExceeded()
        {
            CancellationToken.ThrowIfCancellationRequested();
            RequestState state = CurrentState.Value;
            if (state != null && state.DeadlineUnixMs > 0 &&
                state.DeadlineUnixMs <= UtcNowUnixMilliseconds())
            {
                throw new TimeoutException("Request deadline expired while the debugger operation was running.");
            }
        }

        public static void RebaseDebuggerIdentity(
            long debugSessionGeneration,
            long breakSequence,
            long contextEpoch)
        {
            RequestState state = CurrentState.Value;
            if (state == null)
            {
                return;
            }
            state.ExpectedDebugSessionGeneration = debugSessionGeneration;
            state.ExpectedBreakSequence = breakSequence;
            state.ExpectedContextEpoch = contextEpoch;
            RecordCompletionCandidate(debugSessionGeneration, breakSequence, contextEpoch);
        }

        public static IDisposable Push(
            long deadlineUnixMs,
            string requestId,
            string kind,
            long expectedDebugSessionGeneration,
            bool requiresStableDebugSession,
            long expectedBreakSequence,
            long expectedContextEpoch,
            bool requiresStableDebugContext,
            CancellationToken cancellationToken,
            bool mayEvaluateUserCode = false,
            Action onSideEffectIssued = null,
            Action<string, string> onEffectStageChanged = null,
            string method = null)
        {
            RequestState previous = CurrentState.Value;
            CurrentState.Value = new RequestState
            {
                DeadlineUnixMs = deadlineUnixMs,
                RequestId = requestId,
                Method = method,
                Kind = kind,
                ExpectedDebugSessionGeneration = expectedDebugSessionGeneration,
                RequiresStableDebugSession = requiresStableDebugSession,
                ExpectedBreakSequence = expectedBreakSequence,
                ExpectedContextEpoch = expectedContextEpoch,
                RequiresStableDebugContext = requiresStableDebugContext,
                CancellationToken = cancellationToken,
                MayEvaluateUserCode = mayEvaluateUserCode,
                OnSideEffectIssued = onSideEffectIssued,
                OnEffectStageChanged = onEffectStageChanged,
            };
            return new Scope(() => CurrentState.Value = previous);
        }

        private static long UtcNowUnixMilliseconds()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        private sealed class RequestState
        {
            public long DeadlineUnixMs { get; set; }
            public string RequestId { get; set; }
            public string Method { get; set; }
            public string Kind { get; set; }
            public long ExpectedDebugSessionGeneration { get; set; }
            public bool RequiresStableDebugSession { get; set; }
            public long ExpectedBreakSequence { get; set; }
            public long ExpectedContextEpoch { get; set; }
            public bool RequiresStableDebugContext { get; set; }
            public CancellationToken CancellationToken { get; set; }
            public DebuggerIdentitySnapshot CompletionCandidate { get; set; }
            public bool SideEffectIssued { get; set; }
            public bool MayEvaluateUserCode { get; set; }
            public bool UnsafeEvaluationContextInvalidated { get; set; }
            public Action OnSideEffectIssued { get; set; }
            public string EffectOperation { get; set; }
            public string EffectStage { get; set; }
            public Action<string, string> OnEffectStageChanged { get; set; }
        }

        private sealed class Scope : IDisposable
        {
            private Action _dispose;

            public Scope(Action dispose)
            {
                _dispose = dispose;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _dispose, null)?.Invoke();
            }
        }
    }

    internal sealed class DebuggerIdentitySnapshot
    {
        public long DebugSessionGeneration { get; set; }
        public long BreakSequence { get; set; }
        public long ContextEpoch { get; set; }
    }
}
