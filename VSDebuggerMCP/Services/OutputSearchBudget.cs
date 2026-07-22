using System;
using System.Collections.Generic;

namespace VSDebuggerMCP.Services
{
    internal sealed class OutputSearchBudget
    {
        internal const int MaxSerializedResultBytes = 3 * 1024 * 1024;
        internal const int MaxResultNodes = 4096;
        internal const int MaxLineCharacters = 4096;
        internal const int MaxContextNodesPerMatch = 64;
        private const int InitialEnvelopeBytes = 1024;
        private const int MatchEnvelopeBytes = 192;

        internal OutputSearchBudget()
        {
            SerializedBytes = InitialEnvelopeBytes;
        }

        internal int SerializedBytes { get; private set; }
        internal int Nodes { get; private set; }

        internal bool TryConsumeMatch(string line, IList<string> context)
        {
            long bytes = MatchEnvelopeBytes + OutputContentBudget.GetSerializedStringUtf8ByteCount(line ?? string.Empty);
            int nodes = 1;
            if (context != null)
            {
                nodes += context.Count;
                foreach (string value in context)
                    bytes += OutputContentBudget.GetSerializedStringUtf8ByteCount(value ?? string.Empty) + 16;
            }
            if (nodes > MaxResultNodes - Nodes || bytes > MaxSerializedResultBytes - SerializedBytes)
                return false;
            Nodes += nodes;
            SerializedBytes += (int)bytes;
            return true;
        }

        internal static string BoundLine(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= MaxLineCharacters) return value ?? string.Empty;
            int length = MaxLineCharacters;
            if (length > 0 && length < value.Length && char.IsLowSurrogate(value[length]) &&
                char.IsHighSurrogate(value[length - 1])) length--;
            return value.Substring(0, length) + "…";
        }
    }
}
