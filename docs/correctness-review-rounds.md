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
