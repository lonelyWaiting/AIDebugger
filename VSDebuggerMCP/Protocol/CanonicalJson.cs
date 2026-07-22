using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace VSDebuggerMCP.Protocol
{
    internal static class CanonicalJson
    {
        public static string Serialize(object value)
        {
            var output = new StringBuilder();
            WriteValue(output, value);
            return output.ToString();
        }

        private static void WriteValue(StringBuilder output, object value)
        {
            if (value == null)
            {
                output.Append("null");
                return;
            }
            if (value is IDictionary<string, object> dictionary)
            {
                output.Append('{');
                bool first = true;
                foreach (string key in dictionary.Keys.OrderBy(item => item, StringComparer.Ordinal))
                {
                    if (!first) output.Append(',');
                    first = false;
                    output.Append(new JavaScriptSerializer().Serialize(key));
                    output.Append(':');
                    WriteValue(output, dictionary[key]);
                }
                output.Append('}');
                return;
            }
            if (value is IDictionary untypedDictionary)
            {
                var normalized = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in untypedDictionary)
                {
                    if (!(entry.Key is string key))
                        throw new InvalidOperationException("Canonical JSON object keys must be strings");
                    normalized[key] = entry.Value;
                }
                WriteValue(output, normalized);
                return;
            }
            if (value is IEnumerable enumerable && !(value is string))
            {
                output.Append('[');
                bool first = true;
                foreach (object item in enumerable)
                {
                    if (!first) output.Append(',');
                    first = false;
                    WriteValue(output, item);
                }
                output.Append(']');
                return;
            }
            if (value is string || value is bool)
            {
                output.Append(new JavaScriptSerializer().Serialize(value));
                return;
            }
            if (value is byte || value is sbyte || value is short || value is ushort ||
                value is int || value is uint || value is long || value is ulong ||
                value is float || value is double || value is decimal)
            {
                if (value is double doubleValue && (double.IsNaN(doubleValue) || double.IsInfinity(doubleValue)))
                    throw new InvalidOperationException("Canonical JSON does not support non-finite numbers");
                if (value is float floatValue && (float.IsNaN(floatValue) || float.IsInfinity(floatValue)))
                    throw new InvalidOperationException("Canonical JSON does not support non-finite numbers");
                output.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }
            throw new InvalidOperationException("Unsupported canonical JSON value type: " + value.GetType().FullName);
        }
    }
}
