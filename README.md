# AIDebugger MCP

A Model Context Protocol (MCP) server that enables Cursor IDE to access Visual Studio and Rider debugging information, including call stacks, local variables, threads, modules, memory inspection, and disassembly.

## Architecture

```
Visual Studio (AIDebuggerVS) -- named pipe --> Python MCP server (AIDebugger) -- stdio --> Cursor
Rider (AIDebuggerRider) -----^
```


### Multi-Instance Support

The system supports multiple IDE instances (Visual Studio + Rider) running simultaneously:

- **Automatic Selection**: If only one instance is in debugging/paused state, it will be automatically selected
- **Strict IDE Binding**: `AIDebuggerVS` only connects to Visual Studio; `AIDebuggerRider` only connects to Rider
- **Proactive Detection**: Use `detect_debug_sessions` to auto-detect and select a session
- **Manual Selection**: Use `list_ide_instances` and `select_ide_instance` to choose one
- **Smart Priority**: Paused instances (at breakpoint) have highest priority for auto-selection

## Components

### 1. Visual Studio Extension (AIDebugger VS)

A VSIX extension that runs inside Visual Studio and provides access to the debugger API via a Named Pipe server.

**Location:** `VSDebuggerMCP/`

**Features:**
- Automatic initialization on VS startup
- Named Pipe server for IPC communication
- Pipe security allows admin IDE with non-admin Cursor
- Access to EnvDTE debugger APIs
- Support for all debugging operations
- **Auto-registration with Cursor MCP** - registers `AIDebuggerVS` in `mcp.json` on first run

### 2. Rider Plugin (AIDebugger Rider)

A Rider plugin that runs inside JetBrains Rider and provides access to the debugger API via a Named Pipe server.

**Location:** `RiderDebuggerMCP/`

**Features:**
- Automatic initialization on Rider startup
- Named Pipe server for IPC communication
- Pipe security allows admin IDE with non-admin Cursor
- Access to Rider debugger APIs
- Auto-registration with Cursor MCP (`AIDebuggerRider`)

### 3. Python MCP Server

A Python-based MCP server that connects to the VS extension and Rider plugin and exposes tools to Cursor.

**Location:** `mcp_server/`

**Files:**
- `server.py` - Main MCP server with tool definitions
- `vs_bridge.py` - Named Pipe client for VS communication
- `rider_bridge.py` - Named Pipe client for Rider communication
- `ide_bridge.py` - Unified bridge with auto-selection logic
- `bootstrap.py` - Loads bundled Python dependencies on startup
- `vendor/wheels/` - Bundled Python wheels (no pip required)
- `vendor/wheels/manifest.json` - Wheel manifest (files + hashes)
- `requirements.txt` - Python dependencies (dev only)

## Available Tools

The MCP keeps the low-level tools exposed for client compatibility, but AI agents should use `debug_capabilities` as the progressive-discovery entry point when they are unsure which tool to call. This avoids relying on one large flat tool list while still keeping direct calls available for precise workflows.

### Instance Management

| Tool | Description |
|------|-------------|
| `debug_capabilities` | Categorized tool index for progressive discovery |
| `detect_debug_sessions` | Detect available VS/Rider debug sessions and auto-select one |
| `list_ide_instances` | List all IDE instances (VS + Rider) with their status |
| `select_ide_instance` | Select a specific IDE instance by IDE + process ID |
| `clear_ide_selection` | Clear selection and return to auto-select mode |
| `list_vs_instances` | List all VS instances with their status (legacy) |
| `select_vs_instance` | Select a specific VS instance by process ID (legacy) |
| `clear_vs_selection` | Clear VS selection and return to auto-select mode (legacy) |

### Debug Information

| Tool | Description |
|------|-------------|
| `get_debug_status` | Get current debugging state (running, paused, etc.) |
| `get_callstack` | Get complete call stack with file paths and line numbers |
| `get_locals` | Get local variables and parameters in current scope |
| `get_threads` | Get all threads with their states and locations |
| `get_modules` | Get loaded modules/assemblies |
| `get_exception` | Get current exception details |
| `evaluate_expression` | Evaluate arbitrary expressions in debug context |
| `evaluate_expressions_batch` | Evaluate multiple expressions in one snapshot to reduce drift |
| `read_memory` | Read raw memory at specified address |
| `get_disassembly` | Get disassembly at current location |
| `get_disassembly_at` | Get disassembly at specified address |
| `get_build_errors` | Get all build errors and warnings (with error codes like CS0001) |
| `get_errors_only` | Get only build errors |
| `get_warnings_only` | Get only build warnings |
| `get_full_context` | Get all debugging information at once |
| `capture_debug_bundle` | Capture a stable debug bundle with snapshot metadata for AI diagnosis |

`get_debug_status` and `capture_debug_bundle` include standardized `stopReason` values (`not_paused`, `breakpoint`, `exception`, `step`, `user_break`, `pause_unknown`) so AI prompts can branch on a stable schema.

### Debug Control

| Tool | Description |
|------|-------------|
| `attach_process` | Attach Visual Studio to a process by PID or executable name |
| `start_debugging` | Start the current VS debug profile, or launch an exe and attach |
| `debug_continue` | Continue execution (F5) |
| `continue` | Alias for `debug_continue` |
| `continue_and_wait_for_break` | Continue or wait for the next debug stop, then auto-capture a debug bundle |
| `debug_step_over` | Step over current line (F10) |
| `step_over` | Alias for `debug_step_over` |
| `debug_step_into` | Step into function (F11) |
| `step_into` | Alias for `debug_step_into` |
| `debug_step_out` | Step out of function (Shift+F11) |
| `step_out` | Alias for `debug_step_out` |
| `debug_break` | Break/pause execution |
| `pause` | Alias for `debug_break` |
| `debug_stop` | Stop debugging session (Shift+F5) |
| `debug_run_to_cursor` | Run to specified file and line |
| `debug_set_next_statement` | Set next statement to execute (move IP) |

### Breakpoint Management

| Tool | Description |
|------|-------------|
| `get_breakpoints` | Get all breakpoints with conditions and hit counts |
| `set_breakpoint` | Set breakpoint at file:line (supports conditions and hit counts) |
| `set_function_breakpoint` | Set breakpoint on function entry (supports conditions and hit counts) |
| `set_data_breakpoint` | Set a Visual Studio native data breakpoint/watchpoint where supported |
| `set_watchpoint` | Alias for `set_data_breakpoint` |
| `delete_breakpoint` | Delete breakpoint at file:line |
| `clear_breakpoint` | Alias for `delete_breakpoint` |
| `delete_all_breakpoints` | Delete all breakpoints |
| `enable_breakpoint` | Enable or disable a breakpoint |

Non-breaking Visual Studio tracepoint/logpoint actions are not exposed as MCP tools because EnvDTE has no reliable public action/message API for them.

### Navigation

| Tool | Description |
|------|-------------|
| `switch_stack_frame` | Switch to different stack frame |
| `select_stack_frame` | Alias for `switch_stack_frame` |
| `switch_thread` | Switch to different thread |
| `select_thread` | Alias for `switch_thread` |
| `get_call_stack` | Alias for `get_callstack` |
| `list_threads` | Alias for `get_threads` |

### Advanced

| Tool | Description |
|------|-------------|
| `get_registers` | Get CPU registers (RAX, RBX, RIP, etc.) |
| `add_watch` | Add expression to watch window |
| `search_memory` | Search for pattern in memory |

## Installation

### Prerequisites

- Windows 10/11 (debugger integrations are Windows-only)
- Visual Studio 2026 (18.0+) recommended (Visual Studio 2022 17.0+ also works)
- JDK 21+ (for Rider plugin build)
- Python 3.10-3.13 (x64)
- Cursor IDE

### Step 1: Build and Install VS Extension

1. Open `AIDebugger.sln` in Visual Studio
2. Build the solution in Release mode (builds the VSIX and triggers the Rider plugin build via `RiderBuild.csproj`)
   - The first build will download Gradle via the wrapper in `RiderDebuggerMCP/`
3. Install the generated VSIX package for your Visual Studio version:
   - `VSDebuggerMCP/bin/Release/VSDebuggerMCP.VS2022.vsix` (targets VS 2022 only)
   - `VSDebuggerMCP/bin/Release/VSDebuggerMCP.VS2026.vsix` (targets VS 2026 only)
   - The generic `VSDebuggerMCP.vsix` is removed after build to avoid accidentally installing the wrong package.
   - The VS 2026 package does not target VS 2022, so the installer will not prompt to install it into VS 2022.
   - Optional scripted install (instance-aware):
     - `VSDebuggerMCP/Install-VSIX.cmd -Target vs2022`
     - `VSDebuggerMCP/Install-VSIX.cmd -Target vs2026`
     - `VSDebuggerMCP/Install-VSIX.cmd -Target all`
     - This script also seeds MCP server files to `<user-home>/.ai-debugger/mcp_server`.
4. The Rider plugin ZIP is created in `RiderDebuggerMCP/build-<id>/distributions/` when building via Visual Studio.
   - When building directly with Gradle, the ZIP is created in `RiderDebuggerMCP/build/distributions/`
   - Visual Studio builds keep only the most recent Rider build folders by default. Adjust `RiderBuildRetention` in `RiderBuild.csproj` to change the retention count.
5. Restart Visual Studio

### Step 2: Configure MCP Clients (Automatic!)

Each extension **registers its own MCP server** on first start:

- VS extension: `AIDebuggerVS` (strictly Visual Studio)
- Rider plugin: `AIDebuggerRider` (strictly Rider)

The Python MCP server files and dependencies are **bundled with each extension**, so no manual pip install is required.
On first run, the MCP server extracts bundled wheels to `<user-home>/.ai-debugger/pydeps`.

Automatic registration targets:

- Cursor: `~/.cursor/mcp.json`
- Claude Desktop: `%APPDATA%/Claude/claude_desktop_config.json`
- Claude Code (including the Claude Code extension used inside Cursor/VS Code): `~/.claude.json`

After installing the VS extension and restarting Visual Studio, restart the MCP client you want to use so the new server list is picked up.

The VS install script and Rider plugin both copy MCP server files to `<user-home>/.ai-debugger/mcp_server` when needed.

**Manual Configuration (Optional):**

If auto-registration doesn't work, you can manually add the following to your Cursor MCP configuration (`~/.cursor/mcp.json`):

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

**Note:** Replace `<user-home>` with your home directory. If your default `python` points to Python 3.14+, use `py -3.13` (or another 3.10-3.13 interpreter).
If you only use one IDE, you can omit the other MCP entry.

## Usage

### Single IDE Instance (Simple Case)

1. Start Visual Studio or Rider with the extension installed
2. Open your project and start debugging (F5)
3. When the debugger hits a breakpoint, use Cursor to query debugging information
4. Or use `continue_and_wait_for_break` to let the program run, reproduce the issue, and auto-capture a bundle on the next stop

### Multiple IDE Instances (VS + Rider)

When you have multiple IDE instances running:

1. **Pick the matching MCP**:
   - `AIDebuggerVS` only connects to Visual Studio
   - `AIDebuggerRider` only connects to Rider

2. **Automatic Selection** (most common case):
   - If only one instance is paused at a breakpoint, it will be automatically selected
   - Just ask your debugging question directly

3. **Manual Selection** (when needed):
   - Use `detect_debug_sessions` to auto-detect and select a session
   - Or use `list_ide_instances` to see all available instances
   - Use `select_ide_instance` with the IDE + process ID to select one
   - All subsequent queries will use that instance

### Example Queries in Cursor

**Basic Debugging:**
- "What's the current call stack?"
- "Show me the local variables"
- "What exception was thrown?"
- "Read 256 bytes of memory at &myVariable"
- "Get the disassembly of the current function"

**Multi-Instance:**
- "Detect debug sessions" -> calls `detect_debug_sessions` and auto-selects a paused/debugging instance
- "List all IDE instances" -> shows all instances with their status
- "Select VS instance 12345" -> selects the VS instance with PID 12345
- "Select Rider instance 56789" -> selects the Rider instance with PID 56789
- "Analyze current call stack" -> analyzes call stack of the selected (or auto-selected) instance

## Troubleshooting

### "Cannot connect to IDE"

1. Ensure Visual Studio or Rider is running
2. Check that the AIDebugger extensions are installed (VS: Extensions > Manage Extensions, Rider: Settings > Plugins)
3. Look for "AIDebugger" logs in VS Output window or Rider logs
4. Try restarting the IDE

### "Not in break mode"

Most debugging tools require the debugger to be paused at a breakpoint. Start debugging and hit a breakpoint first.

### "Preferred IDE not found"

This means the MCP entry does not match the running IDE. For example, `AIDebuggerVS` is running but only Rider is open.
Start the correct IDE or use the matching MCP entry in Cursor.

### Named Pipe Issues

Each VS/Rider instance creates a unique named pipe:

- VS: `\\.\pipe\AIDebuggerVS_<PID>` (e.g., `AIDebuggerVS_12345`)
- Rider: `\\.\pipe\AIDebuggerRider_<PID>` (e.g., `AIDebuggerRider_56789`)

If you have connection issues:
1. Check if pipes exist using PowerShell:
   - VS: `Get-ChildItem \\.\pipe\ | Where-Object { $_.Name -like "AIDebuggerVS_*" }`
   - Rider: `Get-ChildItem \\.\pipe\ | Where-Object { $_.Name -like "AIDebuggerRider_*" }`
2. Ensure no firewall or security software is blocking named pipes
3. Use `list_ide_instances` in Cursor to see which IDE instances are detected

## Development

### Building the VS Extension

```bash
cd VSDebuggerMCP
msbuild /p:Configuration=Release
```

To build the Rider plugin against a local Rider installation, set `RIDER_HOME` to the Rider install directory before running Gradle.
Otherwise the build will download the Rider platform defined by `riderPlatformVersion` in `RiderDebuggerMCP/gradle.properties`.

### Running the MCP Server Directly

```bash
cd mcp_server
python server.py
```

The server will extract bundled dependencies to `<user-home>/.ai-debugger/pydeps` on first run.

### Testing the Named Pipe

You can test the named pipe connection using the included test script:

```bash
cd mcp_server
python -c "import asyncio; import vs_bridge; print(asyncio.run(vs_bridge.is_vs_available()))"
```

## Protocol

### Named Pipe Protocol

The VS extension and Python server communicate using JSON-RPC style messages over a named pipe:

**Request:**
```json
{
  "id": "unique-request-id",
  "method": "get_callstack",
  "params": {}
}
```

**Response:**
```json
{
  "id": "unique-request-id",
  "result": { ... }
}
```

**Error:**
```json
{
  "id": "unique-request-id",
  "error": {
    "code": "ERROR_CODE",
    "message": "Error description"
  }
}
```

## License

MIT License
