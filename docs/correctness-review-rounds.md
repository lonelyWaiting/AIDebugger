# Routine debugging correctness review

The stopping criterion is two consecutive independent review passes without a
confirmed finding. Each pass starts from the current code and traces the common
VS, IDA and kernel workflows. A confirmed fix resets the clean-pass count.
Source review, controlled regression tests and native live-target acceptance
remain separate evidence.

## Round 1: response handling and operation lifecycle

Reviewed step/capture and start/poll routing, independent backend session locks,
kernel worker request/reply/close transitions, stdio admission and IDA response
decoding. Found that malformed inner MCP envelopes and identity values bypassed
the typed transport failure handling added in the preceding review. Reproduced
null/list/string identities and malformed content before fixing them.

IDA now validates lifecycle tool envelopes, text content and identity objects.
Malformed identity preflight cannot dispatch a mutation; malformed close
responses preserve `IDA_OUTCOME_UNKNOWN` and `doNotRetry`, without terminating
the process. Explicit tool errors retain their existing semantics.

Validation: 53 IDA tests and all 368 Python tests passed; syntax and diff checks
passed. No real debugger process is launched by the audited test runner.

## Round 2: native effects and follow-up failures

Restarted from the committed round-1 code. Reviewed DbgEng native operation
results and worker/supervisor propagation, VS opaque process handles, mapped
dump cursor bindings and step/start follow-up behavior, plus IDA identity and
close receipts. Found that a successfully issued kernel mutation followed by
failed target/status collection returned an ordinary engine error, losing the
fact that the effect might already have occurred.

Reproduced failures in both post-step target verification and status collection.
Post-operation failures now report `OUTCOME_UNKNOWN` for commands, control,
memory writes and breakpoint changes, with an explicit no-retry message. Read
and query failures retain their original error codes. No operation is replayed.
This resets the consecutive-clean count to zero.

Validation: all 43 kernel tests and 370 Python tests passed; syntax and diff
checks passed. Native execution/status failures were injected through owned
mock engine interfaces; live kernel stepping remains unverified.

## Round 3: snapshot completeness and stack enumeration

Reviewed VS sliced thread capture, composite completeness and native follow-up
results, memory chunk continuations, plus the revised IDA/kernel lifecycle
boundaries. Found that VS stack collection/count exceptions were swallowed and
converted to zero frames without marking the result partial. This affected both
thread capture and fallback module scanning.

Stack count capture now distinguishes a real empty stack from an unavailable
collection. Both callers preserve thread metadata and report partial capture
with a diagnostic when the count fails. Metadata-only capture still avoids
reading the stack count. A controlled C# getter test proves that failure is
distinct from zero and the getter is called once, without retry.

Validation: 116 C# tests, 370 Python tests, diff checks and Release compilation
passed. The initial build
identified a missing UI-thread assertion inside the new getter lambda; that
assertion was added before the successful rebuild. Real VS COM fault injection
and deployment have not been performed. Clean-pass count remains zero.

## Round 4: target selection and total deadlines

Restarted review from the round-3 commit, tracing exact target binding, selection
generations, launch/attach/poll deadlines, cursor validation and request leases.
Found that `ensure_vs_instance` captured its selection generation after waiting
for readiness, so it could overwrite a selection made during that wait. Its
attach then used global selection instead of the instance just returned. It
also granted attach/poll an extra second after the total deadline expired.

Regressions reproduced overwritten newer selection and dispatch after deadline.
Selection generation is now captured before the readiness wait, attach uses
the returned exact instance, and exhausted budgets skip attach. Exact transport
failure preserves the ready-instance receipt. Three MCP contract regressions
cover these paths. Clean-pass count resets to zero.

Validation: 66 VS launch/tool tests and all 373 Python tests passed; syntax and
diff checks passed. These are controlled MCP routing/race/deadline tests; no VS
launch, attach or user-target intervention was performed.

## Round 5: close admission and async responsiveness

Reviewed VS owned-process close against cancellation and PID reuse, debugger
request identity/leases, IDA unknown outcomes and kernel terminal close states.
Found synchronous `taskkill` inside the async close path. A 150 ms owned fixture
blocked every heartbeat before the fix. Restored ownership records also left a
gap between PID identity checks and taskkill's PID lookup.

Each taskkill now runs in a worker thread holding a process handle whose
creation ticks and liveness were verified. The handle stays with that worker
through subprocess completion even if the caller cancels. A changed/unavailable
identity rejects taskkill. Graceful/forced behavior and ownership cleanup remain
covered by the existing close tests.

Additional evidence: the heartbeat no longer starves, cancellation does not
release the pin early, a changed identity never invokes taskkill, and a Windows
native fixture verifies the calling Python process's exact handle stays open
inside the pin and closes afterward. No real VS or debuggee was terminated.
Clean-pass count resets to zero.

Round-5 validation: all 377 Python tests, syntax checks and diff checks passed.

## Round 6: complete-result and truncated-result semantics

Traced successful and failed results back through VS request ledgers, step and
memory continuation wrappers, IDA lifecycle preflight and kernel job receipts.
Found that callstack field bounding shortened strings without marking the frame
`Truncated`. The collector consequently could report the shortened frame as a
complete result. A new C# regression failed on the original implementation.

All bounded frame fields now propagate truncation, preserving any pre-existing
flag. The existing collector uses that flag to mark its page partial. The
regression distinguishes short fields, truncated fields and pre-existing
incompleteness. Clean-pass count resets to zero.

Validation: 117 C# tests, 377 Python tests, diff checks and Release compilation
passed; real VS runtime acceptance remains open.

## Round 7: fresh completeness and target-binding review

Reviewed commit `aba317f` without changing production code. Independently traced
routine step/context receipts, thread and callstack completeness, memory-search
continuations, exact-instance dispatch, IDA lifecycle admission, and kernel
worker/session state transitions. No new confirmed correctness defect was found.

Validation: 48 bounded memory-search overlap/completion cases covered chunk
boundaries, duplicate suppression, complete byte coverage and exact target
preservation. Another 32 context and kernel-state tests passed. The round-6
full-suite and Release-build results still apply to the unchanged source.
Consecutive clean-pass count: one.

## Round 8: independent cancellation and resource-ownership review

Started a separate pass on the same commit, focusing on cancellation during
close, concurrent selection and session work, post-mutation uncertainty, and
late replies after terminal close. Traced the worker-owned VS process pin,
kernel close admission/watchdog and poll-only waits, IDA lifecycle locks, and VS
step follow-up receipts. No new confirmed correctness defect was found.

Validation: 33 cancellation, busy, interrupt, shutdown, unknown-outcome and
process-identity tests passed. Test taskkill calls were mocked; the native handle
fixture inspected only its own Python process. No real VS/debuggee was launched
or terminated. Consecutive clean-pass count: two; the requested stopping
criterion is satisfied.

Final evidence: 377 Python tests, 117 C# tests and VS Release compilation passed
for the final production source. Live VS deployment/COM fault injection, real
IDA fault injection and live kernel stepping were not performed in these rounds.
