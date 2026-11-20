"""Read a Windows user-mode minidump on the owning DbgEng thread."""
import ctypes as C
import re
from pathlib import Path
import struct

from debug_backend.dbgeng import DbgEng, PTR, U32, U64, check
from debug_backend.errors import DebugEngineError


DUMP_QUERIES = {
    "stack": "kv",
    "threads": "~",
    "modules": "lm",
    "registers": "r",
    "exception": ".exr -1",
}
IDEBUG_ADVANCED2 = "716d14c9-119b-4ba5-af1f-0890e672416a"
DEBUG_REQUEST_TARGET_EXCEPTION_CONTEXT = 1


def read_exception_record(stream):
    """Read the stored record, since DbgEng can synthesize a breakpoint event."""
    header = stream.read(32)
    if len(header) != 32 or header[:4] != b"MDMP":
        raise ValueError("Expected a Windows user-mode minidump header")
    count, directory = struct.unpack_from("<II", header, 8)
    if count > 4096:
        raise ValueError("Too many minidump streams")
    stream.seek(directory)
    entries = stream.read(count * 12)
    if len(entries) != count * 12:
        raise ValueError("Truncated minidump stream directory")
    for kind, size, offset in struct.iter_unpack("<III", entries):
        if kind == 6:  # ExceptionStream
            stream.seek(offset)
            record = stream.read(168)
            if size < 168 or len(record) != 168:
                raise ValueError("Truncated minidump exception stream")
            thread_id, = struct.unpack_from("<I", record)
            code, = struct.unpack_from("<I", record, 8)
            address, = struct.unpack_from("<Q", record, 24)
            return {"threadId": thread_id, "code": f"0x{code:08x}", "address": f"0x{address:x}"}
    return None


def memory_range(address, size):
    if not re.fullmatch(r"0x[0-9a-fA-F]{1,16}", address):
        raise ValueError("Use a virtual address in 0x hexadecimal notation")
    start = int(address, 16)
    if type(size) is not int or not 1 <= size <= 65536 or start + size > 2**64:
        raise ValueError("Expected a memory range of 1..65536 bytes within the address space")
    return start


def duplicate_dump_handle(stream):
    """Give DbgEng a handle to the same file already inspected by Python."""
    import msvcrt

    duplicate = C.WinDLL("kernel32", use_last_error=True).DuplicateHandle
    duplicate.argtypes = [PTR, PTR, PTR, C.POINTER(PTR), U32, C.c_int, U32]
    duplicate.restype = C.c_int
    current_process, handle = PTR(-1), PTR()
    if not duplicate(current_process, msvcrt.get_osfhandle(stream.fileno()),
                     current_process, C.byref(handle), 0, False, 2):  # DUPLICATE_SAME_ACCESS
        raise C.WinError(C.get_last_error())
    return handle.value


class UserDump(DbgEng):
    def open(self, dump_path, symbol_path):
        dump = Path(dump_path).expanduser().resolve(strict=True)
        if not dump.is_file():
            raise ValueError("Expected a Windows user-mode minidump file")
        symbols = Path(symbol_path).expanduser().resolve(strict=True) if symbol_path else (
            Path.home() / ".ai-debugger" / "symbols")
        if symbol_path and not symbols.is_dir():
            raise ValueError("Expected a local directory containing matching symbols")
        # DbgEng interprets these characters as symbol path syntax.
        if any(char in str(symbols) for char in ";*"):
            raise ValueError("Expected a single local symbol directory")
        check(self.symbols.call(77, [C.c_wchar_p], str(symbols)), "SetSymbolPathWide")
        with dump.open("rb") as stream:
            self.exception_record = read_exception_record(stream)
            stream.seek(0)
            # The native duplicate is confined to the one-request worker.
            # Process exit reclaims it even if opening the dump fails.
            handle = duplicate_dump_handle(stream)
            check(self.client.call(60, [C.c_wchar_p, U64], str(dump), handle), "OpenDumpFileWide")
        check(self.control.call(93, [U32, U32], 0, 0xffffffff), "WaitForEvent")
        kind, qualifier = U32(), U32()
        check(self.control.call(34, [C.POINTER(U32), C.POINTER(U32)],
                                C.byref(kind), C.byref(qualifier)), "GetDebuggeeType")
        if kind.value != 2 or qualifier.value < 1024:
            raise ValueError("DbgEng did not identify a Windows user-mode dump")
        return {"dumpPath": str(dump), "symbolPath": str(symbols), "dumpQualifier": qualifier.value}

    def has_exception_context(self):
        advanced = self.client.query(IDEBUG_ADVANCED2)
        try:
            data, used = C.create_string_buffer(16384), U32()
            hr = advanced.call(5, [U32, PTR, U32, PTR, U32, C.POINTER(U32)],
                               DEBUG_REQUEST_TARGET_EXCEPTION_CONTEXT, None, 0, data, len(data), C.byref(used))
            return hr == 0 and 0 < used.value <= len(data)
        finally:
            advanced.release()

    def inspect(self, query, frame, exception_context):
        if query == "locals":
            if type(frame) is not int or not 0 <= frame <= 256:
                raise ValueError("Expected a frame index of 0..256")
            commands = ["dv /t"]
        elif query == "triage":
            commands = ["kv", "lm", "r"]
        elif query in DUMP_QUERIES:
            commands = [DUMP_QUERIES[query]]
        else:
            raise ValueError("Unknown Windows dump query")
        sections = []
        metadata = {}
        if query in {"exception", "triage"}:
            available = self.exception_record is not None
            if query == "exception" and not available:
                raise DebugEngineError("EXCEPTION_RECORD_UNAVAILABLE", "The minidump has no saved exception record")
            metadata["exceptionRecord"] = "available" if available else "unavailable"
            if available:
                metadata["storedException"] = self.exception_record
            if query == "triage":
                if available:
                    sections.append(self.execute_query(".exr -1"))
                metadata["context"] = "default"
        if exception_context and query in {"stack", "registers", "locals", "triage"}:
            applied = False
            if self.has_exception_context():
                section = self.execute_query(".ecxr")
                sections.append(section)
                applied = section["hresult"] == "0x00000000" and not section["debuggerError"]
            if not applied and query != "triage":
                raise DebugEngineError("EXCEPTION_CONTEXT_UNAVAILABLE", "Could not select a saved exception context")
            metadata["context"] = "exception" if applied else "default"
        if query == "locals":
            # IDebugSymbols3::SetScopeFrameByIndex takes a numeric index and
            # fails for an unavailable frame, unlike the .frame text command.
            hr = self.symbols.call(100, [U32], frame)
            if hr != 0:
                raise DebugEngineError("FRAME_UNAVAILABLE", f"Frame {frame} is unavailable (HRESULT 0x{hr & 0xffffffff:08x})")
            metadata["frame"] = frame
        return self.execute_queries(commands, sections, **metadata)

    def execute_query(self, command):
        self.output.reset()
        hr = self.control.call(66, [U32, C.c_char_p, U32], 0, command.encode("ascii"), 0)
        return {"command": command, "hresult": f"0x{hr & 0xffffffff:08x}",
                "debuggerError": self.output.has_error or hr < 0, **self.output.result()}

    def execute_queries(self, commands, prefix=(), **metadata):
        return {"behavior": "dbgeng_text_output", "semanticSuccess": "unknown",
                "sections": [*prefix, *(self.execute_query(command) for command in commands)], **metadata}


def inspect_dump(dump_path, symbol_path, *, query="", frame=0, exception_context=False,
                 address="", size=256, instruction_count=0):
    engine = UserDump()
    try:
        target = engine.open(dump_path, symbol_path)
        if instruction_count:
            start = memory_range(address, 1)
            if type(instruction_count) is not int or not 1 <= instruction_count <= 128:
                raise ValueError("Expected an instruction count of 1..128")
            result = engine.execute_queries([f"u 0x{start:x} L0x{instruction_count:x}"])
        elif address:
            result = {"address": address, "behavior": "dbgeng_virtual_memory",
                      **engine.read_virtual(memory_range(address, size), size)}
        else:
            result = engine.inspect(query, frame, exception_context)
        return {"target": target, **result}
    finally:
        engine.close()
