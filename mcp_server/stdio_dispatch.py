"""Concurrent stdio dispatch shared by independent backends; no debugger imports."""
from concurrent.futures import ThreadPoolExecutor
import json
import sys
import threading


def serve(mcp, stdin=None, stdout=None, *, fast_tools=(), control_tools=()):
    stdin = stdin or sys.stdin.buffer
    stdout = stdout or sys.stdout.buffer
    output_lock = threading.Lock()
    disconnected = threading.Event()
    fast_tools, control_tools = frozenset(fast_tools), frozenset(control_tools)

    def write(response):
        if response is not None and not disconnected.is_set():
            try:
                with output_lock:
                    # JSON escaping also handles isolated UTF-16 surrogates from
                    # request IDs or debugger strings without dropping the reply.
                    stdout.write(json.dumps(response).encode("utf-8") + b"\n")
                    stdout.flush()
            except (BrokenPipeError, OSError):
                disconnected.set()

    def dispatch(request):
        setattr(mcp._transport_session_id, "data", "stdio:default")
        try:
            response = mcp.registry.dispatch(request)
            write(response)
        except (BrokenPipeError, OSError):
            disconnected.set()
        finally:
            setattr(mcp._transport_session_id, "data", None)

    def lane(request):
        if not isinstance(request, dict):
            return "work"
        method = request.get("method")
        if method == "notifications/cancelled":
            return "fast"
        if method in ("initialize", "ping", "tools/list", "notifications/initialized"):
            return "fast"
        params = request.get("params")
        if method == "tools/call" and isinstance(params, dict):
            name = params.get("name")
            if isinstance(name, str):
                if name in control_tools:
                    return "control"
                if name in fast_tools:
                    return "fast"
        return "work"

    with ThreadPoolExecutor(max_workers=8, thread_name_prefix="aidb-rpc") as work, \
         ThreadPoolExecutor(max_workers=4, thread_name_prefix="aidb-status") as fast, \
         ThreadPoolExecutor(max_workers=2, thread_name_prefix="aidb-control") as control:
        pools = {"work": work, "fast": fast, "control": control}
        slots = {"work": threading.BoundedSemaphore(8), "fast": threading.BoundedSemaphore(4),
                 "control": threading.BoundedSemaphore(2)}
        while not disconnected.is_set():
            line = stdin.readline()
            if not line:
                break
            if line.strip():
                try:
                    request = json.loads(line)
                except (ValueError, UnicodeError):
                    request = None  # Registry supplies the existing parse error.
                if not isinstance(request, dict) or request.get("method") == "notifications/cancelled":
                    # Invalid input cannot invoke a tool. Cancellation only sets
                    # the registry's request event; do not queue it behind work.
                    dispatch(line.strip())
                    continue
                category = lane(request)
                permit = slots[category]
                if not permit.acquire(blocking=False):
                    # No unbounded queue or delayed mutation after client timeout.
                    # A notification has no response, as required by JSON-RPC.
                    if isinstance(request, dict) and "id" in request:
                        write({"jsonrpc": "2.0", "id": request["id"], "error": {
                            "code": -32000, "message": "Dispatcher busy; request was not started",
                            "data": {"code": "STDIO_BUSY_NOT_STARTED", "requestStarted": False}}})
                    continue
                try:
                    future = pools[category].submit(dispatch, line.strip())
                    future.add_done_callback(lambda _, permit=permit: permit.release())
                except Exception:
                    permit.release()
                    raise
