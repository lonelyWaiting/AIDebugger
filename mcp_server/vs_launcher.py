"""Stable, version-independent launcher for the Visual Studio MCP runtime."""

from __future__ import annotations

import hashlib
import os
import re
import runpy
import stat
import sys
from pathlib import Path


LAUNCHER_VERSION = 1
_SIGNATURE_RE = re.compile(r"^[0-9a-f]{64}$")
_VERSION_RE = re.compile(r"^\d+(?:\.\d+){2,3}$")
_ROOT_FILES = {
    "bootstrap.py",
    "debug_method_manifest.json",
    "output_models.py",
    "requirements.txt",
    "tool_context.py",
    "tool_runtime.py",
    "vs_bridge.py",
    "vs_errors.py",
    "vs_session.py",
    "vs_mcp_app.py",
    "vs_tool_registry.py",
    "vs_trust.py",
    "vs_launch.py",
    "vs_launcher.py",
    "vs_server.py",
}
_TOOL_MODULE_FILES = {
    "__init__.py",
    "breakpoint_tools.py",
    "capability_tools.py",
    "control_tools.py",
    "memory_tools.py",
    "multi_vs_tools.py",
    "native_managed_tools.py",
    "snapshot_tools.py",
    "vs_instance_tools.py",
}
_PAYLOAD_DIRECTORIES = {
    "tool_modules",
    "vendor",
    "vendor/wheels",
}
_REPARSE_POINT_ATTRIBUTE = getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)


def _is_payload_file(relative: str) -> bool:
    parts = relative.replace("\\", "/").split("/")
    name = parts[-1].lower()
    if len(parts) == 1:
        return name in _ROOT_FILES
    if parts[0].lower() == "tool_modules" and len(parts) == 2:
        return name in _TOOL_MODULE_FILES
    if [part.lower() for part in parts[:2]] == ["vendor", "wheels"] and len(parts) == 3:
        return name.endswith(".whl") or name == "manifest.json"
    return False


def _path_is_reparse(path: Path) -> bool:
    try:
        metadata = path.lstat()
    except OSError:
        return True
    return path.is_symlink() or bool(
        getattr(metadata, "st_file_attributes", 0) & _REPARSE_POINT_ATTRIBUTE
    )


def _is_plain_file(path: Path) -> bool:
    try:
        metadata = path.lstat()
    except OSError:
        return False
    return stat.S_ISREG(metadata.st_mode) and not _path_is_reparse(path)


def _payload_signature(bundle: Path, *, reject_bytecode_caches: bool = False) -> str | None:
    if _path_is_reparse(bundle):
        return None
    records: list[str] = []
    for directory, directory_names, file_names in os.walk(bundle, followlinks=False):
        current = Path(directory)
        accepted_directories: list[str] = []
        for name in directory_names:
            path = current / name
            relative = path.relative_to(bundle).as_posix()
            if _path_is_reparse(path):
                return None
            if name == "__pycache__":
                if reject_bytecode_caches:
                    return None
                continue
            if relative.lower() not in _PAYLOAD_DIRECTORIES:
                return None
            accepted_directories.append(name)
        directory_names[:] = accepted_directories
        for name in file_names:
            path = current / name
            relative = path.relative_to(bundle).as_posix()
            if not _is_plain_file(path):
                return None
            if relative in {".bundle-signature", ".bundle-version"}:
                continue
            if not _is_payload_file(relative):
                return None
            digest = hashlib.sha256(path.read_bytes()).hexdigest()
            records.append(f"{relative}:{digest}")
    records.sort()
    return hashlib.sha256("\n".join(records).encode("utf-8")).hexdigest()


def _version_tuple(value: str) -> tuple[int, int, int, int] | None:
    if not _VERSION_RE.fullmatch(value):
        return None
    parts = [int(part) for part in value.split(".")]
    padded = (parts + [0] * 4)[:4]
    return padded[0], padded[1], padded[2], padded[3]


def _validated_candidate(root: Path, bundle: Path):
    try:
        if _path_is_reparse(bundle) or bundle.parent != root:
            return None
        version_file = bundle / ".bundle-version"
        signature_file = bundle / ".bundle-signature"
        if not _is_plain_file(version_file) or not _is_plain_file(signature_file):
            return None
        version_text = version_file.read_text(encoding="utf-8-sig").strip()
        signature = signature_file.read_text(encoding="utf-8-sig").strip().lower()
        version = _version_tuple(version_text)
        expected_name = f"{version_text.replace('.', '_')}_{signature[:16]}"
        if version is None or not _SIGNATURE_RE.fullmatch(signature) or bundle.name != expected_name:
            return None
        if _payload_signature(bundle, reject_bytecode_caches=True) != signature:
            return None
        server = bundle / "vs_server.py"
        if not _is_plain_file(server):
            return None
        return version, bundle.stat().st_mtime_ns, server
    except (OSError, ValueError):
        return None


def _select_server() -> Path:
    root = Path(__file__).resolve().parent
    active_marker = root / ".active-bundle"
    if not _is_plain_file(active_marker):
        raise RuntimeError("The Visual Studio MCP active-bundle marker is not a plain file")
    active_name = active_marker.read_text(encoding="utf-8-sig").strip()
    if not re.fullmatch(r"[0-9_]+_[0-9a-f]{16}", active_name):
        raise RuntimeError("The Visual Studio MCP active-bundle marker is invalid")
    candidate = _validated_candidate(root, root / active_name)
    if candidate is None:
        raise RuntimeError(f"The active Visual Studio MCP runtime bundle '{active_name}' failed validation")
    return candidate[2]


def main() -> None:
    server = _select_server()
    sys.dont_write_bytecode = True
    sys.path.insert(0, str(server.parent))
    runpy.run_path(str(server), run_name="__main__")


if __name__ == "__main__":
    main()
