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
