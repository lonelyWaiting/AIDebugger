using System.Collections.Generic;

namespace VSDebuggerMCP.Services
{
    internal sealed class ExceptionCaptureBudget
    {
        internal const int MaxSerializedBytes = 3 * 1024 * 1024;
        internal const int MaxFieldBytes = 256 * 1024;
        internal const int MaxReasonBytes = 8 * 1024;
        private const int InitialEnvelopeBytes = 1024;
        private const int NodeEnvelopeBytes = 256;
        private readonly List<string> _reasons = new List<string>();

        internal int SerializedBytes { get; private set; } = InitialEnvelopeBytes;
        internal bool IsExceeded { get; private set; }
        internal bool IsIncomplete => IsExceeded || _reasons.Count > 0;
        internal string CombinedReason => string.Join("; ", _reasons);

        internal bool TryEnterNode()
        {
            DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
            if (IsExceeded) return false;
            if (NodeEnvelopeBytes > MaxSerializedBytes - SerializedBytes)
            {
                MarkExceeded($"Exception capture reached {MaxSerializedBytes} bytes");
                return false;
            }
            SerializedBytes += NodeEnvelopeBytes;
            return true;
        }

        internal string CaptureString(string value, string field, out bool truncated)
        {
            value = value ?? string.Empty;
            int remaining = MaxSerializedBytes - SerializedBytes;
            if (remaining <= 16)
            {
                truncated = value.Length > 0;
                if (truncated) MarkExceeded($"Exception {field} was omitted at the result byte budget");
                return string.Empty;
            }
            int payloadBudget = System.Math.Min(MaxFieldBytes, remaining - 16);
            string bounded = OutputContentBudget.FitPrefix(value, System.Math.Max(2, payloadBudget), out truncated);
            int bytes = OutputContentBudget.GetSerializedStringUtf8ByteCount(bounded) + 16;
            if (bytes > remaining)
            {
                bounded = string.Empty;
                bytes = 0;
                truncated = value.Length > 0;
            }
            SerializedBytes += bytes;
            if (truncated)
            {
                AddReason($"Exception {field} was truncated");
                if (payloadBudget < MaxFieldBytes) IsExceeded = true;
            }
            return bounded;
        }

        internal void AddReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return;
            if (_reasons.Count < 8)
            {
                string bounded = OutputContentBudget.FitPrefix(reason, MaxReasonBytes, out bool truncated);
                int bytes = OutputContentBudget.GetSerializedStringUtf8ByteCount(bounded) + 8;
                if (bytes <= MaxSerializedBytes - SerializedBytes)
                {
                    _reasons.Add(bounded);
                    SerializedBytes += bytes;
                    if (truncated) IsExceeded = true;
                }
                else
                {
                    IsExceeded = true;
                }
            }
            else if (_reasons.Count == 8)
            {
                const string omitted = "additional exception capture errors omitted";
                int bytes = OutputContentBudget.GetSerializedStringUtf8ByteCount(omitted) + 8;
                if (bytes <= MaxSerializedBytes - SerializedBytes)
                {
                    _reasons.Add(omitted);
                    SerializedBytes += bytes;
                }
                IsExceeded = true;
            }
        }

        private void MarkExceeded(string reason)
        {
            IsExceeded = true;
            AddReason(reason);
        }
    }
}
