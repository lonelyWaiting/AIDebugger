"""Register the common discovery and inspection tools on an independent MCP server."""


def register_debug_tools(mcp, platform, translate, default_view):
    @mcp.tool
    @translate
    def debug_capabilities(domain: str = "") -> dict:
        """Discover this platform's inspection views and tool domains. Pass a domain such as inspection to get exact direct tool names. Use those tools for precise filters, paging or platform-specific actions."""
        return platform.capabilities(domain)

    @mcp.tool
    @translate
    def debug_inspect(target: str, query: str, view: str = default_view) -> dict:
        """Run a common read-only inspection. Use debug_capabilities for target format, views, queries, and more detailed tools. If the result contains jobId, poll the platform's job status tool from the session domain."""
        return platform.inspect(target, query, view)
