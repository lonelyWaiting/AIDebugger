using System;

namespace VSDebuggerMCP.Services
{
    internal sealed class PostLaunchCaptureResult<T>
    {
        internal bool ProcessStarted { get; set; }
        internal T Value { get; set; }
        internal Exception CaptureError { get; set; }
    }

    /// <summary>
    /// Establishes the at-most-once boundary immediately after an external launch
    /// API returns a non-null process object, before any fallible property access.
    /// </summary>
    internal static class ProcessLaunchBoundary
    {
        internal static PostLaunchCaptureResult<TValue> CaptureAfterStart<TProcess, TValue>(
            TProcess process,
            Action markProcessStarted,
            Func<TProcess, TValue> captureIdentity)
            where TProcess : class
        {
            if (markProcessStarted == null) throw new ArgumentNullException(nameof(markProcessStarted));
            if (captureIdentity == null) throw new ArgumentNullException(nameof(captureIdentity));
            if (process == null) return new PostLaunchCaptureResult<TValue>();

            markProcessStarted();
            try
            {
                return new PostLaunchCaptureResult<TValue>
                {
                    ProcessStarted = true,
                    Value = captureIdentity(process),
                };
            }
            catch (Exception ex)
            {
                return new PostLaunchCaptureResult<TValue>
                {
                    ProcessStarted = true,
                    CaptureError = ex,
                };
            }
        }
    }
}
