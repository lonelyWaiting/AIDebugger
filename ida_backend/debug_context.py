"""Bounded, read-only context collection on the owning IDA thread."""
import inspect
import os
from itertools import islice


def collect_stack(debugger, idd, names, tid, max_frames):
    """Unwind natively once; resolve symbols only for the requested output page."""
    trace = idd.call_stack_t()
    if not debugger.collect_stack_trace(tid, trace):
        return [], False
    frames = []
    for frame in islice(trace, max_frames):
        value = {"addr": hex(frame.callea)}
        try:
            module = idd.modinfo_t()
            value["module"] = (os.path.basename(module.name)
                               if debugger.get_module_info(frame.callea, module) else "<unknown>")
            value["symbol"] = names.get_nice_colored_name(frame.callea,
                names.GNCN_NOCOLOR | names.GNCN_NOLABEL | names.GNCN_NOSEG | names.GNCN_PREFDBG) or "<unnamed>"
        except Exception as error:
            value["module"], value["symbol"] = "<error>", str(error)
        frames.append(value)
    return frames, len(trace) > max_frames


def collect_context(debugger, api, idd, names, max_threads=128, max_frames=64):
    for value, maximum, name in ((max_threads, 128, "max_threads"), (max_frames, 128, "max_frames")):
        if type(value) is not int or not 1 <= value <= maximum:
            raise ValueError(f"{name} must be an integer in 1..{maximum}")
    api.dbg_ensure_suspended()
    before = api._get_debug_state_result()
    tid = debugger.get_current_thread()
    quantity = debugger.get_thread_qty()
    threads = [debugger.getn_thread(i) for i in range(min(quantity, max_threads))]
    # Already inside the worker's single @idasync boundary. Calling a decorated
    # tool here would nest execute_sync and fail the vendor's reentrancy guard.
    registers = inspect.unwrap(api.dbg_regs)()
    stack_error = None
    try:
        stack, stack_truncated = collect_stack(debugger, idd, names, tid, max_frames)
    except Exception as error:
        stack, stack_truncated, stack_error = [], False, str(error)
    after = api._get_debug_state_result()
    stable = (before.get("suspended") is True and after.get("suspended") is True
              and before.get("ip") is not None
              and before.get("ip") == after.get("ip")
              and registers.get("thread_id") == tid == debugger.get_current_thread()
              and quantity == debugger.get_thread_qty())
    return {"status": after, "threads": threads, "totalThreads": quantity,
            "threadsTruncated": quantity > max_threads, "registers": registers,
            "stack": stack[:max_frames], "stackTruncated": stack_truncated,
            "stackStatus": "available" if stack else "unavailable", "stackError": stack_error,
            "snapshotStable": stable, "snapshotStatus": "stable" if stable else "changed_during_collection"}
