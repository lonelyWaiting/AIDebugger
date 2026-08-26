using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace VSDebuggerMCP.Services
{
    /// <summary>
    /// Serializes the opaque cursor used to continue mapped-minidump searches.
    /// A cursor is bound to the exact dump metadata, paused debugger snapshot,
    /// and parsed pattern that produced it so it cannot silently change query
    /// semantics when it is reused.
    /// </summary>
    internal static class MappedDumpSearchContinuationCodec
    {
        private const int CursorVersion = 3;
        private const int MaximumCursorLength = 512;

        internal static string Create(
            int rangeIndex,
            long rangeOffset,
            int carryBytes,
            string dumpIdentity,
            string snapshotToken,
            byte[] patternBytes)
        {
            if (rangeIndex < 0 || rangeIndex > 1000000) throw new ArgumentOutOfRangeException(nameof(rangeIndex));
            if (rangeOffset < 0) throw new ArgumentOutOfRangeException(nameof(rangeOffset));
            if (carryBytes < 0) throw new ArgumentOutOfRangeException(nameof(carryBytes));
            ValidateBindingInputs(dumpIdentity, snapshotToken, patternBytes);
            if (carryBytes >= patternBytes.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(carryBytes));
            }

            var value = new MappedDumpSearchContinuationState
            {
                Version = CursorVersion,
                Source = "mapped_dump",
                RangeIndex = rangeIndex,
                RangeOffset = rangeOffset,
                CarryBytes = carryBytes,
                DumpIdentityDigest = ComputeStringDigest(dumpIdentity),
                SnapshotDigest = ComputeStringDigest(snapshotToken),
                PatternDigest = ComputeBytesDigest(patternBytes),
            };
            string json = new JavaScriptSerializer().Serialize(value);
            string cursor = ToBase64Url(Encoding.UTF8.GetBytes(json));
            if (cursor.Length > MaximumCursorLength)
            {
                throw new InvalidOperationException("Mapped-memory cursor exceeds the protocol size limit.");
            }
            return cursor;
        }

        internal static bool TryDecode(
            string continuation,
            int patternLength,
            string dumpIdentity,
            string snapshotToken,
            byte[] patternBytes,
            out int rangeIndex,
            out ulong rangeOffset,
            out int carryBytes,
            out string error)
        {
            rangeIndex = 0;
            rangeOffset = 0;
            carryBytes = 0;
            error = null;
            if (string.IsNullOrWhiteSpace(continuation))
            {
                return true;
            }
            if (patternLength <= 0 || patternBytes == null || patternBytes.Length != patternLength ||
                string.IsNullOrEmpty(dumpIdentity) || string.IsNullOrEmpty(snapshotToken))
            {
                error = "continuation cannot be validated for the active mapped-memory search";
                return false;
            }
            if (continuation.Length > MaximumCursorLength ||
                !Regex.IsMatch(continuation, "^[A-Za-z0-9_-]+$"))
            {
                error = "continuation is not a valid mapped-memory cursor";
                return false;
            }

            try
            {
                string base64 = continuation.Replace('-', '+').Replace('_', '/');
                switch (base64.Length % 4)
                {
                    case 2: base64 += "=="; break;
                    case 3: base64 += "="; break;
                    case 1:
                        error = "continuation is not a valid mapped-memory cursor";
                        return false;
                }

                var value = new JavaScriptSerializer().Deserialize<MappedDumpSearchContinuationState>(
                    Encoding.UTF8.GetString(Convert.FromBase64String(base64)));
                if (value == null || value.Version != CursorVersion ||
                    !string.Equals(value.Source, "mapped_dump", StringComparison.Ordinal) ||
                    value.RangeIndex < 0 || value.RangeIndex > 1000000 || value.RangeOffset < 0 ||
                    value.CarryBytes < 0 || value.CarryBytes >= patternLength ||
                    string.IsNullOrEmpty(value.DumpIdentityDigest) ||
                    string.IsNullOrEmpty(value.SnapshotDigest) ||
                    string.IsNullOrEmpty(value.PatternDigest))
                {
                    error = "continuation is not a valid mapped-memory cursor";
                    return false;
                }

                if (!string.Equals(value.DumpIdentityDigest, ComputeStringDigest(dumpIdentity), StringComparison.Ordinal) ||
                    !string.Equals(value.SnapshotDigest, ComputeStringDigest(snapshotToken), StringComparison.Ordinal) ||
                    !string.Equals(value.PatternDigest, ComputeBytesDigest(patternBytes), StringComparison.Ordinal))
                {
                    error = "continuation does not match the active dump, debugger context, or search pattern; restart the search";
                    return false;
                }

                rangeIndex = value.RangeIndex;
                rangeOffset = (ulong)value.RangeOffset;
                carryBytes = value.CarryBytes;
                return true;
            }
            catch (Exception)
            {
                error = "continuation is not a valid mapped-memory cursor";
                return false;
            }
        }

        internal static string CreateDumpIdentity(string dumpPath)
        {
            if (string.IsNullOrWhiteSpace(dumpPath))
            {
                throw new ArgumentException("A dump path is required.", nameof(dumpPath));
            }

            var info = new FileInfo(dumpPath);
            if (!info.Exists)
            {
                throw new FileNotFoundException("The mapped dump no longer exists.", dumpPath);
            }
            return string.Concat(
                Path.GetFullPath(info.FullName), "\n",
                info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), "\n",
                info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        private static void ValidateBindingInputs(string dumpIdentity, string snapshotToken, byte[] patternBytes)
        {
            if (string.IsNullOrEmpty(dumpIdentity))
            {
                throw new ArgumentException("A dump identity is required.", nameof(dumpIdentity));
            }
            if (string.IsNullOrEmpty(snapshotToken))
            {
                throw new ArgumentException("A debugger snapshot identity is required.", nameof(snapshotToken));
            }
            if (patternBytes == null || patternBytes.Length == 0)
            {
                throw new ArgumentException("A non-empty search pattern is required.", nameof(patternBytes));
            }
        }

        private static string ComputeStringDigest(string value)
        {
            return ComputeBytesDigest(Encoding.UTF8.GetBytes(value));
        }

        private static string ComputeBytesDigest(byte[] value)
        {
            using (var sha256 = SHA256.Create())
            {
                return ToBase64Url(sha256.ComputeHash(value));
            }
        }

        private static string ToBase64Url(byte[] value)
        {
            return Convert.ToBase64String(value)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private sealed class MappedDumpSearchContinuationState
        {
            public int Version { get; set; }
            public string Source { get; set; }
            public int RangeIndex { get; set; }
            public long RangeOffset { get; set; }
            public int CarryBytes { get; set; }
            public string DumpIdentityDigest { get; set; }
            public string SnapshotDigest { get; set; }
            public string PatternDigest { get; set; }
        }
    }
}
