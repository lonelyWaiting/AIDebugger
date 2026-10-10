"""Opt-in real IDA smoke. Uses copied system DLLs; never executes those DLLs."""
import argparse
import json
from pathlib import Path
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from ida_backend.sessions import IdaSessions, file_hash
from ida_backend.transport import tool_value, rpc, IdaError


def wait_ready(manager, session_id, timeout=60):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        result = manager.status(session_id)
        if result["state"] == "ready":
            return result
        if result["state"] == "exited":
            raise RuntimeError(json.dumps(result))
        time.sleep(0.2)
    raise TimeoutError("IDA readiness timed out; inspect process.log")


def wait_exit(manager, session_id, timeout=10):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        manager.status(session_id)
        session = manager.get(session_id)
        if session.process.poll() is not None:
            assert session.process.returncode == 0 or session.close_receipt.get("ownedProcessTerminationAfterGrace")
            return
        time.sleep(0.1)
    raise TimeoutError("IDA did not exit after acknowledged close")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--mode", choices=["headless", "gui"], default="headless")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2] / ".tmp" / "ida-smoke" / str(time.time_ns())
    manager = IdaSessions(root=root)
    sources = [Path("C:/Windows/System32/version.dll"), Path("C:/Windows/System32/winmm.dll")]
    before = [file_hash(p) for p in sources]
    opened = []
    evidence = {"mode": args.mode, "checks": [], "sessions": []}
    try:
        for source in sources:
            opened.append(manager.open(str(source), args.mode)["sessionId"])
        for session_id in opened:
            evidence["sessions"].append(wait_ready(manager, session_id))
            try:
                rpc(manager.get(session_id).port, "invalid-token", "ping")
                raise AssertionError("Unauthenticated request was accepted")
            except IdaError as error:
                assert error.payload.get("status") == 403, error
            tools = manager.tools(session_id, count=100)
            assert {"decompile", "patch", "dbg_status", "debugger_configure", "export_patched_binary"} <= {t["name"] for t in tools["tools"]}
            health = tool_value(manager.call(session_id, "server_health", {}))
            evidence["checks"].append({"session": session_id, "toolCount": tools["total"], "health": health})
            functions = tool_value(manager.call(session_id, "list_funcs", {"queries": {"count": 5}}))
            evidence["checks"].append({"functions": functions})
            function = functions["result"][0]["data"][0]["addr"]
            decompiled = tool_value(manager.call(session_id, "decompile", {"addr": function}))
            evidence["checks"].append({"decompile": decompiled})
            assert not decompiled.get("error"), decompiled
        # Modify a mapped file-backed byte in the isolated IDB; never execute it.
        sid = opened[0]
        metadata = tool_value(manager.call(sid, "py_eval", {"code": "import ida_funcs, ida_loader; ea=ida_funcs.get_next_func(0).start_ea; (hex(ea), ida_loader.get_fileregion_offset(ea))"}))
        print(json.dumps({"baseProbe": metadata}, ensure_ascii=False), flush=True)
        # Structured py_eval returns the expression as a result string.
        import ast
        address, file_offset = ast.literal_eval(metadata["result"])
        original = tool_value(manager.call(sid, "get_bytes", {"regions": {"addr": address, "size": 1}}))
        patch = manager.call(sid, "patch", {"patches": {"addr": address, "data": "AB"}})
        assert not patch.get("isError"), patch
        assert not tool_value(patch)["result"][0].get("error"), patch
        destination = root / "version.patched.dll"
        exported = tool_value(manager.call(sid, "export_patched_binary", {"output_path": str(destination)}))
        assert exported["patchCount"] == 1, exported
        expected = bytearray(sources[0].read_bytes())
        expected[file_offset] = 0xAB
        assert destination.read_bytes() == expected
        evidence["checks"].append({"patchOriginal": original, "export": exported})
        for session_id in opened:
            evidence["checks"].append({"close": manager.close(session_id)})
            wait_exit(manager, session_id)
        # Reopen the saved IDB and verify the patch survives; no full reanalysis prerequisite.
        database = Path(evidence["sessions"][0]["identity"]["idbPath"])
        assert database.is_file(), str(database)
        reopened = manager.open(str(database), args.mode)["sessionId"]
        opened.append(reopened)
        wait_ready(manager, reopened)
        persisted = tool_value(manager.call(reopened, "get_bytes", {"regions": {"addr": address, "size": 1}}))
        evidence["checks"].append({"savedIdbReopened": str(database), "bytes": persisted})
        assert "ab" in json.dumps(persisted).lower(), persisted
        evidence["checks"].append({"closeReopened": manager.close(reopened)})
        wait_exit(manager, reopened)
        assert before == [file_hash(p) for p in sources]
        evidence["passed"] = True
    finally:
        manager.shutdown()
        (root / "evidence.json").write_text(json.dumps(evidence, indent=2, ensure_ascii=False), encoding="utf-8")
        print(str(root / "evidence.json"), flush=True)


if __name__ == "__main__":
    main()
