# Visual Studio / Rider capability alignment

Last verified: 2026-07-23

This document describes the current public contracts. It is not a historical
security review and must not be used as evidence for older AIDB/1 builds.

## Runtime separation

Visual Studio and Rider use separate MCP entry points and discovery paths.
The Visual Studio package deploys only the VS runtime (`vs_server.py`), which
does not import, discover, select, or fail over to Rider. The combined
development entry remains available for explicit cross-IDE use, but it is not
the VSIX production path.

```text
VSIX -> AIDebuggerVS_<PID> + _control + _recovery
     -> authenticated AIDB/2 -> VS-only FastMCP server

Rider plugin -> AIDebuggerRider_<PID>
             -> authenticated AIDB/2 -> Rider FastMCP server
```

The `_control` and `_recovery` pipes are paired with one VS instance; neither is
a separately selectable instance. Interrupt operations use `_control`, while
immutable dispatcher health, request-status, and cancellation recovery remain
available on `_recovery`. Debugger and solution state use the ordinary pipe and
the per-instance actor.

## Visual Studio security and command semantics

The current VS transport has these mandatory properties:

- protected current-user DACL with read/write/synchronize/create-instance for
  the current user SID only. The create-instance right is required for the
  configured 8 data, 4 interrupt, and 2 recovery listeners sharing each lane
  name; no world, permission-change, or ownership right is granted;
- local-client, same-user SID, process creation time, integrity level, PID,
  server nonce, and per-instance authentication-token validation. Executable
  signing is checked separately when selecting a Python runtime for generated
  client launchers; it is not part of per-request pipe authentication;
- length-prefixed UTF-8 frames with byte limits, strict envelopes, duplicate
  property rejection, JSON depth limits, and request/response ID matching;
- immutable target identity: PID, process creation time, server instance ID,
  protocol catalog hash, debug session generation, break sequence, and context
  epoch. A VSIX/Python catalog mismatch is rejected during discovery and on
  every response;
- bounded command queue and UUIDv7 request-ID tombstones covering a 24-hour
  acceptance window; old IDs are rejected before execution and completed
  tombstones can then be reclaimed safely;
- no automatic replay of mutating commands after a transport failure;
- an accepted command remains owned until its Visual Studio operation reaches a
  definitive result. `get_command_status` is the recovery path after the client
  receives an unknown outcome;
- queries that finish after their transport deadline enter a bounded five-minute
  result journal and can be recovered through `get_command_status` instead of
  being executed again;
- side effects pass through one structural `CommandEffectExecutor` boundary,
  which records `issued` before the external call and `confirmed` only after it
  returns successfully;
- UI/COM dispatch ownership is process-wide. An unresolved admitted operation
  transitions from `draining_unknown` to `poisoned`; `get_dispatcher_health`
  remains available with actor/listener/ledger/journal capacity metrics, and
  recovery requires restarting Visual Studio, not merely restarting the MCP
  server;
- session-scoped capability grants. Read-only startup grants only
  `server.control` and `debug.read`; every additional capability is selected and
  confirmed explicitly.

The package does not auto-start the server and does not modify MCP client
configuration during normal Visual Studio startup. Setup and removal are
explicit Tools-menu actions with per-client preview and confirmation.

## Rider security and command semantics

Rider now shares the transport invariants that prevent stale-target execution
and command replay:

- protected current-user owner-only pipe and atomic instance-registration ACLs;
- owner-registration discovery, a 256-bit per-instance token, Rider PID/process
  creation time, server nonce, client PID/process creation time, and response
  identity validation;
- the same manifest SHA-256 catalog hash as Python and Visual Studio;
- four-byte big-endian frames, strict UTF-8, duplicate-property and JSON-depth
  rejection, bounded requests/responses, typed errors, and response-ID checks;
- a bounded single actor around every live `DebuggerService` operation, removing
  the previous cross-connection thread/frame selection race;
- UUIDv7 command IDs, fingerprint conflicts, 24-hour fail-closed tombstones,
  bounded response bodies, no transport replay, and `OUTCOME_UNKNOWN` plus
  `get_command_status` recovery;
- direct health/status and queued-cancellation handling outside the debugger
  actor, so a slow Rider debugger call does not hide command ownership.

Rider does not yet mirror the VS process-wide UI/COM poison circuit or its
five-minute late-query journal. Running debugger calls are conservatively marked
outcome-unknown on deadline and are allowed to finish under the single actor;
queued calls can be cancelled before execution.

## Capability contract

The machine-readable source of truth is
`mcp_server/debug_method_manifest.json`. C# and Python load that same manifest,
publish its SHA-256 catalog hash, and fail closed if deployed hashes differ.
Parameter range semantics have one C# owner (`DebugRequestSchemaValidator`);
the method router performs only lossless extraction after schema preflight.
Public FastMCP tools also have strict output
models in `mcp_server/output_models.py`.

Important semantic differences:

| Area | Visual Studio | Rider |
|---|---|---|
| Output | Multiple named panes | Debug console only |
| Watch | Persists in the VS Watch window | Evaluate-only |
| Same-session process selection | Enumerates `Debugger.DebuggedProcesses`; `switch_process` selects an opaque session/PID/start-time handle while paused (legacy PID fallback remains) | No equivalent multi-process selection contract exposed |
| Source location | Official debugger document context; explicit unavailable status when absent | Rider debugger position APIs |
| Native/dump inspection | VS-specific native, memory, disassembly, module, and dump workflows | Capability-dependent and more limited |
| Command recovery | AIDB/2 request ledger, exact target handle, late-query journal, UI/COM poison circuit | AIDB/2 request ledger, exact process/instance target, single actor; no late-query journal or poison circuit |

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
