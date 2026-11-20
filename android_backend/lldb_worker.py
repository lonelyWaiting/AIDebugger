"""JSON-line worker executed by the private LLDB's embedded Python."""
import json
import os
import sys
import time

import lldb


WORKER_MARKER = "AIDB_LLDB " + os.environ.pop("AIDB_ANDROID_REPLY_TOKEN") + " "
PAUSED = (lldb.eStateStopped, lldb.eStateCrashed, lldb.eStateSuspended)
TERMINAL = (lldb.eStateDetached, lldb.eStateExited, lldb.eStateInvalid, lldb.eStateUnloaded)
SIGNIFICANT_STOP = {lldb.eStopReasonBreakpoint, lldb.eStopReasonWatchpoint, lldb.eStopReasonPlanComplete,
                    lldb.eStopReasonTrace, lldb.eStopReasonException}


def file_path(spec):
    if not spec.IsValid():
        return None
    return str(spec.fullpath)


def address(value):
    return None if value == lldb.LLDB_INVALID_ADDRESS else hex(value)


def check(error):
    if error.Fail():
        raise RuntimeError(error.GetCString())


def frame_info(frame):
    location = frame.GetLineEntry()
    line = location.GetLine()
    return {"index": frame.GetFrameID(), "function": frame.GetDisplayFunctionName(),
            "pc": address(frame.GetPC()), "module": file_path(frame.GetModule().GetFileSpec()),
            "file": file_path(location.GetFileSpec()),
            "line": line if line not in (0, lldb.LLDB_INVALID_LINE_NUMBER) else None}


def breakpoint_info(bp):
    return {"breakpointId": bp.GetID(), "locations": bp.GetNumLocations(),
            "resolvedLocations": bp.GetNumResolvedLocations(), "hitCount": bp.GetHitCount(),
            "enabled": bp.IsEnabled(), "condition": bp.GetCondition()}


def quote(value):
    if not value or any(ord(c) < 32 for c in value):
        raise ValueError("Empty paths and control characters are unsupported")
    return '"' + value.replace("\\", "\\\\").replace('"', '\\"') + '"'


class Debugger:
    def __init__(self):
        self.debugger = lldb.debugger
        self.debugger.SetAsync(True)
        self.listener = lldb.SBListener("aidb-android")
        self.target = None
        self.process = None

    def setting(self, setting, *values):
        result = lldb.SBCommandReturnObject()
        command = setting + " " + " ".join(str(value) if isinstance(value, int) else quote(value)
                                          for value in values)
        self.debugger.GetCommandInterpreter().HandleCommand(command, result)
        if not result.Succeeded():
            raise RuntimeError(result.GetError())

    def prepare(self, symbol_dirs, source_map, executable, sysroot):
        for path in symbol_dirs:
            self.setting("settings append target.exec-search-paths", path)
        for build, local in source_map.items():
            self.setting("settings append target.source-map", build, local)
        self.setting("settings set plugin.process.gdb-remote.packet-timeout", 8)
        self.target = self.debugger.CreateTarget(executable)
        if not self.target.IsValid():
            raise RuntimeError("LLDB could not create an Android target")
        error = lldb.SBError()
        self.target.AppendImageSearchPath("/", sysroot + "/", error)
        check(error)
        return {"prepared": True}

    def connect(self, port, pid):
        error = lldb.SBError()
        self.process = self.target.ConnectRemote(
            self.listener, f"connect://127.0.0.1:{port}", "gdb-remote", error)
        check(error)
        if not self.process.IsValid() or self.process.GetProcessID() != pid:
            # Avoid sending any execution control to an unexpected process.
            self.process = None
            raise RuntimeError("LLDB did not bind the requested Android PID")
        self.wait_for_state(PAUSED + (lldb.eStateRunning,))
        if self.process.GetState() == lldb.eStateRunning:
            check(self.process.Stop())
            self.wait_for_state(PAUSED)
        self.drain_events()
        if self.process.GetState() not in PAUSED:
            state = lldb.SBDebugger.StateAsCString(self.process.GetState())
            raise RuntimeError(f"LLDB attach ended in state {state}: {self.process.GetExitDescription() or ''}")
        return self.status()

    def wait_for_state(self, expected):
        deadline = time.monotonic() + 8
        self.drain_events()
        final = expected + (lldb.eStateDetached, lldb.eStateExited)
        while self.process.GetState() not in final and time.monotonic() < deadline:
            self.listener.WaitForEvent(1, lldb.SBEvent())
            self.drain_events()

    def drain_events(self):
        event = lldb.SBEvent()
        while self.listener.GetNextEvent(event):
            pass

    def status(self):
        self.drain_events()
        process = self.process
        state = process.GetState()
        result = {"state": lldb.SBDebugger.StateAsCString(state),
                  "pid": process.GetProcessID(), "stopId": process.GetStopID()}
        if state in PAUSED:
            thread = self.selected_thread()
            if thread.IsValid():
                result.update(threadId=thread.GetThreadID(), stopReason=thread.GetStopReason(),
                              stopDescription=thread.GetStopDescription(512))
        elif state == lldb.eStateExited:
            result.update(exitStatus=process.GetExitStatus(), exitDescription=process.GetExitDescription())
        return result

    def selected_thread(self):
        selected = self.process.GetSelectedThread()
        if selected.IsValid() and selected.GetStopReason() in SIGNIFICANT_STOP:
            return selected
        for thread in self.process:
            if thread.GetStopReason() in SIGNIFICANT_STOP:
                return thread
        return selected

    def paused(self):
        self.drain_events()
        if self.process.GetState() not in PAUSED:
            raise RuntimeError("The Android process must be paused")

    def thread(self, thread_id):
        self.paused()
        thread = (self.process.GetThreadByID(thread_id) if thread_id
                  else self.selected_thread())
        if not thread.IsValid():
            raise ValueError("Android thread is unavailable")
        return thread

    def inspect(self, query, thread_id=0, index=0, offset=0, limit=32):
        if query == "status":
            return self.status()
        self.paused()
        if query == "threads":
            return {"threads": [
                {"threadId": thread.GetThreadID(), "name": thread.GetName(),
                 "stopReason": thread.GetStopReason(), "stopDescription": thread.GetStopDescription(512)}
                for thread in (self.process.GetThreadAtIndex(i) for i in
                               range(offset, min(offset + limit, self.process.GetNumThreads())))]}
        if query == "modules":
            return {"modules": [
                {"file": file_path(module.GetFileSpec()), "deviceFile": file_path(module.GetPlatformFileSpec()),
                 "symbols": file_path(module.GetSymbolFileSpec()), "uuid": module.GetUUIDString(),
                 "base": address(module.GetObjectFileHeaderAddress().GetLoadAddress(self.target))}
                for module in (self.target.GetModuleAtIndex(i) for i in
                               range(offset, min(offset + limit, self.target.GetNumModules())))]}
        if query == "breakpoints":
            return {"breakpoints": [breakpoint_info(self.target.GetBreakpointAtIndex(i)) for i in
                                    range(offset, min(offset + limit, self.target.GetNumBreakpoints()))]}
        thread = self.thread(thread_id)
        if query == "stack":
            frames = []
            for i in range(offset, offset + limit):
                frame = thread.GetFrameAtIndex(i)
                if not frame.IsValid():
                    break
                frames.append(frame_info(frame))
            return {"threadId": thread.GetThreadID(), "frames": frames}
        if query != "locals":
            raise ValueError("Unknown Android inspection query")
        frame = thread.GetFrameAtIndex(index)
        if not frame.IsValid():
            raise ValueError("Android stack frame is unavailable")
        values = frame.GetVariables(True, True, False, True, lldb.eNoDynamicValues)
        variables = []
        for i in range(offset, min(offset + limit, values.GetSize())):
            value = values.GetValueAtIndex(i)
            raw = value.GetValue()
            variables.append({"name": value.GetName(), "type": value.GetTypeName(),
                              "value": raw[:4096] if raw is not None else None,
                              "error": value.GetError().GetCString()})
        return {"frame": frame_info(frame), "variables": variables}

    def set_breakpoint(self, symbol, file, line, module, load_address=None, condition=""):
        self.paused()
        if load_address is not None:
            bp = self.target.BreakpointCreateByAddress(load_address)
        elif symbol:
            bp = (self.target.BreakpointCreateByName(symbol, module) if module
                  else self.target.BreakpointCreateByName(symbol))
        elif module:
            modules = lldb.SBFileSpecList()
            modules.Append(lldb.SBFileSpec(module))
            bp = self.target.BreakpointCreateByLocation(lldb.SBFileSpec(file), line, 0, 0, modules)
        else:
            bp = self.target.BreakpointCreateByLocation(file, line)
        if not bp.IsValid():
            raise RuntimeError("LLDB could not create the breakpoint")
        if condition:
            bp.SetCondition(condition)
        return breakpoint_info(bp)

    def native(self, query, thread_id=0, index=0, load_address=None, count=32, expression="", pattern=""):
        self.paused()
        if query in {"memory", "search_memory"}:
            error = lldb.SBError()
            data = self.process.ReadMemory(load_address, count, error) or b""
            result = {"address": address(load_address), "bytesRead": len(data),
                      "partial": len(data) != count, "error": error.GetCString()}
            if query == "memory":
                return {**result, "dataHex": data.hex()}
            needle = bytes.fromhex(pattern)
            matches, offset = [], data.find(needle)
            while offset >= 0 and len(matches) < 128:
                matches.append(address(load_address + offset))
                offset = data.find(needle, offset + 1)
            return {**result, "matches": matches, "truncated": offset >= 0}
        if query == "resolve_address":
            where = self.target.ResolveLoadAddress(load_address)
            entry = where.GetLineEntry()
            function, symbol = where.GetFunction(), where.GetSymbol()
            line = entry.GetLine()
            return {"address": address(load_address), "module": file_path(where.GetModule().GetFileSpec()),
                    "function": (function.GetName() if function.IsValid() else symbol.GetName()),
                    "file": file_path(entry.GetFileSpec()),
                    "line": line if line not in (0, lldb.LLDB_INVALID_LINE_NUMBER) else None}
        if query == "disassembly":
            start = (self.target.ResolveLoadAddress(load_address) if load_address is not None
                     else self.thread(thread_id).GetFrameAtIndex(index).GetPCAddress())
            if not start.IsValid():
                raise ValueError("Disassembly address is unavailable")
            instructions = self.target.ReadInstructions(start, count)
            return {"instructions": [
                {"address": address(item.GetAddress().GetLoadAddress(self.target)),
                 "mnemonic": item.GetMnemonic(self.target), "operands": item.GetOperands(self.target),
                 "comment": item.GetComment(self.target)}
                for item in (instructions.GetInstructionAtIndex(i) for i in range(instructions.GetSize()))]}
        frame = self.thread(thread_id).GetFrameAtIndex(index)
        if not frame.IsValid():
            raise ValueError("Android stack frame is unavailable")
        if query == "registers":
            registers = frame.GetRegisters()
            groups = [registers.GetValueAtIndex(i) for i in range(registers.GetSize())]
            return {"groups": [
                {"name": group.GetName(), "registers": [
                    {"name": value.GetName(), "value": value.GetValue()}
                    for value in (group.GetChildAtIndex(i) for i in range(min(group.GetNumChildren(), count)))]}
                for group in groups]}
        options = lldb.SBExpressionOptions()
        options.SetTimeoutInMicroSeconds(2_000_000)
        options.SetIgnoreBreakpoints(True)
        options.SetUnwindOnError(True)
        options.SetTryAllThreads(False)
        value = frame.EvaluateExpression(expression, options)
        check(value.GetError())
        return {"type": value.GetTypeName(), "value": value.GetValue(), "summary": value.GetSummary()}

    def write_memory(self, load_address, data_hex, expected_stop_id):
        self.paused()
        stop_id = self.process.GetStopID()
        if expected_stop_id is not None and expected_stop_id != stop_id:
            raise ValueError("Android stop changed before memory write")
        data = bytes.fromhex(data_hex)
        width = self.process.GetAddressByteSize()
        if not width or load_address + len(data) >= 1 << (8 * width):
            raise ValueError("Memory write range exceeds the target address space")
        write_error = lldb.SBError()
        written = self.process.WriteMemory(load_address, data, write_error)
        result = {"address": address(load_address), "stopId": stop_id,
                  "bytesRequested": len(data), "bytesWritten": written, "partial": written < len(data),
                  "writeError": write_error.GetCString(), "bytesReadBack": 0, "verified": None,
                  "requestedRangeVerified": False, "readBackError": None}
        if written:
            try:
                verify_error = lldb.SBError()
                actual = self.process.ReadMemory(load_address, written, verify_error) or b""
                result.update(bytesReadBack=len(actual), readBackError=verify_error.GetCString())
                if verify_error.Success() and len(actual) == written:
                    result["verified"] = actual == data[:written]
                    result["requestedRangeVerified"] = (written == len(data) and write_error.Success()
                                                        and result["verified"])
            except Exception as error:
                result["readBackError"] = str(error)
        return result

    def watchpoint(self, load_address, size, read, write, watchpoint_id=0):
        self.paused()
        if watchpoint_id:
            if not self.target.DeleteWatchpoint(watchpoint_id):
                raise ValueError("Android watchpoint does not exist")
            return {"removed": watchpoint_id}
        error = lldb.SBError()
        options = lldb.SBWatchpointOptions()
        options.SetWatchpointTypeRead(read)
        options.SetWatchpointTypeWrite(lldb.eWatchpointWriteTypeAlways if write
                                       else lldb.eWatchpointWriteTypeDisabled)
        watch = self.target.WatchpointCreateByAddress(load_address, size, options, error)
        check(error)
        if not watch.IsValid():
            raise RuntimeError("LLDB could not install the hardware watchpoint")
        return {"watchpointId": watch.GetID(), "address": address(load_address), "size": size}

    def snapshot(self, thread_id=0, depth=12, limit=32):
        first = self.status()
        snapshot = {"status": first}
        if self.process.GetState() in PAUSED:
            thread_id = thread_id or first.get("threadId", 0)
            snapshot["stack"] = self.inspect("stack", thread_id, limit=depth)["frames"]
            if snapshot["stack"]:
                snapshot["locals"] = self.inspect("locals", thread_id, limit=limit)["variables"]
            last = self.status()
            if last["stopId"] != first["stopId"] or last["state"] != first["state"]:
                return {"status": last, "snapshotStable": False}
        snapshot["snapshotStable"] = self.process.GetState() in PAUSED
        return snapshot

    def remove_breakpoint(self, breakpoint_id):
        self.paused()
        if not self.target.BreakpointDelete(breakpoint_id):
            raise ValueError("Android breakpoint does not exist")
        return {"removed": breakpoint_id}

    def configure_breakpoint(self, breakpoint_id, enabled, condition):
        self.paused()
        bp = self.target.FindBreakpointByID(breakpoint_id)
        if not bp.IsValid():
            raise ValueError("Android breakpoint does not exist")
        if enabled is not None:
            bp.SetEnabled(enabled)
        if condition is not None:
            bp.SetCondition(condition or None)
        return breakpoint_info(bp)

    def control(self, action, thread_id):
        if action == "pause":
            check(self.process.Stop())
        elif action == "continue":
            self.paused()
            check(self.process.Continue())
        else:
            thread = self.thread(thread_id)
            error = lldb.SBError()
            if action == "step_into":
                thread.StepInto(None, lldb.LLDB_INVALID_LINE_NUMBER, error, lldb.eOnlyDuringStepping)
            elif action == "step_over":
                thread.StepOver(lldb.eOnlyDuringStepping, error)
            elif action == "step_out":
                thread.StepOut(error)
            else:
                raise ValueError("Unknown Android control action")
            check(error)
        return self.status()

    def detach(self):
        if self.process and self.process.IsValid():
            self.drain_events()
            state = self.process.GetState()
            if state == lldb.eStateExited:
                return {"state": "exited", "exitStatus": self.process.GetExitStatus(),
                        "exitDescription": self.process.GetExitDescription()}
            if state not in TERMINAL:
                check(self.process.Detach())
        return {"state": "detached"}


def emit(payload):
    print("\n" + WORKER_MARKER + json.dumps(payload), file=sys.__stdout__, flush=True)


def main():
    debugger = Debugger()
    operations = {name: getattr(debugger, name) for name in (
        "prepare", "connect", "inspect", "set_breakpoint", "remove_breakpoint", "configure_breakpoint", "control",
        "snapshot", "native", "write_memory", "watchpoint", "detach")}
    emit({"ready": True})
    try:
        for line in sys.__stdin__:
            if not line.strip():
                continue
            request = json.loads(line)
            operation = request["operation"]
            try:
                result = operations[operation](**request["arguments"])
                emit({"result": result})
                if operation == "detach":
                    break
            except Exception as error:
                emit({"error": str(error)})
    finally:
        try:
            debugger.detach()
        except Exception as error:
            print(str(error), file=sys.__stderr__, flush=True)


main()
