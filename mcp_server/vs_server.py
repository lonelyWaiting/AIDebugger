"""Visual Studio-only AIDebugger MCP stdio entry point."""

import io
import os
import sys

os.environ["AIDB_PREFERRED_IDE"] = "vs"
os.environ["AIDB_SERVER_SCOPE"] = "vs"
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
    from .vs_mcp_app import mcp
    from .tool_modules import register_vs
except ImportError:
    from vs_mcp_app import mcp
    from tool_modules import register_vs

register_vs()

if __name__ == "__main__":
    mcp.run(transport="stdio")
