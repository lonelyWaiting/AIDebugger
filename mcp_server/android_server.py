"""Android native debugging MCP entrypoint."""
import functools
import http.server  # Cache stdlib before the vendored path containing http.py.
from pathlib import Path
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
from mcp_server.stdio_dispatch import serve

mcp = McpServer("AIDebugger Android")
runtime = Runtime(destination(ROOT))
sessions = Sessions(runtime)


def translate(function):
    @functools.wraps(function)
    def wrapped(*args, **kwargs):
        try:
            return function(*args, **kwargs)
        except (AndroidError, OSError, ValueError) as error:
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
                   source_map: dict[str, str] | None = None) -> dict:
    """Attach LLDB to a running debuggable app. Process defaults to package; use package:suffix for a child. Pass unstripped library directories and optional build-prefix to local-directory source mappings. Poll jobId."""
    return sessions.open(serial, package, process, pid, abi, symbol_dirs or [], source_map or {})


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
                           line: int = 0, module: str = "") -> dict:
    """Add a native breakpoint at a symbol or source file and line in a paused target. A breakpoint with zero locations is pending. Poll jobId."""
    if (not symbol and not (file and 1 <= line <= 2**32 - 1)
            or symbol and (file or line) or any(len(value) > 4096 for value in (symbol, file, module))):
        raise ValueError("Specify either a symbol or a source file and positive line")
    return sessions.submit(session_id, "set_breakpoint", symbol=symbol, file=file, line=line, module=module)


@mcp.tool
@translate
def android_remove_breakpoint(session_id: str, breakpoint_id: int) -> dict:
    """Delete one breakpoint by its session-local ID. Poll jobId."""
    if not 0 < breakpoint_id < 2**31:
        raise ValueError("Expected a positive LLDB breakpoint ID")
    return sessions.submit(session_id, "remove_breakpoint", breakpoint_id=breakpoint_id)


@mcp.tool
@translate
def android_control(session_id: str, action: str, thread_id: int = 0) -> dict:
    """Pause, continue, step_into, step_over or step_out. Poll jobId, then inspect status and stopId to confirm the next stop."""
    if action not in {"pause", "continue", "step_into", "step_over", "step_out"} or not 0 <= thread_id < 2**64:
        raise ValueError("Invalid Android execution action")
    return sessions.submit(session_id, "control", action=action, thread_id=thread_id)


@mcp.tool
@translate
def android_detach(session_id: str) -> dict:
    """Detach LLDB and remove this session's forwarding and temporary device files. Poll jobId."""
    return sessions.submit(session_id, "detach")


if __name__ == "__main__":
    try:
        serve(mcp, fast_tools={"android_list_sessions", "android_job_status"})
    finally:
        sessions.shutdown()
