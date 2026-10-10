"""Small ctypes binding to the public DbgEng COM ABI (Windows x64 only).

Slots/GUIDs are from Microsoft's WinSDK DbgEng.h. Ordinary calls remain on
the session owner thread; only SetInterrupt is called from the reader thread.
No DLL search through cwd or PATH, no native binaries redistributed.
"""
import ctypes as C
import os
from pathlib import Path
import struct
import uuid
import hashlib
import json

from .contracts import KernelError, address, query_command

U32 = C.c_uint32
U64 = C.c_uint64
PTR = C.c_void_p
HR = C.c_int32
GUID = C.c_ubyte * 16
IIDS = {"client": "ca83c3de-5089-4cf8-93c8-d892387f2a5e",
        "control": "5182e668-105e-416e-ad92-24ef800424ba",
        "memory": "88f7dfab-3ea7-4c3a-aefb-c4e8106173aa",
        "symbols": "f02fbecc-50ac-4f36-9ad9-c975e8f32ff8"}  # IDebugSymbols3


def check(hr, method):
    if hr < 0:
        raise KernelError("DBGENG_ERROR", f"{method} failed (HRESULT 0x{hr & 0xffffffff:08x})")
    return hr


def find_engine():
    if os.name != "nt" or struct.calcsize("P") != 8:
        raise KernelError("PLATFORM_UNSUPPORTED", "Windows x64 Python is required")
    explicit = os.environ.get("AIDB_DBGENG_DIR")
    if explicit:
        path = Path(explicit).expanduser().resolve(strict=True) / "dbgeng.dll"
        if not path.is_file():
            raise KernelError("ENGINE_NOT_FOUND", "AIDB_DBGENG_DIR must contain x64 dbgeng.dll")
        return path
    candidates = [Path.home() / ".ai-debugger/debugging-tools/x64/dbgeng.dll",
                  Path(os.environ.get("ProgramFiles(x86)", "C:/Program Files (x86)")) /
                  "Windows Kits/10/Debuggers/x64/dbgeng.dll"]
    candidates.append(Path(os.environ.get("SystemRoot", "C:/Windows")) / "System32/dbgeng.dll")
    for path in candidates:
        if path.is_file():
            return path.resolve()
    raise KernelError("ENGINE_NOT_FOUND", "Install Windows Debugging Tools, run tools/prepare_dbgeng.py for Store WinDbg, or set AIDB_DBGENG_DIR")


def find_store_engine():
    # Store package directories cannot reliably be enumerated via glob. Read
    # the current user's registered package root, then verify the actual DLL.
    import winreg
    try:
        key_path = r"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages"
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, key_path) as packages:
            for index in range(winreg.QueryInfoKey(packages)[0]):
                name = winreg.EnumKey(packages, index)
                if name.startswith("Microsoft.WinDbg_") and "_x64__" in name:
                    with winreg.OpenKey(packages, name) as package:
                        root = winreg.QueryValueEx(package, "PackageRootFolder")[0]
                        path = Path(root) / "amd64/dbgeng.dll"
                        if path.is_file():
                            return path
    except OSError:
        pass
    raise KernelError("ENGINE_NOT_FOUND", "No registered x64 Microsoft WinDbg Store package found")


def guid(value):
    return GUID.from_buffer_copy(uuid.UUID(value).bytes_le)


class Interface:
    def __init__(self, pointer):
        self.pointer = pointer

    def call(self, slot, types, *args):
        table = C.cast(self.pointer, C.POINTER(C.POINTER(PTR))).contents
        function = C.WINFUNCTYPE(HR, PTR, *types)(table[slot])
        return function(self.pointer, *args)

    def query(self, iid):
        result = PTR()
        check(self.call(0, [C.POINTER(GUID), C.POINTER(PTR)], C.byref(guid(iid)), C.byref(result)), "QueryInterface")
        return Interface(result)

    def release(self):
        if self.pointer:
            self.call(2, [])
            self.pointer = None


class Output:
    """Keep callback objects/vtable alive until SetOutputCallbacks(NULL)."""
    LIMIT = 131072

    def __init__(self, redact):
        self.redact = redact
        self.reset()
        query = C.WINFUNCTYPE(HR, PTR, C.POINTER(GUID), C.POINTER(PTR))
        ref = C.WINFUNCTYPE(U32, PTR)
        output = C.WINFUNCTYPE(HR, PTR, U32, C.c_char_p)

        def qi(this, iid, result):
            if bytes(iid.contents) in (uuid.UUID("00000000-0000-0000-c000-000000000046").bytes_le,
                                      uuid.UUID("4bf58045-d654-4c40-b0af-683090f356dc").bytes_le):
                result[0] = this
                return 0
            result[0] = None
            return C.c_int32(0x80004002).value

        def write(this, mask, data):
            try:
                value = self.redact((data or b"").decode("mbcs", errors="replace"))
                remaining = self.LIMIT - self.length
                if remaining:
                    self.parts.append(value[:remaining])
                self.length += min(len(value), remaining)
                self.truncated |= len(value) > remaining
            except Exception:
                self.truncated = True
            return 0

        self.callbacks = [query(qi), ref(lambda _: 1), ref(lambda _: 1), output(write)]
        self.table = (PTR * 4)(*[C.cast(f, PTR).value for f in self.callbacks])
        self.instance = (PTR * 1)(C.cast(self.table, PTR))

    def reset(self):
        self.parts, self.length, self.truncated = [], 0, False

    def result(self):
        return {"output": "".join(self.parts), "truncated": self.truncated}


class Engine:
    def __init__(self, redact=lambda x: x):
        self.redact = redact
        self.path = find_engine()
        manifest = self.path.parent / "aidb-runtime.json"
        if manifest.is_file():
            receipt = json.loads(manifest.read_text(encoding="utf-8"))
            declared = receipt.get("files", {})
            actual = {path.relative_to(self.path.parent).as_posix() for path in self.path.parent.rglob("*.dll")}
            if actual != set(declared):
                raise KernelError("RUNTIME_INTEGRITY", "Prepared debugging runtime DLL tree changed")
            for relative, entry in declared.items():
                path = self.path.parent / relative
                if hashlib.sha256(path.read_bytes()).hexdigest() != entry["sha256"]:
                    raise KernelError("RUNTIME_INTEGRITY", "Prepared debugging runtime hash mismatch: " + relative)
        self.directory = os.add_dll_directory(str(self.path.parent))
        self.dll = C.WinDLL(str(self.path), winmode=0x1100)
        create = self.dll.DebugCreate
        create.argtypes = [C.POINTER(GUID), C.POINTER(PTR)]
        create.restype = HR
        pointer = PTR()
        check(create(C.byref(guid(IIDS["client"])), C.byref(pointer)), "DebugCreate")
        self.client = Interface(pointer)
        self.control = self.client.query(IIDS["control"])
        self.memory = self.client.query(IIDS["memory"])
        self.symbols = self.client.query(IIDS["symbols"])
        self.output = Output(redact)
        check(self.client.call(34, [PTR], C.cast(self.output.instance, PTR)), "SetOutputCallbacks")
        check(self.client.call(36, [U32], 0x87), "SetOutputMask")
        self.mode = None
        self.qualifier = None
        self.extensions = []

    def open(self, config):
        self.mode = config["mode"]
        # Do not inherit DbgEng's implicit "srv*" network default. Caller
        # explicitly opts into a symbol server, otherwise use a local cache.
        self.symbol_path = config["symbol_path"] or str(Path.home() / ".ai-debugger/symbols")
        check(self.symbols.call(77, [C.c_wchar_p], self.symbol_path), "SetSymbolPathWide")
        if self.mode == "dump":
            check(self.client.call(60, [C.c_wchar_p, U64], config["dump_path"], 0), "OpenDumpFileWide")
        else:
            options = config["connection"].encode("ascii") if self.mode == "remote" else None
            check(self.client.call(3, [U32, C.c_char_p], 1 if self.mode == "local" else 0, options), "AttachKernel")
        self.wait()
        self.verify_target()
        for name in ("ext.dll", "kdexts.dll"):
            extension = self.path.parent / "winext" / name
            if extension.is_file():
                handle = U64()
                check(self.control.call(74, [C.c_char_p, U32, C.POINTER(U64)],
                                        str(extension).encode("mbcs"), 0, C.byref(handle)), "AddExtension")
                self.extensions.append(name)
        return self.status()

    def wait(self):
        # A live kernel requires INFINITE. The out-of-band reader can issue
        # DEBUG_INTERRUPT_EXIT; MCP itself never waits for this call.
        check(self.control.call(93, [U32, U32], 0, 0xffffffff), "WaitForEvent")

    def verify_target(self):
        kind, qualifier = U32(), U32()
        check(self.control.call(34, [C.POINTER(U32), C.POINTER(U32)], C.byref(kind), C.byref(qualifier)), "GetDebuggeeType")
        expected = (qualifier.value >= 1024 if self.mode == "dump" else
                    qualifier.value == (1 if self.mode == "local" else 0))
        if kind.value != 1 or not expected or (self.qualifier is not None and self.qualifier != qualifier.value):
            raise KernelError("TARGET_MISMATCH", "Engine target is not the requested kernel session")
        self.qualifier = qualifier.value

    def status(self):
        value = U32()
        check(self.control.call(49, [C.POINTER(U32)], C.byref(value)), "GetExecutionStatus")
        raw = value.value & 0x1f
        state = {6: "paused", 7: "disconnected", 15: "out_of_sync", 16: "waiting_input"}.get(raw, "running")
        if self.mode == "dump":
            state = "paused"
        if self.mode == "local":
            state = "local_inspection"
        event_type, pid, tid, extra_used, description_used = U32(), U32(), U32(), U32(), U32()
        description = C.create_string_buffer(1024)
        event_hr = self.control.call(94, [C.POINTER(U32), C.POINTER(U32), C.POINTER(U32), PTR, U32,
                                          C.POINTER(U32), PTR, U32, C.POINTER(U32)],
                                     C.byref(event_type), C.byref(pid), C.byref(tid), None, 0,
                                     C.byref(extra_used), description, len(description), C.byref(description_used))
        stop = {1: "breakpoint", 2: "exception"}.get(event_type.value, "pause_unknown") if event_hr >= 0 else "pause_unknown"
        return {"state": state, "executionStatusRaw": raw, "kernelQualifier": self.qualifier,
                "snapshotStable": self.mode == "dump" or state == "paused",
                "snapshotStatus": "stable" if self.mode == "dump" or state == "paused" else "unstable",
                "stopReason": stop if state == "paused" else "not_paused",
                "stopReasonRaw": event_type.value if event_hr >= 0 else None,
                "eventDescription": self.redact(description.value.decode("mbcs", errors="replace")) if event_hr >= 0 else "",
                "enginePath": str(self.path), "registeredExtensions": list(self.extensions), "symbolPath": self.symbol_path}

    def interrupt(self, exit_wait=False):
        check(self.control.call(4, [U32], 2 if exit_wait else 0), "SetInterrupt")

    def perform(self, operation, args):
        self.output.reset()
        if operation in {"query", "command"}:
            if operation == "query" and args["query"] in {"analyze", "processes", "threads"} and not self.extensions:
                raise KernelError("EXTENSIONS_UNAVAILABLE", "Install WinDbg/Debugging Tools (with winext) or set AIDB_DBGENG_DIR; system DbgEng alone has no kernel extensions")
            command = query_command(args["query"], args["arguments"], self.mode) if operation == "query" else args["command"]
            check(self.control.call(66, [U32, C.c_char_p, U32], 0, command.encode("mbcs"), 0), "Execute")
            result = self.output.result()
            result["behavior"] = "dbgeng_text_output"
            result["semanticSuccess"] = "unknown"  # Commands can print errors with S_OK.
        elif operation == "read_memory":
            size = args["size"]
            buffer, read = C.create_string_buffer(size), U32()
            hr = self.memory.call(3, [U64, PTR, U32, C.POINTER(U32)], address(args["address"]), buffer, size, C.byref(read))
            result = {"address": args["address"], "bytesRequested": size, "bytesRead": read.value,
                      "dataHex": buffer.raw[:read.value].hex(), "partial": read.value != size,
                      "status": "complete" if read.value == size else ("partial" if read.value else "unavailable"),
                      "hresult": f"0x{hr & 0xffffffff:08x}"}
        elif operation == "write_memory":
            data = bytes.fromhex(args["data_hex"])
            written = U32()
            buffer = C.create_string_buffer(data)
            hr = self.memory.call(4, [U64, PTR, U32, C.POINTER(U32)], address(args["address"]), buffer, len(data), C.byref(written))
            result = {"address": args["address"], "bytesRequested": len(data), "bytesWritten": written.value,
                      "partial": written.value != len(data), "status": "complete" if written.value == len(data) else ("partial" if written.value else "unavailable"),
                      "hresult": f"0x{hr & 0xffffffff:08x}", "behavior": "engine_memory_write"}
        elif operation == "set_breakpoint":
            pointer = PTR()
            check(self.control.call(72, [U32, U32, C.POINTER(PTR)], 0, 0xffffffff, C.byref(pointer)), "AddBreakpoint")
            bp = Interface(pointer)
            try:
                check(bp.call(11, [U64], address(args["address"])), "SetOffset")
                check(bp.call(7, [U32], 4), "AddFlags(ENABLED)")
                identifier = U32()
                check(bp.call(3, [C.POINTER(U32)], C.byref(identifier)), "GetBreakpointId")
                result = {"breakpointId": identifier.value, "address": args["address"], "enabled": True}
            except Exception:
                self.control.call(73, [PTR], pointer)
                raise
            # IDebugBreakpoint lifetime is controlled by RemoveBreakpoint,
            # not IUnknown reference counting. Never Release a removed pointer.
        elif operation == "remove_breakpoint":
            pointer = PTR()
            check(self.control.call(70, [U32, C.POINTER(PTR)], args["breakpoint_id"], C.byref(pointer)), "GetBreakpointById")
            check(self.control.call(73, [PTR], pointer), "RemoveBreakpoint")
            result = {"breakpointId": args["breakpoint_id"], "removed": True}
        elif operation == "control":
            status = {"continue": 1, "step_into": 5, "step_over": 4}[args["action"]]
            check(self.control.call(50, [U32], status), "SetExecutionStatus")
            result = {"requested": True, "action": args["action"]}
        else:
            raise KernelError("INVALID_ARGUMENT", "Unknown engine operation")
        self.verify_target()
        result.update(self.status())
        return result

    def close(self):
        # Never terminate targets or implicitly resume a paused kernel.
        check(self.client.call(26, [U32], 0), "EndSession(PASSIVE)")
        self.client.call(34, [PTR], None)
        for interface in (self.symbols, self.memory, self.control, self.client):
            interface.release()
        self.directory.close()
