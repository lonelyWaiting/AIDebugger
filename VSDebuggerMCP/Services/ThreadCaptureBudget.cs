using System.Text;
using System.Web.Script.Serialization;
using VSDebuggerMCP.Protocol;

namespace VSDebuggerMCP.Services
{
    internal sealed class ThreadCaptureBudget
    {
        internal const int MaxResultBytes = 3 * 1024 * 1024;
        internal const int MaxFieldBytes = 64 * 1024;
        private const int InitialEnvelopeBytes = 1024;
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();

        internal int ResultBytes { get; private set; } = InitialEnvelopeBytes;

        internal static string BoundField(string value, out bool truncated) =>
            OutputContentBudget.FitPrefix(value ?? string.Empty, MaxFieldBytes, out truncated);

        internal bool TryConsumeThreadHeader(ThreadInfo thread)
        {
            if (thread == null) return false;
            var header = new ThreadInfo
            {
                Id = thread.Id,
                Name = thread.Name,
                IsCurrent = thread.IsCurrent,
                IsFrozen = thread.IsFrozen,
                Location = thread.Location,
                Priority = thread.Priority,
                SuspendCount = thread.SuspendCount,
            };
            return TryConsume(header);
        }

        internal bool TryConsumeFrame(StackFrameInfo frame)
        {
            if (frame == null) return false;
            frame.FunctionName = BoundField(frame.FunctionName, out bool functionTruncated);
            frame.FilePath = BoundField(frame.FilePath, out bool pathTruncated);
            frame.SourceLocationStatus = BoundField(frame.SourceLocationStatus, out bool statusTruncated);
            frame.Module = BoundField(frame.Module, out bool moduleTruncated);
            frame.Language = BoundField(frame.Language, out bool languageTruncated);
            frame.ReturnType = BoundField(frame.ReturnType, out bool returnTypeTruncated);
            frame.Truncated = functionTruncated || pathTruncated || statusTruncated ||
                moduleTruncated || languageTruncated || returnTypeTruncated;
            return TryConsume(frame);
        }

        private bool TryConsume(object value)
        {
            int bytes = Encoding.UTF8.GetByteCount(_serializer.Serialize(value)) + 1;
            if (bytes > MaxResultBytes - ResultBytes) return false;
            ResultBytes += bytes;
            return true;
        }
    }
}
