# IDA integration

AIDebugger now has independent Visual Studio and IDA MCP entrypoints. The VSIX
remains responsible for Visual Studio and its authenticated AIDB/2 transport.
IDA analysis, patching, process ownership and IDB lifetime belong to `ida_backend/`.
Neither backend imports the other's debugger implementation.

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

The project installer deploys IDA runtime sources to `~/.ai-debugger/ida/`,
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
