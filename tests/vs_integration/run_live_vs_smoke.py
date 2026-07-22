"""Live VS Experimental Instance smoke test for the authenticated AIDB/2 path."""

from __future__ import annotations

import asyncio
import os
from pathlib import Path
import sys
import time

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))

from mcp_server import bootstrap

bootstrap.ensure_deps()
from mcp_server import vs_bridge


PROGRAM = ROOT / "tests" / "vs_integration" / "Debuggee" / "Program.cs"


async def wait_for_instance(process_id: int, timeout: float = 60.0) -> str:
    deadline = time.monotonic() + timeout
    last = []
    while time.monotonic() < deadline:
        instances = await vs_bridge.discover_instances()
        last = [instance.to_dict() for instance in instances]
        for instance in instances:
            if instance.process_id == process_id and instance.connection_status == "ok":
                return instance.pipe_name
        await asyncio.sleep(0.5)
    raise AssertionError(f"AIDebugger pipe for devenv PID {process_id} did not appear: {last}")


async def wait_for_pause(pipe_name: str, timeout: float = 45.0) -> dict:
    deadline = time.monotonic() + timeout
    last = None
    while time.monotonic() < deadline:
        last = await vs_bridge.call_pipe(pipe_name, "get_debug_status", timeout=3.0)
        if last.get("isPaused"):
            return last
        await asyncio.sleep(0.25)
    raise AssertionError(f"Debuggee did not reach breakpoint: {last}")


async def main() -> None:
    process_id = int(os.environ["AIDB_TEST_VS_PID"])
    breakpoint_line = next(
        index for index, line in enumerate(PROGRAM.read_text(encoding="utf-8").splitlines(), 1)
        if "BREAKPOINT_MARKER" in line
    )
    pipe_name = await wait_for_instance(process_id)

    breakpoint = await vs_bridge.call_pipe(
        pipe_name,
        "set_breakpoint",
        {"filePath": str(PROGRAM), "lineNumber": breakpoint_line},
        timeout=10.0,
    )
    assert breakpoint.get("success"), breakpoint

    started = await vs_bridge.call_pipe(pipe_name, "start_debugging", timeout=20.0)
    assert started.get("success"), started
    status = await wait_for_pause(pipe_name)
    assert status.get("debugSessionGeneration", 0) > 0, status
    assert status.get("contextEpoch", 0) > 0, status

    callstack = await vs_bridge.call_pipe(pipe_name, "get_callstack", timeout=10.0)
    frames = callstack.get("frames") or []
    assert frames, callstack
    top_frame = frames[0]
    assert top_frame.get("sourceLocationStatus") == "available", top_frame
    assert os.path.normcase(os.path.realpath(top_frame.get("filePath") or "")) == os.path.normcase(os.path.realpath(PROGRAM)), top_frame
    assert top_frame.get("lineNumber") == breakpoint_line, (breakpoint_line, top_frame)
    assert top_frame.get("lineNumberReliable") is True, top_frame
    context = frames[0].get("context") or {}
    for field in ("debugSessionGeneration", "breakSequence", "contextEpoch", "processId", "threadId", "frameIndex", "frameId"):
        assert context.get(field) not in (None, ""), (field, context)

    locals_result = await vs_bridge.call_pipe(
        pipe_name,
        "get_locals",
        {
            "depth": 1,
            "debugSessionGeneration": context["debugSessionGeneration"],
            "breakSequence": context["breakSequence"],
            "contextEpoch": context["contextEpoch"],
            "processId": context["processId"],
            "threadId": context["threadId"],
            "frameIndex": context["frameIndex"],
            "frameId": context["frameId"],
        },
        timeout=10.0,
    )
    assert any(variable.get("name") == "seed" for variable in locals_result.get("variables", [])), locals_result

    modules = await vs_bridge.call_pipe(pipe_name, "get_modules", timeout=15.0)
    assert modules.get("totalCount", 0) > 0, modules

    stopped = await vs_bridge.call_pipe(pipe_name, "stop", timeout=10.0)
    assert stopped.get("success"), stopped
    print("LIVE_VS_SMOKE_OK")


if __name__ == "__main__":
    asyncio.run(main())
