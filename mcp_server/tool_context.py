"""Shared helpers for AIDebugger MCP tool modules."""

import asyncio
import json
import math
import os
import re
from typing import Any, Dict, List, Optional

try:
    from . import vs_bridge
    from .tool_runtime import backend as ide_bridge
except ImportError:
    import vs_bridge
    from tool_runtime import backend as ide_bridge

from mcp.server.fastmcp.exceptions import ToolError

__all__ = [
    "BATCH_EVALUATION_TIMEOUT_SECONDS",
    "EVALUATION_TIMEOUT_SECONDS",
    "SAFE_DEBUG_BUNDLE_TIMEOUT_SECONDS",
    "DEBUG_BUNDLE_TIMEOUT_SECONDS",
    "THREADS_TIMEOUT_SECONDS",
    "MODULES_TIMEOUT_SECONDS",
    "EXCEPTION_TIMEOUT_SECONDS",
    "OUTPUT_TIMEOUT_SECONDS",
    "START_DEBUGGING_TIMEOUT_SECONDS",
    "MEMORY_TIMEOUT_SECONDS",
    "DISASSEMBLY_TIMEOUT_SECONDS",
    "NATIVE_CONTEXT_TIMEOUT_SECONDS",
    "MANAGED_INSPECTION_TIMEOUT_SECONDS",
    "HYBRID_CONTEXT_TIMEOUT_SECONDS",
    "MAX_TOOL_TIMEOUT_SECONDS",
    "MAX_THREAD_BUDGET_SECONDS",
    "MAX_MEMORY_SEARCH_CHUNK_SIZE",
    "MAX_MEMORY_SEARCH_SIZE",
    "invalid_argument",
    "clamp_tool_timeout",
    "validate_int_range",
    "validate_enum",
    "validate_nonempty_string",
    "validate_address_literal",
    "validate_address_breakpoint_target",
    "tool_timeout_for_budget",
    "remaining_tool_timeout",
    "add_thread_capture_params",
    "add_bounded_collection_params",
    "wait_for_debugging_to_start",
    "format_result",
    "use_vs_safe_backend",
    "handle_ide_error",
]

BATCH_EVALUATION_TIMEOUT_SECONDS = 45.0
EVALUATION_TIMEOUT_SECONDS = 30.0
SAFE_DEBUG_BUNDLE_TIMEOUT_SECONDS = 20.0
DEBUG_BUNDLE_TIMEOUT_SECONDS = 60.0
THREADS_TIMEOUT_SECONDS = 30.0
MODULES_TIMEOUT_SECONDS = 45.0
EXCEPTION_TIMEOUT_SECONDS = 20.0
OUTPUT_TIMEOUT_SECONDS = 20.0
START_DEBUGGING_TIMEOUT_SECONDS = 60.0
MEMORY_TIMEOUT_SECONDS = 20.0
DISASSEMBLY_TIMEOUT_SECONDS = 20.0
NATIVE_CONTEXT_TIMEOUT_SECONDS = 45.0
MANAGED_INSPECTION_TIMEOUT_SECONDS = 75.0
HYBRID_CONTEXT_TIMEOUT_SECONDS = 135.0
MAX_TOOL_TIMEOUT_SECONDS = 300.0
MAX_THREAD_BUDGET_SECONDS = 115.0
MAX_MEMORY_SEARCH_CHUNK_SIZE = 0x100000
MAX_MEMORY_SEARCH_SIZE = 0x40000000

def _invalid_argument(message: str) -> ToolError:
    return ToolError(json.dumps({
        "code": "INVALID_ARGUMENT",
        "message": message,
        "retryable": False,
        "outcomeUnknown": False,
    }, ensure_ascii=False, separators=(",", ":")))


def invalid_argument(message: str) -> ToolError:
    """Create the standard public invalid-argument tool error."""
    return _invalid_argument(message)


def clamp_tool_timeout(timeout_seconds: Optional[float], default_seconds: float) -> float:
    if timeout_seconds is None:
        return default_seconds
    try:
        value = float(timeout_seconds)
    except (TypeError, ValueError) as exc:
        raise _invalid_argument("timeout_seconds must be a number") from exc
    if not math.isfinite(value) or value < 1.0 or value > MAX_TOOL_TIMEOUT_SECONDS:
        raise _invalid_argument(
            f"timeout_seconds must be between 1 and {MAX_TOOL_TIMEOUT_SECONDS:g}"
        )
    return value

def validate_int_range(name: str, value: int, minimum: int, maximum: int) -> int:
    if isinstance(value, bool) or not isinstance(value, int):
        raise _invalid_argument(f"{name} must be an integer")
    if value < minimum or value > maximum:
        raise _invalid_argument(f"{name} must be between {minimum} and {maximum}")
    return value


def validate_enum(name: str, value: str, allowed: tuple[str, ...]) -> str:
    if not isinstance(value, str) or value not in allowed:
        choices = ", ".join(allowed)
        raise _invalid_argument(f"{name} must be one of: {choices}")
    return value


def validate_nonempty_string(name: str, value: str, maximum: int = 512) -> str:
    if not isinstance(value, str):
        raise _invalid_argument(f"{name} must be a string")
    trimmed = value.strip()
    if not trimmed or len(trimmed) > maximum:
        raise _invalid_argument(f"{name} must be between 1 and {maximum} characters")
    return trimmed


def validate_address_literal(name: str, value: str) -> str:
    if not isinstance(value, str):
        raise _invalid_argument(f"{name} must be a string address literal")
    trimmed = value.strip()
    if not trimmed or len(trimmed) > 64:
        raise _invalid_argument(f"{name} must be a non-zero address literal of at most 64 characters")
    try:
        if re.fullmatch(r"0[xX][0-9A-Fa-f]+", trimmed):
            parsed = int(trimmed[2:], 16)
        elif re.fullmatch(r"[0-9A-Fa-f]+[hH]", trimmed):
            parsed = int(trimmed[:-1], 16)
        elif re.fullmatch(r"[0-9]+", trimmed):
            parsed = int(trimmed, 10)
        elif re.fullmatch(r"[0-9A-Fa-f]+", trimmed):
            parsed = int(trimmed, 16)
        else:
            raise ValueError
    except ValueError as exc:
        raise _invalid_argument(
            f"{name} must be an unsigned decimal or hexadecimal address literal"
        ) from exc
    if parsed <= 0 or parsed > 0xFFFFFFFFFFFFFFFF:
        raise _invalid_argument(f"{name} must be between 1 and 0xFFFFFFFFFFFFFFFF")
    return trimmed


def validate_address_breakpoint_target(name: str, value: str) -> str:
    """Accept only an absolute address or a module identifier plus a literal RVA.

    Address breakpoints intentionally do not accept arbitrary debugger expressions;
    callers must resolve those explicitly through the unsafe address-resolution tool.
    """
    if not isinstance(value, str):
        raise _invalid_argument(f"{name} must be an address literal or module+RVA")
    trimmed = value.strip()
    if not trimmed or len(trimmed) > 512 or any(ord(char) < 0x20 for char in trimmed):
        raise _invalid_argument(f"{name} must be a valid address literal or module+RVA")

    try:
        return validate_address_literal(name, trimmed)
    except ToolError:
        pass

    plus_index = trimmed.rfind("+")
    if plus_index <= 0 or plus_index >= len(trimmed) - 1:
        raise _invalid_argument(f"{name} must be an address literal or module+RVA")
    module = trimmed[:plus_index].strip().strip('"')
    rva = trimmed[plus_index + 1:].strip()
    if not module or len(module) > 384:
        raise _invalid_argument(f"{name} contains an invalid module identifier")
    try:
        if re.fullmatch(r"0[xX][0-9A-Fa-f]+", rva):
            parsed_rva = int(rva[2:], 16)
        elif re.fullmatch(r"[0-9A-Fa-f]+[hH]", rva):
            parsed_rva = int(rva[:-1], 16)
        elif re.fullmatch(r"[0-9]+", rva):
            parsed_rva = int(rva, 10)
        elif re.fullmatch(r"[0-9A-Fa-f]+", rva):
            parsed_rva = int(rva, 16)
        else:
            raise ValueError
    except ValueError as exc:
        raise _invalid_argument(f"{name} contains an invalid literal RVA") from exc
    if parsed_rva < 0 or parsed_rva > 0xFFFFFFFFFFFFFFFF:
        raise _invalid_argument(f"{name} RVA must be between 0 and 0xFFFFFFFFFFFFFFFF")
    return trimmed

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
    except (TypeError, ValueError) as exc:
        raise _invalid_argument("budget_ms must be an integer") from exc
    if budget_seconds < 0:
        raise _invalid_argument("budget_ms must be non-negative")
    if budget_seconds <= 0:
        return default_seconds
    return min(MAX_TOOL_TIMEOUT_SECONDS, max(default_seconds, budget_seconds + overhead_seconds))

def remaining_tool_timeout(deadline: float, default_seconds: float) -> Optional[float]:
    remaining = deadline - asyncio.get_running_loop().time()
    if remaining <= 0.1:
        return None
    return min(default_seconds, remaining)

def add_thread_capture_params(
    params: Dict[str, Any],
    thread_include_frames: bool,
    thread_max_frames_per_thread: Optional[int],
    thread_max_nodes: Optional[int],
    thread_budget_ms: Optional[int],
) -> Dict[str, Any]:
    params["threadIncludeFrames"] = thread_include_frames
    if thread_max_frames_per_thread is not None:
        params["threadMaxFramesPerThread"] = validate_int_range(
            "thread_max_frames_per_thread", thread_max_frames_per_thread, 0, 256
        )
    if thread_max_nodes is not None:
        params["threadMaxNodes"] = validate_int_range("thread_max_nodes", thread_max_nodes, 0, 4096)
    if thread_budget_ms is not None:
        params["threadBudgetMs"] = validate_int_range("thread_budget_ms", thread_budget_ms, 0, 120000)
    return params

def add_bounded_collection_params(
    params: Dict[str, Any],
    start_index: Optional[int],
    max_nodes: Optional[int],
    budget_ms: Optional[int],
) -> Dict[str, Any]:
    if start_index is not None:
        params["startIndex"] = validate_int_range("start_index", start_index, 0, 1_000_000)
    if max_nodes is not None:
        params["maxNodes"] = validate_int_range("max_nodes", max_nodes, 0, 4096)
    if budget_ms is not None:
        params["budgetMs"] = validate_int_range("budget_ms", budget_ms, 0, 120000)
    return params

async def wait_for_debugging_to_start(
    result: Any,
    timeout_seconds: float,
    attach: bool,
    target: Optional[Dict[str, Any]] = None,
) -> Any:
    if not isinstance(result, dict):
        return result
    if result.get("processStarted") or result.get("commandIssued"):
        result["retryCommand"] = False
    if not result.get("success"):
        return result
    result["retryCommand"] = False
    if attach is False:
        result["debuggerActivationStatus"] = "not_requested"
        return result

    executable_path = str(result.get("executablePath") or "")
    wait_for_pause = executable_path.lower().endswith((".dmp", ".mdmp", ".hdmp"))
    status = result.get("status")
    if isinstance(status, dict) and status.get("isDebugging") and (not wait_for_pause or status.get("isPaused")):
        result["debuggerActivationStatus"] = "active"
        result["followUpTimedOut"] = False
        return result

    deadline = asyncio.get_running_loop().time() + timeout_seconds
    last_status = status
    last_error = None
    poll_delay = 0.0
    while True:
        remaining = deadline - asyncio.get_running_loop().time()
        if remaining <= 0.1:
            break
        # Probe immediately: the launch response can precede debugger activation
        # by only a few milliseconds. Back off while pending, without a fixed
        # half-second floor or a busy loop against the VS UI thread.
        if poll_delay:
            await asyncio.sleep(min(poll_delay, remaining))
        poll_delay = min(0.25, poll_delay * 2) if poll_delay else 0.05
        remaining = deadline - asyncio.get_running_loop().time()
        if remaining <= 0.1:
            break
        poll_timeout = min(5.0, remaining)
        try:
            last_status = await (
                vs_bridge.call_pipe_exact(target, "get_debug_status", timeout=poll_timeout)
                if target else
                ide_bridge.call("get_debug_status", timeout=poll_timeout)
            )
        except (ide_bridge.IDEBridgeError, vs_bridge.VSBridgeError) as e:
            last_error = str(e)
            if getattr(e, "code", "") in {
                "STALE_TARGET",
                "INSTANCE_REGISTRATION_NOT_FOUND",
                "STALE_DEBUG_SESSION",
            }:
                result["partial"] = True
                result["debuggerActivationStatus"] = "stale_target"
                result["followUpTimedOut"] = False
                result["retryCommand"] = False
                result["lastPollError"] = last_error
                result["lastPollErrorCode"] = getattr(e, "code", "STALE_TARGET")
                result["targetIdentity"] = target
                result["message"] = "The start command succeeded, but the accepting Visual Studio target became unavailable during activation polling. Do not repeat the command."
                return result
            continue

        if isinstance(last_status, dict):
            result["status"] = last_status
            if last_status.get("isDebugging") and (not wait_for_pause or last_status.get("isPaused")):
                result["success"] = True
                result["debuggerActivationStatus"] = "active"
                result["followUpTimedOut"] = False
                result["retryCommand"] = False
                result["message"] = "Started debugging" if not wait_for_pause else "Started dump debugging and paused"
                return result

    result["debuggerActivationStatus"] = "timed_out"
    result["followUpTimedOut"] = True
    result["retryCommand"] = False
    result["partial"] = True
    result["message"] = (
        "The start command succeeded, but activation polling timed out waiting for Visual Studio to pause in dump debugging. Do not repeat the command."
        if wait_for_pause
        else "The start command succeeded, but activation polling timed out waiting for Visual Studio to enter debugging mode. Do not repeat the command."
    )
    if isinstance(last_status, dict):
        result["status"] = last_status
    if last_error:
        result["lastPollError"] = last_error
    return result

def format_result(result: Any) -> Any:
    """Keep backend values typed so FastMCP can expose structuredContent."""
    return result


def use_vs_safe_backend(
    expressions: List[str],
    *,
    include_locals: bool = False,
    include_registers: bool = False,
    include_exception: bool = False,
) -> bool:
    """Use the VS read-only bundle only when no evaluation-like data is requested."""
    preferred = (os.environ.get("AIDB_PREFERRED_IDE") or "").strip().lower()
    return (
        not expressions
        and not include_locals
        and not include_registers
        and not include_exception
        and preferred in {"vs", "visualstudio", "visual-studio", "visual_studio"}
    )

def handle_ide_error(e: Exception, operation: str, *, method: Optional[str] = None) -> None:
    """Raise a typed MCP tool error instead of returning success-shaped error text.

    ``method`` lets a tool that forwards an arbitrary caller-chosen backend
    method (for example multi-target dispatch) report the kind of the method
    that actually failed, rather than a conservative union over every method
    the tool could route.
    """
    code = getattr(e, "code", "IDE_BRIDGE_ERROR")
    try:
        try:
            from .vs_tool_registry import (
                DEBUG_METHOD_MANIFEST,
                backend_methods_for_tool,
            )
        except ImportError:
            from vs_tool_registry import DEBUG_METHOD_MANIFEST, backend_methods_for_tool
        backend_methods = (method,) if method is not None else backend_methods_for_tool(operation)
        if not backend_methods and operation in DEBUG_METHOD_MANIFEST:
            backend_methods = (operation,)
        operation_kind = (
            "command"
            if any(DEBUG_METHOD_MANIFEST[method]["kind"] == "command" for method in backend_methods)
            else "query"
            if backend_methods
            else "unknown"
        )
    except (KeyError, TypeError, ValueError):
        operation_kind = "unknown"

    outcome_unknown = bool(getattr(e, "outcome_unknown", False)) or code == "OUTCOME_UNKNOWN"
    explicitly_not_started = code in {
        "CANCELLED_NOT_STARTED",
        "COMMAND_NOT_STARTED",
        "DEADLINE_EXCEEDED_NOT_STARTED",
    }
    safe_to_reinvoke = explicitly_not_started and operation_kind != "unknown" and not outcome_unknown
    payload = {
        "code": code,
        "message": str(e),
        "operation": operation,
        "operationKind": operation_kind,
        # Retained for MCP clients that only understand the legacy field. It is
        # true solely when the backend proved that no operation started.
        "retryable": safe_to_reinvoke,
        "safeToRetrySameRequest": False,
        "safeToReinvokeWithNewRequestId": safe_to_reinvoke,
        "requiresTargetRefresh": code == "STALE_TARGET",
        "requiresDebugSessionRefresh": code == "STALE_DEBUG_SESSION",
        "requiresContextRefresh": code in {"STALE_DEBUG_SESSION", "STALE_DEBUG_CONTEXT"},
        "outcomeUnknown": outcome_unknown,
    }
    request_id = getattr(e, "request_id", None)
    if request_id:
        payload["requestId"] = request_id
    details = getattr(e, "details", None)
    if isinstance(details, dict):
        payload["commandDetails"] = details
    target_identity = getattr(e, "target_identity", None)
    if isinstance(target_identity, dict):
        payload["targetIdentity"] = target_identity
    raise ToolError(json.dumps(payload, ensure_ascii=False, separators=(",", ":")))
