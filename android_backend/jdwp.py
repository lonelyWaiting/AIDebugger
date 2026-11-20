"""Typed JDWP session for debuggable ART processes over a pinned ADB transport."""
import select
import math
import struct
import time

from .jobs import JobStopped, checkpoint
from .runtime import AndroidError


u4 = struct.Struct(">I").pack
u8 = struct.Struct(">Q").pack
HEADER = struct.Struct(">IIBBB")
HANDSHAKE = b"JDWP-Handshake"
STEP, BREAKPOINT, EXCEPTION, CLASS_PREPARE, VM_START, VM_DEATH = 1, 2, 4, 8, 90, 99


class JavaLost(AndroidError):
    pass


class JdwpError(AndroidError):
    def __init__(self, code, command):
        self.code = code
        super().__init__(f"JDWP {command[0]}/{command[1]} returned error {code}")


class Cursor:
    def __init__(self, data):
        self.data = memoryview(data)
        self.offset = 0

    def take(self, size):
        end = self.offset + size
        if end > len(self.data):
            raise JavaLost("Truncated JDWP reply")
        data = self.data[self.offset:end]
        self.offset = end
        return data

    def number(self, size=4, signed=False):
        return int.from_bytes(self.take(size), "big", signed=signed)

    def text(self):
        return self.take(self.number()).tobytes().decode("utf-8", "replace")


def string(value):
    raw = value.encode("utf-8")
    return u4(len(raw)) + raw


class JavaSession:
    def __init__(self, sock):
        self.socket = sock
        self.buffer = bytearray()
        self.order = 0
        self.events = []
        self.stop_id = 0
        self.suspensions = 0
        self.state = "running"
        self.stop = None
        self.breakpoints = {}
        self.next_breakpoint = 0
        self.requests = {}
        self.methods = {}
        self.classes = {}
        self.step = None
        self.exception_request = None
        self.gate = False
        try:
            self.socket.settimeout(0.1)
            self.socket.sendall(HANDSHAKE)
            if self.receive_bytes(len(HANDSHAKE), time.monotonic() + 4) != HANDSHAKE:
                raise JavaLost("Android runtime did not complete the JDWP handshake")
            ids = self.request(1, 7)
            self.field_size, self.method_size, self.object_size, self.type_size, self.frame_size = (
                ids.number() for _ in range(5))
            if any(size not in range(1, 9) for size in (
                    self.field_size, self.method_size, self.object_size, self.type_size, self.frame_size)):
                raise JavaLost("Unsupported JDWP ID size")
            self.sync()
        except BaseException:
            self.socket.close()
            raise

    def fill(self, size, deadline):
        while len(self.buffer) < size:
            if min(checkpoint(), deadline - time.monotonic()) <= 0:
                raise JavaLost("JDWP reply timed out")
            try:
                data = self.socket.recv(65536)
            except TimeoutError:
                continue
            if not data:
                raise JavaLost("JDWP transport closed")
            self.buffer.extend(data)

    def receive_bytes(self, size, deadline):
        self.fill(size, deadline)
        result = bytes(self.buffer[:size])
        del self.buffer[:size]
        return result

    def packet(self, deadline):
        self.fill(11, deadline)
        length, packet_id, flags, first, second = HEADER.unpack_from(self.buffer)
        if not 11 <= length <= 8 * 1024 * 1024:
            raise JavaLost("Invalid JDWP packet length")
        packet = self.receive_bytes(length, deadline)
        return packet_id, flags, first, second, packet[11:]

    def request(self, cmdset, cmd, data=b""):
        checkpoint()
        self.order += 1
        deadline = time.monotonic() + 5
        try:
            self.socket.sendall(HEADER.pack(11 + len(data), self.order, 0, cmdset, cmd) + data)
            while True:
                pid, flags, first, second, payload = self.packet(deadline)
                if flags == 128 and pid == self.order:
                    code = first * 256 + second
                    if code:
                        raise JdwpError(code, (cmdset, cmd))
                    return Cursor(payload)
                self.queue_event(flags, first, second, payload)
        except JobStopped as error:
            raise JavaLost(f"{error}; JDWP operation outcome is unknown") from error

    def queue_event(self, flags, cmdset, command, data):
        if flags == 0 and (cmdset, command) == (199, 1):
            # ART sends asynchronous DDMS chunks such as WAIT and APNM on the
            # same socket. They are independent of JDWP suspension events.
            return
        if flags != 0 or (cmdset, command) != (64, 100) or len(self.events) >= 256:
            raise JavaLost(f"Unexpected JDWP packet (flags {flags}, command {cmdset}/{command})")
        self.events.append(data)

    def sync(self):
        for _ in range(64):
            if not self.pending():
                break
            if not self.events:
                _, flags, first, second, data = self.packet(time.monotonic() + 1)
                self.queue_event(flags, first, second, data)
                if not self.events:  # An asynchronous ART DDMS chunk.
                    continue
            try:
                self.handle_events(Cursor(self.events.pop(0)))
            except JobStopped as error:
                raise JavaLost("JDWP event processing interrupted; suspension state is unknown") from error

    def pending(self):
        return (self.state not in {"exited", "detached"} and
                bool(self.events or self.buffer or select.select([self.socket], [], [], 0)[0]))

    def location(self, cursor):
        return (cursor.number(1), cursor.number(self.type_size), cursor.number(self.method_size), cursor.number(8))

    def encode_location(self, location):
        tag, klass, method, code = location
        return (bytes([tag]) + klass.to_bytes(self.type_size, "big")
                + method.to_bytes(self.method_size, "big") + u8(code))

    def event_request(self, kind, policy, modifiers):
        data = bytes([kind, policy]) + u4(len(modifiers)) + b"".join(modifiers)
        return self.request(15, 1, data).number()

    def event_clear(self, kind, request):
        self.request(15, 2, bytes([kind]) + u4(request))

    def handle_events(self, cursor):
        policy = cursor.number(1)
        visible = None
        for _ in range(cursor.number()):
            kind, request = cursor.number(1), cursor.number()
            if kind == VM_DEATH:
                self.state, self.stop = "exited", None
                continue
            thread = cursor.number(self.object_size)
            if kind == CLASS_PREPARE:
                tag, klass, signature = cursor.number(1), cursor.number(self.type_size), cursor.text()
                cursor.number()  # class status
                for bp_id in tuple(self.breakpoints):
                    bp = self.breakpoints[bp_id]
                    if bp["signature"] == signature:
                        try:
                            self.bind_breakpoint(bp_id, tag, klass)
                        except JdwpError as error:
                            bp["bindingError"] = str(error)
            elif kind in (BREAKPOINT, STEP, EXCEPTION):
                location = self.location(cursor)
                visible = {"reason": {BREAKPOINT: "breakpoint", STEP: "step", EXCEPTION: "exception"}[kind],
                           "threadId": str(thread)}
                try:
                    visible["location"] = self.describe(location)
                except JdwpError as error:
                    visible["locationError"] = str(error)
                if kind == BREAKPOINT:
                    visible["breakpointId"] = self.requests.get(request)
                if kind == STEP and self.step == request:
                    self.step = None  # Its count modifier consumes the event.
                if kind == EXCEPTION:
                    cursor.number(1)
                    visible["exceptionId"] = str(cursor.number(self.object_size))
                    catch = self.location(cursor)
                    visible["caught"] = bool(catch[1])
            elif kind == VM_START and policy:
                visible = {"reason": "startup", "threadId": str(thread)}
            elif kind != VM_START:
                raise JavaLost(f"Unsupported JDWP event kind {kind}")
        if visible and policy:
            self.record_stop(visible)
        elif policy == 2 and self.state != "exited":
            # A class-prepare request adds its own suspend count.
            self.request(1, 9)

    def record_stop(self, stop):
        self.stop_id += 1
        self.suspensions += 1
        self.state, self.stop = "stopped", stop

    def status(self, *, poll=True):
        if poll and self.state != "exited":
            self.sync()
        return {"state": self.state, "stopId": self.stop_id, **(self.stop or {})}

    def pause(self):
        self.sync()
        if self.state == "running":
            self.request(1, 8)
            self.record_stop({"reason": "pause"})
        return self.status(poll=False)

    def resume(self):
        self.sync()
        if self.state != "stopped":
            raise AndroidError("The Java VM must be suspended")
        while self.suspensions:
            self.request(1, 9)
            self.suspensions -= 1
        self.state, self.stop = "running", None
        return self.status(poll=False)

    def control(self, action, thread_id):
        if action == "pause":
            return self.pause()
        self.sync()
        if self.state != "stopped":
            raise AndroidError("The Java VM must be suspended")
        if self.step is not None:
            self.event_clear(STEP, self.step)
            self.step = None
        if action != "continue":
            thread = self.thread_bytes(thread_id)
            depth = {"step_into": 0, "step_over": 1, "step_out": 2}[action]
            self.step = self.event_request(STEP, 2, [
                bytes([10]) + thread + u4(1) + u4(depth), bytes([1]) + u4(1)])
        return self.resume()

    def thread_bytes(self, thread_id):
        text = thread_id or (self.stop or {}).get("threadId")
        if not text or not 0 < int(text) < 1 << (8 * self.object_size):
            raise AndroidError("Specify a Java thread ID from the current stop or thread list")
        return int(text).to_bytes(self.object_size, "big")

    def class_details(self, klass):
        if klass not in self.classes:
            data = klass.to_bytes(self.type_size, "big")
            signature = self.request(2, 1, data).text()
            try:
                source = self.request(2, 7, data).text()
            except JdwpError as error:
                if error.code != 101:
                    raise
                source = None
            self.classes[klass] = (signature, source)
        return self.classes[klass]

    def type_methods(self, klass):
        if klass not in self.methods:
            cursor = self.request(2, 5, klass.to_bytes(self.type_size, "big"))
            result = {}
            for _ in range(cursor.number()):
                method = cursor.number(self.method_size)
                name, signature, cursor_flags = cursor.text(), cursor.text(), cursor.number()
                result[method] = [name, signature, cursor_flags, None]
            self.methods[klass] = result
        return self.methods[klass]

    def method_data(self, klass, method):
        return klass.to_bytes(self.type_size, "big") + method.to_bytes(self.method_size, "big")

    def lines(self, klass, method):
        item = self.type_methods(klass).get(method)
        if not item:
            return []
        if item[3] is None:
            try:
                cursor = self.request(6, 1, self.method_data(klass, method))
                cursor.take(16)
                item[3] = [(cursor.number(8), cursor.number()) for _ in range(cursor.number())]
            except JdwpError as error:
                if error.code not in (101, 511):  # absent information / native method
                    raise
                item[3] = []
        return item[3]

    def describe(self, location):
        _, klass, method, code = location
        signature, source = self.class_details(klass)
        details = self.type_methods(klass).get(method, [None, None, None])
        lines = self.lines(klass, method)
        line = next((line for offset, line in reversed(lines) if offset <= code), None)
        return {"class": signature[1:-1].replace("/", ".") if signature.startswith("L") else signature,
                "method": details[0], "signature": details[1], "file": source,
                "line": line, "codeIndex": str(code), "native": bool((details[2] or 0) & 0x100)}

    def bind_breakpoint(self, bp_id, tag, klass):
        bp = self.breakpoints[bp_id]
        if klass in bp["bound"]:
            return
        matches = []
        for method, (name, signature, flags, _) in self.type_methods(klass).items():
            if flags & (0x100 | 0x400) or bp["method"] and bp["method"] != name:
                continue
            if bp["descriptor"] and bp["descriptor"] != signature:
                continue
            entries = self.lines(klass, method)
            match = next((offset for offset, line in entries if not bp["line"] or line == bp["line"]), None)
            if match is not None:
                matches.append((tag, klass, method, match))
        for location in matches:
            request = self.event_request(BREAKPOINT, 2, [b"\x07" + self.encode_location(location)])
            self.requests[request] = bp_id
            bp["requests"].append(request)
        bp["bound"].add(klass)

    def add_breakpoint(self, class_name, method="", line=0, descriptor=""):
        signature = "L" + class_name.replace(".", "/") + ";"
        self.next_breakpoint += 1
        bp_id = self.next_breakpoint
        prepare = self.event_request(CLASS_PREPARE, 2, [b"\x05" + string(class_name)])
        bp = self.breakpoints[bp_id] = {
            "class": class_name, "signature": signature, "method": method, "line": line, "descriptor": descriptor,
            "prepare": prepare, "requests": [], "bound": set(),
        }
        try:
            cursor = self.request(1, 2, string(signature))
            for _ in range(cursor.number()):
                tag, klass, status = cursor.number(1), cursor.number(self.type_size), cursor.number()
                if status & 2:
                    self.bind_breakpoint(bp_id, tag, klass)
        except JdwpError:
            self.remove_breakpoint(bp_id)
            raise
        return self.breakpoint_info(bp_id)

    def breakpoint_info(self, bp_id):
        bp = self.breakpoints[bp_id]
        return {"breakpointId": bp_id, "class": bp["class"], "method": bp["method"],
                "line": bp["line"] or None, "resolvedLocations": len(bp["requests"]),
                "classPrepared": bool(bp["bound"]), "bindingError": bp.get("bindingError")}

    def remove_breakpoint(self, breakpoint_id):
        bp = self.breakpoints.get(breakpoint_id)
        if not bp:
            raise ValueError("Java breakpoint does not exist")
        for request in bp["requests"]:
            self.event_clear(BREAKPOINT, request)
            del self.requests[request]
        self.event_clear(CLASS_PREPARE, bp["prepare"])
        del self.breakpoints[breakpoint_id]
        return {"removed": breakpoint_id}

    def exceptions(self, caught, uncaught):
        if self.exception_request is not None:
            self.event_clear(EXCEPTION, self.exception_request)
            self.exception_request = None
        if caught or uncaught:
            self.exception_request = self.event_request(EXCEPTION, 2, [
                b"\x08" + bytes(self.type_size) + bytes([caught, uncaught])])
        return {"caught": caught, "uncaught": uncaught, "enabled": self.exception_request is not None}

    def frames(self, thread, index, count):
        total = self.request(11, 7, thread).number()
        if index >= total:
            return []
        cursor = self.request(11, 6, thread + u4(index) + u4(min(count, total-index)))
        return [(cursor.number(self.frame_size), self.location(cursor)) for _ in range(cursor.number())]

    def tagged(self, cursor, tag=None):
        tag = tag or chr(cursor.number(1))
        if tag in ("F", "D"):
            value = struct.unpack(">f" if tag == "F" else ">d", cursor.take(4 if tag == "F" else 8))[0]
            return value if math.isfinite(value) else str(value)
        primitives = {"B": 1, "Z": 1, "C": 2, "S": 2, "I": 4, "J": 8}
        if tag in primitives:
            value = cursor.number(primitives[tag], signed=tag not in ("Z", "C"))
            return bool(value) if tag == "Z" else str(value) if tag == "J" else value
        if tag == "V":
            return None
        object_id = cursor.number(self.object_size)
        if not object_id:
            return None
        if tag == "s":
            return self.request(10, 1, object_id.to_bytes(self.object_size, "big")).text()[:4096]
        return {"objectId": str(object_id), "tag": tag}

    def object(self, object_id, query, offset, limit):
        if self.status()["state"] != "stopped":
            raise AndroidError("Object inspection requires a suspended Java VM")
        number = int(object_id)
        if not 0 < number < 1 << self.object_size * 8:
            raise ValueError("Invalid Java object ID")
        data = number.to_bytes(self.object_size, "big")
        if query == "string":
            return {"value": self.request(10, 1, data).text()[:4096]}
        if query == "array":
            length = self.request(13, 1, data).number()
            if offset >= length:
                return {"length": length, "values": []}
            cursor = self.request(13, 2, data + u4(offset) + u4(min(limit, length - offset)))
            tag, count = chr(cursor.number(1)), cursor.number()
            return {"length": length, "values": [
                self.tagged(cursor, tag if tag in "BCDFIJSZ" else None) for _ in range(count)]}
        reference = self.request(9, 1, data)
        reference.number(1)
        klass = reference.number(self.type_size)
        fields = self.request(2, 4, klass.to_bytes(self.type_size, "big"))
        members = []
        for _ in range(fields.number()):
            field_id, name, signature, modifiers = (
                fields.number(self.field_size), fields.text(), fields.text(), fields.number())
            if not modifiers & 8:
                members.append((field_id, name, signature))
        selected = members[offset:offset+limit]
        values = self.request(9, 2, data + u4(len(selected)) + b"".join(
            field_id.to_bytes(self.field_size, "big") for field_id, _, _ in selected))
        if values.number() != len(selected):
            raise JavaLost("JDWP returned a mismatched object field count")
        return {"class": self.class_details(klass)[0], "total": len(members),
                "fields": [{"name": name, "type": signature, "value": self.tagged(values)}
                           for _, name, signature in selected]}

    def inspect(self, query, thread_id="", index=0, offset=0, limit=32):
        status = self.status()
        if query == "status":
            return status
        if query == "breakpoints":
            return {"breakpoints": [self.breakpoint_info(i) for i in list(self.breakpoints)[offset:offset+limit]]}
        if query == "threads":
            cursor = self.request(1, 4)
            ids = [cursor.number(self.object_size) for _ in range(cursor.number())]
            return {"threads": [{"threadId": str(t), "name": self.request(
                11, 1, t.to_bytes(self.object_size, "big")).text()} for t in ids[offset:offset+limit]]}
        if status["state"] != "stopped":
            raise AndroidError("The Java VM must be suspended")
        thread = self.thread_bytes(thread_id)
        if query == "stack":
            return {"frames": [{"index": offset+i, **self.describe(location)}
                               for i, (_, location) in enumerate(self.frames(thread, offset, limit))]}
        frames = self.frames(thread, index, 1)
        if not frames:
            raise ValueError("Java stack frame is unavailable")
        frame, (_, klass, method, code) = frames[0]
        try:
            cursor = self.request(6, 2, self.method_data(klass, method))
        except JdwpError as error:
            if error.code in (101, 511):
                return {"variables": [], "availability": "debug_info_missing"}
            raise
        cursor.number()
        variables = []
        for _ in range(cursor.number()):
            start, name, signature, length, slot = (
                cursor.number(8), cursor.text(), cursor.text(), cursor.number(), cursor.number())
            if start <= code < start + length:
                variables.append((name, signature, slot))
        variables = variables[offset:offset+limit]
        if not variables:
            return {"variables": []}
        payload = thread + frame.to_bytes(self.frame_size, "big") + u4(len(variables))
        payload += b"".join(u4(slot) + signature[0].encode("ascii") for _, signature, slot in variables)
        values = self.request(16, 1, payload)
        if values.number() != len(variables):
            raise JavaLost("JDWP returned a mismatched local-variable count")
        return {"variables": [{"name": name, "type": signature, "value": self.tagged(values)}
                              for name, signature, _ in variables]}

    def detach(self):
        try:
            if self.state not in {"exited", "detached"}:
                self.request(1, 6)
        finally:
            self.close()

    def close(self):
        self.socket.close()
        self.state, self.stop = "detached", None
