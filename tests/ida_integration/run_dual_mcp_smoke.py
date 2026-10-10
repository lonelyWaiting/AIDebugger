"""Fresh SDK clients against installed IDA and VS stdio registrations."""
import asyncio
import argparse
from contextlib import AsyncExitStack
import json
import os
from pathlib import Path
import sys
import subprocess
import time

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
from mcp_server import bootstrap
bootstrap.ensure_deps()
from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client


def value(result):
    if result.isError:
        raise RuntimeError(str(result))
    return result.structuredContent or json.loads(result.content[0].text)


async def main(launch_vs=False, debug_vs=False):
    config = json.loads((Path.home() / ".claude.json").read_text(encoding="utf-8-sig"))["mcpServers"]
    clients = {}
    evidence = {}
    async with AsyncExitStack() as stack:
        for name in ("ida", "AIDebuggerVS"):
            registration = config[name]
            params = StdioServerParameters(command=registration["command"], args=registration.get("args", []),
                                           env={**os.environ, **registration.get("env", {}), "AIDB_IDA_WORK_ROOT": str(ROOT / ".tmp/dual-mcp-sessions")})
            read, write = await stack.enter_async_context(stdio_client(params))
            client = await stack.enter_async_context(ClientSession(read, write))
            initialized = await client.initialize()
            evidence[name] = {"serverInfo": initialized.serverInfo.model_dump(),
                              "toolCount": len((await client.list_tools()).tools)}
            clients[name] = client
        ida = clients["ida"]
        owned_vs = None
        target = None
        if launch_vs:
            launched = value(await clients["AIDebuggerVS"].call_tool("ensure_vs_instance", {
                "solution_path": str(ROOT / "tests/vs_integration/AIDebugger.LiveSmoke.sln"),
                "reuse_existing": False, "attach": False, "timeout_seconds": 40,
            }))["result"]
            evidence["vsLaunch"] = launched
            if launched.get("launched"):
                owned_vs = launched.get("processId")
            if not launched.get("success"):
                if owned_vs:
                    await clients["AIDebuggerVS"].call_tool("close_vs_instance", {"process_id": owned_vs})
                raise RuntimeError(json.dumps(launched))
        if debug_vs:
            targets = list((ROOT / ".tmp/ida-debug-smoke").glob("*/ida-debuggee.exe"))
            executable = max(targets, key=lambda p: p.stat().st_mtime)
            target = subprocess.Popen([str(executable)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            evidence["vsAttach"] = value(await clients["AIDebuggerVS"].call_tool("attach_process", {"process_id": target.pid}))
            evidence["vsBreak"] = value(await clients["AIDebuggerVS"].call_tool("debug_break", {}))
        opened = value(await ida.call_tool("ida_open", {"input_path": "C:/Windows/System32/version.dll"}))
        sid = opened["sessionId"]
        try:
            for _ in range(150):
                status = value(await ida.call_tool("ida_session_status", {"session_id": sid}))
                if status["state"] == "ready":
                    break
                if status["state"] == "exited":
                    raise RuntimeError(str(status))
                await asyncio.sleep(0.2)
            assert status["state"] == "ready", status
            evidence["idaIdentity"] = status
            # Both servers receive work at the same time. No selection or debugger
            # mutation is sent to a pre-existing user-started VS instance.
            vs_result, ida_result = await asyncio.gather(
                clients["AIDebuggerVS"].call_tool("list_vs_instances", {}),
                ida.call_tool("ida_call_tool", {"session_id": sid, "tool": "server_health", "arguments": {}}),
            )
            evidence["vsInstances"] = value(vs_result)
            evidence["idaHealth"] = value(ida_result)
            if launch_vs:
                assert evidence["vsInstances"]["result"]["reachableCount"] >= 1
                evidence["liveVsStatus"] = value(await clients["AIDebuggerVS"].call_tool("get_debug_status", {}))
            if debug_vs:
                stack_result, decompile_result = await asyncio.gather(
                    clients["AIDebuggerVS"].call_tool("get_callstack", {}),
                    ida.call_tool("ida_call_tool", {"session_id": sid, "tool": "decompile", "arguments": {"addr": "DllMain"}}),
                )
                evidence["liveVsCallstack"] = value(stack_result)
                evidence["idaDllMainDecompiledWhileVsPaused"] = value(decompile_result)
                assert not evidence["idaDllMainDecompiledWhileVsPaused"].get("isError")
                status = evidence["liveVsStatus"]["result"]
                assert status.get("isPaused"), status
                await ida.call_tool("ida_call_tool", {"session_id": sid, "tool": "debugger_configure", "arguments": {}})
                rejected = value(await ida.call_tool("ida_call_tool", {"session_id": sid, "tool": "debugger_attach", "arguments": {"pid": target.pid}}))
                assert rejected.get("isError"), rejected
                evidence["samePidIdaAttachRejected"] = rejected
            evidence["passed"] = True
        finally:
            await ida.call_tool("ida_close", {"session_id": sid})
            for _ in range(100):
                status = value(await ida.call_tool("ida_session_status", {"session_id": sid}))
                if status["state"] in ("closed", "exited"):
                    break
                await asyncio.sleep(0.1)
            assert status["state"] == "closed", status
            if target:
                evidence["vsDetach"] = value(await clients["AIDebuggerVS"].call_tool("detach_process", {}))
                if target.poll() is None:
                    target.terminate()
                    target.wait(timeout=5)
            if owned_vs:
                evidence["vsClose"] = value(await clients["AIDebuggerVS"].call_tool("close_vs_instance", {"process_id": owned_vs}))
    directory = ROOT / ".tmp/dual-mcp-smoke"
    directory.mkdir(parents=True, exist_ok=True)
    path = directory / (str(time.time_ns()) + ".json")
    path.write_text(json.dumps(evidence, ensure_ascii=False, indent=2), encoding="utf-8")
    print(str(path))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--launch-vs", action="store_true")
    parser.add_argument("--debug-vs", action="store_true")
    args = parser.parse_args()
    asyncio.run(main(args.launch_vs or args.debug_vs, args.debug_vs))
