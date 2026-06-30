import pathlib
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[1]


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
        self.assertIn("instances = await self.discover_instances()", ide_bridge_py)

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
