# AIDebugger MCP

AIDebugger exposes Visual Studio debugging and an independent IDA analysis/patch
backend through Model Context Protocol (MCP) servers. It is intended for clients such as
Cursor, Codex, Claude Desktop, Claude Code, and OpenCode.

It supports call stacks, locals, threads, modules, exceptions, build errors,
debug output, expression evaluation, memory reads/search, disassembly, breakpoints,
execution control, and Visual Studio-only no-source native/managed workflows.

IDA supports automatically managed GUI/headless instances, multiple isolated
DLL/IDB sessions, Hex-Rays analysis, IDB patching and binary export. VS and IDA
can run together. See [IDA architecture, installation and validation](docs/ida-backend.md).
Run `Install-AIDebugger.cmd` to deploy the IDA MCP and project-maintained analysis
Skill to Claude/Codex; Visual Studio keeps its existing VSIX installer/setup.

## Architecture

```
Visual Studio VSIX -- authenticated AIDB/2 pipe --> VS-only FastMCP entry -- stdio --> MCP client
```

A Visual Studio instance owns a paired data/control named pipe:

- Data: `\\.\pipe\AIDebuggerVS_<PID>`
- Control: `\\.\pipe\AIDebuggerVS_<PID>_control` (paired with the data pipe; not a separate instance)

Visual Studio instances are discovered through owner-only instance registrations
rather than pipe names alone. A selected target is pinned by PID, process
creation time, server instance nonce, authentication token, and
granted-capability snapshot.
Authentication data remains private and is never serialized in MCP results.
Composite/context-sensitive calls additionally pin
the debug-session generation and break/context epoch. The bridge never silently
redirects a failed selected request to another Visual Studio instance; a stale
pin must be explicitly cleared or reselected.

The Visual Studio extension refuses to start its MCP server or modify per-user
MCP client configuration when `devenv.exe` is elevated. Run Visual Studio at
normal user integrity. This prevents the writable per-user Python runtime from
becoming an elevation bridge.

## Components

### Visual Studio Extension

Location: `VSDebuggerMCP/`

Important files:

- `VSDebuggerMCPPackage.cs` - explicit Tools-menu activation, setup commands, and the
  launcher branch of `AIDebugger.StartFullAccessServer` that the MCP launcher dispatches
  with a one-time launch id.
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

### Python MCP Server

Location: `mcp_server/`

Important files:

- `vs_server.py` - Visual Studio MCP stdio entry point.
- `vs_launcher.py` - stable, version-independent launcher that validates the
  signed bundle and runs the active `vs_server.py`.
- `vs_mcp_app.py` - `VisualStudioFastMCP` application instance.
- `tool_modules/` - domain modules for capability, instance, snapshot, control,
  breakpoint, memory, and native/managed tools; registered through
  `tool_modules/__init__.py::register_vs()`.
- `vs_tool_registry.py` - declarative tool/backend matrix, aliases, timeout classes,
  supported backends, and registry validation.
- `debug_method_manifest.json` - shared VSIX/Python source of truth for backend
  method kind, base capabilities, and conditional capabilities.
- `tool_context.py` - shared formatting, error handling, timeout, and request helpers.
- `vs_session.py` - `VSSessionManager` target selection, pinning, and dispatch.
- `vs_bridge.py` - named pipe client.
- `vs_launch.py` - discovers trusted `devenv.exe` installations, launches one with the
  launcher environment request, waits for its authenticated registration, and closes
  only instances this MCP server started.
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

The server exposes 70 canonical MCP tools by default; deprecated compatibility
aliases are omitted. Set `AIDB_ENABLE_LEGACY_ALIASES=1` only for migration.
The registry in `mcp_server/vs_tool_registry.py` is the source of truth for tool
names, aliases, backend support, and timeout classes. Backend method kind and
capability policy come from `mcp_server/debug_method_manifest.json`, which is
embedded into the VSIX assembly and shipped with the Python runtime.

### Capability and Instance Selection

- `debug_capabilities`
- `detect_debug_sessions`
- `list_vs_instances`, `select_vs_instance`, `clear_vs_selection`
- `ensure_vs_instance`, `close_vs_instance`
- Multi-target dispatch: `dispatch_vs_instances(method, process_ids, ...)` runs a
  read-only AIDB/2 query against independently authenticated immutable VS targets
  concurrently, and `dispatch_vs_commands(method, process_ids, ...)` is the
  destructive counterpart for command methods. Both accept AIDB/2 backend method
  names rather than MCP tool names and leave the normal pinned single-instance
  target unchanged. The two tools are split by method kind so their published
  annotations are accurate: the query tool is annotated read-only, the command
  tool destructive.

When no Visual Studio instance is available at all, `ensure_vs_instance` launches one
instead of asking the user to open Visual Studio by hand. It picks the newest trusted IDE
installation (`vswhere`, excluding Build Tools) and starts `devenv.exe` with
`/Command Tools.AIDebugger.StartFullAccessServer`. Using `/Command` keeps the IDE off the
Start Page, and the command itself runs the same code a Tools-menu click runs — minus the
capability dialogs, because the launcher's one-time launch id is present in the process
environment. If no registration appears, the launcher falls back to dispatching that same
command through the IDE's DTE automation. It then waits for the authenticated registration
and pipe, pins the instance, and optionally attaches to `process_id`/`process_name` in the
same call. Launched instances receive the full debug capability set by default
(`capabilities` can narrow it). Instances the user started are untouched: the extension
still starts its server only from the Tools menu. `close_vs_instance` refuses instances it
did not launch.

### Snapshot and Inspection

- `get_debug_status`
- `get_callstack` (`get_call_stack` is a legacy alias)
- `get_locals`
- `evaluate_expression`, `evaluate_expressions_batch`
- `get_threads` (`list_threads` is a legacy alias)
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

- Windows 10/11 for the Visual Studio debugger integration.
- Visual Studio 2022 17.0+ or Visual Studio 2026 18.0+.
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

### Explicit MCP Client Registration

Visual Studio no longer starts the pipe server or modifies external client
configuration on every IDE startup. Invoke these commands from **Tools**:

- **Start AIDebugger MCP Server (Read Only)** — exposes only `server.control` and `debug.read`
- **Start AIDebugger MCP Server (Full Access)** — explicitly enables expression, memory, breakpoint, debugger-context, execution-control, and session-write capabilities
- **Configure AIDebugger MCP Clients...**

Visual Studio setup registers `AIDebuggerVS` with `AIDB_PREFERRED_IDE=vs`.

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

1. Start Visual Studio with the AIDebugger extension installed, or let the MCP client do
   it: `ensure_vs_instance(process_name="app.exe")` launches a trusted installation and
   attaches in one call when no instance is running.
2. Invoke **Tools > Start AIDebugger MCP Server** (and run the setup command once).
3. Start or attach to a debug session.
4. Ask the MCP client to call `detect_debug_sessions`.
5. In a multi-process Visual Studio session, call `get_debug_processes` and then
   `switch_process(process_handle=...)` while paused before reading target-specific state.
   The opaque handle binds the debug-session generation and immutable OS process
   creation time; `process_id` remains a deprecated compatibility fallback.
6. Use `debug_capabilities` when unsure which tool is appropriate.
7. For repro loops, use `continue_and_wait_for_break` or the more focused
   `continue_and_capture_*` tools.
8. For first-pass crash/bug diagnosis, prefer `capture_debug_bundle()` in its
   default low-blocking mode and opt in to heavier data only when needed.

When multiple Visual Studio instances are running, use `list_vs_instances` and
`select_vs_instance` to pin the target. Without a manual selection, the bridge
prefers a uniquely paused or uniquely debugging instance.
For one concurrent operation across several VS instances, use
`dispatch_vs_instances(method="capture_debug_bundle_safe", process_ids=[...])`, or
`dispatch_vs_commands(...)` when the same command must run against several
confirmed targets; each result reports its exact target identity and an
unavailable target does not prevent the other authenticated targets from running.

## Development

Run the MCP server directly:

```powershell
cd mcp_server
python vs_server.py
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

GitHub Actions builds intentionally disable NuGet cache writing to avoid
consuming the repository cache quota.

## Troubleshooting

### Cannot connect to IDE

1. Call `ensure_vs_instance`: with no Visual Studio running it launches a trusted
   installation, starts the MCP server in it, and returns once the pipe is usable.
2. If one is already running, confirm the AIDebugger extension is installed and that
   **Tools > Start AIDebugger MCP Server** has been invoked for that instance.
3. Check the AIDebugger output/logs in Visual Studio.
4. Use `list_vs_instances` or `detect_debug_sessions` to discover the target.
5. Restart Visual Studio if the named pipe is stale.

### Not in break mode

Most inspection tools require the debugger to be paused. Start debugging and hit
a breakpoint, break manually, or use `continue_and_wait_for_break`.

### Preferred IDE not found

The MCP entry is strict: `AIDebuggerVS` only routes to Visual Studio. Call
`ensure_vs_instance` to launch one, or start Visual Studio yourself, and confirm the
client sets `AIDB_PREFERRED_IDE=vs`.

### ensure_vs_instance times out waiting for the launched instance

The launch is honoured only by an extension build that carries the launcher command, which
is passed as `devenv /Command` and falls back to DTE automation. When the instance starts
but never publishes a registration:

1. Read `diagnostics` in the result: it names the failure directly (an unresolved command
   name, DTE automation being unavailable, or a rejected launch id). Note that Visual
   Studio registers the canonical name with the top-level menu prefixed — a bare
   `AIDebugger.<Command>` is rejected as "not valid".
2. Rebuild and reinstall the extension for the installation that was launched
   (`VSDebuggerMCP/Install-VSIX.cmd`); a stale VSIX has no launcher command and the
   instance will sit there with no server.
3. Confirm the launched installation is the intended one; pass `install_path` or
   `instance_id` to force a specific IDE, since Build Tools installations are excluded.
4. `launch_failed` means the process exited (a solution already open in another instance is
   handed over and exits), while `ready_timeout` means it stayed up without registering.

### Check named pipes

```powershell
Get-ChildItem \\.\pipe\ | Where-Object { $_.Name -match '^AIDebuggerVS_[1-9][0-9]*(?:_control)?$' }
```

## Protocol

The Visual Studio extension uses AIDB/2: a four-byte big-endian payload length
followed by strict UTF-8 JSON. Frames, parameters, results, and deadlines are
bounded.
Every request is authenticated and declares its command/query kind, primary
capability, immutable target identity, deadline, and debug-session generation.
The server catalog additionally enforces every capability required by composite
operations (for example execution control plus read access):

```json
{
  "protocol":"aidb/2",
  "id":"019f8a0b-f758-7722-888f-83c5dce0c096",
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

The owner-only instance registration and every response also carry a
`protocolCatalogHash` (SHA-256 of the shared method manifest). Discovery fails
with `PROTOCOL_CATALOG_MISMATCH` when the deployed VSIX and Python bundle
do not describe the same protocol surface.

Successful response:

```json
{"protocol":"aidb/2","id":"...","status":"ok","server":{},"result":{}}
```

Error response:

```json
{"protocol":"aidb/2","id":"...","status":"error","server":{},"error":{"code":"STALE_DEBUG_SESSION","message":"..."}}
```

Mutating commands require canonical UUIDv7 request IDs and are deduplicated for
a bounded 24-hour acceptance window. Completed tombstones remain fail-closed for
that whole window; expired IDs are rejected before they can execute, so bounded
reclamation never permits replay. Response bodies have a separate bounded cache.
Transport loss after command acceptance returns `OUTCOME_UNKNOWN`; clients query
`get_command_status` rather than replaying the command. Visual Studio retains
24-hour command tombstones and bounded response bodies, and additionally retains
queries that finish after the transport deadline in a bounded five-minute result
journal.

The VSIX maintains 8 data, 4 interrupt, and 2 recovery listeners. Their protected
current-user ACL includes `CreateNewInstance` only so the owning VS process can
create the configured same-name listener pool; no other SID is granted access.
An admitted UI/COM operation that remains unresolved after the poison threshold
puts the process-wide dispatcher into `poisoned`. Use `get_dispatcher_health` for
the active request/effect state plus actor queue depth, configured listener pool,
command-ledger capacity, and query-journal usage; recovery requires restarting
that Visual Studio process and cannot be bypassed by toggling the MCP server.
`get_dispatcher_health`, `get_command_status`, and queued-request cancellation
bypass debugger execution and remain available while the actor is busy.

For Visual Studio, `AIDB_ALLOWED_CAPABILITIES` can restrict the server to a
comma-separated subset. Pipe and instance-registration ACLs are always
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
