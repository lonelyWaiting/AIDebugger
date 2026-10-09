import asyncio
import base64
import copy
import ctypes
from ctypes import wintypes
import importlib.util
import json
import os
import pathlib
import re
import shutil
import stat
import subprocess
import sys
import tempfile
import threading
import time
import unittest
import zipfile
from unittest import mock


ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))


class InfrastructureBehaviorTests(unittest.TestCase):
    @staticmethod
    def _ide_instance(
        process_id: int,
        *,
        paused: bool = False,
        debugging: bool = False,
        connection_status: str = "ok",
    ):
        from mcp_server.vs_bridge import VSInstance

        return VSInstance(
            f"AIDebuggerVS_{process_id}",
            {
                "processId": process_id,
                "solutionName": f"Solution{process_id}",
                "solutionPath": f"C:/src/Solution{process_id}.sln",
                "isDebugging": debugging or paused,
                "isPaused": paused,
                "debugMode": "break" if paused else ("run" if debugging else "design"),
                "currentProcess": "game.exe" if debugging or paused else "",
                "vsVersion": "17.0",
                "connectionStatus": connection_status,
                "lastError": "" if connection_status == "ok" else "unreachable",
                "allowedCapabilities": ["server.control", "debug.read"],
                "accessMode": "read_only",
                "processStartTimeUtcTicks": process_id * 1000,
                "serverInstanceId": f"server-{process_id}",
            },
        )

    @staticmethod
    def _tool(tool):
        """Return a tool's undecorated function, bypassing MCP output validation.

        Behavior tests inject deliberately partial backend fixtures that the
        structured output contract would reject; the output contract itself is
        covered by the schema/registry tests. This mirrors the pre-refactor
        wrapper, which returned the raw tool payload.
        """
        return getattr(tool, "__wrapped__", tool)

    @staticmethod
    def _payload(response):
        return response.get("result", response) if isinstance(response, dict) else response

    def test_dependency_extraction_lock_serializes_same_stamp(self) -> None:
        from mcp_server import bootstrap

        with tempfile.TemporaryDirectory() as temp:
            active = 0
            maximum = 0
            state_lock = threading.Lock()
            barrier = threading.Barrier(2)

            def worker() -> None:
                nonlocal active, maximum
                barrier.wait()
                with bootstrap._dependency_lock("f" * 64, pathlib.Path(temp)):
                    with state_lock:
                        active += 1
                        maximum = max(maximum, active)
                    time.sleep(0.1)
                    with state_lock:
                        active -= 1

            threads = [threading.Thread(target=worker) for _ in range(2)]
            for thread in threads:
                thread.start()
            for thread in threads:
                thread.join(timeout=5)
                self.assertFalse(thread.is_alive())
            self.assertEqual(1, maximum)

    def test_dependency_bootstrap_fails_closed_when_bundle_is_missing(self) -> None:
        from mcp_server import bootstrap

        original = bootstrap._DEPS_ENSURED
        try:
            bootstrap._DEPS_ENSURED = False
            with tempfile.TemporaryDirectory() as temporary, \
                 mock.patch.object(
                     bootstrap,
                     "_bundled_wheels_dir",
                     return_value=pathlib.Path(temporary) / "missing",
                 ):
                with self.assertRaisesRegex(RuntimeError, "refusing to use ambient"):
                    bootstrap.ensure_deps()
            self.assertFalse(bootstrap._DEPS_ENSURED)
        finally:
            bootstrap._DEPS_ENSURED = original

    def test_dependency_bundle_requires_exact_manifest_wheel_set(self) -> None:
        from mcp_server import bootstrap

        digest = "a" * 64
        manifest = {
            "wheels": [
                {"file": "vendor/wheels/one-1.0-py3-none-any.whl", "sha256": digest},
                {"file": "vendor/wheels/two-1.0-py3-none-any.whl", "sha256": digest},
            ],
        }
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            (root / "one-1.0-py3-none-any.whl").write_bytes(b"one")
            with self.assertRaisesRegex(RuntimeError, "does not exactly match"):
                bootstrap._select_wheels(root, manifest)

    def test_dependency_manifest_rejects_multiple_distribution_versions(self) -> None:
        from mcp_server import bootstrap

        digest = "b" * 64
        manifest = {
            "wheels": [
                {"file": "vendor/wheels/demo-1.0-py3-none-any.whl", "sha256": digest},
                {"file": "vendor/wheels/demo-2.0-py3-none-any.whl", "sha256": digest},
            ],
        }
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            for entry in manifest["wheels"]:
                (root / pathlib.Path(entry["file"]).name).write_bytes(b"wheel")
            with self.assertRaisesRegex(RuntimeError, "multiple versions"):
                bootstrap._select_wheels(root, manifest)

    def test_valid_instance_registration_decodes_auth_token(self) -> None:
        from mcp_server import vs_trust

        pipe_name = "AIDebuggerVS_42"
        payload = {
            "protocol": "aidb/2",
            "pipeName": pipe_name,
            "controlPipeName": pipe_name + "_control",
            "processId": 42,
            "processStartTimeUtcTicks": 123456,
            "serverInstanceId": "a" * 32,
            "protocolCatalogHash": vs_trust.DEBUG_METHOD_CATALOG_HASH,
            "authToken": base64.b64encode(b"x" * 32).decode("ascii"),
            "capabilities": ["server.control", "debug.read"],
        }
        with tempfile.TemporaryDirectory() as temporary:
            path = pathlib.Path(temporary) / "AIDebugger" / "instances" / f"{pipe_name}.json"
            path.parent.mkdir(parents=True)
            path.write_text(json.dumps(payload), encoding="utf-8")
            with mock.patch.dict(os.environ, {"LOCALAPPDATA": temporary}), \
                 mock.patch.object(vs_trust, "_validate_registration_security"), \
                 mock.patch.object(vs_trust, "_validate_visual_studio_process"):
                registration = vs_trust._load_instance_registration(pipe_name)

        self.assertEqual(payload["authToken"], registration["authToken"])
        self.assertEqual(payload["capabilities"], registration["capabilities"])

    @unittest.skipUnless(sys.platform == "win32", "Windows process-time ABI")
    def test_vs_client_process_time_declares_pointer_sized_kernel32_signature(self) -> None:
        from mcp_server import vs_trust

        expected_filetime = 0x123456789

        class FakeKernel32:
            def __init__(self) -> None:
                self.GetCurrentProcess = mock.Mock(return_value=wintypes.HANDLE(-1))
                self.GetProcessTimes = mock.Mock(side_effect=self._get_process_times)

            @staticmethod
            def _get_process_times(handle, creation, exit_time, kernel, user):
                filetime = ctypes.cast(
                    creation,
                    ctypes.POINTER(wintypes.FILETIME),
                ).contents
                filetime.dwLowDateTime = expected_filetime & 0xFFFFFFFF
                filetime.dwHighDateTime = expected_filetime >> 32
                return True

        fake_kernel32 = FakeKernel32()
        previous = vs_trust._CLIENT_PROCESS_START_TICKS
        try:
            vs_trust._CLIENT_PROCESS_START_TICKS = None
            with mock.patch.object(vs_trust.ctypes, "WinDLL", return_value=fake_kernel32):
                actual = vs_trust._current_process_start_time_utc_ticks()
        finally:
            vs_trust._CLIENT_PROCESS_START_TICKS = previous

        self.assertEqual(expected_filetime + 504_911_232_000_000_000, actual)
        self.assertEqual([], fake_kernel32.GetCurrentProcess.argtypes)
        self.assertIs(wintypes.HANDLE, fake_kernel32.GetCurrentProcess.restype)
        self.assertIs(wintypes.BOOL, fake_kernel32.GetProcessTimes.restype)
        self.assertEqual(
            [
                wintypes.HANDLE,
                ctypes.POINTER(wintypes.FILETIME),
                ctypes.POINTER(wintypes.FILETIME),
                ctypes.POINTER(wintypes.FILETIME),
                ctypes.POINTER(wintypes.FILETIME),
            ],
            fake_kernel32.GetProcessTimes.argtypes,
        )

    def test_instance_registration_rejects_protocol_catalog_mismatch(self) -> None:
        from mcp_server import vs_trust

        pipe_name = "AIDebuggerVS_42"
        payload = {
            "protocol": "aidb/2",
            "pipeName": pipe_name,
            "controlPipeName": pipe_name + "_control",
            "recoveryPipeName": pipe_name + "_recovery",
            "processId": 42,
            "processStartTimeUtcTicks": 123456,
            "serverInstanceId": "a" * 32,
            "protocolCatalogHash": "0" * 64,
            "authToken": base64.b64encode(b"x" * 32).decode("ascii"),
            "capabilities": ["server.control", "debug.read"],
        }
        with tempfile.TemporaryDirectory() as temporary:
            path = pathlib.Path(temporary) / "AIDebugger" / "instances" / f"{pipe_name}.json"
            path.parent.mkdir(parents=True)
            path.write_text(json.dumps(payload), encoding="utf-8")
            with mock.patch.dict(os.environ, {"LOCALAPPDATA": temporary}), \
                 mock.patch.object(vs_trust, "_validate_registration_security"), \
                 mock.patch.object(vs_trust, "_validate_visual_studio_process"):
                with self.assertRaises(vs_trust.VSBridgeError) as raised:
                    vs_trust._load_instance_registration(pipe_name)

        self.assertEqual("PROTOCOL_CATALOG_MISMATCH", raised.exception.code)

    def test_vs_instance_summary_is_an_exclusive_partition(self) -> None:
        from mcp_server.tool_modules import vs_instance_tools

        instances = [
            self._ide_instance(1, paused=True),
            self._ide_instance(2, debugging=True),
            self._ide_instance(3),
            self._ide_instance(4, connection_status="unreachable"),
        ]
        summary = vs_instance_tools._summary(instances)
        self.assertEqual({"paused": 1, "debugging": 1, "idle": 1, "unreachable": 1}, summary)
        self.assertEqual(len(instances), sum(summary.values()))

    def test_vs_detect_reports_zero_reachable_without_claiming_ambiguity(self) -> None:
        from mcp_server.tool_modules import vs_instance_tools

        unreachable = self._ide_instance(42, connection_status="unreachable")
        manager = mock.Mock()
        manager.get_selection_snapshot = mock.AsyncMock(return_value=(None, None, None, 0))
        manager.auto_select_instance = mock.Mock(side_effect=AssertionError("must not auto-select"))
        with mock.patch.object(vs_instance_tools.backend, "get_bridge", return_value=manager), \
             mock.patch.object(
                 vs_instance_tools.backend,
                 "discover_instances",
                 mock.AsyncMock(return_value=[unreachable]),
             ):
            payload = asyncio.run(vs_instance_tools.detect_debug_sessions())["result"]

        self.assertEqual(0, payload["reachableCount"])
        self.assertEqual(1, payload["unreachableCount"])
        self.assertEqual("no_reachable_instance", payload["selectionStatus"])
        self.assertIn("No reachable authenticated", payload["message"])
        manager.auto_select_instance.assert_not_called()

    def test_vs_discovery_candidates_come_only_from_bounded_registration_directory(self) -> None:
        from mcp_server import vs_bridge

        with tempfile.TemporaryDirectory() as temporary:
            instances = pathlib.Path(temporary) / "AIDebugger" / "instances"
            instances.mkdir(parents=True)
            for process_id in range(1, vs_bridge.MAX_DISCOVERY_CANDIDATES + 8):
                path = instances / f"AIDebuggerVS_{process_id}.json"
                path.write_text("{}", encoding="utf-8")
                os.utime(path, ns=(process_id, process_id))
            (instances / "AIDebuggerVS_999_control.json").write_text("{}", encoding="utf-8")
            (instances / "not-a-registration.json").write_text("{}", encoding="utf-8")
            with mock.patch.dict(os.environ, {"LOCALAPPDATA": temporary}):
                candidates = vs_bridge._discover_vs_pipes()

        self.assertEqual(vs_bridge.MAX_DISCOVERY_CANDIDATES, len(candidates))
        self.assertTrue(all(re.fullmatch(r"AIDebuggerVS_[1-9][0-9]*", item) for item in candidates))
        self.assertEqual(f"AIDebuggerVS_{vs_bridge.MAX_DISCOVERY_CANDIDATES + 7}", candidates[0])

    def test_packaged_vs_runtime_registry_and_instance_tools_are_self_contained(self) -> None:
        root_files = (
            "bootstrap.py", "debug_method_manifest.json", "output_models.py", "requirements.txt",
            "tool_context.py", "tool_runtime.py", "vs_bridge.py", "vs_errors.py", "vs_session.py",
            "vs_mcp_app.py", "vs_tool_registry.py", "vs_trust.py", "vs_launcher.py", "vs_server.py",
        )
        tool_files = (
            "__init__.py", "capability_tools.py", "vs_instance_tools.py", "snapshot_tools.py",
            "multi_vs_tools.py", "native_managed_tools.py", "control_tools.py", "breakpoint_tools.py",
            "memory_tools.py",
        )
        with tempfile.TemporaryDirectory() as temporary_directory:
            target = pathlib.Path(temporary_directory)
            source = ROOT / "mcp_server"
            for name in root_files:
                shutil.copy2(source / name, target / name)
            (target / "tool_modules").mkdir()
            for name in tool_files:
                shutil.copy2(source / "tool_modules" / name, target / "tool_modules" / name)
            shutil.copytree(source / "vendor", target / "vendor")
            self.assertFalse((target / "tool_registry.py").exists())

            program = r'''
import asyncio, os
os.environ["AIDB_PREFERRED_IDE"] = "vs"
import vs_server, vs_bridge, vs_session, vs_tool_registry

registration = {
    "protocol": "aidb/2", "pipeName": "AIDebuggerVS_42",
    "controlPipeName": "AIDebuggerVS_42_control", "processId": 42,
    "processStartTimeUtcTicks": 42000, "serverInstanceId": "a" * 32,
    "protocolCatalogHash": vs_tool_registry.DEBUG_METHOD_CATALOG_HASH,
    "authToken": "private", "capabilities": list(vs_bridge._all_vs_capabilities()),
}
instance = vs_bridge.VSInstance("AIDebuggerVS_42", {
    "processId": 42, "processStartTimeUtcTicks": 42000, "serverInstanceId": "a" * 32,
    "vsVersion": "18.0", "connectionStatus": "ok",
    "allowedCapabilities": registration["capabilities"], "accessMode": "full_access",
}, registration_snapshot=registration)

class FakeSession:
    def __init__(self):
        self.generation = 0
        self.selected = None
        self.conflict = False
    async def discover_instances(self, preferred_ide=None, timeout=None): return [instance]
    async def get_selection_snapshot(self):
        return ("vs", instance.pipe_name, self.selected, self.generation) if self.selected else (None, None, None, self.generation)
    def get_preferred_ide(self): return "vs"
    def get_candidate_instances(self, instances, preferred_ide=None): return (instances, "vs")
    def auto_select_instance(self, instances, preferred_ide=None): return instances[0] if len(instances) == 1 else None
    async def try_select_instance(self, expected_generation, **kwargs):
        if self.conflict:
            return (False, self.selected, self.generation)
        self.selected = dict(kwargs["target_identity"]); self.generation += 1
        return (True, self.selected, self.generation)
    async def try_clear_selection(self, expected_generation):
        if self.conflict: return (False, self.generation)
        self.selected = None; self.generation += 1; return (True, self.generation)

fake = FakeSession()
vs_session._session = fake

async def check():
    assert vs_bridge._backend_method_kind("stop") == "command"
    assert vs_bridge._backend_method_capabilities("set_address_breakpoint", {"condition": "x"}) == (
        "debug.breakpoint.write", "debug.evaluate.unsafe"
    )
    for name, arguments in (
        ("list_vs_instances", {}),
        ("detect_debug_sessions", {"select": True}),
        ("select_vs_instance", {"process_id": 42}),
        ("debug_capabilities", {}),
    ):
        _, payload = await vs_server.mcp.call_tool(name, arguments)
        assert isinstance(payload["result"], dict), name
    async def exact(target, method, params=None, timeout=10.0, include_target=False, registration_snapshot=None, advance_selection=True):
        assert method == "get_debug_status"
        assert registration_snapshot["processId"] == 42
        assert advance_selection is False
        return {"isDebugging": True}, dict(target)
    vs_bridge.call_pipe_exact = exact
    _, dispatched = await vs_server.mcp.call_tool(
        "dispatch_vs_instances",
        {"method": "get_debug_status", "process_ids": [42]},
    )
    assert dispatched["result"]["success"] is True
    assert dispatched["result"]["results"][0]["target"]["processId"] == 42
    fake.conflict = True
    _, selected = await vs_server.mcp.call_tool("select_vs_instance", {"process_id": 42})
    assert selected["result"]["errorCode"] == "SELECTION_CHANGED"
    _, cleared = await vs_server.mcp.call_tool("clear_vs_selection", {})
    assert cleared["result"]["errorCode"] == "SELECTION_CHANGED"
    fake.selected = None
    _, detected = await vs_server.mcp.call_tool("detect_debug_sessions", {"select": True})
    assert detected["result"]["selectionStatus"] == "selection_changed"

asyncio.run(check())
'''
            environment = os.environ.copy()
            environment["PYTHONDONTWRITEBYTECODE"] = "1"
            result = subprocess.run(
                [sys.executable, "-c", program],
                cwd=target,
                env=environment,
                capture_output=True,
                text=True,
                timeout=45,
            )
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_vs_launcher_signature_matches_cross_language_fixture(self) -> None:
        spec = importlib.util.spec_from_file_location(
            "aidb_vs_launcher_signature_test", ROOT / "mcp_server" / "vs_launcher.py"
        )
        launcher = importlib.util.module_from_spec(spec)
        assert spec.loader is not None
        spec.loader.exec_module(launcher)
        self.assertEqual(
            "3fd3d920468408e15d6356ba7cd450c20903e500e1385b431ecac5223db6b79e",
            launcher._payload_signature(ROOT / "tests" / "fixtures" / "VsBundleSignature"),
        )

    def test_vs_payload_allowlists_agree_across_python_powershell_and_csharp(self) -> None:
        """The VS bundle allowlist is duplicated across four languages; they must match.

        A module added to the bundle but missing from any one copy makes the
        launcher's signature check reject every shipped bundle.
        """
        spec = importlib.util.spec_from_file_location(
            "aidb_vs_launcher_allowlist_test", ROOT / "mcp_server" / "vs_launcher.py"
        )
        launcher = importlib.util.module_from_spec(spec)
        assert spec.loader is not None
        spec.loader.exec_module(launcher)

        def ps_array(text: str, variable: str) -> set:
            match = re.search(
                r"\$" + variable + r"\s*=\s*@\((?P<body>.*?)\)", text, re.DOTALL
            )
            self.assertIsNotNone(match, f"${variable} array not found")
            return set(re.findall(r'"([^"]+)"', match.group("body")))

        def cs_hash_set(text: str, name: str) -> set:
            match = re.search(
                name + r"\s*=\s*new HashSet<string>\([^)]*\)\s*\{(?P<body>.*?)\}",
                text,
                re.DOTALL,
            )
            self.assertIsNotNone(match, f"{name} not found")
            return set(re.findall(r'"([^"]+)"', match.group("body")))

        def cs_root_files(text: str) -> set:
            match = re.search(
                r"IsVsRuntimeRootFile\(string fileName\).*?\n(?P<body>.*?)\n        \}",
                text,
                re.DOTALL,
            )
            self.assertIsNotNone(match, "IsVsRuntimeRootFile not found")
            return set(re.findall(r'case\s+"([^"]+)"', match.group("body")))

        scripts = (
            ("tools/Verify-Vsix.ps1", (ROOT / "tools" / "Verify-Vsix.ps1").read_text(encoding="utf-8")),
            (
                "VSDebuggerMCP/Install-VSIX.ps1",
                (ROOT / "VSDebuggerMCP" / "Install-VSIX.ps1").read_text(encoding="utf-8"),
            ),
        )
        bundle_policy = (
            ROOT / "VSDebuggerMCP" / "Registration" / "BundlePathPolicy.cs"
        ).read_text(encoding="utf-8")

        python_modules = set(launcher._TOOL_MODULE_FILES)
        self.assertIn("multi_vs_tools.py", python_modules)
        self.assertTrue(
            launcher._is_payload_file("tool_modules/multi_vs_tools.py"),
            "vs_launcher must accept the multi-instance dispatch tool module",
        )
        self.assertEqual(
            python_modules,
            cs_hash_set(bundle_policy, "VsToolModuleFiles"),
            "vs_launcher._TOOL_MODULE_FILES and BundlePathPolicy.VsToolModuleFiles disagree",
        )
        for name, script in scripts:
            prefixed = ps_array(script, "toolModules")
            self.assertTrue(
                all(entry.startswith("tool_modules/") for entry in prefixed),
                f"{name} $toolModules entries must be tool_modules/-prefixed",
            )
            self.assertEqual(
                python_modules,
                {entry.split("/", 1)[1] for entry in prefixed},
                f"{name} $toolModules disagrees with vs_launcher._TOOL_MODULE_FILES",
            )

        python_root = set(launcher._ROOT_FILES)
        self.assertEqual(
            python_root,
            cs_root_files(bundle_policy),
            "vs_launcher._ROOT_FILES and BundlePathPolicy.IsVsRuntimeRootFile disagree",
        )
        for name, script in scripts:
            self.assertEqual(
                python_root,
                ps_array(script, "rootFiles"),
                f"{name} $rootFiles disagrees with vs_launcher._ROOT_FILES",
            )

    def test_vs_launcher_validates_canonical_signature_and_honors_explicit_rollback(self) -> None:
        spec = importlib.util.spec_from_file_location(
            "aidb_vs_launcher_test", ROOT / "mcp_server" / "vs_launcher.py"
        )
        launcher = importlib.util.module_from_spec(spec)
        assert spec.loader is not None
        spec.loader.exec_module(launcher)

        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)

            def make_bundle(version: str, value: str) -> pathlib.Path:
                staging = root / f"staging_{version}"
                staging.mkdir()
                (staging / "vs_server.py").write_text(f"VALUE = {value!r}\n", encoding="utf-8")
                (staging / "tool_modules").mkdir()
                (staging / "tool_modules" / "__init__.py").write_text("", encoding="utf-8")
                (staging / "tool_modules" / "breakpoint_tools.py").write_text("X = 1\n", encoding="utf-8")
                (staging / "tool_modules" / "multi_vs_tools.py").write_text("Y = 2\n", encoding="utf-8")
                signature = launcher._payload_signature(staging)
                self.assertRegex(signature, r"^[0-9a-f]{64}$")
                identity = f"{version.replace('.', '_')}_{signature[:16]}"
                bundle = staging.rename(root / identity)
                # utf-8-sig exercises compatibility with historical C# BOM output.
                (bundle / ".bundle-version").write_text(version, encoding="utf-8-sig")
                (bundle / ".bundle-signature").write_text(signature, encoding="utf-8-sig")
                return bundle

            old_bundle = make_bundle("1.0.0.0", "old")
            new_bundle = make_bundle("2.0.0.0", "new")
            launcher.__file__ = str(root / "vs_server.py")

            (root / ".active-bundle").write_text(new_bundle.name, encoding="utf-8-sig")
            self.assertEqual(new_bundle / "vs_server.py", launcher._select_server())

            # A deliberate installer rollback must win over stale higher bundles.
            (root / ".active-bundle").write_text(old_bundle.name, encoding="utf-8")
            self.assertEqual(old_bundle / "vs_server.py", launcher._select_server())

    def test_vs_launcher_rejects_unexpected_and_reparse_directories(self) -> None:
        spec = importlib.util.spec_from_file_location(
            "aidb_vs_launcher_directory_test", ROOT / "mcp_server" / "vs_launcher.py"
        )
        launcher = importlib.util.module_from_spec(spec)
        assert spec.loader is not None
        spec.loader.exec_module(launcher)

        with tempfile.TemporaryDirectory() as temp:
            bundle = pathlib.Path(temp)
            (bundle / "vs_server.py").write_text("VALUE = 1\n", encoding="utf-8")
            unexpected = bundle / "json"
            unexpected.mkdir()
            (unexpected / "__init__.py").write_text("RAISE = True\n", encoding="utf-8")
            self.assertIsNone(launcher._payload_signature(bundle))

        with tempfile.TemporaryDirectory() as temp:
            bundle = pathlib.Path(temp)
            (bundle / "vs_server.py").write_text("VALUE = 1\n", encoding="utf-8")
            modules = bundle / "tool_modules"
            modules.mkdir()
            (modules / "__init__.py").write_text("", encoding="utf-8")
            original = launcher._path_is_reparse
            with mock.patch.object(
                launcher,
                "_path_is_reparse",
                side_effect=lambda path: path == modules or original(path),
            ):
                self.assertIsNone(launcher._payload_signature(bundle))

    def test_vs_launcher_runtime_rejects_unsigned_bytecode_caches(self) -> None:
        spec = importlib.util.spec_from_file_location(
            "aidb_vs_launcher_bytecode_test", ROOT / "mcp_server" / "vs_launcher.py"
        )
        launcher = importlib.util.module_from_spec(spec)
        assert spec.loader is not None
        spec.loader.exec_module(launcher)

        with tempfile.TemporaryDirectory() as temp:
            bundle = pathlib.Path(temp)
            (bundle / "vs_server.py").write_text("VALUE = 1\n", encoding="utf-8")
            cache = bundle / "__pycache__"
            cache.mkdir()
            (cache / "vs_server.pyc").write_bytes(b"unsigned")
            self.assertRegex(launcher._payload_signature(bundle), r"^[0-9a-f]{64}$")
            self.assertIsNone(
                launcher._payload_signature(bundle, reject_bytecode_caches=True)
            )

    def test_attach_process_omits_absent_selector_parameters(self) -> None:
        from mcp_server.tool_modules.control_tools import _build_attach_process_params

        self.assertEqual(
            {"processName": "UnrealEditor.exe"},
            _build_attach_process_params(None, "UnrealEditor.exe"),
        )
        self.assertEqual(
            {"processId": 12345},
            _build_attach_process_params(12345, None),
        )

    def test_same_session_process_tools_route_only_to_visual_studio(self) -> None:
        from mcp_server import bootstrap

        bootstrap.ensure_deps()
        from mcp_server.tool_modules import control_tools

        list_call = mock.AsyncMock(return_value={
            "processes": [],
            "currentProcessId": 0,
            "capability": "same_debug_session_process_selection",
            "behavior": "opaque_session_process_handles_with_legacy_pid_fallback",
            "totalAvailable": 0,
            "startIndex": 2,
            "nextIndex": 2,
            "hasMore": False,
            "partial": False,
            "snapshotStable": True,
            "snapshotStatus": "ok",
            "maxNodes": 4,
            "budgetMs": 500,
        })
        with mock.patch.object(control_tools.ide_bridge, "call", list_call):
            asyncio.run(control_tools.get_debug_processes(2, 4, 500, 10.0))
        self.assertEqual("get_debug_processes", list_call.await_args.args[0])
        self.assertEqual(
            {"startIndex": 2, "maxNodes": 4, "budgetMs": 500},
            list_call.await_args.args[1],
        )
        self.assertEqual("vs", list_call.await_args.kwargs["preferred_ide"])

        switch_call = mock.AsyncMock(return_value={
            "success": True,
            "message": "selected",
            "action": "SwitchProcess",
            "processId": 12345,
        })
        with mock.patch.object(control_tools.ide_bridge, "call", switch_call):
            asyncio.run(control_tools.switch_process(12345, 750, 10.0))
        self.assertEqual("switch_process", switch_call.await_args.args[0])
        self.assertEqual(
            {"processId": 12345, "processLookupBudgetMs": 750},
            switch_call.await_args.args[1],
        )
        self.assertEqual("vs", switch_call.await_args.kwargs["preferred_ide"])

        handle = "dph_0123456789abcdef0123456789abcdef"
        switch_call.reset_mock()
        with mock.patch.object(control_tools.ide_bridge, "call", switch_call):
            asyncio.run(control_tools.switch_process(process_handle=handle))
        self.assertEqual(
            {"processHandle": handle},
            switch_call.await_args.args[1],
        )

    def test_version_dry_run_tracks_nearest_semver_tag(self) -> None:
        description = subprocess.run(
            ["git", "describe", "--tags", "--long", "--match", "v[0-9]*.[0-9]*.[0-9]*", "HEAD"],
            cwd=ROOT,
            check=True,
            capture_output=True,
            text=True,
        ).stdout.strip()
        match = re.fullmatch(r"v?(\d+)\.(\d+)\.(\d+)-(\d+)-g[0-9a-f]+", description)
        self.assertIsNotNone(match)
        base = ".".join(match.groups()[:3])
        expected = base if match.group(4) == "0" else f"{base}.{match.group(4)}"

        result = subprocess.run(
            [
                "powershell",
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                str(ROOT / "tools" / "Stamp-Version.ps1"),
                "-DryRun",
                "-Target",
                "VS",
            ],
            cwd=ROOT,
            check=True,
            capture_output=True,
            text=True,
        )
        self.assertIn(f"Stamp-Version: {expected} (target=VS)", result.stdout)

    def test_vs_only_tool_surface_omits_cross_ide_selection_aliases(self) -> None:
        environment = os.environ.copy()
        environment["AIDB_PREFERRED_IDE"] = "vs"
        result = subprocess.run(
            [
                sys.executable,
                "-c",
                "import asyncio; "
                "from mcp_server import bootstrap; bootstrap.ensure_deps(); "
                "from mcp_server.vs_mcp_app import mcp; "
                "from mcp_server.tool_modules import register_vs; register_vs(); "
                "names={tool.name for tool in asyncio.run(mcp.list_tools())}; "
                "assert {'list_vs_instances','select_vs_instance','clear_vs_selection','dispatch_vs_instances'} <= names; "
                "assert not {'list_ide_instances','select_ide_instance','clear_ide_selection'} & names; "
                "assert not {'get_call_stack','list_threads','continue','step_over','step_into','step_out','pause','stop_debugging'} & names",
            ],
            cwd=ROOT,
            env=environment,
            capture_output=True,
            text=True,
            timeout=30,
        )
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_vs_registry_exactly_matches_registered_tools_with_alias_policy(self) -> None:
        for aliases_enabled in (False, True):
            with self.subTest(aliases_enabled=aliases_enabled):
                environment = os.environ.copy()
                environment["AIDB_PREFERRED_IDE"] = "vs"
                if aliases_enabled:
                    environment["AIDB_ENABLE_LEGACY_ALIASES"] = "1"
                else:
                    environment.pop("AIDB_ENABLE_LEGACY_ALIASES", None)
                result = subprocess.run(
                    [
                        sys.executable,
                        "-c",
                        "import asyncio; "
                        "from mcp_server.vs_mcp_app import mcp; "
                        "from mcp_server.tool_modules import register_vs; register_vs(); "
                        "from mcp_server.vs_tool_registry import tool_registry_summary; "
                        "actual={t.name for t in asyncio.run(mcp.list_tools())}; "
                        "summary=tool_registry_summary(); expected=set(summary['toolSupport']); "
                        "assert actual == expected, (sorted(actual-expected), sorted(expected-actual)); "
                        "assert all(v['requiredCapabilities'] is not None for v in summary['toolSupport'].values()); "
                        "assert summary['toolSupport']['crash_triage']['backendMethods']; "
                        "assert summary['toolSupport']['step_and_capture']['backendMethods']; "
                        "assert summary['toolSupport']['debug_control']['backendMethods']",
                    ],
                    cwd=ROOT,
                    env=environment,
                    capture_output=True,
                    text=True,
                    timeout=30,
                )
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_all_mcp_tools_publish_and_return_structured_content(self) -> None:
        from mcp_server.vs_mcp_app import mcp
        from mcp_server.tool_modules import register_vs

        register_vs()
        tools = asyncio.run(mcp.list_tools())
        # The Visual Studio-only server exposes the full VS tool surface, which
        # is smaller than the former VS+Rider combined catalog.
        self.assertGreater(len(tools), 60)
        schema_shapes = set()
        for tool in tools:
            with self.subTest(tool=tool.name):
                self.assertIsInstance(tool.outputSchema, dict)
                self.assertEqual("object", tool.outputSchema.get("type"))
                self.assertIn("result", tool.outputSchema.get("properties", {}))
                normalized = dict(tool.outputSchema)
                normalized.pop("title", None)
                schema_shapes.add(json.dumps(normalized, sort_keys=True))

        self.assertGreater(len(schema_shapes), 10)
        status_schema = next(tool.outputSchema for tool in tools if tool.name == "get_debug_status")
        self.assertIn("DebugStatusPayload", status_schema.get("$defs", {}))
        memory_schema = next(tool.outputSchema for tool in tools if tool.name == "read_memory")
        self.assertIn("MemoryReadPayload", memory_schema.get("$defs", {}))
        memory_definition = memory_schema["$defs"]["MemoryReadPayload"]
        self.assertFalse(memory_definition["additionalProperties"])
        self.assertIn("bytesRead", memory_definition["required"])

        disassembly_schema = next(tool.outputSchema for tool in tools if tool.name == "get_disassembly")
        self.assertIn("DisassemblyPayload", disassembly_schema.get("$defs", {}))
        registers_schema = next(tool.outputSchema for tool in tools if tool.name == "get_registers")
        self.assertIn("RegistersPayload", registers_schema.get("$defs", {}))
        native_schema = next(tool.outputSchema for tool in tools if tool.name == "capture_native_context")
        self.assertIn("NativeContextPayload", native_schema.get("$defs", {}))

        with mock.patch(
            "mcp_server.tool_modules.capability_tools.ide_bridge.discover_instances",
            new=mock.AsyncMock(return_value=[]),
        ):
            _, structured = asyncio.run(mcp.call_tool("debug_capabilities", {}))
        self.assertIsInstance(structured, dict)
        self.assertIsInstance(structured.get("result"), dict)

    def test_output_models_are_strict_and_cover_the_registry(self) -> None:
        from pydantic import ValidationError
        from mcp_server.output_models import _MODEL_BY_TOOL, output_model_for
        from mcp_server.vs_tool_registry import VS_TOOL_DOMAINS

        tools = {name for names in VS_TOOL_DOMAINS.values() for name in names}
        self.assertEqual(tools, set(_MODEL_BY_TOOL))
        status_model = output_model_for("get_debug_status")
        validated = status_model.model_validate({
            "result": {"isDebugging": False, "isPaused": False, "debugMode": "design"}
        })
        self.assertFalse(validated.result.isDebugging)
        with self.assertRaises(ValidationError):
            status_model.model_validate({
                "result": {
                    "isDebugging": False,
                    "isPaused": False,
                    "debugMode": "design",
                    "misspelledField": True,
                }
            })
        with self.assertRaises(KeyError):
            output_model_for("unregistered_tool")
        for invalid_status in (
            {"isDebugging": "false", "isPaused": False, "debugMode": "design"},
            {"isDebugging": False, "isPaused": False, "debugMode": "design", "processId": "123"},
            {"isDebugging": False, "isPaused": False, "debugMode": "design", "processId": 123.0},
        ):
            with self.subTest(invalid_status=invalid_status), self.assertRaises(ValidationError):
                status_model.model_validate({"result": invalid_status})

    def test_partial_process_start_results_are_never_marked_retryable(self) -> None:
        from mcp_server.tool_context import wait_for_debugging_to_start

        for evidence in ({"processStarted": True}, {"commandIssued": True}):
            result = {
                "success": False,
                "errorCode": "PROCESS_STARTED_IDENTITY_UNAVAILABLE",
                **evidence,
            }
            returned = asyncio.run(wait_for_debugging_to_start(result, 1.0, True))
            self.assertIs(returned, result)
            self.assertFalse(returned["retryCommand"])

    def test_capability_workflow_is_conservative_without_full_access(self) -> None:
        from mcp_server.tool_modules.capability_tools import _recommended_workflow_for_access

        missing = " ".join(_recommended_workflow_for_access(None))
        self.assertIn("do not assume", missing)
        self.assertNotIn("attach_process", missing)

        custom = " ".join(_recommended_workflow_for_access({
            "accessMode": "custom",
            "allowedCapabilities": ["server.control", "debug.read", "debug.memory.read"],
        }))
        self.assertIn("read_memory", custom)
        self.assertNotIn("attach_process", custom)
        self.assertNotIn("evaluate_expression", custom)
        self.assertNotIn("continue/pause/step", custom)

        full = " ".join(_recommended_workflow_for_access({
            "accessMode": "full_access",
            "allowedCapabilities": [],
        }))
        self.assertIn("attach_process", full)
        self.assertIn("evaluate_expression", full)

    def test_output_models_accept_protocol_variants_and_reject_nested_drift(self) -> None:
        from pydantic import ValidationError
        from mcp_server.output_models import output_model_for

        memory = output_model_for("search_memory").model_validate({
            "result": {
                "pattern": "DE AD",
                "patternFormat": "hex",
                "status": "partial",
                "matches": ["0x1000"],
                "totalFound": 1,
                "bytesSearched": 4096,
                "bytesAttempted": 4096,
                "requestedBytes": 8192,
                "failedReads": 0,
                "unreadableBytes": 0,
                "unattemptedBytes": 4096,
                "partial": True,
                "chunked": True,
                "chunkSize": 4096,
                "chunkCount": 1,
                "chunks": [{
                    "startAddress": "0x1000",
                    "requestedBytes": 4096,
                    "bytesSearched": 4096,
                    "status": "ok",
                    "partial": False,
                    "truncated": False,
                    "nextStartAddress": "0x2000",
                    "matchCount": 1,
                }],
                "nextStartExpression": "0x1000",
                "nextStartOffset": 4096,
                "remainingBytes": 4096,
                "hasMore": True,
            }
        })
        self.assertEqual(1, memory.result.chunkCount)

        capture = output_model_for("continue_and_capture_breakpoint").model_validate({
            "result": {
                "success": True,
                "message": "break detected",
                "hitAttributionStatus": "matched",
                "hitAttributionError": None,
                "hitVerificationStatus": "verified",
                "hitVerificationMessage": "exact address",
                "hitBreakpoint": {
                    "confidence": "high",
                    "reason": "address_match",
                    "currentAddress": {"expression": "@rip", "status": "ok"},
                    "breakpoint": {"name": "main", "enabled": True},
                },
            }
        })
        self.assertEqual("matched", capture.result.hitAttributionStatus)
        self.assertEqual("main", capture.result.hitBreakpoint.breakpoint.name)

        start = output_model_for("start_debugging").model_validate({
            "result": {
                "success": False,
                "message": "debugger did not start",
                "lastPollError": "target exited",
            }
        })
        self.assertEqual("target exited", start.result.lastPollError)

        with self.assertRaises(ValidationError):
            output_model_for("get_callstack").model_validate({
                "result": {
                    "frames": [{"index": 0, "unknownNestedField": True}],
                    "threadId": 1,
                }
            })

    def test_operation_output_families_reject_foreign_fields(self) -> None:
        from pydantic import ValidationError
        from mcp_server.output_models import output_model_for

        valid_cases = {
            "debug_continue": {"success": True, "message": "continued", "action": "continue"},
            "start_debugging": {
                "success": True,
                "message": "started",
                "action": "start",
                "processId": 42,
            },
            "set_breakpoint": {
                "success": True,
                "message": "set",
                "operation": "Set",
                "breakpointInfo": {"filePath": "a.cpp", "lineNumber": 7},
            },
            "switch_stack_frame": {
                "success": True,
                "message": "selected",
                "action": "switch_stack_frame",
                "newLocation": {"functionName": "main", "lineNumber": 7, "columnNumber": 0},
            },
            "get_debug_processes": {
                "processes": [{
                    "processId": 42,
                    "processHandle": "dph_0123456789abcdef0123456789abcdef",
                    "processStartTimeUtcTicks": 638887392000000000,
                    "identityStatus": "verified",
                    "debugSessionGeneration": 7,
                    "name": "app.exe",
                    "isCurrent": True,
                    "programCount": 1,
                }],
                "currentProcessId": 42,
                "capability": "same_debug_session_process_selection",
                "behavior": "opaque_session_process_handles_with_legacy_pid_fallback",
                "totalAvailable": 1,
                "startIndex": 0,
                "nextIndex": 1,
                "hasMore": False,
                "partial": False,
                "snapshotStable": True,
                "snapshotStatus": "ok",
                "maxNodes": 512,
                "budgetMs": 10000,
            },
            "switch_process": {
                "success": True,
                "message": "selected",
                "action": "SwitchProcess",
                "processId": 42,
                "processName": "app.exe",
                "outcome": "process_selected",
            },
        }
        for tool_name, payload in valid_cases.items():
            with self.subTest(tool=tool_name):
                output_model_for(tool_name).model_validate({"result": payload})

        invalid_cases = {
            "debug_continue": {"success": True, "message": "x", "processId": 42},
            "start_debugging": {"success": True, "message": "x", "breakpointInfo": {}},
            "set_breakpoint": {"success": True, "message": "x", "executablePath": "a.exe"},
            "switch_stack_frame": {"success": True, "message": "x", "operation": "Set"},
        }
        for tool_name, payload in invalid_cases.items():
            with self.subTest(tool=tool_name), self.assertRaises(ValidationError):
                output_model_for(tool_name).model_validate({"result": payload})

    def test_stable_nested_output_definitions_forbid_extra_fields(self) -> None:
        from mcp_server.output_models import output_model_for

        cases = {
            "get_callstack": "StackFramePayload",
            "get_locals": "VariablePayload",
            "get_threads": "ThreadPayload",
            "get_debug_processes": "DebugProcessInfoPayload",
            "get_modules": "ModulePayload",
            "get_breakpoints": "BreakpointPayload",
            "capture_native_context": "NativeAddressDetailPayload",
            "inspect_managed_assembly": "ManagedTypeInfoPayload",
        }
        for tool_name, definition_name in cases.items():
            with self.subTest(tool=tool_name, definition=definition_name):
                schema = output_model_for(tool_name).model_json_schema()
                self.assertIn(definition_name, schema["$defs"])
                self.assertFalse(schema["$defs"][definition_name]["additionalProperties"])

    def test_timeout_validation_rejects_invalid_values(self) -> None:
        from mcp.server.fastmcp.exceptions import ToolError
        from mcp_server.tool_context import (
            clamp_tool_timeout,
            tool_timeout_for_budget,
            validate_address_breakpoint_target,
            validate_address_literal,
            validate_enum,
            validate_int_range,
        )

        for invalid in (0, -1, 301, "bad"):
            with self.subTest(invalid=invalid), self.assertRaises(ToolError) as raised:
                clamp_tool_timeout(invalid, 20)
            self.assertIn('"code":"INVALID_ARGUMENT"', str(raised.exception))
        with self.assertRaises(ToolError):
            tool_timeout_for_budget(None, 20, -1)
        self.assertEqual(4, validate_int_range("depth", 4, 0, 8))
        for invalid in (-1, 9, 2.5, True, "4"):
            with self.subTest(range_value=invalid), self.assertRaises(ToolError):
                validate_int_range("depth", invalid, 0, 8)
        self.assertEqual("step_over", validate_enum("action", "step_over", ("step_over", "step_into")))
        with self.assertRaises(ToolError) as enum_error:
            validate_enum("action", "teleport", ("step_over", "step_into"))
        self.assertIn('"code":"INVALID_ARGUMENT"', str(enum_error.exception))
        self.assertEqual("0x7ff61234", validate_address_literal("address", "0x7ff61234"))
        for invalid in ("", "0", "myPointer", "GetAddress()", "0x1234 trailing", -1):
            with self.subTest(address=invalid), self.assertRaises(ToolError):
                validate_address_literal("address", invalid)
        self.assertEqual(
            "game.exe+0x0",
            validate_address_breakpoint_target("address", " game.exe+0x0 "),
        )
        self.assertEqual(
            "C:\\bin\\game.exe+1234h",
            validate_address_breakpoint_target("address", "C:\\bin\\game.exe+1234h"),
        )
        for invalid in ("symbol", "GetAddress()", "game.exe+symbol", "game.exe+0x1 trailing"):
            with self.subTest(breakpoint_address=invalid), self.assertRaises(ToolError):
                validate_address_breakpoint_target("address", invalid)

    def test_delete_data_breakpoint_validates_identifier_before_calling_bridge(self) -> None:
        from mcp_server import bootstrap

        bootstrap.ensure_deps()
        from mcp.server.fastmcp.exceptions import ToolError
        from mcp_server.tool_modules import breakpoint_tools

        call = mock.AsyncMock(return_value={"success": True})
        with mock.patch.object(breakpoint_tools.ide_bridge, "call", call):
            for value in (None, "", "x" * 33):
                with self.subTest(data_breakpoint_id=value), self.assertRaises(ToolError):
                    asyncio.run(breakpoint_tools.delete_data_breakpoint(value))

        call.assert_not_awaited()

    def test_memory_search_rejects_dump_continuation_with_start_address(self) -> None:
        from mcp_server import bootstrap

        bootstrap.ensure_deps()
        from mcp.server.fastmcp.exceptions import ToolError
        from mcp_server.tool_modules import memory_tools

        call = mock.AsyncMock(return_value={})
        with mock.patch.object(memory_tools.ide_bridge, "call", call):
            with self.assertRaises(ToolError) as raised:
                asyncio.run(memory_tools.search_memory(
                    start_address="0x1000",
                    pattern="AA",
                    continuation="opaque-cursor",
                ))

        self.assertIn('"code":"INVALID_ARGUMENT"', str(raised.exception))
        call.assert_not_awaited()

    def test_memory_search_rejects_start_offset_range_that_exceeds_backend_int64(self) -> None:
        from mcp_server import bootstrap

        bootstrap.ensure_deps()
        from mcp.server.fastmcp.exceptions import ToolError
        from mcp_server.tool_modules import memory_tools

        call = mock.AsyncMock(return_value={})
        with mock.patch.object(memory_tools.ide_bridge, "call", call):
            with self.assertRaises(ToolError) as raised:
                asyncio.run(memory_tools.search_memory(
                    start_address="0x1000",
                    pattern="AA",
                    start_offset=0x7FFFFFFFFFFFFFFF,
                    search_size=2,
                ))

        self.assertIn('"code":"INVALID_ARGUMENT"', str(raised.exception))
        call.assert_not_awaited()

    def test_chunked_memory_search_does_not_synthesize_terminal_backend_cursor(self) -> None:
        from mcp_server import bootstrap

        bootstrap.ensure_deps()
        from mcp_server.tool_context import MAX_MEMORY_SEARCH_CHUNK_SIZE
        from mcp_server.tool_modules import memory_tools

        first_page = {
            "patternBytes": ["AA", "BB"],
            "matches": [],
            "bytesSearched": 0,
            "bytesAttempted": 0,
            "unreadableBytes": 0,
            "unattemptedBytes": MAX_MEMORY_SEARCH_CHUNK_SIZE,
            "status": "unavailable",
            "partial": True,
            "truncated": False,
            "hasMore": False,
            "capability": "debugger_expression_fallback_untrusted_session",
            "error": "bounded fallback cannot search this pattern",
        }
        call_with_target = mock.AsyncMock(return_value=(first_page, {"pipeName": "target"}))
        with mock.patch.object(memory_tools.ide_bridge, "call_with_target", call_with_target):
            response = asyncio.run(memory_tools.search_memory(
                start_address="0x1000",
                pattern="AA",
                search_size=MAX_MEMORY_SEARCH_CHUNK_SIZE + 1,
            ))

        result = response if isinstance(response, dict) else json.loads(response)
        result = result.get("result", result)
        self.assertTrue(result["partial"])
        self.assertFalse(result["hasMore"])
        self.assertIsNone(result["nextStartExpression"])
        self.assertIsNone(result["nextStartOffset"])
        self.assertEqual(1, call_with_target.await_count)

    def test_chunked_memory_search_finishes_when_final_match_reaches_result_limit(self) -> None:
        from mcp_server import bootstrap

        bootstrap.ensure_deps()
        from mcp_server.tool_context import MAX_MEMORY_SEARCH_CHUNK_SIZE
        from mcp_server.tool_modules import memory_tools

        first_page = {
            "patternBytes": ["AA", "BB"],
            "matches": [],
            "bytesSearched": MAX_MEMORY_SEARCH_CHUNK_SIZE,
            "bytesAttempted": MAX_MEMORY_SEARCH_CHUNK_SIZE,
            "status": "ok",
            "partial": False,
            "truncated": False,
            "hasMore": False,
            "capability": "trusted_live_process_read",
        }
        final_page = {
            "patternBytes": ["AA", "BB"],
            "matches": ["0x10100000"],
            "bytesSearched": 2,
            "bytesAttempted": 2,
            "status": "ok",
            "partial": False,
            "truncated": False,
            "hasMore": False,
            "capability": "trusted_live_process_read",
        }
        target = {"pipeName": "target"}
        with mock.patch.object(
            memory_tools.ide_bridge,
            "call_with_target",
            mock.AsyncMock(return_value=(first_page, target)),
        ), mock.patch.object(
            memory_tools.vs_bridge,
            "call_pipe_exact",
            mock.AsyncMock(return_value=final_page),
        ):
            response = asyncio.run(memory_tools.search_memory(
                start_address="0x1000",
                pattern="AA BB",
                max_results=1,
                search_size=MAX_MEMORY_SEARCH_CHUNK_SIZE + 1,
            ))

        result = response["result"]
        self.assertEqual("ok", result["status"])
        self.assertFalse(result["partial"])
        self.assertFalse(result["hasMore"])
        # The VS MCP wrapper now serializes the full structured output model, so
        # an absent optional error is a present-and-null field rather than a
        # missing key. The semantic assertion is unchanged: no error occurred.
        self.assertIsNone(result["errorCode"])
        self.assertIsNone(result["error"])
        self.assertEqual(["0x10100000"], result["matches"])

    def test_get_locals_requires_complete_frame_context(self) -> None:
        from mcp_server import bootstrap

        bootstrap.ensure_deps()
        from mcp.server.fastmcp.exceptions import ToolError
        from mcp_server.tool_modules import snapshot_tools

        call = mock.AsyncMock(return_value={"variables": []})
        with mock.patch.object(snapshot_tools.ide_bridge, "call", call):
            asyncio.run(self._tool(snapshot_tools.get_locals)(
                debug_session_generation=3,
                break_sequence=4,
                context_epoch=5,
                process_id=123,
                thread_id=7,
                frame_index=0,
                frame_id="3:4:5:123:7:0",
            ))
        params = call.await_args.args[1]
        self.assertEqual(7, params["threadId"])
        self.assertEqual(0, params["frameIndex"])
        self.assertEqual("3:4:5:123:7:0", params["frameId"])

        with self.assertRaises(ToolError):
            asyncio.run(self._tool(snapshot_tools.get_locals)(
                debug_session_generation=3,
                break_sequence=4,
                context_epoch=5,
                process_id=123,
                thread_id=0,
                frame_index=0,
                frame_id="3:4:5:123:7:0",
            ))

    def test_get_modules_separates_scan_continuation_from_snapshot_paging(self) -> None:
        from mcp_server import bootstrap

        bootstrap.ensure_deps()
        from mcp.server.fastmcp.exceptions import ToolError
        from mcp_server.tool_modules import snapshot_tools

        call = mock.AsyncMock(return_value={"modules": []})
        with mock.patch.object(snapshot_tools.ide_bridge, "call", call):
            asyncio.run(self._tool(snapshot_tools.get_modules)(
                start_module_index=7,
                scan_continuation_token="scan-token",
            ))
        params = call.await_args.args[1]
        self.assertEqual(7, params["startModuleIndex"])
        self.assertEqual("scan-token", params["scanContinuationToken"])
        self.assertNotIn("moduleSnapshotId", params)

        with self.assertRaises(ToolError):
            asyncio.run(self._tool(snapshot_tools.get_modules)(
                module_snapshot_id="page-token",
                scan_continuation_token="scan-token",
            ))
        for invalid in ("   ", "x" * 129):
            with self.subTest(scan_token=invalid), self.assertRaises(ToolError):
                asyncio.run(self._tool(snapshot_tools.get_modules)(scan_continuation_token=invalid))

    def test_crash_triage_uses_only_stable_available_stack_registers(self) -> None:
        from mcp_server.tool_modules.snapshot_tools import _stack_pointer_from_bundle

        stable = {
            "registers": {
                "status": "partial",
                "snapshotStatus": "stable",
                "is64Bit": True,
                "unavailableRegisters": ["XMM15"],
                "registers": [
                    {"name": "RSP", "value": "4096", "hexValue": "0x1000"},
                    {"name": "ESP", "value": "8192", "hexValue": "0x2000"},
                ],
            }
        }
        self.assertEqual(("0x1000", "@rsp"), _stack_pointer_from_bundle(stable))

        unstable = copy.deepcopy(stable)
        unstable["registers"]["snapshotStatus"] = "unstable"
        self.assertIsNone(_stack_pointer_from_bundle(unstable))

        missing_core = copy.deepcopy(stable)
        missing_core["registers"]["unavailableRegisters"].append("RSP")
        self.assertIsNone(_stack_pointer_from_bundle(missing_core))

        unavailable = copy.deepcopy(stable)
        unavailable["registers"]["status"] = "unavailable"
        self.assertIsNone(_stack_pointer_from_bundle(unavailable))

    def test_tool_error_preserves_transport_outcome_unknown_and_request_id(self) -> None:
        from mcp.server.fastmcp.exceptions import ToolError
        from mcp_server import vs_session
        from mcp_server.tool_context import handle_ide_error

        error = vs_session.IDEBridgeError(
            "response pipe disconnected",
            code="PIPE_DISCONNECTED",
            outcome_unknown=True,
        )
        error.request_id = "7e93aa46-d9e4-4a79-8781-71e77e936fec"
        with self.assertRaises(ToolError) as raised:
            handle_ide_error(error, "continue")
        payload = json.loads(str(raised.exception))
        self.assertTrue(payload["outcomeUnknown"])
        self.assertFalse(payload["retryable"])
        self.assertEqual(error.request_id, payload["requestId"])

    def test_stale_errors_require_refresh_and_are_never_blindly_retryable(self) -> None:
        from mcp.server.fastmcp.exceptions import ToolError
        from mcp_server import vs_session
        from mcp_server.tool_context import handle_ide_error

        cases = (
            ("continue", "STALE_DEBUG_CONTEXT", "command", False, True),
            ("get_debug_status", "STALE_TARGET", "query", True, False),
        )
        for operation, code, kind, target_refresh, context_refresh in cases:
            with self.subTest(operation=operation, code=code):
                with self.assertRaises(ToolError) as raised:
                    handle_ide_error(vs_session.IDEBridgeError("stale", code=code), operation)
                payload = json.loads(str(raised.exception))
                self.assertEqual(kind, payload["operationKind"])
                self.assertFalse(payload["retryable"])
                self.assertFalse(payload["safeToRetrySameRequest"])
                self.assertFalse(payload["safeToReinvokeWithNewRequestId"])
                self.assertEqual(target_refresh, payload["requiresTargetRefresh"])
                self.assertEqual(context_refresh, payload["requiresContextRefresh"])

    def test_not_started_command_is_explicitly_safe_to_reinvoke_with_new_id(self) -> None:
        from mcp.server.fastmcp.exceptions import ToolError
        from mcp_server import vs_session
        from mcp_server.tool_context import handle_ide_error

        with self.assertRaises(ToolError) as raised:
            handle_ide_error(
                vs_session.IDEBridgeError("not started", code="DEADLINE_EXCEEDED_NOT_STARTED"),
                "continue",
            )
        payload = json.loads(str(raised.exception))
        self.assertEqual("command", payload["operationKind"])
        self.assertTrue(payload["retryable"])
        self.assertFalse(payload["safeToRetrySameRequest"])
        self.assertTrue(payload["safeToReinvokeWithNewRequestId"])

    def test_step_and_capture_preserves_completed_step_when_bundle_fails(self) -> None:
        from mcp_server import vs_session
        from mcp_server.tool_modules import snapshot_tools

        step = {
            "success": True,
            "message": "Stepped over",
            "commandIssued": True,
            "outcome": "completed",
        }
        bundle_error = vs_session.IDEBridgeError(
            "capture disconnected",
            code="PIPE_DISCONNECTED",
            outcome_unknown=True,
        )
        bundle_error.request_id = "59b5d26d-318e-4e6f-8dc1-a5990487f72f"
        target = {
            "pipeName": "AIDebuggerVS_123",
            "processId": 123,
            "processStartTimeUtcTicks": 456,
            "serverInstanceId": "server",
            "debugSessionGeneration": 2,
            "breakSequence": 3,
            "contextEpoch": 4,
        }
        step_call = mock.AsyncMock(return_value=(step, target))
        bundle_call = mock.AsyncMock(side_effect=bundle_error)
        with mock.patch.object(snapshot_tools.ide_bridge, "call_with_target", step_call), \
             mock.patch.object(snapshot_tools.vs_bridge, "call_pipe_exact", bundle_call):
            response = asyncio.run(self._tool(snapshot_tools.step_and_capture)())

        payload = self._payload(response)
        self.assertEqual(step, payload["step"])
        self.assertIsNone(payload["bundle"])
        self.assertEqual("PIPE_DISCONNECTED", payload["bundleErrorCode"])
        self.assertTrue(payload["bundleOutcomeUnknown"])
        self.assertEqual(bundle_error.request_id, payload["bundleRequestId"])

    def test_chunked_memory_search_preserves_prefix_when_later_chunk_fails(self) -> None:
        from mcp_server import vs_bridge
        from mcp_server.tool_modules import memory_tools

        chunk_size = memory_tools.MAX_MEMORY_SEARCH_CHUNK_SIZE
        first = {
            "status": "ok",
            "partial": False,
            "truncated": False,
            "matches": ["0x1000"],
            "bytesSearched": chunk_size,
            "patternBytes": ["AA"],
            "capability": "live_process_memory",
        }
        target = {
            "pipeName": "AIDebuggerVS_123",
            "processId": 123,
            "processStartTimeUtcTicks": 456,
            "serverInstanceId": "server",
            "debugSessionGeneration": 2,
            "breakSequence": 3,
            "contextEpoch": 4,
        }
        failure = vs_bridge.VSBridgeError(
            "debug context changed",
            code="STALE_DEBUG_CONTEXT",
        )
        with mock.patch.object(
            memory_tools.ide_bridge,
            "call_with_target",
            mock.AsyncMock(return_value=(first, target)),
        ), mock.patch.object(
            memory_tools.vs_bridge,
            "call_pipe_exact",
            mock.AsyncMock(side_effect=failure),
        ):
            response = asyncio.run(memory_tools.search_memory(
                "0x1000",
                "AA",
                search_size=chunk_size * 2,
            ))

        payload = response["result"]
        self.assertEqual("partial", payload["status"])
        self.assertEqual(["0x1000"], payload["matches"])
        self.assertEqual(chunk_size, payload["bytesSearched"])
        self.assertEqual("STALE_DEBUG_CONTEXT", payload["errorCode"])
        self.assertTrue(payload["continuationRequiresRefresh"])
        self.assertEqual(chunk_size, payload["nextStartOffset"])
        self.assertEqual(chunk_size, payload["remainingBytes"])

    def test_chunked_memory_search_uses_backend_absolute_continuation(self) -> None:
        from mcp_server.tool_modules import memory_tools

        backend_offset = 257
        first = {
            "status": "partial",
            "partial": True,
            "truncated": True,
            "matches": ["0x1100"],
            "bytesSearched": 512,
            "bytesAttempted": 512,
            "patternBytes": ["AA"],
            "capability": "live_process_memory",
            "nextStartOffset": backend_offset,
            "nextStartAddress": "0x1101",
        }
        target = {
            "pipeName": "AIDebuggerVS_123",
            "processId": 123,
            "processStartTimeUtcTicks": 456,
            "serverInstanceId": "server",
            "debugSessionGeneration": 2,
            "breakSequence": 3,
            "contextEpoch": 4,
        }
        search_size = memory_tools.MAX_MEMORY_SEARCH_CHUNK_SIZE + 4096
        with mock.patch.object(
            memory_tools.ide_bridge,
            "call_with_target",
            mock.AsyncMock(return_value=(first, target)),
        ), mock.patch.object(
            memory_tools.vs_bridge,
            "call_pipe_exact",
            mock.AsyncMock(side_effect=AssertionError("must stop at backend continuation")),
        ):
            response = asyncio.run(memory_tools.search_memory(
                "0x1000", "AA", max_results=1, search_size=search_size,
            ))

        payload = response["result"]
        self.assertEqual("0x1000", payload["nextStartExpression"])
        self.assertEqual(backend_offset, payload["nextStartOffset"])
        self.assertEqual(search_size - backend_offset, payload["remainingBytes"])
        self.assertTrue(payload["hasMore"])

    def test_unified_memory_search_uses_vs_mapped_path_without_an_address(self) -> None:
        from mcp_server.tool_modules import memory_tools
        from mcp_server.vs_tool_registry import VS_TOOL_DOMAINS

        tools = {name for names in VS_TOOL_DOMAINS.values() for name in names}

        backend_result = {
            "pattern": "AA",
            "status": "partial",
            "matches": ["0x1000"],
            "totalFound": 1,
            "bytesSearched": 4096,
            "bytesAttempted": 4096,
            "requestedBytes": 4096,
            "failedReads": 0,
            "unreadableBytes": 0,
            "unattemptedBytes": 0,
            "partial": True,
            "truncated": False,
            "hasMore": True,
            "continuation": "eyJ2ZXJzaW9uIjoxfQ",
            "source": "memory64_list",
        }
        call = mock.AsyncMock(return_value=backend_result)
        with mock.patch.object(memory_tools.ide_bridge, "call", call):
            response = asyncio.run(memory_tools.search_memory(
                pattern="AA",
                search_size=4096,
                continuation="eyJ2ZXJzaW9uIjoxfQ",
            ))

        payload = response["result"]
        for key, value in backend_result.items():
            with self.subTest(field=key):
                self.assertEqual(value, payload[key])
        self.assertEqual("search_memory", call.await_args.args[0])
        params = call.await_args.args[1]
        self.assertNotIn("startAddress", params)
        self.assertEqual("eyJ2ZXJzaW9uIjoxfQ", params["continuation"])
        self.assertEqual("vs", call.await_args.kwargs["preferred_ide"])
        self.assertIn("search_memory", tools)
        self.assertNotIn("search_dump_memory", tools)

    def test_step_and_capture_uses_safe_bundle_unless_unsafe_fields_are_requested(self) -> None:
        from mcp_server.tool_modules import snapshot_tools

        step = {"success": True, "message": "Stepped", "commandIssued": True, "outcome": "completed"}
        target = {
            "pipeName": "AIDebuggerVS_123",
            "processId": 123,
            "processStartTimeUtcTicks": 456,
            "serverInstanceId": "server",
            "debugSessionGeneration": 2,
            "breakSequence": 3,
            "contextEpoch": 4,
        }
        with mock.patch.dict(os.environ, {"AIDB_PREFERRED_IDE": "vs"}), \
             mock.patch.object(snapshot_tools.ide_bridge, "call_with_target", mock.AsyncMock(return_value=(step, target))), \
             mock.patch.object(snapshot_tools.vs_bridge, "call_pipe_exact", mock.AsyncMock(return_value={})) as bundle_call:
            asyncio.run(self._tool(snapshot_tools.step_and_capture)())
            self.assertIn("waitTimeoutMs", snapshot_tools.ide_bridge.call_with_target.await_args.args[1])
            self.assertEqual("capture_debug_bundle_safe", bundle_call.await_args.args[1])
            self.assertNotIn("includeException", bundle_call.await_args.args[2])

            asyncio.run(self._tool(snapshot_tools.step_and_capture)(include_exception=True))
            self.assertEqual("capture_debug_bundle", bundle_call.await_args.args[1])
            self.assertTrue(bundle_call.await_args.args[2]["includeException"])

    def test_crash_triage_reads_stack_from_captured_literal_register(self) -> None:
        from mcp_server.tool_modules import snapshot_tools

        bundle = {
            "registers": {
                "status": "complete",
                "snapshotStatus": "stable",
                "unavailableRegisters": [],
                "registers": [
                    {"name": "RSP", "value": "140000000", "hexValue": "0x0000000140000000"},
                ],
                "is64Bit": True,
            },
        }
        target = {
            "pipeName": "AIDebuggerVS_123",
            "processId": 123,
            "processStartTimeUtcTicks": 456,
            "serverInstanceId": "server",
            "debugSessionGeneration": 2,
            "breakSequence": 3,
            "contextEpoch": 4,
        }

        async def exact_call(call_target, method, params, **kwargs):
            if method == "get_disassembly":
                return {"instructions": []}
            if method == "read_memory":
                self.assertEqual("0x140000000", params["address"])
                self.assertNotIn("@", params["address"])
                return {"status": "ok", "bytesRead": 16, "data": "00"}
            self.fail(f"unexpected backend method: {method}")

        with mock.patch.object(
            snapshot_tools.ide_bridge,
            "call_with_target",
            mock.AsyncMock(return_value=(bundle, target)),
        ), mock.patch.object(
            snapshot_tools.vs_bridge,
            "call_pipe_exact",
            mock.AsyncMock(side_effect=exact_call),
        ) as exact:
            response = asyncio.run(self._tool(snapshot_tools.crash_triage)())

        payload = self._payload(response)
        self.assertEqual("0x140000000", payload["stackMemoryAddress"])
        self.assertEqual("@rsp", payload["stackMemoryAddressExpression"])
        self.assertNotIn("stackMemoryError", payload)
        self.assertNotIn("resolve_address", [call.args[1] for call in exact.await_args_list])

    def test_invalid_composite_actions_are_mcp_errors(self) -> None:
        from mcp_server import bootstrap

        bootstrap.ensure_deps()
        from mcp.server.fastmcp.exceptions import ToolError
        from mcp_server.tool_modules import control_tools, snapshot_tools

        for call in (
            lambda: control_tools.debug_control("teleport"),
            lambda: snapshot_tools.step_and_capture(action="teleport"),
        ):
            with self.subTest(call=call), self.assertRaises(ToolError) as raised:
                asyncio.run(call())
            payload = json.loads(str(raised.exception))
            self.assertEqual("INVALID_ARGUMENT", payload["code"])
            self.assertFalse(payload["retryable"])
            self.assertFalse(payload["outcomeUnknown"])

    def test_recovery_target_requires_exact_typed_ide_identity(self) -> None:
        from mcp_server import bootstrap

        bootstrap.ensure_deps()
        from mcp.server.fastmcp.exceptions import ToolError
        from mcp_server.tool_modules.control_tools import _build_recovery_target

        nonce = "a" * 32
        self.assertEqual(
            {
                "ide": "vs",
                "pipeName": "AIDebuggerVS_42",
                "serverInstanceId": nonce,
                "processId": 42,
                "processStartTimeUtcTicks": 638000000000000000,
            },
            _build_recovery_target(
                "AIDebuggerVS_42",
                nonce,
                42,
                638000000000000000,
            ),
        )
        invalid_values = (
            ("AIDebuggerVS_1", nonce, True, 638000000000000000),
            ("AIDebuggerVS_42", nonce, 42, False),
            ("AIDebuggerVS_43", nonce, 42, 638000000000000000),
            ("AIDebuggerVS_42", "server-42", 42, 638000000000000000),
        )
        for values in invalid_values:
            with self.subTest(values=values), self.assertRaises(ToolError) as raised:
                _build_recovery_target(*values)
            payload = json.loads(str(raised.exception))
            self.assertEqual("INVALID_ARGUMENT", payload["code"])
            self.assertFalse(payload["retryable"])
            self.assertFalse(payload["outcomeUnknown"])

    def test_exact_recovery_routes_to_visual_studio_backend(self) -> None:
        from mcp_server.tool_modules import control_tools

        target = {
            "ide": "vs",
            "pipeName": "AIDebuggerVS_42",
            "serverInstanceId": "a" * 32,
            "processId": 42,
            "processStartTimeUtcTicks": 638000000000000000,
        }
        with mock.patch.object(
            control_tools.vs_bridge,
            "call_pipe_exact",
            mock.AsyncMock(return_value={"state": "completed"}),
        ) as exact_call:
            result = asyncio.run(control_tools._call_recovery_target(
                target,
                "get_request_status",
                {"requestId": "request"},
                timeout=10.0,
            ))
        self.assertEqual({"state": "completed"}, result)
        exact_call.assert_awaited_once_with(
            {
                "pipeName": "AIDebuggerVS_42",
                "serverInstanceId": "a" * 32,
                "processId": 42,
                "processStartTimeUtcTicks": 638000000000000000,
            },
            "get_request_status",
            {"requestId": "request"},
            timeout=10.0,
        )

    def test_detect_preserves_explicit_target_over_paused_candidate(self) -> None:
        from mcp_server import vs_bridge
        from mcp_server.tool_modules import vs_instance_tools

        bridge = vs_bridge.VSBridge()
        first = self._ide_instance(101)
        paused = self._ide_instance(202, paused=True)
        bridge._selected_pipe = first.pipe_name
        bridge._selected_target = first.target_identity()
        with mock.patch.object(vs_instance_tools.backend, "get_bridge", return_value=bridge), \
             mock.patch.object(vs_instance_tools.backend, "discover_instances", mock.AsyncMock(return_value=[first, paused])):
            payload = asyncio.run(vs_instance_tools.detect_debug_sessions())["result"]

        self.assertTrue(payload["selected"])
        self.assertIsNone(payload["autoSelected"])
        self.assertEqual(first.pipe_name, payload["selectedInstance"]["pipeName"])
        self.assertTrue(next(item for item in payload["instances"] if item["processId"] == 101)["isSelected"])
        self.assertFalse(next(item for item in payload["instances"] if item["processId"] == 202)["isSelected"])
        selected_ide, selected_pipe, _, _ = asyncio.run(bridge.get_selection_snapshot())
        self.assertEqual(("vs", first.pipe_name), (selected_ide, selected_pipe))

    def test_detect_reports_stale_pin_without_switching_target(self) -> None:
        from mcp_server import vs_bridge
        from mcp_server.tool_modules import vs_instance_tools

        bridge = vs_bridge.VSBridge()
        stale_pipe = "AIDebuggerVS_101"
        available = self._ide_instance(202, paused=True)
        bridge._selected_pipe = stale_pipe
        bridge._selected_target = {
            "pipeName": stale_pipe,
            "processId": 101,
            "processStartTimeUtcTicks": 101000,
            "serverInstanceId": "server-101",
        }
        with mock.patch.object(vs_instance_tools.backend, "get_bridge", return_value=bridge), \
             mock.patch.object(vs_instance_tools.backend, "discover_instances", mock.AsyncMock(return_value=[available])):
            payload = asyncio.run(vs_instance_tools.detect_debug_sessions())["result"]

        # The stale pin is reported as an unreachable pinned selection rather
        # than a live selected instance, and the underlying pin is left intact.
        self.assertFalse(payload["selected"])
        self.assertFalse(payload["selectedReachable"])
        self.assertTrue(payload["hasPinnedSelection"])
        self.assertIsNone(payload["autoSelected"])
        self.assertIsNone(payload["selectedInstance"])
        self.assertEqual(stale_pipe, payload["staleSelectedTarget"]["pipeName"])
        self.assertEqual("not_found_in_current_discovery", payload["staleSelectedTarget"]["connectionStatus"])
        self.assertEqual("stale_selection", payload["selectionStatus"])
        self.assertFalse(payload["instances"][0]["isSelected"])
        # The structured output emits an explicit null rather than omitting the
        # key; no would-be auto-selection was computed for the stale pin.
        self.assertIsNone(payload["wouldAutoSelectInstance"])
        selected_ide, selected_pipe, _, _ = asyncio.run(bridge.get_selection_snapshot())
        self.assertEqual(("vs", stale_pipe), (selected_ide, selected_pipe))

    def test_detect_treats_same_pipe_with_new_instance_identity_as_stale(self) -> None:
        from mcp_server import vs_bridge
        from mcp_server.tool_modules import vs_instance_tools

        bridge = vs_bridge.VSBridge()
        original = self._ide_instance(101)
        replacement = self._ide_instance(101, paused=True)
        replacement.process_start_time_utc_ticks += 1
        replacement.server_instance_id = "replacement-server"
        bridge._selected_pipe = original.pipe_name
        bridge._selected_target = original.target_identity()
        with mock.patch.object(vs_instance_tools.backend, "get_bridge", return_value=bridge), \
             mock.patch.object(vs_instance_tools.backend, "discover_instances", mock.AsyncMock(return_value=[replacement])):
            payload = asyncio.run(vs_instance_tools.detect_debug_sessions())["result"]

        # A same-pipe replacement with a new immutable identity is a stale pin,
        # not a re-selection: the original authenticated target is preserved.
        self.assertFalse(payload["selected"])
        self.assertFalse(payload["selectedReachable"])
        self.assertTrue(payload["hasPinnedSelection"])
        self.assertIsNone(payload["autoSelected"])
        self.assertEqual(original.server_instance_id, payload["staleSelectedTarget"]["serverInstanceId"])
        self.assertEqual("not_found_in_current_discovery", payload["staleSelectedTarget"]["connectionStatus"])
        self.assertEqual("stale_selection", payload["selectionStatus"])
        self.assertFalse(payload["instances"][0]["isSelected"])
        _, _, selected_target, _ = asyncio.run(bridge.get_selection_snapshot())
        self.assertEqual(original.target_identity(), selected_target)

    def test_detect_without_selection_only_reports_would_select(self) -> None:
        from mcp_server import vs_bridge
        from mcp_server.tool_modules import vs_instance_tools

        bridge = vs_bridge.VSBridge()
        available = self._ide_instance(202, paused=True)
        with mock.patch.object(vs_instance_tools.backend, "get_bridge", return_value=bridge), \
             mock.patch.object(vs_instance_tools.backend, "discover_instances", mock.AsyncMock(return_value=[available])):
            payload = asyncio.run(vs_instance_tools.detect_debug_sessions(select=False))["result"]

        self.assertFalse(payload["selected"])
        self.assertIsNone(payload["autoSelected"])
        self.assertEqual(available.pipe_name, payload["wouldAutoSelectInstance"]["pipeName"])
        selected_ide, selected_pipe, _, _ = asyncio.run(bridge.get_selection_snapshot())
        self.assertEqual((None, None), (selected_ide, selected_pipe))

    def test_list_instances_marks_only_the_pinned_target(self) -> None:
        from mcp_server import vs_bridge
        from mcp_server.tool_modules import vs_instance_tools

        bridge = vs_bridge.VSBridge()
        first = self._ide_instance(101)
        second = self._ide_instance(202, paused=True)
        bridge._selected_pipe = first.pipe_name
        bridge._selected_target = first.target_identity()
        with mock.patch.object(vs_instance_tools.backend, "get_bridge", return_value=bridge), \
             mock.patch.object(vs_instance_tools.backend, "discover_instances", mock.AsyncMock(return_value=[first, second])):
            payload = asyncio.run(vs_instance_tools.list_vs_instances())["result"]

        self.assertTrue(payload["selected"])
        self.assertEqual(first.pipe_name, payload["selectedInstance"]["pipeName"])
        selected = [item["pipeName"] for item in payload["instances"] if item["isSelected"]]
        self.assertEqual([first.pipe_name], selected)

    def test_vs_selection_tools_forward_authenticated_registration_snapshot(self) -> None:
        from mcp_server import vs_bridge
        from mcp_server.tool_modules import vs_instance_tools

        target = self._ide_instance(101, paused=True)
        registration = {
            "protocol": "aidb/2",
            "pipeName": target.pipe_name,
            "processId": target.process_id,
            "processStartTimeUtcTicks": target.process_start_time_utc_ticks,
            "serverInstanceId": target.server_instance_id,
            "authToken": "private-token",
            "capabilities": ["server.control", "debug.read"],
        }
        target._registration_snapshot = dict(registration)
        expected_snapshot = target.registration_snapshot()

        calls = (
            lambda: vs_instance_tools.detect_debug_sessions(select=True),
            lambda: vs_instance_tools.select_vs_instance(101),
        )
        for call in calls:
            bridge = vs_bridge.VSBridge()
            select = mock.AsyncMock(return_value=(True, target.target_identity(), 1))
            bridge.try_select_instance = select
            with self.subTest(call=call), \
                 mock.patch.object(vs_instance_tools.backend, "get_bridge", return_value=bridge), \
                 mock.patch.object(
                     vs_instance_tools.backend,
                     "discover_instances",
                     mock.AsyncMock(return_value=[target]),
                 ):
                asyncio.run(call())
            self.assertEqual(
                expected_snapshot,
                select.await_args.kwargs["registration_snapshot"],
            )
            self.assertEqual(
                "private-token",
                select.await_args.kwargs["registration_snapshot"]["authToken"],
            )

    def test_multi_vs_dispatches_exact_targets_concurrently_without_selection_mutation(self) -> None:
        from mcp_server.tool_modules import multi_vs_tools
        from mcp_server.output_models import output_model_for

        first = self._ide_instance(101, paused=True)
        second = self._ide_instance(202, paused=True)
        for instance in (first, second):
            instance._registration_snapshot = {
                "protocol": "aidb/2",
                "pipeName": instance.pipe_name,
                "processId": instance.process_id,
                "processStartTimeUtcTicks": instance.process_start_time_utc_ticks,
                "serverInstanceId": instance.server_instance_id,
                "authToken": f"private-{instance.process_id}",
                "capabilities": ["server.control", "debug.read"],
            }

        active = 0
        maximum_active = 0
        both_started = asyncio.Event()

        async def exact_call(target, method, params, timeout, **kwargs):
            nonlocal active, maximum_active
            active += 1
            maximum_active = max(maximum_active, active)
            try:
                if active == 2:
                    both_started.set()
                else:
                    await asyncio.wait_for(both_started.wait(), timeout=0.25)
                self.assertEqual("get_debug_status", method)
                self.assertEqual({"includeDetails": True}, params)
                # timeout_seconds budgets the whole dispatch, so the per-target
                # call gets whatever remains of the 20s default budget.
                self.assertAlmostEqual(20.0, timeout, delta=0.5)
                self.assertTrue(kwargs["include_target"])
                self.assertIs(False, kwargs["advance_selection"])
                self.assertEqual(target["processId"], kwargs["registration_snapshot"]["processId"])
                return {"reportedPid": target["processId"]}, dict(target)
            finally:
                active -= 1

        with mock.patch.object(
            multi_vs_tools.ide_bridge,
            "discover_instances",
            mock.AsyncMock(return_value=[first, second]),
        ), mock.patch.object(
            multi_vs_tools.vs_bridge,
            "call_pipe_exact",
            side_effect=exact_call,
        ) as exact, mock.patch.object(
            multi_vs_tools.ide_bridge,
            "select_instance",
        ) as select, mock.patch.object(
            multi_vs_tools.ide_bridge,
            "clear_selection",
        ) as clear:
            payload = asyncio.run(multi_vs_tools.dispatch_vs_instances(
                "get_debug_status",
                [101, 202],
                params={"includeDetails": True},
                max_concurrency=2,
            ))["result"]

        self.assertEqual(2, maximum_active)
        self.assertEqual(2, exact.await_count)
        self.assertEqual([101, 202], [item["processId"] for item in payload["results"]])
        self.assertEqual(["ok", "ok"], [item["status"] for item in payload["results"]])
        self.assertTrue(payload["success"])
        self.assertFalse(payload["partial"])
        self.assertEqual("does_not_change_selected_vs", payload["selectionBehavior"])
        select.assert_not_called()
        clear.assert_not_called()
        output_model_for("dispatch_vs_instances").model_validate({"result": payload})

    def test_multi_vs_dispatch_keeps_other_targets_running_after_preflight_failure(self) -> None:
        from mcp_server.tool_modules import multi_vs_tools

        available = self._ide_instance(101, paused=True)
        available._registration_snapshot = {
            "protocol": "aidb/2",
            "pipeName": available.pipe_name,
            "processId": available.process_id,
            "processStartTimeUtcTicks": available.process_start_time_utc_ticks,
            "serverInstanceId": available.server_instance_id,
            "authToken": "private-101",
            "capabilities": ["server.control", "debug.read"],
        }

        async def exact_call(target, method, params, timeout, **kwargs):
            return {"reportedPid": target["processId"]}, dict(target)

        with mock.patch.object(
            multi_vs_tools.ide_bridge,
            "discover_instances",
            mock.AsyncMock(return_value=[available]),
        ), mock.patch.object(
            multi_vs_tools.vs_bridge,
            "call_pipe_exact",
            side_effect=exact_call,
        ) as exact:
            payload = asyncio.run(multi_vs_tools.dispatch_vs_instances(
                "get_debug_status", [101, 202]
            ))["result"]

        self.assertEqual(1, exact.await_count)
        self.assertFalse(payload["success"])
        self.assertTrue(payload["partial"])
        self.assertEqual(["ok", "error"], [item["status"] for item in payload["results"]])
        self.assertEqual("TARGET_NOT_FOUND", payload["results"][1]["error"]["code"])
        # The drop-out target never dispatched, so the effective concurrency is
        # one, not the requested two.
        self.assertEqual(1, payload["maxConcurrency"])

    def test_multi_vs_dispatch_bounds_discovery_and_flags_unknown_command_outcome(self) -> None:
        from mcp_server import vs_bridge
        from mcp_server.tool_modules import multi_vs_tools

        instance = self._ide_instance(101, paused=True)
        instance._registration_snapshot = {
            "protocol": "aidb/2",
            "pipeName": instance.pipe_name,
            "processId": instance.process_id,
            "processStartTimeUtcTicks": instance.process_start_time_utc_ticks,
            "serverInstanceId": instance.server_instance_id,
            "authToken": "private-101",
            "capabilities": list(vs_bridge._all_vs_capabilities()),
        }

        discover = mock.AsyncMock(return_value=[instance])
        transport = mock.AsyncMock(side_effect=OSError("pipe closed"))

        with mock.patch.object(
            multi_vs_tools.ide_bridge, "discover_instances", discover
        ), mock.patch.object(
            multi_vs_tools.vs_bridge, "call_pipe_exact", transport
        ):
            asyncio.run(multi_vs_tools.dispatch_vs_instances(
                "get_debug_status", [101], timeout_seconds=5,
            ))
            # timeout_seconds budgets the whole call, so discovery must be
            # capped by the remaining budget rather than the bridge default.
            discover_timeout = discover.await_args.kwargs["timeout"]
            self.assertGreater(discover_timeout, 0.0)
            self.assertLessEqual(discover_timeout, 5.0)

            # A command that dies on the transport may already have been
            # accepted by Visual Studio, so it must not be reported as
            # definitely-not-applied (that would invite a blind re-invoke).
            command = asyncio.run(multi_vs_tools.dispatch_vs_commands(
                "continue", [101],
            ))["result"]
            self.assertIs(True, command["results"][0]["error"]["outcomeUnknown"])

            query = asyncio.run(multi_vs_tools.dispatch_vs_instances(
                "get_debug_status", [101],
            ))["result"]
            self.assertIs(False, query["results"][0]["error"]["outcomeUnknown"])

    def test_multi_vs_dispatch_tools_are_split_by_method_kind(self) -> None:
        from mcp.server.fastmcp.exceptions import ToolError
        from mcp_server.tool_modules import multi_vs_tools

        # A command must not be reachable through the read-only query tool; the
        # error has to name the tool that can actually run it.
        with self.assertRaises(ToolError) as raised:
            asyncio.run(multi_vs_tools.dispatch_vs_instances("continue", [101]))
        payload = json.loads(str(raised.exception))
        self.assertEqual("INVALID_ARGUMENT", payload["code"])
        self.assertIn("dispatch_vs_commands", payload["message"])

        # ...and the reverse: the destructive tool rejects query methods.
        with self.assertRaises(ToolError) as raised:
            asyncio.run(multi_vs_tools.dispatch_vs_commands("get_debug_status", [101]))
        payload = json.loads(str(raised.exception))
        self.assertEqual("INVALID_ARGUMENT", payload["code"])
        self.assertIn("dispatch_vs_instances", payload["message"])

    def test_wheel_extraction_rejects_traversal_links_and_conflicts(self) -> None:
        from mcp_server import bootstrap

        with tempfile.TemporaryDirectory() as temporary_directory:
            root = pathlib.Path(temporary_directory)
            target = root / "target"

            traversal = root / "traversal.whl"
            with zipfile.ZipFile(traversal, "w") as archive:
                archive.writestr("../escape.py", "bad")
            with self.assertRaises(RuntimeError):
                bootstrap._extract_wheel(traversal, target, {})
            self.assertFalse((root / "escape.py").exists())

            symlink = root / "symlink.whl"
            link_info = zipfile.ZipInfo("package/link.py")
            link_info.external_attr = (stat.S_IFLNK | 0o777) << 16
            with zipfile.ZipFile(symlink, "w") as archive:
                archive.writestr(link_info, "target.py")
            with self.assertRaises(RuntimeError):
                bootstrap._extract_wheel(symlink, target, {})

            first = root / "first.whl"
            second = root / "second.whl"
            with zipfile.ZipFile(first, "w") as archive:
                archive.writestr("package/data.txt", "one")
            with zipfile.ZipFile(second, "w") as archive:
                archive.writestr("package/data.txt", "two")
            extracted = {}
            with self.assertRaisesRegex(RuntimeError, "SHA-256 mismatch"):
                bootstrap._extract_wheel(first, target, {}, "0" * 64)
            bootstrap._extract_wheel(first, target, extracted)
            with self.assertRaises(RuntimeError):
                bootstrap._extract_wheel(second, target, extracted)
            self.assertEqual("one", (target / "package" / "data.txt").read_text(encoding="utf-8"))

            unsafe_members = (
                "package/name:stream.py",
                "package/CON.txt",
                "package/trailing. ",
                "Package/Value.py",
            )
            for index, unsafe_name in enumerate(unsafe_members):
                archive_path = root / f"unsafe-{index}.whl"
                with zipfile.ZipFile(archive_path, "w") as archive:
                    archive.writestr(unsafe_name, "bad")
                    if index == 3:
                        archive.writestr("package/value.py", "duplicate")
                with self.subTest(member=unsafe_name), self.assertRaises(RuntimeError):
                    bootstrap._extract_wheel(archive_path, target, {})

            collision = root / "file-directory-collision.whl"
            with zipfile.ZipFile(collision, "w") as archive:
                archive.writestr("package", "file")
                archive.writestr("package/value.py", "child")
            with self.assertRaises(RuntimeError):
                bootstrap._extract_wheel(collision, target, {})

    def test_dependency_marker_requires_exact_tree_and_prioritizes_validated_pth_paths(self) -> None:
        from mcp_server import bootstrap

        stamp = "ab" * 32
        with tempfile.TemporaryDirectory() as temporary_directory:
            target = pathlib.Path(temporary_directory) / "deps"
            package = target / "package"
            package.mkdir(parents=True)
            win32 = target / "win32"
            win32_lib = win32 / "lib"
            pythonwin = target / "pythonwin"
            win32_lib.mkdir(parents=True)
            pythonwin.mkdir()
            installed = package / "__init__.py"
            installed.write_text("VALUE = 1\n", encoding="utf-8")
            (target / "pywin32_bootstrap.py").write_text("SOURCE = 'bundled'\n", encoding="utf-8")
            (target / "pywin32.pth").write_text(
                "win32\nwin32\\lib\npythonwin\nimport pywin32_bootstrap\n",
                encoding="utf-8",
            )
            bootstrap._write_target_marker(target, stamp)
            self.assertTrue(bootstrap._target_marker_matches(target, stamp))

            injected = target / "injected.pth"
            injected.write_text("import pathlib; pathlib.Path('executed').touch()\n", encoding="utf-8")
            self.assertFalse(bootstrap._target_marker_matches(target, stamp))

            with self.assertRaises(RuntimeError):
                bootstrap._add_sys_path(target, stamp)

            injected.unlink()
            global_site_path = pathlib.Path(temporary_directory) / "global-site-packages"
            global_site_path.mkdir()
            global_marker = pathlib.Path(temporary_directory) / "global-bootstrap-executed"
            (global_site_path / "pywin32_bootstrap.py").write_text(
                "from pathlib import Path\n"
                f"Path({str(global_marker)!r}).touch()\n"
                "SOURCE = 'global'\n",
                encoding="utf-8",
            )
            global_site = str(global_site_path)
            original_path = list(sys.path)
            original_module = sys.modules.pop("pywin32_bootstrap", None)
            sys.path.insert(0, global_site)
            try:
                bootstrap._add_sys_path(target, stamp)
                self.assertEqual(
                    [str(target), str(win32), str(win32_lib), str(pythonwin)],
                    sys.path[:4],
                )
                self.assertGreater(sys.path.index(global_site), sys.path.index(str(pythonwin)))
                self.assertEqual("bundled", sys.modules["pywin32_bootstrap"].SOURCE)
                self.assertFalse(global_marker.exists())
            finally:
                sys.path[:] = original_path
                sys.modules.pop("pywin32_bootstrap", None)
                if original_module is not None:
                    sys.modules["pywin32_bootstrap"] = original_module
            extra_directory = target / "extra"
            extra_directory.mkdir()
            self.assertFalse(bootstrap._target_marker_matches(target, stamp))
            extra_directory.rmdir()

            original_reparse = bootstrap._path_is_reparse
            with mock.patch.object(
                bootstrap,
                "_path_is_reparse",
                side_effect=lambda path: path == installed or original_reparse(path),
            ):
                self.assertFalse(bootstrap._target_marker_matches(target, stamp))

    def test_dependency_cache_rejects_a_reparse_home(self) -> None:
        from mcp_server import bootstrap

        with tempfile.TemporaryDirectory() as temporary_directory:
            home = pathlib.Path(temporary_directory)
            original_reparse = bootstrap._path_is_reparse
            with (
                mock.patch.object(bootstrap.Path, "home", return_value=home),
                mock.patch.object(
                    bootstrap,
                    "_path_is_reparse",
                    side_effect=lambda path: path == home or original_reparse(path),
                ),
            ):
                with self.assertRaises(RuntimeError):
                    bootstrap._get_target_dir("ab" * 32)


if __name__ == "__main__":
    unittest.main()
