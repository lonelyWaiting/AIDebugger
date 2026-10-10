# AGENTS.md

This file provides guidance to Codex (Codex.ai/code) when working with code in this repository.

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

These tests combine contract checks with Python behavior tests. Pure C# protocol,
JSONC ownership, and frame-codec tests live in `VSDebuggerMCP.Tests/` and run with
`dotnet test VSDebuggerMCP.Tests/VSDebuggerMCP.Tests.csproj -c Release`.

### Test Named Pipe Connection
```bash
cd mcp_server
python -c "import asyncio; import vs_bridge; print(asyncio.run(vs_bridge.is_vs_available()))"
```

## Architecture

### Independent IDA backend

- `ida_backend/` owns IDA process/session management and private authenticated
  loopback transport; `mcp_server/ida_server.py` is its independent stdio entrypoint.
- Do not import VS debugger/session state into IDA or IDAPython into VS. Explicit
  IDA session IDs bind each binary/IDB. VS live debugging and IDA static analysis
  can coexist; only one user-mode debugger may attach to a given PID.
- Project installer: `Install-AIDebugger.cmd` / `tools/install_aidebugger.py` deploys
  IDA MCP and `skills/ida-evidence-analysis/` to Claude/Codex, preserving VS config.
  VSIX install/setup stays VS-only. See `docs/ida-backend.md` for workflow/tests.
- Maintain the IDA Skill in this repository, then redeploy with the installer.
  Do not modify only an installed Skill copy or vendor upstream without recording
  original hashes/license and scoped local changes.
- Validate IDA changes with `tests/test_ida_backend.py` and relevant opt-in real
  `tests/ida_integration/` probes; transport, source tests, IDB persistence,
  file export and dynamic debugger proof are separate evidence boundaries.

### Component Structure

| Component | Location | Language | Purpose |
|-----------|----------|----------|---------|
| VS Extension | `VSDebuggerMCP/` | C# (.NET 4.7.2) | VSIX package exposing EnvDTE debugger APIs via Named Pipe |
| MCP Server | `mcp_server/` | Python 3.10-3.14 (x64) | FastMCP server bridging Named Pipes to MCP stdio |

### Key Files

**VS Extension:**
- `VSDebuggerMCPPackage.cs` - Explicit Tools-menu activation/setup entry points plus the launcher branch of the existing `AIDebugger.StartFullAccessServer` command
- `DebuggerService.cs` - Shared debugger facade, state, and UI-thread helpers
- `Services/DebuggerService.*.cs` - Domain partials for inspection, execution, breakpoints, context, memory, disassembly, native, and navigation operations
- `Services/BuildErrorService.cs` / `Services/OutputWindowService.cs` - Bounded build/output collection services
- `NamedPipeServer.cs` - Authenticated AIDB/2 host (`\\.\pipe\AIDebuggerVS_<PID>`)
- `Ipc/LengthPrefixedFrameCodec.cs` - strict UTF-8 length-prefixed transport
- `Ipc/NamedPipeRequestProcessor.cs` / `NamedPipeMethodRouter.cs` / `NamedPipeProtocolValidator.cs` - deduplication, dispatch, strict validation, and typed errors
- `Protocol/DebugMethodCatalog.cs` - method risk/capability/deadline descriptors
- `McpClientRegistration.cs` / `Registration/McpClientRegistration.*.cs` - Explicit setup-only bundle deployment and atomic client registration
- `Protocol/DebugModels.cs` - Shared data models for responses

**MCP Server:**
- `vs_server.py` - FastMCP stdio entry point and VS tool registration
- `vs_launcher.py` - Stable, version-independent launcher for the active `vs_server.py`
- `vs_mcp_app.py` - `VisualStudioFastMCP` application instance
- `vs_tool_registry.py` - VS tool catalog, aliases, and backend method matrix
- `tool_modules/` - Domain tool modules registered through `register_vs()`
- `vs_session.py` - `VSSessionManager` target selection and dispatch
- `vs_bridge.py` - Named Pipe client with overlapped I/O and timeout handling
- `vs_launch.py` - Locates trusted devenv.exe installs, launches Visual Studio with the
  AIDB_LAUNCH_* request contract, waits for its registration, and closes only
  MCP-launched instances
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

- Each Visual Studio instance creates a unique pipe: `AIDebuggerVS_<PID>`
- `AIDB_PREFERRED_IDE` env var is set to `vs` for the Visual Studio server
- A user-started Visual Studio starts its server only from the Tools menu. An instance
  launched by `ensure_vs_instance` carries a one-shot `AIDB_LAUNCH_ID` /
  `AIDB_LAUNCH_CAPABILITIES` / `AIDB_LAUNCH_EXPIRES_UNIX_MS` environment request and is
  started with `/Command Tools.AIDebugger.StartFullAccessServer`; that command sees the
  launch id and starts the server without dialogs instead of prompting. `/Command` is also
  what keeps the IDE off the Start Page. When no registration appears, the launcher falls
  back to dispatching the same command through DTE automation. Two traps worth knowing:
  Visual Studio registers canonical names with the top-level menu prefixed (a bare
  `AIDebugger.<Command>` is rejected as "not valid"), and it rejects arguments for commands
  without the `AllowParams` flag, which is why the launch id travels in the environment.
  Do not switch this to `[ProvideAutoLoad]`/background autoload either: starting the server
  from a background package load left `attach_process` and `start_debugging` hanging.
- Auto-selection priority: paused at breakpoint > debugging > single instance
- VS pipe/instance registration default to current-user owner-only ACLs
- `AIDB_ALLOWED_CAPABILITIES` optionally restricts the VS server capability set
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

### Process Lookup Performance

`attach_process`/`start_debugging` resolve their target through
`DebuggerService.Execution.cs`. Two rules keep that path usable on busy machines
(637+ processes were measured):

- Resolve `Debugger.LocalProcesses` **once per snapshot** and pass the collection down.
  Re-reading `debugger.LocalProcesses` per process costs ~200 ms through the automation
  layer, which made one snapshot take minutes and every attach look like a hang.
- A PID is resolved with a single by-name automation lookup
  (`LocalProcesses.Item(name)`), verified against the process id and creation time, and
  only falls back to the full walk when that misses. Snapshots are cached for
  `LocalProcessSnapshotCacheMs` and invalidated when the process count changes.
  Correctness comes from verifying the chosen process through its OS handle, not from
  re-walking the whole list.

### Python Dependencies

Dependencies are bundled as wheels in `mcp_server/vendor/wheels/` with a
Python/platform coverage manifest and SHA-256 hashes. Runtime verifies selected
wheels and never invokes pip.
