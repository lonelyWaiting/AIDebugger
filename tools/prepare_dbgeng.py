"""Prepare a local DbgEng runtime from installed WinDbg; never redistribute DLLs.

Store WinDbg DLLs can reject loading outside their package. Copy their unchanged
bytes to a user-owned cache, recording source/destination SHA-256. Re-running
preserves local edits and rolls back failures. No elevation or OS debug changes.
"""
import argparse
import json
from pathlib import Path
import struct
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from kernel_backend.engine import find_store_engine
from tools.install_aidebugger import atomic_write, checked_path, digest


def prepare(source=None, destination=None):
    source = Path(source).resolve(strict=True) if source else find_store_engine().parent
    destination = checked_path(Path(destination or Path.home() / ".ai-debugger/debugging-tools/x64").absolute())
    if source == destination:
        raise ValueError("Source and destination must differ")
    image = (source / "dbgeng.dll").read_bytes()
    pe = struct.unpack_from("<I", image, 0x3c)[0]
    if image[:2] != b"MZ" or image[pe:pe+4] != b"PE\0\0" or struct.unpack_from("<H", image, pe+4)[0] != 0x8664:
        raise ValueError("Source dbgeng.dll must be an AMD64 PE image")
    manifest = destination / "aidb-runtime.json"
    previous_bytes = manifest.read_bytes() if manifest.exists() else None
    previous = json.loads(previous_bytes) if previous_bytes else {}
    paths = [*source.glob("*.dll"), *(source / "winext").rglob("*.dll")]
    files, snapshots, entries = {}, {}, {}
    for path in paths:
        relative = path.relative_to(source).as_posix()
        content = path.read_bytes()
        target = checked_path(destination / relative)
        old = target.read_bytes() if target.exists() else None
        if old is not None and digest(old) not in {digest(content), previous.get("files", {}).get(relative, {}).get("sha256")}:
            raise ValueError("Local runtime edits preserved: " + str(target))
        files[target], snapshots[target] = content, old
        entries[relative] = {"source": str(path), "sha256": digest(content), "size": len(content)}
    receipt = {"source": str(source), "architecture": "amd64", "files": entries,
               "redistributed": False}
    for relative, entry in previous.get("files", {}).items():
        if relative not in entries:
            target = checked_path(destination / relative)
            # Do not follow out-of-root entries from a damaged manifest.
            if not target.resolve().is_relative_to(destination.resolve()):
                raise ValueError("Runtime manifest path escapes destination")
            old = target.read_bytes() if target.exists() else None
            if old is not None and digest(old) != entry["sha256"]:
                raise ValueError("Obsolete local runtime edits preserved: " + str(target))
            files[target], snapshots[target] = None, old
    files[manifest] = (json.dumps(receipt, indent=2) + "\n").encode()
    snapshots[manifest] = previous_bytes
    written = []
    try:
        for target, content in files.items():
            if (target.read_bytes() if target.exists() else None) != snapshots[target]:
                raise ValueError("Runtime changed during preparation: " + str(target))
            if content is None:
                target.unlink(missing_ok=True)
            else:
                atomic_write(target, content)
            written.append(target)
    except BaseException:
        for target in reversed(written):
            if (target.read_bytes() if target.exists() else None) != files[target]:
                continue
            old = snapshots[target]
            if old is None:
                target.unlink(missing_ok=True)
            else:
                atomic_write(target, old)
        raise
    return {"prepared": True, "directory": str(destination), "fileCount": len(entries), "manifest": str(manifest)}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path)
    parser.add_argument("--destination", type=Path)
    args = parser.parse_args()
    print(json.dumps(prepare(args.source, args.destination), indent=2))
