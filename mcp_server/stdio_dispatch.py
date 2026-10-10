"""Concurrent stdio dispatch shared by independent backends; no debugger imports."""
from concurrent.futures import ThreadPoolExecutor
import json
import sys
import threading


def serve(mcp, stdin=None, stdout=None):
    stdin = stdin or sys.stdin.buffer
    stdout = stdout or sys.stdout.buffer
    output_lock = threading.Lock()
    disconnected = threading.Event()

    def dispatch(request):
        setattr(mcp._transport_session_id, "data", "stdio:default")
        try:
            response = mcp.registry.dispatch(request)
            if response is not None and not disconnected.is_set():
                with output_lock:
                    stdout.write(json.dumps(response, ensure_ascii=False).encode("utf-8") + b"\n")
                    stdout.flush()
        except (BrokenPipeError, OSError):
            disconnected.set()
        finally:
            setattr(mcp._transport_session_id, "data", None)

    with ThreadPoolExecutor(max_workers=8, thread_name_prefix="aidb-rpc") as pool:
        while not disconnected.is_set():
            line = stdin.readline()
            if not line:
                break
            if line.strip():
                pool.submit(dispatch, line.strip())
