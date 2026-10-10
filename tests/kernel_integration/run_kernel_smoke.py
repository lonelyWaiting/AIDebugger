"""Opt-in real stdio/DbgEng proof. Never changes boot settings or crashes a target.

python tests/kernel_integration/run_kernel_smoke.py --dump file.dmp --report .tmp/kernel/report.json
Optional --connection tests a configured remote kernel; --live-control additionally
checks a real break/step/resume sequence. Without it, no execution change is made.
"""
import argparse
import hashlib
import json
from pathlib import Path
import queue
import re
import subprocess
import sys
import tempfile
import threading
import time

ROOT = Path(__file__).resolve().parents[2]


class Client:
    def __init__(self, server):
        self.process = subprocess.Popen([sys.executable, "-u", str(server)], cwd=ROOT,
                                        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        self.responses = queue.Queue()
        self.next_id = 0
        self.stderr = []
        def read():
            for line in self.process.stdout:
                self.responses.put(json.loads(line))
        def errors():
            for line in self.process.stderr:
                self.stderr.append(line.decode(errors="replace"))
        threading.Thread(target=read, daemon=True).start()
        threading.Thread(target=errors, daemon=True).start()
        self.rpc("initialize", {"protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "kernel-smoke", "version": "1"}})

    def rpc(self, method, params):
        self.next_id += 1
        request = {"jsonrpc": "2.0", "id": self.next_id, "method": method, "params": params}
        self.process.stdin.write((json.dumps(request) + "\n").encode())
        self.process.stdin.flush()
        response = self.responses.get(timeout=10)
        assert response["id"] == self.next_id, response
        assert "error" not in response, response
        return response["result"]

    def call(self, name, **arguments):
        result = self.rpc("tools/call", {"name": name, "arguments": arguments})
        assert not result.get("isError"), result
        return result.get("structuredContent") or json.loads(result["content"][0]["text"])

    def wait(self, receipt, timeout=60):
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            job = self.call("kernel_job_status", session_id=receipt["sessionId"], job_id=receipt["jobId"], count=16384)
            if job["state"] != "running":
                assert job["state"] == "complete", job
                result = job.get("result", {})
                output = result.get("output", "")
                while result.get("nextOffset") is not None:
                    page = self.call("kernel_job_status", session_id=receipt["sessionId"], job_id=receipt["jobId"], offset=result["nextOffset"], count=16384)
                    result = page["result"]
                    output += result["output"]
                result["output"] = output
                return result
            time.sleep(0.05)
        raise TimeoutError("Operation still running; use kernel_interrupt(cancel_wait=True)")

    def close(self):
        self.process.stdin.close()
        try:
            self.process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.process.terminate()
            self.process.wait(timeout=3)
        assert self.process.returncode == 0, (self.process.returncode, self.stderr)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dump", type=Path, required=True)
    parser.add_argument("--server", type=Path, default=ROOT / "mcp_server/kernel_server.py")
    parser.add_argument("--symbol-path", default="")
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--connection", default="")
    parser.add_argument("--live-control", action="store_true")
    args = parser.parse_args()
    before = hashlib.sha256(args.dump.read_bytes()).hexdigest()
    client = Client(args.server)
    report = {"dumpSha256": before, "server": str(args.server), "checks": {}, "evidence": {}}
    try:
        tools = client.rpc("tools/list", {})
        report["checks"]["freshToolDiscovery"] = len(tools["tools"])
        receipt = client.call("kernel_open_dump", dump_path=str(args.dump.resolve()), symbol_path=args.symbol_path, full_access=True)
        sid = receipt["sessionId"]
        opened = client.wait(receipt)
        assert opened["kernelQualifier"] >= 1024 and opened["state"] == "paused", opened
        report["checks"]["kernelDumpOpened"] = True
        report["enginePath"] = opened["enginePath"]
        for query in ("bugcheck", "modules", "registers", "stack", "analyze"):
            result = client.wait(client.call("kernel_query", session_id=sid, query=query))
            assert result["output"], (query, result)
            report["evidence"][query] = result
        assert "0000013a" in report["evidence"]["bugcheck"]["output"].lower() or "Bugcheck code" in report["evidence"]["bugcheck"]["output"]
        registers = report["evidence"]["registers"]["output"]
        rsp = re.search(r"\brsp=([0-9a-f`]+)", registers, re.I)
        assert rsp, registers
        address = rsp[1].replace("`", "")
        memory = client.wait(client.call("kernel_read_memory", session_id=sid, address=address, size=64))
        assert memory["bytesRead"] > 0 and len(memory["dataHex"]) == memory["bytesRead"] * 2, memory
        report["evidence"]["memory"] = memory
        original = memory["dataHex"][:2]
        changed = f"{int(original, 16) ^ 0xff:02x}"
        written = client.wait(client.call("kernel_write_memory", session_id=sid, address=address, data_hex=changed))
        assert written["bytesWritten"] == 1 and not written["partial"], written
        reread = client.wait(client.call("kernel_read_memory", session_id=sid, address=address, size=1))
        assert reread["dataHex"] == changed, reread
        restored = client.wait(client.call("kernel_write_memory", session_id=sid, address=address, data_hex=original))
        assert restored["bytesWritten"] == 1, restored
        report["checks"]["engineMemoryWriteReadRestore"] = True
        # Mini dumps need not contain RIP's code page. Use captured bytes in
        # this offline engine view, restoring them even if the radix test fails.
        assert memory["bytesRead"] >= 16, memory
        saved = memory["dataHex"][:32]
        try:
            nops = client.wait(client.call("kernel_write_memory", session_id=sid, address=address, data_hex="90" * 16))
            assert nops["bytesWritten"] == 16, nops
            baseline = client.wait(client.call("kernel_query", session_id=sid, query="disassemble",
                                              arguments={"address": address, "count": 16}))
            assert len(re.findall(r"\bnop\b", baseline["output"])) == 16, baseline
            client.wait(client.call("kernel_execute", session_id=sid, command="n 10"))
            try:
                decimal = client.wait(client.call("kernel_query", session_id=sid, query="disassemble",
                                                 arguments={"address": address, "count": 16}))
                assert decimal["output"] == baseline["output"], (baseline, decimal)
            finally:
                client.wait(client.call("kernel_execute", session_id=sid, command="n 16"))
        finally:
            restored = client.wait(client.call("kernel_write_memory", session_id=sid, address=address, data_hex=saved))
            assert restored["bytesWritten"] == 16, restored
        report["checks"]["disassemblyIndependentOfRadix"] = True
        missing = client.wait(client.call("kernel_read_memory", session_id=sid, address="1", size=64))
        assert missing["partial"] and missing["status"] == "unavailable", missing
        report["checks"]["missingMemoryContract"] = True
        with tempfile.TemporaryDirectory(prefix="aidb-symbols-\U0001f680-") as symbol_directory:
            restricted = client.call("kernel_open_dump", dump_path=str(args.dump.resolve()), symbol_path=symbol_directory)
            opened_restricted = client.wait(restricted)
            assert opened_restricted["symbolPath"] == symbol_directory, opened_restricted
            report["checks"]["unicodeSymbolPath"] = True
        denied = client.rpc("tools/call", {"name": "kernel_execute", "arguments": {"session_id": restricted["sessionId"], "command": "lm"}})
        assert denied.get("isError"), denied
        report["checks"]["twoIsolatedSessionsAndAccessBoundary"] = True
        paged = client.wait(client.call("kernel_execute", session_id=sid,
                            command='.for (r $t0=0; @$t0<20000; r $t0=@$t0+1) {.echo abcdefghij}'))
        assert paged["truncated"] and len(paged["output"]) == 131072, (len(paged["output"]), paged["truncated"])
        report["checks"]["boundedOutputAndPaging"] = True
        for session_id in (restricted["sessionId"], sid):
            closed = client.wait(client.call("kernel_close", session_id=session_id))
            assert closed["state"] == "closed" and not closed["targetResumed"], closed
        report["checks"]["passiveClose"] = True
        if args.connection:
            remote = client.call("kernel_connect", connection=args.connection, full_access=args.live_control, symbol_path=args.symbol_path)
            report["evidence"]["remote"] = client.wait(remote, timeout=120)
            if args.live_control:
                for action in ("step_into", "step_over"):
                    client.wait(client.call("kernel_control", session_id=remote["sessionId"], action=action))
                    end = time.monotonic() + 30
                    while time.monotonic() < end:
                        status = client.call("kernel_session_status", session_id=remote["sessionId"])
                        if status["state"] == "paused":
                            break
                        time.sleep(0.05)
                    assert status["state"] == "paused", status
                client.wait(client.call("kernel_control", session_id=remote["sessionId"], action="continue"))
                client.wait(client.call("kernel_interrupt", session_id=remote["sessionId"]))
                end = time.monotonic() + 30
                while time.monotonic() < end:
                    status = client.call("kernel_session_status", session_id=remote["sessionId"])
                    if status["state"] == "paused":
                        break
                    time.sleep(0.05)
                assert status["state"] == "paused", status
            client.wait(client.call("kernel_close", session_id=remote["sessionId"]))
        report["liveKernelValidated"] = bool(args.connection)
        report["checks"]["dumpBytesPreserved"] = hashlib.sha256(args.dump.read_bytes()).hexdigest() == before
        assert report["checks"]["dumpBytesPreserved"]
    finally:
        client.close()
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"checks": report["checks"], "liveKernelValidated": report["liveKernelValidated"], "report": str(args.report)}, indent=2))


if __name__ == "__main__":
    main()
