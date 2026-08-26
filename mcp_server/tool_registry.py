"""Declarative registry for AIDebugger MCP tools and backend methods."""

from __future__ import annotations

from dataclasses import dataclass
import json
import os
from pathlib import Path
from typing import Dict, Iterable, List, Optional, Sequence, Tuple

VS = "vs"
RIDER = "rider"
COMMON_BACKENDS = (VS, RIDER)
VS_ONLY_BACKENDS = (VS,)

DOMAIN_MODULES: Dict[str, str] = {
    "capability": "tool_modules.capability_tools",
    "instance": "tool_modules.instance_tools",
    "snapshot": "tool_modules.snapshot_tools",
    "native_managed": "tool_modules.native_managed_tools",
    "control": "tool_modules.control_tools",
    "breakpoint": "tool_modules.breakpoint_tools",
    "memory": "tool_modules.memory_tools",
}

COMMON_BACKEND_METHODS = frozenset({
    "add_watch",
    "break",
    "capture_debug_bundle",
    "continue",
    "continue_and_wait_for_break",
    "delete_all_breakpoints",
    "delete_breakpoint",
    "detach",
    "enable_breakpoint",
    "evaluate_expression",
    "evaluate_expressions_batch",
    "get_breakpoints",
    "get_build_errors",
    "get_callstack",
    "get_debug_status",
    "get_dispatcher_health",
    "get_disassembly",
    "get_disassembly_at",
    "get_errors_only",
    "get_exception",
    "get_full_context",
    "get_instance_info",
    "get_locals",
    "get_modules",
    "get_output_window",
    "get_request_status",
    "get_registers",
    "get_threads",
    "get_warnings_only",
    "ping",
    "read_memory",
    "run_to_cursor",
    "search_memory",
    "search_output",
    "set_breakpoint",
    "set_function_breakpoint",
    "set_next_statement",
    "step_into",
    "step_out",
    "step_over",
    "stop",
    "switch_stack_frame",
    "switch_thread",
    "cancel_request",
})

VS_ONLY_BACKEND_METHODS = frozenset({
    "attach_process",
    "capture_debug_bundle_safe",
    "capture_hybrid_context",
    "capture_native_context",
    "clear_breakpoint",
    "continue_and_capture_breakpoint",
    "continue_and_capture_native",
    "continue_and_wait_for_break_safe",
    "get_breakpoint_bindings",
    "get_debug_processes",
    "get_dump_memory_ranges",
    "get_module_layout",
    "inspect_managed_assembly",
    "inspect_managed_method_il",
    "inspect_managed_type",
    "resolve_address",
    "delete_data_breakpoint",
    "set_address_breakpoint",
    "set_data_breakpoint",
    "set_watchpoint",
    "start_debugging",
    "switch_process",
})

METHOD_BACKENDS: Dict[str, Tuple[str, ...]] = {
    **{method: COMMON_BACKENDS for method in COMMON_BACKEND_METHODS},
    **{method: VS_ONLY_BACKENDS for method in VS_ONLY_BACKEND_METHODS},
}

def _load_debug_method_manifest() -> Dict[str, Dict[str, object]]:
    path = Path(__file__).with_name("debug_method_manifest.json")
    payload = json.loads(path.read_text(encoding="utf-8"))
    if payload.get("protocol") != "aidb/2" or not isinstance(payload.get("methods"), dict):
        raise RuntimeError("Invalid AIDebugger debug method manifest")
    methods = payload["methods"]
    if set(methods) != set(METHOD_BACKENDS):
        missing = sorted(set(METHOD_BACKENDS) - set(methods))
        extra = sorted(set(methods) - set(METHOD_BACKENDS))
        raise RuntimeError(f"Debug method manifest mismatch (missing={missing}, extra={extra})")
    for name, entry in methods.items():
        if entry.get("kind") not in {"query", "command"}:
            raise RuntimeError(f"Debug method manifest has invalid kind for {name}")
        capabilities = entry.get("capabilities")
        if not isinstance(capabilities, list) or not capabilities or not all(
            isinstance(value, str) and value for value in capabilities
        ) or len(capabilities) != len(set(capabilities)):
            raise RuntimeError(f"Debug method manifest has invalid capabilities for {name}")
        conditions = entry.get("conditionalCapabilities", [])
        if not isinstance(conditions, list):
            raise RuntimeError(f"Debug method manifest has invalid conditional capabilities for {name}")
        for condition in conditions:
            if (
                not isinstance(condition, dict)
                or not isinstance(condition.get("parameter"), str)
                or not condition["parameter"]
                or not isinstance(condition.get("capability"), str)
                or not condition["capability"]
                or (("equals" in condition) == (condition.get("presentNonBlank") is True))
            ):
                raise RuntimeError(f"Debug method manifest has invalid conditional capability for {name}")
    return methods


DEBUG_METHOD_MANIFEST = _load_debug_method_manifest()
MUTATING_BACKEND_METHODS = frozenset(
    name for name, entry in DEBUG_METHOD_MANIFEST.items() if entry["kind"] == "command"
)


def backend_method_kind(method: str) -> str:
    """Return command for methods that must never be transport-retried."""
    entry = DEBUG_METHOD_MANIFEST.get(method)
    return str(entry["kind"]) if entry is not None else "query"


def backend_method_session_refresh(method: str) -> bool:
    """Return whether a method may legitimately advance the pinned debug context."""
    entry = DEBUG_METHOD_MANIFEST.get(method)
    return bool(entry and entry.get("sessionRefresh") is True)


def backend_method_capability(method: str) -> str:
    """Return the AIDB/2 capability that must be declared for a backend method."""
    return backend_method_capabilities(method)[0]


def backend_method_capabilities(method: str, params: Optional[Dict[str, object]] = None) -> Tuple[str, ...]:
    """Return every capability required by a backend method, in wire-priority order."""
    entry = DEBUG_METHOD_MANIFEST.get(method)
    if entry is None:
        return ("debug.read",)
    capabilities = list(entry["capabilities"])
    for condition in entry.get("conditionalCapabilities", []):
        actual = params.get(condition["parameter"]) if params else None
        matches = (
            isinstance(actual, str) and bool(actual.strip())
            if condition.get("presentNonBlank") is True
            else params is not None and type(actual) is type(condition["equals"]) and actual == condition["equals"]
        )
        if matches and condition["capability"] not in capabilities:
            capabilities.append(condition["capability"])
    return tuple(capabilities)


def all_vs_capabilities() -> Tuple[str, ...]:
    """Return the exact capability set required to expose every VS backend method."""
    return tuple(sorted({
        capability
        for method, backends in METHOD_BACKENDS.items()
        if VS in backends
        for capability in backend_method_capabilities(method)
    }))


@dataclass(frozen=True)
class ToolSpec:
    name: str
    domain: str
    backend_methods: Tuple[str, ...] = ()
    backends: Tuple[str, ...] = COMMON_BACKENDS
    preferred_ide: Optional[str] = None
    timeout_class: str = "default"
    alias_for: Optional[str] = None
    unsupported_backends: Tuple[str, ...] = ()


def _backends_for_methods(methods: Sequence[str]) -> Tuple[str, ...]:
    if not methods:
        return COMMON_BACKENDS
    resolved = [METHOD_BACKENDS.get(method, COMMON_BACKENDS) for method in methods]
    if all(backends == VS_ONLY_BACKENDS for backends in resolved):
        return VS_ONLY_BACKENDS
    return COMMON_BACKENDS


def _preferred_for_methods(methods: Sequence[str]) -> Optional[str]:
    return VS if methods and _backends_for_methods(methods) == VS_ONLY_BACKENDS else None


def _spec(
    name: str,
    domain: str,
    *backend_methods: str,
    timeout_class: str = "default",
    alias_for: Optional[str] = None,
    preferred_ide: Optional[str] = None,
    unsupported_backends: Tuple[str, ...] = (),
) -> ToolSpec:
    methods = tuple(dict.fromkeys(backend_methods))
    preferred = preferred_ide if preferred_ide is not None else _preferred_for_methods(methods)
    return ToolSpec(
        name=name,
        domain=domain,
        backend_methods=methods,
        backends=_backends_for_methods(methods),
        preferred_ide=preferred,
        timeout_class=timeout_class,
        alias_for=alias_for,
        unsupported_backends=unsupported_backends,
    )


TOOLS: Dict[str, ToolSpec] = {
    # Capability / discovery
    "debug_capabilities": _spec("debug_capabilities", "capability"),
    "get_dispatcher_health": _spec(
        "get_dispatcher_health", "control", "get_dispatcher_health"),
    "get_command_status": _spec("get_command_status", "control", "get_request_status"),
    "cancel_debug_request": _spec("cancel_debug_request", "control", "cancel_request"),

    # Instance management
    "list_vs_instances": _spec("list_vs_instances", "instance", "ping", "get_instance_info"),
    "list_ide_instances": _spec("list_ide_instances", "instance", "ping", "get_instance_info"),
    "detect_debug_sessions": _spec("detect_debug_sessions", "instance", "ping", "get_instance_info", "get_debug_status"),
    "select_ide_instance": _spec("select_ide_instance", "instance", "ping", "get_instance_info"),
    "clear_ide_selection": _spec("clear_ide_selection", "instance"),
    "select_vs_instance": _spec("select_vs_instance", "instance", "ping", "get_instance_info"),
    "clear_vs_selection": _spec("clear_vs_selection", "instance"),

    # Snapshot and inspection
    "get_debug_status": _spec("get_debug_status", "snapshot", "get_debug_status"),
    "get_callstack": _spec("get_callstack", "snapshot", "get_callstack"),
    "get_call_stack": _spec("get_call_stack", "snapshot", "get_callstack", alias_for="get_callstack"),
    "get_locals": _spec("get_locals", "snapshot", "get_locals"),
    "evaluate_expression": _spec("evaluate_expression", "snapshot", "evaluate_expression", timeout_class="evaluation"),
    "evaluate_expressions_batch": _spec("evaluate_expressions_batch", "snapshot", "evaluate_expressions_batch", timeout_class="batch_evaluation"),
    "get_threads": _spec("get_threads", "snapshot", "get_threads", timeout_class="threads"),
    "list_threads": _spec("list_threads", "snapshot", "get_threads", alias_for="get_threads", timeout_class="threads"),
    "get_modules": _spec("get_modules", "snapshot", "get_modules", timeout_class="modules"),
    "get_exception": _spec("get_exception", "snapshot", "get_exception", timeout_class="exception"),
    "get_build_errors": _spec("get_build_errors", "snapshot", "get_build_errors"),
    "get_errors_only": _spec("get_errors_only", "snapshot", "get_errors_only"),
    "get_warnings_only": _spec("get_warnings_only", "snapshot", "get_warnings_only"),
    "get_debug_output": _spec("get_debug_output", "snapshot", "get_output_window", timeout_class="output"),
    "search_output": _spec("search_output", "snapshot", "search_output", timeout_class="output"),
    "get_full_context": _spec("get_full_context", "snapshot", "get_full_context", timeout_class="debug_bundle"),
    "capture_debug_bundle": _spec("capture_debug_bundle", "snapshot", "capture_debug_bundle", timeout_class="debug_bundle"),
    "crash_triage": _spec("crash_triage", "snapshot", "capture_debug_bundle", "get_disassembly", "read_memory", timeout_class="debug_bundle"),
    "step_and_capture": _spec("step_and_capture", "snapshot", "step_over", "step_into", "step_out", "capture_debug_bundle", timeout_class="debug_bundle"),

    # Native / managed VS-only tools
    "resolve_address": _spec("resolve_address", "native_managed", "resolve_address", timeout_class="native_context"),
    "get_module_layout": _spec("get_module_layout", "native_managed", "get_module_layout", timeout_class="native_context"),
    "capture_native_context": _spec("capture_native_context", "native_managed", "capture_native_context", timeout_class="native_context"),
    "inspect_managed_assembly": _spec("inspect_managed_assembly", "native_managed", "inspect_managed_assembly", timeout_class="managed_inspection"),
    "inspect_managed_type": _spec("inspect_managed_type", "native_managed", "inspect_managed_type", timeout_class="managed_inspection"),
    "inspect_managed_method_il": _spec("inspect_managed_method_il", "native_managed", "inspect_managed_method_il", timeout_class="managed_inspection"),
    "capture_hybrid_context": _spec("capture_hybrid_context", "native_managed", "capture_hybrid_context", timeout_class="hybrid_context"),

    # Execution control
    "attach_process": _spec("attach_process", "control", "attach_process", timeout_class="control"),
    "get_debug_processes": _spec("get_debug_processes", "control", "get_debug_processes", timeout_class="control"),
    "list_debug_processes": _spec("list_debug_processes", "control", "get_debug_processes", timeout_class="control", alias_for="get_debug_processes"),
    "switch_process": _spec("switch_process", "control", "switch_process", timeout_class="control"),
    "select_process": _spec("select_process", "control", "switch_process", timeout_class="control", alias_for="switch_process"),
    "start_debugging": _spec("start_debugging", "control", "start_debugging", timeout_class="start_debugging"),
    "debug_control": _spec("debug_control", "control", "continue", "step_over", "step_into", "step_out", "break", "stop", "detach"),
    "debug_continue": _spec("debug_continue", "control", "continue"),
    "continue": _spec("continue", "control", "continue", alias_for="debug_continue"),
    "continue_and_wait_for_break": _spec("continue_and_wait_for_break", "control", "continue_and_wait_for_break", timeout_class="continue_wait"),
    "continue_and_capture_native": _spec("continue_and_capture_native", "control", "continue_and_capture_native", timeout_class="native_context"),
    "debug_step_over": _spec("debug_step_over", "control", "step_over"),
    "step_over": _spec("step_over", "control", "step_over", alias_for="debug_step_over"),
    "debug_step_into": _spec("debug_step_into", "control", "step_into"),
    "step_into": _spec("step_into", "control", "step_into", alias_for="debug_step_into"),
    "debug_step_out": _spec("debug_step_out", "control", "step_out"),
    "step_out": _spec("step_out", "control", "step_out", alias_for="debug_step_out"),
    "debug_break": _spec("debug_break", "control", "break"),
    "pause": _spec("pause", "control", "break", alias_for="debug_break"),
    "debug_stop": _spec("debug_stop", "control", "stop"),
    "stop_debugging": _spec("stop_debugging", "control", "stop", alias_for="debug_stop"),
    "detach_process": _spec("detach_process", "control", "detach", unsupported_backends=(RIDER,)),
    "detach_debugger": _spec("detach_debugger", "control", "detach", alias_for="detach_process", unsupported_backends=(RIDER,)),
    "debug_run_to_cursor": _spec("debug_run_to_cursor", "control", "run_to_cursor"),
    "debug_set_next_statement": _spec("debug_set_next_statement", "control", "set_next_statement"),

    # Breakpoints and navigation
    "get_breakpoints": _spec("get_breakpoints", "breakpoint", "get_breakpoints"),
    "get_breakpoint_bindings": _spec("get_breakpoint_bindings", "breakpoint", "get_breakpoint_bindings"),
    "continue_and_capture_breakpoint": _spec("continue_and_capture_breakpoint", "breakpoint", "continue_and_capture_breakpoint", timeout_class="hybrid_context"),
    "set_breakpoint": _spec("set_breakpoint", "breakpoint", "set_breakpoint"),
    "set_function_breakpoint": _spec("set_function_breakpoint", "breakpoint", "set_function_breakpoint"),
    "set_data_breakpoint": _spec("set_data_breakpoint", "breakpoint", "set_data_breakpoint"),
    "set_address_breakpoint": _spec("set_address_breakpoint", "breakpoint", "set_address_breakpoint"),
    "set_watchpoint": _spec("set_watchpoint", "breakpoint", "set_data_breakpoint", alias_for="set_data_breakpoint"),
    "delete_breakpoint": _spec("delete_breakpoint", "breakpoint", "delete_breakpoint"),
    "delete_data_breakpoint": _spec("delete_data_breakpoint", "breakpoint", "delete_data_breakpoint"),
    "clear_breakpoint": _spec("clear_breakpoint", "breakpoint", "delete_breakpoint", alias_for="delete_breakpoint"),
    "delete_all_breakpoints": _spec("delete_all_breakpoints", "breakpoint", "delete_all_breakpoints"),
    "enable_breakpoint": _spec("enable_breakpoint", "breakpoint", "enable_breakpoint"),
    "switch_stack_frame": _spec("switch_stack_frame", "breakpoint", "switch_stack_frame"),
    "select_stack_frame": _spec("select_stack_frame", "breakpoint", "switch_stack_frame", alias_for="switch_stack_frame"),
    "switch_thread": _spec("switch_thread", "breakpoint", "switch_thread"),
    "select_thread": _spec("select_thread", "breakpoint", "switch_thread", alias_for="switch_thread"),

    # Memory / disassembly / registers / watches
    "read_memory": _spec("read_memory", "memory", "read_memory", timeout_class="memory"),
    "get_dump_memory_ranges": _spec("get_dump_memory_ranges", "memory", "get_dump_memory_ranges", timeout_class="memory"),
    "get_disassembly": _spec("get_disassembly", "memory", "get_disassembly", timeout_class="disassembly"),
    "get_disassembly_at": _spec("get_disassembly_at", "memory", "get_disassembly_at", timeout_class="disassembly"),
    "get_registers": _spec("get_registers", "memory", "get_registers"),
    "add_watch": _spec("add_watch", "memory", "add_watch"),
    "search_memory": _spec("search_memory", "memory", "search_memory", timeout_class="memory"),
}


def preferred_ide_for_backend_method(method: str) -> Optional[str]:
    return VS if METHOD_BACKENDS.get(method) == VS_ONLY_BACKENDS else None


def supported_backends_for_tool(spec: ToolSpec) -> Tuple[str, ...]:
    unsupported = set(spec.unsupported_backends)
    return tuple(backend for backend in spec.backends if backend not in unsupported)


def tool_registry_summary() -> Dict[str, object]:
    preferred = (os.environ.get("AIDB_PREFERRED_IDE") or "").strip().lower()
    expose_aliases = os.environ.get("AIDB_ENABLE_LEGACY_ALIASES") == "1"
    suppress_aliases = preferred in {"vs", "visualstudio", "visual-studio", "visual_studio"} and not expose_aliases
    domains: Dict[str, List[str]] = {domain: [] for domain in DOMAIN_MODULES}
    vs_only: List[str] = []
    aliases: Dict[str, str] = {}
    tool_support: Dict[str, Dict[str, object]] = {}
    for name, spec in sorted(TOOLS.items()):
        if suppress_aliases and spec.alias_for:
            continue
        domains.setdefault(spec.domain, []).append(name)
        supported_backends = supported_backends_for_tool(spec)
        if supported_backends == VS_ONLY_BACKENDS:
            vs_only.append(name)
        if spec.alias_for:
            aliases[name] = spec.alias_for
        tool_support[name] = {
            "domain": spec.domain,
            "backendMethods": list(spec.backend_methods),
            "routableBackends": list(spec.backends),
            "supportedBackends": list(supported_backends),
            "unsupportedBackends": list(spec.unsupported_backends),
            "preferredIde": spec.preferred_ide,
            "timeoutClass": spec.timeout_class,
            "aliasFor": spec.alias_for,
            "requiredCapabilities": sorted({
                capability
                for method in spec.backend_methods
                for capability in backend_method_capabilities(method)
            }),
            "conditionalCapabilities": [
                dict(condition, backendMethod=method)
                for method in spec.backend_methods
                for condition in DEBUG_METHOD_MANIFEST.get(method, {}).get("conditionalCapabilities", [])
            ],
            "implementation": "backend_composite" if len(spec.backend_methods) > 1 else (
                "local_only" if not spec.backend_methods else "backend_direct"
            ),
        }
    return {
        "legacyAliasesEnabled": not suppress_aliases,
        "domains": domains,
        "vsOnlyTools": vs_only,
        "aliases": aliases,
        "toolSupport": tool_support,
        "methodBackends": {method: list(backends) for method, backends in sorted(METHOD_BACKENDS.items())},
    }


def validate_tool_specs(vs_methods: Iterable[str], rider_methods: Iterable[str], tool_names: Iterable[str]) -> List[str]:
    errors: List[str] = []
    vs_set = set(vs_methods)
    rider_set = set(rider_methods)
    tool_set = set(tool_names)

    missing_specs = sorted(tool_set - set(TOOLS))
    if missing_specs:
        errors.append("missing tool specs: " + ", ".join(missing_specs))

    stale_specs = sorted(set(TOOLS) - tool_set)
    if stale_specs:
        errors.append("stale tool specs: " + ", ".join(stale_specs))

    for name, spec in sorted(TOOLS.items()):
        if spec.domain not in DOMAIN_MODULES:
            errors.append(f"{name}: unknown domain {spec.domain}")
        if spec.alias_for and spec.alias_for not in TOOLS:
            errors.append(f"{name}: alias target {spec.alias_for} is not registered")
        if spec.alias_for and not spec.backend_methods:
            errors.append(f"{name}: alias must declare backend_methods from its target")
        for backend in spec.unsupported_backends:
            if backend not in spec.backends:
                errors.append(f"{name}: unsupported backend {backend} is not routable")
        for method in spec.backend_methods:
            backends = METHOD_BACKENDS.get(method)
            if backends is None:
                errors.append(f"{name}: backend method {method} is not in METHOD_BACKENDS")
                continue
            if VS in backends and method not in vs_set:
                errors.append(f"{name}: backend method {method} is missing in VS")
            if RIDER in backends and method not in rider_set:
                errors.append(f"{name}: backend method {method} is missing in Rider")
            if backends == VS_ONLY_BACKENDS and spec.preferred_ide != VS:
                errors.append(f"{name}: VS-only method {method} requires preferred_ide='vs'")

    dispatched_methods = vs_set | rider_set
    missing_backend_specs = sorted(dispatched_methods - set(METHOD_BACKENDS))
    if missing_backend_specs:
        errors.append("backend methods missing from METHOD_BACKENDS: " + ", ".join(missing_backend_specs))

    stale_backend_specs = sorted(
        method
        for method, backends in METHOD_BACKENDS.items()
        if (VS in backends and method not in vs_set)
        or (RIDER in backends and method not in rider_set)
    )
    if stale_backend_specs:
        errors.append("METHOD_BACKENDS entries missing backend dispatch: " + ", ".join(stale_backend_specs))
    return errors
