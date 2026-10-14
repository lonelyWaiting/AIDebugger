"""Launch a Visual Studio instance that hosts the AIDebugger MCP server.

A user-started Visual Studio deliberately keeps the extension dormant until the
Tools menu starts the MCP server. When no instance is available at all, the MCP
server launches one itself, suppresses the Start Page (otherwise the IDE parks on
"choose a project or continue without code" and never finishes starting up), and
then starts the server by invoking the extension's launcher command through
Visual Studio's own DTE automation. That is the same load path a Tools-menu click
takes; starting the server from a background package autoload instead left
debugger automation wedged, so attach/start-debugging never returned.

The command is accepted only for the one-time launch id this process was started
with, which travels both in the child's environment and as the command argument.

Nothing here goes through the pipe catalog: launching is a local process and COM
automation operation, which is why this feature does not change the AIDB/2 method
manifest that the extension and the Python bridge hash-check against each other.
"""

from __future__ import annotations

import asyncio
import ctypes
from ctypes import wintypes
from collections.abc import Iterable, Sequence
from contextlib import contextmanager
import json
import logging
import os
import pathlib
import re
import subprocess
import time
import uuid
from dataclasses import dataclass, field
from typing import Any, Dict, List, Optional, Tuple

try:
    from . import vs_bridge, vs_trust
    from .vs_errors import VSBridgeError
except ImportError:  # pragma: no cover - direct module import
    import vs_bridge  # type: ignore
    import vs_trust  # type: ignore
    from vs_errors import VSBridgeError  # type: ignore

logger = logging.getLogger(__name__)

# Environment contract with VSDebuggerMCPPackage.TryStartLauncherRequestedServer.
LAUNCH_ID_VARIABLE = "AIDB_LAUNCH_ID"
LAUNCH_CAPABILITIES_VARIABLE = "AIDB_LAUNCH_CAPABILITIES"
LAUNCH_EXPIRES_VARIABLE = "AIDB_LAUNCH_EXPIRES_UNIX_MS"
LAUNCH_REQUEST_LIFETIME_SECONDS = 300.0

DEFAULT_LAUNCH_TIMEOUT_SECONDS = 120.0
FORCE_GRACE_SECONDS = 5.0
FORCE_KILL_SECONDS = 5.0
READY_POLL_INTERVAL_SECONDS = 0.5
VERSION_QUERY_TIMEOUT_SECONDS = 10.0

# The /Command value: the launcher command itself. Any /Command suppresses the Start Page
# (otherwise the IDE parks on "choose a project or continue without code" and never
# finishes starting up), so this one switch both skips that screen and starts the server.
# The name carries the menu prefix Visual Studio registers commands under.
START_PAGE_SUPPRESSING_COMMAND = "Tools.AIDebugger.StartFullAccessServer"
LAUNCH_SWITCH_GRACE_SECONDS = 20.0
PROCESS_EXITED_FAILURE = "process_exited"

# The command the launcher dispatches. It reuses the long-standing "choose capabilities"
# command, which switches to a non-interactive start when it receives the one-time launch
# id. Visual Studio exposes the canonical name with the top-level menu prefixed, so the
# name is resolved from the live command table rather than assumed.
LAUNCH_COMMAND_CANONICAL_NAME = "AIDebugger.StartFullAccessServer"
LAUNCH_COMMAND_NAME_CANDIDATES = (
    "Tools.AIDebugger.StartFullAccessServer",
    "AIDebugger.StartFullAccessServer",
)
DTE_LOOKUP_TIMEOUT_SECONDS = 45.0
DTE_POLL_INTERVAL_SECONDS = 1.0

READY_STATUS = "launched"
REUSED_STATUS = "reused"
AMBIGUOUS_STATUS = "ambiguous"
READY_TIMEOUT_STATUS = "ready_timeout"
LAUNCH_FAILED_STATUS = "launch_failed"

LAUNCH_FAILED_ERROR_CODE = "LAUNCH_FAILED"
INSTALLATION_NOT_FOUND_ERROR_CODE = "VS_INSTALLATION_NOT_FOUND"

_LAUNCH_ID_PATTERN = re.compile(r"\A[0-9a-f]{32}\Z")
_ROOT_SUFFIX_PATTERN = re.compile(r"\A[A-Za-z0-9]{1,48}\Z")
_CAPABILITY_PATTERN = re.compile(r"\A[a-z][a-z0-9.]{0,63}\Z")
_PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
_NT_STILL_ACTIVE = 259
_DOTNET_EPOCH_TICKS = 504911232000000000


@dataclass(frozen=True)
class DevenvInstallation:
    """A launchable Visual Studio IDE installation that passed the trust allow-list."""

    instance_id: str
    installation_version: str
    product_id: str
    install_path: str
    devenv_path: str

    def to_dict(self) -> Dict[str, Any]:
        return {
            "instanceId": self.instance_id,
            "installationVersion": self.installation_version,
            "productId": self.product_id,
            "installPath": self.install_path,
            "devenvPath": self.devenv_path,
        }


@dataclass
class LaunchRecord:
    """A Visual Studio process this MCP server started."""

    process_id: int
    launch_id: str
    installation: DevenvInstallation
    requested_capabilities: Tuple[str, ...]
    start_time_utc_ticks: Optional[int]
    process: Optional[subprocess.Popen] = None
    solution_path: Optional[str] = None
    root_suffix: Optional[str] = None
    ready_instance: Optional[Any] = None
    failure: Optional[str] = None
    diagnostics: List[str] = field(default_factory=list)


_LAUNCHES: Dict[int, LaunchRecord] = {}
_LAUNCHES_LOADED = False
LAUNCH_RECORD_SCHEMA_VERSION = 1
_LAUNCH_RECORD_NAME = re.compile(r"\A([1-9][0-9]{0,9})\.json\Z")


def _launches_root() -> pathlib.Path:
    local_app_data = os.environ.get("LOCALAPPDATA")
    if not local_app_data:
        raise VSBridgeError("LOCALAPPDATA is unavailable", code="INSTANCE_REGISTRATION_NOT_FOUND")
    return pathlib.Path(local_app_data) / "AIDebugger" / "launches"


def _delete_launch_record(process_id: int) -> None:
    try:
        (_launches_root() / f"{int(process_id)}.json").unlink(missing_ok=True)
    except (OSError, VSBridgeError) as exc:
        logger.warning("Unable to remove the launch record for PID %s: %s", process_id, exc)


def _persist_launch_record(record: LaunchRecord) -> None:
    """Record ownership on disk so a restarted MCP server can still close its own instances.

    Losing this record must never break a launch, so every failure here is logged and
    swallowed: the in-process registry remains authoritative for this session.
    """
    if record.start_time_utc_ticks is None:
        return
    payload = {
        "schemaVersion": LAUNCH_RECORD_SCHEMA_VERSION,
        "processId": record.process_id,
        "processStartTimeUtcTicks": record.start_time_utc_ticks,
        "launchId": record.launch_id,
        "devenvPath": record.installation.devenv_path,
        "installPath": record.installation.install_path,
        "installationVersion": record.installation.installation_version,
        "instanceId": record.installation.instance_id,
        "productId": record.installation.product_id,
        "requestedCapabilities": list(record.requested_capabilities),
        "solutionPath": record.solution_path,
        "rootSuffix": record.root_suffix,
        "startedAtUnixMs": int(time.time() * 1000),
    }
    try:
        root = vs_trust.ensure_owner_only_directory(_launches_root())
        path = root / f"{record.process_id}.json"
        temporary = root / f"{record.process_id}.{uuid.uuid4().hex}.tmp"
        temporary.write_text(json.dumps(payload, separators=(",", ":")), encoding="utf-8")
        # The record file needs its own protected owner-only DACL: the loader validates the
        # file, not just the directory, so an inherited DACL makes it unreadable to us.
        vs_trust.apply_owner_only_security(temporary)
        os.replace(temporary, path)
    except Exception as exc:  # noqa: BLE001 - a record failure must never break a launch
        # Deliberately broad: pywin32 raises pywintypes.error (not OSError) from the ACL
        # calls, and a failed record must not abort a launch that already started devenv.
        logger.warning("Unable to persist the launch record for PID %s: %s", record.process_id, exc)


def _load_persisted_launches() -> None:
    """Seed ownership from the owner-only launch records, pruning dead or reused PIDs."""
    global _LAUNCHES_LOADED
    if _LAUNCHES_LOADED:
        return
    _LAUNCHES_LOADED = True
    try:
        root = _launches_root()
        entries = sorted(root.glob("*.json")) if root.is_dir() else []
    except (OSError, VSBridgeError) as exc:
        logger.warning("Unable to read persisted launch records: %s", exc)
        return
    for entry in entries:
        match = _LAUNCH_RECORD_NAME.match(entry.name)
        if match is None or int(match.group(1)) in _LAUNCHES:
            continue
        try:
            vs_trust._validate_registration_security(entry, subdirectory="launches")
            payload = json.loads(entry.read_text(encoding="utf-8"))
        except Exception as exc:  # noqa: BLE001 - one bad record must not abort the load
            # pywin32's ACL validation raises pywintypes.error, which is not an OSError.
            logger.warning("Ignoring an untrusted launch record %s: %s", entry.name, exc)
            continue
        file_process_id = int(match.group(1))
        if not isinstance(payload, dict):
            _delete_launch_record(file_process_id)
            continue
        if type(payload.get("schemaVersion")) is not int or payload["schemaVersion"] != LAUNCH_RECORD_SCHEMA_VERSION:
            logger.warning("Ignoring unsupported launch record schema in %s", entry.name)
            continue
        process_id = payload.get("processId")
        start_ticks = payload.get("processStartTimeUtcTicks")
        devenv_path = payload.get("devenvPath")
        capabilities = payload.get("requestedCapabilities")
        if (type(process_id) is not int or process_id != file_process_id
                or not 0 < process_id <= 0x7FFFFFFF
                or type(start_ticks) is not int or start_ticks <= 0
                or not isinstance(devenv_path, str) or not devenv_path.strip()
                or not isinstance(capabilities, list)
                or any(not isinstance(value, str) or not value for value in capabilities)):
            _delete_launch_record(file_process_id)
            continue
        # A reused PID or an exited process must never look like an owned instance.
        state = _process_state(process_id)
        if state is None:
            # Access/query failure is not proof of exit. Preserve the receipt and
            # retry loading later; do not grant close ownership until verified.
            _LAUNCHES_LOADED = False
            continue
        current_ticks, running = state
        if not running or current_ticks != start_ticks:
            _delete_launch_record(process_id)
            continue
        _LAUNCHES[process_id] = LaunchRecord(
            process_id=process_id,
            launch_id=str(payload.get("launchId") or ""),
            installation=DevenvInstallation(
                instance_id=str(payload.get("instanceId") or ""),
                installation_version=str(payload.get("installationVersion") or ""),
                product_id=str(payload.get("productId") or ""),
                install_path=str(payload.get("installPath") or ""),
                devenv_path=devenv_path,
            ),
            requested_capabilities=tuple(capabilities) if isinstance(capabilities, list) else (),
            start_time_utc_ticks=start_ticks,
            process=None,
            solution_path=payload.get("solutionPath") if isinstance(payload.get("solutionPath"), str) else None,
            root_suffix=payload.get("rootSuffix") if isinstance(payload.get("rootSuffix"), str) else None,
        )


def owned_launches() -> Dict[int, LaunchRecord]:
    """Launches this MCP server owns, keyed by Visual Studio PID, including persisted ones."""
    _load_persisted_launches()
    return dict(_LAUNCHES)


def get_owned_launch(process_id: int) -> Optional[LaunchRecord]:
    _load_persisted_launches()
    return _LAUNCHES.get(int(process_id))


# ---------------------------------------------------------------------------
# Installation discovery
# ---------------------------------------------------------------------------


def _vswhere_path() -> pathlib.Path:
    program_files_x86 = os.environ.get("ProgramFiles(x86)")
    if not program_files_x86:
        raise VSBridgeError(
            "Program Files (x86) is unavailable, so Visual Studio installations cannot be located",
            code=INSTALLATION_NOT_FOUND_ERROR_CODE,
        )
    return pathlib.Path(program_files_x86) / "Microsoft Visual Studio" / "Installer" / "vswhere.exe"


def _parse_version(value: str) -> Tuple[int, ...]:
    parts = []
    for chunk in str(value or "").split("."):
        if not chunk.isdigit():
            break
        parts.append(int(chunk))
    return tuple(parts)


def _is_ide_product(product_id: str) -> bool:
    """Build Tools and other non-IDE products ship devenv.exe without a usable IDE."""
    normalized = str(product_id or "").strip().lower()
    if not normalized:
        return False
    return not normalized.endswith("buildtools")


def discover_devenv_installations(
    timeout: float = VERSION_QUERY_TIMEOUT_SECONDS,
) -> List[DevenvInstallation]:
    """Return launchable IDE installations, newest first, filtered by the trust allow-list."""
    vswhere = _vswhere_path()
    if not vswhere.is_file():
        raise VSBridgeError(
            f"Visual Studio installer discovery is unavailable at '{vswhere}'",
            code=INSTALLATION_NOT_FOUND_ERROR_CODE,
        )
    try:
        completed = subprocess.run(
            [str(vswhere), "-all", "-products", "*", "-format", "json", "-utf8"],
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="strict",
            timeout=timeout,
            check=False,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
    except (OSError, subprocess.SubprocessError, UnicodeError) as exc:
        raise VSBridgeError(
            "Unable to query the Visual Studio installer for installations",
            code=INSTALLATION_NOT_FOUND_ERROR_CODE,
        ) from exc
    if completed.returncode != 0:
        raise VSBridgeError(
            f"Visual Studio installer discovery failed with exit code {completed.returncode}",
            code=INSTALLATION_NOT_FOUND_ERROR_CODE,
        )
    try:
        payload = json.loads(completed.stdout) if completed.stdout.strip() else []
    except json.JSONDecodeError as exc:
        raise VSBridgeError(
            "Visual Studio installer discovery returned malformed JSON",
            code=INSTALLATION_NOT_FOUND_ERROR_CODE,
        ) from exc
    if not isinstance(payload, list):
        payload = []

    try:
        trusted = vs_trust._trusted_visual_studio_paths()
    except VSBridgeError as exc:
        raise VSBridgeError(
            f"Visual Studio installations could not be validated against the trust allow-list: {exc}",
            code=INSTALLATION_NOT_FOUND_ERROR_CODE,
        ) from exc
    installations: List[DevenvInstallation] = []
    rejected: List[str] = []
    for item in payload:
        if not isinstance(item, dict):
            continue
        install_path = item.get("installationPath")
        if not isinstance(install_path, str) or not install_path:
            continue
        product_id = str(item.get("productId") or "")
        if not _is_ide_product(product_id):
            continue
        devenv_path = os.path.join(install_path, "Common7", "IDE", "devenv.exe")
        normalized = os.path.normcase(os.path.abspath(devenv_path))
        if normalized not in trusted:
            rejected.append(devenv_path)
            continue
        if not pathlib.Path(devenv_path).is_file():
            continue
        installations.append(
            DevenvInstallation(
                instance_id=str(item.get("instanceId") or ""),
                installation_version=str(item.get("installationVersion") or ""),
                product_id=product_id,
                install_path=install_path,
                devenv_path=devenv_path,
            )
        )
    if not installations and rejected:
        raise VSBridgeError(
            "Visual Studio installations were found but none is a trusted launchable IDE:\n"
            + "\n".join(f"  - {path}" for path in rejected),
            code=INSTALLATION_NOT_FOUND_ERROR_CODE,
        )
    installations.sort(
        key=lambda item: (_parse_version(item.installation_version), item.instance_id),
        reverse=True,
    )
    return installations


def select_devenv_installation(
    installations: Sequence[DevenvInstallation],
    install_path: Optional[str] = None,
    instance_id: Optional[str] = None,
) -> DevenvInstallation:
    """Pick the installation to launch, honouring an explicit request."""
    if not installations:
        raise VSBridgeError(
            "No launchable Visual Studio installation was found. Install Visual Studio, "
            "or pass install_path/instance_id for an installation whose devenv.exe is trusted.",
            code=INSTALLATION_NOT_FOUND_ERROR_CODE,
        )
    # Sort here rather than trusting the caller's order, so the newest IDE is chosen
    # no matter how the candidate list was assembled.
    installations = sorted(
        installations,
        key=lambda item: (_parse_version(item.installation_version), item.instance_id),
        reverse=True,
    )
    if install_path is not None or instance_id is not None:
        normalized_install = (
            os.path.normcase(os.path.abspath(install_path)) if isinstance(install_path, str) else None
        )
        matches = [
            item
            for item in installations
            if (instance_id is None or item.instance_id == instance_id)
            and (
                normalized_install is None
                or os.path.normcase(os.path.abspath(item.install_path)) == normalized_install
            )
        ]
        if not matches:
            available = "\n".join(
                f"  - instanceId={item.instance_id or '<unknown>'} version={item.installation_version} "
                f"path={item.install_path}"
                for item in installations
            )
            raise VSBridgeError(
                "The requested Visual Studio installation is not a trusted launchable IDE.\n"
                f"Available installations:\n{available}",
                code="TARGET_NOT_FOUND",
            )
        return matches[0]
    return installations[0]


# ---------------------------------------------------------------------------
# Request contract
# ---------------------------------------------------------------------------


def all_launchable_capabilities() -> Tuple[str, ...]:
    """Every capability the installed method manifest can request."""
    try:
        from .vs_tool_registry import all_vs_capabilities
    except ImportError:  # pragma: no cover - direct module import
        from vs_tool_registry import all_vs_capabilities  # type: ignore
    return tuple(all_vs_capabilities())


def parse_requested_capabilities(raw: Optional[str]) -> Tuple[str, ...]:
    """Normalize the caller's capability request; the default is the full debug set."""
    if raw is None or not str(raw).strip():
        return all_launchable_capabilities()

    requested: List[str] = []
    for entry in str(raw).split(","):
        name = entry.strip()
        if not name:
            continue
        if not _CAPABILITY_PATTERN.match(name):
            raise VSBridgeError(
                f"Capability name '{name}' is malformed",
                code="INVALID_ARGUMENT",
            )
        if name not in requested:
            requested.append(name)

    if not requested:
        raise VSBridgeError(
            "capabilities must name at least one capability when it is provided",
            code="INVALID_ARGUMENT",
        )
    known = set(all_launchable_capabilities()) | {"server.control"}
    unknown = [name for name in requested if name not in known]
    if unknown:
        raise VSBridgeError(
            "Unknown capability names requested: " + ", ".join(sorted(unknown)),
            code="INVALID_ARGUMENT",
        )
    # start_server always requires the control plane, and the extension adds it
    # regardless; asking for it explicitly keeps the request self-describing.
    if "server.control" not in requested:
        requested.append("server.control")
    return tuple(sorted(requested))


def build_launch_environment(
    capabilities: Iterable[str],
    launch_id: str,
    expires_unix_ms: int,
    base: Optional[Dict[str, str]] = None,
) -> Dict[str, str]:
    """Environment for the devenv child: the launcher request plus the parent environment."""
    environment = dict(os.environ if base is None else base)
    environment[LAUNCH_ID_VARIABLE] = launch_id
    environment[LAUNCH_CAPABILITIES_VARIABLE] = ",".join(sorted(set(capabilities)))
    environment[LAUNCH_EXPIRES_VARIABLE] = str(int(expires_unix_ms))
    return environment


def build_launch_arguments(
    installation: DevenvInstallation,
    solution_path: Optional[str] = None,
    root_suffix: Optional[str] = None,
) -> List[str]:
    """Command line for devenv.exe: switches first, the optional solution last."""
    arguments = [installation.devenv_path]
    if root_suffix is not None:
        if not _ROOT_SUFFIX_PATTERN.match(str(root_suffix)):
            raise VSBridgeError(
                "root_suffix must be 1-48 alphanumeric characters",
                code="INVALID_ARGUMENT",
            )
        arguments.extend(["/RootSuffix", str(root_suffix)])
    if solution_path is not None:
        candidate = pathlib.Path(str(solution_path))
        if not candidate.is_file():
            raise VSBridgeError(
                f"solution_path does not exist: '{solution_path}'",
                code="INVALID_ARGUMENT",
            )
        arguments.append(str(candidate))
    # /Command is the only documented switch that suppresses the Start Page, and a
    # harmless built-in keeps the IDE from parking on the "choose a project" screen
    # waiting for a decision. That wait also kept startup from ever completing, which
    # in turn left debugger automation unusable (attach/start-debugging hung).
    arguments.extend(["/Command", START_PAGE_SUPPRESSING_COMMAND])
    return arguments


# ---------------------------------------------------------------------------
# Process handling
# ---------------------------------------------------------------------------


def _process_state(process_id: int, handle=None) -> Optional[Tuple[int, bool]]:
    """Creation ticks plus liveness; None means the OS query is unavailable.

    Opening a process is not evidence of life: Windows keeps the process object, with its
    recorded creation time, queryable for as long as any handle to it remains open, so a
    Visual Studio that has already terminated can still be opened by its PID.
    GetExitCodeProcess is what decides.
    """
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
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
    kernel32.GetExitCodeProcess.restype = wintypes.BOOL
    kernel32.GetExitCodeProcess.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel32.CloseHandle.restype = wintypes.BOOL
    owns_handle = handle is None
    if owns_handle:
        handle = kernel32.OpenProcess(_PROCESS_QUERY_LIMITED_INFORMATION, False, int(process_id))
    if not handle:
        if ctypes.get_last_error() == 87 and int(process_id) > 0:
            # ERROR_INVALID_PARAMETER: this PID has no process object. Access
            # denied and other query failures remain unknown.
            return 0, False
        return None
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
            return None
        exit_code = wintypes.DWORD()
        if not kernel32.GetExitCodeProcess(handle, ctypes.byref(exit_code)):
            return None
        filetime = (creation.dwHighDateTime << 32) | creation.dwLowDateTime
        return filetime + _DOTNET_EPOCH_TICKS, exit_code.value == _NT_STILL_ACTIVE
    finally:
        if owns_handle:
            kernel32.CloseHandle(handle)


@contextmanager
def _pin_owned_process(record: LaunchRecord):
    """Keep the validated Windows process object alive while taskkill uses its PID."""
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel32.OpenProcess.restype = wintypes.HANDLE
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel32.CloseHandle.restype = wintypes.BOOL
    handle = kernel32.OpenProcess(_PROCESS_QUERY_LIMITED_INFORMATION, False, record.process_id)
    if not handle:
        raise VSBridgeError("Could not pin the owned process before close", code="PROCESS_STATE_UNAVAILABLE")
    try:
        state = _process_state(record.process_id, handle=handle)
        if state is None or record.start_time_utc_ticks is None:
            raise VSBridgeError("Could not verify the pinned process identity", code="PROCESS_STATE_UNAVAILABLE")
        if state != (record.start_time_utc_ticks, True):
            raise VSBridgeError("The pinned process no longer matches the running owned instance", code="STALE_TARGET")
        yield handle
    finally:
        kernel32.CloseHandle(handle)


def _process_start_time_utc_ticks(process_id: int) -> Optional[int]:
    """.NET-style creation ticks, but only for a process that is still running.

    Callers compare these ticks to decide whether a PID still belongs to the process they
    started, so a terminated process must not answer here even though its object may still
    be openable.
    """
    state = _process_state(process_id)
    if state is None:
        return None
    ticks, running = state
    return ticks if running else None


def _process_image_name(process_id: int) -> Optional[str]:
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.OpenProcess.restype = wintypes.HANDLE
    kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel32.QueryFullProcessImageNameW.restype = wintypes.BOOL
    kernel32.QueryFullProcessImageNameW.argtypes = [
        wintypes.HANDLE,
        wintypes.DWORD,
        wintypes.LPWSTR,
        ctypes.POINTER(wintypes.DWORD),
    ]
    handle = kernel32.OpenProcess(_PROCESS_QUERY_LIMITED_INFORMATION, False, int(process_id))
    if not handle:
        return None
    try:
        capacity = wintypes.DWORD(32768)
        buffer = ctypes.create_unicode_buffer(capacity.value)
        if not kernel32.QueryFullProcessImageNameW(handle, 0, buffer, ctypes.byref(capacity)):
            return None
        return pathlib.Path(buffer.value).name
    finally:
        kernel32.CloseHandle(handle)


def _record_liveness(record: LaunchRecord) -> Optional[bool]:
    """Whether the Visual Studio this record launched is still running.

    A record restored from disk carries no process handle, so the PID alone is not the
    instance: Windows reuses PIDs, and a terminated process stays queryable while any
    handle to it is open. The recorded creation time is the identity and the exit code is
    the liveness, so neither a stranger that inherited the PID nor a process that has
    already exited counts as alive - reporting either as alive is what made a close that
    had actually completed look like a timeout.
    """
    if record.process is not None:
        return record.process.poll() is None
    if record.start_time_utc_ticks is None:
        # Nothing was recorded to compare against; existence is all that is left.
        state = _process_state(record.process_id)
        return state[1] if state is not None else None
    state = _process_state(record.process_id)
    if state is None:
        return None
    ticks, running = state
    return running and ticks == record.start_time_utc_ticks


def _record_alive(record: LaunchRecord) -> bool:
    return _record_liveness(record) is True


def launch_devenv(
    installation: DevenvInstallation,
    capabilities: Iterable[str],
    solution_path: Optional[str] = None,
    root_suffix: Optional[str] = None,
) -> LaunchRecord:
    """Start devenv.exe with a launcher request in its environment."""
    arguments = build_launch_arguments(installation, solution_path, root_suffix)
    launch_id = uuid.uuid4().hex
    expires_unix_ms = int(time.time() * 1000 + LAUNCH_REQUEST_LIFETIME_SECONDS * 1000)
    environment = build_launch_environment(capabilities, launch_id, expires_unix_ms)
    try:
        # stdin/stdout/stderr must not be inherited: this MCP server's stdio carries the
        # MCP protocol, so anything devenv writes there corrupts the channel, and a write
        # to a pipe nobody drains can block the writing thread inside Visual Studio.
        process = subprocess.Popen(
            arguments,
            env=environment,
            close_fds=True,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            creationflags=getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0),
        )
    except OSError as exc:
        raise VSBridgeError(
            f"Unable to start Visual Studio at '{installation.devenv_path}'",
            code=LAUNCH_FAILED_ERROR_CODE,
        ) from exc
    record = LaunchRecord(
        process_id=int(process.pid),
        launch_id=launch_id,
        installation=installation,
        requested_capabilities=tuple(sorted(set(capabilities))),
        start_time_utc_ticks=_process_start_time_utc_ticks(int(process.pid)),
        process=process,
        solution_path=solution_path,
        root_suffix=root_suffix,
    )
    _LAUNCHES[record.process_id] = record
    _persist_launch_record(record)
    logger.info(
        "Launched Visual Studio PID %s from %s for AIDebugger launch %s",
        record.process_id,
        installation.devenv_path,
        launch_id,
    )
    return record


def _resolve_launcher_command_name(dte) -> Optional[str]:
    """Return the command name Visual Studio actually registered for this package.

    The table prefixes the top-level menu ("Tools.AIDebugger.X"), which is also why a
    name that looks canonical in the .vsct can be rejected as "not valid".
    """
    for name in LAUNCH_COMMAND_NAME_CANDIDATES:
        try:
            if dte.Commands.Item(name, 0) is not None:
                return name
        except Exception:
            continue
    try:
        for command in dte.Commands:
            try:
                name = str(command.Name)
            except Exception:
                continue
            if name.endswith(LAUNCH_COMMAND_CANONICAL_NAME):
                return name
    except Exception:
        pass
    return None


def _invoke_launcher_command(process_id: int, launch_id: str) -> str:
    """Trigger the launcher command in the Visual Studio process we started.

    Runs entirely on one thread because COM automation objects are apartment-bound.
    Returns a status string for diagnostics; never raises.
    """
    try:
        import pythoncom
        import win32com.client
    except ImportError as exc:  # pywin32 is a documented runtime requirement
        return f"Visual Studio automation is unavailable (pywin32: {exc}); start Visual Studio manually and use Tools > Start AIDebugger MCP Server"

    pythoncom.CoInitialize()
    last_error = "no matching DTE object was registered"
    deadline = time.monotonic() + DTE_LOOKUP_TIMEOUT_SECONDS
    try:
        while time.monotonic() < deadline:
            dte = None
            try:
                rot = pythoncom.GetRunningObjectTable()
                for moniker in rot:
                    try:
                        display = str(moniker.GetDisplayName(pythoncom.CreateBindCtx(0), None))
                    except Exception:
                        continue
                    # Visual Studio registers its automation object as
                    # "!VisualStudio.DTE.<version>:<pid>"; the suffix is the process id.
                    if not display.startswith("!VisualStudio.DTE."):
                        continue
                    suffix = display.rsplit(":", 1)[-1]
                    if not suffix.isdigit() or int(suffix) != int(process_id):
                        continue
                    # GetObject hands back a raw PyIUnknown; win32com needs the
                    # IDispatch interface queried out of it before dispatching.
                    dispatch = rot.GetObject(moniker).QueryInterface(pythoncom.IID_IDispatch)
                    dte = win32com.client.Dispatch(dispatch)
                    break
            except Exception as exc:
                last_error = f"{type(exc).__name__}: {exc}"

            if dte is not None:
                command_name = _resolve_launcher_command_name(dte)
                if command_name is None:
                    last_error = (
                        f"Visual Studio has not registered '{LAUNCH_COMMAND_CANONICAL_NAME}' yet; "
                        "the extension may not be installed for this instance"
                    )
                else:
                    try:
                        # No argument: the command reads the launch id from this process's
                        # environment, and Visual Studio rejects arguments for commands
                        # that do not carry the AllowParams flag.
                        dte.ExecuteCommand(command_name)
                        return f"launcher command dispatched as {command_name}"
                    except Exception as exc:
                        last_error = f"ExecuteCommand({command_name}) failed: {type(exc).__name__}: {exc}"
            time.sleep(DTE_POLL_INTERVAL_SECONDS)
    finally:
        pythoncom.CoUninitialize()
    return (
        f"could not start the server through Visual Studio automation ({last_error}); "
        "start Visual Studio manually and use Tools > Start AIDebugger MCP Server"
    )


async def _request_launcher_server_start(record: LaunchRecord) -> str:
    """Ask the launched instance to start its MCP server, off the event loop."""
    loop = asyncio.get_running_loop()
    return await loop.run_in_executor(
        None, _invoke_launcher_command, record.process_id, record.launch_id
    )


async def wait_for_ready_instance(
    record: LaunchRecord,
    timeout_seconds: float,
) -> Tuple[Optional[Any], List[str]]:
    """Wait for the launched instance to publish an authenticated, reachable registration."""
    deadline = time.monotonic() + max(0.0, timeout_seconds)
    diagnostics = record.diagnostics
    while True:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            record.failure = record.failure or READY_TIMEOUT_STATUS
            return None, diagnostics

        timeout = min(vs_bridge.DEFAULT_TIMEOUT, max(1.0, remaining))
        try:
            instances = await vs_bridge.discover_instances(timeout=timeout)
        except VSBridgeError as exc:
            diagnostics.append(f"discovery failed while waiting for the launched instance: {exc}")
            instances = []

        for instance in instances:
            if instance.process_id != record.process_id:
                continue
            if instance.connection_status == "ok":
                record.ready_instance = instance
                return instance, diagnostics
            if instance.last_error:
                entry = (
                    f"Visual Studio PID {instance.process_id} has a registration that is not "
                    f"reachable yet: {instance.last_error}"
                )
                if entry not in diagnostics:
                    diagnostics.append(entry)

        if not _record_alive(record):
            record.failure = PROCESS_EXITED_FAILURE
            exit_code = record.process.returncode if record.process is not None else None
            diagnostics.append(
                f"Visual Studio PID {record.process_id} exited before the AIDebugger server "
                f"registered (exit code {exit_code}). Visual Studio hands a solution that is already "
                "open in another instance over to that instance and exits; omit solution_path to "
                "avoid that."
            )
            return None, diagnostics

        await asyncio.sleep(min(READY_POLL_INTERVAL_SECONDS, max(0.0, deadline - time.monotonic())))


@dataclass
class EnsureOutcome:
    status: str
    message: str
    instance: Optional[Any] = None
    record: Optional[LaunchRecord] = None
    diagnostics: List[str] = field(default_factory=list)


def _reachable(instances: Iterable[Any]) -> List[Any]:
    return [instance for instance in instances if instance.connection_status == "ok"]


def _launch_matches_request(
    candidate: LaunchRecord,
    requested_capabilities: Sequence[str],
    install_path: Optional[str],
    instance_id: Optional[str],
    solution_path: Optional[str],
    root_suffix: Optional[str],
) -> bool:
    """Whether an in-flight launch already satisfies this caller's selection request."""
    if root_suffix != candidate.root_suffix or solution_path != candidate.solution_path:
        return False
    # Joining a launch that asked for a different access set would hand back an instance
    # that cannot serve the request, so the capability sets must match exactly.
    if tuple(sorted(set(candidate.requested_capabilities))) != tuple(sorted(set(requested_capabilities))):
        return False
    if instance_id is not None and candidate.installation.instance_id != instance_id:
        return False
    if install_path is not None:
        try:
            requested_path = os.path.normcase(os.path.abspath(install_path))
        except (OSError, ValueError):
            return False
        if os.path.normcase(os.path.abspath(candidate.installation.install_path)) != requested_path:
            return False
    return True


_LAUNCH_START_LOCK: Optional[asyncio.Lock] = None
_LAUNCH_START_LOCK_LOOP: Optional[asyncio.AbstractEventLoop] = None


def _launch_start_lock() -> asyncio.Lock:
    """Serialise the check-then-start sequence for callers on the running loop.

    The lock is created per event loop rather than at import time: an asyncio
    primitive binds to the loop that first awaits it, and a server restart (or a
    test) creates a new loop in the same process.
    """
    global _LAUNCH_START_LOCK, _LAUNCH_START_LOCK_LOOP
    loop = asyncio.get_running_loop()
    if _LAUNCH_START_LOCK is None or _LAUNCH_START_LOCK_LOOP is not loop:
        _LAUNCH_START_LOCK = asyncio.Lock()
        _LAUNCH_START_LOCK_LOOP = loop
    return _LAUNCH_START_LOCK


def _find_joinable_launch(
    requested_capabilities: Sequence[str],
    install_path: Optional[str],
    instance_id: Optional[str],
    solution_path: Optional[str],
    root_suffix: Optional[str],
) -> Optional[LaunchRecord]:
    """The in-flight launch this request can join, if any. Cheap; no blocking I/O."""
    for candidate in _LAUNCHES.values():
        if (
            candidate.ready_instance is None
            and candidate.failure is None
            and _record_alive(candidate)
            and _launch_matches_request(
                candidate, requested_capabilities, install_path, instance_id, solution_path, root_suffix
            )
        ):
            return candidate
    return None


def _select_and_launch(
    requested_capabilities: Sequence[str],
    install_path: Optional[str],
    instance_id: Optional[str],
    solution_path: Optional[str],
    root_suffix: Optional[str],
) -> LaunchRecord:
    """Locate a trusted installation and start it. Runs on a worker thread."""
    installations = discover_devenv_installations()
    installation = select_devenv_installation(installations, install_path, instance_id)
    return launch_devenv(
        installation,
        requested_capabilities,
        solution_path=solution_path,
        root_suffix=root_suffix,
    )


async def _start_or_join_launch(
    requested_capabilities: Sequence[str],
    install_path: Optional[str] = None,
    instance_id: Optional[str] = None,
    solution_path: Optional[str] = None,
    root_suffix: Optional[str] = None,
) -> Tuple[LaunchRecord, List[str], bool]:
    """Join a launch already in flight in this process, otherwise start one.

    Discovery shells out to vswhere and verifies signatures, and creating a process
    is not instant either, so both run on a worker thread: doing that work on the
    event loop stalls every other tool call for seconds. The lock keeps the
    check-then-start sequence atomic instead, so two concurrent callers cannot both
    launch a Visual Studio. The flag reports whether this call is the one that
    started the process, and therefore owns asking it to serve MCP.

    The lock is released before waiting for readiness, so a caller that arrives
    while a launch is still starting joins it instead of queueing behind it.
    """
    async with _launch_start_lock():
        joined = _find_joinable_launch(
            requested_capabilities, install_path, instance_id, solution_path, root_suffix
        )
        if joined is not None:
            return joined, [
                f"Joined the Visual Studio launch already in progress (PID {joined.process_id})."
            ], False
        record = await asyncio.get_running_loop().run_in_executor(
            None,
            _select_and_launch,
            requested_capabilities,
            install_path,
            instance_id,
            solution_path,
            root_suffix,
        )
        return record, [], True


async def ensure_instance(
    requested_capabilities: Sequence[str],
    install_path: Optional[str] = None,
    instance_id: Optional[str] = None,
    solution_path: Optional[str] = None,
    root_suffix: Optional[str] = None,
    reuse_existing: bool = True,
    timeout_seconds: float = DEFAULT_LAUNCH_TIMEOUT_SECONDS,
) -> EnsureOutcome:
    """Reuse a reachable instance or launch a new one, then wait until it is usable."""
    diagnostics: List[str] = []
    if reuse_existing:
        try:
            instances = await vs_bridge.discover_instances()
        except VSBridgeError as exc:
            instances = []
            diagnostics.append(f"discovery before launching failed: {exc}")
        reachable = _reachable(instances)
        if len(reachable) == 1:
            return EnsureOutcome(
                status=REUSED_STATUS,
                message=f"Reused the reachable Visual Studio instance PID {reachable[0].process_id}.",
                instance=reachable[0],
                diagnostics=diagnostics,
            )
        if len(reachable) > 1:
            candidates = vs_bridge.get_bridge().auto_select_instance(reachable)
            if candidates is not None:
                return EnsureOutcome(
                    status=REUSED_STATUS,
                    message=f"Reused the reachable Visual Studio instance PID {candidates.process_id}.",
                    instance=candidates,
                    diagnostics=diagnostics,
                )
            listed = "\n".join(
                f"  - PID {instance.process_id}: {instance.solution_name or '<no solution>'} "
                f"{'[paused]' if instance.is_paused else '[debugging]' if instance.is_debugging else '[idle]'}"
                for instance in reachable
            )
            return EnsureOutcome(
                status=AMBIGUOUS_STATUS,
                message=(
                    "Several Visual Studio instances are reachable, so none was reused and none was "
                    "launched. Call select_vs_instance(process_id=...) to pin one, or pass "
                    "reuse_existing=False to launch a dedicated instance.\n" + listed
                ),
                diagnostics=diagnostics,
            )

    deadline = time.monotonic() + max(0.0, timeout_seconds)
    record, launch_diagnostics, started_here = await _start_or_join_launch(
        requested_capabilities,
        install_path=install_path,
        instance_id=instance_id,
        solution_path=solution_path,
        root_suffix=root_suffix,
    )
    for entry in (*diagnostics, *launch_diagnostics):
        if entry not in record.diagnostics:
            record.diagnostics.append(entry)

    if started_here:
        # /Command normally starts the server while the shell comes up, so give that a
        # short head start before paying for COM automation. If it did not happen (an IDE
        # that never ran the switch, or an older extension), fall back to dispatching the
        # command through Visual Studio's own automation object.
        grace = min(LAUNCH_SWITCH_GRACE_SECONDS, max(0.0, deadline - time.monotonic()))
        instance, record_diagnostics = await wait_for_ready_instance(record, grace)
        if instance is None and record.failure != PROCESS_EXITED_FAILURE:
            # The short grace probe is not the final verdict; clear it before retrying.
            record.failure = None
            status = await _request_launcher_server_start(record)
            logger.info("Launcher command fallback for PID %s: %s", record.process_id, status)
            record.diagnostics.append(status)
            instance, record_diagnostics = await wait_for_ready_instance(
                record, max(0.0, deadline - time.monotonic())
            )
    else:
        instance, record_diagnostics = await wait_for_ready_instance(
            record, max(0.0, deadline - time.monotonic())
        )
    # Union rather than relying on wait_for_ready_instance returning record.diagnostics.
    for entry in record_diagnostics:
        if entry not in record.diagnostics:
            record.diagnostics.append(entry)
    record_diagnostics = list(record.diagnostics)
    if instance is None:
        status = LAUNCH_FAILED_STATUS if record.failure == PROCESS_EXITED_FAILURE else READY_TIMEOUT_STATUS
        message = (
            f"Visual Studio PID {record.process_id} started but did not publish a reachable "
            "AIDebugger registration in time."
        )
        if status == READY_TIMEOUT_STATUS:
            message += (
                " Check whether the AIDebuggerVS extension is installed and enabled for the "
                "installation that was launched, then retry."
            )
        return EnsureOutcome(
            status=status,
            message=message,
            record=record,
            diagnostics=record_diagnostics,
        )

    granted = tuple(instance.allowed_capabilities)
    missing = [name for name in record.requested_capabilities if name not in granted]
    if missing:
        record_diagnostics.append(
            "The launched Visual Studio granted a narrower capability set; missing: "
            + ", ".join(sorted(missing))
            + ". The installed AIDebuggerVS extension probably predates the launcher contract; "
            "reinstall the extension for this Visual Studio installation."
        )
    return EnsureOutcome(
        status=READY_STATUS,
        message=f"Launched Visual Studio PID {record.process_id} and it is ready.",
        instance=instance,
        record=record,
        diagnostics=record_diagnostics,
    )


async def close_owned_instance(
    process_id: int,
    force: bool = False,
    timeout_seconds: float = 30.0,
    debug_state: Optional[Dict[str, Any]] = None,
) -> Dict[str, Any]:
    """Close a Visual Studio instance this MCP server launched, never a user's instance."""
    record = get_owned_launch(process_id)
    if record is None:
        return {
            "success": False,
            "message": (
                f"Visual Studio PID {process_id} was not launched by this MCP server, so it will not "
                "be closed. Close it manually if that is what you want."
            ),
            "processId": int(process_id),
            "closed": False,
            "errorCode": "NOT_LAUNCHED_BY_MCP",
        }

    def _report_already_closed() -> Dict[str, Any]:
        # The launched instance is gone, so the record is pruned here too: leaving it behind
        # would keep a dead PID in the owned set (and in the on-disk record) forever.
        _LAUNCHES.pop(record.process_id, None)
        _delete_launch_record(record.process_id)
        return {
            "success": True,
            "message": f"Visual Studio PID {process_id} is already closed.",
            "processId": int(process_id),
            "closed": True,
            "forced": False,
            "graceful": True,
        }

    def _report_unavailable() -> Dict[str, Any]:
        return {
            "success": False,
            "message": f"Unable to verify whether Visual Studio PID {process_id} has exited; ownership was preserved.",
            "processId": int(process_id),
            "closed": False,
            "forced": False,
            "graceful": False,
            "errorCode": "PROCESS_STATE_UNAVAILABLE",
        }

    liveness = _record_liveness(record)
    if liveness is None:
        return _report_unavailable()
    if liveness is False:
        return _report_already_closed()
    current_ticks = _process_start_time_utc_ticks(record.process_id)
    if (
        record.start_time_utc_ticks is not None
        and current_ticks is not None
        and current_ticks != record.start_time_utc_ticks
    ):
        return {
            "success": False,
            "message": (
                f"PID {process_id} now belongs to a different process than the launched Visual Studio "
                "instance, so it will not be closed."
            ),
            "processId": int(process_id),
            "closed": False,
            "errorCode": "STALE_TARGET",
        }
    image_name = _process_image_name(record.process_id)
    if image_name is None:
        # A failed image query can also mean access failure, not just exit.
        return _report_already_closed() if _record_liveness(record) is False else _report_unavailable()
    if image_name.lower() != "devenv.exe":
        return {
            "success": False,
            "message": f"PID {process_id} is not a running devenv.exe process, so it will not be closed.",
            "processId": int(process_id),
            "closed": False,
            "errorCode": "STALE_TARGET",
        }
    if not force and (not isinstance(debug_state, dict) or type(debug_state.get("isDebugging")) is not bool):
        return {
            "success": False,
            "message": f"Visual Studio PID {process_id} has no verified debugger status; close was not started. Query its status or explicitly request force=True.",
            "processId": int(process_id),
            "closed": False,
            "errorCode": "DEBUG_STATE_UNAVAILABLE",
        }
    if not force and debug_state["isDebugging"]:
        return {
            "success": False,
            "message": (
                f"Visual Studio PID {process_id} is in a debug session. Stop or detach debugging first, "
                "or call close_vs_instance(force=True) to close it anyway."
            ),
            "processId": int(process_id),
            "closed": False,
            "errorCode": "DEBUG_SESSION_ACTIVE",
        }

    kill_attempts: List[str] = []

    def _taskkill(extra: Sequence[str]) -> None:
        """Ask Windows to close the process, keeping what the call actually reported.

        taskkill fails for real reasons (a process still winding up, an access boundary),
        and discarding its exit code and message made a close that never had a chance look
        exactly like one that timed out, with nothing left to diagnose it from.
        """
        # The worker owns the pin through subprocess completion even if its
        # async caller is cancelled. Never close this handle on the event loop.
        with _pin_owned_process(record):
            _run_taskkill(extra)

    def _run_taskkill(extra: Sequence[str]) -> None:
        label = f"taskkill /PID {record.process_id}" + (" " + " ".join(extra) if extra else "")
        try:
            completed = subprocess.run(
                ["taskkill", "/PID", str(record.process_id), *extra],
                capture_output=True,
                text=True,
                errors="replace",
                timeout=15,
                check=False,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
        except (OSError, subprocess.SubprocessError) as exc:
            detail = f"{label}: could not run ({type(exc).__name__}: {exc})"
        else:
            first_line = next(
                (
                    line.strip()
                    for line in (completed.stdout or completed.stderr or "").splitlines()
                    if line.strip()
                ),
                "",
            )
            detail = f"{label}: exit {completed.returncode}" + (
                f" ({first_line})" if first_line else ""
            )
        kill_attempts.append(detail)
        logger.info("Visual Studio PID %s close: %s", record.process_id, detail)

    def _report_pin_failure(error: VSBridgeError) -> Dict[str, Any]:
        if _record_liveness(record) is False:
            return _report_already_closed()
        return {
            "success": False, "message": str(error), "processId": record.process_id,
            "closed": False, "forced": False, "graceful": False, "errorCode": error.code,
        }

    try:
        await asyncio.to_thread(_taskkill, ())
    except VSBridgeError as error:
        return _report_pin_failure(error)
    # A forced close must not sit through the whole graceful timeout first: the caller asked
    # for the process to go away, so give Visual Studio a short window to exit cleanly and
    # escalate instead of blocking for tens of seconds.
    graceful_seconds = min(timeout_seconds, FORCE_GRACE_SECONDS) if force else timeout_seconds
    deadline = time.monotonic() + max(0.0, graceful_seconds)
    while time.monotonic() < deadline and _record_alive(record):
        await asyncio.sleep(0.25)

    forced = False
    # Checked immediately before the one destructive step, and deliberately _record_alive
    # rather than a plain existence test: it also matches the recorded creation time, so a PID
    # that changed hands during the grace window is never force-terminated.
    if force and _record_alive(record):
        try:
            await asyncio.to_thread(_taskkill, ("/F",))
        except VSBridgeError as error:
            return _report_pin_failure(error)
        forced = True
        force_deadline = time.monotonic() + FORCE_KILL_SECONDS
        while time.monotonic() < force_deadline and _record_alive(record):
            await asyncio.sleep(0.25)

    final_liveness = _record_liveness(record)
    closed = final_liveness is False
    # "Graceful" means the process left on its own; recomputing it after the escalation made a
    # forced close report both forced and graceful.
    graceful = closed and not forced
    if closed:
        _LAUNCHES.pop(record.process_id, None)
        _delete_launch_record(record.process_id)
    return {
        "success": closed,
        "message": (
            f"Closed the Visual Studio instance launched by this MCP server (PID {record.process_id})."
            if closed
            else f"Unable to verify exit of Visual Studio PID {record.process_id}; ownership was preserved."
            if final_liveness is None
            else (
                f"Visual Studio PID {record.process_id} did not close within the timeout: "
                + "; ".join(kill_attempts)
            )
        ),
        "processId": record.process_id,
        "closed": closed,
        "forced": forced,
        "graceful": graceful,
        "errorCode": None if closed else (
            "PROCESS_STATE_UNAVAILABLE" if final_liveness is None else "CLOSE_TIMEOUT"
        ),
    }
