"""Opt-in debugging of our own tiny native fixture, no external target execution."""
import json
from pathlib import Path
import subprocess
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from ida_backend.sessions import IdaSessions
from ida_backend.transport import tool_value
from run_ida_smoke import wait_ready, wait_exit


def main():
    root = Path(__file__).resolve().parents[2] / ".tmp" / "ida-debug-smoke" / str(time.time_ns())
    root.mkdir(parents=True)
    candidates = sorted(Path("C:/Program Files/Microsoft Visual Studio").glob("*/*/Common7/Tools/VsDevCmd.bat"), reverse=True)
    if not candidates:
        raise RuntimeError("Visual Studio C++ tools required to build the controlled fixture")
    source = Path(__file__).with_name("debuggee.cpp")
    target = root / "ida-debuggee.exe"
    command = f'"{candidates[0]}" -arch=x64 -host_arch=x64 && cl /nologo /Od /Zi /Fe:"{target}" "{source}" /link /DEBUG'
    build_script = root / "build.cmd"
    build_script.write_text("@echo off\ncall " + command + "\n", encoding="utf-8")
    built = subprocess.run(["cmd.exe", "/d", "/c", str(build_script)], cwd=root, capture_output=True)
    (root / "build.log").write_bytes(built.stdout + built.stderr)
    if built.returncode:
        raise RuntimeError("Fixture build failed: " + str(root / "build.log"))
    manager = IdaSessions(root=root / "sessions")
    sid = manager.open(str(target))["sessionId"]
    evidence = {}
    try:
        evidence["identity"] = wait_ready(manager, sid)
        evidence["configure"] = tool_value(manager.call(sid, "debugger_configure", {"executable": str(target)}))
        evidence["start"] = tool_value(manager.call(sid, "dbg_start", {}))
        assert evidence["start"].get("running"), evidence["start"]
        for _ in range(50):
            status = tool_value(manager.call(sid, "dbg_status", {}))
            if status.get("suspended"):
                break
            time.sleep(0.1)
        assert status.get("suspended"), status
        evidence["status"] = status
        evidence["registers"] = tool_value(manager.call(sid, "dbg_regs", {}))
        evidence["events"] = tool_value(manager.call(sid, "debugger_events", {}))
        # Closing an active debugger must fail while preserving the target.
        try:
            manager.close(sid)
            raise AssertionError("Active debug session was closed")
        except RuntimeError as error:
            evidence["activeCloseRejected"] = str(error)
        evidence["detach"] = tool_value(manager.call(sid, "debugger_detach", {}))
        for _ in range(50):
            status = tool_value(manager.call(sid, "dbg_status", {}))
            if not status.get("running"):
                break
            time.sleep(0.1)
        assert not status.get("running"), status
        manager.close(sid)
        wait_exit(manager, sid)
        evidence["passed"] = True
    finally:
        manager.shutdown()
        (root / "evidence.json").write_text(json.dumps(evidence, indent=2), encoding="utf-8")
        print(str(root / "evidence.json"), flush=True)


if __name__ == "__main__":
    main()
