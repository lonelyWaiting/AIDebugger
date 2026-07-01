"""Shared helpers for AIDebugger MCP tool modules."""

import asyncio
import json
from typing import Any, Dict, List, Optional

try:
    from . import ide_bridge
except ImportError:
    import ide_bridge

__all__ = [
    "BATCH_EVALUATION_TIMEOUT_SECONDS",
    "EVALUATION_TIMEOUT_SECONDS",
    "SAFE_DEBUG_BUNDLE_TIMEOUT_SECONDS",
    "DEBUG_BUNDLE_TIMEOUT_SECONDS",
    "THREADS_TIMEOUT_SECONDS",
    "MODULES_TIMEOUT_SECONDS",
    "EXCEPTION_TIMEOUT_SECONDS",
    "OUTPUT_TIMEOUT_SECONDS",
    "START_DEBUGGING_TIMEOUT_SECONDS",
    "MEMORY_TIMEOUT_SECONDS",
    "DISASSEMBLY_TIMEOUT_SECONDS",
    "NATIVE_CONTEXT_TIMEOUT_SECONDS",
    "MANAGED_INSPECTION_TIMEOUT_SECONDS",
    "HYBRID_CONTEXT_TIMEOUT_SECONDS",
    "MAX_TOOL_TIMEOUT_SECONDS",
    "MAX_THREAD_BUDGET_SECONDS",
    "MAX_MEMORY_SEARCH_CHUNK_SIZE",
    "MAX_MEMORY_SEARCH_SIZE",
    "clamp_tool_timeout",
    "tool_timeout_for_budget",
    "remaining_tool_timeout",
    "add_thread_capture_params",
    "wait_for_debugging_to_start",
    "format_result",
    "handle_ide_error",
]

BATCH_EVALUATION_TIMEOUT_SECONDS = 45.0
EVALUATION_TIMEOUT_SECONDS = 30.0
SAFE_DEBUG_BUNDLE_TIMEOUT_SECONDS = 20.0
DEBUG_BUNDLE_TIMEOUT_SECONDS = 60.0
THREADS_TIMEOUT_SECONDS = 30.0
MODULES_TIMEOUT_SECONDS = 45.0
EXCEPTION_TIMEOUT_SECONDS = 20.0
OUTPUT_TIMEOUT_SECONDS = 20.0
START_DEBUGGING_TIMEOUT_SECONDS = 60.0
MEMORY_TIMEOUT_SECONDS = 20.0
DISASSEMBLY_TIMEOUT_SECONDS = 20.0
NATIVE_CONTEXT_TIMEOUT_SECONDS = 45.0
MANAGED_INSPECTION_TIMEOUT_SECONDS = 75.0
HYBRID_CONTEXT_TIMEOUT_SECONDS = 135.0
MAX_TOOL_TIMEOUT_SECONDS = 300.0
MAX_THREAD_BUDGET_SECONDS = 115.0
MAX_MEMORY_SEARCH_CHUNK_SIZE = 0x100000
MAX_MEMORY_SEARCH_SIZE = 0x40000000


def clamp_tool_timeout(timeout_seconds: Optional[float], default_seconds: float) -> float:
    if timeout_seconds is None:
        return default_seconds
    try:
        return max(1.0, min(float(timeout_seconds), MAX_TOOL_TIMEOUT_SECONDS))
    except (TypeError, ValueError):
        return default_seconds

def tool_timeout_for_budget(
    timeout_seconds: Optional[float],
    default_seconds: float,
    budget_ms: Optional[int],
    overhead_seconds: float = 15.0,
) -> float:
    if timeout_seconds is not None:
        return clamp_tool_timeout(timeout_seconds, default_seconds)
    try:
        if budget_ms is None:
            budget_seconds = 0.0
        elif float(budget_ms) == 0.0:
            budget_seconds = MAX_THREAD_BUDGET_SECONDS
        else:
            budget_seconds = float(budget_ms) / 1000.0
    except (TypeError, ValueError):
        budget_seconds = 0.0
    if budget_seconds <= 0:
        return default_seconds
    return min(MAX_TOOL_TIMEOUT_SECONDS, max(default_seconds, budget_seconds + overhead_seconds))

def remaining_tool_timeout(deadline: float, default_seconds: float) -> Optional[float]:
    remaining = deadline - asyncio.get_running_loop().time()
    if remaining <= 0:
        return None
    return max(1.0, min(default_seconds, remaining))

def add_thread_capture_params(
    params: Dict[str, Any],
    thread_include_frames: bool,
    thread_max_frames_per_thread: Optional[int],
    thread_max_nodes: Optional[int],
    thread_budget_ms: Optional[int],
) -> Dict[str, Any]:
    params["threadIncludeFrames"] = thread_include_frames
    if thread_max_frames_per_thread is not None:
        params["threadMaxFramesPerThread"] = int(thread_max_frames_per_thread)
    if thread_max_nodes is not None:
        params["threadMaxNodes"] = int(thread_max_nodes)
    if thread_budget_ms is not None:
        params["threadBudgetMs"] = int(thread_budget_ms)
    return params

async def wait_for_debugging_to_start(result: Any, timeout_seconds: float, attach: bool) -> Any:
    if not isinstance(result, dict) or not result.get("success") or attach is False:
        return result

    executable_path = str(result.get("executablePath") or "")
    wait_for_pause = executable_path.lower().endswith((".dmp", ".mdmp", ".hdmp"))
    status = result.get("status")
    if isinstance(status, dict) and status.get("isDebugging") and (not wait_for_pause or status.get("isPaused")):
        return result

    deadline = asyncio.get_running_loop().time() + timeout_seconds
    last_status = status
    last_error = None
    while asyncio.get_running_loop().time() < deadline:
        await asyncio.sleep(0.5)
        try:
            last_status = await ide_bridge.call("get_debug_status", timeout=5.0)
        except ide_bridge.IDEBridgeError as e:
            last_error = str(e)
            continue

        if isinstance(last_status, dict):
            result["status"] = last_status
            if last_status.get("isDebugging") and (not wait_for_pause or last_status.get("isPaused")):
                result["success"] = True
                result["message"] = "Started debugging" if not wait_for_pause else "Started dump debugging and paused"
                return result

    result["success"] = False
    result["message"] = (
        "Timed out waiting for Visual Studio to pause in dump debugging"
        if wait_for_pause
        else "Timed out waiting for Visual Studio to enter debugging mode"
    )
    if isinstance(last_status, dict):
        result["status"] = last_status
    if last_error:
        result["lastPollError"] = last_error
    return result

def format_result(result: Any) -> str:
    """Format result as readable text for MCP response."""
    if isinstance(result, dict):
        return json.dumps(result, indent=2, ensure_ascii=False)
    return str(result)

def handle_ide_error(e: Exception, operation: str) -> str:
    """Handle IDE bridge errors and return user-friendly message."""
    error_msg = str(e)
    if "No IDE instances found" in error_msg:
        return (
            "未找到IDE实例。请确保：\n"
            "1. Visual Studio 或 Rider 已启动\n"
            "2. 对应的插件已安装并启用\n"
            "3. 插件已正确加载（可能需要重启IDE）"
        )
    if "Preferred IDE" in error_msg:
        return f"Preferred IDE constraint: {error_msg}"
    if "Multiple IDE instances found" in error_msg:
        return error_msg  # Already formatted with instance list
    if "Failed to connect" in error_msg:
        return (
            "无法连接到IDE。请确保：\n"
            "1. Visual Studio 或 Rider 已启动\n"
            "2. 对应的插件已安装并启用\n"
            "3. 插件已正确加载（可能需要重启IDE）\n\n"
            f"错误详情: {error_msg}"
        )
    return f"执行 {operation} 时出错: {error_msg}"
