using System;

namespace VSDebuggerMCP.Services
{
    internal static class BuildErrorCodeParser
    {
        private static readonly string[] KnownPrefixes =
        {
            "CS", "CA", "IDE", "BC", "NU", "SA", "RCS", "S", "DOC",
            "C", "LNK", "MSB", "NETSDK",
        };

        internal static string Extract(string description)
        {
            if (string.IsNullOrEmpty(description)) return string.Empty;

            // A bounded linear scan avoids regex backtracking and stays cheap on the
            // Visual Studio UI thread even for adversarial diagnostic text.
            for (int start = 0; start < description.Length; start++)
            {
                if (!IsAsciiLetter(description[start]) ||
                    (start > 0 && IsAsciiLetterOrDigit(description[start - 1])))
                {
                    continue;
                }

                int lettersEnd = start;
                while (lettersEnd < description.Length &&
                       lettersEnd - start < 6 &&
                       IsAsciiLetter(description[lettersEnd]))
                {
                    lettersEnd++;
                }
                int digitsEnd = lettersEnd;
                while (digitsEnd < description.Length &&
                       digitsEnd - lettersEnd < 5 &&
                       IsAsciiDigit(description[digitsEnd]))
                {
                    digitsEnd++;
                }
                int digitCount = digitsEnd - lettersEnd;
                if (digitCount < 4 ||
                    (digitsEnd < description.Length && IsAsciiLetterOrDigit(description[digitsEnd])))
                {
                    continue;
                }

                string code = description.Substring(start, digitsEnd - start).ToUpperInvariant();
                if (IsKnown(code)) return code;
            }

            int tokenStart = SkipDiagnosticPrefix(description);
            int tokenEnd = tokenStart;
            while (tokenEnd < description.Length &&
                   tokenEnd - tokenStart < 3 &&
                   IsAsciiLetter(description[tokenEnd]))
            {
                tokenEnd++;
            }
            int numberEnd = tokenEnd;
            while (numberEnd < description.Length &&
                   numberEnd - tokenEnd < 5 &&
                   IsAsciiDigit(description[numberEnd]))
            {
                numberEnd++;
            }
            int letters = tokenEnd - tokenStart;
            int digits = numberEnd - tokenEnd;
            if (letters >= 1 && digits >= 4 &&
                numberEnd < description.Length && description[numberEnd] == ':')
            {
                return description.Substring(tokenStart, numberEnd - tokenStart).ToUpperInvariant();
            }
            return string.Empty;
        }

        private static int SkipDiagnosticPrefix(string value)
        {
            int index = 0;
            while (index < value.Length && char.IsWhiteSpace(value[index])) index++;
            foreach (string prefix in new[] { "error", "warning" })
            {
                if (value.Length - index > prefix.Length &&
                    value.Substring(index, prefix.Length).Equals(prefix, StringComparison.OrdinalIgnoreCase) &&
                    char.IsWhiteSpace(value[index + prefix.Length]))
                {
                    index += prefix.Length;
                    while (index < value.Length && char.IsWhiteSpace(value[index])) index++;
                    break;
                }
            }
            return index;
        }

        private static bool IsKnown(string code)
        {
            foreach (string prefix in KnownPrefixes)
            {
                if (code.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool IsAsciiLetter(char value) =>
            (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');

        private static bool IsAsciiDigit(char value) => value >= '0' && value <= '9';

        private static bool IsAsciiLetterOrDigit(char value) =>
            IsAsciiLetter(value) || IsAsciiDigit(value);
    }
}
