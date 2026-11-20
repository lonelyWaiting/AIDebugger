"""Explicit Android session and job lifecycles."""
from concurrent.futures import ThreadPoolExecutor
from threading import RLock
from uuid import uuid4

from .runtime import AndroidError
from .transport import Connection, WorkerLost, paths, process_name


class Session:
    def __init__(self, connection):
        self.connection = connection
        self.executor = ThreadPoolExecutor(max_workers=1, thread_name_prefix="aidb-android")
        self.phase = "attaching"
        self.jobs = {}
        self.current = None


class Sessions:
    def __init__(self, runtime):
        self.runtime = runtime
        self.lock = RLock()
        self.sessions = {}

    def open(self, serial, package, process, pid, abi, symbol_dirs, source_map):
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
            self.sessions[session_id] = Session(connection)
            return self.submit(session_id, "attach")

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
                 "phase": session.phase, "jobId": session.current}
                for session_id, session in self.sessions.items()]}

    def submit(self, session_id, operation, **arguments):
        with self.lock:
            session = self.get(session_id)
            if session.current and not session.jobs[session.current].done():
                raise AndroidError("Android session is busy; poll its active job")
            if operation != "attach" and session.phase != "attached":
                raise AndroidError(f"Android session is {session.phase}")
            if operation == "detach":
                session.phase = "closing"
            if len(session.jobs) >= 64:
                del session.jobs[next(iter(session.jobs))]
            job_id = uuid4().hex
            session.current = job_id
            session.jobs[job_id] = session.executor.submit(self.run, session, operation, arguments)
            return {"sessionId": session_id, "jobId": job_id, "state": "pending"}

    def run(self, session, operation, arguments):
        connection = session.connection
        phase = None
        try:
            if operation == "attach":
                result = connection.attach()
                result.update(abi=connection.abi, serial=connection.serial)
                phase = "attached"
            else:
                result = connection.worker.call(operation, **arguments)
                if operation == "detach":
                    phase = "closed"
            receipt = {"state": "completed", "result": result}
        except Exception as error:
            lost = isinstance(error, (WorkerLost, OSError))
            if lost or operation in {"attach", "detach"}:
                phase = "failed"
            receipt = {"state": "failed", "error": str(error)}
            target_may_have_changed = (connection.attach_requested if operation == "attach"
                                       else lost or operation == "detach")
            if target_may_have_changed:
                receipt["targetState"] = "unknown"
        if phase in {"failed", "closed"}:
            errors = connection.cleanup()
            if errors:
                receipt["cleanupErrors"] = errors
        if phase:
            with self.lock:
                session.phase = phase
                if phase in {"failed", "closed"}:
                    session.executor.shutdown(wait=False)
        return receipt

    def job(self, session_id, job_id):
        with self.lock:
            future = self.get(session_id).jobs.get(job_id)
            if future is None:
                raise ValueError("Unknown or expired Android job")
        result = future.result() if future.done() else {"state": "pending"}
        return {"sessionId": session_id, "jobId": job_id, **result}

    def shutdown(self):
        with self.lock:
            sessions = list(self.sessions.items())
        for _, session in sessions:
            if session.current:
                session.jobs[session.current].result()
        for session_id, session in sessions:
            if session.phase == "attached":
                self.submit(session_id, "detach")
        for _, session in sessions:
            session.executor.shutdown()
