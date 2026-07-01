# AIDebugger MCP

AIDebugger exposes Visual Studio and JetBrains Rider debugger state through a
Model Context Protocol (MCP) server. It is intended for MCP clients such as
Cursor, Codex, Claude Desktop, Claude Code, and OpenCode.

It supports call stacks, locals, threads, modules, exceptions, build errors,
debug output, expression evaluation, memory reads/search, disassembly, breakpoints,
execution control, and Visual Studio-only no-source native/managed workflows.

## Architecture

```
Visual Studio VSIX  -- named pipe JSON-RPC --+
                                             +--> Python FastMCP server -- stdio --> MCP client
Rider plugin       -- named pipe JSON-RPC --+
```

Each IDE instance owns a named pipe:

- Visual Studio: `\\.\pipe\AIDebuggerVS_<PID>`
- Rider: `\\.\pipe\AIDebuggerRider_<PID>`

The Python server can discover multiple instances, prefer paused/debugging
sessions, and pin later calls to a selected IDE instance.

## Components

### Visual Studio Extension

Location: `VSDebuggerMCP/`

Important files:

- `VSDebuggerMCPPackage.cs` - package startup and MCP client registration.
- `DebuggerService.cs` - debugger operations implemented on top of EnvDTE and VS APIs.
- `NamedPipeServer.cs` - JSON-RPC request dispatch over named pipes.
- `McpClientRegistration.cs` - seeds the Python MCP server and registers MCP clients.
- `Protocol/DebugModels.cs` - response models shared with the bridge contract tests.

Visual Studio has the richest backend. VS-only tools include attach/start,
detach, data/address breakpoints, native address/module inspection, managed IL
inspection, and hybrid no-source capture flows.

### Rider Plugin

Location: `RiderDebuggerMCP/`

Important files:

- `RiderDebuggerPlugin.kt` - plugin startup and MCP client registration.
- `DebuggerService.kt` - Rider debugger operations.
- `NamedPipeServer.kt` - JSON-RPC request dispatch over named pipes.
- `McpClientRegistration.kt` - seeds the Python MCP server and registers MCP clients.
- `protocol/DebugModels.kt` - response models aligned with the VS contracts.

Rider supports the common snapshot, stepping, breakpoint, memory, disassembly,
and stop flows. Rider detach and the VS-only no-source/native-managed tools are
not exposed as supported Rider backends.

### Python MCP Server

Location: `mcp_server/`

Important files:

- `server.py` - thin stdio entry point; sets UTF-8, runs bootstrap, registers tool modules.
- `mcp_app.py` - shared FastMCP application instance.
- `tool_modules/` - domain modules for capability, instance, snapshot, control,
  breakpoint, memory, and native/managed tools.
- `tool_registry.py` - declarative tool/backend matrix, aliases, timeout classes,
  supported backends, and registry validation.
- `tool_context.py` - shared formatting, error handling, timeout, and request helpers.
- `ide_bridge.py` - backend selection and preferred-IDE routing.
- `vs_bridge.py` / `rider_bridge.py` - named pipe clients.
- `bootstrap.py` - extracts bundled wheels to a content-addressed
  `~/.ai-debugger/pydeps/...` directory.
- `vendor/wheels/` - bundled Python wheels; runtime does not require `pip install`.

`debug_capabilities` returns the current categorized tool index and embeds the
registry summary. Use it as the authoritative discovery entry point instead of
assuming a fixed flat tool list.

## Tool Domains

The server currently exposes 77 MCP tools. The registry in
`mcp_server/tool_registry.py` is the source of truth for names, aliases, backend
support, and timeout classes.

### Capability and Instance Selection

- `debug_capabilities`
- `detect_debug_sessions`
- `list_ide_instances`, `select_ide_instance`, `clear_ide_selection`
- Legacy VS-scoped aliases: `list_vs_instances`, `select_vs_instance`, `clear_vs_selection`

### Snapshot and Inspection

- `get_debug_status`
- `get_callstack`, `get_call_stack`
- `get_locals`
- `evaluate_expression`, `evaluate_expressions_batch`
- `get_threads`, `list_threads`
- `get_modules`, `get_exception`
- `get_build_errors`, `get_errors_only`, `get_warnings_only`
- `get_debug_output`, `search_output`
- `get_full_context`
- `capture_debug_bundle`
- `crash_triage`
- `step_and_capture`

`capture_debug_bundle()` is low-blocking by default: it captures status,
callstack, exception, and budgeted threads. Locals, modules, registers, and
output are explicit opt-ins. Expression tools attempt the requested expression
and return the IDE's explicit value/error/timeout result.

### Execution Control

- `attach_process` (Visual Studio only)
- `start_debugging` (Visual Studio only)
- `debug_control`
- `debug_continue`, `continue`
- `continue_and_wait_for_break`
- `continue_and_capture_native` (Visual Studio only)
- `debug_step_over`, `step_over`
- `debug_step_into`, `step_into`
- `debug_step_out`, `step_out`
- `debug_break`, `pause`
- `debug_stop`, `stop_debugging`
- `detach_process`, `detach_debugger` (Visual Studio only)
- `debug_run_to_cursor`
- `debug_set_next_statement`

For Rider, use stop tools to terminate the current debugging session; detach is
not advertised as supported.

### Breakpoints and Navigation

- `get_breakpoints`
- `get_breakpoint_bindings` (Visual Studio only)
- `continue_and_capture_breakpoint` (Visual Studio only)
- `set_breakpoint`
- `set_function_breakpoint`
- `set_data_breakpoint`, `set_watchpoint` (Visual Studio only)
- `set_address_breakpoint` (Visual Studio only)
- `delete_breakpoint`, `clear_breakpoint`
- `delete_all_breakpoints`
- `enable_breakpoint`
- `switch_stack_frame`, `select_stack_frame`
- `switch_thread`, `select_thread`

Non-breaking Visual Studio tracepoint/logpoint actions are not exposed because
EnvDTE does not provide a reliable public action/message API for them.

### Memory, Disassembly, Registers, Watches

- `read_memory`
- `search_memory`
- `get_disassembly`
- `get_disassembly_at`
- `get_registers`
- `add_watch`

No write-memory or patching tool is exposed.

### Visual Studio No-Source Workflows

- Native: `resolve_address`, `get_module_layout`, `capture_native_context`,
  `continue_and_capture_native`, `set_address_breakpoint`
- Managed: `inspect_managed_assembly`, `inspect_managed_type`,
  `inspect_managed_method_il`
- Hybrid: `capture_hybrid_context`, `continue_and_capture_breakpoint`,
  `get_breakpoint_bindings`

These tools are Visual Studio only in the current implementation.

## Installation

### Prerequisites

- Windows 10/11 for the IDE debugger integrations.
- Visual Studio 2022 17.0+ or Visual Studio 2026 18.0+.
- JetBrains Rider for the Rider plugin.
- JDK 21+ for Rider plugin builds.
- Python 3.10-3.13 x64 for the MCP server.
- One or more MCP clients: Cursor, Codex, Claude Desktop, Claude Code, or OpenCode.

### Build Visual Studio VSIX

```powershell
nuget restore VSDebuggerMCP/VSDebuggerMCP.csproj
msbuild VSDebuggerMCP/VSDebuggerMCP.csproj /p:Configuration=Release /p:DeployExtension=false
```

Outputs:

- `VSDebuggerMCP/bin/Release/VSDebuggerMCP.VS2022.vsix`
- `VSDebuggerMCP/bin/Release/VSDebuggerMCP.VS2026.vsix`

The intermediate `VSDebuggerMCP.vsix` is deleted at the end of the build to
avoid installing the wrong target package.

Install examples:

```powershell
VSDebuggerMCP/Install-VSIX.cmd -Target vs2022
VSDebuggerMCP/Install-VSIX.cmd -Target vs2026
VSDebuggerMCP/Install-VSIX.cmd -Target all
```

The install script also seeds MCP server files to
`<user-home>/.ai-debugger/mcp_server`.

### Build Rider Plugin

```powershell
cd RiderDebuggerMCP
./gradlew.bat buildPlugin
```

Output:

- `RiderDebuggerMCP/build/distributions/*.zip`

Set `RIDER_HOME` to a local Rider installation if you need to build against that
installation. Otherwise Gradle uses the platform version configured in
`RiderDebuggerMCP/gradle.properties`.

### Automatic MCP Client Registration

On IDE startup, each extension registers its own strict MCP entry:

- Visual Studio: `AIDebuggerVS` with `AIDB_PREFERRED_IDE=vs`
- Rider: `AIDebuggerRider` with `AIDB_PREFERRED_IDE=rider`

Supported registration targets:

- Cursor: `~/.cursor/mcp.json`
- Codex: `$CODEX_HOME/config.toml` or `~/.codex/config.toml`
- Claude Desktop: `%APPDATA%/Claude/claude_desktop_config.json`
- Claude Code: `~/.claude.json`
- OpenCode: `~/.config/opencode/opencode.json`

Restart the MCP client after installing or updating the IDE extension.

### Manual MCP Configuration

If automatic registration fails, point the client at the seeded server:

```json
{
  "mcpServers": {
    "AIDebuggerVS": {
      "command": "py",
      "args": ["-3.13", "<user-home>/.ai-debugger/mcp_server/server.py"],
      "env": { "AIDB_PREFERRED_IDE": "vs" }
    },
    "AIDebuggerRider": {
      "command": "python",
      "args": ["<user-home>/.ai-debugger/mcp_server/server.py"],
      "env": { "AIDB_PREFERRED_IDE": "rider" }
    }
  }
}
```

Use an installed Python 3.10-3.13 interpreter. Runtime dependencies are bundled;
the server extracts wheels on first run.

## Usage

1. Start Visual Studio or Rider with the AIDebugger extension installed.
2. Start or attach to a debug session.
3. Ask the MCP client to call `detect_debug_sessions`.
4. Use `debug_capabilities` when unsure which tool is appropriate.
5. For repro loops, use `continue_and_wait_for_break` or the more focused
   `continue_and_capture_*` tools.
6. For first-pass crash/bug diagnosis, prefer `capture_debug_bundle()` in its
   default low-blocking mode and opt in to heavier data only when needed.

When multiple IDE instances are running, use `list_ide_instances` and
`select_ide_instance` to pin the target. Without a manual selection, the bridge
prefers paused/debugging instances.

## Development

Run the MCP server directly:

```powershell
cd mcp_server
python server.py
```

For development-only dependency installation:

```powershell
cd mcp_server
pip install -r requirements.txt
```

Contract tests:

```powershell
python -m unittest discover -s tests -p "test_*.py"
```

Named pipe smoke test:

```powershell
cd mcp_server
python -c "import asyncio; import vs_bridge; print(asyncio.run(vs_bridge.is_vs_available()))"
```

GitHub Actions builds intentionally disable NuGet, Gradle, and Rider SDK cache
writing to avoid consuming the repository cache quota.

## Troubleshooting

### Cannot connect to IDE

1. Confirm Visual Studio or Rider is running.
2. Confirm the matching AIDebugger extension/plugin is installed.
3. Check the AIDebugger output/logs in the IDE.
4. Use `list_ide_instances` or `detect_debug_sessions` from the MCP client.
5. Restart the IDE if the named pipe is stale.

### Not in break mode

Most inspection tools require the debugger to be paused. Start debugging and hit
a breakpoint, break manually, or use `continue_and_wait_for_break`.

### Preferred IDE not found

The MCP entry is strict. `AIDebuggerVS` only routes to Visual Studio and
`AIDebuggerRider` only routes to Rider. Start the matching IDE or use the other
MCP entry.

### Check named pipes

```powershell
Get-ChildItem \\.\pipe\ | Where-Object { $_.Name -like "AIDebuggerVS_*" }
Get-ChildItem \\.\pipe\ | Where-Object { $_.Name -like "AIDebuggerRider_*" }
```

## Protocol

Named pipe requests use JSON-RPC style messages:

```json
{"id":"request-id","method":"get_callstack","params":{}}
```

Successful response:

```json
{"id":"request-id","result":{}}
```

Error response:

```json
{"id":"request-id","error":{"code":"ERROR_CODE","message":"Error description"}}
```

## License

MIT License
