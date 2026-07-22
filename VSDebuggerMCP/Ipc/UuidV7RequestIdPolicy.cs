using System;
using System.Globalization;
using System.Linq;

namespace VSDebuggerMCP.Ipc
{
    internal static class UuidV7RequestIdPolicy
    {
        internal static bool TryValidate(
            string requestId,
            DateTime utcNow,
            TimeSpan maximumAge,
            TimeSpan maximumFutureSkew,
            out string errorCode,
            out string errorMessage)
        {
            errorCode = null;
            errorMessage = null;
            if (!Guid.TryParseExact(requestId, "D", out Guid parsed) ||
                !string.Equals(parsed.ToString("D"), requestId, StringComparison.Ordinal))
            {
                errorCode = "INVALID_REQUEST_ID";
                errorMessage = "Command request ids must be canonical lowercase UUIDv7 values.";
                return false;
            }

            string compact = requestId.Replace("-", string.Empty);
            if (compact.Length != 32 || compact[12] != '7' ||
                "89ab".IndexOf(compact[16]) < 0 ||
                compact.Any(character => !Uri.IsHexDigit(character)) ||
                !long.TryParse(
                    compact.Substring(0, 12),
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out long unixMilliseconds))
            {
                errorCode = "INVALID_REQUEST_ID";
                errorMessage = "Command request ids must be canonical lowercase UUIDv7 values.";
                return false;
            }

            DateTime timestamp;
            try
            {
                timestamp = DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).UtcDateTime;
            }
            catch (ArgumentOutOfRangeException)
            {
                errorCode = "INVALID_REQUEST_ID";
                errorMessage = "The UUIDv7 timestamp is outside the supported range.";
                return false;
            }

            if (timestamp < utcNow - maximumAge)
            {
                errorCode = "REQUEST_ID_EXPIRED";
                errorMessage =
                    $"The command request id is older than the {maximumAge.TotalHours:0.#}-hour at-most-once window.";
                return false;
            }
            if (timestamp > utcNow + maximumFutureSkew)
            {
                errorCode = "INVALID_REQUEST_ID";
                errorMessage = "The UUIDv7 timestamp is too far in the future.";
                return false;
            }
            return true;
        }
    }
}
