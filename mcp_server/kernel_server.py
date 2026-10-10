"""Independent kernel MCP entrypoint; shares only vendored generic MCP transport."""
import http.server  # Cache stdlib before vendor path containing http.py.
import functools
import json
from pathlib import Path
import sys

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / "ida_backend/vendor/ida_pro_mcp/ida_mcp"))
from zeromcp import McpServer, McpToolError
sys.path.pop(0)
from mcp_server.stdio_dispatch import serve
from kernel_backend.contracts import KernelError
from kernel_backend.sessions import KernelSessions

mcp = McpServer("AIDebugger Kernel")
sessions = KernelSessions()


def translate(function):
    @functools.wraps(function)
    def wrapped(*args, **kwargs):
        try:
            return function(*args, **kwargs)
        except (KernelError, OSError, ValueError) as error:
            raise McpToolError(json.dumps({"code": getattr(error, "code", "KERNEL_ERROR"),
                                          "message": str(error)})) from error
    return wrapped


@mcp.tool
@translate
def kernel_open_dump(dump_path: str, symbol_path: str = "", full_access: bool = False) -> dict:
    """Open a kernel dump in an isolated DbgEng worker. Poll returned jobId; user-mode dumps are rejected. Defaults to restricted queries."""
    return sessions.open("dump", dump_path=dump_path, symbol_path=symbol_path, full_access=full_access)


@mcp.tool
@translate
def kernel_connect(connection: str = "", local: bool = False, symbol_path: str = "", full_access: bool = False) -> dict:
    """Connect to an already configured kernel. Remote connection is WinDbg -k options (e.g. net:port=...,key=...). Local inspection cannot break or step. No boot changes/elevation; poll jobId."""
    return sessions.open("local" if local else "remote", connection=connection,
                         symbol_path=symbol_path, full_access=full_access)


@mcp.tool
@translate
def kernel_list_sessions() -> dict:
    """List explicit kernel sessions, cached status and mode-specific capabilities. No automatic target selection."""
    return {"sessions": sessions.list()}


@mcp.tool
@translate
def kernel_session_status(session_id: str) -> dict:
    """Return cached worker/target state immediately, including active job and bounded event output. Busy is not dead."""
    return sessions.status(session_id)


@mcp.tool
@translate
def kernel_job_status(session_id: str, job_id: str, offset: int = 0, count: int = 8192) -> dict:
    """Poll an asynchronous operation; page output via nextOffset. Jobs retain at most 128 Ki characters; truncated means evidence incomplete. Mutations are never retried."""
    return sessions.job(session_id, job_id, offset, count)


@mcp.tool
@translate
def kernel_query(session_id: str, query: str, arguments: dict | None = None) -> dict:
    """Submit restricted query: version/modules/processes/threads/registers/stack/bugcheck/analyze/breakpoints/disassemble. Disassemble accepts hex address/count; other queries take no arguments. Poll jobId. Text semantic success must be checked."""
    return sessions.submit(session_id, "query", {"query": query, "arguments": arguments or {}})


@mcp.tool
@translate
def kernel_read_memory(session_id: str, address: str, size: int = 256) -> dict:
    """Read 1..65536 bytes of virtual memory, with bytesRead/partial/status/dataHex. Missing dump pages never imply absence. Poll jobId."""
    return sessions.submit(session_id, "read_memory", {"address": address, "size": size})


@mcp.tool
@translate
def kernel_write_memory(session_id: str, address: str, data_hex: str) -> dict:
    """Write 1..65536 bytes in full_access; returns bytesWritten/partial. Dump writes affect the engine view, not source file persistence. Never retry a partial write. Poll jobId."""
    return sessions.submit(session_id, "write_memory", {"address": address, "data_hex": data_hex})


@mcp.tool
@translate
def kernel_set_breakpoint(session_id: str, address: str) -> dict:
    """Add an enabled code breakpoint at a hex address in remote full_access. Returns native breakpointId. Symbolic/pending/data breakpoints use kernel_execute. Poll jobId."""
    return sessions.submit(session_id, "set_breakpoint", {"address": address})


@mcp.tool
@translate
def kernel_remove_breakpoint(session_id: str, breakpoint_id: int) -> dict:
    """Remove one native breakpoint by ID in remote full_access. Never chooses another session or retries. Poll jobId."""
    return sessions.submit(session_id, "remove_breakpoint", {"breakpoint_id": breakpoint_id})


@mcp.tool
@translate
def kernel_execute(session_id: str, command: str) -> dict:
    """Run a WinDbg command in an explicitly full_access session: breakpoint/context/symbol/type/extension/memory operations. Commands can mutate kernel memory, load extensions and access host files. Use typed lifecycle tools; poll jobId and inspect output."""
    return sessions.submit(session_id, "command", {"command": command})


@mcp.tool
@translate
def kernel_control(session_id: str, action: str) -> dict:
    """Request continue/step_into/step_over in a remote full_access paused session. Job completion acknowledges the request; poll session status for the next stop."""
    return sessions.submit(session_id, "control", {"action": action})


@mcp.tool
@translate
def kernel_interrupt(session_id: str, cancel_wait: bool = False) -> dict:
    """Out-of-band interrupt while worker waits or runs a command. Default requests remote full_access break-in. cancel_wait=True exits a wait without guaranteeing target pause; usable to cancel connection/query. Poll jobId and session status."""
    return sessions.interrupt(session_id, exit_wait=cancel_wait)


@mcp.tool
@translate
def kernel_close(session_id: str) -> dict:
    """Passively end only this owned engine session; no target termination or implicit resume. A paused remote kernel may remain paused: explicitly continue first if desired. Poll jobId and workerAlive."""
    return sessions.submit(session_id, "close", {})


if __name__ == "__main__":
    try:
        serve(mcp)
    finally:
        sessions.shutdown()
