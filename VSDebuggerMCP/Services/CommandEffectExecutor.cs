using System;

namespace VSDebuggerMCP.Services
{
    /// <summary>
    /// The only boundary allowed to issue debugger or operating-system effects.
    /// It records the command as issued immediately before crossing the external
    /// boundary and confirms it only after the call returns successfully.
    /// </summary>
    internal static class CommandEffectExecutor
    {
        internal static void Issue(string operation, Action effect)
        {
            if (effect == null) throw new ArgumentNullException(nameof(effect));
            EnsureCommand(operation);
            DebuggerRequestContext.MarkSideEffectIssued(operation);
            UiOperationCircuit.ProcessWide.RecordEffectStage(
                DebuggerRequestContext.RequestId, operation, "issued");
            effect();
            DebuggerRequestContext.MarkSideEffectConfirmed(operation);
            UiOperationCircuit.ProcessWide.RecordEffectStage(
                DebuggerRequestContext.RequestId, operation, "confirmed");
        }

        internal static T Issue<T>(string operation, Func<T> effect)
        {
            if (effect == null) throw new ArgumentNullException(nameof(effect));
            EnsureCommand(operation);
            DebuggerRequestContext.MarkSideEffectIssued(operation);
            UiOperationCircuit.ProcessWide.RecordEffectStage(
                DebuggerRequestContext.RequestId, operation, "issued");
            T result = effect();
            DebuggerRequestContext.MarkSideEffectConfirmed(operation);
            UiOperationCircuit.ProcessWide.RecordEffectStage(
                DebuggerRequestContext.RequestId, operation, "confirmed");
            return result;
        }

        internal static void RecordIssued(string operation)
        {
            EnsureCommand(operation);
            DebuggerRequestContext.MarkSideEffectIssued(operation);
            UiOperationCircuit.ProcessWide.RecordEffectStage(
                DebuggerRequestContext.RequestId, operation, "issued");
        }

        internal static void RecordConfirmed(string operation)
        {
            EnsureCommand(operation);
            DebuggerRequestContext.MarkSideEffectConfirmed(operation);
            UiOperationCircuit.ProcessWide.RecordEffectStage(
                DebuggerRequestContext.RequestId, operation, "confirmed");
        }

        private static void EnsureCommand(string operation)
        {
            if (string.IsNullOrWhiteSpace(operation))
                throw new ArgumentException("An effect operation name is required.", nameof(operation));
            if (!DebuggerRequestContext.IsCommand)
                throw new InvalidOperationException(
                    $"Effect '{operation}' was attempted outside an authenticated command request.");
        }
    }
}
