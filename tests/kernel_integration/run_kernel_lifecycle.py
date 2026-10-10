"""Real engine failure/close proof without a configured target or boot changes."""
import argparse
import json
from pathlib import Path
import socket
import time

from run_kernel_smoke import Client, ROOT


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", type=Path, default=ROOT / "mcp_server/kernel_server.py")
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as listener:
        listener.bind(("127.0.0.1", 0))
        port = listener.getsockname()[1]
    client = Client(args.server)
    report = {}
    try:
        remote = client.call("kernel_connect", connection=f"net:port={port},key=1.2.3.4", full_access=True)
        end = time.monotonic() + 3
        while time.monotonic() < end:
            status = client.call("kernel_session_status", session_id=remote["sessionId"])
            if status["state"] != "starting":
                break
            time.sleep(0.05)
        assert status["state"] == "connecting", status
        # Give AttachKernel/WaitForEvent time to enter the native wait, so this
        # verifies close of a blocked engine rather than only startup cancel.
        time.sleep(0.3)
        start = time.monotonic()
        status = client.call("kernel_session_status", session_id=remote["sessionId"])
        report["pollElapsedSeconds"] = round(time.monotonic() - start, 3)
        assert time.monotonic() - start < 1, status
        duplicate = client.rpc("tools/call", {"name": "kernel_connect", "arguments": {"connection": f"net:key=4.3.2.1,port={port}"}})
        assert duplicate.get("isError"), duplicate
        report["duplicateEndpointRejected"] = True
        closed = client.wait(client.call("kernel_close", session_id=remote["sessionId"]), timeout=6)
        report["closeReceipt"] = closed
        assert closed["state"] == "closed", closed
        end = time.monotonic() + 2
        while time.monotonic() < end:
            status = client.call("kernel_session_status", session_id=remote["sessionId"])
            if not status["workerAlive"]:
                break
            time.sleep(0.05)
        assert not status["workerAlive"], status
        report["ownedWorkerExited"] = True
        assert "1.2.3.4" not in json.dumps(client.call("kernel_list_sessions"))
        report["connectionKeyNotReturned"] = True
        local = client.call("kernel_connect", local=True)
        end = time.monotonic() + 3
        while time.monotonic() < end:
            job = client.call("kernel_job_status", session_id=local["sessionId"], job_id=local["jobId"])
            if job["state"] != "running":
                break
            time.sleep(0.05)
        report["localAttempt"] = job
        if job["state"] in {"complete", "running"}:
            client.wait(client.call("kernel_close", session_id=local["sessionId"]), timeout=6)
        report["liveTargetValidated"] = False
    finally:
        client.close()
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
