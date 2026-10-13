using System;
using VSDebuggerMCP.Protocol;

namespace VSDebuggerMCP.Ipc
{
    internal static class OperationResultPolicy
    {
        internal static void RecordCompletedStepCaptureFailure(
            DebugControlResult result,
            Exception captureError)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (captureError == null) throw new ArgumentNullException(nameof(captureError));
            if (!result.Success || !result.CommandIssued || result.Outcome != "step_completed")
                throw new InvalidOperationException("Only an observed completed step may retain success after capture failure.");

            result.Outcome = "step_completed_capture_unavailable";
            result.Message += "; source-location capture was unavailable: " + captureError.Message;
        }

        /// <summary>
        /// A debugger wait timeout is a completed command result, not a transport or
        /// dispatch failure. Preserve issuedContinue, current status, and request-ledger
        /// semantics so callers can make a non-replaying recovery decision.
        /// </summary>
        internal static bool ShouldReturnStructuredResult(object result)
        {
            return (result is ContinueAndWaitResult wait && (wait.TimedOut || wait.IssuedContinue)) ||
                   (result is NativeContinueResult native && (native.TimedOut || native.IssuedContinue)) ||
                   (result is BreakpointCaptureResult capture && (capture.TimedOut || capture.IssuedContinue)) ||
                   (result is ProcessDebugResult process && (process.ProcessStarted || process.CommandIssued || process.UiNavigationIssued)) ||
                   (result is BreakpointOperationResult breakpoint && breakpoint.CommandIssued) ||
                   (result is WatchResult watch && (watch.CommandIssued || watch.EvaluationIssued)) ||
                   (result is DebugControlResult control && (control.CommandIssued || control.UiNavigationIssued));
        }

        internal static bool WasCommandIssued(object result)
        {
            return (result is ContinueAndWaitResult wait && wait.IssuedContinue) ||
                   (result is NativeContinueResult native && native.IssuedContinue) ||
                   (result is BreakpointCaptureResult capture && capture.IssuedContinue) ||
                   (result is ProcessDebugResult process && (process.CommandIssued || process.ProcessStarted || process.UiNavigationIssued)) ||
                   (result is BreakpointOperationResult breakpoint && breakpoint.CommandIssued) ||
                   (result is WatchResult watch && (watch.CommandIssued || watch.EvaluationIssued)) ||
                   (result is DebugControlResult control && (control.CommandIssued || control.UiNavigationIssued));
        }

        internal static string NormalizeFailureCode(object result, string errorCode)
        {
            return string.Equals(errorCode, "OUTCOME_UNKNOWN", System.StringComparison.Ordinal) &&
                   !WasCommandIssued(result)
                ? "COMMAND_NOT_STARTED"
                : errorCode;
        }
    }
}
