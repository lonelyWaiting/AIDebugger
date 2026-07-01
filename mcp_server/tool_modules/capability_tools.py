from typing import Any, Dict, List, Optional
import asyncio

try:
    from .. import ide_bridge
    from ..mcp_app import mcp
    from ..tool_context import *
    from ..tool_registry import tool_registry_summary
except ImportError:
    import ide_bridge
    from mcp_app import mcp
    from tool_context import *
    from tool_registry import tool_registry_summary
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

Crash/dump triage: prefer crash_triage() for exception, callstack, modules,
registers, output tail, nearby disassembly, and stack memory in one pass.
Step plus snapshot: prefer step_and_capture(action="step_over") when the next
question depends on the new stop location.

## 多实例场景

如果有多个 VS/Rider 实例运行：
1. 调用 list_ide_instances() 查看所有实例
2. 调用 select_ide_instance(ide="vs", process_id=12345) 选择特定实例
"""

def _categories_with_registry_coverage(registry: Dict[str, Any]) -> Dict[str, List[str]]:
    categories: Dict[str, List[str]] = {
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
            "get_breakpoint_bindings",
            "continue_and_capture_breakpoint",
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
            "step_and_capture",
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
            "crash_triage",
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
            "get_breakpoint_bindings",
            "continue_and_capture_breakpoint",
            "add_watch",
            "get_debug_output",
            "search_output",
        ],
        "noSourceNative": [
            "resolve_address",
            "get_module_layout",
            "set_address_breakpoint",
            "capture_native_context",
            "continue_and_capture_native",
            "get_disassembly_at",
            "read_memory",
            "search_memory",
            "get_registers",
        ],
        "noSourceManaged": [
            "inspect_managed_assembly",
            "inspect_managed_type",
            "inspect_managed_method_il",
        ],
        "noSourceHybrid": [
            "capture_hybrid_context",
            "continue_and_capture_breakpoint",
            "get_breakpoint_bindings",
            "switch_thread",
            "switch_stack_frame",
        ],
    }
    categorized = {tool for tools in categories.values() for tool in tools}
    registry_tools = set((registry.get("toolSupport") or {}).keys())
    missing = sorted(registry_tools - categorized)
    if missing:
        categories["registryOther"] = missing
    return categories


@mcp.tool()
async def debug_capabilities() -> str:
    """
    Return a categorized index of AIDebugger tools. Use this first when unsure which
    debugger tool to call, especially for active debugging workflows such as attach,
    start, breakpoints, stepping, stack/thread selection, expression evaluation,
    memory inspection, stop debugging, detach, or data breakpoints.

    Use this before concluding a debugger capability is missing.
    """
    registry = tool_registry_summary()
    return format_result({
        "toolRegistry": registry,
        "categoryNames": [
            "noSourceNative",
            "noSourceManaged",
            "noSourceHybrid",
        ],
        "recommendedWorkflow": [
            "detect_debug_sessions(prefer_ide='vs') or list_vs_instances()",
            "attach_process(...) or start_debugging(...) if no debug session is active",
            "set_breakpoint(...) / set_function_breakpoint(...) / set_data_breakpoint(...)",
            "Visual Studio only: for no-source native debugging use resolve_address, get_module_layout, set_address_breakpoint, capture_native_context, and continue_and_capture_native",
            "Visual Studio only: for no-source managed analysis use inspect_managed_assembly, inspect_managed_type, and inspect_managed_method_il against the target assembly path",
            "Visual Studio only: for mixed native/managed stops use capture_hybrid_context and continue_and_capture_breakpoint to collect native context, threads, and breakpoint binding data together",
            "continue_and_wait_for_break(...) or continue()",
            "capture_debug_bundle() for low-blocking status/callstack/exception/threads",
            "for complete get_threads frames pass max_frames_per_thread=0, max_nodes=0, budget_ms=0 or a large budget_ms, and enough timeout_seconds",
            "for capture_debug_bundle/full_context/continue_and_wait thread frames use thread_max_frames_per_thread/thread_max_nodes/thread_budget_ms",
            "opt in to include_locals/include_modules/include_registers/include_output only when needed",
            "evaluate_expressions_batch(...) for multiple values, evaluate_expression(...) for one value; prefer field/address/memory reads when function calls are risky",
            "debug_control(action='stop') or stop_debugging() to stop/terminate the current debug session",
        ],
        "categories": _categories_with_registry_coverage(registry),
        "capabilityNotes": {
            "capture_debug_bundle": "Safe by default: captures status, callstack, exception, and budgeted threads. Locals/modules/registers/output are explicit opt-ins; complete thread frames require thread_max_frames_per_thread=0, thread_max_nodes=0, thread_budget_ms=0 or a large thread_budget_ms, and enough timeout_seconds.",
            "get_threads": "Direct get_threads uses max_frames_per_thread, max_nodes, and budget_ms. The thread_* names are for bundle/full-context tools.",
            "crash_triage": "Focused Visual Studio crash/dump first pass: captures exception, callstack, modules, registers, output tail, nearby disassembly, and stack memory when available.",
            "expression_evaluation": "Expressions are attempted by default with bounded IDE timeouts. If a function call is unavailable or unsafe in the debug engine, the IDE result is returned instead of pre-rejecting it.",
            "stop": "Supported. Use debug_stop, stop_debugging, or debug_control(action='stop') to stop debugging / terminate the current debug session.",
            "detach": "Visual Studio detach is supported for the current debugged process via detach_process or debug_control(action='detach'). Rider is not exposed as a supported detach backend because no reliable public detach path is available here.",
            "set_data_breakpoint": "Uses Visual Studio native data breakpoints/watchpoints where supported by the active debug engine.",
            "breakpoint_binding_verification": "Breakpoint results include bindingVerificationStatus, isRelocated, nearbyProbeAttempted, optimizedCodeSuspected, and verificationStatus so optimized-away or unbound source locations are explicit instead of treated as successful hits.",
            "no_source_native": "Information-only native debugging surface: normalize addresses as module+RVA, inspect PE layout, set address breakpoints, capture registers/disassembly/stack memory/modules, and let the AI decide the next probe. No write-memory or patching tool is exposed.",
            "no_source_managed": "Read-only managed assembly analysis: enumerate metadata, select types/methods, and decode IL instructions/tokens without source or UI automation.",
            "no_source_hybrid": "Breakpoint-oriented dynamic loop: collect binding status, continue until the next stop, then capture native context plus managed thread/frame data in one snapshot.",
            "no_source_backend_support": "The no-source native/managed/hybrid tools are Visual Studio only in this version. Rider support is explicitly missing rather than inferred from VS behavior.",
            "logpoints": "Non-breaking Visual Studio tracepoint/logpoint actions are not exposed as a tool because EnvDTE has no reliable public action/message API.",
            "aliases": "Short aliases are exposed for common LLM names, but existing debug_* and switch_* names remain supported.",
        },
    })
