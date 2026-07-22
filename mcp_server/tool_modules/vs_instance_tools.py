"""Visual Studio-only discovery and selection tools."""

try:
    from ..tool_runtime import backend, mcp
    from ..tool_context import format_result, handle_ide_error, validate_int_range
except ImportError:
    from tool_runtime import backend, mcp
    from tool_context import format_result, handle_ide_error, validate_int_range


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
