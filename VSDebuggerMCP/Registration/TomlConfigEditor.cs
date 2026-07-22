using System;
using System.Collections.Generic;
using System.Text;

namespace VSDebuggerMCP
{
    /// <summary>
    /// Text-preserving editor for owned TOML tables. A lexical pass tracks
    /// strings, comments, arrays and inline tables so table-looking text inside
    /// values can never be interpreted as configuration structure.
    /// </summary>
    internal static class TomlConfigEditor
    {
        public static bool HasSection(string content, string semanticName)
        {
            string[] expected = ParseDottedKey(semanticName);
            foreach (Header header in Analyze(content ?? string.Empty).Headers)
            {
                if (!header.ArrayTable && KeysEqual(ParseDottedKey(header.Key), expected)) return true;
            }
            return false;
        }

        public static string RemoveSection(string content, string semanticName)
        {
            if (string.IsNullOrEmpty(content)) return string.Empty;
            string[] expected = ParseDottedKey(semanticName);
            Analysis analysis = Analyze(content);
            var ranges = new List<Tuple<int, int>>();
            for (int index = 0; index < analysis.Headers.Count; index++)
            {
                Header header = analysis.Headers[index];
                if (header.ArrayTable || !KeysEqual(ParseDottedKey(header.Key), expected)) continue;
                int end = index + 1 < analysis.Headers.Count
                    ? analysis.Headers[index + 1].Start
                    : content.Length;
                ranges.Add(Tuple.Create(header.Start, end));
            }
            var builder = new StringBuilder(content);
            for (int index = ranges.Count - 1; index >= 0; index--)
                builder.Remove(ranges[index].Item1, ranges[index].Item2 - ranges[index].Item1);
            // Re-lex the result before any caller writes it.
            Analyze(builder.ToString());
            return builder.ToString();
        }

        public static void RejectUnsupportedOwnedAssignments(string content, params string[] ownedNames)
        {
            var owned = new List<string[]>();
            foreach (string name in ownedNames ?? new string[0]) owned.Add(ParseDottedKey(name));
            Analysis analysis = Analyze(content ?? string.Empty);
            string[] currentTable = new string[0];
            foreach (Line line in analysis.Lines)
            {
                Header header = line.Header;
                if (header != null)
                {
                    currentTable = header.ArrayTable ? new string[0] : ParseDottedKey(header.Key);
                    continue;
                }
                if (!line.TopLevel) continue;
                int equals = FindUnquotedEquals(line.Text);
                if (equals < 0) continue;
                string[] assignment = ParseDottedKey(line.Text.Substring(0, equals).Trim());
                var full = new string[currentTable.Length + assignment.Length];
                currentTable.CopyTo(full, 0);
                assignment.CopyTo(full, currentTable.Length);
                foreach (string[] target in owned)
                {
                    if (!StartsWith(currentTable, target) && StartsWith(full, target))
                    {
                        throw new InvalidOperationException(
                            "Owned MCP TOML entry uses an inline/dotted assignment that cannot be updated without rewriting user content: " +
                            string.Join(".", target));
                    }
                }
            }
        }

        public static string[] ParseDottedKey(string value)
        {
            var result = new List<string>();
            string text = (value ?? string.Empty).Trim();
            int index = 0;
            while (index < text.Length)
            {
                while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
                if (index >= text.Length) break;
                var part = new StringBuilder();
                if (text[index] == '"' || text[index] == '\'')
                {
                    char quote = text[index++];
                    bool closed = false;
                    while (index < text.Length)
                    {
                        char character = text[index++];
                        if (character == quote) { closed = true; break; }
                        if (quote == '"' && character == '\\')
                        {
                            if (index >= text.Length) throw new FormatException("Invalid quoted TOML key");
                            char escaped = text[index++];
                            if (escaped != '\\' && escaped != '"')
                                throw new FormatException("Unsupported escape in TOML key");
                            character = escaped;
                        }
                        part.Append(character);
                    }
                    if (!closed) throw new FormatException("Unterminated quoted TOML key");
                }
                else
                {
                    while (index < text.Length && text[index] != '.' && !char.IsWhiteSpace(text[index]))
                    {
                        char character = text[index++];
                        if (!(char.IsLetterOrDigit(character) || character == '_' || character == '-'))
                            throw new FormatException("Invalid bare TOML key");
                        part.Append(character);
                    }
                }
                if (part.Length == 0) throw new FormatException("Empty TOML key segment");
                result.Add(part.ToString());
                while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
                if (index == text.Length) break;
                if (text[index++] != '.') throw new FormatException("Invalid dotted TOML key");
            }
            if (result.Count == 0) throw new FormatException("TOML key is empty");
            return result.ToArray();
        }

        private static Analysis Analyze(string content)
        {
            var analysis = new Analysis();
            LexState state = LexState.Normal;
            int arrayDepth = 0;
            int inlineTableDepth = 0;
            for (int lineStart = 0; lineStart <= content.Length;)
            {
                int newline = content.IndexOf('\n', lineStart);
                int lineEnd = newline < 0 ? content.Length : newline;
                string lineText = content.Substring(lineStart, lineEnd - lineStart).TrimEnd('\r');
                bool topLevel = state == LexState.Normal && arrayDepth == 0 && inlineTableDepth == 0;
                Header header = topLevel ? TryParseHeader(lineText, lineStart) : null;
                analysis.Lines.Add(new Line { Text = lineText, TopLevel = topLevel, Header = header });
                if (header != null)
                {
                    analysis.Headers.Add(header);
                }
                else
                {
                    ScanValueLine(lineText, ref state, ref arrayDepth, ref inlineTableDepth);
                }

                if (state == LexState.Basic || state == LexState.Literal)
                    throw new FormatException("TOML single-line string crosses a line boundary");
                if (newline < 0) break;
                lineStart = newline + 1;
            }
            if (state == LexState.MultiBasic || state == LexState.MultiLiteral)
                throw new FormatException("TOML contains an unterminated multiline string");
            if (arrayDepth != 0 || inlineTableDepth != 0)
                throw new FormatException("TOML contains an unterminated array or inline table");
            return analysis;
        }

        private static void ScanValueLine(
            string line,
            ref LexState state,
            ref int arrayDepth,
            ref int inlineTableDepth)
        {
            bool escaped = false;
            for (int index = 0; index < line.Length; index++)
            {
                char character = line[index];
                if (state == LexState.MultiBasic)
                {
                    if (!escaped && StartsWith(line, index, "\"\"\"")) { state = LexState.Normal; index += 2; continue; }
                    if (!escaped && character == '\\') { escaped = true; continue; }
                    escaped = false;
                    continue;
                }
                if (state == LexState.MultiLiteral)
                {
                    if (StartsWith(line, index, "'''")) { state = LexState.Normal; index += 2; }
                    continue;
                }
                if (state == LexState.Basic)
                {
                    if (!escaped && character == '"') state = LexState.Normal;
                    if (!escaped && character == '\\') { escaped = true; continue; }
                    escaped = false;
                    continue;
                }
                if (state == LexState.Literal)
                {
                    if (character == '\'') state = LexState.Normal;
                    continue;
                }
                if (character == '#') break;
                if (StartsWith(line, index, "\"\"\"")) { state = LexState.MultiBasic; index += 2; continue; }
                if (StartsWith(line, index, "'''")) { state = LexState.MultiLiteral; index += 2; continue; }
                if (character == '"') { state = LexState.Basic; continue; }
                if (character == '\'') { state = LexState.Literal; continue; }
                if (character == '[') arrayDepth++;
                else if (character == ']') { if (--arrayDepth < 0) throw new FormatException("Unexpected TOML array terminator"); }
                else if (character == '{') inlineTableDepth++;
                else if (character == '}') { if (--inlineTableDepth < 0) throw new FormatException("Unexpected TOML inline-table terminator"); }
            }
        }

        private static Header TryParseHeader(string line, int absoluteStart)
        {
            int index = 0;
            while (index < line.Length && (line[index] == ' ' || line[index] == '\t')) index++;
            if (index >= line.Length || line[index] != '[') return null;
            bool array = index + 1 < line.Length && line[index + 1] == '[';
            int keyStart = index + (array ? 2 : 1);
            char quote = '\0';
            bool escaped = false;
            int close = -1;
            for (int cursor = keyStart; cursor < line.Length; cursor++)
            {
                char character = line[cursor];
                if (quote != '\0')
                {
                    if (quote == '"' && !escaped && character == '\\') { escaped = true; continue; }
                    if (!escaped && character == quote) quote = '\0';
                    escaped = false;
                    continue;
                }
                if (character == '"' || character == '\'') { quote = character; continue; }
                if (character == ']' && (!array || cursor + 1 < line.Length && line[cursor + 1] == ']'))
                {
                    close = cursor;
                    int after = cursor + (array ? 2 : 1);
                    while (after < line.Length && char.IsWhiteSpace(line[after])) after++;
                    if (after < line.Length && line[after] != '#') return null;
                    break;
                }
            }
            if (close < 0 || quote != '\0') return null;
            string key = line.Substring(keyStart, close - keyStart).Trim();
            ParseDottedKey(key);
            return new Header { Start = absoluteStart, Key = key, ArrayTable = array };
        }

        private static int FindUnquotedEquals(string line)
        {
            char quote = '\0';
            bool escaped = false;
            for (int index = 0; index < (line ?? string.Empty).Length; index++)
            {
                char character = line[index];
                if (quote != '\0')
                {
                    if (quote == '"' && !escaped && character == '\\') { escaped = true; continue; }
                    if (!escaped && character == quote) quote = '\0';
                    escaped = false;
                    continue;
                }
                if (character == '#') return -1;
                if (character == '"' || character == '\'') quote = character;
                else if (character == '=') return index;
            }
            return -1;
        }

        private static bool StartsWith(string value, int index, string token)
        {
            return index + token.Length <= value.Length &&
                string.CompareOrdinal(value, index, token, 0, token.Length) == 0;
        }

        private static bool KeysEqual(string[] left, string[] right)
        {
            if (left.Length != right.Length) return false;
            for (int index = 0; index < left.Length; index++)
                if (!string.Equals(left[index], right[index], StringComparison.Ordinal)) return false;
            return true;
        }

        private static bool StartsWith(string[] value, string[] prefix)
        {
            if (value.Length < prefix.Length) return false;
            for (int index = 0; index < prefix.Length; index++)
                if (!string.Equals(value[index], prefix[index], StringComparison.Ordinal)) return false;
            return true;
        }

        private enum LexState { Normal, Basic, Literal, MultiBasic, MultiLiteral }
        private sealed class Analysis
        {
            internal List<Header> Headers { get; } = new List<Header>();
            internal List<Line> Lines { get; } = new List<Line>();
        }
        private sealed class Header
        {
            internal int Start { get; set; }
            internal string Key { get; set; }
            internal bool ArrayTable { get; set; }
        }
        private sealed class Line
        {
            internal string Text { get; set; }
            internal bool TopLevel { get; set; }
            internal Header Header { get; set; }
        }
    }
}
