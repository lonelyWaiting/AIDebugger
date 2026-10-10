# IDA migration validation — 2026-10-10

Validated locally on Windows x64 with IDA Pro 9.1 / embedded Python 3.13.15.
The supervisor/installer used Python 3.14. Original DLLs and existing user IDEs
were preserved. No VSIX build or replacement was required for this migration.

| Boundary | Result | Evidence |
|---|---|---|
| Python regression | 272 tests passed, including 33 IDA lifecycle/installer/native-boundary tests | `python -m unittest discover -s tests -p "test_*.py"` |
| Skill format | Passed; deployed Claude/Codex bytes match project source | `quick_validate.py`, installation hash verification |
| Source provenance | 53 original file hashes recorded; one documented change to main-thread pump | `ida_backend/vendor/UPSTREAM.json`, `LOCAL_CHANGES.md`, MIT license |
| Headless analysis | Two simultaneous DLL sessions, 94 operations discoverable, Hex-Rays function decompilation | `.tmp/ida-smoke/1791602749267732900/evidence.json` |
| Endpoint authentication | Invalid bearer token rejected with HTTP 403 | Same headless/GUI smoke |
| Patch/file export | One mapped byte changed; output compared byte-for-byte; original DLL SHA-256 unchanged | Same headless smoke |
| IDB persistence | Saved, closed, reopened copied IDB; patch byte retained | Same headless smoke |
| GUI lifecycle | Both DLLs analyzed/exported; saved IDBs reopened; owned processes exited | `.tmp/ida-smoke/1791602875088531500/evidence.json` |
| GUI exit boundary | Native GUI shutdown left processes alive; confirmed save receipts permitted owned-handle termination after grace. All three resulting saved IDBs reopened successfully | GUI `closeReceipt.ownedProcessTerminationAfterGrace=true` |
| Stdio disconnect without client polling | GUI and headless owned workers exited and saved their databases; saved IDBs reopened. GUI repeated against the updated installed stdio entrypoint | `.tmp/ida-shutdown-smoke/1791604829646535900/evidence.json` (installed GUI), `1791604755204500000/evidence.json` (headless) |
| Installer concurrency and client directory | Concurrent Claude/Codex edits abort and survive rollback; `CODEX_HOME` used for both config and Skill; partial client installs retain other client ownership; unrelated TOML changes refused | `tests/test_ida_backend.py` |
| Headless debugger | Configured automatically; controlled native fixture started and suspended; registers and 19 events read; active-debug close refused; detached and IDA exited | `.tmp/ida-debug-smoke/1791602875096283500/evidence.json` |
| Real VS + IDA coexistence | Fresh installed stdio clients; owned VS attached/paused fixture and read callstack while IDA decompiled DllMain in another DLL; IDA refused attachment to VS-held PID | `.tmp/dual-mcp-smoke/1791602912281235700.json` |
| Installation integrity | All installed runtime/Skill file hashes match manifest; Claude's other MCP entries and all Codex settings outside `ida` unchanged | `~/.ai-debugger/ida/install-manifest.json`, first-install config backups |
| Failure behavior | Save error, invalid save receipt, transport uncertainty and busy state preserve worker; mutations not retried; never-ready owned startup can be explicitly discarded | `tests/test_ida_backend.py` |

The project installer migrated the `ida` entry in Claude Code, Claude Desktop and
Codex from the old HTTP endpoint to the independent installed stdio entrypoint.
Both clients received the project-owned analysis Skill. A running chat needs a
new MCP connection to use the updated registration.

This validates the installed backend on this IDA/Windows combination, not every
IDA edition, architecture, remote debugger or privilege level. File export is
not proof that a patched application still works or that its signature remains
valid. GUI owned-handle termination is a confirmed-save fallback, not proof that
native GUI shutdown always exits cleanly. Historical unreachable VS registration
records appeared during discovery; a freshly launched authenticated VS was
reachable and was the only VS controlled by the coexistence test.
