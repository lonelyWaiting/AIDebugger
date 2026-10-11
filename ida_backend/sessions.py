"""Owned IDA processes, one private database and endpoint per session."""
from __future__ import annotations

from contextlib import contextmanager
import hashlib
import json
import math
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import threading
import time
import uuid
from dataclasses import dataclass, field

from .transport import IdaError, rpc, tool_value


def file_hash(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def find_ida(ida_dir=None):
    configured = ida_dir or os.environ.get("AIDB_IDA_DIR")
    if configured:
        root = Path(configured).expanduser().resolve()
        if not (root / "ida.exe").is_file():
            raise IdaError("IDA_NOT_FOUND", "AIDB_IDA_DIR must contain ida.exe")
        return root
    roots = [Path(os.environ.get("ProgramFiles", "C:/Program Files")), Path("C:/Program Files")]
    candidates = sorted({p for root in roots for p in root.glob("IDA*/*")
                         if p.name == "ida.exe"}, reverse=True)
    if not candidates:
        raise IdaError("IDA_NOT_FOUND", "Set AIDB_IDA_DIR to your licensed IDA installation")
    return candidates[0].parent


@dataclass
class Session:
    session_id: str
    source: str
    sha256: str
    mode: str
    directory: Path
    process: object
    token: str = field(repr=False)
    port: int | None = None
    state: str = "starting"
    identity: dict = field(default_factory=dict)
    closing_at: float | None = None
    close_receipt: dict = field(default_factory=dict)
    lock: object = field(default_factory=threading.RLock, repr=False)

    def describe(self):
        return {"sessionId": self.session_id, "sourcePath": self.source,
                "sourceSha256": self.sha256, "mode": self.mode,
                "pid": self.process.pid, "state": self.state,
                "exitCode": self.process.poll(), "owned": True,
                "workDirectory": str(self.directory), "identity": self.identity,
                "closeReceipt": self.close_receipt}


class IdaSessions:
    def __init__(self, root=None, ida_dir=None, max_sessions=4):
        self.root = Path(root or os.environ.get("AIDB_IDA_WORK_ROOT") or
                         Path.home() / ".ai-debugger" / "ida-sessions").resolve()
        self.ida_dir = ida_dir
        self.max_sessions = max_sessions
        self.sessions = {}
        self.lock = threading.RLock()
        self._opening = 0

    def open(self, input_path, mode="headless"):
        if mode not in ("headless", "gui"):
            raise IdaError("INVALID_ARGUMENT", "mode must be headless or gui")
        source = Path(input_path).expanduser().resolve(strict=True)
        if not source.is_file():
            raise IdaError("INVALID_ARGUMENT", "input_path must be a binary or saved IDB file")
        if source.suffix.lower() in (".id0", ".id1", ".id2", ".nam", ".til"):
            raise IdaError("INVALID_ARGUMENT", "Open the saved .i64/.idb, not a loose working file")
        install = find_ida(self.ida_dir)
        executable = install / ("idat.exe" if mode == "headless" else "ida.exe")
        if not executable.is_file():
            raise IdaError("IDA_NOT_FOUND", "Requested IDA executable is missing")
        with self.lock:
            if sum(s.process.poll() is None for s in self.sessions.values()) + self._opening >= self.max_sessions:
                raise IdaError("IDA_SESSION_LIMIT", "Close a session before opening another")
            self._opening += 1
        try:
            session_id = uuid.uuid4().hex
            directory = self.root / session_id
            directory.mkdir(parents=True)
            # Isolate both IDBs and raw inputs. Originals and external open IDBs
            # are never used as writable database storage.
            input_directory = directory / "input"
            input_directory.mkdir()
            copied = input_directory / source.name
            shutil.copy2(source, copied)
            sha256 = file_hash(copied)
            if file_hash(source) != sha256:
                raise IdaError("INPUT_CHANGED", "Input changed during copy")
            token = secrets.token_hex(32)
            config = {"sessionId": session_id, "token": token,
                      "sourcePath": str(source), "sourceSha256": sha256,
                      "mode": mode, "parentPid": os.getpid(),
                      "directory": str(directory)}
            config_path = directory / "launch.json"
            config_path.write_text(json.dumps(config), encoding="utf-8")
            user = directory / "ida-user"
            user.mkdir()
            env = dict(os.environ, AIDB_IDA_CONFIG=str(config_path), IDAUSR=str(user),
                       PYTHONIOENCODING="utf-8")
            if mode == "headless":
                env["TVHEADLESS"] = "1"
            script = Path(__file__).with_name("worker.py").resolve()
            command = [str(executable), "-A", "-L" + str(directory / "ida.log"),
                       "-S" + str(script), str(copied)]
            flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" and mode == "headless" else 0
            with (directory / "process.log").open("wb") as log:
                process = subprocess.Popen(command, cwd=directory, env=env,
                                           stdin=subprocess.DEVNULL, stdout=log, stderr=log,
                                           creationflags=flags)
            session = Session(session_id, str(source), sha256, mode, directory, process, token)
            with self.lock:
                self.sessions[session_id] = session
            return session.describe()
        finally:
            with self.lock:
                self._opening -= 1

    def get(self, session_id):
        with self.lock:
            session = self.sessions.get(session_id)
        if session is None:
            raise IdaError("IDA_SESSION_NOT_FOUND", "Unknown explicit session_id")
        return session

    def status(self, session_id):
        session = self.get(session_id)
        if not session.lock.acquire(blocking=False):
            return {**session.describe(), "state": "busy"}
        try:
            return self._status_locked(session)
        finally:
            session.lock.release()

    def _status_locked(self, session, timeout=2):
        session_id = session.session_id
        with session.lock:
            if session.process.poll() is not None:
                session.state = "closed" if session.state in ("closing", "closed") else "exited"
                return session.describe()
            if session.state == "closing":
                # GUI plugins can keep the process alive after native qexit.
                # Only an acknowledged close (saved and no debugger) permits
                # terminating our original Popen handle; never kill on timeout.
                if (session.closing_at is not None and time.monotonic() - session.closing_at >= 5
                        and not session.close_receipt.get("ownedProcessTerminationAfterGrace")):
                    session.process.terminate()
                    session.close_receipt["ownedProcessTerminationAfterGrace"] = True
                return session.describe()
            ready = session.directory / "ready.json"
            if not ready.exists():
                return session.describe()
            data = json.loads(ready.read_text(encoding="utf-8"))
            if data.get("sessionId") != session_id or data.get("pid") != session.process.pid:
                raise IdaError("IDA_IDENTITY_MISMATCH", "Invalid worker registration")
            session.port = data["port"]
            try:
                identity = tool_value(self._rpc_tool(session, "aidb_identity", {}, timeout=timeout))
                if not isinstance(identity, dict):
                    raise IdaError("IDA_TRANSPORT_ERROR", "IDA identity must be an object")
            except (OSError, TimeoutError, IdaError) as error:
                if isinstance(error, IdaError) and error.payload["code"] != "IDA_TRANSPORT_ERROR":
                    raise
                session.state = "busy"
                return session.describe()
            if identity.get("sessionId") != session_id or identity.get("pid") != session.process.pid:
                raise IdaError("IDA_IDENTITY_MISMATCH", "Worker target changed")
            if identity.get("sourceSha256") != session.sha256:
                raise IdaError("IDA_IDENTITY_MISMATCH", "Worker source hash mismatch")
            if session.identity and any(identity.get(key) != session.identity.get(key)
                                        for key in ("inputSha256", "idbPath")):
                raise IdaError("IDA_IDENTITY_MISMATCH", "The active database changed inside this session")
            if Path(session.source).suffix.lower() not in (".i64", ".idb") and identity.get("inputSha256") != session.sha256:
                raise IdaError("IDA_IDENTITY_MISMATCH", "Loaded binary does not match requested input")
            session.identity = identity
            session.state = "ready"
            return session.describe()

    def list(self):
        # No network probes: one busy database must not stall enumeration.
        with self.lock:
            return [s.describe() for s in self.sessions.values()]

    def _rpc_tool(self, session, name, arguments, timeout=30):
        return rpc(session.port, session.token, "tools/call",
                   {"name": name, "arguments": arguments}, timeout)

    @contextmanager
    def _operation_lock(self, session):
        if not session.lock.acquire(blocking=False):
            raise IdaError("IDA_BUSY_NOT_STARTED", "This session already has an operation; poll status",
                           state="busy", requestStarted=False)
        try:
            yield
        finally:
            session.lock.release()

    def call(self, session_id, name, arguments=None, timeout=30):
        if not isinstance(timeout, (int, float)) or not math.isfinite(timeout) or not 0 < timeout <= 120:
            raise IdaError("INVALID_ARGUMENT", "timeout must be between 0 and 120 seconds")
        deadline = time.monotonic() + timeout
        session = self.get(session_id)
        with self._operation_lock(session):
            status = self._status_locked(session, timeout=min(2, timeout))
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise IdaError("IDA_DEADLINE_EXCEEDED_NOT_STARTED", "Identity preflight exhausted the request budget",
                               requestStarted=False)
            if status["state"] != "ready":
                raise IdaError("IDA_NOT_READY", "Poll ida_session_status before using this session", state=status["state"])
            if name in ("aidb_close", "aidb_identity"):
                raise IdaError("INVALID_ARGUMENT", "Use the lifecycle tools for this operation")
            try:
                return self._rpc_tool(session, name, arguments or {}, remaining)
            except (OSError, TimeoutError, IdaError) as error:
                if isinstance(error, IdaError) and error.payload["code"] != "IDA_TRANSPORT_ERROR":
                    raise
                # The command may still be executing. Never restart or replay it.
                raise IdaError("IDA_OUTCOME_UNKNOWN", "Transport lost while IDA may still be executing; do not retry mutations",
                               tool=name, outcomeUnknown=True, doNotRetry=True) from error

    def tools(self, session_id, offset=0, count=30):
        if not 0 <= offset or not 1 <= count <= 100:
            raise IdaError("INVALID_ARGUMENT", "offset >= 0 and count in 1..100 required")
        session = self.get(session_id)
        with self._operation_lock(session):
            if self._status_locked(session)["state"] != "ready":
                raise IdaError("IDA_NOT_READY", "Session is not ready")
            data = rpc(session.port, session.token, "tools/list")
        tools = [t for t in data["tools"] if not t["name"].startswith("aidb_")]
        return {"tools": tools[offset:offset+count], "total": len(tools),
                "nextOffset": offset+count if offset+count < len(tools) else None}

    def close(self, session_id, save=True):
        session = self.get(session_id)
        with self._operation_lock(session):
            state = self.status(session_id)["state"]
            if state in ("closed", "exited", "closing"):
                return session.describe()
            if state == "starting" and not save and not (session.directory / "ready.json").exists():
                # Explicitly discard an unready, never-configured owned worker.
                # Busy/unknown outcomes after readiness are never killed here.
                session.process.terminate()
                session.state = "closing"
                session.close_receipt = {"startupCancelled": True, "saved": False}
                return session.describe()
            if state != "ready":
                raise IdaError("IDA_NOT_READY", "Session is busy or starting; it has not been killed")
            try:
                result = tool_value(self._rpc_tool(session, "aidb_close", {"save": save}, 10))
            except (OSError, TimeoutError, IdaError) as error:
                if isinstance(error, IdaError) and error.payload["code"] != "IDA_TRANSPORT_ERROR":
                    raise
                raise IdaError("IDA_OUTCOME_UNKNOWN", "Close outcome unknown; poll status, do not force kill",
                               outcomeUnknown=True, doNotRetry=True) from error
            if (not isinstance(result, dict) or result.get("accepted") is not True
                    or result.get("saved") is not save):
                raise IdaError("IDA_OUTCOME_UNKNOWN", "Invalid close/save receipt; session is preserved",
                               outcomeUnknown=True, doNotRetry=True)
            session.state = "closing"
            session.closing_at = time.monotonic()
            session.close_receipt = result
            return {**session.describe(), "closeReceipt": result}

    def shutdown(self):
        # Finish acknowledged closes before the supervisor exits: GUI qexit may
        # need the owned-process fallback even when no client polls status.
        # Active debugging, failed saves and unknown outcomes never authorize it.
        for session in tuple(self.sessions.values()):
            try:
                self.close(session.session_id, save=not (session.state == "starting" and not (session.directory / "ready.json").exists()))
            except Exception:
                pass
        deadline = time.monotonic() + 7
        pending = [s for s in self.sessions.values() if s.state == "closing"]
        while pending and time.monotonic() < deadline:
            for session in tuple(pending):
                try:
                    self.status(session.session_id)
                except OSError:
                    pass
                if session.process.poll() is not None:
                    pending.remove(session)
            if pending:
                time.sleep(0.1)
