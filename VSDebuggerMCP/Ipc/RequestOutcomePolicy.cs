namespace VSDebuggerMCP.Ipc
{
    /// <summary>
    /// Conservatively classifies failures that occur after a command enters
    /// the session actor. Once entered, a compound operation may already have
    /// produced a side effect even if a later UI dispatch did not start.
    /// </summary>
    internal static class RequestOutcomePolicy
    {
        internal static string ClassifyNotStartedFailure(
            bool isCommand,
            bool actorInvocationStarted,
            string notStartedCode)
        {
            return isCommand && actorInvocationStarted
                ? "OUTCOME_UNKNOWN"
                : notStartedCode;
        }
    }
}
