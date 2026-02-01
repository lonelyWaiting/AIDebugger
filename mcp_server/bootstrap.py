"""Bundled dependency bootstrapper for the MCP server."""

from __future__ import annotations

import shutil
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
        if any(path.name.startswith("pydantic_core-") for path in wheels_dir.glob("*.whl")):
            raise RuntimeError(
                "Bundled dependencies do not include a pydantic_core wheel for "
                f"Python {py_tag} on {platform_tag}. Please use Python 3.10-3.13 x64."
            )
    else:
        selected.append(pydantic_core)

    return selected


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
