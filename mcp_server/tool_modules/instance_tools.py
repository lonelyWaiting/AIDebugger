from typing import Any, Dict, List, Optional
import asyncio

try:
    from .. import ide_bridge
    from ..mcp_app import mcp
    from ..tool_context import *
except ImportError:
    import ide_bridge
    from mcp_app import mcp
    from tool_context import *
@mcp.tool()
async def list_vs_instances() -> str:
    """
    列出所有可用的Visual Studio实例及其状态。
    显示每个实例的进程ID、解决方案名称、调试状态等信息。
    当有多个VS实例时，使用此工具查看所有实例，然后用select_vs_instance选择要操作的实例。
    """
    try:
        instances = [i for i in await ide_bridge.discover_instances(preferred_ide="vs") if i.ide == "vs"]

        if not instances:
            return "未找到任何Visual Studio实例。请确保VS已启动且AIDebugger扩展已加载。"

        result = {
            "instanceCount": len(instances),
            "instances": [inst.to_dict() for inst in instances],
            "hint": "使用 select_vs_instance 工具选择要操作的实例（通过 process_id 参数）",
            "selected": False
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
            result["wouldAutoSelect"] = f"自动选择了唯一处于断点暂停状态的实例 (PID: {paused[0].process_id})"
        elif len(selectable_debugging) == 1 and len(selectable_paused) == 0:
            result["wouldAutoSelect"] = f"自动选择了唯一正在调试的实例 (PID: {debugging[0].process_id})"
        elif len(selectable_instances) == 1:
            result["wouldAutoSelect"] = f"自动选择了唯一的实例 (PID: {instances[0].process_id})"

        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "list_vs_instances")

@mcp.tool()
async def list_ide_instances() -> str:
    """
    列出所有可用的IDE实例（Visual Studio + Rider）及其状态。
    """
    try:
        instances = await ide_bridge.discover_instances()

        if not instances:
            return "未找到任何IDE实例。请确保Visual Studio或Rider已启动且插件已加载。"

        result = {
            "instanceCount": len(instances),
            "instances": [inst.to_dict() for inst in instances],
            "hint": "使用 select_ide_instance 选择要操作的实例（通过 ide + process_id 参数）"
        }

        result["selected"] = False

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
                f"自动选择了唯一处于断点暂停状态的实例 ({paused[0].ide.upper()} PID: {paused[0].process_id})"
            )
        elif len(debugging) == 1 and len(paused) == 0:
            result["wouldAutoSelect"] = (
                f"自动选择了唯一正在调试的实例 ({debugging[0].ide.upper()} PID: {debugging[0].process_id})"
            )
        elif len(instances) == 1:
            result["wouldAutoSelect"] = (
                f"自动选择了唯一的实例 ({instances[0].ide.upper()} PID: {instances[0].process_id})"
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
        instances = await ide_bridge.discover_instances(preferred_ide=prefer_ide)

        if not instances:
            return "未找到任何IDE实例。请确保 Visual Studio 或 Rider 已启动且扩展已加载。"

        bridge = ide_bridge.get_bridge()
        candidates, resolved_preferred = bridge.get_candidate_instances(instances, preferred_ide=prefer_ide)
        selected = bridge.auto_select_instance(candidates, preferred_ide=resolved_preferred)

        result = {
            "instanceCount": len(candidates),
            "instances": [inst.to_dict() for inst in candidates],
            "preferredIde": resolved_preferred or bridge.get_preferred_ide(),
        }

        if selected is not None:
            if select:
                ide_bridge.select_instance(ide=selected.ide, pipe_name=selected.pipe_name)
            result["autoSelected"] = selected.to_dict()
            result["selected"] = select
        else:
            result["autoSelected"] = None
            result["selected"] = False
            result["message"] = "发现多个实例且无法自动选择，请使用 list_ide_instances + select_ide_instance 进行选择。"

        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "detect_debug_sessions")

@mcp.tool()
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
            return "ide 参数必须是 'vs' 或 'rider'"

        instances = [i for i in await ide_bridge.discover_instances(preferred_ide=ide_key) if i.ide == ide_key]

        if not instances:
            return f"未找到 {ide} 实例。"

        target = next((i for i in instances if i.process_id == process_id), None)
        if target is None:
            available = ", ".join([str(i.process_id) for i in instances if i.process_id != 0])
            return f"未找到进程ID为 {process_id} 的 {ide} 实例。可用实例: {available if available else '无'}"

        ide_bridge.select_instance(ide=ide_key, pipe_name=target.pipe_name)

        result = {
            "success": True,
            "message": f"已选择 {ide} 实例",
            "selectedInstance": target.to_dict()
        }

        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "select_ide_instance")

@mcp.tool()
async def clear_ide_selection() -> str:
    """
    清除已选择的IDE实例，恢复自动选择模式。
    """
    ide_bridge.clear_selection()
    return "已清除IDE实例选择，恢复自动选择模式。"

@mcp.tool()
async def select_vs_instance(process_id: int) -> str:
    """
    选择要操作的Visual Studio实例。选择后，所有后续的调试工具调用都会使用这个实例。

    Args:
        process_id: Visual Studio进程ID，可通过list_vs_instances获取
    """
    try:
        # Discover instances
        instances = [i for i in await ide_bridge.discover_instances(preferred_ide="vs") if i.ide == "vs"]

        # Build pipe name from process_id
        target_pipe = f"AIDebuggerVS_{process_id}"

        # Find the instance - match by process_id OR by pipe name containing the PID
        target = None
        for inst in instances:
            # Match by process_id if it's set correctly
            if inst.process_id == process_id:
                target = inst
                break
            # Also match by pipe name (handles case where get_instance_info failed)
            if inst.pipe_name == target_pipe:
                target = inst
                # Update the process_id from pipe name
                inst.process_id = process_id
                break

        if target is None:
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
            return f"未找到进程ID为 {process_id} 的VS实例。可用的实例: {available if available else '无'}"

        # Select using pipe name directly (more reliable)
        ide_bridge.select_instance(ide="vs", pipe_name=target_pipe)

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
    return "已清除VS实例选择，恢复自动选择模式。"
