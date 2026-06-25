"""
IDE Debugger MCP Server

This MCP server provides tools for Cursor to access debugging information
from Visual Studio and Rider, including call stacks, variables, memory, and more.

Supports multiple IDE instances with automatic discovery and smart selection:
- If only one instance is in debugging/paused state, it will be auto-selected
- Use list_ide_instances to see all available instances
- Use select_ide_instance to choose a specific instance
"""

import io
import os
import sys
import json
import asyncio
from typing import Any, Dict, List, Optional

# Force UTF-8 for stdio before anything else.
# On Windows the default encoding may be cp1252 which cannot encode CJK characters
# used in tool descriptions, causing UnicodeEncodeError when MCP writes responses.
os.environ.setdefault("PYTHONIOENCODING", "utf-8")
if hasattr(sys.stdout, "buffer"):
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
if hasattr(sys.stderr, "buffer"):
    sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding="utf-8", errors="replace")

import bootstrap

bootstrap.ensure_deps()

# Use FastMCP for easy tool registration
from mcp.server.fastmcp import FastMCP

import ide_bridge

# Create MCP server instance using FastMCP
mcp = FastMCP("AIDebugger")

BATCH_EVALUATION_TIMEOUT_SECONDS = 45.0
EVALUATION_TIMEOUT_SECONDS = 30.0
SAFE_DEBUG_BUNDLE_TIMEOUT_SECONDS = 20.0
DEBUG_BUNDLE_TIMEOUT_SECONDS = 60.0
THREADS_TIMEOUT_SECONDS = 30.0
MODULES_TIMEOUT_SECONDS = 45.0
EXCEPTION_TIMEOUT_SECONDS = 20.0
OUTPUT_TIMEOUT_SECONDS = 20.0
START_DEBUGGING_TIMEOUT_SECONDS = 60.0
MAX_TOOL_TIMEOUT_SECONDS = 300.0
MAX_THREAD_BUDGET_SECONDS = 115.0


def clamp_tool_timeout(timeout_seconds: Optional[float], default_seconds: float) -> float:
    if timeout_seconds is None:
        return default_seconds
    try:
        return max(1.0, min(float(timeout_seconds), MAX_TOOL_TIMEOUT_SECONDS))
    except (TypeError, ValueError):
        return default_seconds


def tool_timeout_for_budget(
    timeout_seconds: Optional[float],
    default_seconds: float,
    budget_ms: Optional[int],
    overhead_seconds: float = 15.0,
) -> float:
    if timeout_seconds is not None:
        return clamp_tool_timeout(timeout_seconds, default_seconds)
    try:
        if budget_ms is None:
            budget_seconds = 0.0
        elif float(budget_ms) == 0.0:
            budget_seconds = MAX_THREAD_BUDGET_SECONDS
        else:
            budget_seconds = float(budget_ms) / 1000.0
    except (TypeError, ValueError):
        budget_seconds = 0.0
    if budget_seconds <= 0:
        return default_seconds
    return min(MAX_TOOL_TIMEOUT_SECONDS, max(default_seconds, budget_seconds + overhead_seconds))


def add_thread_capture_params(
    params: Dict[str, Any],
    thread_include_frames: bool,
    thread_max_frames_per_thread: Optional[int],
    thread_max_nodes: Optional[int],
    thread_budget_ms: Optional[int],
) -> Dict[str, Any]:
    params["threadIncludeFrames"] = thread_include_frames
    if thread_max_frames_per_thread is not None:
        params["threadMaxFramesPerThread"] = int(thread_max_frames_per_thread)
    if thread_max_nodes is not None:
        params["threadMaxNodes"] = int(thread_max_nodes)
    if thread_budget_ms is not None:
        params["threadBudgetMs"] = int(thread_budget_ms)
    return params


async def wait_for_debugging_to_start(result: Any, timeout_seconds: float, attach: bool) -> Any:
    if not isinstance(result, dict) or not result.get("success") or attach is False:
        return result

    executable_path = str(result.get("executablePath") or "")
    wait_for_pause = executable_path.lower().endswith((".dmp", ".mdmp", ".hdmp"))
    status = result.get("status")
    if isinstance(status, dict) and status.get("isDebugging") and (not wait_for_pause or status.get("isPaused")):
        return result

    deadline = asyncio.get_running_loop().time() + timeout_seconds
    last_status = status
    while asyncio.get_running_loop().time() < deadline:
        await asyncio.sleep(0.5)
        try:
            last_status = await ide_bridge.call("get_debug_status", timeout=5.0)
        except ide_bridge.IDEBridgeError:
            continue

        if isinstance(last_status, dict):
            result["status"] = last_status
            if last_status.get("isDebugging") and (not wait_for_pause or last_status.get("isPaused")):
                result["success"] = True
                result["message"] = "Started debugging" if not wait_for_pause else "Started dump debugging and paused"
                return result

    result["success"] = False
    result["message"] = (
        "Timed out waiting for Visual Studio to pause in dump debugging"
        if wait_for_pause
        else "Timed out waiting for Visual Studio to enter debugging mode"
    )
    if isinstance(last_status, dict):
        result["status"] = last_status
    return result


# ============== MCP Prompt: Tool Usage Guide ==============
@mcp.prompt()
def debug_guide() -> str:
    """
    AIDebugger 工具使用指南 - 用于调试 Visual Studio / Rider 程序。
    当需要分析调试现场时，请先阅读此指南。
    """
    return """# AIDebugger MCP 工具使用指南

## 快速入门

当用户提到调试、崩溃、异常等场景时，推荐的调用顺序：

### 1. 获取低阻塞调试快照（推荐首选）
```
capture_debug_bundle()
```
默认只返回状态、堆栈、异常、线程，避免 locals/modules/registers/output 等重操作卡住 VS/Rider UI 线程。

### 2. 按需分步获取重信息
```
get_debug_status()     # 无参数 - 查看调试状态
get_callstack()        # 无参数 - 查看调用堆栈
get_locals()           # 无参数 - 查看局部变量
get_exception()        # 无参数 - 查看异常信息
```
确实需要完整包时显式传 include_locals/include_modules/include_registers/include_output。

### 3. 求值特定表达式
```
evaluate_expression(expression="myVariable")
evaluate_expression(expression="obj.Property", expand_children=True)
```
表达式默认交给 IDE 求值；失败、不可用或超时会返回明确错误。UE 对象名等信息优先用字段、地址、FName/内存解码等无调用路径补充分析。

## 参数格式说明

- 所有无参数的工具直接调用，不需要传任何参数
- 表达式类工具的参数名是 `expression`（不是 `expr`）
- 布尔参数使用 Python 风格：`True`/`False`（不是 `true`/`false`）

## 常用场景

| 场景 | 推荐工具 |
|------|----------|
| 分析崩溃/异常 | capture_debug_bundle()（低阻塞）或 get_exception() |
| 等待复现并自动抓现场 | continue_and_wait_for_break() |
| 查看变量值 | get_locals() 或 evaluate_expression(expression="xxx") |
| 查看调用链 | get_callstack() |
| 设置断点 | set_breakpoint(file_path="...", line_number=42) |
| 单步调试 | debug_step_over() / debug_step_into() / debug_step_out() |

## 多实例场景

如果有多个 VS/Rider 实例运行：
1. 调用 list_ide_instances() 查看所有实例
2. 调用 select_ide_instance(ide="vs", process_id=12345) 选择特定实例
"""


def format_result(result: Any) -> str:
    """Format result as readable text for MCP response."""
    if isinstance(result, dict):
        return json.dumps(result, indent=2, ensure_ascii=False)
    return str(result)


def handle_ide_error(e: Exception, operation: str) -> str:
    """Handle IDE bridge errors and return user-friendly message."""
    error_msg = str(e)
    if "No IDE instances found" in error_msg:
        return (
            "未找到IDE实例。请确保：\n"
            "1. Visual Studio 或 Rider 已启动\n"
            "2. 对应的插件已安装并启用\n"
            "3. 插件已正确加载（可能需要重启IDE）"
        )
    if "Preferred IDE" in error_msg:
        return f"Preferred IDE constraint: {error_msg}"
    if "Multiple IDE instances found" in error_msg:
        return error_msg  # Already formatted with instance list
    if "Failed to connect" in error_msg:
        return (
            "无法连接到IDE。请确保：\n"
            "1. Visual Studio 或 Rider 已启动\n"
            "2. 对应的插件已安装并启用\n"
            "3. 插件已正确加载（可能需要重启IDE）\n\n"
            f"错误详情: {error_msg}"
        )
    return f"执行 {operation} 时出错: {error_msg}"


@mcp.tool()
async def debug_capabilities() -> str:
    """
    Return a categorized index of AIDebugger tools. Use this first when unsure which
    debugger tool to call, especially for active debugging workflows such as attach,
    start, breakpoints, stepping, stack/thread selection, expression evaluation,
    memory inspection, stop debugging, detach, or data breakpoints.

    Use this before concluding a debugger capability is missing.
    """
    return format_result({
        "recommendedWorkflow": [
            "detect_debug_sessions(prefer_ide='vs') or list_vs_instances()",
            "attach_process(...) or start_debugging(...) if no debug session is active",
            "set_breakpoint(...) / set_function_breakpoint(...) / set_data_breakpoint(...)",
            "continue_and_wait_for_break(...) or continue()",
            "capture_debug_bundle() for low-blocking status/callstack/exception/threads",
            "for complete thread frames pass thread_max_frames_per_thread=0, thread_max_nodes=0, thread_budget_ms=0 or a large thread_budget_ms, and enough timeout_seconds",
            "opt in to include_locals/include_modules/include_registers/include_output only when needed",
            "evaluate_expressions_batch(...) for multiple values, evaluate_expression(...) for one value; prefer field/address/memory reads when function calls are risky",
            "debug_control(action='stop') or stop_debugging() to stop/terminate the current debug session",
        ],
        "categories": {
            "session": [
                "detect_debug_sessions",
                "list_vs_instances",
                "select_vs_instance",
                "attach_process",
                "start_debugging",
                "get_debug_status",
            ],
            "breakpoints": [
                "set_breakpoint",
                "set_function_breakpoint",
                "clear_breakpoint",
                "delete_breakpoint",
                "get_breakpoints",
                "enable_breakpoint",
                "delete_all_breakpoints",
            ],
            "executionControl": [
                "debug_control",
                "continue",
                "debug_continue",
                "pause",
                "debug_break",
                "step_over",
                "step_into",
                "step_out",
                "debug_run_to_cursor",
                "debug_set_next_statement",
                "debug_stop",
                "stop_debugging",
            ],
            "visualStudioOnly": [
                "detach_process",
                "detach_debugger",
            ],
            "inspection": [
                "capture_debug_bundle",
                "get_callstack",
                "get_call_stack",
                "get_locals",
                "evaluate_expression",
                "evaluate_expressions_batch",
                "get_exception",
                "get_modules",
                "get_registers",
                "get_disassembly",
                "get_disassembly_at",
            ],
            "navigation": [
                "get_threads",
                "list_threads",
                "switch_thread",
                "select_thread",
                "switch_stack_frame",
                "select_stack_frame",
            ],
            "advanced": [
                "read_memory",
                "search_memory",
                "set_data_breakpoint",
                "set_watchpoint",
                "add_watch",
                "get_debug_output",
                "search_output",
            ],
        },
        "capabilityNotes": {
            "capture_debug_bundle": "Safe by default: captures status, callstack, exception, and budgeted threads. Locals/modules/registers/output are explicit opt-ins; complete thread frames require thread_max_frames_per_thread=0, thread_max_nodes=0, thread_budget_ms=0 or a large thread_budget_ms, and enough timeout_seconds.",
            "expression_evaluation": "Expressions are attempted by default with bounded IDE timeouts. If a function call is unavailable or unsafe in the debug engine, the IDE result is returned instead of pre-rejecting it.",
            "stop": "Supported. Use debug_stop, stop_debugging, or debug_control(action='stop') to stop debugging / terminate the current debug session.",
            "detach": "Visual Studio detach is supported for the current debugged process via detach_process or debug_control(action='detach'). Rider detach returns an explicit unsupported result because no reliable public detach path is exposed here.",
            "set_data_breakpoint": "Uses Visual Studio native data breakpoints/watchpoints where supported by the active debug engine.",
            "logpoints": "Non-breaking Visual Studio tracepoint/logpoint actions are not exposed as a tool because EnvDTE has no reliable public action/message API.",
            "aliases": "Short aliases are exposed for common LLM names, but existing debug_* and switch_* names remain supported.",
        },
    })
# ============== Instance Management Tools ==============

@mcp.tool()
async def list_vs_instances() -> str:
    """
    列出所有可用的Visual Studio实例及其状态。
    显示每个实例的进程ID、解决方案名称、调试状态等信息。
    当有多个VS实例时，使用此工具查看所有实例，然后用select_vs_instance选择要操作的实例。
    """
    try:
        instances = [i for i in await ide_bridge.discover_instances() if i.ide == "vs"]
        
        if not instances:
            return "未找到任何Visual Studio实例。请确保VS已启动且AIDebugger扩展已加载。"
        
        result = {
            "instanceCount": len(instances),
            "instances": [inst.to_dict() for inst in instances],
            "hint": "使用 select_vs_instance 工具选择要操作的实例（通过 process_id 参数）"
        }
        
        # Add summary
        paused = [i for i in instances if i.is_paused]
        debugging = [i for i in instances if i.is_debugging and not i.is_paused]
        idle = [i for i in instances if not i.is_debugging]
        
        result["summary"] = {
            "paused": len(paused),
            "debugging": len(debugging),
            "idle": len(idle)
        }
        
        if len(paused) == 1:
            result["autoSelected"] = f"自动选择了唯一处于断点暂停状态的实例 (PID: {paused[0].process_id})"
        elif len(debugging) == 1 and len(paused) == 0:
            result["autoSelected"] = f"自动选择了唯一正在调试的实例 (PID: {debugging[0].process_id})"
        elif len(instances) == 1:
            result["autoSelected"] = f"自动选择了唯一的实例 (PID: {instances[0].process_id})"
        
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

        paused = [i for i in instances if i.is_paused]
        debugging = [i for i in instances if i.is_debugging and not i.is_paused]
        idle = [i for i in instances if not i.is_debugging]

        result["summary"] = {
            "paused": len(paused),
            "debugging": len(debugging),
            "idle": len(idle)
        }

        if len(paused) == 1:
            result["autoSelected"] = (
                f"自动选择了唯一处于断点暂停状态的实例 ({paused[0].ide.upper()} PID: {paused[0].process_id})"
            )
        elif len(debugging) == 1 and len(paused) == 0:
            result["autoSelected"] = (
                f"自动选择了唯一正在调试的实例 ({debugging[0].ide.upper()} PID: {debugging[0].process_id})"
            )
        elif len(instances) == 1:
            result["autoSelected"] = (
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
        instances = await ide_bridge.discover_instances()

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

        instances = [i for i in await ide_bridge.discover_instances() if i.ide == ide_key]

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
        instances = [i for i in await ide_bridge.discover_instances() if i.ide == "vs"]
        
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


# ============== Debug Status Tools ==============

@mcp.tool()
async def get_debug_status() -> str:
    """
    获取当前调试状态，包括是否暂停、当前位置，以及标准化的 stopReason（breakpoint/exception/step/user_break/...）。
    
    无需任何参数。
    
    返回示例:
    ```json
    {
      "isDebugging": true,
      "isPaused": true,
      "stopReason": "breakpoint",
      "currentLocation": {"file": "Program.cs", "line": 42, "function": "Main"}
    }
    ```
    
    **触发场景**: 当用户提到"调试"、"debug"、"断点"、"breakpoint"、"VS"、"Visual Studio"时，优先调用此工具了解当前状态。
    """
    try:
        result = await ide_bridge.call("get_debug_status")
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "get_debug_status")


# ============== Call Stack Tools ==============

@mcp.tool()
async def get_callstack() -> str:
    """
    获取当前线程的完整调用堆栈，包含每个栈帧的函数名、文件路径、行号等信息。
    Cursor可以根据返回的文件路径自动读取相关代码。
    
    无需任何参数。
    
    返回示例:
    ```json
    {
      "frames": [
        {"index": 0, "function": "DoSomething", "file": "Helper.cs", "line": 25},
        {"index": 1, "function": "Main", "file": "Program.cs", "line": 42}
      ],
      "totalFrames": 2
    }
    ```
    
    **触发场景**: 当用户询问"怎么执行到这里的"、"调用链"、"堆栈"、"callstack"、"函数调用顺序"时调用此工具。
    """
    try:
        result = await ide_bridge.call("get_callstack")
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "get_callstack")


@mcp.tool()
async def get_call_stack() -> str:
    """
    Alias for get_callstack.
    """
    return await get_callstack()


# ============== Variables Tools ==============

@mcp.tool()
async def get_locals(depth: int = 2) -> str:
    """
    获取当前栈帧的所有局部变量和参数，包含变量名、类型和当前值。
    
    Args:
        depth: 变量展开深度（0-4），默认2。值越大展开的子成员层级越多，超过预算会截断。
    
    返回示例:
    ```json
    {
      "variables": [
        {"name": "count", "type": "int", "value": "42"},
        {"name": "items", "type": "List<string>", "value": "{Count=3}"}
      ]
    }
    ```
    
    **触发场景**: 当用户询问"变量值是什么"、"参数是多少"、"局部变量"、"当前状态"时调用此工具。
    """
    try:
        result = await ide_bridge.call("get_locals", {"depth": max(0, min(depth, 4))})
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "get_locals")


@mcp.tool()
async def evaluate_expression(
    expression: str,
    expand_children: bool = False,
    max_depth: int = 2,
    timeout_seconds: Optional[float] = None,
    allow_side_effects: bool = False
) -> str:
    """
    在当前调试上下文中求值任意表达式。可以获取变量的地址、计算偏移量、访问结构成员等。
    对于指针和数值类型会同时返回十进制和十六进制值。
    
    Args:
        expression: 要求值的表达式，如：&myVariable、myPointer->field、sizeof(MyStruct)、(int*)0x1234
        expand_children: 是否展开对象的子成员，默认false
        max_depth: 展开子成员的最大深度，默认2
        timeout_seconds: 等待 IDE 响应的最长秒数，默认30，最大300
        allow_side_effects: 兼容旧客户端的参数；当前不会因函数调用形态预先拒绝表达式，IDE 会返回实际求值结果/错误/超时
    
    调用示例:
    ```
    evaluate_expression(expression="myObject.Property")
    evaluate_expression(expression="&myVariable", expand_children=True)
    evaluate_expression(expression="arr[0]", expand_children=True, max_depth=3)
    ```
    
    **触发场景**: 当用户想查看特定表达式的值、询问"xxx的值是多少"、"计算xxx"、"查看指针"、"展开对象"时调用此工具。
    """
    try:
        result = await ide_bridge.call("evaluate_expression", {
            "expression": expression,
            "expandChildren": expand_children,
            "maxDepth": max_depth,
            "allowSideEffects": allow_side_effects,
        }, timeout=clamp_tool_timeout(timeout_seconds, EVALUATION_TIMEOUT_SECONDS))
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "evaluate_expression")


@mcp.tool()
async def evaluate_expressions_batch(
    expressions: List[str],
    expand_children: bool = False,
    max_depth: int = 2,
    timeout_seconds: Optional[float] = None,
    allow_side_effects: bool = False
) -> str:
    """
    在同一调试快照中批量求值多个表达式，减少逐条请求导致的上下文漂移。

    Args:
        expressions: 表达式列表（最多100条）
        expand_children: 是否展开对象子成员，默认False
        max_depth: 子成员展开深度，默认2
        timeout_seconds: 等待 IDE 响应的最长秒数，默认45，最大300
        allow_side_effects: 兼容旧客户端的参数；当前不会因函数调用形态预先拒绝表达式
    """
    normalized_expressions = expressions or []
    try:
        result = await ide_bridge.call("evaluate_expressions_batch", {
            "expressions": normalized_expressions,
            "expandChildren": expand_children,
            "maxDepth": max_depth,
            "allowSideEffects": allow_side_effects,
        }, timeout=clamp_tool_timeout(timeout_seconds, BATCH_EVALUATION_TIMEOUT_SECONDS))
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "evaluate_expressions_batch")


# ============== Thread Tools ==============

@mcp.tool()
async def get_threads(
    timeout_seconds: Optional[float] = None,
    include_frames: bool = True,
    max_frames_per_thread: Optional[int] = None,
    max_nodes: Optional[int] = None,
    budget_ms: Optional[int] = None,
) -> str:
    """
    获取调试进程中所有线程的信息，包括线程ID、名称、状态和当前位置。

    默认低阻塞，可能返回 partial=true。需要完整线程帧时传
    max_frames_per_thread=0、max_nodes=0、budget_ms=0 或较大的 budget_ms；timeout_seconds 会随
    budget_ms 自动上调。partial=false 表示请求预算内线程/帧集合完整返回。
    
    **触发场景**: 当用户提到"线程"、"thread"、"多线程"、"并发"、"死锁"时调用此工具。
    """
    try:
        params: Dict[str, Any] = {"includeFrames": include_frames}
        if max_frames_per_thread is not None:
            params["maxFramesPerThread"] = int(max_frames_per_thread)
        if max_nodes is not None:
            params["maxNodes"] = int(max_nodes)
        if budget_ms is not None:
            params["budgetMs"] = int(budget_ms)
        result = await ide_bridge.call(
            "get_threads",
            params,
            timeout=tool_timeout_for_budget(timeout_seconds, THREADS_TIMEOUT_SECONDS, budget_ms),
        )
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "get_threads")


@mcp.tool()
async def list_threads(
    timeout_seconds: Optional[float] = None,
    include_frames: bool = True,
    max_frames_per_thread: Optional[int] = None,
    max_nodes: Optional[int] = None,
    budget_ms: Optional[int] = None,
) -> str:
    """
    Get debugger thread metadata. Defaults are low-blocking and may return partial=true.

    For a complete thread snapshot, raise max_frames_per_thread, max_nodes,
    budget_ms, and timeout_seconds. Use max_frames_per_thread=0, max_nodes=0,
    or budget_ms=0 to use the maximum bounded capture budget for an explicit full capture. partial=false means the
    returned thread/frame set was complete within the requested budget.

    Alias for get_threads.
    """
    return await get_threads(
        timeout_seconds=timeout_seconds,
        include_frames=include_frames,
        max_frames_per_thread=max_frames_per_thread,
        max_nodes=max_nodes,
        budget_ms=budget_ms,
    )


# ============== Module Tools ==============

@mcp.tool()
async def get_modules(timeout_seconds: Optional[float] = None) -> str:
    """
    获取调试进程中已加载的所有模块/程序集信息。
    """
    try:
        result = await ide_bridge.call("get_modules", timeout=clamp_tool_timeout(timeout_seconds, MODULES_TIMEOUT_SECONDS))
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "get_modules")


# ============== Exception Tools ==============

@mcp.tool()
async def get_exception(timeout_seconds: Optional[float] = None) -> str:
    """
    获取当前异常的详细信息，包括异常类型、消息、堆栈跟踪和内部异常。
    
    无需任何参数。仅当程序因异常暂停时有效。
    
    返回示例:
    ```json
    {
      "hasException": true,
      "exceptionType": "System.NullReferenceException",
      "message": "Object reference not set to an instance of an object",
      "stackTrace": "at Program.Main() in Program.cs:line 42"
    }
    ```
    
    **触发场景**: 当用户提到"崩溃"、"crash"、"异常"、"exception"、"报错"、"出错了"、"抛异常"时，优先调用此工具。
    """
    try:
        result = await ide_bridge.call("get_exception", timeout=clamp_tool_timeout(timeout_seconds, EXCEPTION_TIMEOUT_SECONDS))
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "get_exception")


# ============== Memory Tools ==============

@mcp.tool()
async def read_memory(address: str, size: int = 256) -> str:
    """
    读取指定内存地址的原始字节数据。用于分析内存布局、检查数据结构、排查内存损坏问题。
    返回十六进制转储、ASCII视图和格式化的内存视图。地址可以是十六进制或变量表达式。
    
    **触发场景**: 当用户提到"内存"、"memory"、"内存损坏"、"内存布局"、"查看地址"、"hex dump"时调用此工具。
    
    Args:
        address: 内存地址，支持十六进制（如0x7ff61234）或表达式（如&myVariable、myPointer）
        size: 要读取的字节数，默认256，最大4096
    """
    # Clamp size
    size = max(1, min(size, 4096))
    
    try:
        result = await ide_bridge.call("read_memory", {
            "address": address,
            "size": size
        })
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "read_memory")


# ============== Disassembly Tools ==============

@mcp.tool()
async def get_disassembly(lines_before: int = 20, lines_after: int = 20) -> str:
    """
    获取当前断点/暂停位置附近的反汇编代码。用于分析底层机器指令、排查优化代码问题、理解编译器生成的代码。
    返回汇编指令、机器码和对应的内存地址。
    
    **触发场景**: 当用户提到"反汇编"、"disassembly"、"汇编代码"、"机器指令"、"底层分析"、"asm"时调用此工具。
    
    Args:
        lines_before: 当前指令之前要显示的行数，默认20
        lines_after: 当前指令之后要显示的行数，默认20
    """
    try:
        result = await ide_bridge.call("get_disassembly", {
            "linesBefore": lines_before,
            "linesAfter": lines_after
        })
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "get_disassembly")


@mcp.tool()
async def get_disassembly_at(address: str, count: int = 50) -> str:
    """
    获取指定内存地址处的反汇编代码。可以用于查看特定函数或代码段的汇编指令。
    地址可以是十六进制格式（如0x7ff61234）或表达式（如&myFunction）。
    
    Args:
        address: 起始内存地址，支持十六进制（如0x7ff61234）或表达式（如&myFunction）
        count: 要获取的指令数量，默认50
    """
    try:
        result = await ide_bridge.call("get_disassembly_at", {
            "address": address,
            "count": count
        })
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "get_disassembly_at")


# ============== Build Error Tools ==============

@mcp.tool()
async def get_build_errors() -> str:
    """
    获取Visual Studio Error List中的所有编译错误和警告，包含错误代码（如CS0001）、文件路径、行号和详细描述。
    Cursor可以根据返回的文件路径自动读取相关代码并帮助修复。
    
    **触发场景**: 当用户提到"编译错误"、"build error"、"构建失败"、"无法编译"、"语法错误"、"红色波浪线"时，优先调用此工具。
    """
    try:
        result = await ide_bridge.call("get_build_errors")
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "get_build_errors")


@mcp.tool()
async def get_errors_only() -> str:
    """
    仅获取编译错误（不包括警告）。
    """
    try:
        result = await ide_bridge.call("get_errors_only")
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "get_errors_only")


@mcp.tool()
async def get_warnings_only() -> str:
    """
    仅获取编译警告（不包括错误）。
    """
    try:
        result = await ide_bridge.call("get_warnings_only")
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "get_warnings_only")


# ============== Output Window Tools ==============

@mcp.tool()
async def get_debug_output(
    pane: str = "Debug",
    tail_lines: int = 50,
    timeout_seconds: Optional[float] = None
) -> str:
    """
    获取Visual Studio Output窗口的内容。用于查看调试输出、构建日志、程序输出等。
    默认返回最后50行以避免内容过多。
    
    **触发场景**: 当用户提到"输出"、"output"、"日志"、"log"、"打印了什么"、"构建日志"时调用此工具。
    
    Args:
        pane: 输出面板名称，如 "Debug"（调试输出）, "Build"（构建日志）, "General" 等。默认为 "Debug"
        tail_lines: 返回最后N行，0表示返回全部。默认50行
    """
    try:
        result = await ide_bridge.call("get_output_window", {
            "pane": pane,
            "tailLines": tail_lines
        }, timeout=clamp_tool_timeout(timeout_seconds, OUTPUT_TIMEOUT_SECONDS))
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "get_debug_output")


@mcp.tool()
async def search_output(
    pattern: str,
    pane: str = "Debug",
    is_regex: bool = False,
    context_lines: int = 2,
    max_matches: int = 20,
    timeout_seconds: Optional[float] = None
) -> str:
    """
    在Visual Studio Output窗口中搜索指定模式。可用于查找特定错误、检查执行状态、定位关键输出等。
    
    Args:
        pattern: 搜索模式（字符串或正则表达式）
        pane: 输出面板名称，默认 "Debug"
        is_regex: 是否使用正则表达式匹配，默认False（普通字符串匹配，不区分大小写）
        context_lines: 每个匹配结果显示的上下文行数，默认2
        max_matches: 最大返回匹配数，默认20
    """
    try:
        result = await ide_bridge.call("search_output", {
            "pattern": pattern,
            "pane": pane,
            "isRegex": is_regex,
            "contextLines": context_lines,
            "maxMatches": max_matches
        }, timeout=clamp_tool_timeout(timeout_seconds, OUTPUT_TIMEOUT_SECONDS))
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "search_output")


# ============== Full Context Tools ==============

@mcp.tool()
async def get_full_context(
    timeout_seconds: Optional[float] = None,
    thread_include_frames: bool = True,
    thread_max_frames_per_thread: Optional[int] = None,
    thread_max_nodes: Optional[int] = None,
    thread_budget_ms: Optional[int] = None,
) -> str:
    """
    一次性获取所有调试上下文信息，包括状态、堆栈、变量、异常、线程和模块。
    当你需要完整分析调试现场时使用此工具。
    如需更强一致性（带 snapshotId 与批量表达式求值），优先使用 capture_debug_bundle。
    
    **触发场景**: 当用户说"帮我分析一下"、"看看发生了什么"、"调试现场"、"为什么会这样"等需要全面了解情况时调用此工具。
    """
    try:
        params = add_thread_capture_params(
            {},
            thread_include_frames,
            thread_max_frames_per_thread,
            thread_max_nodes,
            thread_budget_ms,
        )
        result = await ide_bridge.call(
            "get_full_context",
            params,
            timeout=tool_timeout_for_budget(timeout_seconds, DEBUG_BUNDLE_TIMEOUT_SECONDS, thread_budget_ms),
        )
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "get_full_context")


@mcp.tool()
async def capture_debug_bundle(
    expressions: Optional[List[str]] = None,
    expand_children: bool = False,
    max_depth: int = 2,
    output_tail_lines: int = 80,
    timeout_seconds: Optional[float] = None,
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
    抓取可分析调试快照包。
    返回 snapshotId/capturedAt/snapshotStatus/partial，可直接用于AI诊断。
    
    默认低阻塞：只抓 status/callstack/exception/threads，避免 locals/modules/registers/output 在 VS UI 线程上拖住整次请求。
    需要完整包时显式打开 include_locals/include_modules/include_registers/include_output。

    Args:
        expressions: 可选表达式列表，会在同一快照中批量求值
        expand_children: 表达式是否展开子成员，默认False
        max_depth: 表达式子成员展开深度，默认2
        output_tail_lines: Debug Output 末尾行数，默认80
        timeout_seconds: 等待 IDE 响应的最长秒数，默认低阻塞20/完整60，最大300；传 thread_budget_ms 时自动上调
        include_locals: 是否抓取当前帧 locals，默认False
        include_modules: 是否抓取模块/程序集，默认False
        include_registers: 是否抓取寄存器，默认False
        include_output: 是否读取 Debug Output，默认False
        include_threads: 是否抓取线程列表，默认True
        include_exception: 是否抓取异常信息，默认True
        allow_side_effects: 兼容旧客户端的参数；当前不会因函数调用形态预先拒绝表达式
        thread_include_frames: 是否在 threads 中包含 stack frames，默认True
        thread_max_frames_per_thread/thread_max_nodes/thread_budget_ms: 线程帧抓取预算；0 表示使用最大受控抓取预算
    
    调用示例:
    ```
    # 基础用法 - 无需参数
    capture_debug_bundle()
    
    # 带表达式求值
    capture_debug_bundle(expressions=["myVar", "obj.Property", "arr.Length"])
    
    # 显式完整包
    capture_debug_bundle(include_locals=True, include_modules=True, include_registers=True, include_output=True)
    ```
    """
    normalized_expressions = expressions or []
    heavy_components_requested = (
        include_locals or include_modules or include_registers or include_output or bool(normalized_expressions)
    )
    default_timeout = DEBUG_BUNDLE_TIMEOUT_SECONDS if heavy_components_requested else SAFE_DEBUG_BUNDLE_TIMEOUT_SECONDS

    try:
        params = add_thread_capture_params({
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
            "capture_debug_bundle",
            params,
            timeout=tool_timeout_for_budget(timeout_seconds, default_timeout, thread_budget_ms),
        )
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "capture_debug_bundle")


# ============== Debug Control Tools ==============

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
        })
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
        result = await ide_bridge.call("start_debugging", {
            "executablePath": executable_path,
            "workingDirectory": working_dir,
            "arguments": args,
            "attach": attach
        }, timeout=wait_timeout)
        result = await wait_for_debugging_to_start(result, wait_timeout, attach)
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
    without killing the process. Rider returns an explicit unsupported result.
    """
    try:
        result = await ide_bridge.call("detach")
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


# ============== Breakpoint Tools ==============

@mcp.tool()
async def get_breakpoints() -> str:
    """
    获取所有已设置的断点列表，包括文件断点、函数断点和条件断点。
    返回断点位置、条件、命中次数等详细信息。
    
    **触发场景**: 当用户询问"断点在哪"、"有哪些断点"、"breakpoint列表"时调用此工具。
    """
    try:
        result = await ide_bridge.call("get_breakpoints")
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "get_breakpoints")


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
        params = {
            "filePath": file_path,
            "lineNumber": line_number,
            "hitCount": max(0, hit_count),
        }
        if condition:
            params["condition"] = condition
        if hit_count_type:
            params["hitCountType"] = hit_count_type
        if condition_type:
            params["conditionType"] = condition_type
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
        params = {
            "functionName": function_name,
            "hitCount": max(0, hit_count),
        }
        if condition:
            params["condition"] = condition
        if hit_count_type:
            params["hitCountType"] = hit_count_type
        if condition_type:
            params["conditionType"] = condition_type
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
        params = {
            "dataExpression": data_expression,
            "byteCount": max(1, min(byte_count, 1024)),
            "hitCount": max(0, hit_count),
        }
        if condition:
            params["condition"] = condition
        if hit_count_type:
            params["hitCountType"] = hit_count_type
        if condition_type:
            params["conditionType"] = condition_type
        result = await ide_bridge.call("set_data_breakpoint", params)
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "set_data_breakpoint")


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
async def delete_breakpoint(file_path: str, line_number: int) -> str:
    """
    删除指定位置的断点。
    
    Args:
        file_path: 源文件的完整路径
        line_number: 断点所在行号
    """
    try:
        result = await ide_bridge.call("delete_breakpoint", {
            "filePath": file_path,
            "lineNumber": line_number
        })
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "delete_breakpoint")


@mcp.tool()
async def clear_breakpoint(file_path: str, line_number: int) -> str:
    """
    Alias for delete_breakpoint.
    """
    try:
        result = await ide_bridge.call("clear_breakpoint", {
            "filePath": file_path,
            "lineNumber": line_number
        })
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "clear_breakpoint")


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
    enabled: bool = True
) -> str:
    """
    启用或禁用指定位置的断点。禁用的断点保留但不触发。
    
    Args:
        file_path: 源文件的完整路径
        line_number: 断点所在行号
        enabled: True启用，False禁用
    """
    try:
        result = await ide_bridge.call("enable_breakpoint", {
            "filePath": file_path,
            "lineNumber": line_number,
            "enabled": enabled
        })
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "enable_breakpoint")


# ============== Navigation Tools ==============

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
async def switch_thread(thread_id: int) -> str:
    """
    切换到指定线程以查看其调用堆栈和变量。
    使用 get_threads 获取线程ID。
    
    Args:
        thread_id: 目标线程的ID
    """
    try:
        result = await ide_bridge.call("switch_thread", {
            "threadId": thread_id
        })
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "switch_thread")


@mcp.tool()
async def select_thread(thread_id: int) -> str:
    """
    Alias for switch_thread.
    """
    return await switch_thread(thread_id)


# ============== Register Tools ==============

@mcp.tool()
async def get_registers() -> str:
    """
    获取CPU寄存器值（RAX, RBX, RCX, RDX, RSP, RBP, RIP, RFLAGS等）。
    用于底层调试和分析。包括通用寄存器、段寄存器和XMM寄存器。
    
    **触发场景**: 当用户提到"寄存器"、"register"、"RAX"、"RSP"、"RIP"、"CPU状态"时调用此工具。
    """
    try:
        result = await ide_bridge.call("get_registers")
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "get_registers")


# ============== Watch Tools ==============

@mcp.tool()
async def add_watch(expression: str) -> str:
    """
    添加一个监视表达式到VS的Watch窗口，并返回当前值。
    
    Args:
        expression: 要监视的表达式
    """
    try:
        result = await ide_bridge.call("add_watch", {
            "expression": expression
        })
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "add_watch")


# ============== Memory Search Tools ==============

@mcp.tool()
async def search_memory(
    start_address: str,
    pattern: str,
    max_results: int = 100,
    search_size: int = 0x10000
) -> str:
    """
    在内存中搜索指定模式。可用于查找特定数据或字符串。
    
    Args:
        start_address: 搜索起始地址（十六进制如0x7ff61234或表达式如&myVar）
        pattern: 搜索模式，可以是十六进制字节(如"4D 5A")或ASCII字符串
        max_results: 最大返回结果数，默认100
        search_size: 搜索范围大小（字节），默认64KB，最大10MB
    """
    # Clamp parameters to safe ranges
    search_size = max(1, min(search_size, 10 * 1024 * 1024))  # max 10 MB
    max_results = max(1, min(max_results, 10000))
    
    try:
        result = await ide_bridge.call("search_memory", {
            "startAddress": start_address,
            "pattern": pattern,
            "maxResults": max_results,
            "searchSize": search_size
        })
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "search_memory")


# ============== Main Entry Point ==============

if __name__ == "__main__":
    # Run the server using stdio transport
    mcp.run(transport="stdio")
