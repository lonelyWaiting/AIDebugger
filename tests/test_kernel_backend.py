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
