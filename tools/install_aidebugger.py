"""Install independent IDA/kernel MCP backends, preserving VS registration.

Python 3.11+. No pip, IDA plugin replacement, administrator rights or VS required.
Use --dry-run to review destinations. Existing configuration is backed up.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sys
import tempfile
import time
import tomllib

SOURCE = Path(__file__).resolve().parents[1]
SKILL = "ida-evidence-analysis"


def digest(data):
    return hashlib.sha256(data).hexdigest()


def replace_backend_toml(text, command, server, backend):
    """Replace only the selected backend's table; compare unrelated parsed data."""
    if backend not in {"ida", "kernel"}:
        raise ValueError("Unknown backend")
    original = tomllib.loads(text)
    lines = text.splitlines(keepends=True)
    result = []
    skipping = False
    for line in lines:
        if line.lstrip().startswith("["):
            # Refuse exotic quoted ownership rather than editing another table.
            match = re.match(r"\s*\[([^\]]+)\]\s*(?:#.*)?$", line.strip())
            table = match.group(1).strip() if match else ""
            skipping = table == "mcp_servers." + backend or table.startswith("mcp_servers." + backend + ".")
        if not skipping:
            result.append(line)
    section = ("\n[mcp_servers." + backend + "]\ncommand = " + json.dumps(command) +
               "\nargs = [" + json.dumps(str(server)) + "]\nenabled = true\n" +
               "startup_timeout_sec = 20\ntool_timeout_sec = 120\n")
    updated = "".join(result).rstrip() + "\n" + section
    parsed = tomllib.loads(updated)
    if parsed.get("mcp_servers", {}).get(backend, {}).get("command") != command:
        raise ValueError("Could not safely replace " + backend + " MCP table")
    # Header-like text inside multiline TOML values must never change unrelated
    # settings. Refuse edits the small table replacer cannot safely represent.
    for config in (original, parsed):
        config.get("mcp_servers", {}).pop(backend, None)
        if config.get("mcp_servers") == {}:
            config.pop("mcp_servers")
    if parsed != original:
        raise ValueError(backend + " registration would change unrelated TOML settings")
    return updated


def replace_ida_toml(text, command, server):
    return replace_backend_toml(text, command, server, "ida")


def payload_files(backend="ida"):
    paths = [* (SOURCE / "ida_backend").rglob("*")]
    if backend == "ida":
        paths.extend((SOURCE / "skills" / SKILL).rglob("*"))
    else:
        # Share the vendored generic MCP library, without IDA sessions/worker.
        paths = list((SOURCE / "ida_backend/vendor/ida_pro_mcp/ida_mcp/zeromcp").rglob("*"))
        paths.extend(SOURCE / "ida_backend/vendor" / name for name in
                     ("LICENSE.ida-pro-mcp", "LOCAL_CHANGES.md", "UPSTREAM.json"))
        paths.extend((SOURCE / "kernel_backend").rglob("*"))
    files = {}
    for path in paths:
        if not path.is_file() or "__pycache__" in path.parts or path.suffix == ".pyc":
            continue
        files[path.relative_to(SOURCE).as_posix()] = path.read_bytes()
    entrypoint = "mcp_server/" + backend + "_server.py"
    files[entrypoint] = (SOURCE / entrypoint).read_bytes()
    files["mcp_server/stdio_dispatch.py"] = (SOURCE / "mcp_server/stdio_dispatch.py").read_bytes()
    return files


def checked_path(path):
    for part in (path, *path.parents):
        if part.is_symlink() or getattr(part, "is_junction", lambda: False)():
            raise ValueError("Refusing installer path through a link: " + str(path))
    return path


def atomic_write(path, data):
    checked_path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, name = tempfile.mkstemp(prefix=".aidb-install-", dir=path.parent)
    try:
        with os.fdopen(fd, "wb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(name, path)
    finally:
        Path(name).unlink(missing_ok=True)


def install(home, clients=("claude", "codex"), python=None, dry_run=False, backends=("ida", "kernel")):
    if not backends or len(set(backends)) != len(backends) or any(b not in {"ida", "kernel"} for b in backends):
        raise ValueError("Choose ida, kernel, or both backends")
    home = checked_path(Path(home).absolute())
    codex_home = (checked_path(Path(os.environ.get("CODEX_HOME") or home / ".codex").expanduser().absolute())
                  if "codex" in clients else None)
    python = str(Path(python or sys.executable).resolve(strict=True))
    deploy = home / ".ai-debugger" / backends[0]
    servers = {backend: home / ".ai-debugger" / backend / "mcp_server" / (backend + "_server.py") for backend in backends}
    server = servers[backends[0]]
    snapshots = {}
    def read_original(path):
        if path not in snapshots:
            checked_path(path)
            try:
                snapshots[path] = path.read_bytes()
            except FileNotFoundError:
                snapshots[path] = None
        return snapshots[path]
    destinations, backend_destinations = {}, {}
    for backend in backends:
        files = payload_files(backend)
        selected = {home / ".ai-debugger" / backend / key: data for key, data in files.items() if not key.startswith("skills/")}
        for key, data in files.items():
            if key.startswith("skills/"):
                relative = Path(key).relative_to("skills")
                if "codex" in clients:
                    selected[codex_home / "skills" / relative] = data
                if "claude" in clients:
                    selected[home / ".claude" / "skills" / relative] = data
        backend_destinations[backend] = selected
        destinations.update(selected)
    configs = []
    registrations = {backend: {"type": "stdio", "command": python, "args": [str(path)]} for backend, path in servers.items()}
    if "claude" in clients:
        for path in (home / ".claude.json", home / "AppData/Roaming/Claude/claude_desktop_config.json"):
            original = read_original(path)
            data = json.loads(original.decode("utf-8-sig")) if original is not None else {}
            data.setdefault("mcpServers", {}).update(registrations)
            destinations[path] = (json.dumps(data, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
            configs.append(path)
    if "codex" in clients:
        path = codex_home / "config.toml"
        original = read_original(path)
        text = original.decode("utf-8-sig") if original is not None else ""
        for backend, entrypoint in servers.items():
            text = replace_backend_toml(text, python, entrypoint, backend)
        destinations[path] = text.encode("utf-8")
        configs.append(path)
    owned, previous_manifests = {}, {}
    for backend in backends:
        manifest_path = home / ".ai-debugger" / backend / "install-manifest.json"
        original = read_original(manifest_path)
        previous = json.loads(original.decode("utf-8")) if original is not None else {}
        previous_manifests[backend] = previous
        owned.update(previous.get("files", {}))
    # Existing skill came from this user's skill. Record its initial snapshot so
    # first migration can accept it; future edits require ownership/hash match.
    adopted = json.loads((SOURCE / "skills" / "ORIGIN.json").read_text(encoding="utf-8"))
    for path, content in destinations.items():
        original = read_original(path)
        if path in configs or original is None:
            continue
        actual = digest(original)
        if actual == digest(content) or owned.get(str(path)) == actual:
            continue
        relative = path.as_posix().split("/skills/", 1)[-1]
        if adopted.get(relative) == actual:
            continue
        raise ValueError("Destination has unowned/local edits; preserved: " + str(path))
    for backend, selected in backend_destinations.items():
        previous = previous_manifests[backend]
        receipt = {"backend": backend, "server": str(servers[backend]),
                   "clients": sorted(set(previous.get("clients", [])) | set(clients)),
                   "files": {**previous.get("files", {}), **{str(path): digest(content) for path, content in selected.items()}}}
        manifest_path = home / ".ai-debugger" / backend / "install-manifest.json"
        destinations[manifest_path] = (json.dumps(receipt, indent=2) + "\n").encode("utf-8")
    if dry_run:
        return {"dryRun": True, "server": str(server), "servers": {b: str(p) for b, p in servers.items()}, "destinations": [str(p) for p in destinations]}
    backup = deploy / "backups" / str(time.time_ns())
    for index, path in enumerate(configs):
        if snapshots[path] is not None:
            atomic_write(backup / (str(index) + "-" + path.name), snapshots[path])
    written = []
    try:
        for path, content in destinations.items():
            # Compare original bytes immediately before replacing a config.
            current = path.read_bytes() if path.exists() else None
            if current != snapshots[path]:
                raise ValueError("Destination changed during installation: " + str(path))
            atomic_write(path, content)
            written.append(path)
    except BaseException as error:
        for path in reversed(written):
            try:
                checked_path(path)
                current = path.read_bytes() if path.exists() else None
                if current != destinations[path]:
                    error.add_note("Concurrent edit preserved during rollback: " + str(path))
                    continue
                old = snapshots[path]
                if old is None:
                    path.unlink(missing_ok=True)
                else:
                    atomic_write(path, old)
            except Exception as rollback_error:
                # A locked or inaccessible destination must not prevent restoring
                # the rest of the transaction or hide the original failure.
                error.add_note(f"Rollback failed for {path}: {rollback_error}")
        raise
    return {"installed": True, "server": str(server), "clients": list(clients),
            "servers": {b: str(p) for b, p in servers.items()}, "configBackups": str(backup), "fileCount": len(destinations)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--home", type=Path, default=Path.home())
    parser.add_argument("--client", choices=["claude", "codex", "all"], default="all")
    parser.add_argument("--python", type=Path)
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--backend", choices=["ida", "kernel", "all"], default="all")
    args = parser.parse_args()
    backends = ("ida", "kernel") if args.backend == "all" else (args.backend,)
    result = install(args.home, ("claude", "codex") if args.client == "all" else (args.client,), args.python, args.dry_run, backends)
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
