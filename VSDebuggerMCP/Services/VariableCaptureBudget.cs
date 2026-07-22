using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace VSDebuggerMCP.Services
{
    internal sealed class VariableCaptureBudget
    {
        internal const int MaxSerializedBytes = 3 * 1024 * 1024;
        internal const int MaxFieldSerializedBytes = 64 * 1024;
        private const int InitialEnvelopeBytes = 1024;
        private const int NodeEnvelopeBytes = 192;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly int _maxNodes;
        private readonly int _maxElapsedMs;
        private readonly List<string> _captureErrors = new List<string>();
        private readonly List<string> _truncationReasons = new List<string>();
        private int _nodes;

        internal VariableCaptureBudget(int maxNodes, int maxElapsedMs)
        {
            _maxNodes = maxNodes;
            _maxElapsedMs = maxElapsedMs;
            SerializedBytes = InitialEnvelopeBytes;
        }

        internal bool IsExceeded { get; private set; }
        internal string Reason { get; private set; }
        internal int SerializedBytes { get; private set; }
        internal bool HasCaptureErrors => _captureErrors.Count > 0;
        internal bool HasTruncations => _truncationReasons.Count > 0;
        internal bool IsIncomplete => IsExceeded || HasCaptureErrors || HasTruncations;
        internal string CombinedReason
        {
            get
            {
                var reasons = new List<string>();
                if (!string.IsNullOrEmpty(Reason)) reasons.Add(Reason);
                reasons.AddRange(_truncationReasons);
                reasons.AddRange(_captureErrors);
                return string.Join("; ", reasons);
            }
        }

        internal bool TryEnterNode()
        {
            DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
            if (IsExceeded) return false;
            if (_nodes >= _maxNodes)
            {
                MarkExceeded($"Expansion truncated after {_maxNodes} nodes");
                return false;
            }
            if (_stopwatch.ElapsedMilliseconds >= _maxElapsedMs)
            {
                MarkExceeded($"Expansion truncated after {_maxElapsedMs}ms");
                return false;
            }
            if (NodeEnvelopeBytes > MaxSerializedBytes - SerializedBytes)
            {
                MarkExceeded($"Expansion truncated at the {MaxSerializedBytes}-byte result budget");
                return false;
            }
            SerializedBytes += NodeEnvelopeBytes;
            _nodes++;
            return true;
        }

        internal bool CanContinue()
        {
            DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
            if (IsExceeded) return false;
            if (_nodes >= _maxNodes)
            {
                MarkExceeded($"Expansion truncated after {_maxNodes} nodes");
                return false;
            }
            if (_stopwatch.ElapsedMilliseconds >= _maxElapsedMs)
            {
                MarkExceeded($"Expansion truncated after {_maxElapsedMs}ms");
                return false;
            }
            return true;
        }

        internal string CaptureString(string value, string field, out bool truncated)
        {
            value = value ?? string.Empty;
            int remainingBytes = MaxSerializedBytes - SerializedBytes;
            if (remainingBytes <= 16)
            {
                truncated = value.Length > 0;
                if (truncated)
                    MarkExceeded($"Variable {field} was omitted at the {MaxSerializedBytes}-byte result budget");
                return string.Empty;
            }

            int payloadBudget = Math.Min(MaxFieldSerializedBytes, remainingBytes - 16);
            int available = Math.Max(2, payloadBudget);
            string bounded = OutputContentBudget.FitPrefix(value, available, out truncated);
            int bytes = OutputContentBudget.GetSerializedStringUtf8ByteCount(bounded) + 16;
            if (bytes > remainingBytes)
            {
                bounded = string.Empty;
                bytes = 0;
                truncated = value.Length > 0;
            }
            SerializedBytes += bytes;
            if (truncated)
            {
                string reason = $"Variable {field} was truncated at the serialized byte budget";
                if (payloadBudget < MaxFieldSerializedBytes)
                    MarkExceeded(reason);
                else if (_truncationReasons.Count < 8)
                    _truncationReasons.Add(reason);
            }
            return bounded;
        }

        internal void MarkExceeded(string reason)
        {
            IsExceeded = true;
            Reason = reason;
        }

        internal void ReportCaptureError(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return;
            string bounded = CaptureString(reason, "capture error", out _);
            if (_captureErrors.Count < 8)
                _captureErrors.Add(bounded);
            else if (_captureErrors.Count == 8)
                _captureErrors.Add("additional variable capture errors omitted");
        }
    }
}
