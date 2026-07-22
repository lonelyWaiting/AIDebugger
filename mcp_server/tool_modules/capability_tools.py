from typing import Any, Dict, List, Optional
import asyncio
import os

try:
    from ..tool_runtime import backend as ide_bridge, mcp
    from ..tool_context import *
    from ..tool_runtime import tool_registry_summary
except ImportError:
    from tool_runtime import backend as ide_bridge, mcp
    from tool_context import *
    from tool_runtime import tool_registry_summary
@mcp.prompt()
def debug_guide() -> str:
    """
    AIDebugger 工具使用指南 - 用于调试 Visual Studio / Rider 程序。
    当需要分析调试现场时，请先阅读此指南。
    """
    if os.environ.get("AIDB_SERVER_SCOPE") == "vs":
        return """# AIDebugger Visual Studio guide

Use detect_debug_sessions() or list_vs_instances() first. If several Visual Studio
instances are reachable, pin one with select_vs_instance(process_id=...). Use
capture_debug_bundle() for a bounded first snapshot, then request locals,
expressions, memory, or execution control only when the selected instance declares
the required capability. This server never discovers or selects Rider.
"""
    return """# AIDebugger MCP 工具使用指南

## 快速入门

当用户提到调试、崩溃、异常等场景时，推荐的调用顺序：

### 1. 获取低阻塞调试快照（推荐首选）
```
capture_debug_bundle()
```
Visual Studio read-only mode returns status, call stack, and budgeted threads.
Exception, locals, registers, and expressions require full-access unsafe-evaluation capability.
默认只返回状态、堆栈、预算化线程；异常需显式 include_exception=True，避免 exception/locals/modules/registers/output 等重操作卡住 VS/Rider UI 线程。

### 2. 按需分步获取重信息
```
get_debug_status()     # 无参数 - 查看调试状态
get_callstack()        # 无参数 - 查看调用堆栈
get_callstack()        # 先取得 frame.context
get_locals(...)        # 必须传入完整 frame context，查看该帧局部变量
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
| 查看变量值 | 先 get_callstack()，再用完整 frame context 调用 get_locals(...)；或 evaluate_expression(expression="xxx") |
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
            "get_debug_processes",
            "list_debug_processes",
            "switch_process",
            "select_process",
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
    registry_tools = set((registry.get("toolSupport") or {}).keys())
    categories = {
        name: [tool for tool in tools if tool in registry_tools]
        for name, tools in categories.items()
    }
    categories = {name: tools for name, tools in categories.items() if tools}
    categorized = {tool for tools in categories.values() for tool in tools}
    missing = sorted(registry_tools - categorized)
    if missing:
        categories["registryOther"] = missing
    return categories


def _recommended_workflow_for_access(effective_instance: Optional[Dict[str, Any]]) -> List[str]:
    if not effective_instance:
        return [
            "No unique effective IDE instance is selected; do not assume any debugger capability.",
            "Use detect_debug_sessions or list_vs_instances, inspect allowedCapabilities/accessMode, then select the intended instance.",
        ]

    access_mode = effective_instance.get("accessMode")
    allowed = set(effective_instance.get("allowedCapabilities") or [])
    if access_mode == "full_access":
        return [
            "detect_debug_sessions(prefer_ide='vs') or list_vs_instances()",
            "attach_process(...) or start_debugging(...) if no debug session is active",
            "Visual Studio multi-process session: get_debug_processes(), then switch_process(process_id) while paused",
            "set_breakpoint(...) / set_function_breakpoint(...) / set_data_breakpoint(...)",
            "Visual Studio only: for no-source native debugging use resolve_address, get_module_layout, set_address_breakpoint, capture_native_context, and continue_and_capture_native",
            "Visual Studio only: for no-source managed analysis use inspect_managed_assembly, inspect_managed_type, and inspect_managed_method_il against the target assembly path",
            "Visual Studio only: for mixed native/managed stops use capture_hybrid_context and continue_and_capture_breakpoint to collect native context, threads, and breakpoint binding data together",
            "continue_and_wait_for_break(...) or continue()",
            "capture_debug_bundle() for low-blocking status/callstack/budgeted threads; set include_exception=True only when unsafe evaluation is explicitly needed",
            "for the broadest bounded get_threads capture pass max_frames_per_thread=0, max_nodes=0, budget_ms=0, and enough timeout_seconds; zero selects server hard caps",
            "for capture_debug_bundle/full_context/continue_and_wait thread frames use thread_max_frames_per_thread/thread_max_nodes/thread_budget_ms",
            "opt in to include_exception/include_locals/include_modules/include_registers/include_output only when needed",
            "evaluate_expressions_batch(...) for multiple values, evaluate_expression(...) for one value; prefer field/address/memory reads when function calls are risky",
            "debug_control(action='stop') or stop_debugging() to stop/terminate the current debug session",
        ]

    workflow = [
        f"The effective Visual Studio instance has {access_mode or 'unknown'} access; use only actions whose required capabilities are present in allowedCapabilities.",
        "list_vs_instances() to re-check the exact target access before changing debugger state.",
    ]
    if "debug.read" in allowed:
        workflow.append("Available read path: get_debug_status/get_callstack/get_threads/get_breakpoints/get_build_errors/get_debug_output and default capture_debug_bundle(); Visual Studio also provides get_debug_processes for same-session targets.")
    if "debug.memory.read" in allowed:
        workflow.append("Memory capability is enabled: read_memory/get_disassembly/get_registers may be used.")
        if "debug.read" in allowed:
            workflow.append("Combined debug.memory.read + debug.read also enables read-only native/hybrid capture tools.")
    if "debug.evaluate.unsafe" in allowed:
        workflow.append("Unsafe evaluation is enabled: get_exception/evaluate_expression tools may execute user code and must be requested explicitly.")
        if "debug.read" in allowed:
            workflow.append("Combined debug.evaluate.unsafe + debug.read also enables locals/full-context capture.")
    if "debug.breakpoint.write" in allowed:
        workflow.append("Breakpoint-write capability enables unconditional source/function/address breakpoints plus delete/enable operations.")
        if "debug.evaluate.unsafe" in allowed:
            workflow.append("Combined debug.breakpoint.write + debug.evaluate.unsafe additionally enables conditional source/function/address breakpoints and data/watchpoint expressions.")
    if "debug.execution.control" in allowed:
        workflow.append("Execution-control capability is enabled: continue/pause/step/stop/detach may be used in a compatible debugger state.")
    if "debug.context.write" in allowed:
        workflow.append("Context-write capability is enabled: switch_thread/switch_stack_frame and Visual Studio same-session switch_process may be used while paused.")
    if "debug.session.write" in allowed:
        workflow.append("Session-write capability is enabled: attach_process/start_debugging may be used.")
    if access_mode == "read_only":
        workflow.append("Restart the Visual Studio server explicitly in full-access mode before requesting mutating or unsafe capabilities.")
    return workflow


@mcp.tool()
async def debug_capabilities() -> str:
    """
    Return a categorized index of AIDebugger tools. Use this first when unsure which
    debugger tool to call, especially for active debugging workflows such as attach,
    start, breakpoints, stepping, process/thread/stack selection, expression evaluation,
    memory inspection, stop debugging, detach, or data breakpoints.

    Use this before concluding a debugger capability is missing.
    """
    registry = tool_registry_summary()
    access_instances = []
    effective_instance = None
    stale_selected_target = None
    access_discovery_error = None
    bridge = ide_bridge.get_bridge()
    try:
        instances = await ide_bridge.discover_instances(
            preferred_ide=bridge.get_preferred_ide(),
            timeout=2.0,
        )
        access_instances = [instance.to_dict() for instance in instances]
        selected_ide, selected_pipe, selected_target, _ = await bridge.get_selection_snapshot()
        selected = next((
            instance for instance in instances
            if instance.ide == selected_ide and instance.pipe_name == selected_pipe and
            instance.connection_status == "ok" and (
                selected_ide != "vs" or selected_target is None or all(
                    instance.to_dict().get(key) == selected_target.get(key)
                    for key in ("processId", "processStartTimeUtcTicks", "serverInstanceId")
                )
            )
        ), None)
        has_pinned_selection = bool(selected_ide and selected_pipe)
        if selected is None and has_pinned_selection:
            stale_selected_target = {
                "ide": selected_ide,
                "pipeName": selected_pipe,
                "status": "selected_target_not_reachable_in_current_discovery",
            }
            if selected_target is not None:
                stale_selected_target.update(selected_target)
        elif selected is None:
            candidates, preferred = bridge.get_candidate_instances(
                instances,
                preferred_ide=bridge.get_preferred_ide(),
            )
            selected = bridge.auto_select_instance(candidates, preferred_ide=preferred)
        effective_instance = selected.to_dict() if selected is not None else None
    except Exception as error:
        access_discovery_error = f"{type(error).__name__}: {error}"

    recommended_workflow = _recommended_workflow_for_access(effective_instance)
    categories = _categories_with_registry_coverage(registry)
    return format_result({
        "toolRegistry": registry,
        "catalogScope": "static_tool_index; effective availability is constrained by each instance's allowedCapabilities",
        "instanceAccess": access_instances,
        "effectiveInstance": effective_instance,
        "staleSelectedTarget": stale_selected_target,
        "accessDiscoveryError": access_discovery_error,
        "categoryNames": list(categories),
        "recommendedWorkflow": recommended_workflow,
        "categories": categories,
        "capabilityNotes": {
            "capture_debug_bundle": "Safe by default: captures status, callstack, and budgeted threads. Exception requires include_exception=True plus debug.evaluate.unsafe; locals/modules/registers/output are also explicit opt-ins. Zero-valued thread limits select bounded server hard caps rather than an unbounded capture.",
            "get_threads": "Direct get_threads uses max_frames_per_thread, max_nodes, and budget_ms. The thread_* names are for bundle/full-context tools.",
            "crash_triage": "Focused Visual Studio crash/dump first pass: captures exception, callstack, modules, registers, output tail, nearby disassembly, and stack memory when available.",
            "expression_evaluation": "Expressions are attempted by default with bounded IDE timeouts. If a function call is unavailable or unsafe in the debug engine, the IDE result is returned instead of pre-rejecting it.",
            "stop": "Supported. Use debug_stop, stop_debugging, or debug_control(action='stop') to stop debugging / terminate the current debug session.",
            "detach": "Visual Studio detach is supported for the current debugged process via detach_process or debug_control(action='detach'). Rider is not exposed as a supported detach backend because no reliable public detach path is available here.",
            "same_session_process_selection": "Visual Studio only: call get_debug_processes, then switch_process(process_id). Only Debugger.DebuggedProcesses entries are eligible, so switching never attaches a new local process. Rider exposes no equivalent multi-process selection contract.",
            "set_data_breakpoint": "Uses Visual Studio native data breakpoints/watchpoints where supported by the active debug engine.",
            "breakpoint_binding_verification": "Breakpoint results include bindingVerificationStatus, isRelocated, nearbyProbeAttempted, optimizedCodeSuspected, and verificationStatus so optimized-away or unbound source locations are explicit instead of treated as successful hits.",
            "no_source_native": "Information-only native debugging surface: normalize addresses as module+RVA, inspect PE layout, set address breakpoints, capture registers/disassembly/stack memory/modules, and let the AI decide the next probe. No write-memory or patching tool is exposed.",
            "no_source_managed": "Read-only managed assembly analysis: enumerate metadata, select types/methods, and decode IL instructions/tokens without source or UI automation.",
            "no_source_hybrid": "Breakpoint-oriented dynamic loop: collect binding status, continue until the next stop, then capture native context plus managed thread/frame data in one snapshot.",
            "no_source_backend_support": "The no-source native/managed/hybrid tools are Visual Studio only in this version. Rider support is explicitly missing rather than inferred from VS behavior.",
            "logpoints": "Non-breaking Visual Studio tracepoint/logpoint actions are not exposed as a tool because EnvDTE has no reliable public action/message API.",
            "aliases": (
                "Legacy short aliases are registered and listed in toolRegistry.aliases."
                if registry.get("legacyAliasesEnabled")
                else "Legacy short aliases are disabled; only the canonical registered tool names in toolRegistry.toolSupport are available. Set AIDB_ENABLE_LEGACY_ALIASES=1 before startup to enable them."
            ),
        },
    })
