"""Activation polling latency and identity boundaries, without a live VS target."""
import unittest
from unittest.mock import AsyncMock, patch
from mcp_server import tool_context


class ActivationLatencyTests(unittest.IsolatedAsyncioTestCase):
    async def test_ready_target_is_probed_before_any_poll_delay(self):
        status = {"isDebugging": True, "isPaused": True}
        target = {"pipeName": "AIDebuggerVS_123"}
        with patch.object(tool_context.vs_bridge, "call_pipe_exact", new=AsyncMock(return_value=status)) as call, \
             patch.object(tool_context.asyncio, "sleep", new=AsyncMock()) as sleep:
            result = await tool_context.wait_for_debugging_to_start(
                {"success": True, "commandIssued": True, "executablePath": "owned.dmp"}, 1, True, target)
        self.assertEqual(result["debuggerActivationStatus"], "active")
        sleep.assert_not_awaited()
        self.assertEqual(call.await_count, 1)
        self.assertEqual(call.call_args.args[0], target)
        self.assertFalse(result["retryCommand"])

    async def test_pending_activation_backs_off_without_spinning_or_reissuing_start(self):
        class Clock:
            now = 0
            def time(self):
                return self.now
        clock, delays = Clock(), []
        async def sleep(delay):
            delays.append(delay)
            clock.now += delay
        statuses = [{"isDebugging": False}] * 5 + [{"isDebugging": True}]
        with patch.object(tool_context.ide_bridge, "call", new=AsyncMock(side_effect=statuses)) as call, \
             patch.object(tool_context.asyncio, "sleep", new=sleep), \
             patch.object(tool_context.asyncio, "get_running_loop", return_value=clock):
            result = await tool_context.wait_for_debugging_to_start({"success": True}, 5, True)
        self.assertEqual(result["debuggerActivationStatus"], "active")
        self.assertEqual(delays, [.05, .1, .2, .25, .25])
        self.assertEqual(call.await_count, 6)
        self.assertTrue(all(args.args[0] == "get_debug_status" for args in call.call_args_list))


if __name__ == "__main__":
    unittest.main()
