# Routine debugger performance review

## Changes

VS pipe response reads now submit overlapped `ReadFile` directly instead of
sleeping 20 ms between `PeekNamedPipe` probes. Pending reads still use the request
deadline and cancel/reap their OVERLAPPED before releasing buffers and events.
Length, UTF-8, server identity, catalog and response ID validation are preserved.
Copying a short response no longer copies the entire 1 MiB scratch buffer.

VS startup activation is queried immediately after a successful start receipt.
Pending activation uses 50, 100, 200, then 250 ms intervals. The original command
is never replayed; exact target binding and the total deadline are retained.

`step_and_capture` keeps the current callstack and defaults other threads to
metadata only. Set `thread_include_frames=True` for their frames, or
`include_threads=False` to omit that list. Frame/node/time limits are exposed and
validated before issuing the step. At the VS backend, metadata-only thread
capture skips the complete stack depth query and reuses COM properties within
one UI slice. Module fallback collection still requests the frame count. Snapshot
boundary checks and current-location reporting are retained.
If the step was issued without an observed new stop, context collection is
skipped with `STEP_STOP_NOT_OBSERVED`; the original step receipt is preserved.

## Reproduction and evidence

```powershell
python -m unittest discover -s tests -p test_vs_pipe_reads.py
python tests/run_vs_pipe_performance.py --baseline-ref 82aa16b --samples 20 --report .tmp/performance/vs-pipe-read-comparison.json
python tests/run_debug_performance.py --backend all --samples 30 --dump .tmp/kernel/sample-kernel.dmp --report .tmp/performance/routine-debug-current.json
```

The immediate-response Windows pipe fixture alternates baseline/current calls.
Observed median latency fell from 22.087 to 1.120 ms (P95 22.610 to 1.379 ms).
Native tests also verify split header/body reads, Unicode payloads, broken pipes,
and pending-read timeout cancellation/reaping. This is transport evidence from an
owned Python protocol fixture; it does not simulate DTE debugger work.

Thirty operations per category on the owned IDA executable showed these medians:

| Operation | Median ms | P95 ms |
| --- | ---: | ---: |
| Debug status | 16.687 | 30.065 |
| Current registers | 17.378 | 43.898 |
| All fixture threads' registers | 17.806 | 43.564 |
| Stack | 15.812 | 31.220 |
| Read 64 bytes | 18.275 | 39.138 |
| Separate status/register/stack requests | 61.093 | 93.383 |
| Single context snapshot | 16.007 | 38.711 |
| Step into until a fresh stop | 31.929 | 62.377 |
| Step over until a fresh stop | 33.373 | 60.851 |

The DbgEng dump medians with `kernel_job_status(wait_ms=10)` were 0.320 ms for
registers, 0.684 ms for stack, and 0.312 ms for reading 64 bytes. The dump's
`!thread` query returned no evidence; successful thread context collection is
not established. Matching-PDB and live kernel stop/reconnect proof remain open.

The installed idle VS instance still showed complete-query medians around
60--70 ms after the pipe change. The transport fixture speedup must not be
reported as the complete VS debugger speedup. The built extension has not been
deployed; the new thread capture behavior has compilation/source and MCP routing
test evidence, without live VS stepping or many-thread runtime acceptance.

The follow-up host review found four synchronous Output-pane logging calls per
connection (waiting, connected, received, sent). These routine messages are now
disabled by default; set `AIDB_TRACE_IPC=1` in the VS launch environment to enable
them. Startup, shutdown and error logs remain enabled. This removes COM logging
and redundant UTF-8 counts from ordinary requests. Release compilation passed;
an installed-host A/B measurement is still required to quantify the complete
query improvement. The 60--70 ms measurement above predates this host change.
An isolated experimental-profile attempt verified that the installed DLL hash
matched the compiled DLL. Its configuration refresh did not complete; another
HeartGame VS instance appeared during `/Setup`, which can wait on a global VS
configuration lock. The owned setup process was stopped and the experimental
profile cleaned; the HeartGame instance was preserved. No A/B timings were
collected from this attempt.

Launch ownership loading now preserves a receipt if OS process identity cannot
be queried, and retries verification on a later lookup. An unavailable query
does not grant ownership; confirmed exit or PID reuse still prunes the receipt.
`test_unavailable_identity_preserves_receipt_and_retries_loading` covers failure
followed by successful verification. This fixes a reproducible receipt-loss
path; it does not establish why the previously observed VS PID 73024 had no
receipt. That idle, solution-free instance was gracefully closed after checking
its exact OS creation time, and OS enumeration confirmed it exited.
Close also retains ownership and returns `PROCESS_STATE_UNAVAILABLE` when
process/image queries cannot establish exit. It never reports successful close
or escalates from an unknown state. Access denied remains unknown; a missing
process object or a matching object's terminated exit code establishes exit.

The subsequent correctness review reproduced and fixed four input/state issues:
non-forced VS close now requires an explicitly idle debugger status and returns
`DEBUG_STATE_UNAVAILABLE` if status cannot be obtained; persisted ownership
records require an object, supported schema, matching filename/PID and typed
identity/capabilities; step snapshots validate expression count, nonempty text
and the VS UTF-16 length limit before issuing the step; tool timeout validation
rejects NaN and infinities. Unknown record schemas are retained without granting
ownership. Mock process-close tests cannot load/delete real launch receipts.
Regression tests verify rejected requests do not call step/close, valid idle
close still succeeds, and malformed records cannot block or replace a valid
record. These are source/MCP routing and controlled-fixture tests, not live VS
debuggee shutdown evidence.
