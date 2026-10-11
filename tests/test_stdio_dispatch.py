"""Portable starvation/admission proof, not native debugger timing."""
import json
import queue
import threading
import unittest
from types import SimpleNamespace

from mcp_server.stdio_dispatch import serve


class Input:
    def __init__(self):
        self.lines = queue.Queue()
    def readline(self):
        return self.lines.get(timeout=2)
    def put(self, request):
        self.lines.put((json.dumps(request) + "\n").encode())


class Output:
    def __init__(self):
        self.lines = queue.Queue()
    def write(self, data):
        self.lines.put(json.loads(data))
    def flush(self):
        pass


class StdioDispatchTests(unittest.TestCase):
    def harness(self):
        incoming, output = Input(), Output()
        occupied = threading.Barrier(9)
        release = threading.Event()
        calls = []
        cancelled = threading.Event()
        def dispatch(raw):
            try:
                request = json.loads(raw)
            except ValueError:
                return {"jsonrpc": "2.0", "id": None, "error": {"code": -32700}}
            if request.get("method") == "notifications/cancelled":
                cancelled.set()
                return None
            calls.append(request["id"])
            if request["id"] < 8:
                occupied.wait(timeout=2)
                release.wait(timeout=2)
            return {"jsonrpc": "2.0", "id": request["id"], "result": {}}
        mcp = SimpleNamespace(registry=SimpleNamespace(dispatch=dispatch), _transport_session_id=threading.local())
        errors = []
        def run():
            try:
                serve(mcp, incoming, output, fast_tools={"list"}, control_tools={"close"})
            except Exception as error:
                errors.append(error)
                occupied.abort()
        thread = threading.Thread(target=run)
        thread.start()
        for i in range(8):
            incoming.put({"jsonrpc": "2.0", "id": i, "method": "tools/call", "params": {"name": "work"}})
        try:
            occupied.wait(timeout=2)
        except Exception:
            release.set()
            incoming.lines.put(b"")
            thread.join(2)
            if errors:
                raise errors[0]
            raise
        incoming.cancelled = cancelled
        return incoming, output, release, thread, calls, errors

    def finish(self, incoming, release, thread, errors):
        release.set()
        incoming.lines.put(b"")
        thread.join(2)
        self.assertFalse(thread.is_alive())
        self.assertFalse(errors, errors)

    def test_list_and_close_do_not_queue_behind_eight_long_requests(self):
        incoming, output, release, thread, calls, errors = self.harness()
        try:
            incoming.put({"jsonrpc": "2.0", "id": 8, "method": "tools/call", "params": {"name": "list"}})
            incoming.put({"jsonrpc": "2.0", "id": 9, "method": "tools/call", "params": {"name": "close"}})
            self.assertEqual({output.lines.get(timeout=.5)["id"] for _ in range(2)}, {8, 9})
            self.assertFalse(release.is_set())
        finally:
            self.finish(incoming, release, thread, errors)

    def test_saturated_work_is_rejected_before_start_without_late_execution(self):
        incoming, output, release, thread, calls, errors = self.harness()
        try:
            incoming.put({"jsonrpc": "2.0", "id": 8, "method": "tools/call", "params": {"name": "mutation"}})
            result = output.lines.get(timeout=.5)
            self.assertEqual(result["error"]["data"]["code"], "STDIO_BUSY_NOT_STARTED")
            self.assertFalse(result["error"]["data"]["requestStarted"])
        finally:
            self.finish(incoming, release, thread, errors)
        self.assertNotIn(8, calls)

    def test_parse_errors_are_not_silently_dropped_when_work_is_full(self):
        incoming, output, release, thread, calls, errors = self.harness()
        try:
            incoming.lines.put(b"invalid-json\n")
            self.assertEqual(output.lines.get(timeout=.5)["error"]["code"], -32700)
        finally:
            self.finish(incoming, release, thread, errors)

    def test_cancellation_is_delivered_while_work_is_full_without_response(self):
        incoming, output, release, thread, calls, errors = self.harness()
        try:
            incoming.put({"jsonrpc": "2.0", "method": "notifications/cancelled", "params": {"requestId": 0}})
            self.assertTrue(incoming.cancelled.wait(.5))
            self.assertTrue(output.lines.empty())
            self.assertFalse(release.is_set())
        finally:
            self.finish(incoming, release, thread, errors)


if __name__ == "__main__":
    unittest.main()
