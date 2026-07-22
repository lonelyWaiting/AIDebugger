"""Select the MCP application and backend for the current server scope."""

import os

if os.environ.get("AIDB_SERVER_SCOPE") == "vs":
    try:
        from . import vs_session as backend
        from .vs_mcp_app import mcp
        from .vs_tool_registry import tool_registry_summary
    except ImportError:
        import vs_session as backend
        from vs_mcp_app import mcp
        from vs_tool_registry import tool_registry_summary
else:
    try:
        from . import ide_bridge as backend
        from .mcp_app import mcp
        from .tool_registry import tool_registry_summary
    except ImportError:
        import ide_bridge as backend
        from mcp_app import mcp
        from tool_registry import tool_registry_summary
