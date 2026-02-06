"""Bundled dependency bootstrapper for the MCP server."""

from __future__ import annotations

import shutil
import subprocess
import sys
import sysconfig
import zipfile
from pathlib import Path
from typing import List, Optional

_MIN_PYTHON = (3, 10)


def ensure_deps() -> None:
    if sys.version_info < _MIN_PYTHON:
        raise RuntimeError("Python 3.10+ is required to run AIDebugger.")

    wheels_dir = Path(__file__).resolve().parent / "vendor" / "wheels"
    if not wheels_dir.exists():
        return

    selected = _select_wheels(wheels_dir)
    if not selected:
        return

    target_dir = _get_target_dir()
    marker = target_dir / ".bundled_deps"
    stamp = "|".join(sorted([wheel.name for wheel in selected]))

    if marker.exists():
        try:
            existing = marker.read_text(encoding="utf-8")
            if existing == stamp and target_dir.exists():
                _add_sys_path(target_dir)
                return
        except Exception:
            pass

    if target_dir.exists():
        shutil.rmtree(target_dir)
    target_dir.mkdir(parents=True, exist_ok=True)

    for wheel in selected:
        _extract_wheel(wheel, target_dir)

    marker.write_text(stamp, encoding="utf-8")
    _add_sys_path(target_dir)


def _get_target_dir() -> Path:
    py_tag = _python_tag()
    platform_tag = _platform_tag()
    return Path.home() / ".ai-debugger" / "pydeps" / f"{py_tag}_{platform_tag}"


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
        subprocess.check_call(
            [
                sys.executable, "-m", "pip", "download",
                f"pydantic-core=={required_version}",
                "--only-binary=:all:",
                "--no-deps",
                f"--python-version={sys.version_info.major}.{sys.version_info.minor}",
                f"--platform={platform_tag}",
                "-d", str(wheels_dir),
            ],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.PIPE,
            timeout=120,
        )
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
