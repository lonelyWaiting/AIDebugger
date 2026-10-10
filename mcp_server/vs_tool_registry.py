"""Static Visual Studio MCP catalog.

The entrypoint must not import a broader registry merely to decide which tools
exist.  This catalog describes only its own public surface.
"""

import json
import hashlib
import os
from pathlib import Path
from typing import Dict, List, Optional, Tuple

LEGACY_ALIASES = {
    "get_call_stack": "get_callstack",
    "list_threads": "get_threads",
    "continue": "debug_continue",
    "step_over": "debug_step_over",
    "step_into": "debug_step_into",
    "step_out": "debug_step_out",
    "pause": "debug_break",
    "stop_debugging": "debug_stop",
    "detach_debugger": "detach_process",
    "set_watchpoint": "set_data_breakpoint",
    "clear_breakpoint": "delete_breakpoint",
    "select_stack_frame": "switch_stack_frame",
    "select_thread": "switch_thread",
    "list_debug_processes": "get_debug_processes",
    "select_process": "switch_process",
}

VS_TOOL_DOMAINS: Dict[str, List[str]] = {
    "capability": ["debug_capabilities"],
    "instance": [
        "list_vs_instances", "detect_debug_sessions", "select_vs_instance", "clear_vs_selection",
        "ensure_vs_instance", "close_vs_instance",
        "dispatch_vs_instances", "dispatch_vs_commands",
    ],
    "snapshot": [
        "get_debug_status", "get_callstack", "get_call_stack", "get_locals",
        "evaluate_expression", "evaluate_expressions_batch", "get_threads", "list_threads",
        "get_modules", "get_exception", "get_build_errors", "get_errors_only",
        "get_warnings_only", "get_debug_output", "search_output", "get_full_context",
        "capture_debug_bundle", "crash_triage", "step_and_capture",
    ],
    "native_managed": [
        "resolve_address", "get_module_layout", "capture_native_context",
        "inspect_managed_assembly", "inspect_managed_type", "inspect_managed_method_il",
        "capture_hybrid_context",
    ],
    "control": [
        "get_dispatcher_health", "get_command_status", "cancel_debug_request",
        "attach_process", "get_debug_processes",
        "list_debug_processes", "switch_process", "select_process", "start_debugging",
        "debug_control", "debug_continue", "continue", "continue_and_wait_for_break",
        "continue_and_capture_native", "debug_step_over", "step_over", "debug_step_into",
        "step_into", "debug_step_out", "step_out", "debug_break", "pause", "debug_stop",
        "stop_debugging", "detach_process", "detach_debugger", "debug_run_to_cursor",
        "debug_set_next_statement",
    ],
    "breakpoint": [
        "get_breakpoints", "get_breakpoint_bindings", "continue_and_capture_breakpoint",
        "set_breakpoint", "set_function_breakpoint", "set_data_breakpoint",
        "set_address_breakpoint", "set_watchpoint", "delete_breakpoint", "clear_breakpoint",
        "delete_data_breakpoint",
        "delete_all_breakpoints", "enable_breakpoint", "switch_stack_frame",
        "select_stack_frame", "switch_thread", "select_thread",
    ],
    "memory": [
        "read_memory", "get_disassembly", "get_disassembly_at", "get_registers",
        "get_dump_memory_ranges", "add_watch", "search_memory",
    ],
}

_METHOD_OVERRIDES = {
    "get_command_status": ["get_request_status"],
    "cancel_debug_request": ["cancel_request"],
    "get_debug_output": ["get_output_window"],
    "debug_continue": ["continue"],
    "debug_step_over": ["step_over"],
    "debug_step_into": ["step_into"],
    "debug_step_out": ["step_out"],
    "debug_break": ["break"],
    "debug_stop": ["stop"],
    "detach_process": ["detach"],
    "debug_run_to_cursor": ["run_to_cursor"],
    "debug_set_next_statement": ["set_next_statement"],
}

_COMPOSITE_METHODS = {
    "debug_capabilities": ["ping", "get_instance_info"],
    "list_vs_instances": ["ping", "get_instance_info"],
    "detect_debug_sessions": ["ping", "get_instance_info", "get_debug_status"],
    # Launches a Visual Studio instance locally and can then attach, so the catalog
    # keeps the conservative command annotation (attach_process is a session write)
    # even though the launch itself never reaches the pipe catalog.
    "ensure_vs_instance": ["ping", "get_instance_info", "attach_process"],
    "select_vs_instance": ["ping", "get_instance_info"],
    "crash_triage": ["capture_debug_bundle", "get_disassembly", "resolve_address", "read_memory"],
    "debug_control": ["continue", "step_over", "step_into", "step_out", "break", "stop", "detach"],
}

# The multi-target dispatch tools take the backend method as a parameter, so the
# catalog cannot know it statically. Splitting them by method kind is what keeps
# the published annotations and requiredCapabilities honest: the query tool
# really is read-only, and only the command tool carries the destructive set.
_KIND_FILTERED_TOOLS = {
    "dispatch_vs_instances": "query",
    "dispatch_vs_commands": "command",
}

_PARAMETER_CONDITIONAL_ROUTES = {
    "capture_debug_bundle": {
        "defaultMethods": ["capture_debug_bundle_safe"],
        "alternativeMethods": ["capture_debug_bundle"],
        "whenAny": [
            {"parameter": "expressions", "presentNonEmpty": True},
            {"parameter": "includeLocals", "equals": True},
            {"parameter": "includeRegisters", "equals": True},
            {"parameter": "includeException", "equals": True},
        ],
    },
    "continue_and_wait_for_break": {
        "defaultMethods": ["continue_and_wait_for_break_safe"],
        "alternativeMethods": ["continue_and_wait_for_break"],
        "whenAny": [
            {"parameter": "expressions", "presentNonEmpty": True},
            {"parameter": "includeLocals", "equals": True},
            {"parameter": "includeRegisters", "equals": True},
            {"parameter": "includeException", "equals": True},
        ],
    },
    "step_and_capture": {
        "defaultMethods": ["step_over", "step_into", "step_out", "capture_debug_bundle_safe"],
        "alternativeMethods": ["capture_debug_bundle"],
        "whenAny": [
            {"parameter": "expressions", "presentNonEmpty": True},
            {"parameter": "includeLocals", "equals": True},
            {"parameter": "includeRegisters", "equals": True},
            {"parameter": "includeException", "equals": True},
        ],
    },
}


_METHOD_MANIFEST_PATH = Path(__file__).with_name("debug_method_manifest.json")
DEBUG_METHOD_CATALOG_HASH = hashlib.sha256(_METHOD_MANIFEST_PATH.read_bytes()).hexdigest()


def _method_manifest() -> Dict[str, object]:
    payload = json.loads(_METHOD_MANIFEST_PATH.read_text(encoding="utf-8"))
    if payload.get("protocol") != "aidb/2" or not isinstance(payload.get("methods"), dict):
        raise RuntimeError("Invalid Visual Studio debug method manifest")
    return payload["methods"]


DEBUG_METHOD_MANIFEST = _method_manifest()


def backend_method_kind(method: str) -> str:
    entry = DEBUG_METHOD_MANIFEST.get(method)
    return str(entry["kind"]) if entry is not None else "query"


def backend_method_session_refresh(method: str) -> bool:
    entry = DEBUG_METHOD_MANIFEST.get(method)
    return bool(entry and entry.get("sessionRefresh") is True)


def backend_method_capability(method: str) -> str:
    return backend_method_capabilities(method)[0]


def backend_method_capabilities(
    method: str,
    params: Optional[Dict[str, object]] = None,
) -> Tuple[str, ...]:
    entry = DEBUG_METHOD_MANIFEST.get(method)
    if entry is None:
        return ("debug.read",)
    capabilities = list(entry["capabilities"])
    for condition in entry.get("conditionalCapabilities", []):
        actual = params.get(condition["parameter"]) if params else None
        matches = (
            isinstance(actual, str) and bool(actual.strip())
            if condition.get("presentNonBlank") is True
            else params is not None
            and type(actual) is type(condition["equals"])
            and actual == condition["equals"]
        )
        if matches and condition["capability"] not in capabilities:
            capabilities.append(condition["capability"])
    return tuple(capabilities)


def all_vs_capabilities() -> Tuple[str, ...]:
    return tuple(sorted({
        capability
        for method in DEBUG_METHOD_MANIFEST
        for capability in backend_method_capabilities(method)
    }))


def control_plane_methods() -> frozenset[str]:
    return recovery_plane_methods() | interrupt_pipe_methods()


def recovery_plane_methods() -> frozenset[str]:
    return frozenset(
        name for name, entry in DEBUG_METHOD_MANIFEST.items()
        if entry.get("controlPlane") is True
    )


def interrupt_pipe_methods() -> frozenset[str]:
    return frozenset({"get_debug_status", "break", "stop", "detach"})


def _backend_methods(tool: str, methods: Dict[str, object]) -> List[str]:
    canonical = LEGACY_ALIASES.get(tool, tool)
    kind_filter = _KIND_FILTERED_TOOLS.get(canonical)
    if kind_filter is not None:
        return [
            name
            for name, entry in methods.items()
            if entry.get("kind", "query") == kind_filter
        ]
    conditional_route = _PARAMETER_CONDITIONAL_ROUTES.get(canonical)
    if conditional_route:
        return list(dict.fromkeys(
            conditional_route["defaultMethods"] + conditional_route["alternativeMethods"]
        ))
    if canonical in _COMPOSITE_METHODS:
        return list(_COMPOSITE_METHODS[canonical])
    if canonical in _METHOD_OVERRIDES:
        return list(_METHOD_OVERRIDES[canonical])
    return [canonical] if canonical in methods else []


def backend_methods_for_tool(tool: str) -> Tuple[str, ...]:
    return tuple(_backend_methods(tool, DEBUG_METHOD_MANIFEST))


def tool_annotation_values(tool: str) -> Dict[str, bool]:
    """Conservative MCP annotations covering every parameter-dependent route."""
    methods = backend_methods_for_tool(tool)
    entries = [DEBUG_METHOD_MANIFEST[method] for method in methods]
    may_evaluate = any(
        "debug.evaluate.unsafe" in entry.get("capabilities", []) or
        any(condition.get("capability") == "debug.evaluate.unsafe"
            for condition in entry.get("conditionalCapabilities", []))
        for entry in entries
    )
    has_command = any(entry.get("kind") == "command" for entry in entries)
    local_selection_write = tool in {"select_vs_instance", "clear_vs_selection"}
    # Closing an MCP-launched Visual Studio process is a local, destructive write that
    # reaches no backend method at all, so it needs its own annotation instead of the
    # read-only default that a method-less tool would otherwise get.
    local_process_lifecycle = tool == "close_vs_instance"
    read_only = (
        not has_command
        and not may_evaluate
        and not local_selection_write
        and not local_process_lifecycle
    )
    idempotent = read_only or tool in {
        "select_vs_instance", "clear_vs_selection", "cancel_debug_request",
    }
    return {
        "readOnlyHint": read_only,
        "destructiveHint": has_command or may_evaluate or local_process_lifecycle,
        "idempotentHint": idempotent,
        "openWorldHint": bool(methods),
    }


def tool_registry_summary() -> Dict[str, object]:
    methods = DEBUG_METHOD_MANIFEST
    expose_aliases = os.environ.get("AIDB_ENABLE_LEGACY_ALIASES") == "1"
    domains = {
        domain: [name for name in names if expose_aliases or name not in LEGACY_ALIASES]
        for domain, names in VS_TOOL_DOMAINS.items()
    }
    tools = [tool for names in domains.values() for tool in names]
    domain_by_tool = {tool: domain for domain, names in domains.items() for tool in names}
    support = {}
    for tool in sorted(tools):
        backend_methods = _backend_methods(tool, methods)
        route = _PARAMETER_CONDITIONAL_ROUTES.get(LEGACY_ALIASES.get(tool, tool))
        required_methods = route["defaultMethods"] if route else backend_methods
        required_capabilities = sorted({
            capability
            for method in required_methods
            for capability in methods[method].get("capabilities", [])
        })
        conditional_capabilities = [
            dict(condition, backendMethod=method)
            for method in backend_methods
            for condition in methods[method].get("conditionalCapabilities", [])
        ]
        if route:
            alternative_capabilities = sorted({
                capability
                for method in route["alternativeMethods"]
                for capability in methods[method].get("capabilities", [])
                if capability not in required_capabilities
            })
            conditional_capabilities.extend({
                "capability": capability,
                "whenAny": route["whenAny"],
                "backendMethods": route["alternativeMethods"],
            } for capability in alternative_capabilities)
        support[tool] = {
            "domain": domain_by_tool[tool],
            "backendMethods": backend_methods,
            "requiredCapabilities": required_capabilities,
            "conditionalCapabilities": conditional_capabilities,
            "defaultBackendMethods": list(route["defaultMethods"]) if route else backend_methods,
            "alternativeBackendMethods": list(route["alternativeMethods"]) if route else [],
            "parameterConditionalRouting": bool(route),
            "defaultInvocationAnnotations": (
                _annotation_values_for_methods(route["defaultMethods"], tool)
                if route else tool_annotation_values(tool)
            ),
            "routableBackends": ["vs"],
            "supportedBackends": ["vs"],
            "unsupportedBackends": [],
            "preferredIde": "vs",
            "aliasFor": LEGACY_ALIASES.get(tool),
            "implementation": "parameter_conditional" if route else "backend_composite" if len(backend_methods) > 1 else (
                "local_only" if not backend_methods else "backend_direct"
            ),
        }
    return {
        "scope": "visual_studio",
        "legacyAliasesEnabled": expose_aliases,
        "domains": domains,
        "vsOnlyTools": sorted(tools),
        "aliases": dict(sorted(LEGACY_ALIASES.items())) if expose_aliases else {},
        "toolSupport": support,
        "methodBackends": {name: ["vs"] for name in sorted(methods)},
    }


def _annotation_values_for_methods(methods: List[str], tool: str) -> Dict[str, bool]:
    entries = [DEBUG_METHOD_MANIFEST[method] for method in methods]
    may_evaluate = any(
        "debug.evaluate.unsafe" in entry.get("capabilities", []) or
        any(condition.get("capability") == "debug.evaluate.unsafe"
            for condition in entry.get("conditionalCapabilities", []))
        for entry in entries
    )
    has_command = any(entry.get("kind") == "command" for entry in entries)
    local_selection_write = tool in {"select_vs_instance", "clear_vs_selection"}
    read_only = not has_command and not may_evaluate and not local_selection_write
    return {
        "readOnlyHint": read_only,
        "destructiveHint": has_command or may_evaluate,
        "idempotentHint": read_only or tool in {
            "select_vs_instance", "clear_vs_selection", "cancel_debug_request",
        },
        "openWorldHint": bool(methods),
    }
