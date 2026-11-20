"""Small ctypes binding to the public DbgEng COM ABI (Windows x64 only).

Slots/GUIDs are from Microsoft's WinSDK DbgEng.h. Ordinary calls remain on
the session owner thread.
No DLL search through cwd or PATH, no native binaries redistributed.
"""
import ctypes as C
import hashlib
import json
import os
from pathlib import Path
import struct
import uuid

from .errors import DebugEngineError

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
        raise DebugEngineError("DBGENG_ERROR", f"{method} failed (HRESULT 0x{hr & 0xffffffff:08x})")
    return hr


def find_engine():
    if os.name != "nt" or struct.calcsize("P") != 8:
        raise DebugEngineError("PLATFORM_UNSUPPORTED", "Windows x64 Python is required")
    explicit = os.environ.get("AIDB_DBGENG_DIR")
    if explicit:
        path = Path(explicit).expanduser().resolve(strict=True) / "dbgeng.dll"
        if not path.is_file():
            raise DebugEngineError("ENGINE_NOT_FOUND", "AIDB_DBGENG_DIR must contain x64 dbgeng.dll")
        return path
    candidates = [Path.home() / ".ai-debugger/debugging-tools/x64/dbgeng.dll",
                  Path(os.environ.get("ProgramFiles(x86)", "C:/Program Files (x86)")) /
                  "Windows Kits/10/Debuggers/x64/dbgeng.dll"]
    candidates.append(Path(os.environ.get("SystemRoot", "C:/Windows")) / "System32/dbgeng.dll")
    for path in candidates:
        if path.is_file():
            return path.resolve()
    raise DebugEngineError("ENGINE_NOT_FOUND", "Install Windows Debugging Tools, run tools/prepare_dbgeng.py for Store WinDbg, or set AIDB_DBGENG_DIR")


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
    raise DebugEngineError("ENGINE_NOT_FOUND", "No registered x64 Microsoft WinDbg Store package found")


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
            self.has_error |= bool(mask & 0x2)  # DEBUG_OUTPUT_ERROR
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
        self.has_error = False

    def result(self):
        return {"output": "".join(self.parts), "truncated": self.truncated}


class DbgEng:
    def __init__(self, redact=lambda x: x):
        self.redact = redact
        self.path = find_engine()
        manifest = self.path.parent / "aidb-runtime.json"
        if manifest.is_file():
            receipt = json.loads(manifest.read_text(encoding="utf-8"))
            declared = receipt.get("files", {})
            actual = {path.relative_to(self.path.parent).as_posix() for path in self.path.parent.rglob("*.dll")}
            if actual != set(declared):
                raise DebugEngineError("RUNTIME_INTEGRITY", "Prepared debugging runtime DLL tree changed")
            for relative, entry in declared.items():
                path = self.path.parent / relative
                if hashlib.sha256(path.read_bytes()).hexdigest() != entry["sha256"]:
                    raise DebugEngineError("RUNTIME_INTEGRITY", "Prepared debugging runtime hash mismatch: " + relative)
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

    def read_virtual(self, start, size):
        buffer, read = C.create_string_buffer(size), U32()
        hr = self.memory.call(3, [U64, PTR, U32, C.POINTER(U32)], start, buffer, size, C.byref(read))
        return {"bytesRequested": size, "bytesRead": read.value,
                "dataHex": buffer.raw[:read.value].hex(), "partial": read.value != size,
                "status": "complete" if read.value == size else ("partial" if read.value else "unavailable"),
                "hresult": f"0x{hr & 0xffffffff:08x}"}

    def close(self):
        check(self.client.call(26, [U32], 0), "EndSession(PASSIVE)")
        self.client.call(34, [PTR], None)
        for interface in (self.symbols, self.memory, self.control, self.client):
            interface.release()
        self.directory.close()
