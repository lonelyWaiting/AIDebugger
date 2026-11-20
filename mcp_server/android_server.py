"""Android native LLDB and Java JDWP debugging MCP entrypoint."""
import functools
import http.server  # Cache stdlib before the vendored path containing http.py.
from pathlib import Path
import re
import sys

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from mcp_server.typing_compat import ensure_pep655_typing_markers

ensure_pep655_typing_markers()
sys.path.insert(0, str(ROOT / "ida_backend/vendor/ida_pro_mcp/ida_mcp"))
from zeromcp import McpServer, McpToolError
sys.path.pop(0)
from android_backend.runtime import AndroidError, Runtime, destination
from android_backend.sessions import Sessions
from android_backend.transport import Adb
from debug_backend.errors import DebugEngineError
from debug_backend.inspection import DebugPlatform, InspectionView
from mcp_server.debug_discovery import register_debug_tools
from mcp_server.stdio_dispatch import serve

mcp = McpServer("AIDebugger Android")
runtime = Runtime(destination(ROOT))
sessions = Sessions(runtime)
CONTROL = {"pause", "continue", "step_into", "step_over", "step_out"}
JAVA_CLASS = re.compile(r"[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)+")


def address_value(address):
    if not re.fullmatch(r"0x[0-9a-fA-F]{1,16}", address):
        raise ValueError("Expected an Android load address in 0x hexadecimal notation")
    return int(address, 16)


def breakpoint_args(symbol="", file="", line=0, module="", address="", condition=""):
    if sum((bool(symbol), bool(file), bool(address))) != 1 or (
            not (1 <= line <= 2**32 - 1 if file else line == 0)) or (
            any(len(value) > 4096 for value in (symbol, file, module, condition))):
        raise ValueError("Specify one breakpoint location: symbol, file and line, or address")
    return dict(symbol=symbol, file=file, line=line, module=module,
                load_address=address_value(address) if address else None, condition=condition)


def java_breakpoint_args(class_name, method="", line=0, descriptor=""):
    if not JAVA_CLASS.fullmatch(class_name) or not (0 <= line <= 2**31 - 1) or not (method or line):
        raise ValueError("Specify a fully qualified Java class and a method or source line")
    if any(len(value) > 4096 for value in (class_name, method, descriptor)):
        raise ValueError("Java breakpoint parameter is too long")
    return dict(class_name=class_name, method=method, line=line, descriptor=descriptor)


def initial_breakpoints(items, parser):
    if len(items) > 16:
        raise ValueError("Use at most 16 initial breakpoints for each debugger")
    try:
        return [parser(**item) for item in items]
    except TypeError as error:
        raise ValueError("Invalid initial breakpoint fields or types") from error


def session_options(mode, deadline_ms):
    if mode not in {"native", "java", "dual"} or not 1000 <= deadline_ms <= 120_000:
        raise ValueError("Choose native, java or dual with a deadline of 1000..120000 ms")


def translate(function):
    @functools.wraps(function)
    def wrapped(*args, **kwargs):
        try:
            return function(*args, **kwargs)
        except (AndroidError, DebugEngineError, OSError, ValueError) as error:
            raise McpToolError(str(error)) from error
    return wrapped


@mcp.tool
@translate
def android_list_devices() -> dict:
    """List ADB devices and authorization states using the private Android runtime."""
    return {"devices": Adb(runtime.check()).devices()}


@mcp.tool
@translate
def android_attach(serial: str, package: str, process: str = "", pid: int | None = None,
                   abi: str = "", symbol_dirs: list[str] | None = None,
                   source_map: dict[str, str] | None = None, mode: str = "native",
                   deadline_ms: int = 60_000) -> dict:
    """Attach LLDB, Java JDWP, or both to a running debuggable app. Pass matching unstripped library directories and optional source mappings. Poll jobId."""
    session_options(mode, deadline_ms)
    return sessions.open(serial, package, process, pid, abi, symbol_dirs or [], source_map or {},
                         mode=mode, deadline_ms=deadline_ms)


@mcp.tool
@translate
def android_launch(serial: str, package: str, activity: str, mode: str = "native",
                   abi: str = "", symbol_dirs: list[str] | None = None,
                   source_map: dict[str, str] | None = None, deadline_ms: int = 60_000,
                   java_breakpoints: list[dict] | None = None,
                   native_breakpoints: list[dict] | None = None, process: str = "") -> dict:
    """Launch a stopped debuggable app's explicit activity with a startup debugger gate. Set initial breakpoints before releasing it. Java uses android_java_control; dual releases the native attach stop first, then the Java gate."""
    session_options(mode, deadline_ms)
    if not re.fullmatch(r"\.?[A-Za-z_$][\w.$]*", activity):
        raise ValueError("Specify an activity class such as .MainActivity")
    if java_breakpoints and mode == "native" or native_breakpoints and mode == "java":
        raise ValueError("Initial breakpoints must match the selected debug mode")
    return sessions.open(serial, package, process, None, abi, symbol_dirs or [], source_map or {},
                         activity=f"{package}/{activity}", mode=mode, deadline_ms=deadline_ms,
                         java_breakpoints=initial_breakpoints(java_breakpoints or [], java_breakpoint_args),
                         native_breakpoints=initial_breakpoints(native_breakpoints or [], breakpoint_args))


@mcp.tool
@translate
def android_list_sessions() -> dict:
    """List explicit Android sessions, lifecycle phases and active/latest jobs."""
    return sessions.listing()


@mcp.tool
@translate
def android_job_status(session_id: str, job_id: str) -> dict:
    """Poll Android job completion, error, and target-state receipt without blocking."""
    return sessions.job(session_id, job_id)


@mcp.tool
@translate
def android_cancel_job(session_id: str, job_id: str) -> dict:
    """Request cancellation of an active job. Poll its receipt. A dispatched mutation can have an unknown outcome."""
    return sessions.cancel(session_id, job_id)


@mcp.tool
@translate
def android_inspect(session_id: str, query: str, thread_id: int = 0, index: int = 0,
                    offset: int = 0, limit: int = 32) -> dict:
    """Inspect status, threads, stack, locals, modules or breakpoints. Except status, the process must be paused. Use index for the locals frame; offset/limit page lists. Poll jobId."""
    if query not in {"status", "threads", "stack", "locals", "modules", "breakpoints"}:
        raise ValueError("Unknown Android inspection query")
    if not 0 <= thread_id < 2**64 or not 0 <= index <= 65535 or not 0 <= offset <= 65535 or not 1 <= limit <= 128:
        raise ValueError("Invalid Android inspection range")
    return sessions.submit(session_id, "inspect", query=query, thread_id=thread_id,
                           index=index, offset=offset, limit=limit)


@mcp.tool
@translate
def android_set_breakpoint(session_id: str, symbol: str = "", file: str = "",
                           line: int = 0, module: str = "", address: str = "", condition: str = "") -> dict:
    """Add a native breakpoint by symbol, source line or load address with an optional LLDB condition. Poll jobId."""
    return sessions.submit(session_id, "set_breakpoint",
                           **breakpoint_args(symbol, file, line, module, address, condition))


@mcp.tool
@translate
def android_remove_breakpoint(session_id: str, breakpoint_id: int) -> dict:
    """Delete one breakpoint by its session-local ID. Poll jobId."""
    if not 0 < breakpoint_id < 2**31:
        raise ValueError("Expected a positive LLDB breakpoint ID")
    return sessions.submit(session_id, "remove_breakpoint", breakpoint_id=breakpoint_id)


@mcp.tool
@translate
def android_configure_breakpoint(session_id: str, breakpoint_id: int, enabled: bool | None = None,
                                 condition: str | None = None) -> dict:
    """Enable or disable a native breakpoint, or update its condition. An empty condition clears it."""
    if not 0 < breakpoint_id < 2**31 or condition is not None and len(condition) > 4096:
        raise ValueError("Invalid native breakpoint ID or condition")
    return sessions.submit(session_id, "configure_breakpoint", breakpoint_id=breakpoint_id,
                           enabled=enabled, condition=condition)


@mcp.tool
@translate
def android_control(session_id: str, action: str, thread_id: int = 0) -> dict:
    """Pause, continue, step_into, step_over or step_out. Poll jobId, then inspect status and stopId to confirm the next stop."""
    if action not in CONTROL or not 0 <= thread_id < 2**64:
        raise ValueError("Invalid Android execution action")
    return sessions.submit(session_id, "control", action=action, thread_id=thread_id)


@mcp.tool
@translate
def android_native_query(session_id: str, query: str, address: str = "", count: int = 32,
                         thread_id: int = 0, index: int = 0, expression: str = "", pattern_hex: str = "") -> dict:
    """Query native memory, search_memory, resolve_address, disassembly, registers or evaluate. Expressions can run code in the app. Poll jobId."""
    limits = {"memory": 65_536, "search_memory": 65_536, "resolve_address": 1,
              "disassembly": 256, "registers": 128, "evaluate": 1}
    if query in {"evaluate", "resolve_address"}:
        count = 1
    if query not in limits or not 1 <= count <= limits[query]:
        raise ValueError("Invalid native query or count")
    if not 0 <= thread_id < 2**64 or not 0 <= index <= 65535:
        raise ValueError("Invalid native thread or frame")
    if query in {"memory", "search_memory", "resolve_address"} and not address or (
            query == "evaluate" and not 0 < len(expression) <= 4096) or (
            query == "search_memory" and not re.fullmatch(r"(?:[0-9a-fA-F]{2}){1,256}", pattern_hex)):
        raise ValueError("Supply an address, expression or hex search pattern as required by the query")
    load_address = address_value(address) if address else None
    if load_address is not None and query in {"memory", "search_memory"} and load_address + count > 2**64:
        raise ValueError("Android memory range exceeds the address space")
    return sessions.submit(session_id, "native", query=query, load_address=load_address,
                           count=count, thread_id=thread_id, index=index, expression=expression,
                           pattern=pattern_hex)


@mcp.tool
@translate
def android_write_memory(session_id: str, address: str, data_hex: str,
                         expected_stop_id: int | None = None) -> dict:
    """Write 1..65536 bytes to a suspended native target. Poll jobId and check requestedRangeVerified for a complete write, or inspect bytesWritten and errors. Never replay an uncertain write."""
    if not 2 <= len(data_hex) <= 131_072 or not re.fullmatch(r"(?:[0-9a-fA-F]{2})+", data_hex):
        raise ValueError("Supply 1..65536 bytes as contiguous pairs of hex digits")
    load_address = address_value(address)
    if load_address + len(data_hex) // 2 >= 2**64:
        raise ValueError("Memory write range exceeds the address space")
    if expected_stop_id is not None and not 0 <= expected_stop_id < 2**32:
        raise ValueError("Invalid native stop ID")
    return sessions.submit(session_id, "write_memory", load_address=load_address,
                           data_hex=data_hex, expected_stop_id=expected_stop_id)


@mcp.tool
@translate
def android_watchpoint(session_id: str, address: str = "", size: int = 4, read: bool = False,
                       write: bool = True, remove_id: int = 0) -> dict:
    """Add a hardware data watchpoint by load address, or remove one by ID. Platform hardware limits apply. Poll jobId."""
    if remove_id and not (0 < remove_id < 2**31) or not remove_id and (
            not address or size not in (1, 2, 4, 8) or not (read or write)):
        raise ValueError("Invalid hardware watchpoint address, size, access or ID")
    return sessions.submit(session_id, "watchpoint", load_address=address_value(address) if address else None,
                           size=size, read=read, write=write, watchpoint_id=remove_id)


@mcp.tool
@translate
def android_snapshot(session_id: str, thread_id: int = 0, depth: int = 12, limit: int = 32) -> dict:
    """Capture status, stack and top-frame locals together for one native stop. Poll jobId."""
    if not 0 <= thread_id < 2**64 or not 1 <= depth <= 32 or not 1 <= limit <= 64:
        raise ValueError("Invalid snapshot range")
    return sessions.submit(session_id, "snapshot", thread_id=thread_id, depth=depth, limit=limit)


@mcp.tool
@translate
def android_run_until_stop(session_id: str, action: str = "", thread_id: int = 0,
                           after_stop_id: int | None = None, wait_ms: int = 10_000,
                           depth: int = 12, limit: int = 32) -> dict:
    """Optionally continue or step once, then wait server-side for a new native stop and return its stack and locals. Timeout leaves the target in its current state. Poll jobId; cancellation is supported."""
    if action and action not in CONTROL or not 0 <= thread_id < 2**64 or (
            after_stop_id is not None and after_stop_id < 0):
        raise ValueError("Invalid native stop request")
    if not 1 <= wait_ms <= 60_000 or not 1 <= depth <= 32 or not 1 <= limit <= 64:
        raise ValueError("Invalid wait or snapshot range")
    return sessions.submit(session_id, "wait_stop", action=action, thread_id=thread_id,
                           after_stop_id=after_stop_id, wait_ms=wait_ms, depth=depth, limit=limit,
                           deadline_ms=wait_ms + 30_000)


@mcp.tool
@translate
def android_java_inspect(session_id: str, query: str, thread_id: str = "", index: int = 0,
                         offset: int = 0, limit: int = 32) -> dict:
    """Inspect Java status, threads, stack, locals or breakpoints in a Java/dual session. Java thread IDs are decimal strings. Poll jobId."""
    if query not in {"status", "threads", "stack", "locals", "breakpoints"} or (
            thread_id and not re.fullmatch(r"[0-9]{1,20}", thread_id)) or (
            not 0 <= index <= 65535 or not 0 <= offset <= 65535 or not 1 <= limit <= 64):
        raise ValueError("Invalid Java inspection query or range")
    return sessions.submit(session_id, "java_inspect", query=query, thread_id=thread_id,
                           index=index, offset=offset, limit=limit)


@mcp.tool
@translate
def android_java_breakpoint(session_id: str, class_name: str = "", method: str = "",
                            line: int = 0, descriptor: str = "", remove_id: int = 0) -> dict:
    """Add a Java method or source-line breakpoint, or remove one by ID. Unloaded classes are resolved when prepared. Poll jobId."""
    if remove_id:
        if not 0 < remove_id < 2**31:
            raise ValueError("Invalid Java breakpoint ID")
        return sessions.submit(session_id, "java_remove_breakpoint", breakpoint_id=remove_id)
    return sessions.submit(session_id, "java_set_breakpoint",
                           **java_breakpoint_args(class_name, method, line, descriptor))


@mcp.tool
@translate
def android_java_control(session_id: str, action: str, thread_id: str = "", wait_ms: int = 0,
                         release_native_startup: bool = False) -> dict:
    """Pause, continue, or step Java. Optionally resume the initial dual-mode LLDB stop, wait for a Java/native stop, and return its stack/locals. Poll jobId."""
    if action not in CONTROL or not 0 <= wait_ms <= 60_000 or (
            thread_id and not re.fullmatch(r"[0-9]{1,20}", thread_id)):
        raise ValueError("Invalid Java execution request")
    if release_native_startup and action != "continue":
        raise ValueError("Only continue can release the initial dual-mode LLDB stop")
    return sessions.submit(session_id, "java_control", action=action, thread_id=thread_id,
                           wait_ms=wait_ms, release_native_startup=release_native_startup,
                           deadline_ms=wait_ms + 30_000)


@mcp.tool
@translate
def android_java_exception_breakpoint(session_id: str, caught: bool = False,
                                      uncaught: bool = True) -> dict:
    """Break on Java exceptions. Set both flags false to clear the exception request."""
    return sessions.submit(session_id, "java_exceptions", caught=caught, uncaught=uncaught)


@mcp.tool
@translate
def android_java_object(session_id: str, object_id: str, query: str = "fields",
                        offset: int = 0, limit: int = 32) -> dict:
    """Inspect declared instance fields, array elements, or string value using a Java object ID from the current stop."""
    if not re.fullmatch(r"[0-9]{1,20}", object_id) or query not in {"fields", "array", "string"} or (
            not 0 <= offset <= 65535 or not 1 <= limit <= 64):
        raise ValueError("Invalid Java object ID, query or range")
    return sessions.submit(session_id, "java_object", object_id=object_id, query=query,
                           offset=offset, limit=limit)


@mcp.tool
@translate
def android_detach(session_id: str) -> dict:
    """Detach LLDB and remove this session's forwarding and temporary device files. Poll jobId."""
    return sessions.submit(session_id, "detach")


def inspect_native(session_id, query):
    if query == "snapshot":
        return android_snapshot(session_id)
    if query == "registers":
        return android_native_query(session_id, query)
    return android_inspect(session_id, query)


def resolve_inspection_view(session_id, view):
    with sessions.lock:
        mode = sessions.get(session_id).connection.mode
    if view and mode not in (view, "dual"):
        raise DebugEngineError("UNSUPPORTED_INSPECTION", f"This Android session uses the {mode} view")
    return view or ("native" if mode == "dual" else mode)


ANDROID_INSPECTION = ("status", "threads", "stack", "locals", "breakpoints")
platform = DebugPlatform(
    "android", "Android session ID from android_attach or android_launch. The default view follows the session mode, with native as the default in dual mode.",
    {
        "native": InspectionView("lldb",
            {**dict.fromkeys((*ANDROID_INSPECTION, "modules"), "android_inspect"),
             "registers": "android_native_query", "snapshot": "android_snapshot"}, inspect_native),
        "java": InspectionView("jdwp", dict.fromkeys(ANDROID_INSPECTION, "android_java_inspect"), android_java_inspect),
    },
    {
        "session": ("android_list_devices", "android_attach", "android_launch", "android_list_sessions",
                    "android_job_status", "android_cancel_job", "android_detach"),
        "inspection": ("android_inspect", "android_snapshot", "android_native_query", "android_java_inspect",
                       "android_java_object"),
        "execution": ("android_control", "android_run_until_stop", "android_java_control"),
        "breakpoints": ("android_set_breakpoint", "android_configure_breakpoint", "android_remove_breakpoint",
                        "android_watchpoint", "android_java_breakpoint", "android_java_exception_breakpoint"),
        "memory": ("android_native_query", "android_write_memory"),
    },
    resolve_view=resolve_inspection_view,
)
register_debug_tools(mcp, platform, translate, default_view="")


if __name__ == "__main__":
    try:
        serve(mcp, fast_tools={"debug_capabilities", "android_list_sessions", "android_job_status", "android_cancel_job"})
    finally:
        sessions.shutdown()
