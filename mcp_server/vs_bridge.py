"""
Named Pipe client for communication with Visual Studio Debugger MCP Extension.
Provides async interface for calling debugger methods over the named pipe.
Supports multiple VS instances with automatic discovery and smart selection.
"""

import json
import uuid
import asyncio
import base64
import ctypes
import time
import concurrent.futures
import contextvars
from ctypes import wintypes
from typing import Any, Dict, List, Optional
import logging
import heapq
import math
import os
import pathlib
import re
import stat
import struct
import threading

try:
    from .vs_errors import VSBridgeError, VSRemoteError
    from .vs_trust import (
        MAX_INSTANCE_REGISTRATION_SIZE,
        _current_process_start_time_utc_ticks,
        _current_token_can_modify_path,
        _load_instance_registration,
        _path_has_reparse_component,
        _remaining_validation_seconds,
        _scoped_validation_deadline,
        _trusted_visual_studio_paths,
        _validate_registration_security,
        _validate_same_user_process,
        _validate_trusted_visual_studio_image,
        _validate_visual_studio_process,
        _win_verify_trust,
    )
except ImportError:
    from vs_errors import VSBridgeError, VSRemoteError
    from vs_trust import (
        MAX_INSTANCE_REGISTRATION_SIZE,
        _current_process_start_time_utc_ticks,
        _current_token_can_modify_path,
        _load_instance_registration,
        _path_has_reparse_component,
        _remaining_validation_seconds,
        _scoped_validation_deadline,
        _trusted_visual_studio_paths,
        _validate_registration_security,
        _validate_same_user_process,
        _validate_trusted_visual_studio_image,
        _validate_visual_studio_process,
        _win_verify_trust,
    )

logger = logging.getLogger(__name__)

PIPE_NAME_PREFIX = r"\\.\pipe\AIDebuggerVS"
PROTOCOL_VERSION = "aidb/2"
DEFAULT_TIMEOUT = 10.0  # seconds
MAX_REQUEST_MESSAGE_SIZE = 4 * 1024 * 1024
MAX_RESPONSE_MESSAGE_SIZE = 16 * 1024 * 1024
MAX_JSON_NESTING_DEPTH = 64
MAX_DISCOVERY_CANDIDATES = 64
MAX_DISCOVERY_DIRECTORY_ENTRIES = 1024
_INSTANCE_STATE_LOCK = threading.Lock()
_INSTANCE_STATE: Dict[tuple[str, str], Dict[str, Any]] = {}
_LAST_COMMAND_RECEIPT: contextvars.ContextVar[Optional[Dict[str, Any]]] = contextvars.ContextVar(
    "aidb_last_command_receipt",
    default=None,
)
_UUID7_LOCK = threading.Lock()
_UUID7_LAST_MILLISECONDS = -1
_UUID7_RANDOM_SEQUENCE = 0


def _new_request_id() -> str:
    """Generate a process-monotonic UUIDv7 for bounded server replay windows."""
    global _UUID7_LAST_MILLISECONDS, _UUID7_RANDOM_SEQUENCE
    with _UUID7_LOCK:
        milliseconds = int(time.time() * 1000)
        if milliseconds > _UUID7_LAST_MILLISECONDS:
            _UUID7_LAST_MILLISECONDS = milliseconds
            _UUID7_RANDOM_SEQUENCE = uuid.uuid4().int & ((1 << 74) - 1)
        else:
            milliseconds = _UUID7_LAST_MILLISECONDS
            _UUID7_RANDOM_SEQUENCE += 1
            if _UUID7_RANDOM_SEQUENCE >= (1 << 74):
                _UUID7_LAST_MILLISECONDS += 1
                milliseconds = _UUID7_LAST_MILLISECONDS
                _UUID7_RANDOM_SEQUENCE = 0
        random_a = (_UUID7_RANDOM_SEQUENCE >> 62) & 0xFFF
        random_b = _UUID7_RANDOM_SEQUENCE & ((1 << 62) - 1)
        value = (
            (milliseconds & ((1 << 48) - 1)) << 80
            | 0x7 << 76
            | random_a << 64
            | 0b10 << 62
            | random_b
        )
        return str(uuid.UUID(int=value))


def begin_tool_invocation() -> None:
    """Clear command metadata at the start of one MCP tool invocation."""
    _LAST_COMMAND_RECEIPT.set(None)


def get_last_command_receipt() -> Optional[Dict[str, Any]]:
    value = _LAST_COMMAND_RECEIPT.get()
    return dict(value) if isinstance(value, dict) else None


def _validate_json_nesting(source: str) -> None:
    """Reject pathological JSON before the recursive CPython decoder sees it."""
    depth = 0
    in_string = False
    escaped = False
    for value in source:
        if in_string:
            if escaped:
                escaped = False
            elif value == "\\":
                escaped = True
            elif value == '"':
                in_string = False
            continue
        if value == '"':
            in_string = True
        elif value in "[{":
            depth += 1
            if depth > MAX_JSON_NESTING_DEPTH:
                raise VSBridgeError(
                    f"Response JSON nesting exceeds {MAX_JSON_NESTING_DEPTH}",
                    code="INVALID_RESPONSE",
                    outcome_unknown=True,
                )
        elif value in "]}":
            depth -= 1
            if depth < 0:
                raise VSBridgeError(
                    "Response JSON delimiters are unbalanced",
                    code="INVALID_RESPONSE",
                    outcome_unknown=True,
                )
    if in_string or depth != 0:
        raise VSBridgeError(
            "Response JSON is incomplete",
            code="INVALID_RESPONSE",
            outcome_unknown=True,
        )


def _strict_json_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise VSBridgeError(
                f"Response contains duplicate JSON property {key!r}",
                code="INVALID_RESPONSE",
                outcome_unknown=True,
            )
        result[key] = value
    return result


def _reject_json_constant(token: str):
    raise ValueError(f"Non-finite JSON constant is not allowed: {token}")


def _parse_bounded_json_int(token: str) -> int:
    if len(token) > 21:
        raise ValueError("JSON integer exceeds the 64-bit protocol budget")
    value = int(token, 10)
    if value < -(1 << 63) or value > (1 << 64) - 1:
        raise ValueError("JSON integer is outside the supported 64-bit range")
    return value


def _parse_bounded_json_float(token: str) -> float:
    if len(token) > 128:
        raise ValueError("JSON floating-point token is too long")
    value = float(token)
    if not math.isfinite(value):
        raise ValueError("JSON floating-point value must be finite")
    return value


def _reject_unknown_fields(value: Dict[str, Any], allowed: frozenset[str], path: str) -> None:
    unknown = sorted(set(value) - allowed)
    if unknown:
        raise VSBridgeError(
            f"Response contains unknown {path} field {unknown[0]!r}",
            code="INVALID_RESPONSE",
            outcome_unknown=True,
        )


def _control_pipe_methods() -> frozenset[str]:
    try:
        from .vs_tool_registry import control_plane_methods
    except ImportError:
        from vs_tool_registry import control_plane_methods
    return control_plane_methods()


def _recovery_pipe_methods() -> frozenset[str]:
    try:
        from .vs_tool_registry import recovery_plane_methods
    except ImportError:
        from vs_tool_registry import recovery_plane_methods
    return recovery_plane_methods()


def _interrupt_pipe_methods() -> frozenset[str]:
    try:
        from .vs_tool_registry import interrupt_pipe_methods
    except ImportError:
        from vs_tool_registry import interrupt_pipe_methods
    return interrupt_pipe_methods()


def _record_instance_state(pipe_name: str, server_identity: Dict[str, Any]) -> Dict[str, Any]:
    """Cache one server identity without allowing out-of-order responses to rewind it."""
    incoming = dict(server_identity)
    server_instance_id = str(incoming.get("serverInstanceId") or "")
    key = (pipe_name, server_instance_id)
    with _INSTANCE_STATE_LOCK:
        current = _INSTANCE_STATE.get(key)
        if current is None:
            _INSTANCE_STATE[key] = incoming
        else:
            keys = ("debugSessionGeneration", "breakSequence", "contextEpoch")
            incoming_order = tuple(int(incoming.get(key) or 0) for key in keys)
            current_order = tuple(int(current.get(key) or 0) for key in keys)
            if incoming_order >= current_order:
                _INSTANCE_STATE[key] = incoming
        return dict(_INSTANCE_STATE[key])


def _get_instance_state(pipe_name: str, server_instance_id: str) -> Dict[str, Any]:
    with _INSTANCE_STATE_LOCK:
        return dict(_INSTANCE_STATE.get((pipe_name, server_instance_id)) or {})


def _resolve_request_debug_identity(
    pipe_name: str,
    registration: Dict[str, Any],
    expected_target: Optional[Dict[str, Any]],
) -> tuple[int, int, int]:
    keys = ("debugSessionGeneration", "breakSequence", "contextEpoch")
    supplied = expected_target or {}
    supplied_keys = [key for key in keys if key in supplied]
    if supplied_keys:
        if len(supplied_keys) != len(keys):
            raise VSBridgeError("Pinned debug identity is incomplete", code="INVALID_TARGET")
        values = tuple(supplied[key] for key in keys)
        if any(isinstance(value, bool) or not isinstance(value, int) or value < 0 for value in values):
            raise VSBridgeError("Pinned debug identity is invalid", code="INVALID_TARGET")
        return values
    cached_state = _get_instance_state(pipe_name, str(registration["serverInstanceId"]))
    return tuple(int(cached_state.get(key) or 0) for key in keys)

FILE_FLAG_OVERLAPPED = 0x40000000
ERROR_BROKEN_PIPE = 109
ERROR_IO_PENDING = 997
ERROR_OPERATION_ABORTED = 995
ERROR_NOT_FOUND = 1168
WAIT_OBJECT_0 = 0
WAIT_TIMEOUT = 258

ULONG_PTR = ctypes.c_ulonglong if ctypes.sizeof(ctypes.c_void_p) == 8 else ctypes.c_ulong


class OVERLAPPED(ctypes.Structure):
    _fields_ = [
        ("Internal", ULONG_PTR),
        ("InternalHigh", ULONG_PTR),
        ("Offset", wintypes.DWORD),
        ("OffsetHigh", wintypes.DWORD),
        ("hEvent", wintypes.HANDLE),
    ]


def _finalize_call_error(
    error: VSBridgeError,
    *,
    is_command: bool,
    write_started: bool,
    request_id: str,
) -> VSBridgeError:
    if not is_command:
        error.outcome_unknown = False
    elif write_started and not isinstance(error, VSRemoteError):
        error.outcome_unknown = True
    if error.outcome_unknown:
        error.request_id = request_id
    return error


def _decode_response(
    response_line: bytes,
    request_id: str,
    expected_server: Optional[Dict[str, Any]] = None,
    include_server: bool = False,
) -> Any:
    """Decode and validate one bounded VS pipe response frame."""
    if len(response_line) > MAX_RESPONSE_MESSAGE_SIZE:
        raise VSBridgeError(
            f"Response exceeds maximum size of {MAX_RESPONSE_MESSAGE_SIZE} bytes",
            code="RESOURCE_LIMIT_EXCEEDED",
            outcome_unknown=True,
        )

    try:
        response_json = response_line.decode("utf-8", errors="strict")
    except UnicodeDecodeError as exc:
        raise VSBridgeError(
            "Response is not valid UTF-8",
            code="INVALID_RESPONSE",
            outcome_unknown=True,
        ) from exc

    if not response_json.strip(" \t\r\n"):
        raise VSBridgeError(
            "Empty response received from pipe",
            code="EMPTY_RESPONSE",
            outcome_unknown=True,
        )

    _validate_json_nesting(response_json)
    try:
        response = json.loads(
            response_json,
            object_pairs_hook=_strict_json_object,
            parse_constant=_reject_json_constant,
            parse_int=_parse_bounded_json_int,
            parse_float=_parse_bounded_json_float,
        )
    except VSBridgeError:
        raise
    except (json.JSONDecodeError, RecursionError, MemoryError, ValueError) as exc:
        raise VSBridgeError(
            "Response is not valid JSON",
            code="INVALID_RESPONSE",
            outcome_unknown=True,
        ) from exc

    if not isinstance(response, dict):
        raise VSBridgeError(
            "Response envelope must be a JSON object",
            code="INVALID_RESPONSE",
            outcome_unknown=True,
        )
    _reject_unknown_fields(
        response,
        frozenset({"protocol", "id", "status", "server", "result", "error"}),
        "envelope",
    )

    if response.get("protocol") != PROTOCOL_VERSION:
        raise VSBridgeError(
            f"Response protocol mismatch (expected {PROTOCOL_VERSION})",
            code="PROTOCOL_MISMATCH",
            outcome_unknown=True,
        )

    response_id = response.get("id")
    if response_id != request_id:
        raise VSBridgeError(
            f"Response id mismatch (expected {request_id}, received {response_id})",
            code="RESPONSE_ID_MISMATCH",
            outcome_unknown=True,
        )

    server = response.get("server")
    if not isinstance(server, dict):
        raise VSBridgeError(
            "Response is missing server identity",
            code="INVALID_RESPONSE",
            outcome_unknown=True,
        )
    _reject_unknown_fields(
        server,
        frozenset({
            "processId", "processStartTimeUtcTicks", "serverInstanceId",
            "protocolCatalogHash", "debugSessionGeneration", "breakSequence", "contextEpoch",
        }),
        "server identity",
    )
    catalog_hash = server.get("protocolCatalogHash")
    if not isinstance(catalog_hash, str) or re.fullmatch(r"[0-9a-f]{64}", catalog_hash) is None:
        raise VSBridgeError(
            "Response server identity has invalid protocolCatalogHash",
            code="INVALID_RESPONSE",
            outcome_unknown=True,
        )
    if expected_server:
        for key in ("processId", "processStartTimeUtcTicks", "serverInstanceId"):
            if server.get(key) != expected_server.get(key):
                raise VSBridgeError(
                    f"Response server identity mismatch for {key}",
                    code="STALE_TARGET",
                    outcome_unknown=True,
                )
        if catalog_hash != expected_server.get("protocolCatalogHash"):
            raise VSBridgeError(
                "Response protocol catalog does not match the selected Visual Studio registration",
                code="PROTOCOL_CATALOG_MISMATCH",
                outcome_unknown=True,
            )
    for key in ("debugSessionGeneration", "breakSequence", "contextEpoch"):
        if not isinstance(server.get(key), int) or server[key] < 0:
            raise VSBridgeError(
                f"Response server identity has invalid {key}",
                code="INVALID_RESPONSE",
                outcome_unknown=True,
            )

    status = response.get("status")
    if status not in ("ok", "error"):
        raise VSBridgeError(
            "Response status must be 'ok' or 'error'",
            code="INVALID_RESPONSE",
            outcome_unknown=True,
        )

    error = response.get("error")
    has_error = "error" in response and error is not None
    has_result_field = "result" in response
    has_non_null_result = has_result_field and response.get("result") is not None
    if status == "ok" and (has_error or not has_result_field):
        raise VSBridgeError(
            "Successful response must contain result and no error",
            code="INVALID_RESPONSE",
            outcome_unknown=True,
        )
    if status == "error" and (not has_error or has_non_null_result):
        raise VSBridgeError(
            "Error response must contain error and no result",
            code="INVALID_RESPONSE",
            outcome_unknown=True,
        )

    if status == "error":
        if not isinstance(error, dict):
            raise VSBridgeError(
                "Response error envelope is invalid",
                code="INVALID_RESPONSE",
                outcome_unknown=True,
            )
        _reject_unknown_fields(
            error,
            frozenset({"code", "message", "outcomeUnknown", "requestId", "details"}),
            "error",
        )
        if not isinstance(error.get("code"), str) or not error["code"] or \
           not isinstance(error.get("message"), str) or not error["message"] or \
           ("outcomeUnknown" in error and not isinstance(error["outcomeUnknown"], bool)) or \
           ("requestId" in error and not isinstance(error["requestId"], str)) or \
           ("details" in error and not isinstance(error["details"], dict)):
            raise VSBridgeError(
                "Response error envelope has invalid fields",
                code="INVALID_RESPONSE",
                outcome_unknown=True,
            )
        error_code = error["code"]
        error_message = error["message"]
        remote_error = VSRemoteError(
            f"{error_code}: {error_message}",
            code=error_code,
            outcome_unknown=bool(error.get("outcomeUnknown")) or error_code == "OUTCOME_UNKNOWN",
            details=error.get("details") if isinstance(error.get("details"), dict) else None,
        )
        if error.get("requestId"):
            remote_error.request_id = str(error.get("requestId"))
        remote_error.server_identity = dict(server)
        raise remote_error

    result = response.get("result")
    return (result, server) if include_server else result


def _backend_method_kind(method: str) -> str:
    try:
        from .vs_tool_registry import backend_method_kind
    except ImportError:
        from vs_tool_registry import backend_method_kind
    return backend_method_kind(method)


def _backend_method_session_refresh(method: str) -> bool:
    try:
        from .vs_tool_registry import backend_method_session_refresh
    except ImportError:
        from vs_tool_registry import backend_method_session_refresh
    return backend_method_session_refresh(method)


def _backend_method_capability(method: str) -> str:
    try:
        from .vs_tool_registry import backend_method_capability
    except ImportError:
        from vs_tool_registry import backend_method_capability
    return backend_method_capability(method)


def _backend_method_capabilities(method: str, params: Optional[Dict[str, Any]] = None) -> tuple[str, ...]:
    try:
        from .vs_tool_registry import backend_method_capabilities
    except ImportError:
        from vs_tool_registry import backend_method_capabilities
    return backend_method_capabilities(method, params)


def _all_vs_capabilities() -> tuple[str, ...]:
    try:
        from .vs_tool_registry import all_vs_capabilities
    except ImportError:
        from vs_tool_registry import all_vs_capabilities
    return all_vs_capabilities()


def _copy_registration_snapshot(registration: Dict[str, Any]) -> Dict[str, Any]:
    """Copy only the authenticated registration fields used by the transport."""
    pipe_name = registration.get("pipeName")
    return {
        "protocol": registration.get("protocol"),
        "pipeName": pipe_name,
        "controlPipeName": registration.get("controlPipeName") or (
            f"{pipe_name}_control" if pipe_name else None
        ),
        "recoveryPipeName": registration.get("recoveryPipeName") or
            registration.get("controlPipeName") or (
                f"{pipe_name}_control" if pipe_name else None
            ),
        "processId": registration.get("processId"),
        "processStartTimeUtcTicks": registration.get("processStartTimeUtcTicks"),
        "serverInstanceId": registration.get("serverInstanceId"),
        "protocolCatalogHash": registration.get("protocolCatalogHash"),
        "authToken": registration.get("authToken"),
        "capabilities": list(registration.get("capabilities") or []),
    }


def _target_identity_from_registration(pipe_name: str, registration: Dict[str, Any]) -> Dict[str, Any]:
    return {
        "pipeName": pipe_name,
        "processId": registration.get("processId"),
        "processStartTimeUtcTicks": registration.get("processStartTimeUtcTicks"),
        "serverInstanceId": registration.get("serverInstanceId"),
    }


def _bind_registration_snapshot(
    pipe_name: str,
    expected_target: Optional[Dict[str, Any]] = None,
    registration_snapshot: Optional[Dict[str, Any]] = None,
) -> tuple[Dict[str, Any], Dict[str, Any]]:
    """Bind one request to one complete registration without a second mutable read."""
    try:
        registration = (
            _copy_registration_snapshot(registration_snapshot)
            if registration_snapshot is not None
            else _load_instance_registration(pipe_name)
        )
        registration = _copy_registration_snapshot(registration)
    except VSBridgeError as error:
        if expected_target is not None:
            error.target_identity = dict(expected_target)
        raise

    actual_target = _target_identity_from_registration(pipe_name, registration)
    if registration.get("pipeName") != pipe_name:
        raise VSBridgeError(
            "Bound Visual Studio registration pipe changed",
            code="STALE_TARGET",
            target_identity=dict(expected_target or actual_target),
        )
    if expected_target is not None:
        for key, actual in actual_target.items():
            if actual != expected_target.get(key):
                raise VSBridgeError(
                    f"Pinned target identity changed for {key}",
                    code="STALE_TARGET",
                    target_identity=dict(expected_target),
                )
    return registration, actual_target


class BoundVSTarget(dict):
    """Public target identity plus a private, non-serialized authentication binding."""

    def __init__(self, identity: Dict[str, Any], registration_snapshot: Dict[str, Any]):
        super().__init__(identity)
        self._registration_snapshot = _copy_registration_snapshot(registration_snapshot)

    def registration_snapshot(self) -> Dict[str, Any]:
        return _copy_registration_snapshot(self._registration_snapshot)

    def instance_target(self) -> "BoundVSTarget":
        return BoundVSTarget(
            {
                key: self[key]
                for key in (
                    "pipeName",
                    "processId",
                    "processStartTimeUtcTicks",
                    "serverInstanceId",
                )
            },
            self._registration_snapshot,
        )


class VSInstance:
    """Represents a discovered Visual Studio instance."""
    
    def __init__(
        self,
        pipe_name: str,
        info: Optional[Dict[str, Any]] = None,
        registration_snapshot: Optional[Dict[str, Any]] = None,
    ):
        self.ide = "vs"
        self.pipe_name = pipe_name
        self.process_id = 0
        self.solution_name = ""
        self.solution_path = ""
        self.is_debugging = False
        self.is_paused = False
        self.debug_mode = ""
        self.current_process = ""
        self.vs_version = ""
        self.connection_status = "ok"
        self.last_error = ""
        self.allowed_capabilities: List[str] = []
        self.access_mode = "unknown"
        self.process_start_time_utc_ticks: Optional[int] = None
        self.server_instance_id: Optional[str] = None
        self._registration_snapshot = (
            _copy_registration_snapshot(registration_snapshot)
            if registration_snapshot is not None
            else None
        )
        
        if info:
            self.process_id = info.get("processId", 0)
            self.solution_name = info.get("solutionName", "")
            self.solution_path = info.get("solutionPath", "")
            self.is_debugging = info.get("isDebugging", False)
            self.is_paused = info.get("isPaused", False)
            self.debug_mode = info.get("debugMode", "")
            self.current_process = info.get("currentProcess", "")
            self.vs_version = info.get("vsVersion", "")
            self.connection_status = info.get("connectionStatus", self.connection_status)
            self.last_error = info.get("lastError", self.last_error)
            capabilities = info.get("allowedCapabilities", info.get("capabilities", []))
            if isinstance(capabilities, list):
                self.allowed_capabilities = sorted({str(value) for value in capabilities if value})
            self.access_mode = str(info.get("accessMode") or self.access_mode)
            start_ticks = info.get("processStartTimeUtcTicks")
            server_instance_id = info.get("serverInstanceId")
            if isinstance(start_ticks, int) and start_ticks > 0:
                self.process_start_time_utc_ticks = start_ticks
            if isinstance(server_instance_id, str) and server_instance_id:
                self.server_instance_id = server_instance_id
        
        # If process_id is still 0, try to extract from pipe name
        if self.process_id == 0 and pipe_name.startswith("AIDebuggerVS_"):
            try:
                self.process_id = int(pipe_name.split("_")[1])
            except (ValueError, IndexError):
                pass
    
    def to_dict(self) -> Dict[str, Any]:
        return {
            "ide": "vs",
            "ideName": "Visual Studio",
            "pipeName": self.pipe_name,
            "processId": self.process_id,
            "solutionName": self.solution_name,
            "solutionPath": self.solution_path,
            "isDebugging": self.is_debugging,
            "isPaused": self.is_paused,
            "debugMode": self.debug_mode,
            "currentProcess": self.current_process,
            "ideVersion": self.vs_version,
            "connectionStatus": self.connection_status,
            "lastError": self.last_error,
            "allowedCapabilities": list(self.allowed_capabilities),
            "accessMode": self.access_mode,
            "processStartTimeUtcTicks": self.process_start_time_utc_ticks,
            "serverInstanceId": self.server_instance_id,
        }

    def target_identity(self) -> Dict[str, Any]:
        if self.process_id <= 0 or not self.process_start_time_utc_ticks or not self.server_instance_id:
            raise VSBridgeError("Visual Studio instance has no authenticated immutable identity", code="INVALID_TARGET")
        return {
            "ide": "vs",
            "pipeName": self.pipe_name,
            "processId": self.process_id,
            "processStartTimeUtcTicks": self.process_start_time_utc_ticks,
            "serverInstanceId": self.server_instance_id,
        }

    def registration_snapshot(self) -> Dict[str, Any]:
        """Return the private authenticated snapshot that produced this discovery result."""
        if self._registration_snapshot is None:
            raise VSBridgeError(
                "Visual Studio instance has no authenticated registration snapshot",
                code="INVALID_TARGET",
                target_identity=self.target_identity(),
            )
        snapshot = _copy_registration_snapshot(self._registration_snapshot)
        _bind_registration_snapshot(
            self.pipe_name,
            expected_target=self.target_identity(),
            registration_snapshot=snapshot,
        )
        return snapshot
    
    def __repr__(self):
        status = "Paused" if self.is_paused else ("Debugging" if self.is_debugging else "Idle")
        return f"VSInstance(pid={self.process_id}, solution='{self.solution_name}', status={status})"


def _discover_vs_pipes() -> List[str]:
    """Enumerate only owner-registration rendezvous candidates, never global pipes."""
    local_app_data = os.environ.get("LOCALAPPDATA")
    if not local_app_data:
        return []
    instances_root = pathlib.Path(local_app_data) / "AIDebugger" / "instances"
    try:
        metadata = instances_root.lstat()
        if (
            not stat.S_ISDIR(metadata.st_mode)
            or instances_root.is_symlink()
            or bool(
                getattr(metadata, "st_file_attributes", 0)
                & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
            )
        ):
            return []
        newest_candidates = []
        scanned_count = 0
        with os.scandir(instances_root) as entries:
            for scanned_count, entry in enumerate(entries, start=1):
                match = re.fullmatch(r"(AIDebuggerVS_([1-9][0-9]*))\.json", entry.name)
                if not match:
                    continue
                try:
                    entry_metadata = entry.stat(follow_symlinks=False)
                    if (
                        not stat.S_ISREG(entry_metadata.st_mode)
                        or entry.is_symlink()
                        or bool(
                            getattr(entry_metadata, "st_file_attributes", 0)
                            & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
                        )
                    ):
                        continue
                    # PID is the deterministic newest-first tie-breaker. Windows
                    # filesystem timestamp granularity can make many registration
                    # writes appear to have the same mtime.
                    candidate = (entry_metadata.st_mtime_ns, int(match.group(2)), match.group(1))
                    if len(newest_candidates) < MAX_DISCOVERY_CANDIDATES:
                        heapq.heappush(newest_candidates, candidate)
                    elif candidate > newest_candidates[0]:
                        heapq.heapreplace(newest_candidates, candidate)
                except OSError:
                    continue
        if scanned_count > MAX_DISCOVERY_DIRECTORY_ENTRIES:
            logger.warning(
                "Visual Studio registration directory contains %d entries; all were scanned with bounded memory",
                scanned_count,
            )
        newest_candidates.sort(key=lambda item: (-item[0], -item[1]))
        return [item[2] for item in newest_candidates]
    except OSError as error:
        logger.debug("Registration rendezvous discovery failed: %s", type(error).__name__)
        return []


def _close_win_handle(kernel32: ctypes.WinDLL, handle: wintypes.HANDLE) -> None:
    if handle:
        kernel32.CloseHandle(handle)


def _create_overlapped(
    kernel32: ctypes.WinDLL,
    outcome_unknown: bool = False,
) -> tuple[OVERLAPPED, wintypes.HANDLE]:
    event = kernel32.CreateEventW(None, True, False, None)
    if not event:
        raise VSBridgeError(
            f"Failed to create overlapped event. Error: {ctypes.get_last_error()}",
            code="TRANSPORT_ERROR",
            outcome_unknown=outcome_unknown,
        )

    overlapped = OVERLAPPED()
    overlapped.hEvent = event
    return overlapped, event


def _cancel_pending_io(kernel32: ctypes.WinDLL, handle: wintypes.HANDLE, overlapped: OVERLAPPED) -> None:
    """Cancel and synchronously reap an OVERLAPPED before its event/buffer is freed."""
    if hasattr(kernel32, "CancelIoEx"):
        cancelled = bool(kernel32.CancelIoEx(handle, ctypes.byref(overlapped)))
        cancel_error = 0 if cancelled else ctypes.get_last_error()
        if not cancelled and cancel_error != ERROR_NOT_FOUND:
            raise VSBridgeError(
                f"CancelIoEx failed. Error: {cancel_error}",
                code="TRANSPORT_ERROR",
                outcome_unknown=True,
            )
    elif hasattr(kernel32, "CancelIo"):
        if not kernel32.CancelIo(handle):
            raise VSBridgeError(
                f"CancelIo failed. Error: {ctypes.get_last_error()}",
                code="TRANSPORT_ERROR",
                outcome_unknown=True,
            )
    else:
        raise VSBridgeError(
            "Windows overlapped I/O cancellation is unavailable",
            code="TRANSPORT_ERROR",
            outcome_unknown=True,
        )

    # GetOverlappedResult(bWait=True) is the documented completion barrier. The
    # OVERLAPPED structure, event and buffers remain alive until it returns.
    completed_bytes = wintypes.DWORD()
    kernel32.GetOverlappedResult(
        handle,
        ctypes.byref(overlapped),
        ctypes.byref(completed_bytes),
        True,
    )


def _wait_for_overlapped_result(
    kernel32: ctypes.WinDLL,
    handle: wintypes.HANDLE,
    overlapped: OVERLAPPED,
    bytes_transferred: wintypes.DWORD,
    timeout_ms: int,
    operation: str,
    timeout_message: str,
    outcome_unknown: bool = False,
) -> int:
    wait_result = kernel32.WaitForSingleObject(overlapped.hEvent, timeout_ms)
    if wait_result == WAIT_OBJECT_0:
        ok = kernel32.GetOverlappedResult(handle, ctypes.byref(overlapped), ctypes.byref(bytes_transferred), False)
        if not ok:
            error = ctypes.get_last_error()
            if error in (ERROR_BROKEN_PIPE, ERROR_OPERATION_ABORTED):
                raise VSBridgeError(
                    "Pipe disconnected by server",
                    code="PIPE_DISCONNECTED",
                    outcome_unknown=outcome_unknown,
                )
            raise VSBridgeError(
                f"{operation} failed. Error: {error}",
                code="TRANSPORT_ERROR",
                outcome_unknown=outcome_unknown,
            )
        return bytes_transferred.value

    if wait_result == WAIT_TIMEOUT:
        _cancel_pending_io(kernel32, handle, overlapped)
        raise VSBridgeError(timeout_message, code="PIPE_TIMEOUT", outcome_unknown=outcome_unknown)

    _cancel_pending_io(kernel32, handle, overlapped)
    raise VSBridgeError(
        f"{operation} wait failed. Error: {ctypes.get_last_error()}",
        code="TRANSPORT_ERROR",
        outcome_unknown=outcome_unknown,
    )


def _sync_call_to_pipe(
    pipe_name: str,
    method: str,
    params: Optional[Dict[str, Any]],
    timeout: float = 5.0,
    request_id: Optional[str] = None,
    deadline_monotonic: Optional[float] = None,
    deadline_unix_ms: Optional[int] = None,
    include_server: bool = False,
    expected_target: Optional[Dict[str, Any]] = None,
    registration_snapshot: Optional[Dict[str, Any]] = None,
) -> Any:
    """Synchronous call to a specific named pipe."""
    timeout = max(timeout, 0.1)
    deadline_monotonic = deadline_monotonic or (time.monotonic() + timeout)
    deadline_unix_ms = deadline_unix_ms or int(time.time() * 1000 + timeout * 1000)
    deadline = deadline_monotonic
    request_id = request_id or _new_request_id()
    is_command = _backend_method_kind(method) == "command"
    write_started = False
    with _scoped_validation_deadline(deadline_monotonic):
        registration = (
            dict(registration_snapshot)
            if registration_snapshot is not None
            else _load_instance_registration(pipe_name)
        )
    if expected_target is not None:
        for key in ("pipeName", "processId", "processStartTimeUtcTicks", "serverInstanceId"):
            actual = pipe_name if key == "pipeName" else registration.get(key)
            if actual != expected_target.get(key):
                raise VSBridgeError(
                    f"Pinned target identity changed for {key}",
                    code="STALE_TARGET",
                    target_identity=dict(expected_target),
                )
    debug_session_generation, break_sequence, context_epoch = _resolve_request_debug_identity(
        pipe_name,
        registration,
        expected_target,
    )

    def remaining_timeout_ms() -> int:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            return 0
        return max(1, int(remaining * 1000))

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

    GENERIC_READ = 0x80000000
    GENERIC_WRITE = 0x40000000
    OPEN_EXISTING = 3
    FILE_ATTRIBUTE_NORMAL = 0x80

    transport_pipe_name = (
        registration["recoveryPipeName"]
        if method in _recovery_pipe_methods()
        else registration["controlPipeName"]
        if method in _interrupt_pipe_methods()
        else pipe_name
    )
    full_pipe_name = f"\\\\.\\pipe\\{transport_pipe_name}"

    kernel32.CreateFileW.restype = wintypes.HANDLE
    kernel32.CreateFileW.argtypes = [
        wintypes.LPCWSTR,
        wintypes.DWORD,
        wintypes.DWORD,
        wintypes.LPVOID,
        wintypes.DWORD,
        wintypes.DWORD,
        wintypes.HANDLE,
    ]
    kernel32.WaitNamedPipeW.restype = wintypes.BOOL
    kernel32.WaitNamedPipeW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD]
    kernel32.SetNamedPipeHandleState.restype = wintypes.BOOL
    kernel32.SetNamedPipeHandleState.argtypes = [
        wintypes.HANDLE,
        ctypes.POINTER(wintypes.DWORD),
        wintypes.LPVOID,
        wintypes.LPVOID,
    ]
    kernel32.WriteFile.restype = wintypes.BOOL
    kernel32.WriteFile.argtypes = [
        wintypes.HANDLE,
        wintypes.LPVOID,
        wintypes.DWORD,
        ctypes.POINTER(wintypes.DWORD),
        ctypes.c_void_p,
    ]
    kernel32.ReadFile.restype = wintypes.BOOL
    kernel32.ReadFile.argtypes = [
        wintypes.HANDLE,
        wintypes.LPVOID,
        wintypes.DWORD,
        ctypes.POINTER(wintypes.DWORD),
        ctypes.c_void_p,
    ]
    kernel32.CreateEventW.restype = wintypes.HANDLE
    kernel32.CreateEventW.argtypes = [wintypes.LPVOID, wintypes.BOOL, wintypes.BOOL, wintypes.LPCWSTR]
    kernel32.WaitForSingleObject.restype = wintypes.DWORD
    kernel32.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    kernel32.GetOverlappedResult.restype = wintypes.BOOL
    kernel32.GetOverlappedResult.argtypes = [
        wintypes.HANDLE,
        ctypes.c_void_p,
        ctypes.POINTER(wintypes.DWORD),
        wintypes.BOOL,
    ]
    if hasattr(kernel32, "CancelIoEx"):
        kernel32.CancelIoEx.restype = wintypes.BOOL
        kernel32.CancelIoEx.argtypes = [wintypes.HANDLE, ctypes.c_void_p]
    if hasattr(kernel32, "CancelIo"):
        kernel32.CancelIo.restype = wintypes.BOOL
        kernel32.CancelIo.argtypes = [wintypes.HANDLE]
    kernel32.CloseHandle.restype = wintypes.BOOL
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel32.GetNamedPipeServerProcessId.restype = wintypes.BOOL
    kernel32.GetNamedPipeServerProcessId.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.ULONG)]

    # Avoid blocking indefinitely when the pipe exists but has no available server instance.
    wait_timeout = remaining_timeout_ms()
    if wait_timeout <= 0:
        raise VSBridgeError(f"Timeout waiting to connect to '{pipe_name}'", code="PIPE_TIMEOUT")
    waited = kernel32.WaitNamedPipeW(full_pipe_name, wait_timeout)
    if not waited:
        error = ctypes.get_last_error()
        error_messages = {
            2: f"Named pipe '{pipe_name}' not found.",
            5: "Access denied to named pipe. VS may be running as admin while Python is not.",
            121: f"Timeout waiting for named pipe '{pipe_name}'.",
        }
        msg = error_messages.get(error, f"Unknown error code: {error}")
        code = "PERMISSION_DENIED" if error == 5 else ("PIPE_TIMEOUT" if error == 121 else "PIPE_NOT_FOUND")
        raise VSBridgeError(f"Failed to connect to '{pipe_name}'. {msg}", code=code)
    
    # Open the pipe in overlapped mode so timed-out operations can be canceled.
    handle = kernel32.CreateFileW(
        full_pipe_name,
        GENERIC_READ | GENERIC_WRITE,
        0,  # No sharing
        None,  # Default security
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED,
        None
    )
    
    # Check for invalid handle (works correctly on both 32-bit and 64-bit)
    INVALID_HANDLE_VALUE = wintypes.HANDLE(-1).value
    if handle == INVALID_HANDLE_VALUE:
        error = ctypes.get_last_error()
        error_messages = {
            2: f"Named pipe '{pipe_name}' not found.",
            5: "Access denied to named pipe. VS may be running as admin while Python is not.",
            231: "All pipe instances are busy. Try again later.",
        }
        msg = error_messages.get(error, f"Unknown error code: {error}")
        code = "PERMISSION_DENIED" if error == 5 else ("PIPE_BUSY" if error == 231 else "PIPE_NOT_FOUND")
        raise VSBridgeError(f"Failed to connect to '{pipe_name}'. {msg}", code=code)

    server_process_id = wintypes.ULONG()
    if not kernel32.GetNamedPipeServerProcessId(handle, ctypes.byref(server_process_id)):
        _close_win_handle(kernel32, handle)
        raise VSBridgeError("Unable to verify named pipe server process", code="INVALID_TARGET")
    if server_process_id.value != registration["processId"]:
        _close_win_handle(kernel32, handle)
        raise VSBridgeError("Named pipe server PID does not match registration", code="STALE_TARGET")
    
    try:
        # Set pipe to byte mode
        PIPE_READMODE_BYTE = 0x00000000
        mode = wintypes.DWORD(PIPE_READMODE_BYTE)
        kernel32.SetNamedPipeHandleState(handle, ctypes.byref(mode), None, None)
        
        # Build request
        required_capabilities = _backend_method_capabilities(method, params)
        capability = required_capabilities[0]
        missing_capability = next(
            (value for value in required_capabilities if value not in registration["capabilities"]),
            None,
        )
        if missing_capability is not None:
            raise VSBridgeError(
                f"Capability '{missing_capability}' is disabled for this Visual Studio server",
                code="PERMISSION_DENIED",
            )
        request = {
            "protocol": PROTOCOL_VERSION,
            "id": request_id,
            "method": method,
            "params": params or {},
            "meta": {
                "authToken": registration["authToken"],
                "deadlineUnixMs": deadline_unix_ms,
                "kind": _backend_method_kind(method),
                "capability": capability,
                "clientProcessId": os.getpid(),
                "clientProcessStartTimeUtcTicks": _current_process_start_time_utc_ticks(),
                "target": {
                    "processId": registration["processId"],
                    "processStartTimeUtcTicks": registration["processStartTimeUtcTicks"],
                    "serverInstanceId": registration["serverInstanceId"],
                    "debugSessionGeneration": debug_session_generation,
                    "breakSequence": break_sequence,
                    "contextEpoch": context_epoch,
                },
            },
        }

        request_bytes = json.dumps(request).encode("utf-8")
        if len(request_bytes) > MAX_REQUEST_MESSAGE_SIZE:
            raise VSBridgeError(
                f"Request exceeds maximum size of {MAX_REQUEST_MESSAGE_SIZE} bytes",
                code="RESOURCE_LIMIT_EXCEEDED",
            )
        request_frame = struct.pack(">I", len(request_bytes)) + request_bytes
        request_buffer = ctypes.create_string_buffer(request_frame)

        write_overlapped, write_event = _create_overlapped(kernel32)
        bytes_written = wintypes.DWORD()
        try:
            write_started = True
            success = kernel32.WriteFile(
                handle,
                request_buffer,
                len(request_frame),
                ctypes.byref(bytes_written),
                ctypes.byref(write_overlapped),
            )

            if not success:
                error = ctypes.get_last_error()
                if error == ERROR_IO_PENDING:
                    write_timeout = remaining_timeout_ms()
                    if write_timeout <= 0:
                        _cancel_pending_io(kernel32, handle, write_overlapped)
                        raise VSBridgeError(
                            f"Timeout writing request to '{pipe_name}' (method: {method})",
                            code="PIPE_TIMEOUT",
                        )
                    _wait_for_overlapped_result(
                        kernel32,
                        handle,
                        write_overlapped,
                        bytes_written,
                        write_timeout,
                        "WriteFile",
                        f"Timeout writing request to '{pipe_name}' (method: {method})",
                    )
                else:
                    raise VSBridgeError(f"Failed to write to pipe. Error: {error}", code="TRANSPORT_ERROR")

            if bytes_written.value != len(request_frame):
                raise VSBridgeError(
                    f"Incomplete write to pipe '{pipe_name}' (expected {len(request_frame)}, wrote {bytes_written.value})",
                    code="TRANSPORT_ERROR",
                )
        finally:
            _close_win_handle(kernel32, write_event)
        
        # Read a 4-byte network-order length followed by the exact JSON payload.
        buffer_size = 1024 * 1024  # 1MB buffer
        buffer = ctypes.create_string_buffer(buffer_size)

        def read_exact(expected_size: int) -> bytes:
            received = bytearray()
            while len(received) < expected_size:
                remaining = remaining_timeout_ms()
                if remaining <= 0:
                    raise VSBridgeError(
                        f"Timeout waiting for response from '{pipe_name}' (method: {method})",
                        code="PIPE_TIMEOUT",
                        outcome_unknown=True,
                    )

                # A pending byte-mode ReadFile wakes on incoming data. Polling
                # PeekNamedPipe first imposed a 20ms sleep floor on each chunk.
                # Keep the OVERLAPPED and buffer alive until completion/reap.
                to_read = min(buffer_size, expected_size - len(received))
                bytes_read = wintypes.DWORD()
                read_overlapped, read_event = _create_overlapped(kernel32, outcome_unknown=True)
                try:
                    success = kernel32.ReadFile(
                        handle,
                        buffer,
                        to_read,
                        ctypes.byref(bytes_read),
                        ctypes.byref(read_overlapped),
                    )

                    if not success:
                        error = ctypes.get_last_error()
                        if error == ERROR_IO_PENDING:
                            read_timeout = remaining_timeout_ms()
                            if read_timeout <= 0:
                                _cancel_pending_io(kernel32, handle, read_overlapped)
                                raise VSBridgeError(
                                    f"Timeout waiting for response from '{pipe_name}' (method: {method})",
                                    code="PIPE_TIMEOUT",
                                    outcome_unknown=True,
                                )
                            _wait_for_overlapped_result(
                                kernel32,
                                handle,
                                read_overlapped,
                                bytes_read,
                                read_timeout,
                                "ReadFile",
                                f"Timeout waiting for response from '{pipe_name}' (method: {method})",
                                outcome_unknown=True,
                            )
                        elif error == ERROR_BROKEN_PIPE:
                            raise VSBridgeError("Pipe disconnected by server", code="PIPE_DISCONNECTED", outcome_unknown=True)
                        else:
                            raise VSBridgeError(
                                f"Failed to read from pipe. Error: {error}",
                                code="TRANSPORT_ERROR",
                                outcome_unknown=True,
                            )
                finally:
                    _close_win_handle(kernel32, read_event)

                if bytes_read.value == 0:
                    raise VSBridgeError("No data received from pipe", code="PIPE_DISCONNECTED", outcome_unknown=True)
                received.extend(ctypes.string_at(buffer, bytes_read.value))

            return bytes(received)

        response_length = struct.unpack(">I", read_exact(4))[0]
        if response_length <= 0 or response_length > MAX_RESPONSE_MESSAGE_SIZE:
            raise VSBridgeError(
                f"Invalid response frame length {response_length}; allowed range is "
                f"1..{MAX_RESPONSE_MESSAGE_SIZE} bytes",
                code="RESOURCE_LIMIT_EXCEEDED",
                outcome_unknown=True,
            )
        result, server = _decode_response(
            read_exact(response_length),
            request_id,
            expected_server=registration,
            include_server=True,
        )
        _record_instance_state(pipe_name, server)
        return (result, dict(server)) if include_server else result
    except VSBridgeError as exc:
        server_identity = getattr(exc, "server_identity", None)
        if isinstance(server_identity, dict):
            _record_instance_state(pipe_name, server_identity)
        raise _finalize_call_error(
            exc,
            is_command=is_command,
            write_started=write_started,
            request_id=request_id,
        )
    except Exception as exc:
        wrapped = VSBridgeError(
            "Unexpected failure while processing the Visual Studio response",
            code="INVALID_RESPONSE" if write_started else "TRANSPORT_ERROR",
            outcome_unknown=write_started,
        )
        raise _finalize_call_error(
            wrapped,
            is_command=is_command,
            write_started=write_started,
            request_id=request_id,
        ) from exc
    finally:
        # Always close the handle
        _close_win_handle(kernel32, handle)


class VSBridge:
    """
    Client for communicating with VS Debugger extension via Named Pipe.
    Supports multiple VS instances with automatic discovery and smart selection.
    """
    
    _SYNC_TIMEOUT_MARGIN = 0.2
    _MIN_SYNC_TIMEOUT = 0.1
    _MAX_WORKERS = 8
    _MAX_PENDING_CALLS = 128
    _MAX_PENDING_CONTROL_CALLS = 32
    _MAX_PENDING_RECOVERY_CALLS = 32
    _MAX_CANCELLATION_TASKS = 32

    def __init__(self):
        self._lock = asyncio.Lock()
        self._selected_pipe: Optional[str] = None
        self._selected_target: Optional[Dict[str, Any]] = None
        self._selected_registration: Optional[Dict[str, Any]] = None
        self._selection_generation = 0
        self._cancellation_tasks: Dict[tuple[str, str], asyncio.Task] = {}
        self._cached_instances: List[VSInstance] = []
        self._call_admission = asyncio.BoundedSemaphore(self._MAX_PENDING_CALLS)
        self._control_admission = asyncio.BoundedSemaphore(self._MAX_PENDING_CONTROL_CALLS)
        self._recovery_admission = asyncio.BoundedSemaphore(self._MAX_PENDING_RECOVERY_CALLS)
        self._executor = concurrent.futures.ThreadPoolExecutor(
            max_workers=self._MAX_WORKERS,
            thread_name_prefix="aidb-vs-bridge",
        )
        self._control_executor = concurrent.futures.ThreadPoolExecutor(
            max_workers=2,
            thread_name_prefix="aidb-vs-control",
        )
        self._recovery_executor = concurrent.futures.ThreadPoolExecutor(
            max_workers=2,
            thread_name_prefix="aidb-vs-recovery",
        )

    async def _submit_bounded(
        self,
        executor: concurrent.futures.ThreadPoolExecutor,
        admission: asyncio.BoundedSemaphore,
        function,
        args: tuple,
        deadline_monotonic: float,
        operation: str,
    ) -> asyncio.Future:
        """Bound executor backlog and charge admission wait to the request deadline."""
        remaining = deadline_monotonic - time.monotonic()
        if remaining <= 0:
            raise VSBridgeError(
                f"{operation} was not started because its timeout budget was exhausted",
                code="DEADLINE_EXCEEDED_NOT_STARTED",
            )
        try:
            await asyncio.wait_for(admission.acquire(), timeout=remaining)
        except asyncio.TimeoutError as error:
            raise VSBridgeError(
                f"{operation} was not started because the bounded executor queue is full",
                code="DEADLINE_EXCEEDED_NOT_STARTED",
            ) from error

        loop = asyncio.get_running_loop()

        def invoke():
            try:
                return function(*args)
            finally:
                # A timed-out asyncio Future may be cancelled while its worker is still
                # running. Release only from the worker so capacity reflects real work.
                try:
                    loop.call_soon_threadsafe(admission.release)
                except RuntimeError:
                    # The owning event loop is already closed; no future request can
                    # observe this semaphore, so there is nothing left to release.
                    pass

        try:
            future = loop.run_in_executor(executor, invoke)

            def observe_completion(done: asyncio.Future) -> None:
                # A shielded caller may have timed out while the worker continues.
                # Retrieve its eventual exception so asyncio does not report an
                # unobserved background failure; normal awaiters still receive it.
                try:
                    done.exception()
                except (asyncio.CancelledError, Exception):
                    pass

            future.add_done_callback(observe_completion)
            return future
        except Exception:
            admission.release()
            raise

    def _compute_timeouts(self, timeout: float) -> tuple[float, float, float]:
        request_timeout = max(float(timeout), self._MIN_SYNC_TIMEOUT)
        sync_timeout = max(self._MIN_SYNC_TIMEOUT, request_timeout - self._SYNC_TIMEOUT_MARGIN)
        guard_timeout = request_timeout
        return request_timeout, sync_timeout, guard_timeout
    
    def _sync_discover_instances(
        self,
        probe_timeout: float = 0.5,
        discovery_deadline: Optional[float] = None,
    ) -> List[VSInstance]:
        """Discover all VS instances and get their info."""
        pipe_names = _discover_vs_pipes()
        if not pipe_names:
            return []
        discovery_deadline = discovery_deadline or (time.monotonic() + max(0.1, probe_timeout))
        with _scoped_validation_deadline(discovery_deadline):
            _trusted_visual_studio_paths()

        def probe(pipe_name: str) -> Optional[VSInstance]:
            registration = None
            try:
                if time.monotonic() >= discovery_deadline:
                    return None
                probe_deadline = min(discovery_deadline, time.monotonic() + probe_timeout)
                with _scoped_validation_deadline(probe_deadline):
                    registration = _load_instance_registration(pipe_name)
                remaining = probe_deadline - time.monotonic()
                if remaining <= 0:
                    raise VSBridgeError(
                        "Visual Studio instance probe exhausted its deadline",
                        code="DEADLINE_EXCEEDED_NOT_STARTED",
                    )
                info = _sync_call_to_pipe(
                    pipe_name,
                    "get_instance_info",
                    None,
                    timeout=remaining,
                    deadline_monotonic=probe_deadline,
                    registration_snapshot=registration,
                )
                info["processStartTimeUtcTicks"] = registration["processStartTimeUtcTicks"]
                info["serverInstanceId"] = registration["serverInstanceId"]
                info["allowedCapabilities"] = list(registration["capabilities"])
                capability_set = set(registration["capabilities"])
                info["accessMode"] = (
                    "read_only"
                    if capability_set == {"server.control", "debug.read"}
                    else ("full_access" if capability_set == set(_all_vs_capabilities()) else "custom")
                )
                logger.debug("Validated Visual Studio instance registration for %s", pipe_name)
                return VSInstance(pipe_name, info, registration_snapshot=registration)
            except Exception as e:
                logger.warning("Visual Studio instance probe failed for %s: %s", pipe_name, type(e).__name__)
                # Only registrations that passed owner/process validation are visible
                # as unreachable diagnostics. Global pipe names are never candidates.
                if registration is None:
                    return None
                diagnostic = {
                    "connectionStatus": "unreachable",
                    "lastError": str(e),
                }
                diagnostic["processId"] = registration["processId"]
                diagnostic["processStartTimeUtcTicks"] = registration["processStartTimeUtcTicks"]
                diagnostic["serverInstanceId"] = registration["serverInstanceId"]
                diagnostic["allowedCapabilities"] = list(registration["capabilities"])
                capability_set = set(registration["capabilities"])
                diagnostic["accessMode"] = (
                    "read_only"
                    if capability_set == {"server.control", "debug.read"}
                    else ("full_access" if capability_set == set(_all_vs_capabilities()) else "custom")
                )
                return VSInstance(pipe_name, diagnostic, registration_snapshot=registration)

        # Discovery is a rendezvous operation, so probe stale and live registrations in
        # parallel with a small bound instead of serially paying each instance timeout.
        max_workers = min(4, len(pipe_names))
        discovery_executor = concurrent.futures.ThreadPoolExecutor(
            max_workers=max_workers,
            thread_name_prefix="aidb-vs-discovery",
        )
        pending: Dict[concurrent.futures.Future, str] = {}
        results: List[VSInstance] = []
        names = iter(pipe_names)
        try:
            for _ in range(max_workers):
                if time.monotonic() >= discovery_deadline:
                    break
                try:
                    name = next(names)
                except StopIteration:
                    break
                pending[discovery_executor.submit(probe, name)] = name

            while pending and time.monotonic() < discovery_deadline:
                remaining = discovery_deadline - time.monotonic()
                done, _ = concurrent.futures.wait(
                    tuple(pending),
                    timeout=max(0.0, remaining),
                    return_when=concurrent.futures.FIRST_COMPLETED,
                )
                if not done:
                    break
                for future in done:
                    pending.pop(future, None)
                    try:
                        instance = future.result()
                    except Exception as error:
                        logger.debug("Visual Studio discovery worker failed: %s", type(error).__name__)
                        instance = None
                    if instance is not None:
                        results.append(instance)
                    if time.monotonic() >= discovery_deadline:
                        continue
                    try:
                        name = next(names)
                    except StopIteration:
                        continue
                    pending[discovery_executor.submit(probe, name)] = name
        finally:
            for future in pending:
                future.cancel()
            discovery_executor.shutdown(wait=False, cancel_futures=True)
        return sorted(results, key=lambda instance: (instance.process_id, instance.pipe_name))
    
    async def discover_instances(self, timeout: float = DEFAULT_TIMEOUT) -> List[VSInstance]:
        """
        Discover all available VS instances.
        Returns list of VSInstance objects with their current state.
        """
        discovery_timeout = max(self._MIN_SYNC_TIMEOUT, float(timeout))
        discovery_deadline = time.monotonic() + discovery_timeout
        probe_timeout = min(0.5, max(self._MIN_SYNC_TIMEOUT, discovery_timeout - 0.05))
        try:
            future = await self._submit_bounded(
                self._executor,
                self._call_admission,
                self._sync_discover_instances,
                (probe_timeout, discovery_deadline),
                discovery_deadline,
                "Visual Studio discovery",
            )
            instances = await asyncio.wait_for(
                asyncio.shield(future),
                timeout=max(0.0, discovery_deadline - time.monotonic()),
            )
        except asyncio.TimeoutError as error:
            raise VSBridgeError(
                "Visual Studio discovery exhausted its timeout budget",
                code="DEADLINE_EXCEEDED_NOT_STARTED",
            ) from error
        async with self._lock:
            self._cached_instances = instances
        return instances
    
    def select_instance(
        self,
        pipe_name: Optional[str] = None,
        process_id: Optional[int] = None,
        target_identity: Optional[Dict[str, Any]] = None,
        registration_snapshot: Optional[Dict[str, Any]] = None,
    ) -> Optional[Dict[str, Any]]:
        """
        Select a specific VS instance to communicate with.
        
        Args:
            pipe_name: Full pipe name (e.g., 'AIDebuggerVS_1234')
            process_id: VS process ID (will be converted to pipe name)
        """
        return self._select_instance_unlocked(
            pipe_name=pipe_name,
            process_id=process_id,
            target_identity=target_identity,
            registration_snapshot=registration_snapshot,
        )

    def _select_instance_unlocked(
        self,
        pipe_name: Optional[str] = None,
        process_id: Optional[int] = None,
        target_identity: Optional[Dict[str, Any]] = None,
        registration_snapshot: Optional[Dict[str, Any]] = None,
    ) -> Optional[Dict[str, Any]]:
        resolved_pipe = f"AIDebuggerVS_{process_id}" if process_id is not None else pipe_name
        if resolved_pipe is None:
            self._selected_pipe = None
            self._selected_target = None
            self._selected_registration = None
            self._selection_generation += 1
            return None

        registration, exact_target = _bind_registration_snapshot(
            resolved_pipe,
            expected_target=target_identity,
            registration_snapshot=registration_snapshot,
        )
        self._selected_pipe = resolved_pipe
        self._selected_target = exact_target
        self._selected_registration = _copy_registration_snapshot(registration)
        self._selection_generation += 1
        return dict(exact_target)

    async def try_select_instance(
        self,
        expected_generation: int,
        pipe_name: Optional[str] = None,
        process_id: Optional[int] = None,
        target_identity: Optional[Dict[str, Any]] = None,
        registration_snapshot: Optional[Dict[str, Any]] = None,
    ) -> tuple[bool, Optional[Dict[str, Any]], int]:
        async with self._lock:
            if self._selection_generation != expected_generation:
                return False, (
                    dict(self._selected_target) if self._selected_target is not None else None
                ), self._selection_generation
            selected = self._select_instance_unlocked(
                pipe_name=pipe_name,
                process_id=process_id,
                target_identity=target_identity,
                registration_snapshot=registration_snapshot,
            )
            return True, selected, self._selection_generation

    def get_selected_registration_snapshot(self) -> Optional[Dict[str, Any]]:
        return (
            _copy_registration_snapshot(self._selected_registration)
            if self._selected_registration is not None
            else None
        )
    
    def clear_selection(self):
        """Clear the selected instance, allowing auto-selection."""
        self._selected_pipe = None
        self._selected_target = None
        self._selected_registration = None
        self._selection_generation += 1

    async def try_clear_selection(self, expected_generation: int) -> tuple[bool, int]:
        async with self._lock:
            if self._selection_generation != expected_generation:
                return False, self._selection_generation
            self.clear_selection()
            return True, self._selection_generation

    async def get_selection_snapshot(
        self,
    ) -> tuple[Optional[str], Optional[str], Optional[Dict[str, Any]], int]:
        async with self._lock:
            return (
                "vs" if self._selected_pipe is not None else None,
                self._selected_pipe,
                dict(self._selected_target) if self._selected_target is not None else None,
                self._selection_generation,
            )

    @staticmethod
    def get_candidate_instances(
        instances: List[VSInstance],
        preferred_ide: Optional[str] = None,
    ) -> tuple[List[VSInstance], str]:
        if preferred_ide not in (None, "vs", "visualstudio", "visual-studio", "visual_studio"):
            raise VSBridgeError("The Visual Studio server accepts only preferred_ide='vs'", code="INVALID_ARGUMENT")
        return list(instances), "vs"

    def auto_select_instance(
        self,
        instances: List[VSInstance],
        preferred_ide: Optional[str] = None,
    ) -> Optional[VSInstance]:
        candidates, _ = self.get_candidate_instances(instances, preferred_ide)
        return self._find_best_instance(candidates)

    @staticmethod
    def get_preferred_ide() -> str:
        return "vs"
    
    def _find_best_instance(self, instances: List[VSInstance]) -> Optional[VSInstance]:
        """
        Find the best VS instance to use.
        Priority:
        1. If only one instance is paused (in break mode), use it
        2. If only one instance is debugging, use it
        3. If only one instance exists, use it
        4. Otherwise, return None (user must select)
        """
        if not instances:
            return None

        instances = [i for i in instances if i.connection_status == "ok"]
        if not instances:
            return None
        
        # If only one instance, use it
        if len(instances) == 1:
            return instances[0]
        
        # Find paused instances (highest priority for debugging questions)
        paused = [i for i in instances if i.is_paused]
        if len(paused) == 1:
            return paused[0]
        
        # Find debugging instances
        debugging = [i for i in instances if i.is_debugging]
        if len(debugging) == 1:
            return debugging[0]
        
        # Multiple instances in same state - can't auto-select
        return None
    
    def _sync_call(
        self,
        pipe_name: str,
        method: str,
        params: Optional[Dict[str, Any]],
        timeout: float,
        request_id: str,
        deadline_monotonic: float,
        deadline_unix_ms: int,
        include_server: bool = False,
        expected_target: Optional[Dict[str, Any]] = None,
        registration_snapshot: Optional[Dict[str, Any]] = None,
    ) -> Any:
        """Synchronous call to the specified pipe."""
        return _sync_call_to_pipe(
            pipe_name,
            method,
            params,
            timeout=timeout,
            request_id=request_id,
            deadline_monotonic=deadline_monotonic,
            deadline_unix_ms=deadline_unix_ms,
            include_server=include_server,
            expected_target=expected_target,
            registration_snapshot=registration_snapshot,
        )

    async def _cancel_remote_request(
        self,
        pipe_name: str,
        request_id: str,
        target_identity: Dict[str, Any],
        registration_snapshot: Dict[str, Any],
    ) -> None:
        """Best-effort control-plane cancellation on a separate pipe connection."""
        try:
            await self.call_pipe(
                pipe_name,
                "cancel_request",
                {"requestId": request_id},
                timeout=2.0,
                expected_target=target_identity,
                registration_snapshot=registration_snapshot,
            )
        except (asyncio.CancelledError, Exception):
            # The original cancellation/timeout remains authoritative. The server-side
            # deadline still bounds the request if this recovery connection also fails.
            return

    def _schedule_remote_cancellation(
        self,
        pipe_name: str,
        request_id: str,
        target_identity: Dict[str, Any],
        registration_snapshot: Dict[str, Any],
    ) -> None:
        """Start best-effort cleanup without extending the caller's deadline/cancellation."""
        key = (pipe_name, request_id)
        if key in self._cancellation_tasks or len(self._cancellation_tasks) >= self._MAX_CANCELLATION_TASKS:
            return
        task = asyncio.create_task(self._cancel_remote_request(
            pipe_name,
            request_id,
            dict(target_identity),
            _copy_registration_snapshot(registration_snapshot),
        ))
        self._cancellation_tasks[key] = task

        def completed(finished: asyncio.Task) -> None:
            if self._cancellation_tasks.get(key) is finished:
                self._cancellation_tasks.pop(key, None)
            try:
                finished.result()
            except (asyncio.CancelledError, Exception):
                pass

        task.add_done_callback(completed)

    async def call_pipe(
        self,
        pipe_name: str,
        method: str,
        params: Optional[Dict[str, Any]] = None,
        timeout: float = DEFAULT_TIMEOUT,
        include_target: bool = False,
        expected_target: Optional[Dict[str, Any]] = None,
        registration_snapshot: Optional[Dict[str, Any]] = None,
    ) -> Any:
        """Call a method on a specific VS pipe without reading mutable global selection."""
        if not pipe_name:
            raise VSBridgeError("No Visual Studio pipe selected")
        total_timeout = max(self._MIN_SYNC_TIMEOUT, float(timeout))
        call_deadline = time.monotonic() + total_timeout
        with _scoped_validation_deadline(call_deadline):
            registration, target_identity = _bind_registration_snapshot(
                pipe_name,
                expected_target=expected_target,
                registration_snapshot=registration_snapshot,
            )
        bound_target = dict(expected_target) if expected_target is not None else dict(target_identity)
        bound_registration = dict(registration)

        if not _backend_method_session_refresh(method):
            has_session_identity = bool(_get_instance_state(
                pipe_name,
                str(bound_registration["serverInstanceId"]),
            ))
            if not has_session_identity:
                remaining = call_deadline - time.monotonic()
                if remaining <= self._MIN_SYNC_TIMEOUT:
                    raise VSBridgeError(
                        "Request was not started because target preflight exhausted its timeout budget",
                        code="DEADLINE_EXCEEDED_NOT_STARTED",
                        target_identity=target_identity,
                    )
                preflight_timeout = min(2.0, max(self._MIN_SYNC_TIMEOUT, remaining * 0.25))
                try:
                    await self.call_pipe(
                        pipe_name,
                        "get_instance_info",
                        None,
                        timeout=preflight_timeout,
                        expected_target=bound_target,
                        registration_snapshot=bound_registration,
                    )
                except VSBridgeError as error:
                    if error.code not in {
                        "PIPE_TIMEOUT",
                        "DEADLINE_EXCEEDED_NOT_STARTED",
                    }:
                        raise
                    raise VSBridgeError(
                        "Request was not started because target preflight exhausted its timeout budget",
                        code="DEADLINE_EXCEEDED_NOT_STARTED",
                        target_identity=target_identity,
                    ) from error
                remaining = call_deadline - time.monotonic()
                if remaining <= self._MIN_SYNC_TIMEOUT:
                    raise VSBridgeError(
                        "Request was not started because target preflight exhausted its timeout budget",
                        code="DEADLINE_EXCEEDED_NOT_STARTED",
                        target_identity=target_identity,
                    )
                timeout = remaining
            else:
                timeout = call_deadline - time.monotonic()
        else:
            timeout = call_deadline - time.monotonic()

        minimum_dispatch_budget = (
            0.0 if _backend_method_session_refresh(method) else self._MIN_SYNC_TIMEOUT
        )
        if timeout <= minimum_dispatch_budget:
            raise VSBridgeError(
                "Request was not started because its timeout budget was exhausted before dispatch",
                code="DEADLINE_EXCEEDED_NOT_STARTED",
                target_identity=target_identity,
            )

        request_timeout, sync_timeout, guard_timeout = self._compute_timeouts(timeout)
        guard_timeout = min(guard_timeout, max(0.0, call_deadline - time.monotonic()))
        if guard_timeout <= 0:
            raise VSBridgeError(
                "Request was not started because its timeout budget was exhausted before dispatch",
                code="DEADLINE_EXCEEDED_NOT_STARTED",
                target_identity=target_identity,
            )
        request_id = _new_request_id()
        dispatched = False
        deadline_monotonic = time.monotonic() + sync_timeout
        deadline_unix_ms = int(time.time() * 1000 + sync_timeout * 1000)
        try:
            sync_args = (
                pipe_name,
                method,
                params,
                sync_timeout,
                request_id,
                deadline_monotonic,
                deadline_unix_ms,
            )
            if method in _recovery_pipe_methods():
                executor = self._recovery_executor
                admission = self._recovery_admission
            elif method in _interrupt_pipe_methods():
                executor = self._control_executor
                admission = self._control_admission
            else:
                executor = self._executor
                admission = self._call_admission
            future = await self._submit_bounded(
                executor,
                admission,
                self._sync_call,
                sync_args + (include_target, bound_target, bound_registration),
                call_deadline,
                f"Visual Studio request '{method}'",
            )
            dispatched = True
            response = await asyncio.wait_for(
                # Do not let wait_for/caller cancellation cancel a queued executor
                # future before invoke() starts; invoke.finally owns the exact-once
                # admission release.
                asyncio.shield(future),
                timeout=min(guard_timeout, max(0.0, call_deadline - time.monotonic())),
            )
            response_result = response[0] if include_target else response
            if (
                _backend_method_kind(method) == "command"
                and isinstance(response_result, dict)
                and response_result.get("success") is False
                and response_result.get("errorCode") == "OUTCOME_UNKNOWN"
            ):
                remote_error = VSRemoteError(
                    "OUTCOME_UNKNOWN: Visual Studio accepted the command but could not determine its final outcome; query get_command_status before retrying.",
                    code="OUTCOME_UNKNOWN",
                    outcome_unknown=True,
                    details=dict(response_result),
                    target_identity=target_identity,
                )
                remote_error.request_id = request_id
                raise remote_error
            if _backend_method_kind(method) == "command":
                receipt_target = dict(target_identity)
                latest = _get_instance_state(
                    pipe_name,
                    str(bound_registration["serverInstanceId"]),
                )
                receipt_target.update({
                    key: int(latest.get(key) or 0)
                    for key in ("debugSessionGeneration", "breakSequence", "contextEpoch")
                })
                _LAST_COMMAND_RECEIPT.set({
                    "requestId": request_id,
                    "method": method,
                    "commandCompleted": True,
                    "outcomeUnknown": False,
                    "targetIdentity": receipt_target,
                })
            if not include_target:
                return response
            result, server = response
            exact_target = dict(target_identity)
            exact_target.update({
                "debugSessionGeneration": int(server.get("debugSessionGeneration") or 0),
                "breakSequence": int(server.get("breakSequence") or 0),
                "contextEpoch": int(server.get("contextEpoch") or 0),
            })
            return result, BoundVSTarget(exact_target, bound_registration)
        except asyncio.CancelledError:
            if method != "cancel_request":
                self._schedule_remote_cancellation(
                    pipe_name, request_id, bound_target, bound_registration
                )
            raise
        except asyncio.TimeoutError:
            if method != "cancel_request":
                self._schedule_remote_cancellation(
                    pipe_name, request_id, bound_target, bound_registration
                )
            error = VSBridgeError(
                f"Timeout waiting for response from VS extension (method: {method}, timeout={request_timeout:.1f}s)",
                code="PIPE_TIMEOUT",
                outcome_unknown=_backend_method_kind(method) == "command",
            )
            if error.outcome_unknown:
                error.request_id = request_id
            error.target_identity = target_identity
            raise error
        except VSBridgeError as error:
            server_identity = getattr(error, "server_identity", None)
            if isinstance(server_identity, dict):
                current_target = dict(target_identity)
                current_target.update({
                    key: int(server_identity.get(key) or 0)
                    for key in ("debugSessionGeneration", "breakSequence", "contextEpoch")
                })
                error.original_target_identity = dict(target_identity)
                error.current_target_identity = current_target
                error.target_identity = current_target
            if method != "cancel_request" and error.outcome_unknown:
                self._schedule_remote_cancellation(
                    pipe_name, request_id, bound_target, bound_registration
                )
            if error.outcome_unknown and _backend_method_kind(method) == "command":
                error.request_id = request_id
            if not isinstance(server_identity, dict):
                error.original_target_identity = dict(target_identity)
                error.current_target_identity = dict(target_identity)
                error.target_identity = dict(target_identity)
            raise
        except Exception as exc:
            is_command = _backend_method_kind(method) == "command"
            error = VSBridgeError(
                "Unexpected client-side failure while processing the Visual Studio request",
                code="TRANSPORT_ERROR",
                outcome_unknown=is_command and dispatched,
                target_identity=target_identity,
            )
            if error.outcome_unknown:
                error.request_id = request_id
                if method != "cancel_request":
                    self._schedule_remote_cancellation(
                        pipe_name, request_id, bound_target, bound_registration
                    )
            raise error from exc

    async def call_pipe_exact(
        self,
        target: Dict[str, Any],
        method: str,
        params: Optional[Dict[str, Any]] = None,
        timeout: float = DEFAULT_TIMEOUT,
        include_target: bool = False,
        registration_snapshot: Optional[Dict[str, Any]] = None,
        advance_selection: bool = True,
    ) -> Any:
        """Call only the immutable authenticated VS instance described by target.

        ``advance_selection=False`` leaves the pinned single-instance target
        untouched. Callers that must not redirect what a later bare tool call
        resolves to (for example multi-target dispatch) pass it explicitly.
        """
        call_deadline = time.monotonic() + max(self._MIN_SYNC_TIMEOUT, float(timeout))
        pipe_name = str(target.get("pipeName") or "")
        if registration_snapshot is None and isinstance(target, BoundVSTarget):
            registration_snapshot = target.registration_snapshot()
        if registration_snapshot is None:
            selected_target = self._selected_target
            if selected_target is not None and all(
                selected_target.get(key) == target.get(key)
                for key in (
                    "pipeName",
                    "processId",
                    "processStartTimeUtcTicks",
                    "serverInstanceId",
                )
            ):
                registration_snapshot = self.get_selected_registration_snapshot()
        with _scoped_validation_deadline(call_deadline):
            bound_registration, _ = _bind_registration_snapshot(
                pipe_name,
                expected_target=target,
                registration_snapshot=registration_snapshot,
            )
        remaining_timeout = call_deadline - time.monotonic()
        if remaining_timeout <= self._MIN_SYNC_TIMEOUT:
            raise VSBridgeError(
                "Request was not started because target binding exhausted its timeout budget",
                code="DEADLINE_EXCEEDED_NOT_STARTED",
                target_identity=dict(target),
            )
        context_keys = ("debugSessionGeneration", "breakSequence", "contextEpoch")
        expected_context = {key: target.get(key) for key in context_keys if key in target}
        try:
            result, response_target = await self.call_pipe(
                pipe_name,
                method,
                params,
                remaining_timeout,
                include_target=True,
                expected_target=target,
                registration_snapshot=bound_registration,
            )
        except VSBridgeError as error:
            current_target = getattr(error, "current_target_identity", None)
            if advance_selection and isinstance(current_target, dict):
                await self._advance_selected_target(target, current_target)
            raise
        if advance_selection:
            await self._advance_selected_target(target, response_target)
        if (
            expected_context
            and _backend_method_kind(method) != "command"
            and not _backend_method_session_refresh(method)
        ):
            for key, expected in expected_context.items():
                if int(response_target.get(key) or 0) != int(expected or 0):
                    error = VSBridgeError(
                        f"Pinned debug context changed while executing {method}",
                        code="STALE_DEBUG_CONTEXT",
                        target_identity=dict(response_target),
                    )
                    error.original_target_identity = dict(target)
                    error.current_target_identity = dict(response_target)
                    raise error
        return (result, response_target) if include_target else result

    async def _advance_selected_target(
        self,
        request_target: Dict[str, Any],
        response_target: Dict[str, Any],
    ) -> None:
        immutable_keys = (
            "pipeName", "processId", "processStartTimeUtcTicks", "serverInstanceId",
        )
        if not all(request_target.get(key) == response_target.get(key) for key in immutable_keys):
            return
        async with self._lock:
            current = self._selected_target
            if current is None or not all(
                current.get(key) == request_target.get(key) for key in immutable_keys
            ):
                return
            current_epoch = tuple(int(current.get(key) or 0) for key in (
                "debugSessionGeneration", "breakSequence", "contextEpoch",
            ))
            response_epoch = tuple(int(response_target.get(key) or 0) for key in (
                "debugSessionGeneration", "breakSequence", "contextEpoch",
            ))
            if response_epoch >= current_epoch:
                self._selected_target = dict(response_target)
    
    async def call(
        self,
        method: str,
        params: Optional[Dict[str, Any]] = None,
        timeout: float = DEFAULT_TIMEOUT,
        include_target: bool = False,
    ) -> Any:
        """
        Call a method on the VS Debugger extension.
        
        If a specific instance is selected, uses that.
        Otherwise, auto-discovers instances and uses smart selection.
        
        Args:
            method: The method name to call
            params: Optional parameters for the method
            timeout: Timeout in seconds
            
        Returns:
            The result from the VS extension
            
        Raises:
            VSBridgeError: If communication fails or no suitable instance found
        """
        loop = asyncio.get_running_loop()
        deadline = loop.time() + max(self._MIN_SYNC_TIMEOUT, float(timeout))
        async with self._lock:
            pipe_name = self._selected_pipe
            selected_target = dict(self._selected_target) if self._selected_target is not None else None
            selection_generation = self._selection_generation
            selected_registration = (
                _copy_registration_snapshot(self._selected_registration)
                if self._selected_registration is not None
                else None
            )

        if selected_target is not None:
            remaining = deadline - loop.time()
            if remaining <= self._MIN_SYNC_TIMEOUT:
                raise VSBridgeError(
                    "Request was not started because its timeout budget was exhausted",
                    code="DEADLINE_EXCEEDED_NOT_STARTED",
                    target_identity=selected_target,
                )
            return await self.call_pipe_exact(
                selected_target,
                method,
                params,
                remaining,
                include_target=include_target,
                registration_snapshot=selected_registration,
            )
        if pipe_name is not None:
            raise VSBridgeError(
                "Pinned Visual Studio selection has no immutable instance identity",
                code="INVALID_TARGET",
            )

        # If no specific instance selected, auto-discover and select.
        if pipe_name is None:
            remaining = deadline - loop.time()
            if remaining <= self._MIN_SYNC_TIMEOUT:
                raise VSBridgeError(
                    "Request was not started because discovery exhausted its timeout budget",
                    code="DEADLINE_EXCEEDED_NOT_STARTED",
                )
            instances = await self.discover_instances(timeout=remaining)

            if not instances:
                raise VSBridgeError(
                    "No Visual Studio instance is available. Call ensure_vs_instance() to let the "
                    "MCP server launch one (and optionally attach to a process) in a single step. "
                    "To use a Visual Studio you started yourself instead:\n"
                    "1. Start Visual Studio\n"
                    "2. Run Tools > Start AIDebugger MCP Server\n"
                    "3. Confirm the AIDebuggerVS extension is installed and enabled",
                    code="NO_VS_INSTANCE",
                )

            selected_instance = self._find_best_instance(instances)

            if selected_instance is None:
                instance_list = "\n".join([
                    f"  - PID {i.process_id}: {i.solution_name} ({i.debug_mode}) "
                    f"[{i.connection_status}{': ' + i.last_error if i.last_error else ''}]"
                    for i in instances
                ])
                raise VSBridgeError(
                    f"Multiple VS instances found or no reachable instance could be auto-selected. "
                    f"Please use 'list_vs_instances' to see all instances and "
                    f"'select_vs_instance' to choose one.\n\nAvailable instances:\n{instance_list}"
                )
            remaining = deadline - loop.time()
            if remaining <= self._MIN_SYNC_TIMEOUT:
                raise VSBridgeError(
                    "Request was not started because discovery exhausted its timeout budget",
                    code="DEADLINE_EXCEEDED_NOT_STARTED",
                )
            selected_target = selected_instance.target_identity()
            selected_registration = selected_instance.registration_snapshot()

            # Auto-selection is a session binding, not a per-call hint. Pin the
            # immutable process/start-time/server identity before issuing the
            # first tool so later mode changes or instance churn cannot silently
            # redirect the MCP session to another Visual Studio process.
            async with self._lock:
                if (
                    self._selection_generation == selection_generation
                    and self._selected_target is None
                    and self._selected_pipe is None
                ):
                    self._selected_pipe = selected_instance.pipe_name
                    self._selected_target = dict(selected_target)
                    self._selected_registration = _copy_registration_snapshot(selected_registration)
                    self._selection_generation += 1
                elif self._selected_target is not None:
                    selected_target = dict(self._selected_target)
                    selected_registration = _copy_registration_snapshot(self._selected_registration)
                else:
                    raise VSBridgeError(
                        "Visual Studio selection changed while automatic target binding was in progress; the request was not started",
                        code="TARGET_SELECTION_CHANGED",
                    )

        remaining = deadline - loop.time()
        if remaining <= self._MIN_SYNC_TIMEOUT:
            raise VSBridgeError(
                "Request was not started because discovery exhausted its timeout budget",
                code="DEADLINE_EXCEEDED_NOT_STARTED",
            )
        if selected_target is not None:
            return await self.call_pipe_exact(
                selected_target,
                method,
                params,
                remaining,
                include_target=include_target,
                registration_snapshot=selected_registration,
            )
        raise VSBridgeError(
            "Visual Studio selection has no immutable instance identity",
            code="INVALID_TARGET",
        )


# Global bridge instance
_bridge: Optional[VSBridge] = None


def get_bridge() -> VSBridge:
    """Get the global VSBridge instance."""
    global _bridge
    if _bridge is None:
        _bridge = VSBridge()
    return _bridge


async def call(method: str, params: Optional[Dict[str, Any]] = None, timeout: float = DEFAULT_TIMEOUT) -> Any:
    """
    Convenience function to call a method on the VS extension.
    
    Args:
        method: The method name to call
        params: Optional parameters for the method
        timeout: Timeout in seconds
        
    Returns:
        The result from the VS extension
    """
    bridge = get_bridge()
    return await bridge.call(method, params, timeout)


async def call_pipe(pipe_name: str, method: str, params: Optional[Dict[str, Any]] = None, timeout: float = DEFAULT_TIMEOUT) -> Any:
    """Convenience function to call a specific VS pipe without using global selection."""
    bridge = get_bridge()
    return await bridge.call_pipe(pipe_name, method, params, timeout)


async def call_pipe_with_target(
    pipe_name: str,
    method: str,
    params: Optional[Dict[str, Any]] = None,
    timeout: float = DEFAULT_TIMEOUT,
) -> Any:
    """Call one VS instance and return the exact server/debug context that produced the result."""
    bridge = get_bridge()
    return await bridge.call_pipe(pipe_name, method, params, timeout, include_target=True)


async def call_pipe_exact(
    target: Dict[str, Any],
    method: str,
    params: Optional[Dict[str, Any]] = None,
    timeout: float = DEFAULT_TIMEOUT,
    include_target: bool = False,
    registration_snapshot: Optional[Dict[str, Any]] = None,
    advance_selection: bool = True,
) -> Any:
    """Call the exact authenticated server instance recorded for lost-response recovery."""
    return await get_bridge().call_pipe_exact(
        target,
        method,
        params,
        timeout,
        include_target=include_target,
        registration_snapshot=registration_snapshot,
        advance_selection=advance_selection,
    )


async def discover_instances(timeout: float = DEFAULT_TIMEOUT) -> List[VSInstance]:
    """
    Discover all available VS instances.
    
    Returns:
        List of VSInstance objects
    """
    bridge = get_bridge()
    return await bridge.discover_instances(timeout=timeout)


def select_instance(
    pipe_name: Optional[str] = None,
    process_id: Optional[int] = None,
    target_identity: Optional[Dict[str, Any]] = None,
    registration_snapshot: Optional[Dict[str, Any]] = None,
) -> Optional[Dict[str, Any]]:
    """
    Select a specific VS instance to use.
    
    Args:
        pipe_name: Full pipe name (e.g., 'AIDebuggerVS_1234')
        process_id: VS process ID
    """
    bridge = get_bridge()
    return bridge.select_instance(
        pipe_name=pipe_name,
        process_id=process_id,
        target_identity=target_identity,
        registration_snapshot=registration_snapshot,
    )


def get_selected_registration_snapshot() -> Optional[Dict[str, Any]]:
    """Return a private copy for the unified bridge; never serialize this value."""
    return get_bridge().get_selected_registration_snapshot()


def instance_target(target: Dict[str, Any]) -> Dict[str, Any]:
    """Drop debug-context fields while preserving a private binding when available."""
    if isinstance(target, BoundVSTarget):
        return target.instance_target()
    return {
        key: target[key]
        for key in (
            "pipeName",
            "processId",
            "processStartTimeUtcTicks",
            "serverInstanceId",
        )
    }


def clear_selection():
    """Clear the selected instance, allowing auto-selection."""
    bridge = get_bridge()
    bridge.clear_selection()


async def is_vs_available() -> bool:
    """Check if any VS extension is available."""
    try:
        instances = await discover_instances()
        return any(i.connection_status == "ok" for i in instances)
    except:
        return False
