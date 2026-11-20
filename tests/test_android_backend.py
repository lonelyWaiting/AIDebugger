"""Contracts for Android deployment, target validation, and async ownership."""
import io
import os
import queue
import socket
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import Mock, patch

from android_backend.jdwp import HEADER, VM_DEATH, Cursor, JavaLost, JavaSession, u4
from android_backend.jobs import Budget, JobStopped, checkpoint, current_job
from android_backend.runtime import AndroidError, NDK, Runtime, destination, provision, runtime_member
from android_backend.sessions import Sessions
from android_backend.transport import Connection, LldbWorker, WorkerLost, paths, process_name, process_start_time
from tools import install_aidebugger as installer


class AndroidRuntimeTests(unittest.TestCase):
    def test_extract_only_private_debug_runtime_and_reject_traversal(self):
        prefix = NDK + "/toolchains/llvm/prebuilt/windows-x86_64/"
        for component in ("bin/lldb.exe", "bin/liblldb.dll", "python3/Lib/encodings/__init__.py",
                          "lib/clang/18/lib/linux/x86_64/lldb-server"):
            self.assertEqual(runtime_member(prefix + component).as_posix(), "llvm/" + component)
        self.assertIsNone(runtime_member(prefix + "bin/clang.exe"))
        self.assertIsNone(runtime_member("platform-tools/fastboot.exe"))
        with self.assertRaises(AndroidError):
            runtime_member("platform-tools/../adb.exe")

    @unittest.skipUnless(os.name == "nt", "Managed Android runtime targets Windows")
    def test_cached_runtime_is_revalidated_before_reuse(self):
        with tempfile.TemporaryDirectory() as directory:
            install_root = Path(directory)
            destination(install_root).mkdir(parents=True)
            with patch("android_backend.runtime.platform.machine", return_value="AMD64"), \
                    patch.object(Runtime, "smoke", side_effect=AndroidError("missing DLL")) as smoke:
                with self.assertRaisesRegex(AndroidError, "Cached Android runtime.*missing DLL"):
                    provision(install_root)
                smoke.assert_called_once()

    @unittest.skipIf(sys.version_info < (3, 11), "Installer needs Python 3.11")
    def test_android_installer_preserves_existing_unrelated_registrations(self):
        with tempfile.TemporaryDirectory() as directory:
            home = Path(directory)
            codex = home / ".codex"
            codex.mkdir()
            config = codex / "config.toml"
            before = '[mcp_servers.AIDebuggerVS]\ncommand = "vs"\n'
            config.write_text(before, encoding="utf-8")
            with patch.dict(os.environ, {"CODEX_HOME": str(codex)}), \
                    patch("android_backend.runtime.provision") as provision:
                installer.install(home, clients=("codex",), backends=("android",), dry_run=True)
                provision.assert_not_called()
                receipt = installer.install(home, clients=("codex",), backends=("android",),
                                            android_archives=home)
                provision.assert_called_once_with(home / ".ai-debugger/android",
                                                  accept_licenses=False, archives=home)
            self.assertTrue(config.read_text(encoding="utf-8").startswith(before))
            self.assertFalse((home / ".ai-debugger/android/ida_backend/worker.py").exists())
            server = Path(receipt["server"])
            result = subprocess.run(
                [sys.executable, str(server)], input='{"jsonrpc":"2.0","id":1,"method":"tools/list"}\n',
                capture_output=True, text=True, check=True, timeout=10)
            self.assertIn('"name": "android_attach"', result.stdout)


class AndroidSessionTests(unittest.TestCase):
    def test_target_must_be_package_or_its_named_subprocess(self):
        self.assertEqual(process_name("com.example.debug", ""), "com.example.debug")
        self.assertEqual(process_name("com.example.debug", "com.example.debug:worker"),
                         "com.example.debug:worker")
        for package, process in (("com.example;id", ""), ("com.example", "other.example"),
                                 ("com.example", "com.example:;id")):
            with self.assertRaises(ValueError):
                process_name(package, process)

    def test_pid_identity_uses_kernel_start_time_after_the_full_comm_field(self):
        fields = ["S", *map(str, range(4, 23)), "23"]
        stat = "4242 (includes ) delimiters) " + " ".join(fields)
        self.assertEqual(process_start_time(stat), "22")
        with self.assertRaises(AndroidError):
            process_start_time("4242 (truncated) S 4 5")

    def test_startup_wait_uses_the_first_successful_pid_snapshot(self):
        connection = Connection(Mock(), "id", "serial", "com.example", "com.example",
                                None, "", [], {})
        connection.adb = Mock()
        connection.adb.shell.side_effect = [(1, "1234"), (0, "1234")]
        connection.adb.run.return_value = (0, "serial")
        self.assertEqual(connection.resolve_pids(wait=True), ["1234"])
        self.assertEqual(connection.adb.shell.call_count, 2)

    def test_path_arguments_reject_debugger_expansion(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(ValueError):
                paths([], {"`build/path`": directory})

    def test_worker_protocol_ignores_untagged_process_output(self):
        worker = LldbWorker.__new__(LldbWorker)
        worker.marker = b"AIDB_LLDB private-token "
        worker.process = Mock(stdout=io.BytesIO(
            b'AIDB_LLDB {"result":{"state":"spoof"}}\n'
            b'AIDB_LLDB private-token {"result":{"state":"stopped"}}\n'))
        worker.replies = queue.Queue()
        worker.read()
        self.assertEqual(worker.receive(0.01), {"result": {"state": "stopped"}})
        with self.assertRaises(WorkerLost):
            worker.receive(0.01)

    def test_cleanup_never_uses_a_reassigned_adb_transport(self):
        connection = Connection(Mock(), "id", "expected", "com.example", "com.example",
                                42, "arm64-v8a", [], {})
        connection.remote = "/data/user/0/com.example/.aidb-lldb-id"
        connection.staging = "/data/local/tmp/aidb-lldb-id"
        connection.adb = Mock()
        connection.adb.run.return_value = (0, "different")
        connection.worker = Mock()
        self.assertRegex(connection.cleanup()[0], "no longer matches")
        connection.adb.run.assert_called_once_with("get-serialno")
        connection.adb.shell.assert_not_called()
        connection.worker.close.assert_called_once_with()

    def test_symbol_read_recovery_refuses_a_rebooted_device(self):
        connection = Connection(Mock(), "id", "serial", "com.example", "com.example",
                                42, "arm64-v8a", [], {})
        original = connection.adb = Mock()
        connection.boot_id = "previous boot"
        discover, candidate = Mock(), Mock()
        discover.devices.return_value = [{"serial": "serial", "state": "device", "transportId": "2"}]
        candidate.run.return_value = (0, "serial")
        candidate.shell.return_value = (0, "new boot")
        with patch("android_backend.transport.Adb", side_effect=[discover, candidate]):
            with self.assertRaisesRegex(AndroidError, "rebooted or changed"):
                connection.recover_read_transport()
        self.assertIs(connection.adb, original)

    def test_cleanup_retains_a_stub_still_tracing_the_app_and_only_removes_its_port(self):
        connection = Connection(Mock(), "id", "serial", "com.example", "com.example",
                                42, "arm64-v8a", [], {})
        connection.remote = "/data/user/0/com.example/.aidb-lldb-id"
        connection.port = 1234
        connection.forward_requested = True
        connection.attach_requested = True
        connection.stub = Mock(poll=Mock(return_value=1))
        connection.stub_pid = "99"
        connection.adb = Mock()
        connection.adb.run.side_effect = [
            (0, "serial"),
            (0, f"serial tcp:1234 {connection.forward}\nserial tcp:1235 {connection.forward}"),
            (0, ""),
        ]
        connection.adb.shell.side_effect = AndroidError("Stub is still tracing the app")
        self.assertEqual(connection.cleanup(), ["Stub is still tracing the app"])
        connection.adb.shell.assert_called_once()
        connection.adb.run.assert_called_with("forward", "--remove", "tcp:1234")

    def test_attach_jobs_are_pollable_and_worker_loss_releases_ownership(self):
        release = threading.Event()
        connection = Mock(abi="arm64-v8a", serial="serial", process_name="com.example")
        connection.attach.side_effect = lambda: (release.wait(5), {"state": "stopped"})[1]
        connection.cleanup.return_value = ["device disconnected during cleanup"]
        connection.worker.call.side_effect = WorkerLost("worker disconnected")
        connection.native_control.side_effect = connection.worker.call
        sessions = Sessions(Mock())

        def wait(receipt):
            deadline = time.monotonic() + 5
            while time.monotonic() < deadline:
                job = sessions.job(receipt["sessionId"], receipt["jobId"])
                if job["state"] != "pending":
                    return job
                time.sleep(0.01)
            self.fail("Android job did not finish")

        with patch("android_backend.sessions.Connection", return_value=connection):
            try:
                receipt = sessions.open("serial", "com.example", "", None, "", [], {})
                session_id = receipt["sessionId"]
                self.assertEqual(sessions.job(session_id, receipt["jobId"])["state"], "pending")
                with self.assertRaisesRegex(AndroidError, "already owns"):
                    sessions.open("serial", "com.example", "", None, "", [], {})
                release.set()
                self.assertEqual(wait(receipt)["state"], "completed")
                failed = wait(sessions.submit(session_id, "control", action="continue", thread_id=0))
                self.assertEqual(failed["targetState"], "unknown")
                self.assertEqual(failed["cleanupErrors"], connection.cleanup.return_value)
                self.assertEqual(sessions.listing()["sessions"][0]["phase"], "failed")
                connection.worker.call.assert_called_once()
                connection.cleanup.assert_called_once()
            finally:
                release.set()
                sessions.shutdown()

    def test_cancel_before_attach_reports_progress_and_releases_the_session(self):
        connection = Mock(abi="", serial="serial", process_name="com.example", attach_requested=False)
        connection.cleanup.return_value = []
        began = threading.Event()

        def attach():
            checkpoint("wait_for_launched_process")
            began.set()
            while True:
                checkpoint()
                time.sleep(0.01)

        connection.attach.side_effect = attach
        sessions = Sessions(Mock())
        with patch("android_backend.sessions.Connection", return_value=connection):
            receipt = sessions.open("serial", "com.example", "", None, "", [], {})
            session_id, job_id = receipt["sessionId"], receipt["jobId"]
            try:
                self.assertTrue(began.wait(2))
                self.assertEqual(sessions.job(session_id, job_id)["stage"], "wait_for_launched_process")
                self.assertTrue(sessions.cancel(session_id, job_id)["cancellationRequested"])
                deadline = time.monotonic() + 2
                while time.monotonic() < deadline:
                    job = sessions.job(session_id, job_id)
                    if job["state"] != "pending":
                        break
                    time.sleep(0.01)
                self.assertEqual(job["state"], "cancelled")
                self.assertNotIn("targetState", job)
                connection.cleanup.assert_called_once()
            finally:
                sessions.cancel(session_id, job_id)
                sessions.shutdown()


class JdwpTests(unittest.TestCase):
    def test_vm_death_is_observed_before_the_socket_eof(self):
        reader, peer = socket.socketpair()
        with reader, peer:
            reader.settimeout(1)
            session = JavaSession.__new__(JavaSession)
            session.socket, session.buffer, session.events = reader, bytearray(), []
            session.state, session.stop = "running", None
            event = b"\0" + u4(1) + bytes([VM_DEATH]) + u4(0)
            peer.sendall(HEADER.pack(11 + len(event), 7, 0, 64, 100) + event)
            peer.shutdown(socket.SHUT_WR)
            session.sync()
            self.assertEqual(session.state, "exited")

    def test_cancelled_event_processing_marks_the_stream_unusable(self):
        session = JavaSession.__new__(JavaSession)
        session.state, session.events = "stopped", [b"jdwp-event"]
        session.handle_events = Mock(side_effect=JobStopped("cancelled"))
        with self.assertRaisesRegex(JavaLost, "suspension state is unknown"):
            session.sync()

    def test_incomplete_packet_keeps_header_when_deadline_expires(self):
        session = JavaSession.__new__(JavaSession)
        session.buffer = bytearray(HEADER.pack(14, 1, 128, 0, 0) + b"x")
        expected = session.buffer[:]
        token = current_job.set(Budget(0))
        try:
            with self.assertRaises(JobStopped):
                session.packet(time.monotonic() + 1)
        finally:
            current_job.reset(token)
        self.assertEqual(session.buffer, expected)

    def test_truncated_jdwp_value_is_rejected(self):
        with self.assertRaises(JavaLost):
            Cursor(b"\0\0\0\x06short").text()
