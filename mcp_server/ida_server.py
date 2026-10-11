"""Independent AIDebugger IDA stdio entrypoint (no VS/IDAPython dependencies)."""
import http.server  # Cache stdlib before vendor path containing http.py.
import json
from pathlib import Path
import sys

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "ida_backend" / "vendor" / "ida_pro_mcp" / "ida_mcp"))
from zeromcp import McpServer, McpToolError
sys.path.pop(0)
from ida_backend.sessions import IdaSessions
from ida_backend.transport import IdaError
from ida_backend.stdio import serve

mcp = McpServer("AIDebugger IDA")
sessions = IdaSessions()


def translate(function):
    import functools
    @functools.wraps(function)
    def wrapped(*args, **kwargs):
        try:
            return function(*args, **kwargs)
        except IdaError as error:
            raise McpToolError(str(error)) from error
        except (OSError, ValueError) as error:
            raise McpToolError(json.dumps({"code": "IDA_ERROR", "message": str(error)})) from error
    return wrapped


@mcp.tool
@translate
def ida_open(input_path: str, mode: str = "headless") -> dict:
    """Copy and verify a binary or saved IDB, then launch owned IDA. Poll ida_session_status for analysis readiness. Other sessions remain available during input preparation. Default has no GUI."""
    return sessions.open(input_path, mode)


@mcp.tool
@translate
def ida_session_status(session_id: str) -> dict:
    """Probe explicit session identity/readiness with a short wait. Busy is not dead; do not automatically restart."""
    return sessions.status(session_id)


@mcp.tool
@translate
def ida_list_sessions() -> dict:
    """List owned IDA sessions without blocking on any busy worker."""
    return {"sessions": sessions.list()}


@mcp.tool
@translate
def ida_list_tools(session_id: str, offset: int = 0, count: int = 30) -> dict:
    """Discover migrated IDA tool names, exact argument schemas and descriptions, including debugging and patch/export."""
    return sessions.tools(session_id, offset, count)


@mcp.tool
@translate
def ida_call_tool(session_id: str, tool: str, arguments: dict, timeout_seconds: float = 30) -> dict:
    """Call a discovered IDA operation against an explicit DLL/IDB session. Timeout includes identity preflight. Busy same-session calls return IDA_BUSY_NOT_STARTED without queueing. Preserves upstream content, structuredContent and isError. Mutations are never retried. VS and IDA cannot simultaneously attach to the same PID."""
    return sessions.call(session_id, tool, arguments, timeout_seconds)


@mcp.tool
@translate
def ida_close(session_id: str, save: bool = True) -> dict:
    """Save and close owned IDA; save errors and active debugger prevent closure. save=False can cancel a never-ready startup. Poll status for exit."""
    return sessions.close(session_id, save)


if __name__ == "__main__":
    try:
        serve(mcp, fast_tools={"ida_list_sessions"}, control_tools={"ida_close"})
    finally:
        sessions.shutdown()
