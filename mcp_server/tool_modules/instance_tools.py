from typing import Any, Dict, List, Optional
import asyncio
import os

try:
    from ..tool_runtime import backend as ide_bridge, mcp
    from ..tool_context import *
except ImportError:
    from tool_runtime import backend as ide_bridge, mcp
    from tool_context import *


def _combined_only_tool():
    preferred = (os.environ.get("AIDB_PREFERRED_IDE") or "").strip().lower()
    if preferred == "vs":
        return lambda function: function
    return mcp.tool()


async def _instances_with_selection(instances, ide_scope: Optional[str] = None):
    bridge = ide_bridge.get_bridge()
    selected_ide, selected_pipe, selected_target, _ = await bridge.get_selection_snapshot()
    selection_in_scope = bool(
        selected_ide and selected_pipe and (ide_scope is None or selected_ide == ide_scope)
    )

    def matches_selection(instance) -> bool:
        if not selection_in_scope or instance.ide != selected_ide or instance.pipe_name != selected_pipe:
            return False
        if selected_ide != "vs" or selected_target is None:
            return True
        return all(
            instance.to_dict().get(key) == selected_target.get(key)
            for key in ("processId", "processStartTimeUtcTicks", "serverInstanceId")
        )

    selected_instance = next((
        instance for instance in instances
        if matches_selection(instance)
    ), None)
    serialized = []
    for instance in instances:
        item = instance.to_dict()
        item["isSelected"] = matches_selection(instance)
        serialized.append(item)
    if selected_instance is not None:
        selected_payload = selected_instance.to_dict()
        selected_payload["isSelected"] = True
    elif selection_in_scope:
        selected_payload = {
            "ide": selected_ide,
            "pipeName": selected_pipe,
            "isSelected": True,
            "connectionStatus": "not_found_in_current_discovery",
        }
        if selected_target is not None:
            selected_payload.update(selected_target)
    else:
        selected_payload = None
    return serialized, selection_in_scope, selected_payload, selected_instance


@mcp.tool()
async def list_vs_instances() -> str:
    """
    列出所有可用的Visual Studio实例及其状态。
    显示每个实例的进程ID、解决方案名称、调试状态等信息。
    当有多个VS实例时，使用此工具查看所有实例，然后用select_vs_instance选择要操作的实例。
    """
    try:
        instances = [i for i in await ide_bridge.discover_instances(preferred_ide="vs") if i.ide == "vs"]
        serialized, has_selection, selected_payload, _ = await _instances_with_selection(instances, "vs")

        if not instances:
            return format_result({
                "instanceCount": 0,
                "instances": serialized,
                "selected": has_selection,
                "selectedInstance": selected_payload,
                "hint": "未找到任何Visual Studio实例。请确保VS已启动且AIDebugger扩展已加载。",
                "summary": {"paused": 0, "debugging": 0, "idle": 0, "unreachable": 0},
            })

        result = {
            "instanceCount": len(instances),
            "instances": serialized,
            "hint": "使用 select_vs_instance 工具选择要操作的实例（通过 process_id 参数）",
            "selected": has_selection,
            "selectedInstance": selected_payload,
        }

        # Add summary
        paused = [i for i in instances if i.is_paused]
        debugging = [i for i in instances if i.is_debugging and not i.is_paused]
        idle = [i for i in instances if not i.is_debugging]
        selectable_instances = [i for i in instances if i.connection_status == "ok"]
        selectable_paused = [i for i in selectable_instances if i.is_paused]
        selectable_debugging = [i for i in selectable_instances if i.is_debugging and not i.is_paused]

        result["summary"] = {
            "paused": len(paused),
            "debugging": len(debugging),
            "idle": len(idle),
            "unreachable": len(instances) - len(selectable_instances)
        }

        paused = selectable_paused
        debugging = selectable_debugging
        instances = selectable_instances

        if len(selectable_paused) == 1:
            result["wouldAutoSelect"] = f"未固定目标时将选择唯一处于断点暂停状态的实例 (PID: {paused[0].process_id})"
        elif len(selectable_debugging) == 1 and len(selectable_paused) == 0:
            result["wouldAutoSelect"] = f"未固定目标时将选择唯一正在调试的实例 (PID: {debugging[0].process_id})"
        elif len(selectable_instances) == 1:
            result["wouldAutoSelect"] = f"未固定目标时将选择唯一的实例 (PID: {instances[0].process_id})"

        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "list_vs_instances")

@_combined_only_tool()
async def list_ide_instances() -> str:
    """
    列出所有可用的IDE实例（Visual Studio + Rider）及其状态。
    """
    try:
        instances = await ide_bridge.discover_instances()
        serialized, has_selection, selected_payload, _ = await _instances_with_selection(instances)

        if not instances:
            return format_result({
                "instanceCount": 0,
                "instances": serialized,
                "selected": has_selection,
                "selectedInstance": selected_payload,
                "hint": "未找到任何IDE实例。请确保Visual Studio或Rider已启动且插件已加载。",
                "summary": {"paused": 0, "debugging": 0, "idle": 0, "unreachable": 0},
            })

        result = {
            "instanceCount": len(instances),
            "instances": serialized,
            "hint": "使用 select_ide_instance 选择要操作的实例（通过 ide + process_id 参数）"
        }

        result["selected"] = has_selection
        result["selectedInstance"] = selected_payload

        paused = [i for i in instances if i.is_paused]
        debugging = [i for i in instances if i.is_debugging and not i.is_paused]
        idle = [i for i in instances if not i.is_debugging]
        selectable_instances = [i for i in instances if i.connection_status == "ok"]
        selectable_paused = [i for i in selectable_instances if i.is_paused]
        selectable_debugging = [i for i in selectable_instances if i.is_debugging and not i.is_paused]

        result["summary"] = {
            "paused": len(paused),
            "debugging": len(debugging),
            "idle": len(idle),
            "unreachable": len(instances) - len(selectable_instances)
        }

        paused = selectable_paused
        debugging = selectable_debugging
        instances = selectable_instances

        if len(paused) == 1:
            result["wouldAutoSelect"] = (
                f"未固定目标时将选择唯一处于断点暂停状态的实例 ({paused[0].ide.upper()} PID: {paused[0].process_id})"
            )
        elif len(debugging) == 1 and len(paused) == 0:
            result["wouldAutoSelect"] = (
                f"未固定目标时将选择唯一正在调试的实例 ({debugging[0].ide.upper()} PID: {debugging[0].process_id})"
            )
        elif len(instances) == 1:
            result["wouldAutoSelect"] = (
                f"未固定目标时将选择唯一的实例 ({instances[0].ide.upper()} PID: {instances[0].process_id})"
            )

        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "list_ide_instances")

@mcp.tool()
async def detect_debug_sessions(prefer_ide: Optional[str] = None, select: bool = True) -> str:
    """
    检测是否存在 VS / Rider 调试现场，并根据规则自动选择最合适的实例。

    调用示例:
    ```
    detect_debug_sessions()                          # 自动检测并选择
    detect_debug_sessions(prefer_ide="vs")           # 优先选择 VS
    detect_debug_sessions(prefer_ide="rider", select=False)  # 只检测不选择
    ```

    Args:
        prefer_ide: 可选，优先使用的 IDE（"vs" 或 "rider"）
        select: 是否自动选择匹配的实例（默认 True）
    """
    try:
        locked_ide = ide_bridge.get_bridge().get_preferred_ide()
        if locked_ide == "vs":
            if prefer_ide is not None and str(prefer_ide).strip().lower() not in (
                "vs", "visualstudio", "visual-studio", "visual_studio"
            ):
                raise ide_bridge.IDEBridgeError(
                    "This MCP server is locked to Visual Studio.",
                    code="INVALID_ARGUMENT",
                )
            prefer_ide = "vs"
        instances = await ide_bridge.discover_instances(preferred_ide=prefer_ide)
        serialized, has_selection, selected_payload, pinned_instance = await _instances_with_selection(instances)

        if not instances:
            return format_result({
                "instanceCount": 0,
                "instances": serialized,
                "selected": has_selection,
                "selectedInstance": selected_payload,
                "preferredIde": prefer_ide,
                "autoSelected": None,
                "message": (
                    "当前固定目标未出现在本次发现结果中；请恢复目标或先 clear selection 再重新选择。"
                    if has_selection else
                    "未找到任何IDE实例。请确保 Visual Studio 或 Rider 已启动且扩展已加载。"
                ),
            })

        bridge = ide_bridge.get_bridge()
        candidates, resolved_preferred = bridge.get_candidate_instances(instances, preferred_ide=prefer_ide)
        serialized, has_selection, selected_payload, pinned_instance = await _instances_with_selection(candidates)

        result = {
            "instanceCount": len(candidates),
            "instances": serialized,
            "preferredIde": resolved_preferred or bridge.get_preferred_ide(),
            "selectedInstance": selected_payload,
        }

        if has_selection:
            result["autoSelected"] = None
            result["selected"] = True
            result["message"] = (
                "保留当前固定目标；detect_debug_sessions 不会覆盖显式选择。"
                if pinned_instance is not None and pinned_instance.connection_status == "ok" else
                "当前固定目标不可达或不在请求的 IDE 范围内；未自动切换到其他实例。请 clear selection 后重新选择。"
            )
            return format_result(result)

        selected = bridge.auto_select_instance(candidates, preferred_ide=resolved_preferred)

        if selected is not None:
            if select:
                ide_bridge.select_instance(
                    ide=selected.ide,
                    pipe_name=selected.pipe_name,
                    target_identity=selected.target_identity(),
                    registration_snapshot=selected.registration_snapshot(),
                )
                selected_payload = selected.to_dict()
                selected_payload["isSelected"] = True
                result["autoSelected"] = selected_payload
                result["selectedInstance"] = selected_payload
                result["selected"] = True
                for item in result["instances"]:
                    item["isSelected"] = item.get("ide") == selected.ide and item.get("pipeName") == selected.pipe_name
            else:
                result["autoSelected"] = None
                result["wouldAutoSelectInstance"] = selected.to_dict()
                result["selected"] = False
        else:
            result["autoSelected"] = None
            result["selected"] = False
            result["message"] = (
                "发现多个实例且无法自动选择，请使用 list_vs_instances + select_vs_instance 进行选择。"
                if locked_ide == "vs"
                else "发现多个实例且无法自动选择，请使用 list_ide_instances + select_ide_instance 进行选择。"
            )

        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "detect_debug_sessions")

@_combined_only_tool()
async def select_ide_instance(ide: str, process_id: int) -> str:
    """
    选择要操作的IDE实例。选择后，所有调试工具调用都会使用这个实例。

    调用示例:
    ```
    select_ide_instance(ide="vs", process_id=12345)
    select_ide_instance(ide="rider", process_id=67890)
    ```

    Args:
        ide: IDE类型，必须是 "vs" 或 "rider"
        process_id: 进程ID，可通过 list_ide_instances() 获取
    """
    try:
        ide_key = (ide or "").lower()
        if ide_key not in ("vs", "rider"):
            raise ide_bridge.IDEBridgeError(
                "ide 参数必须是 'vs' 或 'rider'",
                code="INVALID_ARGUMENT",
            )
        process_id = validate_int_range("process_id", process_id, 1, 0x7FFFFFFF)

        instances = [i for i in await ide_bridge.discover_instances(preferred_ide=ide_key) if i.ide == ide_key]

        if not instances:
            raise ide_bridge.IDEBridgeError(f"未找到 {ide} 实例。", code="TARGET_NOT_FOUND")

        target = next((
            i for i in instances
            if i.process_id == process_id and i.connection_status == "ok"
        ), None)
        if target is None:
            unreachable = next((i for i in instances if i.process_id == process_id), None)
            if unreachable is not None:
                raise ide_bridge.IDEBridgeError(
                    f"{ide} 实例 {process_id} 未通过认证握手，不能选择。",
                    code="TARGET_UNREACHABLE",
                )
            available = ", ".join([str(i.process_id) for i in instances if i.process_id != 0])
            raise ide_bridge.IDEBridgeError(
                f"未找到进程ID为 {process_id} 的 {ide} 实例。可用实例: {available if available else '无'}",
                code="TARGET_NOT_FOUND",
            )

        ide_bridge.select_instance(
            ide=ide_key,
            pipe_name=target.pipe_name,
            target_identity=target.target_identity(),
            registration_snapshot=target.registration_snapshot(),
        )

        result = {
            "success": True,
            "message": f"已选择 {ide} 实例",
            "selectedInstance": target.to_dict()
        }

        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "select_ide_instance")

@_combined_only_tool()
async def clear_ide_selection() -> str:
    """
    清除已选择的IDE实例，恢复自动选择模式。
    """
    ide_bridge.clear_selection()
    return format_result({
        "success": True,
        "selected": False,
        "message": "已清除IDE实例选择，恢复自动选择模式。",
    })

@mcp.tool()
async def select_vs_instance(process_id: int) -> str:
    """
    选择要操作的Visual Studio实例。选择后，所有后续的调试工具调用都会使用这个实例。

    Args:
        process_id: Visual Studio进程ID，可通过list_vs_instances获取
    """
    try:
        process_id = validate_int_range("process_id", process_id, 1, 0x7FFFFFFF)
        # Discover instances
        instances = [i for i in await ide_bridge.discover_instances(preferred_ide="vs") if i.ide == "vs"]

        # Build pipe name from process_id
        target_pipe = f"AIDebuggerVS_{process_id}"

        # Find the instance - match by process_id OR by pipe name containing the PID
        target = None
        for inst in instances:
            # Match by process_id if it's set correctly
            if inst.process_id == process_id and inst.connection_status == "ok":
                target = inst
                break

        if target is None:
            unreachable = next((
                inst for inst in instances
                if inst.process_id == process_id or inst.pipe_name == target_pipe
            ), None)
            if unreachable is not None:
                raise ide_bridge.IDEBridgeError(
                    f"VS实例 {process_id} 未通过认证握手，不能选择。",
                    code="TARGET_UNREACHABLE",
                )
            # Show available PIDs extracted from pipe names
            available_pids = []
            for inst in instances:
                if inst.process_id != 0:
                    available_pids.append(str(inst.process_id))
                elif inst.pipe_name.startswith("AIDebuggerVS_"):
                    try:
                        pid = int(inst.pipe_name.split("_")[1])
                        available_pids.append(str(pid))
                    except (ValueError, IndexError):
                        pass
            available = ", ".join(available_pids)
            raise ide_bridge.IDEBridgeError(
                f"未找到进程ID为 {process_id} 的VS实例。可用的实例: {available if available else '无'}",
                code="TARGET_NOT_FOUND",
            )

        # Select using pipe name directly (more reliable)
        ide_bridge.select_instance(
            ide="vs",
            pipe_name=target.pipe_name,
            target_identity=target.target_identity(),
            registration_snapshot=target.registration_snapshot(),
        )

        result = {
            "success": True,
            "message": f"已选择VS实例",
            "selectedInstance": target.to_dict()
        }

        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "select_vs_instance")

@mcp.tool()
async def clear_vs_selection() -> str:
    """
    清除已选择的VS实例，恢复自动选择模式。
    自动选择逻辑：
    1. 如果只有一个实例处于断点暂停状态，自动使用它
    2. 如果只有一个实例正在调试，自动使用它
    3. 如果只有一个实例，自动使用它
    4. 否则需要手动选择
    """
    ide_bridge.clear_selection()
    return format_result({
        "success": True,
        "selected": False,
        "message": "已清除VS实例选择，恢复自动选择模式。",
    })
