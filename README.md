# AIDebugger MCP

AIDebugger exposes Visual Studio and JetBrains Rider debugger state through a
Model Context Protocol (MCP) server. It is intended for MCP clients such as
Cursor, Codex, Claude Desktop, Claude Code, and OpenCode.

It supports call stacks, locals, threads, modules, exceptions, build errors,
debug output, expression evaluation, memory reads/search, disassembly, breakpoints,
execution control, and Visual Studio-only no-source native/managed workflows.

## Architecture

```
Visual Studio VSIX -- authenticated AIDB/2 pipe --> VS-only FastMCP entry -- stdio --> MCP client
Rider plugin       -- Rider pipe ----------------> Rider FastMCP entry ---- stdio --> MCP client
```

Each IDE instance owns a named pipe:

- Visual Studio data: `\\.\pipe\AIDebuggerVS_<PID>`
- Visual Studio control: `\\.\pipe\AIDebuggerVS_<PID>_control` (paired with the data pipe; not a separate IDE instance)
- Rider: `\\.\pipe\AIDebuggerRider_<PID>`

Visual Studio discovery is based on owner-only instance registrations, not on
pipe names alone. A selected target is pinned by PID, process creation time,
server instance nonce, authentication token, and granted-capability snapshot.
Authentication data remains private and is never serialized in MCP results.
Composite/context-sensitive calls additionally pin
the debug-session generation and break/context epoch. The bridge never silently
redirects a failed selected request to another IDE instance; a stale pin must be
explicitly cleared or reselected.

The Visual Studio extension refuses to start its MCP server or modify per-user
MCP client configuration when `devenv.exe` is elevated. Run Visual Studio at
normal user integrity. This prevents the writable per-user Python runtime from
becoming an elevation bridge.

## Components

### Visual Studio Extension

Location: `VSDebuggerMCP/`

Important files:

- `VSDebuggerMCPPackage.cs` - explicit Tools-menu activation and setup commands.
- `DebuggerService.cs` - shared debugger facade, state, and UI-thread helpers.
- `Services/DebuggerService.*.cs` - inspection, evaluation/memory, disassembly,
  native, context, execution, breakpoint, navigation, and secondary-inspection
  domain implementations.
- `Services/BuildErrorService.cs` / `Services/OutputWindowService.cs` - bounded
  build/output collection with parsing and filtering outside the UI-thread snapshot.
- `NamedPipeServer.cs` - authenticated AIDB/2 request host and per-instance actor.
- `Ipc/LengthPrefixedFrameCodec.cs` - bounded, strict UTF-8 framing.
- `Ipc/NamedPipeRequestProcessor.cs`, `Ipc/NamedPipeMethodRouter.cs`, and
  `Ipc/NamedPipeProtocolValidator.cs` - command deduplication, routing, strict
  parameter/envelope validation, and typed error mapping.
- `Protocol/DebugMethodCatalog.cs` - request kind, capability, state, deadline,
  and result-budget descriptors.
- `DebugSessionTracker.cs` - event-driven session generation and stop reasons.
- `McpClientRegistration.cs` and `Registration/McpClientRegistration.*.cs` -
  explicit setup-only bundle deployment and atomic client configuration editing.
- `Protocol/DebugModels.cs` - response models shared with the bridge contract tests.

Visual Studio has the richest backend. VS-only tools include attach/start,
same-session debug-process enumeration/selection, detach, data/address
breakpoints, native address/module inspection, managed IL inspection, and
hybrid no-source capture flows.

### Rider Plugin

Location: `RiderDebuggerMCP/`

Important files:

- `RiderDebuggerPlugin.kt` - plugin startup and MCP client registration.
- `DebuggerService.kt` - Rider debugger operations.
- `NamedPipeServer.kt` - JSON-RPC request dispatch over named pipes.
- `McpClientRegistration.kt` - seeds the Python MCP server and registers MCP clients.
- `protocol/DebugModels.kt` - response models aligned with the VS contracts.

Rider supports the common snapshot, stepping, breakpoint, memory, disassembly,
and stop flows. Rider detach, same-session multi-process selection, and the
VS-only no-source/native-managed tools are not exposed as supported Rider
backends.

### Python MCP Server

Location: `mcp_server/`

Important files:

- `server.py` - combined thin stdio entry point.
- `vs_server.py` - strict VS-only entry point that does not import Rider.
- `mcp_app.py` - shared FastMCP application instance.
- `tool_modules/` - domain modules for capability, instance, snapshot, control,
  breakpoint, memory, and native/managed tools.
- `tool_registry.py` - declarative tool/backend matrix, aliases, timeout classes,
  supported backends, and registry validation.
- `debug_method_manifest.json` - shared VSIX/Python source of truth for backend
  method kind, base capabilities, and conditional capabilities.
- `tool_context.py` - shared formatting, error handling, timeout, and request helpers.
- `ide_bridge.py` - backend selection and preferred-IDE routing.
- `vs_bridge.py` / `rider_bridge.py` - named pipe clients.
- `bootstrap.py` - extracts bundled wheels to a content-addressed
  `~/.ai-debugger/pydeps/...` directory and validates the exact plain-file tree
  before adding it to `sys.path`; the wheel directory must exactly match its
  manifest, only wheel-declared/hash-validated `.pth` behavior is accepted,
  and critical imports must resolve inside that validated cache.
- `vendor/wheels/` - bundled Python wheels; runtime does not require `pip install`.

`debug_capabilities` returns the current categorized tool index and embeds the
registry summary. Use it as the authoritative discovery entry point instead of
assuming a fixed flat tool list.

## Tool Domains

The combined entry point exposes 83 MCP tools. The VS-only entry point exposes
65 canonical tools by default: cross-IDE selectors and deprecated compatibility
aliases are omitted. Set `AIDB_ENABLE_LEGACY_ALIASES=1` only for migration.
The registry in `mcp_server/tool_registry.py` is the source of truth for tool
names, aliases, backend support, and timeout classes. Backend method kind and
capability policy come from `mcp_server/debug_method_manifest.json`, which is
embedded into the VSIX assembly and shipped with the Python runtime.

### Capability and Instance Selection

- `debug_capabilities`
- `detect_debug_sessions`
- Combined entry only: `list_ide_instances`, `select_ide_instance`, `clear_ide_selection`
- VS-only entry: `list_vs_instances`, `select_vs_instance`, `clear_vs_selection`

### Snapshot and Inspection

- `get_debug_status`
- `get_callstack` (`get_call_stack` is a combined/legacy alias)
- `get_locals`
- `evaluate_expression`, `evaluate_expressions_batch`
- `get_threads` (`list_threads` is a combined/legacy alias)
- `get_modules`, `get_exception`
- `get_build_errors`, `get_errors_only`, `get_warnings_only`
- `get_debug_output`, `search_output`
- `get_full_context`
- `capture_debug_bundle`
- `crash_triage`
- `step_and_capture`

`capture_debug_bundle()` is low-blocking by default: it captures status,
callstack, and budgeted threads. Locals, exception, registers, and expressions
are explicit unsafe-evaluation opt-ins; modules and output are read-only opt-ins.
Only a bundle without locals, exception, registers, or expressions uses the
`debug.read` backend. `get_exception` also requires `debug.evaluate.unsafe`
because Visual Studio obtains it through debugger expression evaluation.
Expression tools attempt the requested expression
and return the IDE's explicit value/error/timeout result.

When `get_modules` must fall back to stack-frame discovery,
`scanContinuationToken` resumes the bounded scan against the same cached debug
context. `moduleSnapshotId` and `nextModuleIndex` only page modules already
captured in one immutable snapshot; they never silently start another scan.

### Execution Control

- `attach_process` (Visual Studio only)
- `get_debug_processes`, `list_debug_processes` (Visual Studio only)
- `switch_process`, `select_process` (Visual Studio only)
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

`get_debug_processes` lists only processes already in the current Visual Studio
debug session. While paused, pass one returned PID to `switch_process`; it does
not enumerate ordinary local processes or attach a new target. The switch
invalidates previously captured thread/frame context handles.

For Rider, use stop tools to terminate the current debugging session; detach and
same-session multi-process switching are not advertised as supported.

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
The VS adapter distinguishes x86, x64, ARM32, ARM64, IA64, and unknown targets.
Iced decoding is used only for proven x86/x64 targets; other architectures
return typed unsupported/unknown errors instead of guessed instructions.
Register snapshots use the architecture-correct register set (including 8 XMM
registers on x86 and 16 on x64) and report requested, captured, and unavailable
registers plus partial/availability and snapshot-consistency status.

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
- Python 3.10-3.14 x64 for the MCP server.
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
CI also generates and validates an SPDX 2.2 SBOM. Tagged releases fail unless
both VSIX packages are signed through Sign CLI using the configured signing certificate.

First install (run from a normal, non-administrator terminal):

```powershell
VSDebuggerMCP/Install-VSIX.cmd -Target vs2022
VSDebuggerMCP/Install-VSIX.cmd -Target vs2026
VSDebuggerMCP/Install-VSIX.cmd -Target all
```

An upgrade is fail-safe only when the installer can restore the exact package
that is currently installed. Keep each downloaded release VSIX together with
its published `.sha256` file. For example, when upgrading both targets from a
previous release:

```powershell
$old2022 = "C:\Downloads\AIDebugger-v2.0.2\VSDebuggerMCP.VS2022.vsix"
$old2026 = "C:\Downloads\AIDebugger-v2.0.2\VSDebuggerMCP.VS2026.vsix"
$hash2022 = ((Get-Content "$old2022.sha256" -Raw) -split '\s+')[0]
$hash2026 = ((Get-Content "$old2026.sha256" -Raw) -split '\s+')[0]
& .\VSDebuggerMCP\Install-VSIX.ps1 -Target all `
  -RollbackVsixPath $old2022,$old2026 `
  -RollbackVsixSha256 $hash2022,$hash2026
```

Verify the checksum files against the corresponding published GitHub release,
not against an untrusted local copy. The installer copies and revalidates those
rollback packages before changing any VS instance. For an installation made
before release checksums were published, uninstall the existing AIDebugger VSIX
(`VSDebuggerMCP.05f75d2f-40fb-469a-906f-8e2e6cedcf01`) from Visual Studio's
Manage Extensions UI, then use the first-install command. This one-time
migration intentionally has no automatic rollback because no independently
verifiable original artifact exists.

The install/setup flow seeds the VS-only server transactionally to
`<user-home>/.ai-debugger/mcp_server_vs`. Each staged payload is hash-validated
and retained under a content/version-addressed directory. The stable
`vs_server.py` launcher follows the atomically updated `.active-bundle` marker;
the installer may therefore perform an explicit rollback without stale newer
payloads winning automatically. Cleanup never removes the active payload,
keeps the three newest inactive payloads, and only removes older inactive
payloads after 30 days.

The stable launcher rejects unexpected files/directories, bytecode caches,
symbolic links, junctions, and other reparse points. Setup applies the same
strict tree policy so a rejected bundle is rebuilt instead of remaining stuck.

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

### Explicit MCP Client Registration

Visual Studio no longer starts the pipe server or modifies external client
configuration on every IDE startup. Invoke these commands from **Tools**:

- **Start AIDebugger MCP Server (Read Only)** — exposes only `server.control` and `debug.read`
- **Start AIDebugger MCP Server (Full Access)** — explicitly enables expression, memory, breakpoint, debugger-context, execution-control, and session-write capabilities
- **Configure AIDebugger MCP Clients...**

Visual Studio setup registers only `AIDebuggerVS` with
`AIDB_PREFERRED_IDE=vs`. Rider owns its independent `AIDebuggerRider`
registration and is not discovered or configured by the VS-only server.

Supported registration targets:

- Cursor: `~/.cursor/mcp.json`
- Codex: `$CODEX_HOME/config.toml` or `~/.codex/config.toml`
- Claude Desktop: `%APPDATA%/Claude/claude_desktop_config.json`
- Claude Code: `~/.claude.json`
- OpenCode: `~/.config/opencode/opencode.json`

Restart the MCP client after installing or updating the IDE extension.

### Manual MCP Configuration

For manual Visual Studio configuration, point the client at the VS-only seeded server:

```json
{
  "mcpServers": {
    "AIDebuggerVS": {
      "command": "C:/Path/To/Python/python.exe",
      "args": ["<user-home>/.ai-debugger/mcp_server_vs/vs_server.py"],
      "env": { "AIDB_PREFERRED_IDE": "vs" }
    }
  }
}
```

Use an installed Python 3.10-3.14 x64 interpreter. Runtime dependencies are
manifest-pinned and bundled; startup verifies SHA-256 before extraction and
then requires both the wheel directory and the marker's declared dependency
tree to match exactly. Critical modules must import from that validated cache,
not ambient site-packages.
It never downloads packages with `pip`.

## Usage

1. Start Visual Studio with the AIDebugger extension installed.
2. Invoke **Tools > Start AIDebugger MCP Server** (and run the setup command once).
3. Start or attach to a debug session.
4. Ask the MCP client to call `detect_debug_sessions`.
5. In a multi-process Visual Studio session, call `get_debug_processes` and then
   `switch_process(process_id)` while paused before reading target-specific state.
6. Use `debug_capabilities` when unsure which tool is appropriate.
7. For repro loops, use `continue_and_wait_for_break` or the more focused
   `continue_and_capture_*` tools.
8. For first-pass crash/bug diagnosis, prefer `capture_debug_bundle()` in its
   default low-blocking mode and opt in to heavier data only when needed.

When multiple Visual Studio instances are running through the VS-only entry,
use `list_vs_instances` and `select_vs_instance` to pin the target. The combined
entry uses `list_ide_instances` and `select_ide_instance`. Without a manual
selection, the bridge prefers a uniquely paused or uniquely debugging instance.

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

Pure C# protocol/configuration tests:

```powershell
dotnet test VSDebuggerMCP.Tests/VSDebuggerMCP.Tests.csproj -c Release
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
4. With the VS-only server, use `list_vs_instances` or `detect_debug_sessions`; the explicit combined development entry also provides `list_ide_instances`.
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
Get-ChildItem \\.\pipe\ | Where-Object { $_.Name -match '^AIDebuggerVS_[1-9][0-9]*(?:_control)?$' }
Get-ChildItem \\.\pipe\ | Where-Object { $_.Name -like "AIDebuggerRider_*" }
```

## Protocol

Visual Studio uses AIDB/2: a four-byte big-endian payload length followed by
strict UTF-8 JSON. Frames, parameters, results, and deadlines are bounded.
Every request is authenticated and declares its command/query kind, primary
capability, immutable target identity, deadline, and debug-session generation.
The server catalog additionally enforces every capability required by composite
operations (for example execution control plus read access):

```json
{
  "protocol":"aidb/2",
  "id":"4eb7d887-3799-43ee-8c7c-79ef509ef15a",
  "method":"get_callstack",
  "params":{},
  "meta":{
    "authToken":"owner-only registration token",
    "clientProcessId":5678,
    "clientProcessStartTimeUtcTicks":638000000000000000,
    "deadlineUnixMs":1784600000000,
    "kind":"query",
    "capability":"debug.read",
    "target":{
      "processId":1234,
      "processStartTimeUtcTicks":638000000000000000,
      "serverInstanceId":"instance nonce",
      "debugSessionGeneration":7,
      "breakSequence":12,
      "contextEpoch":19
    }
  }
}
```

Successful response:

```json
{"protocol":"aidb/2","id":"...","status":"ok","server":{},"result":{}}
```

Error response:

```json
{"protocol":"aidb/2","id":"...","status":"error","server":{},"error":{"code":"STALE_DEBUG_SESSION","message":"..."}}
```

Mutating commands are deduplicated by request ID for the entire VS server-instance
lifetime; response bodies have a separate bounded cache and eviction never permits
re-execution. Transport loss after command
acceptance returns `OUTCOME_UNKNOWN`; clients query `get_command_status` rather
than replaying the command. `AIDB_ALLOWED_CAPABILITIES` can restrict the server
to a comma-separated subset. Pipe and instance-registration ACLs are always
protected current-user owner-only ACLs; there is no world-access compatibility mode.
This is intentionally a same-Windows-user trust boundary, not a defense against a
malicious process already running as that user. Run untrusted tools under another
Windows identity and do not start the full-access server in that environment.

Managed assembly/type/IL inspection parses ECMA-335 metadata and method bodies
directly with Mono.Cecil. It never CLR-loads the inspected assembly or its
dependencies, so module initializers cannot execute during inspection. The helper
is isolated in a kill-on-close Windows Job with process/job memory caps, a 256 MiB
input limit, bounded output, deadline cancellation, and metadata traversal limits.

## License

MIT License
