import asyncio
import json
import os
import pathlib
import stat
import tempfile
import threading
import time
import sys
import unittest
import uuid
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock, patch


ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from mcp_server import bootstrap

bootstrap.ensure_deps()

from mcp_server import vs_bridge, vs_session, vs_tool_registry


class VSBridgeResponseTests(unittest.TestCase):
    def setUp(self) -> None:
        self.request_id = "7e93aa46-d9e4-4a79-8781-71e77e936fec"

    def encode(self, payload: dict) -> bytes:
        envelope = {
            "protocol": vs_bridge.PROTOCOL_VERSION,
            "status": "ok",
            "server": {
                "processId": 42,
                "processStartTimeUtcTicks": 123456,
                "serverInstanceId": "instance",
                "protocolCatalogHash": vs_tool_registry.DEBUG_METHOD_CATALOG_HASH,
                "debugSessionGeneration": 7,
                "breakSequence": 11,
                "contextEpoch": 13,
            },
        }
        envelope.update(payload)
        if envelope.get("error"):
            envelope["status"] = "error"
        return json.dumps(envelope).encode("utf-8")

    def test_command_request_ids_are_canonical_monotonic_uuid7(self) -> None:
        request_ids = [vs_bridge._new_request_id() for _ in range(1000)]
        self.assertEqual(request_ids, sorted(request_ids))
        self.assertEqual(len(request_ids), len(set(request_ids)))
        for request_id in request_ids:
            parsed = uuid.UUID(request_id)
            self.assertEqual(7, parsed.version)
            self.assertEqual(uuid.RFC_4122, parsed.variant)
            self.assertEqual(str(parsed), request_id)

    def test_valid_response_returns_structured_result(self) -> None:
        result = vs_bridge._decode_response(
            self.encode({"id": self.request_id, "result": {"isPaused": True}, "error": None}),
            self.request_id,
        )
        self.assertEqual({"isPaused": True}, result)

    def test_response_rejects_protocol_catalog_mismatch(self) -> None:
        expected = {
            "processId": 42,
            "processStartTimeUtcTicks": 123456,
            "serverInstanceId": "instance",
            "protocolCatalogHash": "0" * 64,
        }
        with self.assertRaises(vs_bridge.VSBridgeError) as raised:
            vs_bridge._decode_response(
                self.encode({"id": self.request_id, "result": {}}),
                self.request_id,
                expected_server=expected,
            )
        self.assertEqual("PROTOCOL_CATALOG_MISMATCH", raised.exception.code)

    def test_response_id_mismatch_is_rejected(self) -> None:
        with self.assertRaises(vs_bridge.VSBridgeError) as raised:
            vs_bridge._decode_response(
                self.encode({"id": "different", "result": {}}),
                self.request_id,
            )
        self.assertEqual("RESPONSE_ID_MISMATCH", raised.exception.code)

    def test_oversized_response_is_rejected_before_decoding(self) -> None:
        with self.assertRaises(vs_bridge.VSBridgeError) as raised:
            vs_bridge._decode_response(
                b"x" * (vs_bridge.MAX_RESPONSE_MESSAGE_SIZE + 1),
                self.request_id,
            )
        self.assertEqual("RESOURCE_LIMIT_EXCEEDED", raised.exception.code)

    def test_instance_state_cache_does_not_rewind_on_out_of_order_responses(self) -> None:
        pipe_name = "AIDebuggerVS_42"
        newer = {
            "serverInstanceId": "a" * 32,
            "debugSessionGeneration": 7,
            "breakSequence": 11,
            "contextEpoch": 13,
        }
        older = {
            "serverInstanceId": "a" * 32,
            "debugSessionGeneration": 6,
            "breakSequence": 10,
            "contextEpoch": 12,
        }
        replacement = {
            "serverInstanceId": "b" * 32,
            "debugSessionGeneration": 1,
            "breakSequence": 0,
            "contextEpoch": 1,
        }
        with vs_bridge._INSTANCE_STATE_LOCK:
            for key in [key for key in vs_bridge._INSTANCE_STATE if key[0] == pipe_name]:
                vs_bridge._INSTANCE_STATE.pop(key, None)
        try:
            self.assertEqual(newer, vs_bridge._record_instance_state(pipe_name, newer))
            self.assertEqual(newer, vs_bridge._record_instance_state(pipe_name, older))
            self.assertEqual(replacement, vs_bridge._record_instance_state(pipe_name, replacement))
            self.assertEqual(newer, vs_bridge._record_instance_state(pipe_name, older))
            self.assertEqual(
                replacement,
                vs_bridge._get_instance_state(pipe_name, replacement["serverInstanceId"]),
            )
        finally:
            with vs_bridge._INSTANCE_STATE_LOCK:
                for key in [key for key in vs_bridge._INSTANCE_STATE if key[0] == pipe_name]:
                    vs_bridge._INSTANCE_STATE.pop(key, None)

    def test_exact_request_identity_uses_pinned_target_not_mutable_cache(self) -> None:
        pipe_name = "AIDebuggerVS_42"
        registration = {"serverInstanceId": "a" * 32}
        pinned = {
            "debugSessionGeneration": 3,
            "breakSequence": 4,
            "contextEpoch": 5,
        }
        newer = {
            "serverInstanceId": registration["serverInstanceId"],
            "debugSessionGeneration": 8,
            "breakSequence": 9,
            "contextEpoch": 10,
        }
        try:
            vs_bridge._record_instance_state(pipe_name, newer)
            self.assertEqual(
                (3, 4, 5),
                vs_bridge._resolve_request_debug_identity(pipe_name, registration, pinned),
            )
            with self.assertRaises(vs_bridge.VSBridgeError) as raised:
                vs_bridge._resolve_request_debug_identity(
                    pipe_name,
                    registration,
                    {"debugSessionGeneration": 3},
                )
            self.assertEqual("INVALID_TARGET", raised.exception.code)
        finally:
            with vs_bridge._INSTANCE_STATE_LOCK:
                for key in [key for key in vs_bridge._INSTANCE_STATE if key[0] == pipe_name]:
                    vs_bridge._INSTANCE_STATE.pop(key, None)

    def test_invalid_utf8_and_json_are_typed_errors(self) -> None:
        for payload in (b"\xff", b"{"):
            with self.subTest(payload=payload), self.assertRaises(vs_bridge.VSBridgeError) as raised:
                vs_bridge._decode_response(payload, self.request_id)
            self.assertEqual("INVALID_RESPONSE", raised.exception.code)

    def test_non_json_unicode_whitespace_is_not_stripped(self) -> None:
        valid = self.encode({"id": self.request_id, "result": {"value": 1}})
        for whitespace in ("\u000b", "\u000c", "\u00a0", "\u2028"):
            encoded = whitespace.encode("utf-8")
            for position, payload in (("prefix", encoded + valid), ("suffix", valid + encoded)):
                with self.subTest(whitespace=repr(whitespace), position=position), \
                     self.assertRaises(vs_bridge.VSBridgeError) as raised:
                    vs_bridge._decode_response(payload, self.request_id)
                self.assertEqual("INVALID_RESPONSE", raised.exception.code)

    def test_non_finite_and_oversized_json_numbers_are_rejected(self) -> None:
        template = self.encode({
            "id": self.request_id,
            "result": {"value": 0},
        })
        for token in (
            b"NaN",
            b"Infinity",
            b"-Infinity",
            b"1e9999",
            b"18446744073709551616",
            b"-9223372036854775809",
            b"1" + (b"0" * 128),
            b"1." + (b"0" * 129),
        ):
            payload = template.replace(b'"value": 0', b'"value": ' + token)
            with self.subTest(token=token), self.assertRaises(vs_bridge.VSBridgeError) as raised:
                vs_bridge._decode_response(payload, self.request_id)
            self.assertEqual("INVALID_RESPONSE", raised.exception.code)

    def test_protocol_boundary_integers_are_accepted(self) -> None:
        for value in (-(1 << 63), (1 << 64) - 1):
            decoded = vs_bridge._decode_response(
                self.encode({"id": self.request_id, "result": {"value": value}}),
                self.request_id,
            )
            self.assertEqual(value, decoded["value"])

    def test_backend_error_code_is_preserved(self) -> None:
        with self.assertRaises(vs_bridge.VSBridgeError) as raised:
            vs_bridge._decode_response(
                self.encode({
                    "id": self.request_id,
                    "result": None,
                    "error": {"code": "INVALID_ARGUMENT", "message": "bad depth"},
                }),
                self.request_id,
            )
        self.assertEqual("INVALID_ARGUMENT", raised.exception.code)
        self.assertIsInstance(raised.exception, vs_bridge.VSRemoteError)
        self.assertEqual(11, raised.exception.server_identity["breakSequence"])
        self.assertEqual(13, raised.exception.server_identity["contextEpoch"])
        finalized = vs_bridge._finalize_call_error(
            raised.exception,
            is_command=True,
            write_started=True,
            request_id=self.request_id,
        )
        self.assertFalse(finalized.outcome_unknown)
        self.assertIsNone(finalized.request_id)

    def test_remote_outcome_unknown_keeps_request_id_for_status_query(self) -> None:
        with self.assertRaises(vs_bridge.VSRemoteError) as raised:
            vs_bridge._decode_response(
                self.encode({
                    "id": self.request_id,
                    "result": None,
                    "error": {
                        "code": "OUTCOME_UNKNOWN",
                        "message": "The accepted command did not produce a final status.",
                    },
                }),
                self.request_id,
            )

        finalized = vs_bridge._finalize_call_error(
            raised.exception,
            is_command=True,
            write_started=True,
            request_id=self.request_id,
        )
        self.assertTrue(finalized.outcome_unknown)
        self.assertEqual(self.request_id, finalized.request_id)

    def test_command_transport_error_after_write_has_unknown_outcome(self) -> None:
        error = vs_bridge.VSBridgeError("disconnected", code="PIPE_DISCONNECTED")
        finalized = vs_bridge._finalize_call_error(
            error,
            is_command=True,
            write_started=True,
            request_id=self.request_id,
        )
        self.assertTrue(finalized.outcome_unknown)
        self.assertEqual(self.request_id, finalized.request_id)

    def test_overlapped_cancellation_reaps_terminal_completion_before_cleanup(self) -> None:
        calls = []

        class Kernel:
            def CancelIoEx(self, handle, overlapped):
                calls.append("cancel")
                return True

            def GetOverlappedResult(self, handle, overlapped, transferred, wait):
                calls.append(("reap", wait))
                return False

        overlapped = vs_bridge.OVERLAPPED()
        vs_bridge._cancel_pending_io(Kernel(), 1, overlapped)
        self.assertEqual(["cancel", ("reap", True)], calls)

    def test_capability_mapping_matches_method_risk(self) -> None:
        self.assertEqual("debug.execution.control", vs_tool_registry.backend_method_capability("continue"))
        self.assertEqual("debug.evaluate.unsafe", vs_tool_registry.backend_method_capability("evaluate_expression"))
        self.assertEqual("debug.memory.read", vs_tool_registry.backend_method_capability("read_memory"))
        self.assertEqual("debug.context.write", vs_tool_registry.backend_method_capability("switch_thread"))
        self.assertEqual("debug.read", vs_tool_registry.backend_method_capability("get_debug_processes"))
        self.assertEqual("debug.context.write", vs_tool_registry.backend_method_capability("switch_process"))
        self.assertEqual("server.control", vs_tool_registry.backend_method_capability("cancel_request"))
        self.assertEqual("command", vs_tool_registry.backend_method_kind("cancel_request"))
        cancel_annotations = vs_tool_registry.tool_annotation_values("cancel_debug_request")
        self.assertFalse(cancel_annotations["readOnlyHint"])
        self.assertTrue(cancel_annotations["destructiveHint"])
        self.assertTrue(cancel_annotations["idempotentHint"])
        # The dispatch tools are split by method kind precisely so their
        # published annotations are accurate rather than a conservative union
        # over every method either one could route.
        query_annotations = vs_tool_registry.tool_annotation_values("dispatch_vs_instances")
        self.assertTrue(query_annotations["readOnlyHint"])
        self.assertFalse(query_annotations["destructiveHint"])
        self.assertTrue(query_annotations["idempotentHint"])
        command_annotations = vs_tool_registry.tool_annotation_values("dispatch_vs_commands")
        self.assertFalse(command_annotations["readOnlyHint"])
        self.assertTrue(command_annotations["destructiveHint"])
        self.assertFalse(command_annotations["idempotentHint"])

        queries = vs_tool_registry.backend_methods_for_tool("dispatch_vs_instances")
        commands = vs_tool_registry.backend_methods_for_tool("dispatch_vs_commands")
        self.assertTrue(queries and commands)
        self.assertEqual(set(), set(queries) & set(commands))
        self.assertEqual(
            set(vs_tool_registry.DEBUG_METHOD_MANIFEST),
            set(queries) | set(commands),
        )
        self.assertTrue(all(
            vs_tool_registry.backend_method_kind(name) == "query" for name in queries
        ))
        self.assertTrue(all(
            vs_tool_registry.backend_method_kind(name) == "command" for name in commands
        ))
        self.assertEqual(
            ("debug.execution.control", "debug.memory.read", "debug.read"),
            vs_tool_registry.backend_method_capabilities("continue_and_capture_breakpoint"),
        )
        self.assertEqual(
            ("debug.read",),
            vs_tool_registry.backend_method_capabilities("capture_debug_bundle_safe"),
        )
        self.assertEqual(
            ("debug.execution.control", "debug.read"),
            vs_tool_registry.backend_method_capabilities("continue_and_wait_for_break_safe"),
        )
        self.assertEqual(
            ("debug.evaluate.unsafe", "debug.read"),
            vs_tool_registry.backend_method_capabilities("get_locals"),
        )
        for method in ("get_locals", "get_full_context", "get_exception"):
            with self.subTest(method=method):
                self.assertEqual("command", vs_tool_registry.backend_method_kind(method))
        self.assertEqual(
            ("debug.memory.read", "debug.read"),
            vs_tool_registry.backend_method_capabilities("capture_native_context"),
        )
        self.assertIn(
            "debug.memory.read",
            vs_tool_registry.backend_method_capabilities("capture_debug_bundle", {"includeRegisters": True}),
        )

    def test_vs_registry_models_parameter_conditional_safe_routes(self) -> None:
        summary = vs_tool_registry.tool_registry_summary()["toolSupport"]
        bundle = summary["capture_debug_bundle"]
        self.assertEqual(["capture_debug_bundle_safe"], bundle["defaultBackendMethods"])
        self.assertEqual(["capture_debug_bundle"], bundle["alternativeBackendMethods"])
        self.assertEqual(["debug.read"], bundle["requiredCapabilities"])
        self.assertTrue(bundle["parameterConditionalRouting"])
        self.assertTrue(bundle["defaultInvocationAnnotations"]["readOnlyHint"])
        self.assertTrue(any(
            item["capability"] == "debug.evaluate.unsafe" and item.get("whenAny")
            for item in bundle["conditionalCapabilities"]
        ))
        # The MCP annotation is intentionally conservative because annotations
        # cannot vary by invocation parameters.
        self.assertFalse(vs_tool_registry.tool_annotation_values("capture_debug_bundle")["readOnlyHint"])

        step = summary["step_and_capture"]
        self.assertIn("capture_debug_bundle_safe", step["defaultBackendMethods"])
        self.assertIn("capture_debug_bundle", step["alternativeBackendMethods"])
        self.assertIn("resolve_address", summary["crash_triage"]["backendMethods"])

    @unittest.skipUnless(sys.platform == "win32", "Windows ACL behavior")
    def test_registration_acl_accepts_only_owner_and_rejects_world_access(self) -> None:
        import win32security
        import ntsecuritycon
        from mcp_server import vs_trust

        with tempfile.TemporaryDirectory() as directory, patch.dict(
            os.environ,
            {"LOCALAPPDATA": directory},
        ):
            # Elevated CI runners often create TEMP files owned by Administrators
            # while TokenUser is the interactive SID; set owner+DACL explicitly.
            aidb_root = vs_trust.ensure_owner_only_directory(
                pathlib.Path(directory) / "AIDebugger"
            )
            instances_root = vs_trust.ensure_owner_only_directory(aidb_root / "instances")
            path = instances_root / "instance.json"
            path.write_text("{}", encoding="utf-8")
            vs_trust.apply_owner_only_security(path)
            vs_bridge._validate_registration_security(path)

            security = win32security.GetFileSecurity(
                str(path),
                win32security.OWNER_SECURITY_INFORMATION | win32security.DACL_SECURITY_INFORMATION,
            )
            owner = security.GetSecurityDescriptorOwner()
            dacl = win32security.ACL()
            dacl.AddAccessAllowedAce(win32security.ACL_REVISION, ntsecuritycon.FILE_ALL_ACCESS, owner)
            world_sid = win32security.CreateWellKnownSid(win32security.WinWorldSid, None)
            dacl.AddAccessAllowedAce(win32security.ACL_REVISION, ntsecuritycon.FILE_GENERIC_READ, world_sid)
            descriptor = win32security.SECURITY_DESCRIPTOR()
            descriptor.SetSecurityDescriptorOwner(owner, False)
            descriptor.SetSecurityDescriptorDacl(True, dacl, False)
            descriptor.SetSecurityDescriptorControl(
                win32security.SE_DACL_PROTECTED, win32security.SE_DACL_PROTECTED
            )
            win32security.SetFileSecurity(
                str(path),
                win32security.OWNER_SECURITY_INFORMATION
                | win32security.DACL_SECURITY_INFORMATION
                | win32security.PROTECTED_DACL_SECURITY_INFORMATION,
                descriptor,
            )
            with self.assertRaises(vs_bridge.VSBridgeError) as raised:
                vs_bridge._validate_registration_security(path)
            self.assertEqual("INVALID_TARGET", raised.exception.code)

    def test_registration_rejects_reparse_parent_and_final_entry_before_acl_read(self) -> None:
        with tempfile.TemporaryDirectory() as directory, patch.dict(
            os.environ,
            {"LOCALAPPDATA": directory},
        ):
            local_root = pathlib.Path(directory)
            aidb_root = local_root / "AIDebugger"
            instances_root = aidb_root / "instances"
            instances_root.mkdir(parents=True)
            path = instances_root / "AIDebuggerVS_42.json"
            path.write_text("{}", encoding="utf-8")
            original_lstat = pathlib.Path.lstat

            for unsafe_path in (aidb_root, instances_root, path):
                def fake_lstat(candidate, *, _unsafe=unsafe_path):
                    metadata = original_lstat(candidate)
                    if candidate == _unsafe:
                        return SimpleNamespace(
                            st_mode=metadata.st_mode,
                            st_file_attributes=getattr(metadata, "st_file_attributes", 0) | 0x400,
                        )
                    return metadata

                with self.subTest(unsafe_path=unsafe_path), patch.object(
                    pathlib.Path,
                    "lstat",
                    side_effect=fake_lstat,
                    autospec=True,
                ):
                    with self.assertRaises(vs_bridge.VSBridgeError) as raised:
                        vs_bridge._validate_registration_security(path)
                    self.assertEqual("INVALID_TARGET", raised.exception.code)

    def test_registration_rejects_oversized_file_before_acl_read(self) -> None:
        with tempfile.TemporaryDirectory() as directory, patch.dict(
            os.environ,
            {"LOCALAPPDATA": directory},
        ):
            path = pathlib.Path(directory) / "AIDebugger" / "instances" / "AIDebuggerVS_42.json"
            path.parent.mkdir(parents=True)
            path.write_bytes(b"x" * (vs_bridge.MAX_INSTANCE_REGISTRATION_SIZE + 1))
            with self.assertRaises(vs_bridge.VSBridgeError) as raised:
                vs_bridge._validate_registration_security(path)
            self.assertEqual("INVALID_TARGET", raised.exception.code)


class VSBridgeSelectionTests(unittest.IsolatedAsyncioTestCase):
    @staticmethod
    async def wait_until(predicate, timeout: float = 0.5) -> bool:
        deadline = asyncio.get_running_loop().time() + timeout
        while asyncio.get_running_loop().time() < deadline:
            if predicate():
                return True
            await asyncio.sleep(0.01)
        return bool(predicate())

    async def test_exact_call_advances_selected_epoch_on_authenticated_error_without_replay(self) -> None:
        """A failed exact call advances the pinned epoch but is never replayed.

        The deleted combined bridge performed this advance inside
        ``_call_vs_exact_and_advance``; the VS-only bridge now owns the same
        behavior in ``VSBridge.call_pipe_exact``/``_advance_selected_target``.
        """
        bridge = vs_bridge.VSBridge()
        registration = self.registration("AIDebuggerVS_42")
        target = dict(registration, debugSessionGeneration=3, breakSequence=5, contextEpoch=7)
        current = dict(target, debugSessionGeneration=4, breakSequence=1, contextEpoch=2)
        bridge._selected_pipe = target["pipeName"]
        bridge._selected_target = dict(target)
        bridge._selected_registration = dict(registration)
        error = vs_bridge.VSBridgeError("session changed", code="STALE_DEBUG_SESSION")
        error.current_target_identity = current
        error.original_target_identity = dict(target)
        error.target_identity = current
        transport = AsyncMock(side_effect=error)

        try:
            with patch.object(bridge, "call_pipe", transport):
                with self.assertRaises(vs_bridge.VSBridgeError):
                    await bridge.call_pipe_exact(
                        target,
                        "get_debug_status",
                        registration_snapshot=registration,
                    )
        finally:
            bridge._executor.shutdown(wait=True)

        transport.assert_awaited_once()
        self.assertEqual(current, bridge._selected_target)

    async def test_direct_vs_discovery_consumes_the_single_call_deadline(self) -> None:
        bridge = vs_bridge.VSBridge()

        def slow_discovery(probe_timeout=0.5, discovery_deadline=None):
            time.sleep(0.15)
            return [vs_bridge.VSInstance("AIDebuggerVS_42", {
                "processId": 42,
                "connectionStatus": "ok",
                "solutionName": "test",
                "solutionPath": "test.sln",
                "isDebugging": False,
                "isPaused": False,
                "debugMode": "design",
            })]

        try:
            with patch.object(bridge, "_sync_discover_instances", side_effect=slow_discovery), \
                 patch.object(bridge, "call_pipe", AsyncMock()) as transport:
                with self.assertRaises(vs_bridge.VSBridgeError) as raised:
                    await bridge.call("get_debug_status", timeout=0.2)
            self.assertEqual("DEADLINE_EXCEEDED_NOT_STARTED", raised.exception.code)
            transport.assert_not_awaited()
        finally:
            bridge._executor.shutdown(wait=True)

    async def test_target_preflight_cannot_refill_the_call_deadline(self) -> None:
        bridge = vs_bridge.VSBridge()
        pipe_name = "AIDebuggerVS_42"
        calls = []

        def slow_sync_call(*args, **kwargs):
            calls.append(args[1])
            time.sleep(0.15)
            return {"processId": 42}

        registration = {
            "pipeName": pipe_name,
            "processId": 42,
            "processStartTimeUtcTicks": 123456,
            "serverInstanceId": "instance",
        }
        with vs_bridge._INSTANCE_STATE_LOCK:
            vs_bridge._INSTANCE_STATE.pop((pipe_name, "instance"), None)

        try:
            with patch.object(vs_bridge, "_load_instance_registration", return_value=registration), \
                 patch.object(bridge, "_sync_call", side_effect=slow_sync_call):
                with self.assertRaises(vs_bridge.VSBridgeError) as raised:
                    await bridge.call_pipe(pipe_name, "continue", timeout=0.2)

            self.assertEqual("DEADLINE_EXCEEDED_NOT_STARTED", raised.exception.code)
            self.assertFalse(raised.exception.outcome_unknown)
            self.assertEqual(["get_instance_info"], calls)
        finally:
            with vs_bridge._INSTANCE_STATE_LOCK:
                vs_bridge._INSTANCE_STATE.pop((pipe_name, "instance"), None)
            bridge._executor.shutdown(wait=True)

    @staticmethod
    def registration(pipe_name: str) -> dict:
        return {
            "pipeName": pipe_name,
            "processId": 42,
            "processStartTimeUtcTicks": 123456,
            "serverInstanceId": "instance",
        }

    async def assert_selected_failure_is_not_retried(
        self,
        method: str,
        expected_outcome_unknown: bool,
    ) -> None:
        """A pinned-target failure must neither be replayed nor redirected.

        The VS-only bridge preserves the transport error code verbatim (the old
        combined bridge rewrote it to OUTCOME_UNKNOWN/STALE_TARGET, which no
        longer exists), but the safety contract is unchanged: exactly one
        dispatch, no discovery fallback, and the pinned selection left intact.
        """
        bridge = vs_bridge.VSBridge()
        registration = self.registration("AIDebuggerVS_42")
        bridge._selected_pipe = registration["pipeName"]
        bridge._selected_target = dict(registration)
        bridge._selected_registration = dict(registration)

        transport_error = vs_bridge.VSBridgeError(
            "Pipe disconnected by server",
            code="PIPE_DISCONNECTED",
            outcome_unknown=expected_outcome_unknown,
        )
        call_pipe_exact = AsyncMock(side_effect=transport_error)
        discover = AsyncMock()
        try:
            with patch.object(bridge, "call_pipe_exact", call_pipe_exact), patch.object(
                bridge, "discover_instances", discover
            ):
                with self.assertRaises(vs_bridge.VSBridgeError) as raised:
                    await bridge.call(method)

            self.assertEqual("PIPE_DISCONNECTED", raised.exception.code)
            self.assertEqual(expected_outcome_unknown, raised.exception.outcome_unknown)
            self.assertEqual("AIDebuggerVS_42", bridge._selected_pipe)
            self.assertEqual(registration, bridge._selected_target)
            self.assertEqual(registration, bridge._selected_registration)
            call_pipe_exact.assert_awaited_once()
            discover.assert_not_awaited()
        finally:
            bridge._executor.shutdown(wait=True)

    async def test_command_transport_failure_is_not_retried_and_flags_unknown_outcome(self) -> None:
        self.assertEqual("command", vs_tool_registry.backend_method_kind("continue"))
        await self.assert_selected_failure_is_not_retried("continue", True)

    async def test_process_started_unknown_outcome_preserves_recovery_evidence(self) -> None:
        bridge = vs_bridge.VSBridge()
        pipe_name = "AIDebuggerVS_42"
        registration = self.registration(pipe_name)
        recovery = {
            "success": False,
            "commandIssued": True,
            "processStarted": True,
            "processId": 4321,
            "processStartTimeUtcTicks": 638000000000000000,
            "errorCode": "OUTCOME_UNKNOWN",
            "outcome": "process_started_attach_outcome_unknown",
        }
        with vs_bridge._INSTANCE_STATE_LOCK:
            vs_bridge._INSTANCE_STATE[(pipe_name, "instance")] = {
                "debugSessionGeneration": 7,
                "breakSequence": 8,
                "contextEpoch": 9,
            }
        try:
            with patch.object(vs_bridge, "_load_instance_registration", return_value=registration), \
                 patch.object(bridge, "_sync_call", return_value=recovery):
                with self.assertRaises(vs_bridge.VSRemoteError) as raised:
                    await bridge.call_pipe(pipe_name, "start_debugging")
            self.assertEqual("OUTCOME_UNKNOWN", raised.exception.code)
            self.assertTrue(raised.exception.outcome_unknown)
            self.assertEqual(4321, raised.exception.details["processId"])
            self.assertEqual(
                638000000000000000,
                raised.exception.details["processStartTimeUtcTicks"],
            )
            self.assertTrue(raised.exception.details["processStarted"])
            self.assertIsNotNone(raised.exception.request_id)
        finally:
            with vs_bridge._INSTANCE_STATE_LOCK:
                vs_bridge._INSTANCE_STATE.pop((pipe_name, "instance"), None)
            bridge._executor.shutdown(wait=True)

    async def test_query_transport_failure_is_not_retried_or_redirected(self) -> None:
        self.assertEqual("query", vs_tool_registry.backend_method_kind("get_debug_status"))
        await self.assert_selected_failure_is_not_retried("get_debug_status", False)

    async def test_backend_typed_error_is_preserved_without_clearing_target(self) -> None:
        bridge = vs_bridge.VSBridge()
        registration = self.registration("AIDebuggerVS_42")
        bridge._selected_pipe = registration["pipeName"]
        bridge._selected_target = dict(registration)
        bridge._selected_registration = dict(registration)
        backend_error = vs_bridge.VSBridgeError("Debugger must be paused", code="NOT_PAUSED")

        try:
            with patch.object(bridge, "call_pipe_exact", AsyncMock(side_effect=backend_error)):
                with self.assertRaises(vs_bridge.VSBridgeError) as raised:
                    await bridge.call("get_locals")
        finally:
            bridge._executor.shutdown(wait=True)

        self.assertEqual("NOT_PAUSED", raised.exception.code)
        self.assertEqual("AIDebuggerVS_42", bridge._selected_pipe)
        self.assertEqual(registration, bridge._selected_target)
        self.assertEqual(registration, bridge._selected_registration)

    def test_response_status_must_match_exactly_one_result_or_error(self) -> None:
        request_id = "7e93aa46-d9e4-4a79-8781-71e77e936fec"
        server = {
            "processId": 42,
            "processStartTimeUtcTicks": 123456,
            "serverInstanceId": "instance",
            "protocolCatalogHash": vs_tool_registry.DEBUG_METHOD_CATALOG_HASH,
            "debugSessionGeneration": 7,
            "breakSequence": 11,
            "contextEpoch": 13,
        }
        invalid_error_without_error = json.dumps({
            "protocol": vs_bridge.PROTOCOL_VERSION,
            "id": request_id,
            "status": "error",
            "server": server,
            "result": {"isPaused": True},
        }).encode("utf-8")
        ok_with_error = json.dumps({
            "protocol": vs_bridge.PROTOCOL_VERSION,
            "id": request_id,
            "status": "ok",
            "server": server,
            "error": {"code": "BROKEN", "message": "broken"},
        }).encode("utf-8")

        for payload in (invalid_error_without_error, ok_with_error):
            with self.subTest(payload=payload), self.assertRaises(vs_bridge.VSBridgeError) as raised:
                vs_bridge._decode_response(payload, request_id)
            self.assertEqual("INVALID_RESPONSE", raised.exception.code)

    async def test_pinned_call_deducts_selection_lock_wait_from_deadline(self) -> None:
        bridge = vs_bridge.VSBridge()
        registration = self.registration("AIDebuggerVS_42")
        bridge._selected_pipe = registration["pipeName"]
        bridge._selected_target = dict(registration)
        bridge._selected_registration = dict(registration)
        transport = AsyncMock(return_value={"isPaused": True})

        try:
            await bridge._lock.acquire()
            try:
                with patch.object(bridge, "call_pipe_exact", transport):
                    task = asyncio.create_task(bridge.call("get_debug_status", timeout=0.4))
                    await asyncio.sleep(0.15)
                    bridge._lock.release()
                    self.assertEqual({"isPaused": True}, await task)
            finally:
                if bridge._lock.locked():
                    bridge._lock.release()
        finally:
            bridge._executor.shutdown(wait=True)

        forwarded_timeout = transport.await_args.args[3]
        self.assertGreater(forwarded_timeout, 0.1)
        self.assertLess(forwarded_timeout, 0.35)

    async def test_exact_call_charges_registration_binding_to_deadline(self) -> None:
        for binding_seconds in (0.15, 0.5):
            with self.subTest(binding_seconds=binding_seconds):
                bridge = vs_bridge.VSBridge()
                registration = self.registration("AIDebuggerVS_42")
                target = dict(registration)
                now = [100.0]
                transport = AsyncMock(return_value=({"isPaused": True}, target))

                def bind(*args, **kwargs):
                    now[0] += binding_seconds
                    return registration, target

                try:
                    with patch.object(vs_bridge, "time", SimpleNamespace(monotonic=lambda: now[0])), \
                         patch.object(vs_bridge, "_bind_registration_snapshot", side_effect=bind), \
                         patch.object(bridge, "call_pipe", transport):
                        if binding_seconds < 0.4:
                            self.assertEqual(
                                {"isPaused": True},
                                await bridge.call_pipe_exact(target, "get_debug_status", timeout=0.4),
                            )
                            self.assertAlmostEqual(0.25, transport.await_args.args[3])
                        else:
                            with self.assertRaises(vs_bridge.VSBridgeError) as raised:
                                await bridge.call_pipe_exact(target, "continue", timeout=0.4)
                            self.assertEqual("DEADLINE_EXCEEDED_NOT_STARTED", raised.exception.code)
                            self.assertFalse(raised.exception.outcome_unknown)
                            transport.assert_not_awaited()
                finally:
                    bridge._executor.shutdown(wait=True)
                    bridge._control_executor.shutdown(wait=True)
                    bridge._recovery_executor.shutdown(wait=True)

    async def test_exact_pin_rejects_registration_replacement_before_transport(self) -> None:
        bridge = vs_bridge.VSBridge()
        target = self.registration("AIDebuggerVS_42")
        replacement = dict(target, serverInstanceId="replacement")
        transport = Mock()
        try:
            with patch.object(vs_bridge, "_load_instance_registration", return_value=replacement), \
                 patch.object(bridge, "_sync_call", transport):
                with self.assertRaises(vs_bridge.VSBridgeError) as raised:
                    await bridge.call_pipe_exact(target, "get_debug_status")
            self.assertEqual("STALE_TARGET", raised.exception.code)
            self.assertEqual(target, raised.exception.target_identity)
            transport.assert_not_called()
        finally:
            bridge._executor.shutdown(wait=True)

    async def test_session_refresh_query_advances_selected_debug_epoch(self) -> None:
        bridge = vs_bridge.VSBridge()
        registration = self.registration("AIDebuggerVS_42")
        target = dict(
            registration,
            debugSessionGeneration=7,
            breakSequence=8,
            contextEpoch=9,
        )
        response_target = dict(
            target,
            debugSessionGeneration=8,
            breakSequence=0,
            contextEpoch=10,
        )
        bridge._selected_pipe = target["pipeName"]
        bridge._selected_target = dict(target)
        bridge._selected_registration = dict(registration)
        try:
            with patch.object(
                bridge,
                "call_pipe",
                AsyncMock(return_value=({"isPaused": False}, response_target)),
            ):
                result = await bridge.call_pipe_exact(
                    target,
                    "get_debug_status",
                    registration_snapshot=registration,
                )
            self.assertEqual({"isPaused": False}, result)
            self.assertEqual(response_target, bridge._selected_target)
        finally:
            bridge._executor.shutdown(wait=True)

    async def test_exact_call_can_skip_advancing_the_selected_target(self) -> None:
        """Multi-target dispatch must not rewrite the pinned single-instance target."""
        bridge = vs_bridge.VSBridge()
        registration = self.registration("AIDebuggerVS_42")
        target = dict(
            registration,
            debugSessionGeneration=7,
            breakSequence=8,
            contextEpoch=9,
        )
        response_target = dict(
            target,
            debugSessionGeneration=8,
            breakSequence=0,
            contextEpoch=10,
        )
        bridge._selected_pipe = target["pipeName"]
        bridge._selected_target = dict(target)
        bridge._selected_registration = dict(registration)
        try:
            with patch.object(
                bridge,
                "call_pipe",
                AsyncMock(return_value=({"isPaused": False}, response_target)),
            ):
                result, returned_target = await bridge.call_pipe_exact(
                    target,
                    "get_debug_status",
                    registration_snapshot=registration,
                    include_target=True,
                    advance_selection=False,
                )
            self.assertEqual({"isPaused": False}, result)
            # The caller still learns the new epoch from the return value...
            self.assertEqual(response_target, returned_target)
            # ...but the pinned target a later bare call resolves to is untouched.
            self.assertEqual(target, bridge._selected_target)
        finally:
            bridge._executor.shutdown(wait=True)

    async def test_non_refresh_query_rejects_changed_debug_epoch(self) -> None:
        bridge = vs_bridge.VSBridge()
        registration = self.registration("AIDebuggerVS_42")
        target = dict(
            registration,
            debugSessionGeneration=7,
            breakSequence=8,
            contextEpoch=9,
        )
        response_target = dict(target, breakSequence=9, contextEpoch=10)
        try:
            with patch.object(
                bridge,
                "call_pipe",
                AsyncMock(return_value=({"threads": []}, response_target)),
            ):
                with self.assertRaises(vs_bridge.VSBridgeError) as raised:
                    await bridge.call_pipe_exact(
                        target,
                        "get_threads",
                        registration_snapshot=registration,
                    )
            self.assertEqual("STALE_DEBUG_CONTEXT", raised.exception.code)
        finally:
            bridge._executor.shutdown(wait=True)

    async def test_selected_target_advances_after_session_refresh_via_call(self) -> None:
        bridge = vs_bridge.VSBridge()
        registration = self.registration("AIDebuggerVS_42")
        target = dict(
            registration,
            debugSessionGeneration=7,
            breakSequence=8,
            contextEpoch=9,
        )
        response_target = dict(
            target,
            debugSessionGeneration=8,
            breakSequence=0,
            contextEpoch=10,
        )
        bridge._selected_pipe = target["pipeName"]
        bridge._selected_target = dict(target)
        bridge._selected_registration = dict(registration)
        try:
            with patch.object(
                bridge,
                "call_pipe",
                AsyncMock(return_value=({"isPaused": False}, response_target)),
            ):
                result = await bridge.call("get_debug_status")
        finally:
            bridge._executor.shutdown(wait=True)
        self.assertEqual({"isPaused": False}, result)
        self.assertEqual(response_target, bridge._selected_target)

    async def test_failed_repin_keeps_previous_immutable_selection(self) -> None:
        bridge = vs_bridge.VSBridge()
        previous = self.registration("AIDebuggerVS_42")
        bridge._selected_pipe = previous["pipeName"]
        bridge._selected_target = dict(previous)
        replacement = self.registration("AIDebuggerVS_43")
        try:
            with patch.object(vs_bridge, "_load_instance_registration", return_value=replacement):
                with self.assertRaises(vs_bridge.VSBridgeError) as raised:
                    bridge.select_instance(
                        pipe_name=replacement["pipeName"],
                        target_identity=dict(replacement, serverInstanceId="stale"),
                    )
            self.assertEqual("STALE_TARGET", raised.exception.code)
            self.assertEqual(previous["pipeName"], bridge._selected_pipe)
            self.assertEqual(previous, bridge._selected_target)
        finally:
            bridge._executor.shutdown(wait=True)

    async def test_async_command_guard_timeout_preserves_request_id(self) -> None:
        bridge = vs_bridge.VSBridge()
        captured = {"calls": []}
        pipe_name = "AIDebuggerVS_42"
        with vs_bridge._INSTANCE_STATE_LOCK:
            vs_bridge._INSTANCE_STATE[(pipe_name, "instance")] = {"debugSessionGeneration": 7}

        def slow_call(
            pipe_name, method, params, timeout, request_id, deadline_monotonic, deadline_unix_ms,
            include_server=False, expected_target=None, registration_snapshot=None,
        ):
            captured["calls"].append((method, request_id, params))
            time.sleep(0.1)
            return None

        try:
            with patch.object(vs_bridge, "_load_instance_registration", side_effect=self.registration), \
                 patch.object(bridge, "_compute_timeouts", return_value=(0.01, 0.01, 0.01)), \
                 patch.object(bridge, "_sync_call", side_effect=slow_call):
                with self.assertRaises(vs_bridge.VSBridgeError) as raised:
                    await bridge.call_pipe(pipe_name, "continue")
                self.assertEqual("PIPE_TIMEOUT", raised.exception.code)
                self.assertTrue(raised.exception.outcome_unknown)
                original_request_id = captured["calls"][0][1]
                self.assertEqual(original_request_id, raised.exception.request_id)
                self.assertIsNotNone(original_request_id)
                self.assertTrue(await self.wait_until(lambda: any(
                    method == "cancel_request" and params == {"requestId": original_request_id}
                    for method, _, params in captured["calls"]
                )))
        finally:
            with vs_bridge._INSTANCE_STATE_LOCK:
                vs_bridge._INSTANCE_STATE.pop((pipe_name, "instance"), None)
            bridge._executor.shutdown(wait=True)

    async def test_task_cancellation_sends_remote_cancel_request(self) -> None:
        bridge = vs_bridge.VSBridge()
        pipe_name = "AIDebuggerVS_43"
        calls = []
        entered = threading.Event()
        release = threading.Event()
        with vs_bridge._INSTANCE_STATE_LOCK:
            vs_bridge._INSTANCE_STATE[(pipe_name, "instance")] = {"debugSessionGeneration": 7}

        def slow_call(
            selected_pipe, method, params, timeout, request_id, deadline_monotonic, deadline_unix_ms,
            include_server=False, expected_target=None, registration_snapshot=None,
        ):
            calls.append((method, request_id, params))
            if method != "cancel_request":
                entered.set()
                # Block until the test cancels; do not rely on wall-clock races.
                release.wait(timeout=5.0)
            return {"state": "cancellation_requested"}

        try:
            with patch.object(vs_bridge, "_load_instance_registration", side_effect=self.registration), \
                 patch.object(bridge, "_sync_call", side_effect=slow_call):
                task = asyncio.create_task(bridge.call_pipe(pipe_name, "get_modules", timeout=5.0))
                self.assertTrue(
                    await self.wait_until(entered.is_set, timeout=5.0),
                    "sync call did not start",
                )
                task.cancel()
                with self.assertRaises(asyncio.CancelledError):
                    await task
                release.set()

            original_request_id = calls[0][1]
            self.assertTrue(await self.wait_until(lambda: any(
                method == "cancel_request" and params == {"requestId": original_request_id}
                for method, _, params in calls
            )))
        finally:
            release.set()
            with vs_bridge._INSTANCE_STATE_LOCK:
                vs_bridge._INSTANCE_STATE.pop((pipe_name, "instance"), None)
            bridge._executor.shutdown(wait=True)

    async def test_sync_pipe_timeout_sends_remote_cancel_request(self) -> None:
        bridge = vs_bridge.VSBridge()
        pipe_name = "AIDebuggerVS_44"
        calls = []
        with vs_bridge._INSTANCE_STATE_LOCK:
            vs_bridge._INSTANCE_STATE[(pipe_name, "instance")] = {"debugSessionGeneration": 7}

        def timeout_call(
            selected_pipe, method, params, timeout, request_id, deadline_monotonic, deadline_unix_ms,
            include_server=False, expected_target=None, registration_snapshot=None,
        ):
            calls.append((method, request_id, params))
            if method == "cancel_request":
                return {"state": "cancellation_requested"}
            raise vs_bridge.VSBridgeError(
                "read deadline expired", code="PIPE_TIMEOUT", outcome_unknown=True
            )

        try:
            with patch.object(vs_bridge, "_load_instance_registration", side_effect=self.registration), \
                 patch.object(bridge, "_sync_call", side_effect=timeout_call):
                with self.assertRaises(vs_bridge.VSBridgeError) as raised:
                    await bridge.call_pipe(pipe_name, "continue", timeout=1.0)
                original_request_id = calls[0][1]
                self.assertEqual(original_request_id, raised.exception.request_id)
                self.assertTrue(await self.wait_until(lambda: any(
                    method == "cancel_request" and params == {"requestId": original_request_id}
                    for method, _, params in calls
                )))
        finally:
            with vs_bridge._INSTANCE_STATE_LOCK:
                vs_bridge._INSTANCE_STATE.pop((pipe_name, "instance"), None)
            bridge._executor.shutdown(wait=True)

    async def test_first_session_bound_call_preflights_instance_identity(self) -> None:
        bridge = vs_bridge.VSBridge()
        pipe_name = "AIDebuggerVS_31415"
        calls = []

        def fake_call(
            selected_pipe, method, params, timeout, request_id, deadline_monotonic, deadline_unix_ms,
            include_server=False, expected_target=None, registration_snapshot=None,
        ):
            calls.append(method)
            if method == "get_instance_info":
                with vs_bridge._INSTANCE_STATE_LOCK:
                    vs_bridge._INSTANCE_STATE[(selected_pipe, "instance")] = {"debugSessionGeneration": 11}
                return {"debugSessionGeneration": 11}
            return {"variables": []}

        try:
            with vs_bridge._INSTANCE_STATE_LOCK:
                vs_bridge._INSTANCE_STATE.pop((pipe_name, "instance"), None)
            with patch.object(vs_bridge, "_load_instance_registration", side_effect=self.registration), \
                 patch.object(bridge, "_sync_call", side_effect=fake_call):
                result = await bridge.call_pipe(pipe_name, "get_locals")
            self.assertEqual(["get_instance_info", "get_locals"], calls)
            self.assertEqual({"variables": []}, result)
        finally:
            with vs_bridge._INSTANCE_STATE_LOCK:
                vs_bridge._INSTANCE_STATE.pop((pipe_name, "instance"), None)
            bridge._executor.shutdown(wait=True)

    async def test_call_pipe_with_target_returns_response_identity_without_discovery(self) -> None:
        bridge = vs_bridge.VSBridge()
        pipe_name = "AIDebuggerVS_2718"
        server = {
            "processId": 42,
            "processStartTimeUtcTicks": 123456,
            "serverInstanceId": "instance",
            "debugSessionGeneration": 7,
            "breakSequence": 11,
            "contextEpoch": 13,
        }
        with vs_bridge._INSTANCE_STATE_LOCK:
            vs_bridge._INSTANCE_STATE[(pipe_name, "instance")] = dict(server)

        def fake_call(*args):
            self.assertTrue(args[-3])
            expected_identity = {
                "pipeName": pipe_name,
                "processId": 42,
                "processStartTimeUtcTicks": 123456,
                "serverInstanceId": "instance",
            }
            self.assertEqual(expected_identity, args[-2])
            self.assertEqual(
                {
                    "pipeName": pipe_name,
                    "processId": 42,
                    "processStartTimeUtcTicks": 123456,
                    "serverInstanceId": "instance",
                },
                {key: args[-1][key] for key in expected_identity},
            )
            return {"isPaused": True}, dict(server)

        try:
            with patch.object(vs_bridge, "_load_instance_registration", side_effect=self.registration), \
                 patch.object(bridge, "_sync_call", side_effect=fake_call):
                result, target = await bridge.call_pipe(
                    pipe_name,
                    "get_debug_status",
                    include_target=True,
                )
            self.assertEqual({"isPaused": True}, result)
            self.assertEqual(13, target["contextEpoch"])
            self.assertEqual(pipe_name, target["pipeName"])
        finally:
            with vs_bridge._INSTANCE_STATE_LOCK:
                vs_bridge._INSTANCE_STATE.pop((pipe_name, "instance"), None)
            bridge._executor.shutdown(wait=True)

    async def test_call_pipe_with_target_uses_one_registration_snapshot_across_executor_handoff(self) -> None:
        bridge = vs_bridge.VSBridge()
        pipe_name = "AIDebuggerVS_42"
        original = dict(
            self.registration(pipe_name),
            protocol=vs_bridge.PROTOCOL_VERSION,
            authToken="token-a",
            capabilities=["server.control", "debug.read"],
        )
        captured = {}
        server = dict(original, debugSessionGeneration=7, breakSequence=8, contextEpoch=9)

        def fake_call(*args):
            captured["expectedTarget"] = args[-2]
            captured["registrationSnapshot"] = args[-1]
            return {"isPaused": True}, server

        with vs_bridge._INSTANCE_STATE_LOCK:
            vs_bridge._INSTANCE_STATE[(pipe_name, "instance")] = {
                "serverInstanceId": original["serverInstanceId"],
                "debugSessionGeneration": 7,
            }
        try:
            with patch.object(vs_bridge, "_load_instance_registration", return_value=original) as load, \
                 patch.object(bridge, "_sync_call", side_effect=fake_call):
                result, target = await bridge.call_pipe(
                    pipe_name,
                    "get_debug_status",
                    include_target=True,
                )
            self.assertEqual({"isPaused": True}, result)
            self.assertEqual(9, target["contextEpoch"])
            self.assertIsInstance(target, vs_bridge.BoundVSTarget)
            self.assertNotIn("authToken", target)
            self.assertEqual(vs_bridge._copy_registration_snapshot(original), target.registration_snapshot())
            load.assert_called_once_with(pipe_name)
            expected_snapshot = vs_bridge._copy_registration_snapshot(original)
            self.assertEqual(expected_snapshot, captured["registrationSnapshot"])
            self.assertEqual(
                {key: original[key] for key in (
                    "pipeName", "processId", "processStartTimeUtcTicks", "serverInstanceId"
                )},
                captured["expectedTarget"],
            )
        finally:
            with vs_bridge._INSTANCE_STATE_LOCK:
                vs_bridge._INSTANCE_STATE.pop((pipe_name, "instance"), None)
            bridge._executor.shutdown(wait=True)

    async def test_bound_target_survives_global_reselection_without_registration_reread(self) -> None:
        bridge = vs_bridge.VSBridge()
        pipe_name = "AIDebuggerVS_42"
        original = dict(
            self.registration(pipe_name),
            protocol=vs_bridge.PROTOCOL_VERSION,
            authToken="token-a",
            capabilities=["server.control", "debug.read"],
        )
        identity = {
            key: original[key]
            for key in (
                "pipeName",
                "processId",
                "processStartTimeUtcTicks",
                "serverInstanceId",
            )
        }
        bound = vs_bridge.BoundVSTarget(
            dict(identity, debugSessionGeneration=7, breakSequence=8, contextEpoch=9),
            original,
        )
        bridge._selected_target = dict(identity, serverInstanceId="other")
        bridge._selected_registration = dict(original, serverInstanceId="other", authToken="token-b")
        captured = {}

        def transport(*args):
            captured["registrationSnapshot"] = args[-1]
            server = dict(identity, debugSessionGeneration=7, breakSequence=8, contextEpoch=9)
            return {"isPaused": True}, server

        with vs_bridge._INSTANCE_STATE_LOCK:
            vs_bridge._INSTANCE_STATE[(pipe_name, "instance")] = {
                "debugSessionGeneration": 7,
                "breakSequence": 8,
                "contextEpoch": 9,
            }
        try:
            with patch.object(
                vs_bridge,
                "_load_instance_registration",
                side_effect=AssertionError("bound exact call must not reread registration"),
            ), patch.object(bridge, "_sync_call", side_effect=transport):
                result = await bridge.call_pipe_exact(bound, "get_debug_status")
            self.assertEqual({"isPaused": True}, result)
            self.assertEqual(vs_bridge._copy_registration_snapshot(original), captured["registrationSnapshot"])
            instance_target = vs_bridge.instance_target(bound)
            self.assertIsInstance(instance_target, vs_bridge.BoundVSTarget)
            self.assertNotIn("authToken", instance_target)
            self.assertNotIn("debugSessionGeneration", instance_target)
            self.assertEqual(
                vs_bridge._copy_registration_snapshot(original),
                instance_target.registration_snapshot(),
            )
        finally:
            with vs_bridge._INSTANCE_STATE_LOCK:
                vs_bridge._INSTANCE_STATE.pop((pipe_name, "instance"), None)
            bridge._executor.shutdown(wait=True)

    async def test_discovery_uses_the_registration_that_was_authenticated_for_the_probe(self) -> None:
        bridge = vs_bridge.VSBridge()
        pipe_name = "AIDebuggerVS_42"
        registration = dict(
            self.registration(pipe_name),
            protocol=vs_bridge.PROTOCOL_VERSION,
            authToken="token-a",
            capabilities=["server.control", "debug.read"],
        )
        captured = {}

        def probe(*args, **kwargs):
            captured["registrationSnapshot"] = kwargs.get("registration_snapshot")
            return {
                "processId": 42,
                "solutionName": "Sample",
                "isDebugging": False,
                "isPaused": False,
                "debugMode": "design",
            }

        try:
            with patch.object(vs_bridge, "_discover_vs_pipes", return_value=[pipe_name]), \
                 patch.object(vs_bridge, "_load_instance_registration", return_value=registration) as load, \
                 patch.object(
                     vs_bridge,
                     "_trusted_visual_studio_paths",
                     return_value=frozenset({r"C:\trusted\Common7\IDE\devenv.exe"}),
                 ), \
                 patch.object(vs_bridge, "_sync_call_to_pipe", side_effect=probe):
                instances = bridge._sync_discover_instances()
            self.assertEqual(1, len(instances))
            self.assertEqual(registration, captured["registrationSnapshot"])
            self.assertEqual(sorted(registration["capabilities"]), instances[0].allowed_capabilities)
            self.assertEqual(registration["serverInstanceId"], instances[0].server_instance_id)
            load.assert_called_once_with(pipe_name)
        finally:
            bridge._executor.shutdown(wait=True)

    async def test_vsbridge_auto_selection_uses_discovery_registration_without_reread(self) -> None:
        bridge = vs_bridge.VSBridge()
        pipe_name = "AIDebuggerVS_42"
        original = dict(
            self.registration(pipe_name),
            protocol=vs_bridge.PROTOCOL_VERSION,
            authToken="token-a",
            capabilities=["server.control", "debug.read"],
        )
        discovered = vs_bridge.VSInstance(pipe_name, {
            "processId": 42,
            "processStartTimeUtcTicks": original["processStartTimeUtcTicks"],
            "serverInstanceId": original["serverInstanceId"],
            "connectionStatus": "ok",
            "isPaused": True,
            "isDebugging": True,
        }, registration_snapshot=original)
        replacement = dict(
            original,
            authToken="token-b",
            capabilities=["server.control", "debug.execution.control"],
        )
        captured = {}

        def transport(*args):
            captured["registrationSnapshot"] = args[-1]
            return {
                "isPaused": True
            }, dict(
                discovered.target_identity(),
                debugSessionGeneration=7,
                breakSequence=0,
                contextEpoch=0,
            )

        with vs_bridge._INSTANCE_STATE_LOCK:
            vs_bridge._INSTANCE_STATE[(pipe_name, "instance")] = {"debugSessionGeneration": 7}
        try:
            with patch.object(bridge, "_sync_discover_instances", return_value=[discovered]) as discover, \
                 patch.object(vs_bridge, "_load_instance_registration", return_value=replacement) as load, \
                 patch.object(bridge, "_sync_call", side_effect=transport):
                result = await bridge.call("get_debug_status")
                second = await bridge.call("get_debug_status")
            self.assertEqual({"isPaused": True}, result)
            self.assertEqual({"isPaused": True}, second)
            load.assert_not_called()
            discover.assert_called_once()
            self.assertEqual(vs_bridge._copy_registration_snapshot(original), captured["registrationSnapshot"])
            self.assertEqual(
                dict(
                    self.registration(pipe_name),
                    debugSessionGeneration=7,
                    breakSequence=0,
                    contextEpoch=0,
                ),
                bridge._selected_target,
            )
        finally:
            with vs_bridge._INSTANCE_STATE_LOCK:
                vs_bridge._INSTANCE_STATE.pop((pipe_name, "instance"), None)
            bridge._executor.shutdown(wait=True)

    async def test_auto_selection_reuses_discovery_registration_for_selection_and_call(self) -> None:
        bridge = vs_bridge.VSBridge()
        pipe_name = "AIDebuggerVS_42"
        registration = dict(
            self.registration(pipe_name),
            protocol=vs_bridge.PROTOCOL_VERSION,
            authToken="token-a",
            capabilities=["server.control", "debug.read"],
        )
        discovered = vs_bridge.VSInstance(pipe_name, {
            "processId": 42,
            "processStartTimeUtcTicks": registration["processStartTimeUtcTicks"],
            "serverInstanceId": registration["serverInstanceId"],
            "connectionStatus": "ok",
            "isPaused": True,
            "isDebugging": True,
        }, registration_snapshot=registration)
        call = AsyncMock(return_value={"isPaused": True})

        try:
            with patch.object(bridge, "discover_instances", AsyncMock(return_value=[discovered])) as discover, \
                 patch.object(bridge, "call_pipe_exact", call):
                result = await bridge.call("get_debug_status")
        finally:
            bridge._executor.shutdown(wait=True)

        self.assertEqual({"isPaused": True}, result)
        expected_snapshot = vs_bridge._copy_registration_snapshot(registration)
        self.assertEqual(expected_snapshot, call.await_args.kwargs["registration_snapshot"])
        self.assertEqual(expected_snapshot, bridge._selected_registration)
        # Auto-selection pins the immutable instance identity before dispatching.
        self.assertEqual(discovered.target_identity(), call.await_args.args[0])
        self.assertEqual(discovered.target_identity(), bridge._selected_target)
        discover.assert_awaited_once()

    async def test_concurrent_explicit_selection_wins_over_inflight_auto_selection(self) -> None:
        bridge = vs_bridge.VSBridge()
        entered_discovery = asyncio.Event()
        release_discovery = asyncio.Event()

        def discovered_instance(process_id: int, token: str) -> tuple[vs_bridge.VSInstance, dict]:
            pipe_name = f"AIDebuggerVS_{process_id}"
            registration = {
                "protocol": vs_bridge.PROTOCOL_VERSION,
                "pipeName": pipe_name,
                "processId": process_id,
                "processStartTimeUtcTicks": process_id * 1000,
                "serverInstanceId": f"server-{process_id}",
                "authToken": token,
                "capabilities": ["server.control", "debug.read"],
            }
            instance = vs_bridge.VSInstance(pipe_name, {
                "processId": process_id,
                "processStartTimeUtcTicks": registration["processStartTimeUtcTicks"],
                "serverInstanceId": registration["serverInstanceId"],
                "connectionStatus": "ok",
                "isPaused": True,
                "isDebugging": True,
            }, registration_snapshot=registration)
            return instance, registration

        automatic, _ = discovered_instance(41, "token-a")
        explicit, explicit_registration = discovered_instance(42, "token-b")

        async def discovery(*args, **kwargs):
            entered_discovery.set()
            await release_discovery.wait()
            return [automatic]

        transport = AsyncMock(return_value={"isPaused": True})
        try:
            with patch.object(bridge, "discover_instances", side_effect=discovery), \
                 patch.object(bridge, "call_pipe_exact", transport):
                request = asyncio.create_task(bridge.call("get_debug_status"))
                await asyncio.wait_for(entered_discovery.wait(), timeout=5.0)
                bridge.select_instance(
                    pipe_name=explicit.pipe_name,
                    target_identity=explicit.target_identity(),
                    registration_snapshot=explicit.registration_snapshot(),
                )
                release_discovery.set()
                result = await request

            expected_identity = vs_bridge._target_identity_from_registration(
                explicit.pipe_name, explicit_registration
            )
            self.assertEqual({"isPaused": True}, result)
            # The in-flight auto request is redirected to the explicit selection.
            self.assertEqual(expected_identity, transport.await_args.args[0])
            self.assertEqual(
                explicit.registration_snapshot(),
                transport.await_args.kwargs["registration_snapshot"],
            )
            ide, pipe_name, target, _ = await bridge.get_selection_snapshot()
            self.assertEqual("vs", ide)
            self.assertEqual(explicit.pipe_name, pipe_name)
            self.assertEqual(expected_identity, target)
        finally:
            release_discovery.set()
            bridge._executor.shutdown(wait=True)

    async def test_guard_timeout_returns_without_waiting_for_remote_cleanup(self) -> None:
        bridge = vs_bridge.VSBridge()
        pipe_name = "AIDebuggerVS_45"
        cleanup_started = asyncio.Event()
        release_cleanup = asyncio.Event()

        def slow_call(*args):
            time.sleep(0.1)
            return None

        async def blocked_cleanup(*args):
            cleanup_started.set()
            await release_cleanup.wait()

        with vs_bridge._INSTANCE_STATE_LOCK:
            vs_bridge._INSTANCE_STATE[(pipe_name, "instance")] = {"debugSessionGeneration": 7}
        try:
            with patch.object(vs_bridge, "_load_instance_registration", side_effect=self.registration), \
                 patch.object(bridge, "_compute_timeouts", return_value=(0.01, 0.01, 0.01)), \
                 patch.object(bridge, "_sync_call", side_effect=slow_call), \
                 patch.object(bridge, "_cancel_remote_request", side_effect=blocked_cleanup):
                started = time.monotonic()
                with self.assertRaises(vs_bridge.VSBridgeError):
                    await bridge.call_pipe(pipe_name, "continue")
                elapsed = time.monotonic() - started
                self.assertLess(elapsed, 0.1)
                await asyncio.wait_for(cleanup_started.wait(), timeout=0.2)
                self.assertEqual(1, len(bridge._cancellation_tasks))
                release_cleanup.set()
                self.assertTrue(await self.wait_until(lambda: not bridge._cancellation_tasks))
        finally:
            release_cleanup.set()
            with vs_bridge._INSTANCE_STATE_LOCK:
                vs_bridge._INSTANCE_STATE.pop((pipe_name, "instance"), None)
            bridge._executor.shutdown(wait=True)

    async def test_remote_cancel_uses_control_executor_when_all_data_workers_are_busy(self) -> None:
        bridge = vs_bridge.VSBridge()
        pipe_name = "AIDebuggerVS_46"
        release = threading.Event()
        started = threading.Barrier(bridge._MAX_WORKERS + 1)
        cancel_seen = threading.Event()
        registration = self.registration(pipe_name)
        with vs_bridge._INSTANCE_STATE_LOCK:
            vs_bridge._INSTANCE_STATE[(pipe_name, "instance")] = {"debugSessionGeneration": 7}

        def blocking_call(
            selected_pipe, method, params, timeout, request_id, deadline_monotonic, deadline_unix_ms,
            include_server=False, expected_target=None, registration_snapshot=None,
        ):
            if method == "cancel_request":
                cancel_seen.set()
                return {"state": "cancellation_requested"}
            started.wait(timeout=2.0)
            release.wait(timeout=2.0)
            return {"ok": True}

        tasks = []
        try:
            with patch.object(vs_bridge, "_load_instance_registration", return_value=registration), \
                 patch.object(bridge, "_sync_call", side_effect=blocking_call):
                tasks = [
                    asyncio.create_task(bridge.call_pipe(pipe_name, "get_modules", timeout=5.0))
                    for _ in range(bridge._MAX_WORKERS)
                ]
                await asyncio.to_thread(started.wait, 2.0)
                tasks[0].cancel()
                with self.assertRaises(asyncio.CancelledError):
                    await tasks[0]
                self.assertTrue(await self.wait_until(cancel_seen.is_set, timeout=0.5))
        finally:
            release.set()
            await asyncio.gather(*tasks, return_exceptions=True)
            with vs_bridge._INSTANCE_STATE_LOCK:
                vs_bridge._INSTANCE_STATE.pop((pipe_name, "instance"), None)
            bridge._executor.shutdown(wait=True)
            bridge._control_executor.shutdown(wait=True)


class VSSessionManagerTests(unittest.IsolatedAsyncioTestCase):
    """The thin VS-only session wrapper that replaced the combined IDEBridge."""

    def test_preferred_ide_is_always_vs(self) -> None:
        self.assertEqual("vs", vs_session.VSSessionManager.get_preferred_ide())

    async def test_non_vs_preference_is_rejected_before_dispatch(self) -> None:
        backend = Mock()
        backend.discover_instances = AsyncMock()
        manager = vs_session.VSSessionManager(bridge=backend)

        with self.assertRaises(vs_session.IDEBridgeError) as raised:
            await manager.discover_instances(preferred_ide="rider")
        self.assertEqual("INVALID_ARGUMENT", raised.exception.code)

        with self.assertRaises(vs_session.IDEBridgeError) as raised:
            manager.select_instance(ide="rider")
        self.assertEqual("INVALID_ARGUMENT", raised.exception.code)

        backend.discover_instances.assert_not_awaited()

    async def test_call_forwards_include_target_and_accepts_vs_preference(self) -> None:
        backend = Mock()
        backend.call = AsyncMock(return_value={"isPaused": True})
        manager = vs_session.VSSessionManager(bridge=backend)

        result = await manager.call(
            "get_debug_status",
            {"depth": 1},
            3.0,
            preferred_ide="vs",
            _include_target=True,
        )

        self.assertEqual({"isPaused": True}, result)
        backend.call.assert_awaited_once_with(
            "get_debug_status", {"depth": 1}, 3.0, include_target=True
        )

    async def test_discover_instances_defaults_to_bridge_timeout(self) -> None:
        backend = Mock()
        backend.discover_instances = AsyncMock(return_value=[])
        manager = vs_session.VSSessionManager(bridge=backend)

        self.assertEqual([], await manager.discover_instances(preferred_ide=None, timeout=None))
        backend.discover_instances.assert_awaited_once_with(timeout=vs_bridge.DEFAULT_TIMEOUT)


if __name__ == "__main__":
    unittest.main()
