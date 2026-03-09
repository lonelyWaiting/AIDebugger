"""Update Cursor's MCP allow list for a registered server."""

from __future__ import annotations

import argparse
import json
import os
import sqlite3
import sys
from typing import Any, Dict, List


APPLICATION_USER_KEY = (
    "src.vs.platform.reactivestorage.browser.reactiveStorageServiceImpl."
    "persistentStorage.applicationUser"
)


def get_default_db_path() -> str:
    appdata = os.environ.get("APPDATA")
    if not appdata:
        user_home = os.path.expanduser("~")
        appdata = os.path.join(user_home, "AppData", "Roaming")
    return os.path.join(appdata, "Cursor", "User", "globalStorage", "state.vscdb")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Add a Cursor MCP server wildcard entry to the MCP allow list."
    )
    parser.add_argument("--server", required=True, help="Cursor MCP server name")
    parser.add_argument(
        "--db-path",
        default=get_default_db_path(),
        help="Path to Cursor's state.vscdb",
    )
    return parser.parse_args()


def normalize_allowlist_entry(server_name: str) -> str:
    return f"{server_name.strip().lower()}:*"


def ensure_object(value: Any, name: str) -> Dict[str, Any]:
    if value is None:
        return {}
    if isinstance(value, dict):
        return value
    raise RuntimeError(f"{name} must be a JSON object")


def ensure_string_list(value: Any, name: str) -> List[str]:
    if value is None:
        return []
    if not isinstance(value, list):
        raise RuntimeError(f"{name} must be a JSON array")
    result: List[str] = []
    for item in value:
        if not isinstance(item, str):
            raise RuntimeError(f"{name} must contain only strings")
        stripped = item.strip()
        if stripped:
            result.append(stripped)
    return result


def load_application_user_state(connection: sqlite3.Connection) -> Dict[str, Any]:
    row = connection.execute(
        "SELECT value FROM ItemTable WHERE key = ?",
        (APPLICATION_USER_KEY,),
    ).fetchone()
    if row is None or row[0] is None or row[0] == "":
        return {}
    try:
        parsed = json.loads(row[0])
    except json.JSONDecodeError as exc:
        raise RuntimeError(f"Invalid Cursor applicationUser JSON: {exc}") from exc
    if not isinstance(parsed, dict):
        raise RuntimeError("Cursor applicationUser payload must be a JSON object")
    return parsed


def save_application_user_state(connection: sqlite3.Connection, payload: Dict[str, Any]) -> None:
    serialized = json.dumps(payload, ensure_ascii=False, separators=(",", ":"))
    connection.execute(
        "INSERT OR REPLACE INTO ItemTable(key, value) VALUES(?, ?)",
        (APPLICATION_USER_KEY, serialized),
    )


def sync_allowlist(db_path: str, server_name: str) -> str:
    if not os.path.isfile(db_path):
        raise FileNotFoundError(f"Cursor state database not found: {db_path}")

    allowlist_entry = normalize_allowlist_entry(server_name)

    with sqlite3.connect(db_path, timeout=15) as connection:
        payload = load_application_user_state(connection)
        composer_state = ensure_object(payload.get("composerState"), "composerState")
        allowlist = ensure_string_list(composer_state.get("mcpAllowedTools"), "mcpAllowedTools")

        if allowlist_entry in {item.lower() for item in allowlist}:
            return "already_present"

        allowlist.append(allowlist_entry)
        composer_state["mcpAllowedTools"] = allowlist
        payload["composerState"] = composer_state

        connection.execute("BEGIN IMMEDIATE")
        save_application_user_state(connection, payload)
        connection.commit()
        return "updated"


def main() -> int:
    args = parse_args()
    try:
        status = sync_allowlist(args.db_path, args.server)
    except Exception as exc:
        print(str(exc), file=sys.stderr)
        return 1

    print(status)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
