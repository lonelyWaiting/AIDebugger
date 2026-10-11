"""Portable contract tests; real DbgEng tests are explicitly opt-in."""
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import tomllib
import unittest
from types import SimpleNamespace
from unittest.mock import Mock, patch

from kernel_backend.contracts import KernelError, capabilities, connection_identity, query_command, validate_operation
from kernel_backend.sessions import KernelSessions, Session
from tools import install_aidebugger as installer

ROOT = Path(__file__).resolve().parents[1]


class PolicyTests(unittest.TestCase):
    def test_inspect_mode_cannot_run_raw_or_control(self):
        for operation, args in [("command", {"command": "!process 0 0"}), ("control", {"action": "continue"})]:
            with self.assertRaises(KernelError):
                validate_operation("remote", False, operation, args)

    def test_injection_cannot_enter_query_or_address(self):
        for args in [{"address": "0x10;g"}, {"address": "@rip"}, {"address": "10", "count": True},
                     {"address": "10", "extra": "g"}, {"address": "10", "count": 129}]:
            with self.assertRaises(KernelError):
                query_command("disassemble", args, "dump")
        with self.assertRaises(KernelError):
            query_command("modules", {"module": "nt;g"}, "dump")

    def test_valid_disassembly_uses_explicit_hex_radix(self):
        self.assertEqual(query_command("disassemble", {"address": "0x10", "count": 16}, "dump"), "u 0x10 L0x10")

    def test_mode_capabilities_and_local_limits(self):
        self.assertFalse(capabilities("dump", True)["executionControl"])
        self.assertFalse(capabilities("local", False)["snapshotStable"])
        for query in ("registers", "stack", "breakpoints"):
            with self.assertRaises(KernelError):
                query_command(query, {}, "local")
        with self.assertRaises(KernelError):
            validate_operation("dump", True, "control", {"action": "step_into"})

    def test_lifecycle_commands_are_rejected(self):
        for command in ("q", "lm; .opendump x", ".attach 12", ".reboot", ".crash"):
            with self.assertRaises(KernelError):
                validate_operation("remote", True, "command", {"command": command})

    def test_endpoint_identity_excludes_key_and_option_order(self):
        self.assertEqual(connection_identity("net:port=50000,key=secret"), connection_identity("NET:key=different,port=50000"))
        for connection in ("local", "net:key=x\ng", "net:", "invalid:key=x"):
            with self.assertRaises(KernelError):
                connection_identity(connection)

    def test_typed_mutations_enforce_mode_and_validate_payload(self):
        for operation, arguments in [("set_breakpoint", {"address": "ffff800000001000"}),
                                     ("remove_breakpoint", {"breakpoint_id": 1}),
                                     ("write_memory", {"address": "10", "data_hex": "aabb"})]:
            validate_operation("remote", True, operation, arguments)
            with self.assertRaises(KernelError):
                validate_operation("remote", False, operation, arguments)
        for arguments in ({"address": "10", "data_hex": "0"}, {"address": "10", "data_hex": "zz"}):
            with self.assertRaises(KernelError):
                validate_operation("dump", True, "write_memory", arguments)
        with self.assertRaises(KernelError):
            validate_operation("dump", True, "set_breakpoint", {"address": "10"})

    def test_partial_native_read_does_not_hide_available_bytes(self):
        from kernel_backend.engine import Engine
        import ctypes
        engine = object.__new__(Engine)
        engine.output = Mock()
        engine.verify_target = Mock()
        engine.status = Mock(return_value={"state": "paused"})
        engine.memory = Mock()
        def read(slot, types, offset, buffer, size, bytes_read):
            ctypes.memmove(buffer, b"abcd", 4)
            bytes_read._obj.value = 4
            return -2147467259
        engine.memory.call.side_effect = read
        result = engine.perform("read_memory", {"address": "10", "size": 8})
        self.assertTrue(result["partial"])
        self.assertEqual(result["status"], "partial")
        self.assertEqual(result["bytesRead"], 4)
        self.assertEqual(result["dataHex"], "61626364")

    def test_removing_native_breakpoint_never_dereferences_deleted_interface(self):
        from kernel_backend.engine import Engine
        engine = object.__new__(Engine)
        engine.output = Mock()
        engine.verify_target = Mock()
        engine.status = Mock(return_value={"state": "paused"})
        engine.control = Mock()
        engine.control.call.return_value = 0
        # DbgEng deletes the breakpoint object on RemoveBreakpoint. It is
        # unlike normal COM interfaces; even Release afterwards is invalid.
        with patch("kernel_backend.engine.Interface") as interface:
            interface.return_value.release.side_effect = AssertionError("deleted breakpoint accessed")
            result = engine.perform("remove_breakpoint", {"breakpoint_id": 7})
            self.assertTrue(result["removed"])
            interface.assert_not_called()


class SupervisorTests(unittest.TestCase):
    def setUp(self):
        self.manager = KernelSessions()
        self.process = Mock()
        self.process.poll.return_value = None
        self.process.stdin = io.BytesIO()
        self.session = Session("one", "remote", True, {"kind": "remote"}, self.process, status={"state": "paused"})
        self.manager.sessions["one"] = self.session

    def test_short_job_wait_releases_lock_and_wakes_on_native_reply(self):
        receipt = self.manager.submit("one", "query", {"query": "registers", "arguments": {}})
        waiting = threading.Event()
        finished = threading.Event()
        result = []
        real_wait = self.session.changed.wait
        def wait(timeout=None):
            waiting.set()
            return real_wait(timeout)
        def poll():
            result.append(self.manager.job("one", receipt["jobId"], wait_ms=50))
            finished.set()
        with patch.object(self.session.changed, "wait", side_effect=wait):
            thread = threading.Thread(target=poll)
            thread.start()
            self.assertTrue(waiting.wait(1))
            self.process.stdout = io.BytesIO((json.dumps({"id": receipt["jobId"], "result": {"state": "paused"}}) + "\n").encode())
            self.manager._read(self.session)
            self.assertTrue(finished.wait(1))
            thread.join(1)
        self.assertEqual(result[0]["state"], "complete")

    def test_short_job_wait_is_bounded_and_does_not_repeat_mutation(self):
        receipt = self.manager.submit("one", "control", {"action": "step_into"})
        sent = self.process.stdin.getvalue()
        with patch.object(self.session.changed, "wait_for", return_value=False) as waiter:
            self.assertEqual(self.manager.job("one", receipt["jobId"], wait_ms=10)["state"], "running")
            self.assertEqual(waiter.call_args.kwargs["timeout"], .01)
        self.assertEqual(self.process.stdin.getvalue(), sent)
        for invalid in (True, -1, 51, 1.5):
            with self.assertRaises(KernelError):
                self.manager.job("one", receipt["jobId"], wait_ms=invalid)

    def test_fresh_event_sequence_survives_bounded_history(self):
        messages = [{"status": {"state": "paused"}, "eventOutput": {"output": str(i)}} for i in range(12)]
        messages.append({"status": {"state": "paused"}})  # Query/ack is not a new stop.
        self.process.stdout = io.BytesIO(b"".join((json.dumps(m) + "\n").encode() for m in messages))
        self.manager._read(self.session)
        self.assertEqual(self.manager.status("one")["eventSequence"], 12)
        self.assertEqual(len(self.session.events), 8)

    def test_missing_session_does_not_fall_back(self):
        with self.assertRaises(KernelError):
            self.manager.submit("other", "query", {"query": "modules", "arguments": {}})
        self.assertEqual(self.process.stdin.getvalue(), b"")

    def test_busy_session_rejects_commands_but_allows_interrupt(self):
        receipt = self.manager.submit("one", "control", {"action": "continue"})
        with self.assertRaises(KernelError):
            self.manager.submit("one", "query", {"query": "modules", "arguments": {}})
        interrupt = self.manager.interrupt("one")
        self.assertNotEqual(interrupt["jobId"], receipt["jobId"])
        self.assertEqual(self.session.active, receipt["jobId"])

    def test_pending_interrupts_reserve_capacity_for_close(self):
        self.manager.MAX_JOBS = 3
        self.manager.submit("one", "control", {"action": "continue"})
        self.manager.interrupt("one")
        with self.assertRaises(KernelError) as error:
            self.manager.interrupt("one")
        self.assertEqual(error.exception.code, "JOB_LIMIT")
        with patch("kernel_backend.sessions.threading.Thread"):
            closed = self.manager.submit("one", "close", {})
        self.assertEqual(len(self.session.jobs), 3)
        self.assertEqual(self.session.close_job, closed["jobId"])

    def test_running_status_never_allows_queries(self):
        self.session.status["state"] = "running"
        with self.assertRaises(KernelError):
            self.manager.submit("one", "query", {"query": "modules", "arguments": {}})

    def test_closed_status_never_reports_old_snapshot_as_stable(self):
        self.session.status.update(state="closed", snapshotStable=True, stopReason="breakpoint")
        status = self.manager.status("one")
        self.assertFalse(status["snapshotStable"])
        self.assertEqual(status["stopReason"], "not_paused")

    def test_bounded_output_pagination(self):
        self.session.jobs["job"] = {"state": "complete", "started": 0, "result": {"output": "abcdef", "truncated": True}}
        result = self.manager.job("one", "job", 2, 2)["result"]
        self.assertEqual(result["output"], "cd")
        self.assertEqual(result["nextOffset"], 4)
        self.assertTrue(result["truncated"])
        with self.assertRaises(KernelError):
            self.manager.job("one", "job", count=20000)

    def test_worker_exit_marks_pending_mutation_outcome_unknown(self):
        self.session.jobs["job"] = {"state": "running", "operation": "command", "started": 0}
        self.session.active = "job"
        self.process.stdout = io.BytesIO(b"")
        self.manager._read(self.session)
        self.assertEqual(self.manager.job("one", "job")["error"]["code"], "WORKER_EXITED")
        self.assertIsNone(self.session.active)
        self.assertEqual(self.session.status["state"], "disconnected")

    def test_failed_pipe_mutation_is_never_retried(self):
        self.process.stdin = Mock()
        self.process.stdin.write.side_effect = BrokenPipeError()
        with self.assertRaises(KernelError):
            self.manager.submit("one", "command", {"command": "bp nt!Foo"})
        self.process.stdin.write.assert_called_once()

    def test_local_session_denies_full_access(self):
        with self.assertRaises(KernelError):
            self.manager.open("local", full_access=True)

    def test_force_close_records_unknown_target_without_retry(self):
        self.session.jobs["close"] = {"state": "running", "operation": "close", "started": 0}
        self.process.wait.side_effect = [subprocess.TimeoutExpired("worker", 2), 0]
        self.manager._close_watchdog(self.session, "close")
        self.process.terminate.assert_called_once()
        receipt = self.manager.job("one", "close")["result"]
        self.assertFalse(receipt["nativeCloseConfirmed"])
        self.assertFalse(receipt["targetStateKnown"])
        self.assertFalse(receipt["targetResumed"])

    def test_native_close_receipt_not_overwritten_by_watchdog(self):
        self.session.jobs["close"] = {"state": "complete", "operation": "close", "started": 0,
                                      "result": {"nativeCloseConfirmed": True}}
        self.manager._close_watchdog(self.session, "close")
        self.assertTrue(self.manager.job("one", "close")["result"]["nativeCloseConfirmed"])
        self.process.terminate.assert_not_called()

    def test_close_remains_terminal_after_late_operation_reply(self):
        self.session.jobs["old"] = {"state": "running", "operation": "query", "started": 0}
        self.session.active = "old"
        with patch("kernel_backend.sessions.threading.Thread"):
            closed = self.manager.submit("one", "close", {})
        messages = [{"id": "old", "result": {"state": "paused"}},
                    {"status": {"state": "paused"}}]
        self.process.stdout = io.BytesIO(b"".join(json.dumps(m).encode() + b"\n" for m in messages))
        self.manager._read(self.session)
        self.assertEqual(self.session.status["state"], "closing")
        for operation in ("close", "query"):
            with self.assertRaises(KernelError) as error:
                self.manager.submit("one", operation, {} if operation == "close" else {"query": "modules", "arguments": {}})
            self.assertEqual(error.exception.code, "SESSION_CLOSING")
        with self.assertRaises(KernelError):
            self.manager.interrupt("one")
        self.manager._close_watchdog(self.session, closed["jobId"])
        self.assertEqual(self.session.status["state"], "closed")

    def test_close_pipe_failure_still_schedules_owned_worker_cleanup(self):
        self.process.stdin = Mock()
        self.process.stdin.write.side_effect = BrokenPipeError()
        with patch("kernel_backend.sessions.threading.Thread") as thread:
            with self.assertRaises(KernelError):
                self.manager.submit("one", "close", {})
            thread.return_value.start.assert_called_once()
        self.assertEqual(self.session.status["state"], "closing")

    def test_close_job_waits_for_watchdog_receipt_on_eof_or_native_error(self):
        for messages in ([], [{"error": {"code": "DBGENG_ERROR", "message": "EndSession failed"}}]):
            with self.subTest(messages=messages):
                self.session.close_job = None
                self.session.active = None
                with patch("kernel_backend.sessions.threading.Thread"):
                    closed = self.manager.submit("one", "close", {})
                self.process.stdout = io.BytesIO(b"".join(
                    json.dumps({"id": closed["jobId"], **m}).encode() + b"\n" for m in messages))
                self.manager._read(self.session)
                self.assertEqual(self.manager.job("one", closed["jobId"])["state"], "running")
                self.manager._close_watchdog(self.session, closed["jobId"])
                receipt = self.manager.job("one", closed["jobId"])
                self.assertEqual(receipt["state"], "complete")
                self.assertFalse(receipt["result"]["nativeCloseConfirmed"])
                if messages:
                    self.assertEqual(receipt["result"]["nativeCloseError"]["code"], "DBGENG_ERROR")

    def test_unknown_pipe_outcome_invalidates_cached_snapshot(self):
        self.process.stdin = Mock()
        self.process.stdin.write.side_effect = BrokenPipeError()
        with self.assertRaises(KernelError):
            self.manager.submit("one", "command", {"command": "bp nt!Foo"})
        self.assertFalse(self.manager.status("one")["snapshotStable"])

    def test_late_status_cannot_restore_snapshot_after_pipe_failure(self):
        self.process.stdin = Mock()
        self.process.stdin.write.side_effect = BrokenPipeError()
        with self.assertRaises(KernelError):
            self.manager.submit("one", "command", {"command": "bp nt!Foo"})
        # Inspect state immediately before EOF cleanup would mask the race.
        snapshots = []
        def replies():
            yield b'{"status":{"state":"paused"}}\n'
            snapshots.append(self.manager.status("one"))
        self.process.stdout = replies()
        self.manager._read(self.session)
        self.assertFalse(snapshots[0]["snapshotStable"])

    def test_exited_worker_never_reports_cached_pause_as_stable(self):
        self.process.poll.return_value = 1
        status = self.manager.status("one")
        self.assertFalse(status["snapshotStable"])
        self.assertEqual(status["state"], "disconnected")

    def test_failed_startup_has_no_permanently_running_job(self):
        process = Mock()
        process.poll.return_value = None
        process.stdin.write.side_effect = BrokenPipeError()
        with patch("kernel_backend.sessions.subprocess.Popen", return_value=process), \
                patch("kernel_backend.sessions.threading.Thread"):
            with self.assertRaises(KernelError):
                self.manager.open("remote", connection="net:port=50001,key=secret")
        failed = next(s for s in self.manager.sessions.values() if s is not self.session)
        self.assertEqual(failed.status["state"], "failed")
        self.assertTrue(all(j["state"] == "failed" for j in failed.jobs.values()))
        self.assertIsNone(failed.active)

    def test_interrupt_requires_real_boolean(self):
        self.session.full_access = False
        for value in ("false", 1, [], None):
            with self.assertRaises(KernelError):
                self.manager.interrupt("one", exit_wait=value)
        self.assertEqual(self.process.stdin.getvalue(), b"")

    def test_user_dump_rejected_before_spawning(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "user.dmp"
            path.write_bytes(b"MDMP" + b"\0" * 4096)
            with patch("kernel_backend.sessions.subprocess.Popen") as popen:
                with self.assertRaises(KernelError):
                    self.manager.open("dump", dump_path=str(path))
                popen.assert_not_called()

    def test_conflicting_endpoint_rejected_before_spawn_and_never_leaks_key(self):
        self.session.connection_id = connection_identity("net:port=50000,key=secret")
        with patch("kernel_backend.sessions.subprocess.Popen") as popen:
            with self.assertRaises(KernelError):
                self.manager.open("remote", connection="net:key=different,port=50000")
            popen.assert_not_called()
        self.assertNotIn("secret", json.dumps(self.manager.status("one")))


class WorkerSchedulingTests(unittest.TestCase):
    def run_worker(self, running=False):
        from kernel_backend import worker
        import queue
        ready, waited = threading.Event(), threading.Event()
        timeline = []
        engine = Mock()
        engine.output.result.return_value = {"output": "", "truncated": False}
        def open_engine(config):
            ready.set()
            return {"state": "running" if running else "paused"}
        engine.open.side_effect = open_engine
        engine.status.return_value = {"state": "paused"}
        engine.wait.side_effect = lambda: (timeline.append("wait"), waited.set())
        real_queue = queue.Queue
        class RecordingQueue(real_queue):
            def get(self, *args, **kwargs):
                timeline.append("get")
                return super().get(*args, **kwargs)
        config = {"mode": "remote", "connection": "net:port=50000,key=secret",
                  "full_access": True, "open_id": "open"}
        def lines():
            yield json.dumps(config).encode()
            ready.wait(1)
            if running:
                waited.wait(1)
            else:
                waited.wait(0.2)  # Observe a genuinely idle interval.
            yield b'{"id":"close","operation":"close","arguments":{}}'
        output = io.BytesIO()
        threads = []
        real_thread = threading.Thread
        def start_thread(*args, **kwargs):
            thread = real_thread(*args, **kwargs)
            threads.append(thread)
            return thread
        with patch.object(worker, "raw_lines", lines), patch.object(worker, "Engine", return_value=engine), \
                patch.object(worker.sys, "stdout", SimpleNamespace(buffer=output)), \
                patch.object(worker.queue, "Queue", RecordingQueue), patch.object(worker.threading, "Timer"), \
                patch.object(worker.threading, "Thread", side_effect=start_thread):
            worker.run()
            for thread in threads:
                thread.join(1)
                self.assertFalse(thread.is_alive())
        self.assertIn(b'"state": "closed"', output.getvalue())
        return engine, timeline

    def test_paused_worker_has_no_periodic_native_status_calls(self):
        engine, timeline = self.run_worker()
        engine.status.assert_not_called()

    def test_running_worker_enters_native_wait_before_queue_poll(self):
        engine, timeline = self.run_worker(running=True)
        self.assertEqual(timeline[0], "wait")
        engine.wait.assert_called_once()


class InstallerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.home = Path(self.temp.name)
        self.environment = patch.dict(os.environ, {"CODEX_HOME": ""})
        self.environment.start()
        (self.home / ".codex").mkdir()
        self.config = self.home / ".codex/config.toml"
        self.original = '[mcp_servers.ida]\ncommand="existing-ida"\n[mcp_servers.VS]\ncommand="existing-vs"\n'
        self.config.write_text(self.original)

    def tearDown(self):
        self.environment.stop()
        self.temp.cleanup()

    def test_kernel_only_install_preserves_ida_and_vs(self):
        result = installer.install(self.home, clients=("codex",), backends=("kernel",))
        config = tomllib.loads(self.config.read_text())
        self.assertEqual(config["mcp_servers"]["ida"]["command"], "existing-ida")
        self.assertEqual(config["mcp_servers"]["VS"]["command"], "existing-vs")
        server = Path(result["server"])
        self.assertTrue(server.is_file())
        deploy = server.parents[1]
        self.assertFalse((deploy / "ida_backend/worker.py").exists())
        self.assertFalse((deploy / "ida_backend/vendor/ida_pro_mcp/ida_mcp/api_debug.py").exists())
        self.assertTrue((deploy / "kernel_backend/engine.py").is_file())
        fresh = subprocess.run([sys.executable, str(server)], input=b'{"jsonrpc":"2.0","id":1,"method":"tools/list"}\n', capture_output=True, timeout=10, cwd=self.home)
        self.assertEqual(fresh.returncode, 0, fresh.stderr)
        self.assertIn(b"kernel_open_dump", fresh.stdout)
        self.assertNotIn(b"ida_open", fresh.stdout)

    def test_kernel_edit_is_preserved_and_blocks_reinstall(self):
        installer.install(self.home, clients=("codex",), backends=("kernel",))
        engine = self.home / ".ai-debugger/kernel/kernel_backend/engine.py"
        engine.write_text("local change")
        with self.assertRaises(ValueError):
            installer.install(self.home, clients=("codex",), backends=("kernel",))
        self.assertEqual(engine.read_text(), "local change")

    def test_kernel_manifest_failure_rolls_back_all_backends_and_config(self):
        original_write = installer.atomic_write
        def fail(path, data):
            if path.name == "install-manifest.json" and path.parent.name == "kernel":
                raise OSError("injected kernel failure")
            return original_write(path, data)
        with patch.object(installer, "atomic_write", side_effect=fail):
            with self.assertRaises(OSError):
                installer.install(self.home, clients=("codex",))
        self.assertEqual(self.config.read_text(), self.original)
        self.assertFalse((self.home / ".ai-debugger/kernel/kernel_backend/engine.py").exists())
        self.assertFalse((self.home / ".ai-debugger/ida/mcp_server/ida_server.py").exists())

    def test_kernel_quoted_table_cannot_silently_duplicate(self):
        with self.assertRaises(ValueError):
            installer.replace_backend_toml('[mcp_servers."kernel"]\ncommand="old"\n', sys.executable, "server", "kernel")


class NativeRuntimePreparationTests(unittest.TestCase):
    def test_prepare_is_idempotent_and_protects_local_edits(self):
        from tools.prepare_dbgeng import prepare
        import struct
        with tempfile.TemporaryDirectory() as directory:
            source, target = Path(directory) / "source", Path(directory) / "target"
            source.mkdir()
            image = bytearray(128)
            image[:2] = b"MZ"
            struct.pack_into("<I", image, 0x3c, 64)
            image[64:68] = b"PE\0\0"
            struct.pack_into("<H", image, 68, 0x8664)
            (source / "dbgeng.dll").write_bytes(image)
            (source / "winext").mkdir()
            (source / "winext/ext.dll").write_bytes(b"fixture")
            first = prepare(source, target)
            self.assertEqual(first["fileCount"], 2)
            prepare(source, target)
            (source / "winext/ext.dll").unlink()
            prepare(source, target)
            self.assertFalse((target / "winext/ext.dll").exists())
            original = (target / "aidb-runtime.json").read_bytes()
            (target / "dbgeng.dll").write_bytes(b"local")
            with self.assertRaises(ValueError):
                prepare(source, target)
            self.assertEqual((target / "aidb-runtime.json").read_bytes(), original)
            self.assertEqual((target / "dbgeng.dll").read_bytes(), b"local")

    def test_preparation_failure_rolls_back_written_runtime(self):
        from tools import prepare_dbgeng
        import struct
        with tempfile.TemporaryDirectory() as directory:
            source, target = Path(directory) / "source", Path(directory) / "target"
            source.mkdir()
            image = bytearray(128)
            image[:2] = b"MZ"
            struct.pack_into("<I", image, 0x3c, 64)
            image[64:68] = b"PE\0\0"
            struct.pack_into("<H", image, 68, 0x8664)
            (source / "dbgeng.dll").write_bytes(image)
            write = prepare_dbgeng.atomic_write
            def fail(path, content):
                if path.name == "aidb-runtime.json":
                    raise OSError("injected failure")
                write(path, content)
            with patch.object(prepare_dbgeng, "atomic_write", side_effect=fail):
                with self.assertRaises(OSError):
                    prepare_dbgeng.prepare(source, target)
            self.assertFalse((target / "dbgeng.dll").exists())


if __name__ == "__main__":
    unittest.main()
