import pathlib
import sys
import unittest
from unittest.mock import AsyncMock, patch


ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from mcp_server.tool_modules import memory_tools


class MemorySearchContinuationTests(unittest.IsolatedAsyncioTestCase):
    async def test_interrupted_chunk_search_resumes_all_boundary_candidates(self):
        # Use small pages so the byte reader, rather than hard-coded expected
        # payloads, proves that continuing the returned cursor finds the match.
        data = b"xxxxxxABCDEFxxxxxxxxxxxx"
        pattern = b"ABCDEF"
        chunk_size = 8
        start_offset = 3
        search_size = len(data) - start_offset

        async def read_page(method, params, **kwargs):
            offset = params["startOffset"]
            page = data[offset:offset + params["searchSize"]]
            matches = [
                f"0x{0x1000 + offset + index:X}"
                for index in range(len(page) - len(pattern) + 1)
                if page[index:index + len(pattern)] == pattern
            ]
            return {
                "status": "ok",
                "partial": False,
                "truncated": False,
                "matches": matches,
                "bytesSearched": len(page),
                "bytesAttempted": len(page),
                "patternBytes": [f"{value:02X}" for value in pattern],
                "capability": "live_process_memory",
            }

        async def read_first(method, params, **kwargs):
            return await read_page(method, params, **kwargs), {"pipeName": "target"}

        async def read_exact(target, method, params, **kwargs):
            return await read_page(method, params, **kwargs)

        for interruption in ("transport", "deadline"):
            with self.subTest(interruption=interruption):
                failure = memory_tools.vs_bridge.VSBridgeError(
                    "pipe reply lost", code="PIPE_TIMEOUT"
                )
                with patch.object(memory_tools, "MAX_MEMORY_SEARCH_CHUNK_SIZE", chunk_size), \
                     patch.object(memory_tools.ide_bridge, "call_with_target", side_effect=read_first), \
                     patch.object(memory_tools.vs_bridge, "call_pipe_exact", AsyncMock(side_effect=failure)), \
                     patch.object(memory_tools, "remaining_tool_timeout", side_effect=(
                         [10.0, None] if interruption == "deadline" else [10.0, 10.0]
                     )):
                    first = (await memory_tools.search_memory(
                        "0x1000", "ABCDEF", pattern_format="text",
                        start_offset=start_offset, search_size=search_size,
                    ))["result"]

                self.assertTrue(first["partial"])
                self.assertEqual([], first["matches"])
                self.assertEqual(chunk_size, first["bytesSearched"])
                with patch.object(memory_tools, "MAX_MEMORY_SEARCH_CHUNK_SIZE", chunk_size), \
                     patch.object(memory_tools.ide_bridge, "call_with_target", side_effect=read_first), \
                     patch.object(memory_tools.ide_bridge, "call", side_effect=read_page), \
                     patch.object(memory_tools.vs_bridge, "call_pipe_exact", side_effect=read_exact):
                    resumed = (await memory_tools.search_memory(
                        first["nextStartExpression"], "ABCDEF", pattern_format="text",
                        start_offset=first["nextStartOffset"], search_size=first["remainingBytes"],
                    ))["result"]
                self.assertEqual(["0x1006"], resumed["matches"])
                self.assertFalse(resumed["hasMore"])


if __name__ == "__main__":
    unittest.main()
