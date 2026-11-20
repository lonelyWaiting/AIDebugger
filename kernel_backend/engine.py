"""Kernel-target policy and operations on the shared DbgEng binding."""
import ctypes as C
from pathlib import Path

from debug_backend.dbgeng import DbgEng, Interface, PTR, U32, U64, check, find_store_engine

from .contracts import KernelError, address, query_command


class Engine(DbgEng):
    def __init__(self, redact=lambda x: x):
        super().__init__(redact)
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
            result = {"address": args["address"], **self.read_virtual(address(args["address"]), args["size"])}
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
        try:
            self.verify_target()
            result.update(self.status())
        except Exception as error:
            if operation in {"command", "control", "write_memory", "set_breakpoint", "remove_breakpoint"}:
                raise KernelError("OUTCOME_UNKNOWN",
                    f"{operation} was issued, but follow-up target/status failed; do not retry mutations: {error}") from error
            raise
        return result
