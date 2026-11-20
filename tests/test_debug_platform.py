"""Verify that platform discovery links to callable tools without mixing targets."""
from io import BytesIO
import json
from pathlib import Path
import struct
import subprocess
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

from debug_backend.errors import DebugEngineError
from windows_backend.dump import UserDump, memory_range, read_exception_record


ROOT = Path(__file__).resolve().parents[1]


class DebugPlatformTests(unittest.TestCase):
    def test_platform_catalog_matches_mcp_tools(self):
        for platform, views, direct in (
            ("windows", {"dump"}, "windows_dump_disassemble"),
            ("android", {"native", "java"}, "android_java_inspect"),
        ):
            with self.subTest(platform=platform):
                calls = [
                    ("tools/list", {}),
                    ("tools/call", {"name": "debug_capabilities", "arguments": {"domain": "inspection"}}),
                    ("tools/call", {"name": "debug_inspect", "arguments": {
                        "target": "unused", "query": "stack", "view": "wrong-platform"}}),
                ]
                requests = "".join(json.dumps(dict(jsonrpc="2.0", id=index, method=method, params=params)) + "\n"
                                   for index, (method, params) in enumerate(calls))
                child = subprocess.run([sys.executable, str(ROOT / "mcp_server" / f"{platform}_server.py")],
                                       input=requests, cwd=ROOT, capture_output=True, encoding="utf-8", timeout=10)
                self.assertEqual(0, child.returncode, child.stderr)
                responses = {message["id"]: message["result"]
                             for message in map(json.loads, child.stdout.splitlines())}
                tools = {tool["name"] for tool in responses[0]["tools"]}
                catalog = responses[1]["structuredContent"]
                self.assertEqual(platform, catalog["platform"])
                self.assertEqual(views, set(catalog["inspectionViews"]))
                self.assertIn(direct, catalog["directTools"])
                self.assertLessEqual(set(catalog["directTools"]), tools)
                for view in catalog["inspectionViews"].values():
                    self.assertLessEqual(set(view["directToolsByQuery"].values()), tools)
                self.assertTrue(responses[2]["isError"])

    def test_windows_dump_memory_bounds(self):
        self.assertEqual(2**64 - 1, memory_range("0xffffffffffffffff", 1))
        for address, size in (("0xffffffffffffffff", 2), ("0x00; q", 1), ("0x0", 65537), ("0x0", True)):
            with self.subTest(address=address, size=size):
                with self.assertRaises(ValueError):
                    memory_range(address, size)

    def test_android_inspection_uses_only_views_attached_to_the_session(self):
        from mcp_server import android_server

        session = SimpleNamespace(connection=SimpleNamespace(mode="java"))
        submit = Mock(return_value={"jobId": "java-job"})
        with patch.object(android_server.sessions, "sessions", {"test-session": session}), \
                patch.object(android_server.sessions, "submit", submit):
            result = android_server.platform.inspect("test-session", "stack", "")
            self.assertEqual({"jobId": "java-job"}, result)
            self.assertEqual(("test-session", "java_inspect"), submit.call_args.args)
            submit.reset_mock()
            with self.assertRaises(DebugEngineError):
                android_server.platform.inspect("test-session", "stack", "native")
            submit.assert_not_called()
            session.connection.mode = "dual"
            android_server.platform.inspect("test-session", "stack", "")
            self.assertEqual(("test-session", "inspect"), submit.call_args.args)

    def test_dump_without_stored_exception_uses_only_default_context_for_triage(self):
        dump = UserDump.__new__(UserDump)
        dump.exception_record = None
        dump.has_exception_context = Mock(return_value=False)
        dump.execute_query = Mock(side_effect=lambda command: {"command": command})
        result = dump.inspect("triage", 0, True)
        self.assertEqual("unavailable", result["exceptionRecord"])
        self.assertEqual("default", result["context"])
        self.assertEqual(["kv", "lm", "r"], [s["command"] for s in result["sections"]])
        for query, code in (("exception", "EXCEPTION_RECORD_UNAVAILABLE"),
                            ("stack", "EXCEPTION_CONTEXT_UNAVAILABLE")):
            with self.subTest(query=query):
                dump.execute_query.reset_mock()
                with self.assertRaises(DebugEngineError) as caught:
                    dump.inspect(query, 0, True)
                self.assertEqual(code, caught.exception.code)
                dump.execute_query.assert_not_called()

    def test_dump_uses_stored_exception_and_native_frame_selection(self):
        dump = UserDump.__new__(UserDump)
        dump.exception_record = {"threadId": 8, "code": "0xe0424242", "address": "0x1234"}
        dump.has_exception_context = Mock(return_value=True)
        dump.execute_query = Mock(side_effect=lambda command: {
            "command": command, "hresult": "0x00000000", "debuggerError": False,
        })
        dump.symbols = Mock()
        result = dump.inspect("triage", 0, True)
        self.assertEqual("exception", result["context"])
        self.assertEqual(dump.exception_record, result["storedException"])
        self.assertEqual([".exr -1", ".ecxr", "kv", "lm", "r"], [s["command"] for s in result["sections"]])

        dump.symbols.call.return_value = 0
        self.assertEqual(16, dump.inspect("locals", 16, False)["frame"])
        self.assertEqual(16, dump.symbols.call.call_args.args[-1])
        dump.execute_query.reset_mock()
        dump.symbols.call.return_value = -2147024809  # E_INVALIDARG
        with self.assertRaises(DebugEngineError) as caught:
            dump.inspect("locals", 256, False)
        self.assertEqual("FRAME_UNAVAILABLE", caught.exception.code)
        dump.execute_query.assert_not_called()

    def test_minidump_exception_record_must_exist_in_a_complete_stream(self):
        header = struct.pack("<4s7I", b"MDMP", 0, 1, 32, 0, 0, 0, 0)
        entry = struct.pack("<III", 6, 168, 44)
        record = struct.pack("<IIIIQQ", 8, 0, 0xe0424242, 0, 0, 0x1234).ljust(168, b"\0")
        self.assertEqual({"threadId": 8, "code": "0xe0424242", "address": "0x1234"},
                         read_exception_record(BytesIO(header + entry + record)))
        self.assertIsNone(read_exception_record(BytesIO(header + struct.pack("<III", 7, 0, 0))))
        with self.assertRaisesRegex(ValueError, "Truncated minidump exception stream"):
            read_exception_record(BytesIO(header + entry + record[:-1]))
