"""Concurrent exact-target dispatch for independently running Visual Studio instances."""

import asyncio
import copy
from typing import Any, Dict, List, Optional, Tuple

try:
    from .. import vs_bridge
    from ..tool_context import (
        SAFE_DEBUG_BUNDLE_TIMEOUT_SECONDS,
        clamp_tool_timeout,
        format_result,
        handle_ide_error,
        invalid_argument,
        remaining_tool_timeout,
        validate_int_range,
        validate_nonempty_string,
    )
    from ..tool_runtime import backend as ide_bridge, mcp
except ImportError:
    import vs_bridge
    from tool_context import (
        SAFE_DEBUG_BUNDLE_TIMEOUT_SECONDS,
        clamp_tool_timeout,
        format_result,
        handle_ide_error,
        invalid_argument,
        remaining_tool_timeout,
        validate_int_range,
        validate_nonempty_string,
    )
    from tool_runtime import backend as ide_bridge, mcp

try:
    from ..vs_tool_registry import (
        DEBUG_METHOD_MANIFEST,
        backend_method_capabilities,
        backend_method_kind,
    )
except ImportError:
    from vs_tool_registry import (
        DEBUG_METHOD_MANIFEST,
        backend_method_capabilities,
        backend_method_kind,
    )


MAX_MULTI_VS_TARGETS = 8
MAX_MULTI_VS_CONCURRENCY = 8
_PUBLIC_TARGET_KEYS = (
    "pipeName",
    "processId",
    "processStartTimeUtcTicks",
    "serverInstanceId",
    "debugSessionGeneration",
    "breakSequence",
    "contextEpoch",
)


def _public_target(target: Optional[Dict[str, Any]]) -> Optional[Dict[str, Any]]:
    if not isinstance(target, dict):
        return None
    payload = {key: target[key] for key in _PUBLIC_TARGET_KEYS if key in target}
    return payload or None


def _error_payload(
    error: Exception,
    fallback_code: str = "IDE_BRIDGE_ERROR",
    *,
    outcome_unknown: bool = False,
) -> Dict[str, Any]:
    payload: Dict[str, Any] = {
        "code": str(getattr(error, "code", fallback_code) or fallback_code),
        "message": str(error),
        "outcomeUnknown": bool(getattr(error, "outcome_unknown", False)) or outcome_unknown,
    }
    request_id = getattr(error, "request_id", None)
    if isinstance(request_id, str) and request_id:
        payload["requestId"] = request_id
    return payload


def _local_error_payload(code: str, message: str) -> Dict[str, Any]:
    return {
        "code": code,
        "message": message,
        "outcomeUnknown": False,
    }


def _error_item(
    process_id: int,
    error: Dict[str, Any],
    target: Optional[Dict[str, Any]] = None,
) -> Dict[str, Any]:
    return {
        "processId": process_id,
        "status": "error",
        "target": _public_target(target),
        "error": error,
    }


def _validate_process_ids(process_ids: List[int]) -> List[int]:
    if not isinstance(process_ids, list):
        raise invalid_argument("process_ids must be an array of Visual Studio process IDs")
    if not process_ids:
        raise invalid_argument("process_ids must contain at least one Visual Studio process ID")
    if len(process_ids) > MAX_MULTI_VS_TARGETS:
        raise invalid_argument(
            f"process_ids supports at most {MAX_MULTI_VS_TARGETS} Visual Studio targets per call"
        )

    validated: List[int] = []
    seen = set()
    for index, process_id in enumerate(process_ids):
        value = validate_int_range(
            f"process_ids[{index}]", process_id, 1, 0x7FFFFFFF
        )
        if value in seen:
            raise invalid_argument("process_ids must not contain duplicate process IDs")
        seen.add(value)
        validated.append(value)
    return validated


def _validate_backend_method(method: str, allowed_kind: str) -> str:
    value = validate_nonempty_string("method", method, maximum=128)
    if value not in DEBUG_METHOD_MANIFEST:
        raise invalid_argument(
            "method must be a known AIDB/2 backend method, not an MCP tool name"
        )
    actual_kind = backend_method_kind(value)
    if actual_kind != allowed_kind:
        other = (
            "dispatch_vs_commands" if actual_kind == "command"
            else "dispatch_vs_instances"
        )
        raise invalid_argument(
            f"{value} is a {actual_kind} method; use {other} for multi-target "
            f"{actual_kind} dispatch."
        )
    return value


def _validate_params(params: Optional[Dict[str, Any]]) -> Dict[str, Any]:
    if params is None:
        return {}
    if not isinstance(params, dict):
        raise invalid_argument("params must be an object")
    # Shape validation only. Every target copies the params before dispatch,
    # so a second full copy here would be pure overhead.
    return params


async def _discover_and_prepare(
    process_ids: List[int],
    method: str,
    params: Dict[str, Any],
    timeout: float,
) -> Tuple[List[Tuple[int, Dict[str, Any], Dict[str, Any]]], Dict[int, Dict[str, Any]]]:
    instances = [
        instance
        for instance in await ide_bridge.discover_instances(
            preferred_ide="vs", timeout=timeout
        )
        if getattr(instance, "ide", None) == "vs"
    ]
    jobs: List[Tuple[int, Dict[str, Any], Dict[str, Any]]] = []
    preflight_failures: Dict[int, Dict[str, Any]] = {}

    for process_id in process_ids:
        matches = [
            instance for instance in instances
            if getattr(instance, "process_id", None) == process_id
        ]
        reachable = [
            instance for instance in matches
            if getattr(instance, "connection_status", "ok") == "ok"
        ]
        if not matches:
            preflight_failures[process_id] = _error_item(
                process_id,
                _local_error_payload(
                    "TARGET_NOT_FOUND",
                    f"No authenticated Visual Studio instance with process ID {process_id} was discovered.",
                ),
            )
            continue
        if not reachable:
            preflight_failures[process_id] = _error_item(
                process_id,
                _local_error_payload(
                    "TARGET_UNREACHABLE",
                    f"Visual Studio process {process_id} is not reachable through an authenticated pipe.",
                ),
            )
            continue
        if len(reachable) != 1:
            preflight_failures[process_id] = _error_item(
                process_id,
                _local_error_payload(
                    "TARGET_AMBIGUOUS",
                    f"More than one authenticated Visual Studio instance reported process ID {process_id}.",
                ),
            )
            continue

        instance = reachable[0]
        try:
            target = instance.target_identity()
            registration = instance.registration_snapshot()
        except (ide_bridge.IDEBridgeError, vs_bridge.VSBridgeError) as error:
            preflight_failures[process_id] = _error_item(
                process_id,
                _error_payload(error, "INVALID_TARGET"),
            )
            continue
        allowed = registration.get("capabilities") or []
        missing_capability = next(
            (
                capability
                for capability in backend_method_capabilities(method, params)
                if capability not in allowed
            ),
            None,
        )
        if missing_capability is not None:
            preflight_failures[process_id] = _error_item(
                process_id,
                _local_error_payload(
                    "PERMISSION_DENIED",
                    f"Capability '{missing_capability}' is disabled for Visual Studio "
                    f"process {process_id}.",
                ),
                target,
            )
            continue
        jobs.append((process_id, target, registration))

    return jobs, preflight_failures


async def _dispatch_one(
    semaphore: asyncio.Semaphore,
    process_id: int,
    target: Dict[str, Any],
    registration: Dict[str, Any],
    method: str,
    params: Dict[str, Any],
    timeout: float,
    deadline: float,
) -> Dict[str, Any]:
    async with semaphore:
        # The budget covers the whole dispatch call, so a queued target gets
        # only whatever is left when it actually starts.
        call_timeout = remaining_tool_timeout(deadline, timeout)
        if call_timeout is None:
            return _error_item(
                process_id,
                _local_error_payload(
                    "DEADLINE_EXCEEDED_NOT_STARTED",
                    f"Target {process_id} was not dispatched: the {timeout:g}s dispatch "
                    "budget was exhausted before this target could start.",
                ),
                target,
            )
        try:
            call_params = copy.deepcopy(params)
        except (TypeError, ValueError):
            return _error_item(
                process_id,
                _local_error_payload(
                    "INVALID_PARAMS", "params must be a copyable JSON-style object"
                ),
                target,
            )
        try:
            result, response_target = await vs_bridge.call_pipe_exact(
                target,
                method,
                call_params,
                call_timeout,
                include_target=True,
                registration_snapshot=registration,
                advance_selection=False,
            )
            return {
                "processId": process_id,
                "status": "ok",
                "target": _public_target(response_target),
                "result": result,
            }
        except vs_bridge.VSBridgeError as error:
            return _error_item(
                process_id,
                _error_payload(error),
                getattr(error, "target_identity", None) or target,
            )
        except OSError as error:
            # Transport-level failure (closed pipe, reset connection) is scoped
            # to this target. Anything else is a bug and should not be hidden
            # behind a per-target error item.
            #
            # A command that failed on the transport may already have been
            # accepted by Visual Studio, so its outcome is unknown rather than
            # definitely-not-applied. Reporting False here would tell the caller
            # it is safe to re-invoke a destructive command.
            return _error_item(
                process_id,
                _error_payload(
                    error,
                    "TARGET_DISPATCH_FAILED",
                    outcome_unknown=backend_method_kind(method) == "command",
                ),
                target,
            )


async def _dispatch_multi_target(
    *,
    tool_name: str,
    allowed_kind: str,
    method: str,
    process_ids: List[int],
    params: Optional[Dict[str, Any]],
    timeout_seconds: Optional[float],
    max_concurrency: int,
) -> Any:
    method = _validate_backend_method(method, allowed_kind)
    process_ids = _validate_process_ids(process_ids)
    params = _validate_params(params)
    timeout = clamp_tool_timeout(timeout_seconds, SAFE_DEBUG_BUNDLE_TIMEOUT_SECONDS)
    max_concurrency = validate_int_range(
        "max_concurrency", max_concurrency, 1, MAX_MULTI_VS_CONCURRENCY
    )
    method_kind = backend_method_kind(method)

    # The budget covers the whole call, discovery included.
    deadline = asyncio.get_running_loop().time() + timeout
    discover_budget = remaining_tool_timeout(deadline, timeout) or timeout
    try:
        jobs, preflight_failures = await _discover_and_prepare(
            process_ids, method, params, discover_budget
        )
    except ide_bridge.IDEBridgeError as error:
        return handle_ide_error(error, tool_name, method=method)
    except vs_bridge.VSBridgeError as error:
        return handle_ide_error(error, tool_name, method=method)

    semaphore = asyncio.Semaphore(min(max_concurrency, len(jobs)))
    dispatched = await asyncio.gather(*(
        _dispatch_one(
            semaphore,
            process_id,
            target,
            registration,
            method,
            params,
            timeout,
            deadline,
        )
        for process_id, target, registration in jobs
    )) if jobs else []
    dispatched_by_process = {item["processId"]: item for item in dispatched}
    results = [
        preflight_failures.get(process_id) or dispatched_by_process[process_id]
        for process_id in process_ids
    ]
    success_count = sum(item["status"] == "ok" for item in results)
    failure_count = len(results) - success_count
    return format_result({
        "method": method,
        "methodKind": method_kind,
        "requestedProcessIds": process_ids,
        "maxConcurrency": min(max_concurrency, len(jobs)),
        "timeoutSeconds": timeout,
        "selectionBehavior": "does_not_change_selected_vs",
        "success": failure_count == 0,
        "partial": failure_count != 0,
        "successCount": success_count,
        "failureCount": failure_count,
        "results": results,
    })


@mcp.tool()
async def dispatch_vs_instances(
    method: str,
    process_ids: List[int],
    params: Optional[Dict[str, Any]] = None,
    timeout_seconds: Optional[float] = None,
    max_concurrency: int = 4,
) -> str:
    """Run one read-only AIDB/2 query against explicit Visual Studio PIDs concurrently.

    This is the multi-instance path for queries: it discovers each requested
    process, captures its authenticated immutable target snapshot, and uses exact
    pipe routing. It does not select, clear, or redirect the normal single-instance
    target — a dispatched call never rewrites the pinned single-instance target.
    `method` must be an AIDB/2 *query* method (for example `get_debug_status` or
    `capture_debug_bundle_safe`), not an MCP tool name; command methods are
    rejected and belong to `dispatch_vs_commands`. A target whose registration
    does not grant the method's capability reports `PERMISSION_DENIED` instead of
    being called. `timeout_seconds` budgets the whole call — discovery included —
    not each target, so targets that cannot start within it report
    `DEADLINE_EXCEEDED_NOT_STARTED`. Results remain in `process_ids` order and one
    target's transport or protocol failure does not prevent calls to the other
    authenticated targets.
    """
    return await _dispatch_multi_target(
        tool_name="dispatch_vs_instances",
        allowed_kind="query",
        method=method,
        process_ids=process_ids,
        params=params,
        timeout_seconds=timeout_seconds,
        max_concurrency=max_concurrency,
    )


@mcp.tool()
async def dispatch_vs_commands(
    method: str,
    process_ids: List[int],
    params: Optional[Dict[str, Any]] = None,
    timeout_seconds: Optional[float] = None,
    max_concurrency: int = 4,
) -> str:
    """Run one mutating AIDB/2 command against explicit Visual Studio PIDs concurrently.

    Destructive multi-target counterpart of `dispatch_vs_instances`. Calling this
    tool is itself the explicit opt-in, so there is no separate confirmation flag.
    `method` must be an AIDB/2 *command* method (for example `continue` or
    `set_breakpoint`); query methods are rejected and belong to
    `dispatch_vs_instances`. The same exact-target routing, per-target capability
    preflight, whole-call `timeout_seconds` budget and
    `does_not_change_selected_vs` guarantee apply. Confirm every target before
    issuing a command that advances or stops a debuggee: a target that fails on
    the transport reports `outcomeUnknown: true`, meaning it may already have been
    applied.
    """
    return await _dispatch_multi_target(
        tool_name="dispatch_vs_commands",
        allowed_kind="command",
        method=method,
        process_ids=process_ids,
        params=params,
        timeout_seconds=timeout_seconds,
        max_concurrency=max_concurrency,
    )
