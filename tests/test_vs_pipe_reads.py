"""Real Windows pipe I/O using an owned Python protocol fixture, not a VS target."""
import json
import os
import struct
import sys
import threading
import unittest
import uuid
from unittest.mock import patch

from mcp_server import bootstrap
bootstrap.ensure_deps()
from mcp_server import vs_bridge, vs_tool_registry


@unittest.skipUnless(sys.platform == 'win32', 'Windows pipe I/O')
class PipeReadTests(unittest.TestCase):
    def exchange(self, mode):
        import win32file
        import win32pipe
        name = 'AIDebuggerVS_fixture_' + uuid.uuid4().hex
        handle = win32pipe.CreateNamedPipe(
            '\\\\.\\pipe\\' + name, win32pipe.PIPE_ACCESS_DUPLEX,
            win32pipe.PIPE_TYPE_BYTE | win32pipe.PIPE_READMODE_BYTE | win32pipe.PIPE_WAIT,
            1, 65536, 65536, 1000, None)
        stop, errors = threading.Event(), []
        identity = {'processId': os.getpid(), 'processStartTimeUtcTicks': 123,
                    'serverInstanceId': uuid.uuid4().hex,
                    'protocolCatalogHash': vs_tool_registry.DEBUG_METHOD_CATALOG_HASH,
                    'debugSessionGeneration': 0, 'breakSequence': 0, 'contextEpoch': 0}
        registration = {'protocol': 'aidb/2', 'pipeName': name, 'recoveryPipeName': name,
                        'controlPipeName': name, 'authToken': 'owned-fixture-token',
                        'capabilities': ['debug.read', 'server.control', 'debug.execution.control'], **identity}
        def read_exact(size):
            data = b''
            while len(data) < size:
                data += win32file.ReadFile(handle, size - len(data))[1]
            return data
        def server():
            try:
                win32pipe.ConnectNamedPipe(handle, None)
                request = json.loads(read_exact(struct.unpack('>I', read_exact(4))[0]))
                assert request['meta']['authToken'] == registration['authToken']
                if mode == 'timeout':
                    stop.wait(2)
                    return
                if mode == 'disconnect':
                    return
                body = json.dumps({'protocol': 'aidb/2', 'status': 'ok', 'id': request['id'],
                                   'server': identity, 'result': {'text': '\u7ebf\u7a0b\U0001f680'}}).encode()
                frame = struct.pack('>I', len(body)) + body
                if mode == 'fast':
                    win32file.WriteFile(handle, frame)
                    stop.wait(2)
                    return
                # Make both header and payload arrive in short independent chunks.
                for part in (frame[:1], frame[1:4], frame[4:13], frame[13:]):
                    if stop.wait(.003):
                        return
                    win32file.WriteFile(handle, part)
                stop.wait(2)
            except Exception as error:
                errors.append(error)
            finally:
                win32file.CloseHandle(handle)
        worker = threading.Thread(target=server, daemon=True)
        worker.start()
        try:
            # Supplying a trusted registration is confined to this owned fixture.
            # Production discovery/process/pipe identity validation is unchanged.
            return vs_bridge._sync_call_to_pipe(
                name, 'get_debug_status' if mode in ('fragments', 'fast') else 'continue', {}, timeout=.15,
                registration_snapshot=registration)
        finally:
            stop.set()
            worker.join(2)
            self.assertFalse(worker.is_alive())
            self.assertFalse(errors, errors)

    def test_fragmented_response_waits_for_io_without_sleep_polling(self):
        with patch.object(vs_bridge.time, 'sleep', side_effect=AssertionError('Polling sleep')):
            self.assertEqual(self.exchange('fragments'), {'text': '\u7ebf\u7a0b\U0001f680'})

    def test_pending_read_timeout_cancels_and_reaps_before_cleanup(self):
        with patch.object(vs_bridge, '_cancel_pending_io', wraps=vs_bridge._cancel_pending_io) as cancel:
            with self.assertRaises(vs_bridge.VSBridgeError) as raised:
                self.exchange('timeout')
            self.assertEqual(raised.exception.code, 'PIPE_TIMEOUT')
            self.assertTrue(raised.exception.outcome_unknown)
            self.assertEqual(cancel.call_count, 1)

    def test_disconnected_server_preserves_unknown_outcome(self):
        with self.assertRaises(vs_bridge.VSBridgeError) as raised:
            self.exchange('disconnect')
        self.assertEqual(raised.exception.code, 'PIPE_DISCONNECTED')
        self.assertTrue(raised.exception.outcome_unknown)


if __name__ == '__main__':
    unittest.main()
