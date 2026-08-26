"""Owner-only Rider instance registration and process identity validation."""

import base64
import ctypes
from ctypes import wintypes
import json
import os
import pathlib
import hashlib
import stat
from typing import Any, Dict

try:
    from .rider_errors import RiderBridgeError
except ImportError:
    from rider_errors import RiderBridgeError

PROTOCOL_VERSION = "aidb/2"
DEBUG_METHOD_CATALOG_HASH = hashlib.sha256(
    pathlib.Path(__file__).with_name("debug_method_manifest.json").read_bytes()
).hexdigest()
_CLIENT_PROCESS_START_TICKS = None


def _close_win_handle(kernel32: ctypes.WinDLL, handle: wintypes.HANDLE) -> None:
    if handle:
        kernel32.CloseHandle(handle)


def _registration_value(payload: Dict[str, Any], name: str) -> Any:
    pascal_name = name[0].upper() + name[1:]
    return payload.get(name, payload.get(pascal_name))


def _current_process_start_time_utc_ticks() -> int:
    global _CLIENT_PROCESS_START_TICKS
    if _CLIENT_PROCESS_START_TICKS is not None:
        return _CLIENT_PROCESS_START_TICKS
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.GetCurrentProcess.restype = wintypes.HANDLE
    kernel32.GetProcessTimes.restype = wintypes.BOOL
    kernel32.GetProcessTimes.argtypes = [
        wintypes.HANDLE,
        ctypes.POINTER(wintypes.FILETIME),
        ctypes.POINTER(wintypes.FILETIME),
        ctypes.POINTER(wintypes.FILETIME),
        ctypes.POINTER(wintypes.FILETIME),
    ]
    creation = wintypes.FILETIME()
    exit_time = wintypes.FILETIME()
    kernel = wintypes.FILETIME()
    user = wintypes.FILETIME()
    if not kernel32.GetProcessTimes(
        kernel32.GetCurrentProcess(),
        ctypes.byref(creation),
        ctypes.byref(exit_time),
        ctypes.byref(kernel),
        ctypes.byref(user),
    ):
        raise RiderBridgeError("Unable to read MCP client process creation time", code="INVALID_CLIENT")
    filetime = (creation.dwHighDateTime << 32) | creation.dwLowDateTime
    _CLIENT_PROCESS_START_TICKS = filetime + 504_911_232_000_000_000
    return _CLIENT_PROCESS_START_TICKS


def _validate_registration_security(path: pathlib.Path) -> None:
    local_app_data = os.environ.get("LOCALAPPDATA")
    if not local_app_data:
        raise RiderBridgeError("LOCALAPPDATA is unavailable", code="INSTANCE_REGISTRATION_NOT_FOUND")
    local_root = pathlib.Path(local_app_data)
    aidb_root = local_root / "AIDebugger"
    instances_root = aidb_root / "instances"
    expected = instances_root / path.name
    if os.path.normcase(os.path.abspath(path)) != os.path.normcase(os.path.abspath(expected)):
        raise RiderBridgeError("Rider registration is outside the trusted directory", code="INVALID_TARGET")

    def plain(candidate: pathlib.Path, *, directory: bool) -> bool:
        try:
            metadata = candidate.lstat()
        except OSError:
            return False
        expected_type = stat.S_ISDIR(metadata.st_mode) if directory else stat.S_ISREG(metadata.st_mode)
        is_reparse = candidate.is_symlink() or bool(
            getattr(metadata, "st_file_attributes", 0)
            & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
        )
        return expected_type and not is_reparse

    if not plain(local_root, directory=True):
        raise RiderBridgeError("LOCALAPPDATA is missing or is a reparse point", code="INVALID_TARGET")
    for directory in (aidb_root, instances_root):
        if not plain(directory, directory=True):
            raise RiderBridgeError("Rider registration directory is unsafe", code="INVALID_TARGET")
    if not plain(path, directory=False) or path.lstat().st_size > 64 * 1024:
        raise RiderBridgeError("Rider registration must be a bounded plain file", code="INVALID_TARGET")

    try:
        import win32api
        import win32security
    except ImportError as exc:
        raise RiderBridgeError(
            "pywin32 is required to validate Rider registration ownership",
            code="INVALID_TARGET",
        ) from exc
    token = win32security.OpenProcessToken(win32api.GetCurrentProcess(), win32security.TOKEN_QUERY)
    current_user = win32security.GetTokenInformation(token, win32security.TokenUser)[0]
    current_user_text = win32security.ConvertSidToStringSid(current_user)
    for secured_path in (aidb_root, instances_root, path):
        security = win32security.GetFileSecurity(
            str(secured_path),
            win32security.OWNER_SECURITY_INFORMATION | win32security.DACL_SECURITY_INFORMATION,
        )
        if win32security.ConvertSidToStringSid(security.GetSecurityDescriptorOwner()) != current_user_text:
            raise RiderBridgeError("Rider registration owner mismatch", code="INVALID_TARGET")
        control, _ = security.GetSecurityDescriptorControl()
        if not control & win32security.SE_DACL_PROTECTED:
            raise RiderBridgeError("Rider registration DACL inherits permissions", code="INVALID_TARGET")
        dacl = security.GetSecurityDescriptorDacl()
        if dacl is None or dacl.GetAceCount() != 1:
            raise RiderBridgeError("Rider registration DACL is not owner-only", code="INVALID_TARGET")
        ace = dacl.GetAce(0)
        if (
            ace[0][0] != win32security.ACCESS_ALLOWED_ACE_TYPE
            or win32security.ConvertSidToStringSid(ace[2]) != current_user_text
        ):
            raise RiderBridgeError("Rider registration DACL is not owner-only", code="INVALID_TARGET")


def _load_rider_registration(pipe_name: str) -> Dict[str, Any]:
    if not pipe_name.startswith("AIDebuggerRider_"):
        raise RiderBridgeError("Invalid Rider pipe name", code="INVALID_TARGET")
    try:
        process_id = int(pipe_name.rsplit("_", 1)[1])
    except (ValueError, IndexError) as exc:
        raise RiderBridgeError("Invalid Rider pipe process id", code="INVALID_TARGET") from exc

    local_app_data = os.environ.get("LOCALAPPDATA")
    if not local_app_data:
        raise RiderBridgeError(
            "LOCALAPPDATA is unavailable",
            code="INSTANCE_REGISTRATION_NOT_FOUND",
        )
    path = pathlib.Path(local_app_data) / "AIDebugger" / "instances" / f"{pipe_name}.json"
    try:
        _validate_registration_security(path)
        payload = json.loads(path.read_text(encoding="utf-8"))
    except RiderBridgeError:
        raise
    except (OSError, json.JSONDecodeError) as exc:
        raise RiderBridgeError(
            f"Authenticated Rider instance registration is unavailable for '{pipe_name}'",
            code="INSTANCE_REGISTRATION_NOT_FOUND",
        ) from exc
    if not isinstance(payload, dict):
        raise RiderBridgeError("Rider instance registration is invalid", code="INVALID_TARGET")

    registration = {
        "protocol": _registration_value(payload, "protocol"),
        "pipeName": _registration_value(payload, "pipeName"),
        "processId": _registration_value(payload, "processId"),
        "processStartTimeUtcTicks": _registration_value(payload, "processStartTimeUtcTicks"),
        "serverInstanceId": _registration_value(payload, "serverInstanceId"),
        "protocolCatalogHash": _registration_value(payload, "protocolCatalogHash"),
        "authToken": _registration_value(payload, "authToken"),
        "capabilities": _registration_value(payload, "capabilities"),
    }
    if registration["protocol"] != PROTOCOL_VERSION or registration["pipeName"] != pipe_name:
        raise RiderBridgeError(
            "Rider instance registration protocol or pipe mismatch",
            code="PROTOCOL_MISMATCH",
        )
    if registration["protocolCatalogHash"] != DEBUG_METHOD_CATALOG_HASH:
        raise RiderBridgeError(
            "Rider plugin and MCP server protocol catalogs do not match",
            code="PROTOCOL_CATALOG_MISMATCH",
        )
    if registration["processId"] != process_id:
        raise RiderBridgeError("Rider instance registration PID mismatch", code="STALE_TARGET")
    if not isinstance(registration["processStartTimeUtcTicks"], int) or not isinstance(
        registration["serverInstanceId"], str
    ) or not registration["serverInstanceId"]:
        raise RiderBridgeError("Rider instance registration identity is invalid", code="INVALID_TARGET")
    if not isinstance(registration["capabilities"], list) or not all(
        isinstance(value, str) and value for value in registration["capabilities"]
    ):
        raise RiderBridgeError("Rider instance registration capabilities are invalid", code="INVALID_TARGET")
    try:
        token_bytes = base64.b64decode(str(registration["authToken"]), validate=True)
    except (ValueError, TypeError) as exc:
        raise RiderBridgeError("Rider instance authentication token is invalid", code="INVALID_TARGET") from exc
    if len(token_bytes) != 32:
        raise RiderBridgeError("Rider instance authentication token has invalid length", code="INVALID_TARGET")
    _validate_rider_process(process_id, registration["processStartTimeUtcTicks"])
    return registration


def _validate_same_user_process(process_handle) -> None:
    advapi32 = ctypes.WinDLL("advapi32", use_last_error=True)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    TOKEN_QUERY = 0x0008
    TokenUser = 1

    class TOKEN_USER(ctypes.Structure):
        _fields_ = [("Sid", ctypes.c_void_p), ("Attributes", wintypes.DWORD)]

    advapi32.OpenProcessToken.argtypes = [wintypes.HANDLE, wintypes.DWORD, ctypes.POINTER(wintypes.HANDLE)]
    advapi32.OpenProcessToken.restype = wintypes.BOOL
    advapi32.GetTokenInformation.argtypes = [
        wintypes.HANDLE,
        ctypes.c_int,
        ctypes.c_void_p,
        wintypes.DWORD,
        ctypes.POINTER(wintypes.DWORD),
    ]
    advapi32.GetTokenInformation.restype = wintypes.BOOL
    advapi32.EqualSid.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    advapi32.EqualSid.restype = wintypes.BOOL
    kernel32.GetCurrentProcess.restype = wintypes.HANDLE

    def token_user(process):
        token = wintypes.HANDLE()
        if not advapi32.OpenProcessToken(process, TOKEN_QUERY, ctypes.byref(token)):
            raise RiderBridgeError("Unable to verify Rider process owner", code="INVALID_TARGET")
        size = wintypes.DWORD()
        advapi32.GetTokenInformation(token, TokenUser, None, 0, ctypes.byref(size))
        if size.value == 0:
            _close_win_handle(kernel32, token)
            raise RiderBridgeError("Unable to read Rider process owner", code="INVALID_TARGET")
        buffer = ctypes.create_string_buffer(size.value)
        if not advapi32.GetTokenInformation(token, TokenUser, buffer, size.value, ctypes.byref(size)):
            _close_win_handle(kernel32, token)
            raise RiderBridgeError("Unable to read Rider process owner", code="INVALID_TARGET")
        sid = ctypes.cast(buffer, ctypes.POINTER(TOKEN_USER)).contents.Sid
        return token, buffer, sid

    target_token = current_token = None
    try:
        target_token, target_buffer, target_sid = token_user(process_handle)
        current_token, current_buffer, current_sid = token_user(kernel32.GetCurrentProcess())
        if not advapi32.EqualSid(target_sid, current_sid):
            raise RiderBridgeError("Rider process owner mismatch", code="INVALID_TARGET")
        _ = (target_buffer, current_buffer)
    finally:
        if target_token:
            _close_win_handle(kernel32, target_token)
        if current_token:
            _close_win_handle(kernel32, current_token)


def _validate_rider_process(process_id: int, expected_start_ticks: int) -> None:
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
    kernel32.OpenProcess.restype = wintypes.HANDLE
    kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel32.GetProcessTimes.restype = wintypes.BOOL
    kernel32.GetProcessTimes.argtypes = [
        wintypes.HANDLE,
        ctypes.POINTER(wintypes.FILETIME),
        ctypes.POINTER(wintypes.FILETIME),
        ctypes.POINTER(wintypes.FILETIME),
        ctypes.POINTER(wintypes.FILETIME),
    ]
    handle = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, process_id)
    if not handle:
        raise RiderBridgeError("Rider process is unavailable", code="STALE_TARGET")
    try:
        creation = wintypes.FILETIME()
        exit_time = wintypes.FILETIME()
        kernel = wintypes.FILETIME()
        user = wintypes.FILETIME()
        if not kernel32.GetProcessTimes(
            handle,
            ctypes.byref(creation),
            ctypes.byref(exit_time),
            ctypes.byref(kernel),
            ctypes.byref(user),
        ):
            raise RiderBridgeError("Unable to verify Rider process creation time", code="INVALID_TARGET")
        filetime = (creation.dwHighDateTime << 32) | creation.dwLowDateTime
        if filetime + 504_911_232_000_000_000 != expected_start_ticks:
            raise RiderBridgeError("Rider process creation time mismatch", code="STALE_TARGET")
        _validate_same_user_process(handle)
    finally:
        _close_win_handle(kernel32, handle)
