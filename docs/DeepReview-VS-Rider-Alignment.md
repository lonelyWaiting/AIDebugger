# Visual Studio / Rider capability alignment

Last verified: 2026-07-22

This document describes the current public contracts. It is not a historical
security review and must not be used as evidence for older AIDB/1 builds.

## Runtime separation

Visual Studio and Rider use separate MCP entry points and discovery paths.
The Visual Studio package deploys only the VS runtime (`vs_server.py`), which
does not import, discover, select, or fail over to Rider. The combined
development entry remains available for explicit cross-IDE use, but it is not
the VSIX production path.

```text
VSIX -> AIDebuggerVS_<PID> + AIDebuggerVS_<PID>_control
     -> authenticated AIDB/2 -> VS-only FastMCP server

Rider plugin -> AIDebuggerRider_<PID>
             -> Rider FastMCP server
```

The `_control` pipe is paired with one VS instance; it is not a separately
selectable instance. It exposes only immutable health/request-status and
cancellation operations. Debugger and solution state use the ordinary pipe and
the per-instance actor.

## Visual Studio security and command semantics

The current VS transport has these mandatory properties:

- protected current-user DACL with read/write/synchronize only; each public pipe
  name has exactly one reconnecting server instance, so clients never receive
  `FILE_CREATE_PIPE_INSTANCE`/`CreateNewInstance`;
- local-client, same-user SID, process creation time, integrity level, PID,
  server nonce, and per-instance authentication-token validation. Executable
  signing is checked separately when selecting a Python runtime for generated
  client launchers; it is not part of per-request pipe authentication;
- length-prefixed UTF-8 frames with byte limits, strict envelopes, duplicate
  property rejection, JSON depth limits, and request/response ID matching;
- immutable target identity: PID, process creation time, server instance ID,
  debug session generation, break sequence, and context epoch;
- bounded command queue and lifetime request-ID tombstones;
- no automatic replay of mutating commands after a transport failure;
- an accepted command remains owned until its Visual Studio operation reaches a
  definitive result. `get_command_status` is the recovery path after the client
  receives an unknown outcome;
- session-scoped capability grants. Read-only startup grants only
  `server.control` and `debug.read`; every additional capability is selected and
  confirmed explicitly.

The package does not auto-start the server and does not modify MCP client
configuration during normal Visual Studio startup. Setup and removal are
explicit Tools-menu actions with per-client preview and confirmation.

## Capability contract

The machine-readable source of truth is
`mcp_server/debug_method_manifest.json`. C# and Python validate against the same
manifest at build/test time. Public FastMCP tools also have strict output
models in `mcp_server/output_models.py`.

Important semantic differences:

| Area | Visual Studio | Rider |
|---|---|---|
| Output | Multiple named panes | Debug console only |
| Watch | Persists in the VS Watch window | Evaluate-only |
| Same-session process selection | Enumerates `Debugger.DebuggedProcesses`; `switch_process` selects an existing PID while paused | No equivalent multi-process selection contract exposed |
| Source location | Official debugger document context; explicit unavailable status when absent | Rider debugger position APIs |
| Native/dump inspection | VS-specific native, memory, disassembly, module, and dump workflows | Capability-dependent and more limited |
| Command recovery | AIDB/2 request ledger and exact target handle | Rider-specific transport semantics |

Clients must read `debug_capabilities` and each result's `capability`,
`behavior`, `partial`, and snapshot fields. They must not infer parity from a
shared tool name.

## Source and snapshot truthfulness

Call-stack source paths and statement ranges come from the official Visual
Studio debugger interop document context. Editor cursor state and module paths
are not used as substitutes. A frame reports `sourceLocationStatus` and omits
default file/line fields when no authoritative context exists.

Multi-slice results report snapshot stability. Debug context handles become
stale after continue, step, stop, process/thread/frame changes, unsafe
evaluation, or a new debug session. A stale handle produces a typed error instead of silently
reading a different context.

## Validation gates

The supported release gate is:

1. C# protocol/service unit tests;
2. isolated Python unit/contract tests without a live user Visual Studio;
3. Release VSIX build;
4. `tools/Verify-Vsix.ps1` for split package identity, payload allowlist,
   hashes, dependency manifest, and version consistency;
5. the Windows Visual Studio Experimental Instance smoke workflow, including
   exact top-frame source path and marker-line validation.

Static source-string assertions are compatibility guards only. They are not a
substitute for pipe-security, actor-ordering, timeout, output-snapshot, or live
debugger behavior tests.

## Remaining platform boundary

Visual Studio debugger engines can legitimately omit source documents,
statement ranges, symbols, native memory, or function-evaluation support. Such
cases must return an explicit unavailable/unsupported/partial contract. They
must not be synthesized from editor state or reported as a successful complete
snapshot.
