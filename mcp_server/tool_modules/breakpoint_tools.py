from typing import Any, Dict, List, Optional
import asyncio

try:
    from ..tool_runtime import backend as ide_bridge, mcp
    from ..tool_context import *
except ImportError:
    from tool_runtime import backend as ide_bridge, mcp
    from tool_context import *
@mcp.tool()
async def get_breakpoints(
    start_index: int = 0,
    max_nodes: Optional[int] = None,
    budget_ms: Optional[int] = None,
    timeout_seconds: Optional[float] = None,
) -> str:
    """
    获取所有已设置的断点列表，包括文件断点、函数断点和条件断点。
    返回断点位置、条件、命中次数等详细信息。

    **触发场景**: 当用户询问"断点在哪"、"有哪些断点"、"breakpoint列表"时调用此工具。
    """
    try:
        params = add_bounded_collection_params({}, start_index, max_nodes, budget_ms)
        result = await ide_bridge.call(
            "get_breakpoints", params,
            timeout=tool_timeout_for_budget(timeout_seconds, THREADS_TIMEOUT_SECONDS, budget_ms),
        )
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "get_breakpoints")

@mcp.tool()
async def get_breakpoint_bindings(
    start_index: int = 0,
    max_nodes: Optional[int] = None,
    budget_ms: Optional[int] = None,
    timeout_seconds: Optional[float] = None,
) -> str:
    """
    Return breakpoints with binding status and bound-child counts.

    This is a no-source attribution helper for function/address breakpoints:
    bound, pending, unbound, and multi-bind states are returned without reading
    the Visual Studio Breakpoints UI.
    """
    try:
        params = add_bounded_collection_params({}, start_index, max_nodes, budget_ms)
        result = await ide_bridge.call(
            "get_breakpoint_bindings", params,
            preferred_ide="vs",
            timeout=tool_timeout_for_budget(timeout_seconds, THREADS_TIMEOUT_SECONDS, budget_ms),
        )
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "get_breakpoint_bindings")

@mcp.tool()
async def continue_and_capture_breakpoint(
    timeout_seconds: int = 120,
    poll_interval_ms: int = 200,
    disassembly_count: int = 32,
    stack_bytes: int = 256,
    include_native: bool = True,
    include_modules: bool = True,
    include_threads: bool = True,
    include_breakpoints: bool = True,
    thread_include_frames: bool = True,
    thread_max_frames_per_thread: Optional[int] = None,
    thread_max_nodes: Optional[int] = None,
    thread_budget_ms: Optional[int] = None,
) -> str:
    """
    Continue execution until the next debug stop and capture breakpoint context.

    Use after setting file/function/address/data breakpoints in no-source tasks.
    The result includes stop reason, current status, breakpoint bindings, and an
    optional hybrid native+managed snapshot at the stop.
    """
    timeout_seconds = validate_int_range("timeout_seconds", timeout_seconds, 1, 1800)
    poll_interval_ms = validate_int_range("poll_interval_ms", poll_interval_ms, 50, 1000)
    try:
        params = add_thread_capture_params({
            "timeoutMs": timeout_seconds * 1000,
            "pollIntervalMs": poll_interval_ms,
            "disassemblyCount": validate_int_range("disassembly_count", disassembly_count, 1, 256),
            "stackBytes": validate_int_range("stack_bytes", stack_bytes, 16, 4096),
            "includeNative": include_native,
            "includeModules": include_modules,
            "includeThreads": include_threads,
            "includeBreakpoints": include_breakpoints,
        }, thread_include_frames, thread_max_frames_per_thread, thread_max_nodes, thread_budget_ms)
        result = await ide_bridge.call(
            "continue_and_capture_breakpoint",
            params,
            timeout=float(timeout_seconds + tool_timeout_for_budget(None, HYBRID_CONTEXT_TIMEOUT_SECONDS, thread_budget_ms)),
            preferred_ide="vs",
        )
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "continue_and_capture_breakpoint")

@mcp.tool()
async def set_breakpoint(
    file_path: str,
    line_number: int,
    condition: str = None,
    hit_count: int = 0,
    hit_count_type: Optional[str] = None,
    condition_type: Optional[str] = None
) -> str:
    """
    在指定文件的指定行设置断点。

    **触发场景**: 当用户说"设置断点"、"加个断点"、"在xxx行打断点"时调用此工具。

    Args:
        file_path: 源文件的完整路径
        line_number: 要设置断点的行号
        condition: 可选的条件表达式，当条件为true时才触发断点
        hit_count: 可选命中次数，0 表示不限制
        hit_count_type: equal / greater_or_equal / multiple
        condition_type: when_true / when_changed
    """
    try:
        params = _validated_breakpoint_options({
            "filePath": file_path,
            "lineNumber": line_number,
        }, hit_count, hit_count_type, condition_type)
        if condition:
            params["condition"] = condition
        result = await ide_bridge.call("set_breakpoint", params)
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "set_breakpoint")

@mcp.tool()
async def set_function_breakpoint(
    function_name: str,
    condition: str = None,
    hit_count: int = 0,
    hit_count_type: Optional[str] = None,
    condition_type: Optional[str] = None
) -> str:
    """
    在指定函数入口设置断点。适用于不知道确切文件位置的情况。

    Args:
        function_name: 函数名（可包含类名，如 MyClass::MyMethod 或 MyNamespace.MyClass.MyMethod）
        condition: 可选的条件表达式
    """
    try:
        params = _validated_breakpoint_options({
            "functionName": function_name,
        }, hit_count, hit_count_type, condition_type)
        if condition:
            params["condition"] = condition
        result = await ide_bridge.call("set_function_breakpoint", params)
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "set_function_breakpoint")

@mcp.tool()
async def set_data_breakpoint(
    data_expression: str,
    byte_count: int = 1,
    condition: Optional[str] = None,
    hit_count: int = 0,
    hit_count_type: Optional[str] = None,
    condition_type: Optional[str] = None
) -> str:
    """
    Set a Visual Studio native data breakpoint/watchpoint for an expression or
    address expression. Example: set_data_breakpoint(data_expression="*(int*)0x12345678", byte_count=4)
    """
    try:
        params = _validated_breakpoint_options({
            "dataExpression": data_expression,
            "byteCount": validate_int_range("byte_count", byte_count, 1, 64),
        }, hit_count, hit_count_type, condition_type)
        if condition:
            params["condition"] = condition
        result = await ide_bridge.call("set_data_breakpoint", params, preferred_ide="vs")
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "set_data_breakpoint")

@mcp.tool()
async def set_address_breakpoint(
    address: str,
    condition: Optional[str] = None,
    hit_count: int = 0,
    hit_count_type: Optional[str] = None,
    condition_type: Optional[str] = None
) -> str:
    """
    Set a Visual Studio native code breakpoint at an absolute address or module+RVA.

    Examples: set_address_breakpoint(address="0x7ff700001234"),
    set_address_breakpoint(address="devenv.exe+0x1234").
    """
    try:
        params = _validated_breakpoint_options({
            "address": validate_address_breakpoint_target("address", address),
        }, hit_count, hit_count_type, condition_type)
        if condition:
            params["condition"] = condition
        result = await ide_bridge.call("set_address_breakpoint", params, preferred_ide="vs")
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "set_address_breakpoint")


def _validated_breakpoint_options(
    params: Dict[str, Any],
    hit_count: int,
    hit_count_type: Optional[str],
    condition_type: Optional[str],
) -> Dict[str, Any]:
    params["hitCount"] = validate_int_range("hit_count", hit_count, 0, 0x7FFFFFFF)
    if hit_count_type is not None:
        params["hitCountType"] = validate_enum(
            "hit_count_type", hit_count_type, ("equal", "greater_or_equal", "multiple")
        )
    if condition_type is not None:
        params["conditionType"] = validate_enum(
            "condition_type", condition_type, ("when_true", "when_changed")
        )
    return params

@mcp.tool(name="set_watchpoint")
async def set_watchpoint_alias(
    data_expression: str,
    byte_count: int = 1,
    condition: Optional[str] = None,
    hit_count: int = 0,
    hit_count_type: Optional[str] = None,
    condition_type: Optional[str] = None
) -> str:
    """Alias for set_data_breakpoint."""
    return await set_data_breakpoint(
        data_expression=data_expression,
        byte_count=byte_count,
        condition=condition,
        hit_count=hit_count,
        hit_count_type=hit_count_type,
        condition_type=condition_type,
    )

@mcp.tool()
async def delete_breakpoint(
    file_path: str,
    line_number: int,
    breakpoint_lookup_budget_ms: Optional[int] = None,
    timeout_seconds: Optional[float] = None,
) -> str:
    """
    删除指定位置的断点。

    Args:
        file_path: 源文件的完整路径
        line_number: 断点所在行号
    """
    try:
        params = {
            "filePath": file_path,
            "lineNumber": line_number
        }
        if breakpoint_lookup_budget_ms is not None:
            params["breakpointLookupBudgetMs"] = validate_int_range(
                "breakpoint_lookup_budget_ms", breakpoint_lookup_budget_ms, 0, 120000
            )
        result = await ide_bridge.call(
            "delete_breakpoint", params,
            timeout=tool_timeout_for_budget(
                timeout_seconds, THREADS_TIMEOUT_SECONDS, breakpoint_lookup_budget_ms
            ),
        )
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "delete_breakpoint")

@mcp.tool()
async def clear_breakpoint(file_path: str, line_number: int) -> str:
    """
    Alias for delete_breakpoint.
    """
    return await delete_breakpoint(file_path, line_number)

@mcp.tool()
async def delete_all_breakpoints() -> str:
    """
    删除所有断点。谨慎使用。
    """
    try:
        result = await ide_bridge.call("delete_all_breakpoints")
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "delete_all_breakpoints")

@mcp.tool()
async def enable_breakpoint(
    file_path: str,
    line_number: int,
    enabled: bool = True,
    breakpoint_lookup_budget_ms: Optional[int] = None,
    timeout_seconds: Optional[float] = None,
) -> str:
    """
    启用或禁用指定位置的断点。禁用的断点保留但不触发。

    Args:
        file_path: 源文件的完整路径
        line_number: 断点所在行号
        enabled: True启用，False禁用
    """
    try:
        params = {
            "filePath": file_path,
            "lineNumber": line_number,
            "enabled": enabled
        }
        if breakpoint_lookup_budget_ms is not None:
            params["breakpointLookupBudgetMs"] = validate_int_range(
                "breakpoint_lookup_budget_ms", breakpoint_lookup_budget_ms, 0, 120000
            )
        result = await ide_bridge.call(
            "enable_breakpoint", params,
            timeout=tool_timeout_for_budget(
                timeout_seconds, THREADS_TIMEOUT_SECONDS, breakpoint_lookup_budget_ms
            ),
        )
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "enable_breakpoint")

@mcp.tool()
async def switch_stack_frame(frame_index: int) -> str:
    """
    切换到调用堆栈中的指定帧，以查看该层级的局部变量和上下文。
    使用 get_callstack 获取帧索引。

    Args:
        frame_index: 栈帧索引（0为当前帧，越大越靠近调用起点）
    """
    try:
        result = await ide_bridge.call("switch_stack_frame", {
            "frameIndex": frame_index
        })
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "switch_stack_frame")

@mcp.tool()
async def select_stack_frame(frame_index: int) -> str:
    """
    Alias for switch_stack_frame.
    """
    return await switch_stack_frame(frame_index)

@mcp.tool()
async def switch_thread(
    thread_id: int,
    thread_lookup_budget_ms: Optional[int] = None,
    timeout_seconds: Optional[float] = None,
) -> str:
    """
    切换到指定线程以查看其调用堆栈和变量。
    使用 get_threads 获取线程ID。

    Args:
        thread_id: 目标线程的ID
    """
    try:
        params = {"threadId": thread_id}
        if thread_lookup_budget_ms is not None:
            params["threadLookupBudgetMs"] = validate_int_range(
                "thread_lookup_budget_ms", thread_lookup_budget_ms, 0, 120000
            )
        result = await ide_bridge.call(
            "switch_thread",
            params,
            timeout=tool_timeout_for_budget(
                timeout_seconds, THREADS_TIMEOUT_SECONDS, thread_lookup_budget_ms
            ),
        )
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "switch_thread")

@mcp.tool()
async def select_thread(thread_id: int) -> str:
    """
    Alias for switch_thread.
    """
    return await switch_thread(thread_id)
