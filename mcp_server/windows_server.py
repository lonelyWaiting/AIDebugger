"""Independent Windows user-mode debugging MCP entrypoint."""
import functools
import http.server  # Resolve stdlib before the vendor path containing http.py.
import json
from pathlib import Path
import subprocess
import sys

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from mcp_server.typing_compat import ensure_pep655_typing_markers

ensure_pep655_typing_markers()
sys.path.insert(0, str(ROOT / "ida_backend/vendor/ida_pro_mcp/ida_mcp"))
from zeromcp import McpServer, McpToolError
sys.path.pop(0)
from debug_backend.errors import DebugEngineError
from debug_backend.inspection import DebugPlatform, InspectionView
from mcp_server.debug_discovery import register_debug_tools
from mcp_server.stdio_dispatch import serve
from windows_backend.dump import DUMP_QUERIES, memory_range

mcp = McpServer("AIDebugger Windows")


def translate(function):
    @functools.wraps(function)
    def wrapped(*args, **kwargs):
        try:
            return function(*args, **kwargs)
        except (DebugEngineError, OSError, ValueError) as error:
            raise McpToolError(json.dumps({"code": getattr(error, "code", "WINDOWS_ERROR"),
                                           "message": str(error)})) from error
    return wrapped


def request_dump(dump_path, symbol_path, options):
    if not dump_path or len(dump_path) > 32767 or len(symbol_path) > 32767:
        raise ValueError("Specify a Windows user dump path and an optional local symbol directory")
    try:
        child = subprocess.run(
            [sys.executable, "-B", "-m", "windows_backend.worker"], cwd=ROOT,
            input=json.dumps(dict(dump_path=dump_path, symbol_path=symbol_path, **options)),
            encoding="utf-8", capture_output=True, timeout=30,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
    except subprocess.TimeoutExpired as error:
        raise DebugEngineError("ENGINE_TIMEOUT", "The isolated dump inspection exceeded 30 seconds") from error
    if child.returncode != 0:
        raise DebugEngineError("ENGINE_EXITED", f"The isolated dump worker exited with code {child.returncode}")
    receipt = json.loads(child.stdout)
    if "error" in receipt:
        raise DebugEngineError(**receipt["error"])
    return receipt["result"]


@mcp.tool
@translate
def windows_dump_query(dump_path: str, query: str, symbol_path: str = "", frame: int = 0,
                       exception_context: bool = False) -> dict:
    """Inspect a Windows user-mode minidump. Queries: triage, stack, threads, modules, registers, exception, locals. Give a local directory of matching PDBs for source/symbol details. Request exception_context for triage, stack, registers or locals."""
    if query not in {*DUMP_QUERIES, "triage", "locals"}:
        raise ValueError("Unknown Windows dump query")
    if not 0 <= frame <= 256 or (frame and query != "locals"):
        raise ValueError("Frame index must be 0..256 and applies only to locals")
    if exception_context and query not in {"triage", "stack", "registers", "locals"}:
        raise ValueError("Exception context applies only to triage, stack, registers or locals")
    return request_dump(dump_path, symbol_path, dict(query=query, frame=frame, exception_context=exception_context))


@mcp.tool
@translate
def windows_dump_read_memory(dump_path: str, address: str, size: int = 256,
                             symbol_path: str = "") -> dict:
    """Read up to 65536 bytes in DbgEng's minidump address space. Check bytesRead and status for partial reads. DbgEng can supply image-backed bytes from a local module."""
    memory_range(address, size)
    return request_dump(dump_path, symbol_path, dict(address=address, size=size))


@mcp.tool
@translate
def windows_dump_disassemble(dump_path: str, address: str, count: int = 16, symbol_path: str = "") -> dict:
    """Disassemble 1..128 instructions starting at an explicit minidump address. Check DbgEng output for missing code pages."""
    memory_range(address, 1)
    if not 1 <= count <= 128:
        raise ValueError("Expected an instruction count of 1..128")
    return request_dump(dump_path, symbol_path, dict(address=address, instruction_count=count))


def inspect_default_dump(dump_path, query):
    return windows_dump_query(dump_path, query, exception_context=query == "triage")


platform = DebugPlatform(
    "windows", "Local Windows user-mode minidump file path",
    {"dump": InspectionView("dbgeng", dict.fromkeys((*DUMP_QUERIES, "triage", "locals"), "windows_dump_query"),
                            inspect_default_dump)},
    {"inspection": ("windows_dump_query", "windows_dump_disassemble"),
     "memory": ("windows_dump_read_memory",)})
register_debug_tools(mcp, platform, translate, default_view="dump")


if __name__ == "__main__":
    serve(mcp, fast_tools={"debug_capabilities"})
