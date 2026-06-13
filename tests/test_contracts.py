import pathlib
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


class ContractTests(unittest.TestCase):
    def test_memory_contract_fields_present(self) -> None:
        vs_models = read("VSDebuggerMCP/Protocol/DebugModels.cs")
        rider_models = read("RiderDebuggerMCP/src/main/kotlin/com/vsdebugger/rider/protocol/DebugModels.kt")

        required_fields = [
            "status",
            "requestedSize",
            "bytesRead",
            "failedReads",
            "partial",
            "requestedBytes",
        ]
        for field in required_fields:
            self.assertIn(field, vs_models)
            self.assertIn(field, rider_models)

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
        self.assertIn("capture_debug_bundle", server_py)
        self.assertIn("evaluate_expressions_batch", server_py)
        self.assertIn("continue_and_wait_for_break", server_py)

    def test_server_exposes_active_debugging_tools_and_aliases(self) -> None:
        server_py = read("mcp_server/server.py")

        for tool_name in (
            "debug_capabilities",
            "attach_process",
            "start_debugging",
            'name="continue"',
            "pause",
            'name="step_over"',
            'name="step_into"',
            'name="step_out"',
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
        ):
            self.assertIn(method_name, vs_pipe)

        self.assertNotIn('"set_logpoint"', vs_pipe)
        for symbol in (
            "AttachProcess",
            "StartDebugging",
            "SetDataBreakpoint",
            "ParseHitCountType",
            "ProcessNameMatches",
        ):
            self.assertIn(symbol, vs_service)

        self.assertNotIn("SetLogpoint", vs_service)
        self.assertIn("ProcessDebugResult", vs_models)
        self.assertIn("KnownType(typeof(ProcessDebugResult))", vs_response)

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

        self.assertIn("BATCH_EVALUATION_TIMEOUT_SECONDS", server_py)
        self.assertIn("DEBUG_BUNDLE_TIMEOUT_SECONDS", server_py)
        self.assertIn("timeout_seconds", server_py)
        self.assertIn("ExpressionEvaluationTimeoutMs", vs_service)
        self.assertIn("ExpressionBatchBudgetMs", vs_service)
        self.assertIn("GetExpression(expression, false, ExpressionEvaluationTimeoutMs)", vs_service)

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
