import json
import pathlib
import subprocess
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[1]


def find_msbuild() -> pathlib.Path | None:
    candidates = [
        pathlib.Path(r"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"),
        pathlib.Path(r"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"),
        pathlib.Path(r"C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe"),
        pathlib.Path(r"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"),
    ]
    return next((path for path in candidates if path.exists()), None)


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def function_source(source: str, name: str) -> str:
    async_marker = f"async def {name}"
    sync_marker = f"def {name}"
    start = source.find(async_marker)
    if start == -1:
        start = source.index(sync_marker)
    end = source.find("\n@mcp.tool", start + 1)
    if end == -1:
        end = len(source)
    return source[start:end]


def csharp_slice(source: str, start_marker: str, end_marker: str) -> str:
    start = source.index(start_marker)
    end = source.index(end_marker, start + len(start_marker))
    return source[start:end]


class ContractTests(unittest.TestCase):
    def test_memory_contract_fields_present(self) -> None:
        vs_models = read("VSDebuggerMCP/Protocol/DebugModels.cs")
        rider_models = read("RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/protocol/DebugModels.kt")
        vs_memory = csharp_slice(vs_models, "public class MemorySearchResult", "    #endregion")
        rider_memory = rider_models[
            rider_models.index("data class MemorySearchResult("):
            rider_models.index("data class OutputWindowResult(", rider_models.index("data class MemorySearchResult("))
        ]

        required_fields = [
            "status",
            "failedReads",
            "partial",
            "requestedBytes",
            "truncated",
            "nextStartAddress",
        ]
        for field in required_fields:
            self.assertIn(field, vs_memory)
            self.assertIn(field, rider_memory)

        for general_memory_field in ("requestedSize", "bytesRead"):
            self.assertIn(general_memory_field, vs_models)
            self.assertIn(general_memory_field, rider_models)

    def test_memory_search_continuation_fields_do_not_leak_to_threads(self) -> None:
        vs_models = read("VSDebuggerMCP/Protocol/DebugModels.cs")
        vs_threads = csharp_slice(vs_models, "public class ThreadsResult", "    [DataContract]\n    public class ThreadInfo")

        self.assertNotIn('Name = "truncated"', vs_threads)
        self.assertNotIn("NextStartAddress", vs_threads)

    def test_snapshot_contract_fields_present(self) -> None:
        vs_models = read("VSDebuggerMCP/Protocol/DebugModels.cs")
        rider_models = read("RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/protocol/DebugModels.kt")

        for field in ("snapshotStable", "snapshotAttempts", "snapshotStatus"):
            self.assertIn(field, vs_models)
            self.assertIn(field, rider_models)

    def test_capability_and_behavior_fields_present(self) -> None:
        vs_models = read("VSDebuggerMCP/Protocol/DebugModels.cs")
        rider_models = read("RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/protocol/DebugModels.kt")

        for field in ("capability", "availableCategories", "behavior"):
            self.assertIn(field, vs_models)
            self.assertIn(field, rider_models)

    def test_p0_bundle_and_stop_reason_contract_fields_present(self) -> None:
        vs_models = read("VSDebuggerMCP/Protocol/DebugModels.cs")
        rider_models = read("RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/protocol/DebugModels.kt")

        for field in (
            "stopReason",
            "stopReasonRaw",
            "BatchEvaluateResult",
            "DebugBundleResult",
            "snapshotId",
            "capturedAt",
            "includedComponents",
            "omittedComponents",
        ):
            self.assertIn(field, vs_models)
            self.assertIn(field, rider_models)

    def test_continue_and_wait_contract_fields_present(self) -> None:
        vs_models = read("VSDebuggerMCP/Protocol/DebugModels.cs")
        rider_models = read("RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/protocol/DebugModels.kt")

        for field in (
            "ContinueAndWaitResult",
            "waitTimeoutMs",
            "pollIntervalMs",
            "waitedMs",
            "issuedContinue",
            "timedOut",
            "breakDetected",
            "bundle",
        ):
            self.assertIn(field, vs_models)
            self.assertIn(field, rider_models)

    def test_debug_response_knows_continue_and_wait_result(self) -> None:
        vs_response = read("VSDebuggerMCP/Protocol/DebugResponse.cs")
        self.assertIn("KnownType(typeof(ContinueAndWaitResult))", vs_response)

    def test_named_pipe_bridge_uses_io_timeout(self) -> None:
        vs_bridge = read("mcp_server/vs_bridge.py")
        rider_bridge = read("mcp_server/rider_bridge.py")

        for bridge_code in (vs_bridge, rider_bridge):
            self.assertIn("remaining_timeout_ms", bridge_code)
            self.assertIn("WaitNamedPipeW", bridge_code)
            self.assertIn("PeekNamedPipe", bridge_code)
            self.assertIn("_compute_timeouts", bridge_code)
            self.assertIn("ThreadPoolExecutor", bridge_code)
            self.assertIn("run_in_executor(self._executor", bridge_code)
            self.assertIn("FILE_FLAG_OVERLAPPED", bridge_code)
            self.assertIn("CancelIoEx", bridge_code)
            self.assertIn("GetOverlappedResult", bridge_code)

    def test_rider_registration_guards_present(self) -> None:
        rider_registration = read("RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/McpClientRegistration.kt")
        self.assertIn("isPythonAvailable()", rider_registration)
        self.assertIn("computeBundleSignature", rider_registration)
        self.assertIn(".bundle-signature", rider_registration)
        self.assertIn("capture_debug_bundle", rider_registration)
        self.assertIn("evaluate_expressions_batch", rider_registration)
        self.assertIn("cursor_allowlist_sync.py", rider_registration)

    def test_vs_registration_instructions_include_snapshot_workflow(self) -> None:
        vs_registration = read("VSDebuggerMCP/McpClientRegistration.cs")
        self.assertIn("capture_debug_bundle", vs_registration)
        self.assertIn("evaluate_expressions_batch", vs_registration)
        self.assertIn("stopReason", vs_registration)
        self.assertIn("cursor_allowlist_sync.py", vs_registration)
        self.assertIn("continue_and_wait_for_break", vs_registration)

    def test_vs_registration_refreshes_seeded_bundle_and_pins_python_versions(self) -> None:
        vs_registration = read("VSDebuggerMCP/McpClientRegistration.cs")
        self.assertIn(".bundle-signature", vs_registration)
        self.assertIn("TryEnsureBundledServerFiles", vs_registration)
        self.assertIn("ComputeDirectorySignature", vs_registration)
        self.assertIn('manifest.json', vs_registration)
        self.assertIn('PrefixArgs = new[] { $"-3.{minor}" }', vs_registration)

    def test_named_pipe_strict_mode_toggle_present(self) -> None:
        vs_pipe = read("VSDebuggerMCP/NamedPipeServer.cs")
        rider_pipe = read("RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/NamedPipeServer.kt")
        self.assertIn("AIDB_PIPE_SECURITY", vs_pipe)
        self.assertIn("AIDB_PIPE_SECURITY", rider_pipe)
        self.assertIn("WaitForConnectionAsync", vs_pipe)
        self.assertIn("cancellationToken.Register", vs_pipe)

    def test_server_exposes_p0_tools(self) -> None:
        server_py = read("mcp_server/server.py")
        for tool_name in (
            "capture_debug_bundle",
            "crash_triage",
            "step_and_capture",
            "evaluate_expressions_batch",
            "continue_and_wait_for_break",
        ):
            self.assertIn(f"@mcp.tool()\nasync def {tool_name}", server_py)

    def test_server_exposes_active_debugging_tools_and_aliases(self) -> None:
        server_py = read("mcp_server/server.py")

        for tool_name in (
            "debug_capabilities",
            "attach_process",
            "start_debugging",
            "debug_control",
            'name="continue"',
            "pause",
            'name="step_over"',
            'name="step_into"',
            'name="step_out"',
            "stop_debugging",
            "detach_process",
            "detach_debugger",
            "get_call_stack",
            "list_threads",
            "select_stack_frame",
            "select_thread",
            "clear_breakpoint",
            "set_data_breakpoint",
            'name="set_watchpoint"',
            "hit_count",
            "condition_type",
        ):
            self.assertIn(tool_name, server_py)

        self.assertNotIn("async def set_logpoint", server_py)
        data_breakpoint_tool = function_source(server_py, "set_data_breakpoint")
        self.assertIn('preferred_ide="vs"', data_breakpoint_tool)
        watchpoint_alias = function_source(server_py, "set_watchpoint_alias")
        self.assertIn("return await set_data_breakpoint(", watchpoint_alias)
        self.assertNotIn("ide_bridge.call", watchpoint_alias)

    def test_stop_and_detach_discovery_terms_are_indexed(self) -> None:
        server_py = read("mcp_server/server.py")
        vs_registration = read("VSDebuggerMCP/McpClientRegistration.cs")
        rider_registration = read("RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/McpClientRegistration.kt")

        for keyword in (
            "Use this before concluding a debugger capability is missing",
            "stop debugging",
            "stop debug",
            "terminate debug session",
            "stop current Visual Studio debugging session",
            "debug_control(action='stop')",
            "detach_process",
            "detach_debugger",
            "Rider detach returns an explicit unsupported result",
        ):
            self.assertIn(keyword, server_py)

        for registration in (vs_registration, rider_registration):
            self.assertIn("stop debugging", registration)
            self.assertIn("stop_debugging", registration)
            self.assertIn('debug_control(action=', registration)

    def test_vs_active_debugging_protocol_is_routed(self) -> None:
        vs_pipe = read("VSDebuggerMCP/NamedPipeServer.cs")
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")
        vs_models = read("VSDebuggerMCP/Protocol/DebugModels.cs")
        vs_response = read("VSDebuggerMCP/Protocol/DebugResponse.cs")

        for method_name in (
            '"attach_process"',
            '"start_debugging"',
            '"clear_breakpoint"',
            '"set_data_breakpoint"',
            '"set_watchpoint"',
            '"hitCount"',
            '"conditionType"',
            '"detach"',
        ):
            self.assertIn(method_name, vs_pipe)

        self.assertNotIn('"set_logpoint"', vs_pipe)
        for symbol in (
            "AttachProcess",
            "StartDebugging",
            "Detach",
            "process.Detach(false)",
            "SetDataBreakpoint",
            "ParseHitCountType",
            "ProcessNameMatches",
        ):
            self.assertIn(symbol, vs_service)

        self.assertNotIn("SetLogpoint", vs_service)
        self.assertIn("ProcessDebugResult", vs_models)
        self.assertIn("KnownType(typeof(ProcessDebugResult))", vs_response)

    def test_no_source_native_tools_are_exposed_without_patch_surface(self) -> None:
        server_py = read("mcp_server/server.py")

        for tool_name in (
            "resolve_address",
            "get_module_layout",
            "capture_native_context",
            "continue_and_capture_native",
            "set_address_breakpoint",
        ):
            self.assertIn(f"async def {tool_name}", server_py)

        debug_capabilities = function_source(server_py, "debug_capabilities")
        self.assertIn("noSourceNative", debug_capabilities)
        self.assertIn("module+RVA", debug_capabilities)
        self.assertIn("No write-memory or patching tool is exposed", debug_capabilities)
        self.assertIn("Visual Studio only", debug_capabilities)
        self.assertIn("Rider support is explicitly missing", debug_capabilities)
        self.assertIn("NATIVE_CONTEXT_TIMEOUT_SECONDS = 45.0", server_py)
        self.assertIn("timeout=float(timeout_seconds + NATIVE_CONTEXT_TIMEOUT_SECONDS)", function_source(server_py, "continue_and_capture_native"))
        for tool_name in (
            "resolve_address",
            "get_module_layout",
            "capture_native_context",
            "continue_and_capture_native",
            "set_address_breakpoint",
        ):
            self.assertIn('preferred_ide="vs"', function_source(server_py, tool_name))
        self.assertNotIn("async def write_memory", server_py)
        self.assertNotIn("WriteProcessMemory", read("VSDebuggerMCP/DebuggerService.cs"))

    def test_vs_no_source_native_protocol_is_routed_and_serializable(self) -> None:
        vs_pipe = read("VSDebuggerMCP/NamedPipeServer.cs")
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")
        vs_models = read("VSDebuggerMCP/Protocol/DebugModels.cs")
        vs_response = read("VSDebuggerMCP/Protocol/DebugResponse.cs")

        for method_name in (
            '"resolve_address"',
            '"get_module_layout"',
            '"capture_native_context"',
            '"continue_and_capture_native"',
            '"set_address_breakpoint"',
            '"maxImportFunctions"',
            '"maxExports"',
            '"disassemblyCount"',
            '"stackBytes"',
        ):
            self.assertIn(method_name, vs_pipe)

        for symbol in (
            "ResolveAddress",
            "GetModuleLayout",
            "CaptureNativeContext",
            "ContinueAndCaptureNative",
            "SetAddressBreakpoint",
            "NativeAddressInfo",
            "ModuleLayoutResult",
            "NativeContextResult",
            "NativeContinueResult",
        ):
            self.assertIn(symbol, vs_service + vs_models)

        for known_type in (
            "KnownType(typeof(NativeAddressInfo))",
            "KnownType(typeof(ModuleLayoutResult))",
            "KnownType(typeof(ModuleSectionInfo))",
            "KnownType(typeof(ImportedModuleInfo))",
            "KnownType(typeof(ImportFunctionInfo))",
            "KnownType(typeof(ExportInfo))",
            "KnownType(typeof(NativeContextResult))",
            "KnownType(typeof(NativeContinueResult))",
        ):
            self.assertIn(known_type, vs_response)

        for field in (
            "virtualAddress",
            "addressValue",
            "moduleName",
            "moduleBase",
            "moduleSizeBytes",
            "rva",
            "sectionName",
            "entryPointRva",
            "imports",
            "exports",
            "stackMemory",
        ):
            self.assertIn(field, vs_models)

    def test_vs_native_address_resolution_handles_hard_address_cases(self) -> None:
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")
        vs_models = read("VSDebuggerMCP/Protocol/DebugModels.cs")
        vs_response = read("VSDebuggerMCP/Protocol/DebugResponse.cs")

        self.assertIn("TryParseModuleRvaExpression", vs_service)
        self.assertIn("expression.LastIndexOf('+')", vs_service)
        self.assertIn("module+RVA expression overflows address space", vs_service)
        self.assertIn("Expression-based address resolution requires break mode", vs_service)
        self.assertIn("FindContainingModule", vs_service)
        self.assertIn("m.Size != 0 && address >= m.Base && address - m.Base < m.Size", vs_service)
        self.assertIn("OrderByDescending(m => m.Base)", vs_service)
        self.assertIn("ModuleInfo explicitModule = null", vs_service)
        self.assertIn("module+RVA is outside the module image size", vs_service)
        self.assertNotIn(".Where(m => m.Base != 0 && address >= m.Base)", vs_service)
        self.assertIn("FindSectionForRva", vs_service)
        self.assertIn("NearestSymbol", vs_service)
        self.assertIn("SizeBytes = GetModuleSizeFromBase", vs_service)
        self.assertIn("existing.SizeBytes == 0 && moduleInfo.SizeBytes != 0", vs_service)
        self.assertIn("FormatRva(result.RvaValue - sectionRva)", vs_service)
        self.assertIn("FunctionStatus", vs_models)
        self.assertIn("FunctionError", vs_models)
        self.assertIn('Name = "functionStatus"', vs_models)
        self.assertIn('Name = "functionError"', vs_models)
        self.assertIn("KnownType(typeof(NativeFunctionInfo))", vs_response)
        self.assertNotIn("NativeControlFlowInfo", vs_models + vs_response)
        self.assertNotIn("controlFlow", vs_models)
        self.assertNotIn("prologueFound", vs_models)
        self.assertNotIn("terminatorFound", vs_models)
        self.assertIn("BuildExactExportFunctionAttribution", vs_service)
        self.assertIn('"No exact native function symbol is available for this address"', vs_service)
        self.assertIn('"Only nearestSymbol+offset is available; exact native function attribution is missing"', vs_service)
        self.assertNotIn("FindFunctionStartIndex", vs_service)
        self.assertNotIn("FindFunctionEndIndex", vs_service)
        self.assertNotIn("Math.Max(0, currentIndex - 16)", vs_service)
        self.assertNotIn("function boundary is heuristic", vs_service)
        self.assertNotIn("prologue", vs_service.lower())
        self.assertNotIn("heuristic", vs_service.lower())

    def test_vs_module_layout_parser_covers_pe_boundaries_and_truncation(self) -> None:
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")

        for marker in (
            "Missing MZ header",
            "Missing PE signature",
            "Unsupported PE optional header magic",
            "safeSectionCount = Math.Min(sectionCount, (ushort)96)",
            "ReadExports",
            "ReadImports",
            "RvaToFileOffset",
            "ReadNullTerminatedAscii",
            "maxImportFunctions",
            "maxExports",
            "result.Truncated = true",
            "ordinalFlag",
            "thunkMask",
            "0x8000000000000000UL",
            "0x80000000UL",
            "FileShare.ReadWrite | System.IO.FileShare.Delete",
        ):
            self.assertIn(marker, vs_service)

    def test_vs_native_context_is_snapshot_oriented_and_read_only(self) -> None:
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")
        server_py = read("mcp_server/server.py")

        native_context = csharp_slice(vs_service, "private NativeContextResult BuildNativeContextResult", "        private NativeAddressInfo ResolveAddressInternal")
        self.assertIn('string ipRegister = is64Bit ? "@rip" : "@eip"', native_context)
        self.assertIn('string spRegister = is64Bit ? "@rsp" : "@esp"', native_context)
        self.assertIn("ResolveAddressInternal(debugger, ipRegister)", native_context)
        self.assertIn("BuildRegistersResult(debugger)", native_context)
        self.assertIn("BuildCallStackResult(debugger)", native_context)
        self.assertIn("ExecuteDisassemblyAt(debugger, ipExpression, disassemblyCount", native_context)
        self.assertIn("ReadMemoryInternal(debugger, spExpression, stackBytes)", native_context)
        self.assertIn("BuildModulesResult(debugger)", native_context)

        continue_native = csharp_slice(vs_service, "public NativeContinueResult ContinueAndCaptureNative", "        private NativeContextResult BuildNativeContextResult")
        self.assertIn("Math.Max(50, Math.Min(pollIntervalMs, 1000))", continue_native)
        self.assertIn("System.Threading.Thread.Sleep(normalizedPollIntervalMs)", continue_native)
        self.assertIn("CaptureNativeContext(disassemblyCount, stackBytes, includeModules)", continue_native)
        self.assertIn("Timed out waiting for debugger to enter break mode", continue_native)

        self.assertIn('"includeModules": include_modules', function_source(server_py, "capture_native_context"))
        self.assertIn('"includeModules": include_modules', function_source(server_py, "continue_and_capture_native"))

    def test_no_source_managed_and_hybrid_tools_are_exposed(self) -> None:
        server_py = read("mcp_server/server.py")
        vs_registration = read("VSDebuggerMCP/McpClientRegistration.cs")

        for tool_name in (
            "inspect_managed_assembly",
            "inspect_managed_type",
            "inspect_managed_method_il",
            "capture_hybrid_context",
            "get_breakpoint_bindings",
            "continue_and_capture_breakpoint",
        ):
            self.assertIn(f"async def {tool_name}", server_py)

        debug_capabilities = function_source(server_py, "debug_capabilities")
        self.assertIn("noSourceManaged", debug_capabilities)
        self.assertIn("noSourceHybrid", debug_capabilities)
        self.assertIn("without source or UI automation", debug_capabilities)
        self.assertIn("Breakpoint-oriented dynamic loop", debug_capabilities)
        self.assertIn("MANAGED_INSPECTION_TIMEOUT_SECONDS = 75.0", server_py)
        self.assertIn("HYBRID_CONTEXT_TIMEOUT_SECONDS = 135.0", server_py)
        self.assertIn("tool_timeout_for_budget(timeout_seconds, HYBRID_CONTEXT_TIMEOUT_SECONDS, thread_budget_ms)", function_source(server_py, "capture_hybrid_context"))
        for tool_name in (
            "inspect_managed_assembly",
            "inspect_managed_type",
            "inspect_managed_method_il",
            "capture_hybrid_context",
            "get_breakpoint_bindings",
            "continue_and_capture_breakpoint",
        ):
            self.assertIn('preferred_ide="vs"', function_source(server_py, tool_name))
        self.assertIn("continue_and_capture_breakpoint", vs_registration)
        self.assertIn("inspect_managed_method_il", vs_registration)
        self.assertIn("No-source hybrid dynamic loop", vs_registration)

    def test_vs_no_source_managed_and_hybrid_protocol_is_routed_and_serializable(self) -> None:
        vs_pipe = read("VSDebuggerMCP/NamedPipeServer.cs")
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")
        vs_models = read("VSDebuggerMCP/Protocol/DebugModels.cs")
        vs_response = read("VSDebuggerMCP/Protocol/DebugResponse.cs")

        for method_name in (
            '"inspect_managed_assembly"',
            '"inspect_managed_type"',
            '"inspect_managed_method_il"',
            '"capture_hybrid_context"',
            '"get_breakpoint_bindings"',
            '"continue_and_capture_breakpoint"',
            '"assemblyPath"',
            '"typeName"',
            '"methodName"',
            '"overloadIndex"',
            '"maxInstructions"',
            '"includeNative"',
            '"includeBreakpoints"',
        ):
            self.assertIn(method_name, vs_pipe)

        for symbol in (
            "InspectManagedAssembly",
            "InspectManagedType",
            "InspectManagedMethodIl",
            "CaptureHybridContext",
            "GetBreakpointBindings",
            "ContinueAndCaptureBreakpoint",
            "ManagedAssemblyResult",
            "ManagedMethodIlResult",
            "HybridContextResult",
            "BreakpointCaptureResult",
        ):
            self.assertIn(symbol, vs_service + vs_models)

        for known_type in (
            "KnownType(typeof(ManagedAssemblyResult))",
            "KnownType(typeof(ManagedTypeResult))",
            "KnownType(typeof(ManagedTypeInfo))",
            "KnownType(typeof(ManagedMethodInfo))",
            "KnownType(typeof(ManagedParameterInfo))",
            "KnownType(typeof(ManagedLocalInfo))",
            "KnownType(typeof(ManagedMethodIlResult))",
            "KnownType(typeof(ManagedIlInstructionInfo))",
            "KnownType(typeof(HybridContextResult))",
            "KnownType(typeof(BreakpointCaptureResult))",
            "KnownType(typeof(BreakpointHitInfo))",
        ):
            self.assertIn(known_type, vs_response)

        for field in (
            "assemblyFullName",
            "moduleVersionId",
            "metadataTokenHex",
            "operandType",
            "resolvedMember",
            "branchTarget",
            "switchTargets",
            "nativeContext",
            "selectedStackFrameIndex",
            "continueResult",
            "hitAttributionStatus",
            "hitAttributionError",
            "hitVerificationStatus",
            "hitVerificationMessage",
            "hitBreakpoint",
            "trackedAddress",
            "trackedAddresses",
            "bindingVerificationStatus",
            "bindingWarning",
            "isRelocated",
            "nearbyProbeAttempted",
            "nearbyProbeSucceeded",
            "optimizedCodeSuspected",
            "verificationStatus",
            "verificationMessage",
        ):
            self.assertIn(field, vs_models)
        self.assertNotIn("likelyHitBreakpoint", vs_models)
        self.assertNotIn("boundAddresses", vs_models)

    def test_managed_inspection_helper_is_packaged_as_external_process(self) -> None:
        vs_project = read("VSDebuggerMCP/VSDebuggerMCP.csproj")
        helper_project = read("VSDebuggerMCP/ManagedInspectionHelper/ManagedInspectionHelper.csproj")
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")

        self.assertIn("<OutputType>Exe</OutputType>", helper_project)
        self.assertIn("<TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>", helper_project)
        self.assertIn('<Compile Include="..\\Protocol\\DebugModels.cs">', helper_project)
        self.assertIn("<Link>Protocol\\DebugModels.cs</Link>", helper_project)
        self.assertIn('<Reference Include="System.Web.Extensions" />', helper_project)
        self.assertIn("BuildManagedInspectionHelper", vs_project)
        self.assertIn("ManagedInspectionHelper\\ManagedInspectionHelper.csproj", vs_project)
        self.assertIn("ManagedInspectionHelper\\bin\\$(Configuration)\\ManagedInspectionHelper.exe", vs_project)
        self.assertIn("<IncludeInVSIX>true</IncludeInVSIX>", vs_project)

        managed_entrypoints = csharp_slice(vs_service, "public ManagedAssemblyResult InspectManagedAssembly", "        public HybridContextResult CaptureHybridContext")
        self.assertIn("RunManagedInspectionHelper<ManagedAssemblyResult>", managed_entrypoints)
        self.assertIn("RunManagedInspectionHelper<ManagedTypeResult>", managed_entrypoints)
        self.assertIn("RunManagedInspectionHelper<ManagedMethodIlResult>", managed_entrypoints)
        self.assertIn("external_process_managed_metadata_and_il_read_only", managed_entrypoints)
        self.assertIn("FileNotFoundException(\"ManagedInspectionHelper.exe was not found", vs_service)
        self.assertNotIn("Assembly.LoadFrom", vs_service)
        self.assertNotIn("ReflectionTypeLoadException", vs_service)
        self.assertNotIn("AssemblyResolve", vs_service)
        self.assertNotIn("DecodeIlInstructions", vs_service)
        self.assertNotIn("OperandType.", vs_service)
        self.assertNotIn("BuildOpCodeMap", vs_service)

    def test_managed_inspection_helper_covers_hard_operands_and_metadata_boundaries(self) -> None:
        helper = read("VSDebuggerMCP/ManagedInspectionHelper/Program.cs")
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")
        il_decoder = csharp_slice(helper, "private static List<ManagedIlInstructionInfo> DecodeIlInstructions", "        private static OpCode[] BuildOpCodeMap")

        for marker in (
            "OperandType.InlineSwitch",
            "OperandType.ShortInlineBrTarget",
            "OperandType.InlineBrTarget",
            "OperandType.InlineMethod",
            "OperandType.InlineField",
            "OperandType.InlineString",
            "OperandType.InlineType",
            "OperandType.InlineTok",
            "OperandType.InlineSig",
            "ResolveIlToken",
            "module.ResolveMember",
            "module.ResolveString",
            "module.ResolveType",
            "module.ResolveField",
            "GetSafeGenericArguments",
            "RequireIlBytes",
            "<truncated operand>",
            "Unknown opcode",
            "FormatIlOffset",
        ):
            self.assertIn(marker, il_decoder)

        for marker in (
            "Assembly.LoadFrom(normalizedPath)",
            "ReflectionTypeLoadException",
            "AssemblyResolve",
            "ResolveManagedDependency",
            "NormalizeProbeDirectories",
            "method.GetMethodBody()",
            "body.GetILAsByteArray()",
            "LocalVariableInfo",
            "methodName, \".ctor\"",
            "methodName, \".cctor\"",
            "overloadIndex",
            "maxInstructions",
        ):
            self.assertIn(marker, helper)

        for marker in (
            "PublicAssemblies",
            "PrivateAssemblies",
            "CommonExtensions\", \"Microsoft\", \"Editor",
            "SharedAssemblies",
        ):
            self.assertIn(marker, vs_service)

        self.assertNotIn("Assembly.LoadFrom", vs_service)

    def test_managed_inspection_edge_fixture_exercises_nontrivial_il(self) -> None:
        fixture_project = read("tests/fixtures/ManagedInspectionEdgeCases/ManagedInspectionEdgeCases.csproj")
        fixture_code = read("tests/fixtures/ManagedInspectionEdgeCases/HardIlCases.cs")

        self.assertIn("<OutputType>Library</OutputType>", fixture_project)
        for marker in (
            "public int DenseSwitch",
            "switch (mode)",
            "checked(value * 2)",
            "public string TokenHeavy<T>",
            "typeof(T)",
            "string.Format",
            "public int Overloaded(int value)",
            "public string Overloaded(string value)",
            "public int TryFinallyLoop",
            "finally",
            "private int Helper",
        ):
            self.assertIn(marker, fixture_code)

    def test_managed_inspection_helper_executes_edge_fixture(self) -> None:
        msbuild = find_msbuild()
        if msbuild is None:
            self.skipTest("MSBuild.exe not found")

        helper_project = ROOT / "VSDebuggerMCP/ManagedInspectionHelper/ManagedInspectionHelper.csproj"
        fixture_project = ROOT / "tests/fixtures/ManagedInspectionEdgeCases/ManagedInspectionEdgeCases.csproj"
        for project in (helper_project, fixture_project):
            completed = subprocess.run(
                [str(msbuild), str(project), "/p:Configuration=Release", "/m:1", "/v:minimal"],
                cwd=ROOT,
                text=True,
                encoding="utf-8",
                errors="replace",
                capture_output=True,
            )
            self.assertEqual(completed.returncode, 0, completed.stdout + completed.stderr)

        helper_exe = ROOT / "VSDebuggerMCP/ManagedInspectionHelper/bin/Release/ManagedInspectionHelper.exe"
        fixture_dll = ROOT / "tests/fixtures/ManagedInspectionEdgeCases/bin/Release/ManagedInspectionEdgeCases.dll"
        self.assertTrue(helper_exe.exists())
        self.assertTrue(fixture_dll.exists())

        def invoke_helper(request: dict) -> dict:
            completed = subprocess.run(
                [str(helper_exe)],
                cwd=ROOT,
                input=json.dumps(request),
                text=True,
                encoding="utf-8",
                errors="replace",
                capture_output=True,
            )
            self.assertEqual(completed.returncode, 0, completed.stdout + completed.stderr)
            self.assertTrue(completed.stdout.strip(), completed.stderr)
            return json.loads(completed.stdout)

        def result_value(result: dict, name: str):
            pascal_name = name[:1].upper() + name[1:]
            return result.get(name, result.get(pascal_name))

        common = {
            "AssemblyPath": str(fixture_dll),
            "TypeName": "ManagedInspectionEdgeCases.HardIlCases",
            "ProbeDirectories": [str(fixture_dll.parent)],
        }
        dense_switch = invoke_helper({
            **common,
            "Operation": "inspect_managed_method_il",
            "MethodName": "DenseSwitch",
            "OverloadIndex": 0,
            "MaxInstructions": 512,
        })
        dense_instructions = result_value(dense_switch, "instructions")
        self.assertFalse(result_value(dense_switch, "partial"), result_value(dense_switch, "error"))
        self.assertTrue(any(result_value(i, "switchTargets") for i in dense_instructions))
        self.assertTrue(any(result_value(i, "branchTarget") for i in dense_instructions))

        token_heavy = invoke_helper({
            **common,
            "Operation": "inspect_managed_method_il",
            "MethodName": "TokenHeavy",
            "OverloadIndex": 0,
            "MaxInstructions": 512,
        })
        token_instructions = result_value(token_heavy, "instructions")
        self.assertTrue(any(result_value(i, "resolvedMember") for i in token_instructions))
        self.assertTrue(any(result_value(i, "operandType") == "InlineType" for i in token_instructions))

        bad_overload = invoke_helper({
            **common,
            "Operation": "inspect_managed_method_il",
            "MethodName": "Overloaded",
            "OverloadIndex": 99,
            "MaxInstructions": 512,
        })
        self.assertIn("overloadIndex 99 is out of range", result_value(bad_overload, "error") or "")

    def test_hybrid_breakpoint_capture_composes_no_source_dynamic_snapshot(self) -> None:
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")
        server_py = read("mcp_server/server.py")

        hybrid = csharp_slice(vs_service, "public HybridContextResult CaptureHybridContext", "        public BreakpointsResult GetBreakpointBindings")
        for marker in (
            "BuildDebugStatus(debugger)",
            "SelectedStackFrameIndex = _selectedStackFrameIndex",
            "BuildNativeContextResult(debugger",
            "BuildThreadsResult(",
            "DetermineThreadsSnapshotStatus",
            "BuildBreakpointsResult(debugger)",
            "includeNative",
            "includeThreads",
            "includeBreakpoints",
        ):
            self.assertIn(marker, hybrid)

        capture = csharp_slice(vs_service, "public BreakpointCaptureResult ContinueAndCaptureBreakpoint", "        private static string NormalizeAssemblyPath")
        for marker in (
            "ContinueAndWaitForBreak(",
            "includeException: true",
            "allowSideEffects: false",
            "CaptureHybridContext(",
            "GetBreakpointBindings()",
            "waitResult.BreakDetected",
            "DetermineHitBreakpointExact",
            "HitAttributionStatus = \"ok\"",
            "HitAttributionStatus = \"missing\"",
            "\"No debug stop was captured\"",
            "\"Current instruction address exactly matches an enabled bound tracked address breakpoint\"",
            "\"Current source location exactly matches an enabled bound source breakpoint resolved location\"",
            "\"No enabled bound tracked address breakpoint or source breakpoint exactly matches the current stop location\"",
            "\"Multiple tracked address breakpoints match the current instruction address\"",
            "\"Multiple enabled bound source breakpoints match the current source location\"",
        ):
            self.assertIn(marker, capture)

        self.assertIn("AddressBreakpointTagPrefix = \"AIDB_ADDR=\"", vs_service)
        self.assertIn("TrySetAddressBreakpointTag(breakpoint, addressText)", vs_service)
        self.assertIn("TryGetAddressBreakpointTag", vs_service)
        self.assertIn("breakpointInfo.TrackedAddress = ResolveAddressInternal(debugger, taggedAddress)", vs_service)
        self.assertIn("breakpointInfo.TrackedAddresses.Add(breakpointInfo.TrackedAddress)", vs_service)
        self.assertIn("!breakpoint.Enabled", vs_service)
        self.assertIn("!breakpoint.IsBound", vs_service)
        self.assertIn("PopulateHitVerification(result)", vs_service)
        self.assertIn("hit_exact_tracked_address", vs_service)
        self.assertIn("hit_exact_source_location", vs_service)
        self.assertIn("not_hit_timeout", vs_service)
        self.assertIn("breakpoint_stop_unattributed", vs_service)
        self.assertIn("stopped_not_verified_as_breakpoint", vs_service)
        self.assertIn("FindCurrentSourceLocationBreakpointMatches", vs_service)
        self.assertNotIn("LikelyHitBreakpoint", vs_service)
        self.assertNotIn("exactMatches.Count == 0 && breakpoints.Count == 1", vs_service)

        breakpoint_tool = function_source(server_py, "continue_and_capture_breakpoint")
        self.assertIn("add_thread_capture_params", breakpoint_tool)
        self.assertIn('"includeNative": include_native', breakpoint_tool)
        self.assertIn('"includeBreakpoints": include_breakpoints', breakpoint_tool)
        self.assertIn("timeout=float(timeout_seconds + tool_timeout_for_budget(None, HYBRID_CONTEXT_TIMEOUT_SECONDS, thread_budget_ms))", breakpoint_tool)

    def test_breakpoint_binding_verification_surfaces_optimized_or_unbound_locations(self) -> None:
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")
        vs_models = read("VSDebuggerMCP/Protocol/DebugModels.cs")
        server_py = read("mcp_server/server.py")

        for field in (
            "bindingVerificationStatus",
            "bindingWarning",
            "isRelocated",
            "nearbyProbeAttempted",
            "nearbyProbeSucceeded",
            "optimizedCodeSuspected",
            "verificationStatus",
            "verificationMessage",
        ):
            self.assertIn(field, vs_models)

        for marker in (
            "PopulateBreakpointBindingAwareness",
            "ApplyBreakpointOperationVerification",
            "MarkNearbyProbeResult",
            "IsActiveDebugSession",
            "effectiveRequestedLineNumber",
            "breakpoint.LocationType == dbgBreakpointLocationType.dbgBreakpointLocationTypeFile",
            "bound_relocated",
            "bound_exact",
            "unbound",
            "Breakpoint did not bind in the current debug session",
            "Requested source line did not bind; a nearby executable line was selected",
            "no nearby bound executable line was found",
            "Optimized code, statement folding, or source/symbol mismatch may be involved",
            "BreakpointProbeBindWaitMs",
            "dbgDebugMode.dbgRunMode",
            "return \"pending\"",
            "debugger.CurrentMode == dbgDebugMode.dbgBreakMode",
        ):
            self.assertIn(marker, vs_service)

        set_breakpoint = csharp_slice(vs_service, "public BreakpointOperationResult SetBreakpoint", "        public BreakpointOperationResult SetFunctionBreakpoint")
        self.assertIn("ShouldSearchNearbyBindableLine(debugger, breakpointInfo)", set_breakpoint)
        self.assertIn("TryFindNearbyBoundBreakpoint(", set_breakpoint)
        self.assertIn("ApplyBreakpointOperationVerification(result, breakpointInfo, debugger, requireBoundInActiveSession: true)", set_breakpoint)
        self.assertIn("breakpoint_binding_verification", function_source(server_py, "debug_capabilities"))

    def test_rider_detach_is_explicitly_unsupported(self) -> None:
        rider_service = read("RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/DebuggerService.kt")

        self.assertIn('"detach" -> debugDetach()', rider_service)
        self.assertIn("Detach is not supported by the Rider MCP bridge", rider_service)

    def test_vs_registration_instructions_include_active_debugging_workflow(self) -> None:
        vs_registration = read("VSDebuggerMCP/McpClientRegistration.cs")

        for keyword in (
            "debug_capabilities",
            "主动断点式定位",
            "attach_process",
            "start_debugging",
            "select_stack_frame",
            "set_data_breakpoint",
            "当前不暴露 logpoint 工具",
            "continue、pause、step_over",
        ):
            self.assertIn(keyword, vs_registration)

    def test_heavy_debug_tools_have_explicit_timeouts(self) -> None:
        server_py = read("mcp_server/server.py")
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")
        rider_service = read("RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/DebuggerService.kt")

        self.assertIn("BATCH_EVALUATION_TIMEOUT_SECONDS", server_py)
        self.assertIn("EVALUATION_TIMEOUT_SECONDS", server_py)
        self.assertIn("SAFE_DEBUG_BUNDLE_TIMEOUT_SECONDS", server_py)
        self.assertIn("DEBUG_BUNDLE_TIMEOUT_SECONDS", server_py)
        self.assertIn("THREADS_TIMEOUT_SECONDS", server_py)
        self.assertIn("MODULES_TIMEOUT_SECONDS", server_py)
        self.assertIn("EXCEPTION_TIMEOUT_SECONDS", server_py)
        self.assertIn("MEMORY_TIMEOUT_SECONDS", server_py)
        self.assertIn("DISASSEMBLY_TIMEOUT_SECONDS", server_py)
        self.assertIn("timeout_seconds", server_py)
        self.assertIn("clamp_tool_timeout(timeout_seconds, EVALUATION_TIMEOUT_SECONDS)", server_py)
        self.assertIn("tool_timeout_for_budget(timeout_seconds, THREADS_TIMEOUT_SECONDS, budget_ms)", server_py)
        self.assertIn("clamp_tool_timeout(timeout_seconds, MODULES_TIMEOUT_SECONDS)", server_py)
        self.assertIn("clamp_tool_timeout(timeout_seconds, EXCEPTION_TIMEOUT_SECONDS)", server_py)
        self.assertIn("clamp_tool_timeout(timeout_seconds, MEMORY_TIMEOUT_SECONDS)", function_source(server_py, "read_memory"))
        search_memory = function_source(server_py, "search_memory")
        self.assertIn("tool_timeout = clamp_tool_timeout(", search_memory)
        self.assertIn("remaining_tool_timeout(deadline, MEMORY_TIMEOUT_SECONDS)", search_memory)
        self.assertIn("clamp_tool_timeout(timeout_seconds, DISASSEMBLY_TIMEOUT_SECONDS)", function_source(server_py, "get_disassembly"))
        self.assertIn("clamp_tool_timeout(timeout_seconds, DISASSEMBLY_TIMEOUT_SECONDS)", function_source(server_py, "get_disassembly_at"))
        self.assertIn("allow_side_effects", server_py)
        self.assertIn("allowSideEffects", server_py)
        self.assertIn("include_locals", server_py)
        self.assertIn("includeLocals", server_py)
        self.assertIn("ExpressionEvaluationTimeoutMs", vs_service)
        self.assertIn("ExpressionBatchBudgetMs", vs_service)
        self.assertIn("ExpressionExpansionBudgetMs", vs_service)
        self.assertIn("ExpressionExpansionMaxNodes", vs_service)
        self.assertIn("LocalsExpansionBudgetMs", vs_service)
        self.assertIn("LocalsExpansionMaxNodes", vs_service)
        self.assertIn("allowSideEffects", vs_service)
        self.assertIn("GetExpression(expression, false, ExpressionEvaluationTimeoutMs)", vs_service)
        self.assertIn("Expansion truncated", vs_service)
        self.assertIn("variableExpansionBudgetMs", rider_service)
        self.assertIn("allowSideEffects", rider_service)
        self.assertIn("Expansion truncated", rider_service)

    def test_thread_enumeration_is_budgeted(self) -> None:
        server_py = read("mcp_server/server.py")
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")
        vs_pipe = read("VSDebuggerMCP/NamedPipeServer.cs")
        rider_service = read("RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/DebuggerService.kt")
        vs_models = read("VSDebuggerMCP/Protocol/DebugModels.cs")
        rider_models = read("RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/protocol/DebugModels.kt")

        self.assertIn("timeout_seconds: Optional[float] = None", function_source(server_py, "get_threads"))
        self.assertIn("max_frames_per_thread: Optional[int] = None", function_source(server_py, "get_threads"))
        self.assertIn("max_nodes: Optional[int] = None", function_source(server_py, "get_threads"))
        self.assertIn("budget_ms: Optional[int] = None", function_source(server_py, "get_threads"))
        self.assertIn('"maxFramesPerThread"', server_py)
        self.assertIn('"maxNodes"', server_py)
        self.assertIn('"budgetMs"', server_py)
        self.assertIn("tool_timeout_for_budget(timeout_seconds, THREADS_TIMEOUT_SECONDS, budget_ms)", server_py)
        self.assertIn("MAX_THREAD_BUDGET_SECONDS = 115.0", server_py)
        self.assertIn("elif float(budget_ms) == 0.0", server_py)
        self.assertIn('GetParam<int>(request.Params, "maxFramesPerThread", -1)', vs_pipe)
        self.assertIn("ThreadEnumerationBudgetMs", vs_service)
        self.assertIn("ThreadFramesMaxPerThread", vs_service)
        self.assertIn("NormalizeThreadFrameLimit", vs_service)
        self.assertIn("NormalizeThreadNodeLimit", vs_service)
        self.assertIn("NormalizeThreadBudgetMs", vs_service)
        self.assertIn("maxFramesPerThread == 0", vs_service)
        self.assertIn("maxNodes == 0", vs_service)
        self.assertIn("budgetMs < 0", vs_service)
        self.assertIn("budgetMs == 0", vs_service)
        self.assertIn("return DebugBundleMainThreadTimeoutMs - 5000", vs_service)
        self.assertIn("Thread frame enumeration truncated", vs_service)
        self.assertIn("HeavyMainThreadTimeoutMs", vs_service)
        self.assertIn("AddPartialReason(partialReasons", vs_service)
        self.assertIn("DetermineThreadsSnapshotStatus", vs_service)

        self.assertIn('val includeFrames = getParam(request.params, "includeFrames", true)', rider_service)
        self.assertIn("normalizeThreadFrameLimit", rider_service)
        self.assertIn("normalizeThreadNodeLimit", rider_service)
        self.assertIn("normalizeThreadBudgetMs", rider_service)
        self.assertIn("budgetMs < 0 -> threadEnumerationBudgetMs", rider_service)
        self.assertIn("budgetMs == 0 -> 115000L", rider_service)
        self.assertIn("collectStackFramesWithStatus", rider_service)
        self.assertIn("budget.remainingMs()", rider_service)
        self.assertIn("determineThreadsSnapshotStatus", rider_service)

        for models in (vs_models, rider_models):
            self.assertIn("partial", models)
            self.assertIn("error", models)

    def test_thread_budget_flows_through_bundle_and_full_context(self) -> None:
        server_py = read("mcp_server/server.py")
        vs_pipe = read("VSDebuggerMCP/NamedPipeServer.cs")
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")
        rider_service = read("RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/DebuggerService.kt")

        for body_name in ("get_full_context", "capture_debug_bundle", "continue_and_wait_for_break"):
            body = function_source(server_py, body_name)
            self.assertIn("thread_include_frames", body)
            self.assertIn("thread_max_frames_per_thread", body)
            self.assertIn("thread_max_nodes", body)
            self.assertIn("thread_budget_ms", body)

        self.assertIn("add_thread_capture_params", server_py)
        self.assertIn("tool_timeout_for_budget(timeout_seconds, default_timeout, thread_budget_ms)", server_py)
        self.assertIn('timeout=float(timeout_seconds + tool_timeout_for_budget(None, 15.0, thread_budget_ms))', server_py)

        for field in (
            '"threadIncludeFrames"',
            '"threadMaxFramesPerThread"',
            '"threadMaxNodes"',
            '"threadBudgetMs"',
        ):
            self.assertIn(field, vs_pipe)
            self.assertIn(field, rider_service)

        self.assertIn("public FullContextResult GetFullContext(", vs_service)
        self.assertIn("public DebugBundleResult CaptureDebugBundle(", vs_service)
        self.assertIn("public ContinueAndWaitResult ContinueAndWaitForBreak(", vs_service)
        self.assertIn("BuildThreadsResult(", vs_service)
        self.assertIn("threadMaxFramesPerThread", vs_service)
        self.assertIn("threadMaxNodes", vs_service)
        self.assertIn("threadBudgetMs", vs_service)

        self.assertIn("private fun getFullContext(", rider_service)
        self.assertIn("private fun captureDebugBundle(", rider_service)
        self.assertIn("private fun continueAndWaitForBreak(", rider_service)
        self.assertIn("threadMaxFramesPerThread", rider_service)
        self.assertIn("threadMaxNodes", rider_service)
        self.assertIn("threadBudgetMs", rider_service)

    def test_composite_tools_use_end_to_end_timeout_budget(self) -> None:
        server_py = read("mcp_server/server.py")
        crash_triage = function_source(server_py, "crash_triage")
        step_and_capture = function_source(server_py, "step_and_capture")

        self.assertIn("remaining_tool_timeout", server_py)
        self.assertIn("deadline = asyncio.get_running_loop().time() + tool_timeout", crash_triage)
        self.assertIn("remaining_tool_timeout(deadline, DISASSEMBLY_TIMEOUT_SECONDS)", crash_triage)
        self.assertIn("remaining_tool_timeout(deadline, MEMORY_TIMEOUT_SECONDS)", crash_triage)
        self.assertIn("timeout budget was exhausted", crash_triage)
        self.assertIn("deadline = asyncio.get_running_loop().time() + tool_timeout", step_and_capture)
        self.assertIn("step_timeout = remaining_tool_timeout(deadline, tool_timeout)", step_and_capture)
        self.assertIn("bundle_timeout = remaining_tool_timeout(deadline, tool_timeout)", step_and_capture)
        self.assertIn("bundleError", step_and_capture)

    def test_empty_thread_snapshot_is_not_reported_ok(self) -> None:
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")
        rider_service = read("RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/DebuggerService.kt")

        self.assertIn('return threads.Threads.Count > 0 ? "ok" : "unavailable";', vs_service)
        self.assertIn('return if (threads.threads.isNotEmpty()) "ok" else "unavailable"', rider_service)
        self.assertIn("fullContext.Threads.SnapshotStatus = DetermineThreadsSnapshotStatus(fullContext.Threads, true);", vs_service)
        self.assertIn("bundle.Threads.SnapshotStatus = DetermineThreadsSnapshotStatus(bundle.Threads, true);", vs_service)
        self.assertIn("it.snapshotStatus = determineThreadsSnapshotStatus(it, true)", rider_service)
        self.assertIn('result.Error = "Current debug program unavailable"', vs_service)
        self.assertIn('result.error = "No execution stacks available"', rider_service)

    def test_vs_locals_reports_selected_stack_frame_index(self) -> None:
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")

        self.assertIn("private int _selectedStackFrameIndex;", vs_service)
        self.assertIn("result.FrameIndex = _selectedStackFrameIndex;", vs_service)
        self.assertIn("_selectedStackFrameIndex = frameIndex;", vs_service)
        self.assertIn("_selectedStackFrameIndex = 0;", vs_service)

    def test_vs_native_dump_frame_uses_active_source_fallback(self) -> None:
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")

        self.assertIn("TryGetActiveSourceDocumentPath", vs_service)
        self.assertIn("TryGetActiveSourceLineNumber", vs_service)
        self.assertIn("IsSourceFilePath", vs_service)
        self.assertIn("IsSyntheticExecutionPointFrame", vs_service)
        self.assertIn("Native dump debugging can expose the active source document", vs_service)
        self.assertIn("GetFrameFileName(frame, index)", vs_service)
        self.assertIn("GetFrameLineNumberEx(frame, index)", vs_service)
        self.assertIn("sourceFallbackFrameIndex = threadInfo.IsCurrent ? currentFrameIndex : -1", vs_service)
        self.assertIn("GetFrameLineNumberEx(frame, sourceFallbackFrameIndex)", vs_service)
        self.assertIn("GetFrameFileName(frame, sourceFallbackFrameIndex)", vs_service)
        self.assertIn("!IsSyntheticExecutionPointFrame(candidateFrame)", vs_service)
        self.assertIn("isCurrentOrSelectedFrame && TryGetActiveSourceDocumentPath", vs_service)
        self.assertIn("isCurrentFrame && TryGetActiveSourceLineNumber", vs_service)
        self.assertIn('StartsWith("[Execution point when dump was captured"', vs_service)
        self.assertIn('case ".ush":', vs_service)

    def test_vs_dump_modules_use_minidump_module_list_fallback(self) -> None:
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")

        self.assertIn("GetModulesFromMinidump", vs_service)
        self.assertIn("TryGetActiveDumpFilePath", vs_service)
        self.assertIn("_activeDumpFilePath", vs_service)
        self.assertIn("_activeDumpFilePath = dumpPath", vs_service)
        self.assertIn("IsDumpFilePath(_activeDumpFilePath)", vs_service)
        self.assertIn("ModuleListStream", vs_service)
        self.assertIn("0x504D444D", vs_service)
        self.assertIn("ReadMinidumpCodeViewPdbPath", vs_service)
        self.assertIn("MergeModuleInfo(moduleSet, moduleInfoDict, mod, addIfMissing: true)", vs_service)
        self.assertIn('Address = $"0x{baseOfImage:X16}"', vs_service)
        self.assertIn("SymbolPath = ReadMinidumpCodeViewPdbPath", vs_service)

    def test_start_debugging_uses_control_timeout(self) -> None:
        server_py = read("mcp_server/server.py")
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")

        start_debugging = function_source(server_py, "start_debugging")
        self.assertIn("START_DEBUGGING_TIMEOUT_SECONDS = 60.0", server_py)
        self.assertIn("timeout_seconds: Optional[float] = None", start_debugging)
        self.assertIn("clamp_tool_timeout(timeout_seconds, START_DEBUGGING_TIMEOUT_SECONDS)", start_debugging)
        self.assertIn("deadline = asyncio.get_running_loop().time() + wait_timeout", start_debugging)
        self.assertIn("wait_for_debugging_to_start(result, remaining_timeout, attach)", start_debugging)
        self.assertIn("wait_for_pause = executable_path.lower().endswith", server_py)
        self.assertIn("Started dump debugging and paused", server_py)

        vs_start = vs_service[vs_service.index("public ProcessDebugResult StartDebugging"):]
        vs_start = vs_start[:vs_start.index("private EnvDTE.Process FindLocalProcess")]
        self.assertIn("}, HeavyMainThreadTimeoutMs);", vs_start)

    def test_start_debugging_handles_active_dump_document(self) -> None:
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")
        vs_project = read("VSDebuggerMCP/VSDebuggerMCP.csproj")

        vs_start = vs_service[vs_service.index("public ProcessDebugResult StartDebugging"):]
        vs_start = vs_start[:vs_start.index("private EnvDTE.Process FindLocalProcess")]
        self.assertIn("TryStartActiveDumpDebugging(debugger, result)", vs_start)
        self.assertIn("IsDumpFilePath(normalizedExe)", vs_start)
        self.assertIn("StartDumpDebuggingFromPath(debugger, result, normalizedExe)", vs_start)
        self.assertIn("StartDumpDebuggingFromPath", vs_start)
        self.assertIn("_dte.ItemOperations.OpenFile(dumpPath)", vs_start)
        self.assertIn("TryStartActiveDumpNativeOnlyDebugging(out string startError)", vs_start)
        self.assertIn("_activeDumpFilePath = null", vs_start)
        self.assertIn("Requested Visual Studio to start native debugging for dump file", vs_start)
        self.assertIn('"Debug with Native Only"', vs_start)
        self.assertIn("TryInvokeVisualStudioControl", vs_start)
        self.assertIn("InvokePattern.Pattern", vs_start)
        self.assertIn("Requested Visual Studio to start native debugging for active dump file", vs_start)
        self.assertIn("DumpDebugStartPollMs", vs_start)
        self.assertIn("JoinableTaskFactory.Run(async () =>", vs_start)
        self.assertIn("SwitchToMainThreadAsync", vs_start)
        self.assertIn("_activeDumpFilePath = activeDocumentPath", vs_start)
        self.assertIn("System.IO.File.Exists(activeDocumentPath)", vs_start)
        self.assertIn('string.Equals(extension, ".dmp"', vs_start)
        self.assertNotIn('"Debug.DebugDumpFile"', vs_start)
        self.assertNotIn('"Debug.StartDebugTarget"', vs_start)
        self.assertIn('<Reference Include="UIAutomationClient" />', vs_project)
        self.assertIn('<Reference Include="UIAutomationTypes" />', vs_project)

    def test_ide_bridge_reselects_stale_selected_instance(self) -> None:
        bridge = read("mcp_server/ide_bridge.py")

        self.assertIn("_is_stale_selected_instance_error", bridge)
        self.assertIn("self.clear_selection()", bridge)
        self.assertIn("selected_pipe = self._selected_pipe", bridge)
        self.assertIn("self._selected_pipe == selected.pipe_name", bridge)
        self.assertIn("self._selected_pipe == selected_pipe", bridge)
        self.assertIn("access denied", bridge)
        non_stale_start = bridge.index("non_stale_markers = (")
        stale_start = bridge.index("stale_markers = (", non_stale_start + len("non_stale_markers = ("))
        self.assertIn("return False", bridge[non_stale_start:stale_start])
        self.assertNotIn('"not found"', bridge[bridge.index("stale_markers = ("):bridge.index("return any(marker", bridge.index("stale_markers = ("))])
        self.assertNotIn('"named pipe"', bridge[bridge.index("stale_markers = ("):bridge.index("return any(marker", bridge.index("stale_markers = ("))])
        self.assertIn("broken pipe", bridge)
        self.assertIn("empty response received from pipe", bridge)

    def test_bridge_does_not_hold_selection_lock_across_calls(self) -> None:
        ide_bridge_py = read("mcp_server/ide_bridge.py")
        vs_bridge_py = read("mcp_server/vs_bridge.py")
        rider_bridge_py = read("mcp_server/rider_bridge.py")

        self.assertIn("selected_ide = self._selected_ide", ide_bridge_py)
        self.assertIn("selected_pipe = self._selected_pipe", ide_bridge_py)
        self.assertIn("return await vs_bridge.call_pipe(selected_pipe, method, params, timeout)", ide_bridge_py)
        self.assertIn("return await rider_bridge.call_pipe(selected_pipe, method, params, timeout)", ide_bridge_py)
        self.assertIn("return await vs_bridge.call_pipe(selected.pipe_name, method, params, timeout)", ide_bridge_py)
        self.assertIn("instances = await self.discover_instances(preferred_ide=preferred)", ide_bridge_py)

        self.assertIn("pipe_name = self._selected_pipe", vs_bridge_py)
        self.assertIn("async def call_pipe", vs_bridge_py)
        self.assertIn("loop.run_in_executor", vs_bridge_py)
        self.assertIn("self._cached_instances = instances", vs_bridge_py)
        self.assertIn("return await self.call_pipe(pipe_name, method, params, timeout)", vs_bridge_py)
        self.assertIn("pipe_name = self._selected_pipe", rider_bridge_py)
        self.assertIn("async def call_pipe", rider_bridge_py)
        self.assertIn("loop.run_in_executor", rider_bridge_py)
        self.assertIn("self._cached_instances = instances", rider_bridge_py)
        self.assertIn("return await self.call_pipe(pipe_name, method, params, timeout)", rider_bridge_py)

    def test_unreachable_pipes_are_diagnostic_only(self) -> None:
        vs_bridge_py = read("mcp_server/vs_bridge.py")
        rider_bridge_py = read("mcp_server/rider_bridge.py")
        ide_bridge_py = read("mcp_server/ide_bridge.py")

        for bridge_py in (vs_bridge_py, rider_bridge_py):
            self.assertIn('self.connection_status = "ok"', bridge_py)
            self.assertIn('"connectionStatus": "unreachable"', bridge_py)
            self.assertIn('"lastError": str(e)', bridge_py)
            self.assertIn('instances = [i for i in instances if i.connection_status == "ok"]', bridge_py)
            self.assertIn('return any(i.connection_status == "ok" for i in instances)', bridge_py)

        self.assertIn("Multiple VS instances found or no reachable instance could be auto-selected", vs_bridge_py)
        self.assertIn('self.connection_status = getattr(instance, "connection_status", "ok")', ide_bridge_py)
        self.assertIn('"connectionStatus": self.connection_status', ide_bridge_py)
        self.assertIn('instances = [i for i in instances if i.connection_status == "ok"]', ide_bridge_py)
        self.assertIn('return any(i.connection_status == "ok" for i in instances)', ide_bridge_py)

    def test_list_instances_do_not_claim_selection(self) -> None:
        server_py = read("mcp_server/server.py")

        list_vs = function_source(server_py, "list_vs_instances")
        list_ide = function_source(server_py, "list_ide_instances")
        detect = function_source(server_py, "detect_debug_sessions")

        self.assertIn('"selected": False', list_vs)
        self.assertIn('result["selected"] = False', list_ide)
        self.assertIn("wouldAutoSelect", list_vs)
        self.assertIn("wouldAutoSelect", list_ide)
        self.assertIn('"unreachable": len(instances) - len(selectable_instances)', list_vs)
        self.assertIn('"unreachable": len(instances) - len(selectable_instances)', list_ide)
        self.assertNotIn("autoSelected", list_vs)
        self.assertNotIn("autoSelected", list_ide)
        self.assertIn("autoSelected", detect)

    def test_preferred_ide_discovery_is_scoped(self) -> None:
        server_py = read("mcp_server/server.py")
        ide_bridge_py = read("mcp_server/ide_bridge.py")

        self.assertIn("async def discover_instances(self, preferred_ide: Optional[str] = None)", ide_bridge_py)
        self.assertIn("preferred_ide: Optional[str] = None", ide_bridge_py)
        self.assertIn("preferred_ide must be 'vs' or 'rider'", ide_bridge_py)
        self.assertIn("requires {preferred.upper()}", ide_bridge_py)
        self.assertIn("requires {requested_preferred.upper()}", ide_bridge_py)
        self.assertIn("bridge.call(method, params, timeout, preferred_ide=preferred_ide)", ide_bridge_py)
        self.assertIn('if preferred in (None, "vs"):', ide_bridge_py)
        self.assertIn('if preferred in (None, "rider"):', ide_bridge_py)
        self.assertIn("async def discover_instances(preferred_ide: Optional[str] = None)", ide_bridge_py)
        self.assertIn('discover_instances(preferred_ide="vs")', function_source(server_py, "list_vs_instances"))
        self.assertIn("discover_instances(preferred_ide=ide_key)", function_source(server_py, "select_ide_instance"))
        self.assertIn('discover_instances(preferred_ide="vs")', function_source(server_py, "select_vs_instance"))
        self.assertIn("discover_instances(preferred_ide=prefer_ide)", function_source(server_py, "detect_debug_sessions"))

    def test_vs_memory_search_contract_matches_backend_limit(self) -> None:
        server_py = read("mcp_server/server.py")
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")
        rider_service = read("RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/DebuggerService.kt")

        search_memory = function_source(server_py, "search_memory")
        vs_execute_search = csharp_slice(vs_service, "private MemorySearchResult ExecuteSearchMemory(", "            // Get start address")
        vs_pipe = read("VSDebuggerMCP/NamedPipeServer.cs")
        self.assertIn("MAX_MEMORY_SEARCH_CHUNK_SIZE = 0x100000", server_py)
        self.assertIn("MAX_MEMORY_SEARCH_SIZE = 0x40000000", server_py)
        self.assertIn("MAX_MEMORY_SEARCH_CHUNK_SIZE", search_memory)
        self.assertIn("chunked", search_memory)
        self.assertIn("nextStartExpression", search_memory)
        self.assertIn("nextStartOffset", search_memory)
        self.assertIn("remainingBytes", search_memory)
        self.assertIn('start_offset: int = 0', search_memory)
        self.assertIn('"startOffset": start_offset', search_memory)
        self.assertIn('"startOffset": absolute_offset', search_memory)
        self.assertIn("chunk_start = start_address if absolute_offset == 0 else", search_memory)
        self.assertIn("next_offset = offset + step", search_memory)
        self.assertIn('chunk_result.get("nextStartAddress") or start_address', search_memory)
        self.assertIn('"startAddress": start_address', search_memory)
        self.assertIn("Search stopped after reaching max_results", search_memory)
        self.assertNotIn("10 * 1024 * 1024", search_memory)
        self.assertIn("单次后端调用最大1MB，较大范围会自动分片覆盖", search_memory)
        self.assertIn('pattern_format: str = "auto"', search_memory)
        self.assertIn('"patternFormat": pattern_format', search_memory)
        self.assertIn('string patternFormat = GetParam<string>(request.Params, "patternFormat", "auto")', vs_pipe)
        self.assertIn('long startOffset = GetParam<long>(request.Params, "startOffset", 0)', vs_pipe)
        self.assertIn("Convert.ToInt64(value)", vs_pipe)
        self.assertIn("Pattern cannot be empty", vs_execute_search)
        self.assertIn("string.IsNullOrWhiteSpace(pattern)", vs_execute_search)
        self.assertIn('normalizedPatternFormat == "hex"', vs_execute_search)
        self.assertIn("looksLikeSpacedHex", vs_execute_search)
        self.assertIn('"41" remains literal ASCII text', vs_execute_search)
        self.assertIn("startOffset > 0 ? (ulong)startOffset : 0UL", vs_service)
        self.assertIn("addr += byteOffset", vs_service)
        self.assertNotIn("pattern.Trim().Length == 2", vs_execute_search)
        self.assertIn("result.Truncated = true", vs_service)
        self.assertIn("result.NextStartAddress", vs_service)
        self.assertIn('val patternFormat = getParam(request.params, "patternFormat", "auto")', rider_service)
        self.assertIn('val startOffset = getParam(request.params, "startOffset", 0L)', rider_service)
        self.assertIn("resolvedAddress + startOffset.coerceAtLeast(0L)", rider_service)
        self.assertIn("parsePattern(pattern, patternFormat)", rider_service)
        self.assertIn("looksLikeSpacedHex", rider_service)
        self.assertIn('val hex = trimmed.replace(Regex("\\\\s+"), "")', rider_service)
        self.assertIn("hex.substring(index * 2, index * 2 + 2).toInt(16).toByte()", rider_service)
        self.assertIn("truncated = truncated", rider_service)
        self.assertIn("nextStartAddress = nextStartAddress", rider_service)

    def test_debug_bundle_safe_defaults_are_advertised(self) -> None:
        server_py = read("mcp_server/server.py")
        vs_pipe = read("VSDebuggerMCP/NamedPipeServer.cs")
        rider_service = read("RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/DebuggerService.kt")
        vs_registration = read("VSDebuggerMCP/McpClientRegistration.cs")
        rider_registration = read("RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/McpClientRegistration.kt")
        readme = read("README.md")

        for text in (
            "Safe by default",
            "include_locals/include_modules/include_registers/include_output",
            "Expressions are attempted by default with bounded IDE timeouts",
        ):
            self.assertIn(text, server_py)

        for code in (vs_pipe, rider_service):
            self.assertIn('"includeLocals"', code)
            self.assertIn('"includeOutput"', code)
            self.assertIn('"allowSideEffects"', code)

        for doc in (vs_registration, rider_registration, readme):
            self.assertIn("low-blocking", doc)
            self.assertIn("value/error/timeout", doc)

        guide = function_source(server_py, "debug_guide")
        self.assertIn("crash_triage()", guide)
        self.assertIn("step_and_capture", guide)

        self.assertIn("timeout_seconds", function_source(server_py, "get_disassembly"))
        self.assertIn("timeout_seconds", function_source(server_py, "get_disassembly_at"))

    def test_expression_calls_are_attempted_instead_of_pre_rejected(self) -> None:
        server_py = read("mcp_server/server.py")
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")
        rider_service = read("RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/DebuggerService.kt")

        single_eval = function_source(server_py, "evaluate_expression")
        self.assertNotIn("return rejection", single_eval)
        self.assertNotIn("side_effect_rejection", server_py)
        self.assertNotIn("FindPotentialSideEffectCalls", vs_service)
        self.assertNotIn("findPotentialSideEffectCalls", rider_service)

        for name in (
            "evaluate_expressions_batch",
            "capture_debug_bundle",
            "continue_and_wait_for_break",
        ):
            body = function_source(server_py, name)
            self.assertNotIn("return rejection", body)
            self.assertIn('"allowSideEffects": allow_side_effects', body)

    def test_cursor_allowlist_helper_is_bundled(self) -> None:
        helper_py = read("mcp_server/cursor_allowlist_sync.py")
        vs_project = read("VSDebuggerMCP/VSDebuggerMCP.csproj")
        rider_registration = read("RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/McpClientRegistration.kt")

        self.assertIn("mcpAllowedTools", helper_py)
        self.assertIn("state.vscdb", helper_py)
        self.assertIn("cursor_allowlist_sync.py", vs_project)
        self.assertIn("cursor_allowlist_sync.py", rider_registration)

    def test_codex_registration_is_wired_for_vs_and_rider(self) -> None:
        vs_package = read("VSDebuggerMCP/VSDebuggerMCPPackage.cs")
        vs_registration = read("VSDebuggerMCP/McpClientRegistration.cs")
        rider_plugin = read("RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/RiderDebuggerPlugin.kt")
        rider_registration = read("RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/McpClientRegistration.kt")

        self.assertIn("RegisterWithCodex", vs_package)
        self.assertIn("GetCodexConfigPath", vs_registration)
        self.assertIn("RegisterWithCodex", vs_registration)
        self.assertIn("config.toml", vs_registration)
        self.assertIn("CODEX_HOME", vs_registration)
        self.assertIn("registerWithCodex()", rider_plugin)
        self.assertIn("getCodexConfigPath", rider_registration)
        self.assertIn("registerWithCodex", rider_registration)
        self.assertIn("config.toml", rider_registration)
        self.assertIn("CODEX_HOME", rider_registration)


if __name__ == "__main__":
    unittest.main()
