using System.Text;
using System.Web.Script.Serialization;

namespace VSDebuggerMCP.Services
{
    internal sealed class CompositeCaptureBudget
    {
        internal const int MaxResultBytes = 3 * 1024 * 1024;
        private const int ReservedEnvelopeBytes = 128 * 1024;
        private const int MinimumUsefulComponentBytes = 32 * 1024;
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer
        {
            MaxJsonLength = int.MaxValue,
        };

        internal int ResultBytes { get; private set; } = ReservedEnvelopeBytes;
        internal bool IsExhausted { get; private set; }

        internal bool CanAttemptComponent =>
            !IsExhausted && MaxResultBytes - ResultBytes >= MinimumUsefulComponentBytes;

        internal bool TryConsumeComponent(string name, object component)
        {
            DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
            if (component == null) return true;
            if (!CanAttemptComponent) return false;

            string json = _serializer.Serialize(component);
            int bytes = Encoding.UTF8.GetByteCount(json) +
                Encoding.UTF8.GetByteCount(name ?? string.Empty) + 16;
            if (bytes > MaxResultBytes - ResultBytes)
            {
                IsExhausted = true;
                return false;
            }
            ResultBytes += bytes;
            return true;
        }
    }
}
