"""Live smoke test for launching a Visual Studio instance from the MCP server.

Unlike ``run_live_vs_smoke.py`` this needs no experimental instance and no
pre-built debuggee: it launches a real Visual Studio through the MCP tool layer,
verifies that the launcher contract brought the authenticated pipe server up,
attaches to a freshly started native process, and closes the instance it started.

Requires every existing devenv.exe to be closed so the launch path is exercised
instead of the reuse path.
"""

from __future__ import annotations

import asyncio
import os
from pathlib import Path
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))

from mcp_server import bootstrap

bootstrap.ensure_deps()

from mcp_server import vs_bridge
from mcp_server.tool_modules import vs_instance_tools

LAUNCH_TIMEOUT_SECONDS = 180.0


def visible_dialogs(process_id: int) -> list[str]:
    """Visible modal dialogs owned by a process, as 'class: text' strings.

    A blocked launch shows up here (for example an "operation could not be completed"
    or "command is not valid" message box), which otherwise looks like a silent timeout.
    """
    import ctypes
    from ctypes import wintypes

    user32 = ctypes.WinDLL("user32", use_last_error=True)
    found: list[str] = []

    class RECT(ctypes.Structure):
        _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                    ("right", ctypes.c_long), ("bottom", ctypes.c_long)]

    def text_of(hwnd) -> str:
        buffer = ctypes.create_unicode_buffer(512)
        user32.GetWindowTextW(hwnd, buffer, 512)
        return buffer.value

    def class_of(hwnd) -> str:
        buffer = ctypes.create_unicode_buffer(256)
        user32.GetClassNameW(hwnd, buffer, 256)
        return buffer.value

    def child_text(hwnd) -> str:
        parts: list[str] = []
        enum_child = ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)

        def visit(child, _param):
            if class_of(child) in {"Static", "Button"}:
                part = text_of(child).strip()
                if part:
                    parts.append(part)
            return True

        user32.EnumChildWindows(hwnd, enum_child(visit), 0)
        return " | ".join(parts)

    enum_proc = ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)

    def visit(hwnd, _param):
        owner = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == process_id and user32.IsWindowVisible(hwnd):
            name = class_of(hwnd)
            if name == "#32770":
                found.append(f"{name}: {text_of(hwnd)} :: {child_text(hwnd)}")
        return True

    user32.EnumWindows(enum_proc(visit), 0)
    return found


def ui_thread_responsive(process_id: int, timeout_ms: int = 5000) -> bool:
    """Is the process pumping messages? A wedged UI thread is what Windows calls 'not responding'."""
    import ctypes

    user32 = ctypes.WinDLL("user32", use_last_error=True)
    WM_NULL = 0x0000
    SMTO_ABORTIFHUNG = 0x0002
    windows: list[int] = []

    enum_proc = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)

    def visit(hwnd, _param):
        owner = ctypes.c_ulong()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == process_id and user32.IsWindowVisible(hwnd):
            windows.append(hwnd)
        return True

    user32.EnumWindows(enum_proc(visit), 0)
    if not windows:
        return False
    result = ctypes.c_ulonglong()
    for hwnd in windows:
        user32.SendMessageTimeoutW.argtypes = [
            ctypes.c_void_p, ctypes.c_uint, ctypes.c_void_p, ctypes.c_void_p,
            ctypes.c_uint, ctypes.c_uint, ctypes.POINTER(ctypes.c_ulonglong),
        ]
        if user32.SendMessageTimeoutW(
            hwnd, WM_NULL, 0, 0, SMTO_ABORTIFHUNG, timeout_ms, ctypes.byref(result)
        ):
            return True
    return False


def running_devenv_pids() -> list[int]:
    try:
        completed = subprocess.run(
            ["tasklist", "/FI", "IMAGENAME eq devenv.exe", "/FO", "CSV", "/NH"],
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=30,
            check=False,
        )
    except (OSError, subprocess.SubprocessError):
        return []
    pids = []
    for line in completed.stdout.splitlines():
        fields = [field.strip('"') for field in line.split('","')]
        if len(fields) >= 2 and fields[0].lower() == "devenv.exe" and fields[1].isdigit():
            pids.append(int(fields[1]))
    return pids


async def main() -> None:
    existing = running_devenv_pids()
    if existing:
        raise AssertionError(
            f"Close every Visual Studio process before running this smoke test; found devenv PIDs {existing}"
        )

    # An unknown capability must be rejected before anything is launched.
    try:
        await vs_instance_tools.ensure_vs_instance(capabilities="debug.not.a.capability")
    except Exception as error:  # ToolError carries the structured payload
        assert "INVALID_ARGUMENT" in str(error), error
    else:
        raise AssertionError("An unknown capability name was accepted")

    # A hidden, long-lived native process: notepad.exe on Windows 11 is a stub that
    # hands off to the packaged app and exits, which would break the attach step.
    target = subprocess.Popen(
        ["cmd.exe", "/c", "ping -n 900 127.0.0.1"],
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
    )
    try:
        started = time.monotonic()
        payload = await vs_instance_tools.ensure_vs_instance(
            process_id=target.pid, timeout_seconds=LAUNCH_TIMEOUT_SECONDS
        )
        # The FastMCP tool wrapper returns the structured result under "result".
        result = payload.get("result", payload) if isinstance(payload, dict) else payload
        elapsed = time.monotonic() - started
        print(f"ensure_vs_instance returned status={result.get('status')} after {elapsed:.1f}s")
        for entry in result.get("diagnostics") or []:
            print(f"  diagnostic: {entry}")
        # A blocked Visual Studio (Start Page, or a modal error dialog) must be reported
        # instead of surfacing as a bare timeout.
        launched = result.get("processId")
        if isinstance(launched, int):
            dialogs = visible_dialogs(launched)
            if dialogs:
                print("  modal dialogs: " + "; ".join(dialogs))
            responsive = ui_thread_responsive(launched)
            print(f"  UI thread responsive: {responsive}")
            if not responsive:
                raise AssertionError("the launched Visual Studio is not pumping messages")
        assert elapsed < 90.0, f"launch took {elapsed:.1f}s; the Start Page stall is back"

        assert result.get("success"), result
        assert result.get("status") == "launched", result
        assert result.get("launched") is True, result
        assert "debug.session.write" in (result.get("grantedCapabilities") or []), result
        assert result.get("accessMode") == "full_access", result

        launched_pid = result.get("processId")
        assert launched_pid in running_devenv_pids(), (launched_pid, running_devenv_pids())

        registration = (
            Path(os.environ["LOCALAPPDATA"]) / "AIDebugger" / "instances" / f"AIDebuggerVS_{launched_pid}.json"
        )
        assert registration.is_file(), registration

        instances = await vs_bridge.discover_instances()
        reachable = [item for item in instances if item.process_id == launched_pid and item.connection_status == "ok"]
        assert reachable, [item.to_dict() for item in instances]

        assert result.get("attached") is True, result.get("attachResult")
        activation = (result.get("attachResult") or {}).get("debuggerActivationStatus")
        assert activation == "active", result.get("attachResult")

        status = await vs_bridge.call_pipe_exact(
            vs_bridge.instance_target(reachable[0].target_identity()),
            "get_debug_status",
            {},
            timeout=15.0,
        )
        assert status.get("isDebugging") is True, status

        closed = await vs_instance_tools.close_vs_instance(process_id=launched_pid, force=True)
        closed_result = closed.get("result", closed) if isinstance(closed, dict) else closed
        assert closed_result.get("success") and closed_result.get("closed"), closed_result
        assert launched_pid not in running_devenv_pids(), running_devenv_pids()
        print("LIVE_VS_LAUNCH_SMOKE_OK")
    finally:
        target.terminate()
        for pid in running_devenv_pids():
            print(f"WARNING: Visual Studio PID {pid} is still running")


if __name__ == "__main__":
    asyncio.run(main())
