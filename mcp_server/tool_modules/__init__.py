"""Register the AIDebugger Visual Studio MCP tool modules."""


def register_vs() -> None:
    # Import modules for their @mcp.tool registration side effects.
    from . import capability_tools  # noqa: F401
    from . import vs_instance_tools  # noqa: F401
    from . import multi_vs_tools  # noqa: F401
    from . import snapshot_tools  # noqa: F401
    from . import native_managed_tools  # noqa: F401
    from . import control_tools  # noqa: F401
    from . import breakpoint_tools  # noqa: F401
    from . import memory_tools  # noqa: F401
