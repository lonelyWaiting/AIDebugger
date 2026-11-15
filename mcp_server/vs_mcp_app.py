"""FastMCP application for the dedicated Visual Studio server."""

import functools
import inspect
import json
import os
from typing import Annotated, Any, Dict, Optional

try:
    from . import bootstrap
except ImportError:
    import bootstrap

bootstrap.ensure_deps()

from mcp.server.fastmcp import FastMCP
from mcp.server.fastmcp.exceptions import ToolError
from mcp.types import ToolAnnotations
from pydantic import Field, ValidationError

try:
    from .output_models import output_model_for
    from .vs_tool_registry import LEGACY_ALIASES, VS_TOOL_DOMAINS, tool_annotation_values
    from . import vs_bridge
except ImportError:
    from output_models import output_model_for
    from vs_tool_registry import LEGACY_ALIASES, VS_TOOL_DOMAINS, tool_annotation_values
    import vs_bridge


_INSTANCE_ROUTABLE_TOOLS = {
    tool
    for domain in ("snapshot", "native_managed", "control", "breakpoint", "memory")
    for tool in VS_TOOL_DOMAINS[domain]
} - {"get_command_status", "cancel_debug_request", "get_dispatcher_health"}

VSProcessId = Annotated[int, Field(
    strict=True, ge=1, le=0x7FFFFFFF,
    description="Visual Studio instance PID from list_vs_instances. Routes this call only.",
)]


def _structured_result(value: Any) -> Dict[str, Any]:
    if isinstance(value, dict) and set(value) == {"result"}:
        return value
    return {"result": value if isinstance(value, dict) else {"value": value}}


class VisualStudioFastMCP(FastMCP):
    def tool(self, *args, **kwargs):
        kwargs["structured_output"] = True

        def decorator(function):
            tool_name = kwargs.get("name") or function.__name__
            if os.environ.get("AIDB_ENABLE_LEGACY_ALIASES") != "1" and tool_name in LEGACY_ALIASES:
                return function
            output_model = output_model_for(tool_name)
            route_by_instance = tool_name in _INSTANCE_ROUTABLE_TOOLS

            def validate_result(value: Any) -> Dict[str, Any]:
                structured = _structured_result(value)
                try:
                    return output_model.model_validate(structured).model_dump(mode="python")
                except ValidationError as error:
                    receipt = vs_bridge.get_last_command_receipt()
                    validation_errors = error.errors(include_url=False, include_context=False, include_input=False)[:16]
                    if receipt:
                        payload = {
                            "code": "COMMAND_COMPLETED_RESULT_INVALID",
                            "message": "Visual Studio completed the command, but its result did not match the MCP output contract. Do not repeat the command; query get_command_status with requestId.",
                            "operation": tool_name,
                            "retryable": False,
                            "doNotRetry": True,
                            "commandCompleted": True,
                            "outcomeUnknown": False,
                            "requestId": receipt.get("requestId"),
                            "targetIdentity": receipt.get("targetIdentity"),
                            "validationErrors": validation_errors,
                        }
                    else:
                        payload = {
                            "code": "RESULT_VALIDATION_FAILED",
                            "message": "The debugger result did not match the MCP output contract.",
                            "operation": tool_name,
                            "retryable": False,
                            "outcomeUnknown": False,
                            "validationErrors": validation_errors,
                        }
                    raise ToolError(json.dumps(payload, ensure_ascii=False, separators=(",", ":"))) from error

            if inspect.iscoroutinefunction(function):
                @functools.wraps(function)
                async def wrapped(*call_args, vs_process_id=None, **call_kwargs):
                    vs_bridge.begin_tool_invocation()
                    if vs_process_id is None:
                        return validate_result(await function(*call_args, **call_kwargs))
                    try:
                        target = await vs_bridge.resolve_tool_vs_target(vs_process_id)
                    except vs_bridge.VSBridgeError as error:
                        # Load lazily because the tool runtime imports this module.
                        if __package__:
                            from .tool_context import handle_ide_error
                        else:
                            from tool_context import handle_ide_error
                        return handle_ide_error(error, tool_name)
                    with vs_bridge.tool_vs_target(target):
                        return validate_result(await function(*call_args, **call_kwargs))
            else:
                @functools.wraps(function)
                def wrapped(*call_args, **call_kwargs):
                    vs_bridge.begin_tool_invocation()
                    return validate_result(function(*call_args, **call_kwargs))
            wrapped.__annotations__ = dict(getattr(function, "__annotations__", {}))
            wrapped.__annotations__["return"] = output_model
            signature = inspect.signature(function)
            parameters = list(signature.parameters.values())
            if route_by_instance:
                parameter = inspect.Parameter(
                    "vs_process_id", inspect.Parameter.KEYWORD_ONLY,
                    default=None, annotation=Optional[VSProcessId],
                )
                parameters.append(parameter)
                wrapped.__annotations__[parameter.name] = parameter.annotation
            wrapped.__signature__ = signature.replace(parameters=parameters, return_annotation=output_model)
            registration_kwargs = dict(kwargs)
            registration_kwargs.setdefault(
                "annotations",
                ToolAnnotations(**tool_annotation_values(tool_name)),
            )
            register = super(VisualStudioFastMCP, self).tool(*args, **registration_kwargs)
            return register(wrapped)
        return decorator


mcp = VisualStudioFastMCP("AIDebugger Visual Studio")
