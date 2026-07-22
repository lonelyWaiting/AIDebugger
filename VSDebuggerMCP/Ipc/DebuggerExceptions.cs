using System;

namespace VSDebuggerMCP
{
    internal sealed class MainThreadDispatchTimeoutException : TimeoutException
    {
        public MainThreadDispatchTimeoutException(string message) : base(message) { }
    }

    internal sealed class DebuggerStateRequirementException : InvalidOperationException
    {
        public DebuggerStateRequirementException(string code, string message) : base(message) { Code = code; }
        public string Code { get; }
    }

    internal sealed class StaleDebugSessionException : Exception
    {
        public StaleDebugSessionException(string message) : base(message) { }
    }

    internal sealed class StaleDebugContextException : Exception
    {
        public StaleDebugContextException(string message) : base(message) { }
    }

    internal sealed class DebugContextNotFoundException : Exception
    {
        public DebugContextNotFoundException(string message) : base(message) { }
    }

    internal sealed class ResourceLimitExceededException : Exception
    {
        public ResourceLimitExceededException(string message) : base(message) { }
    }

    internal sealed class RequestCancelledNotStartedException : OperationCanceledException
    {
        public RequestCancelledNotStartedException(string message) : base(message) { }
    }
}
