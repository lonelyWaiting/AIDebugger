using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

namespace VSDebuggerMCP.Protocol
{
    internal sealed class DebugMethodDescriptor
    {
        public string Name { get; set; }
        public string Kind { get; set; }
        public string Capability { get; set; }
        public IReadOnlyCollection<string> RequiredCapabilities { get; set; }
        public string RequiredState { get; set; }
        public int DefaultDeadlineMs { get; set; }
        public int MaxDeadlineMs { get; set; }
        public int MaxResultBytes { get; set; }
        public bool SessionRefresh { get; set; }
        public bool RequiresStableDebugSession { get; set; }
        public bool ControlPlane { get; set; }
        public bool ControlPipeEligible { get; set; }
        public bool OfflineInspection { get; set; }
        public bool RequiresStableDebugContext { get; set; }
        public bool MayEvaluateUserCode { get; set; }
        public bool LongRunningWait { get; set; }
        public IReadOnlyCollection<string> AllowedParameters { get; set; }
        public IReadOnlyCollection<string> RequiredParameters { get; set; }
    }

    internal static class DebugMethodCatalog
    {
        private const string ManifestResourceName = "VSDebuggerMCP.Protocol.DebugMethodManifest.json";
        public static string CatalogHash { get; } = ComputeCatalogHash();
        private static readonly IReadOnlyDictionary<string, DebugMethodManifestEntry> ManifestMethods = LoadManifest();
        private static readonly IReadOnlyCollection<string> NoParameters = new string[0];
        private static readonly Dictionary<string, ParameterSpec> ParameterSpecs = BuildParameterSpecs();
        private static readonly HashSet<string> Commands = new HashSet<string>(
            ManifestMethods.Where(pair => pair.Value.kind == "command").Select(pair => pair.Key),
            StringComparer.Ordinal);

        private static readonly HashSet<string> SessionRefresh = new HashSet<string>(
            ManifestMethods.Where(pair => pair.Value.sessionRefresh).Select(pair => pair.Key),
            StringComparer.Ordinal);

        private static readonly HashSet<string> PausedState = new HashSet<string>(StringComparer.Ordinal)
        {
            "add_watch", "capture_debug_bundle", "capture_debug_bundle_safe", "capture_hybrid_context", "capture_native_context",
            "continue",
            "evaluate_expression", "evaluate_expressions_batch", "get_callstack", "get_disassembly",
            "get_disassembly_at", "get_exception", "get_full_context", "get_locals", "get_registers",
            "get_dump_memory_ranges", "read_memory", "resolve_address", "search_memory", "set_address_breakpoint", "set_data_breakpoint",
            "run_to_cursor", "set_next_statement", "set_watchpoint", "step_into", "step_out", "step_over",
            "switch_process", "switch_stack_frame", "switch_thread",
        };

        private static readonly HashSet<string> RunningState = new HashSet<string>(StringComparer.Ordinal)
        {
            "break",
        };

        private static readonly HashSet<string> DebuggingState = new HashSet<string>(StringComparer.Ordinal)
        {
            "continue_and_capture_breakpoint", "continue_and_capture_native",
            "continue_and_wait_for_break", "continue_and_wait_for_break_safe", "detach", "get_debug_processes", "stop",
        };

        private static readonly HashSet<string> LongRunningWait = new HashSet<string>(StringComparer.Ordinal)
        {
            "continue_and_capture_breakpoint", "continue_and_capture_native",
            "continue_and_wait_for_break", "continue_and_wait_for_break_safe",
        };

        private static readonly HashSet<string> ControlPipeInterrupts = new HashSet<string>(StringComparer.Ordinal)
        {
            "get_debug_status", "break", "stop", "detach",
        };

        // State admission and context pinning are separate concerns. Wait/capture
        // commands accept both run and break modes but must still stay bound to the
        // exact run/break epoch that the caller selected.
        private static readonly HashSet<string> StableDebugContext = new HashSet<string>(PausedState, StringComparer.Ordinal)
        {
            "continue_and_capture_breakpoint", "continue_and_capture_native",
            "continue_and_wait_for_break", "continue_and_wait_for_break_safe",
        };

        private static readonly HashSet<string> KnownMethods = new HashSet<string>(ManifestMethods.Keys, StringComparer.Ordinal);

        public static DebugMethodDescriptor Get(string method)
        {
            string name = (method ?? string.Empty).ToLowerInvariant();
            if (!KnownMethods.Contains(name)) return null;
            string[] capabilities = GetCapabilities(name);
            string capability = capabilities[0];
            return new DebugMethodDescriptor
            {
                Name = name,
                Kind = Commands.Contains(name) ? "command" : "query",
                Capability = capability,
                RequiredCapabilities = capabilities,
                RequiredState = PausedState.Contains(name)
                    ? "paused"
                    : (RunningState.Contains(name) ? "running" : (DebuggingState.Contains(name) ? "debugging" : "any")),
                DefaultDeadlineMs = GetDefaultDeadline(name),
                MaxDeadlineMs = GetMaxDeadline(name),
                MaxResultBytes = name == "get_request_status"
                    ? 16 * 1024 * 1024
                    : (capability == "debug.memory.read" ? 16 * 1024 * 1024 : 4 * 1024 * 1024),
                SessionRefresh = SessionRefresh.Contains(name),
                ControlPlane = ManifestMethods[name].controlPlane,
                ControlPipeEligible = ManifestMethods[name].controlPlane || ControlPipeInterrupts.Contains(name),
                OfflineInspection = name.StartsWith("inspect_managed_", StringComparison.Ordinal),
                RequiresStableDebugSession = !SessionRefresh.Contains(name) &&
                    !name.StartsWith("inspect_managed_", StringComparison.Ordinal),
                RequiresStableDebugContext = StableDebugContext.Contains(name),
                MayEvaluateUserCode = capabilities.Contains("debug.evaluate.unsafe", StringComparer.Ordinal),
                LongRunningWait = LongRunningWait.Contains(name),
                AllowedParameters = ParameterSpecs.TryGetValue(name, out ParameterSpec parameterSpec)
                    ? parameterSpec.Allowed
                    : NoParameters,
                RequiredParameters = parameterSpec?.Required ?? NoParameters,
            };
        }

        public static IReadOnlyCollection<string> AllCapabilities => KnownMethods
            .SelectMany(GetCapabilities)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        private static string[] GetCapabilities(string name) => ManifestMethods[name].capabilities.ToArray();

        public static IReadOnlyCollection<string> GetRequiredCapabilities(
            DebugMethodDescriptor descriptor,
            IDictionary<string, object> parameters)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            var required = new List<string>(descriptor.RequiredCapabilities);
            DebugMethodManifestEntry entry = ManifestMethods[descriptor.Name];
            foreach (ConditionalCapability conditional in entry.conditionalCapabilities ?? new ConditionalCapability[0])
            {
                bool matches = false;
                if (parameters != null &&
                    parameters.TryGetValue(conditional.parameter, out object value))
                {
                    matches = conditional.presentNonBlank
                        ? value is string text && !string.IsNullOrWhiteSpace(text)
                        : object.Equals(value, conditional.equals);
                }
                if (matches && !required.Contains(conditional.capability))
                {
                    required.Add(conditional.capability);
                }
            }
            return required;
        }

        private static IReadOnlyDictionary<string, DebugMethodManifestEntry> LoadManifest()
        {
            Assembly assembly = typeof(DebugMethodCatalog).Assembly;
            using (Stream stream = assembly.GetManifestResourceStream(ManifestResourceName))
            {
                if (stream == null)
                    throw new InvalidOperationException("Embedded debugger method manifest is missing: " + ManifestResourceName);
                using (var reader = new StreamReader(stream))
                {
                    var manifest = new JavaScriptSerializer().Deserialize<DebugMethodManifest>(reader.ReadToEnd());
                    if (manifest == null || manifest.protocol != "aidb/2" || manifest.methods == null || manifest.methods.Count == 0)
                        throw new InvalidOperationException("Debugger method manifest is invalid or uses an unsupported protocol.");

                    foreach (KeyValuePair<string, DebugMethodManifestEntry> pair in manifest.methods)
                    {
                        DebugMethodManifestEntry entry = pair.Value;
                        if (string.IsNullOrWhiteSpace(pair.Key) ||
                            entry == null ||
                            (entry.kind != "query" && entry.kind != "command") ||
                            entry.capabilities == null ||
                            entry.capabilities.Length == 0 ||
                            entry.capabilities.Any(string.IsNullOrWhiteSpace) ||
                            entry.capabilities.Distinct(StringComparer.Ordinal).Count() != entry.capabilities.Length)
                        {
                            throw new InvalidOperationException("Debugger method manifest contains an invalid entry: " + pair.Key);
                        }

                        foreach (ConditionalCapability conditional in entry.conditionalCapabilities ?? new ConditionalCapability[0])
                        {
                            if (string.IsNullOrWhiteSpace(conditional?.parameter) ||
                            string.IsNullOrWhiteSpace(conditional.capability) ||
                            (conditional.equals == null && !conditional.presentNonBlank) ||
                            (conditional.equals != null && conditional.presentNonBlank))
                            {
                                throw new InvalidOperationException("Debugger method manifest contains an invalid conditional capability: " + pair.Key);
                            }
                        }
                    }

                    return new Dictionary<string, DebugMethodManifestEntry>(manifest.methods, StringComparer.Ordinal);
                }
            }
        }

        private static string ComputeCatalogHash()
        {
            Assembly assembly = typeof(DebugMethodCatalog).Assembly;
            using (Stream stream = assembly.GetManifestResourceStream(ManifestResourceName))
            {
                if (stream == null)
                    throw new InvalidOperationException("Embedded debugger method manifest is missing: " + ManifestResourceName);
                using (var sha256 = SHA256.Create())
                {
                    return BitConverter.ToString(sha256.ComputeHash(stream))
                        .Replace("-", string.Empty)
                        .ToLowerInvariant();
                }
            }
        }

        private static Dictionary<string, ParameterSpec> BuildParameterSpecs()
        {
            var specs = new Dictionary<string, ParameterSpec>(StringComparer.Ordinal);
            Add(specs, "get_request_status cancel_request", "requestId", "");
            Add(specs, "get_callstack", "", "startIndex maxFrames budgetMs");
            Add(specs, "get_locals", "debugSessionGeneration breakSequence contextEpoch processId threadId frameIndex frameId", "depth threadLookupBudgetMs");
            Add(specs, "get_threads", "", "includeFrames maxFramesPerThread maxNodes budgetMs startThreadIndex startFrameIndex");
            Add(specs, "get_modules", "", "maxNodes maxFramesScanned budgetMs startModuleIndex moduleSnapshotId scanContinuationToken");
            Add(specs, "get_breakpoints get_breakpoint_bindings get_build_errors get_errors_only get_warnings_only", "", "startIndex maxNodes budgetMs");
            Add(specs, "evaluate_expression", "expression", "expandChildren maxDepth evaluationPolicy");
            Add(specs, "evaluate_expressions_batch", "expressions", "expandChildren maxDepth evaluationPolicy");
            Add(specs, "capture_debug_bundle_safe", "", "includeModules includeOutput includeThreads outputTailLines threadIncludeFrames threadMaxFramesPerThread threadMaxNodes threadBudgetMs");
            Add(specs, "capture_debug_bundle", "", "expressions expandChildren maxDepth evaluationPolicy includeLocals includeModules includeRegisters includeOutput includeThreads includeException outputTailLines threadIncludeFrames threadMaxFramesPerThread threadMaxNodes threadBudgetMs");
            Add(specs, "read_memory", "address", "size");
            Add(specs, "get_dump_memory_ranges", "", "startIndex maxNodes budgetMs");
            Add(specs, "get_disassembly", "", "linesBefore linesAfter");
            Add(specs, "get_disassembly_at", "address", "count");
            Add(specs, "resolve_address", "address", "");
            Add(specs, "get_module_layout", "module", "maxImportFunctions maxExports");
            Add(specs, "capture_native_context", "", "disassemblyCount stackBytes includeModules");
            Add(specs, "continue_and_capture_native", "", "timeoutMs pollIntervalMs disassemblyCount stackBytes includeModules");
            Add(specs, "inspect_managed_assembly", "assemblyPath", "typeFilter maxTypes maxMethodsPerType");
            Add(specs, "inspect_managed_type", "assemblyPath typeName", "maxMethods");
            Add(specs, "inspect_managed_method_il", "assemblyPath typeName methodName", "overloadIndex maxInstructions");
            Add(specs, "capture_hybrid_context", "", "disassemblyCount stackBytes includeNative includeModules includeThreads includeBreakpoints threadIncludeFrames threadMaxFramesPerThread threadMaxNodes threadBudgetMs");
            Add(specs, "continue_and_capture_breakpoint", "", "timeoutMs pollIntervalMs disassemblyCount stackBytes includeNative includeModules includeThreads includeBreakpoints threadIncludeFrames threadMaxFramesPerThread threadMaxNodes threadBudgetMs");
            Add(specs, "get_full_context", "", "threadIncludeFrames threadMaxFramesPerThread threadMaxNodes threadBudgetMs");
            Add(specs, "attach_process", "", "processId processName");
            Add(specs, "get_debug_processes", "", "startIndex maxNodes budgetMs");
            Add(specs, "switch_process", "", "processHandle processId processLookupBudgetMs");
            Add(specs, "start_debugging", "", "executablePath workingDirectory arguments attach");
            Add(specs, "step_into step_over step_out", "", "waitTimeoutMs");
            Add(specs, "continue_and_wait_for_break", "", "expressions expandChildren maxDepth evaluationPolicy outputTailLines timeoutMs pollIntervalMs includeLocals includeModules includeRegisters includeOutput includeThreads includeException threadIncludeFrames threadMaxFramesPerThread threadMaxNodes threadBudgetMs");
            Add(specs, "continue_and_wait_for_break_safe", "", "outputTailLines timeoutMs pollIntervalMs includeModules includeOutput includeThreads threadIncludeFrames threadMaxFramesPerThread threadMaxNodes threadBudgetMs");
            Add(specs, "run_to_cursor set_next_statement", "filePath lineNumber", "");
            Add(specs, "set_breakpoint", "filePath lineNumber", "condition hitCount hitCountType conditionType");
            Add(specs, "set_function_breakpoint", "functionName", "condition hitCount hitCountType conditionType");
            Add(specs, "set_data_breakpoint set_watchpoint", "dataExpression", "byteCount condition hitCount hitCountType conditionType");
            Add(specs, "set_address_breakpoint", "address", "condition hitCount hitCountType conditionType");
            Add(specs, "delete_breakpoint clear_breakpoint", "filePath lineNumber", "breakpointLookupBudgetMs");
            Add(specs, "delete_data_breakpoint", "dataBreakpointId", "breakpointLookupBudgetMs");
            Add(specs, "enable_breakpoint", "filePath lineNumber", "enabled breakpointLookupBudgetMs");
            Add(specs, "add_watch", "expression", "");
            Add(specs, "search_memory", "pattern", "startAddress maxResults searchSize patternFormat startOffset continuation");
            Add(specs, "switch_stack_frame", "", "frameIndex");
            Add(specs, "switch_thread", "threadId", "threadLookupBudgetMs");
            Add(specs, "get_output_window", "", "pane tailLines endCharacter maxBytes");
            Add(specs, "search_output", "pattern", "pane isRegex contextLines maxMatches startLine endCharacter expectedCaptureStartCharacter");
            return specs;
        }

        private static void Add(
            IDictionary<string, ParameterSpec> specs,
            string methods,
            string required,
            string optional)
        {
            string[] requiredParameters = Split(required);
            string[] allowed = requiredParameters.Concat(Split(optional)).Distinct(StringComparer.Ordinal).ToArray();
            foreach (string method in Split(methods))
            {
                specs.Add(method, new ParameterSpec { Allowed = allowed, Required = requiredParameters });
            }
        }

        private static string[] Split(string values) => (values ?? string.Empty)
            .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        private sealed class ParameterSpec
        {
            internal IReadOnlyCollection<string> Allowed { get; set; }
            internal IReadOnlyCollection<string> Required { get; set; }
        }

        private sealed class DebugMethodManifest
        {
            public string protocol { get; set; }
            public Dictionary<string, DebugMethodManifestEntry> methods { get; set; }
        }

        private sealed class DebugMethodManifestEntry
        {
            public string kind { get; set; }
            public string[] capabilities { get; set; }
            public ConditionalCapability[] conditionalCapabilities { get; set; }
            public bool controlPlane { get; set; }
            public bool sessionRefresh { get; set; }
        }

        private sealed class ConditionalCapability
        {
            public string parameter { get; set; }
            public object equals { get; set; }
            public bool presentNonBlank { get; set; }
            public string capability { get; set; }
        }

        private static int GetDefaultDeadline(string name)
        {
            if (name == "ping" || name == "get_instance_info" || name == "get_request_status" || name == "cancel_request") return 2000;
            if (name.StartsWith("continue_and_") || name == "start_debugging") return 120000;
            if (name.StartsWith("inspect_managed_")) return 60000;
            if (name == "capture_debug_bundle" || name == "capture_debug_bundle_safe" || name == "capture_hybrid_context") return 120000;
            return 10000;
        }

        private static int GetMaxDeadline(string name)
        {
            if (name == "continue_and_wait_for_break" ||
                name == "continue_and_wait_for_break_safe" ||
                name == "continue_and_capture_native" ||
                name == "continue_and_capture_breakpoint")
                return 35 * 60 * 1000;
            return 30 * 60 * 1000;
        }
    }
}
