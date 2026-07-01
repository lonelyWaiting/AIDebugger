"""
IDE Debugger MCP Server

This MCP server provides tools for Cursor to access debugging information
from Visual Studio and Rider, including call stacks, variables, memory, and more.

Supports multiple IDE instances with automatic discovery and smart selection:
- If only one instance is in debugging/paused state, it will be auto-selected
- Use list_ide_instances to see all available instances
- Use select_ide_instance to choose a specific instance
"""

import io
import os
import sys

# Force UTF-8 for stdio before anything else.
# On Windows the default encoding may be cp1252 which cannot encode CJK characters
# used in tool descriptions, causing UnicodeEncodeError when MCP writes responses.
os.environ.setdefault("PYTHONIOENCODING", "utf-8")
if hasattr(sys.stdout, "buffer"):
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
if hasattr(sys.stderr, "buffer"):
    sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding="utf-8", errors="replace")

try:
    from . import bootstrap
except ImportError:
    import bootstrap

bootstrap.ensure_deps()

try:
    from .mcp_app import mcp
    from .tool_modules import register_all
except ImportError:
    from mcp_app import mcp
    from tool_modules import register_all

register_all()


# ============== Main Entry Point ==============

if __name__ == "__main__":
    # Run the server using stdio transport
    mcp.run(transport="stdio")
