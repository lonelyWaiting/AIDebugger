"""Own one process per kernel target. No implicit selection or mutation retries."""
from dataclasses import dataclass, field
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import threading
import time
import uuid

from .contracts import KernelError, capabilities, connection_identity, integer, require, text, validate_operation


@dataclass
class Session:
    id: str
    mode: str
    full_access: bool
    target: dict
    process: object
    status: dict = field(default_factory=lambda: {"state": "starting"})
    jobs: dict = field(default_factory=dict)
    active: str | None = None
    events: list = field(default_factory=list)
    lock: object = field(default_factory=threading.RLock)
    connection_id: str = ""
    close_job: str | None = None
    pipe_failed: bool = False
    event_sequence: int = 0
    changed: object = field(init=False, repr=False)

    def __post_init__(self):
        self.changed = threading.Condition(self.lock)


class KernelSessions:
    MAX_SESSIONS = 8
    MAX_JOBS = 64

    def __init__(self):
        self.sessions = {}
        self.lock = threading.RLock()

    def _get(self, session_id):
        with self.lock:
            session = self.sessions.get(session_id)
        require(session is not None, "Unknown kernel session ID", "SESSION_NOT_FOUND")
        return session

    def open(self, mode, dump_path="", connection="", symbol_path="", full_access=False):
        require(mode in {"dump", "local", "remote"}, "mode must be dump, local or remote")
        require(type(full_access) is bool, "full_access must be boolean")
        text(symbol_path, "symbol_path")
        text(connection, "connection")
        require(mode == "remote" or not connection, "Only remote sessions accept connection options")
        require(mode == "dump" or not dump_path, "Only dump sessions accept a dump path")
        require(mode != "remote" or (connection and connection.isascii()), "Remote sessions require ASCII WinDbg -k connection options")
        require(mode != "local" or not full_access, "Local kernel supports restricted inspection only", "UNSUPPORTED")
        target = {"kind": mode}
        if mode == "dump":
            path = Path(dump_path).expanduser().resolve(strict=True)
            require(path.is_file() and path.stat().st_size > 0, "Dump must be a nonempty file")
            stat = path.stat()
            # Bounded identity check: opening a multi-GB dump must not hash the
            # entire file on an MCP request thread. This is a header hash only.
            with path.open("rb") as stream:
                header = stream.read(4096)
            require(header[:8] in {b"PAGEDU64", b"PAGEDUMP"}, "Expected a Windows kernel dump header", "TARGET_MISMATCH")
            dump_path = str(path)
            target.update({"path": dump_path, "headerSha256": hashlib.sha256(header).hexdigest(),
                           "size": stat.st_size, "mtimeNs": stat.st_mtime_ns})
        if mode == "remote":
            connection_id = connection_identity(connection)
            target["transport"] = connection.split(":", 1)[0]
        session_id, job_id = str(uuid.uuid4()), str(uuid.uuid4())
        config = dict(mode=mode, dump_path=dump_path, connection=connection, symbol_path=symbol_path,
                      full_access=full_access, open_id=job_id, target=target)
        with self.lock:
            if mode == "remote":
                require(not any(s.mode == "remote" and s.connection_id == connection_id and s.process.poll() is None
                                for s in self.sessions.values()), "This remote endpoint already has an owned engine session", "TARGET_BUSY")
            # Retain a bounded history; live sessions are never evicted.
            if len(self.sessions) >= 32:
                expired = next((key for key, s in self.sessions.items() if s.process.poll() is not None), None)
                if expired:
                    del self.sessions[expired]
            require(sum(s.process.poll() is None for s in self.sessions.values()) < self.MAX_SESSIONS,
                    "Too many live kernel sessions", "SESSION_LIMIT")
            process = subprocess.Popen([sys.executable, "-u", "-m", "kernel_backend.worker"],
                                       cwd=Path(__file__).resolve().parents[1], stdin=subprocess.PIPE,
                                       stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                                       creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            session = Session(session_id, mode, full_access, target, process,
                              connection_id=connection_id if mode == "remote" else "")
            session.jobs[job_id] = {"state": "running", "operation": "open", "started": time.monotonic()}
            session.active = job_id
            self.sessions[session_id] = session
            try:
                process.stdin.write((json.dumps(config) + "\n").encode())
                process.stdin.flush()
            except OSError as exception:
                with session.lock:
                    session.pipe_failed = True
                    session.status["state"] = "failed"
                    session.jobs[job_id].update(state="failed", error={"code": "WORKER_START_FAILED",
                        "message": "Worker initialization pipe failed; target outcome may be unknown"})
                    session.active = None
                process.terminate()
                raise KernelError("WORKER_START_FAILED", "Could not initialize the owned engine worker") from exception
            finally:
                threading.Thread(target=self._read, args=(session,), daemon=True, name="kernel-replies").start()
        return {"sessionId": session_id, "jobId": job_id, "state": "starting", "target": target,
                "capability": capabilities(mode, full_access)}

    def _read(self, session):
        try:
            for line in session.process.stdout:
                message = json.loads(line)
                with session.lock:
                    if "status" in message and session.close_job is None and not session.pipe_failed:
                        session.status.update(message["status"])
                    if "eventOutput" in message or "eventError" in message:
                        session.event_sequence += 1
                        session.events.append({key: value for key, value in message.items() if key != "status"})
                        session.events[:] = session.events[-8:]
                    job = session.jobs.get(message.get("id"))
                    if job and job["state"] == "running":
                        if message["id"] == session.close_job and "error" in message:
                            # The watchdog still owns bounded helper cleanup.
                            # Do not publish a terminal failure followed by success.
                            job["nativeCloseError"] = message["error"]
                            continue
                        if message["id"] == session.close_job:
                            job.pop("nativeCloseError", None)
                        job["state"] = "failed" if "error" in message else "complete"
                        job.update({key: value for key, value in message.items() if key in {"result", "error"}})
                        if ("result" in message and "state" in message["result"] and
                                ((session.close_job is None and not session.pipe_failed) or
                                 message["id"] == session.close_job)):
                            session.status.update({key: value for key, value in message["result"].items()
                                                   if key in {"state", "snapshotStable", "snapshotStatus", "stopReason", "stopReasonRaw", "eventDescription", "executionStatusRaw", "kernelQualifier", "enginePath", "registeredExtensions", "symbolPath"}})
                        if session.active == message["id"]:
                            session.active = None
                    session.changed.notify_all()
        except Exception:
            with session.lock:
                if session.close_job is None:
                    session.status["state"] = "failed"
        finally:
            with session.lock:
                # The only reply reader has stopped. The helper can still be
                # alive, but no subsequently submitted job can receive a reply.
                session.pipe_failed = True
                for job_id, job in session.jobs.items():
                    if job["state"] == "running" and job_id != session.close_job:
                        job.update(state="failed", error={"code": "WORKER_EXITED", "message": "Engine worker exited; operation outcome may be unknown"})
                session.active = None
                if session.close_job is None and session.status["state"] not in {"closed", "failed"}:
                    session.status["state"] = "disconnected"
                session.changed.notify_all()

    def status(self, session_id):
        session = self._get(session_id)
        with session.lock:
            snapshot = dict(session.status)
            worker_alive = session.process.poll() is None
            if not worker_alive and snapshot["state"] not in {"closed", "closing", "failed"}:
                snapshot["state"] = "disconnected"
            snapshot["snapshotStable"] = snapshot["state"] == "paused"
            snapshot["snapshotStatus"] = "stable" if snapshot["snapshotStable"] else "unstable"
            if snapshot["state"] != "paused":
                snapshot["stopReason"] = "not_paused"
            return {"sessionId": session.id, **snapshot, "activeJobId": session.active,
                    "workerAlive": worker_alive, "target": session.target, "eventSequence": session.event_sequence,
                    "capability": capabilities(session.mode, session.full_access), "events": list(session.events)}

    def list(self):
        with self.lock:
            return [self.status(key) for key in self.sessions]

    def submit(self, session_id, operation, arguments):
        session = self._get(session_id)
        validate_operation(session.mode, session.full_access, operation, arguments)
        with session.lock:
            require(session.close_job is None,
                    "Session close is already pending or complete", "SESSION_CLOSING")
            require(session.process.poll() is None, "Engine worker has exited", "WORKER_EXITED")
            require(operation == "close" or not session.pipe_failed,
                    "Engine worker transport is unavailable; close this session", "WORKER_UNAVAILABLE")
            require(session.active is None or operation == "close", "Poll the active operation before submitting another", "SESSION_BUSY")
            require(operation == "close" or session.status["state"] in {"paused", "local_inspection"},
                    "Target must be available and paused for inspection or commands", "NOT_PAUSED")
            return self._send(session, operation, arguments)

    def _send(self, session, operation, arguments=None, **extra):
        # Pending interrupts must never exhaust the slot needed for cleanup.
        limit = self.MAX_JOBS if operation == "close" else self.MAX_JOBS - 1
        if len(session.jobs) >= limit:
            finished = next((key for key, value in session.jobs.items() if value["state"] != "running"), None)
            require(finished is not None, "Too many pending operations", "JOB_LIMIT")
            del session.jobs[finished]
        job_id = str(uuid.uuid4())
        session.jobs[job_id] = {"state": "running", "operation": operation, "started": time.monotonic()}
        if operation != "interrupt":
            session.active = job_id
        if operation == "close":
            session.close_job = job_id
            session.status["state"] = "closing"
        try:
            session.process.stdin.write((json.dumps({"id": job_id, "operation": operation,
                                                    "arguments": arguments or {}, **extra}) + "\n").encode())
            session.process.stdin.flush()
        except (OSError, BrokenPipeError) as exception:
            session.pipe_failed = True
            error = {"code": "OUTCOME_UNKNOWN", "message": "Worker pipe failed; do not retry mutations"}
            if operation == "close":
                session.jobs[job_id]["nativeCloseError"] = error
            else:
                session.jobs[job_id].update(state="failed", error=error)
            if operation != "close":
                session.status["state"] = "out_of_sync"
            raise KernelError("OUTCOME_UNKNOWN", "Worker pipe failed; do not retry mutations") from exception
        finally:
            if operation == "close":
                threading.Thread(target=self._close_watchdog, args=(session, job_id), daemon=True,
                                 name="kernel-close-watchdog").start()
        return {"sessionId": session.id, "jobId": job_id, "state": "running"}

    def _close_watchdog(self, session, job_id):
        """Some engine builds do not interrupt AttachKernel until a peer connects.

        Bound the close of the owned helper, with explicit unknown target state.
        Never kill a target process, resume the kernel, or retry a command.
        """
        try:
            session.process.wait(timeout=2)
        except subprocess.TimeoutExpired:
            session.process.terminate()
            try:
                session.process.wait(timeout=1)
            except subprocess.TimeoutExpired:
                session.process.kill()
                session.process.wait(timeout=1)
        with session.lock:
            job = session.jobs[job_id]
            if job["state"] != "complete":
                job.pop("error", None)
                job.update(state="complete", result={"state": "closed", "behavior": "owned_worker_closed",
                           "nativeCloseConfirmed": False, "targetStateKnown": False, "targetResumed": False})
                if "nativeCloseError" in job:
                    job["result"]["nativeCloseError"] = job.pop("nativeCloseError")
            session.status["state"] = "closed"
            session.active = None
            session.changed.notify_all()

    def interrupt(self, session_id, exit_wait=False):
        require(type(exit_wait) is bool, "exit_wait must be boolean")
        session = self._get(session_id)
        with session.lock:
            require(session.close_job is None,
                    "Session close is already pending or complete", "SESSION_CLOSING")
            require(session.process.poll() is None, "Engine worker has exited", "WORKER_EXITED")
            require(not session.pipe_failed,
                    "Engine worker transport is unavailable; close this session", "WORKER_UNAVAILABLE")
            require(exit_wait or session.mode == "remote" and session.full_access,
                    "Break-in requires a remote full_access session", "CAPABILITY_DENIED")
            return self._send(session, "interrupt", exit_wait=exit_wait)

    def job(self, session_id, job_id, offset=0, count=8192, wait_ms=0):
        integer(offset, 0, 131072, "offset")
        integer(count, 1, 16384, "count")
        integer(wait_ms, 0, 50, "wait_ms")
        session = self._get(session_id)
        with session.lock:
            require(job_id in session.jobs, "Unknown or expired job ID", "JOB_NOT_FOUND")
            job = session.jobs[job_id]
            if wait_ms and job["state"] == "running":
                # Release the session lock while the reader receives completion.
                # This waits only for the existing job; it never repeats work.
                session.changed.wait_for(lambda: job["state"] != "running", timeout=wait_ms / 1000)
            result = {key: value for key, value in job.items() if key not in {"started", "result"}}
            result.update(sessionId=session_id, jobId=job_id, elapsedSeconds=round(time.monotonic() - job["started"], 3))
            if "result" in job:
                payload = dict(job["result"])
                output = payload.get("output", "")
                if "output" in payload:
                    payload["output"] = output[offset:offset + count]
                    payload["outputCharacters"] = len(output)
                    payload["nextOffset"] = offset + count if offset + count < len(output) else None
                result["result"] = payload
            return result

    def shutdown(self):
        for session in list(self.sessions.values()):
            if session.process.poll() is None:
                try:
                    self.submit(session.id, "close", {})
                    session.process.wait(timeout=2)
                except Exception:
                    # Only our isolated engine process; never a target process.
                    session.process.terminate()
                    try:
                        session.process.wait(timeout=1)
                    except subprocess.TimeoutExpired:
                        session.process.kill()
