"""Windows trust and authenticated instance-registration validation for VS."""

import contextlib
import base64
import ctypes
from ctypes import wintypes
import json
import logging
import os
import pathlib
import stat
import subprocess
import threading
import time
from typing import Any, Dict, Optional

try:
    from .vs_errors import VSBridgeError
    from .vs_tool_registry import DEBUG_METHOD_CATALOG_HASH
except ImportError:
    from vs_errors import VSBridgeError
    from vs_tool_registry import DEBUG_METHOD_CATALOG_HASH

logger = logging.getLogger(__name__)
PROTOCOL_VERSION = "aidb/2"
MAX_INSTANCE_REGISTRATION_SIZE = 64 * 1024
_CLIENT_PROCESS_START_TICKS: Optional[int] = None
_TRUSTED_VS_PATHS_LOCK = threading.Lock()
_TRUSTED_VS_PATHS: tuple[float, frozenset[str]] = (0.0, frozenset())
_VERIFIED_MICROSOFT_IMAGES: set[tuple[Any, ...]] = set()
_VALIDATION_DEADLINE = threading.local()


@contextlib.contextmanager
def _scoped_validation_deadline(deadline_monotonic: Optional[float]):
    previous = getattr(_VALIDATION_DEADLINE, "value", None)
    _VALIDATION_DEADLINE.value = deadline_monotonic
    try:
        yield
    finally:
        _VALIDATION_DEADLINE.value = previous


def _remaining_validation_seconds(maximum: float) -> float:
    deadline = getattr(_VALIDATION_DEADLINE, "value", None)
    if deadline is None:
        return maximum
    remaining = deadline - time.monotonic()
    if remaining <= 0:
        raise VSBridgeError(
            "Visual Studio identity validation exhausted its deadline",
            code="DEADLINE_EXCEEDED_NOT_STARTED",
        )
    return min(maximum, remaining)


def _close_win_handle(kernel32: ctypes.WinDLL, handle: wintypes.HANDLE) -> None:
    if handle:
        kernel32.CloseHandle(handle)


def _current_process_start_time_utc_ticks() -> int:
    global _CLIENT_PROCESS_START_TICKS
    if _CLIENT_PROCESS_START_TICKS is not None:
        return _CLIENT_PROCESS_START_TICKS

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.GetCurrentProcess.argtypes = []
    kernel32.GetCurrentProcess.restype = wintypes.HANDLE
    kernel32.GetProcessTimes.restype = wintypes.BOOL
    kernel32.GetProcessTimes.argtypes = [
        wintypes.HANDLE,
        ctypes.POINTER(wintypes.FILETIME),
        ctypes.POINTER(wintypes.FILETIME),
        ctypes.POINTER(wintypes.FILETIME),
        ctypes.POINTER(wintypes.FILETIME),
    ]
    handle = kernel32.GetCurrentProcess()
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
        raise VSBridgeError("Unable to read MCP client process creation time", code="INVALID_CLIENT")
    filetime = (creation.dwHighDateTime << 32) | creation.dwLowDateTime
    _CLIENT_PROCESS_START_TICKS = filetime + 504911232000000000
    return _CLIENT_PROCESS_START_TICKS


def _registration_value(payload: Dict[str, Any], name: str) -> Any:
    pascal_name = name[0].upper() + name[1:]
    return payload.get(name, payload.get(pascal_name))


def _validate_registration_security(path: pathlib.Path, subdirectory: str = "instances") -> None:
    local_app_data = os.environ.get("LOCALAPPDATA")
    if not local_app_data:
        raise VSBridgeError("LOCALAPPDATA is unavailable", code="INSTANCE_REGISTRATION_NOT_FOUND")
    local_root = pathlib.Path(local_app_data)
    aidb_root = local_root / "AIDebugger"
    instances_root = aidb_root / subdirectory
    expected_path = instances_root / path.name
    if os.path.normcase(os.path.abspath(path)) != os.path.normcase(os.path.abspath(expected_path)):
        raise VSBridgeError("Instance registration is outside the trusted directory", code="INVALID_TARGET")

    def plain_path(candidate: pathlib.Path, *, directory: bool) -> bool:
        try:
            metadata = candidate.lstat()
        except OSError:
            return False
        is_reparse = candidate.is_symlink() or bool(
            getattr(metadata, "st_file_attributes", 0) &
            getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
        )
        expected_type = stat.S_ISDIR(metadata.st_mode) if directory else stat.S_ISREG(metadata.st_mode)
        return expected_type and not is_reparse

    if not plain_path(local_root, directory=True):
        raise VSBridgeError("LOCALAPPDATA is missing or is a reparse point", code="INVALID_TARGET")
    for directory in (aidb_root, instances_root):
        if not plain_path(directory, directory=True):
            raise VSBridgeError("Instance registration directory is missing or is a reparse point", code="INVALID_TARGET")
    if not plain_path(path, directory=False):
        raise VSBridgeError("Instance registration must be a plain file", code="INVALID_TARGET")
    try:
        if path.lstat().st_size > MAX_INSTANCE_REGISTRATION_SIZE:
            raise VSBridgeError("Instance registration exceeds the size limit", code="INVALID_TARGET")
    except OSError as exc:
        raise VSBridgeError("Instance registration metadata is unavailable", code="INVALID_TARGET") from exc
    try:
        import win32api
        import win32security
    except ImportError as exc:
        raise VSBridgeError(
            "pywin32 is required to validate instance registration ownership",
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
        owner = security.GetSecurityDescriptorOwner()
        if win32security.ConvertSidToStringSid(owner) != current_user_text:
            raise VSBridgeError("Instance registration owner does not match the MCP client user", code="INVALID_TARGET")

        control, _ = security.GetSecurityDescriptorControl()
        if not control & win32security.SE_DACL_PROTECTED:
            raise VSBridgeError("Instance registration DACL inherits external permissions", code="INVALID_TARGET")
        dacl = security.GetSecurityDescriptorDacl()
        if dacl is None:
            raise VSBridgeError("Instance registration has no restrictive DACL", code="INVALID_TARGET")
        if dacl.GetAceCount() != 1:
            raise VSBridgeError("Instance registration DACL has unexpected entries", code="INVALID_TARGET")
        ace = dacl.GetAce(0)
        ace_type = ace[0][0]
        sid = ace[2]
        if (
            ace_type != win32security.ACCESS_ALLOWED_ACE_TYPE
            or win32security.ConvertSidToStringSid(sid) != current_user_text
        ):
            raise VSBridgeError("Instance registration DACL is not owner-only", code="INVALID_TARGET")


def apply_owner_only_security(path: pathlib.Path) -> pathlib.Path:
    """Give a file or directory the protected, single-ACE owner-only DACL the validator requires.

    The extension creates its instance-registration directory *and* the files in it with
    that contract; anything the MCP server stores beside that data (launch records) has to
    match it, or the validator rejects the very files the server wrote itself.
    """
    try:
        import ntsecuritycon
        import win32api
        import win32security
    except ImportError as exc:
        raise VSBridgeError(
            "pywin32 is required to secure the AIDebugger state path",
            code="INVALID_TARGET",
        ) from exc

    candidate = pathlib.Path(os.path.abspath(path))
    if _path_has_reparse_component(candidate):
        raise VSBridgeError("AIDebugger state path is a reparse point", code="INVALID_TARGET")

    token = win32security.OpenProcessToken(win32api.GetCurrentProcess(), win32security.TOKEN_QUERY)
    user = win32security.GetTokenInformation(token, win32security.TokenUser)[0]
    descriptor = win32security.SECURITY_DESCRIPTOR()
    descriptor.SetSecurityDescriptorOwner(user, False)
    dacl = win32security.ACL()
    dacl.AddAccessAllowedAce(win32security.ACL_REVISION, ntsecuritycon.FILE_ALL_ACCESS, user)
    descriptor.SetSecurityDescriptorDacl(True, dacl, False)
    # PROTECTED_DACL_SECURITY_INFORMATION alone does not make GetFileSecurity report
    # SE_DACL_PROTECTED here, and the validator requires the flag.
    descriptor.SetSecurityDescriptorControl(
        win32security.SE_DACL_PROTECTED, win32security.SE_DACL_PROTECTED
    )
    win32security.SetFileSecurity(
        str(candidate),
        win32security.OWNER_SECURITY_INFORMATION
        | win32security.DACL_SECURITY_INFORMATION
        | win32security.PROTECTED_DACL_SECURITY_INFORMATION,
        descriptor,
    )
    return candidate


def ensure_owner_only_directory(path: pathlib.Path) -> pathlib.Path:
    """Create a directory and give it the protected owner-only contract."""
    candidate = pathlib.Path(os.path.abspath(path))
    candidate.mkdir(parents=True, exist_ok=True)
    if _path_has_reparse_component(candidate):
        raise VSBridgeError("AIDebugger state directory is a reparse point", code="INVALID_TARGET")
    return apply_owner_only_security(candidate)


def _load_instance_registration(pipe_name: str) -> Dict[str, Any]:
    if not pipe_name.startswith("AIDebuggerVS_"):
        raise VSBridgeError("Invalid Visual Studio pipe name", code="INVALID_TARGET")
    try:
        process_id = int(pipe_name.rsplit("_", 1)[1])
    except (ValueError, IndexError) as exc:
        raise VSBridgeError("Invalid Visual Studio pipe process id", code="INVALID_TARGET") from exc

    local_app_data = os.environ.get("LOCALAPPDATA")
    if not local_app_data:
        raise VSBridgeError("LOCALAPPDATA is unavailable", code="INSTANCE_REGISTRATION_NOT_FOUND")
    path = pathlib.Path(local_app_data) / "AIDebugger" / "instances" / f"{pipe_name}.json"
    try:
        _validate_registration_security(path)
        payload = json.loads(path.read_text(encoding="utf-8"))
    except VSBridgeError:
        raise
    except (OSError, json.JSONDecodeError) as exc:
        raise VSBridgeError(
            f"Authenticated instance registration is unavailable for '{pipe_name}'",
            code="INSTANCE_REGISTRATION_NOT_FOUND",
        ) from exc
    if not isinstance(payload, dict):
        raise VSBridgeError("Instance registration is invalid", code="INVALID_TARGET")

    registration = {
        "protocol": _registration_value(payload, "protocol"),
        "pipeName": _registration_value(payload, "pipeName"),
        "controlPipeName": _registration_value(payload, "controlPipeName"),
        "recoveryPipeName": payload.get("recoveryPipeName"),
        "processId": _registration_value(payload, "processId"),
        "processStartTimeUtcTicks": _registration_value(payload, "processStartTimeUtcTicks"),
        "serverInstanceId": _registration_value(payload, "serverInstanceId"),
        "protocolCatalogHash": _registration_value(payload, "protocolCatalogHash"),
        "authToken": _registration_value(payload, "authToken"),
        "capabilities": _registration_value(payload, "capabilities"),
    }
    if registration["protocol"] != PROTOCOL_VERSION or registration["pipeName"] != pipe_name:
        raise VSBridgeError("Instance registration protocol or pipe mismatch", code="PROTOCOL_MISMATCH")
    if registration["protocolCatalogHash"] != DEBUG_METHOD_CATALOG_HASH:
        raise VSBridgeError(
            "Visual Studio extension and MCP server protocol catalogs do not match",
            code="PROTOCOL_CATALOG_MISMATCH",
        )
    if registration["controlPipeName"] != pipe_name + "_control":
        raise VSBridgeError("Instance registration control pipe mismatch", code="PROTOCOL_MISMATCH")
    if registration["recoveryPipeName"] is None:
        # Backward-compatible fallback for an authenticated pre-recovery-lane
        # registration. New servers always publish the separate lane.
        registration["recoveryPipeName"] = registration["controlPipeName"]
    elif registration["recoveryPipeName"] != pipe_name + "_recovery":
        raise VSBridgeError("Instance registration recovery pipe mismatch", code="PROTOCOL_MISMATCH")
    if registration["processId"] != process_id:
        raise VSBridgeError("Instance registration PID mismatch", code="STALE_TARGET")
    if not isinstance(registration["processStartTimeUtcTicks"], int) or not isinstance(
        registration["serverInstanceId"], str
    ):
        raise VSBridgeError("Instance registration identity is invalid", code="INVALID_TARGET")
    if not isinstance(registration["capabilities"], list) or not all(
        isinstance(value, str) and value for value in registration["capabilities"]
    ):
        raise VSBridgeError("Instance registration capabilities are invalid", code="INVALID_TARGET")
    try:
        token_bytes = base64.b64decode(str(registration["authToken"]), validate=True)
    except (ValueError, TypeError) as exc:
        raise VSBridgeError("Instance authentication token is invalid", code="INVALID_TARGET") from exc
    if len(token_bytes) != 32:
        raise VSBridgeError("Instance authentication token has invalid length", code="INVALID_TARGET")

    _validate_visual_studio_process(process_id, registration["processStartTimeUtcTicks"])
    return registration


def _validate_visual_studio_process(process_id: int, expected_start_ticks: int) -> None:
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
    kernel32.QueryFullProcessImageNameW.restype = wintypes.BOOL
    kernel32.QueryFullProcessImageNameW.argtypes = [
        wintypes.HANDLE,
        wintypes.DWORD,
        wintypes.LPWSTR,
        ctypes.POINTER(wintypes.DWORD),
    ]
    handle = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, process_id)
    if not handle:
        raise VSBridgeError("Visual Studio process is unavailable", code="STALE_TARGET")
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
            raise VSBridgeError("Unable to verify Visual Studio process creation time", code="INVALID_TARGET")
        filetime = (creation.dwHighDateTime << 32) | creation.dwLowDateTime
        dotnet_ticks = filetime + 504911232000000000
        if dotnet_ticks != expected_start_ticks:
            raise VSBridgeError("Visual Studio process creation time mismatch", code="STALE_TARGET")

        capacity = wintypes.DWORD(32768)
        image_path = ctypes.create_unicode_buffer(capacity.value)
        if not kernel32.QueryFullProcessImageNameW(handle, 0, image_path, ctypes.byref(capacity)):
            raise VSBridgeError("Unable to verify Visual Studio executable", code="INVALID_TARGET")
        if pathlib.Path(image_path.value).name.lower() != "devenv.exe":
            raise VSBridgeError("Registered pipe owner is not devenv.exe", code="INVALID_TARGET")
        _validate_same_user_process(handle)
        _validate_trusted_visual_studio_image(image_path.value)
    finally:
        _close_win_handle(kernel32, handle)


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
        wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)
    ]
    advapi32.GetTokenInformation.restype = wintypes.BOOL
    advapi32.EqualSid.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    advapi32.EqualSid.restype = wintypes.BOOL

    def token_user(process):
        token = wintypes.HANDLE()
        if not advapi32.OpenProcessToken(process, TOKEN_QUERY, ctypes.byref(token)):
            raise VSBridgeError("Unable to verify Visual Studio process owner", code="INVALID_TARGET")
        size = wintypes.DWORD()
        advapi32.GetTokenInformation(token, TokenUser, None, 0, ctypes.byref(size))
        if size.value == 0:
            _close_win_handle(kernel32, token)
            raise VSBridgeError("Unable to read Visual Studio process owner", code="INVALID_TARGET")
        buffer = ctypes.create_string_buffer(size.value)
        if not advapi32.GetTokenInformation(
            token, TokenUser, buffer, size.value, ctypes.byref(size)
        ):
            _close_win_handle(kernel32, token)
            raise VSBridgeError("Unable to read Visual Studio process owner", code="INVALID_TARGET")
        sid = ctypes.cast(buffer, ctypes.POINTER(TOKEN_USER)).contents.Sid
        return token, buffer, sid

    target_token = current_token = None
    try:
        target_token, target_buffer, target_sid = token_user(process_handle)
        current_token, current_buffer, current_sid = token_user(kernel32.GetCurrentProcess())
        if not advapi32.EqualSid(target_sid, current_sid):
            raise VSBridgeError("Visual Studio process owner does not match the MCP client user", code="INVALID_TARGET")
        # Keep both buffers alive through EqualSid.
        _ = (target_buffer, current_buffer)
    finally:
        if target_token:
            _close_win_handle(kernel32, target_token)
        if current_token:
            _close_win_handle(kernel32, current_token)


def _trusted_visual_studio_paths() -> frozenset[str]:
    global _TRUSTED_VS_PATHS
    now = time.monotonic()
    lock_timeout = _remaining_validation_seconds(5.0)
    if not _TRUSTED_VS_PATHS_LOCK.acquire(timeout=lock_timeout):
        raise VSBridgeError(
            "Timed out waiting for Visual Studio installation validation",
            code="DEADLINE_EXCEEDED_NOT_STARTED",
        )
    try:
        cached_at, cached = _TRUSTED_VS_PATHS
        if cached and now - cached_at < 60.0:
            return cached

        program_files_x86 = os.environ.get("ProgramFiles(x86)")
        if not program_files_x86:
            raise VSBridgeError("Program Files (x86) is unavailable", code="INVALID_TARGET")
        vswhere = pathlib.Path(program_files_x86) / "Microsoft Visual Studio" / "Installer" / "vswhere.exe"
        if not vswhere.is_file() or _path_has_reparse_component(vswhere):
            raise VSBridgeError("Trusted Visual Studio installer discovery is unavailable", code="INVALID_TARGET")
        try:
            completed = subprocess.run(
                [str(vswhere), "-products", "*", "-format", "json", "-utf8"],
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="strict",
                timeout=_remaining_validation_seconds(5.0),
                check=False,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
            payload = json.loads(completed.stdout) if completed.returncode == 0 else []
        except (OSError, subprocess.SubprocessError, json.JSONDecodeError, UnicodeError) as exc:
            raise VSBridgeError("Unable to verify the Visual Studio installation", code="INVALID_TARGET") from exc
        reported_paths = (
            os.path.normcase(os.path.abspath(os.path.join(str(item.get("installationPath")), "Common7", "IDE", "devenv.exe")))
            for item in payload
            if isinstance(item, dict) and isinstance(item.get("installationPath"), str)
        )
        paths = frozenset(path for path in reported_paths if pathlib.Path(path).is_file())
        if not paths:
            raise VSBridgeError("No trusted Visual Studio installation was reported", code="INVALID_TARGET")
        _TRUSTED_VS_PATHS = (now, paths)
        return paths
    finally:
        _TRUSTED_VS_PATHS_LOCK.release()


def _path_has_reparse_component(path: pathlib.Path) -> bool:
    current = pathlib.Path(os.path.abspath(path))
    while True:
        try:
            metadata = current.lstat()
        except OSError:
            return True
        if current.is_symlink() or bool(
            getattr(metadata, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
        ):
            return True
        parent = current.parent
        if parent == current:
            return False
        current = parent


def _validate_trusted_visual_studio_image(image_path: str) -> None:
    normalized = os.path.normcase(os.path.abspath(image_path))
    if normalized not in _trusted_visual_studio_paths() or _path_has_reparse_component(pathlib.Path(normalized)):
        raise VSBridgeError("devenv.exe is not from a registered plain Visual Studio installation", code="INVALID_TARGET")
    current = pathlib.Path(normalized)
    installation_root = current.parent.parent.parent
    while True:
        inside_installation = current == installation_root or installation_root in current.parents
        if _current_token_can_modify_path(str(current), include_contents=inside_installation):
            raise VSBridgeError(
                "Visual Studio executable or an installation-path component is writable by the MCP client user",
                code="INVALID_TARGET",
            )
        parent = current.parent
        if parent == current:
            break
        current = parent
    try:
        metadata = os.stat(normalized, follow_symlinks=False)
        file_identity = (
            normalized,
            metadata.st_dev,
            metadata.st_ino,
            metadata.st_size,
            metadata.st_mtime_ns,
        )
    except OSError as exc:
        raise VSBridgeError("Unable to identify the Visual Studio executable", code="INVALID_TARGET") from exc
    with _TRUSTED_VS_PATHS_LOCK:
        if file_identity in _VERIFIED_MICROSOFT_IMAGES:
            return
    _remaining_validation_seconds(1.0)
    _win_verify_trust(normalized)
    _remaining_validation_seconds(1.0)
    with _TRUSTED_VS_PATHS_LOCK:
        _VERIFIED_MICROSOFT_IMAGES.add(file_identity)


def _current_token_can_modify_path(path: str, *, include_contents: bool = True) -> bool:
    """Use the Windows access-check engine against the caller's effective token."""

    class GENERIC_MAPPING(ctypes.Structure):
        _fields_ = [
            ("GenericRead", wintypes.DWORD),
            ("GenericWrite", wintypes.DWORD),
            ("GenericExecute", wintypes.DWORD),
            ("GenericAll", wintypes.DWORD),
        ]

    advapi32 = ctypes.WinDLL("advapi32", use_last_error=True)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    TOKEN_QUERY = 0x0008
    TOKEN_DUPLICATE = 0x0002
    SECURITY_IMPERSONATION = 2
    SE_FILE_OBJECT = 1
    OWNER_GROUP_DACL_SECURITY_INFORMATION = 0x00000001 | 0x00000002 | 0x00000004
    MAXIMUM_ALLOWED = 0x02000000
    FILE_GENERIC_READ = 0x00120089
    FILE_GENERIC_WRITE = 0x00120116
    FILE_GENERIC_EXECUTE = 0x001200A0
    FILE_ALL_ACCESS = 0x001F01FF
    modifying_rights = (
        0x00000040  # FILE_DELETE_CHILD permits replacing a protected child
        | 0x00010000  # DELETE
        | 0x00040000  # WRITE_DAC
        | 0x00080000  # WRITE_OWNER
    )
    if include_contents:
        modifying_rights |= (
            0x00000002  # FILE_WRITE_DATA / FILE_ADD_FILE
            | 0x00000004  # FILE_APPEND_DATA / FILE_ADD_SUBDIRECTORY
            | 0x00000010  # FILE_WRITE_EA
            | 0x00000100  # FILE_WRITE_ATTRIBUTES
        )

    token = wintypes.HANDLE()
    impersonation = wintypes.HANDLE()
    security_descriptor = ctypes.c_void_p()
    advapi32.OpenProcessToken.argtypes = [wintypes.HANDLE, wintypes.DWORD, ctypes.POINTER(wintypes.HANDLE)]
    advapi32.OpenProcessToken.restype = wintypes.BOOL
    advapi32.DuplicateToken.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.POINTER(wintypes.HANDLE)]
    advapi32.DuplicateToken.restype = wintypes.BOOL
    advapi32.GetNamedSecurityInfoW.argtypes = [
        wintypes.LPWSTR,
        ctypes.c_int,
        wintypes.DWORD,
        ctypes.c_void_p,
        ctypes.c_void_p,
        ctypes.c_void_p,
        ctypes.c_void_p,
        ctypes.POINTER(ctypes.c_void_p),
    ]
    advapi32.GetNamedSecurityInfoW.restype = wintypes.DWORD
    advapi32.AccessCheck.argtypes = [
        ctypes.c_void_p,
        wintypes.HANDLE,
        wintypes.DWORD,
        ctypes.POINTER(GENERIC_MAPPING),
        ctypes.c_void_p,
        ctypes.POINTER(wintypes.DWORD),
        ctypes.POINTER(wintypes.DWORD),
        ctypes.POINTER(wintypes.BOOL),
    ]
    advapi32.AccessCheck.restype = wintypes.BOOL
    try:
        if not advapi32.OpenProcessToken(
            kernel32.GetCurrentProcess(), TOKEN_QUERY | TOKEN_DUPLICATE, ctypes.byref(token)
        ):
            raise VSBridgeError("Unable to open the MCP process token", code="INVALID_TARGET")
        if not advapi32.DuplicateToken(token, SECURITY_IMPERSONATION, ctypes.byref(impersonation)):
            raise VSBridgeError("Unable to duplicate the MCP process token", code="INVALID_TARGET")
        status = advapi32.GetNamedSecurityInfoW(
            path,
            SE_FILE_OBJECT,
            OWNER_GROUP_DACL_SECURITY_INFORMATION,
            None,
            None,
            None,
            None,
            ctypes.byref(security_descriptor),
        )
        if status != 0 or not security_descriptor.value:
            raise VSBridgeError(
                f"Unable to read Visual Studio path security (Win32 {status})",
                code="INVALID_TARGET",
            )
        mapping = GENERIC_MAPPING(
            FILE_GENERIC_READ,
            FILE_GENERIC_WRITE,
            FILE_GENERIC_EXECUTE,
            FILE_ALL_ACCESS,
        )
        privilege_buffer = ctypes.create_string_buffer(1024)
        privilege_size = wintypes.DWORD(len(privilege_buffer))
        granted = wintypes.DWORD()
        access_status = wintypes.BOOL()
        if not advapi32.AccessCheck(
            security_descriptor,
            impersonation,
            MAXIMUM_ALLOWED,
            ctypes.byref(mapping),
            privilege_buffer,
            ctypes.byref(privilege_size),
            ctypes.byref(granted),
            ctypes.byref(access_status),
        ):
            raise VSBridgeError(
                f"Unable to evaluate Visual Studio path security (Win32 {ctypes.get_last_error()})",
                code="INVALID_TARGET",
            )
        return bool(access_status.value and (granted.value & modifying_rights))
    finally:
        if security_descriptor.value:
            kernel32.LocalFree(security_descriptor)
        if impersonation:
            _close_win_handle(kernel32, impersonation)
        if token:
            _close_win_handle(kernel32, token)


def _win_verify_trust(image_path: str) -> None:
    """Verify the embedded Authenticode signature without spawning PowerShell."""

    class GUID(ctypes.Structure):
        _fields_ = [
            ("Data1", wintypes.DWORD),
            ("Data2", wintypes.WORD),
            ("Data3", wintypes.WORD),
            ("Data4", ctypes.c_ubyte * 8),
        ]

    class WINTRUST_FILE_INFO(ctypes.Structure):
        _fields_ = [
            ("cbStruct", wintypes.DWORD),
            ("pcwszFilePath", wintypes.LPCWSTR),
            ("hFile", wintypes.HANDLE),
            ("pgKnownSubject", ctypes.POINTER(GUID)),
        ]

    class WINTRUST_DATA(ctypes.Structure):
        _fields_ = [
            ("cbStruct", wintypes.DWORD),
            ("pPolicyCallbackData", wintypes.LPVOID),
            ("pSIPClientData", wintypes.LPVOID),
            ("dwUIChoice", wintypes.DWORD),
            ("fdwRevocationChecks", wintypes.DWORD),
            ("dwUnionChoice", wintypes.DWORD),
            ("pFile", ctypes.POINTER(WINTRUST_FILE_INFO)),
            ("dwStateAction", wintypes.DWORD),
            ("hWVTStateData", wintypes.HANDLE),
            ("pwszURLReference", wintypes.LPCWSTR),
            ("dwProvFlags", wintypes.DWORD),
            ("dwUIContext", wintypes.DWORD),
            ("pSignatureSettings", wintypes.LPVOID),
        ]

    class CRYPT_PROVIDER_SGNR(ctypes.Structure):
        _fields_ = [
            ("cbStruct", wintypes.DWORD),
            ("sftVerifyAsOf", wintypes.FILETIME),
            ("csCertChain", wintypes.DWORD),
            ("pasCertChain", ctypes.c_void_p),
            ("dwSignerType", wintypes.DWORD),
            ("psSigner", ctypes.c_void_p),
            ("dwError", wintypes.DWORD),
            ("csCounterSigners", wintypes.DWORD),
            ("pasCounterSigners", ctypes.c_void_p),
            ("pChainContext", ctypes.c_void_p),
        ]

    class CERT_CHAIN_POLICY_PARA(ctypes.Structure):
        _fields_ = [
            ("cbSize", wintypes.DWORD),
            ("dwFlags", wintypes.DWORD),
            ("pvExtraPolicyPara", ctypes.c_void_p),
        ]

    class CERT_CHAIN_POLICY_STATUS(ctypes.Structure):
        _fields_ = [
            ("cbSize", wintypes.DWORD),
            ("dwError", wintypes.DWORD),
            ("lChainIndex", ctypes.c_long),
            ("lElementIndex", ctypes.c_long),
            ("pvExtraPolicyStatus", ctypes.c_void_p),
        ]

    action = GUID(
        0x00AAC56B,
        0xCD44,
        0x11D0,
        (ctypes.c_ubyte * 8)(0x8C, 0xC2, 0x00, 0xC0, 0x4F, 0xC2, 0x95, 0xEE),
    )
    file_info = WINTRUST_FILE_INFO(
        ctypes.sizeof(WINTRUST_FILE_INFO),
        image_path,
        None,
        None,
    )
    trust_data = WINTRUST_DATA()
    trust_data.cbStruct = ctypes.sizeof(WINTRUST_DATA)
    trust_data.dwUIChoice = 2  # WTD_UI_NONE
    trust_data.fdwRevocationChecks = 0  # WTD_REVOKE_NONE
    trust_data.dwUnionChoice = 1  # WTD_CHOICE_FILE
    trust_data.pFile = ctypes.pointer(file_info)
    trust_data.dwStateAction = 1  # WTD_STATEACTION_VERIFY
    trust_data.dwProvFlags = 0x1000  # WTD_CACHE_ONLY_URL_RETRIEVAL
    wintrust = ctypes.WinDLL("wintrust", use_last_error=True)
    wintrust.WinVerifyTrust.argtypes = [wintypes.HWND, ctypes.POINTER(GUID), ctypes.POINTER(WINTRUST_DATA)]
    wintrust.WinVerifyTrust.restype = ctypes.c_long
    status = wintrust.WinVerifyTrust(None, ctypes.byref(action), ctypes.byref(trust_data))
    try:
        if status != 0:
            raise VSBridgeError(
                f"devenv.exe Authenticode validation failed (0x{status & 0xFFFFFFFF:08x})",
                code="INVALID_TARGET",
            )
        wintrust.WTHelperProvDataFromStateData.argtypes = [wintypes.HANDLE]
        wintrust.WTHelperProvDataFromStateData.restype = ctypes.c_void_p
        wintrust.WTHelperGetProvSignerFromChain.argtypes = [ctypes.c_void_p, wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        wintrust.WTHelperGetProvSignerFromChain.restype = ctypes.POINTER(CRYPT_PROVIDER_SGNR)
        provider_data = wintrust.WTHelperProvDataFromStateData(trust_data.hWVTStateData)
        signer = wintrust.WTHelperGetProvSignerFromChain(provider_data, 0, False, 0) if provider_data else None
        if not signer or not signer.contents.pChainContext:
            raise VSBridgeError("Unable to inspect the devenv.exe signer chain", code="INVALID_TARGET")
        policy = CERT_CHAIN_POLICY_PARA()
        policy.cbSize = ctypes.sizeof(CERT_CHAIN_POLICY_PARA)
        policy.dwFlags = 0x00020000  # MICROSOFT_ROOT_CERT_CHAIN_POLICY_CHECK_APPLICATION_ROOT_FLAG
        policy_status = CERT_CHAIN_POLICY_STATUS()
        policy_status.cbSize = ctypes.sizeof(CERT_CHAIN_POLICY_STATUS)
        crypt32 = ctypes.WinDLL("crypt32", use_last_error=True)
        crypt32.CertVerifyCertificateChainPolicy.argtypes = [
            ctypes.c_void_p,
            ctypes.c_void_p,
            ctypes.POINTER(CERT_CHAIN_POLICY_PARA),
            ctypes.POINTER(CERT_CHAIN_POLICY_STATUS),
        ]
        crypt32.CertVerifyCertificateChainPolicy.restype = wintypes.BOOL
        if not crypt32.CertVerifyCertificateChainPolicy(
            ctypes.c_void_p(7),
            signer.contents.pChainContext,
            ctypes.byref(policy),
            ctypes.byref(policy_status),
        ) or policy_status.dwError != 0:
            raise VSBridgeError(
                f"devenv.exe signer does not chain to a Microsoft root (0x{policy_status.dwError:08x})",
                code="INVALID_TARGET",
            )
    finally:
        trust_data.dwStateAction = 2  # WTD_STATEACTION_CLOSE
        wintrust.WinVerifyTrust(None, ctypes.byref(action), ctypes.byref(trust_data))
