# Visual Studio Debugger MCP

A Model Context Protocol (MCP) server that enables Cursor IDE to access Visual Studio debugging information, including call stacks, local variables, threads, modules, memory inspection, and disassembly.

## Architecture

```
┌─────────────────┐
│  Visual Studio  │──┐
│   Instance 1    │  │
│   (PID: 1234)   │  │   Named Pipes
└─────────────────┘  │   (VSDebuggerMCP_<PID>)
                     │                          ┌─────────────────┐     stdio     ┌─────────────────┐
┌─────────────────┐  ├─────────────────────────►│   Python MCP    │◄────────────►│   Cursor IDE    │
│  Visual Studio  │──┤   JSON Protocol          │     Server      │    MCP       │                 │
│   Instance 2    │  │                          │  (Auto-select   │   Protocol   │                 │
│   (PID: 5678)   │  │                          │   or manual)    │              │                 │
└─────────────────┘  │                          └─────────────────┘              └─────────────────┘
        ...          │
┌─────────────────┐  │
│  Visual Studio  │──┘
│   Instance N    │
└─────────────────┘
```

### Multi-Instance Support

The extension supports multiple Visual Studio instances running simultaneously:

- **Automatic Selection**: If only one VS instance is in debugging/paused state, it will be automatically selected
- **Manual Selection**: Use `list_vs_instances` to see all instances and `select_vs_instance` to choose one
- **Smart Priority**: Paused instances (at breakpoint) have highest priority for auto-selection

## Components

### 1. Visual Studio Extension (VSDebuggerMCP)

A VSIX extension that runs inside Visual Studio and provides access to the debugger API via a Named Pipe server.

**Location:** `VSDebuggerMCP/`

**Features:**
- Automatic initialization on VS startup
- Named Pipe server for IPC communication
- Access to EnvDTE debugger APIs
- Support for all debugging operations
- **Auto-registration with Cursor MCP** - automatically adds itself to Cursor's `mcp.json` on first run

### 2. Python MCP Server

A Python-based MCP server that connects to the VS extension and exposes tools to Cursor.

**Location:** `mcp_server/`

**Files:**
- `server.py` - Main MCP server with tool definitions
- `vs_bridge.py` - Named Pipe client for VS communication
- `requirements.txt` - Python dependencies

## Available Tools

### Instance Management

| Tool | Description |
|------|-------------|
| `list_vs_instances` | List all VS instances with their status (PID, solution, debug state) |
| `select_vs_instance` | Select a specific VS instance by process ID |
| `clear_vs_selection` | Clear selection and return to auto-select mode |

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
| `read_memory` | Read raw memory at specified address |
| `get_disassembly` | Get disassembly at current location |
| `get_disassembly_at` | Get disassembly at specified address |
| `get_build_errors` | Get all build errors and warnings (with error codes like CS0001) |
| `get_errors_only` | Get only build errors |
| `get_warnings_only` | Get only build warnings |
| `get_full_context` | Get all debugging information at once |

### Debug Control

| Tool | Description |
|------|-------------|
| `debug_continue` | Continue execution (F5) |
| `debug_step_over` | Step over current line (F10) |
| `debug_step_into` | Step into function (F11) |
| `debug_step_out` | Step out of function (Shift+F11) |
| `debug_break` | Break/pause execution |
| `debug_stop` | Stop debugging session (Shift+F5) |
| `debug_run_to_cursor` | Run to specified file and line |
| `debug_set_next_statement` | Set next statement to execute (move IP) |

### Breakpoint Management

| Tool | Description |
|------|-------------|
| `get_breakpoints` | Get all breakpoints with conditions and hit counts |
| `set_breakpoint` | Set breakpoint at file:line (supports conditions) |
| `set_function_breakpoint` | Set breakpoint on function entry |
| `delete_breakpoint` | Delete breakpoint at file:line |
| `delete_all_breakpoints` | Delete all breakpoints |
| `enable_breakpoint` | Enable or disable a breakpoint |

### Navigation

| Tool | Description |
|------|-------------|
| `switch_stack_frame` | Switch to different stack frame |
| `switch_thread` | Switch to different thread |

### Advanced

| Tool | Description |
|------|-------------|
| `get_registers` | Get CPU registers (RAX, RBX, RIP, etc.) |
| `add_watch` | Add expression to watch window |
| `search_memory` | Search for pattern in memory |

## Installation

### Prerequisites

- Visual Studio 2022 (17.0+)
- Python 3.8+
- Cursor IDE

### Step 1: Build and Install VS Extension

1. Open `VSDebuggerMCP/VSDebuggerMCP.slnx` in Visual Studio
2. Build the solution in Release mode
3. Install the generated VSIX from `VSDebuggerMCP/bin/Release/VSDebuggerMCP.vsix`
4. Restart Visual Studio

### Step 2: Install Python Dependencies

```bash
cd mcp_server
pip install -r requirements.txt
```

### Step 3: Configure Cursor (Automatic!)

The VS extension will **automatically register** itself with Cursor when it first starts. It modifies the Cursor MCP configuration file (`~/.cursor/mcp.json`) to add the vs-debugger server.

After installing the VS extension and restarting Visual Studio, you just need to **restart Cursor** for the MCP tools to become available.

**Manual Configuration (Optional):**

If auto-registration doesn't work, you can manually add the following to your Cursor MCP configuration (`~/.cursor/mcp.json`):

```json
{
  "mcpServers": {
    "vs-debugger": {
      "command": "python",
      "args": ["d:/CursorMCP/mcp_server/server.py"]
    }
  }
}
```

**Note:** Adjust the path according to your installation location.

## Usage

### Single VS Instance (Simple Case)

1. Start Visual Studio with the extension installed
2. Open your project and start debugging (F5)
3. When the debugger hits a breakpoint, use Cursor to query debugging information

### Multiple VS Instances

When you have multiple VS instances running:

1. **Automatic Selection** (most common case):
   - If only one VS is paused at a breakpoint, it will be automatically selected
   - Just ask your debugging question directly

2. **Manual Selection** (when needed):
   - Use `list_vs_instances` to see all available instances
   - Use `select_vs_instance` with the process ID to select one
   - All subsequent queries will use that instance

### Example Queries in Cursor

**Basic Debugging:**
- "What's the current call stack?"
- "Show me the local variables"
- "What exception was thrown?"
- "Read 256 bytes of memory at &myVariable"
- "Get the disassembly of the current function"

**Multi-Instance:**
- "List all VS instances" → shows all instances with their status
- "Select VS instance 12345" → selects the instance with PID 12345
- "分析当前调用栈" → analyzes call stack of the selected (or auto-selected) instance

## Troubleshooting

### "Cannot connect to Visual Studio"

1. Ensure Visual Studio is running
2. Check that the VSDebuggerMCP extension is installed (Extensions > Manage Extensions)
3. Look for "VSDebuggerMCP" in the VS Output window (View > Output, select "VSDebuggerMCP" from dropdown)
4. Try restarting Visual Studio

### "Not in break mode"

Most debugging tools require the debugger to be paused at a breakpoint. Start debugging and hit a breakpoint first.

### Named Pipe Issues

Each VS instance creates a unique named pipe: `\\.\pipe\VSDebuggerMCP_<PID>` (e.g., `VSDebuggerMCP_12345`).

If you have connection issues:
1. Check if pipes exist using PowerShell: `Get-ChildItem \\.\pipe\ | Where-Object { $_.Name -like "VSDebuggerMCP_*" }`
2. Ensure no firewall or security software is blocking named pipes
3. Use `list_vs_instances` in Cursor to see which VS instances are detected

## Development

### Building the VS Extension

```bash
cd VSDebuggerMCP
msbuild /p:Configuration=Release
```

### Running the MCP Server Directly

```bash
cd mcp_server
python server.py
```

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
