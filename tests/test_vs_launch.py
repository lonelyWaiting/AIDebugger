"""Unit tests for launching a Visual Studio instance from the MCP server."""

import asyncio
import json
import pathlib
import stat
import sys
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import AsyncMock, patch


ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from mcp_server import bootstrap

bootstrap.ensure_deps()

from mcp_server import vs_bridge, vs_launch, vs_server, vs_session, vs_tool_registry, vs_trust
from mcp_server.tool_modules import vs_instance_tools
from mcp_server.vs_errors import VSBridgeError


COMMUNITY_PRODUCT = "Microsoft.VisualStudio.Product.Community"
BUILD_TOOLS_PRODUCT = "Microsoft.VisualStudio.Product.BuildTools"


def installation(
    version: str = "18.10.0.0",
    instance_id: str = "community",
    product_id: str = COMMUNITY_PRODUCT,
    install_path: str = r"C:\Program Files\Microsoft Visual Studio\18\Community",
) -> vs_launch.DevenvInstallation:
    return vs_launch.DevenvInstallation(
        instance_id=instance_id,
        installation_version=version,
        product_id=product_id,
        install_path=install_path,
        devenv_path=install_path + r"\Common7\IDE\devenv.exe",
    )


class FakeProcess:
    """Minimal stand-in for subprocess.Popen used by liveness checks."""

    def __init__(self, pid: int = 4242, returncode=None):
        self.pid = pid
        self.returncode = returncode

    def poll(self):
        return self.returncode


def record_for(pid: int = 4242, alive: bool = True, **overrides) -> vs_launch.LaunchRecord:
    record = vs_launch.LaunchRecord(
        process_id=pid,
        launch_id="0" * 32,
        installation=installation(),
        requested_capabilities=("debug.read", "server.control"),
        start_time_utc_ticks=123456789,
        process=FakeProcess(pid=pid, returncode=None if alive else 1),
    )
    for name, value in overrides.items():
        setattr(record, name, value)
    return record


def instance_payload(pid: int = 4242, status: str = "ok", **overrides):
    info = {
        "processId": pid,
        "connectionStatus": status,
        "solutionName": "(No solution open)",
        "allowedCapabilities": ["debug.read", "server.control"],
        "accessMode": "read_only",
    }
    info.update(overrides)
    return vs_bridge.VSInstance(f"AIDebuggerVS_{pid}", info)


class LaunchArgumentTests(unittest.TestCase):
    def test_switches_precede_the_solution_and_suppress_the_start_page(self):
        with tempfile.NamedTemporaryFile(suffix=".sln") as handle:
            arguments = vs_launch.build_launch_arguments(
                installation(), solution_path=handle.name, root_suffix="AIDebuggerProbe"
            )
        self.assertEqual(arguments[0], installation().devenv_path)
        self.assertEqual(arguments[1:3], ["/RootSuffix", "AIDebuggerProbe"])
        self.assertEqual(arguments[3], handle.name)
        # A bare /Command whose name is unknown makes Visual Studio pop a modal
        # "Command is not valid" dialog, so the suppressing command must be built in
        # and the argument pair must always be present.
        self.assertEqual(arguments[4:], ["/Command", vs_launch.START_PAGE_SUPPRESSING_COMMAND])
        self.assertNotIn("-", vs_launch.START_PAGE_SUPPRESSING_COMMAND)

    def test_start_page_suppression_is_present_without_a_solution(self):
        arguments = vs_launch.build_launch_arguments(installation())
        self.assertEqual(arguments[1:], ["/Command", vs_launch.START_PAGE_SUPPRESSING_COMMAND])

    def test_missing_solution_is_rejected(self):
        with self.assertRaises(VSBridgeError) as caught:
            vs_launch.build_launch_arguments(installation(), solution_path="C:/missing/nothing.sln")
        self.assertEqual(caught.exception.code, "INVALID_ARGUMENT")

    def test_malformed_root_suffix_is_rejected(self):
        with self.assertRaises(VSBridgeError) as caught:
            vs_launch.build_launch_arguments(installation(), root_suffix="bad suffix!")
        self.assertEqual(caught.exception.code, "INVALID_ARGUMENT")


class LaunchEnvironmentTests(unittest.TestCase):
    def test_environment_contract_is_additive_and_does_not_mutate_the_source(self):
        base = {"PATH": "x", vs_launch.LAUNCH_ID_VARIABLE: "stale"}
        environment = vs_launch.build_launch_environment(
            ["debug.session.write"], "a" * 32, 1700000000000, base=base
        )
        self.assertEqual(base[vs_launch.LAUNCH_ID_VARIABLE], "stale")
        self.assertEqual(environment[vs_launch.LAUNCH_ID_VARIABLE], "a" * 32)
        self.assertEqual(environment[vs_launch.LAUNCH_EXPIRES_VARIABLE], "1700000000000")
        self.assertEqual(environment[vs_launch.LAUNCH_CAPABILITIES_VARIABLE], "debug.session.write")
        self.assertEqual(environment["PATH"], "x")


class CapabilityRequestTests(unittest.TestCase):
    def test_default_request_is_the_full_debug_set(self):
        requested = vs_launch.parse_requested_capabilities(None)
        self.assertEqual(list(requested), sorted(vs_launch.all_launchable_capabilities()))
        self.assertIn("debug.session.write", requested)
        self.assertIn("server.control", requested)

    def test_explicit_request_is_narrowed_and_sorted(self):
        requested = vs_launch.parse_requested_capabilities(" debug.session.write , debug.read ,debug.read")
        self.assertEqual(requested, ("debug.read", "debug.session.write", "server.control"))

    def test_unknown_or_malformed_capabilities_are_rejected(self):
        for raw in ("debug.nonexistent", "Debug.Read", "not a capability"):
            with self.subTest(raw=raw), self.assertRaises(VSBridgeError) as caught:
                vs_launch.parse_requested_capabilities(raw)
            self.assertEqual(caught.exception.code, "INVALID_ARGUMENT")

    def test_empty_request_is_rejected_rather_than_silently_widened(self):
        with self.assertRaises(VSBridgeError) as caught:
            vs_launch.parse_requested_capabilities(" , ")
        self.assertEqual(caught.exception.code, "INVALID_ARGUMENT")


class InstallationSelectionTests(unittest.TestCase):
    def test_newest_installation_wins_and_build_tools_is_never_an_ide(self):
        self.assertFalse(vs_launch._is_ide_product(BUILD_TOOLS_PRODUCT))
        self.assertTrue(vs_launch._is_ide_product(COMMUNITY_PRODUCT))
        newest = installation(version="18.10.0.0", instance_id="newest")
        older = installation(version="17.14.0.0", instance_id="older")
        selected = vs_launch.select_devenv_installation([older, newest])
        self.assertEqual(selected.instance_id, "newest")

    def test_explicit_instance_id_and_path_are_honoured(self):
        older = installation(
            version="17.14.0.0",
            instance_id="older",
            install_path=r"C:\Program Files (x86)\Microsoft Visual Studio\2022\Community",
        )
        newest = installation(version="18.10.0.0", instance_id="newest")
        self.assertEqual(
            vs_launch.select_devenv_installation([newest, older], instance_id="older").instance_id,
            "older",
        )
        self.assertEqual(
            vs_launch.select_devenv_installation(
                [newest, older], install_path=older.install_path
            ).instance_id,
            "older",
        )

    def test_unknown_explicit_installation_is_reported_with_candidates(self):
        with self.assertRaises(VSBridgeError) as caught:
            vs_launch.select_devenv_installation([installation()], instance_id="nope")
        self.assertEqual(caught.exception.code, "TARGET_NOT_FOUND")
        self.assertIn("community", str(caught.exception))

    def test_no_installations_is_reported(self):
        with self.assertRaises(VSBridgeError) as caught:
            vs_launch.select_devenv_installation([])
        self.assertEqual(caught.exception.code, "VS_INSTALLATION_NOT_FOUND")


class DiscoveryTests(unittest.TestCase):
    def _run_discovery(self, payload, trusted_paths, existing):
        completed = SimpleNamespace(returncode=0, stdout=json.dumps(payload), stderr="")
        vswhere = pathlib.Path(tempfile.gettempdir()) / "vswhere.exe"
        with patch.object(vs_launch, "_vswhere_path", return_value=vswhere), patch.object(
            vswhere.__class__, "is_file", return_value=True
        ), patch.object(vs_launch.subprocess, "run", return_value=completed), patch.object(
            vs_launch.vs_trust, "_trusted_visual_studio_paths", return_value=frozenset(trusted_paths)
        ), patch.object(pathlib.Path, "is_file", autospec=True, side_effect=existing):
            return vs_launch.discover_devenv_installations()

    def test_ide_installations_are_filtered_by_product_and_trust_allow_list(self):
        root = pathlib.Path(tempfile.mkdtemp())
        community = root / "Community"
        build_tools = root / "BuildTools"
        for path in (community, build_tools):
            (path / "Common7" / "IDE").mkdir(parents=True)
            (path / "Common7" / "IDE" / "devenv.exe").write_text("", encoding="utf-8")
        payload = [
            {
                "instanceId": "community",
                "installationVersion": "18.10.0.0",
                "productId": COMMUNITY_PRODUCT,
                "installationPath": str(community),
            },
            {
                "instanceId": "buildtools",
                "installationVersion": "17.14.0.0",
                "productId": BUILD_TOOLS_PRODUCT,
                "installationPath": str(build_tools),
            },
        ]
        import os

        trusted = {os.path.normcase(os.path.abspath(community / "Common7" / "IDE" / "devenv.exe"))}
        discovered = self._run_discovery(payload, trusted, [True])
        self.assertEqual([item.instance_id for item in discovered], ["community"])


class DteAutomationTests(unittest.TestCase):
    def test_launcher_command_reuses_the_long_standing_menu_command(self):
        # Reusing an existing canonical name is what keeps the dispatch resolvable in
        # Visual Studio's command table; a brand-new name is "not valid" until the IDE
        # refreshes that table.
        self.assertEqual(vs_launch.LAUNCH_COMMAND_CANONICAL_NAME, "AIDebugger.StartFullAccessServer")

    def test_dte_lookup_never_raises_and_reports_why(self):
        # Without a matching ROT entry (or without COM at all) the helper must degrade to a
        # diagnostic string instead of breaking the launch.
        vs_launch.DTE_LOOKUP_TIMEOUT_SECONDS, original = 0.0, vs_launch.DTE_LOOKUP_TIMEOUT_SECONDS
        try:
            status = vs_launch._invoke_launcher_command(999_999_999, "0" * 32)
        finally:
            vs_launch.DTE_LOOKUP_TIMEOUT_SECONDS = original
        self.assertIsInstance(status, str)
        self.assertTrue(status, "a diagnostic must always be produced")


class ReadyWaitTests(unittest.TestCase):
    def setUp(self):
        self._poll = vs_launch.READY_POLL_INTERVAL_SECONDS
        vs_launch.READY_POLL_INTERVAL_SECONDS = 0.01
        self.addCleanup(setattr, vs_launch, "READY_POLL_INTERVAL_SECONDS", self._poll)

    def test_waits_through_an_unreachable_registration_until_the_pipe_is_reachable(self):
        record = record_for(pid=4242)
        responses = [
            [instance_payload(4242, "unreachable", lastError="pipe not found")],
            [instance_payload(4242)],
        ]
        with patch.object(vs_launch.vs_bridge, "discover_instances", AsyncMock(side_effect=responses)):
            instance, diagnostics = asyncio.run(vs_launch.wait_for_ready_instance(record, 5.0))
        self.assertIsNotNone(instance)
        self.assertEqual(instance.process_id, 4242)
        self.assertTrue(any("not\nreachable yet" in entry or "not reachable yet" in entry for entry in diagnostics))
        self.assertIsNone(record.failure)

    def test_ignores_other_instances_and_fails_fast_when_the_child_exits(self):
        record = record_for(pid=4242, alive=False)
        with patch.object(
            vs_launch.vs_bridge,
            "discover_instances",
            AsyncMock(return_value=[instance_payload(9999)]),
        ):
            instance, diagnostics = asyncio.run(vs_launch.wait_for_ready_instance(record, 30.0))
        self.assertIsNone(instance)
        self.assertEqual(record.failure, "process_exited")
        self.assertTrue(any("exited before the AIDebugger server" in entry for entry in diagnostics))

    def test_timeout_is_reported_when_the_child_stays_up_without_registering(self):
        record = record_for(pid=4242)
        with patch.object(vs_launch.vs_bridge, "discover_instances", AsyncMock(return_value=[])):
            instance, _ = asyncio.run(vs_launch.wait_for_ready_instance(record, 0.05))
        self.assertIsNone(instance)
        self.assertEqual(record.failure, "ready_timeout")


class EnsureInstanceTests(unittest.TestCase):
    def setUp(self):
        self._launches = dict(vs_launch._LAUNCHES)
        self.addCleanup(self._restore_launches)

    def _restore_launches(self):
        vs_launch._LAUNCHES.clear()
        vs_launch._LAUNCHES.update(self._launches)

    def test_reuses_a_single_reachable_instance_without_starting_a_process(self):
        def explode(*args, **kwargs):
            raise AssertionError("a launch must not happen when an instance is reachable")

        with patch.object(
            vs_launch.vs_bridge, "discover_instances", AsyncMock(return_value=[instance_payload(99)])
        ), patch.object(vs_launch.subprocess, "Popen", side_effect=explode):
            outcome = asyncio.run(vs_launch.ensure_instance(("debug.read", "server.control")))
        self.assertEqual(outcome.status, vs_launch.REUSED_STATUS)
        self.assertEqual(outcome.instance.process_id, 99)
        self.assertIsNone(outcome.record)

    def test_ambiguous_reachable_instances_are_reported_instead_of_launching(self):
        instances = [instance_payload(11), instance_payload(22)]
        with patch.object(
            vs_launch.vs_bridge, "discover_instances", AsyncMock(return_value=instances)
        ), patch.object(
            vs_launch.vs_bridge.get_bridge(), "auto_select_instance", return_value=None
        ), patch.object(vs_launch, "discover_devenv_installations") as discover:
            outcome = asyncio.run(vs_launch.ensure_instance(("debug.read",)))
        self.assertEqual(outcome.status, vs_launch.AMBIGUOUS_STATUS)
        self.assertIsNone(outcome.instance)
        self.assertIn("select_vs_instance", outcome.message)
        discover.assert_not_called()

    def test_launches_and_reports_a_ready_instance(self):
        record = record_for(pid=31415)
        ready = instance_payload(31415)
        with patch.object(
            vs_launch.vs_bridge, "discover_instances", AsyncMock(return_value=[])
        ), patch.object(
            vs_launch, "discover_devenv_installations", return_value=[installation()]
        ), patch.object(
            vs_launch, "launch_devenv", return_value=record
        ) as launch, patch.object(
            vs_launch, "_request_launcher_server_start",
            AsyncMock(return_value="launcher command dispatched"),
        ) as request_start, patch.object(
            vs_launch, "wait_for_ready_instance", AsyncMock(return_value=(ready, []))
        ):
            outcome = asyncio.run(
                vs_launch.ensure_instance(("debug.read",), solution_path=None, root_suffix="Exp")
            )
        self.assertEqual(outcome.status, vs_launch.READY_STATUS)
        self.assertEqual(outcome.instance.process_id, 31415)
        self.assertEqual(outcome.record.launch_id, "0" * 32)
        self.assertEqual(launch.call_args.kwargs["root_suffix"], "Exp")
        # The /Command switch starts the server while the shell comes up, so no COM
        # automation is paid for on the happy path.
        request_start.assert_not_called()

    def test_falls_back_to_dte_when_the_command_switch_starts_nothing(self):
        record = record_for(pid=31415)
        ready = instance_payload(31415)
        with patch.object(
            vs_launch.vs_bridge, "discover_instances", AsyncMock(return_value=[])
        ), patch.object(
            vs_launch, "discover_devenv_installations", return_value=[installation()]
        ), patch.object(
            vs_launch, "launch_devenv", return_value=record
        ), patch.object(
            vs_launch, "wait_for_ready_instance",
            AsyncMock(side_effect=[(None, []), (ready, [])]),
        ), patch.object(
            vs_launch, "_request_launcher_server_start",
            AsyncMock(return_value="launcher command dispatched as Tools.AIDebugger.StartFullAccessServer"),
        ) as request_start:
            outcome = asyncio.run(vs_launch.ensure_instance(("debug.read",)))
        self.assertEqual(outcome.status, vs_launch.READY_STATUS)
        self.assertEqual(request_start.await_count, 1)
        self.assertTrue(any("dispatched as" in entry for entry in outcome.diagnostics))

    def test_narrower_granted_capabilities_are_reported_as_a_diagnostic(self):
        record = record_for(pid=31415)
        ready = instance_payload(31415, allowedCapabilities=["debug.read"])
        ready.allowed_capabilities = ["debug.read"]
        with patch.object(
            vs_launch.vs_bridge, "discover_instances", AsyncMock(return_value=[])
        ), patch.object(
            vs_launch, "discover_devenv_installations", return_value=[installation()]
        ), patch.object(
            vs_launch, "launch_devenv", return_value=record
        ), patch.object(
            vs_launch, "_request_launcher_server_start",
            AsyncMock(return_value="launcher command dispatched"),
        ), patch.object(
            vs_launch, "wait_for_ready_instance", AsyncMock(return_value=(ready, []))
        ):
            outcome = asyncio.run(
                vs_launch.ensure_instance(("debug.read", "debug.session.write"))
            )
        self.assertEqual(outcome.status, vs_launch.READY_STATUS)
        self.assertTrue(any("narrower capability set" in entry for entry in outcome.diagnostics))

    def test_concurrent_launches_join_the_one_already_in_flight(self):
        in_flight = record_for(pid=777, alive=True)
        vs_launch._LAUNCHES[777] = in_flight
        with patch.object(
            vs_launch.vs_bridge, "discover_instances", AsyncMock(return_value=[])
        ), patch.object(
            vs_launch, "launch_devenv"
        ) as launch, patch.object(
            vs_launch, "_request_launcher_server_start"
        ) as request_start, patch.object(
            vs_launch, "wait_for_ready_instance", AsyncMock(return_value=(instance_payload(777), []))
        ):
            outcome = asyncio.run(vs_launch.ensure_instance(("debug.read",)))
        launch.assert_not_called()
        # Joining an in-flight launch must not re-dispatch the command; the process that
        # started it owns that.
        request_start.assert_not_called()
        self.assertEqual(outcome.record.process_id, 777)
        self.assertTrue(any("already in progress" in entry for entry in outcome.diagnostics))


class CloseInstanceTests(unittest.TestCase):
    def setUp(self):
        self._launches = dict(vs_launch._LAUNCHES)
        self.addCleanup(self._restore_launches)

    def _restore_launches(self):
        vs_launch._LAUNCHES.clear()
        vs_launch._LAUNCHES.update(self._launches)

    def test_refuses_a_pid_this_mcp_server_did_not_launch(self):
        result = asyncio.run(vs_launch.close_owned_instance(4242))
        self.assertFalse(result["success"])
        self.assertEqual(result["errorCode"], "NOT_LAUNCHED_BY_MCP")

    def test_refuses_an_active_debug_session_unless_forced(self):
        vs_launch._LAUNCHES[4242] = record_for(pid=4242)
        with patch.object(vs_launch, "_process_image_name", return_value="devenv.exe"), patch.object(
            vs_launch, "_process_start_time_utc_ticks", return_value=123456789
        ):
            result = asyncio.run(
                vs_launch.close_owned_instance(4242, debug_state={"isDebugging": True})
            )
        self.assertFalse(result["success"])
        self.assertEqual(result["errorCode"], "DEBUG_SESSION_ACTIVE")

    def test_refuses_when_the_pid_no_longer_matches_the_launched_process(self):
        vs_launch._LAUNCHES[4242] = record_for(pid=4242)
        with patch.object(vs_launch, "_process_start_time_utc_ticks", return_value=999999999):
            result = asyncio.run(vs_launch.close_owned_instance(4242, force=True))
        self.assertFalse(result["success"])
        self.assertEqual(result["errorCode"], "STALE_TARGET")

    def test_already_closed_instance_is_reported_without_killing_anything(self):
        vs_launch._LAUNCHES[4242] = record_for(pid=4242, alive=False)
        with patch.object(vs_launch.subprocess, "run") as run:
            result = asyncio.run(vs_launch.close_owned_instance(4242))
        run.assert_not_called()
        self.assertTrue(result["success"])
        self.assertTrue(result["closed"])


class ToolContractTests(unittest.TestCase):
    """Drive the real FastMCP app so the new tools are validated like every other tool."""

    def setUp(self):
        self._session = vs_session._session
        self.addCleanup(setattr, vs_session, "_session", self._session)
        self._launches = dict(vs_launch._LAUNCHES)
        self.addCleanup(self._restore_launches)

    def _restore_launches(self):
        vs_launch._LAUNCHES.clear()
        vs_launch._LAUNCHES.update(self._launches)

    def _call(self, coroutine_factory):
        return asyncio.run(coroutine_factory())

    def _reusable_instance(self, pid: int = 4242):
        return vs_bridge.VSInstance(
            f"AIDebuggerVS_{pid}",
            {
                "processId": pid,
                "processStartTimeUtcTicks": 42000,
                "serverInstanceId": "a" * 32,
                "connectionStatus": "ok",
                "allowedCapabilities": ["debug.read", "debug.session.write", "server.control"],
                "accessMode": "full_access",
            },
            registration_snapshot={
                "protocol": "aidb/2",
                "pipeName": f"AIDebuggerVS_{pid}",
                "controlPipeName": f"AIDebuggerVS_{pid}_control",
                "processId": pid,
                "processStartTimeUtcTicks": 42000,
                "serverInstanceId": "a" * 32,
                "protocolCatalogHash": vs_tool_registry.DEBUG_METHOD_CATALOG_HASH,
                "authToken": "private",
                "capabilities": ["debug.read", "server.control"],
            },
        )

    def _fake_session(self):
        instance = self._reusable_instance()

        class FakeSession:
            def __init__(self):
                self.generation = 0
                self.selected = None

            async def discover_instances(self, preferred_ide=None, timeout=None):
                return [instance]

            async def get_selection_snapshot(self):
                return (None, None, self.selected, self.generation)

            def get_preferred_ide(self):
                return "vs"

            def auto_select_instance(self, instances, preferred_ide=None):
                return instances[0] if len(instances) == 1 else None

            async def try_select_instance(self, expected_generation, **kwargs):
                self.selected = dict(kwargs["target_identity"])
                self.generation += 1
                return (True, self.selected, self.generation)

        return FakeSession()

    def test_reused_instance_result_satisfies_the_published_contract(self):
        vs_session._session = self._fake_session()
        instance = self._reusable_instance()
        outcome = vs_launch.EnsureOutcome(
            status=vs_launch.REUSED_STATUS, message="Reused.", instance=instance
        )

        async def check():
            with patch.object(
                vs_instance_tools.vs_launch, "ensure_instance", AsyncMock(return_value=outcome)
            ):
                return await vs_server.mcp.call_tool("ensure_vs_instance", {})

        _, payload = self._call(check)
        result = payload["result"]
        self.assertTrue(result["success"])
        self.assertEqual(result["status"], "reused")
        self.assertFalse(result["launched"])
        self.assertFalse(result["attached"])
        self.assertTrue(result["instance"]["isSelected"])
        self.assertIn("debug.session.write", result["grantedCapabilities"])

    def test_ready_timeout_result_is_partial_and_actionable(self):
        vs_session._session = self._fake_session()
        record = record_for(pid=5150)
        outcome = vs_launch.EnsureOutcome(
            status=vs_launch.READY_TIMEOUT_STATUS,
            message="Visual Studio PID 5150 started but did not register.",
            record=record,
            diagnostics=["Visual Studio PID 5150 has a registration that is not reachable yet: x"],
        )

        async def check():
            with patch.object(
                vs_instance_tools.vs_launch, "ensure_instance", AsyncMock(return_value=outcome)
            ):
                return await vs_server.mcp.call_tool("ensure_vs_instance", {})

        _, payload = self._call(check)
        result = payload["result"]
        self.assertFalse(result["success"])
        self.assertEqual(result["status"], "ready_timeout")
        self.assertTrue(result["partial"])
        self.assertTrue(result["launched"])
        self.assertEqual(result["processId"], 5150)
        self.assertIn("extension", result["hint"])

    def test_ambiguous_selection_never_launches_and_explains_the_next_step(self):
        vs_session._session = self._fake_session()
        outcome = vs_launch.EnsureOutcome(
            status=vs_launch.AMBIGUOUS_STATUS, message="Several instances are reachable."
        )

        async def check():
            with patch.object(
                vs_instance_tools.vs_launch, "ensure_instance", AsyncMock(return_value=outcome)
            ):
                return await vs_server.mcp.call_tool("ensure_vs_instance", {})

        _, payload = self._call(check)
        result = payload["result"]
        self.assertFalse(result["success"])
        self.assertEqual(result["errorCode"], "TARGET_NOT_FOUND")
        self.assertIn("select_vs_instance", result["hint"])

    def test_attach_path_reports_activation_and_validates_the_attach_result(self):
        vs_session._session = self._fake_session()
        instance = self._reusable_instance()
        outcome = vs_launch.EnsureOutcome(
            status=vs_launch.REUSED_STATUS, message="Reused.", instance=instance
        )
        attach_result = {
            "success": True,
            "message": "Attached to process 1234.",
            "action": "attach_process",
            "attachCompleted": True,
            "debuggerActivationStatus": "active",
        }

        async def check():
            with patch.object(
                vs_instance_tools.vs_launch, "ensure_instance", AsyncMock(return_value=outcome)
            ), patch.object(
                vs_instance_tools.backend, "call", AsyncMock(return_value=attach_result)
            ) as call, patch.object(
                vs_instance_tools,
                "wait_for_debugging_to_start",
                AsyncMock(return_value=attach_result),
            ):
                result = await vs_server.mcp.call_tool(
                    "ensure_vs_instance", {"process_id": 1234}
                )
                return result, call

        (_, payload), call = self._call(check)
        result = payload["result"]
        self.assertTrue(result["attached"])
        self.assertEqual(result["attachResult"]["debuggerActivationStatus"], "active")
        self.assertEqual(call.call_args.args[0], "attach_process")
        self.assertEqual(call.call_args.args[1], {"processId": 1234})

    def test_close_refuses_an_instance_the_mcp_server_did_not_launch(self):
        async def check():
            return await vs_server.mcp.call_tool("close_vs_instance", {"process_id": 987654})

        _, payload = self._call(check)
        result = payload["result"]
        self.assertFalse(result["success"])
        self.assertEqual(result["errorCode"], "NOT_LAUNCHED_BY_MCP")
        self.assertFalse(result["closed"])

    def test_both_tools_are_published_with_conservative_annotations(self):
        summary = vs_tool_registry.tool_registry_summary()
        support = summary["toolSupport"]
        self.assertIn("ensure_vs_instance", summary["domains"]["instance"])
        self.assertIn("close_vs_instance", summary["domains"]["instance"])
        launch_annotations = support["ensure_vs_instance"]["defaultInvocationAnnotations"]
        self.assertFalse(launch_annotations["readOnlyHint"])
        self.assertTrue(launch_annotations["destructiveHint"])
        close_annotations = support["close_vs_instance"]["defaultInvocationAnnotations"]
        self.assertFalse(close_annotations["readOnlyHint"])
        self.assertTrue(close_annotations["destructiveHint"])
        self.assertFalse(close_annotations["idempotentHint"])


if __name__ == "__main__":
    unittest.main()


class PersistedLaunchRecordTests(unittest.TestCase):
    """The launch record the server writes must satisfy the same trust contract it validates."""

    def test_record_file_gets_a_protected_owner_only_dacl(self):
        import shutil
        import win32security
        import win32api

        root = pathlib.Path(tempfile.mkdtemp()) / "launches"
        try:
            directory = vs_trust.ensure_owner_only_directory(root)
            record = directory / "4242.json"
            record.write_text("{}", encoding="utf-8")
            vs_trust.apply_owner_only_security(record)

            security = win32security.GetFileSecurity(
                str(record),
                win32security.OWNER_SECURITY_INFORMATION | win32security.DACL_SECURITY_INFORMATION,
            )
            token = win32security.OpenProcessToken(
                win32api.GetCurrentProcess(), win32security.TOKEN_QUERY
            )
            user = win32security.ConvertSidToStringSid(
                win32security.GetTokenInformation(token, win32security.TokenUser)[0]
            )
            control, _ = security.GetSecurityDescriptorControl()
            dacl = security.GetSecurityDescriptorDacl()
            self.assertEqual(
                win32security.ConvertSidToStringSid(security.GetSecurityDescriptorOwner()), user
            )
            self.assertTrue(bool(control & win32security.SE_DACL_PROTECTED), "DACL must not inherit")
            self.assertEqual(dacl.GetAceCount(), 1)
        finally:
            shutil.rmtree(root.parent, ignore_errors=True)

    def test_persisted_record_is_reloaded_as_owned(self):
        # A record written for a live PID must be accepted after the in-process registry is
        # dropped, which is what makes close_vs_instance work across an MCP restart. The
        # trust validation is stubbed because it (correctly) only accepts the real
        # %LOCALAPPDATA%\AIDebugger\launches path; its own contract is covered above.
        import os
        import shutil

        root = pathlib.Path(tempfile.mkdtemp()) / "launches"
        record = record_for(pid=os.getpid())
        record.start_time_utc_ticks = vs_launch._process_start_time_utc_ticks(os.getpid())
        self.addCleanup(shutil.rmtree, root.parent, True)
        self.addCleanup(vs_launch._LAUNCHES.clear)
        try:
            with patch.object(vs_launch, "_launches_root", return_value=root), patch.object(
                vs_trust, "_validate_registration_security", lambda *args, **kwargs: None
            ):
                vs_launch._persist_launch_record(record)
                vs_launch._LAUNCHES.clear()
                vs_launch._LAUNCHES_LOADED = False
                owned = vs_launch.get_owned_launch(os.getpid())
        finally:
            vs_launch._LAUNCHES_LOADED = False
        self.assertIsNotNone(owned, "the server must recognize the instance it launched")
        self.assertEqual(owned.process_id, os.getpid())

    def test_record_of_a_dead_pid_is_pruned(self):
        import os
        import shutil

        root = pathlib.Path(tempfile.mkdtemp()) / "launches"
        dead = record_for(pid=os.getpid())
        dead.start_time_utc_ticks = 12345  # does not match this process
        self.addCleanup(shutil.rmtree, root.parent, True)
        self.addCleanup(vs_launch._LAUNCHES.clear)
        try:
            with patch.object(vs_launch, "_launches_root", return_value=root), patch.object(
                vs_trust, "_validate_registration_security", lambda *args, **kwargs: None
            ):
                vs_launch._persist_launch_record(dead)
                vs_launch._LAUNCHES.clear()
                vs_launch._LAUNCHES_LOADED = False
                owned = vs_launch.get_owned_launch(os.getpid())
                still_there = list(root.glob("*.json"))
        finally:
            vs_launch._LAUNCHES_LOADED = False
        self.assertIsNone(owned, "a reused or exited PID must not look owned")
        self.assertEqual(still_there, [], "the stale record should be pruned")
