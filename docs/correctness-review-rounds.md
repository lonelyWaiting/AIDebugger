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
