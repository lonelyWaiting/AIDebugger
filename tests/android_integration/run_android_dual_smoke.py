"""Emulator-only end-to-end regression for the owned Java/JNI APK."""
import argparse
import os
from pathlib import Path
import re
import subprocess
import sys
import time

from run_android_smoke import Client


PACKAGE = "com.aidebugger.dualprobe"
CLASS = PACKAGE + ".ProbeActivity"
NATIVE = "Java_com_aidebugger_dualprobe_ProbeActivity_nativeTick"
MODULE = "libdualprobe.so"
FIXTURE = Path(__file__).with_name("fixture")


def signature(stop):
    return stop["status"]["location"]["method"]


class Probe:
    def __init__(self, client, args):
        self.client, self.args = client, args
        self.session = None
        self.launch_times = []

    def adb(self, *words):
        return subprocess.check_output([self.args.adb, "-s", self.args.serial, *words],
                                       text=True, timeout=15).strip()

    def start(self, mode, java_breakpoints=(), native_breakpoints=()):
        self.adb("shell", "am", "force-stop", PACKAGE)
        now = time.monotonic()
        receipt = self.client.call("launch", serial=self.args.serial, package=PACKAGE,
                                   activity=".ProbeActivity", mode=mode,
                                   symbol_dirs=[str(self.args.fixture / "lib/x86_64")],
                                   java_breakpoints=list(java_breakpoints),
                                   native_breakpoints=list(native_breakpoints))
        self.session = receipt["sessionId"]
        result = self.client.wait(receipt)
        self.launch_times.append((mode, round(time.monotonic() - now, 2)))
        return result

    def call(self, name, **args):
        return self.client.wait(self.client.call(name, session_id=self.session, **args))

    def java(self, action, thread_id=""):
        return self.call("java_control", action=action, thread_id=thread_id, wait_ms=10_000)

    def native(self, action, wait_ms=10_000):
        return self.call("run_until_stop", action=action, wait_ms=wait_ms)

    def native_break(self, function):
        for _ in range(4):
            stop = self.native("continue")
            if stop.get("stack") and stop["stack"][0]["function"] == function:
                return stop
            assert stop["status"].get("stopDescription") in ("signal SIGSTOP", "signal SIGCONT"), stop
        raise AssertionError(f"Attach signals repeatedly interrupted {function}")

    def reject_write(self, address, message, **args):
        receipt = self.client.call("write_memory", session_id=self.session,
                                   address=address, data_hex="00ff007f", **args)
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            job = self.client.call("job_status", session_id=self.session, job_id=receipt["jobId"])
            if job["state"] != "pending":
                assert job["state"] == "failed" and message in job["error"], job
                return
            time.sleep(0.05)
        raise TimeoutError("Memory write rejection did not complete")

    def memory_write(self, address, stop_id):
        captured = self.call("native_query", query="memory", address=address, count=4)
        original = captured["dataHex"]
        assert captured["bytesRead"] == 4 and original != "00ff007f", captured
        self.reject_write(address, "stop changed", expected_stop_id=stop_id + 1)
        invalid = self.call("write_memory", address="0x0", data_hex="ab", expected_stop_id=stop_id)
        assert invalid["bytesWritten"] == 0 and invalid["partial"] and invalid["verified"] is None, invalid
        result = self.call("write_memory", address=address, data_hex="00ff007f", expected_stop_id=stop_id)
        try:
            assert result["bytesWritten"] == 4 and result["bytesReadBack"] == 4 and result["requestedRangeVerified"], result
            read_back = self.call("native_query", query="memory", address=address, count=4)
            assert read_back["dataHex"] == "00ff007f", read_back
        finally:
            restored = self.call("write_memory", address=address, data_hex=original, expected_stop_id=stop_id)
            assert restored["bytesWritten"] == 4 and restored["verified"], restored

    def cancel_wait(self, stop_id):
        receipt = self.client.call("run_until_stop", session_id=self.session,
                                   after_stop_id=stop_id, wait_ms=30_000)
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            status = self.client.call("job_status", session_id=self.session, job_id=receipt["jobId"])
            if status.get("stage") == "waiting_for_stop":
                break
            assert status["state"] == "pending", status
            time.sleep(0.05)
        else:
            raise TimeoutError("Wait did not enter the server-side polling phase")
        cancel = self.client.call("cancel_job", session_id=self.session, job_id=receipt["jobId"])
        assert cancel["cancellationRequested"], cancel
        while time.monotonic() < deadline:
            status = self.client.call("job_status", session_id=self.session, job_id=receipt["jobId"])
            if status["state"] != "pending":
                assert status["state"] == "cancelled", status
                return
            time.sleep(0.05)
        raise TimeoutError("Cancelled wait did not finish")

    def detached(self, pid):
        assert self.call("detach")["state"] == "detached"
        assert str(pid) in self.adb("shell", "pidof", PACKAGE).split()
        forwards = self.adb("forward", "--list")
        assert "aidb-lldb-" not in forwards and f"jdwp:{pid}" not in forwards, forwards
        self.session = None

    def attach_running(self, original_pid):
        receipt = self.client.call("attach", serial=self.args.serial, package=PACKAGE,
                                   pid=original_pid, mode="dual",
                                   symbol_dirs=[str(self.args.fixture / "lib/x86_64")])
        self.session = receipt["sessionId"]
        attached = self.client.wait(receipt)
        assert attached["pid"] == original_pid and attached["systemSymbolsCacheHit"], attached
        self.call("control", action="continue")
        bp = self.call("java_breakpoint", class_name=CLASS, method="javaTick")
        assert bp["resolvedLocations"], bp
        pending = self.client.call("run_until_stop", session_id=self.session, wait_ms=6000)
        stopped = self.client.wait(pending)
        assert stopped["outcome"] == "java_stop" and stopped["stack"][0]["method"] == "javaTick", stopped
        self.detached(original_pid)
        print("Dual attach to the running app reused system symbols and preserved its PID", flush=True)

    def start_wait_for_close(self):
        pid, = self.adb("shell", "pidof", PACKAGE).split()
        reply = self.client.call("attach", serial=self.args.serial, package=PACKAGE, pid=int(pid),
                                 symbol_dirs=[str(self.args.fixture / "lib/x86_64")])
        self.session = reply["sessionId"]
        self.client.wait(reply)
        job = self.client.call("run_until_stop", session_id=self.session, wait_ms=60_000)
        deadline = time.monotonic() + 4
        while time.monotonic() < deadline:
            status = self.client.call("job_status", session_id=self.session, job_id=job["jobId"])
            if status.get("stage") == "waiting_for_stop":
                return pid
            assert status["state"] == "pending", status
            time.sleep(0.05)
        raise TimeoutError("Wait did not enter polling before MCP EOF")

    def java_only(self):
        lines = (FIXTURE / "ProbeActivity.java").read_text(encoding="utf-8").splitlines()
        obj_line = next(i for i, line in enumerate(lines, 1) if "JDWP object inspection stop" in line)
        launch = self.start("java", [
            {"class_name": CLASS, "method": "entry"},
            {"class_name": CLASS, "method": "inspectObjects", "line": obj_line},
        ])
        assert launch["java"]["state"] == "stopped", launch
        assert f"jdwp:{launch['pid']}" not in self.adb("forward", "--list")
        self.call("java_control", action="continue")
        time.sleep(2)
        entry = self.call("java_inspect", query="status")
        assert entry["reason"] == "breakpoint" and entry["location"]["method"] == "entry", entry
        thread = entry["threadId"]
        java_threads = self.call("java_inspect", query="threads")
        assert any(item["threadId"] == thread for item in java_threads["threads"]), java_threads
        over = self.java("step_over", thread)
        assert signature(over) == "entry" and over["status"]["reason"] == "step", over
        into = self.java("step_into", thread)
        assert signature(into) == "javaTick" and into["status"]["reason"] == "step", into
        out = self.java("step_out", thread)
        assert signature(out) == "entry" and out["status"]["reason"] == "step", out
        objects = self.java("continue")
        assert signature(objects) == "inspectObjects", objects
        variables = {item["name"]: item["value"] for item in objects["locals"]}
        payload = self.call("java_object", object_id=variables["payload"]["objectId"])
        assert {item["name"]: item["value"] for item in payload["fields"]} == {
            "label": "android-probe", "number": 35}, payload
        array = self.call("java_object", object_id=variables["data"]["objectId"], query="array")
        assert array["values"] == [35, 36], array
        self.call("java_exception_breakpoint", caught=True, uncaught=False)
        exception = self.java("continue")
        assert signature(exception) == "inspectObjects" and exception["status"]["caught"], exception
        self.call("java_exception_breakpoint", caught=False, uncaught=False)
        self.detached(launch["pid"])
        print("Java startup, idle event pump, three steps, object/array inspection and exception", flush=True)

    def dual(self):
        launch = self.start("dual", [
            {"class_name": CLASS, "method": "entry"},
            {"class_name": CLASS, "method": "afterNative"},
        ], [{"symbol": "JNI_OnLoad", "module": MODULE}, {"symbol": NATIVE, "module": MODULE}])
        status = self.call("java_inspect", query="status")
        assert status["blockedByNative"], status
        entry = self.call("java_control", action="continue", wait_ms=10_000, release_native_startup=True)
        assert signature(entry) == "entry" and entry["locals"][0]["value"] == 35, entry
        loader = self.java("continue")
        assert loader["outcome"] == "native_stop" and loader["stack"][0]["function"] == "JNI_OnLoad", loader
        native = self.native_break(NATIVE)
        assert native["snapshotStable"] and native["stack"][0]["function"] == NATIVE, native
        result = self.call("native_query", query="evaluate", expression="seed + 7")
        assert result["value"] == "43", result
        pc = native["stack"][0]["pc"]
        read = self.call("native_query", query="memory", address=pc, count=16)
        assert read["bytesRead"] == 16 and not read["partial"], read
        search = self.call("native_query", query="search_memory", address=pc, pattern_hex=read["dataHex"])
        assert pc in search["matches"], search
        assert self.call("native_query", query="resolve_address", address=pc)["function"] == NATIVE
        assert self.call("native_query", query="disassembly", count=5)["instructions"]
        assert self.call("native_query", query="registers", count=4)["groups"]
        bp = self.call("set_breakpoint", address=pc, condition="seed == 36")
        assert bp["resolvedLocations"] and bp["condition"] == "seed == 36", bp
        disabled = self.call("configure_breakpoint", breakpoint_id=bp["breakpointId"], enabled=False)
        assert not disabled["enabled"], disabled
        self.call("remove_breakpoint", breakpoint_id=bp["breakpointId"])
        returned = self.native("continue")
        assert returned["outcome"] == "java_stop" and returned["java"]["location"]["method"] == "afterNative", returned
        assert returned["stack"][0]["method"] == "afterNative" and returned["locals"][0]["value"] == 43, returned
        assert self.call("java_inspect", query="locals")["variables"][0]["value"] == 43
        self.detached(launch["pid"])
        print("Dual startup, Java → JNI_OnLoad → native function → Java return and native queries", flush=True)

    def native_only(self):
        launch = self.start("native", native_breakpoints=[{"symbol": NATIVE, "module": MODULE}])
        assert launch["systemSymbolsCacheHit"], launch
        stop = self.native_break(NATIVE)
        assert stop["stack"][0]["function"] == NATIVE, stop
        watched = self.call("native_query", query="evaluate", expression="(uintptr_t)&watched_value")["value"]
        address = hex(int(watched, 0))
        self.memory_write(address, stop["status"]["stopId"])
        watch = self.call("watchpoint", address=address)
        self.call("remove_breakpoint", breakpoint_id=launch["breakpoints"][0]["breakpointId"])
        result = self.native("continue")
        assert result["status"]["stopReason"] == 4, result
        self.call("watchpoint", remove_id=watch["watchpointId"])
        self.call("control", action="continue")
        self.reject_write(address, "must be paused")
        timeout = self.call("run_until_stop", wait_ms=200)
        assert timeout["outcome"] == "wait_timeout", timeout
        self.cancel_wait(result["status"]["stopId"])
        self.detached(launch["pid"])
        print("Native startup, memory write and restoration, hardware watchpoint, timeout and cancellation",
              flush=True)
        self.attach_running(launch["pid"])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", type=Path, required=True)
    parser.add_argument("--adb", type=Path, required=True)
    parser.add_argument("--serial", default="emulator-5588")
    parser.add_argument("--fixture", type=Path, required=True, help="Directory produced by build_dual_probe.py")
    args = parser.parse_args()
    if not re.fullmatch(r"emulator-[0-9]+", args.serial):
        parser.error("This probe only operates on an explicitly selected emulator")
    args.fixture = args.fixture.resolve()
    args.adb = args.adb.resolve()
    env = {key: value for key, value in os.environ.items()
           if not key.upper().startswith("ANDROID_")}
    env["PATH"] = os.defpath
    with subprocess.Popen([sys.executable, str(args.server.resolve())], env=env, stdin=subprocess.PIPE,
                          stdout=subprocess.PIPE, encoding="utf-8") as process:
        probe = Probe(Client(process), args)
        eof_pid = None
        try:
            probe.java_only()
            probe.dual()
            probe.native_only()
            print("Launch seconds", probe.launch_times, flush=True)
            eof_pid = probe.start_wait_for_close()
        finally:
            start = time.monotonic()
            process.stdin.close()
            try:
                process.wait(timeout=35)
                if eof_pid:
                    elapsed = time.monotonic() - start
                    assert process.returncode == 0 and elapsed < 10, elapsed
                    assert eof_pid in probe.adb("shell", "pidof", PACKAGE).split()
                    trace = probe.adb("shell", "run-as", PACKAGE, "cat", f"/proc/{eof_pid}/status")
                    assert re.search(r"^TracerPid:\s+0$", trace, re.MULTILINE), trace
                    assert "aidb-lldb-" not in probe.adb("forward", "--list")
                    print("MCP EOF cancelled a 60-second wait and detached in", round(elapsed, 2), "seconds",
                          flush=True)
            except subprocess.TimeoutExpired:
                process.kill()
                raise
            finally:
                probe.adb("shell", "am", "force-stop", PACKAGE)


if __name__ == "__main__":
    main()
