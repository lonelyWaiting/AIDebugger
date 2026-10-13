"""Opt-in real DbgEng lifecycle proof using owned workers and an offline dump.

Exercises a no-peer KDNET wait without local attach, then injects a supervisor
reply-reader failure after a real dump query. Never changes boot settings or
controls a connected kernel target.
"""
import argparse
import hashlib
import json
from pathlib import Path
import socket
import sys
import threading
import time

from run_kernel_smoke import Client, ROOT

sys.path.insert(0, str(ROOT))
from kernel_backend.contracts import KernelError
from kernel_backend.sessions import KernelSessions


def until(function, predicate, seconds=6):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        value = function()
        if predicate(value):
            return value
        time.sleep(0.01)
    raise TimeoutError(f"Owned worker did not reach the expected state: {value}")


def no_peer(server):
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as listener:
        listener.bind(("127.0.0.1", 0))
        port = listener.getsockname()[1]
    client = Client(server)
    evidence = {"kind": "no_peer_kdnet", "connectedTarget": False}
    try:
        opened = client.call("kernel_connect", connection=f"net:port={port},key=1.2.3.4", full_access=True)
        sid = opened["sessionId"]
        status = until(lambda: client.call("kernel_session_status", session_id=sid),
                       lambda value: value["state"] != "starting")
        assert status["state"] == "connecting", status
        # Poll through a real native attach interval instead of blocking the client.
        observed = []
        deadline = time.monotonic() + .3
        while time.monotonic() < deadline:
            start = time.monotonic()
            observed.append(client.call("kernel_session_status", session_id=sid)["state"])
            assert time.monotonic() - start < 1
            time.sleep(.01)
        assert set(observed) == {"connecting"}, observed
        cancel = client.call("kernel_interrupt", session_id=sid, cancel_wait=True)
        cancelled = until(lambda: client.call("kernel_job_status", session_id=sid, job_id=cancel["jobId"]),
                          lambda value: value["state"] != "running")
        evidence["cancelWaitJob"] = cancelled
        assert cancelled["state"] == "complete", cancelled
        start = time.monotonic()
        close = client.call("kernel_close", session_id=sid)
        for tool, arguments in (("kernel_query", {"query": "modules"}), ("kernel_interrupt", {}), ("kernel_close", {})):
            denied = client.rpc("tools/call", {"name": tool, "arguments": {"session_id": sid, **arguments}})
            assert denied.get("isError") and "SESSION_CLOSING" in json.dumps(denied), denied
        closed = client.wait(close, timeout=6)
        final = until(lambda: client.call("kernel_session_status", session_id=sid),
                      lambda value: not value["workerAlive"])
        assert closed["state"] == "closed" and closed["targetResumed"] is False, closed
        assert "1.2.3.4" not in json.dumps(client.call("kernel_list_sessions"))
        evidence.update(closeReceipt=closed, finalStatus=final,
                        closeElapsedSeconds=round(time.monotonic() - start, 3),
                        closingRejectsNewWork=True, credentialsRedacted=True)
        return evidence
    finally:
        client.close()


class ReaderFaultSessions(KernelSessions):
    """Fault only this test's transport reader; DbgEng/worker stay unmodified."""
    def __init__(self):
        super().__init__()
        self.fail_next_reply = threading.Event()
        self.failure_observed = threading.Event()

    def _read(self, session):
        stream = session.process.stdout
        open_id = session.active

        def replies():
            for line in stream:
                reply_id = json.loads(line).get("id")
                if self.fail_next_reply.is_set() and reply_id and reply_id != open_id:
                    self.failure_observed.set()
                    raise OSError("Test-injected failure after receiving a real worker reply")
                yield line

        session.process.stdout = replies()
        try:
            super()._read(session)
        finally:
            # Leave the real pipe open while the worker finishes native close.
            session.process.stdout = stream


def reader_failure(dump):
    manager = ReaderFaultSessions()
    evidence = {"kind": "real_dump_worker_with_injected_supervisor_reader_failure"}
    try:
        opened = manager.open("dump", dump_path=str(dump), full_access=True)
        sid = opened["sessionId"]
        result = until(lambda: manager.job(sid, opened["jobId"], wait_ms=10),
                       lambda value: value["state"] != "running")
        assert result["state"] == "complete", result
        session = manager._get(sid)
        evidence.update(workerPid=session.process.pid, opened=result)
        manager.fail_next_reply.set()
        query = manager.submit(sid, "query", {"query": "registers", "arguments": {}})
        failed = until(lambda: manager.job(sid, query["jobId"], wait_ms=10),
                       lambda value: value["state"] != "running")
        assert manager.failure_observed.is_set(), failed
        assert failed["state"] == "failed" and failed["error"]["code"] == "WORKER_EXITED", failed
        status = manager.status(sid)
        assert status["workerAlive"] and not status["snapshotStable"], status
        jobs_before = len(session.jobs)
        try:
            manager.interrupt(sid)
            raise AssertionError("A failed reply reader accepted a new interrupt")
        except KernelError as error:
            assert error.code == "WORKER_UNAVAILABLE", error
            evidence["interruptRejected"] = error.code
        assert len(session.jobs) == jobs_before
        close = manager.submit(sid, "close", {})
        closed = until(lambda: manager.job(sid, close["jobId"], wait_ms=10),
                       lambda value: value["state"] != "running")
        final = until(lambda: manager.status(sid), lambda value: not value["workerAlive"])
        assert closed["state"] == "complete" and closed["result"]["state"] == "closed", closed
        assert not closed["result"]["nativeCloseConfirmed"], closed
        evidence.update(failedQuery=failed, failedStatus=status, closeJob=closed,
                        finalStatus=final, noOrphanInterruptJob=True)
        return evidence
    finally:
        manager.shutdown()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dump", required=True, type=Path)
    parser.add_argument("--server", type=Path, default=ROOT / "mcp_server/kernel_server.py")
    parser.add_argument("--report", required=True, type=Path)
    args = parser.parse_args()
    dump = args.dump.resolve(strict=True)
    before = hashlib.sha256(dump.read_bytes()).hexdigest()
    report = {"server": str(args.server), "dump": str(dump), "dumpSha256": before,
              "liveKernelControlValidated": False, "hostKernelAttached": False}
    try:
        report["noPeer"] = no_peer(args.server)
        report["readerFailure"] = reader_failure(dump)
        report["dumpBytesPreserved"] = hashlib.sha256(dump.read_bytes()).hexdigest() == before
        assert report["dumpBytesPreserved"]
        report["passed"] = True
    finally:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"passed": True, "report": str(args.report)}, indent=2))


if __name__ == "__main__":
    main()
