"""FastMCP application instance shared by tool modules."""

import functools
import inspect
import os
from typing import Any, Dict

try:
    from . import bootstrap
except ImportError:
    import bootstrap

bootstrap.ensure_deps()

from mcp.server.fastmcp import FastMCP
try:
    from .output_models import output_model_for
except ImportError:
    from output_models import output_model_for


def _structured_result(value: Any) -> Dict[str, Any]:
    # The declared Dict[str, Any] return annotation produces a stable
    # {"result": object} output schema in FastMCP. Always match that envelope,
    # including when the backend value is already a dictionary.
    if isinstance(value, dict) and set(value) == {"result"}:
        return value
    payload = value if isinstance(value, dict) else {"value": value}
    return {"result": payload}


class AIDebuggerFastMCP(FastMCP):
    """FastMCP variant that gives every tool an explicit structured output schema."""

    def tool(self, *args, **kwargs):
        kwargs["structured_output"] = True
        register = super().tool(*args, **kwargs)

        def decorator(function):
            tool_name = kwargs.get("name") or function.__name__
            preferred = (os.environ.get("AIDB_PREFERRED_IDE") or "").strip().lower()
            expose_legacy = os.environ.get("AIDB_ENABLE_LEGACY_ALIASES") == "1"
            if preferred in {"vs", "visualstudio", "visual-studio", "visual_studio"} and not expose_legacy:
                try:
                    from .tool_registry import TOOLS
                except ImportError:
                    from tool_registry import TOOLS
                spec = TOOLS.get(tool_name)
                if spec is not None and spec.alias_for:
                    return function

            output_model = output_model_for(tool_name)
            if inspect.iscoroutinefunction(function):
                @functools.wraps(function)
                async def wrapped(*call_args, **call_kwargs):
                    return _structured_result(await function(*call_args, **call_kwargs))
            else:
                @functools.wraps(function)
                def wrapped(*call_args, **call_kwargs):
                    return _structured_result(function(*call_args, **call_kwargs))

            wrapped.__annotations__ = dict(getattr(function, "__annotations__", {}))
            wrapped.__annotations__["return"] = output_model
            wrapped.__signature__ = inspect.signature(function).replace(
                return_annotation=output_model
            )
            return register(wrapped)

        return decorator


mcp = AIDebuggerFastMCP("AIDebugger")
