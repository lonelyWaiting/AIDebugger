"""Bundled dependency bootstrapper for the MCP server."""

from __future__ import annotations

import sys
import sysconfig
import time
from contextlib import contextmanager
import zipfile
import hashlib
import hmac
import importlib
import json
import os
import re
import shutil
import stat
from pathlib import Path
from typing import Dict, List, Optional

_MIN_PYTHON = (3, 10)
_DEPS_ENSURED = False
REPAIR_TARGET_STALE_SECONDS = 15 * 60
MAX_WHEEL_MEMBER_BYTES = 256 * 1024 * 1024
MAX_WHEEL_EXTRACTED_BYTES = 2 * 1024 * 1024 * 1024
MAX_WHEEL_MEMBERS = 100_000
_WINDOWS_RESERVED_NAMES = {
    "con", "prn", "aux", "nul",
    *(f"com{index}" for index in range(1, 10)),
    *(f"lpt{index}" for index in range(1, 10)),
}


def ensure_deps() -> None:
    global _DEPS_ENSURED
    # The validated dependency cache is immutable. Direct development entrypoints
    # do not pass through vs_launcher, so enforce this before importing from pydeps.
    sys.dont_write_bytecode = True
    if _DEPS_ENSURED:
        return

    if sys.version_info < _MIN_PYTHON:
        raise RuntimeError("Python 3.10+ is required to run AIDebugger.")

    wheels_dir = _bundled_wheels_dir()
    if not wheels_dir.is_dir() or _path_is_reparse(wheels_dir):
        raise RuntimeError(
            "Bundled dependency directory is missing; refusing to use ambient Python packages"
        )

    manifest = _load_and_validate_manifest(wheels_dir)
    selected = _select_wheels(wheels_dir, manifest)
    if not selected:
        raise RuntimeError(
            "No compatible bundled dependencies were found; refusing to use ambient Python packages"
        )
    _verify_selected_wheels(selected, manifest)

    stamp = _dependency_stamp(selected, wheels_dir)
    target_dir = _get_target_dir(stamp)
    canonical_target_dir = target_dir

    with _dependency_lock(stamp, target_dir.parent):
        # Recheck after acquiring the cross-process lock. Another MCP process may
        # have completed the same content-addressed extraction while we waited.
        if _target_marker_matches(target_dir, stamp):
            _add_sys_path(target_dir, stamp)
            _validate_runtime_import_origins(target_dir)
            _DEPS_ENSURED = True
            return

        # A previous run may have been interrupted after creating the content-addressed
        # directory but before writing its marker. Do not delete that directory because
        # Windows can keep extracted .pyd files locked; reuse a completed repair dir
        # instead of allocating a new one on every startup.
        if target_dir.exists():
            repaired_dir = _find_valid_repair_target_dir(target_dir, stamp)
            if repaired_dir is not None:
                _cleanup_stale_repair_target_dirs(canonical_target_dir, stamp, keep=repaired_dir)
                _add_sys_path(repaired_dir, stamp)
                _validate_runtime_import_origins(repaired_dir)
                _DEPS_ENSURED = True
                return
            target_dir = _allocate_repair_target_dir(target_dir, stamp)
        target_dir.mkdir(parents=True, exist_ok=True)

        extracted_files: Dict[str, str] = {}
        manifest_wheels = _manifest_wheel_entries(manifest)
        for wheel in selected:
            _extract_wheel(
                wheel,
                target_dir,
                extracted_files,
                manifest_wheels[wheel.name],
            )

        _write_target_marker(target_dir, stamp)
        if not _target_marker_matches(target_dir, stamp):
            raise RuntimeError("Bundled dependency cache failed post-extraction validation")
        _cleanup_stale_repair_target_dirs(canonical_target_dir, stamp, keep=target_dir)
        _add_sys_path(target_dir, stamp)
        _validate_runtime_import_origins(target_dir)
        _DEPS_ENSURED = True


def _bundled_wheels_dir() -> Path:
    return Path(__file__).resolve().parent / "vendor" / "wheels"


@contextmanager
def _dependency_lock(stamp: str, lock_root: Path):
    lock_root.mkdir(parents=True, exist_ok=True)
    if os.name == "nt":
        import ctypes

        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel32.CreateMutexW.argtypes = [ctypes.c_void_p, ctypes.c_bool, ctypes.c_wchar_p]
        kernel32.CreateMutexW.restype = ctypes.c_void_p
        kernel32.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
        kernel32.WaitForSingleObject.restype = ctypes.c_uint32
        kernel32.ReleaseMutex.argtypes = [ctypes.c_void_p]
        kernel32.ReleaseMutex.restype = ctypes.c_bool
        kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
        kernel32.CloseHandle.restype = ctypes.c_bool
        name = f"Local\\AIDebugger_PyDeps_{stamp}"
        handle = kernel32.CreateMutexW(None, False, name)
        if not handle:
            raise OSError(ctypes.get_last_error(), "CreateMutexW failed")
        acquired = False
        deadline = time.monotonic() + 60.0
        try:
            while time.monotonic() < deadline:
                result = kernel32.WaitForSingleObject(handle, 250)
                if result in (0x00000000, 0x00000080):
                    acquired = True
                    break
                if result != 0x00000102:
                    raise OSError(ctypes.get_last_error(), f"WaitForSingleObject failed: {result}")
            if not acquired:
                raise TimeoutError("Timed out waiting for bundled dependency extraction")
            yield
        finally:
            if acquired:
                kernel32.ReleaseMutex(handle)
            kernel32.CloseHandle(handle)
        return

    import fcntl

    lock_path = lock_root / f".{stamp}.lock"
    with lock_path.open("a+b") as lock_file:
        deadline = time.monotonic() + 60.0
        while True:
            try:
                fcntl.flock(lock_file.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
                break
            except BlockingIOError:
                if time.monotonic() >= deadline:
                    raise TimeoutError("Timed out waiting for bundled dependency extraction")
                time.sleep(0.25)
        try:
            yield
        finally:
            fcntl.flock(lock_file.fileno(), fcntl.LOCK_UN)


def _dependency_stamp(wheels: List[Path], wheels_dir: Path) -> str:
    digest = hashlib.sha256()
    digest.update(b"AIDebugger bundled deps v2\n")

    manifest = wheels_dir / "manifest.json"
    if manifest.exists():
        digest.update(b"manifest.json\n")
        digest.update(_file_sha256(manifest).encode("ascii"))
        digest.update(b"\n")

    for wheel in sorted(wheels, key=lambda path: path.name.lower()):
        digest.update(wheel.name.encode("utf-8"))
        digest.update(b"\n")
        digest.update(str(wheel.stat().st_size).encode("ascii"))
        digest.update(b"\n")
        digest.update(_file_sha256(wheel).encode("ascii"))
        digest.update(b"\n")

    return digest.hexdigest()


def _file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _get_target_dir(stamp: str) -> Path:
    py_tag = _python_tag()
    platform_tag = _platform_tag()
    home = Path.home()
    aidb_root = _ensure_plain_child_directory(home, ".ai-debugger")
    pydeps_root = _ensure_plain_child_directory(aidb_root, "pydeps")
    return pydeps_root / f"{py_tag}_{platform_tag}_{stamp[:16]}"


def _ensure_plain_child_directory(
    parent: Path,
    name: str,
    *,
    require_plain_parent: bool = True,
) -> Path:
    if not name or name in (".", "..") or "/" in name or "\\" in name:
        raise RuntimeError("Dependency cache directory must be a direct child")
    if not parent.is_dir() or (require_plain_parent and _path_is_reparse(parent)):
        raise RuntimeError("Dependency cache parent is missing or is a reparse point")
    child = parent / name
    child.mkdir(exist_ok=True)
    if not child.is_dir() or _path_is_reparse(child):
        raise RuntimeError("Dependency cache directory is a reparse point")
    return child


def _allocate_repair_target_dir(target_dir: Path, stamp: str) -> Path:
    base = target_dir.parent
    prefix = f"{target_dir.name}_repair_{os.getpid()}_"
    for index in range(100):
        candidate = base / f"{prefix}{index}"
        if not os.path.lexists(candidate):
            return candidate
    raise RuntimeError(f"Unable to allocate pydeps repair directory for {stamp[:16]}")


def _target_marker_matches(target_dir: Path, stamp: str) -> bool:
    marker = target_dir / ".bundled_deps"
    if not target_dir.exists() or not _is_plain_file(marker):
        return False
    try:
        payload = json.loads(marker.read_text(encoding="utf-8"))
        if not isinstance(payload, dict) or payload.get("version") != 2 or payload.get("stamp") != stamp:
            return False
        files = payload.get("files")
        directories = payload.get("directories")
        if not isinstance(files, dict) or not files:
            return False
        if not isinstance(directories, list) or not all(
            isinstance(value, str) for value in directories
        ):
            return False
        expected_directories = set(directories)
        if len(expected_directories) != len(directories) or any(
            not _is_safe_relative_name(value) for value in expected_directories
        ):
            return False

        scanned = _scan_dependency_tree(target_dir)
        if scanned is None:
            return False
        actual_directories, actual_files = scanned
        if actual_directories != expected_directories or set(actual_files) != set(files):
            return False
        for relative_name, metadata in files.items():
            if not isinstance(relative_name, str) or not isinstance(metadata, dict):
                return False
            if not _is_safe_relative_name(relative_name):
                return False
            expected_size = metadata.get("size")
            expected_hash = metadata.get("sha256")
            if (
                isinstance(expected_size, bool)
                or not isinstance(expected_size, int)
                or expected_size < 0
                or not isinstance(expected_hash, str)
                or not re.fullmatch(r"[0-9a-f]{64}", expected_hash)
            ):
                return False
            installed = actual_files[relative_name]
            if installed.stat().st_size != expected_size:
                return False
            if not hmac.compare_digest(_file_sha256(installed), expected_hash):
                return False
        return True
    except (OSError, UnicodeError, json.JSONDecodeError, ValueError):
        return False


def _write_target_marker(target_dir: Path, stamp: str) -> None:
    target_root = target_dir
    scanned = _scan_dependency_tree(target_root)
    if scanned is None:
        raise RuntimeError("Bundled dependency extraction produced an unsafe directory tree")
    directories, installed_files = scanned
    files: Dict[str, dict] = {}
    for relative_name, installed in sorted(
        installed_files.items(), key=lambda item: item[0].lower()
    ):
        files[relative_name] = {
            "size": installed.stat().st_size,
            "sha256": _file_sha256(installed),
        }
    if not files:
        raise RuntimeError("Bundled dependency extraction produced no files")
    payload = {
        "version": 2,
        "stamp": stamp,
        "directories": sorted(directories, key=str.lower),
        "files": files,
    }
    marker = target_root / ".bundled_deps"
    temporary = marker.with_name(f".{marker.name}.{os.getpid()}.{time.time_ns()}.tmp")
    try:
        temporary.write_text(
            json.dumps(payload, sort_keys=True, separators=(",", ":")),
            encoding="utf-8",
        )
        os.replace(temporary, marker)
    finally:
        try:
            temporary.unlink(missing_ok=True)
        except OSError:
            pass


def _is_safe_relative_name(relative_name: str) -> bool:
    parts = relative_name.split("/")
    return bool(parts) and not any(
        part in ("", ".", "..") or part == "__pycache__"
        for part in parts
    )


def _path_is_reparse(path: Path) -> bool:
    try:
        metadata = path.lstat()
    except OSError:
        return True
    return path.is_symlink() or bool(
        getattr(metadata, "st_file_attributes", 0) &
        getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
    )


def _is_plain_file(path: Path) -> bool:
    try:
        metadata = path.lstat()
    except OSError:
        return False
    return stat.S_ISREG(metadata.st_mode) and not _path_is_reparse(path)


def _scan_dependency_tree(
    target_dir: Path,
) -> Optional[tuple[set[str], Dict[str, Path]]]:
    if not target_dir.is_dir() or _path_is_reparse(target_dir):
        return None
    directories: set[str] = set()
    files: Dict[str, Path] = {}
    for current_name, directory_names, file_names in os.walk(target_dir, followlinks=False):
        current = Path(current_name)
        for name in directory_names:
            directory = current / name
            relative = directory.relative_to(target_dir).as_posix()
            if not _is_safe_relative_name(relative) or _path_is_reparse(directory):
                return None
            directories.add(relative)
        for name in file_names:
            installed = current / name
            relative = installed.relative_to(target_dir).as_posix()
            if relative == ".bundled_deps":
                continue
            if (
                not _is_safe_relative_name(relative)
                or name.endswith((".pyc", ".pyo"))
                or (name.startswith(".bundled_deps.") and name.endswith(".tmp"))
                or not _is_plain_file(installed)
            ):
                return None
            files[relative] = installed
    return directories, files


def _repair_target_dirs(target_dir: Path) -> List[Path]:
    base = target_dir.parent
    if not base.exists():
        return []
    prefix = f"{target_dir.name}_repair_"
    return sorted(
        (
            candidate for candidate in base.glob(f"{prefix}*")
            if candidate.is_dir() and not _path_is_reparse(candidate)
        ),
        key=lambda path: path.name.lower(),
    )


def _find_valid_repair_target_dir(target_dir: Path, stamp: str) -> Optional[Path]:
    for candidate in _repair_target_dirs(target_dir):
        if _target_marker_matches(candidate, stamp):
            return candidate
    return None


def _cleanup_stale_repair_target_dirs(target_dir: Path, stamp: str, keep: Path) -> None:
    for candidate in _repair_target_dirs(target_dir):
        if candidate == keep or _target_marker_matches(candidate, stamp):
            continue
        if not _repair_target_is_stale(candidate):
            continue
        try:
            shutil.rmtree(candidate)
        except OSError:
            pass


def _repair_target_is_stale(candidate: Path) -> bool:
    newest_mtime = 0.0
    try:
        newest_mtime = max(newest_mtime, candidate.stat().st_mtime)
        for path in candidate.rglob("*"):
            try:
                newest_mtime = max(newest_mtime, path.stat().st_mtime)
            except OSError:
                return False
    except OSError:
        return False
    return (time.time() - newest_mtime) >= REPAIR_TARGET_STALE_SECONDS


def _select_wheels(wheels_dir: Path, manifest: dict) -> List[Path]:
    py_tag = _python_tag()
    platform_tag = _platform_tag()
    declared = _manifest_wheel_entries(manifest)
    actual = {}
    for wheel in wheels_dir.glob("*.whl"):
        if not _is_plain_file(wheel):
            raise RuntimeError(f"Bundled wheel is not a plain file: {wheel.name}")
        actual[wheel.name] = wheel
    declared_names = set(declared)
    actual_names = set(actual)
    if actual_names != declared_names:
        missing = sorted(declared_names - actual_names)
        extra = sorted(actual_names - declared_names)
        detail = []
        if missing:
            detail.append("missing=" + ",".join(missing[:8]))
        if extra:
            detail.append("extra=" + ",".join(extra[:8]))
        raise RuntimeError(
            "Bundled wheel directory does not exactly match its manifest: " + "; ".join(detail)
        )
    selected = [
        actual[name]
        for name in sorted(declared_names)
        if _wheel_is_compatible(name, py_tag, platform_tag)
    ]
    _assert_single_distribution_versions(path.name for path in selected)
    if not selected or not any(path.name.startswith("pydantic_core-") for path in selected):
        raise RuntimeError(
            f"No complete bundled dependency set for Python {py_tag} on {platform_tag}. "
            "Install a supported AIDebugger build; runtime dependency downloads are disabled."
        )
    return selected


def _load_and_validate_manifest(wheels_dir: Path) -> dict:
    manifest_path = wheels_dir / "manifest.json"
    if not _is_plain_file(manifest_path):
        raise RuntimeError("Bundled wheel manifest is missing")
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise RuntimeError("Bundled wheel manifest is invalid") from exc

    py_tag = _python_tag()
    platform_tag = _platform_tag()
    supported_python = manifest.get("python")
    if not isinstance(supported_python, list) or py_tag not in supported_python:
        supported_text = ", ".join(supported_python) if isinstance(supported_python, list) else "none"
        raise RuntimeError(
            f"Python {py_tag} is not covered by this AIDebugger dependency bundle; "
            f"supported tags: {supported_text}"
        )
    if manifest.get("platform") != platform_tag:
        raise RuntimeError(
            f"Platform {platform_tag} is not covered by this AIDebugger dependency bundle"
        )
    if not isinstance(manifest.get("wheels"), list):
        raise RuntimeError("Bundled wheel manifest has no wheel list")
    _manifest_wheel_entries(manifest)
    return manifest


def _manifest_wheel_entries(manifest: dict) -> Dict[str, str]:
    expected: Dict[str, str] = {}
    for entry in manifest.get("wheels", []):
        if not isinstance(entry, dict):
            raise RuntimeError("Bundled wheel manifest contains an invalid entry")
        relative = str(entry.get("file") or "").replace("\\", "/")
        match = re.fullmatch(r"vendor/wheels/([^/]+\.whl)", relative)
        digest = str(entry.get("sha256") or "").lower()
        if match is None or not re.fullmatch(r"[0-9a-f]{64}", digest):
            raise RuntimeError("Bundled wheel manifest contains an invalid filename or SHA-256")
        filename = match.group(1)
        if filename in expected:
            raise RuntimeError(f"Bundled wheel manifest contains duplicate entry: {filename}")
        expected[filename] = digest
    if not expected:
        raise RuntimeError("Bundled wheel manifest has no wheel entries")
    return expected


def _assert_single_distribution_versions(filenames) -> None:
    distribution_versions: Dict[str, str] = {}
    for filename in filenames:
        wheel_parts = filename[:-4].split("-")
        if len(wheel_parts) < 5:
            raise RuntimeError(f"Bundled wheel has an invalid filename: {filename}")
        distribution = wheel_parts[0].replace(".", "_").lower()
        version = wheel_parts[1]
        existing_version = distribution_versions.get(distribution)
        if existing_version is not None and existing_version != version:
            raise RuntimeError(
                f"Bundled wheel set contains multiple versions of {distribution}"
            )
        distribution_versions[distribution] = version


def _wheel_is_compatible(filename: str, py_tag: str, platform_tag: str) -> bool:
    if filename.endswith("-none-any.whl"):
        return True
    match = re.search(
        r"-(cp(\d{2,3})t?)-(cp\d{2,3}t?|abi3)-([a-z0-9_]+)\.whl$",
        filename,
    )
    if match is None or match.group(4) != platform_tag:
        return False
    wheel_python = match.group(1)
    abi = match.group(3)
    if abi == "abi3":
        return int(py_tag[2:]) >= int(match.group(2))
    return wheel_python == py_tag and abi == py_tag


def _verify_selected_wheels(selected: List[Path], manifest: dict) -> None:
    expected = _manifest_wheel_entries(manifest)

    for wheel in selected:
        expected_digest = expected.get(wheel.name)
        if expected_digest is None:
            raise RuntimeError(f"Bundled wheel is not declared in manifest: {wheel.name}")
        actual_digest = _file_sha256(wheel)
        if not hmac.compare_digest(actual_digest, expected_digest):
            raise RuntimeError(f"Bundled wheel SHA-256 mismatch: {wheel.name}")


def _extract_wheel(
    wheel: Path,
    target_dir: Path,
    extracted_files: Optional[Dict[str, str]] = None,
    expected_sha256: Optional[str] = None,
) -> None:
    """Extract a verified wheel without trusting ZIP member paths or link metadata."""
    extracted_files = extracted_files if extracted_files is not None else {}
    target_root = target_dir.resolve()
    total_bytes = 0

    # Hash and extract through the same open handle so a path replacement cannot
    # swap different bytes into the verified-to-extracted gap.
    with wheel.open("rb") as wheel_stream:
        if expected_sha256 is not None:
            digest = hashlib.sha256()
            for chunk in iter(lambda: wheel_stream.read(1024 * 1024), b""):
                digest.update(chunk)
            if not hmac.compare_digest(digest.hexdigest(), expected_sha256):
                raise RuntimeError(f"Bundled wheel SHA-256 mismatch: {wheel.name}")
            wheel_stream.seek(0)
        with zipfile.ZipFile(wheel_stream, "r") as archive:
            members = archive.infolist()
            if len(members) > MAX_WHEEL_MEMBERS:
                raise RuntimeError(f"Bundled wheel has too many members: {wheel.name}")

            planned = []
            entry_types: Dict[str, bool] = {}
            for member in members:
                raw_name = member.filename
                if not raw_name or "\x00" in raw_name or "\\" in raw_name:
                    raise RuntimeError(f"Bundled wheel has an invalid member path: {wheel.name}")

                normalized = raw_name.rstrip("/")
                parts = normalized.split("/") if normalized else []
                if (
                    raw_name.startswith("/")
                    or not parts
                    or any(part in ("", ".", "..") for part in parts)
                ):
                    raise RuntimeError(f"Bundled wheel has an unsafe member path: {wheel.name}")

                relative_key = "/".join(parts)
                canonical_parts = [_windows_archive_component_key(part, wheel.name) for part in parts]
                case_key = "/".join(canonical_parts)
                if case_key in entry_types:
                    raise RuntimeError(f"Bundled wheel has duplicate members: {wheel.name}")
                entry_types[case_key] = member.is_dir()

                unix_mode = (member.external_attr >> 16) & 0xFFFF
                file_type = unix_mode & 0o170000
                if file_type not in (0, 0o040000, 0o100000) or file_type == 0o120000:
                    raise RuntimeError(f"Bundled wheel contains a link or special file: {wheel.name}")
                if member.file_size < 0 or member.file_size > MAX_WHEEL_MEMBER_BYTES:
                    raise RuntimeError(f"Bundled wheel member exceeds the size limit: {wheel.name}")
                total_bytes += member.file_size
                if total_bytes > MAX_WHEEL_EXTRACTED_BYTES:
                    raise RuntimeError(f"Bundled wheel exceeds the extraction size limit: {wheel.name}")

                destination = (target_root / Path(*parts)).resolve()
                try:
                    destination.relative_to(target_root)
                except ValueError as exc:
                    raise RuntimeError(f"Bundled wheel member escapes the target: {wheel.name}") from exc
                planned.append((member, destination, relative_key, member.is_dir()))

            for canonical_path, is_directory in entry_types.items():
                parts = canonical_path.split("/")
                for index in range(1, len(parts)):
                    parent = "/".join(parts[:index])
                    if parent in entry_types and not entry_types[parent]:
                        raise RuntimeError(f"Bundled wheel has a file/directory path collision: {wheel.name}")
                if not is_directory and any(
                    other.startswith(canonical_path + "/") for other in entry_types
                ):
                    raise RuntimeError(f"Bundled wheel has a file/directory path collision: {wheel.name}")

            for member, destination, relative_key, is_directory in planned:
                if is_directory:
                    destination.mkdir(parents=True, exist_ok=True)
                    continue

                digest = hashlib.sha256()
                destination.parent.mkdir(parents=True, exist_ok=True)
                temporary = destination.with_name(
                    f".{destination.name}.{os.getpid()}.{time.time_ns()}.tmp"
                )
                try:
                    with archive.open(member, "r") as source, temporary.open("xb") as output:
                        copied = 0
                        for chunk in iter(lambda: source.read(1024 * 1024), b""):
                            copied += len(chunk)
                            if copied > member.file_size or copied > MAX_WHEEL_MEMBER_BYTES:
                                raise RuntimeError(
                                    f"Bundled wheel member expanded beyond its declared size: {wheel.name}"
                                )
                            digest.update(chunk)
                            output.write(chunk)
                    if copied != member.file_size:
                        raise RuntimeError(f"Bundled wheel member size mismatch: {wheel.name}")

                    content_hash = digest.hexdigest()
                    collision_key = relative_key.casefold()
                    previous_hash = extracted_files.get(collision_key)
                    if previous_hash is not None and not hmac.compare_digest(previous_hash, content_hash):
                        raise RuntimeError(
                            f"Bundled wheels contain conflicting files: {relative_key}"
                        )
                    if previous_hash is None:
                        os.replace(temporary, destination)
                        extracted_files[collision_key] = content_hash
                finally:
                    try:
                        temporary.unlink(missing_ok=True)
                    except OSError:
                        pass


def _windows_archive_component_key(component: str, archive_name: str) -> str:
    if (
        not component
        or component[-1] in (" ", ".")
        or any(ord(character) < 32 or character in '<>:"/\\|?*' for character in component)
    ):
        raise RuntimeError(f"Bundled wheel has a Windows-unsafe member path: {archive_name}")
    base_name = component.split(".", 1)[0].casefold()
    if base_name in _WINDOWS_RESERVED_NAMES:
        raise RuntimeError(f"Bundled wheel uses a reserved Windows path: {archive_name}")
    return component.casefold()


def _add_sys_path(target_dir: Path, stamp: Optional[str] = None) -> None:
    if stamp is not None and not _target_marker_matches(target_dir, stamp):
        raise RuntimeError("Refusing to load an unvalidated bundled dependency cache")
    path_str = str(target_dir)
    pth_paths, bootstrap_modules = _validated_pth_plan(target_dir)
    bundled_paths = [path_str, *pth_paths]
    for entry in bundled_paths:
        while entry in sys.path:
            sys.path.remove(entry)
    sys.path[0:0] = bundled_paths

    # Do not delegate to site.addsitedir: it appends .pth paths and executes
    # imports before we can establish bundled-path precedence. Only the exact
    # pywin32 bootstrap hook from the hash-validated wheel is supported.
    for module_name in bootstrap_modules:
        existing = sys.modules.get(module_name)
        existing_file = getattr(existing, "__file__", None) if existing is not None else None
        if existing_file is not None and not _path_is_within(target_dir, Path(existing_file)):
            del sys.modules[module_name]
        module = importlib.import_module(module_name)
        module_file = getattr(module, "__file__", None)
        if module_file is None or not _path_is_within(target_dir, Path(module_file)):
            raise RuntimeError(f"Bundled .pth bootstrap resolved outside dependency cache: {module_name}")
    if stamp is not None and not _target_marker_matches(target_dir, stamp):
        raise RuntimeError("Bundled dependency cache changed while processing its .pth files")


def _validate_runtime_import_origins(target_dir: Path) -> None:
    """Prove that security-critical runtime imports resolve inside the verified cache."""
    for module_name in (
        "mcp", "pydantic", "pydantic_core", "win32file", "win32pipe", "pywintypes",
    ):
        module = importlib.import_module(module_name)
        module_file = getattr(module, "__file__", None)
        if module_file is None or not _path_is_within(target_dir, Path(module_file)):
            raise RuntimeError(
                f"Bundled dependency {module_name} resolved outside the validated cache"
            )


def _validated_pth_plan(target_dir: Path) -> tuple[List[str], List[str]]:
    paths: List[str] = []
    modules: List[str] = []
    for pth_file in sorted(target_dir.glob("*.pth"), key=lambda path: path.name.lower()):
        if not _is_plain_file(pth_file):
            raise RuntimeError("Bundled dependency .pth file is not a plain file")
        try:
            lines = pth_file.read_text(encoding="utf-8-sig").splitlines()
        except UnicodeError as exc:
            raise RuntimeError("Bundled dependency .pth file is not UTF-8") from exc
        for raw_line in lines:
            line = raw_line.strip()
            if not line or line.startswith("#"):
                continue
            if line.startswith("import ") or line.startswith("import\t"):
                if line != "import pywin32_bootstrap":
                    raise RuntimeError("Bundled dependency .pth contains an unsupported import hook")
                if "pywin32_bootstrap" not in modules:
                    modules.append("pywin32_bootstrap")
                continue
            if os.path.isabs(line) or ":" in line:
                raise RuntimeError("Bundled dependency .pth path must be relative")
            normalized = line.replace("\\", os.sep).replace("/", os.sep)
            candidate = target_dir / normalized
            if (
                not candidate.is_dir()
                or _path_is_reparse(candidate)
                or not _path_is_within(target_dir, candidate)
            ):
                raise RuntimeError("Bundled dependency .pth path escaped its validated cache")
            candidate_text = str(candidate)
            if candidate_text not in paths:
                paths.append(candidate_text)
    return paths, modules


def _path_is_within(root: Path, candidate: Path) -> bool:
    root_absolute = os.path.normcase(os.path.abspath(root))
    candidate_absolute = os.path.normcase(os.path.abspath(candidate))
    try:
        return os.path.commonpath((root_absolute, candidate_absolute)) == root_absolute
    except ValueError:
        return False


def _python_tag() -> str:
    return f"cp{sys.version_info.major}{sys.version_info.minor}"


def _platform_tag() -> str:
    value = sysconfig.get_platform().lower()
    return value.replace("-", "_").replace(".", "_")
