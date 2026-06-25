"""
Unified IDE bridge for VS and Rider.
"""

import asyncio
import os
from typing import Any, Dict, List, Optional, Tuple

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
        }

    def __repr__(self) -> str:
        status = "Paused" if self.is_paused else ("Debugging" if self.is_debugging else "Idle")
        return f"IDEInstance(ide={self.ide}, pid={self.process_id}, solution='{self.solution_name}', status={status})"


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

    async def discover_instances(self) -> List[IDEInstance]:
        vs_instances = await vs_bridge.discover_instances()
        rider_instances = await rider_bridge.discover_instances()
        result: List[IDEInstance] = []
        result.extend([IDEInstance("vs", inst) for inst in vs_instances])
        result.extend([IDEInstance("rider", inst) for inst in rider_instances])
        return result

    def select_instance(self, ide: Optional[str] = None, process_id: Optional[int] = None, pipe_name: Optional[str] = None):
        if ide not in (None, "vs", "rider"):
            raise IDEBridgeError("ide must be 'vs' or 'rider'")
        if self._preferred_ide and ide is not None and ide != self._preferred_ide:
            raise IDEBridgeError(
                f"Preferred IDE '{self._preferred_ide}' is locked for this MCP server. "
                f"Cannot select '{ide}'."
            )
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
        stale_markers = (
            "not found",
            "broken pipe",
            "no data received from pipe",
            "empty response received from pipe",
            "error: 109",
        )
        return any(marker in message for marker in stale_markers)

    async def call(self, method: str, params: Optional[Dict[str, Any]] = None, timeout: float = 10.0) -> Any:
        async with self._lock:
            if self._selected_ide == "vs":
                try:
                    return await vs_bridge.call(method, params, timeout)
                except vs_bridge.VSBridgeError as e:
                    if not self._is_stale_selected_instance_error(e):
                        raise IDEBridgeError(str(e))
                    self.clear_selection()
            elif self._selected_ide == "rider":
                try:
                    return await rider_bridge.call(method, params, timeout)
                except rider_bridge.RiderBridgeError as e:
                    if not self._is_stale_selected_instance_error(e):
                        raise IDEBridgeError(str(e))
                    self.clear_selection()

            instances = await self.discover_instances()
            if not instances:
                raise IDEBridgeError(
                    "No IDE instances found. Please ensure VS or Rider is running with the MCP plugin installed."
                )

            candidates, preferred = self.get_candidate_instances(instances)
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

            self.select_instance(selected.ide, process_id=selected.process_id, pipe_name=selected.pipe_name)
            try:
                if selected.ide == "vs":
                    return await vs_bridge.call(method, params, timeout)
                return await rider_bridge.call(method, params, timeout)
            except (vs_bridge.VSBridgeError, rider_bridge.RiderBridgeError) as e:
                raise IDEBridgeError(str(e))

    def _auto_select_core(self, instances: List[IDEInstance]) -> Optional[IDEInstance]:
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


async def call(method: str, params: Optional[Dict[str, Any]] = None, timeout: float = 10.0) -> Any:
    bridge = get_bridge()
    return await bridge.call(method, params, timeout)


async def discover_instances() -> List[IDEInstance]:
    bridge = get_bridge()
    return await bridge.discover_instances()


def select_instance(ide: Optional[str] = None, process_id: Optional[int] = None, pipe_name: Optional[str] = None):
    bridge = get_bridge()
    bridge.select_instance(ide=ide, process_id=process_id, pipe_name=pipe_name)


def clear_selection():
    bridge = get_bridge()
    bridge.clear_selection()


async def is_any_available() -> bool:
    try:
        instances = await discover_instances()
        return len(instances) > 0
    except Exception:
        return False
