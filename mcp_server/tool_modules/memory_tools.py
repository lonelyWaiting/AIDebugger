from typing import Any, Dict, List, Optional
import asyncio

try:
    from .. import ide_bridge
    from ..mcp_app import mcp
    from ..tool_context import *
except ImportError:
    import ide_bridge
    from mcp_app import mcp
    from tool_context import *
@mcp.tool()
async def read_memory(address: str, size: int = 256, timeout_seconds: Optional[float] = None) -> str:
    """
    读取指定内存地址的原始字节数据。用于分析内存布局、检查数据结构、排查内存损坏问题。
    返回十六进制转储、ASCII视图和格式化的内存视图。地址可以是十六进制或变量表达式。

    **触发场景**: 当用户提到"内存"、"memory"、"内存损坏"、"内存布局"、"查看地址"、"hex dump"时调用此工具。

    Args:
        address: 内存地址，支持十六进制（如0x7ff61234）或表达式（如&myVariable、myPointer）
        size: 要读取的字节数，默认256，最大4096
        timeout_seconds: 等待 IDE 响应的最长秒数
    """
    # Clamp size
    size = max(1, min(size, 4096))

    try:
        result = await ide_bridge.call("read_memory", {
            "address": address,
            "size": size
        }, timeout=clamp_tool_timeout(timeout_seconds, MEMORY_TIMEOUT_SECONDS))
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "read_memory")

@mcp.tool()
async def get_disassembly(
    lines_before: int = 20,
    lines_after: int = 20,
    timeout_seconds: Optional[float] = None
) -> str:
    """
    获取当前断点/暂停位置附近的反汇编代码。用于分析底层机器指令、排查优化代码问题、理解编译器生成的代码。
    返回汇编指令、机器码和对应的内存地址。

    **触发场景**: 当用户提到"反汇编"、"disassembly"、"汇编代码"、"机器指令"、"底层分析"、"asm"时调用此工具。

    Args:
        lines_before: 当前指令之前要显示的行数，默认20
        lines_after: 当前指令之后要显示的行数，默认20
        timeout_seconds: 等待 IDE 响应的最长秒数
    """
    try:
        result = await ide_bridge.call("get_disassembly", {
            "linesBefore": lines_before,
            "linesAfter": lines_after
        }, timeout=clamp_tool_timeout(timeout_seconds, DISASSEMBLY_TIMEOUT_SECONDS))
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "get_disassembly")

@mcp.tool()
async def get_disassembly_at(
    address: str,
    count: int = 50,
    timeout_seconds: Optional[float] = None
) -> str:
    """
    获取指定内存地址处的反汇编代码。可以用于查看特定函数或代码段的汇编指令。
    地址可以是十六进制格式（如0x7ff61234）或表达式（如&myFunction）。

    Args:
        address: 起始内存地址，支持十六进制（如0x7ff61234）或表达式（如&myFunction）
        count: 要获取的指令数量，默认50
        timeout_seconds: 等待 IDE 响应的最长秒数
    """
    try:
        result = await ide_bridge.call("get_disassembly_at", {
            "address": address,
            "count": count
        }, timeout=clamp_tool_timeout(timeout_seconds, DISASSEMBLY_TIMEOUT_SECONDS))
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "get_disassembly_at")

@mcp.tool()
async def get_registers() -> str:
    """
    获取CPU寄存器值（RAX, RBX, RCX, RDX, RSP, RBP, RIP, RFLAGS等）。
    用于底层调试和分析。包括通用寄存器、段寄存器和XMM寄存器。

    **触发场景**: 当用户提到"寄存器"、"register"、"RAX"、"RSP"、"RIP"、"CPU状态"时调用此工具。
    """
    try:
        result = await ide_bridge.call("get_registers")
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "get_registers")

@mcp.tool()
async def add_watch(expression: str) -> str:
    """
    添加一个监视表达式到VS的Watch窗口，并返回当前值。

    Args:
        expression: 要监视的表达式
    """
    try:
        result = await ide_bridge.call("add_watch", {
            "expression": expression
        })
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "add_watch")

@mcp.tool()
async def search_memory(
    start_address: str,
    pattern: str,
    max_results: int = 100,
    search_size: int = 0x10000,
    pattern_format: str = "auto",
    start_offset: int = 0,
    timeout_seconds: Optional[float] = None
) -> str:
    """
    在内存中搜索指定模式。可用于查找特定数据或字符串。

    Args:
        start_address: 搜索起始地址（十六进制如0x7ff61234或表达式如&myVar）
        pattern: 搜索模式，可以是十六进制字节(如"4D 5A")或ASCII字符串
        max_results: 最大返回结果数，默认100
        search_size: 搜索范围大小（字节），默认64KB；Visual Studio 单次后端调用最大1MB，较大范围会自动分片覆盖
        pattern_format: auto/text/hex。auto 仅把带空格的十六进制串当字节，"41" 默认按 ASCII 文本搜索；hex 可显式搜索单字节
        start_offset: 从 start_address 解析出的基址开始增加的字节偏移，便于从 nextStartOffset 继续搜索
        timeout_seconds: 等待 IDE 响应的最长秒数
    """
    # Clamp parameters to safe ranges. The total requested range is not reduced to
    # the VSIX per-call limit; larger searches are chunked below.
    search_size = max(1, min(search_size, MAX_MEMORY_SEARCH_SIZE))
    max_results = max(1, min(max_results, 10000))
    start_offset = max(0, int(start_offset))
    tool_timeout = clamp_tool_timeout(
        timeout_seconds,
        min(MAX_TOOL_TIMEOUT_SECONDS, max(MEMORY_TIMEOUT_SECONDS, ((search_size + MAX_MEMORY_SEARCH_CHUNK_SIZE - 1) // MAX_MEMORY_SEARCH_CHUNK_SIZE) * MEMORY_TIMEOUT_SECONDS))
    )
    deadline = asyncio.get_running_loop().time() + tool_timeout

    try:
        if search_size <= MAX_MEMORY_SEARCH_CHUNK_SIZE:
            result = await ide_bridge.call("search_memory", {
                "startAddress": start_address,
                "pattern": pattern,
                "maxResults": max_results,
                "searchSize": search_size,
                "patternFormat": pattern_format,
                "startOffset": start_offset
            }, timeout=remaining_tool_timeout(deadline, MEMORY_TIMEOUT_SECONDS) or 1.0)
            return format_result(result)

        matches: List[str] = []
        seen_matches = set()
        chunks: List[Dict[str, Any]] = []
        bytes_searched = 0
        failed_reads = 0
        pattern_bytes = None
        status = "ok"
        partial = False
        error = None
        offset = 0
        overlap = 0
        continuation_expression = None
        continuation_offset = None

        while offset < search_size:
            remaining_timeout = remaining_tool_timeout(deadline, MEMORY_TIMEOUT_SECONDS)
            if remaining_timeout is None:
                partial = True
                status = "partial"
                error = "Search stopped because search_memory timeout budget was exhausted"
                break

            chunk_size = min(MAX_MEMORY_SEARCH_CHUNK_SIZE, search_size - offset)
            absolute_offset = start_offset + offset
            chunk_start = start_address if absolute_offset == 0 else f"{start_address} + byte offset 0x{absolute_offset:X}"
            chunk_result = await ide_bridge.call("search_memory", {
                "startAddress": start_address,
                "pattern": pattern,
                "maxResults": max_results - len(matches),
                "searchSize": chunk_size,
                "patternFormat": pattern_format,
                "startOffset": absolute_offset
            }, timeout=remaining_timeout)

            if not isinstance(chunk_result, dict):
                partial = True
                status = "partial"
                error = str(chunk_result)
                break

            if pattern_bytes is None and chunk_result.get("patternBytes"):
                pattern_bytes = chunk_result.get("patternBytes")
                overlap = max(0, len(pattern_bytes) - 1)

            chunk_matches = chunk_result.get("matches") or []
            for match in chunk_matches:
                if match not in seen_matches:
                    seen_matches.add(match)
                    matches.append(match)
                    if len(matches) >= max_results:
                        break

            bytes_searched += int(chunk_result.get("bytesSearched") or 0)
            failed_reads += int(chunk_result.get("failedReads") or 0)
            chunk_status = str(chunk_result.get("status") or "unavailable").lower()
            chunk_truncated = bool(chunk_result.get("truncated"))
            chunk_partial = bool(chunk_result.get("partial")) or chunk_status not in ("ok",)
            chunk_error = chunk_result.get("error")
            chunks.append({
                "startAddress": chunk_start,
                "requestedBytes": chunk_size,
                "bytesSearched": chunk_result.get("bytesSearched", 0),
                "status": chunk_result.get("status", "unavailable"),
                "partial": bool(chunk_result.get("partial")),
                "truncated": chunk_truncated,
                "nextStartAddress": chunk_result.get("nextStartAddress"),
                "matchCount": len(chunk_matches),
                "error": chunk_error,
            })

            if chunk_truncated:
                partial = True
                status = "partial"
                continuation_expression = chunk_result.get("nextStartAddress") or chunk_start
                continuation_offset = None
                error = "Search stopped after reaching max_results; continue from nextStartExpression or increase max_results"
                break

            step = max(1, chunk_size - overlap)
            next_offset = offset + step

            if chunk_partial:
                partial = True
                status = "partial"
                error = chunk_error or f"Chunk at {chunk_start} was partial"
                break

            if len(matches) >= max_results:
                partial = True
                status = "partial"
                continuation_expression = chunk_result.get("nextStartAddress") or start_address
                continuation_offset = None if chunk_result.get("nextStartAddress") else start_offset + next_offset
                error = "Search stopped after reaching max_results; continue from nextStartExpression or increase max_results"
                break

            offset = next_offset

        next_start_expression = None
        next_start_offset = None
        remaining_bytes = 0
        if continuation_expression:
            next_start_expression = continuation_expression
            next_start_offset = continuation_offset
            remaining_bytes = None
        elif offset < search_size:
            next_start_expression = start_address
            next_start_offset = start_offset + offset
            remaining_bytes = search_size - offset

        result = {
            "pattern": pattern,
            "patternFormat": pattern_format,
            "status": status,
            "patternBytes": pattern_bytes,
            "matches": matches,
            "totalFound": len(matches),
            "bytesSearched": bytes_searched,
            "requestedBytes": search_size,
            "failedReads": failed_reads,
            "partial": partial,
            "chunked": True,
            "chunkSize": MAX_MEMORY_SEARCH_CHUNK_SIZE,
            "chunkCount": len(chunks),
            "chunks": chunks,
            "nextStartExpression": next_start_expression,
            "nextStartOffset": next_start_offset,
            "remainingBytes": remaining_bytes,
        }
        if error:
            result["error"] = error
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "search_memory")
