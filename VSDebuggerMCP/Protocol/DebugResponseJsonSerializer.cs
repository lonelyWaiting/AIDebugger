using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Web.Script.Serialization;

namespace VSDebuggerMCP.Protocol
{
    /// <summary>
    /// Serializes protocol DTOs without the DataContractJsonSerializer __type metadata.
    /// That metadata is both an implementation leak and is interpreted as a type hint by
    /// some JSON decoders.  Convert the data-contract graph to JSON primitives first so
    /// dictionaries always remain JSON objects on the wire.
    /// </summary>
    internal static class DebugResponseJsonSerializer
    {
        public static string Serialize(DebugResponse response)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                .Serialize(ToJsonValue(response));
        }

        private static object ToJsonValue(object value)
        {
            if (value is float single)
            {
                if (float.IsNaN(single) || float.IsInfinity(single))
                    throw new SerializationException("Protocol JSON floating-point values must be finite");
                return single;
            }
            if (value is double number)
            {
                if (double.IsNaN(number) || double.IsInfinity(number))
                    throw new SerializationException("Protocol JSON floating-point values must be finite");
                return number;
            }
            if (value == null || value is string || value is bool ||
                value is byte || value is sbyte || value is short || value is ushort ||
                value is int || value is uint || value is long || value is ulong ||
                value is decimal)
            {
                return value;
            }

            if (value is Enum)
                return value.ToString();

            if (value is byte[] bytes)
                return Convert.ToBase64String(bytes);

            if (value is IDictionary dictionary)
            {
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (!(entry.Key is string key))
                        throw new SerializationException("Protocol JSON object keys must be strings");
                    result[key] = ToJsonValue(entry.Value);
                }
                return result;
            }

            if (value is IEnumerable enumerable)
            {
                var result = new List<object>();
                foreach (object item in enumerable)
                    result.Add(ToJsonValue(item));
                return result;
            }

            var members = value.GetType()
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(property => new
                {
                    Property = property,
                    DataMember = property.GetCustomAttribute<DataMemberAttribute>(),
                })
                .Where(item => item.DataMember != null && item.Property.CanRead)
                .OrderBy(item => item.DataMember.Order)
                .ThenBy(item => item.Property.MetadataToken)
                .ToArray();

            if (members.Length == 0)
                throw new SerializationException(
                    "Protocol value is not a supported primitive, collection, or data contract: " +
                    value.GetType().FullName);

            var objectResult = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var member in members)
            {
                object memberValue = member.Property.GetValue(value, null);
                if (!member.DataMember.EmitDefaultValue && IsDefaultValue(memberValue, member.Property.PropertyType))
                    continue;
                string name = string.IsNullOrEmpty(member.DataMember.Name)
                    ? member.Property.Name
                    : member.DataMember.Name;
                objectResult[name] = ToJsonValue(memberValue);
            }
            return objectResult;
        }

        private static bool IsDefaultValue(object value, Type type)
        {
            if (value == null) return true;
            if (!type.IsValueType) return false;
            return value.Equals(Activator.CreateInstance(type));
        }
    }
}
