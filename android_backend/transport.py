"""ADB target binding and a private LLDB worker per Android session."""
import hashlib
import json
import os
from pathlib import Path
import queue
import re
import shlex
import socket
import subprocess
import tempfile
import threading
import time
from uuid import uuid4

from .runtime import AndroidError, ARCHES, CREATE_FLAGS
from .jobs import JobStopped, checkpoint, current_job
from .jdwp import JavaSession, JavaLost


PACKAGE = re.compile(r"[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z][A-Za-z0-9_]*)+")
SUFFIX = re.compile(r"[A-Za-z0-9_.]+")
ADB_SERVER_OVERRIDES = {"ADB_SERVER_SOCKET", "ANDROID_ADB_SERVER_ADDRESS", "ANDROID_ADB_SERVER_PORT"}


def process_name(package, process):
    if not PACKAGE.fullmatch(package):
        raise ValueError("Expected an Android application ID")
    if not process or process == package:
        return package
    if process.startswith(package + ":") and SUFFIX.fullmatch(process.split(":", 1)[1]):
        return process
    raise ValueError("Process must be the package or a package:subprocess name")


def paths(symbol_dirs, source_map):
    if len(symbol_dirs) > 32 or len(source_map) > 32:
        raise ValueError("Use at most 32 symbol directories and 32 source mappings")

    def directory(path):
        value = Path(path)
        if not value.is_absolute() or not value.is_dir():
            raise ValueError(f"Expected an existing absolute directory: {path}")
        return value.resolve().as_posix()

    directories = [directory(path) for path in symbol_dirs]
    mapping = {source: directory(local) for source, local in source_map.items()}
    if any(not path or len(path) > 4096 or "`" in path or any(ord(c) < 32 for c in path)
           for path in (*directories, *mapping, *mapping.values())):
        raise ValueError("Invalid LLDB symbol directory or source mapping")
    return directories, mapping


def process_start_time(stat):
    # The comm field can contain spaces and closing parentheses.
    _, separator, fields = stat.rpartition(") ")
    rest = fields.split()
    if not separator or len(rest) < 20 or not rest[19].isascii() or not rest[19].isdecimal():
        raise AndroidError("Could not verify the Android process identity")
    return rest[19]


class Adb:
    def __init__(self, runtime, transport_id=None):
        self.command = [str(runtime.adb)]
        # LLDB consumes the forwarding port on this host's loopback interface.
        self.env = {key: value for key, value in os.environ.items()
                    if key.upper() not in ADB_SERVER_OVERRIDES}
        self.transport_id = transport_id
        if transport_id is not None:
            self.command.extend(("-t", transport_id))

    def run(self, *args, timeout=10, allow_failure=False):
        options = dict(stdin=subprocess.DEVNULL, creationflags=CREATE_FLAGS, env=self.env)
        command = [*self.command, *args]
        try:
            if current_job.get() is None:
                result = subprocess.run(command, capture_output=True, timeout=timeout, **options)
            else:
                checkpoint()
                deadline = time.monotonic() + timeout
                with subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, **options) as process:
                    try:
                        while True:
                            remaining = min(checkpoint(), deadline - time.monotonic())
                            if remaining <= 0:
                                raise subprocess.TimeoutExpired(command, timeout)
                            try:
                                out, err = process.communicate(timeout=min(remaining, 0.2))
                                break
                            except subprocess.TimeoutExpired:
                                pass
                    except (JobStopped, subprocess.TimeoutExpired):
                        process.kill()
                        process.communicate()
                        raise
                    result = subprocess.CompletedProcess(command, process.returncode, out, err)
        except subprocess.TimeoutExpired as error:
            raise AndroidError(f"ADB {args[0]} timed out; device state may have changed") from error
        out = result.stdout.decode("utf-8", "replace").strip()
        if result.returncode and not allow_failure:
            detail = result.stderr.decode("utf-8", "replace").strip() or out
            raise AndroidError(f"ADB {args[0]} failed: {detail[-2048:]}")
        return result.returncode, out

    def devices(self):
        _, output = self.run("devices", "-l")
        devices = []
        for line in output.splitlines():
            fields = line.split()
            if line.startswith("*") or len(fields) < 2 or fields[:2] == ["List", "of"]:
                continue
            props = dict(part.split(":", 1) for part in fields[2:] if ":" in part)
            devices.append({"serial": fields[0], "state": fields[1], "model": props.get("model"),
                            "transportId": props.get("transport_id")})
        return devices

    def shell(self, *words, **options):
        return self.run("shell", "-T", shlex.join(words), **options)

    @staticmethod
    def read_service(sock, size):
        data = bytearray()
        deadline = time.monotonic() + 2
        while len(data) < size:
            remaining = min(deadline - time.monotonic(), checkpoint())
            if remaining <= 0:
                raise AndroidError("ADB device service did not respond")
            sock.settimeout(remaining)
            chunk = sock.recv(size - len(data))
            if not chunk:
                raise AndroidError("ADB closed the device service")
            data.extend(chunk)
        return bytes(data)

    def open_device_service(self, service):
        sock = socket.create_connection(("127.0.0.1", 5037), timeout=min(2, checkpoint()))
        try:
            for command in (f"host:transport-id:{self.transport_id}", service):
                sock.settimeout(min(2, checkpoint()))
                request = command.encode("ascii")
                sock.sendall(f"{len(request):04x}".encode("ascii") + request)
                if self.read_service(sock, 4) != b"OKAY":
                    raise AndroidError(f"ADB rejected {command}")
            return sock
        except BaseException:
            sock.close()
            raise

    def jdwp_pids(self):
        # The adb jdwp CLI streams indefinitely. Read one framed snapshot.
        with self.open_device_service("track-jdwp") as sock:
            size = int(self.read_service(sock, 4), 16)
            return self.read_service(sock, size).decode("ascii").split()

    def start_shell(self, *words, output):
        return subprocess.Popen([*self.command, "shell", "-T", shlex.join(words)],
                                stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=output,
                                creationflags=CREATE_FLAGS, env=self.env)


class WorkerLost(AndroidError):
    """The last LLDB operation may have run before the worker stopped replying."""


class LldbWorker:
    def __init__(self, runtime):
        env = runtime.lldb_env()
        env["AIDB_ANDROID_WORKER"] = str(Path(__file__).with_name("lldb_worker.py").resolve())
        env["AIDB_ANDROID_REPLY_TOKEN"] = uuid4().hex
        self.marker = ("AIDB_LLDB " + env["AIDB_ANDROID_REPLY_TOKEN"] + " ").encode("ascii")
        self.process = subprocess.Popen(
            [runtime.lldb, "--no-lldbinit", "--batch", "-o",
             'script __import__("runpy").run_path(__import__("os").environ["AIDB_ANDROID_WORKER"])'],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            env=env, creationflags=CREATE_FLAGS)
        self.replies = queue.Queue()
        self.requests = queue.Queue(maxsize=1)
        self.reader = threading.Thread(target=self.read, daemon=True)
        self.writer = threading.Thread(target=self.write, daemon=True)
        self.reader.start()
        self.writer.start()
        try:
            if not self.receive(15).get("ready"):
                raise WorkerLost("LLDB worker did not initialize")
        except BaseException:
            self.close()
            raise

    def read(self):
        detail = ""
        try:
            for line in self.process.stdout:
                if line.startswith(self.marker):
                    payload = json.loads(line[len(self.marker):])
                    if not isinstance(payload, dict) or len(payload) != 1 or not (
                            set(payload) <= {"ready", "result", "error"}):
                        raise ValueError("Invalid LLDB worker response")
                    self.replies.put(payload)
                elif line.strip():
                    detail = line[-2048:].decode("utf-8", "replace").strip()
        except (OSError, ValueError) as error:
            detail = str(error)
        finally:
            self.replies.put({"lost": detail or "LLDB worker exited"})

    def write(self):
        while (request := self.requests.get()) is not None:
            try:
                remaining = memoryview(request)
                while remaining:
                    size = self.process.stdin.raw.write(remaining)
                    if not size:
                        raise OSError("LLDB worker pipe closed")
                    remaining = remaining[size:]
            except (OSError, ValueError) as error:
                self.replies.put({"lost": str(error)})
                return

    def receive(self, timeout):
        deadline = time.monotonic() + timeout
        while True:
            try:
                remaining = min(checkpoint(), deadline - time.monotonic())
            except JobStopped as error:
                raise WorkerLost(f"{error}; LLDB operation outcome is unknown") from error
            if remaining <= 0:
                raise WorkerLost("LLDB response timed out; last operation outcome is unknown")
            try:
                reply = self.replies.get(timeout=min(remaining, 0.1))
                break
            except queue.Empty:
                continue
        if "lost" in reply:
            raise WorkerLost(reply["lost"])
        return reply

    def call(self, operation, **arguments):
        request = json.dumps({"operation": operation, "arguments": arguments}).encode("ascii")
        checkpoint()
        self.requests.put_nowait(request + b"\n")
        try:
            reply = self.receive(25)
        except WorkerLost as error:
            if operation not in {"inspect", "snapshot"} or not isinstance(error.__cause__, JobStopped):
                raise
            # A read-only probe can be cancelled without losing the session if
            # its reply can still be drained before accepting another command.
            token = current_job.set(None)
            try:
                self.receive(0.3)
            except WorkerLost:
                raise error
            finally:
                current_job.reset(token)
            raise error.__cause__
        if "error" in reply:
            raise AndroidError(reply["error"])
        if "result" not in reply:
            raise WorkerLost("Unexpected LLDB reply; last operation outcome is unknown")
        return reply["result"]

    def close(self):
        try:
            self.process.stdin.close()
        except OSError:
            pass
        try:
            self.process.wait(timeout=3)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait(timeout=3)
        self.reader.join(timeout=3)
        try:
            self.requests.put_nowait(None)
        except queue.Full:
            pass
        self.writer.join(timeout=1)
        self.process.stdout.close()


class Connection:
    def __init__(self, runtime, session_id, serial, package, process, pid, abi, symbol_dirs, source_map):
        self.runtime = runtime
        self.serial = serial
        self.package = package
        self.process_name = process
        self.pid = pid
        self.abi = abi
        self.symbol_dirs = symbol_dirs
        self.source_map = source_map
        self.adb = None
        self.stub = None
        self.stub_output = None
        self.worker = None
        self.port = None
        self.forward_requested = False
        self.socket = "/aidb-lldb-" + session_id
        self.forward = "localabstract:" + self.socket
        self.remote = None
        self.staging = None
        self.stub_reader = None
        self.stub_pid = None
        self.sysroot = None
        self.sysroot_path = None
        self.symbol_cache = None
        self.symbol_cache_hit = False
        self.attach_requested = False
        self.activity = ""
        self.mode = "native"
        self.java_breakpoints = []
        self.native_breakpoints = []
        self.java = None
        self.java_error = None
        self.startup_released = None
        self.startup_native_stop = None
        self.boot_id = ""
        self.start_time = ""

    def check_device(self):
        if self.adb.run("get-serialno")[1] != self.serial:
            raise AndroidError("ADB device transport no longer matches the selected serial")

    def recover_read_transport(self):
        if self.attach_requested or self.java or not self.boot_id:
            return
        checkpoint("recheck_read_transport")
        deadline = time.monotonic() + 3
        while True:
            device = next((d for d in Adb(self.runtime).devices() if d["serial"] == self.serial
                           and d["state"] == "device" and (d["transportId"] or "").isdigit()), None)
            if device:
                break
            if time.monotonic() >= deadline:
                raise AndroidError("The selected Android device disconnected during symbol preparation")
            time.sleep(min(0.15, checkpoint()))
        candidate = Adb(self.runtime, device["transportId"])
        if (candidate.run("get-serialno")[1] != self.serial or
                candidate.shell("cat", "/proc/sys/kernel/random/boot_id")[1] != self.boot_id):
            raise AndroidError("The Android device rebooted or changed during symbol preparation")
        _, stat = candidate.shell("run-as", self.package, "cat", f"/proc/{self.pid}/stat")
        if process_start_time(stat) != self.start_time:
            raise AndroidError("The Android process changed during symbol preparation")
        self.adb = candidate

    def pull_symbol(self, remote, local, *, allow_missing=False):
        try:
            return self.adb.run("pull", remote, str(local), allow_failure=allow_missing)
        except AndroidError as error:
            if isinstance(error, JobStopped) or self.attach_requested or self.java or not self.boot_id:
                raise
            # Pulling an ELF file only writes into this private staging directory.
            self.recover_read_transport()
            checkpoint("prepare_symbols")
            return self.adb.run("pull", remote, str(local), allow_failure=allow_missing)

    def stage_sysroot(self):
        is_64bit = self.abi in {"arm64-v8a", "x86_64", "riscv64"}
        binary = "app_process64" if is_64bit else "app_process32"
        linker = "linker64" if is_64bit else "linker"
        cached = None
        if self.symbol_cache:
            _, fingerprint = self.adb.shell("getprop", "ro.build.fingerprint")
            if fingerprint:
                key = hashlib.sha256(f"{self.serial}\0{fingerprint}\0{self.abi}".encode()).hexdigest()
                cached = self.symbol_cache / key
                bin_dir = cached / "system/bin"
                if (bin_dir / binary).is_file() and (bin_dir / linker).is_file():
                    self.symbol_cache_hit = True
                    self.sysroot_path = cached
                    self.symbol_dirs.append(bin_dir.as_posix())
                    return (bin_dir / binary).as_posix()
        self.sysroot = tempfile.TemporaryDirectory(prefix="aidb-android-symbols-", dir=self.symbol_cache)
        root = Path(self.sysroot.name)
        bin_dir = root / "system/bin"
        bin_dir.mkdir(parents=True)
        executable = bin_dir / binary
        code, _ = self.pull_symbol(f"/system/bin/{executable.name}", executable, allow_missing=not is_64bit)
        if code:
            self.pull_symbol("/system/bin/app_process", executable)
        self.pull_symbol(f"/system/bin/{linker}", bin_dir / linker)
        if cached:
            try:
                root.rename(cached)
            except OSError:
                if not (cached / "system/bin" / binary).is_file() or not (cached / "system/bin" / linker).is_file():
                    raise
            root, bin_dir = cached, cached / "system/bin"
            executable = bin_dir / binary
        self.sysroot_path = root
        self.symbol_dirs.append(bin_dir.as_posix())
        return executable.as_posix()

    def resolve_pids(self, wait=False):
        while True:
            code, output = self.adb.shell("pidof", self.process_name, allow_failure=True)
            pids = output.split()
            if not code and pids and all(re.fullmatch(r"[1-9][0-9]*", pid) for pid in pids):
                return pids
            if not wait:
                return []
            self.check_device()
            time.sleep(min(0.1, checkpoint()))

    def attach(self):
        checkpoint("resolve_device")
        device = next((item for item in Adb(self.runtime).devices() if item["serial"] == self.serial), None)
        if device is None or device["state"] != "device" or not (device["transportId"] or "").isdigit():
            raise AndroidError("Requested Android device is unavailable or unauthorized")
        self.adb = Adb(self.runtime, device["transportId"])
        self.check_device()
        _, cwd = self.adb.shell("run-as", self.package, "pwd")
        if not (cwd in {f"/data/user/0/{self.package}", f"/data/data/{self.package}"}
                or re.fullmatch(r"/data/user/\d+/" + re.escape(self.package), cwd)):
            raise AndroidError("Android run-as did not return the application data directory")
        if self.activity:
            checkpoint("launch_activity")
            if self.resolve_pids():
                raise AndroidError("Startup debugging requires the requested app process to be stopped")
            self.check_device()
            self.attach_requested = True
            _, launched = self.adb.shell("am", "start", "-D", "-n", self.activity)
            if "Error:" in launched or "Warning: Activity not started" in launched:
                raise AndroidError(f"Android did not start the requested activity: {launched}")
            checkpoint("wait_for_launched_process")
        pids = self.resolve_pids(wait=bool(self.activity))
        if not pids:
            raise AndroidError("Start the requested Android app process before attaching")
        if self.pid is None:
            if len(pids) != 1:
                raise AndroidError("Multiple matching Android processes; specify pid")
            self.pid = int(pids[0])
        elif str(self.pid) not in pids:
            raise AndroidError("PID does not belong to the requested Android process name")
        _, stat = self.adb.shell("run-as", self.package, "cat", f"/proc/{self.pid}/stat")
        start_time = process_start_time(stat)
        self.start_time = start_time
        if not self.activity and self.mode == "native":
            code, boot_id = self.adb.shell("cat", "/proc/sys/kernel/random/boot_id", allow_failure=True)
            if code == 0:
                self.boot_id = boot_id
        if self.activity or self.mode != "native":
            self.connect_java(start_time)
            if self.mode == "java":
                return {"pid": self.pid, "mode": self.mode, "java": self.java.status()}
        _, device_abis = self.adb.shell("getprop", "ro.product.cpu.abilist")
        abis = device_abis.split(",")
        if not self.abi:
            _, info = self.adb.shell("dumpsys", "package", self.package)
            match = re.search(r"^\s*primaryCpuAbi=(\S+)", info, re.MULTILINE)
            self.abi = match[1] if match and match[1] != "null" else abis[0]
        if self.abi not in ARCHES or self.abi not in abis:
            raise AndroidError(f"Unsupported Android process ABI: {self.abi!r}; device ABI list: {device_abis}")
        checkpoint("prepare_symbols")
        executable = self.stage_sysroot()
        self.worker = LldbWorker(self.runtime)
        self.worker.call("prepare", symbol_dirs=self.symbol_dirs, source_map=self.source_map,
                         executable=executable, sysroot=self.sysroot_path.as_posix())
        self.check_device()
        checkpoint("deploy_lldb_server")
        self.remote = cwd + "/." + self.socket[1:]
        self.staging = "/data/local/tmp/" + self.socket[1:]
        server = self.runtime.server(self.abi)
        self.adb.run("push", str(server), self.staging, timeout=30)
        copy = ("cat " + shlex.quote(self.staging) + " | " +
                shlex.join(("run-as", self.package, "sh", "-c",
                            "umask 077; set -C; cat > " + shlex.quote(self.remote))))
        self.adb.run("shell", "-T", copy, timeout=15)
        _, size = self.adb.shell("run-as", self.package, "stat", "-c", "%s", self.remote)
        if size != str(server.stat().st_size):
            raise AndroidError("Staged device lldb-server has the wrong size")
        self.adb.shell("rm", "-f", self.staging)
        self.staging = None
        self.adb.shell("run-as", self.package, "chmod", "700", self.remote)
        self.forward_requested = True
        _, port = self.adb.run("forward", "tcp:0", self.forward)
        if not port.isdecimal() or not 1 <= int(port) <= 65535:
            raise AndroidError("ADB did not report its allocated forwarding port")
        self.port = int(port)
        self.check_device()
        if str(self.pid) not in self.resolve_pids():
            raise AndroidError("The requested Android process exited before attach")
        self.stub_output = tempfile.TemporaryFile()
        checkpoint("attach_lldb_server")
        self.attach_requested = True
        verify = ('verify() { stat=$(cat "/proc/$1/stat") || return 1; '
                  'set -f; set -- ${stat##*) }; [ "${20}" = "$start" ]; }; '
                  'start=$2; verify "$1" || { echo "Android PID changed before attach" >&2; exit 1; }; '
                  'shift 2; printf "%s\\n" "$$"; exec "$@"')
        self.stub = self.adb.start_shell("run-as", self.package, "sh", "-c", verify, "aidb",
                                         str(self.pid), start_time,
                                         self.remote, "gdbserver",
                                         "--pipe", "1", "unix-abstract://" + self.socket,
                                         "--attach", str(self.pid),
                                         output=self.stub_output)
        ready = queue.Queue()

        def read_ready():
            packet = bytearray()
            try:
                pid = self.stub.stdout.readline(32).strip()
                if not pid.isdigit() or int(pid) <= 1:
                    ready.put(False)
                    return
                self.stub_pid = pid.decode("ascii")
                while len(packet) <= 256:
                    byte = self.stub.stdout.read(1)
                    if byte in (b"", b"\x00"):
                        ready.put(byte == b"\x00" and packet == self.socket.encode("ascii"))
                        return
                    packet.extend(byte)
            except OSError:
                pass
            ready.put(False)

        self.stub_reader = threading.Thread(target=read_ready, daemon=True)
        self.stub_reader.start()
        deadline = time.monotonic() + 8
        listening = False
        while time.monotonic() < deadline:
            try:
                listening = ready.get(timeout=min(checkpoint(), 0.1))
                break
            except queue.Empty:
                pass
        if listening:
            checkpoint("connect_lldb")
            self.check_device()
            if f"tcp:{self.port}" not in self.forwarded_ports():
                raise AndroidError("The Android LLDB forwarding registration changed before connect")
            result = self.worker.call("connect", port=self.port, pid=self.pid)
            result["systemSymbolsCacheHit"] = self.symbol_cache_hit
            if self.native_breakpoints:
                result["breakpoints"] = [self.worker.call("set_breakpoint", **bp)
                                         for bp in self.native_breakpoints]
            if self.java:
                result.update(mode=self.mode, java=self.java.status(poll=False))
                if self.java.gate:
                    self.startup_native_stop = result["stopId"]
            return result
        self.stub_output.seek(0)
        detail = self.stub_output.read(2048).decode("utf-8", "replace").strip()
        raise AndroidError("Device lldb-server did not create a socket: " + detail)

    def connect_java(self, start_time):
        checkpoint("wait_for_jdwp")
        while True:
            if str(self.pid) in self.adb.jdwp_pids():
                break
            if not self.activity:
                raise AndroidError("The selected Android PID is not advertised by JDWP")
            time.sleep(min(0.1, checkpoint()))
        _, stat = self.adb.shell("run-as", self.package, "cat", f"/proc/{self.pid}/stat")
        if process_start_time(stat) != start_time:
            raise AndroidError("Android PID changed before JDWP attach")
        checkpoint("connect_jdwp")
        self.check_device()
        self.attach_requested = True
        sock = self.adb.open_device_service(f"jdwp:{self.pid}")
        self.java = JavaSession(sock)
        if self.activity:
            self.invoke_java(self.java.pause)
            self.java.gate = True
        for bp in self.java_breakpoints:
            self.invoke_java(self.java.add_breakpoint, **bp)

    def native_control(self, action, thread_id=0):
        if self.worker is None:
            raise AndroidError("No native debugger is attached to this session")
        gate = self.mode == "native" and self.java and self.java.gate
        if gate and action.startswith("step_"):
            raise AndroidError("Continue once to release the startup gate before stepping")
        result = self.worker.call("control", action=action, thread_id=thread_id)
        if action == "continue" and gate and result["state"] == "running":
            # Android's startup wait requires a connected, settled debugger.
            # Keep the private JDWP connection until native detach.
            self.invoke_java(self.java.resume)
            self.java.gate = False
            self.startup_released = time.monotonic()
        return result

    def java_operation(self, operation, **arguments):
        if not self.java or self.mode == "native":
            raise AndroidError("This Android session does not expose a Java debugger")
        if self.java_error:
            if operation == "inspect" and arguments["query"] == "status":
                return {**self.java.status(poll=False), "transportError": self.java_error}
            raise AndroidError(self.java_error)
        if self.worker:
            native = self.worker.call("inspect", query="status")
            if native["state"] in {"stopped", "crashed", "suspended"}:
                if operation == "inspect" and arguments["query"] == "status":
                    return {**self.java.status(poll=False), "blockedByNative": True, "nativeStatus": native}
                release_startup = (operation == "control" and arguments.get("release_native_startup")
                                   and self.java.gate and native["stopId"] == self.startup_native_stop)
                if not release_startup:
                    raise AndroidError("LLDB has paused the process; continue the native session before using JDWP")
                resumed = self.native_control("continue")
                if resumed["state"] != "running":
                    return {"outcome": "native_stop", **self.worker.call("snapshot")}
        operations = {"inspect": self.java.inspect, "control": self.java_control,
                      "set_breakpoint": self.java.add_breakpoint, "remove_breakpoint": self.java.remove_breakpoint,
                      "exceptions": self.java.exceptions, "object": self.java.object, "snapshot": self.java_snapshot}
        return self.invoke_java(operations[operation], **arguments)

    def invoke_java(self, operation, **arguments):
        try:
            return operation(**arguments)
        except (JavaLost, OSError) as error:
            # A partial protocol exchange cannot be replayed.
            self.java.close()
            raise WorkerLost(str(error)) from (error.__cause__ or error)

    def poll_java_events(self):
        if self.worker:
            native = self.worker.call("inspect", query="status")
            if native["state"] != "running":
                return
        self.java.sync()

    def java_control(self, action, thread_id, wait_ms, release_native_startup=False):
        previous = self.java.status()["stopId"]
        status = self.java.control(action, thread_id)
        if self.java.gate and action != "pause":
            self.java.gate = False
            self.startup_released = time.monotonic()
        if not wait_ms:
            return status
        checkpoint("waiting_for_java_stop")
        deadline = time.monotonic() + wait_ms / 1000
        while True:
            if self.worker:
                native = self.worker.call("inspect", query="status")
                if native["state"] in {"stopped", "crashed", "suspended", "exited", "detached"}:
                    return {"outcome": "native_stop", **self.worker.call("snapshot")}
            status = self.java.status()
            if status["state"] == "exited":
                return {"outcome": "exited", "status": status}
            if status["state"] == "stopped" and (status["stopId"] > previous or action == "pause"):
                checkpoint("capture_java_stop")
                return {"outcome": "java_stop", "status": status, **self.java_snapshot(status)}
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                return {"outcome": "wait_timeout", "status": status}
            time.sleep(min(remaining, checkpoint(), 0.05))

    def java_snapshot(self, status):
        if not status.get("threadId"):
            return {}
        return {"stack": self.java.inspect("stack", limit=12)["frames"],
                "locals": self.java.inspect("locals")["variables"]}

    def detach(self):
        result = self.worker.call("detach") if self.worker else {"state": "detached"}
        if self.java:
            self.invoke_java(self.detach_java)
            self.java = None
        return result

    def detach_java(self):
        if self.java.gate and self.java.state == "stopped":
            self.java.resume()
            self.java.gate = False
            self.startup_released = time.monotonic()
        if self.startup_released is not None:
            # ART's waitForDebugger needs the connection to stay quiet after
            # resume before allowing startup. Keep it alive for that window.
            remaining = self.startup_released + 1.6 - time.monotonic()
            while remaining > 0:
                time.sleep(min(remaining, checkpoint(), 0.1))
                remaining = self.startup_released + 1.6 - time.monotonic()
        self.java.detach()

    def cleanup(self):
        errors = []

        def attempt(call, *args):
            try:
                call(*args)
                return True
            except (AndroidError, OSError, subprocess.SubprocessError) as error:
                errors.append(str(error))
                return False

        if self.worker:
            attempt(self.worker.close)
        if self.java:
            if not attempt(self.invoke_java, self.detach_java):
                self.java.close()
        device_matches = ((self.remote or self.staging) and attempt(self.check_device))
        stub_released = not self.stub_pid
        if device_matches and self.stub_pid:
            try:
                self.stub.wait(timeout=1)
            except subprocess.TimeoutExpired:
                pass
            # The local ADB client can exit while its device process survives.
            stub_released = attempt(self.stop_stub)
        if self.stub and self.stub.poll() is None:
            attempt(self.stub.terminate)
            attempt(self.stub.wait, 3)
        if self.stub_reader:
            self.stub_reader.join(timeout=3)
        if self.stub:
            self.stub.stdout.close()
        if self.stub_output:
            self.stub_output.close()
        if device_matches and self.remote:
            if self.forward_requested:
                attempt(self.remove_forward)
            if stub_released:
                attempt(self.adb.shell, "run-as", self.package, "rm", "-f", self.remote)
        if device_matches and self.staging:
            attempt(self.adb.shell, "rm", "-f", self.staging)
        if self.sysroot:
            attempt(self.sysroot.cleanup)
        return errors

    def forwarded_ports(self):
        _, output = self.adb.run("forward", "--list")
        ports = []
        for entry in output.splitlines():
            fields = entry.split()
            if len(fields) == 3 and fields[0] == self.serial and fields[2] == self.forward:
                ports.append(fields[1])
        return ports

    def remove_forward(self):
        for port in self.forwarded_ports():
            if self.port is None or port == f"tcp:{self.port}":
                self.adb.run("forward", "--remove", port)

    def stop_stub(self):
        # PID reuse must never redirect a cleanup signal to an application.
        command = ('[ "$(readlink /proc/$1/exe)" = "$2" ] || exit 0; '
                   'if [ "$3" != 0 ] && [ -d "/proc/$3" ]; then '
                   'tracer=$(sed -n "s/^TracerPid:[[:space:]]*//p" "/proc/$3/status"); '
                   'if [ -z "$tracer" ] || [ "$tracer" = "$1" ]; then '
                   'echo "Owned lldb-server may still trace the app; process left running" >&2; exit 1; fi; fi; '
                   'kill -TERM "$1"')
        self.adb.shell("run-as", self.package, "sh", "-c", command, "aidb",
                       self.stub_pid, self.remote, str(self.pid if self.attach_requested else 0))
