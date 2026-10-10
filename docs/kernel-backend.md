# Windows kernel debugging

AIDebugger has three independent MCP entrypoints. The kernel backend uses the
public Microsoft DbgEng interfaces directly, without requiring a third-party
MCP server or loading IDAPython into the debugger process.

```text
MCP client
  ├─ AIDebuggerVS → VS session manager → authenticated AIDB/2 → VSIX
  ├─ ida         → IDA session manager → private HTTP       → IDA/IDB
  └─ kernel      → kernel supervisor  → inherited pipes    → DbgEng worker
                                                              one per target
```

## Ownership and capability layers

| Responsibility | Owner |
|---|---|
| Source/PDB user-mode debugger, managed locals, VS project/output | VS |
| Driver disassembly/decompilation, xrefs/types, persistent IDB/patch export | IDA |
| Kernel dumps, remote kernel execution, kernel memory/context/extensions | Kernel |
| Stdio dispatch and generic vendored JSON-RPC/MCP code | Shared transport only |
| Client registration, backups, hash ownership and rollback | Project installer |

The kernel deployment shares the existing vendored generic MCP implementation
(and its license/provenance records), not IDA's worker, session manager, or
debugger. It imports neither VS debugger state nor IDAPython. VSIX setup remains
VS-only. Each kernel session requires an explicit UUID; a failed request never
falls back to another target. Remote endpoint ownership is reserved while its
worker lives; changing the KDNET key cannot create a competing session for the
same endpoint within this supervisor. This reservation does not discover other
independent debugger applications or other MCP server processes.

| Mode | Restricted query/read | Breakpoint/step/resume | Full commands/write |
|---|---|---|---|
| Kernel dump | Yes, limited to captured pages | Unavailable | Explicit `full_access=True`; source dump is not an export destination |
| Local kernel | Yes; no register/stack/breakpoint commands | Unavailable | Unavailable |
| Remote kernel | When stopped and available | Explicit `full_access=True` | Explicit `full_access=True` |

`kernel_query` accepts fixed queries and narrowly validated disassembly addresses.
It cannot accept arbitrary debugger expressions or command fragments. Memory
reads return `bytesRead`, `partial`, `status`, `dataHex` and HRESULT. Missing dump
memory is unavailable evidence, not proof that an object never existed.

`kernel_execute` is an explicitly granted full-access escape hatch for symbolic
or data breakpoints, processor/process context, symbol/type queries and extension
commands. It can modify kernel memory, load DLL extensions and access host files;
it is not a read-only command sandbox. Direct target replacement, quit and reboot
commands are rejected as accidental lifecycle misuse. Arbitrary scripts/aliases
are not a security boundary. Native target class/qualifier is checked after each
operation, but a full-access script can have side effects before that check.

Dump identity is a canonical path, file size, mtime and **header** SHA-256,
rechecked in the worker. It deliberately does not hash multi-GB dumps on an MCP
request thread. Full-file SHA-256 preservation is a separate integration check.
Remote identity refers to the requested transport, not an independently verified
physical-machine identity. Correlate module hashes/build identity before joining
kernel addresses to IDA (`RVA = runtime address - loaded module base`). Kernel
debugging and user-mode debugging have different ownership models: the existing
one-user-debugger-per-PID restriction must not be applied to a whole kernel target.

## Installation

Requires Windows x64 Python 3.11+ and separately installed Microsoft WinDbg or
Windows Debugging Tools. No native debugger binaries are redistributed.

```powershell
# Default deploys IDA + kernel together, preserving VS registration.
Install-AIDebugger.cmd --dry-run
Install-AIDebugger.cmd

# Add only kernel, leaving existing IDA registration/runtime/skill untouched.
Install-AIDebugger.cmd --backend kernel
```

Kernel files deploy to `~/.ai-debugger/kernel`. Registrations use the `kernel`
name in Codex and Claude; each backend retains its own install manifest. Combined
installation is one rollback transaction. Edited/unowned files and concurrent
configuration edits are preserved. `--client codex|claude|all` and `CODEX_HOME`
retain their existing meanings. Restart/reconnect the MCP client after deployment.

Engine discovery uses `AIDB_DBGENG_DIR` first, then the prepared user runtime,
Windows SDK x64 Debuggers, then system32. System32 DbgEng may support basic dump
reads without kernel extensions; extension-dependent queries fail explicitly
when the required runtime is absent.
The default symbol path is the local `~/.ai-debugger/symbols` directory. Network
symbol-server lookup is explicit through `symbol_path`, avoiding DbgEng's implicit
`srv*` default and its potentially lengthy network waits.

Store WinDbg's packaged DLLs can refuse direct loading by an external Python
process. Prepare unchanged copies from the registered Microsoft package:

```powershell
python tools/prepare_dbgeng.py
# Or select an existing x64 debugger installation explicitly:
python tools/prepare_dbgeng.py --source "C:/path/to/amd64"
```

This prepares `~/.ai-debugger/debugging-tools/x64`, copies DLLs and `winext`, and
records original paths/SHA-256 in `aidb-runtime.json`. It does not modify WinDbg,
elevate, change boot settings, or install a driver. Preparation is rollback-safe
and refuses local edits. Prepared DLL tree/hashes are verified before loading.
For another location, use `--destination` and set `AIDB_DBGENG_DIR` for the MCP
server. Installed-runtime provenance and matching DLL dependencies matter; do not
mix unrelated dbgeng/dbghelp/symsrv/extension versions.

## Tool workflow

There are 14 tools: open/connect, session list/status, job status, restricted
query, read/write memory, set/remove breakpoint, full command, execution control,
interrupt, and close. Opening or submitting an operation returns a job UUID
immediately. Poll `kernel_job_status` with short intervals; `nextOffset` pages
output. A completed control job acknowledges a request; only session status and
the following debug event establish the next stop.

```python
opened = kernel_open_dump(dump_path="D:/Dumps/kernel.dmp",
    symbol_path="srv*C:/Symbols*https://msdl.microsoft.com/download/symbols")
# Poll kernel_job_status(opened['sessionId'], opened['jobId']).
query = kernel_query(opened['sessionId'], "analyze")
# Poll query['jobId']; check symbol warnings and textual command errors.

remote = kernel_connect(connection="net:port=50000,key=<target-key>",
    full_access=True, symbol_path="srv*C:/Symbols*https://msdl.microsoft.com/download/symbols")
# Requires a target whose kernel debugging transport is already configured.
# After its initial stop: kernel_set_breakpoint(session_id, "fffff80012345678").
# kernel_control(session_id, "continue"), then kernel_interrupt(session_id).
```

DbgEng `Execute` can return S_OK while a command prints an error. Text responses
therefore report `semanticSuccess="unknown"`; read the output and symbol status.
Output retains at most 128 Ki characters per job, pages at most 16 Ki characters,
and retains 64 jobs/session and eight bounded asynchronous event outputs. A
truncated result is incomplete evidence. At most eight workers can be alive;
closed history is bounded to 32 sessions.

All ordinary COM calls, including `WaitForEvent`, run on the thread that owns
the engine. A reader thread uses only the documented cross-thread `SetInterrupt`.
Live kernel `WaitForEvent` requires INFINITE, so it runs in an isolated helper,
never on an MCP request thread. The MCP supervisor remains available for polling
and interrupt/close while a native call blocks.

`kernel_interrupt(cancel_wait=True)` requests exit from a wait without promising
that the target stopped. Some engine builds do not promptly interrupt initial
attachment; `kernel_close` provides bounded helper shutdown. It first requests
passive native detach. After two seconds it closes only its owned worker, with
`nativeCloseConfirmed=False` and `targetStateKnown=False`. No target process is
killed and no kernel is implicitly resumed. A paused remote kernel can remain
paused after close; explicitly continue it first if that is the desired outcome.
Mutations are never retried. A lost worker reply is an unknown operation outcome.

Local kernel inspection requires the relevant Windows permissions/configuration;
this backend does not request elevation or silently enable boot debugging.
Remote KDNET/COM/USB/1394 is passed to DbgEng for validation and support. Transport
availability depends on the installed engine/Windows version; it is not a claim
that all four have been exercised. KDNET keys travel through private inherited
pipes, not command-line arguments, status or receipts; output/error redaction
removes the supplied key. Connection parameters passed to MCP are still visible
in the invoking client's own tool history.

## Validation and evidence boundaries

```powershell
python -m unittest tests.test_kernel_backend tests.test_ida_backend -q
python -m unittest discover -s tests -p "test_*.py" -q
python tests/kernel_integration/run_kernel_smoke.py --dump D:/Dumps/kernel.dmp `
    --report .tmp/kernel/smoke.json
```

The smoke launches a **fresh stdio MCP process**, opens two isolated sessions,
executes actual DbgEng queries/extensions, reads present and absent memory, tests
access restrictions/output cap/paging/passive close, and verifies source dump
bytes. Add `--server` to test a deployed entrypoint, `--symbol-path` for symbols,
and `--connection` for a configured remote kernel. `--live-control` explicitly
adds step/resume/break checks. Its omission avoids execution changes.

See [the integration validation ledger](kernel-validation.md). Portable contract
tests, real dump/native ABI proof, loaded matching PDB/type proof, and live
kernel breakpoint/step/reconnect proof are separate boundaries.

Primary API references: [Microsoft WinSDK DbgEng.h](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/DbgEng.h),
[AttachKernel](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/dbgeng/nf-dbgeng-idebugclient5-attachkernel),
[WaitForEvent](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/dbgeng/nf-dbgeng-idebugcontrol-waitforevent),
[SetInterrupt](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/dbgeng/nf-dbgeng-idebugcontrol-setinterrupt),
[breakpoint object lifetime](https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger/setting-breakpoints),
[local-kernel limitations](https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger/performing-local-kernel-debugging).
