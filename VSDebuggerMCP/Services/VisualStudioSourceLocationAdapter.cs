using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger.Interop;
using Microsoft.VisualStudio.Shell;

namespace VSDebuggerMCP.Services
{
    internal sealed class VisualStudioSourceLocation
    {
        internal static readonly VisualStudioSourceLocation Unavailable =
            new VisualStudioSourceLocation(null, null, null, false);

        internal VisualStudioSourceLocation(string filePath, int? lineNumber, int? columnNumber, bool reliable)
        {
            FilePath = filePath;
            LineNumber = lineNumber;
            ColumnNumber = columnNumber;
            IsReliable = reliable;
        }

        internal string FilePath { get; }
        internal int? LineNumber { get; }
        internal int? ColumnNumber { get; }
        internal bool IsReliable { get; }
        internal string Status => IsReliable
            ? "available"
            : (string.IsNullOrWhiteSpace(FilePath) ? "unavailable" : "path_only");
    }

    /// <summary>
    /// Reads execution source metadata from the supported Visual Studio debugger
    /// interop contract. Editor selection and module paths are intentionally not
    /// used as substitutes for a frame's document context.
    /// </summary>
    internal sealed class VisualStudioSourceLocationAdapter
    {
        internal VisualStudioSourceLocation GetLocation(EnvDTE.StackFrame frame)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (frame == null) return VisualStudioSourceLocation.Unavailable;

            try
            {
                var debugFrame = frame as IDebugStackFrame2;
                if (debugFrame == null ||
                    ErrorHandler.Failed(debugFrame.GetDocumentContext(out IDebugDocumentContext2 documentContext)) ||
                    documentContext == null)
                {
                    return VisualStudioSourceLocation.Unavailable;
                }

                string filePath = null;
                if (ErrorHandler.Failed(documentContext.GetName(enum_GETNAME_TYPE.GN_FILENAME, out filePath)) ||
                    string.IsNullOrWhiteSpace(filePath))
                {
                    return VisualStudioSourceLocation.Unavailable;
                }

                var start = new TEXT_POSITION[1];
                var end = new TEXT_POSITION[1];
                if (ErrorHandler.Failed(documentContext.GetStatementRange(start, end)))
                {
                    return new VisualStudioSourceLocation(filePath, null, null, false);
                }

                return new VisualStudioSourceLocation(
                    filePath,
                    checked((int)start[0].dwLine + 1),
                    checked((int)start[0].dwColumn + 1),
                    true);
            }
            catch (Exception error) when (
                error is COMException ||
                error is InvalidCastException ||
                error is OverflowException)
            {
                return VisualStudioSourceLocation.Unavailable;
            }
        }
    }
}
