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
async def attach_process(
    process_id: Optional[int] = None,
    process_name: Optional[str] = None
) -> str:
    """
    Attach Visual Studio to a local process by PID or executable name.

    Examples:
    attach_process(process_name="UnrealEditor.exe")
    attach_process(process_id=12345)
    """
    try:
        result = await ide_bridge.call("attach_process", {
            "processId": process_id or 0,
            "processName": process_name
        }, preferred_ide="vs")
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "attach_process")

@mcp.tool()
async def start_debugging(
    executable_path: Optional[str] = None,
    working_dir: Optional[str] = None,
    args: Optional[str] = None,
    attach: bool = True,
    timeout_seconds: Optional[float] = None
) -> str:
    """
    Start debugging. With no executable_path, uses the current Visual Studio
    startup/debug configuration. With executable_path, launches that process and
    attaches the VS debugger by default.
    """
    try:
        wait_timeout = clamp_tool_timeout(timeout_seconds, START_DEBUGGING_TIMEOUT_SECONDS)
        deadline = asyncio.get_running_loop().time() + wait_timeout
        result = await ide_bridge.call("start_debugging", {
            "executablePath": executable_path,
            "workingDirectory": working_dir,
            "arguments": args,
            "attach": attach
        }, timeout=wait_timeout, preferred_ide="vs")
        remaining_timeout = max(1.0, deadline - asyncio.get_running_loop().time())
        result = await wait_for_debugging_to_start(result, remaining_timeout, attach)
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "start_debugging")

@mcp.tool()
async def debug_control(action: str) -> str:
    """
    Unified debugger execution-control entry point.

    Use this when searching for stop debugging, stop debug, pause, continue,
    step over, step into, step out, or detach current Visual Studio debugger
    capability. Supported actions: continue, pause, break, stop, stop_debugging,
    step_over, step_into, step_out, detach, detach_process.
    """
    normalized = (action or "").strip().lower().replace("-", "_").replace(" ", "_")
    aliases = {
        "go": "continue",
        "run": "continue",
        "resume": "continue",
        "debug_continue": "continue",
        "pause": "break",
        "debug_break": "break",
        "debug_pause": "break",
        "stop": "stop",
        "stop_debug": "stop",
        "stop_debugging": "stop",
        "debug_stop": "stop",
        "terminate": "stop",
        "terminate_debug_session": "stop",
        "stepover": "step_over",
        "debug_step_over": "step_over",
        "stepinto": "step_into",
        "debug_step_into": "step_into",
        "stepout": "step_out",
        "debug_step_out": "step_out",
        "detach": "detach",
        "detach_process": "detach",
        "detach_debugger": "detach",
        "debug_detach": "detach",
    }
    normalized = aliases.get(normalized, normalized)

    if normalized == "continue":
        return await debug_continue()
    if normalized == "break":
        return await debug_break()
    if normalized == "stop":
        return await debug_stop()
    if normalized == "step_over":
        return await debug_step_over()
    if normalized == "step_into":
        return await debug_step_into()
    if normalized == "step_out":
        return await debug_step_out()
    if normalized == "detach":
        return await detach_process()

    return format_result({
        "action": action,
        "success": False,
        "message": (
            "Unsupported debug_control action. Use one of: continue, pause, "
            "break, stop, step_over, step_into, step_out, detach."
        ),
    })

@mcp.tool()
async def debug_continue() -> str:
    """
    继续执行程序直到下一个断点或程序结束 (F5)。
    返回执行后的新状态。

    **触发场景**: 当用户说"继续"、"continue"、"运行"、"F5"、"执行下去"时调用此工具。
    """
    try:
        result = await ide_bridge.call("continue")
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "continue")

@mcp.tool(name="continue")
async def continue_alias() -> str:
    """
    Alias for debug_continue. Continue execution until the next breakpoint,
    exception, user break, or process exit.
    """
    return await debug_continue()

@mcp.tool()
async def continue_and_wait_for_break(
    timeout_seconds: int = 120,
    poll_interval_ms: int = 200,
    expressions: Optional[List[str]] = None,
    expand_children: bool = False,
    max_depth: int = 2,
    output_tail_lines: int = 80,
    include_locals: bool = False,
    include_modules: bool = False,
    include_registers: bool = False,
    include_output: bool = False,
    include_threads: bool = True,
    include_exception: bool = True,
    allow_side_effects: bool = False,
    thread_include_frames: bool = True,
    thread_max_frames_per_thread: Optional[int] = None,
    thread_max_nodes: Optional[int] = None,
    thread_budget_ms: Optional[int] = None,
) -> str:
    """
    继续运行（或在已经运行时直接等待），直到调试器再次进入暂停状态，然后自动抓取一个 debug bundle。
    适合“先放行让我复现，命中后再自动分析”的工作流。

    Args:
        timeout_seconds: 最长等待秒数，默认120，最大1800
        poll_interval_ms: IDE 侧轮询间隔毫秒，默认200，范围50-1000
        expressions: 命中后在同一快照中批量求值的表达式列表
        expand_children: 是否展开表达式子成员，默认False
        max_depth: 表达式展开深度，默认2
        output_tail_lines: Debug Output 末尾行数，默认80
        include_locals/include_modules/include_registers/include_output: 命中后是否抓取重组件，默认False
        include_threads/include_exception: 命中后是否抓取线程/异常，默认True
        allow_side_effects: 兼容旧客户端的参数；当前不会因函数调用形态预先拒绝表达式
        thread_include_frames/thread_max_frames_per_thread/thread_max_nodes/thread_budget_ms: 命中后线程帧抓取预算
    """
    timeout_seconds = max(1, min(timeout_seconds, 1800))
    poll_interval_ms = max(50, min(poll_interval_ms, 1000))
    normalized_expressions = expressions or []
    try:
        params = add_thread_capture_params({
            "timeoutMs": timeout_seconds * 1000,
            "pollIntervalMs": poll_interval_ms,
            "expressions": normalized_expressions,
            "expandChildren": expand_children,
            "maxDepth": max_depth,
            "outputTailLines": output_tail_lines,
            "includeLocals": include_locals,
            "includeModules": include_modules,
            "includeRegisters": include_registers,
            "includeOutput": include_output,
            "includeThreads": include_threads,
            "includeException": include_exception,
            "allowSideEffects": allow_side_effects,
        }, thread_include_frames, thread_max_frames_per_thread, thread_max_nodes, thread_budget_ms)
        result = await ide_bridge.call(
            "continue_and_wait_for_break",
            params,
            timeout=float(timeout_seconds + tool_timeout_for_budget(None, 15.0, thread_budget_ms)),
        )
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "continue_and_wait_for_break")

@mcp.tool()
async def continue_and_capture_native(
    timeout_seconds: int = 120,
    poll_interval_ms: int = 200,
    disassembly_count: int = 32,
    stack_bytes: int = 256,
    include_modules: bool = True
) -> str:
    """
    Continue execution, poll briefly until the debugger breaks, then capture native context.

    This is the no-source native debugging loop: it returns the stop status,
    normalized current address, registers, nearby disassembly, stack memory,
    callstack, and optionally loaded modules.
    """
    timeout_seconds = max(1, min(timeout_seconds, 1800))
    poll_interval_ms = max(50, min(poll_interval_ms, 1000))
    try:
        result = await ide_bridge.call(
            "continue_and_capture_native",
            {
                "timeoutMs": timeout_seconds * 1000,
                "pollIntervalMs": poll_interval_ms,
                "disassemblyCount": max(1, min(disassembly_count, 256)),
                "stackBytes": max(16, min(stack_bytes, 4096)),
                "includeModules": include_modules,
            },
            timeout=float(timeout_seconds + NATIVE_CONTEXT_TIMEOUT_SECONDS),
            preferred_ide="vs",
        )
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "continue_and_capture_native")

@mcp.tool()
async def debug_step_over() -> str:
    """
    单步跳过当前行 (F10)，不进入函数内部。
    返回执行后的新位置信息。

    **触发场景**: 当用户说"单步"、"下一行"、"step over"、"F10"、"跳过这行"时调用此工具。
    """
    try:
        result = await ide_bridge.call("step_over")
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "step_over")

@mcp.tool(name="step_over")
async def step_over_alias() -> str:
    """Alias for debug_step_over."""
    return await debug_step_over()

@mcp.tool()
async def debug_step_into() -> str:
    """
    单步进入当前行的函数 (F11)。
    返回执行后的新位置信息。

    **触发场景**: 当用户说"进入函数"、"step into"、"F11"、"跟进去"时调用此工具。
    """
    try:
        result = await ide_bridge.call("step_into")
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "step_into")

@mcp.tool(name="step_into")
async def step_into_alias() -> str:
    """Alias for debug_step_into."""
    return await debug_step_into()

@mcp.tool()
async def debug_step_out() -> str:
    """
    单步跳出当前函数 (Shift+F11)，执行到调用方。
    返回执行后的新位置信息。

    **触发场景**: 当用户说"跳出函数"、"step out"、"Shift+F11"、"返回上层"时调用此工具。
    """
    try:
        result = await ide_bridge.call("step_out")
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "step_out")

@mcp.tool(name="step_out")
async def step_out_alias() -> str:
    """Alias for debug_step_out."""
    return await debug_step_out()

@mcp.tool()
async def debug_break() -> str:
    """
    中断正在运行的程序 (Ctrl+Alt+Break)。
    仅在程序运行状态下有效。
    """
    try:
        result = await ide_bridge.call("break")
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "break")

@mcp.tool()
async def pause() -> str:
    """
    Alias for debug_break. Pause/break a running debuggee.
    """
    return await debug_break()

@mcp.tool()
async def debug_stop() -> str:
    """
    Stop debugging / stop debug / terminate debug session (Shift+F5).
    Stops the current Visual Studio or Rider debugging session.
    stop current Visual Studio debugging session.
    terminate current debug session.

    停止调试会话 (Shift+F5)。
    """
    try:
        result = await ide_bridge.call("stop")
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "stop")

@mcp.tool()
async def stop_debugging() -> str:
    """
    Alias for debug_stop. Stop debugging, stop debug, terminate debug session,
    stop current Visual Studio debugging session, or Shift+F5.
    """
    return await debug_stop()

@mcp.tool()
async def detach_process() -> str:
    """
    Detach the Visual Studio debugger from the current debugged process without
    terminating it. Use for detach debugger / detach process / stop debugging
    without killing the process. Rider is not routed as a supported detach backend.
    """
    try:
        result = await ide_bridge.call("detach", preferred_ide="vs")
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "detach")

@mcp.tool()
async def detach_debugger() -> str:
    """
    Alias for detach_process. Detach debugger from the current process.
    """
    return await detach_process()

@mcp.tool()
async def debug_run_to_cursor(file_path: str, line_number: int) -> str:
    """
    运行程序到指定的文件和行号位置。

    Args:
        file_path: 目标文件的完整路径
        line_number: 目标行号
    """
    try:
        result = await ide_bridge.call("run_to_cursor", {
            "filePath": file_path,
            "lineNumber": line_number
        })
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "run_to_cursor")

@mcp.tool()
async def debug_set_next_statement(file_path: str, line_number: int) -> str:
    """
    设置下一条要执行的语句（移动指令指针）。
    警告：这可能导致程序行为异常，谨慎使用。

    Args:
        file_path: 目标文件的完整路径
        line_number: 目标行号
    """
    try:
        result = await ide_bridge.call("set_next_statement", {
            "filePath": file_path,
            "lineNumber": line_number
        })
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "set_next_statement")
