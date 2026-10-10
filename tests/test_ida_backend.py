import ast
import hashlib
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
from ida_backend.transport import IdaError, tool_value
from ida_backend.stdio import serve
from tools import install_aidebugger as installer


class IdaLifecycleTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir=ROOT / ".tmp")
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

    def test_mutation_timeout_unknown_never_retried(self):
        with patch.object(self.manager, "_rpc_tool", side_effect=[self.result(self.identity), TimeoutError]) as rpc:
            with self.assertRaisesRegex(IdaError, "IDA_OUTCOME_UNKNOWN"):
                self.manager.call("target", "patch", {"patches": []})
            self.assertEqual(rpc.call_count, 2)  # one identity query, one mutation
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
        with patch.object(self.manager, "_rpc_tool", side_effect=[self.result(self.identity), self.result({"accepted": True, "saved": False})]):
            with self.assertRaisesRegex(IdaError, "IDA_OUTCOME_UNKNOWN"):
                self.manager.close("target")
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

    def test_unmapped_native_visitor_patch_cannot_create_partial_output(self):
        with tempfile.TemporaryDirectory(dir=ROOT / ".tmp") as directory:
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
        self.temp = tempfile.TemporaryDirectory(dir=ROOT / ".tmp")
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
