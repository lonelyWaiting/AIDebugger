using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Pipes;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;
using VSDebuggerMCP.Ipc;
using VSDebuggerMCP.Protocol;


namespace VSDebuggerMCP
{
    public partial class NamedPipeServer
    {
        private DebugMethodDescriptor ValidateRequestEnvelopeImmutable(DebugRequest request, int clientProcessId)
        {
            if (request == null)
            {
                throw new ProtocolRequestException("INVALID_REQUEST", "Request envelope is required");
            }
            if (!string.Equals(request.Protocol, ProtocolVersion, StringComparison.Ordinal))
            {
                throw new ProtocolRequestException(
                    "PROTOCOL_MISMATCH",
                    $"Expected protocol '{ProtocolVersion}'");
            }
            if (string.IsNullOrWhiteSpace(request.Id))
            {
                throw new ProtocolRequestException("INVALID_REQUEST", "Missing request id");
            }
            if (!Guid.TryParseExact(request.Id, "D", out Guid parsedRequestId) ||
                !string.Equals(parsedRequestId.ToString("D"), request.Id, StringComparison.Ordinal))
            {
                throw new ProtocolRequestException(
                    "INVALID_REQUEST",
                    "Request id must be a canonical lowercase UUID in D format");
            }
            if (string.IsNullOrWhiteSpace(request.Method))
            {
                throw new ProtocolRequestException("INVALID_REQUEST", "Missing method");
            }
            if (request.Method.Length > MaxMethodNameLength)
            {
                throw new ProtocolRequestException("INVALID_REQUEST", "Method name is too long");
            }
            if (request.Meta == null)
            {
                throw new ProtocolRequestException("UNAUTHENTICATED", "Request metadata is required");
            }
            if (!FixedTimeEquals(request.Meta.AuthToken, _instanceRegistration.AuthToken))
            {
                throw new ProtocolRequestException("UNAUTHENTICATED", "Invalid instance authentication token");
            }
            DebugMethodDescriptor descriptor;
            try
            {
                descriptor = DebugRequestSchemaValidator.Validate(
                    request,
                    UtcNowUnixMilliseconds(),
                    validateDeadline: false);
                DebugRequestSchemaValidator.ValidateDeadlineHorizon(
                    request,
                    UtcNowUnixMilliseconds(),
                    descriptor);
            }
            catch (DebugRequestSchemaException ex)
            {
                throw new ProtocolRequestException(ex.Code, ex.Message);
            }
            string missingCapability = DebugMethodCatalog.GetRequiredCapabilities(descriptor, request.Params)
                .FirstOrDefault(capability => !_allowedCapabilities.Contains(capability));
            if (missingCapability != null)
            {
                throw new ProtocolRequestException(
                    "PERMISSION_DENIED",
                    $"Capability '{missingCapability}' is not enabled for this Visual Studio server");
            }
            if (request.Meta.ClientProcessId != clientProcessId)
            {
                throw new ProtocolRequestException(
                    "UNAUTHENTICATED",
                    "Declared client PID does not match the named-pipe client process");
            }
            try
            {
                using (var clientProcess = System.Diagnostics.Process.GetProcessById(clientProcessId))
                {
                    long actualStartTicks = clientProcess.StartTime.ToUniversalTime().Ticks;
                    if (request.Meta.ClientProcessStartTimeUtcTicks != actualStartTicks)
                    {
                        throw new ProtocolRequestException(
                            "UNAUTHENTICATED",
                            "Declared client process creation time does not match the named-pipe client process");
                    }
                }
            }
            catch (ProtocolRequestException)
            {
                throw;
            }
            catch (Exception)
            {
                throw new ProtocolRequestException(
                    "UNAUTHENTICATED",
                    "Unable to validate the named-pipe client process identity");
            }
            if (!ProcessIntegrityValidator.TryValidateClientAgainstCurrentProcess(
                clientProcessId,
                out string integrityError))
            {
                throw new ProtocolRequestException("PERMISSION_DENIED", integrityError);
            }

            DebugTargetIdentity target = request.Meta.Target;
            if (target == null ||
                target.ProcessId != _processId ||
                target.ProcessStartTimeUtcTicks != _processStartTimeUtcTicks ||
                !string.Equals(target.ServerInstanceId, _serverInstanceId, StringComparison.Ordinal))
            {
                throw new ProtocolRequestException("STALE_TARGET", "Request target does not match this VS instance");
            }

            return descriptor;
        }

        private void ValidateRequestMutableIdentity(
            DebugRequest request,
            DebugMethodDescriptor descriptor)
        {
            try
            {
                DebugRequestSchemaValidator.ValidateDeadline(
                    request,
                    UtcNowUnixMilliseconds(),
                    descriptor);
            }
            catch (DebugRequestSchemaException ex)
            {
                throw new ProtocolRequestException(ex.Code, ex.Message);
            }

            DebugTargetIdentity target = request.Meta.Target;
            long currentGeneration = _debuggerService.DebugSessionGeneration;
            if (descriptor.RequiresStableDebugSession &&
                target.DebugSessionGeneration != currentGeneration)
            {
                throw new ProtocolRequestException(
                    "STALE_DEBUG_SESSION",
                    $"Debug session generation changed from {target.DebugSessionGeneration} to {currentGeneration}");
            }
            if (descriptor.RequiresStableDebugContext &&
                (target.BreakSequence != _debuggerService.BreakSequence ||
                 target.ContextEpoch != _debuggerService.ContextEpoch))
            {
                throw new ProtocolRequestException(
                    "STALE_DEBUG_CONTEXT",
                    $"Debug context changed from {target.BreakSequence}:{target.ContextEpoch} to " +
                    $"{_debuggerService.BreakSequence}:{_debuggerService.ContextEpoch}");
            }
        }

        private static bool IsSessionRefreshMethod(string method)
        {
            return DebugMethodCatalog.Get(method)?.SessionRefresh == true;
        }

        private static long UtcNowUnixMilliseconds()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        private static bool FixedTimeEquals(string left, string right)
        {
            byte[] leftBytes = Encoding.UTF8.GetBytes(left ?? string.Empty);
            byte[] rightBytes = Encoding.UTF8.GetBytes(right ?? string.Empty);
            int difference = leftBytes.Length ^ rightBytes.Length;
            int length = Math.Max(leftBytes.Length, rightBytes.Length);
            for (int index = 0; index < length; index++)
            {
                byte leftByte = index < leftBytes.Length ? leftBytes[index] : (byte)0;
                byte rightByte = index < rightBytes.Length ? rightBytes[index] : (byte)0;
                difference |= leftByte ^ rightByte;
            }
            return difference == 0;
        }

        private static HashSet<string> ResolveAllowedCapabilities(IEnumerable<string> requestedCapabilities)
        {
            var known = new HashSet<string>(DebugMethodCatalog.AllCapabilities, StringComparer.Ordinal);
            var selected = requestedCapabilities == null
                ? new HashSet<string>(new[] { "server.control", "debug.read" }, StringComparer.Ordinal)
                : new HashSet<string>(requestedCapabilities, StringComparer.Ordinal);
            if (selected.Count == 0 || selected.Any(capability => !known.Contains(capability)))
            {
                throw new InvalidOperationException("Requested AIDebugger capability set is empty or contains an unknown capability");
            }
            if (!selected.Contains("server.control"))
            {
                throw new InvalidOperationException(
                    "Requested AIDebugger capabilities must include server.control");
            }

            string configured = Environment.GetEnvironmentVariable("AIDB_ALLOWED_CAPABILITIES");
            if (string.IsNullOrWhiteSpace(configured))
            {
                return selected;
            }

            var configuredSet = new HashSet<string>(StringComparer.Ordinal);
            foreach (string raw in configured.Split(','))
            {
                string capability = raw.Trim();
                if (capability.Length == 0) continue;
                if (!known.Contains(capability))
                {
                    throw new InvalidOperationException("Unknown AIDB_ALLOWED_CAPABILITIES value: " + capability);
                }
                configuredSet.Add(capability);
            }
            selected.IntersectWith(configuredSet);
            if (selected.Count == 0)
            {
                throw new InvalidOperationException("AIDB_ALLOWED_CAPABILITIES removed every requested capability");
            }
            if (!selected.Contains("server.control"))
            {
                throw new InvalidOperationException(
                    "AIDB_ALLOWED_CAPABILITIES must retain server.control for authenticated discovery and recovery");
            }
            return selected;
        }

        private static string GetMethodKind(string method)
        {
            return DebugMethodCatalog.Get(method)?.Kind ?? "query";
        }

        private List<string> GetStringListParam(Dictionary<string, object> parameters, string key)
        {
            var result = new List<string>();
            if (parameters == null || !parameters.TryGetValue(key, out object value) || value == null)
            {
                return result;
            }

            if (value is string single)
            {
                if (!string.IsNullOrWhiteSpace(single))
                {
                    ValidateExpression(single, key);
                    result.Add(single);
                }
                return result;
            }

            if (value is System.Collections.IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (!(item is string expression))
                    {
                        throw new ParameterValidationException($"Parameter '{key}' must contain only strings.");
                    }

                    if (!string.IsNullOrWhiteSpace(expression))
                    {
                        ValidateExpression(expression, key);
                        result.Add(expression);
                        if (result.Count > MaxExpressionCount)
                        {
                            throw new ParameterValidationException(
                                $"Parameter '{key}' must contain at most {MaxExpressionCount} expressions.");
                        }
                    }
                }

                return result;
            }

            throw new ParameterValidationException($"Parameter '{key}' must be a string or an array of strings.");
        }

        private static void ValidateExpression(string expression, string key)
        {
            if (expression.Length > MaxExpressionLength)
            {
                throw new ParameterValidationException(
                    $"Parameter '{key}' contains an expression longer than {MaxExpressionLength} characters.");
            }
        }

        private static void ValidateAddressLiteral(string key, string value)
        {
            string trimmed = (value ?? string.Empty).Trim();
            ulong parsed = 0;
            bool valid;
            if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                valid = trimmed.Length > 2 && ulong.TryParse(
                    trimmed.Substring(2),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out parsed);
            }
            else if (trimmed.EndsWith("h", StringComparison.OrdinalIgnoreCase))
            {
                valid = trimmed.Length > 1 && ulong.TryParse(
                    trimmed.Substring(0, trimmed.Length - 1),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out parsed);
            }
            else
            {
                valid = ulong.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) ||
                    ulong.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed);
            }

            if (!valid || parsed == 0)
            {
                throw new ParameterValidationException(
                    $"Parameter '{key}' must be a non-zero unsigned decimal or hexadecimal address literal.");
            }
        }

        private static string GetJsonTypeName(Type type)
        {
            if (type == typeof(int) || type == typeof(long)) return "JSON integer";
            if (type == typeof(bool)) return "JSON boolean";
            if (type == typeof(string)) return "JSON string";
            return type.Name;
        }

        private sealed class ParameterValidationException : Exception
        {
            public ParameterValidationException(string message) : base(message)
            {
            }
        }

        private sealed class UnsupportedEvaluationPolicyException : Exception
        {
            public UnsupportedEvaluationPolicyException(string message) : base(message)
            {
            }
        }

    }
}
