using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace VSDebuggerMCP.Ipc
{
    internal static class StrictJsonRequestValidator
    {
        internal const int MaxNestingDepth = 64;
        internal const int MaxNumberTokenLength = 128;
        private static readonly HashSet<string> RootFields = Fields("protocol", "id", "method", "params", "meta");
        private static readonly HashSet<string> MetaFields = Fields(
            "authToken", "deadlineUnixMs", "kind", "capability",
            "clientProcessId", "clientProcessStartTimeUtcTicks", "target");
        private static readonly HashSet<string> TargetFields = Fields(
            "processId", "processStartTimeUtcTicks", "serverInstanceId",
            "debugSessionGeneration", "breakSequence", "contextEpoch");

        public static void Validate(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new FormatException("Request must be a JSON object");
            new DuplicateKeyScanner(json).Validate();
            var root = RequireObject(new JavaScriptSerializer().DeserializeObject(json), "request");
            RejectUnknown(root, RootFields, "request");
            if (root.ContainsKey("params")) RequireObject(root["params"], "params");
            var meta = RequireObject(GetRequired(root, "meta"), "meta");
            RejectUnknown(meta, MetaFields, "meta");
            var target = RequireObject(GetRequired(meta, "target"), "meta.target");
            RejectUnknown(target, TargetFields, "meta.target");
        }

        private static object GetRequired(Dictionary<string, object> value, string key)
        {
            if (!value.TryGetValue(key, out object result) || result == null)
                throw new FormatException("Missing required JSON object '" + key + "'");
            return result;
        }

        private static Dictionary<string, object> RequireObject(object value, string path)
        {
            var result = value as Dictionary<string, object>;
            if (result == null) throw new FormatException("JSON field '" + path + "' must be an object");
            return result;
        }

        private static void RejectUnknown(Dictionary<string, object> value, HashSet<string> allowed, string path)
        {
            foreach (string key in value.Keys)
                if (!allowed.Contains(key)) throw new FormatException("Unknown JSON field '" + path + "." + key + "'");
        }

        private static HashSet<string> Fields(params string[] values) =>
            new HashSet<string>(values, StringComparer.Ordinal);

        private sealed class DuplicateKeyScanner
        {
            private readonly string _source;
            private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();
            private int _position;
            public DuplicateKeyScanner(string source) { _source = source; }

            public void Validate()
            {
                SkipWhitespace();
                ParseValue(0);
                SkipWhitespace();
                if (_position != _source.Length) throw new FormatException("Unexpected content after JSON value");
            }

            private void ParseValue(int depth)
            {
                SkipWhitespace();
                if (_position >= _source.Length) throw new FormatException("Missing JSON value");
                if (depth > MaxNestingDepth)
                    throw new FormatException("JSON nesting exceeds the maximum depth of " + MaxNestingDepth);
                switch (_source[_position])
                {
                    case '{': ParseObject(depth); return;
                    case '[': ParseArray(depth); return;
                    case '"': ParseString(); return;
                    default: ParsePrimitive(); return;
                }
            }

            private void ParseObject(int depth)
            {
                _position++;
                var keys = new HashSet<string>(StringComparer.Ordinal);
                SkipWhitespace();
                if (Consume('}')) return;
                while (true)
                {
                    SkipWhitespace();
                    if (_position >= _source.Length || _source[_position] != '"')
                        throw new FormatException("Expected JSON object property");
                    int start = _position;
                    int end = ParseString();
                    string key = _serializer.Deserialize<string>(_source.Substring(start, end - start));
                    if (!keys.Add(key)) throw new FormatException("Duplicate JSON property '" + key + "'");
                    SkipWhitespace();
                    if (!Consume(':')) throw new FormatException("Expected ':' after JSON property");
                    ParseValue(depth + 1);
                    SkipWhitespace();
                    if (Consume('}')) return;
                    if (!Consume(',')) throw new FormatException("Expected ',' between JSON properties");
                }
            }

            private void ParseArray(int depth)
            {
                _position++;
                SkipWhitespace();
                if (Consume(']')) return;
                while (true)
                {
                    ParseValue(depth + 1);
                    SkipWhitespace();
                    if (Consume(']')) return;
                    if (!Consume(',')) throw new FormatException("Expected ',' between JSON array values");
                }
            }

            private int ParseString()
            {
                if (!Consume('"')) throw new FormatException("Expected JSON string");
                bool escaped = false;
                while (_position < _source.Length)
                {
                    char value = _source[_position++];
                    if (escaped) { escaped = false; continue; }
                    if (value == '\\') { escaped = true; continue; }
                    if (value == '"') return _position;
                    if (value < 0x20) throw new FormatException("JSON string contains a control character");
                }
                throw new FormatException("Unterminated JSON string");
            }

            private void ParsePrimitive()
            {
                int start = _position;
                while (_position < _source.Length && _source[_position] != ',' &&
                    _source[_position] != '}' && _source[_position] != ']' &&
                    !IsJsonWhitespace(_source[_position])) _position++;
                if (_position == start) throw new FormatException("Missing JSON primitive");
                string token = _source.Substring(start, _position - start);
                if (token == "true" || token == "false" || token == "null") return;
                if (!IsStrictJsonNumber(token))
                    throw new FormatException("Invalid JSON primitive");
            }

            private static bool IsStrictJsonNumber(string token)
            {
                if (string.IsNullOrEmpty(token) || token.Length > MaxNumberTokenLength) return false;
                int index = 0;
                if (index < token.Length && token[index] == '-') index++;
                if (index >= token.Length) return false;
                if (token[index] == '0')
                {
                    index++;
                    if (index < token.Length && IsAsciiDigit(token[index])) return false;
                }
                else
                {
                    if (token[index] < '1' || token[index] > '9') return false;
                    while (index < token.Length && IsAsciiDigit(token[index])) index++;
                }
                bool hasFractionOrExponent = false;
                if (index < token.Length && token[index] == '.')
                {
                    hasFractionOrExponent = true;
                    index++;
                    int fractionStart = index;
                    while (index < token.Length && IsAsciiDigit(token[index])) index++;
                    if (index == fractionStart) return false;
                }
                if (index < token.Length && (token[index] == 'e' || token[index] == 'E'))
                {
                    hasFractionOrExponent = true;
                    index++;
                    if (index < token.Length && (token[index] == '+' || token[index] == '-')) index++;
                    int exponentStart = index;
                    while (index < token.Length && IsAsciiDigit(token[index])) index++;
                    if (index == exponentStart) return false;
                }
                if (index != token.Length) return false;

                if (hasFractionOrExponent)
                {
                    if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                        return false;
                    return !double.IsNaN(number) && !double.IsInfinity(number);
                }

                bool negative = token[0] == '-';
                string magnitude = negative ? token.Substring(1) : token;
                if (!ulong.TryParse(magnitude, NumberStyles.None, CultureInfo.InvariantCulture, out ulong integer))
                    return false;
                return !negative || integer <= 9223372036854775808UL;
            }

            private static bool IsAsciiDigit(char value) => value >= '0' && value <= '9';

            private static bool IsJsonWhitespace(char value) =>
                value == ' ' || value == '\t' || value == '\r' || value == '\n';

            private void SkipWhitespace()
            {
                while (_position < _source.Length && IsJsonWhitespace(_source[_position])) _position++;
            }

            private bool Consume(char value)
            {
                if (_position >= _source.Length || _source[_position] != value) return false;
                _position++;
                return true;
            }
        }
    }
}
