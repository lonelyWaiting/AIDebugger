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

    def test_vs_registration_instructions_include_snapshot_workflow(self) -> None:
        vs_registration = read("VSDebuggerMCP/McpClientRegistration.cs")
        self.assertIn("capture_debug_bundle", vs_registration)
        self.assertIn("evaluate_expressions_batch", vs_registration)
        self.assertIn("stopReason", vs_registration)

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


if __name__ == "__main__":
    unittest.main()
