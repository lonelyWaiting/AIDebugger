using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace VSDebuggerMCP.Ipc
{
    internal static class CommandResponseCompactor
    {
        private static readonly string[] OutcomeFields =
        {
            "success", "errorCode", "message", "commandIssued", "issuedContinue",
            "processStarted", "attachCompleted", "processId", "outcome", "timedOut",
            "breakDetected", "stopReason", "affectedCount", "remainingCount", "partial",
            "verificationStatus", "verificationMessage", "status",
        };

        internal static Dictionary<string, object> ExtractOutcome(
            string serializedResponse,
            string method,
            int originalResponseBytes)
        {
            var details = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["commandCompleted"] = true,
                ["method"] = method ?? string.Empty,
                ["originalResponseBytes"] = originalResponseBytes,
                ["responseTruncated"] = true,
            };

            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var envelope = serializer.DeserializeObject(serializedResponse) as Dictionary<string, object>;
                var result = envelope != null && envelope.TryGetValue("result", out object value)
                    ? value as Dictionary<string, object>
                    : null;
                if (result == null)
                {
                    return details;
                }

                foreach (string field in OutcomeFields)
                {
                    if (result.TryGetValue(field, out object fieldValue))
                    {
                        details[field] = fieldValue;
                    }
                }
            }
            catch
            {
                // The original response was already successfully serialized. Failure to
                // inspect its optional result fields must not erase the definitive fact
                // that this command completed and its body exceeded the response budget.
            }

            return details;
        }
    }
}
