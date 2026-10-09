"""Select the MCP application and backend for the Visual Studio MCP server."""

try:
    from . import vs_session as backend
    from .vs_mcp_app import mcp
    from .vs_tool_registry import tool_registry_summary
except ImportError:
    import vs_session as backend
    from vs_mcp_app import mcp
    from vs_tool_registry import tool_registry_summary
