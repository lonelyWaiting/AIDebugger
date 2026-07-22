using System;
using VSDebuggerMCP.Protocol;

namespace VSDebuggerMCP.Services
{
    internal static class SnapshotCompletenessPolicy
    {
        internal static bool IsIncomplete(CallStackResult value)
        {
            return value == null || value.Partial || !string.IsNullOrEmpty(value.Error) || !value.SnapshotStable ||
                !string.Equals(value.SnapshotStatus, "ok", StringComparison.Ordinal);
        }

        internal static bool IsUnstable(CallStackResult value)
        {
            return value == null || !value.SnapshotStable ||
                string.Equals(value.SnapshotStatus, "unstable", StringComparison.Ordinal) ||
                string.Equals(value.SnapshotStatus, "unavailable", StringComparison.Ordinal);
        }

        internal static bool IsIncomplete(ThreadsResult value)
        {
            return value == null || value.Partial || !string.IsNullOrEmpty(value.Error) || !value.SnapshotStable ||
                !string.Equals(value.SnapshotStatus, "ok", StringComparison.Ordinal);
        }

        internal static bool IsUnstable(ThreadsResult value)
        {
            return value == null || !value.SnapshotStable ||
                string.Equals(value.SnapshotStatus, "unstable", StringComparison.Ordinal) ||
                string.Equals(value.SnapshotStatus, "unavailable", StringComparison.Ordinal);
        }

        internal static bool IsIncomplete(ModulesResult value)
        {
            return value == null || value.Partial || value.Truncated || !string.IsNullOrEmpty(value.Error) ||
                !value.SnapshotStable ||
                !string.Equals(value.SnapshotStatus, "ok", StringComparison.Ordinal);
        }

        internal static bool IsUnstable(ModulesResult value)
        {
            return value == null || !value.SnapshotStable;
        }

        internal static bool IsIncomplete(RegistersResult value)
        {
            return value == null || value.Partial || !string.IsNullOrEmpty(value.Error) ||
                !string.Equals(value.Status, "complete", StringComparison.Ordinal) ||
                !string.Equals(value.SnapshotStatus, "stable", StringComparison.Ordinal);
        }

        internal static bool IsUnstable(RegistersResult value)
        {
            return value == null ||
                !string.Equals(value.SnapshotStatus, "stable", StringComparison.Ordinal);
        }

        internal static bool IsIncomplete(OutputWindowResult value)
        {
            return value == null || value.Partial || value.Truncated || !string.IsNullOrEmpty(value.Error) ||
                !value.SnapshotStable ||
                !string.Equals(value.SnapshotStatus, "stable", StringComparison.Ordinal);
        }

        internal static bool IsUnstable(OutputWindowResult value)
        {
            return value == null || !value.SnapshotStable ||
                !string.Equals(value.SnapshotStatus, "stable", StringComparison.Ordinal);
        }

        internal static bool IsIncomplete(DisassemblyResult value)
        {
            return value == null || value.Partial || value.Truncated || !string.IsNullOrEmpty(value.Error);
        }

        internal static bool IsIncomplete(BreakpointsResult value)
        {
            return value == null || value.Partial || value.Truncated || value.HasMore ||
                !string.IsNullOrEmpty(value.Error) || !value.SnapshotStable ||
                !string.Equals(value.SnapshotStatus, "ok", StringComparison.Ordinal);
        }
    }
}
