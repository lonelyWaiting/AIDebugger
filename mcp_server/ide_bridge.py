"""
Unified IDE bridge for VS and Rider.
"""

import asyncio
import os
from typing import Any, Dict, List, Optional, Tuple

try:
    from . import vs_bridge
    from . import rider_bridge
except ImportError:
    import vs_bridge
    import rider_bridge


class IDEBridgeError(Exception):
    """Error communicating with IDE extensions."""
    pass


class IDEInstance:
    def __init__(self, ide: str, instance: Any):
        self.ide = ide
        self.pipe_name = instance.pipe_name
        self.process_id = instance.process_id
        self.solution_name = instance.solution_name
        self.solution_path = instance.solution_path
        self.is_debugging = instance.is_debugging
        self.is_paused = instance.is_paused
        self.debug_mode = instance.debug_mode
        self.current_process = instance.current_process
        self.ide_version = getattr(instance, "ide_version", "") or getattr(instance, "vs_version", "")
        self.ide_name = getattr(instance, "ide_name", "") or ("Visual Studio" if ide == "vs" else "Rider")
        self.connection_status = getattr(instance, "connection_status", "ok")
        self.last_error = getattr(instance, "last_error", "")

    def to_dict(self) -> Dict[str, Any]:
        return {
            "ide": self.ide,
            "pipeName": self.pipe_name,
            "processId": self.process_id,
            "solutionName": self.solution_name,
            "solutionPath": self.solution_path,
            "isDebugging": self.is_debugging,
            "isPaused": self.is_paused,
            "debugMode": self.debug_mode,
            "currentProcess": self.current_process,
            "ideName": self.ide_name,
            "ideVersion": self.ide_version,
            "connectionStatus": self.connection_status,
            "lastError": self.last_error,
        }

    def __repr__(self) -> str:
        status = "Paused" if self.is_paused else ("Debugging" if self.is_debugging else "Idle")
        return f"IDEInstance(ide={self.ide}, pid={self.process_id}, solution='{self.solution_name}', status={status})"


def _registry_preferred_ide_for_method(method: str) -> Optional[str]:
    try:
        from .tool_registry import preferred_ide_for_backend_method
    except ImportError:
        from tool_registry import preferred_ide_for_backend_method

    return preferred_ide_for_backend_method(method)


class IDEBridge:
    def __init__(self):
        self._lock = asyncio.Lock()
        self._selected_ide: Optional[str] = None
        self._selected_pipe: Optional[str] = None
        self._preferred_ide = self._normalize_ide(os.environ.get("AIDB_PREFERRED_IDE") or os.environ.get("AIDebuggerPreferredIDE"))

    def _normalize_ide(self, ide: Optional[str]) -> Optional[str]:
        if ide is None:
            return None
        value = ide.strip().lower()
        if value in ("vs", "visualstudio", "visual-studio", "visual_studio"):
            return "vs"
        if value in ("rider", "jetbrainsrider", "jetbrains-rider", "jetbrains_rider"):
            return "rider"
        return None

    def _resolve_preferred_ide(self, preferred_ide: Optional[str]) -> Optional[str]:
        if self._preferred_ide:
            return self._preferred_ide
        if preferred_ide is None:
            return None
        return self._normalize_ide(preferred_ide)

    def get_candidate_instances(
        self,
        instances: List[IDEInstance],
        preferred_ide: Optional[str] = None
    ) -> Tuple[List[IDEInstance], Optional[str]]:
        preferred = self._resolve_preferred_ide(preferred_ide)
        if preferred:
            candidates = [i for i in instances if i.ide == preferred]
            if not candidates:
                detected = ", ".join(sorted({i.ide for i in instances})) or "none"
                raise IDEBridgeError(
                    f"Preferred IDE '{preferred}' not found. Detected IDEs: {detected}. "
                    "Please start the preferred IDE or switch to the matching MCP server."
                )
            return candidates, preferred
        return instances, None

    async def discover_instances(self, preferred_ide: Optional[str] = None) -> List[IDEInstance]:
        preferred = self._resolve_preferred_ide(preferred_ide)
        result: List[IDEInstance] = []
        tasks = []
        labels = []

        if preferred in (None, "vs"):
            tasks.append(vs_bridge.discover_instances())
            labels.append("vs")
        if preferred in (None, "rider"):
            tasks.append(rider_bridge.discover_instances())
            labels.append("rider")

        outcomes = await asyncio.gather(*tasks, return_exceptions=True)
        errors = []
        for label, outcome in zip(labels, outcomes):
            if isinstance(outcome, Exception):
                errors.append(f"{label}: {outcome}")
                continue
            result.extend([IDEInstance(label, inst) for inst in outcome])

        if not result and errors:
            raise IDEBridgeError("IDE discovery failed: " + "; ".join(errors))
        return result

    def select_instance(self, ide: Optional[str] = None, process_id: Optional[int] = None, pipe_name: Optional[str] = None):
        if ide not in (None, "vs", "rider"):
            raise IDEBridgeError("ide must be 'vs' or 'rider'")
        if self._preferred_ide and ide is not None and ide != self._preferred_ide:
            raise IDEBridgeError(
                f"Preferred IDE '{self._preferred_ide}' is locked for this MCP server. "
                f"Cannot select '{ide}'."
            )
        if pipe_name is None and process_id is not None:
            if ide == "vs":
                pipe_name = f"AIDebuggerVS_{process_id}"
            elif ide == "rider":
                pipe_name = f"AIDebuggerRider_{process_id}"

        self._selected_ide = ide
        self._selected_pipe = pipe_name

        if ide == "vs":
            vs_bridge.select_instance(pipe_name=pipe_name, process_id=process_id)
            rider_bridge.clear_selection()
        elif ide == "rider":
            rider_bridge.select_instance(pipe_name=pipe_name, process_id=process_id)
            vs_bridge.clear_selection()
        else:
            vs_bridge.clear_selection()
            rider_bridge.clear_selection()

    def clear_selection(self):
        self._selected_ide = None
        self._selected_pipe = None
        vs_bridge.clear_selection()
        rider_bridge.clear_selection()

    def _is_stale_selected_instance_error(self, error: Exception) -> bool:
        message = str(error).lower()
        non_stale_markers = (
            "access denied",
            "permission",
            "running as admin",
        )
        if any(marker in message for marker in non_stale_markers):
            return False

        stale_markers = (
            "broken pipe",
            "no data received from pipe",
            "empty response received from pipe",
            "pipe disconnected",
            "timeout waiting to connect",
            "cannot find the file specified",
            "failed to connect",
            "error: 109",
            "error: 2",
            "error: 232",
        )
        return any(marker in message for marker in stale_markers)

    async def call(
        self,
        method: str,
        params: Optional[Dict[str, Any]] = None,
        timeout: float = 10.0,
        preferred_ide: Optional[str] = None,
    ) -> Any:
        if preferred_ide is None:
            preferred_ide = _registry_preferred_ide_for_method(method)
        requested_preferred = self._normalize_ide(preferred_ide)
        if preferred_ide is not None and requested_preferred is None:
            raise IDEBridgeError("preferred_ide must be 'vs' or 'rider'")
        if requested_preferred and self._preferred_ide and self._preferred_ide != requested_preferred:
            raise IDEBridgeError(
                f"{method} requires {requested_preferred.upper()}, but this MCP server is locked to "
                f"{self._preferred_ide.upper()} by AIDB_PREFERRED_IDE."
            )
        preferred = self._resolve_preferred_ide(preferred_ide)

        async with self._lock:
            selected_ide = self._selected_ide
            selected_pipe = self._selected_pipe

        if preferred and selected_ide and selected_ide != preferred:
            raise IDEBridgeError(
                f"{method} requires {preferred.upper()}, but the selected IDE is {selected_ide.upper()}."
            )

        if selected_ide == "vs":
            try:
                if selected_pipe:
                    return await vs_bridge.call_pipe(selected_pipe, method, params, timeout)
                return await vs_bridge.call(method, params, timeout)
            except vs_bridge.VSBridgeError as e:
                if not self._is_stale_selected_instance_error(e):
                    raise IDEBridgeError(str(e))
                async with self._lock:
                    if self._selected_ide == "vs" and self._selected_pipe == selected_pipe:
                        self.clear_selection()
        elif selected_ide == "rider":
            try:
                if selected_pipe:
                    return await rider_bridge.call_pipe(selected_pipe, method, params, timeout)
                return await rider_bridge.call(method, params, timeout)
            except rider_bridge.RiderBridgeError as e:
                if not self._is_stale_selected_instance_error(e):
                    raise IDEBridgeError(str(e))
                async with self._lock:
                    if self._selected_ide == "rider" and self._selected_pipe == selected_pipe:
                        self.clear_selection()

        instances = await self.discover_instances(preferred_ide=preferred)
        if not instances:
            raise IDEBridgeError(
                "No IDE instances found. Please ensure the required IDE is running with the MCP plugin installed."
            )

        candidates, preferred = self.get_candidate_instances(instances, preferred_ide=preferred)
        selected = self._auto_select_core(candidates)
        if selected is None:
            instance_list = "\n".join([
                f"  - {i.ide.upper()} PID {i.process_id}: {i.solution_name} ({i.debug_mode})"
                for i in candidates
            ])
            scope = f"{preferred.upper()} " if preferred else ""
            raise IDEBridgeError(
                f"Multiple {scope}IDE instances found. Please use 'list_ide_instances' to see all instances "
                "and 'select_ide_instance' to choose one.\n\nAvailable instances:\n" + instance_list
            )

        async with self._lock:
            self.select_instance(selected.ide, process_id=selected.process_id, pipe_name=selected.pipe_name)

        try:
            if selected.ide == "vs":
                return await vs_bridge.call_pipe(selected.pipe_name, method, params, timeout)
            return await rider_bridge.call_pipe(selected.pipe_name, method, params, timeout)
        except (vs_bridge.VSBridgeError, rider_bridge.RiderBridgeError) as e:
            if self._is_stale_selected_instance_error(e):
                async with self._lock:
                    if self._selected_ide == selected.ide and self._selected_pipe == selected.pipe_name:
                        self.clear_selection()
            raise IDEBridgeError(str(e))

    def _auto_select_core(self, instances: List[IDEInstance]) -> Optional[IDEInstance]:
        instances = [i for i in instances if i.connection_status == "ok"]
        if not instances:
            return None

        if len(instances) == 1:
            return instances[0]

        paused = [i for i in instances if i.is_paused]
        if len(paused) == 1:
            return paused[0]

        debugging = [i for i in instances if i.is_debugging]
        if len(debugging) == 1:
            return debugging[0]

        return None

    def auto_select_instance(self, instances: List[IDEInstance], preferred_ide: Optional[str] = None) -> Optional[IDEInstance]:
        candidates, _ = self.get_candidate_instances(instances, preferred_ide=preferred_ide)
        return self._auto_select_core(candidates)

    def get_preferred_ide(self) -> Optional[str]:
        return self._preferred_ide


_bridge: Optional[IDEBridge] = None


def get_bridge() -> IDEBridge:
    global _bridge
    if _bridge is None:
        _bridge = IDEBridge()
    return _bridge


async def call(
    method: str,
    params: Optional[Dict[str, Any]] = None,
    timeout: float = 10.0,
    preferred_ide: Optional[str] = None,
) -> Any:
    bridge = get_bridge()
    return await bridge.call(method, params, timeout, preferred_ide=preferred_ide)


async def discover_instances(preferred_ide: Optional[str] = None) -> List[IDEInstance]:
    bridge = get_bridge()
    return await bridge.discover_instances(preferred_ide=preferred_ide)


def select_instance(ide: Optional[str] = None, process_id: Optional[int] = None, pipe_name: Optional[str] = None):
    bridge = get_bridge()
    bridge.select_instance(ide=ide, process_id=process_id, pipe_name=pipe_name)


def clear_selection():
    bridge = get_bridge()
    bridge.clear_selection()


async def is_any_available() -> bool:
    try:
        instances = await discover_instances()
        return any(i.connection_status == "ok" for i in instances)
    except Exception:
        return False
