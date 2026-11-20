"""Provision a private Windows x64 ADB/LLDB runtime from Google archives."""
from contextlib import contextmanager
from dataclasses import dataclass
import hashlib
import os
from pathlib import Path, PurePosixPath
import platform
import shutil
import subprocess
import tempfile
from urllib.request import urlopen
import zipfile


REPOSITORY = "https://dl.google.com/android/repository/"
NDK = "android-ndk-r27c"
PLATFORM_TOOLS = "platform-tools_r37.0.3-win"
RUNTIME_VERSION = "r27c-adb37.0.3-win64"
LLVM = Path("llvm")
ARCHES = {"arm64-v8a": "aarch64", "armeabi-v7a": "arm", "x86": "i386",
          "x86_64": "x86_64", "riscv64": "riscv64"}
SERVER_DIR = LLVM / "lib/clang/18/lib/linux"
CREATE_FLAGS = getattr(subprocess, "CREATE_NO_WINDOW", 0)

# Windows archive sizes and SHA-256, pinned after validating Google's published
# SHA-1 checksums in repository2-3.xml.
ARCHIVES = {
    NDK + "-windows.zip": (781511249, "27e49f11e0cee5800983d8af8f4acd5bf09987aa6f790d4439dda9f3643d2494"),
    PLATFORM_TOOLS + ".zip": (8188601, "89d108360a3bf2924d3bea837239edaccc22fd6ff5befd3787c4348a9fe4f09d"),
}


class AndroidError(RuntimeError):
    pass


@dataclass(frozen=True)
class Runtime:
    root: Path

    @property
    def adb(self):
        return self.root / "platform-tools/adb.exe"

    @property
    def lldb(self):
        return self.root / LLVM / "bin/lldb.exe"

    def server(self, abi):
        return self.root / SERVER_DIR / ARCHES[abi] / "lldb-server"

    def lldb_env(self):
        python = str(self.root / LLVM / "python3")
        env = {key: value for key, value in os.environ.items()
               if not key.upper().startswith("PYTHON")}
        env.update(PYTHONHOME=python, PYTHONNOUSERSITE="1", PYTHONDONTWRITEBYTECODE="1")
        env["PATH"] = python + os.pathsep + env.get("PATH", "")
        return env

    def check(self):
        required = [self.adb, self.lldb, self.root / LLVM / "bin/liblldb.dll",
                    self.root / "platform-tools/AdbWinApi.dll",
                    self.root / "platform-tools/AdbWinUsbApi.dll",
                    self.root / LLVM / "python3/python311.dll",
                    self.root / LLVM / "lib/python3.11/site-packages/lldb/__init__.py"]
        required.extend(self.server(abi) for abi in ARCHES)
        missing = next((path for path in required if not path.is_file()), None)
        if missing:
            raise AndroidError(f"Missing private Android runtime file {missing}. "
                               "Run Install-AIDebugger.cmd --backend android.")
        return self

    def smoke(self):
        self.check()
        for command, expected, env in [
            ([self.adb, "version"], b"Android Debug Bridge version", None),
            ([self.lldb, "--no-lldbinit", "--batch", "-o",
              "script print(lldb.SBDebugger.GetVersionString())"], b"lldb version", self.lldb_env()),
        ]:
            result = subprocess.run(command, env=env, timeout=20, capture_output=True,
                                    stdin=subprocess.DEVNULL, creationflags=CREATE_FLAGS)
            if result.returncode or expected not in result.stdout:
                detail = result.stderr.decode("utf-8", "replace").strip()
                raise AndroidError(f"Private runtime smoke test failed for {command[0]}: {detail[-2048:]}")
        return self


def destination(install_root):
    return install_root / "runtime" / RUNTIME_VERSION


@contextmanager
def verified_archive(name, archives, temporary):
    size, checksum = ARCHIVES[name]
    if archives is None:
        path = temporary / name
        with urlopen(REPOSITORY + name, timeout=60) as reply, path.open("wb") as output:
            remaining = size
            while chunk := reply.read(min(1024 * 1024, remaining + 1)):
                remaining -= len(chunk)
                if remaining < 0:
                    raise AndroidError(f"Download exceeds published size: {name}")
                output.write(chunk)
    else:
        path = archives / name
    with path.open("rb") as stream:
        if os.fstat(stream.fileno()).st_size != size or hashlib.file_digest(stream, "sha256").hexdigest() != checksum:
            raise AndroidError(f"Android archive differs from the pinned official release: {name}")
        with zipfile.ZipFile(stream) as package:
            yield package


def runtime_member(name):
    path = PurePosixPath(name)
    if path.is_absolute() or ".." in path.parts or any(
            "\\" in part or ":" in part for part in path.parts):
        raise AndroidError("Invalid Android archive path")
    if path.parts[:1] == ("platform-tools",):
        if path.name in {"adb.exe", "AdbWinApi.dll", "AdbWinUsbApi.dll", "NOTICE.txt",
                         "source.properties"} and len(path.parts) == 2:
            return path
        return None
    prefix = (NDK, "toolchains", "llvm", "prebuilt", "windows-x86_64")
    if path.parts[:5] != prefix:
        if path.parts[:1] == (NDK,) and len(path.parts) == 2 and (
                path.name.startswith(("NOTICE", "LICENSE")) or path.name == "source.properties"):
            return PurePosixPath("ndk-notices", path.name)
        return None
    relative = PurePosixPath(*path.parts[5:])
    if (relative.parent == PurePosixPath("bin") and (
            relative.name in {"lldb.exe", "lldb-argdumper.exe"} or relative.suffix == ".dll")
            or relative.parts[:1] == ("python3",) and relative.parts[1:2] not in {("include",), ("libs",)}
            or relative.parts[:3] == ("lib", "python3.11", "site-packages")
            or relative.name == "lldb-server" and relative.parent.name in ARCHES.values()
            and relative.parts[:4] == ("lib", "clang", "18", "lib")):
        return PurePosixPath("llvm") / relative
    return None


def provision(install_root, *, accept_licenses=False, archives=None):
    if os.name != "nt" or platform.machine().lower() not in {"amd64", "x86_64"}:
        raise AndroidError("The managed Android debugger runtime requires Windows x64")
    root = destination(install_root)
    if root.exists():
        try:
            return Runtime(root).smoke()
        except (AndroidError, OSError, subprocess.SubprocessError) as error:
            raise AndroidError(f"Cached Android runtime failed validation. Move {root} aside "
                               f"and rerun the installer: {error}") from error
    if archives is None and not accept_licenses:
        raise AndroidError("Review Google's Platform-Tools and NDK terms, then use "
                           "--accept-android-licenses or provide official ZIPs with --android-archives.")
    root.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".android-", dir=root.parent) as name:
        temporary = Path(name)
        stage = temporary / "runtime"
        for archive in ARCHIVES:
            with verified_archive(archive, archives, temporary) as package:
                for member in package.infolist():
                    if member.is_dir():
                        continue
                    relative = runtime_member(member.filename)
                    if relative is None:
                        continue
                    output = stage.joinpath(*relative.parts)
                    output.parent.mkdir(parents=True, exist_ok=True)
                    with package.open(member) as source, output.open("xb") as target:
                        shutil.copyfileobj(source, target)
        Runtime(stage).smoke()
        try:
            stage.rename(root)
        except OSError:
            if not root.is_dir():
                raise
            return Runtime(root).smoke()
    return Runtime(root)
