using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VSDebuggerMCP.Protocol;

namespace VSDebuggerMCP.Ipc
{
    /// <summary>
    /// Pure protocol/schema validation with no Visual Studio or transport dependency.
    /// Authentication, client identity and target/session binding remain transport concerns.
    /// </summary>
    internal static class DebugRequestSchemaValidator
    {
        private const int MaxExpressionCount = 100;
        private const int MaxExpressionLength = 16 * 1024;
        private static readonly HashSet<string> BooleanParameters = new HashSet<string>(StringComparer.Ordinal)
        {
            "expandChildren", "includeLocals", "includeModules", "includeRegisters", "includeOutput",
            "includeThreads", "includeException", "includeFrames", "includeNative", "includeBreakpoints",
            "threadIncludeFrames", "attach", "enabled", "isRegex",
        };
        private static readonly HashSet<string> IntegerParameters = new HashSet<string>(StringComparer.Ordinal)
        {
            "depth", "maxDepth", "threadId", "frameIndex", "processId", "threadLookupBudgetMs", "processLookupBudgetMs",
            "maxFramesPerThread", "maxFrames", "maxFramesScanned", "maxNodes", "budgetMs", "startThreadIndex", "startFrameIndex", "startModuleIndex",
            "startIndex", "outputTailLines", "threadMaxFramesPerThread", "threadMaxNodes", "threadBudgetMs",
            "size", "linesBefore", "linesAfter", "count", "maxImportFunctions", "maxExports", "disassemblyCount",
            "stackBytes", "timeoutMs", "pollIntervalMs", "maxTypes", "maxMethodsPerType", "maxMethods",
            "overloadIndex", "maxInstructions", "lineNumber", "hitCount", "byteCount", "breakpointLookupBudgetMs",
            "maxResults", "searchSize", "tailLines", "contextLines", "maxMatches", "endCharacter",
            "expectedCaptureStartCharacter", "waitTimeoutMs", "maxBytes", "startLine",
        };
        private static readonly HashSet<string> LongParameters = new HashSet<string>(StringComparer.Ordinal)
        {
            "debugSessionGeneration", "breakSequence", "contextEpoch", "startOffset",
        };
        private static readonly HashSet<string> CollectionParameters = new HashSet<string>(StringComparer.Ordinal)
        {
            "expressions",
        };

        internal static DebugMethodDescriptor Validate(
            DebugRequest request,
            long nowUnixMs,
            bool validateDeadline = true)
        {
            DebugMethodDescriptor descriptor = DebugMethodCatalog.Get(request?.Method);
            if (descriptor == null)
            {
                throw new DebugRequestSchemaException(
                    "METHOD_NOT_FOUND",
                    $"Unknown debugger method '{request?.Method}'");
            }

            if (validateDeadline) ValidateDeadline(request, nowUnixMs, descriptor);

            if (!string.Equals(request.Meta.Kind, descriptor.Kind, StringComparison.Ordinal))
            {
                throw new DebugRequestSchemaException(
                    "INVALID_REQUEST",
                    $"Method '{request.Method}' must declare kind '{descriptor.Kind}'");
            }
            if (!string.Equals(request.Meta.Capability, descriptor.Capability, StringComparison.Ordinal))
            {
                throw new DebugRequestSchemaException(
                    "INVALID_REQUEST",
                    $"Method '{request.Method}' must declare capability '{descriptor.Capability}'");
            }

            var parameters = request.Params ?? new Dictionary<string, object>();
            var allowed = new HashSet<string>(descriptor.AllowedParameters, StringComparer.Ordinal);
            string unknown = parameters.Keys.FirstOrDefault(key => !allowed.Contains(key));
            if (unknown != null)
            {
                throw new DebugRequestSchemaException(
                    "INVALID_ARGUMENT",
                    $"Unknown parameter 'params.{unknown}' for method '{descriptor.Name}'.");
            }
            foreach (string required in descriptor.RequiredParameters)
            {
                if (!parameters.TryGetValue(required, out object value) || value == null ||
                    (value is string text && string.IsNullOrWhiteSpace(text)))
                {
                    throw new DebugRequestSchemaException(
                        "INVALID_ARGUMENT",
                        $"Required parameter 'params.{required}' is missing or empty.");
                }
            }
            ValidateAllParameterValues(parameters);
            if (descriptor.Name == "attach_process" &&
                parameters.ContainsKey("processId") == parameters.ContainsKey("processName"))
            {
                throw new DebugRequestSchemaException(
                    "INVALID_ARGUMENT",
                    "attach_process requires exactly one of 'params.processId' or 'params.processName'.");
            }
            if (descriptor.Name == "attach_process" && parameters.ContainsKey("processName") &&
                (!(parameters["processName"] is string processName) || string.IsNullOrWhiteSpace(processName)))
            {
                throw new DebugRequestSchemaException(
                    "INVALID_ARGUMENT", "params.processName must be a non-empty string.");
            }
            if (descriptor.Name == "switch_process" &&
                parameters.ContainsKey("processHandle") == parameters.ContainsKey("processId"))
            {
                throw new DebugRequestSchemaException(
                    "INVALID_ARGUMENT",
                    "switch_process requires exactly one of 'params.processHandle' or legacy 'params.processId'.");
            }
            if (descriptor.Name == "start_debugging")
            {
                if (parameters.ContainsKey("executablePath") &&
                    (!(parameters["executablePath"] is string suppliedExecutable) ||
                     string.IsNullOrWhiteSpace(suppliedExecutable)))
                    throw new DebugRequestSchemaException(
                        "INVALID_ARGUMENT", "params.executablePath must be a non-empty string when supplied.");
                if (parameters.ContainsKey("workingDirectory") &&
                    (!(parameters["workingDirectory"] is string suppliedDirectory) ||
                     string.IsNullOrWhiteSpace(suppliedDirectory)))
                    throw new DebugRequestSchemaException(
                        "INVALID_ARGUMENT", "params.workingDirectory must be a non-empty string when supplied.");
                string executable = parameters.TryGetValue("executablePath", out object executableValue)
                    ? executableValue as string
                    : null;
                string extension;
                try
                {
                    extension = string.IsNullOrWhiteSpace(executable)
                        ? string.Empty
                        : Path.GetExtension(executable);
                }
                catch (ArgumentException)
                {
                    throw new DebugRequestSchemaException(
                        "INVALID_ARGUMENT", "params.executablePath is not a valid path.");
                }
                bool isDump = string.Equals(extension, ".dmp", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".mdmp", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".hdmp", StringComparison.OrdinalIgnoreCase);
                bool hasLaunchOnly = parameters.ContainsKey("workingDirectory") ||
                    parameters.ContainsKey("arguments") || parameters.ContainsKey("attach");
                if ((string.IsNullOrWhiteSpace(executable) || isDump) && hasLaunchOnly)
                {
                    throw new DebugRequestSchemaException(
                        "INVALID_ARGUMENT",
                        "workingDirectory, arguments, and attach require a non-dump executablePath.");
                }
            }
            if (descriptor.Name == "search_memory")
            {
                string pattern = parameters.TryGetValue("pattern", out object patternValue)
                    ? patternValue as string
                    : null;
                string patternFormat = parameters.TryGetValue("patternFormat", out object formatValue)
                    ? formatValue as string
                    : "auto";
                if (pattern == null || (parameters.ContainsKey("patternFormat") && patternFormat == null))
                {
                    throw new DebugRequestSchemaException(
                        "INVALID_ARGUMENT", "params.pattern and params.patternFormat must be strings.");
                }
                try
                {
                    SearchPatternParser.Parse(pattern, patternFormat);
                }
                catch (DebuggerParameterValidationException ex)
                {
                    throw new DebugRequestSchemaException("INVALID_ARGUMENT", ex.Message);
                }
            }
            if (descriptor.Name == "search_output" &&
                parameters.TryGetValue("isRegex", out object regexFlag) &&
                regexFlag is bool isRegex && isRegex)
            {
                if (!(parameters.TryGetValue("pattern", out object regexPatternValue) &&
                    regexPatternValue is string regexPattern))
                {
                    throw new DebugRequestSchemaException(
                        "INVALID_ARGUMENT", "params.pattern must be a string.");
                }
                try
                {
                    _ = new Regex(regexPattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
                }
                catch (ArgumentException ex)
                {
                    throw new DebugRequestSchemaException(
                        "INVALID_ARGUMENT", "params.pattern is not a valid regular expression: " + ex.Message);
                }
            }
            return descriptor;
        }

        internal static void ValidateDeadline(
            DebugRequest request,
            long nowUnixMs,
            DebugMethodDescriptor descriptor)
        {
            if (request.Meta.DeadlineUnixMs == 0)
            {
                request.Meta.DeadlineUnixMs = nowUnixMs + descriptor.DefaultDeadlineMs;
            }
            else if (request.Meta.DeadlineUnixMs < 0 || request.Meta.DeadlineUnixMs <= nowUnixMs)
            {
                throw new DebugRequestSchemaException(
                    "DEADLINE_EXCEEDED_NOT_STARTED",
                    "Request deadline expired before dispatch");
            }
            else if (request.Meta.DeadlineUnixMs - nowUnixMs > descriptor.MaxDeadlineMs)
            {
                throw new DebugRequestSchemaException(
                    "INVALID_ARGUMENT",
                    $"Request deadline may be at most {descriptor.MaxDeadlineMs}ms in the future.");
            }
        }

        internal static void ValidateDeadlineHorizon(
            DebugRequest request,
            long nowUnixMs,
            DebugMethodDescriptor descriptor)
        {
            if (request.Meta.DeadlineUnixMs > nowUnixMs &&
                request.Meta.DeadlineUnixMs - nowUnixMs > descriptor.MaxDeadlineMs)
            {
                throw new DebugRequestSchemaException(
                    "INVALID_ARGUMENT",
                    $"Request deadline may be at most {descriptor.MaxDeadlineMs}ms in the future.");
            }
        }

        private static void ValidateAllParameterValues(IDictionary<string, object> parameters)
        {
            foreach (KeyValuePair<string, object> pair in parameters)
            {
                string key = pair.Key;
                object value = pair.Value;
                if (value == null)
                    throw new DebugRequestSchemaException("INVALID_ARGUMENT", $"params.{key} must not be null.");
                if (BooleanParameters.Contains(key))
                {
                    if (!(value is bool)) ThrowType(key, "boolean");
                    continue;
                }
                if (IntegerParameters.Contains(key))
                {
                    if (!TryGetInt64(value, out long integer) || integer < int.MinValue || integer > int.MaxValue)
                        ThrowType(key, "integer");
                    ValidateIntegerRange(key, (int)integer);
                    continue;
                }
                if (LongParameters.Contains(key))
                {
                    if (!TryGetInt64(value, out long integer)) ThrowType(key, "integer");
                    if (integer < 0)
                        throw new DebugRequestSchemaException("INVALID_ARGUMENT", $"params.{key} must be non-negative.");
                    continue;
                }
                if (CollectionParameters.Contains(key))
                {
                    ValidateExpressions(key, value);
                    continue;
                }
                if (!(value is string)) ThrowType(key, "string");
                ValidateString(key, (string)value);
            }
        }

        private static bool TryGetInt64(object value, out long result)
        {
            if (value is int intValue) { result = intValue; return true; }
            if (value is long longValue) { result = longValue; return true; }
            result = 0;
            return false;
        }

        private static void ValidateExpressions(string key, object value)
        {
            if (value is string single)
            {
                ValidateExpression(key, single);
                return;
            }
            if (!(value is System.Collections.IEnumerable)) ThrowType(key, "string or array of strings");
            int count = 0;
            foreach (object item in (System.Collections.IEnumerable)value)
            {
                if (!(item is string)) ThrowType(key, "array of strings");
                ValidateExpression(key, (string)item);
                if (++count > MaxExpressionCount)
                    throw new DebugRequestSchemaException(
                        "INVALID_ARGUMENT", $"params.{key} must contain at most {MaxExpressionCount} expressions.");
            }
        }

        private static void ValidateExpression(string key, string expression)
        {
            if (string.IsNullOrWhiteSpace(expression))
                throw new DebugRequestSchemaException("INVALID_ARGUMENT", $"params.{key} contains an empty expression.");
            if (expression.Length > MaxExpressionLength)
                throw new DebugRequestSchemaException(
                    "INVALID_ARGUMENT", $"params.{key} contains an expression longer than {MaxExpressionLength} characters.");
        }

        private static void ValidateIntegerRange(string key, int value)
        {
            int minimum = int.MinValue;
            int maximum = int.MaxValue;
            switch (key)
            {
                case "depth": case "maxDepth": minimum = 0; maximum = 4; break;
                case "frameIndex": case "overloadIndex": minimum = 0; maximum = 4096; break;
                case "threadId": case "processId": case "lineNumber": minimum = 1; break;
                case "hitCount": minimum = 0; break;
                case "maxFramesPerThread": case "threadMaxFramesPerThread": minimum = 0; maximum = 256; break;
                case "maxFrames": case "maxFramesScanned": minimum = 0; maximum = 4096; break;
                case "maxNodes": case "threadMaxNodes": minimum = 0; maximum = 4096; break;
                case "budgetMs": case "threadBudgetMs": case "threadLookupBudgetMs": case "processLookupBudgetMs": case "breakpointLookupBudgetMs": minimum = 0; maximum = 120000; break;
                case "startIndex": case "startModuleIndex": minimum = 0; maximum = 1000000; break;
                case "startThreadIndex": minimum = 1; maximum = 1000000; break;
                case "startFrameIndex": minimum = 0; maximum = 1000000; break;
                case "outputTailLines": minimum = 0; maximum = 500; break;
                case "tailLines": minimum = 0; maximum = 10000; break;
                case "endCharacter": case "expectedCaptureStartCharacter": minimum = -1; maximum = int.MaxValue; break;
                case "waitTimeoutMs": minimum = 100; maximum = 300000; break;
                case "maxBytes": minimum = 4 * 1024; maximum = 3 * 1024 * 1024; break;
                case "size": minimum = 1; maximum = 4096; break;
                case "linesBefore": case "linesAfter": minimum = 0; maximum = 256; break;
                case "count": minimum = 1; maximum = 256; break;
                case "maxImportFunctions": case "maxExports": minimum = 1; maximum = 4096; break;
                case "maxTypes": minimum = 1; maximum = 128; break;
                case "maxMethods": case "maxMethodsPerType": minimum = 1; maximum = 32; break;
                case "maxInstructions": minimum = 1; maximum = 2048; break;
                case "maxResults": case "maxMatches": minimum = 1; maximum = 10000; break;
                case "disassemblyCount": minimum = 1; maximum = 256; break;
                case "stackBytes": minimum = 16; maximum = 4096; break;
                case "timeoutMs": minimum = 1000; maximum = 30 * 60 * 1000; break;
                case "pollIntervalMs": minimum = 50; maximum = 1000; break;
                case "byteCount": minimum = 1; maximum = 64; break;
                case "searchSize": minimum = 1; maximum = 1024 * 1024; break;
                case "contextLines": minimum = 0; maximum = 100; break;
                case "startLine": minimum = 0; maximum = 1000000; break;
            }
            if (value < minimum || value > maximum)
                throw new DebugRequestSchemaException(
                    "INVALID_ARGUMENT", $"params.{key} must be between {minimum} and {maximum}.");
        }

        private static void ValidateString(string key, string value)
        {
            int maximum = 64 * 1024;
            switch (key)
            {
                case "requestId": maximum = 64; break;
                case "processHandle": maximum = 64; break;
                case "moduleSnapshotId": case "scanContinuationToken": maximum = 128; break;
                case "pane": case "patternFormat": case "evaluationPolicy": case "hitCountType": case "conditionType": maximum = 256; break;
                case "address": case "startAddress": maximum = 1024; break;
                case "pattern": case "functionName": case "methodName": case "typeName": case "typeFilter": maximum = 4096; break;
                case "condition": case "dataExpression": maximum = 16 * 1024; break;
                case "filePath": case "assemblyPath": case "executablePath": case "workingDirectory": maximum = 32 * 1024; break;
            }
            if (value.Length > maximum)
                throw new DebugRequestSchemaException(
                    "INVALID_ARGUMENT", $"params.{key} exceeds the maximum length of {maximum} characters.");
            if ((key == "moduleSnapshotId" || key == "scanContinuationToken") &&
                string.IsNullOrWhiteSpace(value))
                throw new DebugRequestSchemaException(
                    "INVALID_ARGUMENT", $"params.{key} must not be blank.");
            if (key == "processHandle" &&
                !Regex.IsMatch(value, "^dph_[0-9a-f]{32}$", RegexOptions.CultureInvariant))
                throw new DebugRequestSchemaException(
                    "INVALID_ARGUMENT", "params.processHandle is not a valid opaque debug-process handle.");
            string normalized = value.Trim().ToLowerInvariant();
            if (key == "hitCountType" && normalized != "equal" && normalized != "greater_or_equal" && normalized != "multiple")
                throw new DebugRequestSchemaException("INVALID_ARGUMENT", "params.hitCountType is not supported.");
            if (key == "conditionType" && normalized != "when_true" && normalized != "when_changed")
                throw new DebugRequestSchemaException("INVALID_ARGUMENT", "params.conditionType is not supported.");
            if (key == "patternFormat" && normalized != "auto" && normalized != "hex" && normalized != "bytes" &&
                normalized != "text" && normalized != "ascii" && normalized != "string")
                throw new DebugRequestSchemaException("INVALID_ARGUMENT", "params.patternFormat is not supported.");
            if (key == "evaluationPolicy" && normalized != "engine_default")
                throw new DebugRequestSchemaException("INVALID_ARGUMENT", "params.evaluationPolicy must be engine_default.");
        }

        private static void ThrowType(string key, string expected)
        {
            throw new DebugRequestSchemaException("INVALID_ARGUMENT", $"params.{key} must be a JSON {expected}.");
        }
    }

    internal sealed class DebugRequestSchemaException : Exception
    {
        internal DebugRequestSchemaException(string code, string message) : base(message)
        {
            Code = code;
        }

        internal string Code { get; }
    }
}
