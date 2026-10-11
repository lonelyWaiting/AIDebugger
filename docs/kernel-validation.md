# Kernel integration validation ledger

Date: 2026-10-10 (Asia/Shanghai). This ledger distinguishes implementation and
integration proof from live kernel acceptance.

## Implemented and exercised

- Independent kernel supervisor, private inherited-pipe workers, direct DbgEng
  ABI binding, explicit target/job IDs, bounded asynchronous operations.
- Restricted queries vs full commands/write; remote-only breakpoint/execution
  control; immutable mode and post-operation kernel target class checks.
- Portable contract tests for policy/injection, partial native reads, missing
  sessions, busy/running state, unknown outcomes, breakpoint lifetime, output
  pagination, native runtime ownership/rollback/obsolete-file removal, installer
  isolation and combined rollback. Existing VS/IDA tests pass with shared stdio.
- Fresh stdio MCP discovery exposes 14 kernel tools, including the deployed
  entrypoint outside the repository working directory in installer tests.
- Actual kernel Mini Dump open, BugCheck, module list, registers, stack,
  `!analyze -v`, present/missing memory, write/read/restore, dual isolated workers,
  restricted-session denial, 128 Ki character cap and output paging.
- Chinese/space dump path through `OpenDumpFileWide`.
- Review regressions: typed disassembly uses explicit hexadecimal addresses and
  lengths, remaining identical after `n 10`. Both public dumps passed using 16
  NOPs temporarily written to captured bytes in the offline engine view, then
  restored; no captured code page or matching symbols are assumed.
- Symbol paths use `IDebugSymbols3::SetSymbolPathWide`. A directory containing a
  supplementary Unicode character (`U+1F680`) opened through fresh source and
  deployed stdio processes; native `GetSymbolPathWide` returned the exact path.
- Full-file source dump hashes unchanged after engine memory writes and close.
- Actual native AddBreakpoint/GetId/RemoveBreakpoint interfaces exercised on an
  offline dump, separately from the public policy that restricts breakpoint tools
  to remote targets. This proves the ABI path, not a live breakpoint hit.
- Real no-peer KDNET wait: cached polling remains responsive, duplicate endpoint
  rejected even with a different key, close completes within bounded grace,
  owned worker exits, and returned receipts omit the supplied key. This engine
  required worker closure and returned `nativeCloseConfirmed=False`,
  `targetStateKnown=False`, `targetResumed=False`.
- Kernel-only deployment to Codex/Claude; parsed configuration comparison to
  pre-install backups confirms every unrelated setting and VS/IDA registration
  unchanged. Deployed file hashes match the kernel install manifest.
- Store WinDbg direct DLL load reproduced WinError 5. Preparing unchanged DLL
  copies from the registered installation enabled actual DbgEng/extension use;
  the cache records and verifies original per-file SHA-256.

Final commands and counts are recorded alongside the local reports. Full Python
suite: **311 tests passed**. Focused kernel + IDA suite: **60 tests passed**.
`git diff --check` and Python compile checks passed. No VSIX/C# changes were made;
no VSIX build, installation or live VS attach is claimed by this work.

## Review follow-up: 2026-10-10

The review of `5e58ec7` added nine portable regressions for late replies during
close, failed close/startup writes, unknown-outcome snapshot invalidation, exited
workers, strict interrupt flags, close receipt ordering on EOF/native errors,
and reserved close capacity when interrupts fill the pending-job limit.
Each regression failed before its corresponding repair. Closing is terminal for
new requests; helper cleanup owns the final close receipt when native closure
cannot be confirmed. Session listing also holds the history lock while collecting
statuses so concurrent history eviction cannot turn a list into SESSION_NOT_FOUND.

Fresh source stdio probes passed against both existing kernel dumps. The no-peer
KDNET probe also confirmed that query/interrupt/duplicate close are rejected once
closing starts, polling remains responsive, and the owned helper exits. Forced
closure still reports unknown target state; local attach still returns E_NOTIMPL.
These checks do not establish live-target execution or matching-PDB acceptance.

Local reports: `.tmp/kernel/review-fix-smoke.json`,
`.tmp/kernel/review-fix-smoke2.json`, and `.tmp/kernel/review-fix-lifecycle.json`.
Final checks: **320 full-suite tests passed**, including **36 focused kernel
tests**; Python compile checks and `git diff --check` passed.
The installed client configuration/runtime was not changed by this review.

## Actual sample identity and local evidence

Performance follow-up: the idle owner now sleeps on its queue, and a known
running state enters native `WaitForEvent` without the prior 50 ms queue timeout.
Portable scheduling probes distinguish these changes from live step latency.
Fresh source dump and no-peer lifecycle probes passed again in
`.tmp/kernel/performance-smoke.json` and `.tmp/kernel/performance-lifecycle.json`;
dump bytes remain unchanged and the no-peer helper exits with unknown target-state
receipts. Final combined Python suite: **327 tests passed**. No connected live
kernel was available to measure actual breakpoint/step latency.

Public binary fixtures were downloaded into ignored `.tmp/kernel/` from the
upstream author's sample directory, used as data only, and are not redistributed:

| Upstream fixture | Bytes | SHA-256 |
|---|---:|---|
| [081226-2187-01.dmp](https://github.com/glslang/windbg-mcp/blob/main/docs/samples/081226-2187-01.dmp) | 687172 | `b5dd69ce5de73e71e5659f3611898e6d820ec3d40d73e5109c217bfa0ae93c48` |
| [052126-34312-01.dmp](https://github.com/glslang/windbg-mcp/blob/main/docs/samples/052126-34312-01.dmp) | 5842659 | `29781dccd11d0d02e18ea38559de6ef9ad1abfd733bd9635d519b13b736cd26b` |

Actual reports identify BugCheck `0x13A` and `0x9F`, respectively. System32
DbgEng `10.0.26100.1` exercised basic reads. Full extensions use the installed
Microsoft WinDbg package `Microsoft.WinDbg_1.2610.1001.0_x64__8wekyb3d8bbwe`;
the prepared runtime's dbghelp/extension chain reports `10.0.29661.1004`.
Runtime preparation and deployed ownership manifests are under
`~/.ai-debugger/debugging-tools/x64/aidb-runtime.json` and
`~/.ai-debugger/kernel/install-manifest.json`.

Ignored local reports (not portable artifacts):

- `.tmp/kernel/stdio-smoke.json`: first actual native/stdio dump probe.
- `.tmp/kernel/stdio-symbol-smoke2.json`: explicit symbol-server path probe.
- `.tmp/kernel/unicode-smoke.json`: Chinese/space path, memory write/restore.
- `.tmp/kernel/deployed-final-smoke.json`: final deployed entrypoint acceptance.
- `.tmp/kernel/deployed-final-lifecycle.json`: final no-peer wait/closure probe.
- `.tmp/kernel/review-fixed-smoke2.json`: second dump with review regressions.
- `.tmp/kernel/review-fixed-deployed-smoke.json`: redeployed review regressions.
- `.tmp/kernel/review-native-symbols.json`: native UTF-16 symbol-path round trip.
- `.tmp/kernel/review-fixed-deployed-lifecycle.json`: redeployed no-peer closure.

## Open acceptance boundaries

Common-operation performance follow-up (2026-10-10): 30 samples per successful
operation via a fresh source stdio process and real DbgEng. These are warmed
kernel-dump operations, including request submission and completed-result
observation, not native execution time alone. The same session compares client
poll intervals and completion-triggered short waits:

| Operation | 50 ms polling | 5 ms polling | `wait_ms=10` |
|---|---:|---:|---:|
| Registers | 51.716 ms | 6.049 ms | 0.539 ms |
| Stack (raw offsets; matching PDB unverified) | 51.349 ms | 6.040 ms | 0.701 ms |
| Read 64 captured bytes | 51.398 ms | 6.028 ms | 0.483 ms |

The improvement removes client observation delay; it does not claim an equally
large increase in DbgEng compute throughput. Existing immediate-poll behavior is
unchanged unless the caller opts into `wait_ms`. Both sample dumps returned empty
`!thread` output with this runtime: thread-context evidence is marked unavailable,
not counted as a successful fast query. Missing kernel symbols/pages were not
repaired or inferred from these measurements.

Reports: `.tmp/performance/common-operations-final-30.json`,
`.tmp/performance/kernel-second-dump.json`,
`.tmp/kernel/common-operations-smoke.json`,
`.tmp/kernel/common-operations-lifecycle.json`. Native smoke and no-peer lifecycle
probes passed again, preserving dump hashes and unknown-state close receipts.
Five IDA context and three kernel wait/event-sequence regressions passed; the
full Python suite passed **335 tests**. The new event-sequence rule is exercised
by portable tests; actual remote step latency still needs a configured test
kernel. Installed runtime/client configuration remains unchanged.

- **No real remote kernel target was available.** KDNET connection to an unused
  local port exercises waiting/cancellation, not connection to a target. Actual
  driver breakpoint hits, step/resume/break behavior, physical/processor context
  correctness, reconnect after target reboot, and repeated/cold live sessions
  remain unverified. COM/USB/1394 transport support is delegated to DbgEng and
  has not been exercised.
- Local kernel attach on this host returns `0x80004001` (E_NOTIMPL) through the
  prepared engine. The process is not elevated. This is a verified unsupported
  attempt, not proof of successful local inspection. No boot/elevation changes
  or target crashes were performed to create test data.
- **Matching kernel PDB/type analysis remains unverified.** `!analyze` executes
  and interprets the BugCheck but reports missing/wrong kernel symbols on these
  samples; stack evidence includes raw module offsets. An explicit Microsoft
  symbol-server path probe did not produce matching symbols. Do not use this
  result as a verified crash root cause or complete symbolized stack.
- The current chat has not reloaded the newly registered MCP. Fresh stdio
  processes and deployed file/configuration checks prove the server works;
  reconnect/restart the client to expose the tools in its active session.

For live acceptance, run `tests/kernel_integration/run_kernel_smoke.py` with an
already configured target connection and explicit `--live-control`. Preserve its
report separately from dump proof. For a no-target lifecycle reproduction use
`tests/kernel_integration/run_kernel_lifecycle.py`.
