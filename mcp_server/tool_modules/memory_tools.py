from typing import Any, Dict, List, Optional
import asyncio

try:
    from .. import vs_bridge
    from ..tool_runtime import backend as ide_bridge, mcp
    from ..tool_context import *
except ImportError:
    import vs_bridge
    from tool_runtime import backend as ide_bridge, mcp
    from tool_context import *
@mcp.tool()
async def read_memory(address: str, size: int = 256, timeout_seconds: Optional[float] = None) -> str:
    """Read raw bytes from a non-zero numeric address literal.

    To resolve a variable or symbol, first use evaluate_expression/resolve_address
    under the unsafe-evaluation capability, then pass the returned numeric address.
    """
    size = validate_int_range("size", size, 1, 4096)

    try:
        result = await ide_bridge.call("read_memory", {
            "address": validate_address_literal("address", address),
            "size": size
        }, timeout=clamp_tool_timeout(timeout_seconds, MEMORY_TIMEOUT_SECONDS))
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "read_memory")


@mcp.tool()
async def get_dump_memory_ranges(
    start_index: int = 0,
    max_nodes: Optional[int] = None,
    budget_ms: Optional[int] = None,
    timeout_seconds: Optional[float] = None,
) -> str:
    """
    List virtual-address ranges physically present in the currently opened VS dump.

    This reads only the active dump's minidump directory, never an arbitrary
    caller path or the dump payload. Use a returned startAddress with
    search_memory/read_memory for bounded raw-dump evidence.
    """
    try:
        params = add_bounded_collection_params({}, start_index, max_nodes, budget_ms)
        result = await ide_bridge.call(
            "get_dump_memory_ranges",
            params,
            preferred_ide="vs",
            timeout=tool_timeout_for_budget(timeout_seconds, MEMORY_TIMEOUT_SECONDS, budget_ms),
        )
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "get_dump_memory_ranges")


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
    lines_before = validate_int_range("lines_before", lines_before, 0, 256)
    lines_after = validate_int_range("lines_after", lines_after, 0, 256)
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
    """Disassemble instructions at a non-zero numeric address literal.

    Symbol or expression resolution is deliberately separate and requires
    resolve_address, which is classified as unsafe debugger evaluation.
    """
    count = validate_int_range("count", count, 1, 256)
    try:
        result = await ide_bridge.call("get_disassembly_at", {
            "address": validate_address_literal("address", address),
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
    start_address: Optional[str] = None,
    pattern: str = "",
    max_results: int = 100,
    search_size: int = 0x10000,
    pattern_format: str = "auto",
    start_offset: int = 0,
    continuation: Optional[str] = None,
    timeout_seconds: Optional[float] = None
) -> str:
    """Search active memory without choosing a dump-specific tool.

    With a numeric start_address, search that bounded address range in either a
    live target or the active dump. Without start_address, Visual Studio detects
    its active dump and scans only mapped dump ranges. Pass the opaque
    continuation returned by that page to continue; no dump parser or separate
    search function is required. A continuation is valid only for the same
    active dump, paused debugger context, and parsed pattern; restart the
    search if any of those changes. Live searches require start_address so they
    remain bounded and do not sweep an arbitrary process address space.
    """
    # The total requested range is not reduced to the VSIX per-call limit;
    # larger searches are chunked below. Invalid input is rejected, never changed.
    search_size = validate_int_range("search_size", search_size, 1, MAX_MEMORY_SEARCH_SIZE)
    max_results = validate_int_range("max_results", max_results, 1, 10000)
    start_offset = validate_int_range("start_offset", start_offset, 0, 0x7FFFFFFFFFFFFFFF)
    if start_address is not None:
        start_address = validate_address_literal("start_address", start_address)
        # The VS protocol represents startOffset as an Int64.  Validate the
        # whole requested range before chunking so a later page cannot overflow
        # while being serialized back to the debugger.
        if start_offset > 0x7FFFFFFFFFFFFFFF - (search_size - 1):
            raise invalid_argument(
                "start_offset + search_size exceeds the backend Int64 offset range"
            )
    elif start_offset != 0:
        raise invalid_argument("start_offset requires start_address")
    if continuation is not None:
        continuation = validate_nonempty_string("continuation", continuation, 512)
    if start_address is not None and continuation is not None:
        raise invalid_argument("continuation cannot be combined with start_address")
    pattern_format = validate_enum(
        "pattern_format", pattern_format, ("auto", "hex", "bytes", "text", "ascii", "string")
    )
    tool_timeout = clamp_tool_timeout(
        timeout_seconds,
        min(MAX_TOOL_TIMEOUT_SECONDS, max(MEMORY_TIMEOUT_SECONDS, ((search_size + MAX_MEMORY_SEARCH_CHUNK_SIZE - 1) // MAX_MEMORY_SEARCH_CHUNK_SIZE) * MEMORY_TIMEOUT_SECONDS))
    )
    deadline = asyncio.get_running_loop().time() + tool_timeout

    try:
        if start_address is None:
            # A mapped dump page is intentionally capped to one VSIX request.
            # The backend supplies an opaque continuation when more mapped bytes
            # remain, so callers never need to choose a dump-only method.
            search_size = validate_int_range(
                "search_size", search_size, 1, MAX_MEMORY_SEARCH_CHUNK_SIZE
            )
            call_timeout = remaining_tool_timeout(deadline, MEMORY_TIMEOUT_SECONDS)
            if call_timeout is None:
                return handle_ide_error(
                    ide_bridge.IDEBridgeError(
                        "Search was not started because the search_memory timeout budget was exhausted",
                        code="DEADLINE_EXCEEDED_NOT_STARTED",
                    ),
                    "search_memory",
                )
            params = {
                "pattern": pattern,
                "maxResults": max_results,
                "searchSize": search_size,
                "patternFormat": pattern_format,
                "startOffset": start_offset,
            }
            if continuation is not None:
                params["continuation"] = continuation
            result = await ide_bridge.call(
                "search_memory",
                params,
                preferred_ide="vs",
                timeout=call_timeout,
            )
            return format_result(result)

        if search_size <= MAX_MEMORY_SEARCH_CHUNK_SIZE:
            call_timeout = remaining_tool_timeout(deadline, MEMORY_TIMEOUT_SECONDS)
            if call_timeout is None:
                return handle_ide_error(
                    ide_bridge.IDEBridgeError(
                        "Search was not started because the search_memory timeout budget was exhausted",
                        code="DEADLINE_EXCEEDED_NOT_STARTED",
                    ),
                    "search_memory",
                )
            result = await ide_bridge.call("search_memory", {
                "startAddress": start_address,
                "pattern": pattern,
                "maxResults": max_results,
                "searchSize": search_size,
                "patternFormat": pattern_format,
                "startOffset": start_offset,
            }, timeout=call_timeout)
            return format_result(result)

        matches: List[str] = []
        seen_matches = set()
        chunks: List[Dict[str, Any]] = []
        bytes_searched = 0
        bytes_attempted = 0
        unreadable_bytes = 0
        covered_until = 0
        pattern_bytes = None
        status = "ok"
        partial = False
        error = None
        error_code = None
        continuation_requires_refresh = False
        offset = 0
        overlap = 0
        continuation_expression = None
        continuation_offset = None
        continuation_remaining = None
        terminal_backend_no_continuation = False
        pinned_target = None
        capability = None

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
            chunk_params = {
                "startAddress": start_address,
                "pattern": pattern,
                "maxResults": max_results - len(matches),
                "searchSize": chunk_size,
                "patternFormat": pattern_format,
                "startOffset": absolute_offset,
            }
            try:
                if pinned_target is None:
                    chunk_result, pinned_target = await ide_bridge.call_with_target(
                        "search_memory",
                        chunk_params,
                        timeout=remaining_timeout,
                    )
                else:
                    chunk_result = await vs_bridge.call_pipe_exact(
                        pinned_target,
                        "search_memory",
                        chunk_params,
                        timeout=remaining_timeout,
                    )
            except (ide_bridge.IDEBridgeError, vs_bridge.VSBridgeError) as chunk_error:
                # Before any progress, retain the normal typed tool-error contract.
                # After progress, preserve the useful prefix and expose an explicit
                # continuation instead of discarding all previously captured chunks.
                if not chunks and covered_until == 0:
                    raise
                partial = True
                status = "partial"
                error_code = getattr(chunk_error, "code", None) or "QUERY_FAILED"
                error = f"Search stopped after {bytes_searched} bytes: {chunk_error}"
                continuation_requires_refresh = error_code in {
                    "STALE_TARGET",
                    "STALE_DEBUG_CONTEXT",
                    "STALE_DEBUG_SESSION",
                    "TARGET_NOT_FOUND",
                    "UNAUTHENTICATED",
                }
                break

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

            physical_bytes_searched = max(0, min(
                chunk_size,
                int(chunk_result.get("bytesSearched") or 0),
            ))
            attempted_value = chunk_result.get("bytesAttempted")
            physical_bytes_attempted = max(
                physical_bytes_searched,
                min(
                    chunk_size,
                    int(chunk_size if attempted_value is None else attempted_value),
                ),
            )
            unreadable_value = chunk_result.get("unreadableBytes")
            if unreadable_value is None:
                unreadable_value = chunk_result.get("failedReads")
            if unreadable_value is None:
                unreadable_value = physical_bytes_attempted - physical_bytes_searched
            physical_unreadable = max(0, min(physical_bytes_attempted, int(unreadable_value)))
            physical_end = min(search_size, offset + physical_bytes_searched)
            newly_covered = max(0, physical_end - max(covered_until, offset))
            physical_attempt_end = min(search_size, offset + physical_bytes_attempted)
            newly_attempted = max(
                newly_covered,
                physical_attempt_end - max(covered_until, offset),
            )
            bytes_searched += newly_covered
            bytes_attempted += newly_attempted
            unreadable_bytes += max(0, newly_attempted - newly_covered)
            covered_until = max(covered_until, physical_end)
            chunk_status = str(chunk_result.get("status") or "unavailable").lower()
            chunk_truncated = bool(chunk_result.get("truncated"))
            chunk_partial = bool(chunk_result.get("partial")) or chunk_status not in ("ok",)
            chunk_error = chunk_result.get("error")
            chunk_capability = chunk_result.get("capability")
            if capability is None:
                capability = chunk_capability
            elif chunk_capability != capability:
                capability = "mixed_memory_read_strategies"
            chunks.append({
                "startAddress": chunk_start,
                "requestedBytes": chunk_size,
                "bytesSearched": chunk_result.get("bytesSearched", 0),
                "bytesAttempted": physical_bytes_attempted,
                "unreadableBytes": physical_unreadable,
                "unattemptedBytes": max(0, int(
                    chunk_result.get("unattemptedBytes")
                    if chunk_result.get("unattemptedBytes") is not None
                    else chunk_size - physical_bytes_attempted
                )),
                "status": chunk_result.get("status", "unavailable"),
                "partial": bool(chunk_result.get("partial")),
                "truncated": chunk_truncated,
                "nextStartAddress": chunk_result.get("nextStartAddress"),
                "matchCount": len(chunk_matches),
                "capability": chunk_capability,
                "error": chunk_error,
            })

            if chunk_truncated:
                partial = True
                status = "partial"
                backend_next_offset = chunk_result.get("nextStartOffset")
                try:
                    backend_next_offset = int(backend_next_offset)
                except (TypeError, ValueError, OverflowError):
                    backend_next_offset = None
                range_end_offset = start_offset + search_size
                if backend_next_offset is not None and start_offset <= backend_next_offset < range_end_offset:
                    continuation_expression = start_address
                    continuation_offset = backend_next_offset
                    consumed = backend_next_offset - start_offset
                    continuation_remaining = search_size - consumed
                    error = (
                        "Search stopped after reaching max_results; continue with the same "
                        "start address, nextStartOffset and remainingBytes"
                    )
                else:
                    error_code = "INVALID_BACKEND_CONTINUATION"
                    error = "Search reached max_results but the debugger returned no valid continuation offset"
                break

            next_offset = max(offset + 1, covered_until - overlap)

            if chunk_partial:
                partial = True
                status = "partial"
                error = chunk_error or f"Chunk at {chunk_start} was partial"
                backend_next_offset = chunk_result.get("nextStartOffset")
                try:
                    backend_next_offset = (
                        int(backend_next_offset)
                        if backend_next_offset is not None
                        else None
                    )
                except (TypeError, ValueError, OverflowError):
                    backend_next_offset = None
                range_end_offset = start_offset + search_size
                if (backend_next_offset is not None and
                        start_offset <= backend_next_offset < range_end_offset):
                    continuation_expression = start_address
                    continuation_offset = backend_next_offset
                    consumed = max(0, continuation_offset - start_offset)
                    continuation_remaining = max(0, search_size - consumed)
                elif not bool(chunk_result.get("hasMore")):
                    # The backend explicitly refused to advance across an
                    # inconclusive memory interval.  Do not turn that into a
                    # synthetic startAddress cursor that retries the same page.
                    terminal_backend_no_continuation = True
                break

            if covered_until >= search_size:
                # Overlap is needed only while there are unsearched bytes.
                # Re-reading the tail can incorrectly turn a complete search
                # into a partial result if the target changes afterward.
                offset = search_size
                break

            if len(matches) >= max_results:
                # A backend only emits nextStartOffset when its own result limit
                # truncated an otherwise still-searchable chunk. If the final
                # match merely happens to fill the caller's max_results budget,
                # there may be no backend cursor at all. Use the proven chunk
                # boundary (with overlap) as the continuation for the remaining
                # range; complete coverage has already been handled above.
                next_offset = max(offset + 1, covered_until - overlap)
                partial = True
                status = "partial"
                continuation_expression = start_address
                continuation_offset = start_offset + next_offset
                continuation_remaining = search_size - next_offset
                error = (
                    "Search stopped after reaching max_results; continue with the same "
                    "start address, nextStartOffset and remainingBytes"
                )
                break

            offset = next_offset

        next_start_expression = None
        next_start_offset = None
        remaining_bytes = 0
        if continuation_expression:
            next_start_expression = continuation_expression
            next_start_offset = continuation_offset
            remaining_bytes = continuation_remaining
        elif covered_until < search_size and not terminal_backend_no_continuation:
            # The bytes at the end of the completed chunk have been read, but
            # candidates starting there still need the next chunk's bytes.
            # Preserve that overlap when an interruption prevents the next
            # request from returning a backend continuation.
            resume_offset = max(0, covered_until - overlap)
            next_start_expression = start_address
            next_start_offset = start_offset + resume_offset
            remaining_bytes = search_size - resume_offset

        unattempted_bytes = max(0, search_size - bytes_attempted)
        failed_reads = unreadable_bytes

        has_more = next_start_expression is not None and (
            remaining_bytes is None or remaining_bytes > 0
        )
        if not has_more:
            next_start_expression = None
            next_start_offset = None
        result = {
            "pattern": pattern,
            "patternFormat": pattern_format,
            "status": status,
            "patternBytes": pattern_bytes,
            "matches": matches,
            "totalFound": len(matches),
            "bytesSearched": bytes_searched,
            "bytesAttempted": bytes_attempted,
            "requestedBytes": search_size,
            "failedReads": failed_reads,
            "unreadableBytes": unreadable_bytes,
            "unattemptedBytes": unattempted_bytes,
            "partial": partial,
            "capability": capability,
            "chunked": True,
            "chunkSize": MAX_MEMORY_SEARCH_CHUNK_SIZE,
            "chunkCount": len(chunks),
            "chunks": chunks,
            "nextStartExpression": next_start_expression,
            "nextStartOffset": next_start_offset,
            "remainingBytes": remaining_bytes,
            "hasMore": has_more,
        }
        if error:
            result["error"] = error
        if error_code:
            result["errorCode"] = error_code
        if continuation_requires_refresh:
            result["continuationRequiresRefresh"] = True
        return format_result(result)
    except (ide_bridge.IDEBridgeError, vs_bridge.VSBridgeError) as e:
        return handle_ide_error(e, "search_memory")
