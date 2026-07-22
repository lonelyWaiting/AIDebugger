using System.Text;
using System.Web.Script.Serialization;
using VSDebuggerMCP.Protocol;

namespace VSDebuggerMCP.Services
{
    internal sealed class BuildErrorCaptureBudget
    {
        internal const int MaxResultBytes = 3 * 1024 * 1024;
        internal const int MaxDescriptionBytes = 32 * 1024;
        internal const int MaxPathBytes = 16 * 1024;
        internal const int MaxProjectBytes = 8 * 1024;
        private const int InitialEnvelopeBytes = 1024;
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();

        internal int ResultBytes { get; private set; } = InitialEnvelopeBytes;

        internal static string BoundDescription(string value, out bool truncated) =>
            OutputContentBudget.FitPrefix(value ?? string.Empty, MaxDescriptionBytes, out truncated);

        internal static string BoundPath(string value, out bool truncated) =>
            OutputContentBudget.FitPrefix(value ?? string.Empty, MaxPathBytes, out truncated);

        internal static string BoundProject(string value, out bool truncated) =>
            OutputContentBudget.FitPrefix(value ?? string.Empty, MaxProjectBytes, out truncated);

        internal bool TryConsume(BuildErrorItem item)
        {
            if (item == null) return true;
            int bytes = Encoding.UTF8.GetByteCount(_serializer.Serialize(item)) + 1;
            if (bytes > MaxResultBytes - ResultBytes) return false;
            ResultBytes += bytes;
            return true;
        }
    }
}
