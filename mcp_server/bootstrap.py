"""Bundled dependency bootstrapper for the MCP server."""

from __future__ import annotations

import subprocess
import sys
import sysconfig
import time
import zipfile
import hashlib
import os
import shutil
from pathlib import Path
from typing import List, Optional

_MIN_PYTHON = (3, 10)
_DEPS_ENSURED = False
REPAIR_TARGET_STALE_SECONDS = 15 * 60


def ensure_deps() -> None:
    global _DEPS_ENSURED
    if _DEPS_ENSURED:
        return

    if sys.version_info < _MIN_PYTHON:
        raise RuntimeError("Python 3.10+ is required to run AIDebugger.")

    wheels_dir = Path(__file__).resolve().parent / "vendor" / "wheels"
    if not wheels_dir.exists():
        _DEPS_ENSURED = True
        return

    selected = _select_wheels(wheels_dir)
    if not selected:
        _DEPS_ENSURED = True
        return

    stamp = _dependency_stamp(selected, wheels_dir)
    target_dir = _get_target_dir(stamp)
    canonical_target_dir = target_dir

    if _target_marker_matches(target_dir, stamp):
        _add_sys_path(target_dir)
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
            _add_sys_path(repaired_dir)
            _DEPS_ENSURED = True
            return
        target_dir = _allocate_repair_target_dir(target_dir, stamp)
    marker = target_dir / ".bundled_deps"
    target_dir.mkdir(parents=True, exist_ok=True)

    for wheel in selected:
        _extract_wheel(wheel, target_dir)

    marker.write_text(stamp, encoding="utf-8")
    _cleanup_stale_repair_target_dirs(canonical_target_dir, stamp, keep=target_dir)
    _add_sys_path(target_dir)
    _DEPS_ENSURED = True


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
    return Path.home() / ".ai-debugger" / "pydeps" / f"{py_tag}_{platform_tag}_{stamp[:16]}"


def _allocate_repair_target_dir(target_dir: Path, stamp: str) -> Path:
    base = target_dir.parent
    prefix = f"{target_dir.name}_repair_{os.getpid()}_"
    for index in range(100):
        candidate = base / f"{prefix}{index}"
        if not candidate.exists():
            return candidate
    raise RuntimeError(f"Unable to allocate pydeps repair directory for {stamp[:16]}")


def _target_marker_matches(target_dir: Path, stamp: str) -> bool:
    marker = target_dir / ".bundled_deps"
    if not target_dir.exists() or not marker.exists():
        return False
    try:
        return marker.read_text(encoding="utf-8") == stamp
    except (OSError, UnicodeError):
        return False


def _repair_target_dirs(target_dir: Path) -> List[Path]:
    base = target_dir.parent
    if not base.exists():
        return []
    prefix = f"{target_dir.name}_repair_"
    return sorted(
        (candidate for candidate in base.glob(f"{prefix}*") if candidate.is_dir()),
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


def _select_wheels(wheels_dir: Path) -> List[Path]:
    py_tag = _python_tag()
    platform_tag = _platform_tag()

    selected: List[Path] = []
    pydantic_core: Optional[Path] = None

    for wheel in wheels_dir.glob("*.whl"):
        name = wheel.name
        if name.startswith("pydantic_core-"):
            if f"-{py_tag}-" in name and name.endswith(f"-{platform_tag}.whl"):
                pydantic_core = wheel
            continue
        selected.append(wheel)

    if pydantic_core is None:
        # No bundled wheel for this Python version – try pip install as fallback.
        # This makes the server automatically work with future Python versions
        # without needing to pre-bundle a new pydantic_core wheel.
        pydantic_core = _pip_install_pydantic_core(wheels_dir)
        if pydantic_core is None:
            if any(p.name.startswith("pydantic_core-") for p in wheels_dir.glob("*.whl")):
                raise RuntimeError(
                    f"No pydantic_core wheel for Python {py_tag} on {platform_tag}. "
                    f"Bundled wheels cover other Python versions but not yours. "
                    f"Auto-install via pip also failed. "
                    f"Please install manually: pip install pydantic-core"
                )
    if pydantic_core is not None:
        selected.append(pydantic_core)

    return selected


def _pip_install_pydantic_core(wheels_dir: Path) -> Optional[Path]:
    """Attempt to pip-download the pydantic_core wheel for the current Python version.

    Downloads into the vendor/wheels directory so it's cached for future runs.
    Returns the Path to the downloaded wheel, or None on failure.
    """
    # Determine the required version from an existing bundled wheel
    required_version: Optional[str] = None
    for whl in wheels_dir.glob("pydantic_core-*.whl"):
        # Wheel filename format: pydantic_core-{version}-{py}-{py}-{platform}.whl
        parts = whl.name.split("-")
        if len(parts) >= 2:
            required_version = parts[1]
            break

    if required_version is None:
        return None

    py_tag = _python_tag()
    platform_tag = _platform_tag()

    try:
        print(
            f"[AIDebugger] No bundled pydantic_core for {py_tag}. "
            f"Downloading pydantic-core=={required_version} via pip...",
            file=sys.stderr,
        )
        completed = subprocess.run(
            [
                sys.executable, "-m", "pip", "download",
                f"pydantic-core=={required_version}",
                "--only-binary=:all:",
                "--no-deps",
                f"--python-version={sys.version_info.major}.{sys.version_info.minor}",
                f"--platform={platform_tag}",
                "-d", str(wheels_dir),
            ],
            capture_output=True,
            text=True,
            check=False,
            timeout=120,
        )
        if completed.returncode != 0:
            details = (completed.stderr or completed.stdout or "").strip()
            if len(details) > 2000:
                details = details[-2000:]
            raise RuntimeError(details or f"pip exited with code {completed.returncode}")
    except Exception as exc:
        print(f"[AIDebugger] pip download failed: {exc}", file=sys.stderr)
        return None

    # Find the newly downloaded wheel
    for whl in wheels_dir.glob("pydantic_core-*.whl"):
        if f"-{py_tag}-" in whl.name and whl.name.endswith(f"-{platform_tag}.whl"):
            print(f"[AIDebugger] Downloaded: {whl.name}", file=sys.stderr)
            return whl

    return None


def _extract_wheel(wheel: Path, target_dir: Path) -> None:
    with zipfile.ZipFile(wheel, "r") as archive:
        archive.extractall(target_dir)


def _add_sys_path(target_dir: Path) -> None:
    path_str = str(target_dir)
    if path_str not in sys.path:
        sys.path.insert(0, path_str)


def _python_tag() -> str:
    return f"cp{sys.version_info.major}{sys.version_info.minor}"


def _platform_tag() -> str:
    value = sysconfig.get_platform().lower()
    return value.replace("-", "_").replace(".", "_")
