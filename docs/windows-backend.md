# Windows user-mode debugging

Install the standalone Windows minidump backend from a Windows x64 Python 3.11+
environment:

```powershell
.\Install-AIDebugger.cmd --backend windows --client codex
```

Start a new Codex chat after registration. The backend uses local x64 DbgEng,
discovered through `AIDB_DBGENG_DIR`, the prepared WinDbg runtime, Windows
Debugging Tools, or System32. The installer deploys Python code and the MCP
transport. See [DbgEng runtime preparation](kernel-backend.md#installation).

## Discover and inspect

The Windows and Android MCP servers each expose `debug_capabilities()` and
`debug_inspect()`. Start with the platform that owns the target. Discover the
available views and operations, then supply `domain="inspection"` or another
listed domain for exact direct tool names. The detailed tools remain listed by
MCP `tools/list` and can be called immediately.

| Server | Target reference | Views | Result |
| --- | --- | --- | --- |
| Windows | Local minidump path | `dump` | DbgEng output per command or a bounded memory read |
| Android | Explicit Android session ID | `native` (LLDB) or `java` (JDWP) | Job receipt, followed by `android_job_status` |

Use Windows `debug_inspect(target=<dump path>, query="triage")` for the exception
record, stack, modules and registers. Use `windows_dump_query` for an individual
query, a local PDB directory, a locals frame index or the saved exception context.
Triage reports whether stack and registers used the exception or default context.
An explicit exception-context request for stack, registers or locals fails when
the context is unavailable. The exception query requires a stored exception record.
`storedException` reports the thread ID, code and address from the dump's
ExceptionStream.
Locals require an available frame and matching symbols.
Use `windows_dump_read_memory` or `windows_dump_disassemble` with a virtual address.
Check each command's HRESULT, `debuggerError` and output. DbgEng can emit
diagnostics while returning a successful HRESULT. DbgEng can fill image-backed ranges from local
modules, so a successful virtual-memory read does not establish that the bytes
were physically stored in the dump.

## Backend boundaries and VS migration coverage

`debug_backend/inspection.py` owns platform discovery and routes a supported
operation to the backend's inspection view. Windows and Kernel share native
bindings in `debug_backend/dbgeng.py`. Their workers, target validation, and
session ownership are independent. Each Windows dump request opens the named
file in an isolated worker, using a single owning thread with a 30-second
request deadline. Android owns its LLDB/JDWP session and stop identities.

| Existing workflow | Standalone path or migration gap |
| --- | --- |
| Windows native dump | DbgEng stack, threads, modules, registers, exception, locals, disassembly and bounded reads. Rich VS snapshot fields, address resolution and mapped-memory search still use VS. |
| Windows native live | VS still owns attach, launch, stepping, breakpoints, watches, expression evaluation and stop capture. A Windows live DbgEng adapter needs process identity, symbol and source resolution, stop events, breakpoint binding, and detach tests. |
| Windows managed and mixed-mode | VS still owns managed live debugging, mixed stacks and the managed metadata/IL tools. Port the standalone metadata broker separately from the live CLR adapter and validate mixed stops against the same process and stop. |
| VS project context | Build diagnostics and output need a build/process log provider. VS instance and editor operations remain in the VS integration. |
| Android and Windows kernel | Their existing independent backends provide these workflows. The Windows user dump tool accepts only user-mode minidumps. |

Remove the VS requirement for a workflow after its standalone replacement can
run the same use cases with equivalent target, stop and result evidence.
