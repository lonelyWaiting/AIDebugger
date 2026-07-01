"""FastMCP application instance shared by tool modules."""

try:
    from . import bootstrap
except ImportError:
    import bootstrap

bootstrap.ensure_deps()

from mcp.server.fastmcp import FastMCP


mcp = FastMCP("AIDebugger")
