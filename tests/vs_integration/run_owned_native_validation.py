"""Validate current VS code against an owned native target in an isolated VS profile.

Prerequisite: deploy the current extension into --root-suffix using the VSSDK
DeployVsixExtensionFiles target. Never pass a user profile as the root suffix.
Requires the developer's pywin32 and psutil packages. The debuggee is built with
the selected installation's vcvars64.bat and is never attached to another debugger.
"""
from __future__ import annotations

import argparse
import asyncio
import hashlib
import json
import logging
import os
from pathlib import Path
import subprocess
import sys
import time
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
from mcp_server import bootstrap
bootstrap.ensure_deps()
from mcp_server import vs_bridge, vs_launch, vs_session
from mcp_server.tool_modules import memory_tools
import psutil
import win32api


def file_hash(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


async def main(args):
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    evidence = {"rootSuffix": args.root_suffix, "steps": [], "success": False}
    evidence["sourceHashes"] = {
        path: file_hash(ROOT / path) for path in (
            "mcp_server/vs_bridge.py", "mcp_server/tool_modules/memory_tools.py",
            "VSDebuggerMCP/Services/DebuggerService.Execution.cs", "VSDebuggerMCP/Ipc/OperationResultPolicy.cs",
        )
    }
    evidence_path = output / "evidence.json"
    if evidence_path.exists():
        evidence_path.rename(output / f"evidence-{time.time_ns()}.json")
    vs_bridge.logger.setLevel(logging.ERROR)

    def save(name, value):
        evidence["steps"].append({"name": name, "value": value})
        evidence_path.write_text(json.dumps(evidence, ensure_ascii=False, indent=2), encoding="utf-8")
        print(json.dumps({"step": name, "success": value.get("success") if isinstance(value, dict) else None}), flush=True)

    source = ROOT / "tests/vs_integration/OwnedNativeDebuggee.cpp"
    executable = output / "OwnedNativeDebuggee.exe"
    ready_file = output / f"ready-{time.time_ns()}.json"
    tools = Path(args.install_path) / "VC/Auxiliary/Build/vcvars64.bat"
    compile_command = (
        f'call "{tools}" >nul && cl /nologo /Od /Zi /MD /EHsc "{source}" '
        f'/Fo"{output / "OwnedNativeDebuggee.obj"}" /Fd"{output / "compiler.pdb"}" '
        f'/Fe"{executable}" /link /DEBUG /PDB:"{output / "OwnedNativeDebuggee.pdb"}"'
    )
    build_script = output / "build_debuggee.cmd"
    build_script.write_text("@echo off\n" + compile_command + "\nexit /b %errorlevel%\n", encoding="utf-8")
    build = await asyncio.to_thread(subprocess.run, ["cmd.exe", "/d", "/c", str(build_script)],
                                    capture_output=True, text=True, errors="replace")
    (output / "debuggee-build.log").write_text(build.stdout + build.stderr, encoding="utf-8")
    assert build.returncode == 0, build.stdout + build.stderr
    save("build_debuggee", {"success": True, "exe": str(executable), "sha256": file_hash(executable),
                            "pdbSha256": file_hash(output / "OwnedNativeDebuggee.pdb")})
    startup = subprocess.STARTUPINFO()
    startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
    startup.wShowWindow = 0
    debuggee = subprocess.Popen([str(executable), str(ready_file)], stdin=subprocess.DEVNULL,
                                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, startupinfo=startup)
    owned_vs_pid = None
    owned_vs_process = None
    bridge = vs_bridge.get_bridge()
    target = None
    try:
        for _ in range(100):
            if ready_file.exists():
                break
            assert debuggee.poll() is None, "Owned debuggee exited before ready"
            await asyncio.sleep(0.1)
        ready = json.loads(ready_file.read_text(encoding="utf-8"))
        assert ready["processId"] == debuggee.pid
        save("debuggee_ready", ready)
        print(json.dumps({"step": "launch_isolated_vs"}), flush=True)
        original_arguments = vs_launch.build_launch_arguments
        def isolated_arguments(*values, **keywords):
            return original_arguments(*values, **keywords) + ["/NoSplash", "/Log", str(output / "ActivityLog.xml")]
        with patch.object(vs_launch, "build_launch_arguments", side_effect=isolated_arguments):
            outcome = await vs_launch.ensure_instance(
                requested_capabilities=vs_bridge._all_vs_capabilities(),
                install_path=args.install_path, root_suffix=args.root_suffix,
                solution_path=str(ROOT / "tests/vs_integration/AIDebugger.LiveSmoke.sln"),
                reuse_existing=False, timeout_seconds=120,
            )
        if outcome.record:
            owned_vs_pid = outcome.record.process_id
            owned_vs_process = outcome.record.process
        save("launch_vs", {"status": outcome.status, "message": outcome.message,
                           "processId": owned_vs_pid, "diagnostics": outcome.diagnostics})
        assert outcome.instance is not None, outcome.message
        instance = outcome.instance
        target = vs_bridge.BoundVSTarget(instance.target_identity(), instance.registration_snapshot())

        # Check the file actually mapped by devenv, not only the deployment folder.
        mappings = await asyncio.to_thread(psutil.Process(owned_vs_pid).memory_maps)
        loaded_paths = sorted({mapping.path for mapping in mappings
                               if Path(mapping.path).name.lower() == "vsdebuggermcp.dll"})
        assert len(loaded_paths) == 1, loaded_paths
        loaded_path = loaded_paths[0]
        expected = ROOT / "VSDebuggerMCP/bin/Release/VSDebuggerMCP.dll"
        assert file_hash(loaded_path) == file_hash(expected), "Loaded extension differs from the fresh build"
        assert args.root_suffix.lower() in loaded_path.lower(), loaded_path
        version_info = win32api.GetFileVersionInfo(loaded_path, "\\")
        version = ".".join(str(value) for key in ("FileVersionMS", "FileVersionLS")
                           for value in (version_info[key] >> 16, version_info[key] & 0xFFFF))
        save("loaded_extension", {"path": loaded_path, "sha256": file_hash(loaded_path),
                                  "buildPath": str(expected), "version": version})

        async def call(method, params=None, timeout=15):
            nonlocal target
            try:
                value, target = await bridge.call_pipe_exact(target, method, params, timeout=timeout, include_target=True)
            except vs_bridge.VSBridgeError as error:
                save(method + "_error", {"code": error.code, "message": str(error), "details": error.details,
                                        "requestId": error.request_id, "outcomeUnknown": error.outcome_unknown})
                if error.request_id:
                    recovery = await bridge.call_pipe_exact(target, "get_request_status", {"requestId": error.request_id}, timeout=10)
                    save(method + "_recovery", recovery)
                raise
            save(method, value)
            return value

        attached = await call("attach_process", {"processId": debuggee.pid}, timeout=45)
        assert attached.get("success"), attached
        breakpoint_line = next(index for index, line in enumerate(source.read_text().splitlines(), 1)
                               if "VS_LIVE_BREAKPOINT" in line)
        breakpoint = await call("set_breakpoint", {"filePath": str(source), "lineNumber": breakpoint_line}, timeout=30)
        assert breakpoint.get("success"), breakpoint
        status = await call("get_debug_status")
        if status.get("isPaused"):
            await call("continue")
        for _ in range(100):
            status = await call("get_debug_status", timeout=5)
            if status.get("isPaused"):
                break
            await asyncio.sleep(0.1)
        assert status.get("isPaused"), status
        assert status.get("currentProcessId") == debuggee.pid, status
        stack = await call("get_callstack", {"maxFrames": 8})
        assert stack.get("frames") and "OwnedProbe" in stack["frames"][0].get("functionName", ""), stack
        if stack["frames"][0].get("sourceLocationStatus") == "available":
            assert stack["frames"][0].get("lineNumber") == breakpoint_line, stack["frames"][0]
        save("source_location_evidence", {"status": stack["frames"][0].get("sourceLocationStatus"),
                                          "reportedLine": stack["frames"][0].get("lineNumber")})
        memory = await call("read_memory", {"address": ready["matchAddress"], "size": len(ready["pattern"])})
        assert memory.get("bytesRead") == len(ready["pattern"]), memory
        vs_session.select_instance(pipe_name=target["pipeName"], target_identity=target,
                                   registration_snapshot=instance.registration_snapshot())
        request_size = 1024 * 1024 + 64
        baseline = (await memory_tools.search_memory(ready["address"], ready["pattern"], pattern_format="text",
                                                     search_size=request_size, timeout_seconds=60))["result"]
        save("live_chunk_search", baseline)
        assert [int(value, 16) for value in baseline["matches"]] == [int(ready["matchAddress"], 16)], baseline

        # Only the client deadline is controlled. Every byte read and every
        # match below comes from the real paused native process through the pipe.
        original_remaining = memory_tools.remaining_tool_timeout
        remaining_calls = 0

        def stop_after_first_chunk(*values):
            nonlocal remaining_calls
            remaining_calls += 1
            return original_remaining(*values) if remaining_calls == 1 else None

        with patch.object(memory_tools, "remaining_tool_timeout", side_effect=stop_after_first_chunk):
            partial = (await memory_tools.search_memory(ready["address"], ready["pattern"], pattern_format="text",
                                                        search_size=request_size, timeout_seconds=60))["result"]
        save("controlled_client_deadline_after_real_first_chunk", partial)
        assert partial["partial"] and not partial["matches"] and partial["hasMore"], partial
        resumed = (await memory_tools.search_memory(partial["nextStartExpression"], ready["pattern"], pattern_format="text",
                                                    start_offset=partial["nextStartOffset"], search_size=partial["remainingBytes"],
                                                    timeout_seconds=30))["result"]
        save("live_resume_after_deadline", resumed)
        assert [int(value, 16) for value in resumed["matches"]] == [int(ready["matchAddress"], 16)], resumed
        before_step_status = await call("get_debug_status")
        before_step_registers = await call("get_registers")
        before_rip = next(register["hexValue"] for register in before_step_registers["registers"]
                          if register["name"].upper() == "RIP")
        step_error = None
        try:
            step = await call("step_over", {"waitTimeoutMs": 5000}, timeout=15)
            assert step.get("success"), step
        except vs_bridge.VSBridgeError as error:
            step_error = error
        after_step_status = await call("get_debug_status")
        after_step_registers = await call("get_registers")
        after_rip = next(register["hexValue"] for register in after_step_registers["registers"]
                         if register["name"].upper() == "RIP")
        save("step_execution_evidence", {"beforeRip": before_rip, "afterRip": after_rip,
                                         "beforeBreakSequence": before_step_status["breakSequence"],
                                         "afterBreakSequence": after_step_status["breakSequence"],
                                         "stopReason": after_step_status.get("stopReason")})
        if step_error:
            raise step_error
        assert after_step_status.get("stopReason") == "step", after_step_status
        assert after_step_status["breakSequence"] > before_step_status["breakSequence"], after_step_status
        assert int(before_rip, 16) != int(after_rip, 16), (before_rip, after_rip)
        after = await call("get_callstack", {"maxFrames": 8})
        assert after.get("frames"), after
        assert after["frames"][0]["context"]["processId"] == debuggee.pid, after
        assert after["frames"][0]["context"]["contextEpoch"] > stack["frames"][0]["context"]["contextEpoch"], after
        deleted = await call("delete_breakpoint", {"filePath": str(source), "lineNumber": breakpoint_line})
        assert deleted.get("success"), deleted
        detached = await call("detach")
        assert detached.get("success"), detached
        evidence["success"] = True
    except BaseException as error:
        save("failure", {"type": type(error).__name__, "message": str(error),
                         "code": getattr(error, "code", None), "requestId": getattr(error, "request_id", None),
                         "outcomeUnknown": getattr(error, "outcome_unknown", False), "details": getattr(error, "details", None)})
        raise
    finally:
        if owned_vs_pid is not None:
            try:
                closed = await vs_launch.close_owned_instance(owned_vs_pid, force=True, timeout_seconds=15)
                save("close_owned_vs", closed)
                if not closed.get("success") or not closed.get("closed"):
                    evidence["success"] = False
                owned_exited = owned_vs_process is not None and owned_vs_process.poll() is not None
                save("verify_owned_vs_exit", {"processId": owned_vs_pid, "exited": owned_exited})
                evidence["success"] = evidence["success"] and owned_exited
            except Exception as error:
                save("close_owned_vs_error", {"message": str(error)})
                evidence["success"] = False
        if debuggee.poll() is None:
            debuggee.terminate()
            await asyncio.to_thread(debuggee.wait, 10)
        save("cleanup_debuggee", {"processId": debuggee.pid, "exitCode": debuggee.poll()})
        evidence["success"] = evidence["success"] and debuggee.poll() is not None
        evidence_path.write_text(json.dumps(evidence, ensure_ascii=False, indent=2), encoding="utf-8")
        bridge._executor.shutdown(wait=False, cancel_futures=True)
        bridge._control_executor.shutdown(wait=False, cancel_futures=True)
        bridge._recovery_executor.shutdown(wait=False, cancel_futures=True)
    assert evidence["success"], "Live validation or owned-process cleanup failed; inspect evidence.json"


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--root-suffix", required=True)
    parser.add_argument("--output", default=str(ROOT / ".tmp/live-validation-20261013/vs"))
    parser.add_argument("--install-path", default=r"C:\Program Files\Microsoft Visual Studio\18\Community")
    arguments = parser.parse_args()
    assert arguments.root_suffix.startswith("AIDBLive"), "Use a dedicated AIDBLive experimental profile"
    asyncio.run(main(arguments))
