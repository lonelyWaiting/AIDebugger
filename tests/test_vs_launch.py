"""Unit tests for launching a Visual Studio instance from the MCP server."""

import asyncio
import json
import pathlib
import stat
import subprocess
import sys
import tempfile
import time
import unittest
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock, patch


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
            outcome = asyncio.run(vs_launch.ensure_instance(("debug.read", "server.control")))
        launch.assert_not_called()
        # Joining an in-flight launch must not re-dispatch the command; the process that
        # started it owns that.
        request_start.assert_not_called()
        self.assertEqual(outcome.record.process_id, 777)
        self.assertTrue(any("already in progress" in entry for entry in outcome.diagnostics))

    def test_discovery_and_process_start_do_not_block_the_event_loop(self):
        record = record_for(pid=31415)
        beats = []

        def slow_discovery(*args, **kwargs):
            time.sleep(0.3)
            return [installation()]

        def slow_launch(*args, **kwargs):
            time.sleep(0.2)
            return record

        async def scenario():
            async def heartbeat():
                while True:
                    beats.append(time.monotonic())
                    await asyncio.sleep(0.01)

            beat = asyncio.ensure_future(heartbeat())
            try:
                return await vs_launch.ensure_instance(("debug.read", "server.control"))
            finally:
                beat.cancel()

        with patch.object(
            vs_launch.vs_bridge, "discover_instances", AsyncMock(return_value=[])
        ), patch.object(
            vs_launch, "discover_devenv_installations", side_effect=slow_discovery
        ), patch.object(vs_launch, "launch_devenv", side_effect=slow_launch), patch.object(
            vs_launch, "_request_launcher_server_start", AsyncMock(return_value="dispatched")
        ), patch.object(
            vs_launch, "wait_for_ready_instance", AsyncMock(return_value=(instance_payload(31415), []))
        ):
            outcome = asyncio.run(scenario())
        self.assertEqual(outcome.status, vs_launch.READY_STATUS)
        # vswhere and process creation sleep 0.5s in total; a blocked event loop would
        # starve the heartbeat down to the one tick it gets before the first await.
        self.assertGreater(len(beats), 10)

    def test_concurrent_callers_start_at_most_one_process(self):
        record = record_for(pid=31415)

        def launch_once(*args, **kwargs):
            # Mimic launch_devenv: the record only becomes joinable once it is registered.
            time.sleep(0.2)
            vs_launch._LAUNCHES[record.process_id] = record
            return record

        async def scenario():
            return await asyncio.gather(
                vs_launch.ensure_instance(("debug.read", "server.control")),
                vs_launch.ensure_instance(("debug.read", "server.control")),
            )

        with patch.object(
            vs_launch.vs_bridge, "discover_instances", AsyncMock(return_value=[])
        ), patch.object(
            vs_launch, "discover_devenv_installations", return_value=[installation()]
        ), patch.object(vs_launch, "launch_devenv", side_effect=launch_once) as launch, patch.object(
            vs_launch, "_request_launcher_server_start", AsyncMock(return_value="dispatched")
        ), patch.object(
            vs_launch, "wait_for_ready_instance", AsyncMock(return_value=(instance_payload(31415), []))
        ):
            outcomes = asyncio.run(scenario())
        self.assertEqual(launch.call_count, 1)
        self.assertEqual([outcome.record.process_id for outcome in outcomes], [31415, 31415])
        joined = [outcome for outcome in outcomes if outcome.record is not None]
        self.assertEqual(len(joined), 2)
        self.assertTrue(
            any(
                "already in progress" in entry
                for outcome in outcomes
                for entry in outcome.diagnostics
            )
        )


class RecordLivenessTests(unittest.TestCase):
    """A record restored from disk has no process handle, so liveness needs care."""

    def test_os_query_failure_distinguishes_absence_from_access_denied(self):
        kernel = Mock()
        kernel.OpenProcess.return_value = 0
        with patch.object(vs_launch.ctypes, "WinDLL", return_value=kernel, create=True):
            with patch.object(vs_launch.ctypes, "get_last_error", return_value=5, create=True):
                self.assertIsNone(vs_launch._process_state(4242))
            with patch.object(vs_launch.ctypes, "get_last_error", return_value=87, create=True):
                self.assertEqual(vs_launch._process_state(4242), (0, False))

    def _restored(self, **overrides):
        return record_for(pid=4242, process=None, **overrides)

    def test_a_live_handle_decides_for_itself(self):
        self.assertTrue(vs_launch._record_alive(record_for(pid=4242, alive=True)))
        self.assertFalse(vs_launch._record_alive(record_for(pid=4242, alive=False)))

    def test_a_matching_running_process_is_alive(self):
        with patch.object(vs_launch, "_process_state", return_value=(123456789, True)):
            self.assertTrue(vs_launch._record_alive(self._restored()))

    def test_a_terminated_process_object_is_not_alive(self):
        # Windows keeps a terminated process queryable while any handle to it is open, so
        # a successful OpenProcess must not be read as "still running".
        with patch.object(vs_launch, "_process_state", return_value=(123456789, False)):
            self.assertFalse(vs_launch._record_alive(self._restored()))

    def test_a_reused_pid_is_not_alive(self):
        with patch.object(vs_launch, "_process_state", return_value=(999999999, True)):
            self.assertFalse(vs_launch._record_alive(self._restored()))

    def test_a_missing_process_object_is_not_alive(self):
        with patch.object(vs_launch, "_process_state", return_value=None):
            self.assertFalse(vs_launch._record_alive(self._restored()))

    def test_a_record_without_creation_ticks_falls_back_to_existence(self):
        # Only the existence check is left when nothing was recorded to compare against.
        record = self._restored(start_time_utc_ticks=None)
        with patch.object(vs_launch, "_process_state", return_value=(123456789, True)):
            self.assertTrue(vs_launch._record_alive(record))
        with patch.object(vs_launch, "_process_state", return_value=None):
            self.assertFalse(vs_launch._record_alive(record))

    def test_ticks_are_only_reported_for_a_running_process(self):
        with patch.object(vs_launch, "_process_state", return_value=(123456789, True)):
            self.assertEqual(vs_launch._process_start_time_utc_ticks(4242), 123456789)
        with patch.object(vs_launch, "_process_state", return_value=(123456789, False)):
            self.assertIsNone(vs_launch._process_start_time_utc_ticks(4242))
        with patch.object(vs_launch, "_process_state", return_value=None):
            self.assertIsNone(vs_launch._process_start_time_utc_ticks(4242))


class CloseInstanceTests(unittest.TestCase):
    def test_verified_idle_instance_can_close_without_force(self):
        record = record_for()
        vs_launch._LAUNCHES[4242] = record
        def exit_process(args, **kwargs):
            record.process.returncode = 0
            return subprocess.CompletedProcess(args, 0, "", "")
        with patch.object(vs_launch, "_process_image_name", return_value="devenv.exe"), patch.object(
            vs_launch, "_process_start_time_utc_ticks", return_value=123456789
        ), patch.object(vs_launch.subprocess, "run", side_effect=exit_process) as run:
            result = asyncio.run(vs_launch.close_owned_instance(4242, debug_state={"isDebugging": False}))
        self.assertTrue(result["closed"])
        self.assertTrue(result["graceful"])
        self.assertFalse(result["forced"])
        self.assertEqual(run.call_count, 1)

    def test_nonforced_close_requires_a_definitive_idle_debugger_receipt(self):
        for state in (None, {}, {"isDebugging": None}, {"isDebugging": 0}):
            with self.subTest(state=state):
                vs_launch._LAUNCHES[4242] = record_for()
                with patch.object(vs_launch, "_process_image_name", return_value="devenv.exe"), patch.object(
                    vs_launch, "_process_start_time_utc_ticks", return_value=123456789
                ), patch.object(vs_launch.subprocess, "run") as run:
                    result = asyncio.run(vs_launch.close_owned_instance(4242, timeout_seconds=0, debug_state=state))
                self.assertFalse(result["closed"])
                self.assertEqual(result["errorCode"], "DEBUG_STATE_UNAVAILABLE")
                self.assertIn(4242, vs_launch._LAUNCHES)
                run.assert_not_called()

    def test_unavailable_process_identity_does_not_claim_close_or_discard_ownership(self):
        vs_launch._LAUNCHES[4242] = record_for(process=None)
        with patch.object(vs_launch, "_process_state", return_value=None), patch.object(
            vs_launch.subprocess, "run"
        ) as run, patch.object(vs_launch, "_delete_launch_record") as delete:
            result = asyncio.run(vs_launch.close_owned_instance(4242, force=True))
        self.assertFalse(result["closed"])
        self.assertEqual(result["errorCode"], "PROCESS_STATE_UNAVAILABLE")
        self.assertIn(4242, vs_launch._LAUNCHES)
        run.assert_not_called()
        delete.assert_not_called()

    def test_image_query_failure_is_not_reported_as_exit(self):
        vs_launch._LAUNCHES[4242] = record_for(process=None)
        with patch.object(vs_launch, "_process_state", return_value=(123456789, True)), patch.object(
            vs_launch, "_process_image_name", return_value=None
        ), patch.object(vs_launch.subprocess, "run") as run:
            result = asyncio.run(vs_launch.close_owned_instance(4242, force=True))
        self.assertFalse(result["closed"])
        self.assertIn(4242, vs_launch._LAUNCHES)
        run.assert_not_called()

    def test_unknown_exit_after_close_preserves_receipt_without_escalating(self):
        vs_launch._LAUNCHES[4242] = record_for(process=None)
        state = {"value": (123456789, True)}
        def close(args, **kwargs):
            state["value"] = None
            return subprocess.CompletedProcess(args, 0, "", "")
        with patch.object(vs_launch, "_process_state", side_effect=lambda pid: state["value"]), patch.object(
            vs_launch, "_process_image_name", return_value="devenv.exe"
        ), patch.object(vs_launch.subprocess, "run", side_effect=close) as run:
            result = asyncio.run(vs_launch.close_owned_instance(4242, force=True, timeout_seconds=0))
        self.assertFalse(result["closed"])
        self.assertFalse(result["forced"])
        self.assertEqual(result["errorCode"], "PROCESS_STATE_UNAVAILABLE")
        self.assertIn(4242, vs_launch._LAUNCHES)
        self.assertEqual(run.call_count, 1)

    def setUp(self):
        self._launches = dict(vs_launch._LAUNCHES)
        self.addCleanup(self._restore_launches)
        # Fake PID/state probes must never load or prune real launch receipts.
        loader = patch.object(vs_launch, "_load_persisted_launches")
        loader.start()
        self.addCleanup(loader.stop)
        deletion = patch.object(vs_launch, "_delete_launch_record")
        deletion.start()
        self.addCleanup(deletion.stop)

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

    def test_forced_close_escalates_without_waiting_the_whole_timeout(self):
        # A forced close must escalate within the short grace window instead of blocking for
        # the caller's full timeout while Visual Studio tears a session down.
        vs_launch._LAUNCHES[4242] = record_for(pid=4242)
        alive = {"value": True}
        killed = []

        def fake_run(args, **kwargs):
            killed.append(tuple(args))
            if "/F" in args:
                alive["value"] = False
            return subprocess.CompletedProcess(args, 0, b"", b"")

        with patch.object(vs_launch, "_process_image_name", return_value="devenv.exe"), patch.object(
            vs_launch, "_process_start_time_utc_ticks", return_value=123456789
        ), patch.object(
            vs_launch, "_record_liveness", side_effect=lambda record: alive["value"]
        ), patch.object(
            vs_launch.subprocess, "run", side_effect=fake_run
        ), patch.object(
            vs_launch, "FORCE_GRACE_SECONDS", 0.05
        ):
            started = time.monotonic()
            result = asyncio.run(
                vs_launch.close_owned_instance(4242, force=True, timeout_seconds=30.0)
            )
            elapsed = time.monotonic() - started
        self.assertTrue(result["success"], result)
        self.assertTrue(result["forced"], result)
        # A close that needed the forced termination is not a graceful one.
        self.assertFalse(result["graceful"], result)
        self.assertTrue(any("/F" in args for args in killed), killed)
        self.assertLess(elapsed, 5.0, "a forced close must not wait out the full timeout")

    def test_a_process_that_exits_during_the_grace_window_is_not_force_killed(self):
        vs_launch._LAUNCHES[4242] = record_for(pid=4242, process=None)
        state = {"value": (123456789, True)}

        def graceful_exit(args, **kwargs):
            state["value"] = (123456789, False)  # confirmed exit; nothing left to escalate to
            return subprocess.CompletedProcess(args, 0, "", "")

        with patch.object(vs_launch, "_process_image_name", return_value="devenv.exe"), patch.object(
            vs_launch, "_process_state", side_effect=lambda process_id: state["value"]
        ), patch.object(vs_launch.subprocess, "run", side_effect=graceful_exit) as run, patch.object(
            vs_launch, "FORCE_GRACE_SECONDS", 5.0
        ):
            result = asyncio.run(
                vs_launch.close_owned_instance(4242, force=True, timeout_seconds=30.0)
            )
        self.assertTrue(result["closed"], result)
        self.assertFalse(result["forced"], result)
        self.assertTrue(result["graceful"], result)
        self.assertFalse(
            [args for (args, _) in run.call_args_list if "/F" in args],
            "an already exited instance must not be escalated to a forced kill",
        )

    def test_already_closed_instance_is_reported_without_killing_anything(self):
        vs_launch._LAUNCHES[4242] = record_for(pid=4242, alive=False)
        with patch.object(vs_launch.subprocess, "run") as run:
            result = asyncio.run(vs_launch.close_owned_instance(4242))
        run.assert_not_called()
        self.assertTrue(result["success"])
        self.assertTrue(result["closed"])

    def test_an_exited_process_object_is_reported_as_closed(self):
        # The regression that made a completed close look like a timeout: Windows keeps the
        # process object of an exited Visual Studio queryable, so its PID opened fine.
        vs_launch._LAUNCHES[4242] = record_for(pid=4242, process=None)
        with patch.object(vs_launch, "_process_state", return_value=(123456789, False)), patch.object(
            vs_launch.subprocess, "run"
        ) as run:
            result = asyncio.run(vs_launch.close_owned_instance(4242, force=True))
        run.assert_not_called()
        self.assertTrue(result["closed"], result)
        self.assertNotIn(4242, vs_launch._LAUNCHES)

    def test_a_reused_pid_is_reported_as_closed_and_pruned(self):
        vs_launch._LAUNCHES[4242] = record_for(pid=4242, process=None)
        with patch.object(vs_launch, "_process_state", return_value=(999999999, True)), patch.object(
            vs_launch.subprocess, "run"
        ) as run:
            result = asyncio.run(vs_launch.close_owned_instance(4242, force=True))
        # A stranger that inherited the PID is never terminated; the instance we launched is
        # gone either way, so the record is pruned rather than left dangling.
        run.assert_not_called()
        self.assertTrue(result["closed"], result)
        self.assertNotIn(4242, vs_launch._LAUNCHES)

    def test_a_restored_record_with_matching_identity_is_closed(self):
        vs_launch._LAUNCHES[4242] = record_for(pid=4242, process=None)
        alive = {"value": True}

        def fake_run(args, **kwargs):
            if "/F" in args:
                alive["value"] = False
            return subprocess.CompletedProcess(args, 0, "", "")

        def fake_state(process_id):
            return (123456789, alive["value"])

        with patch.object(vs_launch, "_process_image_name", return_value="devenv.exe"), patch.object(
            vs_launch, "_process_state", side_effect=fake_state
        ), patch.object(vs_launch.subprocess, "run", side_effect=fake_run), patch.object(
            vs_launch, "FORCE_GRACE_SECONDS", 0.05
        ):
            result = asyncio.run(
                vs_launch.close_owned_instance(4242, force=True, timeout_seconds=30.0)
            )
        self.assertTrue(result["closed"], result)
        self.assertTrue(result["forced"], result)

    def test_a_failed_taskkill_is_reported_with_what_it_said(self):
        vs_launch._LAUNCHES[4242] = record_for(pid=4242, process=None)

        def failing_run(args, **kwargs):
            return subprocess.CompletedProcess(
                args, 1, "", "ERROR: The process with PID 4242 could not be terminated."
            )

        with patch.object(vs_launch, "_process_image_name", return_value="devenv.exe"), patch.object(
            vs_launch, "_process_state", return_value=(123456789, True)
        ), patch.object(vs_launch.subprocess, "run", side_effect=failing_run), patch.object(
            vs_launch, "FORCE_GRACE_SECONDS", 0.05
        ), patch.object(
            vs_launch, "FORCE_KILL_SECONDS", 0.05
        ):
            result = asyncio.run(
                vs_launch.close_owned_instance(4242, force=True, timeout_seconds=0.1)
            )
        self.assertFalse(result["closed"], result)
        self.assertEqual(result["errorCode"], "CLOSE_TIMEOUT")
        # Without this the caller only ever saw "did not close within the timeout" and the
        # reason taskkill gave was thrown away.
        self.assertIn("exit 1", result["message"])
        self.assertIn("could not be terminated", result["message"])


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

    def test_close_tool_refuses_unknown_debugger_status_without_closing(self):
        record = record_for(ready_instance=instance_payload(4242, processStartTimeUtcTicks=123456789,
                            serverInstanceId="11111111111111111111111111111111"))
        async def check():
            with patch.object(vs_launch, "get_owned_launch", return_value=record), patch.object(
                vs_bridge, "call_pipe_exact", AsyncMock(side_effect=VSBridgeError("status unavailable", code="PIPE_TIMEOUT"))
            ), patch.object(vs_launch, "_process_image_name", return_value="devenv.exe"), patch.object(
                vs_launch, "_process_start_time_utc_ticks", return_value=123456789
            ), patch.object(vs_launch.subprocess, "run") as run:
                response = await vs_server.mcp.call_tool("close_vs_instance", {"process_id": 4242})
                run.assert_not_called()
                return response
        _, payload = self._call(check)
        self.assertFalse(payload["result"]["closed"])
        self.assertEqual(payload["result"]["errorCode"], "DEBUG_STATE_UNAVAILABLE")

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


class PersistedLaunchRecordTests(unittest.TestCase):
    """The launch record the server writes must satisfy the same trust contract it validates."""

    def test_malformed_receipts_cannot_block_or_replace_a_valid_owned_process(self):
        import shutil
        root = pathlib.Path(tempfile.mkdtemp()) / "launches"
        root.mkdir()
        self.addCleanup(shutil.rmtree, root.parent, True)
        saved, loaded = dict(vs_launch._LAUNCHES), vs_launch._LAUNCHES_LOADED
        def restore():
            vs_launch._LAUNCHES.clear()
            vs_launch._LAUNCHES.update(saved)
            vs_launch._LAUNCHES_LOADED = loaded
        self.addCleanup(restore)
        valid = {"schemaVersion": 1, "processId": 4444, "processStartTimeUtcTicks": 123456789,
                 "devenvPath": installation().devenv_path, "launchId": "good", "requestedCapabilities": ["debug.read"]}
        cases = ([], None, "bad", dict(valid, launchId="wrong"),
                 dict(valid, processId=4242, requestedCapabilities=[[]]),
                 dict(valid, processId=4242, schemaVersion=2))
        with patch.object(vs_launch, "_launches_root", return_value=root), patch.object(
            vs_trust, "_validate_registration_security", lambda *args, **kwargs: None
        ), patch.object(vs_launch, "_process_state", return_value=(123456789, True)):
            for payload in cases:
                with self.subTest(payload=payload):
                    (root / "4242.json").write_text(json.dumps(payload), encoding="utf-8")
                    (root / "4444.json").write_text(json.dumps(valid), encoding="utf-8")
                    vs_launch._LAUNCHES.clear()
                    vs_launch._LAUNCHES_LOADED = False
                    owned = vs_launch.owned_launches()
                    self.assertEqual(set(owned), {4444})
                    self.assertEqual(owned[4444].launch_id, "good")
                    self.assertTrue((root / "4444.json").is_file())

    def test_unavailable_identity_preserves_receipt_and_retries_loading(self):
        import shutil
        root = pathlib.Path(tempfile.mkdtemp()) / "launches"
        self.addCleanup(shutil.rmtree, root.parent, True)
        saved, loaded = dict(vs_launch._LAUNCHES), vs_launch._LAUNCHES_LOADED
        def restore():
            vs_launch._LAUNCHES.clear()
            vs_launch._LAUNCHES.update(saved)
            vs_launch._LAUNCHES_LOADED = loaded
        self.addCleanup(restore)
        with patch.object(vs_launch, "_launches_root", return_value=root), patch.object(
            vs_trust, "_validate_registration_security", lambda *args, **kwargs: None
        ), patch.object(vs_launch, "_process_state", side_effect=[None, (123456789, True)]):
            vs_launch._persist_launch_record(record_for())
            vs_launch._LAUNCHES.clear()
            vs_launch._LAUNCHES_LOADED = False
            self.assertIsNone(vs_launch.get_owned_launch(4242))
            self.assertTrue((root / "4242.json").is_file())
            self.assertIsNotNone(vs_launch.get_owned_launch(4242))

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

    def test_join_requires_a_matching_request(self):
        record = record_for(pid=4242)
        record.root_suffix = "ExpA"
        record.requested_capabilities = ("debug.read", "server.control")
        # Same request: reusable. Any difference in suffix, solution or access set is not.
        self.assertTrue(vs_launch._launch_matches_request(record, ("server.control", "debug.read"), None, None, None, "ExpA"))
        self.assertFalse(vs_launch._launch_matches_request(record, ("debug.read",), None, None, None, "ExpA"))
        self.assertFalse(vs_launch._launch_matches_request(record, ("debug.read", "server.control"), None, None, None, "ExpB"))
        self.assertFalse(vs_launch._launch_matches_request(record, ("debug.read", "server.control"), None, None, "C:/x.sln", "ExpA"))

if __name__ == "__main__":
    unittest.main()
