"""Explicit Android session and job lifecycles."""
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
import tempfile
from threading import Event, RLock, Thread
import time
from uuid import uuid4

from .runtime import AndroidError
from .transport import Connection, WorkerLost, paths, process_name
from .jobs import Budget, JobStopped, checkpoint, current_job


class Session:
    def __init__(self, connection):
        self.connection = connection
        self.executor = ThreadPoolExecutor(max_workers=1, thread_name_prefix="aidb-android")
        self.phase = "attaching"
        self.jobs = {}
        self.budgets = {}
        self.current = None
        self.event_job = None


class Sessions:
    def __init__(self, runtime):
        self.runtime = runtime
        self.lock = RLock()
        self.sessions = {}
        self.symbol_cache = None
        self.monitor = None
        self.monitor_stop = Event()

    def open(self, serial, package, process, pid, abi, symbol_dirs, source_map,
             *, deadline_ms=60_000, activity="", mode="native", java_breakpoints=None, native_breakpoints=None):
        if not serial or len(serial) > 256 or any(char.isspace() for char in serial):
            raise ValueError("Specify the exact adb device serial")
        if pid is not None and not 0 < pid <= 2**31 - 1:
            raise ValueError("Expected a positive Android process ID")
        process = process_name(package, process)
        directories, mapping = paths(symbol_dirs, source_map)
        self.runtime.check()
        with self.lock:
            active = 0
            for session in self.sessions.values():
                connection = session.connection
                if session.phase in {"attaching", "attached", "closing"}:
                    active += 1
                    if (serial, process) == (connection.serial, connection.process_name):
                        raise AndroidError("An Android session already owns this device and process name")
            if active >= 8:
                raise AndroidError("Too many active Android sessions")
            if len(self.sessions) >= 32:
                retired = next(key for key, session in self.sessions.items()
                               if session.phase in {"failed", "closed"})
                del self.sessions[retired]
            session_id = uuid4().hex
            connection = Connection(self.runtime, session_id, serial, package, process,
                                    pid, abi, directories, mapping)
            connection.activity = activity
            connection.mode = mode
            connection.java_breakpoints = java_breakpoints or []
            connection.native_breakpoints = native_breakpoints or []
            if mode != "java":
                if self.symbol_cache is None:
                    self.symbol_cache = tempfile.TemporaryDirectory(prefix="aidb-android-cache-")
                connection.symbol_cache = Path(self.symbol_cache.name)
            if mode != "native" and self.monitor is None:
                self.monitor = Thread(target=self.monitor_java, daemon=True, name="aidb-android-events")
                self.monitor.start()
            self.sessions[session_id] = Session(connection)
            return self.submit(session_id, "attach", deadline_ms=deadline_ms)

    def monitor_java(self):
        while not self.monitor_stop.wait(0.15):
            with self.lock:
                for session in self.sessions.values():
                    connection = session.connection
                    if (session.phase != "attached" or not connection.java or connection.java_error
                            or connection.mode == "native" or not session.jobs[session.current].done()
                            or session.event_job and not session.event_job.done()):
                        continue
                    try:
                        if connection.java.pending():
                            session.event_job = session.executor.submit(self.drain_java, connection)
                    except (OSError, ValueError) as error:
                        connection.java_error = str(error)

    @staticmethod
    def drain_java(connection):
        try:
            connection.poll_java_events()
        except WorkerLost as error:
            connection.java_error = f"Native status became unavailable; target state is unknown: {error}"
            try:
                connection.worker.close()
            except (OSError, ValueError) as cleanup:
                connection.java_error += f"; worker cleanup failed: {cleanup}"
            finally:
                connection.worker = None
        except (AndroidError, OSError, ValueError) as error:
            connection.java_error = str(error)
            connection.java.close()

    def get(self, session_id):
        try:
            return self.sessions[session_id]
        except KeyError as error:
            raise ValueError("Unknown Android session") from error

    def listing(self):
        with self.lock:
            return {"sessions": [
                {"sessionId": session_id, "serial": session.connection.serial,
                 "package": session.connection.package, "process": session.connection.process_name,
                 "pid": session.connection.pid, "mode": session.connection.mode,
                 "phase": session.phase, "jobId": session.current}
                for session_id, session in self.sessions.items()]}

    def submit(self, session_id, operation, *, deadline_ms=30_000, **arguments):
        with self.lock:
            session = self.get(session_id)
            if session.current and not session.jobs[session.current].done():
                raise AndroidError("Android session is busy; poll its active job")
            if operation != "attach" and session.phase != "attached":
                raise AndroidError(f"Android session is {session.phase}")
            if operation == "detach":
                session.phase = "closing"
            if len(session.jobs) >= 64:
                expired = next(iter(session.jobs))
                del session.jobs[expired]
                del session.budgets[expired]
            job_id = uuid4().hex
            session.current = job_id
            budget = session.budgets[job_id] = Budget(deadline_ms / 1000)
            session.jobs[job_id] = session.executor.submit(self.run, session, operation, arguments, budget)
            return {"sessionId": session_id, "jobId": job_id, "state": "pending"}

    def run(self, session, operation, arguments, budget):
        connection = session.connection
        phase = None
        token = current_job.set(budget)
        try:
            checkpoint(operation)
            if operation == "attach":
                result = connection.attach()
                result.update(abi=connection.abi, serial=connection.serial)
                phase = "attached"
            else:
                if operation == "wait_stop":
                    result = self.wait_stop(connection, **arguments)
                elif operation == "control":
                    result = connection.native_control(**arguments)
                elif operation == "detach":
                    result = connection.detach()
                elif operation.startswith("java_"):
                    result = connection.java_operation(operation[5:], **arguments)
                else:
                    if connection.worker is None:
                        raise AndroidError("No native debugger is attached to this session")
                    result = connection.worker.call(operation, **arguments)
                if operation == "detach":
                    phase = "closed"
            receipt = {"state": "completed", "result": result}
        except Exception as error:
            lost = isinstance(error, (WorkerLost, OSError))
            if lost or operation in {"attach", "detach"}:
                phase = "failed"
            stopped = isinstance(error, JobStopped) or isinstance(error.__cause__, JobStopped)
            receipt = {"state": "cancelled" if stopped and budget.cancelled.is_set() else "failed",
                       "error": str(error)}
            target_may_have_changed = (connection.attach_requested if operation == "attach"
                                       else lost or operation == "detach")
            if target_may_have_changed:
                receipt["targetState"] = "unknown"
        finally:
            current_job.reset(token)
        if phase in {"failed", "closed"}:
            budget.stage = "cleanup"
            cleanup_token = current_job.set(Budget(12))
            try:
                errors = connection.cleanup()
            except Exception as error:
                errors = [str(error)]
            finally:
                current_job.reset(cleanup_token)
            if errors:
                receipt["cleanupErrors"] = errors
        if phase:
            with self.lock:
                session.phase = phase
                if phase in {"failed", "closed"}:
                    session.executor.shutdown(wait=False)
        return receipt

    def wait_stop(self, connection, action, thread_id, after_stop_id, wait_ms, depth, limit):
        worker = connection.worker
        if worker is None:
            raise AndroidError("No native debugger is attached to this session")
        status = worker.call("inspect", query="status")
        previous = status["stopId"] if action or after_stop_id is None else after_stop_id
        if action:
            checkpoint("request_execution")
            connection.native_control(action, thread_id)
        checkpoint("waiting_for_stop")
        deadline = time.monotonic() + wait_ms / 1000
        while True:
            status = worker.call("inspect", query="status")
            if status["state"] in {"exited", "detached"}:
                return {"outcome": status["state"], "status": status}
            if status["state"] in {"stopped", "crashed", "suspended"} and status["stopId"] > previous:
                checkpoint("capture_stop")
                return {"outcome": "stopped", **worker.call("snapshot", depth=depth, limit=limit)}
            if status["state"] == "running" and connection.java and connection.mode == "dual":
                java = connection.java_operation("inspect", query="status")
                if java["state"] == "stopped":
                    checkpoint("capture_java_stop")
                    return {"outcome": "java_stop", "status": status, "java": java,
                            **connection.java_operation("snapshot", status=java)}
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                return {"outcome": "wait_timeout", "status": status}
            time.sleep(min(remaining, checkpoint(), 0.05))

    def cancel(self, session_id, job_id):
        with self.lock:
            session = self.get(session_id)
            job = session.jobs.get(job_id)
            if job is None:
                raise ValueError("Unknown or expired Android job")
            if not job.done():
                session.budgets[job_id].cancelled.set()
            return {"sessionId": session_id, "jobId": job_id, "cancellationRequested": not job.done()}

    def job(self, session_id, job_id):
        with self.lock:
            session = self.get(session_id)
            future = session.jobs.get(job_id)
            if future is None:
                raise ValueError("Unknown or expired Android job")
            budget = session.budgets[job_id]
        result = future.result() if future.done() else {
            "state": "pending", "stage": budget.stage, "cancellationRequested": budget.cancelled.is_set()}
        return {"sessionId": session_id, "jobId": job_id, **result}

    def shutdown(self):
        self.monitor_stop.set()
        if self.monitor:
            self.monitor.join(timeout=1)
        with self.lock:
            sessions = list(self.sessions.items())
            for _, session in sessions:
                if session.current and session.phase != "closing" and not session.jobs[session.current].done():
                    session.budgets[session.current].cancelled.set()
        for _, session in sessions:
            if session.current:
                session.jobs[session.current].result()
        for session_id, session in sessions:
            if session.phase == "attached":
                self.submit(session_id, "detach")
        for _, session in sessions:
            session.executor.shutdown()
        if self.symbol_cache:
            self.symbol_cache.cleanup()
