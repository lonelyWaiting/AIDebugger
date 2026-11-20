"""Opt-in smoke for the installed MCP and a running owned Android test app."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import time


class Client:
    def __init__(self, process):
        self.process = process
        self.request_id = 0

    def request(self, method, params):
        self.request_id += 1
        request = dict(jsonrpc="2.0", id=self.request_id, method=method, params=params)
        self.process.stdin.write(json.dumps(request) + "\n")
        self.process.stdin.flush()
        response = json.loads(self.process.stdout.readline())
        assert response["id"] == self.request_id, response
        return response["result"]

    def call(self, name, **arguments):
        result = self.request("tools/call", {"name": "android_" + name, "arguments": arguments})
        assert not result["isError"], result
        return result["structuredContent"]

    def wait(self, receipt):
        deadline = time.monotonic() + 75
        while time.monotonic() < deadline:
            job = self.call("job_status", session_id=receipt["sessionId"], job_id=receipt["jobId"])
            if job["state"] != "pending":
                assert job["state"] == "completed" and not job.get("cleanupErrors"), job
                return job["result"]
            time.sleep(0.1)
        raise TimeoutError(receipt)


def smoke(client, args):
    devices = client.call("list_devices")["devices"]
    assert any(d["serial"] == args.serial and d["state"] == "device" for d in devices), devices
    receipt = client.call("attach", serial=args.serial, package=args.package,
                          symbol_dirs=[str(args.symbols.resolve())])
    session_id = receipt["sessionId"]
    attached = client.wait(receipt)
    print("Attached", attached, flush=True)

    def run(name, **arguments):
        return client.wait(client.call(name, session_id=session_id, **arguments))

    def next_stop(previous, breakpoint=False):
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            status = run("inspect", query="status")
            if status["state"] == "stopped" and status["stopId"] > previous:
                if breakpoint:
                    assert status["stopReason"] == 3, status  # eStopReasonBreakpoint
                return status
            time.sleep(0.1)
        raise TimeoutError("Android process did not reach its next stop")

    try:
        threads = run("inspect", query="threads")
        assert threads["threads"], threads
        bp = run("set_breakpoint", symbol=args.symbol, module=args.module)
        assert bp["resolvedLocations"], bp
        run("control", action="continue")
        hit = next_stop(attached["stopId"], breakpoint=True)
        stack = run("inspect", query="stack", thread_id=hit["threadId"], limit=4)
        frame = stack["frames"][0]
        assert args.symbol in frame["function"] and frame["line"], stack
        values = run("inspect", query="locals", thread_id=hit["threadId"])
        assert any(v["name"] == args.argument and v["value"] == args.value
                   for v in values["variables"]), values
        line_bp = run("set_breakpoint", file=Path(frame["file"]).name, line=frame["line"],
                      module=args.module)
        assert line_bp["resolvedLocations"], line_bp
        run("remove_breakpoint", breakpoint_id=line_bp["breakpointId"])
        steps = []
        for action in ("step_out", "step_into", "step_over"):
            run("remove_breakpoint", breakpoint_id=bp["breakpointId"])
            run("control", action=action, thread_id=hit["threadId"])
            stepped = next_stop(hit["stopId"])
            assert stepped["stopReason"] == 8, (action, stepped)
            steps.append(stepped["stopDescription"])
            if action != "step_over":
                bp = run("set_breakpoint", symbol=args.symbol, module=args.module)
                run("control", action="continue")
                hit = next_stop(stepped["stopId"], breakpoint=True)
        print("Breakpoint, source frame, locals and steps", frame, values["variables"],
              steps, flush=True)
        run("control", action="continue")
        run("control", action="pause")
        paused = next_stop(stepped["stopId"])
        print("Paused", paused, flush=True)
    finally:
        print("Detached", run("detach"), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", type=Path, required=True)
    parser.add_argument("--serial", required=True)
    parser.add_argument("--package", required=True)
    parser.add_argument("--symbols", type=Path, required=True)
    parser.add_argument("--symbol", required=True)
    parser.add_argument("--module", required=True)
    parser.add_argument("--argument", required=True)
    parser.add_argument("--value", required=True)
    args = parser.parse_args()
    # Exercise the installed runtime with Android SDK discovery disabled.
    env = {key: value for key, value in os.environ.items()
           if key.upper() not in {"ANDROID_HOME", "ANDROID_SDK_ROOT", "ANDROID_NDK_HOME"}}
    env["PATH"] = os.defpath
    with subprocess.Popen([sys.executable, str(args.server.resolve())], stdin=subprocess.PIPE,
                          stdout=subprocess.PIPE, text=True, encoding="utf-8", env=env) as process:
        try:
            smoke(Client(process), args)
        finally:
            process.stdin.close()
            try:
                process.wait(timeout=45)
            except subprocess.TimeoutExpired:
                process.kill()
                raise


if __name__ == "__main__":
    main()
