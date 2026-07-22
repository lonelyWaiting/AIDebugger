"""Typed errors shared by Visual Studio discovery, trust, and transport layers."""

from typing import Any, Dict, Optional


class VSBridgeError(Exception):
    """Error communicating with or validating a Visual Studio extension."""

    def __init__(
        self,
        message: str,
        code: str = "BRIDGE_ERROR",
        outcome_unknown: bool = False,
        details: Optional[Dict[str, Any]] = None,
        target_identity: Optional[Dict[str, Any]] = None,
    ):
        super().__init__(message)
        self.code = code
        self.outcome_unknown = outcome_unknown
        self.request_id: Optional[str] = None
        self.details = details
        self.target_identity = target_identity
        self.original_target_identity = target_identity
        self.current_target_identity = target_identity


class VSRemoteError(VSBridgeError):
    """A definitive error response returned by the authenticated VS server."""
