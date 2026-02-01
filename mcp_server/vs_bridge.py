"""
Named Pipe client for communication with Visual Studio Debugger MCP Extension.
Provides async interface for calling debugger methods over the named pipe.
Supports multiple VS instances with automatic discovery and smart selection.
"""

import json
import uuid
import asyncio
import ctypes
from ctypes import wintypes
from typing import Any, Dict, List, Optional
import logging
import re

logger = logging.getLogger(__name__)

PIPE_NAME_PREFIX = r"\\.\pipe\AIDebuggerVS"
DEFAULT_TIMEOUT = 10.0  # seconds


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
        
        if info:
            self.process_id = info.get("processId", 0)
            self.solution_name = info.get("solutionName", "")
            self.solution_path = info.get("solutionPath", "")
            self.is_debugging = info.get("isDebugging", False)
            self.is_paused = info.get("isPaused", False)
            self.debug_mode = info.get("debugMode", "")
            self.current_process = info.get("currentProcess", "")
            self.vs_version = info.get("vsVersion", "")
        
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
            "vsVersion": self.vs_version
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


def _sync_call_to_pipe(pipe_name: str, method: str, params: Optional[Dict[str, Any]], timeout: float = 5.0) -> Any:
    """Synchronous call to a specific named pipe."""
    # Use WinDLL with use_last_error=True to properly capture Windows error codes
    kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)
    
    # Constants for CreateFileW
    GENERIC_READ = 0x80000000
    GENERIC_WRITE = 0x40000000
    OPEN_EXISTING = 3
    FILE_ATTRIBUTE_NORMAL = 0x80
    
    full_pipe_name = f"\\\\.\\pipe\\{pipe_name}"
    
    # Set proper return type for 64-bit compatibility
    kernel32.CreateFileW.restype = wintypes.HANDLE
    kernel32.CreateFileW.argtypes = [
        wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
        wintypes.LPVOID, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE
    ]
    
    # Open the pipe (synchronous mode)
    handle = kernel32.CreateFileW(
        full_pipe_name,
        GENERIC_READ | GENERIC_WRITE,
        0,  # No sharing
        None,  # Default security
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,  # Synchronous mode
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
        request_bytes = request_json.encode('utf-8')
        
        # Write request
        bytes_written = wintypes.DWORD()
        success = kernel32.WriteFile(
            handle,
            request_bytes,
            len(request_bytes),
            ctypes.byref(bytes_written),
            None
        )
        
        if not success:
            error = ctypes.get_last_error()
            raise VSBridgeError(f"Failed to write to pipe. Error: {error}")
        
        # Read response
        buffer_size = 1024 * 1024  # 1MB buffer
        buffer = ctypes.create_string_buffer(buffer_size)
        bytes_read = wintypes.DWORD()
        
        # Read until newline
        response_bytes = b""
        while True:
            success = kernel32.ReadFile(
                handle,
                buffer,
                buffer_size,
                ctypes.byref(bytes_read),
                None
            )
            
            if not success:
                error = ctypes.get_last_error()
                if error == 109:  # ERROR_BROKEN_PIPE
                    raise VSBridgeError("Pipe disconnected by server")
                raise VSBridgeError(f"Failed to read from pipe. Error: {error}")
            
            if bytes_read.value == 0:
                raise VSBridgeError("No data received from pipe")
            
            chunk = buffer.raw[:bytes_read.value]
            response_bytes += chunk
            
            if b"\n" in response_bytes:
                break
        
        # Parse response
        response_json = response_bytes.decode('utf-8').strip()
        response = json.loads(response_json)
        
        if "error" in response and response["error"]:
            error = response["error"]
            raise VSBridgeError(f"{error.get('code', 'ERROR')}: {error.get('message', 'Unknown error')}")
        
        return response.get("result")
        
    finally:
        # Always close the handle
        kernel32.CloseHandle(handle)


class VSBridge:
    """
    Client for communicating with VS Debugger extension via Named Pipe.
    Supports multiple VS instances with automatic discovery and smart selection.
    """
    
    def __init__(self):
        self._lock = asyncio.Lock()
        self._selected_pipe: Optional[str] = None
        self._cached_instances: List[VSInstance] = []
    
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
                # Still add instance with minimal info
                instances.append(VSInstance(pipe_name))
        
        return instances
    
    async def discover_instances(self) -> List[VSInstance]:
        """
        Discover all available VS instances.
        Returns list of VSInstance objects with their current state.
        """
        async with self._lock:
            loop = asyncio.get_event_loop()
            instances = await loop.run_in_executor(None, self._sync_discover_instances)
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
    
    def _sync_call(self, pipe_name: str, method: str, params: Optional[Dict[str, Any]]) -> Any:
        """Synchronous call to the specified pipe."""
        return _sync_call_to_pipe(pipe_name, method, params)
    
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
        async with self._lock:
            pipe_name = self._selected_pipe
            
            # If no specific instance selected, auto-discover and select
            if pipe_name is None:
                loop = asyncio.get_event_loop()
                instances = await asyncio.wait_for(
                    loop.run_in_executor(None, self._sync_discover_instances),
                    timeout=timeout
                )
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
                    # Multiple instances, can't auto-select
                    instance_list = "\n".join([
                        f"  - PID {i.process_id}: {i.solution_name} ({i.debug_mode})"
                        for i in instances
                    ])
                    raise VSBridgeError(
                        f"Multiple VS instances found. Please use 'list_vs_instances' to see all instances "
                        f"and 'select_vs_instance' to choose one.\n\nAvailable instances:\n{instance_list}"
                    )
            
            try:
                # Run synchronous call in thread pool
                loop = asyncio.get_event_loop()
                result = await asyncio.wait_for(
                    loop.run_in_executor(None, self._sync_call, pipe_name, method, params),
                    timeout=timeout
                )
                return result
            except asyncio.TimeoutError:
                raise VSBridgeError(f"Timeout waiting for response from VS extension (method: {method})")
            except VSBridgeError:
                raise
            except Exception as e:
                raise VSBridgeError(f"Error calling VS extension: {e}")


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
        return len(instances) > 0
    except:
        return False
