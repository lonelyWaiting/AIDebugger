using System;
using System.Text;

namespace VSDebuggerMCP.Services
{
    /// <summary>
    /// Applies the output-window content budget to the JSON string token itself.
    /// Counting UTF-16 characters (or even raw UTF-8 bytes) is insufficient because
    /// control characters, quotes and backslashes expand when encoded as JSON.
    /// </summary>
    internal static class OutputContentBudget
    {
        internal const int MinSerializedContentBytes = 4 * 1024;
        internal const int DefaultMaxSerializedContentBytes = 3 * 1024 * 1024;

        internal static OutputContentSlice FitSuffix(string content, int maxSerializedBytes)
        {
            if (content == null) content = string.Empty;
            if (maxSerializedBytes < MinSerializedContentBytes ||
                maxSerializedBytes > DefaultMaxSerializedContentBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(maxSerializedBytes));
            }

            int fullBytes = GetSerializedStringUtf8ByteCount(content, 0);
            if (fullBytes <= maxSerializedBytes)
            {
                return CreateSlice(content, 0, fullBytes);
            }

            int start = FindSuffixStartForPayloadBudget(content, maxSerializedBytes - 2);
            string suffix = content.Substring(start);
            return CreateSlice(suffix, start, GetSerializedStringUtf8ByteCount(content, start));
        }

        internal static int FindSuffixStartForPayloadBudget(string content, int payloadByteBudget)
        {
            if (content == null) content = string.Empty;
            if (payloadByteBudget < 0) throw new ArgumentOutOfRangeException(nameof(payloadByteBudget));

            int used = 0;
            int start = content.Length;
            while (start > 0)
            {
                int candidate = start - 1;
                if (char.IsLowSurrogate(content[candidate]) && candidate > 0 &&
                    char.IsHighSurrogate(content[candidate - 1]))
                {
                    candidate--;
                }
                int elementBytes = GetJsonElementUtf8ByteCount(content, candidate);
                if (elementBytes > payloadByteBudget - used) break;
                used += elementBytes;
                start = candidate;
            }
            return start;
        }

        internal static string FitPrefix(string content, int maxSerializedBytes, out bool truncated)
        {
            if (content == null) content = string.Empty;
            if (maxSerializedBytes < 2) throw new ArgumentOutOfRangeException(nameof(maxSerializedBytes));
            int payloadBudget = maxSerializedBytes - 2;
            int used = 0;
            int end = 0;
            while (end < content.Length)
            {
                int elementBytes = GetJsonElementUtf8ByteCount(content, end);
                if (elementBytes > payloadBudget - used) break;
                used += elementBytes;
                end += char.IsHighSurrogate(content[end]) && end + 1 < content.Length &&
                    char.IsLowSurrogate(content[end + 1]) ? 2 : 1;
            }
            truncated = end < content.Length;
            return truncated ? content.Substring(0, end) : content;
        }

        internal static int FindTailStart(string content, int tailLines)
        {
            if (string.IsNullOrEmpty(content) || tailLines <= 0) return 0;

            int lineCount = 1;
            for (int index = 0; index < content.Length; index++)
            {
                if (content[index] == '\r')
                {
                    lineCount++;
                    if (index + 1 < content.Length && content[index + 1] == '\n') index++;
                }
                else if (content[index] == '\n')
                {
                    lineCount++;
                }
            }
            if (lineCount <= tailLines) return 0;

            int delimitersToSkip = lineCount - tailLines;
            for (int index = 0; index < content.Length; index++)
            {
                if (content[index] != '\r' && content[index] != '\n') continue;
                if (content[index] == '\r' && index + 1 < content.Length && content[index + 1] == '\n')
                    index++;
                if (--delimitersToSkip == 0) return index + 1;
            }
            return content.Length;
        }

        internal static int GetSerializedStringUtf8ByteCount(string value, int startIndex = 0)
        {
            if (value == null) value = string.Empty;
            if (startIndex < 0 || startIndex > value.Length)
                throw new ArgumentOutOfRangeException(nameof(startIndex));

            // Opening and closing JSON quotes.
            long bytes = 2;
            for (int index = startIndex; index < value.Length; index++)
            {
                bytes += GetJsonElementUtf8ByteCount(value, index);
                if (char.IsHighSurrogate(value[index]) && index + 1 < value.Length &&
                    char.IsLowSurrogate(value[index + 1])) index++;
                if (bytes > int.MaxValue) return int.MaxValue;
            }
            return (int)bytes;
        }

        private static int GetJsonElementUtf8ByteCount(string value, int index)
        {
            char current = value[index];
            switch (current)
            {
                case '"':
                case '\\':
                case '\b':
                case '\t':
                case '\n':
                case '\f':
                case '\r':
                    return 2;
            }
            if (current < 0x20) return 6;
            if (char.IsHighSurrogate(current) && index + 1 < value.Length &&
                char.IsLowSurrogate(value[index + 1])) return 4;
            if (current <= 0x7F) return 1;
            if (current <= 0x7FF) return 2;
            // BMP characters use three UTF-8 bytes. Unpaired UTF-16 surrogates
            // are replaced by a three-byte U+FFFD by the transport encoder.
            return 3;
        }

        private static OutputContentSlice CreateSlice(string content, int omitted, int serializedBytes)
        {
            return new OutputContentSlice
            {
                Content = content,
                OmittedLeadingCharacters = omitted,
                ContentUtf8Bytes = Encoding.UTF8.GetByteCount(content),
                SerializedContentBytes = serializedBytes,
            };
        }
    }

    internal sealed class OutputContentSlice
    {
        internal string Content { get; set; }
        internal int OmittedLeadingCharacters { get; set; }
        internal int ContentUtf8Bytes { get; set; }
        internal int SerializedContentBytes { get; set; }
    }
}
