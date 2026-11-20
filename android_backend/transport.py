"""ADB target binding and a private LLDB worker per Android session."""
import json
import os
from pathlib import Path
import queue
import re
import shlex
import subprocess
import tempfile
import threading
from uuid import uuid4

from .runtime import AndroidError, ARCHES, CREATE_FLAGS


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
        if transport_id is not None:
            self.command.extend(("-t", transport_id))

    def run(self, *args, timeout=10, allow_failure=False):
        try:
            result = subprocess.run([*self.command, *args], capture_output=True, timeout=timeout,
                                    stdin=subprocess.DEVNULL, creationflags=CREATE_FLAGS, env=self.env)
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
        self.reader = threading.Thread(target=self.read, daemon=True)
        self.reader.start()
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

    def receive(self, timeout):
        try:
            reply = self.replies.get(timeout=timeout)
        except queue.Empty as error:
            raise WorkerLost("LLDB response timed out; last operation outcome is unknown") from error
        if "lost" in reply:
            raise WorkerLost(reply["lost"])
        return reply

    def call(self, operation, **arguments):
        request = json.dumps({"operation": operation, "arguments": arguments}).encode("ascii")
        try:
            self.process.stdin.write(request + b"\n")
            self.process.stdin.flush()
        except OSError as error:
            raise WorkerLost("LLDB worker pipe closed; last operation outcome is unknown") from error
        reply = self.receive(25)
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
        self.attach_requested = False

    def check_device(self):
        if self.adb.run("get-serialno")[1] != self.serial:
            raise AndroidError("ADB device transport no longer matches the selected serial")

    def stage_sysroot(self):
        self.sysroot = tempfile.TemporaryDirectory(prefix="aidb-android-symbols-")
        root = Path(self.sysroot.name)
        is_64bit = self.abi in {"arm64-v8a", "x86_64", "riscv64"}
        bin_dir = root / "system/bin"
        bin_dir.mkdir(parents=True)
        executable = bin_dir / ("app_process64" if is_64bit else "app_process32")
        code, _ = self.adb.run("pull", f"/system/bin/{executable.name}", str(executable),
                               allow_failure=not is_64bit)
        if code:
            self.adb.run("pull", "/system/bin/app_process", str(executable))
        linker = "linker64" if is_64bit else "linker"
        self.adb.run("pull", f"/system/bin/{linker}", str(bin_dir / linker))
        self.symbol_dirs.append(bin_dir.as_posix())
        return executable.as_posix()

    def attach(self):
        device = next((item for item in Adb(self.runtime).devices() if item["serial"] == self.serial), None)
        if device is None or device["state"] != "device" or not (device["transportId"] or "").isdigit():
            raise AndroidError("Requested Android device is unavailable or unauthorized")
        self.adb = Adb(self.runtime, device["transportId"])
        self.check_device()
        _, cwd = self.adb.shell("run-as", self.package, "pwd")
        if not (cwd in {f"/data/user/0/{self.package}", f"/data/data/{self.package}"}
                or re.fullmatch(r"/data/user/\d+/" + re.escape(self.package), cwd)):
            raise AndroidError("Android run-as did not return the application data directory")
        code, candidates = self.adb.shell("pidof", self.process_name, allow_failure=True)
        pids = candidates.split() if not code else []
        if not pids or any(not pid.isdigit() for pid in pids):
            raise AndroidError("Start the requested Android app process before attaching")
        if self.pid is None:
            if len(pids) != 1:
                raise AndroidError("Multiple matching Android processes; specify pid")
            self.pid = int(pids[0])
        elif str(self.pid) not in pids:
            raise AndroidError("PID does not belong to the requested Android process name")
        _, stat = self.adb.shell("run-as", self.package, "cat", f"/proc/{self.pid}/stat")
        start_time = process_start_time(stat)
        _, device_abis = self.adb.shell("getprop", "ro.product.cpu.abilist")
        abis = device_abis.split(",")
        if not self.abi:
            _, info = self.adb.shell("dumpsys", "package", self.package)
            match = re.search(r"^\s*primaryCpuAbi=(\S+)", info, re.MULTILINE)
            self.abi = match[1] if match and match[1] != "null" else abis[0]
        if self.abi not in ARCHES or self.abi not in abis:
            raise AndroidError(f"Unsupported Android process ABI: {self.abi!r}; device ABI list: {device_abis}")
        executable = self.stage_sysroot()
        self.worker = LldbWorker(self.runtime)
        self.worker.call("prepare", symbol_dirs=self.symbol_dirs, source_map=self.source_map,
                         executable=executable, sysroot=Path(self.sysroot.name).as_posix())
        self.check_device()
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
        _, current_pids = self.adb.shell("pidof", self.process_name, allow_failure=True)
        if str(self.pid) not in current_pids.split():
            raise AndroidError("The requested Android process exited before attach")
        self.stub_output = tempfile.TemporaryFile()
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
        try:
            listening = ready.get(timeout=8)
        except queue.Empty:
            listening = False
        if listening:
            self.check_device()
            if f"tcp:{self.port}" not in self.forwarded_ports():
                raise AndroidError("The Android LLDB forwarding registration changed before connect")
            return self.worker.call("connect", port=self.port, pid=self.pid)
        self.stub_output.seek(0)
        detail = self.stub_output.read(2048).decode("utf-8", "replace").strip()
        raise AndroidError("Device lldb-server did not create a socket: " + detail)

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
