using System;

namespace VSDebuggerMCP.Ipc
{
    internal static class ControlRequestValidator
    {
        internal static bool IsSelfCancellation(string currentRequestId, string targetRequestId) =>
            string.Equals(currentRequestId, targetRequestId, StringComparison.Ordinal);
    }
}
