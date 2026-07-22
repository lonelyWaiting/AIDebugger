# AGENTS.md

This file provides guidance to Codex (Codex.ai/code) when working with code in this repository.

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

First install examples (normal, non-administrator terminal only):
```bash
VSDebuggerMCP/Install-VSIX.cmd -Target vs2022
VSDebuggerMCP/Install-VSIX.cmd -Target vs2026
VSDebuggerMCP/Install-VSIX.cmd -Target all
```

Upgrades require the exact previously installed VSIX plus its independently
published SHA-256, supplied through matching `-RollbackVsixPath` and
`-RollbackVsixSha256` arrays. CI artifacts publish `*.vsix.sha256` beside each
VSIX. Installations predating those records must uninstall the old extension
from Manage Extensions once, then use the first-install command.

Note: `VSDebuggerMCP/bin/Release/VSDebuggerMCP.vsix` is an intermediate package and is deleted at the end of the build.
The explicit VS setup command seeds VS-only MCP files transactionally to
`~/.ai-debugger/mcp_server_vs`.

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

These tests combine contract checks with Python behavior tests. Pure C# protocol,
JSONC ownership, and frame-codec tests live in `VSDebuggerMCP.Tests/` and run with
`dotnet test VSDebuggerMCP.Tests/VSDebuggerMCP.Tests.csproj -c Release`.

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
| MCP Server | `mcp_server/` | Python 3.10-3.14 (x64) | FastMCP server bridging Named Pipes to MCP stdio |

### Key Files

**VS Extension:**
- `VSDebuggerMCPPackage.cs` - Explicit Tools-menu activation/setup entry point
- `DebuggerService.cs` - Shared debugger facade, state, and UI-thread helpers
- `Services/DebuggerService.*.cs` - Domain partials for inspection, execution, breakpoints, context, memory, disassembly, native, and navigation operations
- `Services/BuildErrorService.cs` / `Services/OutputWindowService.cs` - Bounded build/output collection services
- `NamedPipeServer.cs` - Authenticated AIDB/2 host (`\\.\pipe\AIDebuggerVS_<PID>`)
- `Ipc/LengthPrefixedFrameCodec.cs` - strict UTF-8 length-prefixed transport
- `Ipc/NamedPipeRequestProcessor.cs` / `NamedPipeMethodRouter.cs` / `NamedPipeProtocolValidator.cs` - deduplication, dispatch, strict validation, and typed errors
- `Protocol/DebugMethodCatalog.cs` - method risk/capability/deadline descriptors
- `McpClientRegistration.cs` / `Registration/McpClientRegistration.*.cs` - Explicit setup-only bundle deployment and atomic client registration
- `Protocol/DebugModels.cs` - Shared data models for responses

**Rider Plugin:**
- `RiderDebuggerPlugin.kt` - Plugin entry point
- `DebuggerService.kt` - Debugger operations (mirrors VS implementation)
- `NamedPipeServer.kt` - Named Pipe server (`\\.\pipe\AIDebuggerRider_<PID>`)
- `McpClientRegistration.kt` - MCP registration with Python version check

**MCP Server:**
- `server.py` - Combined thin entry point for all 83 MCP tools
- `vs_server.py` - VS-only entry point; never imports or discovers Rider
- `ide_bridge.py` - Unified bridge with auto-selection logic for multiple IDE instances
- `vs_bridge.py` / `rider_bridge.py` - Named Pipe clients with overlapped I/O and timeout handling
- `bootstrap.py` - Extracts bundled wheels to `~/.ai-debugger/pydeps` on first run

### Communication Protocol

Visual Studio uses authenticated AIDB/2 length-prefixed messages. Requests include
`protocol`, UUID `id`, `method`, `params`, and metadata containing `authToken`,
`deadlineUnixMs`, `kind`, `capability`, and immutable target/session identity.
```json
// Request
{"protocol":"aidb/2","id":"uuid","method":"get_callstack","params":{},"meta":{"kind":"query","capability":"debug.read","target":{}}}

// Response
{"id": "uuid", "result": {...}}

// Error
{"id": "uuid", "error": {"code": "ERROR_CODE", "message": "..."}}
```

### Multi-Instance Support

- Each IDE instance creates a unique pipe: `AIDebuggerVS_<PID>` or `AIDebuggerRider_<PID>`
- `AIDB_PREFERRED_IDE` env var locks MCP server to `vs` or `rider`
- Auto-selection priority: paused at breakpoint > debugging > single instance
- VS pipe/instance registration default to current-user owner-only ACLs
- `AIDB_ALLOWED_CAPABILITIES` optionally restricts the server capability set
- VS has no world-access pipe compatibility mode

## Versioning

Version is computed automatically from git history — no manual edits needed.

| Scenario | Version format | Example |
|----------|---------------|---------|
| Tagged commit (`v1.2.3`) | Tag version | `1.2.3` |
| Untagged commit | `<nearest tag>.<distance>` | `2.0.3.6` |

This scheme is identical for local IDE builds and CI, so the same commit always
produces the same version number regardless of where it is built.

### How it works

| Build system | Mechanism |
|-------------|-----------|
| VS Extension (MSBuild) | Pre-build target calls `tools/Stamp-Version.ps1`, which updates `source.extension.vsixmanifest` and `AssemblyInfo.cs` |
| Rider Plugin (Gradle) | `build.gradle.kts` runs `git describe` / `git rev-list --count` at configuration time |
| GitHub Actions | Same as local — `fetch-depth: 0` ensures full git history is available |

### Manual override

```bash
# Stamp a specific version into all project files
powershell tools/Stamp-Version.ps1 -Version 2.0.0

# Preview computed version without modifying files
powershell tools/Stamp-Version.ps1 -DryRun
```

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

Dependencies are bundled as wheels in `mcp_server/vendor/wheels/` with a
Python/platform coverage manifest and SHA-256 hashes. Runtime verifies selected
wheels and never invokes pip.
