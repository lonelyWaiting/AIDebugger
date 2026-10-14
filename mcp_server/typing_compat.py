"""Python 3.10 compatibility for vendored MCP TypedDict markers."""

from __future__ import annotations

import sys
import typing


def ensure_pep655_typing_markers() -> None:
    """Make typing.NotRequired/Required importable before ida-pro-mcp loads.

    Vendored zeromcp imports these from typing, which only gained them in 3.11.
    Inject the typing_extensions backports when needed so IDA/kernel entrypoints
    keep working on the documented 3.10 runtime.
    """
    if hasattr(typing, "NotRequired") and hasattr(typing, "Required"):
        return
    try:
        from typing_extensions import NotRequired, Required
    except ImportError:
        from mcp_server import bootstrap

        bootstrap.ensure_deps()
        from typing_extensions import NotRequired, Required
    typing.NotRequired = NotRequired  # type: ignore[attr-defined, misc]
    typing.Required = Required  # type: ignore[attr-defined, misc]


# Apply on import for callers that only need the side effect.
if sys.version_info < (3, 11):
    ensure_pep655_typing_markers()
