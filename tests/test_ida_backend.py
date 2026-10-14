import ast
import hashlib
import http.client
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import time
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from ida_backend.sessions import IdaSessions, Session
from ida_backend.transport import IdaError, rpc, tool_value
from ida_backend.stdio import serve
from tools import install_aidebugger as installer

_TEST_TMP = ROOT / ".tmp"
_TEST_TMP.mkdir(parents=True, exist_ok=True)


class IdaTransportTests(unittest.TestCase):
    def test_malformed_tool_envelopes_are_transport_errors(self):
        for result in (None, [], "receipt", {"content": None}, {"content": ["receipt"]},
                       {"content": [{"type": "text"}]},
                       {"content": [{"type": "text", "text": None}]}):
            with self.subTest(result=result), self.assertRaisesRegex(IdaError, "IDA_TRANSPORT_ERROR"):
                tool_value(result)

    def test_valid_result_and_explicit_rpc_error_remain_distinct(self):
        for body in (b'{"id":"request","result":{}}',
                     b'{"id":"request","error":{"code":-32601}}'):
            connection = Mock()
            connection.getresponse.return_value.status = 200
            connection.getresponse.return_value.read.return_value = body
            with patch('ida_backend.transport.http.client.HTTPConnection', return_value=connection), \
                    patch('ida_backend.transport.uuid.uuid4', return_value=SimpleNamespace(hex='request')):
                if b'"error"' in body:
                    with self.assertRaisesRegex(IdaError, 'IDA_RPC_ERROR'):
                        rpc(14000, 'private', 'tools/call')
                else:
                    self.assertEqual({}, rpc(14000, 'private', 'tools/call'))

    def test_invalid_responses_are_typed_transport_errors(self):
        bodies = [b'{', b'[]', b'null', b'{"id":"request"}',
                  b'{"id":"request","result":{},"error":{}}']
        for body in bodies + [http.client.IncompleteRead(b'partial', 20)]:
            with self.subTest(body=body):
                connection = Mock()
                response = connection.getresponse.return_value
                response.status = 200
                if isinstance(body, Exception):
                    response.read.side_effect = body
                else:
                    response.read.return_value = body
                with patch('ida_backend.transport.http.client.HTTPConnection', return_value=connection), \
                        patch('ida_backend.transport.uuid.uuid4', return_value=SimpleNamespace(hex='request')):
                    with self.assertRaisesRegex(IdaError, 'IDA_TRANSPORT_ERROR'):
                        rpc(14000, 'private', 'tools/call', {'name': 'patch'})
                connection.request.assert_called_once()
                connection.close.assert_called_once()


class DebugContextTests(unittest.TestCase):
    def setUp(self):
        from ida_backend.debug_context import collect_context
        from functools import wraps
        self.collect = lambda debugger, api, **limits: collect_context(debugger, api, self.idd, self.names, **limits)
        self.debugger = Mock()
        self.debugger.get_current_thread.return_value = 17
        self.debugger.get_thread_qty.return_value = 3
        self.debugger.getn_thread.side_effect = lambda i: 17 + i
        self.trace = [SimpleNamespace(callea=0x1000), SimpleNamespace(callea=0x2000)]
        self.idd = SimpleNamespace(call_stack_t=lambda: list(self.trace), modinfo_t=lambda: SimpleNamespace(name="test.dll"))
        self.names = SimpleNamespace(get_nice_colored_name=Mock(return_value="frame"),
                                     GNCN_NOCOLOR=1, GNCN_NOLABEL=2, GNCN_NOSEG=4, GNCN_PREFDBG=8)
        self.debugger.collect_stack_trace.return_value = True
        self.debugger.get_module_info.return_value = True
        def forbidden_wrapper(function):
            @wraps(function)
            def wrapped():
                raise AssertionError("nested synchronization")
            return wrapped
        self.api = SimpleNamespace(dbg_ensure_suspended=Mock(),
            _get_debug_state_result=Mock(return_value={"suspended": True, "ip": "0x1000"}),
            dbg_regs=forbidden_wrapper(lambda: {"thread_id": 17, "registers": [{"name": "RIP", "value": "0x1000"}]}),
            dbg_stacktrace=forbidden_wrapper(lambda: [{"addr": "0x1000"}, {"addr": "0x2000"}]))

    def test_bundle_is_bounded_and_runs_without_nested_sync(self):
        value = self.collect(self.debugger, self.api, max_threads=2, max_frames=1)
        self.assertTrue(value["snapshotStable"])
        self.assertEqual(value["threads"], [17, 18])
        self.assertTrue(value["threadsTruncated"])
        self.assertTrue(value["stackTruncated"])
        self.assertEqual(len(value["stack"]), 1)
        self.api.dbg_ensure_suspended.assert_called_once_with()

    def test_changed_stop_and_missing_ip_are_unstable(self):
        for after in ({"suspended": True, "ip": "0x2000"}, {"running": True}, {"suspended": True}):
            self.api._get_debug_state_result.side_effect = [{"suspended": True, "ip": "0x1000"}, after]
            self.assertFalse(self.collect(self.debugger, self.api)["snapshotStable"])

    def test_running_debugger_cannot_return_cached_context(self):
        self.api.dbg_ensure_suspended.side_effect = RuntimeError("running")
        with self.assertRaisesRegex(RuntimeError, "running"):
            self.collect(self.debugger, self.api)
        self.debugger.get_current_thread.assert_not_called()

    def test_empty_stack_has_explicit_unavailable_status(self):
        self.debugger.collect_stack_trace.return_value = False
        self.assertEqual(self.collect(self.debugger, self.api)["stackStatus"], "unavailable")

    def test_deep_stack_symbol_resolution_stops_at_requested_bound(self):
        self.trace = [SimpleNamespace(callea=0x1000 + i) for i in range(10000)]
        value = self.collect(self.debugger, self.api, max_frames=2)
        self.assertEqual(len(value["stack"]), 2)
        self.assertTrue(value["stackTruncated"])
        self.assertEqual(self.names.get_nice_colored_name.call_count, 2)
        self.assertEqual(self.debugger.get_module_info.call_count, 2)
        self.debugger.collect_stack_trace.assert_called_once_with(17, self.trace)

    def test_native_stack_failure_preserves_registers_with_explicit_error(self):
        self.debugger.collect_stack_trace.side_effect = RuntimeError("unwind failure")
        value = self.collect(self.debugger, self.api)
        self.assertEqual(value["stackStatus"], "unavailable")
        self.assertEqual(value["stackError"], "unwind failure")
        self.assertTrue(value["registers"]["registers"])
        self.assertTrue(value["snapshotStable"])

    def test_symbol_failure_is_retained_per_frame(self):
        self.names.get_nice_colored_name.side_effect = RuntimeError("symbol failure")
        value = self.collect(self.debugger, self.api, max_frames=1)
        self.assertEqual(value["stack"][0]["symbol"], "symbol failure")
        self.assertEqual(value["stack"][0]["module"], "<error>")

    def test_invalid_bounds_do_not_touch_native_debugger(self):
        for value in (True, 0, 129, 1.5):
            with self.assertRaises(ValueError):
                self.collect(self.debugger, self.api, max_threads=value)
        self.api.dbg_ensure_suspended.assert_not_called()


class IdaLifecycleTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir=_TEST_TMP)
        self.directory = Path(self.temp.name)
        self.manager = IdaSessions(root=self.directory)
        self.process = Mock(pid=123)
        self.process.poll.return_value = None
        self.session = Session("target", "file.dll", "hash", "headless", self.directory, self.process, "private")
        self.manager.sessions["target"] = self.session
        (self.directory / "ready.json").write_text(json.dumps({"sessionId": "target", "pid": 123, "port": 14000}), encoding="utf-8")
        self.identity = {"sessionId": "target", "pid": 123, "sourceSha256": "hash", "inputSha256": "hash"}

    def tearDown(self):
        self.temp.cleanup()

    def result(self, value):
        return {"structuredContent": value, "isError": False}

    def test_target_identity_mismatch_is_rejected(self):
        with patch.object(self.manager, "_rpc_tool", return_value=self.result({**self.identity, "pid": 999})):
            with self.assertRaisesRegex(IdaError, "IDA_IDENTITY_MISMATCH"):
                self.manager.status("target")

    def test_busy_probe_never_kills_or_restarts(self):
        with patch.object(self.manager, "_rpc_tool", side_effect=TimeoutError):
            self.assertEqual(self.manager.status("target")["state"], "busy")
        self.process.terminate.assert_not_called()

    def test_registration_sharing_conflict_remains_starting_until_next_status(self):
        with patch.object(Path, "read_text", side_effect=PermissionError("sharing conflict")), \
                patch.object(self.manager, "_rpc_tool") as rpc:
            self.assertEqual(self.manager.status("target")["state"], "starting")
            rpc.assert_not_called()
        with patch.object(self.manager, "_rpc_tool", return_value=self.result(self.identity)):
            self.assertEqual(self.manager.status("target")["state"], "ready")
        self.process.terminate.assert_not_called()

    def test_registration_sharing_conflict_cannot_dispatch_a_command(self):
        self.session.identity = self.identity
        self.session.state = "ready"
        with patch.object(Path, "read_text", side_effect=PermissionError("sharing conflict")), \
                patch.object(self.manager, "_rpc_tool") as rpc:
            with self.assertRaisesRegex(IdaError, "IDA_NOT_READY"):
                self.manager.call("target", "patch", {})
            self.assertEqual(self.session.state, "busy")
            rpc.assert_not_called()

    def test_status_does_not_wait_for_running_tool_on_same_session(self):
        locked = threading.Event()
        release = threading.Event()
        def hold():
            with self.session.lock:
                locked.set()
                release.wait(2)
        thread = threading.Thread(target=hold)
        thread.start()
        try:
            self.assertTrue(locked.wait(1))
            start = time.monotonic()
            self.assertEqual(self.manager.status("target")["state"], "busy")
            self.assertLess(time.monotonic() - start, 0.2)
        finally:
            release.set()
            thread.join(1)

    def test_same_pid_changed_database_is_rejected(self):
        self.session.identity = {**self.identity, "idbPath": "first.i64"}
        with patch.object(self.manager, "_rpc_tool", return_value=self.result({**self.identity, "idbPath": "other.i64"})):
            with self.assertRaisesRegex(IdaError, "IDA_IDENTITY_MISMATCH"):
                self.manager.status("target")

    def test_list_does_not_probe_other_session(self):
        with patch.object(self.manager, "_rpc_tool", side_effect=AssertionError):
            self.assertEqual(len(self.manager.list()), 1)

    def test_busy_call_rejected_without_occupying_another_rpc_worker(self):
        held, release, finished = threading.Event(), threading.Event(), threading.Event()
        errors = []
        def hold():
            with self.session.lock:
                held.set()
                release.wait(2)
        def call():
            try:
                self.manager.call("target", "patch", {})
            except IdaError as error:
                errors.append(error.payload)
            finally:
                finished.set()
        holder = threading.Thread(target=hold)
        caller = threading.Thread(target=call)
        with patch.object(self.manager, "_rpc_tool", return_value=self.result(self.identity)) as rpc:
            holder.start()
            try:
                self.assertTrue(held.wait(1))
                caller.start()
                self.assertTrue(finished.wait(0.2), "Busy calls must not wait behind the session lock")
                self.assertEqual(errors[0]["code"], "IDA_BUSY_NOT_STARTED")
                self.assertFalse(errors[0]["requestStarted"])
                rpc.assert_not_called()
            finally:
                release.set()
                holder.join(1)
                if caller.ident is not None:
                    caller.join(1)

    def test_open_copy_does_not_block_existing_session_listing(self):
        (self.directory / "idat.exe").touch()
        source = self.directory / "input.dll"
        source.write_bytes(b"input")
        copying, release, listed = threading.Event(), threading.Event(), threading.Event()
        errors = []
        original_copy = __import__("shutil").copy2
        def copy(*args):
            copying.set()
            release.wait(2)
            return original_copy(*args)
        def open_session():
            try:
                self.manager.open(source)
            except Exception as error:
                errors.append(error)
        def list_sessions():
            self.manager.list()
            listed.set()
        opener, lister = threading.Thread(target=open_session), threading.Thread(target=list_sessions)
        with patch("ida_backend.sessions.find_ida", return_value=self.directory), \
                patch("ida_backend.sessions.shutil.copy2", side_effect=copy), \
                patch("ida_backend.sessions.subprocess.Popen", return_value=self.process):
            opener.start()
            try:
                self.assertTrue(copying.wait(1))
                lister.start()
                self.assertTrue(listed.wait(0.2), "Input copy must not hold the global session lock")
                self.manager.max_sessions = 2
                with self.assertRaisesRegex(IdaError, "IDA_SESSION_LIMIT"):
                    self.manager.open(source)
            finally:
                release.set()
                opener.join(2)
                if lister.ident is not None:
                    lister.join(1)
        self.assertEqual(errors, [])

    def test_failed_open_releases_reserved_capacity(self):
        (self.directory / "idat.exe").touch()
        source = self.directory / "input.dll"
        source.write_bytes(b"input")
        self.manager.max_sessions = 2
        with patch("ida_backend.sessions.find_ida", return_value=self.directory), \
                patch("ida_backend.sessions.shutil.copy2", side_effect=OSError("copy failed")):
            for _ in range(2):
                with self.assertRaisesRegex(OSError, "copy failed"):
                    self.manager.open(source)
        self.assertEqual(self.manager._opening, 0)

    def test_identity_preflight_charged_to_total_tool_deadline(self):
        with patch("ida_backend.sessions.time.monotonic", side_effect=[100, 100.1]), \
                patch.object(self.manager, "_rpc_tool", return_value=self.result(self.identity)) as rpc:
            self.manager.call("target", "patch", {}, timeout=0.25)
        self.assertEqual(rpc.call_count, 2)
        self.assertEqual(rpc.call_args_list[0].kwargs["timeout"], 0.25)
        self.assertAlmostEqual(rpc.call_args_list[1].args[-1], 0.15)

    def test_expired_preflight_never_dispatches_mutation(self):
        with patch("ida_backend.sessions.time.monotonic", side_effect=[100, 101]), \
                patch.object(self.manager, "_rpc_tool", return_value=self.result(self.identity)) as rpc:
            with self.assertRaisesRegex(IdaError, "IDA_DEADLINE_EXCEEDED_NOT_STARTED"):
                self.manager.call("target", "patch", {}, timeout=0.25)
        self.assertEqual(rpc.call_count, 1)

    def test_mutation_timeout_unknown_never_retried(self):
        with patch.object(self.manager, "_rpc_tool", side_effect=[self.result(self.identity), TimeoutError]) as rpc:
            with self.assertRaisesRegex(IdaError, "IDA_OUTCOME_UNKNOWN"):
                self.manager.call("target", "patch", {"patches": []})
            self.assertEqual(rpc.call_count, 2)  # one identity query, one mutation
        self.process.terminate.assert_not_called()

    def test_transport_error_preserves_unknown_mutation_and_close(self):
        for close in (False, True):
            with self.subTest(close=close), patch.object(
                self.manager, "_rpc_tool", side_effect=[self.result(self.identity),
                    IdaError("IDA_TRANSPORT_ERROR", "Invalid response")],
            ) as call:
                with self.assertRaisesRegex(IdaError, "IDA_OUTCOME_UNKNOWN") as error:
                    if close:
                        self.manager.close("target")
                    else:
                        self.manager.call("target", "patch", {"patches": []})
                self.assertTrue(error.exception.payload["doNotRetry"])
                self.assertEqual(2, call.call_count)
                self.assertIsNone(self.session.closing_at)
                self.process.terminate.assert_not_called()

    def test_invalid_transport_preflight_does_not_dispatch_mutation(self):
        with patch.object(self.manager, "_rpc_tool", side_effect=IdaError(
            "IDA_TRANSPORT_ERROR", "Invalid identity response",
        )) as call:
            with self.assertRaisesRegex(IdaError, "IDA_NOT_READY"):
                self.manager.call("target", "patch", {})
            self.assertEqual(1, call.call_count)
            self.assertEqual("busy", self.session.state)
        self.process.terminate.assert_not_called()

    def test_malformed_identity_does_not_dispatch_mutation(self):
        for identity in (None, [], "identity"):
            with self.subTest(identity=identity), patch.object(
                self.manager, "_rpc_tool", return_value=self.result(identity),
            ) as call:
                with self.assertRaisesRegex(IdaError, "IDA_NOT_READY"):
                    self.manager.call("target", "patch", {})
                self.assertEqual(1, call.call_count)
        self.process.terminate.assert_not_called()

    def test_save_error_keeps_session_and_process(self):
        with patch.object(self.manager, "_rpc_tool", side_effect=[self.result(self.identity), {"isError": True, "content": []}]):
            with self.assertRaisesRegex(IdaError, "IDA_TOOL_ERROR"):
                self.manager.close("target")
        self.assertEqual(self.session.state, "ready")
        self.process.terminate.assert_not_called()

    def test_close_timeout_does_not_authorize_termination(self):
        with patch.object(self.manager, "_rpc_tool", side_effect=[self.result(self.identity), TimeoutError]):
            with self.assertRaisesRegex(IdaError, "IDA_OUTCOME_UNKNOWN"):
                self.manager.close("target")
        self.assertIsNone(self.session.closing_at)
        self.process.terminate.assert_not_called()

    def test_invalid_save_receipt_does_not_authorize_termination(self):
        for receipt in ({"accepted": True, "saved": False}, None, [], "accepted"):
            with self.subTest(receipt=receipt), patch.object(self.manager, "_rpc_tool",
                side_effect=[self.result(self.identity), self.result(receipt)],
            ):
                with self.assertRaisesRegex(IdaError, "IDA_OUTCOME_UNKNOWN"):
                    self.manager.close("target")
        self.assertIsNone(self.session.closing_at)
        self.process.terminate.assert_not_called()

    def test_malformed_close_envelope_preserves_unknown_outcome(self):
        with patch.object(self.manager, "_rpc_tool", side_effect=[self.result(self.identity), None]) as call:
            with self.assertRaisesRegex(IdaError, "IDA_OUTCOME_UNKNOWN") as error:
                self.manager.close("target")
            self.assertTrue(error.exception.payload["doNotRetry"])
            self.assertEqual(2, call.call_count)
        self.assertIsNone(self.session.closing_at)
        self.process.terminate.assert_not_called()

    def test_only_acknowledged_close_allows_owned_exit_fallback(self):
        with patch.object(self.manager, "_rpc_tool", side_effect=[self.result(self.identity), self.result({"accepted": True, "saved": True})]):
            result = self.manager.close("target")
        self.assertEqual(result["state"], "closing")
        self.session.closing_at = time.monotonic() - 6
        self.manager.status("target")
        self.process.terminate.assert_called_once()
        self.assertTrue(self.session.close_receipt["ownedProcessTerminationAfterGrace"])

    def test_explicit_discard_can_cancel_never_ready_owned_startup(self):
        (self.directory / "ready.json").unlink()
        receipt = self.manager.close("target", save=False)
        self.assertTrue(receipt["closeReceipt"]["startupCancelled"])
        self.process.terminate.assert_called_once()

    def test_shutdown_finishes_acknowledged_close_without_client_polling(self):
        def close(*args, **kwargs):
            self.session.state = "closing"
            self.session.closing_at = time.monotonic() - 6
            self.session.close_receipt = {"accepted": True, "saved": True}
        self.process.terminate.side_effect = lambda: setattr(self.process.poll, "return_value", 0)
        with patch.object(self.manager, "close", side_effect=close):
            self.manager.shutdown()
        self.process.terminate.assert_called_once()
        self.assertEqual(self.process.poll(), 0)

    def test_shutdown_failed_save_or_active_debugger_never_terminates(self):
        self.session.state = "ready"
        with patch.object(self.manager, "close", side_effect=IdaError("IDA_TOOL_ERROR", "close refused")):
            self.manager.shutdown()
        self.process.terminate.assert_not_called()

    def test_shutdown_unknown_close_outcome_never_terminates(self):
        self.session.state = "ready"
        with patch.object(self.manager, "close", side_effect=IdaError("IDA_OUTCOME_UNKNOWN", "timeout")):
            self.manager.shutdown()
        self.process.terminate.assert_not_called()

    def test_timeout_validation_is_finite(self):
        for value in (0, -1, 121, float("inf"), float("nan")):
            with self.assertRaisesRegex(IdaError, "INVALID_ARGUMENT"):
                self.manager.call("target", "patch", {}, value)

    def test_unknown_session_never_falls_back_to_current(self):
        with self.assertRaisesRegex(IdaError, "IDA_SESSION_NOT_FOUND"):
            self.manager.call("other", "patch", {})

    def test_no_ida_or_vs_import_required_for_tool_listing(self):
        code = "import mcp_server.ida_server as s; print([x['name'] for x in s.mcp.registry.methods['tools/list']()['tools']])"
        result = subprocess.run([sys.executable, "-c", code], cwd=ROOT, capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("ida_open", result.stdout)
        self.assertNotIn("vs_", result.stdout)

    def test_per_item_error_preserved_by_transport(self):
        result = {"isError": False, "structuredContent": {"result": [{"error": "unmapped"}]}}
        self.assertEqual(tool_value(result)["result"][0]["error"], "unmapped")

    def test_other_session_stdio_dispatch_proceeds_while_first_is_busy(self):
        finished_second = threading.Event()
        fake_mcp = Mock()
        fake_mcp._transport_session_id = threading.local()
        def dispatch(request):
            request = json.loads(request)
            if request["id"] == 1:
                if not finished_second.wait(1):
                    raise AssertionError("Other session blocked by first request")
            else:
                finished_second.set()
            return {"id": request["id"], "result": {}}
        fake_mcp.registry.dispatch = dispatch
        output = io.BytesIO()
        serve(fake_mcp, io.BytesIO(b'{"id":1}\n{"id":2}\n'), output)
        self.assertEqual({json.loads(line)["id"] for line in output.getvalue().splitlines()}, {1, 2})


class WorkerBoundaryTests(unittest.TestCase):
    def function(self, name, environment):
        tree = ast.parse((ROOT / "ida_backend/worker.py").read_text(encoding="utf-8"))
        function = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == name)
        function.decorator_list = []
        exec(compile(ast.Module(body=[function], type_ignores=[]), "worker-boundary", "exec"), environment)
        return environment[name]

    def test_idle_pump_does_not_duplicate_events_but_native_notifications_do(self):
        event = {"eid": 5, "pid": 10, "tid": 11, "ea": "0x1000"}
        debugger = Mock(WFNE_ANY=1, WFNE_SILENT=2)
        debugger.DBG_Hooks = object
        debugger.is_debugger_on.return_value = True
        debugger.wait_for_next_event.return_value = 5
        events = []
        environment = {"ida_dbg": debugger, "EVENTS": events, "event_metadata": lambda value: dict(event),
                       "PARENT": None, "CLOSING": False, "EXIT_AT": 0, "time": time,
                       "CONFIG": {"mode": "headless"}}
        tree = ast.parse((ROOT / "ida_backend/worker.py").read_text(encoding="utf-8"))
        hook = next(node for node in tree.body if isinstance(node, ast.ClassDef) and node.name == "DebugEventHooks")
        exec(compile(ast.Module(body=[hook], type_ignores=[]), "worker-boundary", "exec"), environment)
        hooks = environment["DebugEventHooks"]()
        hooks.dbg_bpt(11, 0x1000)
        tick = self.function("tick", environment)
        for _ in range(300):
            self.assertEqual(tick(), 100)
        self.assertEqual(events, [event])
        # A later real hit at the same address is a distinct notification.
        hooks.dbg_bpt(11, 0x1000)
        self.assertEqual(events, [event, event])
        self.assertEqual(debugger.wait_for_next_event.call_count, 300)

    def test_unicode_cached_output_pages_reconstruct_without_nested_truncation(self):
        # The vendor's output limiter measures json.dumps(structuredContent),
        # including ASCII escaping. Non-BMP characters require 12 bytes each.
        value = {"output": "\U0001f600" * 18000}
        serialized = json.dumps(value, ensure_ascii=False)
        read = self.function("read_cached_output", {"json": json,
            "get_cached_output": lambda output_id: value, "OUTPUT_LIMIT_MAX_CHARS": 50000})
        for count in (8000, 10000):
            with self.subTest(count=count):
                offset, chunks = 0, []
                while True:
                    page = read("cached", offset=offset, count=count)
                    self.assertLessEqual(len(json.dumps(page)), 50000,
                        "A cache page must not itself be truncated while retaining its nextOffset")
                    chunks.append(page["text"])
                    self.assertEqual(page["totalChars"], len(serialized))
                    if page["nextOffset"] is None:
                        break
                    self.assertGreater(page["nextOffset"], offset)
                    self.assertEqual(page["nextOffset"], offset + len(page["text"]))
                    offset = page["nextOffset"]
                self.assertEqual("".join(chunks), serialized)

    def test_parent_exit_does_not_save_an_acknowledged_discard_close(self):
        kernel = Mock()
        kernel.WaitForSingleObject.return_value = 0
        loader, debugger, pump = Mock(), Mock(), Mock()
        debugger.is_debugger_on.return_value = False
        tick = self.function("tick", {"PARENT": (kernel, 1), "CLOSING": True,
            "SAVE": False, "EXIT_AT": time.monotonic() - 1, "time": time,
            "ida_loader": loader, "ida_dbg": debugger, "get_pump": lambda: pump,
            "CONFIG": {"mode": "headless"}})
        self.assertEqual(tick(), -1)
        loader.save_database.assert_not_called()
        pump.stop.assert_called_once()

    def test_parent_exit_retries_failed_save_then_stops_saving_after_success(self):
        kernel = Mock()
        kernel.WaitForSingleObject.return_value = 0
        loader, debugger, pump = Mock(), Mock(), Mock()
        loader.save_database.side_effect = [False, True]
        debugger.is_debugger_on.return_value = False
        environment = {"PARENT": (kernel, 1), "CLOSING": False, "EXIT_AT": 0,
            "time": time, "ida_loader": loader, "ida_dbg": debugger,
            "get_pump": lambda: pump, "CONFIG": {"mode": "headless"}}
        tick = self.function("tick", environment)
        self.assertEqual(tick(), 100)
        self.assertFalse(environment["CLOSING"])
        self.assertEqual(tick(), -1)
        self.assertEqual(tick(), -1)
        self.assertEqual(loader.save_database.call_count, 2)

    def test_unmapped_native_visitor_patch_cannot_create_partial_output(self):
        with tempfile.TemporaryDirectory(dir=_TEST_TMP) as directory:
            source = Path(directory) / "input.dll"
            source.write_bytes(b"1234")
            output = Path(directory) / "output.dll"
            nalt = Mock()
            nalt.get_input_file_path.return_value = str(source)
            nalt.retrieve_input_file_sha256.return_value = hashlib.sha256(b"1234").digest()
            native = Mock()
            def visit(start, end, callback):
                callback(0x1000, 0, ord("1"), ord("A"))
                callback(0x2000, -1, 0, 1)
            native.visit_patched_bytes.side_effect = visit
            dbg = Mock()
            dbg.is_debugger_on.return_value = False
            export = self.function("export_patched_binary", {"Path": Path, "hashlib": hashlib,
                "ida_dbg": dbg, "ida_nalt": nalt, "ida_bytes": native, "ida_idaapi": Mock(BADADDR=-1),
                "CONFIG": {"sourcePath": str(source)}})
            with self.assertRaisesRegex(RuntimeError, "not backed"):
                export(str(output))
            self.assertFalse(output.exists())
            self.assertEqual(source.read_bytes(), b"1234")

    def test_event_union_guard_never_reads_info_from_process_start(self):
        constants = Mock(PROCESS_STARTED=1, PROCESS_ATTACHED=2, LIB_LOADED=3,
                         PROCESS_EXITED=4, THREAD_EXITED=5, THREAD_STARTED=6,
                         LIB_UNLOADED=7, INFORMATION=8, EXCEPTION=9)
        event = Mock(pid=10, tid=11, ea=0x1000, handled=True)
        event.eid.return_value = 1
        event.modinfo.return_value = SimpleNamespace(name="binary", base=0x1000, size=10)
        event.info.side_effect = AssertionError("Native union assertion")
        with patch.dict(sys.modules, {"ida_idd": constants}):
            serialize = self.function("event_metadata", {})
            result = serialize(event)
        self.assertEqual(result["module"]["name"], "binary")
        event.info.assert_not_called()
        event.exc.assert_not_called()


class IdaInstallerTests(unittest.TestCase):
    def setUp(self):
        self.env = patch.dict(os.environ, {"CODEX_HOME": ""})
        self.env.start()
        self.temp = tempfile.TemporaryDirectory(dir=_TEST_TMP)
        self.home = Path(self.temp.name)
        (self.home / ".codex").mkdir()
        self.toml = self.home / ".codex/config.toml"
        self.original = 'model = "existing"\n[mcp_servers.ida]\nurl = "http://127.0.0.1:13337/mcp"\n[mcp_servers.VS]\ncommand = "vs"\n'
        self.toml.write_text(self.original, encoding="utf-8")
        self.claude = self.home / ".claude.json"
        self.claude.write_text(json.dumps({"mcpServers": {"VS": {"command": "vs"}, "ida": {"url": "old"}}, "unrelated": 42}), encoding="utf-8")

    def tearDown(self):
        self.temp.cleanup()
        self.env.stop()

    def test_install_preserves_vs_and_deploys_skill_and_runtime(self):
        result = installer.install(self.home)
        import tomllib
        config = tomllib.loads(self.toml.read_text(encoding="utf-8"))
        self.assertEqual(config["mcp_servers"]["VS"], {"command": "vs"})
        self.assertEqual(config["model"], "existing")
        claude = json.loads(self.claude.read_text(encoding="utf-8"))
        self.assertEqual(claude["unrelated"], 42)
        self.assertEqual(claude["mcpServers"]["VS"], {"command": "vs"})
        for client in (".claude", ".codex"):
            self.assertTrue((self.home / client / "skills/ida-evidence-analysis/SKILL.md").is_file())
        self.assertTrue(Path(result["server"]).is_file())
        self.assertTrue(list(Path(result["configBackups"]).glob("*-config.toml")))

    def test_dry_run_is_read_only(self):
        before = {str(p): p.read_bytes() for p in self.home.rglob("*") if p.is_file()}
        installer.install(self.home, dry_run=True)
        after = {str(p): p.read_bytes() for p in self.home.rglob("*") if p.is_file()}
        self.assertEqual(before, after)

    def test_reinstall_and_local_skill_edits_protected(self):
        installer.install(self.home)
        installer.install(self.home)
        skill = self.home / ".codex/skills/ida-evidence-analysis/SKILL.md"
        skill.write_text("local edit", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "local edits"):
            installer.install(self.home)
        self.assertEqual(skill.read_text(encoding="utf-8"), "local edit")

    def test_failed_write_rolls_back_both_runtime_and_config(self):
        original_write = installer.atomic_write
        def failure(path, data):
            if path.name == "config.toml" and b"startup_timeout_sec" in data:
                raise OSError("injected write failure")
            return original_write(path, data)
        with patch.object(installer, "atomic_write", side_effect=failure):
            with self.assertRaisesRegex(OSError, "injected write failure"):
                installer.install(self.home)
        self.assertEqual(self.toml.read_text(encoding="utf-8"), self.original)
        self.assertEqual(json.loads(self.claude.read_text(encoding="utf-8"))["mcpServers"]["ida"], {"url": "old"})

    def test_invalid_toml_does_not_deploy_partial_install(self):
        self.toml.write_text("[invalid", encoding="utf-8")
        with self.assertRaises(ValueError):
            installer.install(self.home)
        self.assertFalse((self.home / ".ai-debugger").exists())

    def test_quoted_toml_ownership_is_not_silently_duplicated(self):
        with self.assertRaises(ValueError):
            installer.replace_ida_toml('[mcp_servers."ida"]\nurl="old"\n', sys.executable, Path("server.py"))

    def test_concurrent_config_edit_after_read_is_preserved_and_aborts_install(self):
        checked = installer.checked_path
        injected = False
        def inject(path):
            nonlocal injected
            if path.name == "install-manifest.json" and not injected:
                value = json.loads(self.claude.read_text(encoding="utf-8"))
                value["mcpServers"]["concurrent"] = {"command": "keep-me"}
                self.claude.write_text(json.dumps(value), encoding="utf-8")
                injected = True
            return checked(path)
        with patch.object(installer, "checked_path", side_effect=inject):
            with self.assertRaisesRegex(ValueError, "changed during installation"):
                installer.install(self.home)
        config = json.loads(self.claude.read_text(encoding="utf-8"))
        self.assertEqual(config["mcpServers"]["concurrent"], {"command": "keep-me"})
        self.assertEqual(config["mcpServers"]["ida"], {"url": "old"})
        self.assertEqual(self.toml.read_text(encoding="utf-8"), self.original)

    def test_rollback_does_not_overwrite_config_edited_after_own_write(self):
        write = installer.atomic_write
        def fail(path, content):
            if path == self.toml:
                value = json.loads(self.claude.read_text(encoding="utf-8"))
                value["mcpServers"]["concurrent"] = {"command": "keep-me"}
                self.claude.write_text(json.dumps(value), encoding="utf-8")
                raise OSError("injected failure after concurrent edit")
            return write(path, content)
        with patch.object(installer, "atomic_write", side_effect=fail):
            with self.assertRaisesRegex(OSError, "injected failure") as caught:
                installer.install(self.home)
        self.assertIn("concurrent", json.loads(self.claude.read_text(encoding="utf-8"))["mcpServers"])
        self.assertTrue(any("Concurrent edit preserved" in note for note in caught.exception.__notes__))

    def test_concurrent_codex_edit_after_read_is_preserved(self):
        checked = installer.checked_path
        changed = self.original + '\n[profiles.concurrent]\nmodel = "keep-me"\n'
        def inject(path):
            if path.name == "install-manifest.json":
                self.toml.write_text(changed, encoding="utf-8")
            return checked(path)
        with patch.object(installer, "checked_path", side_effect=inject):
            with self.assertRaisesRegex(ValueError, "changed during installation"):
                installer.install(self.home)
        self.assertEqual(self.toml.read_text(encoding="utf-8"), changed)
        self.assertEqual(json.loads(self.claude.read_text(encoding="utf-8"))["mcpServers"]["ida"], {"url": "old"})

    def test_codex_home_used_for_both_registration_and_skill(self):
        custom = self.home / "custom-codex"
        custom.mkdir()
        config = custom / "config.toml"
        config.write_text(self.original, encoding="utf-8")
        with patch.dict(os.environ, {"CODEX_HOME": str(custom)}):
            installer.install(self.home, clients=("codex",))
        import tomllib
        updated = tomllib.loads(config.read_text(encoding="utf-8"))
        self.assertEqual(updated["mcp_servers"]["VS"], {"command": "vs"})
        self.assertIn("command", updated["mcp_servers"]["ida"])
        self.assertTrue((custom / "skills/ida-evidence-analysis/SKILL.md").is_file())
        self.assertEqual(self.toml.read_text(encoding="utf-8"), self.original)
        self.assertFalse((self.home / ".codex/skills").exists())

    def test_partial_client_install_retains_other_client_skill_ownership(self):
        installer.install(self.home)
        installer.install(self.home, clients=("codex",))
        manifest = json.loads((self.home / ".ai-debugger/ida/install-manifest.json").read_text(encoding="utf-8"))
        self.assertIn(str(self.home / ".claude/skills/ida-evidence-analysis/SKILL.md"), manifest["files"])
        self.assertEqual(manifest["clients"], ["claude", "codex"])

    def test_toml_multiline_header_text_cannot_change_unrelated_settings(self):
        text = 'developer_instructions = """\n[mcp_servers.ida]\nkeep this instruction\n[example]\n"""\n'
        with self.assertRaisesRegex(ValueError, "unrelated TOML settings"):
            installer.replace_ida_toml(text, sys.executable, Path("server.py"))


if __name__ == "__main__":
    unittest.main()
