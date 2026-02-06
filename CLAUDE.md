# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

AIDebugger MCP is a Model Context Protocol server that enables Cursor IDE to access Visual Studio and Rider debugging information. The architecture follows a three-tier design:

```
Visual Studio (VSIX) ──┐
                       ├── Named Pipes ──> Python MCP Server ──> Cursor (stdio)
Rider (IntelliJ Plugin)┘
```

## Build Commands

### Full Solution Build (VS + Rider)
```bash
# Open AIDebugger.sln in Visual Studio and build Release
# This builds the VSIX and triggers Rider plugin build via RiderBuild.csproj
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

### Rider Plugin Only
```bash
# Requires JDK 21+ (set JAVA_HOME or use Rider's bundled JBR)
cd RiderDebuggerMCP
./gradlew.bat buildPlugin
```
Output: `RiderDebuggerMCP/build/distributions/*.zip`

To build against a local Rider installation, set `RIDER_HOME` to the Rider install directory.

### Run MCP Server Directly (Development)
```bash
cd mcp_server
pip install -r requirements.txt  # dev only; extensions bundle wheels
python server.py
```

## Testing

### Contract Tests
```bash
python -m unittest discover -s tests -p "test_*.py"
```

These tests validate protocol field alignment between VS/Rider implementations and bridge timeout semantics. They are static assertions (no running IDE required).

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
| Rider Plugin | `RiderDebuggerMCP/` | Kotlin (JDK 21) | IntelliJ plugin exposing Rider debugger APIs via Named Pipe |
| MCP Server | `mcp_server/` | Python 3.10-3.13 (x64) | FastMCP server bridging Named Pipes to Cursor stdio |

### Key Files

**VS Extension:**
- `VSDebuggerMCPPackage.cs` - Package entry point, auto-loads on VS startup
- `DebuggerService.cs` - All debugger operations (callstack, locals, memory, disassembly)
- `NamedPipeServer.cs` - JSON-RPC over Named Pipe (`\\.\pipe\AIDebuggerVS_<PID>`)
- `CursorMcpRegistration.cs` - Auto-registers MCP server with Cursor's `mcp.json`
- `Protocol/DebugModels.cs` - Shared data models for responses

**Rider Plugin:**
- `RiderDebuggerPlugin.kt` - Plugin entry point
- `DebuggerService.kt` - Debugger operations (mirrors VS implementation)
- `NamedPipeServer.kt` - Named Pipe server (`\\.\pipe\AIDebuggerRider_<PID>`)
- `CursorMcpRegistration.kt` - MCP registration with Python version check

**MCP Server:**
- `server.py` - FastMCP tool definitions (40+ debugging tools)
- `ide_bridge.py` - Unified bridge with auto-selection logic for multiple IDE instances
- `vs_bridge.py` / `rider_bridge.py` - Named Pipe clients with overlapped I/O and timeout handling
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

- Each IDE instance creates a unique pipe: `AIDebuggerVS_<PID>` or `AIDebuggerRider_<PID>`
- `AIDB_PREFERRED_IDE` env var locks MCP server to `vs` or `rider`
- Auto-selection priority: paused at breakpoint > debugging > single instance
- Pipe security configurable via `AIDB_PIPE_SECURITY` (strict/same_user/owner_only)

## Development Notes

### VS/Rider Feature Parity

See `docs/DeepReview-VS-Rider-Alignment.md` for detailed capability matrix. Key differences:
- VS has richer module/exception metadata
- Rider output limited to debug console (VS supports multiple panes)
- Rider `add_watch` only evaluates (VS persists to Watch window)
- Memory operations return `status/bytesRead/partial` for reliability

### Response Contracts

All responses include capability declarations:
- `capability` - Feature support level (e.g., `limited_to_debug_console`)
- `behavior` - Semantic differences (e.g., `evaluate_only` vs `persisted_watch`)
- `snapshotStable/snapshotStatus` - Consistency indicators for callstack/threads

### Standardized Stop Reasons

`get_debug_status` and `capture_debug_bundle` return:
- `stopReason`: `not_paused`, `breakpoint`, `exception`, `step`, `user_break`, `pause_unknown`
- `stopReasonRaw`: IDE-specific raw value

### Python Dependencies

Dependencies are bundled as wheels in `mcp_server/vendor/wheels/` with `manifest.json` for signature-based incremental updates. No pip install required at runtime.
