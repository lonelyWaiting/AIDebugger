"""Install independent IDA MCP and project-owned skills, preserving VS registration.

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


def replace_ida_toml(text, command, server):
    """Replace only ida's own table (and children); parse before and after."""
    original = tomllib.loads(text)
    lines = text.splitlines(keepends=True)
    result = []
    skipping = False
    for line in lines:
        if line.lstrip().startswith("["):
            # Refuse exotic quoted ownership rather than editing another table.
            match = re.match(r"\s*\[([^\]]+)\]\s*(?:#.*)?$", line.strip())
            table = match.group(1).strip() if match else ""
            skipping = table == "mcp_servers.ida" or table.startswith("mcp_servers.ida.")
        if not skipping:
            result.append(line)
    section = ("\n[mcp_servers.ida]\ncommand = " + json.dumps(command) +
               "\nargs = [" + json.dumps(str(server)) + "]\nenabled = true\n" +
               "startup_timeout_sec = 20\ntool_timeout_sec = 120\n")
    updated = "".join(result).rstrip() + "\n" + section
    parsed = tomllib.loads(updated)
    if parsed.get("mcp_servers", {}).get("ida", {}).get("command") != command:
        raise ValueError("Could not safely replace ida MCP table")
    # Header-like text inside multiline TOML values must never change unrelated
    # settings. Refuse edits the small table replacer cannot safely represent.
    for config in (original, parsed):
        config.get("mcp_servers", {}).pop("ida", None)
        if config.get("mcp_servers") == {}:
            config.pop("mcp_servers")
    if parsed != original:
        raise ValueError("IDA registration would change unrelated TOML settings")
    return updated


def payload_files():
    paths = [* (SOURCE / "ida_backend").rglob("*"), * (SOURCE / "skills" / SKILL).rglob("*")]
    files = {}
    for path in paths:
        if not path.is_file() or "__pycache__" in path.parts or path.suffix == ".pyc":
            continue
        files[path.relative_to(SOURCE).as_posix()] = path.read_bytes()
    files["mcp_server/ida_server.py"] = (SOURCE / "mcp_server/ida_server.py").read_bytes()
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


def install(home, clients=("claude", "codex"), python=None, dry_run=False):
    home = checked_path(Path(home).absolute())
    codex_home = (checked_path(Path(os.environ.get("CODEX_HOME") or home / ".codex").expanduser().absolute())
                  if "codex" in clients else None)
    python = str(Path(python or sys.executable).resolve(strict=True))
    deploy = home / ".ai-debugger" / "ida"
    server = deploy / "mcp_server" / "ida_server.py"
    files = payload_files()
    snapshots = {}
    def read_original(path):
        if path not in snapshots:
            checked_path(path)
            try:
                snapshots[path] = path.read_bytes()
            except FileNotFoundError:
                snapshots[path] = None
        return snapshots[path]
    destinations = {deploy / key: data for key, data in files.items() if not key.startswith("skills/")}
    for key, data in files.items():
        if key.startswith("skills/"):
            relative = Path(key).relative_to("skills")
            if "codex" in clients:
                destinations[codex_home / "skills" / relative] = data
            if "claude" in clients:
                destinations[home / ".claude" / "skills" / relative] = data
    configs = []
    registration = {"type": "stdio", "command": python, "args": [str(server)]}
    if "claude" in clients:
        for path in (home / ".claude.json", home / "AppData/Roaming/Claude/claude_desktop_config.json"):
            original = read_original(path)
            data = json.loads(original.decode("utf-8-sig")) if original is not None else {}
            data.setdefault("mcpServers", {})["ida"] = registration
            destinations[path] = (json.dumps(data, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
            configs.append(path)
    if "codex" in clients:
        path = codex_home / "config.toml"
        original = read_original(path)
        text = original.decode("utf-8-sig") if original is not None else ""
        destinations[path] = replace_ida_toml(text, python, server).encode("utf-8")
        configs.append(path)
    manifest_path = deploy / "install-manifest.json"
    original = read_original(manifest_path)
    previous = json.loads(original.decode("utf-8")) if original is not None else {}
    owned = previous.get("files", {})
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
    receipt = {"backend": "ida", "server": str(server),
               "clients": sorted(set(previous.get("clients", [])) | set(clients)),
               "files": {**owned, **{str(path): digest(content) for path, content in destinations.items() if path not in configs}}}
    destinations[manifest_path] = (json.dumps(receipt, indent=2) + "\n").encode("utf-8")
    if dry_run:
        return {"dryRun": True, "server": str(server), "destinations": [str(p) for p in destinations]}
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
            current = path.read_bytes() if path.exists() else None
            if current != destinations[path]:
                error.add_note("Concurrent edit preserved during rollback: " + str(path))
                continue
            old = snapshots[path]
            if old is None:
                path.unlink(missing_ok=True)
            else:
                atomic_write(path, old)
        raise
    return {"installed": True, "server": str(server), "clients": list(clients),
            "configBackups": str(backup), "fileCount": len(destinations)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--home", type=Path, default=Path.home())
    parser.add_argument("--client", choices=["claude", "codex", "all"], default="all")
    parser.add_argument("--python", type=Path)
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()
    result = install(args.home, ("claude", "codex") if args.client == "all" else (args.client,), args.python, args.dry_run)
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
