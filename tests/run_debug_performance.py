"""Opt-in fresh stdio/native benchmarks. Only executes our own IDA fixture.

Kernel measurements use an immutable dump: they do not prove live stepping.
Latency includes identity preflight, transport, and result/stop observation.
"""
import argparse
import ast
import hashlib
import json
import math
from pathlib import Path
import re
import statistics
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / "tests/kernel_integration"))
from run_kernel_smoke import Client
from ida_backend.transport import tool_value


def summary(values):
    ordered = sorted(values)
    return {"samples": len(values), "medianMs": round(statistics.median(values), 3),
            "p95Ms": round(ordered[math.ceil(len(values) * .95) - 1], 3),
            "minMs": round(ordered[0], 3), "maxMs": round(ordered[-1], 3)}


def until(operation, predicate, timeout=20, interval=.005):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        value = operation()
        if predicate(value):
            return value
        time.sleep(interval)
    raise TimeoutError("Fresh operation completion was not observed")


def build_fixture(directory):
    candidates = sorted(Path("C:/Program Files/Microsoft Visual Studio").glob(
        "*/*/Common7/Tools/VsDevCmd.bat"), reverse=True)
    if not candidates:
        raise RuntimeError("Visual Studio C++ tools required")
    target = directory / "debug-performance.exe"
    source = ROOT / "tests/ida_integration/debug_performance.cpp"
    script = directory / "build.cmd"
    script.write_text(f'@echo off\ncall "{candidates[0]}" -arch=x64 -host_arch=x64 && '
                      f'cl /nologo /Od /Zi /Fe:"{target}" "{source}" /link /DEBUG\n', encoding="utf-8")
    built = subprocess.run(["cmd.exe", "/d", "/c", str(script)], cwd=directory, capture_output=True)
    (directory / "build.log").write_bytes(built.stdout + built.stderr)
    if built.returncode:
        raise RuntimeError("Fixture build failed: " + str(directory / "build.log"))
    return target


def ida_benchmark(directory, count, server=None):
    target = build_fixture(directory)
    original = hashlib.sha256(target.read_bytes()).hexdigest()
    server = server or ROOT / "mcp_server/ida_server.py"
    client = Client(server)
    report = {"boundary": "owned native fixture via fresh source MCP stdio", "server": str(server), "operations": {}}
    sid = None
    active = False
    def call(tool, arguments=None):
        value = tool_value(client.call("ida_call_tool", session_id=sid, tool=tool, arguments=arguments or {}))
        if isinstance(value, dict) and isinstance(value.get("result"), list):
            return value["result"]
        return value
    def measure(name, operation, validate):
        cold_start = time.perf_counter()
        validate(operation())
        cold = (time.perf_counter() - cold_start) * 1000
        values = []
        for _ in range(count):
            start = time.perf_counter()
            result = operation()
            elapsed = (time.perf_counter() - start) * 1000
            validate(result)
            values.append(elapsed)
        report["operations"][name] = {**summary(values), "firstMs": round(cold, 3), "rawMs": values}
    def registers(value):
        assert value["thread_id"] > 0 and value["registers"], value
    try:
        sid = client.call("ida_open", input_path=str(target))["sessionId"]
        report["identity"] = until(lambda: client.call("ida_session_status", session_id=sid),
                                   lambda s: s["state"] == "ready", timeout=60, interval=.05)
        tools = []
        offset = 0
        while True:
            page = client.call("ida_list_tools", session_id=sid, offset=offset, count=100)
            tools.extend(page["tools"])
            offset += len(page["tools"])
            if offset >= page["total"]:
                break
        report["schemas"] = [t for t in tools if t["name"] in {
            "dbg_step_into", "dbg_step_over", "dbg_regs", "dbg_regs_all", "dbg_regs_named",
            "dbg_stacktrace", "dbg_read", "debugger_context"}]
        call("debugger_configure", {"executable": str(target)})
        started = call("dbg_start")
        active = True
        assert started.get("running"), started
        until(lambda: call("dbg_status"), lambda s: s.get("suspended"))
        probe = call("py_eval", {"code": "import ida_name; hex(ida_name.get_name_ea(0xffffffffffffffff, 'performance_probe'))"})
        address = hex(int(probe["result"].strip("'\""), 16))
        assert address != "0xffffffffffffffff", probe
        call("dbg_run_to", {"addr": address})
        status = until(lambda: call("dbg_status"), lambda s: s.get("suspended") and int(s.get("ip", "0"), 16) == int(address, 16))
        regs = call("dbg_regs")
        stack_pointer = next(r["value"] for r in regs["registers"] if r["name"] == "RSP")
        measure("status", lambda: call("dbg_status"), lambda v: v["suspended"] or (_ for _ in ()).throw(AssertionError(v)))
        measure("registers_current", lambda: call("dbg_regs"), registers)
        measure("registers_named", lambda: call("dbg_regs_named", {"register_names": "RIP,RSP,RAX"}), registers)
        measure("registers_all_threads", lambda: call("dbg_regs_all"), lambda v: [registers(t) for t in v] if v else (_ for _ in ()).throw(AssertionError(v)))
        measure("stack", lambda: call("dbg_stacktrace"), lambda v: v or (_ for _ in ()).throw(AssertionError(v)))
        def memory(v):
            assert len(v) == 1 and not v[0].get("error") and v[0]["size"] == 64, v
        measure("memory_64", lambda: call("dbg_read", {"regions": {"addr": stack_pointer, "size": 64}}), memory)
        if any(t["name"] == "debugger_context" for t in tools):
            def context(v):
                registers(v["registers"])
                assert v["snapshotStable"] and v["stackStatus"] == "available" and v["threads"], v
            def separate_context():
                return {"status": call("dbg_status"), "registers": call("dbg_regs"),
                        "stack": call("dbg_stacktrace")}
            # Pair and alternate order on the same native pause to reduce
            # scheduling/cache bias. The separate path returns fewer fields:
            # the bundle additionally supplies thread IDs and consistency checks.
            paths = {"context_separate": (separate_context, lambda v: registers(v["registers"])),
                     "context_bundle": (lambda: call("debugger_context"), context)}
            timings = {name: [] for name in paths}
            for operation, validate in paths.values():
                validate(operation())
            for sample in range(count):
                for name in list(paths)[::1 if sample % 2 == 0 else -1]:
                    operation, validate = paths[name]
                    start = time.perf_counter()
                    result = operation()
                    timings[name].append((time.perf_counter() - start) * 1000)
                    validate(result)
            for name, values in timings.items():
                report["operations"][name] = {**summary(values), "rawMs": values, "pairedAlternatingOrder": True}
            def bounded_context(value):
                context(value)
                assert len(value["stack"]) == 1 and value["stackTruncated"], value
            measure("context_frames_1", lambda: call("debugger_context", {"max_frames": 1}), bounded_context)
            # Measure native helper work separately from transport/scheduling.
            # This instrumentation is confined to our owned fixture worker and
            # restored in finally; it never changes installed runtime settings.
            code = '''import inspect, time, ida_dbg, ida_idd, ida_name
from ida_backend import debug_context as dc
from ida_pro_mcp.ida_mcp import api_debug
samples = []
counter = [0]
original = ida_dbg.get_module_info
def counted(*args, _counter=counter, _original=original):
    _counter[0] += 1
    return _original(*args)
ida_dbg.get_module_info = counted
try:
    for _ in range(30):
        start = time.perf_counter()
        if 'idd' in inspect.signature(dc.collect_context).parameters:
            result = dc.collect_context(ida_dbg, api_debug, ida_idd, ida_name, max_frames=1)
        else:
            result = dc.collect_context(ida_dbg, api_debug, max_frames=1)
        assert result['stack'][0]['module'] != '<error>', result
        samples.append((time.perf_counter() - start) * 1000)
finally:
    ida_dbg.get_module_info = original
{'rawMs': samples, 'moduleLookups': counter[0], 'stackTruncated': result['stackTruncated'], 'frames': len(result['stack'])}
'''
            profiling = ast.literal_eval(call("py_eval", {"code": code})["result"])
            assert profiling["frames"] == 1 and profiling["stackTruncated"], profiling
            assert profiling["moduleLookups"] >= 30, profiling
            report["boundedHelperProfile"] = {**profiling, **summary(profiling["rawMs"])}
        for action in ("dbg_step_into", "dbg_step_over"):
            acknowledgements, completed, stops = [], [], []
            for _ in range(count):
                before = call("dbg_status")
                start = time.perf_counter()
                ack = call(action)
                acknowledgements.append((time.perf_counter() - start) * 1000)
                assert ack.get("continued"), ack
                after = until(lambda: call("dbg_status"), lambda s: s.get("suspended") and s.get("ip") != before["ip"])
                completed.append((time.perf_counter() - start) * 1000)
                stops.append({"before": before["ip"], "after": after["ip"]})
            report["operations"][action] = {"ack": summary(acknowledgements), "freshStop": summary(completed),
                                             "ackRawMs": acknowledgements, "stopRawMs": completed, "stops": stops}
        report["events"] = call("debugger_events")
        # This process belongs exclusively to the fixture; explicitly finish it.
        call("dbg_continue")
        until(lambda: call("dbg_status"), lambda s: s["state"] == "not_running")
        active = False
        client.call("ida_close", session_id=sid)
        until(lambda: client.call("ida_session_status", session_id=sid), lambda s: s["state"] == "closed")
        assert hashlib.sha256(target.read_bytes()).hexdigest() == original
        report["fixtureHashPreserved"] = True
        report["passed"] = True
    finally:
        if active:
            # No external PID is ever attached by this harness.
            call("dbg_exit")
            until(lambda: call("dbg_status"), lambda s: s["state"] == "not_running")
            client.call("ida_close", session_id=sid)
        client.close()
        (directory / "ida-evidence.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    return report


def kernel_benchmark(dump, count):
    original = hashlib.sha256(dump.read_bytes()).hexdigest()
    client = Client(ROOT / "mcp_server/kernel_server.py")
    report = {"boundary": "read-only native kernel dump via fresh source MCP stdio; no live stepping", "operations": {}}
    sid = None
    def wait(receipt, interval, wait_ms=0):
        job = until(lambda: client.call("kernel_job_status", session_id=sid, job_id=receipt["jobId"], count=16384, wait_ms=wait_ms),
                    lambda j: j["state"] != "running", timeout=60, interval=interval)
        assert job["state"] == "complete", job
        return job["result"]
    try:
        receipt = client.call("kernel_open_dump", dump_path=str(dump.resolve()))
        sid = receipt["sessionId"]
        report["identity"] = wait(receipt, .005)
        regs = wait(client.call("kernel_query", session_id=sid, query="registers"), .005)
        rsp = re.search(r"\brsp=([0-9a-f`]+)", regs["output"], re.I)
        assert rsp, regs
        address = rsp[1].replace("`", "")
        for interval, wait_ms in ((.05, 0), (.005, 0), (.005, 10)):
            for operation in ("registers", "stack", "threads", "memory_64"):
                acks, results = [], []
                for _ in range(count):
                    start = time.perf_counter()
                    if operation == "memory_64":
                        receipt = client.call("kernel_read_memory", session_id=sid, address=address, size=64)
                    else:
                        receipt = client.call("kernel_query", session_id=sid, query=operation)
                    acks.append((time.perf_counter() - start) * 1000)
                    value = wait(receipt, interval, wait_ms)
                    results.append((time.perf_counter() - start) * 1000)
                    assert value["state"] == "paused" and value["snapshotStable"], value
                    if operation == "memory_64":
                        assert value["bytesRead"] == 64 and not value["partial"], value
                    else:
                        if not value["output"]:
                            report["operations"][operation + "_poll_" + str(int(interval * 1000)) + "_wait_" + str(wait_ms)] = {
                                "status": "unavailable", "reason": "Native extension produced no evidence", "lastEvidence": value}
                            break
                else:
                    key = operation + "_poll_" + str(int(interval * 1000)) + "_wait_" + str(wait_ms)
                    report["operations"][key] = {"ack": summary(acks), "result": summary(results),
                                                   "ackRawMs": acks, "resultRawMs": results,
                                                   "lastEvidence": value}
                    continue
        closed = wait(client.call("kernel_close", session_id=sid), .005)
        assert closed["state"] == "closed" and not closed["targetResumed"], closed
        assert hashlib.sha256(dump.read_bytes()).hexdigest() == original
        report["dumpHashPreserved"] = True
        report["liveKernelStepValidated"] = False
        report["passed"] = True
    finally:
        client.close()
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--backend", choices=["ida", "kernel", "all"], default="all")
    parser.add_argument("--dump", type=Path)
    parser.add_argument("--ida-server", type=Path, help="Optional frozen source/deployed entrypoint for A/B validation")
    parser.add_argument("--samples", type=int, default=20)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    if not 3 <= args.samples <= 100:
        parser.error("--samples must be 3..100")
    if args.backend in {"kernel", "all"} and not args.dump:
        parser.error("--dump required for kernel measurements")
    directory = ROOT / ".tmp/performance" / str(time.time_ns())
    directory.mkdir(parents=True)
    report = {"samples": args.samples, "directory": str(directory), "pollIntervalMs": 5}
    try:
        if args.backend in {"ida", "all"}:
            report["ida"] = ida_benchmark(directory, args.samples, args.ida_server)
        if args.backend in {"kernel", "all"}:
            report["kernel"] = kernel_benchmark(args.dump, args.samples)
    finally:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({b: {k: {n: v for n, v in m.items() if n not in {"rawMs", "ackRawMs", "stopRawMs", "resultRawMs", "stops", "lastEvidence"}}
                         for k, m in report[b]["operations"].items()} for b in ("ida", "kernel") if b in report}, indent=2))


if __name__ == "__main__":
    main()
