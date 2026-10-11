"""Visual Studio-only discovery and selection tools."""

from typing import Any, Dict, Optional

import asyncio

try:
    from .. import vs_bridge, vs_launch
    from ..tool_runtime import backend, mcp
    from ..tool_context import (
        clamp_tool_timeout,
        format_result,
        handle_ide_error,
        validate_int_range,
        validate_nonempty_string,
        wait_for_debugging_to_start,
    )
except ImportError:
    import vs_bridge  # type: ignore
    import vs_launch  # type: ignore
    from tool_runtime import backend, mcp  # type: ignore
    from tool_context import (  # type: ignore
        clamp_tool_timeout,
        format_result,
        handle_ide_error,
        validate_int_range,
        validate_nonempty_string,
        wait_for_debugging_to_start,
    )


async def _snapshot(instances):
    manager = backend.get_bridge()
    _, selected_pipe, selected_target, generation = await manager.get_selection_snapshot()

    def selected(instance) -> bool:
        if not selected_pipe or instance.pipe_name != selected_pipe:
            return False
        if selected_target is None:
            return True
        payload = instance.to_dict()
        return all(
            payload.get(key) == selected_target.get(key)
            for key in ("processId", "processStartTimeUtcTicks", "serverInstanceId")
        )

    serialized = []
    selected_instance = None
    for instance in instances:
        item = instance.to_dict()
        item["isSelected"] = selected(instance)
        serialized.append(item)
        if item["isSelected"]:
            selected_instance = item
    stale_selected_target = None
    if selected_pipe and selected_instance is None:
        stale_selected_target = {
            "ide": "vs",
            "pipeName": selected_pipe,
            "isSelected": False,
            "connectionStatus": "not_found_in_current_discovery",
            **(selected_target or {}),
        }
    return serialized, selected_instance, stale_selected_target, bool(selected_pipe), generation


def _summary(instances):
    reachable = [item for item in instances if item.connection_status == "ok"]
    return {
        "paused": sum(1 for item in reachable if item.is_paused),
        "debugging": sum(1 for item in reachable if item.is_debugging and not item.is_paused),
        "idle": sum(1 for item in reachable if not item.is_debugging),
        "unreachable": len(instances) - len(reachable),
    }


@mcp.tool()
async def list_vs_instances() -> str:
    """List authenticated Visual Studio instances and their debugger state."""
    try:
        instances = await backend.discover_instances()
        serialized, selected, stale_selected, has_pinned, _ = await _snapshot(instances)
        reachable_count = sum(1 for item in instances if item.connection_status == "ok")
        return format_result({
            "instanceCount": len(instances),
            "reachableCount": reachable_count,
            "unreachableCount": len(instances) - reachable_count,
            "instances": serialized,
            "selected": selected is not None,
            "hasPinnedSelection": has_pinned,
            "selectedReachable": selected is not None,
            "selectedInstance": selected,
            "staleSelectedTarget": stale_selected,
            "selectionStatus": "stale_selection" if stale_selected is not None else None,
            "summary": _summary(instances),
            "hint": "Use select_vs_instance(process_id=...) when more than one instance is reachable.",
        })
    except backend.IDEBridgeError as error:
        return handle_ide_error(error, "list_vs_instances")


@mcp.tool()
async def detect_debug_sessions(select: bool = True) -> str:
    """Discover Visual Studio debug sessions and optionally pin the unique best target."""
    try:
        manager = backend.get_bridge()
        _, _, _, expected_generation = await manager.get_selection_snapshot()
        instances = await backend.discover_instances()
        reachable = [item for item in instances if item.connection_status == "ok"]
        serialized, pinned, stale_pinned, has_pinned, current_generation = await _snapshot(instances)
        result = {
            "instanceCount": len(instances),
            "reachableCount": len(reachable),
            "unreachableCount": len(instances) - len(reachable),
            "instances": serialized,
            "selected": pinned is not None,
            "hasPinnedSelection": has_pinned,
            "selectedReachable": pinned is not None,
            "selectedInstance": pinned,
            "staleSelectedTarget": stale_pinned,
            "autoSelected": None,
            "summary": _summary(instances),
        }
        if current_generation != expected_generation:
            result["message"] = "Selection changed while discovery was running; the newer selection was preserved."
            result["selectionStatus"] = "selection_changed"
            return format_result(result)
        if stale_pinned is not None:
            result["message"] = (
                "The pinned Visual Studio target is stale or unreachable. "
                "Use clear_vs_selection, then explicitly select the intended reachable instance."
            )
            result["selectionStatus"] = "stale_selection"
            return format_result(result)
        if pinned is not None or not instances:
            return format_result(result)
        if not reachable:
            result["message"] = (
                "No reachable authenticated Visual Studio instance is available; "
                "unreachable registration diagnostics are included."
            )
            result["selectionStatus"] = "no_reachable_instance"
            return format_result(result)
        candidate = manager.auto_select_instance(reachable)
        if candidate is None:
            result["message"] = "Multiple Visual Studio instances are reachable; select one explicitly."
            return format_result(result)
        if not select:
            result["wouldAutoSelectInstance"] = candidate.to_dict()
            return format_result(result)
        selected, current_target, _ = await manager.try_select_instance(
            expected_generation,
            pipe_name=candidate.pipe_name,
            target_identity=candidate.target_identity(),
            registration_snapshot=candidate.registration_snapshot(),
        )
        if not selected:
            result["message"] = "Selection changed before auto-selection completed; the newer selection was preserved."
            result["selectionStatus"] = "selection_changed"
            result["selectedInstance"] = current_target
            return format_result(result)
        selected_payload = candidate.to_dict()
        selected_payload["isSelected"] = True
        result["selected"] = True
        result["selectedInstance"] = selected_payload
        result["autoSelected"] = selected_payload
        return format_result(result)
    except backend.IDEBridgeError as error:
        return handle_ide_error(error, "detect_debug_sessions")


@mcp.tool()
async def select_vs_instance(process_id: int) -> str:
    """Pin subsequent requests to one authenticated Visual Studio process."""
    try:
        process_id = validate_int_range("process_id", process_id, 1, 0x7FFFFFFF)
        manager = backend.get_bridge()
        _, _, _, expected_generation = await manager.get_selection_snapshot()
        instances = await backend.discover_instances()
        target = next(
            (item for item in instances if item.process_id == process_id and item.connection_status == "ok"),
            None,
        )
        if target is None:
            raise backend.IDEBridgeError(
                f"No reachable authenticated Visual Studio instance has PID {process_id}.",
                code="TARGET_NOT_FOUND",
            )
        selected, current_target, _ = await manager.try_select_instance(
            expected_generation,
            pipe_name=target.pipe_name,
            target_identity=target.target_identity(),
            registration_snapshot=target.registration_snapshot(),
        )
        if not selected:
            return format_result({
                "success": False,
                "message": "Selection changed while discovery was running; the newer selection was preserved.",
                "errorCode": "SELECTION_CHANGED",
                "selectedInstance": current_target,
            })
        return format_result({
            "success": True,
            "message": f"Selected Visual Studio process {process_id}.",
            "selectedInstance": target.to_dict(),
        })
    except backend.IDEBridgeError as error:
        return handle_ide_error(error, "select_vs_instance")


@mcp.tool()
async def clear_vs_selection() -> str:
    """Clear the pinned Visual Studio target and restore unique-target auto-selection."""
    manager = backend.get_bridge()
    _, _, current_target, expected_generation = await manager.get_selection_snapshot()
    cleared, _ = await manager.try_clear_selection(expected_generation)
    if not cleared:
        return format_result({
            "success": False,
            "message": "Selection changed before clear completed; the newer selection was preserved.",
            "errorCode": "SELECTION_CHANGED",
            "selected": current_target is not None,
            "selectedInstance": current_target,
        })
    return format_result({
        "success": True,
        "message": "Cleared the pinned Visual Studio selection.",
        "selected": False,
    })


def _launch_hint(outcome) -> Optional[str]:
    if outcome.status == vs_launch.AMBIGUOUS_STATUS:
        return (
            "Call select_vs_instance(process_id=...) to pin one of the reachable instances, or call "
            "ensure_vs_instance again with reuse_existing=False to launch a dedicated instance."
        )
    if outcome.status in {vs_launch.READY_TIMEOUT_STATUS, vs_launch.LAUNCH_FAILED_STATUS}:
        return (
            "Check the AIDebugger output pane in Visual Studio and confirm the AIDebuggerVS extension is "
            "installed and enabled for the installation that was launched "
            "(list candidates with list_vs_instances, or pass install_path/instance_id explicitly)."
        )
    return None


def _instance_result(outcome, requested_capabilities) -> Dict[str, Any]:
    instance = outcome.instance
    record = outcome.record
    result: Dict[str, Any] = {
        "success": instance is not None,
        "status": outcome.status,
        "message": outcome.message,
        "launched": record is not None,
        "attached": False if instance is not None else None,
        "partial": True if outcome.status == vs_launch.READY_TIMEOUT_STATUS else None,
        "processId": instance.process_id if instance is not None else (
            record.process_id if record is not None else None
        ),
        "pipeName": instance.pipe_name if instance is not None else None,
        "installPath": record.installation.install_path if record is not None else None,
        "installationVersion": record.installation.installation_version if record is not None else None,
        "launchId": record.launch_id if record is not None else None,
        "requestedCapabilities": list(requested_capabilities),
        "grantedCapabilities": list(instance.allowed_capabilities) if instance is not None else None,
        "accessMode": instance.access_mode if instance is not None else None,
        "instance": instance.to_dict() if instance is not None else None,
        "attachResult": None,
        "diagnostics": list(outcome.diagnostics) or None,
        "hint": _launch_hint(outcome),
        "errorCode": None if instance is not None else (
            "TARGET_NOT_FOUND" if outcome.status == vs_launch.AMBIGUOUS_STATUS else None
        ),
    }
    return result


@mcp.tool()
async def ensure_vs_instance(
    process_id: Optional[int] = None,
    process_name: Optional[str] = None,
    solution_path: Optional[str] = None,
    capabilities: Optional[str] = None,
    install_path: Optional[str] = None,
    instance_id: Optional[str] = None,
    root_suffix: Optional[str] = None,
    reuse_existing: bool = True,
    attach: bool = True,
    wait_for_debugger: bool = True,
    timeout_seconds: Optional[float] = None,
) -> str:
    """
    Make an authenticated Visual Studio instance usable, launching one when none is running.

    Call this whenever another tool reports that no Visual Studio instance is available:
    it reuses a reachable instance, otherwise it starts devenv.exe with a short-lived
    launcher request that lets the extension bring its MCP server up without a menu click,
    waits for the authenticated registration, pins the instance, and then optionally
    attaches the debugger to process_id/process_name.

    capabilities defaults to the full debugger set; narrow it (comma-separated capability
    names) only when a read-only or partial server is intended.
    """
    try:
        total_timeout = clamp_tool_timeout(timeout_seconds, vs_launch.DEFAULT_LAUNCH_TIMEOUT_SECONDS)
        loop = asyncio.get_running_loop()
        deadline = loop.time() + total_timeout
        if process_id is not None and process_name is not None:
            raise vs_bridge.VSBridgeError(
                "Provide at most one of process_id or process_name.",
                code="INVALID_ARGUMENT",
            )
        target_params: Dict[str, Any] = {}
        if process_id is not None:
            target_params["processId"] = validate_int_range("process_id", process_id, 1, 0x7FFFFFFF)
        if process_name is not None:
            target_params["processName"] = validate_nonempty_string("process_name", process_name, 1024)

        requested = vs_launch.parse_requested_capabilities(capabilities)
        manager = backend.get_bridge()
        _, _, _, expected_generation = await manager.get_selection_snapshot()
        outcome = await vs_launch.ensure_instance(
            requested,
            install_path=install_path,
            instance_id=instance_id,
            solution_path=solution_path,
            root_suffix=root_suffix,
            reuse_existing=reuse_existing,
            timeout_seconds=total_timeout,
        )
        result = _instance_result(outcome, requested)
        if outcome.instance is None:
            return format_result(result)

        # Pin the instance so every later tool reaches the same Visual Studio process.
        selected, _, _ = await manager.try_select_instance(
            expected_generation,
            pipe_name=outcome.instance.pipe_name,
            target_identity=outcome.instance.target_identity(),
            registration_snapshot=outcome.instance.registration_snapshot(),
        )
        instance_payload = outcome.instance.to_dict()
        instance_payload["isSelected"] = bool(selected)
        result["instance"] = instance_payload
        if not selected:
            result["message"] += (
                " Another selection replaced this instance while it was being pinned; the newer "
                "selection was preserved."
            )
        if not target_params or not attach:
            return format_result(result)

        target = vs_bridge.instance_target(outcome.instance.target_identity())
        remaining = max(0.0, deadline - loop.time())
        if remaining <= 0:
            result["status"] = "attach_failed"
            result["success"] = False
            result["partial"] = True
            result["errorCode"] = "DEADLINE_EXCEEDED_NOT_STARTED"
            result["message"] += " Attach was not started because the ensure_vs_instance timeout budget was exhausted."
            return format_result(result)
        try:
            attach_result = await vs_bridge.call_pipe_exact(
                target, "attach_process", target_params, timeout=remaining
            )
        except (backend.IDEBridgeError, vs_bridge.VSBridgeError) as error:
            result["status"] = "attach_failed"
            result["success"] = False
            result["errorCode"] = getattr(error, "code", "BRIDGE_ERROR")
            result["message"] = (
                f"Visual Studio PID {outcome.instance.process_id} is ready, but attaching failed: {error}"
            )
            return format_result(result)

        if wait_for_debugger and isinstance(attach_result, dict):
            remaining = max(0.0, deadline - loop.time())
            attach_result = await wait_for_debugging_to_start(
                attach_result, remaining, True, target=target
            )
        result["attachResult"] = attach_result
        activation = (
            attach_result.get("debuggerActivationStatus") if isinstance(attach_result, dict) else None
        )
        result["attached"] = bool(
            activation == "active"
            or (isinstance(attach_result, dict) and attach_result.get("attachCompleted"))
        )
        if activation == "timed_out":
            result["partial"] = True
            result["message"] += (
                " The attach command was issued, but Visual Studio had not entered debugging mode "
                "before the timeout; check get_debug_status before repeating anything."
            )
        return format_result(result)
    except (backend.IDEBridgeError, vs_bridge.VSBridgeError) as error:
        return handle_ide_error(error, "ensure_vs_instance")


@mcp.tool()
async def close_vs_instance(
    process_id: int,
    force: bool = False,
    timeout_seconds: Optional[float] = None,
) -> str:
    """
    Close a Visual Studio instance this MCP server launched.

    Instances started by the user are refused: only PIDs recorded by ensure_vs_instance in
    this MCP server process can be closed. A running debug session is refused unless
    force=True.
    """
    try:
        process_id = validate_int_range("process_id", process_id, 1, 0x7FFFFFFF)
        total_timeout = clamp_tool_timeout(timeout_seconds, 30.0)
        debug_state = None
        record = vs_launch.get_owned_launch(process_id)
        if record is not None:
            # A record restored from disk carries no live instance object, so resolve the
            # current instance for this PID (matching the recorded start time) before the
            # debug-session guard below; otherwise a restarted MCP server could kill a
            # Visual Studio that is mid-debug without force=True.
            instance = record.ready_instance
            if instance is None:
                try:
                    candidates = await vs_bridge.discover_instances()
                except vs_bridge.VSBridgeError:
                    candidates = []
                instance = next(
                    (
                        candidate
                        for candidate in candidates
                        if candidate.process_id == process_id
                        and candidate.connection_status == "ok"
                        and candidate.process_start_time_utc_ticks == record.start_time_utc_ticks
                    ),
                    None,
                )
            if instance is not None:
                instance_target = vs_bridge.instance_target(instance.target_identity())
                try:
                    debug_state = await vs_bridge.call_pipe_exact(
                        instance_target,
                        "get_debug_status",
                        {},
                        timeout=min(10.0, total_timeout),
                    )
                except vs_bridge.VSBridgeError:
                    # An unreachable instance may still own a live debuggee. The
                    # close backend rejects unknown status unless force=True.
                    debug_state = None
                if (
                    force
                    and isinstance(debug_state, dict)
                    and debug_state.get("isDebugging")
                ):
                    # Stop the session explicitly before closing. Otherwise Visual Studio
                    # tears the session down inside its own shutdown and the close blocks
                    # for seconds on that teardown.
                    try:
                        await vs_bridge.call_pipe_exact(
                            instance_target,
                            "stop",
                            {},
                            timeout=min(15.0, max(5.0, total_timeout - 5.0)),
                        )
                    except vs_bridge.VSBridgeError:
                        # The close below forces the process anyway.
                        pass
        result = await vs_launch.close_owned_instance(
            process_id,
            force=force,
            timeout_seconds=total_timeout,
            debug_state=debug_state,
        )
        return format_result(result)
    except (backend.IDEBridgeError, vs_bridge.VSBridgeError) as error:
        return handle_ide_error(error, "close_vs_instance")
