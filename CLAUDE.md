# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

AIDebugger MCP is a Model Context Protocol server that enables MCP clients to access Visual Studio debugging information. The architecture is:

```
Visual Studio (VSIX) ── authenticated AIDB/2 named pipe ──> Python MCP Server ── stdio ──> MCP client
```

## Build Commands

### Full Solution Build
```bash
# Open AIDebugger.sln in Visual Studio and build Release
# This builds the VSIX.
msbuild AIDebugger.sln /p:Configuration=Release
```

### VS Extension Only
```bash
nuget restore VSDebuggerMCP/VSDebuggerMCP.csproj
msbuild VSDebuggerMCP/VSDebuggerMCP.csproj /p:Configuration=Release /p:DeployExtension=false
```
Output:
- `VSDebuggerMCP/bin/Release/VSDebuggerMCP.VS2022.vsix`
- `VSDebuggerMCP/bin/Release/VSDebuggerMCP.VS2026.vsix`

Install examples:
```bash
VSDebuggerMCP/Install-VSIX.cmd -Target vs2022
VSDebuggerMCP/Install-VSIX.cmd -Target vs2026
VSDebuggerMCP/Install-VSIX.cmd -Target all
```

Note: `VSDebuggerMCP/bin/Release/VSDebuggerMCP.vsix` is an intermediate package and is deleted at the end of the build.
The install script also seeds MCP server files to `~/.ai-debugger/mcp_server`.

### Run MCP Server Directly (Development)
```bash
cd mcp_server
pip install -r requirements.txt  # dev only; extensions bundle wheels
python vs_server.py
```

## Testing

### Contract Tests
```bash
python -m unittest discover -s tests -p "test_*.py"
```

These tests validate protocol field alignment between the Visual Studio extension and the Python bridge, plus bridge timeout semantics. They are static assertions (no running IDE required).

### Test Named Pipe Connection
```bash
cd mcp_server
python -c "import asyncio; import vs_bridge; print(asyncio.run(vs_bridge.is_vs_available()))"
```

## Architecture

### Component Structure

| Component | Location | Language | Purpose |
|-----------|----------|----------|---------|
| VS Extension | `VSDebuggerMCP/` | C# (.NET 4.7.2) | VSIX package exposing EnvDTE debugger APIs via Named Pipe |
| MCP Server | `mcp_server/` | Python 3.10-3.13 (x64) | FastMCP server bridging Named Pipes to MCP stdio |

### Key Files

**VS Extension:**
- `VSDebuggerMCPPackage.cs` - Package entry point; explicit Tools-menu activation, plus a
  launcher branch of `AIDebugger.StartFullAccessServer` that the MCP launcher dispatches
  through DTE with a one-shot `AIDB_LAUNCH_*` launch id (no dialogs). The debugger service
  and its debugger-event hooks are created lazily, only when a server actually starts.
- `DebuggerService.cs` - All debugger operations (callstack, locals, memory, disassembly)
- `NamedPipeServer.cs` - JSON-RPC over Named Pipe (`\\.\pipe\AIDebuggerVS_<PID>`)
- `McpClientRegistration.cs` - Auto-registers MCP server with Cursor/Claude Desktop/OpenCode
- `Protocol/DebugModels.cs` - Shared data models for responses

**MCP Server:**
- `vs_server.py` - FastMCP stdio entry point and tool registration
- `vs_launcher.py` - Stable, version-independent launcher for the active `vs_server.py`
- `vs_mcp_app.py` - `VisualStudioFastMCP` application instance
- `vs_tool_registry.py` - VS tool catalog, aliases, and backend method matrix
- `tool_modules/` - Domain tool modules registered through `register_vs()`
- `vs_session.py` - `VSSessionManager` target selection and dispatch
- `vs_bridge.py` - Named Pipe client with overlapped I/O and timeout handling
- `vs_launch.py` - Locates trusted `devenv.exe` installs (vswhere, Build Tools excluded),
  launches Visual Studio with the one-shot `AIDB_LAUNCH_*` request, waits for its
  authenticated registration, and closes only MCP-launched instances
- `bootstrap.py` - Extracts bundled wheels to `~/.ai-debugger/pydeps` on first run

### Communication Protocol

Named Pipe uses JSON-RPC style messages:
```json
// Request
{"id": "uuid", "method": "get_callstack", "params": {}}

// Response
{"id": "uuid", "result": {...}}

// Error
{"id": "uuid", "error": {"code": "ERROR_CODE", "message": "..."}}
```

### Multi-Instance Support

- Each Visual Studio instance creates a unique pipe: `AIDebuggerVS_<PID>`
- `AIDB_PREFERRED_IDE` env var is set to `vs` for the Visual Studio server
- Auto-selection priority: paused at breakpoint > debugging > single instance
- Pipe security configurable via `AIDB_PIPE_SECURITY` (strict/same_user/owner_only)

## Versioning

Version is computed automatically from git history — no manual edits needed.

| Scenario | Version format | Example |
|----------|---------------|---------|
| Tagged commit (`v1.2.3`) | Tag version | `1.2.3` |
| Untagged commit | `1.1.<commit count>` | `1.1.18` |

This scheme is identical for local IDE builds and CI, so the same commit always
produces the same version number regardless of where it is built.

### How it works

| Build system | Mechanism |
|-------------|-----------|
| VS Extension (MSBuild) | Pre-build target calls `tools/Stamp-Version.ps1`, which updates `source.extension.vsixmanifest` and `AssemblyInfo.cs` |
| GitHub Actions | Same as local — `fetch-depth: 0` ensures full git history is available |

### Manual override

```bash
# Stamp a specific version into all project files
powershell tools/Stamp-Version.ps1 -Version 2.0.0

# Preview computed version without modifying files
powershell tools/Stamp-Version.ps1 -DryRun
```

## Development Notes

### Response Contracts

All responses include capability declarations:
- `capability` - Feature support level for a backend method
- `behavior` - Semantic differences (e.g., `persisted_watch` for `add_watch`)
- `snapshotStable/snapshotStatus` - Consistency indicators for callstack/threads
- Memory operations return `status/bytesRead/partial` for reliability

### Standardized Stop Reasons

`get_debug_status` and `capture_debug_bundle` return:
- `stopReason`: `not_paused`, `breakpoint`, `exception`, `step`, `user_break`, `pause_unknown`
- `stopReasonRaw`: IDE-specific raw value

### Python Dependencies

Dependencies are bundled as wheels in `mcp_server/vendor/wheels/` with `manifest.json` for signature-based incremental updates. No pip install required at runtime.
