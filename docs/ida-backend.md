# IDA integration

AIDebugger has independent Visual Studio, IDA and kernel MCP entrypoints. The VSIX
remains responsible for Visual Studio and its authenticated AIDB/2 transport.
IDA analysis, patching, process ownership and IDB lifetime belong to `ida_backend/`.
These backends do not import each other's debugger implementation. The kernel
backend owns DbgEng and kernel targets; see [kernel integration](kernel-backend.md).

```text
MCP client ── AIDebuggerVS stdio ── AIDB/2 pipe ── VSIX / Visual Studio
           └─ ida stdio ── IDA session manager ── private authenticated HTTP
                                                ├─ idat / isolated DLL A IDB
                                                ├─ idat / isolated DLL B IDB
                                                └─ optional ida.exe GUI
```

| Need | Backend |
|---|---|
| Source lines, PDB, managed locals, project build errors, existing VS crash scene | VS |
| No-source disassembly, Hex-Rays, xrefs, signatures, types, persistent IDB edits | IDA |
| IDB byte/assembly patches and a separately exported binary | IDA |
| Native registers, memory, breakpoints, stepping | Either; choose one debugger per PID |
| VS live debugging plus static analysis of that module | Both concurrently |
| Dynamic debugging of two different processes | Independent VS and IDA sessions |

A Windows process cannot have two simultaneous user-mode debug attachments.
Detach before handing a PID to another debugger. `debugger_attach` refuses a
process already held by a debugger. Correlate module hashes, then use
`RVA = IDA address - IDA imageBase` and `runtime address = module load base + RVA`;
never equate addresses just because both tools show the same DLL name.

## Installation and source ownership

From the repository, run in a normal user terminal:

```powershell
Install-AIDebugger.cmd --dry-run
Install-AIDebugger.cmd
# Or: python tools/install_aidebugger.py --client claude
# Or: python tools/install_aidebugger.py --client codex
```

The project installer defaults to deploying IDA and kernel in one transaction.
Use `--backend ida` for an IDA-only update. It deploys IDA runtime sources to `~/.ai-debugger/ida/`,
registers the `ida` stdio MCP in Claude Code, Claude Desktop and Codex, and
installs the project-owned `skills/ida-evidence-analysis/` into the selected
clients' skill directories. Codex configuration and Skills use `CODEX_HOME`
when set, otherwise `~/.codex`. It migrates the former `ida` HTTP registration;
it does not edit the existing AIDebuggerVS entry or the old global IDA plugin.
Configurations are backed up and file writes are rolled back if installation
fails. Concurrent edits abort installation and are preserved, including edits
made after an installer write during rollback. Modified/unowned runtime or
Skill files are preserved and reported.
Run the same command after updating project sources.

The VS component still uses its existing VSIX installer and Tools-menu setup.
Install it separately with `VSDebuggerMCP/Install-VSIX.cmd -Target ...`, following
its existing rollback-artifact requirements for upgrades. IDA deployment does
not require building or installing a VSIX.

Requires a separately installed, licensed IDA with IDAPython and `idat.exe`.
Decompiler and debugger availability depends on that installation/license.
No IDA binaries or license are redistributed; IDAPython's existing Python
binding is retained. The independent stdio supervisor requires Python 3.11+
and uses vendored pure-Python transport, without pip or IDALib setup.

Set `AIDB_IDA_DIR` to an IDA installation directory if autodiscovery is
ambiguous. Set `AIDB_IDA_WORK_ROOT` for session storage. Restart the MCP client
or create a fresh session after changing registration; an already-connected
HTTP MCP continues using its old connection.

Upstream plugin source is vendored from the same local `mrexodia/ida-pro-mcp`
2.0.0 archive as the previously installed Claude plugin. Its Python sources
matched the installed plugin byte-for-byte before migration. The archive did
not identify its exact git commit. Original hashes and MIT license are retained
in `ida_backend/vendor/`; `LOCAL_CHANGES.md` records the one local pump change.
This is a source migration, not a remote update to an unpinned latest release.

## Workflow

1. `ida_open(input_path, mode="headless")` returns an owned `sessionId` promptly.
   `headless` runs `idat` with `TVHEADLESS=1`, not a minimized GUI. `mode="gui"`
   starts a separate GUI. One copied database, process and private authenticated
   loopback endpoint per session; the original binary/IDB is not writable storage.
2. Short-poll `ida_session_status(session_id)` until ready. Ready means tools
   can run; `identity.autoAnalysisReady` separately reports automatic analysis.
   A saved IDB can be reused without forcing a full reanalysis.
3. `ida_list_tools(session_id, offset, count)` returns exact operation schemas.
   Use `ida_call_tool(session_id, tool, arguments)` for migrated analysis,
   debugger, type, rename, memory, patch and Python operations. Multiple sessions
   have separate locks; stdio dispatch allows another session to proceed while
   one is busy. There is no implicit current database or port-13337 takeover.
4. `debugger_configure` selects the debugger and target EXE before `dbg_start`;
   `debugger_attach` selects an already-running PID. A DLL needs a loading EXE
   or appropriate process to debug it. Read `dbg_status` and actual events to
   confirm transitions; a request being accepted is not proof of completion.
   `debugger_detach` preserves the target; `dbg_exit` terminates it.
5. `patch` / `patch_asm` edit IDB bytes. `export_patched_binary(output_path)`
   writes only a new destination after checking the input SHA-256, file mapping
   and each original byte. Unmapped patches, changed source files and existing
   destinations are rejected. The export changes signature validity and still
   requires separate runtime validation.
6. `ida_close(session_id, save=True)` checks there is no active debugger and
   confirms saving before exit. Short-poll until closed. If native GUI shutdown
   leaves the owned process alive, status polling finishes that acknowledged
   close after a grace interval using the original process handle, and records
   it in `closeReceipt`. Save failure and transport uncertainty never authorize
   this fallback. `save=False` can explicitly cancel a never-ready startup.
   On normal stdio disconnect, the supervisor also finishes acknowledged closes
   with bounded status polling; client-side polling is not required for cleanup.

Tools preserve upstream `content`, `structuredContent`, `isError`, cursors and
per-item errors. For truncated outputs, use `_meta.ida_mcp.output_id` with the
`read_cached_output` operation to read bounded JSON text pages. Anonymous curl
download hints cannot access a private authenticated endpoint.

Transport timeout on a command returns `IDA_OUTCOME_UNKNOWN`; the operation
may still be running. Never replay a mutation or kill/restart an instance merely
because a response was lost. The supervisor watches owned process handles;
on client exit workers save and close when not debugging. A live debugging
target is preserved if its client disappears.

## Validation

### Request admission and performance validation (2026-10-10)

Input copy/hash verification runs outside the global session lock. A reserved
opening slot still enforces the session limit and is released on startup failure.
Calls, tool discovery and close do not queue behind another operation on the same
session: they return `IDA_BUSY_NOT_STARTED` with `requestStarted=false`. Poll
status before explicitly submitting another operation; mutations are never
automatically retried. Every accepted tool call still validates the live worker,
input hash and database identity. `timeout_seconds` includes that preflight;
`IDA_DEADLINE_EXCEEDED_NOT_STARTED` means no tool command was dispatched.

The real IDA/stdio contention probe starts two independent headless IDA sessions
on isolated system DLL copies. A one-second `py_eval` sleep occupies one owner
thread while eight same-session read-only calls arrive. Three trials alternate
baseline/current order; medians below exclude startup and do not measure analysis
throughput or VS performance.

| Operation under contention | `5e58ec7` baseline | Current source |
|---|---:|---:|
| Enumerate sessions | 1023.712 ms | 2.768 ms |
| Other session's actual `server_health` | 1026.783 ms | 33.873 ms |

All 24 overlapping calls in current-source trials were explicitly rejected
before starting, rather than reported as completed queries. Source hashes were
preserved and all owned workers exited. Report:
`.tmp/performance/ida-contention-3trials.json`. Reproduce with:

```powershell
python tests/ida_integration/run_ida_performance.py --compare-ref 5e58ec7 `
    --report .tmp/performance/ida-contention.json
```

Seven new portable regressions cover global-lock isolation, opening reservations,
nonblocking same-session admission, preflight deadlines and kernel owner-thread
scheduling. Six behavior tests fail against the old implementations and pass
against current source. Final full suite: **327 tests passed**. Real headless IDA
smoke also passed decompilation, isolated patch/export, save/reopen and unchanged
source hashes (`.tmp/ida-smoke/1791632610186711200/evidence.json`). No active VS
instance was available; no VS latency improvement or live kernel step timing is
claimed. Installed client configuration/runtime was not updated.

```powershell
python -m unittest discover -s tests -p "test_*.py"
python tests/ida_integration/run_ida_smoke.py
python tests/ida_integration/run_ida_smoke.py --mode gui
python tests/ida_integration/run_ida_shutdown_smoke.py --mode gui
python tests/ida_integration/run_ida_shutdown_smoke.py --mode headless
python tests/ida_integration/run_ida_debug_smoke.py
python tests/ida_integration/run_dual_mcp_smoke.py
python tests/ida_integration/run_dual_mcp_smoke.py --launch-vs
python tests/ida_integration/run_dual_mcp_smoke.py --debug-vs
```

### Common debugger operations (2026-10-10)

Discover `debugger_context` through `ida_list_tools`, then invoke it through
`ida_call_tool`. It reads paused status, bounded thread IDs, current registers
and stack within one owning-thread operation and one live identity preflight.
It does not resume the target or cache state. `max_threads` defaults to 128 and
`max_frames` to 64. Thread enumeration and stack symbol/module resolution stop
at these bounds; stack unwinding still uses IDA's native collector. Check
`snapshotStable`, `stackStatus`, `stackError` and truncation fields. Missing
stack evidence is explicitly unavailable. A running debugger is rejected.

`tests/run_debug_performance.py` builds and launches only its own C++ fixture,
runs to its exported probe, and measures fresh source MCP stdio requests.
Step timings separately record acknowledgement and a subsequent suspended IP
different from the pre-step IP. This controlled loop does not stand in for
stepping over expensive application calls, source-line stepping or kernel steps.
Context comparisons pair and alternate the two paths at the same native pause:
three separate status/register/stack requests versus the bundle, which also
includes thread IDs and consistency checks. Registers for all threads, selected
registers, stack and 64-byte memory reads are measured separately. Reports retain
raw samples, medians, nearest-rank p95, actual stop addresses and tool schemas.

Final isolated rerun: 30 samples per operation, milliseconds including stdio,
live identity preflight and completed-result observation:

| Operation | Median | p95 |
|---|---:|---:|
| Status + registers + stack as three requests | 57.810 | 92.654 |
| `debugger_context` (also thread IDs/consistency) | 30.659 | 46.839 |
| Current-thread registers | 27.280 | 34.490 |
| Registers for all fixture threads | 30.190 | 47.095 |
| Stack | 21.879 | 42.823 |
| Read 64 bytes | 19.909 | 39.672 |
| Instruction step into: fresh stop observed | 44.493 | 62.948 |
| Instruction step over: fresh stop observed | 37.328 | 64.669 |

Report: `.tmp/performance/ida-common-final.json`. Scheduling variation is visible
across reruns; the bundle reduces round trips, but no improvement in native
instruction execution time is claimed. The full suite passed **335 tests**,
including five context regressions; real native context and 60 total steps
passed after the final worker import change. GUI IDA remains an unrun gate.

Further review found that the first bundle implementation sliced only after
resolving every stack frame. The collector now resolves module/symbol context
only for the requested frame count. A 10,000-frame portable regression requests
two frames and verifies exactly two module and symbol lookups. Native unwind
failure retains registers and returns `stackStatus="unavailable"` plus its
`stackError`; individual symbol failures remain explicit in the affected frame.

A frozen source snapshot and the revised source each completed 30 measurements
on an owned recursive fixture. IDA actually unwound only two frames here, so
this is not proof of a complete 32-level native stack. With `max_frames=1`,
30 native helper calls performed 60 versus 30 module lookups; helper median was
0.827 versus 0.748 ms (p95 1.567 versus 1.261 ms). This excludes transport and
includes profiling overhead. End-to-end latency remains sensitive to scheduling;
the much larger differences between individual runs are not attributed to this
small reduction in native work. Reports:
`.tmp/performance/stack-bound-before.json` and
`.tmp/performance/stack-bound-after.json`. A separate TCP_NODELAY A/B trial did
not demonstrate a stable benefit, so that change was removed. Original vendor
sources, installed runtime/configuration and the IDA Skill remain unchanged.
Final full Python regression suite: **338 tests passed**; compile checks and
`git diff --check` passed. Both A/B workers saved/closed, fixture hashes were
preserved, and each run confirmed 60 actual instruction-step stops. No new
live VS or remote-kernel performance evidence was obtained in this follow-up.

```powershell
python tests/run_debug_performance.py --backend all --samples 30 `
    --dump .tmp/kernel/sample-kernel.dmp `
    --report .tmp/performance/common-operations.json
```

All measurements use separately installed licensed IDA and unchanged native
debugger dependencies. The owned debuggee exits, the IDB saves, the IDA worker
closes, and fixture hashes remain unchanged. Native headless context/stepping
proof is separate from GUI IDA, VS, and live kernel acceptance. No runtime/client
deployment is performed by the benchmark.

The opt-in live tests analyze isolated system DLL copies and debug a project-owned
native fixture. Evidence and logs go under `.tmp/`. The dual test uses freshly
initialized SDK clients against installed registrations; without `--launch-vs`,
a successful VS enumeration with no reachable instance proves transport
coexistence only, not a live VS debugger. Real GUI/headless execution, exported
bytes, saved IDB reopen, source hash preservation and debugger transitions are
checked independently from unit tests.

`--debug-vs` launches an owned VS, attaches it to the native fixture built by
the IDA debug smoke, pauses it, reads its callstack while IDA analyzes a DLL,
and verifies IDA refuses to attach to the VS-held PID. Both owned IDE instances
and the fixture are closed after the test; user-started IDEs are preserved.
