using System;
using System.Collections.Generic;
using System.Linq;

namespace VSDebuggerMCP.Ipc
{
    internal static class ClientLaneRecovery
    {
        internal static bool TryFindDeadLaneRequest<T>(
            IDictionary<string, T> requests,
            string currentLane,
            string requestId,
            Func<string, bool> isLaneAlive,
            out string requestKey,
            out T request)
        {
            if (requests == null) throw new ArgumentNullException(nameof(requests));
            if (isLaneAlive == null) throw new ArgumentNullException(nameof(isLaneAlive));

            string exactKey = CreateRequestKey(currentLane, requestId);
            if (requests.TryGetValue(exactKey, out request))
            {
                requestKey = exactKey;
                return true;
            }

            string suffix = "\n" + requestId;
            KeyValuePair<string, T>[] candidates = requests
                .Where(pair =>
                    pair.Key.EndsWith(suffix, StringComparison.Ordinal) &&
                    !isLaneAlive(pair.Key.Substring(0, pair.Key.Length - suffix.Length)))
                .Take(2)
                .ToArray();
            if (candidates.Length == 1)
            {
                requestKey = candidates[0].Key;
                request = candidates[0].Value;
                return true;
            }

            requestKey = null;
            request = default(T);
            return false;
        }

        internal static string CreateRequestKey(string lane, string requestId) =>
            lane + "\n" + requestId;
    }
}
