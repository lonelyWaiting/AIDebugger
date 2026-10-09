"""Visual Studio-only session manager used by the dedicated VS MCP server.

This module deliberately has no IDE discriminator or cross-backend selection
state.  It presents the small compatibility surface consumed by the shared tool
implementations while delegating directly to VSBridge.
"""

from typing import Any, Dict, List, Optional, Tuple

try:
    from . import vs_bridge
except ImportError:
    import vs_bridge

IDEBridgeError = vs_bridge.VSBridgeError
IDEInstance = vs_bridge.VSInstance
VSInstance = vs_bridge.VSInstance


class VSSessionManager:
    def __init__(self, bridge: Optional[vs_bridge.VSBridge] = None):
        self._bridge = bridge or vs_bridge.get_bridge()

    async def discover_instances(
        self,
        preferred_ide: Optional[str] = None,
        timeout: Optional[float] = None,
    ) -> List[VSInstance]:
        self._validate_preference(preferred_ide)
        return await self._bridge.discover_instances(
            timeout=vs_bridge.DEFAULT_TIMEOUT if timeout is None else timeout
        )

    def select_instance(
        self,
        ide: Optional[str] = None,
        process_id: Optional[int] = None,
        pipe_name: Optional[str] = None,
        target_identity: Optional[Dict[str, Any]] = None,
        registration_snapshot: Optional[Dict[str, Any]] = None,
    ) -> Optional[Dict[str, Any]]:
        self._validate_preference(ide)
        return self._bridge.select_instance(
            pipe_name=pipe_name,
            process_id=process_id,
            target_identity=target_identity,
            registration_snapshot=registration_snapshot,
        )

    def clear_selection(self) -> None:
        self._bridge.clear_selection()

    async def try_select_instance(
        self,
        expected_generation: int,
        pipe_name: str,
        target_identity: Dict[str, Any],
        registration_snapshot: Dict[str, Any],
    ):
        return await self._bridge.try_select_instance(
            expected_generation,
            pipe_name=pipe_name,
            target_identity=target_identity,
            registration_snapshot=registration_snapshot,
        )

    async def try_clear_selection(self, expected_generation: int):
        return await self._bridge.try_clear_selection(expected_generation)

    async def call(
        self,
        method: str,
        params: Optional[Dict[str, Any]] = None,
        timeout: float = 10.0,
        preferred_ide: Optional[str] = None,
        _include_target: bool = False,
    ) -> Any:
        self._validate_preference(preferred_ide)
        return await self._bridge.call(
            method,
            params,
            timeout,
            include_target=_include_target,
        )

    async def get_selection_snapshot(
        self,
    ) -> Tuple[Optional[str], Optional[str], Optional[Dict[str, Any]], int]:
        return await self._bridge.get_selection_snapshot()

    def get_candidate_instances(
        self,
        instances: List[VSInstance],
        preferred_ide: Optional[str] = None,
    ) -> Tuple[List[VSInstance], str]:
        return self._bridge.get_candidate_instances(instances, preferred_ide)

    def auto_select_instance(
        self,
        instances: List[VSInstance],
        preferred_ide: Optional[str] = None,
    ) -> Optional[VSInstance]:
        return self._bridge.auto_select_instance(instances, preferred_ide)

    @staticmethod
    def get_preferred_ide() -> str:
        return "vs"

    @staticmethod
    def _validate_preference(preferred_ide: Optional[str]) -> None:
        if preferred_ide is None:
            return
        normalized = str(preferred_ide).strip().lower()
        if normalized not in ("vs", "visualstudio", "visual-studio", "visual_studio"):
            raise IDEBridgeError(
                "This MCP server supports Visual Studio only.",
                code="INVALID_ARGUMENT",
            )


_session: Optional[VSSessionManager] = None


def get_bridge() -> VSSessionManager:
    global _session
    if _session is None:
        _session = VSSessionManager()
    return _session


async def call(
    method: str,
    params: Optional[Dict[str, Any]] = None,
    timeout: float = 10.0,
    preferred_ide: Optional[str] = None,
) -> Any:
    return await get_bridge().call(method, params, timeout, preferred_ide)


async def call_with_target(
    method: str,
    params: Optional[Dict[str, Any]] = None,
    timeout: float = 10.0,
    preferred_ide: Optional[str] = "vs",
) -> Tuple[Any, Dict[str, Any]]:
    return await get_bridge().call(
        method,
        params,
        timeout,
        preferred_ide,
        _include_target=True,
    )


async def discover_instances(
    preferred_ide: Optional[str] = None,
    timeout: Optional[float] = None,
) -> List[VSInstance]:
    return await get_bridge().discover_instances(preferred_ide, timeout)


def select_instance(
    ide: Optional[str] = None,
    process_id: Optional[int] = None,
    pipe_name: Optional[str] = None,
    target_identity: Optional[Dict[str, Any]] = None,
    registration_snapshot: Optional[Dict[str, Any]] = None,
) -> Optional[Dict[str, Any]]:
    return get_bridge().select_instance(
        ide,
        process_id,
        pipe_name,
        target_identity,
        registration_snapshot,
    )


def clear_selection() -> None:
    get_bridge().clear_selection()


async def try_select_instance(
    expected_generation: int,
    pipe_name: str,
    target_identity: Dict[str, Any],
    registration_snapshot: Dict[str, Any],
):
    return await get_bridge().try_select_instance(
        expected_generation,
        pipe_name,
        target_identity,
        registration_snapshot,
    )


async def try_clear_selection(expected_generation: int):
    return await get_bridge().try_clear_selection(expected_generation)
