"""Register all AIDebugger MCP tool modules."""


def register_all() -> None:
    # Import modules for their @mcp.tool / @mcp.prompt registration side effects.
    from . import capability_tools  # noqa: F401
    from . import instance_tools  # noqa: F401
    from . import snapshot_tools  # noqa: F401
    from . import native_managed_tools  # noqa: F401
    from . import control_tools  # noqa: F401
    from . import breakpoint_tools  # noqa: F401
    from . import memory_tools  # noqa: F401


def register_vs() -> None:
    """Register the VS tool surface; combined-only decorators remain unregistered."""
    from . import capability_tools  # noqa: F401
    from . import vs_instance_tools  # noqa: F401
    from . import snapshot_tools  # noqa: F401
    from . import native_managed_tools  # noqa: F401
    from . import control_tools  # noqa: F401
    from . import breakpoint_tools  # noqa: F401
    from . import memory_tools  # noqa: F401
