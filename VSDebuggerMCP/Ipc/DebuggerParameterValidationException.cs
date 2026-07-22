using System;

namespace VSDebuggerMCP
{
    /// <summary>
    /// Indicates that a debugger method parameter is syntactically or semantically invalid.
    /// The pipe boundary maps this exception to the typed INVALID_ARGUMENT protocol error.
    /// </summary>
    internal sealed class DebuggerParameterValidationException : Exception
    {
        internal DebuggerParameterValidationException(string message) : base(message)
        {
        }
    }
}
