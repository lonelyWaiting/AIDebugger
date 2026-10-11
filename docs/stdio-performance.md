# IDA and kernel stdio admission

The independent IDA and kernel entrypoints share only a generic dispatcher.
Each entrypoint names its cached query and control tools; no debugger state is
shared across backends.

Ordinary requests have eight worker slots. Cached queries and MCP handshake
requests have four separate slots. Close and kernel interrupt have two separate
slots. Cancellation notifications execute the registry's cancellation-event
handler on the reader thread, so occupied worker pools cannot delay delivery.
Delivery does not prove that a native debugger operation was cancelled.

The dispatcher does not queue requests after all slots in their lane are occupied.
It returns JSON-RPC error `-32000` with data
`{"code":"STDIO_BUSY_NOT_STARTED","requestStarted":false}`. Notifications do
not receive responses. This admission error means no backend call started;
already admitted requests retain their backend timeout and unknown-outcome rules.
Clients must not automatically replay a mutation after any other timeout/error.

IDA reserves `ida_list_sessions` and `ida_close`. Kernel reserves
`kernel_list_sessions`, `kernel_session_status`, `kernel_job_status`,
`kernel_interrupt` and `kernel_close`. IDA session status performs an identity
RPC and therefore stays in the ordinary pool. Slow close operations cannot
consume cached-query slots.

## Validation

`python -m unittest discover -s tests -p test_stdio_dispatch.py` holds all eight
ordinary workers until explicitly released. It verifies list/close completion
before release, rejection without later mutation execution, parse-error delivery,
and cancellation delivery without a notification response. The old shared pool
failed the list/close and admission checks. These are portable saturation
regressions, not native debugger timing measurements.

`python tests/ida_integration/run_ida_performance.py --trials 1 --report
.tmp/performance/dispatcher-contention.json` passed with two owned native IDA
sessions: list latency 4.734 ms, other-session health 48.444 ms, and eight
same-session calls rejected before starting. This checks SDK compatibility and
session independence; it does not saturate eight native debugger operations or
prove a native stepping speedup. Default IDA admission allows four sessions.

The real DbgEng dump probe also passed after this change, including two isolated
sessions, memory write/read/restore, bounded output, and passive close. Its report
is `.tmp/kernel/dispatcher-smoke.json`. Live kernel execution and VS live-target
latency remain unverified in this iteration.
