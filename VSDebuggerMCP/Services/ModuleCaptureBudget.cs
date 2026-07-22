using System.Text;
using System.Web.Script.Serialization;
using VSDebuggerMCP.Protocol;

namespace VSDebuggerMCP.Services
{
    internal sealed class ModuleCaptureBudget
    {
        internal const int MaxResultBytes = 3 * 1024 * 1024;
        internal const int MaxFieldBytes = 32 * 1024;
        private const int InitialEnvelopeBytes = 1024;
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();

        internal int ResultBytes { get; private set; } = InitialEnvelopeBytes;

        internal static void BoundFields(ModuleInfo module)
        {
            if (module == null) return;
            bool truncated = false;
            module.Name = Bound(module.Name, ref truncated);
            module.Path = Bound(module.Path, ref truncated);
            module.SymbolPath = Bound(module.SymbolPath, ref truncated);
            module.Version = Bound(module.Version, ref truncated);
            module.Timestamp = Bound(module.Timestamp, ref truncated);
            module.Address = Bound(module.Address, ref truncated);
            module.Size = Bound(module.Size, ref truncated);
            module.Truncated = truncated;
        }

        internal bool TryConsume(ModuleInfo module)
        {
            if (module == null) return false;
            BoundFields(module);
            int bytes = Encoding.UTF8.GetByteCount(_serializer.Serialize(module)) + 1;
            if (bytes > MaxResultBytes - ResultBytes) return false;
            ResultBytes += bytes;
            return true;
        }

        private static string Bound(string value, ref bool anyTruncated)
        {
            string bounded = OutputContentBudget.FitPrefix(
                value ?? string.Empty,
                MaxFieldBytes,
                out bool truncated);
            anyTruncated |= truncated;
            return bounded;
        }
    }
}
