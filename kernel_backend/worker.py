"""Private JSON-lines worker over inherited anonymous pipes; no listening socket."""
import json
import queue
import sys
import threading
import hashlib
import re
import os
from pathlib import Path

from .contracts import KernelError, validate_operation
from .engine import Engine


def raw_lines():
    """Avoid daemon threads holding buffered stdin during Python finalization."""
    pending = b""
    while True:
        chunk = os.read(sys.stdin.fileno(), 65536)
        if not chunk:
            return
        pending += chunk
        if len(pending) > 1048576:
            raise KernelError("FRAME_TOO_LARGE", "Worker request exceeds one MiB")
        while b"\n" in pending:
            line, pending = pending.split(b"\n", 1)
            yield line


def run():
    lines = raw_lines()
    config = json.loads(next(lines))
    requests = queue.Queue(maxsize=8)
    write_lock, engine_lock = threading.Lock(), threading.Lock()
    engine = None
    exiting = threading.Event()
    cancel_start = threading.Event()
    # KDNET credentials must not appear in status, command output or receipts.
    secrets = [config.get("connection", "")]
    connection = config.get("connection", "")
    secrets.extend(re.findall(r"key\s*=\s*([^,\s]+)", connection, re.I))

    def redact(value):
        for secret in secrets:
            if secret:
                value = value.replace(secret, "<redacted>")
        return value

    def send(message):
        with write_lock:
            sys.stdout.buffer.write((json.dumps(message, ensure_ascii=False) + "\n").encode("utf-8"))
            sys.stdout.buffer.flush()

    def error(request_id, exception):
        send({"id": request_id, "error": {"code": getattr(exception, "code", "ENGINE_ERROR"),
                                          "message": redact(str(exception))}})

    def close_engine():
        nonlocal engine
        with engine_lock:
            if engine:
                engine.close()
                engine = None

    def pending_close():
        while not requests.empty():
            request = requests.get_nowait()
            if request["operation"] == "close":
                try:
                    close_engine()
                    send({"id": request["id"], "result": {"state": "closed", "behavior": "startup_cancelled", "nativeCloseConfirmed": True, "targetStateKnown": False, "targetResumed": False}})
                except Exception as exception:
                    error(request["id"], exception)

    def reader():
        try:
            for line in lines:
                request = {}
                try:
                    request = json.loads(line)
                    if request["operation"] == "interrupt":
                        if not request.get("exit_wait", False) and not (config["mode"] == "remote" and config["full_access"]):
                            raise KernelError("CAPABILITY_DENIED", "Break-in requires remote full_access")
                        with engine_lock:
                            if engine is None:
                                if request.get("exit_wait", False):
                                    cancel_start.set()
                                else:
                                    raise KernelError("NOT_READY", "Engine is not initialized")
                            else:
                                engine.interrupt(request.get("exit_wait", False))
                        send({"id": request["id"], "result": {"requested": True, "confirmedPaused": False}})
                    else:
                        validate_operation(config["mode"], config["full_access"], request["operation"], request["arguments"])
                        requests.put_nowait(request)
                        if request["operation"] == "close":
                            exiting.set()
                            with engine_lock:
                                if engine:
                                    try:
                                        engine.interrupt(exit_wait=True)
                                    except Exception:
                                        # Supervisor bounds helper closure even
                                        # when this engine cannot interrupt attach.
                                        pass
                except Exception as exception:
                    error(request.get("id"), exception)
        except Exception as exception:
            error(None, exception)
        finally:
            exiting.set()
            # Parent EOF also needs bounded cleanup when the engine refuses
            # interruption. This timer ends only this helper, never a target.
            guard = threading.Timer(2.5, lambda: os._exit(0))
            guard.daemon = True
            guard.start()
            with engine_lock:
                if engine:
                    try:
                        engine.interrupt(exit_wait=True)
                    except Exception:
                        pass

    threading.Thread(target=reader, name="kernel-input", daemon=True).start()
    try:
        initialized = Engine(redact)
        with engine_lock:
            engine = initialized
        if exiting.is_set() or cancel_start.is_set():
            error(config["open_id"], KernelError("CANCELLED", "Connection cancelled before engine start"))
            pending_close()
            return
        send({"status": {"state": "connecting"}})
        try:
            if config["mode"] == "dump":
                path = Path(config["dump_path"])
                stat = path.stat()
                with path.open("rb") as stream:
                    header = stream.read(4096)
                target = config["target"]
                if (stat.st_size != target["size"] or stat.st_mtime_ns != target["mtimeNs"] or
                        hashlib.sha256(header).hexdigest() != target["headerSha256"]):
                    raise KernelError("STALE_TARGET", "Dump changed between request and engine startup")
            result = engine.open(config)
            send({"id": config["open_id"], "result": {**result, **engine.output.result()}})
            send({"status": result})
        except Exception as exception:
            error(config["open_id"], exception)
            send({"status": {"state": "failed"}})
            pending_close()
            return
        while True:
            if exiting.is_set() and requests.empty():
                break
            try:
                request = requests.get(timeout=0.05)
            except queue.Empty:
                if config["mode"] == "remote" and engine.status()["state"] == "running":
                    engine.output.reset()
                    try:
                        engine.wait()
                        engine.verify_target()
                        send({"status": engine.status(), "eventOutput": engine.output.result()})
                    except Exception as exception:
                        send({"status": {"state": "out_of_sync"}, "eventError": {"code": getattr(exception, "code", "ENGINE_ERROR")}})
                continue
            if request["operation"] == "close":
                close_engine()
                send({"id": request["id"], "result": {"state": "closed", "behavior": "passive_detach", "nativeCloseConfirmed": True, "targetResumed": False}})
                break
            try:
                result = engine.perform(request["operation"], request["arguments"])
                send({"id": request["id"], "result": result})
                send({"status": {key: result[key] for key in ("state", "executionStatusRaw", "snapshotStable", "snapshotStatus", "stopReason", "stopReasonRaw", "eventDescription")}})
            except Exception as exception:
                error(request["id"], exception)
                try:
                    engine.verify_target()
                    send({"status": engine.status()})
                except Exception:
                    send({"status": {"state": "failed"}})
                    break
    except Exception as exception:
        error(config["open_id"], exception)
        pending_close()
    finally:
        with engine_lock:
            if engine:
                try:
                    engine.close()
                except Exception:
                    pass


if __name__ == "__main__":
    run()
