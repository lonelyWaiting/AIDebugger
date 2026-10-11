"""Opt-in real IDA/stdio contention benchmark, using isolated system-DLL copies.

python tests/ida_integration/run_ida_performance.py --compare-ref 5e58ec7 --report .tmp/performance/ida.json
No debugger attachment or binary patching. A one-second py_eval sleep models an
occupied IDA owner thread. Compare status/other-session latency under eight
overlapping same-session read-only calls, keeping target identity checks intact.
"""
import argparse
import asyncio
import json
import os
from pathlib import Path
import subprocess
import statistics
import sys
import time

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
from mcp_server import bootstrap
bootstrap.ensure_deps()
from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client
from ida_backend.sessions import file_hash
from tools.install_aidebugger import payload_files


def value(result):
    assert not result.isError, result
    payload = result.structuredContent or json.loads(result.content[0].text)
    assert not payload.get("isError"), payload  # Preserve/check the upstream tool envelope too.
    return payload


async def probe(server, directory):
    directory.mkdir(parents=True)
    marker = directory / "owner-started.txt"
    sources = [Path("C:/Windows/System32/version.dll"), Path("C:/Windows/System32/winmm.dll")]
    hashes = [file_hash(path) for path in sources]
    env = dict(os.environ, AIDB_IDA_WORK_ROOT=str(directory / "sessions"), PYTHONIOENCODING="utf-8")
    params = StdioServerParameters(command=sys.executable, args=[str(server)], env=env)
    evidence = {"server": str(server), "sourceHashes": hashes, "modelledOwnerOccupancySeconds": 1.0}
    sessions, pending = [], []
    async with stdio_client(params) as (read, write):
        async with ClientSession(read, write) as client:
            await client.initialize()
            try:
                for source in sources:
                    opened = value(await client.call_tool("ida_open", {"input_path": str(source)}))
                    sessions.append(opened["sessionId"])
                for sid in sessions:
                    deadline = time.monotonic() + 30
                    while time.monotonic() < deadline:
                        status = value(await client.call_tool("ida_session_status", {"session_id": sid}))
                        if status["state"] == "ready":
                            break
                        assert status["state"] not in {"exited", "closed"}, status
                        await asyncio.sleep(0.05)
                    assert status["state"] == "ready", status
                    tools = value(await client.call_tool("ida_list_tools", {"session_id": sid, "count": 100}))
                    assert {"py_eval", "server_health"} <= {tool["name"] for tool in tools["tools"]}
                owner = asyncio.create_task(client.call_tool("ida_call_tool", {
                    "session_id": sessions[0], "tool": "py_eval", "arguments": {"code":
                        "import time; from pathlib import Path; " +
                        f"Path({str(marker)!r}).write_text('started'); time.sleep(1.0)"}}))
                pending.append(owner)
                deadline = time.monotonic() + 5
                while not marker.is_file() and time.monotonic() < deadline:
                    assert not owner.done(), "The native owner task finished before measurement"
                    await asyncio.sleep(0.005)
                assert marker.is_file(), "Native IDA did not acknowledge owner-thread occupancy"

                async def timed_call(tool, arguments):
                    start = time.perf_counter()
                    result = await client.call_tool(tool, arguments)
                    return result, round((time.perf_counter() - start) * 1000, 3)
                crowd = [asyncio.create_task(timed_call("ida_call_tool", {
                    "session_id": sessions[0], "tool": "server_health", "arguments": {}})) for _ in range(8)]
                other = asyncio.create_task(timed_call("ida_call_tool", {
                    "session_id": sessions[1], "tool": "server_health", "arguments": {}}))
                listing = asyncio.create_task(timed_call("ida_list_sessions", {}))
                pending.extend([*crowd, other, listing])
                results = await asyncio.gather(*crowd, other, listing)
                rejected = sum(result.isError and "IDA_BUSY_NOT_STARTED" in str(result) for result, _ in results[:8])
                assert rejected in {0, 8}, results
                assert all(not result.isError or "IDA_BUSY_NOT_STARTED" in str(result) for result, _ in results[:8])
                value(results[8][0])
                assert len(value(results[9][0])["sessions"]) == 2
                value(await owner)
                evidence.update(busyCallsRejectedBeforeStart=rejected,
                    busyCallLatencyMs=[elapsed for _, elapsed in results[:8]],
                    otherSessionHealthLatencyMs=results[8][1], listSessionsLatencyMs=results[9][1])
                assert value(await client.call_tool("ida_session_status", {"session_id": sessions[0]}))["state"] == "ready"
                assert hashes == [file_hash(path) for path in sources]
                evidence["sourceBytesPreserved"] = True
            finally:
                if pending:
                    await asyncio.gather(*pending, return_exceptions=True)
                for sid in sessions:
                    value(await client.call_tool("ida_close", {"session_id": sid}))
                    deadline = time.monotonic() + 10
                    while time.monotonic() < deadline:
                        status = value(await client.call_tool("ida_session_status", {"session_id": sid}))
                        if status["state"] in {"closed", "exited"}:
                            break
                        await asyncio.sleep(0.05)
                    assert status["state"] in {"closed", "exited"}, status
                evidence["ownedWorkersExited"] = True
    return evidence


async def main(args):
    directory = ROOT / ".tmp/performance" / str(time.time_ns())
    evidence = {"current": [], "baseline": [], "baselineRef": args.compare_ref}
    servers = {"current": ROOT / "mcp_server/ida_server.py"}
    if args.compare_ref:
        baseline = directory / "baseline-runtime"
        for relative, data in payload_files("ida").items():
            if relative.startswith("skills/"):
                continue
            target = baseline / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
        (baseline / "ida_backend/sessions.py").write_bytes(subprocess.check_output(
            ["git", "show", args.compare_ref + ":ida_backend/sessions.py"], cwd=ROOT))
        servers["baseline"] = baseline / "mcp_server/ida_server.py"
    for trial in range(args.trials):
        order = list(servers) if trial % 2 else list(reversed(servers))
        for name in order:
            evidence[name].append(await probe(servers[name], directory / f"{name}-{trial}"))
    assert all(result["busyCallsRejectedBeforeStart"] == 8 for result in evidence["current"])
    evidence["summary"] = {name: {"trials": len(results),
        "medianListSessionsLatencyMs": statistics.median(result["listSessionsLatencyMs"] for result in results),
        "medianOtherSessionHealthLatencyMs": statistics.median(result["otherSessionHealthLatencyMs"] for result in results),
        "busyCallsRejectedBeforeStart": sum(result["busyCallsRejectedBeforeStart"] for result in results)}
        for name, results in evidence.items() if name in servers}
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(evidence, indent=2), encoding="utf-8")
    print(json.dumps({"summary": evidence["summary"], "report": str(args.report)}, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--compare-ref")
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--trials", type=int, choices=range(1, 6), default=3)
    asyncio.run(main(parser.parse_args()))
