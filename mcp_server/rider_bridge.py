"""Authenticated AIDB/2 named-pipe client for the Rider debugger plugin."""

import asyncio
import concurrent.futures
import ctypes
from ctypes import wintypes
import heapq
import json
import logging
import os
import pathlib
import re
import stat
import struct
import threading
import time
import uuid
from typing import Any, Dict, List, Optional

try:
    from .rider_errors import RiderBridgeError
    from .rider_trust import _current_process_start_time_utc_ticks, _load_rider_registration
    from .tool_registry import backend_method_capability, backend_method_kind
except ImportError:
    from rider_errors import RiderBridgeError
    from rider_trust import _current_process_start_time_utc_ticks, _load_rider_registration
    from tool_registry import backend_method_capability, backend_method_kind

logger = logging.getLogger(__name__)

PIPE_NAME_PREFIX = r"\\.\pipe\AIDebuggerRider"
PROTOCOL_VERSION = "aidb/2"
DEFAULT_TIMEOUT = 10.0
MAX_REQUEST_MESSAGE_SIZE = 4 * 1024 * 1024
MAX_RESPONSE_MESSAGE_SIZE = 16 * 1024 * 1024
MAX_JSON_NESTING_DEPTH = 64
MAX_DISCOVERY_CANDIDATES = 64
MAX_DISCOVERY_DIRECTORY_ENTRIES = 1024

FILE_FLAG_OVERLAPPED = 0x40000000
ERROR_BROKEN_PIPE = 109
ERROR_IO_PENDING = 997
ERROR_OPERATION_ABORTED = 995
ERROR_NOT_FOUND = 1168
WAIT_OBJECT_0 = 0
WAIT_TIMEOUT = 258
ULONG_PTR = ctypes.c_ulonglong if ctypes.sizeof(ctypes.c_void_p) == 8 else ctypes.c_ulong

_UUID7_LOCK = threading.Lock()
_UUID7_LAST_MILLISECONDS = -1
_UUID7_RANDOM_SEQUENCE = 0


class OVERLAPPED(ctypes.Structure):
    _fields_ = [
        ("Internal", ULONG_PTR),
        ("InternalHigh", ULONG_PTR),
        ("Offset", wintypes.DWORD),
        ("OffsetHigh", wintypes.DWORD),
        ("hEvent", wintypes.HANDLE),
    ]


def _new_request_id() -> str:
    """Generate a process-monotonic UUIDv7 for Rider command tombstones."""
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


def _copy_registration(registration: Dict[str, Any]) -> Dict[str, Any]:
    return {
        "protocol": registration.get("protocol"),
        "pipeName": registration.get("pipeName"),
        "processId": registration.get("processId"),
        "processStartTimeUtcTicks": registration.get("processStartTimeUtcTicks"),
        "serverInstanceId": registration.get("serverInstanceId"),
        "protocolCatalogHash": registration.get("protocolCatalogHash"),
        "authToken": registration.get("authToken"),
        "capabilities": list(registration.get("capabilities") or []),
        "debugSessionGeneration": int(registration.get("debugSessionGeneration") or 0),
        "breakSequence": int(registration.get("breakSequence") or 0),
        "contextEpoch": int(registration.get("contextEpoch") or 0),
        "identityKnown": bool(registration.get("identityKnown", False)),
    }


def _target_identity(registration: Dict[str, Any]) -> Dict[str, Any]:
    return {
        "pipeName": registration["pipeName"],
        "processId": registration["processId"],
        "processStartTimeUtcTicks": registration["processStartTimeUtcTicks"],
        "serverInstanceId": registration["serverInstanceId"],
        "protocolCatalogHash": registration["protocolCatalogHash"],
        "debugSessionGeneration": int(registration.get("debugSessionGeneration") or 0),
        "breakSequence": int(registration.get("breakSequence") or 0),
        "contextEpoch": int(registration.get("contextEpoch") or 0),
    }


class RiderInstance:
    """A discovered Rider process plus its authenticated registration snapshot."""

    def __init__(
        self,
        pipe_name: str,
        info: Optional[Dict[str, Any]] = None,
        registration_snapshot: Optional[Dict[str, Any]] = None,
    ):
        self.pipe_name = pipe_name
        self.process_id = 0
        self.solution_name = ""
        self.solution_path = ""
        self.is_debugging = False
        self.is_paused = False
        self.debug_mode = ""
        self.current_process = ""
        self.ide_name = ""
        self.ide_version = ""
        self.connection_status = "ok"
        self.last_error = ""
        self.process_start_time_utc_ticks = None
        self.server_instance_id = None
        self.allowed_capabilities: List[str] = []
        self.access_mode = "full"
        self._registration_snapshot = (
            _copy_registration(registration_snapshot)
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
            self.ide_name = info.get("ideName", info.get("riderName", "Rider"))
            self.ide_version = info.get("ideVersion", info.get("riderVersion", ""))
            self.connection_status = info.get("connectionStatus", self.connection_status)
            self.last_error = info.get("lastError", self.last_error)
        if self.process_id == 0 and self._registration_snapshot is not None:
            self.process_id = int(self._registration_snapshot["processId"])
        if self._registration_snapshot is not None:
            self.process_start_time_utc_ticks = self._registration_snapshot["processStartTimeUtcTicks"]
            self.server_instance_id = self._registration_snapshot["serverInstanceId"]
            self.allowed_capabilities = list(self._registration_snapshot["capabilities"])
        if self.process_id == 0 and pipe_name.startswith("AIDebuggerRider_"):
            try:
                self.process_id = int(pipe_name.rsplit("_", 1)[1])
            except (ValueError, IndexError):
                pass

    def registration_snapshot(self) -> Dict[str, Any]:
        if self._registration_snapshot is None:
            raise RiderBridgeError(
                "Rider instance has no authenticated registration snapshot",
                code="INVALID_TARGET",
            )
        return _copy_registration(self._registration_snapshot)

    def target_identity(self) -> Dict[str, Any]:
        return _target_identity(self.registration_snapshot())

    def to_dict(self) -> Dict[str, Any]:
        result = {
            "pipeName": self.pipe_name,
            "processId": self.process_id,
            "solutionName": self.solution_name,
            "solutionPath": self.solution_path,
            "isDebugging": self.is_debugging,
            "isPaused": self.is_paused,
            "debugMode": self.debug_mode,
            "currentProcess": self.current_process,
            "ideName": self.ide_name,
            "ideVersion": self.ide_version,
            "connectionStatus": self.connection_status,
            "lastError": self.last_error,
        }
        if self._registration_snapshot is not None:
            result.update({
                "processStartTimeUtcTicks": self.process_start_time_utc_ticks,
                "serverInstanceId": self.server_instance_id,
                "protocolCatalogHash": self._registration_snapshot["protocolCatalogHash"],
            })
        return result

    def __repr__(self):
        status = "Paused" if self.is_paused else ("Debugging" if self.is_debugging else "Idle")
        return f"RiderInstance(pid={self.process_id}, solution='{self.solution_name}', status={status})"


def _discover_rider_pipes() -> List[str]:
    """Enumerate only protected owner-registration rendezvous files."""
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
                match = re.fullmatch(r"(AIDebuggerRider_([1-9][0-9]*))\.json", entry.name)
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
                    candidate = (entry_metadata.st_mtime_ns, int(match.group(2)), match.group(1))
                    if len(newest_candidates) < MAX_DISCOVERY_CANDIDATES:
                        heapq.heappush(newest_candidates, candidate)
                    elif candidate > newest_candidates[0]:
                        heapq.heapreplace(newest_candidates, candidate)
                except OSError:
                    continue
        if scanned_count > MAX_DISCOVERY_DIRECTORY_ENTRIES:
            logger.warning(
                "Rider registration directory contains %d entries; all were scanned with bounded memory",
                scanned_count,
            )
        newest_candidates.sort(key=lambda item: (-item[0], -item[1]))
        return [item[2] for item in newest_candidates]
    except OSError as error:
        logger.debug("Rider registration rendezvous discovery failed: %s", type(error).__name__)
        return []


def _close_win_handle(kernel32: ctypes.WinDLL, handle: wintypes.HANDLE) -> None:
    if handle:
        kernel32.CloseHandle(handle)


def _create_overlapped(kernel32: ctypes.WinDLL) -> tuple[OVERLAPPED, wintypes.HANDLE]:
    event = kernel32.CreateEventW(None, True, False, None)
    if not event:
        raise RiderBridgeError(
            f"Failed to create overlapped event. Error: {ctypes.get_last_error()}",
            code="TRANSPORT_ERROR",
        )
    overlapped = OVERLAPPED()
    overlapped.hEvent = event
    return overlapped, event


def _cancel_pending_io(kernel32: ctypes.WinDLL, handle: wintypes.HANDLE, overlapped: OVERLAPPED) -> None:
    if hasattr(kernel32, "CancelIoEx"):
        cancelled = bool(kernel32.CancelIoEx(handle, ctypes.byref(overlapped)))
        if not cancelled and ctypes.get_last_error() != ERROR_NOT_FOUND:
            raise RiderBridgeError(
                f"CancelIoEx failed. Error: {ctypes.get_last_error()}",
                code="TRANSPORT_ERROR",
            )
    elif hasattr(kernel32, "CancelIo"):
        if not kernel32.CancelIo(handle):
            raise RiderBridgeError(
                f"CancelIo failed. Error: {ctypes.get_last_error()}",
                code="TRANSPORT_ERROR",
            )
    completed = wintypes.DWORD()
    kernel32.GetOverlappedResult(
        handle,
        ctypes.byref(overlapped),
        ctypes.byref(completed),
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
) -> int:
    wait_result = kernel32.WaitForSingleObject(overlapped.hEvent, timeout_ms)
    if wait_result == WAIT_OBJECT_0:
        ok = kernel32.GetOverlappedResult(
            handle,
            ctypes.byref(overlapped),
            ctypes.byref(bytes_transferred),
            False,
        )
        if not ok:
            error = ctypes.get_last_error()
            if error in (ERROR_BROKEN_PIPE, ERROR_OPERATION_ABORTED):
                raise RiderBridgeError("Pipe disconnected by server", code="PIPE_DISCONNECTED")
            raise RiderBridgeError(f"{operation} failed. Error: {error}", code="TRANSPORT_ERROR")
        return bytes_transferred.value
    if wait_result == WAIT_TIMEOUT:
        _cancel_pending_io(kernel32, handle, overlapped)
        raise RiderBridgeError(timeout_message, code="PIPE_TIMEOUT")
    _cancel_pending_io(kernel32, handle, overlapped)
    raise RiderBridgeError(
        f"{operation} wait failed. Error: {ctypes.get_last_error()}",
        code="TRANSPORT_ERROR",
    )


def _validate_json_nesting(source: str) -> None:
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
                raise RiderBridgeError("Rider response JSON nesting exceeds the limit", code="INVALID_RESPONSE")
        elif value in "]}":
            depth -= 1
            if depth < 0:
                raise RiderBridgeError("Rider response JSON delimiters are unbalanced", code="INVALID_RESPONSE")
    if in_string or depth != 0:
        raise RiderBridgeError("Rider response JSON is incomplete", code="INVALID_RESPONSE")


def _strict_json_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise RiderBridgeError(
                f"Rider response contains duplicate JSON property {key!r}",
                code="INVALID_RESPONSE",
            )
        result[key] = value
    return result


def _configure_kernel32(kernel32: ctypes.WinDLL) -> None:
    kernel32.CreateFileW.restype = wintypes.HANDLE
    kernel32.CreateFileW.argtypes = [
        wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, wintypes.LPVOID,
        wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE,
    ]
    kernel32.WaitNamedPipeW.restype = wintypes.BOOL
    kernel32.WaitNamedPipeW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD]
    kernel32.PeekNamedPipe.restype = wintypes.BOOL
    kernel32.PeekNamedPipe.argtypes = [
        wintypes.HANDLE, wintypes.LPVOID, wintypes.DWORD, wintypes.LPVOID,
        ctypes.POINTER(wintypes.DWORD), wintypes.LPVOID,
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
    kernel32.GetNamedPipeServerProcessId.restype = wintypes.BOOL
    kernel32.GetNamedPipeServerProcessId.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
    kernel32.CloseHandle.restype = wintypes.BOOL
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]


def _bind_registration(
    pipe_name: str,
    registration_snapshot: Optional[Dict[str, Any]],
) -> Dict[str, Any]:
    live = _load_rider_registration(pipe_name)
    if registration_snapshot is not None:
        for key in (
            "pipeName", "processId", "processStartTimeUtcTicks", "serverInstanceId",
            "protocolCatalogHash", "authToken", "capabilities",
        ):
            if live.get(key) != registration_snapshot.get(key):
                raise RiderBridgeError(
                    f"Pinned Rider target identity changed for {key}",
                    code="STALE_TARGET",
                    target_identity=_target_identity(registration_snapshot),
                )
    bound = _copy_registration(live)
    if registration_snapshot is not None:
        for key in ("debugSessionGeneration", "breakSequence", "contextEpoch"):
            bound[key] = int(registration_snapshot.get(key) or 0)
        bound["identityKnown"] = bool(registration_snapshot.get("identityKnown", False))
    return bound


def _registration_with_server_identity(
    registration: Dict[str, Any],
    server: Dict[str, Any],
) -> Dict[str, Any]:
    updated = _copy_registration(registration)
    for key in ("debugSessionGeneration", "breakSequence", "contextEpoch"):
        value = server.get(key)
        if isinstance(value, bool) or not isinstance(value, int) or value < 0:
            raise RiderBridgeError(
                f"Rider response contains invalid {key}",
                code="INVALID_RESPONSE",
                target_identity=_target_identity(registration),
            )
        updated[key] = value
    updated["identityKnown"] = True
    return updated


def _sync_call_to_pipe(
    pipe_name: str,
    method: str,
    params: Optional[Dict[str, Any]],
    timeout: float = 5.0,
    request_id: Optional[str] = None,
    registration_snapshot: Optional[Dict[str, Any]] = None,
    return_registration_snapshot: bool = False,
) -> Any:
    timeout = max(float(timeout), 0.1)
    deadline = time.monotonic() + timeout
    deadline_unix_ms = int(time.time() * 1000 + timeout * 1000)
    request_id = request_id or _new_request_id()
    is_command = backend_method_kind(method) == "command"
    write_started = False
    registration = _bind_registration(pipe_name, registration_snapshot)
    target = _target_identity(registration)

    def remaining_timeout_ms() -> int:
        remaining = deadline - time.monotonic()
        return max(0, int(remaining * 1000))

    def transport_failure(error: RiderBridgeError) -> RiderBridgeError:
        return RiderBridgeError(
            str(error),
            code=error.code,
            outcome_unknown=is_command and write_started,
            request_id=request_id if is_command and write_started else None,
            target_identity=target,
        )

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    _configure_kernel32(kernel32)
    full_pipe_name = f"\\\\.\\pipe\\{pipe_name}"
    wait_timeout = remaining_timeout_ms()
    if wait_timeout <= 0:
        raise RiderBridgeError("Rider request deadline elapsed before connection", code="DEADLINE_EXCEEDED_NOT_STARTED")
    if not kernel32.WaitNamedPipeW(full_pipe_name, wait_timeout):
        error = ctypes.get_last_error()
        messages = {
            2: f"Named pipe '{pipe_name}' not found.",
            5: "Access denied to the owner-only Rider named pipe.",
            121: f"Timeout waiting for named pipe '{pipe_name}'.",
        }
        raise RiderBridgeError(
            messages.get(error, f"Failed to wait for Rider named pipe. Error: {error}"),
            code="PIPE_NOT_FOUND" if error == 2 else ("PIPE_TIMEOUT" if error == 121 else "TRANSPORT_ERROR"),
            target_identity=target,
        )

    handle = kernel32.CreateFileW(
        full_pipe_name,
        0x80000000 | 0x40000000,
        0,
        None,
        3,
        0x80 | FILE_FLAG_OVERLAPPED,
        None,
    )
    if handle == wintypes.HANDLE(-1).value:
        error = ctypes.get_last_error()
        raise RiderBridgeError(
            f"Failed to connect to '{pipe_name}'. Error: {error}",
            code="PIPE_BUSY" if error == 231 else "TRANSPORT_ERROR",
            target_identity=target,
        )

    try:
        server_process_id = wintypes.DWORD()
        if not kernel32.GetNamedPipeServerProcessId(handle, ctypes.byref(server_process_id)):
            raise RiderBridgeError("Unable to verify Rider pipe server PID", code="INVALID_TARGET")
        if server_process_id.value != registration["processId"]:
            raise RiderBridgeError("Rider pipe server PID does not match registration", code="STALE_TARGET")

        capability = backend_method_capability(method)
        if capability not in registration["capabilities"]:
            raise RiderBridgeError(
                f"Capability '{capability}' is disabled for this Rider server",
                code="PERMISSION_DENIED",
                target_identity=target,
            )
        request = {
            "protocol": PROTOCOL_VERSION,
            "id": request_id,
            "method": method,
            "params": params or {},
            "meta": {
                "authToken": registration["authToken"],
                "deadlineUnixMs": deadline_unix_ms,
                "kind": backend_method_kind(method),
                "capability": capability,
                "clientProcessId": os.getpid(),
                "clientProcessStartTimeUtcTicks": _current_process_start_time_utc_ticks(),
                "target": {
                    "processId": registration["processId"],
                    "processStartTimeUtcTicks": registration["processStartTimeUtcTicks"],
                    "serverInstanceId": registration["serverInstanceId"],
                    "debugSessionGeneration": int(registration.get("debugSessionGeneration") or 0),
                    "breakSequence": int(registration.get("breakSequence") or 0),
                    "contextEpoch": int(registration.get("contextEpoch") or 0),
                },
            },
        }
        payload = json.dumps(request, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
        if len(payload) > MAX_REQUEST_MESSAGE_SIZE:
            raise RiderBridgeError("Rider request exceeds the protocol size limit", code="RESOURCE_LIMIT_EXCEEDED")
        frame = struct.pack(">I", len(payload)) + payload
        buffer = ctypes.create_string_buffer(frame)
        written = wintypes.DWORD()
        overlapped, event = _create_overlapped(kernel32)
        try:
            write_started = True
            success = kernel32.WriteFile(
                handle, buffer, len(frame), ctypes.byref(written), ctypes.byref(overlapped)
            )
            if not success:
                error = ctypes.get_last_error()
                if error != ERROR_IO_PENDING:
                    raise RiderBridgeError(f"Failed to write Rider request. Error: {error}", code="TRANSPORT_ERROR")
                timeout_ms = remaining_timeout_ms()
                if timeout_ms <= 0:
                    _cancel_pending_io(kernel32, handle, overlapped)
                    raise RiderBridgeError("Timeout writing Rider request", code="PIPE_TIMEOUT")
                _wait_for_overlapped_result(
                    kernel32, handle, overlapped, written, timeout_ms,
                    "WriteFile", "Timeout writing Rider request",
                )
            if written.value != len(frame):
                raise RiderBridgeError("Incomplete Rider request write", code="TRANSPORT_ERROR")
        except RiderBridgeError as error:
            raise transport_failure(error) from error
        finally:
            _close_win_handle(kernel32, event)

        def read_exact(size: int) -> bytes:
            output = bytearray()
            while len(output) < size:
                timeout_ms = remaining_timeout_ms()
                if timeout_ms <= 0:
                    raise RiderBridgeError("Timeout waiting for Rider response", code="PIPE_TIMEOUT")
                available = wintypes.DWORD()
                if not kernel32.PeekNamedPipe(handle, None, 0, None, ctypes.byref(available), None):
                    error = ctypes.get_last_error()
                    if error == ERROR_BROKEN_PIPE:
                        raise RiderBridgeError("Pipe disconnected by server", code="PIPE_DISCONNECTED")
                    raise RiderBridgeError(f"Failed to peek Rider pipe. Error: {error}", code="TRANSPORT_ERROR")
                if available.value == 0:
                    time.sleep(min(0.02, timeout_ms / 1000.0))
                    continue
                amount = min(size - len(output), available.value, 64 * 1024)
                chunk = ctypes.create_string_buffer(amount)
                read = wintypes.DWORD()
                read_overlapped, read_event = _create_overlapped(kernel32)
                try:
                    success = kernel32.ReadFile(
                        handle, chunk, amount, ctypes.byref(read), ctypes.byref(read_overlapped)
                    )
                    if not success:
                        error = ctypes.get_last_error()
                        if error == ERROR_IO_PENDING:
                            _wait_for_overlapped_result(
                                kernel32, handle, read_overlapped, read, remaining_timeout_ms(),
                                "ReadFile", "Timeout waiting for Rider response",
                            )
                        elif error == ERROR_BROKEN_PIPE:
                            raise RiderBridgeError("Pipe disconnected by server", code="PIPE_DISCONNECTED")
                        else:
                            raise RiderBridgeError(f"Failed to read Rider response. Error: {error}", code="TRANSPORT_ERROR")
                    if read.value <= 0:
                        raise RiderBridgeError("No data received from Rider pipe", code="PIPE_DISCONNECTED")
                    output.extend(chunk.raw[:read.value])
                finally:
                    _close_win_handle(kernel32, read_event)
            return bytes(output)

        try:
            response_size = struct.unpack(">I", read_exact(4))[0]
            if response_size <= 0 or response_size > MAX_RESPONSE_MESSAGE_SIZE:
                raise RiderBridgeError("Rider response frame exceeds the size limit", code="INVALID_RESPONSE")
            response_text = read_exact(response_size).decode("utf-8", errors="strict")
            _validate_json_nesting(response_text)
            response = json.loads(response_text, object_pairs_hook=_strict_json_object)
        except (UnicodeError, json.JSONDecodeError) as error:
            raise transport_failure(RiderBridgeError("Rider returned invalid JSON", code="INVALID_RESPONSE")) from error
        except RiderBridgeError as error:
            raise transport_failure(error) from error

        if not isinstance(response, dict) or response.get("protocol") != PROTOCOL_VERSION:
            raise transport_failure(RiderBridgeError("Rider response protocol mismatch", code="PROTOCOL_MISMATCH"))
        if response.get("id") != request_id:
            raise transport_failure(RiderBridgeError("Rider response request id mismatch", code="RESPONSE_ID_MISMATCH"))
        server = response.get("server")
        if not isinstance(server, dict):
            raise transport_failure(RiderBridgeError("Rider response has no server identity", code="INVALID_RESPONSE"))
        expected_server = {
            "processId": registration["processId"],
            "processStartTimeUtcTicks": registration["processStartTimeUtcTicks"],
            "serverInstanceId": registration["serverInstanceId"],
            "protocolCatalogHash": registration["protocolCatalogHash"],
        }
        if any(server.get(key) != value for key, value in expected_server.items()):
            raise transport_failure(RiderBridgeError("Rider response server identity mismatch", code="STALE_TARGET"))
        completed_registration = _registration_with_server_identity(registration, server)
        error = response.get("error")
        if error:
            code = str(error.get("code") or "REMOTE_ERROR")
            outcome_unknown = bool(error.get("outcomeUnknown")) or code == "OUTCOME_UNKNOWN"
            raise RiderBridgeError(
                f"{code}: {error.get('message', 'Unknown Rider error')}",
                code=code,
                outcome_unknown=outcome_unknown,
                request_id=error.get("requestId") or (request_id if outcome_unknown else None),
                details=error.get("details") if isinstance(error.get("details"), dict) else None,
                target_identity=_target_identity(completed_registration),
            )
        result = response.get("result")
        return (result, completed_registration) if return_registration_snapshot else result
    finally:
        _close_win_handle(kernel32, handle)


class RiderBridge:
    """Async multi-instance facade over exact authenticated Rider registrations."""

    _CALL_TIMEOUT_GUARD = 0.75
    _SYNC_TIMEOUT_MARGIN = 0.2
    _MIN_SYNC_TIMEOUT = 0.1
    _MAX_WORKERS = 8

    def __init__(self):
        self._lock = asyncio.Lock()
        self._selected_pipe: Optional[str] = None
        self._selected_registration: Optional[Dict[str, Any]] = None
        self._cached_instances: List[RiderInstance] = []
        self._executor = concurrent.futures.ThreadPoolExecutor(
            max_workers=self._MAX_WORKERS,
            thread_name_prefix="aidb-rider-bridge",
        )

    def _compute_timeouts(self, timeout: float) -> tuple[float, float, float]:
        request_timeout = max(float(timeout), self._MIN_SYNC_TIMEOUT)
        sync_timeout = max(self._MIN_SYNC_TIMEOUT, request_timeout - self._SYNC_TIMEOUT_MARGIN)
        guard_timeout = request_timeout + self._CALL_TIMEOUT_GUARD
        return request_timeout, sync_timeout, guard_timeout

    def _sync_discover_instances(self) -> List[RiderInstance]:
        instances = []
        for pipe_name in _discover_rider_pipes():
            registration = None
            try:
                registration = _load_rider_registration(pipe_name)
                info, registration = _sync_call_to_pipe(
                    pipe_name,
                    "get_instance_info",
                    None,
                    timeout=3.0,
                    registration_snapshot=registration,
                    return_registration_snapshot=True,
                )
                instances.append(RiderInstance(pipe_name, info, registration))
            except Exception as error:
                logger.warning("Failed to validate Rider instance %s: %s", pipe_name, error)
                if registration is not None:
                    instances.append(RiderInstance(pipe_name, {
                        "processId": registration["processId"],
                        "connectionStatus": "unreachable",
                        "lastError": str(error),
                    }, registration))
        return instances

    async def discover_instances(self) -> List[RiderInstance]:
        loop = asyncio.get_running_loop()
        _, _, guard_timeout = self._compute_timeouts(DEFAULT_TIMEOUT)
        instances = await asyncio.wait_for(
            loop.run_in_executor(self._executor, self._sync_discover_instances),
            timeout=guard_timeout,
        )
        async with self._lock:
            self._cached_instances = instances
        return instances

    def select_instance(
        self,
        pipe_name: Optional[str] = None,
        process_id: Optional[int] = None,
        registration_snapshot: Optional[Dict[str, Any]] = None,
        target_identity: Optional[Dict[str, Any]] = None,
    ):
        if process_id is not None:
            expected_pipe_name = f"AIDebuggerRider_{process_id}"
            if pipe_name is not None and pipe_name != expected_pipe_name:
                raise RiderBridgeError(
                    "Rider pipe name does not match the selected process id",
                    code="INVALID_TARGET",
                )
            pipe_name = expected_pipe_name
        if pipe_name is None:
            self._selected_pipe = None
            self._selected_registration = None
            return
        registration = _bind_registration(pipe_name, registration_snapshot)
        if target_identity is not None:
            immutable_keys = (
                "pipeName", "processId", "processStartTimeUtcTicks", "serverInstanceId",
            )
            if not all(
                target_identity.get(key) == registration.get(key)
                for key in immutable_keys
            ):
                raise RiderBridgeError(
                    "Pinned Rider target identity changed before selection",
                    code="STALE_TARGET",
                    target_identity=dict(target_identity),
                )
        self._selected_pipe = pipe_name
        self._selected_registration = _copy_registration(registration)

    def clear_selection(self):
        self._selected_pipe = None
        self._selected_registration = None

    def get_selected_registration_snapshot(self) -> Optional[Dict[str, Any]]:
        return (
            _copy_registration(self._selected_registration)
            if self._selected_registration is not None
            else None
        )

    async def _advance_selected_from_error(
        self,
        pipe_name: str,
        error: RiderBridgeError,
    ) -> None:
        target = error.target_identity
        if not isinstance(target, dict):
            return
        immutable_keys = (
            "pipeName", "processId", "processStartTimeUtcTicks", "serverInstanceId",
        )
        async with self._lock:
            current = self._selected_registration
            if pipe_name != self._selected_pipe or current is None:
                return
            if not all(current.get(key) == target.get(key) for key in immutable_keys):
                return
            current_epoch = tuple(
                int(current.get(key) or 0)
                for key in ("debugSessionGeneration", "breakSequence", "contextEpoch")
            )
            target_epoch = tuple(
                int(target.get(key) or 0)
                for key in ("debugSessionGeneration", "breakSequence", "contextEpoch")
            )
            if target_epoch < current_epoch:
                return
            for key in ("debugSessionGeneration", "breakSequence", "contextEpoch"):
                value = target.get(key)
                if isinstance(value, int) and not isinstance(value, bool) and value >= 0:
                    current[key] = value
            current["identityKnown"] = True

    def _find_best_instance(self, instances: List[RiderInstance]) -> Optional[RiderInstance]:
        instances = [i for i in instances if i.connection_status == "ok"]
        if len(instances) == 1:
            return instances[0]
        paused = [i for i in instances if i.is_paused]
        if len(paused) == 1:
            return paused[0]
        debugging = [i for i in instances if i.is_debugging]
        if len(debugging) == 1:
            return debugging[0]
        return None

    def _sync_call(
        self,
        pipe_name: str,
        method: str,
        params: Optional[Dict[str, Any]],
        timeout: float,
        request_id: str,
        registration: Optional[Dict[str, Any]],
    ) -> Any:
        if (
            (registration is None or not registration.get("identityKnown"))
            and method not in {"ping", "get_instance_info", "get_debug_status", "get_dispatcher_health", "get_request_status", "cancel_request"}
        ):
            _, registration = _sync_call_to_pipe(
                pipe_name,
                "get_instance_info",
                {},
                timeout=min(timeout, 3.0),
                registration_snapshot=registration,
                return_registration_snapshot=True,
            )
        return _sync_call_to_pipe(
            pipe_name,
            method,
            params,
            timeout=timeout,
            request_id=request_id,
            registration_snapshot=registration,
            return_registration_snapshot=True,
        )

    async def call_pipe(
        self,
        pipe_name: str,
        method: str,
        params: Optional[Dict[str, Any]] = None,
        timeout: float = DEFAULT_TIMEOUT,
        _registration_snapshot: Optional[Dict[str, Any]] = None,
    ) -> Any:
        if not pipe_name:
            raise RiderBridgeError("No Rider pipe selected", code="INVALID_TARGET")
        request_timeout, sync_timeout, guard_timeout = self._compute_timeouts(timeout)
        request_id = _new_request_id()
        async with self._lock:
            registration = (
                _copy_registration(_registration_snapshot)
                if _registration_snapshot is not None
                else (
                    _copy_registration(self._selected_registration)
                    if pipe_name == self._selected_pipe and self._selected_registration is not None
                    else None
                )
            )
        try:
            loop = asyncio.get_running_loop()
            future = loop.run_in_executor(
                self._executor,
                self._sync_call,
                pipe_name,
                method,
                params,
                sync_timeout,
                request_id,
                registration,
            )
            result, completed_registration = await asyncio.wait_for(future, timeout=guard_timeout)
            async with self._lock:
                if pipe_name == self._selected_pipe and self._selected_registration is not None:
                    expected = self._selected_registration
                    if all(
                        expected.get(key) == completed_registration.get(key)
                        for key in ("pipeName", "processId", "processStartTimeUtcTicks", "serverInstanceId")
                    ):
                        expected_epoch = tuple(
                            int(expected.get(key) or 0)
                            for key in ("debugSessionGeneration", "breakSequence", "contextEpoch")
                        )
                        completed_epoch = tuple(
                            int(completed_registration.get(key) or 0)
                            for key in ("debugSessionGeneration", "breakSequence", "contextEpoch")
                        )
                        if completed_epoch >= expected_epoch:
                            self._selected_registration = _copy_registration(completed_registration)
            return result
        except asyncio.TimeoutError as error:
            command = backend_method_kind(method) == "command"
            raise RiderBridgeError(
                f"Timeout waiting for Rider extension (method: {method}, timeout={request_timeout:.1f}s)",
                code="PIPE_TIMEOUT",
                outcome_unknown=command,
                request_id=request_id if command else None,
                target_identity=_target_identity(registration) if registration else None,
            ) from error
        except RiderBridgeError as error:
            await self._advance_selected_from_error(pipe_name, error)
            raise
        except Exception as error:
            raise RiderBridgeError(f"Error calling Rider extension: {error}") from error

    async def call(
        self,
        method: str,
        params: Optional[Dict[str, Any]] = None,
        timeout: float = DEFAULT_TIMEOUT,
    ) -> Any:
        _, _, guard_timeout = self._compute_timeouts(timeout)
        async with self._lock:
            pipe_name = self._selected_pipe
        if pipe_name is None:
            loop = asyncio.get_running_loop()
            instances = await asyncio.wait_for(
                loop.run_in_executor(self._executor, self._sync_discover_instances),
                timeout=guard_timeout,
            )
            async with self._lock:
                self._cached_instances = instances
            if not instances:
                raise RiderBridgeError(
                    "No authenticated Rider instances found. Ensure Rider is running with the current plugin.",
                    code="INSTANCE_REGISTRATION_NOT_FOUND",
                )
            selected_instance = self._find_best_instance(instances)
            if selected_instance is None:
                instance_list = "\n".join(
                    f"  - PID {i.process_id}: {i.solution_name} ({i.debug_mode})"
                    for i in instances
                )
                raise RiderBridgeError(
                    "Multiple Rider instances found. Select one explicitly.\n\n" + instance_list,
                    code="AMBIGUOUS_TARGET",
                )
            return await self.call_pipe(
                selected_instance.pipe_name,
                method,
                params,
                timeout,
                _registration_snapshot=selected_instance.registration_snapshot(),
            )
        return await self.call_pipe(pipe_name, method, params, timeout)


_bridge: Optional[RiderBridge] = None


def get_bridge() -> RiderBridge:
    global _bridge
    if _bridge is None:
        _bridge = RiderBridge()
    return _bridge


async def call(
    method: str,
    params: Optional[Dict[str, Any]] = None,
    timeout: float = DEFAULT_TIMEOUT,
) -> Any:
    return await get_bridge().call(method, params, timeout)


async def call_pipe(
    pipe_name: str,
    method: str,
    params: Optional[Dict[str, Any]] = None,
    timeout: float = DEFAULT_TIMEOUT,
    _registration_snapshot: Optional[Dict[str, Any]] = None,
) -> Any:
    return await get_bridge().call_pipe(
        pipe_name,
        method,
        params,
        timeout,
        _registration_snapshot=_registration_snapshot,
    )


async def call_pipe_exact(
    target: Dict[str, Any],
    method: str,
    params: Optional[Dict[str, Any]] = None,
    timeout: float = DEFAULT_TIMEOUT,
) -> Any:
    """Call only the authenticated Rider server instance recorded by a lost response."""
    if not isinstance(target, dict):
        raise RiderBridgeError("Exact Rider recovery target is invalid", code="INVALID_TARGET")
    pipe_name = target.get("pipeName")
    process_id = target.get("processId")
    if pipe_name != f"AIDebuggerRider_{process_id}":
        raise RiderBridgeError(
            "Exact Rider recovery pipe does not match its process id",
            code="INVALID_TARGET",
        )
    registration = _load_rider_registration(pipe_name)
    for key in ("pipeName", "processId", "processStartTimeUtcTicks", "serverInstanceId"):
        if registration.get(key) != target.get(key):
            raise RiderBridgeError(
                f"Exact Rider recovery target changed for {key}",
                code="STALE_TARGET",
                target_identity=dict(target),
            )
    if target.get("protocolCatalogHash") is not None and (
        registration.get("protocolCatalogHash") != target.get("protocolCatalogHash")
    ):
        raise RiderBridgeError(
            "Exact Rider recovery protocol catalog changed",
            code="STALE_TARGET",
            target_identity=dict(target),
        )
    return await get_bridge().call_pipe(
        pipe_name,
        method,
        params,
        timeout,
        _registration_snapshot=registration,
    )


async def discover_instances() -> List[RiderInstance]:
    return await get_bridge().discover_instances()


def select_instance(
    pipe_name: Optional[str] = None,
    process_id: Optional[int] = None,
    registration_snapshot: Optional[Dict[str, Any]] = None,
    target_identity: Optional[Dict[str, Any]] = None,
):
    get_bridge().select_instance(
        pipe_name=pipe_name,
        process_id=process_id,
        registration_snapshot=registration_snapshot,
        target_identity=target_identity,
    )


def get_selected_registration_snapshot() -> Optional[Dict[str, Any]]:
    return get_bridge().get_selected_registration_snapshot()


def clear_selection():
    get_bridge().clear_selection()


async def is_rider_available() -> bool:
    try:
        instances = await discover_instances()
        return any(i.connection_status == "ok" for i in instances)
    except Exception:
        return False
