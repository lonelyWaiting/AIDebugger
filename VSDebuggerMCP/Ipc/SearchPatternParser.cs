using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VSDebuggerMCP.Ipc
{
    /// <summary>
    /// Strict, transport-independent parser for search_memory patterns.
    /// Text is UTF-8, ASCII is deliberately restricted to seven-bit input,
    /// and byte formats accept only complete hexadecimal byte pairs.
    /// </summary>
    internal static class SearchPatternParser
    {
        internal const int MaxPatternBytes = 4096;

        internal static byte[] Parse(string pattern, string patternFormat)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                throw new DebuggerParameterValidationException("Parameter 'pattern' must not be empty.");
            }

            string normalized = (patternFormat ?? "auto").Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "auto":
                    return EnforceBudget(LooksLikeSpacedHex(pattern)
                        ? ParseHex(pattern)
                        : Encoding.UTF8.GetBytes(pattern));
                case "hex":
                case "bytes":
                    return EnforceBudget(ParseHex(pattern));
                case "text":
                case "string":
                    return EnforceBudget(Encoding.UTF8.GetBytes(pattern));
                case "ascii":
                    if (pattern.Any(character => character > 0x7f))
                    {
                        throw new DebuggerParameterValidationException(
                            "Parameter 'pattern' contains non-ASCII characters while patternFormat is 'ascii'.");
                    }
                    return EnforceBudget(Encoding.ASCII.GetBytes(pattern));
                default:
                    throw new DebuggerParameterValidationException(
                        "Parameter 'patternFormat' must be auto, hex, bytes, text, ascii, or string.");
            }
        }

        private static byte[] ParseHex(string pattern)
        {
            var compact = new StringBuilder(pattern.Length);
            foreach (char character in pattern)
            {
                if (char.IsWhiteSpace(character)) continue;
                if (!IsHexDigit(character))
                {
                    throw new DebuggerParameterValidationException(
                        "Parameter 'pattern' contains invalid hexadecimal characters.");
                }
                compact.Append(character);
            }
            if (compact.Length == 0 || compact.Length % 2 != 0)
            {
                throw new DebuggerParameterValidationException(
                    "Parameter 'pattern' must contain complete hexadecimal byte pairs.");
            }

            var bytes = new byte[compact.Length / 2];
            for (int index = 0; index < bytes.Length; index++)
            {
                bytes[index] = Convert.ToByte(compact.ToString(index * 2, 2), 16);
            }
            return bytes;
        }

        private static bool LooksLikeSpacedHex(string pattern)
        {
            string[] tokens = pattern.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            return tokens.Length > 1 && tokens.All(token =>
                token.Length == 2 && token.All(IsHexDigit));
        }

        private static bool IsHexDigit(char character)
        {
            return (character >= '0' && character <= '9') ||
                (character >= 'a' && character <= 'f') ||
                (character >= 'A' && character <= 'F');
        }

        private static byte[] EnforceBudget(byte[] bytes)
        {
            if (bytes.Length > MaxPatternBytes)
            {
                throw new DebuggerParameterValidationException(
                    $"Parameter 'pattern' exceeds the {MaxPatternBytes}-byte search limit.");
            }
            return bytes;
        }
    }
}
