"""Run by IDAPython via -S, in GUI or TVHEADLESS idat. Not client Python."""
import ctypes
import hashlib
import hmac
import json
import os
from pathlib import Path
import sys
import threading
import time

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(Path(__file__).resolve().parent / "vendor"))
import idaapi
import ida_auto
import ida_bytes
import ida_dbg
import ida_idaapi
import ida_kernwin
import ida_loader
import ida_nalt
import idc
from ida_pro_mcp.ida_mcp import MCP_SERVER, IdaMcpHttpRequestHandler, idasync
from ida_pro_mcp.ida_mcp.mainthread import get_pump
from ida_pro_mcp.ida_mcp.rpc import tool, set_download_base_url, get_cached_output, OUTPUT_LIMIT_MAX_CHARS
from ida_backend.debug_context import collect_context

CONFIG = json.loads(Path(os.environ["AIDB_IDA_CONFIG"]).read_text(encoding="utf-8"))
DIRECTORY = Path(CONFIG["directory"])
CLOSING = False
EXIT_AT = 0.0
SAVE = True
EVENTS = []


class PrivateHandler(IdaMcpHttpRequestHandler):
    def authorized(self):
        if hmac.compare_digest(self.headers.get("Authorization", ""), "Bearer " + CONFIG["token"]):
            return True
        self.send_error(403)
        return False

    def do_POST(self):
        if self.authorized():
            super().do_POST()

    def do_GET(self):
        if self.authorized():
            super().do_GET()

    def do_OPTIONS(self):
        self.send_error(403)


@tool
@idasync
def aidb_identity() -> dict:
    return {"sessionId": CONFIG["sessionId"], "pid": os.getpid(),
            "sourceSha256": CONFIG["sourceSha256"],
            "inputPath": ida_nalt.get_input_file_path(),
            "inputSha256": bytes(ida_nalt.retrieve_input_file_sha256()).hex(),
            "idbPath": idc.get_idb_path(), "idaVersion": idaapi.get_kernel_version(),
            "imageBase": hex(ida_nalt.get_imagebase()),
            "autoAnalysisReady": bool(ida_auto.auto_is_ok()),
            "debuggerActive": bool(ida_dbg.is_debugger_on()), "mode": CONFIG["mode"]}


@tool
@idasync
def aidb_close(save: bool = True) -> dict:
    global CLOSING, SAVE, EXIT_AT
    if ida_dbg.is_debugger_on():
        raise RuntimeError("Active debugger: explicitly detach or exit before closing IDA")
    if save and not ida_loader.save_database(None, 0):
        raise RuntimeError("Database save failed; session remains open")
    SAVE = save
    CLOSING = True
    EXIT_AT = time.monotonic() + 0.5
    return {"accepted": True, "saved": save, "idbPath": idc.get_idb_path()}


@tool
@idasync
def debugger_configure(executable: str = "", arguments: str = "", working_directory: str = "",
                       debugger: str = "win32", remote: bool = False) -> dict:
    """Select a debugger and optional host EXE for a DLL without interactive dialogs."""
    if ida_dbg.is_debugger_on():
        raise RuntimeError("Detach or stop the existing debugger before configuring")
    if not ida_dbg.load_debugger(debugger, remote):
        raise RuntimeError("Debugger could not be loaded: " + debugger)
    if executable:
        path = Path(executable).resolve(strict=True)
        ida_dbg.set_process_options(str(path), arguments, working_directory or str(path.parent), "", "", 0)
    return {"debugger": debugger, "remote": remote, "executable": executable,
            "configured": True, "started": False}


@tool
@idasync
def debugger_attach(pid: int) -> dict:
    """Attach this explicitly selected IDA session to a process; never use alongside VS on the same PID."""
    if pid <= 0 or ida_dbg.is_debugger_on():
        raise RuntimeError("Positive PID and an inactive debugger required")
    if os.name == "nt":
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.OpenProcess.argtypes = [ctypes.c_uint32, ctypes.c_bool, ctypes.c_uint32]
        kernel.OpenProcess.restype = ctypes.c_void_p
        kernel.CheckRemoteDebuggerPresent.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_int)]
        kernel.CloseHandle.argtypes = [ctypes.c_void_p]
        handle = kernel.OpenProcess(0x1000, False, pid)
        if not handle:
            raise RuntimeError("Could not verify target process before attach")
        try:
            attached = ctypes.c_int()
            if not kernel.CheckRemoteDebuggerPresent(handle, ctypes.byref(attached)):
                raise RuntimeError("Could not inspect target's debugger ownership")
            if attached.value:
                raise RuntimeError("Target already has a debugger; detach VS/other debugger first")
        finally:
            kernel.CloseHandle(handle)
    result = ida_dbg.attach_process(pid, -1)
    return {"requestedPid": pid, "requestResult": result,
            "active": bool(ida_dbg.is_debugger_on()), "state": ida_dbg.get_process_state()}


@tool
@idasync
def debugger_detach() -> dict:
    """Detach without terminating the target; poll dbg_status to confirm completion."""
    if not ida_dbg.is_debugger_on():
        return {"requested": False, "active": False}
    accepted = bool(ida_dbg.detach_process())
    return {"requested": accepted, "active": bool(ida_dbg.is_debugger_on())}


def event_metadata(event):
    """Only access the debug_event_t union payload selected by eid()."""
    import ida_idd
    result = {"eid": event.eid(), "pid": event.pid, "tid": event.tid,
              "ea": hex(event.ea), "handled": event.handled}
    eid = result["eid"]
    if eid in (ida_idd.PROCESS_STARTED, ida_idd.PROCESS_ATTACHED, ida_idd.LIB_LOADED):
        module = event.modinfo()
        result["module"] = {"name": module.name, "base": hex(module.base), "size": module.size}
    elif eid in (ida_idd.PROCESS_EXITED, ida_idd.THREAD_EXITED):
        result["exitCode"] = event.exit_code()
    elif eid in (ida_idd.THREAD_STARTED, ida_idd.LIB_UNLOADED, ida_idd.INFORMATION):
        result["info"] = event.info()
    elif eid == ida_idd.EXCEPTION:
        exception = event.exc()
        result["exception"] = {"code": exception.code, "canContinue": exception.can_cont,
                               "address": hex(exception.ea), "info": exception.info}
    return result


class DebugEventHooks(ida_dbg.DBG_Hooks):
    """Record native notifications, never repeated observations of a stop."""

    def _record(self, *args):
        try:
            EVENTS.append(event_metadata(ida_dbg.get_debug_event()))
        except Exception as error:
            EVENTS.append({"observationError": str(error)})
        del EVENTS[:-200]
        return 0

    dbg_process_start = dbg_process_attach = dbg_process_exit = dbg_process_detach = _record
    dbg_thread_start = dbg_thread_exit = dbg_library_load = dbg_library_unload = _record
    dbg_information = dbg_exception = dbg_bpt = dbg_suspend_process = _record
    dbg_step_into = dbg_step_over = dbg_run_to = dbg_step_until_ret = _record


@tool
@idasync
def debugger_context(max_threads: int = 128, max_frames: int = 64) -> dict:
    """Collect paused status, thread IDs, current registers and stack in one owning-thread request. Bounded output; inspect snapshotStable and stackStatus. Does not resume or cache debugger state."""
    from ida_pro_mcp.ida_mcp import api_debug
    import ida_idd
    import ida_name
    return collect_context(ida_dbg, api_debug, ida_idd, ida_name, max_threads, max_frames)


@tool
@idasync
def debugger_events(clear: bool = False) -> dict:
    """Read bounded native debugger notifications; does not resume the target."""
    result = {"events": list(EVENTS), "active": bool(ida_dbg.is_debugger_on())}
    if clear:
        EVENTS.clear()
    return result


@tool
@idasync
def export_patched_binary(output_path: str) -> dict:
    """Export IDB file-backed patches to a new file; never overwrite the input or an existing output."""
    if ida_dbg.is_debugger_on():
        raise RuntimeError("Stop/detach before exporting file patches")
    source = Path(ida_nalt.get_input_file_path()).resolve(strict=True)
    output = Path(output_path).resolve()
    if output == source or output == Path(CONFIG["sourcePath"]).resolve() or output.exists():
        raise RuntimeError("Choose a new output path")
    data = bytearray(source.read_bytes())
    expected = bytes(ida_nalt.retrieve_input_file_sha256()).hex()
    if hashlib.sha256(data).hexdigest() != expected:
        raise RuntimeError("Original input hash differs from IDB identity")
    patches = []
    def visit(ea, fpos, original, patched):
        patches.append({"address": hex(ea), "offset": fpos, "original": original, "patched": patched})
        return 0
    ida_bytes.visit_patched_bytes(0, ida_idaapi.BADADDR, visit)
    by_offset = {}
    # Validate outside the native SWIG visitor; callback exceptions may be
    # swallowed by the native bridge and must never permit a partial export.
    for patch in patches:
        offset = patch["offset"]
        if offset < 0 or offset >= len(data) or data[offset] != patch["original"]:
            raise RuntimeError("Patch is not backed by matching original file bytes at " + patch["address"])
        if offset in by_offset and by_offset[offset] != patch["patched"]:
            raise RuntimeError("Conflicting patches map to the same file offset")
        by_offset[offset] = patch["patched"]
    for offset, byte in by_offset.items():
        data[offset] = byte
    with output.open("xb") as stream:
        stream.write(data)
    return {"outputPath": str(output), "inputSha256": expected,
            "outputSha256": hashlib.sha256(data).hexdigest(),
            "patchCount": len(patches), "patches": patches[:100],
            "patchListTruncated": len(patches) > 100}


@tool
def read_cached_output(output_id: str, offset: int = 0, count: int = 8000) -> dict:
    """Read a bounded JSON text page when an upstream result is truncated. Private download URLs cannot be accessed unauthenticated."""
    if offset < 0 or not 1 <= count <= 10000:
        raise ValueError("offset >= 0 and count in 1..10000 required")
    value = get_cached_output(output_id)
    if value is None:
        raise ValueError("Output expired or unknown; do not repeat a mutation to recreate it")
    serialized = json.dumps(value, ensure_ascii=False)
    end = min(offset + count, len(serialized))
    while True:
        page = {"text": serialized[offset:end], "totalChars": len(serialized),
                "nextOffset": end if end < len(serialized) else None}
        # The vendor limits the ASCII-escaped JSON of the whole response.
        # Keep this page below that limit so its text and nextOffset cannot
        # diverge when non-ASCII characters trigger a second truncation.
        if len(json.dumps(page)) <= OUTPUT_LIMIT_MAX_CHARS:
            return page
        end = offset + (end - offset) // 2


def parent_handle():
    if os.name != "nt":
        return None
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.OpenProcess.argtypes = [ctypes.c_uint32, ctypes.c_bool, ctypes.c_uint32]
    kernel.OpenProcess.restype = ctypes.c_void_p
    kernel.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
    kernel.WaitForSingleObject.restype = ctypes.c_uint32
    handle = kernel.OpenProcess(0x00100000, False, CONFIG["parentPid"])
    return kernel, handle


PARENT = parent_handle()


def tick():
    global CLOSING
    if not CLOSING and PARENT and (not PARENT[1] or PARENT[0].WaitForSingleObject(PARENT[1], 0) == 0):
        # Preserve a live debug target if client disappears. It can be detached
        # from this IDA explicitly; do not silently terminate another process.
        if not ida_dbg.is_debugger_on():
            if ida_loader.save_database(None, 0):
                CLOSING = True
    if CLOSING and time.monotonic() >= EXIT_AT:
        get_pump().stop()
        # Give the closing HTTP response time to flush before qexit.
        return -1
    if CONFIG["mode"] == "headless" and ida_dbg.is_debugger_on():
        # While paused this can return the same current event on every tick.
        # Hooks record actual notifications, including distinct hits at the
        # same address, without flooding the ring during an idle pause.
        ida_dbg.wait_for_next_event(ida_dbg.WFNE_ANY | ida_dbg.WFNE_SILENT, 0)
    return 100


def main():
    global CLOSING, EVENT_HOOKS
    EVENT_HOOKS = DebugEventHooks()
    if not EVENT_HOOKS.hook():
        raise RuntimeError("Could not register owned debugger event hooks")
    pump = get_pump()
    if CONFIG["mode"] == "headless":
        pump.arm()
    MCP_SERVER.serve("127.0.0.1", 0, request_handler=PrivateHandler, background=True, threaded=True)
    port = MCP_SERVER._http_server.server_address[1]
    set_download_base_url(f"http://127.0.0.1:{port}")
    registration = DIRECTORY / "ready.json"
    temporary = registration.with_suffix(".tmp")
    temporary.write_text(json.dumps({"sessionId": CONFIG["sessionId"], "pid": os.getpid(), "port": port}), encoding="utf-8")
    os.replace(temporary, registration)
    # The token exists only in the launch file until the worker reads it.
    Path(os.environ["AIDB_IDA_CONFIG"]).unlink(missing_ok=True)
    if CONFIG["mode"] == "headless":
        try:
            pump.run(poll_interval=0.1, on_idle=tick)
        finally:
            MCP_SERVER.stop()
            ida_loader.set_database_flag(ida_loader.DBFL_KILL)
            idc.qexit(0)
    else:
        def gui_tick():
            value = tick()
            if value == -1:
                MCP_SERVER.stop()
                if not SAVE:
                    ida_loader.set_database_flag(ida_loader.DBFL_KILL)
                idc.qexit(0)
            return value
        # Keep timer callback and server alive after -S returns.
        global TIMER
        TIMER = ida_kernwin.register_timer(100, gui_tick)


if __name__ == "__main__":
    main()
