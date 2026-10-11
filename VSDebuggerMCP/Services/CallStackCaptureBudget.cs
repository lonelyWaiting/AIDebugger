using System;
using VSDebuggerMCP.Protocol;

namespace VSDebuggerMCP.Services
{
    internal sealed class CallStackCaptureBudget
    {
        internal const int DefaultMaxFrames = 256;
        internal const int HardMaxFrames = 4096;
        internal const int DefaultBudgetMs = 5000;
        internal const int MaxResultBytes = 3 * 1024 * 1024;
        internal const int MaxFrameStringCharacters = 32 * 1024;
        private const int InitialBytes = 1024;
        private const int FrameEnvelopeBytes = 384;

        internal CallStackCaptureBudget()
        {
            ResultBytes = InitialBytes;
        }

        internal int ResultBytes { get; private set; }

        internal bool TryConsume(StackFrameInfo frame)
        {
            if (frame == null) return false;
            BoundFrameStrings(frame);
            long bytes = FrameEnvelopeBytes;
            bytes += StringBytes(frame.FunctionName);
            bytes += StringBytes(frame.FilePath);
            bytes += StringBytes(frame.SourceLocationStatus);
            bytes += StringBytes(frame.Module);
            bytes += StringBytes(frame.Language);
            bytes += StringBytes(frame.ReturnType);
            bytes += StringBytes(frame.Error);
            bytes += StringBytes(frame.Context?.FrameId);
            if (bytes > MaxResultBytes - ResultBytes) return false;
            ResultBytes += (int)bytes;
            return true;
        }

        private static int StringBytes(string value) =>
            OutputContentBudget.GetSerializedStringUtf8ByteCount(value ?? string.Empty) + 16;

        private static void BoundFrameStrings(StackFrameInfo frame)
        {
            bool truncated = frame.Truncated;
            frame.FunctionName = Bound(frame.FunctionName, ref truncated);
            frame.FilePath = Bound(frame.FilePath, ref truncated);
            frame.SourceLocationStatus = Bound(frame.SourceLocationStatus, ref truncated);
            frame.Module = Bound(frame.Module, ref truncated);
            frame.Language = Bound(frame.Language, ref truncated);
            frame.ReturnType = Bound(frame.ReturnType, ref truncated);
            frame.Error = Bound(frame.Error, ref truncated);
            if (frame.Context != null) frame.Context.FrameId = Bound(frame.Context.FrameId, ref truncated);
            frame.Truncated = truncated;
        }

        private static string Bound(string value, ref bool truncated)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= MaxFrameStringCharacters) return value;
            truncated = true;
            int length = MaxFrameStringCharacters;
            if (char.IsLowSurrogate(value[length]) && char.IsHighSurrogate(value[length - 1])) length--;
            return value.Substring(0, length) + "…";
        }
    }
}
