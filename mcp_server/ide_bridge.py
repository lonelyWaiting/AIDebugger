"""
Unified IDE bridge for VS and Rider.
"""

import asyncio
import os
from typing import Any, Dict, List, Optional, Tuple

_configured_ide = (os.environ.get("AIDB_PREFERRED_IDE") or "").strip().lower()
vs_bridge = None
rider_bridge = None
try:
    if _configured_ide != "rider":
        from . import vs_bridge
    if _configured_ide != "vs":
        from . import rider_bridge
except ImportError:
    if _configured_ide != "rider":
        import vs_bridge
    if _configured_ide != "vs":
        import rider_bridge

_VS_BRIDGE_ERRORS = (vs_bridge.VSBridgeError,) if vs_bridge is not None else ()
_RIDER_BRIDGE_ERRORS = (rider_bridge.RiderBridgeError,) if rider_bridge is not None else ()
_TRANSPORT_ERRORS = _VS_BRIDGE_ERRORS + _RIDER_BRIDGE_ERRORS


class IDEBridgeError(Exception):
    """Error communicating with IDE extensions."""

    def __init__(
        self,
        message: str,
        code: str = "IDE_BRIDGE_ERROR",
        outcome_unknown: bool = False,
        request_id: Optional[str] = None,
        details: Optional[Dict[str, Any]] = None,
        target_identity: Optional[Dict[str, Any]] = None,
    ):
        super().__init__(message)
        self.code = code
        self.outcome_unknown = outcome_unknown
        self.request_id = request_id
        self.details = details
        self.target_identity = target_identity


def _typed_bridge_error(error: Exception) -> IDEBridgeError:
    return IDEBridgeError(
        str(error),
        code=getattr(error, "code", "IDE_BRIDGE_ERROR"),
        outcome_unknown=bool(getattr(error, "outcome_unknown", False)),
        request_id=getattr(error, "request_id", None),
        details=getattr(error, "details", None),
        target_identity=getattr(error, "target_identity", None),
    )


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
        self.allowed_capabilities = list(getattr(instance, "allowed_capabilities", []) or [])
        self.access_mode = getattr(instance, "access_mode", "unknown")
        self.process_start_time_utc_ticks = getattr(instance, "process_start_time_utc_ticks", None)
        self.server_instance_id = getattr(instance, "server_instance_id", None)
        snapshot_getter = getattr(instance, "registration_snapshot", None)
        self._registration_snapshot = (
            snapshot_getter()
            if ide == "vs" and callable(snapshot_getter) and self.connection_status == "ok"
            else None
        )

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
            "allowedCapabilities": self.allowed_capabilities,
            "accessMode": self.access_mode,
            "processStartTimeUtcTicks": self.process_start_time_utc_ticks,
            "serverInstanceId": self.server_instance_id,
        }

    def target_identity(self) -> Dict[str, Any]:
        if self.ide != "vs" or not self.process_start_time_utc_ticks or not self.server_instance_id:
            raise IDEBridgeError("Selected instance has no authenticated immutable identity", code="INVALID_TARGET")
        return {
            "pipeName": self.pipe_name,
            "processId": self.process_id,
            "processStartTimeUtcTicks": self.process_start_time_utc_ticks,
            "serverInstanceId": self.server_instance_id,
        }

    def registration_snapshot(self) -> Dict[str, Any]:
        if self.ide != "vs" or self._registration_snapshot is None:
            raise IDEBridgeError(
                "Selected instance has no authenticated registration snapshot",
                code="INVALID_TARGET",
                target_identity=self.target_identity() if self.ide == "vs" else None,
            )
        snapshot = dict(self._registration_snapshot)
        snapshot["capabilities"] = list(snapshot.get("capabilities") or [])
        return snapshot

    def __repr__(self) -> str:
        status = "Paused" if self.is_paused else ("Debugging" if self.is_debugging else "Idle")
        return f"IDEInstance(ide={self.ide}, pid={self.process_id}, solution='{self.solution_name}', status={status})"


def _registry_preferred_ide_for_method(method: str) -> Optional[str]:
    try:
        from .tool_registry import preferred_ide_for_backend_method
    except ImportError:
        from tool_registry import preferred_ide_for_backend_method

    return preferred_ide_for_backend_method(method)


def _registry_backend_method_kind(method: str) -> str:
    try:
        from .tool_registry import backend_method_kind
    except ImportError:
        from tool_registry import backend_method_kind

    return backend_method_kind(method)


class IDEBridge:
    def __init__(self):
        self._lock = asyncio.Lock()
        self._selected_ide: Optional[str] = None
        self._selected_pipe: Optional[str] = None
        self._selected_target: Optional[Dict[str, Any]] = None
        self._selected_registration: Optional[Dict[str, Any]] = None
        self._selection_generation = 0
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

    async def discover_instances(
        self,
        preferred_ide: Optional[str] = None,
        timeout: Optional[float] = None,
    ) -> List[IDEInstance]:
        preferred = self._resolve_preferred_ide(preferred_ide)
        result: List[IDEInstance] = []
        tasks = []
        labels = []

        if preferred in (None, "vs") and vs_bridge is not None:
            tasks.append(vs_bridge.discover_instances(timeout=timeout) if timeout is not None else vs_bridge.discover_instances())
            labels.append("vs")
        if preferred in (None, "rider") and rider_bridge is not None:
            tasks.append(rider_bridge.discover_instances())
            labels.append("rider")

        gathered = asyncio.gather(*tasks, return_exceptions=True)
        try:
            outcomes = await asyncio.wait_for(gathered, timeout=max(0.1, float(timeout))) if timeout is not None else await gathered
        except asyncio.TimeoutError as error:
            raise IDEBridgeError(
                "IDE discovery exhausted its timeout budget",
                code="DEADLINE_EXCEEDED_NOT_STARTED",
            ) from error
        errors = []
        for label, outcome in zip(labels, outcomes):
            if isinstance(outcome, Exception):
                errors.append(f"{label}: {outcome}")
                continue
            result.extend([IDEInstance(label, inst) for inst in outcome])

        if not result and errors:
            raise IDEBridgeError("IDE discovery failed: " + "; ".join(errors))
        return result

    def select_instance(
        self,
        ide: Optional[str] = None,
        process_id: Optional[int] = None,
        pipe_name: Optional[str] = None,
        target_identity: Optional[Dict[str, Any]] = None,
        registration_snapshot: Optional[Dict[str, Any]] = None,
    ):
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

        if ide == "vs":
            if vs_bridge is None:
                raise IDEBridgeError("This MCP server was started without the Visual Studio backend")
            try:
                target_identity = vs_bridge.select_instance(
                    pipe_name=pipe_name,
                    process_id=process_id,
                    target_identity=target_identity,
                    registration_snapshot=registration_snapshot,
                )
            except _VS_BRIDGE_ERRORS as error:
                raise _typed_bridge_error(error)
            if rider_bridge is not None:
                rider_bridge.clear_selection()
            registration_snapshot = vs_bridge.get_selected_registration_snapshot()
        elif ide == "rider":
            if rider_bridge is None:
                raise IDEBridgeError("This MCP server was started without the Rider backend")
            rider_bridge.select_instance(pipe_name=pipe_name, process_id=process_id)
            target_identity = None
            registration_snapshot = None
            if vs_bridge is not None:
                vs_bridge.clear_selection()
        else:
            target_identity = None
            registration_snapshot = None
            if vs_bridge is not None:
                vs_bridge.clear_selection()
            if rider_bridge is not None:
                rider_bridge.clear_selection()

        self._selected_ide = ide
        self._selected_pipe = pipe_name
        self._selected_target = dict(target_identity) if target_identity is not None else None
        self._selected_registration = (
            vs_bridge._copy_registration_snapshot(registration_snapshot)
            if ide == "vs" and registration_snapshot is not None
            else (dict(registration_snapshot) if registration_snapshot is not None else None)
        )
        if self._selected_registration is not None:
            self._selected_registration["capabilities"] = list(
                self._selected_registration.get("capabilities") or []
            )
        self._selection_generation += 1

    def clear_selection(self):
        self._selected_ide = None
        self._selected_pipe = None
        self._selected_target = None
        self._selected_registration = None
        if vs_bridge is not None:
            vs_bridge.clear_selection()
        if rider_bridge is not None:
            rider_bridge.clear_selection()
        self._selection_generation += 1

    def _is_stale_selected_instance_error(self, error: Exception) -> bool:
        if isinstance(error, _VS_BRIDGE_ERRORS):
            return getattr(error, "code", "") in {
                "INSTANCE_REGISTRATION_NOT_FOUND",
                "PIPE_BUSY",
                "PIPE_DISCONNECTED",
                "PIPE_NOT_FOUND",
                "PIPE_TIMEOUT",
                "PROTOCOL_MISMATCH",
                "RESPONSE_ID_MISMATCH",
                "STALE_TARGET",
                "TRANSPORT_ERROR",
            }

        # Rider still uses legacy string-only transport errors. This fallback is
        # intentionally unreachable from the VS-only entrypoint.
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

    def _selected_target_failure(self, method: str, error: Exception) -> IDEBridgeError:
        outcome_unknown = getattr(error, "outcome_unknown", None)
        if _registry_backend_method_kind(method) == "command" and outcome_unknown is not False:
            return IDEBridgeError(
                f"OUTCOME_UNKNOWN: '{method}' may have been accepted by the selected IDE before "
                f"the transport failed. The command was not retried. Refresh IDE instances and "
                f"debug status before deciding whether to issue another command. Original error: {error}",
                code="OUTCOME_UNKNOWN",
                outcome_unknown=True,
                request_id=getattr(error, "request_id", None),
                target_identity=getattr(error, "target_identity", None),
            )

        return IDEBridgeError(
            f"STALE_TARGET: the selected IDE became unavailable while executing '{method}'. "
            f"The request was not redirected to another IDE. Refresh IDE instances and select a "
            f"target explicitly. Original error: {error}",
            code="STALE_TARGET",
            target_identity=getattr(error, "target_identity", None),
        )

    def _unknown_command_outcome(self, method: str, error: Exception) -> IDEBridgeError:
        request_id = getattr(error, "request_id", None)
        status_hint = (
            f" Use get_command_status(request_id='{request_id}') to query the server-side result."
            if request_id
            else ""
        )
        return IDEBridgeError(
            f"OUTCOME_UNKNOWN: '{method}' may have been accepted by the selected IDE before "
            f"the response was lost. The command was not retried. Refresh debug status before "
            f"deciding whether to issue another command.{status_hint} Original error: {error}",
            code="OUTCOME_UNKNOWN",
            outcome_unknown=True,
            request_id=request_id,
            target_identity=getattr(error, "target_identity", None),
        )

    async def _call_vs_exact_and_advance(
        self,
        target: Dict[str, Any],
        method: str,
        params: Optional[Dict[str, Any]],
        timeout: float,
        registration: Optional[Dict[str, Any]],
        selection_generation: int,
        include_target: bool,
    ) -> Any:
        try:
            result, response_target = await vs_bridge.call_pipe_exact(
                target,
                method,
                params,
                timeout,
                include_target=True,
                registration_snapshot=registration,
            )
        except vs_bridge.VSBridgeError as error:
            response_target = getattr(error, "current_target_identity", None)
            if isinstance(response_target, dict):
                await self._advance_selected_vs_target(
                    target, response_target, selection_generation
                )
            raise
        await self._advance_selected_vs_target(
            target, response_target, selection_generation
        )
        return (result, response_target) if include_target else result

    async def _advance_selected_vs_target(
        self,
        target: Dict[str, Any],
        response_target: Dict[str, Any],
        selection_generation: int,
    ) -> None:
        immutable_keys = (
            "pipeName", "processId", "processStartTimeUtcTicks", "serverInstanceId",
        )
        if all(target.get(key) == response_target.get(key) for key in immutable_keys):
            async with self._lock:
                current = self._selected_target
                if (
                    self._selection_generation == selection_generation
                    and current is not None
                    and all(current.get(key) == target.get(key) for key in immutable_keys)
                ):
                    current_epoch = tuple(int(current.get(key) or 0) for key in (
                        "debugSessionGeneration", "breakSequence", "contextEpoch",
                    ))
                    response_epoch = tuple(int(response_target.get(key) or 0) for key in (
                        "debugSessionGeneration", "breakSequence", "contextEpoch",
                    ))
                    if response_epoch >= current_epoch:
                        self._selected_target = dict(response_target)

    async def call(
        self,
        method: str,
        params: Optional[Dict[str, Any]] = None,
        timeout: float = 10.0,
        preferred_ide: Optional[str] = None,
        _include_target: bool = False,
    ) -> Any:
        loop = asyncio.get_running_loop()
        deadline = loop.time() + max(0.1, float(timeout))
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
            selected_target = dict(self._selected_target) if self._selected_target is not None else None
            selected_registration = (
                dict(self._selected_registration)
                if self._selected_registration is not None
                else None
            )
            if selected_registration is not None:
                selected_registration["capabilities"] = list(
                    selected_registration.get("capabilities") or []
                )
            selection_generation = self._selection_generation

        remaining = deadline - loop.time()
        if selected_ide and remaining <= 0.1:
            raise IDEBridgeError(
                "Request was not started because its timeout budget was exhausted before dispatch",
                code="DEADLINE_EXCEEDED_NOT_STARTED",
                target_identity=selected_target,
            )

        if preferred and selected_ide and selected_ide != preferred:
            raise IDEBridgeError(
                f"{method} requires {preferred.upper()}, but the selected IDE is {selected_ide.upper()}."
            )

        if selected_ide == "vs":
            try:
                if selected_target:
                    return await self._call_vs_exact_and_advance(
                        selected_target,
                        method,
                        params,
                        remaining,
                        selected_registration,
                        selection_generation,
                        _include_target,
                    )
                if selected_pipe:
                    raise IDEBridgeError(
                        "Pinned Visual Studio selection has no immutable instance identity",
                        code="INVALID_TARGET",
                    )
                if _include_target:
                    raise IDEBridgeError("A pinned Visual Studio pipe is required for composite calls")
                return await vs_bridge.call(method, params, remaining)
            except _VS_BRIDGE_ERRORS as e:
                if getattr(e, "outcome_unknown", False) and _registry_backend_method_kind(method) == "command":
                    raise self._unknown_command_outcome(method, e)
                if not self._is_stale_selected_instance_error(e):
                    raise _typed_bridge_error(e)
                raise self._selected_target_failure(method, e)
        elif selected_ide == "rider":
            if _include_target:
                raise IDEBridgeError("Pinned composite calls are supported only for Visual Studio")
            try:
                if selected_pipe:
                    return await rider_bridge.call_pipe(selected_pipe, method, params, remaining)
                return await rider_bridge.call(method, params, remaining)
            except _RIDER_BRIDGE_ERRORS as e:
                if not self._is_stale_selected_instance_error(e):
                    raise _typed_bridge_error(e)
                raise self._selected_target_failure(method, e)

        discovery_budget = deadline - loop.time()
        if discovery_budget <= 0.1:
            raise IDEBridgeError(
                "Request was not started because IDE discovery exhausted its timeout budget",
                code="DEADLINE_EXCEEDED_NOT_STARTED",
            )
        instances = await self.discover_instances(preferred_ide=preferred, timeout=discovery_budget)
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

        remaining = deadline - loop.time()
        if remaining <= 0.1:
            raise IDEBridgeError(
                "Request was not started because IDE discovery exhausted its timeout budget",
                code="DEADLINE_EXCEEDED_NOT_STARTED",
            )
        call_ide = selected.ide
        call_pipe = selected.pipe_name
        call_target = selected.target_identity() if selected.ide == "vs" else None
        call_registration = selected.registration_snapshot() if selected.ide == "vs" else None
        call_selection_generation = selection_generation
        async with self._lock:
            if self._selection_generation == selection_generation and self._selected_ide is None:
                self.select_instance(
                    selected.ide,
                    process_id=selected.process_id,
                    pipe_name=selected.pipe_name,
                    target_identity=call_target,
                    registration_snapshot=call_registration,
                )
            elif self._selected_ide is not None:
                call_ide = self._selected_ide
                call_pipe = self._selected_pipe
                call_target = (
                    dict(self._selected_target)
                    if self._selected_target is not None
                    else None
                )
                call_registration = (
                    dict(self._selected_registration)
                    if self._selected_registration is not None
                    else None
                )
                if call_registration is not None:
                    call_registration["capabilities"] = list(
                        call_registration.get("capabilities") or []
                    )
            call_selection_generation = self._selection_generation

        try:
            remaining = deadline - loop.time()
            if remaining <= 0.1:
                raise IDEBridgeError(
                    "Request was not started because IDE discovery exhausted its timeout budget",
                    code="DEADLINE_EXCEEDED_NOT_STARTED",
                )
            if preferred and call_ide != preferred:
                raise IDEBridgeError(
                    f"{method} requires {preferred.upper()}, but the concurrently selected IDE is "
                    f"{str(call_ide).upper()}."
                )
            if call_ide == "vs":
                if call_target is None:
                    raise IDEBridgeError(
                        "Pinned Visual Studio selection has no immutable instance identity",
                        code="INVALID_TARGET",
                    )
                return await self._call_vs_exact_and_advance(
                    call_target,
                    method,
                    params,
                    remaining,
                    call_registration,
                    call_selection_generation,
                    _include_target,
                )
            if _include_target:
                raise IDEBridgeError("Pinned composite calls are supported only for Visual Studio")
            return await rider_bridge.call_pipe(call_pipe, method, params, remaining)
        except _TRANSPORT_ERRORS as e:
            if getattr(e, "outcome_unknown", False) and _registry_backend_method_kind(method) == "command":
                raise self._unknown_command_outcome(method, e)
            if self._is_stale_selected_instance_error(e):
                raise self._selected_target_failure(method, e)
            raise _typed_bridge_error(e)

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

    async def get_selected_target(self) -> Tuple[Optional[str], Optional[str]]:
        selected_ide, selected_pipe, _, _ = await self.get_selection_snapshot()
        return selected_ide, selected_pipe

    async def get_selected_target_identity(self) -> Optional[Dict[str, Any]]:
        _, _, selected_target, _ = await self.get_selection_snapshot()
        return selected_target

    async def get_selection_snapshot(
        self,
    ) -> Tuple[Optional[str], Optional[str], Optional[Dict[str, Any]], int]:
        async with self._lock:
            return (
                self._selected_ide,
                self._selected_pipe,
                dict(self._selected_target) if self._selected_target is not None else None,
                self._selection_generation,
            )


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


async def call_with_target(
    method: str,
    params: Optional[Dict[str, Any]] = None,
    timeout: float = 10.0,
    preferred_ide: Optional[str] = "vs",
) -> Tuple[Any, Dict[str, Any]]:
    """Call VS and return the immutable instance/debug context that produced the result."""
    bridge = get_bridge()
    return await bridge.call(
        method,
        params,
        timeout,
        preferred_ide=preferred_ide,
        _include_target=True,
    )


async def discover_instances(
    preferred_ide: Optional[str] = None,
    timeout: Optional[float] = None,
) -> List[IDEInstance]:
    bridge = get_bridge()
    return await bridge.discover_instances(preferred_ide=preferred_ide, timeout=timeout)


def select_instance(
    ide: Optional[str] = None,
    process_id: Optional[int] = None,
    pipe_name: Optional[str] = None,
    target_identity: Optional[Dict[str, Any]] = None,
    registration_snapshot: Optional[Dict[str, Any]] = None,
):
    bridge = get_bridge()
    bridge.select_instance(
        ide=ide,
        process_id=process_id,
        pipe_name=pipe_name,
        target_identity=target_identity,
        registration_snapshot=registration_snapshot,
    )


def clear_selection():
    bridge = get_bridge()
    bridge.clear_selection()


async def is_any_available() -> bool:
    try:
        instances = await discover_instances()
        return any(i.connection_status == "ok" for i in instances)
    except Exception:
        return False
