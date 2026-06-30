"""
Named Pipe client for communication with Visual Studio Debugger MCP Extension.
Provides async interface for calling debugger methods over the named pipe.
Supports multiple VS instances with automatic discovery and smart selection.
"""

import json
import uuid
import asyncio
import ctypes
import time
import concurrent.futures
from ctypes import wintypes
from typing import Any, Dict, List, Optional
import logging
import re

logger = logging.getLogger(__name__)

PIPE_NAME_PREFIX = r"\\.\pipe\AIDebuggerVS"
DEFAULT_TIMEOUT = 10.0  # seconds

FILE_FLAG_OVERLAPPED = 0x40000000
ERROR_BROKEN_PIPE = 109
ERROR_IO_PENDING = 997
ERROR_OPERATION_ABORTED = 995
ERROR_NOT_FOUND = 1168
WAIT_OBJECT_0 = 0
WAIT_TIMEOUT = 258

ULONG_PTR = ctypes.c_ulonglong if ctypes.sizeof(ctypes.c_void_p) == 8 else ctypes.c_ulong


class OVERLAPPED(ctypes.Structure):
    _fields_ = [
        ("Internal", ULONG_PTR),
        ("InternalHigh", ULONG_PTR),
        ("Offset", wintypes.DWORD),
        ("OffsetHigh", wintypes.DWORD),
        ("hEvent", wintypes.HANDLE),
    ]


class VSBridgeError(Exception):
    """Error communicating with Visual Studio extension."""
    pass


class VSInstance:
    """Represents a discovered Visual Studio instance."""
    
    def __init__(self, pipe_name: str, info: Optional[Dict[str, Any]] = None):
        self.pipe_name = pipe_name
        self.process_id = 0
        self.solution_name = ""
        self.solution_path = ""
        self.is_debugging = False
        self.is_paused = False
        self.debug_mode = ""
        self.current_process = ""
        self.vs_version = ""
        self.connection_status = "ok"
        self.last_error = ""
        
        if info:
            self.process_id = info.get("processId", 0)
            self.solution_name = info.get("solutionName", "")
            self.solution_path = info.get("solutionPath", "")
            self.is_debugging = info.get("isDebugging", False)
            self.is_paused = info.get("isPaused", False)
            self.debug_mode = info.get("debugMode", "")
            self.current_process = info.get("currentProcess", "")
            self.vs_version = info.get("vsVersion", "")
            self.connection_status = info.get("connectionStatus", self.connection_status)
            self.last_error = info.get("lastError", self.last_error)
        
        # If process_id is still 0, try to extract from pipe name
        if self.process_id == 0 and pipe_name.startswith("AIDebuggerVS_"):
            try:
                self.process_id = int(pipe_name.split("_")[1])
            except (ValueError, IndexError):
                pass
    
    def to_dict(self) -> Dict[str, Any]:
        return {
            "pipeName": self.pipe_name,
            "processId": self.process_id,
            "solutionName": self.solution_name,
            "solutionPath": self.solution_path,
            "isDebugging": self.is_debugging,
            "isPaused": self.is_paused,
            "debugMode": self.debug_mode,
            "currentProcess": self.current_process,
            "vsVersion": self.vs_version,
            "connectionStatus": self.connection_status,
            "lastError": self.last_error
        }
    
    def __repr__(self):
        status = "Paused" if self.is_paused else ("Debugging" if self.is_debugging else "Idle")
        return f"VSInstance(pid={self.process_id}, solution='{self.solution_name}', status={status})"


def _discover_vs_pipes() -> List[str]:
    """
    Discover all VS Debugger MCP named pipes.
    Returns list of pipe names like ['AIDebuggerVS_1234', 'AIDebuggerVS_5678']
    """
    import os
    
    pipes = []
    pipe_dir = r"\\.\pipe"
    
    # Method 1: Try using os.listdir (most reliable on modern Windows)
    try:
        all_pipes = os.listdir(pipe_dir)
        for pipe_name in all_pipes:
            if pipe_name.startswith("AIDebuggerVS_"):
                pipes.append(pipe_name)
        if pipes:
            logger.debug(f"Found pipes via os.listdir: {pipes}")
            return pipes
    except Exception as e:
        logger.debug(f"os.listdir failed: {e}, trying Windows API")
    
    # Method 2: Fallback to Windows API with proper 64-bit handling
    try:
        kernel32 = ctypes.windll.kernel32
        
        # For 64-bit Python, INVALID_HANDLE_VALUE is -1 as a signed value
        # which equals 0xFFFFFFFFFFFFFFFF as unsigned
        
        class WIN32_FIND_DATAW(ctypes.Structure):
            _fields_ = [
                ("dwFileAttributes", wintypes.DWORD),
                ("ftCreationTime", wintypes.FILETIME),
                ("ftLastAccessTime", wintypes.FILETIME),
                ("ftLastWriteTime", wintypes.FILETIME),
                ("nFileSizeHigh", wintypes.DWORD),
                ("nFileSizeLow", wintypes.DWORD),
                ("dwReserved0", wintypes.DWORD),
                ("dwReserved1", wintypes.DWORD),
                ("cFileName", wintypes.WCHAR * 260),
                ("cAlternateFileName", wintypes.WCHAR * 14),
            ]
        
        find_data = WIN32_FIND_DATAW()
        
        # Set return type to handle pointer comparison correctly
        kernel32.FindFirstFileW.restype = wintypes.HANDLE
        kernel32.FindNextFileW.argtypes = [wintypes.HANDLE, ctypes.POINTER(WIN32_FIND_DATAW)]
        kernel32.FindClose.argtypes = [wintypes.HANDLE]
        
        handle = kernel32.FindFirstFileW(
            r"\\.\pipe\AIDebuggerVS_*",
            ctypes.byref(find_data)
        )
        
        # Check for invalid handle (works for both 32-bit and 64-bit)
        INVALID_HANDLE = wintypes.HANDLE(-1).value
        if handle == INVALID_HANDLE:
            logger.debug("No pipes found via Windows API")
            return pipes
        
        try:
            while True:
                pipe_name = find_data.cFileName
                if pipe_name and pipe_name.startswith("AIDebuggerVS_"):
                    if pipe_name not in pipes:  # Avoid duplicates
                        pipes.append(pipe_name)
                
                # Find next - create new find_data for each iteration
                find_data = WIN32_FIND_DATAW()
                if not kernel32.FindNextFileW(handle, ctypes.byref(find_data)):
                    break
        finally:
            kernel32.FindClose(handle)
        
        logger.debug(f"Found pipes via Windows API: {pipes}")
            
    except Exception as e:
        logger.warning(f"Error discovering pipes: {e}")
    
    return pipes


def _close_win_handle(kernel32: ctypes.WinDLL, handle: wintypes.HANDLE) -> None:
    if handle:
        kernel32.CloseHandle(handle)


def _create_overlapped(kernel32: ctypes.WinDLL) -> tuple[OVERLAPPED, wintypes.HANDLE]:
    event = kernel32.CreateEventW(None, True, False, None)
    if not event:
        raise VSBridgeError(f"Failed to create overlapped event. Error: {ctypes.get_last_error()}")

    overlapped = OVERLAPPED()
    overlapped.hEvent = event
    return overlapped, event


def _cancel_pending_io(kernel32: ctypes.WinDLL, handle: wintypes.HANDLE, overlapped: OVERLAPPED) -> None:
    try:
        cancelled = False
        if hasattr(kernel32, "CancelIoEx"):
            cancelled = bool(kernel32.CancelIoEx(handle, ctypes.byref(overlapped)))
            if not cancelled and ctypes.get_last_error() == ERROR_NOT_FOUND:
                cancelled = True
        elif hasattr(kernel32, "CancelIo"):
            cancelled = bool(kernel32.CancelIo(handle))

        if cancelled:
            kernel32.WaitForSingleObject(overlapped.hEvent, 10)
    except Exception:
        # Best-effort cancellation.
        pass


def _wait_for_overlapped_result(
    kernel32: ctypes.WinDLL,
    handle: wintypes.HANDLE,
    overlapped: OVERLAPPED,
    bytes_transferred: wintypes.DWORD,
    timeout_ms: int,
    operation: str,
    timeout_message: str,
) -> int:
    wait_result = kernel32.WaitForSingleObject(overlapped.hEvent, timeout_ms)
    if wait_result == WAIT_OBJECT_0:
        ok = kernel32.GetOverlappedResult(handle, ctypes.byref(overlapped), ctypes.byref(bytes_transferred), False)
        if not ok:
            error = ctypes.get_last_error()
            if error in (ERROR_BROKEN_PIPE, ERROR_OPERATION_ABORTED):
                raise VSBridgeError("Pipe disconnected by server")
            raise VSBridgeError(f"{operation} failed. Error: {error}")
        return bytes_transferred.value

    if wait_result == WAIT_TIMEOUT:
        _cancel_pending_io(kernel32, handle, overlapped)
        raise VSBridgeError(timeout_message)

    _cancel_pending_io(kernel32, handle, overlapped)
    raise VSBridgeError(f"{operation} wait failed. Error: {ctypes.get_last_error()}")


def _sync_call_to_pipe(pipe_name: str, method: str, params: Optional[Dict[str, Any]], timeout: float = 5.0) -> Any:
    """Synchronous call to a specific named pipe."""
    timeout = max(timeout, 0.1)
    deadline = time.monotonic() + timeout

    def remaining_timeout_ms() -> int:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            return 0
        return max(1, int(remaining * 1000))

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

    GENERIC_READ = 0x80000000
    GENERIC_WRITE = 0x40000000
    OPEN_EXISTING = 3
    FILE_ATTRIBUTE_NORMAL = 0x80

    full_pipe_name = f"\\\\.\\pipe\\{pipe_name}"

    kernel32.CreateFileW.restype = wintypes.HANDLE
    kernel32.CreateFileW.argtypes = [
        wintypes.LPCWSTR,
        wintypes.DWORD,
        wintypes.DWORD,
        wintypes.LPVOID,
        wintypes.DWORD,
        wintypes.DWORD,
        wintypes.HANDLE,
    ]
    kernel32.WaitNamedPipeW.restype = wintypes.BOOL
    kernel32.WaitNamedPipeW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD]
    kernel32.SetNamedPipeHandleState.restype = wintypes.BOOL
    kernel32.SetNamedPipeHandleState.argtypes = [
        wintypes.HANDLE,
        ctypes.POINTER(wintypes.DWORD),
        wintypes.LPVOID,
        wintypes.LPVOID,
    ]
    kernel32.PeekNamedPipe.restype = wintypes.BOOL
    kernel32.PeekNamedPipe.argtypes = [
        wintypes.HANDLE,
        wintypes.LPVOID,
        wintypes.DWORD,
        wintypes.LPVOID,
        ctypes.POINTER(wintypes.DWORD),
        wintypes.LPVOID,
    ]
    kernel32.WriteFile.restype = wintypes.BOOL
    kernel32.WriteFile.argtypes = [
        wintypes.HANDLE,
        wintypes.LPVOID,
        wintypes.DWORD,
        ctypes.POINTER(wintypes.DWORD),
        ctypes.c_void_p,
    ]
    kernel32.ReadFile.restype = wintypes.BOOL
    kernel32.ReadFile.argtypes = [
        wintypes.HANDLE,
        wintypes.LPVOID,
        wintypes.DWORD,
        ctypes.POINTER(wintypes.DWORD),
        ctypes.c_void_p,
    ]
    kernel32.CreateEventW.restype = wintypes.HANDLE
    kernel32.CreateEventW.argtypes = [wintypes.LPVOID, wintypes.BOOL, wintypes.BOOL, wintypes.LPCWSTR]
    kernel32.WaitForSingleObject.restype = wintypes.DWORD
    kernel32.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    kernel32.GetOverlappedResult.restype = wintypes.BOOL
    kernel32.GetOverlappedResult.argtypes = [
        wintypes.HANDLE,
        ctypes.c_void_p,
        ctypes.POINTER(wintypes.DWORD),
        wintypes.BOOL,
    ]
    if hasattr(kernel32, "CancelIoEx"):
        kernel32.CancelIoEx.restype = wintypes.BOOL
        kernel32.CancelIoEx.argtypes = [wintypes.HANDLE, ctypes.c_void_p]
    if hasattr(kernel32, "CancelIo"):
        kernel32.CancelIo.restype = wintypes.BOOL
        kernel32.CancelIo.argtypes = [wintypes.HANDLE]
    kernel32.CloseHandle.restype = wintypes.BOOL
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]

    # Avoid blocking indefinitely when the pipe exists but has no available server instance.
    wait_timeout = remaining_timeout_ms()
    if wait_timeout <= 0:
        raise VSBridgeError(f"Timeout waiting to connect to '{pipe_name}'")
    waited = kernel32.WaitNamedPipeW(full_pipe_name, wait_timeout)
    if not waited:
        error = ctypes.get_last_error()
        error_messages = {
            2: f"Named pipe '{pipe_name}' not found.",
            5: "Access denied to named pipe. VS may be running as admin while Python is not.",
            121: f"Timeout waiting for named pipe '{pipe_name}'.",
        }
        msg = error_messages.get(error, f"Unknown error code: {error}")
        raise VSBridgeError(f"Failed to connect to '{pipe_name}'. {msg}")
    
    # Open the pipe in overlapped mode so timed-out operations can be canceled.
    handle = kernel32.CreateFileW(
        full_pipe_name,
        GENERIC_READ | GENERIC_WRITE,
        0,  # No sharing
        None,  # Default security
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED,
        None
    )
    
    # Check for invalid handle (works correctly on both 32-bit and 64-bit)
    INVALID_HANDLE_VALUE = wintypes.HANDLE(-1).value
    if handle == INVALID_HANDLE_VALUE:
        error = ctypes.get_last_error()
        error_messages = {
            2: f"Named pipe '{pipe_name}' not found.",
            5: "Access denied to named pipe. VS may be running as admin while Python is not.",
            231: "All pipe instances are busy. Try again later.",
        }
        msg = error_messages.get(error, f"Unknown error code: {error}")
        raise VSBridgeError(f"Failed to connect to '{pipe_name}'. {msg}")
    
    try:
        # Set pipe to byte mode
        PIPE_READMODE_BYTE = 0x00000000
        mode = wintypes.DWORD(PIPE_READMODE_BYTE)
        kernel32.SetNamedPipeHandleState(handle, ctypes.byref(mode), None, None)
        
        # Build request
        request = {
            "id": str(uuid.uuid4()),
            "method": method,
            "params": params or {}
        }
        
        request_json = json.dumps(request) + "\n"
        request_bytes = request_json.encode("utf-8")
        request_buffer = ctypes.create_string_buffer(request_bytes)

        write_overlapped, write_event = _create_overlapped(kernel32)
        bytes_written = wintypes.DWORD()
        try:
            success = kernel32.WriteFile(
                handle,
                request_buffer,
                len(request_bytes),
                ctypes.byref(bytes_written),
                ctypes.byref(write_overlapped),
            )

            if not success:
                error = ctypes.get_last_error()
                if error == ERROR_IO_PENDING:
                    write_timeout = remaining_timeout_ms()
                    if write_timeout <= 0:
                        _cancel_pending_io(kernel32, handle, write_overlapped)
                        raise VSBridgeError(f"Timeout writing request to '{pipe_name}' (method: {method})")
                    _wait_for_overlapped_result(
                        kernel32,
                        handle,
                        write_overlapped,
                        bytes_written,
                        write_timeout,
                        "WriteFile",
                        f"Timeout writing request to '{pipe_name}' (method: {method})",
                    )
                else:
                    raise VSBridgeError(f"Failed to write to pipe. Error: {error}")

            if bytes_written.value != len(request_bytes):
                raise VSBridgeError(
                    f"Incomplete write to pipe '{pipe_name}' (expected {len(request_bytes)}, wrote {bytes_written.value})"
                )
        finally:
            _close_win_handle(kernel32, write_event)
        
        # Read response
        buffer_size = 1024 * 1024  # 1MB buffer
        buffer = ctypes.create_string_buffer(buffer_size)
        bytes_read = wintypes.DWORD()
        
        # Read until newline
        response_bytes = b""
        while True:
            remaining = remaining_timeout_ms()
            if remaining <= 0:
                raise VSBridgeError(f"Timeout waiting for response from '{pipe_name}' (method: {method})")

            bytes_available = wintypes.DWORD()
            peek_ok = kernel32.PeekNamedPipe(
                handle,
                None,
                0,
                None,
                ctypes.byref(bytes_available),
                None
            )
            if not peek_ok:
                error = ctypes.get_last_error()
                if error == 109:  # ERROR_BROKEN_PIPE
                    raise VSBridgeError("Pipe disconnected by server")
                raise VSBridgeError(f"Failed to peek pipe. Error: {error}")

            if bytes_available.value == 0:
                time.sleep(min(0.02, max(0.0, remaining / 1000.0)))
                continue

            to_read = min(buffer_size, bytes_available.value)
            read_overlapped, read_event = _create_overlapped(kernel32)
            try:
                success = kernel32.ReadFile(
                    handle,
                    buffer,
                    to_read,
                    ctypes.byref(bytes_read),
                    ctypes.byref(read_overlapped),
                )

                if not success:
                    error = ctypes.get_last_error()
                    if error == ERROR_IO_PENDING:
                        read_timeout = remaining_timeout_ms()
                        if read_timeout <= 0:
                            _cancel_pending_io(kernel32, handle, read_overlapped)
                            raise VSBridgeError(
                                f"Timeout waiting for response from '{pipe_name}' (method: {method})"
                            )
                        _wait_for_overlapped_result(
                            kernel32,
                            handle,
                            read_overlapped,
                            bytes_read,
                            read_timeout,
                            "ReadFile",
                            f"Timeout waiting for response from '{pipe_name}' (method: {method})",
                        )
                    elif error == ERROR_BROKEN_PIPE:
                        raise VSBridgeError("Pipe disconnected by server")
                    else:
                        raise VSBridgeError(f"Failed to read from pipe. Error: {error}")
            finally:
                _close_win_handle(kernel32, read_event)

            if bytes_read.value == 0:
                raise VSBridgeError("No data received from pipe")
            
            chunk = buffer.raw[:bytes_read.value]
            response_bytes += chunk
            
            if b"\n" in response_bytes:
                break
        
        # Parse response
        response_line = response_bytes.split(b"\n", 1)[0]
        response_json = response_line.decode('utf-8').strip()
        if not response_json:
            raise VSBridgeError("Empty response received from pipe")
        response = json.loads(response_json)
        
        if "error" in response and response["error"]:
            error = response["error"]
            raise VSBridgeError(f"{error.get('code', 'ERROR')}: {error.get('message', 'Unknown error')}")
        
        return response.get("result")
        
    finally:
        # Always close the handle
        _close_win_handle(kernel32, handle)


class VSBridge:
    """
    Client for communicating with VS Debugger extension via Named Pipe.
    Supports multiple VS instances with automatic discovery and smart selection.
    """
    
    _CALL_TIMEOUT_GUARD = 0.75
    _SYNC_TIMEOUT_MARGIN = 0.2
    _MIN_SYNC_TIMEOUT = 0.1
    _MAX_WORKERS = 8

    def __init__(self):
        self._lock = asyncio.Lock()
        self._selected_pipe: Optional[str] = None
        self._cached_instances: List[VSInstance] = []
        self._executor = concurrent.futures.ThreadPoolExecutor(
            max_workers=self._MAX_WORKERS,
            thread_name_prefix="aidb-vs-bridge",
        )

    def _compute_timeouts(self, timeout: float) -> tuple[float, float, float]:
        request_timeout = max(float(timeout), self._MIN_SYNC_TIMEOUT)
        sync_timeout = max(self._MIN_SYNC_TIMEOUT, request_timeout - self._SYNC_TIMEOUT_MARGIN)
        guard_timeout = request_timeout + self._CALL_TIMEOUT_GUARD
        return request_timeout, sync_timeout, guard_timeout
    
    def _sync_discover_instances(self) -> List[VSInstance]:
        """Discover all VS instances and get their info."""
        pipe_names = _discover_vs_pipes()
        instances = []
        
        for pipe_name in pipe_names:
            try:
                info = _sync_call_to_pipe(pipe_name, "get_instance_info", None, timeout=3.0)
                logger.debug(f"get_instance_info response for {pipe_name}: {info}")
                instance = VSInstance(pipe_name, info)
                instances.append(instance)
            except Exception as e:
                logger.warning(f"Failed to get info from {pipe_name}: {e}")
                import traceback
                logger.warning(f"Traceback: {traceback.format_exc()}")
                # Keep the pipe visible for diagnostics, but exclude it from auto-selection.
                instances.append(VSInstance(pipe_name, {
                    "connectionStatus": "unreachable",
                    "lastError": str(e),
                }))
        
        return instances
    
    async def discover_instances(self) -> List[VSInstance]:
        """
        Discover all available VS instances.
        Returns list of VSInstance objects with their current state.
        """
        loop = asyncio.get_event_loop()
        _, _, guard_timeout = self._compute_timeouts(DEFAULT_TIMEOUT)
        instances = await asyncio.wait_for(
            loop.run_in_executor(self._executor, self._sync_discover_instances),
            timeout=guard_timeout,
        )
        async with self._lock:
            self._cached_instances = instances
        return instances
    
    def select_instance(self, pipe_name: Optional[str] = None, process_id: Optional[int] = None):
        """
        Select a specific VS instance to communicate with.
        
        Args:
            pipe_name: Full pipe name (e.g., 'AIDebuggerVS_1234')
            process_id: VS process ID (will be converted to pipe name)
        """
        if process_id is not None:
            self._selected_pipe = f"AIDebuggerVS_{process_id}"
        elif pipe_name is not None:
            self._selected_pipe = pipe_name
        else:
            self._selected_pipe = None
    
    def clear_selection(self):
        """Clear the selected instance, allowing auto-selection."""
        self._selected_pipe = None
    
    def _find_best_instance(self, instances: List[VSInstance]) -> Optional[str]:
        """
        Find the best VS instance to use.
        Priority:
        1. If only one instance is paused (in break mode), use it
        2. If only one instance is debugging, use it
        3. If only one instance exists, use it
        4. Otherwise, return None (user must select)
        """
        if not instances:
            return None

        instances = [i for i in instances if i.connection_status == "ok"]
        if not instances:
            return None
        
        # If only one instance, use it
        if len(instances) == 1:
            return instances[0].pipe_name
        
        # Find paused instances (highest priority for debugging questions)
        paused = [i for i in instances if i.is_paused]
        if len(paused) == 1:
            return paused[0].pipe_name
        
        # Find debugging instances
        debugging = [i for i in instances if i.is_debugging]
        if len(debugging) == 1:
            return debugging[0].pipe_name
        
        # Multiple instances in same state - can't auto-select
        return None
    
    def _sync_call(self, pipe_name: str, method: str, params: Optional[Dict[str, Any]], timeout: float) -> Any:
        """Synchronous call to the specified pipe."""
        return _sync_call_to_pipe(pipe_name, method, params, timeout=timeout)

    async def call_pipe(self, pipe_name: str, method: str, params: Optional[Dict[str, Any]] = None, timeout: float = DEFAULT_TIMEOUT) -> Any:
        """Call a method on a specific VS pipe without reading mutable global selection."""
        if not pipe_name:
            raise VSBridgeError("No Visual Studio pipe selected")

        request_timeout, sync_timeout, guard_timeout = self._compute_timeouts(timeout)
        try:
            loop = asyncio.get_event_loop()
            future = loop.run_in_executor(
                self._executor,
                self._sync_call,
                pipe_name,
                method,
                params,
                sync_timeout,
            )
            return await asyncio.wait_for(future, timeout=guard_timeout)
        except asyncio.TimeoutError:
            raise VSBridgeError(
                f"Timeout waiting for response from VS extension (method: {method}, timeout={request_timeout:.1f}s)"
            )
        except VSBridgeError:
            raise
        except Exception as e:
            raise VSBridgeError(f"Error calling VS extension: {e}")
    
    async def call(self, method: str, params: Optional[Dict[str, Any]] = None, timeout: float = DEFAULT_TIMEOUT) -> Any:
        """
        Call a method on the VS Debugger extension.
        
        If a specific instance is selected, uses that.
        Otherwise, auto-discovers instances and uses smart selection.
        
        Args:
            method: The method name to call
            params: Optional parameters for the method
            timeout: Timeout in seconds
            
        Returns:
            The result from the VS extension
            
        Raises:
            VSBridgeError: If communication fails or no suitable instance found
        """
        request_timeout, sync_timeout, guard_timeout = self._compute_timeouts(timeout)
        async with self._lock:
            pipe_name = self._selected_pipe

        # If no specific instance selected, auto-discover and select.
        if pipe_name is None:
            loop = asyncio.get_event_loop()
            instances = await asyncio.wait_for(
                loop.run_in_executor(self._executor, self._sync_discover_instances),
                timeout=guard_timeout
            )
            async with self._lock:
                self._cached_instances = instances

            if not instances:
                raise VSBridgeError(
                    "No Visual Studio instances found. Please ensure:\n"
                    "1. Visual Studio is running\n"
                    "2. AIDebuggerVS extension is installed and enabled\n"
                    "3. The extension has loaded (may need to restart VS)"
                )

            pipe_name = self._find_best_instance(instances)

            if pipe_name is None:
                instance_list = "\n".join([
                    f"  - PID {i.process_id}: {i.solution_name} ({i.debug_mode}) "
                    f"[{i.connection_status}{': ' + i.last_error if i.last_error else ''}]"
                    for i in instances
                ])
                raise VSBridgeError(
                    f"Multiple VS instances found or no reachable instance could be auto-selected. "
                    f"Please use 'list_vs_instances' to see all instances and "
                    f"'select_vs_instance' to choose one.\n\nAvailable instances:\n{instance_list}"
                )

        return await self.call_pipe(pipe_name, method, params, timeout)


# Global bridge instance
_bridge: Optional[VSBridge] = None


def get_bridge() -> VSBridge:
    """Get the global VSBridge instance."""
    global _bridge
    if _bridge is None:
        _bridge = VSBridge()
    return _bridge


async def call(method: str, params: Optional[Dict[str, Any]] = None, timeout: float = DEFAULT_TIMEOUT) -> Any:
    """
    Convenience function to call a method on the VS extension.
    
    Args:
        method: The method name to call
        params: Optional parameters for the method
        timeout: Timeout in seconds
        
    Returns:
        The result from the VS extension
    """
    bridge = get_bridge()
    return await bridge.call(method, params, timeout)


async def call_pipe(pipe_name: str, method: str, params: Optional[Dict[str, Any]] = None, timeout: float = DEFAULT_TIMEOUT) -> Any:
    """Convenience function to call a specific VS pipe without using global selection."""
    bridge = get_bridge()
    return await bridge.call_pipe(pipe_name, method, params, timeout)


async def discover_instances() -> List[VSInstance]:
    """
    Discover all available VS instances.
    
    Returns:
        List of VSInstance objects
    """
    bridge = get_bridge()
    return await bridge.discover_instances()


def select_instance(pipe_name: Optional[str] = None, process_id: Optional[int] = None):
    """
    Select a specific VS instance to use.
    
    Args:
        pipe_name: Full pipe name (e.g., 'AIDebuggerVS_1234')
        process_id: VS process ID
    """
    bridge = get_bridge()
    bridge.select_instance(pipe_name=pipe_name, process_id=process_id)


def clear_selection():
    """Clear the selected instance, allowing auto-selection."""
    bridge = get_bridge()
    bridge.clear_selection()


async def is_vs_available() -> bool:
    """Check if any VS extension is available."""
    try:
        instances = await discover_instances()
        return any(i.connection_status == "ok" for i in instances)
    except:
        return False
