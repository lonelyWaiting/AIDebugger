from typing import Any, Dict, Optional


class RiderBridgeError(Exception):
    """Typed error raised by the authenticated Rider transport."""

    def __init__(
        self,
        message: str,
        *,
        code: str = "TRANSPORT_ERROR",
        outcome_unknown: bool = False,
        request_id: Optional[str] = None,
        details: Optional[Dict[str, Any]] = None,
        target_identity: Optional[Dict[str, Any]] = None,
    ) -> None:
        super().__init__(message)
        self.code = code
        self.outcome_unknown = outcome_unknown
        self.request_id = request_id
        self.details = dict(details) if details else None
        self.target_identity = dict(target_identity) if target_identity else None
