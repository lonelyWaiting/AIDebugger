from typing import Any, Dict, List, Optional, Tuple
import asyncio
import re

try:
    from .. import vs_bridge
    from ..tool_runtime import backend as ide_bridge, mcp
    from ..tool_context import *
except ImportError:
    import vs_bridge
    from tool_runtime import backend as ide_bridge, mcp
    from tool_context import *


def _validate_step_capture_expressions(expressions: Optional[List[str]]) -> List[str]:
    if expressions is None:
        return []
    if not isinstance(expressions, list) or len(expressions) > 100:
        raise invalid_argument("expressions must be a list of at most 100 strings")
    result = list(expressions)
    for expression in result:
        if not isinstance(expression, str) or not expression.strip():
            raise invalid_argument("expressions must contain nonempty strings")
        try:
            # VS limits System.String.Length, which counts UTF-16 code units.
            units = len(expression.encode("utf-16-le")) // 2
        except UnicodeEncodeError as error:
            raise invalid_argument("expressions must contain valid Unicode") from error
        if units > 16 * 1024:
            raise invalid_argument("each expression must contain at most 16384 UTF-16 code units")
    return result


def _normalize_nonzero_address_literal(value: Any) -> Optional[str]:
    """Return a protocol-safe address literal without evaluating an expression."""
    if value is None:
        return None
    text = str(value).strip().replace("`", "").replace("_", "")
    match = re.fullmatch(r"(?:0[xX])?([0-9A-Fa-f]+)(?:[hH])?", text)
    if not match:
        return None
    try:
        parsed = int(match.group(1), 16)
    except ValueError:
        return None
    return f"0x{parsed:X}" if parsed > 0 else None


def _stack_pointer_from_bundle(bundle: Any) -> Optional[Tuple[str, str]]:
    """Extract a stack pointer only from a stable, explicitly available snapshot."""
    if not isinstance(bundle, dict):
        return None
    register_result = bundle.get("registers")
    if not isinstance(register_result, dict):
        return None
    if str(register_result.get("snapshotStatus") or "").strip().lower() != "stable":
        return None
    status = str(register_result.get("status") or "").strip().lower()
    if status not in ("complete", "partial"):
        return None
    registers = register_result.get("registers")
    if not isinstance(registers, list):
        return None
    unavailable_values = register_result.get("unavailableRegisters")
    if not isinstance(unavailable_values, list):
        return None
    unavailable = {
        str(name or "").strip().lower().lstrip("@")
        for name in unavailable_values
    }
    by_name = {
        str(item.get("name") or "").strip().lower().lstrip("@"): item
        for item in registers
        if isinstance(item, dict)
    }
    is_64_bit = register_result.get("is64Bit")
    if is_64_bit is not None and not isinstance(is_64_bit, bool):
        return None
    candidates = ("rsp",) if is_64_bit is True else (("esp",) if is_64_bit is False else ("rsp", "esp"))
    for name in candidates:
        if name in unavailable:
            continue
        item = by_name.get(name)
        if not item:
            continue
        literal = _normalize_nonzero_address_literal(item.get("hexValue"))
        if literal is None:
            literal = _normalize_nonzero_address_literal(item.get("value"))
        if literal is not None:
            return literal, f"@{name}"
    return None
@mcp.tool()
async def get_debug_status() -> str:
    """
    获取当前调试状态，包括是否暂停、当前位置，以及标准化的 stopReason（breakpoint/exception/step/user_break/...）。

    无需任何参数。

    返回示例:
    ```json
    {
      "isDebugging": true,
      "isPaused": true,
      "stopReason": "breakpoint",
      "currentLocation": {"filePath": "Program.cs", "lineNumber": 42, "functionName": "Main"}
    }
    ```

    **触发场景**: 当用户提到"调试"、"debug"、"断点"、"breakpoint"、"VS"、"Visual Studio"时，优先调用此工具了解当前状态。
    """
    try:
        result = await ide_bridge.call("get_debug_status")
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "get_debug_status")

@mcp.tool()
async def get_callstack(
    start_index: int = 0,
    max_frames: int = 256,
    budget_ms: int = 5000,
) -> str:
    """
    获取当前线程的完整调用堆栈，包含每个栈帧的函数名、文件路径、行号等信息。
    Cursor可以根据返回的文件路径自动读取相关代码。

    无需任何参数。

    返回示例:
    ```json
    {
      "threadId": 1,
      "snapshotStatus": "ok",
      "frames": [
        {"index": 0, "functionName": "DoSomething", "filePath": "Helper.cs", "lineNumber": 25},
        {"index": 1, "functionName": "Main", "filePath": "Program.cs", "lineNumber": 42}
      ]
    }
    ```

    **触发场景**: 当用户询问"怎么执行到这里的"、"调用链"、"堆栈"、"callstack"、"函数调用顺序"时调用此工具。
    """
    try:
        result = await ide_bridge.call("get_callstack", {
            "startIndex": validate_int_range("start_index", start_index, 0, 1_000_000),
            "maxFrames": validate_int_range("max_frames", max_frames, 0, 4096),
            "budgetMs": validate_int_range("budget_ms", budget_ms, 0, 120000),
        })
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "get_callstack")

@mcp.tool()
async def get_call_stack(
    start_index: int = 0,
    max_frames: int = 256,
    budget_ms: int = 5000,
) -> str:
    """
    Alias for get_callstack.
    """
    return await get_callstack(start_index, max_frames, budget_ms)

@mcp.tool()
async def get_locals(
    debug_session_generation: int,
    break_sequence: int,
    context_epoch: int,
    process_id: int,
    thread_id: int,
    frame_index: int,
    frame_id: str,
    depth: int = 2,
    thread_lookup_budget_ms: Optional[int] = None,
    timeout_seconds: Optional[float] = None,
) -> str:
    """
    获取当前栈帧的所有局部变量和参数，包含变量名、类型和当前值。

    Args:
        depth: 变量展开深度（0-4），默认2。值越大展开的子成员层级越多，超过预算会截断。

    返回示例:
    ```json
    {
      "variables": [
        {"name": "count", "type": "int", "value": "42"},
        {"name": "items", "type": "List<string>", "value": "{Count=3}"}
      ]
    }
    ```

    **触发场景**: 当用户询问"变量值是什么"、"参数是多少"、"局部变量"、"当前状态"时调用此工具。
    """
    try:
        params = {
            "depth": validate_int_range("depth", depth, 0, 4),
            "frameIndex": validate_int_range("frame_index", frame_index, 0, 4096),
            "debugSessionGeneration": validate_int_range(
                "debug_session_generation", debug_session_generation, 0, 0x7FFFFFFFFFFFFFFF
            ),
            "breakSequence": validate_int_range(
                "break_sequence", break_sequence, 0, 0x7FFFFFFFFFFFFFFF
            ),
            "contextEpoch": validate_int_range(
                "context_epoch", context_epoch, 0, 0x7FFFFFFFFFFFFFFF
            ),
            "processId": validate_int_range("process_id", process_id, 1, 0x7FFFFFFF),
            "threadId": validate_int_range("thread_id", thread_id, 1, 0x7FFFFFFF),
            "frameId": validate_nonempty_string("frame_id", frame_id),
        }
        if thread_lookup_budget_ms is not None:
            params["threadLookupBudgetMs"] = validate_int_range(
                "thread_lookup_budget_ms", thread_lookup_budget_ms, 0, 120000
            )
        result = await ide_bridge.call(
            "get_locals",
            params,
            timeout=tool_timeout_for_budget(
                timeout_seconds, THREADS_TIMEOUT_SECONDS, thread_lookup_budget_ms
            ),
        )
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "get_locals")

@mcp.tool()
async def evaluate_expression(
    expression: str,
    expand_children: bool = False,
    max_depth: int = 2,
    timeout_seconds: Optional[float] = None,
    evaluation_policy: str = "engine_default",
) -> str:
    """
    在当前调试上下文中求值任意表达式。可以获取变量的地址、计算偏移量、访问结构成员等。
    对于指针和数值类型会同时返回十进制和十六进制值。

    Args:
        expression: 要求值的表达式，如：&myVariable、myPointer->field、sizeof(MyStruct)、(int*)0x1234
        expand_children: 是否展开对象的子成员，默认false
        max_depth: 展开子成员的最大深度，默认2
        timeout_seconds: 等待 IDE 响应的最长秒数，默认30，最大300
        evaluation_policy: VS 当前仅支持 engine_default；不承诺无副作用

    调用示例:
    ```
    evaluate_expression(expression="myObject.Property")
    evaluate_expression(expression="&myVariable", expand_children=True)
    evaluate_expression(expression="arr[0]", expand_children=True, max_depth=3)
    ```

    **触发场景**: 当用户想查看特定表达式的值、询问"xxx的值是多少"、"计算xxx"、"查看指针"、"展开对象"时调用此工具。
    """
    max_depth = validate_int_range("max_depth", max_depth, 0, 4)
    try:
        result = await ide_bridge.call("evaluate_expression", {
            "expression": expression,
            "expandChildren": expand_children,
            "maxDepth": max_depth,
            "evaluationPolicy": evaluation_policy,
        }, timeout=clamp_tool_timeout(timeout_seconds, EVALUATION_TIMEOUT_SECONDS))
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "evaluate_expression")

@mcp.tool()
async def evaluate_expressions_batch(
    expressions: List[str],
    expand_children: bool = False,
    max_depth: int = 2,
    timeout_seconds: Optional[float] = None,
    evaluation_policy: str = "engine_default",
) -> str:
    """
    在同一调试快照中批量求值多个表达式，减少逐条请求导致的上下文漂移。

    Args:
        expressions: 表达式列表（最多100条）
        expand_children: 是否展开对象子成员，默认False
        max_depth: 子成员展开深度，默认2
        timeout_seconds: 等待 IDE 响应的最长秒数，默认45，最大300
        evaluation_policy: VS 当前仅支持 engine_default；不承诺无副作用
    """
    normalized_expressions = expressions or []
    max_depth = validate_int_range("max_depth", max_depth, 0, 4)
    try:
        result = await ide_bridge.call("evaluate_expressions_batch", {
            "expressions": normalized_expressions,
            "expandChildren": expand_children,
            "maxDepth": max_depth,
            "evaluationPolicy": evaluation_policy,
        }, timeout=clamp_tool_timeout(timeout_seconds, BATCH_EVALUATION_TIMEOUT_SECONDS))
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "evaluate_expressions_batch")

@mcp.tool()
async def get_threads(
    timeout_seconds: Optional[float] = None,
    include_frames: bool = True,
    max_frames_per_thread: Optional[int] = None,
    max_nodes: Optional[int] = None,
    budget_ms: Optional[int] = None,
    start_thread_index: int = 1,
    start_frame_index: int = 0,
) -> str:
    """
    获取调试进程中所有线程的信息，包括线程ID、名称、状态和当前位置。

    默认低阻塞，可能返回 partial=true。需要完整线程帧时传
    max_frames_per_thread=0、max_nodes=0、budget_ms=0 或较大的 budget_ms；timeout_seconds 会随
    budget_ms 自动上调。partial=false 表示请求预算内线程/帧集合完整返回。

    **触发场景**: 当用户提到"线程"、"thread"、"多线程"、"并发"、"死锁"时调用此工具。
    """
    try:
        params: Dict[str, Any] = {
            "includeFrames": include_frames,
            "startThreadIndex": validate_int_range("start_thread_index", start_thread_index, 1, 4096),
            "startFrameIndex": validate_int_range("start_frame_index", start_frame_index, 0, 1000000),
        }
        if max_frames_per_thread is not None:
            params["maxFramesPerThread"] = validate_int_range(
                "max_frames_per_thread", max_frames_per_thread, 0, 256
            )
        if max_nodes is not None:
            params["maxNodes"] = validate_int_range("max_nodes", max_nodes, 0, 4096)
        if budget_ms is not None:
            params["budgetMs"] = validate_int_range("budget_ms", budget_ms, 0, 120000)
        result = await ide_bridge.call(
            "get_threads",
            params,
            timeout=tool_timeout_for_budget(timeout_seconds, THREADS_TIMEOUT_SECONDS, budget_ms),
        )
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "get_threads")

@mcp.tool()
async def list_threads(
    timeout_seconds: Optional[float] = None,
    include_frames: bool = True,
    max_frames_per_thread: Optional[int] = None,
    max_nodes: Optional[int] = None,
    budget_ms: Optional[int] = None,
    start_thread_index: int = 1,
    start_frame_index: int = 0,
) -> str:
    """
    Get debugger thread metadata. Defaults are low-blocking and may return partial=true.

    For the broadest bounded thread snapshot, raise max_frames_per_thread,
    max_nodes, budget_ms, and timeout_seconds. Use max_frames_per_thread=0,
    max_nodes=0, or budget_ms=0 to select the server hard caps. partial=false
    means the returned thread/frame set was complete within those bounds.

    Alias for get_threads.
    """
    return await get_threads(
        timeout_seconds=timeout_seconds,
        include_frames=include_frames,
        max_frames_per_thread=max_frames_per_thread,
        max_nodes=max_nodes,
        budget_ms=budget_ms,
        start_thread_index=start_thread_index,
        start_frame_index=start_frame_index,
    )

@mcp.tool()
async def get_modules(
    timeout_seconds: Optional[float] = None,
    max_nodes: Optional[int] = None,
    budget_ms: Optional[int] = None,
    start_module_index: int = 0,
    max_frames_scanned: Optional[int] = None,
    module_snapshot_id: Optional[str] = None,
    scan_continuation_token: Optional[str] = None,
) -> str:
    """
    获取调试进程中已加载的所有模块/程序集信息。
    """
    try:
        params = add_bounded_collection_params({}, None, max_nodes, budget_ms)
        params["startModuleIndex"] = validate_int_range(
            "start_module_index", start_module_index, 0, 1_000_000
        )
        if max_frames_scanned is not None:
            params["maxFramesScanned"] = validate_int_range(
                "max_frames_scanned", max_frames_scanned, 0, 4096
            )
        if module_snapshot_id is not None:
            if not module_snapshot_id.strip() or len(module_snapshot_id) > 128:
                raise ide_bridge.IDEBridgeError(
                    "module_snapshot_id must be a non-empty snapshot identifier",
                    code="INVALID_ARGUMENT",
                )
            params["moduleSnapshotId"] = module_snapshot_id
        if scan_continuation_token is not None:
            if not scan_continuation_token.strip() or len(scan_continuation_token) > 128:
                raise ide_bridge.IDEBridgeError(
                    "scan_continuation_token must be a non-empty snapshot identifier",
                    code="INVALID_ARGUMENT",
                )
            if module_snapshot_id is not None:
                raise ide_bridge.IDEBridgeError(
                    "module_snapshot_id and scan_continuation_token are mutually exclusive",
                    code="INVALID_ARGUMENT",
                )
            params["scanContinuationToken"] = scan_continuation_token
        result = await ide_bridge.call(
            "get_modules",
            params,
            timeout=tool_timeout_for_budget(timeout_seconds, MODULES_TIMEOUT_SECONDS, budget_ms),
        )
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "get_modules")

@mcp.tool()
async def get_exception(timeout_seconds: Optional[float] = None) -> str:
    """
    获取当前异常的详细信息，包括异常类型、消息、堆栈跟踪和内部异常。

    无需任何参数。仅当程序因异常暂停时有效。

    返回示例:
    ```json
    {
      "hasException": true,
      "exceptionType": "System.NullReferenceException",
      "message": "Object reference not set to an instance of an object",
      "stackTrace": "at Program.Main() in Program.cs:line 42"
    }
    ```

    **触发场景**: 当用户提到"崩溃"、"crash"、"异常"、"exception"、"报错"、"出错了"、"抛异常"时，优先调用此工具。
    """
    try:
        result = await ide_bridge.call("get_exception", timeout=clamp_tool_timeout(timeout_seconds, EXCEPTION_TIMEOUT_SECONDS))
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "get_exception")

@mcp.tool()
async def get_build_errors(
    start_index: int = 0,
    max_nodes: Optional[int] = None,
    budget_ms: Optional[int] = None,
    timeout_seconds: Optional[float] = None,
) -> str:
    """
    获取Visual Studio Error List中的所有编译错误和警告，包含错误代码（如CS0001）、文件路径、行号和详细描述。
    Cursor可以根据返回的文件路径自动读取相关代码并帮助修复。

    **触发场景**: 当用户提到"编译错误"、"build error"、"构建失败"、"无法编译"、"语法错误"、"红色波浪线"时，优先调用此工具。
    """
    try:
        params = add_bounded_collection_params({}, start_index, max_nodes, budget_ms)
        result = await ide_bridge.call(
            "get_build_errors", params,
            timeout=tool_timeout_for_budget(timeout_seconds, THREADS_TIMEOUT_SECONDS, budget_ms),
        )
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "get_build_errors")

@mcp.tool()
async def get_errors_only(
    start_index: int = 0,
    max_nodes: Optional[int] = None,
    budget_ms: Optional[int] = None,
    timeout_seconds: Optional[float] = None,
) -> str:
    """
    仅获取编译错误（不包括警告）。
    """
    try:
        params = add_bounded_collection_params({}, start_index, max_nodes, budget_ms)
        result = await ide_bridge.call(
            "get_errors_only", params,
            timeout=tool_timeout_for_budget(timeout_seconds, THREADS_TIMEOUT_SECONDS, budget_ms),
        )
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "get_errors_only")

@mcp.tool()
async def get_warnings_only(
    start_index: int = 0,
    max_nodes: Optional[int] = None,
    budget_ms: Optional[int] = None,
    timeout_seconds: Optional[float] = None,
) -> str:
    """
    仅获取编译警告（不包括错误）。
    """
    try:
        params = add_bounded_collection_params({}, start_index, max_nodes, budget_ms)
        result = await ide_bridge.call(
            "get_warnings_only", params,
            timeout=tool_timeout_for_budget(timeout_seconds, THREADS_TIMEOUT_SECONDS, budget_ms),
        )
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "get_warnings_only")

@mcp.tool()
async def get_debug_output(
    pane: str = "Debug",
    tail_lines: int = 50,
    end_character: int = -1,
    max_bytes: int = 3 * 1024 * 1024,
    timeout_seconds: Optional[float] = None
) -> str:
    """
    获取Visual Studio Output窗口的内容。用于查看调试输出、构建日志、程序输出等。
    默认返回最后50行以避免内容过多。

    **触发场景**: 当用户提到"输出"、"output"、"日志"、"log"、"打印了什么"、"构建日志"时调用此工具。

    Args:
        pane: 输出面板名称，如 "Debug"（调试输出）, "Build"（构建日志）, "General" 等。默认为 "Debug"
        tail_lines: 返回最后N行，0表示返回全部。默认50行
    """
    try:
        tail_lines = validate_int_range("tail_lines", tail_lines, 0, 10000)
        end_character = validate_int_range("end_character", end_character, -1, 0x7FFFFFFF)
        max_bytes = validate_int_range("max_bytes", max_bytes, 4 * 1024, 3 * 1024 * 1024)
        result = await ide_bridge.call("get_output_window", {
            "pane": pane,
            "tailLines": tail_lines,
            "endCharacter": end_character,
            "maxBytes": max_bytes,
        }, timeout=clamp_tool_timeout(timeout_seconds, OUTPUT_TIMEOUT_SECONDS))
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "get_debug_output")

@mcp.tool()
async def search_output(
    pattern: str,
    pane: str = "Debug",
    is_regex: bool = False,
    context_lines: int = 2,
    max_matches: int = 20,
    start_line: int = 0,
    end_character: int = -1,
    expected_capture_start_character: int = -1,
    timeout_seconds: Optional[float] = None
) -> str:
    """
    在Visual Studio Output窗口中搜索指定模式。可用于查找特定错误、检查执行状态、定位关键输出等。

    Args:
        pattern: 搜索模式（字符串或正则表达式）
        pane: 输出面板名称，默认 "Debug"
        is_regex: 是否使用正则表达式匹配，默认False（普通字符串匹配，不区分大小写）
        context_lines: 每个匹配结果显示的上下文行数，默认2
        max_matches: 最大返回匹配数，默认20
    """
    try:
        context_lines = validate_int_range("context_lines", context_lines, 0, 100)
        max_matches = validate_int_range("max_matches", max_matches, 1, 10000)
        start_line = validate_int_range("start_line", start_line, 0, 1_000_000)
        end_character = validate_int_range("end_character", end_character, -1, 2_147_483_647)
        expected_capture_start_character = validate_int_range(
            "expected_capture_start_character",
            expected_capture_start_character,
            -1,
            2_147_483_647,
        )
        result = await ide_bridge.call("search_output", {
            "pattern": pattern,
            "pane": pane,
            "isRegex": is_regex,
            "contextLines": context_lines,
            "maxMatches": max_matches,
            "startLine": start_line,
            "endCharacter": end_character,
            "expectedCaptureStartCharacter": expected_capture_start_character,
        }, timeout=clamp_tool_timeout(timeout_seconds, OUTPUT_TIMEOUT_SECONDS))
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "search_output")

@mcp.tool()
async def get_full_context(
    timeout_seconds: Optional[float] = None,
    thread_include_frames: bool = True,
    thread_max_frames_per_thread: Optional[int] = None,
    thread_max_nodes: Optional[int] = None,
    thread_budget_ms: Optional[int] = None,
) -> str:
    """
    一次性获取所有调试上下文信息，包括状态、堆栈、变量、异常、线程和模块。
    当你需要完整分析调试现场时使用此工具。
    如需更强一致性（带 snapshotId 与批量表达式求值），优先使用 capture_debug_bundle。

    **触发场景**: 当用户说"帮我分析一下"、"看看发生了什么"、"调试现场"、"为什么会这样"等需要全面了解情况时调用此工具。
    """
    try:
        params = add_thread_capture_params(
            {},
            thread_include_frames,
            thread_max_frames_per_thread,
            thread_max_nodes,
            thread_budget_ms,
        )
        result = await ide_bridge.call(
            "get_full_context",
            params,
            timeout=tool_timeout_for_budget(timeout_seconds, DEBUG_BUNDLE_TIMEOUT_SECONDS, thread_budget_ms),
        )
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "get_full_context")

@mcp.tool()
async def capture_debug_bundle(
    expressions: Optional[List[str]] = None,
    expand_children: bool = False,
    max_depth: int = 2,
    output_tail_lines: int = 80,
    timeout_seconds: Optional[float] = None,
    include_locals: bool = False,
    include_modules: bool = False,
    include_registers: bool = False,
    include_output: bool = False,
    include_threads: bool = True,
    include_exception: bool = False,
    evaluation_policy: str = "engine_default",
    thread_include_frames: bool = True,
    thread_max_frames_per_thread: Optional[int] = None,
    thread_max_nodes: Optional[int] = None,
    thread_budget_ms: Optional[int] = None,
) -> str:
    """
    抓取可分析调试快照包。
    返回 snapshotId/capturedAt/snapshotStatus/partial，可直接用于AI诊断。

    默认低阻塞：只抓 status/callstack/threads，避免 exception/locals/modules/registers/output 在 VS UI 线程上拖住整次请求。
    需要完整包时显式打开 include_locals/include_modules/include_registers/include_output。

    Args:
        expressions: 可选表达式列表，会在同一快照中批量求值
        expand_children: 表达式是否展开子成员，默认False
        max_depth: 表达式子成员展开深度，默认2
        output_tail_lines: Debug Output 末尾行数，默认80
        timeout_seconds: 等待 IDE 响应的最长秒数，默认低阻塞20/完整60，最大300；传 thread_budget_ms 时自动上调
        include_locals: 是否抓取当前帧 locals，默认False
        include_modules: 是否抓取模块/程序集，默认False
        include_registers: 是否抓取寄存器，默认False
        include_output: 是否读取 Debug Output，默认False
        include_threads: 是否抓取线程列表，默认True
        include_exception: 是否通过调试表达式抓取异常信息，默认False；启用时需要 unsafe evaluation capability
        evaluation_policy: VS 当前仅支持 engine_default；不承诺无副作用
        thread_include_frames: 是否在 threads 中包含 stack frames，默认True
        thread_max_frames_per_thread/thread_max_nodes/thread_budget_ms: 线程帧抓取预算；0 表示使用最大受控抓取预算

    调用示例:
    ```
    # 基础用法 - 无需参数
    capture_debug_bundle()

    # 带表达式求值
    capture_debug_bundle(expressions=["myVar", "obj.Property", "arr.Length"])

    # 显式完整包
    capture_debug_bundle(include_locals=True, include_modules=True, include_registers=True, include_output=True)
    ```
    """
    normalized_expressions = expressions or []
    max_depth = validate_int_range("max_depth", max_depth, 0, 4)
    output_tail_lines = validate_int_range("output_tail_lines", output_tail_lines, 0, 500)
    heavy_components_requested = (
        include_locals or include_modules or include_registers or include_output or bool(normalized_expressions)
    )
    default_timeout = DEBUG_BUNDLE_TIMEOUT_SECONDS if heavy_components_requested else SAFE_DEBUG_BUNDLE_TIMEOUT_SECONDS

    try:
        params = add_thread_capture_params({
            "expressions": normalized_expressions,
            "expandChildren": expand_children,
            "maxDepth": max_depth,
            "outputTailLines": output_tail_lines,
            "includeLocals": include_locals,
            "includeModules": include_modules,
            "includeRegisters": include_registers,
            "includeOutput": include_output,
            "includeThreads": include_threads,
            "includeException": include_exception,
            "evaluationPolicy": evaluation_policy,
        }, thread_include_frames, thread_max_frames_per_thread, thread_max_nodes, thread_budget_ms)
        backend_method = "capture_debug_bundle_safe" if use_vs_safe_backend(
            normalized_expressions,
            include_locals=include_locals,
            include_registers=include_registers,
            include_exception=include_exception,
        ) else "capture_debug_bundle"
        if backend_method == "capture_debug_bundle_safe":
            for unsafe_parameter in (
                "expressions", "expandChildren", "maxDepth", "evaluationPolicy",
                "includeLocals", "includeRegisters", "includeException",
            ):
                params.pop(unsafe_parameter, None)
        result = await ide_bridge.call(
            backend_method,
            params,
            timeout=tool_timeout_for_budget(timeout_seconds, default_timeout, thread_budget_ms),
        )
        return format_result(result)
    except ide_bridge.IDEBridgeError as e:
        return handle_ide_error(e, "capture_debug_bundle")

@mcp.tool()
async def crash_triage(
    expressions: Optional[List[str]] = None,
    output_tail_lines: int = 120,
    timeout_seconds: Optional[float] = None,
    thread_max_frames_per_thread: Optional[int] = 32,
    thread_max_nodes: Optional[int] = 512,
    thread_budget_ms: Optional[int] = 10000,
) -> str:
    """
    Capture a focused Visual Studio crash/dump triage package.

    Includes status, callstack, exception, modules, registers, Debug output tail,
    thread frames, nearby disassembly, and best-effort stack memory.
    """
    normalized_expressions = expressions or []
    default_timeout = DEBUG_BUNDLE_TIMEOUT_SECONDS
    tool_timeout = tool_timeout_for_budget(timeout_seconds, default_timeout, thread_budget_ms)
    deadline = asyncio.get_running_loop().time() + tool_timeout
    try:
        bundle_params = add_thread_capture_params({
            "expressions": normalized_expressions,
            "expandChildren": False,
            "maxDepth": 1,
            "outputTailLines": validate_int_range("output_tail_lines", output_tail_lines, 0, 500),
            "includeLocals": False,
            "includeModules": True,
            "includeRegisters": True,
            "includeOutput": True,
            "includeThreads": True,
            "includeException": True,
            "evaluationPolicy": "engine_default",
        }, True, thread_max_frames_per_thread, thread_max_nodes, thread_budget_ms)

        bundle_method = "capture_debug_bundle_safe" if use_vs_safe_backend(
            normalized_expressions,
            include_registers=True,
            include_exception=True,
        ) else "capture_debug_bundle"
        if bundle_method == "capture_debug_bundle_safe":
            for unsafe_parameter in ("expressions", "expandChildren", "maxDepth", "evaluationPolicy"):
                bundle_params.pop(unsafe_parameter, None)

        bundle_timeout = remaining_tool_timeout(deadline, tool_timeout)
        if bundle_timeout is None:
            return handle_ide_error(
                ide_bridge.IDEBridgeError(
                    "Crash triage was not started because its timeout budget was exhausted",
                    code="DEADLINE_EXCEEDED_NOT_STARTED",
                ),
                "crash_triage",
            )
        bundle, target = await ide_bridge.call_with_target(
            bundle_method,
            bundle_params,
            timeout=bundle_timeout,
        )
        triage: Dict[str, Any] = {
            "kind": "crash_triage",
            "bundle": bundle,
        }

        disassembly_timeout = remaining_tool_timeout(deadline, DISASSEMBLY_TIMEOUT_SECONDS)
        if disassembly_timeout is None:
            triage["disassemblyError"] = "Skipped because crash_triage timeout budget was exhausted"
        else:
            try:
                triage["disassembly"] = await vs_bridge.call_pipe_exact(
                    target,
                    "get_disassembly",
                    {"linesBefore": 8, "linesAfter": 24},
                    timeout=disassembly_timeout,
                )
            except (ide_bridge.IDEBridgeError, vs_bridge.VSBridgeError) as e:
                triage["disassemblyError"] = str(e)

        last_memory_error = None
        stack_pointer = _stack_pointer_from_bundle(bundle)
        if stack_pointer is None:
            # Register capture can be unavailable for some debug engines. Resolve
            # the expression once, then pin the subsequent literal read to the
            # response context because resolve_address is an unsafe command.
            for stack_register in ("@rsp", "@esp"):
                resolve_timeout = remaining_tool_timeout(deadline, NATIVE_CONTEXT_TIMEOUT_SECONDS)
                if resolve_timeout is None:
                    last_memory_error = "Skipped because crash_triage timeout budget was exhausted"
                    break
                try:
                    resolved, target = await vs_bridge.call_pipe_exact(
                        target,
                        "resolve_address",
                        {"address": stack_register},
                        timeout=resolve_timeout,
                        include_target=True,
                    )
                    literal = _normalize_nonzero_address_literal(
                        resolved.get("virtualAddress") if isinstance(resolved, dict) else None
                    )
                    if literal is not None:
                        stack_pointer = (literal, stack_register)
                        break
                    last_memory_error = (
                        resolved.get("error") if isinstance(resolved, dict) else None
                    ) or f"{stack_register} unavailable"
                except (ide_bridge.IDEBridgeError, vs_bridge.VSBridgeError) as e:
                    last_memory_error = str(e)

        if stack_pointer is not None:
            stack_address, stack_register = stack_pointer
            memory_timeout = remaining_tool_timeout(deadline, MEMORY_TIMEOUT_SECONDS)
            if memory_timeout is None:
                last_memory_error = "Skipped because crash_triage timeout budget was exhausted"
            else:
                try:
                    memory = await vs_bridge.call_pipe_exact(
                        target,
                        "read_memory",
                        {"address": stack_address, "size": 256},
                        timeout=memory_timeout,
                    )
                    if isinstance(memory, dict):
                        status = str(memory.get("status", "")).lower()
                        if status not in ("ok", "partial") and not memory.get("bytesRead"):
                            last_memory_error = memory.get("error") or memory.get("message") or f"{stack_register} unavailable"
                        else:
                            triage["stackMemory"] = memory
                            triage["stackMemoryAddressExpression"] = stack_register
                            triage["stackMemoryAddress"] = stack_address
                    else:
                        triage["stackMemory"] = memory
                        triage["stackMemoryAddressExpression"] = stack_register
                        triage["stackMemoryAddress"] = stack_address
                except (ide_bridge.IDEBridgeError, vs_bridge.VSBridgeError) as e:
                    last_memory_error = str(e)
        if "stackMemory" not in triage and last_memory_error:
            triage["stackMemoryError"] = last_memory_error

        return format_result(triage)
    except (ide_bridge.IDEBridgeError, vs_bridge.VSBridgeError) as e:
        return handle_ide_error(e, "crash_triage")

@mcp.tool()
async def step_and_capture(
    action: str = "step_over",
    expressions: Optional[List[str]] = None,
    include_locals: bool = False,
    include_modules: bool = False,
    include_registers: bool = False,
    include_output: bool = False,
    include_exception: bool = False,
    timeout_seconds: Optional[float] = None,
    include_threads: bool = True,
    thread_include_frames: bool = False,
    thread_max_frames_per_thread: Optional[int] = None,
    thread_max_nodes: Optional[int] = None,
    thread_budget_ms: Optional[int] = None,
) -> str:
    """
    Execute one step operation and capture a debug bundle at the new stop point.

    action accepts step_over, step_into, or step_out. Exception inspection is
    opt-in because the Visual Studio backend may evaluate exception expressions.
    The current callstack is captured; other threads default to metadata only.
    Set thread_include_frames=True for their frames, or include_threads=False
    to omit the thread list. Thread capture limits apply to this same bundle.
    """
    normalized = (action or "step_over").strip().lower().replace("-", "_").replace(" ", "_")
    aliases = {
        "over": "step_over",
        "stepover": "step_over",
        "debug_step_over": "step_over",
        "into": "step_into",
        "stepinto": "step_into",
        "debug_step_into": "step_into",
        "out": "step_out",
        "stepout": "step_out",
        "debug_step_out": "step_out",
    }
    normalized = aliases.get(normalized, normalized)
    method_by_action = {
        "step_over": "step_over",
        "step_into": "step_into",
        "step_out": "step_out",
    }
    normalized = validate_enum("action", normalized, tuple(method_by_action))

    normalized_expressions = _validate_step_capture_expressions(expressions)
    thread_params = add_thread_capture_params(
        {}, thread_include_frames, thread_max_frames_per_thread, thread_max_nodes, thread_budget_ms)
    tool_timeout = clamp_tool_timeout(timeout_seconds, DEBUG_BUNDLE_TIMEOUT_SECONDS)
    deadline = asyncio.get_running_loop().time() + tool_timeout
    try:
        step_timeout = remaining_tool_timeout(deadline, tool_timeout)
        if step_timeout is None:
            return handle_ide_error(
                ide_bridge.IDEBridgeError(
                    "Step was not started because the step_and_capture timeout budget was exhausted",
                    code="DEADLINE_EXCEEDED_NOT_STARTED",
                ),
                "step_and_capture",
            )
        step_result, target = await ide_bridge.call_with_target(
            method_by_action[normalized],
            {
                "waitTimeoutMs": max(
                    2000,
                    min(300000, int(max(0.1, step_timeout - 5.0) * 1000)),
                ),
            },
            timeout=step_timeout,
        )
    except (ide_bridge.IDEBridgeError, vs_bridge.VSBridgeError) as e:
        return handle_ide_error(e, "step_and_capture")

    bundle = None
    bundle_error = None
    bundle_error_code = None
    bundle_request_id = None
    bundle_outcome_unknown = False
    if (isinstance(step_result, dict) and step_result.get("success")
            and step_result.get("outcome") == "step_issued_break_not_observed"):
        bundle_error = "Step was issued, but no new stop was observed; context capture was skipped. Do not repeat the step."
        bundle_error_code = "STEP_STOP_NOT_OBSERVED"
    elif isinstance(step_result, dict) and step_result.get("success"):
        try:
            bundle_timeout = remaining_tool_timeout(deadline, tool_timeout)
            if bundle_timeout is None:
                bundle_error = "Skipped because step_and_capture timeout budget was exhausted"
                bundle_error_code = "DEADLINE_EXCEEDED_NOT_STARTED"
            else:
                bundle_params = {
                    "expressions": normalized_expressions,
                    "expandChildren": False,
                    "maxDepth": 1,
                    "outputTailLines": 80,
                    "includeLocals": include_locals,
                    "includeModules": include_modules,
                    "includeRegisters": include_registers,
                    "includeOutput": include_output,
                    "includeThreads": include_threads,
                    "includeException": include_exception,
                    "evaluationPolicy": "engine_default",
                }
                bundle_params.update(thread_params)
                bundle_method = "capture_debug_bundle_safe" if use_vs_safe_backend(
                    normalized_expressions,
                    include_locals=include_locals,
                    include_registers=include_registers,
                    include_exception=include_exception,
                ) else "capture_debug_bundle"
                if bundle_method == "capture_debug_bundle_safe":
                    for unsafe_parameter in (
                        "expressions", "expandChildren", "maxDepth", "evaluationPolicy",
                        "includeLocals", "includeRegisters", "includeException",
                    ):
                        bundle_params.pop(unsafe_parameter, None)
                bundle = await vs_bridge.call_pipe_exact(
                    target,
                    bundle_method,
                    bundle_params,
                    timeout=bundle_timeout,
                )
        except (ide_bridge.IDEBridgeError, vs_bridge.VSBridgeError) as e:
            # The step already completed. Preserve that definitive result and report
            # snapshot capture failure separately so the caller never replays the step.
            bundle_error = str(e)
            bundle_error_code = getattr(e, "code", "IDE_BRIDGE_ERROR")
            bundle_request_id = getattr(e, "request_id", None)
            bundle_outcome_unknown = bool(getattr(e, "outcome_unknown", False))

    result = {
        "action": normalized,
        "step": step_result,
        "bundle": bundle,
    }
    if bundle_error:
        result["bundleError"] = bundle_error
        result["bundleErrorCode"] = bundle_error_code
        result["bundleOutcomeUnknown"] = bundle_outcome_unknown
        if bundle_request_id:
            result["bundleRequestId"] = bundle_request_id
    return format_result(result)
