using System;
using System.Collections.Generic;
using System.Text;
using System.Web.Script.Serialization;

namespace VSDebuggerMCP
{
    /// <summary>
    /// Performs ownership-scoped JSON/JSONC edits without reserializing the surrounding
    /// client configuration. Comments, property ordering, and unrelated whitespace survive.
    /// </summary>
    internal static class JsonConfigEditor
    {
        private sealed class PropertySpan
        {
            public int Start;
            public int ValueStart;
            public int ValueEnd;
            public int End;
        }

        public static string UpsertOwnedEntry(
            string document,
            string containerName,
            string entryName,
            string entryJson)
        {
            string source = string.IsNullOrWhiteSpace(document) ? "{}" : document;
            GetRootObject(source, out int rootStart, out int rootEnd);
            if (!TryFindProperty(source, rootStart, rootEnd, containerName, out PropertySpan container))
            {
                string containerJson = "{" + Quote(entryName) + ":" + entryJson + "}";
                string inserted = InsertProperty(source, rootStart, rootEnd, containerName, containerJson);
                ValidateUniqueOwnedPath(inserted, containerName, entryName, requireEntry: true);
                return inserted;
            }

            int objectStart = SkipTrivia(source, container.ValueStart);
            if (objectStart >= source.Length || source[objectStart] != '{')
            {
                throw new FormatException("The '" + containerName + "' configuration value must be an object");
            }
            int objectEnd = ScanValue(source, objectStart) - 1;
            if (TryFindProperty(source, objectStart, objectEnd, entryName, out PropertySpan entry))
            {
                string replaced = source.Substring(0, entry.ValueStart) + entryJson + source.Substring(entry.ValueEnd);
                ValidateUniqueOwnedPath(replaced, containerName, entryName, requireEntry: true);
                return replaced;
            }

            string updated = InsertProperty(source, objectStart, objectEnd, entryName, entryJson);
            ValidateUniqueOwnedPath(updated, containerName, entryName, requireEntry: true);
            return updated;
        }

        public static string RemoveOwnedEntry(string document, string containerName, string entryName, out bool removed)
        {
            removed = false;
            if (string.IsNullOrWhiteSpace(document))
            {
                return document ?? string.Empty;
            }

            GetRootObject(document, out int rootStart, out int rootEnd);
            if (!TryFindProperty(document, rootStart, rootEnd, containerName, out PropertySpan container))
            {
                return document;
            }

            int objectStart = SkipTrivia(document, container.ValueStart);
            if (objectStart >= document.Length || document[objectStart] != '{')
            {
                throw new FormatException("The '" + containerName + "' configuration value must be an object");
            }
            int objectEnd = ScanValue(document, objectStart) - 1;
            if (!TryFindProperty(document, objectStart, objectEnd, entryName, out PropertySpan entry))
            {
                return document;
            }

            int removeStart = entry.Start;
            int removeEnd = entry.End;
            int after = SkipTrivia(document, removeEnd);
            if (after < objectEnd && document[after] == ',')
            {
                removeEnd = after + 1;
            }
            else
            {
                int before = SkipTriviaBackward(document, removeStart - 1, objectStart + 1);
                if (before >= objectStart + 1 && document[before] == ',')
                {
                    // Preserve comments/trivia between the previous property's comma
                    // and this entry. The comma and entry are intentionally removed as
                    // two spans so an inline comment on the previous property survives.
                    string withoutEntry = document.Remove(removeStart, removeEnd - removeStart);
                    removed = true;
                    string updated = withoutEntry.Remove(before, 1);
                    ValidateUniqueOwnedPath(updated, containerName, entryName, requireEntry: false);
                    return updated;
                }
            }

            removed = true;
            string result = document.Remove(removeStart, removeEnd - removeStart);
            ValidateUniqueOwnedPath(result, containerName, entryName, requireEntry: false);
            return result;
        }

        public static string StripComments(string document)
        {
            if (string.IsNullOrEmpty(document))
            {
                return document ?? string.Empty;
            }

            var result = new StringBuilder(document);
            bool inString = false;
            bool escaped = false;
            for (int i = 0; i < document.Length; i++)
            {
                char ch = document[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (ch == '\\') escaped = true;
                    else if (ch == '"') inString = false;
                    continue;
                }

                if (ch == '"')
                {
                    inString = true;
                    continue;
                }
                if (ch != '/' || i + 1 >= document.Length)
                {
                    continue;
                }

                if (document[i + 1] == '/')
                {
                    result[i] = result[i + 1] = ' ';
                    i += 2;
                    while (i < document.Length && document[i] != '\r' && document[i] != '\n')
                    {
                        result[i++] = ' ';
                    }
                    i--;
                }
                else if (document[i + 1] == '*')
                {
                    result[i] = result[i + 1] = ' ';
                    i += 2;
                    while (i + 1 < document.Length && !(document[i] == '*' && document[i + 1] == '/'))
                    {
                        if (document[i] != '\r' && document[i] != '\n') result[i] = ' ';
                        i++;
                    }
                    if (i + 1 >= document.Length) throw new FormatException("Unterminated JSON block comment");
                    result[i] = result[i + 1] = ' ';
                    i++;
                }
            }
            return result.ToString();
        }

        private static string InsertProperty(
            string source,
            int objectStart,
            int objectEnd,
            string name,
            string valueJson)
        {
            int cursor = objectStart + 1;
            int lastValueEnd = -1;
            while (true)
            {
                cursor = SkipTrivia(source, cursor);
                if (cursor >= objectEnd) break;
                if (source[cursor] != '"') throw new FormatException("Expected a JSON object property");
                cursor = ScanString(source, cursor);
                cursor = SkipTrivia(source, cursor);
                if (cursor >= objectEnd || source[cursor] != ':') throw new FormatException("Expected ':' after JSON property name");
                cursor = SkipTrivia(source, cursor + 1);
                lastValueEnd = ScanValue(source, cursor);
                cursor = SkipTrivia(source, lastValueEnd);
                if (cursor < objectEnd && source[cursor] == ',') cursor++;
                else break;
            }

            string lineBreak = source.Contains("\r\n") ? "\r\n" : "\n";
            string closeIndent = GetLineIndent(source, objectEnd);
            string childIndent = closeIndent + "  ";
            string propertyText = Quote(name) + ": " + valueJson;
            if (lastValueEnd < 0)
            {
                return source.Insert(objectEnd, lineBreak + childIndent + propertyText + lineBreak + closeIndent);
            }

            // Place the required comma directly after the prior value, before any trailing
            // line comment, then add the owned node immediately before the closing brace.
            int separator = SkipTrivia(source, lastValueEnd);
            bool alreadyHasTrailingComma = separator < objectEnd && source[separator] == ',';
            string withComma = alreadyHasTrailingComma ? source : source.Insert(lastValueEnd, ",");
            int adjustedObjectEnd = objectEnd + (alreadyHasTrailingComma ? 0 : 1);
            return withComma.Insert(adjustedObjectEnd, lineBreak + childIndent + propertyText + lineBreak + closeIndent);
        }

        private static bool TryFindProperty(
            string source,
            int objectStart,
            int objectEnd,
            string propertyName,
            out PropertySpan span)
        {
            int cursor = objectStart + 1;
            PropertySpan match = null;
            while (true)
            {
                cursor = SkipTrivia(source, cursor);
                if (cursor >= objectEnd)
                {
                    span = match;
                    return match != null;
                }
                int propertyStart = cursor;
                if (source[cursor] != '"') throw new FormatException("Expected a JSON object property");
                int keyEnd = ScanString(source, cursor);
                string key = new JavaScriptSerializer().Deserialize<string>(source.Substring(cursor, keyEnd - cursor));
                cursor = SkipTrivia(source, keyEnd);
                if (cursor >= objectEnd || source[cursor] != ':') throw new FormatException("Expected ':' after JSON property name");
                int valueStart = SkipTrivia(source, cursor + 1);
                int valueEnd = ScanValue(source, valueStart);
                if (string.Equals(key, propertyName, StringComparison.Ordinal))
                {
                    if (match != null)
                    {
                        throw new FormatException("Duplicate JSON property '" + propertyName + "' is ambiguous");
                    }
                    match = new PropertySpan
                    {
                        Start = propertyStart,
                        ValueStart = valueStart,
                        ValueEnd = valueEnd,
                        End = valueEnd,
                    };
                }
                cursor = SkipTrivia(source, valueEnd);
                if (cursor < objectEnd && source[cursor] == ',')
                {
                    cursor++;
                    continue;
                }
                span = match;
                return match != null;
            }
        }

        private static void ValidateUniqueOwnedPath(
            string source,
            string containerName,
            string entryName,
            bool requireEntry)
        {
            GetRootObject(source, out int rootStart, out int rootEnd);
            if (!TryFindProperty(source, rootStart, rootEnd, containerName, out PropertySpan container))
            {
                if (requireEntry) throw new FormatException("Owned JSON container was not written");
                return;
            }

            int objectStart = SkipTrivia(source, container.ValueStart);
            if (objectStart >= source.Length || source[objectStart] != '{')
            {
                throw new FormatException("The '" + containerName + "' configuration value must be an object");
            }
            int objectEnd = ScanValue(source, objectStart) - 1;
            bool hasEntry = TryFindProperty(source, objectStart, objectEnd, entryName, out _);
            if (hasEntry != requireEntry)
            {
                throw new FormatException(requireEntry
                    ? "Owned JSON entry was not written"
                    : "Owned JSON entry was not removed");
            }
        }

        private static void GetRootObject(string source, out int rootStart, out int rootEnd)
        {
            rootStart = SkipTrivia(source, 0);
            if (rootStart >= source.Length || source[rootStart] != '{')
            {
                throw new FormatException("MCP client configuration must be a JSON object");
            }
            rootEnd = ScanValue(source, rootStart) - 1;
            if (source[rootEnd] != '}') throw new FormatException("Unterminated JSON root object");
            if (SkipTrivia(source, rootEnd + 1) != source.Length)
            {
                throw new FormatException("Unexpected content after JSON root object");
            }
        }

        private static int ScanValue(string source, int start)
        {
            return ScanValue(source, start, 0);
        }

        private static int ScanValue(string source, int start, int depth)
        {
            if (depth > 128) throw new FormatException("JSON nesting exceeds the supported limit");
            int cursor = SkipTrivia(source, start);
            if (cursor >= source.Length) throw new FormatException("Missing JSON value");
            char first = source[cursor];
            if (first == '"') return ScanString(source, cursor);
            if (first == '{')
            {
                cursor++;
                var names = new HashSet<string>(StringComparer.Ordinal);
                cursor = SkipTrivia(source, cursor);
                if (cursor < source.Length && source[cursor] == '}') return cursor + 1;
                while (true)
                {
                    cursor = SkipTrivia(source, cursor);
                    if (cursor >= source.Length || source[cursor] != '"')
                        throw new FormatException("Expected a JSON object property");
                    int keyEnd = ScanString(source, cursor);
                    string key = new JavaScriptSerializer().Deserialize<string>(
                        source.Substring(cursor, keyEnd - cursor));
                    if (!names.Add(key))
                        throw new FormatException("Duplicate JSON property '" + key + "' is ambiguous");
                    cursor = SkipTrivia(source, keyEnd);
                    if (cursor >= source.Length || source[cursor] != ':')
                        throw new FormatException("Expected ':' after JSON property name");
                    cursor = ScanValue(source, cursor + 1, depth + 1);
                    cursor = SkipTrivia(source, cursor);
                    if (cursor >= source.Length)
                        throw new FormatException("Unterminated JSON object");
                    if (source[cursor] == '}') return cursor + 1;
                    if (source[cursor] != ',')
                        throw new FormatException("Expected ',' or '}' after JSON property value");
                    cursor = SkipTrivia(source, cursor + 1);
                    if (cursor < source.Length && source[cursor] == '}') return cursor + 1;
                }
            }
            if (first == '[')
            {
                cursor++;
                cursor = SkipTrivia(source, cursor);
                if (cursor < source.Length && source[cursor] == ']') return cursor + 1;
                while (true)
                {
                    cursor = ScanValue(source, cursor, depth + 1);
                    cursor = SkipTrivia(source, cursor);
                    if (cursor >= source.Length)
                        throw new FormatException("Unterminated JSON array");
                    if (source[cursor] == ']') return cursor + 1;
                    if (source[cursor] != ',')
                        throw new FormatException("Expected ',' or ']' after JSON array value");
                    cursor = SkipTrivia(source, cursor + 1);
                    if (cursor < source.Length && source[cursor] == ']') return cursor + 1;
                }
            }
            if (MatchesLiteral(source, cursor, "true")) return cursor + 4;
            if (MatchesLiteral(source, cursor, "false")) return cursor + 5;
            if (MatchesLiteral(source, cursor, "null")) return cursor + 4;
            return ScanNumber(source, cursor);
        }

        private static int ScanString(string source, int start)
        {
            for (int cursor = start + 1; cursor < source.Length; cursor++)
            {
                char ch = source[cursor];
                if (ch == '"') return cursor + 1;
                if (ch < 0x20) throw new FormatException("JSON strings cannot contain unescaped control characters");
                if (ch != '\\') continue;
                cursor++;
                if (cursor >= source.Length) break;
                char escape = source[cursor];
                if (escape == '"' || escape == '\\' || escape == '/' ||
                    escape == 'b' || escape == 'f' || escape == 'n' ||
                    escape == 'r' || escape == 't')
                {
                    continue;
                }
                if (escape != 'u' || cursor + 4 >= source.Length)
                    throw new FormatException("Invalid JSON string escape");
                for (int digit = 1; digit <= 4; digit++)
                {
                    if (!Uri.IsHexDigit(source[cursor + digit]))
                        throw new FormatException("Invalid JSON Unicode escape");
                }
                cursor += 4;
            }
            throw new FormatException("Unterminated JSON string");
        }

        private static bool MatchesLiteral(string source, int start, string literal)
        {
            if (start + literal.Length > source.Length ||
                !string.Equals(source.Substring(start, literal.Length), literal, StringComparison.Ordinal))
            {
                return false;
            }
            int end = start + literal.Length;
            return end == source.Length || IsValueDelimiter(source, end);
        }

        private static int ScanNumber(string source, int start)
        {
            int cursor = start;
            if (cursor < source.Length && source[cursor] == '-') cursor++;
            if (cursor >= source.Length) throw new FormatException("Invalid JSON number");
            if (source[cursor] == '0')
            {
                cursor++;
                if (cursor < source.Length && char.IsDigit(source[cursor]))
                    throw new FormatException("JSON numbers cannot have leading zeroes");
            }
            else if (source[cursor] >= '1' && source[cursor] <= '9')
            {
                while (cursor < source.Length && char.IsDigit(source[cursor])) cursor++;
            }
            else
            {
                throw new FormatException("Invalid JSON value");
            }
            if (cursor < source.Length && source[cursor] == '.')
            {
                cursor++;
                int fractionStart = cursor;
                while (cursor < source.Length && char.IsDigit(source[cursor])) cursor++;
                if (cursor == fractionStart) throw new FormatException("Invalid JSON number fraction");
            }
            if (cursor < source.Length && (source[cursor] == 'e' || source[cursor] == 'E'))
            {
                cursor++;
                if (cursor < source.Length && (source[cursor] == '+' || source[cursor] == '-')) cursor++;
                int exponentStart = cursor;
                while (cursor < source.Length && char.IsDigit(source[cursor])) cursor++;
                if (cursor == exponentStart) throw new FormatException("Invalid JSON number exponent");
            }
            if (cursor < source.Length && !IsValueDelimiter(source, cursor))
                throw new FormatException("Invalid content after JSON number");
            return cursor;
        }

        private static bool IsValueDelimiter(string source, int position)
        {
            char ch = source[position];
            return ch == ',' || ch == '}' || ch == ']' || char.IsWhiteSpace(ch) || ch == '/';
        }

        private static int SkipTrivia(string source, int start)
        {
            int cursor = start;
            while (cursor < source.Length)
            {
                if (char.IsWhiteSpace(source[cursor]))
                {
                    cursor++;
                    continue;
                }
                if (source[cursor] == '/' && cursor + 1 < source.Length && source[cursor + 1] == '/')
                {
                    cursor += 2;
                    while (cursor < source.Length && source[cursor] != '\r' && source[cursor] != '\n') cursor++;
                    continue;
                }
                if (source[cursor] == '/' && cursor + 1 < source.Length && source[cursor + 1] == '*')
                {
                    int end = source.IndexOf("*/", cursor + 2, StringComparison.Ordinal);
                    if (end < 0) throw new FormatException("Unterminated JSON block comment");
                    cursor = end + 2;
                    continue;
                }
                break;
            }
            return cursor;
        }

        private static int SkipTriviaBackward(string source, int start, int minimum)
        {
            int cursor = minimum;
            int lastToken = minimum - 1;
            while (cursor <= start)
            {
                if (char.IsWhiteSpace(source[cursor]))
                {
                    cursor++;
                    continue;
                }
                if (source[cursor] == '"')
                {
                    int end = ScanString(source, cursor);
                    lastToken = Math.Min(start, end - 1);
                    cursor = end;
                    continue;
                }
                if (source[cursor] == '/' && cursor + 1 <= start && source[cursor + 1] == '/')
                {
                    cursor += 2;
                    while (cursor <= start && source[cursor] != '\r' && source[cursor] != '\n') cursor++;
                    continue;
                }
                if (source[cursor] == '/' && cursor + 1 <= start && source[cursor + 1] == '*')
                {
                    int end = source.IndexOf("*/", cursor + 2, StringComparison.Ordinal);
                    if (end < 0 || end + 1 > start) throw new FormatException("Unterminated JSON block comment");
                    cursor = end + 2;
                    continue;
                }
                lastToken = cursor;
                cursor++;
            }
            return lastToken;
        }

        private static string GetLineIndent(string source, int position)
        {
            int lineStart = position;
            while (lineStart > 0 && source[lineStart - 1] != '\r' && source[lineStart - 1] != '\n') lineStart--;
            int cursor = lineStart;
            while (cursor < position && (source[cursor] == ' ' || source[cursor] == '\t')) cursor++;
            return source.Substring(lineStart, cursor - lineStart);
        }

        private static string Quote(string value)
        {
            return SerializeUtf8Json(value ?? string.Empty);
        }

        /// <summary>
        /// Serializes JSON for client config files without <c>\uXXXX</c> escapes for
        /// printable Unicode. Control characters and JSON-required escapes stay escaped.
        /// </summary>
        public static string SerializeUtf8Json(object value)
        {
            string json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                .Serialize(value);
            return DecodeNonControlUnicodeEscapes(json);
        }

        private static string DecodeNonControlUnicodeEscapes(string json)
        {
            if (string.IsNullOrEmpty(json) || json.IndexOf("\\u", StringComparison.Ordinal) < 0)
            {
                return json;
            }

            var output = new StringBuilder(json.Length);
            for (int i = 0; i < json.Length; i++)
            {
                if (json[i] == '\\' &&
                    i + 5 < json.Length &&
                    json[i + 1] == 'u' &&
                    TryParseHex16(json, i + 2, out int codeUnit))
                {
                    // Keep escapes that JSON requires or that would change token structure.
                    if (codeUnit < 0x20 || codeUnit == 0x7F || codeUnit == '"' || codeUnit == '\\')
                    {
                        output.Append(json, i, 6);
                    }
                    else
                    {
                        output.Append((char)codeUnit);
                    }
                    i += 5;
                    continue;
                }

                output.Append(json[i]);
            }

            return output.ToString();
        }

        private static bool TryParseHex16(string source, int offset, out int value)
        {
            value = 0;
            for (int i = 0; i < 4; i++)
            {
                char ch = source[offset + i];
                int digit;
                if (ch >= '0' && ch <= '9') digit = ch - '0';
                else if (ch >= 'a' && ch <= 'f') digit = ch - 'a' + 10;
                else if (ch >= 'A' && ch <= 'F') digit = ch - 'A' + 10;
                else return false;
                value = (value << 4) | digit;
            }
            return true;
        }
    }
}
