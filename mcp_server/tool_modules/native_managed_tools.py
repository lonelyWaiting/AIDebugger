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
async def resolve_address(
    address: str,
    timeout_seconds: Optional[float] = None
) -> str:
    """
    Resolve a native address expression into a stable module+RVA form.

    Accepts absolute addresses, register expressions such as @rip/@eip, and
    module+RVA expressions such as devenv.exe+0x1234.
    """
    try:
        result = await ide_bridge.call(
            "resolve_address",
            {"address": address},
            timeout=clamp_tool_timeout(timeout_seconds, NATIVE_CONTEXT_TIMEOUT_SECONDS),
            preferred_ide="vs",
        )
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "resolve_address")

@mcp.tool()
async def get_module_layout(
    module: str,
    max_import_functions: int = 256,
    max_exports: int = 512,
    timeout_seconds: Optional[float] = None
) -> str:
    """
    Return PE layout information for a loaded module or module file path.

    Includes sections, entrypoint, image size, and bounded import/export summaries.
    """
    try:
        result = await ide_bridge.call(
            "get_module_layout",
            {
                "module": module,
                "maxImportFunctions": max(0, min(max_import_functions, 4096)),
                "maxExports": max(0, min(max_exports, 4096)),
            },
            timeout=clamp_tool_timeout(timeout_seconds, NATIVE_CONTEXT_TIMEOUT_SECONDS),
            preferred_ide="vs",
        )
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "get_module_layout")

@mcp.tool()
async def capture_native_context(
    disassembly_count: int = 32,
    stack_bytes: int = 256,
    include_modules: bool = True,
    timeout_seconds: Optional[float] = None
) -> str:
    """
    Capture an information-only native debugging context at the current stop.

    Returns normalized RIP/EIP and stack pointer addresses, registers, callstack,
    nearby disassembly, stack memory, and optionally loaded modules.
    """
    try:
        result = await ide_bridge.call(
            "capture_native_context",
            {
                "disassemblyCount": max(1, min(disassembly_count, 256)),
                "stackBytes": max(16, min(stack_bytes, 4096)),
                "includeModules": include_modules,
            },
            timeout=clamp_tool_timeout(timeout_seconds, NATIVE_CONTEXT_TIMEOUT_SECONDS),
            preferred_ide="vs",
        )
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "capture_native_context")

@mcp.tool()
async def inspect_managed_assembly(
    assembly_path: str,
    type_filter: Optional[str] = None,
    max_types: int = 256,
    max_methods_per_type: int = 64,
    timeout_seconds: Optional[float] = None
) -> str:
    """
    Read managed assembly metadata without source or UI automation.

    Returns bounded type and method summaries for a DLL/EXE path. Use
    type_filter to narrow large shipping assemblies before inspecting IL.
    """
    try:
        params = {
            "assemblyPath": assembly_path,
            "maxTypes": max(1, min(max_types, 4096)),
            "maxMethodsPerType": max(0, min(max_methods_per_type, 512)),
        }
        if type_filter:
            params["typeFilter"] = type_filter
        result = await ide_bridge.call(
            "inspect_managed_assembly",
            params,
            timeout=clamp_tool_timeout(timeout_seconds, MANAGED_INSPECTION_TIMEOUT_SECONDS),
            preferred_ide="vs",
        )
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "inspect_managed_assembly")

@mcp.tool()
async def inspect_managed_type(
    assembly_path: str,
    type_name: str,
    max_methods: int = 512,
    timeout_seconds: Optional[float] = None
) -> str:
    """
    Return detailed metadata for one managed type in a DLL/EXE path.

    type_name may be a full name or a simple type name when unique enough for
    the assembly.
    """
    try:
        result = await ide_bridge.call(
            "inspect_managed_type",
            {
                "assemblyPath": assembly_path,
                "typeName": type_name,
                "maxMethods": max(0, min(max_methods, 4096)),
            },
            timeout=clamp_tool_timeout(timeout_seconds, MANAGED_INSPECTION_TIMEOUT_SECONDS),
            preferred_ide="vs",
        )
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "inspect_managed_type")

@mcp.tool()
async def inspect_managed_method_il(
    assembly_path: str,
    type_name: str,
    method_name: str,
    overload_index: int = 0,
    max_instructions: int = 512,
    timeout_seconds: Optional[float] = None
) -> str:
    """
    Decode IL for a managed method without source or UI automation.

    Includes opcode offsets, branch targets, switch targets, metadata tokens,
    resolved members/strings/types where possible, parameters, and locals.
    Constructors can be requested with method_name=".ctor" or ".cctor".
    """
    try:
        result = await ide_bridge.call(
            "inspect_managed_method_il",
            {
                "assemblyPath": assembly_path,
                "typeName": type_name,
                "methodName": method_name,
                "overloadIndex": max(0, overload_index),
                "maxInstructions": max(1, min(max_instructions, 4096)),
            },
            timeout=clamp_tool_timeout(timeout_seconds, MANAGED_INSPECTION_TIMEOUT_SECONDS),
            preferred_ide="vs",
        )
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "inspect_managed_method_il")

@mcp.tool()
async def capture_hybrid_context(
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
    timeout_seconds: Optional[float] = None
) -> str:
    """
    Capture a mixed no-source debug snapshot at the current stop.

    Combines native address/register/disassembly/stack memory, managed thread
    frames, and breakpoint binding state so the caller can reason about the
    current stop without reading UI panels.
    """
    try:
        params = add_thread_capture_params({
            "disassemblyCount": max(1, min(disassembly_count, 256)),
            "stackBytes": max(16, min(stack_bytes, 4096)),
            "includeNative": include_native,
            "includeModules": include_modules,
            "includeThreads": include_threads,
            "includeBreakpoints": include_breakpoints,
        }, thread_include_frames, thread_max_frames_per_thread, thread_max_nodes, thread_budget_ms)
        result = await ide_bridge.call(
            "capture_hybrid_context",
            params,
            timeout=tool_timeout_for_budget(timeout_seconds, HYBRID_CONTEXT_TIMEOUT_SECONDS, thread_budget_ms),
            preferred_ide="vs",
        )
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "capture_hybrid_context")
