import json
import ast
import importlib.util
import os
import pathlib
import subprocess
import sys
import tempfile
import time
import unittest
import xml.etree.ElementTree as ET


ROOT = pathlib.Path(__file__).resolve().parents[1]

# The VS-only server surface: the FastMCP app plus every tool module that
# ``tool_modules.register_vs()`` imports.  ``read("mcp_server/vs_server.py")``
# concatenates these so tool-source assertions keep seeing the whole surface.
MCP_SERVER_SOURCE_FILES = [
    "mcp_server/vs_mcp_app.py",
    "mcp_server/tool_context.py",
    "mcp_server/vs_tool_registry.py",
    "mcp_server/tool_modules/capability_tools.py",
    "mcp_server/tool_modules/vs_instance_tools.py",
    "mcp_server/tool_modules/multi_vs_tools.py",
    "mcp_server/tool_modules/snapshot_tools.py",
    "mcp_server/tool_modules/native_managed_tools.py",
    "mcp_server/tool_modules/control_tools.py",
    "mcp_server/tool_modules/breakpoint_tools.py",
    "mcp_server/tool_modules/memory_tools.py",
]


def find_msbuild() -> pathlib.Path | None:
    candidates = [
        pathlib.Path(r"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"),
        pathlib.Path(r"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"),
        pathlib.Path(r"C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe"),
        pathlib.Path(r"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"),
    ]
    return next((path for path in candidates if path.exists()), None)


def read(path: str) -> str:
    if path == "mcp_server/vs_server.py":
        return "\n".join((ROOT / source_path).read_text(encoding="utf-8") for source_path in MCP_SERVER_SOURCE_FILES)
    if path == "VSDebuggerMCP/DebuggerService.cs":
        sources = [ROOT / path]
        sources.extend(sorted((ROOT / "VSDebuggerMCP" / "Services").glob("DebuggerService.*.cs")))
        return "\n".join(source.read_text(encoding="utf-8") for source in sources)
    if path == "VSDebuggerMCP/NamedPipeServer.cs":
        sources = [ROOT / path]
        sources.extend(sorted((ROOT / "VSDebuggerMCP" / "Ipc").glob("NamedPipe*.cs")))
        return "\n".join(source.read_text(encoding="utf-8") for source in sources)
    if path == "VSDebuggerMCP/McpClientRegistration.cs":
        sources = [ROOT / path]
        sources.extend(sorted((ROOT / "VSDebuggerMCP" / "Registration").glob("McpClientRegistration.*.cs")))
        return "\n".join(source.read_text(encoding="utf-8") for source in sources)
    return (ROOT / path).read_text(encoding="utf-8")


def function_source(source: str, name: str) -> str:
    async_marker = f"async def {name}"
    sync_marker = f"def {name}"
    start = source.find(async_marker)
    if start == -1:
        start = source.index(sync_marker)
    end = source.find("\n@mcp.tool", start + 1)
    if end == -1:
        end = len(source)
    return source[start:end]


def csharp_slice(source: str, start_marker: str, end_marker: str) -> str:
    start = source.index(start_marker)
    end = source.index(end_marker, start + len(start_marker))
    return source[start:end]


def load_tool_registry():
    module_path = ROOT / "mcp_server/vs_tool_registry.py"
    spec = importlib.util.spec_from_file_location("aidb_vs_tool_registry_for_tests", module_path)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def load_bootstrap_module():
    module_path = ROOT / "mcp_server/bootstrap.py"
    spec = importlib.util.spec_from_file_location("aidb_bootstrap_for_tests", module_path)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


def registered_tool_module_names() -> list[str]:
    init_tree = ast.parse((ROOT / "mcp_server/tool_modules/__init__.py").read_text(encoding="utf-8"))
    registered_modules: list[str] = []
    for node in ast.walk(init_tree):
        if not isinstance(node, ast.ImportFrom) or node.level != 1:
            continue
        for alias in node.names:
            if alias.name != "*":
                registered_modules.append(alias.name)
    return sorted(set(registered_modules))


def discover_mcp_tool_names() -> list[str]:
    names: list[str] = []
    registered_modules = registered_tool_module_names()
    for module_name in registered_modules:
        source_path = f"mcp_server/tool_modules/{module_name}.py"
        tree = ast.parse((ROOT / source_path).read_text(encoding="utf-8"))
        for node in tree.body:
            if not isinstance(node, (ast.AsyncFunctionDef, ast.FunctionDef)):
                continue
            for decorator in node.decorator_list:
                if not (
                    isinstance(decorator, ast.Call)
                    and isinstance(decorator.func, ast.Attribute)
                    and decorator.func.attr == "tool"
                ):
                    continue
                tool_name = node.name
                for keyword in decorator.keywords:
                    if keyword.arg == "name" and isinstance(keyword.value, ast.Constant):
                        tool_name = keyword.value.value
                names.append(tool_name)
    return names


def expected_packaged_mcp_files() -> list[str]:
    files: list[str] = []
    mcp_root = ROOT / "mcp_server"
    for path in mcp_root.rglob("*"):
        if not path.is_file():
            continue
        relative_parts = path.relative_to(mcp_root).parts
        if "vendor" in relative_parts or "__pycache__" in relative_parts:
            continue
        if path.suffix == ".py" or path.name in {"requirements.txt", "debug_method_manifest.json"}:
            files.append(path.relative_to(mcp_root).as_posix())
    return sorted(files)


def vsix_content_items() -> dict[str, dict[str, str]]:
    tree = ET.parse(ROOT / "VSDebuggerMCP/VSDebuggerMCP.csproj")
    items: dict[str, dict[str, str]] = {}
    for content in tree.iter():
        if content.tag.rsplit("}", 1)[-1] != "Content":
            continue
        include = content.attrib.get("Include")
        if not include:
            continue
        metadata = {child.tag.rsplit("}", 1)[-1]: (child.text or "") for child in content}
        items[include] = metadata
    return items


class ContractTests(unittest.TestCase):
    def test_vs_entrypoint_has_an_independent_vs_only_runtime_surface(self) -> None:
        script = """
import asyncio, json, sys
sys.path.insert(0, 'mcp_server')
import vs_server, tool_runtime
tools = asyncio.run(vs_server.mcp.list_tools())
print(json.dumps({
    'backend': tool_runtime.backend.__name__,
    'tools': sorted(tool.name for tool in tools),
    'ideBridgeLoaded': 'ide_bridge' in sys.modules or 'mcp_server.ide_bridge' in sys.modules,
    'riderBridgeLoaded': 'rider_bridge' in sys.modules or 'mcp_server.rider_bridge' in sys.modules,
}))
"""
        environment = dict(os.environ)
        environment.update({"AIDB_PREFERRED_IDE": "vs"})
        completed = subprocess.run(
            [sys.executable, "-c", script],
            cwd=ROOT,
            env=environment,
            capture_output=True,
            text=True,
            timeout=30,
            check=True,
        )
        payload = json.loads(completed.stdout.strip().splitlines()[-1])
        self.assertEqual("vs_session", payload["backend"])
        self.assertFalse(payload["ideBridgeLoaded"])
        self.assertFalse(payload["riderBridgeLoaded"])
        self.assertFalse({"list_ide_instances", "select_ide_instance", "clear_ide_selection"} & set(payload["tools"]))

    def test_memory_contract_fields_present(self) -> None:
        vs_models = read("VSDebuggerMCP/Protocol/DebugModels.cs")
        vs_memory = csharp_slice(vs_models, "public class MemorySearchResult", "    #endregion")

        required_fields = [
            "status",
            "failedReads",
            "partial",
            "requestedBytes",
            "truncated",
            "nextStartAddress",
        ]
        for field in required_fields:
            self.assertIn(field, vs_memory)

        for general_memory_field in ("requestedSize", "bytesRead"):
            self.assertIn(general_memory_field, vs_models)

    def test_memory_search_continuation_fields_do_not_leak_to_threads(self) -> None:
        vs_models = read("VSDebuggerMCP/Protocol/DebugModels.cs")
        vs_threads = csharp_slice(vs_models, "public class ThreadsResult", "    [DataContract]\n    public class ThreadInfo")

        self.assertNotIn('Name = "truncated"', vs_threads)
        self.assertNotIn("NextStartAddress", vs_threads)

    def test_snapshot_contract_fields_present(self) -> None:
        vs_models = read("VSDebuggerMCP/Protocol/DebugModels.cs")

        for field in ("snapshotStable", "snapshotAttempts", "snapshotStatus"):
            self.assertIn(field, vs_models)

    def test_capability_and_behavior_fields_present(self) -> None:
        vs_models = read("VSDebuggerMCP/Protocol/DebugModels.cs")

        for field in ("capability", "availableCategories", "behavior"):
            self.assertIn(field, vs_models)

    def test_p0_bundle_and_stop_reason_contract_fields_present(self) -> None:
        vs_models = read("VSDebuggerMCP/Protocol/DebugModels.cs")

        for field in (
            "stopReason",
            "stopReasonRaw",
            "BatchEvaluateResult",
            "DebugBundleResult",
            "snapshotId",
            "capturedAt",
            "includedComponents",
            "omittedComponents",
        ):
            self.assertIn(field, vs_models)

    def test_continue_and_wait_contract_fields_present(self) -> None:
        vs_models = read("VSDebuggerMCP/Protocol/DebugModels.cs")

        for field in (
            "ContinueAndWaitResult",
            "waitTimeoutMs",
            "pollIntervalMs",
            "waitedMs",
            "issuedContinue",
            "timedOut",
            "breakDetected",
            "bundle",
        ):
            self.assertIn(field, vs_models)

    def test_debug_response_knows_continue_and_wait_result(self) -> None:
        vs_response = read("VSDebuggerMCP/Protocol/DebugResponse.cs")
        self.assertIn("KnownType(typeof(ContinueAndWaitResult))", vs_response)

    def test_named_pipe_bridge_uses_io_timeout(self) -> None:
        vs_bridge = read("mcp_server/vs_bridge.py")

        for marker in (
            "remaining_timeout_ms",
            "WaitNamedPipeW",
            "PeekNamedPipe",
            "_compute_timeouts",
            "ThreadPoolExecutor",
            "run_in_executor",
            "FILE_FLAG_OVERLAPPED",
            "CancelIoEx",
            "GetOverlappedResult",
        ):
            self.assertIn(marker, vs_bridge)

    def test_vs_registration_instructions_include_snapshot_workflow(self) -> None:
        vs_registration = read("VSDebuggerMCP/McpClientRegistration.cs")
        self.assertIn("capture_debug_bundle", vs_registration)
        self.assertIn("evaluate_expressions_batch", vs_registration)
        self.assertIn("stopReason", vs_registration)
        self.assertNotIn("cursor_allowlist_sync.py", vs_registration)
        self.assertIn("continue_and_wait_for_break", vs_registration)

    def test_vs_registration_refreshes_seeded_bundle_and_pins_python_versions(self) -> None:
        vs_registration = read("VSDebuggerMCP/McpClientRegistration.cs")
        self.assertIn(".bundle-signature", vs_registration)
        self.assertIn("TryEnsureBundledServerFiles", vs_registration)
        self.assertIn("ComputeDirectorySignature", vs_registration)
        self.assertIn('manifest.json', vs_registration)
        self.assertIn('RegistryKey.OpenBaseKey(hive, view)', vs_registration)
        self.assertIn('Path.IsPathRooted(command)', vs_registration)
        self.assertNotIn('Command = "py"', vs_registration)
        self.assertNotIn('Command = "python"', vs_registration)
        self.assertNotIn('Command = "python3"', vs_registration)

    def test_named_pipe_security_is_unconditionally_owner_only(self) -> None:
        vs_pipe = read("VSDebuggerMCP/NamedPipeServer.cs")
        self.assertNotIn("WorldSid", vs_pipe)
        self.assertNotIn("AIDB_PIPE_SECURITY", vs_pipe)
        self.assertIn("WaitForConnectionAsync", vs_pipe)
        self.assertIn("cancellationToken.Register", vs_pipe)

    def test_vs_command_recovery_target_is_exact(self) -> None:
        control_tools = read("mcp_server/tool_modules/control_tools.py")
        vs_bridge = read("mcp_server/vs_bridge.py")

        self.assertIn("_call_recovery_target", control_tools)
        self.assertIn("_build_recovery_target", control_tools)
        self.assertIn('"ide": "vs"', control_tools)
        self.assertIn('pipe_name != f"AIDebuggerVS_{process_id}"', control_tools)
        self.assertIn(
            "Exact recovery requires all target identity fields from the original error",
            control_tools,
        )
        self.assertIn(
            'exact_target = {key: value for key, value in target.items() if key != "ide"}',
            control_tools,
        )
        self.assertIn("vs_bridge.call_pipe_exact(exact_target", control_tools)
        self.assertNotIn('target["ide"] == "rider"', control_tools)
        self.assertIn("async def call_pipe_exact", vs_bridge)

    def test_server_exposes_p0_tools(self) -> None:
        server_py = read("mcp_server/vs_server.py")
        for tool_name in (
            "capture_debug_bundle",
            "crash_triage",
            "step_and_capture",
            "evaluate_expressions_batch",
            "continue_and_wait_for_break",
        ):
            self.assertIn(f"@mcp.tool()\nasync def {tool_name}", server_py)

    def test_server_exposes_active_debugging_tools_and_aliases(self) -> None:
        server_py = read("mcp_server/vs_server.py")

        for tool_name in (
            "debug_capabilities",
            "attach_process",
            "get_debug_processes",
            "list_debug_processes",
            "switch_process",
            "select_process",
            "start_debugging",
            "debug_control",
            'name="continue"',
            "pause",
            'name="step_over"',
            'name="step_into"',
            'name="step_out"',
            "stop_debugging",
            "detach_process",
            "detach_debugger",
            "get_call_stack",
            "list_threads",
            "switch_stack_frame",
            "select_thread",
            "clear_breakpoint",
            "set_data_breakpoint",
            'name="set_watchpoint"',
            "hit_count",
            "condition_type",
        ):
            self.assertIn(tool_name, server_py)

        self.assertNotIn("async def set_logpoint", server_py)
        data_breakpoint_tool = function_source(server_py, "set_data_breakpoint")
        self.assertIn('preferred_ide="vs"', data_breakpoint_tool)
        watchpoint_alias = function_source(server_py, "set_watchpoint_alias")
        self.assertIn("return await set_data_breakpoint(", watchpoint_alias)
        self.assertNotIn("ide_bridge.call", watchpoint_alias)

    def test_stop_and_detach_discovery_terms_are_indexed(self) -> None:
        server_py = read("mcp_server/vs_server.py")
        vs_registration = read("VSDebuggerMCP/McpClientRegistration.cs")

        for keyword in (
            "Use this before concluding a debugger capability is missing",
            "stop debugging",
            "stop debug",
            "terminate debug session",
            "stop current Visual Studio debugging session",
            "debug_control(action='stop')",
            "detach_process",
            "detach_debugger",
        ):
            self.assertIn(keyword, server_py)

        self.assertIn("stop debugging", vs_registration)
        self.assertIn("debug_stop", vs_registration)
        self.assertNotIn("stop_debugging", vs_registration)
        self.assertIn('debug_control(action=', vs_registration)

    def test_vs_active_debugging_protocol_is_routed(self) -> None:
        vs_pipe = read("VSDebuggerMCP/NamedPipeServer.cs")
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")
        vs_models = read("VSDebuggerMCP/Protocol/DebugModels.cs")
        vs_response = read("VSDebuggerMCP/Protocol/DebugResponse.cs")

        for method_name in (
            '"attach_process"',
            '"get_debug_processes"',
            '"switch_process"',
            '"start_debugging"',
            '"clear_breakpoint"',
            '"set_data_breakpoint"',
            '"set_watchpoint"',
            '"hitCount"',
            '"conditionType"',
            '"detach"',
        ):
            self.assertIn(method_name, vs_pipe)

        self.assertNotIn('"set_logpoint"', vs_pipe)
        for symbol in (
            "AttachProcess",
            "GetDebugProcesses",
            "SwitchProcess",
            "debugger.DebuggedProcesses",
            "debugger.CurrentProcess = targetProcess",
            '_sessionTracker.InvalidateContext("switch_process")',
            "StartDebugging",
            "Detach",
            "process.Detach(false)",
            "SetDataBreakpoint",
            "ParseHitCountType",
            "ProcessNameMatches",
        ):
            self.assertIn(symbol, vs_service)

        self.assertNotIn("SetLogpoint", vs_service)
        self.assertIn("ProcessDebugResult", vs_models)
        self.assertIn("KnownType(typeof(ProcessDebugResult))", vs_response)
        self.assertIn("DebugProcessesResult", vs_models)
        self.assertIn("KnownType(typeof(DebugProcessesResult))", vs_response)

    def test_no_source_native_tools_are_exposed_without_patch_surface(self) -> None:
        server_py = read("mcp_server/vs_server.py")
        capability_tools = read("mcp_server/tool_modules/capability_tools.py")

        for tool_name in (
            "resolve_address",
            "get_module_layout",
            "capture_native_context",
            "continue_and_capture_native",
            "set_address_breakpoint",
        ):
            self.assertIn(f"async def {tool_name}", server_py)

        self.assertIn('"noSourceNative"', capability_tools)
        self.assertIn("module+RVA", capability_tools)
        self.assertIn("No write-memory or patching tool is exposed", capability_tools)
        self.assertIn("Visual Studio only", capability_tools)
        self.assertIn("require the Visual Studio backend", capability_tools)
        self.assertIn("NATIVE_CONTEXT_TIMEOUT_SECONDS = 45.0", server_py)
        self.assertIn("timeout=float(timeout_seconds + NATIVE_CONTEXT_TIMEOUT_SECONDS)", function_source(server_py, "continue_and_capture_native"))
        for tool_name in (
            "resolve_address",
            "get_module_layout",
            "capture_native_context",
            "continue_and_capture_native",
            "set_address_breakpoint",
        ):
            self.assertIn('preferred_ide="vs"', function_source(server_py, tool_name))
        self.assertNotIn("async def write_memory", server_py)
        self.assertNotIn("WriteProcessMemory", read("VSDebuggerMCP/DebuggerService.cs"))

    def test_vs_no_source_native_protocol_is_routed_and_serializable(self) -> None:
        vs_pipe = read("VSDebuggerMCP/NamedPipeServer.cs")
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")
        vs_models = read("VSDebuggerMCP/Protocol/DebugModels.cs")
        vs_response = read("VSDebuggerMCP/Protocol/DebugResponse.cs")

        for method_name in (
            '"resolve_address"',
            '"get_module_layout"',
            '"capture_native_context"',
            '"continue_and_capture_native"',
            '"set_address_breakpoint"',
            '"maxImportFunctions"',
            '"maxExports"',
            '"disassemblyCount"',
            '"stackBytes"',
        ):
            self.assertIn(method_name, vs_pipe)

        for symbol in (
            "ResolveAddress",
            "GetModuleLayout",
            "CaptureNativeContext",
            "ContinueAndCaptureNative",
            "SetAddressBreakpoint",
            "NativeAddressInfo",
            "ModuleLayoutResult",
            "NativeContextResult",
            "NativeContinueResult",
        ):
            self.assertIn(symbol, vs_service + vs_models)

        for known_type in (
            "KnownType(typeof(NativeAddressInfo))",
            "KnownType(typeof(ModuleLayoutResult))",
            "KnownType(typeof(ModuleSectionInfo))",
            "KnownType(typeof(ImportedModuleInfo))",
            "KnownType(typeof(ImportFunctionInfo))",
            "KnownType(typeof(ExportInfo))",
            "KnownType(typeof(NativeContextResult))",
            "KnownType(typeof(NativeContinueResult))",
        ):
            self.assertIn(known_type, vs_response)

        for field in (
            "virtualAddress",
            "addressValue",
            "moduleName",
            "moduleBase",
            "moduleSizeBytes",
            "rva",
            "sectionName",
            "entryPointRva",
            "imports",
            "exports",
            "stackMemory",
        ):
            self.assertIn(field, vs_models)

    def test_vs_native_address_resolution_handles_hard_address_cases(self) -> None:
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")
        vs_models = read("VSDebuggerMCP/Protocol/DebugModels.cs")
        vs_response = read("VSDebuggerMCP/Protocol/DebugResponse.cs")

        self.assertIn("TryParseModuleRvaExpression", vs_service)
        self.assertIn("expression.LastIndexOf('+')", vs_service)
        self.assertIn("module+RVA expression overflows address space", vs_service)
        self.assertIn("Expression-based address resolution requires break mode", vs_service)
        self.assertIn("FindContainingModule", vs_service)
        self.assertIn("m.Size != 0 && address >= m.Base && address - m.Base < m.Size", vs_service)
        self.assertIn("OrderByDescending(m => m.Base)", vs_service)
        self.assertIn("ModuleInfo explicitModule = null", vs_service)
        self.assertIn("module+RVA is outside the module image size", vs_service)
        self.assertNotIn(".Where(m => m.Base != 0 && address >= m.Base)", vs_service)
        self.assertIn("FindSectionForRva", vs_service)
        self.assertIn("NearestSymbol", vs_service)
        self.assertIn("moduleInformation.SizeOfImage", vs_service)
        self.assertIn("GetModulesFromProcessHandle", vs_service)
        self.assertIn("existing.SizeBytes == 0 && moduleInfo.SizeBytes != 0", vs_service)
        self.assertIn("FormatRva(result.RvaValue - sectionRva)", vs_service)
        self.assertIn("FunctionStatus", vs_models)
        self.assertIn("FunctionError", vs_models)
        self.assertIn('Name = "functionStatus"', vs_models)
        self.assertIn('Name = "functionError"', vs_models)
        self.assertIn("KnownType(typeof(NativeFunctionInfo))", vs_response)
        self.assertNotIn("NativeControlFlowInfo", vs_models + vs_response)
        self.assertNotIn("controlFlow", vs_models)
        self.assertNotIn("prologueFound", vs_models)
        self.assertNotIn("terminatorFound", vs_models)
        self.assertIn("BuildExactExportFunctionAttribution", vs_service)
        self.assertIn('"No exact native function symbol is available for this address"', vs_service)
        self.assertIn('"Only nearestSymbol+offset is available; exact native function attribution is missing"', vs_service)
        self.assertNotIn("FindFunctionStartIndex", vs_service)
        self.assertNotIn("FindFunctionEndIndex", vs_service)
        self.assertNotIn("Math.Max(0, currentIndex - 16)", vs_service)
        self.assertNotIn("function boundary is heuristic", vs_service)
        self.assertNotIn("prologue", vs_service.lower())
        self.assertNotIn("heuristic", vs_service.lower())

    def test_address_breakpoints_are_strict_and_unsafe_capability_gated(self) -> None:
        vs_native = read("VSDebuggerMCP/Services/DebuggerService.Native.cs")
        py_tools = read("mcp_server/tool_modules/breakpoint_tools.py")
        method_manifest = json.loads(read("mcp_server/debug_method_manifest.json"))["methods"]

        self.assertIn("Address breakpoints accept only an absolute address or module+RVA", vs_native)
        self.assertIn('validate_address_breakpoint_target("address", address)', py_tools)
        self.assertEqual(
            ["debug.breakpoint.write"],
            method_manifest["set_address_breakpoint"]["capabilities"],
        )
        self.assertEqual(
            [{
                "capability": "debug.evaluate.unsafe",
                "parameter": "condition",
                "presentNonBlank": True,
            }],
            method_manifest["set_address_breakpoint"]["conditionalCapabilities"],
        )

    def test_debug_waiters_do_not_cross_session_generations(self) -> None:
        tracker = read("VSDebuggerMCP/DebugSessionTracker.cs")
        execution = read("VSDebuggerMCP/Services/DebuggerService.Execution.cs")
        native = read("VSDebuggerMCP/Services/DebuggerService.Native.cs")

        generation_branch = tracker[
            tracker.index("if (current.Generation != afterGeneration)"):
            tracker.index("if (current.BreakSequence > afterBreakSequence")
        ]
        self.assertIn("return current;", generation_branch)
        self.assertNotIn("afterGeneration = current.Generation", tracker)
        self.assertIn('result.ErrorCode = "STALE_DEBUG_SESSION"', execution)
        self.assertIn('result.ErrorCode = "STALE_DEBUG_SESSION"', native)

    def test_context_invalidation_preserves_raw_stop_event(self) -> None:
        tracker = read("VSDebuggerMCP/DebugSessionTracker.cs")
        invalidate = tracker[
            tracker.index("public void InvalidateContext"):
            tracker.index("public StopEventSnapshot GetStopEventSnapshot")
        ]
        self.assertIn("Interlocked.Increment(ref _contextEpoch)", invalidate)
        self.assertNotIn("_stopReasonRaw =", invalidate)

    def test_add_watch_preserves_at_most_once_command_outcome(self) -> None:
        service = read("VSDebuggerMCP/Services/DebuggerService.SecondaryInspection.cs")
        models = read("VSDebuggerMCP/Protocol/DebugModels.cs")
        policy = read("VSDebuggerMCP/Ipc/OperationResultPolicy.cs")
        output_models = read("mcp_server/output_models.py")

        self.assertIn("public class WatchResult : IOperationResult", models)
        self.assertIn("result.CommandIssued = true", service)
        self.assertIn("result.EvaluationIssued = true", service)
        self.assertIn("MarkUnsafeEvaluationIssued()", service)
        self.assertIn("watch.CommandIssued || watch.EvaluationIssued", policy)
        for field in ("success", "message", "commandIssued", "evaluationIssued", "evaluationStatus", "outcome"):
            self.assertIn(f'Name = "{field}"', models)
            self.assertIn(field, output_models)

    def test_ui_thread_and_frame_changes_advance_context_epoch(self) -> None:
        service = read("VSDebuggerMCP/DebuggerService.cs")
        inspection = read("VSDebuggerMCP/Services/DebuggerService.Inspection.cs")
        tracker = read("VSDebuggerMCP/DebugSessionTracker.cs")

        validate = service[
            service.index("private void ValidateExpectedDebuggerIdentity"):
            service.index("private static void RethrowRequestControlFlow")
        ]
        self.assertIn("ObserveDebugContext(Debugger)", validate)
        self.assertIn("debugger?.CurrentThread?.ID", inspection)
        self.assertIn("Marshal.GetIUnknownForObject(frame)", inspection)
        self.assertIn("ObserveDebuggerContext", tracker)
        self.assertIn("Interlocked.Increment(ref _contextEpoch)", tracker)

    def test_callstack_reports_incomplete_frame_capture(self) -> None:
        inspection = read("VSDebuggerMCP/Services/DebuggerService.Inspection.cs")
        models = read("VSDebuggerMCP/Protocol/DebugModels.cs")
        output_models = read("mcp_server/output_models.py")

        self.assertIn("ExpectedFrameCount = header.FrameCount", inspection)
        self.assertIn("result.CapturedFrameCount = result.Frames.Count", inspection)
        self.assertIn("result.SnapshotStatus = result.Partial", inspection)
        for field in ("expectedFrameCount", "capturedFrameCount", "partial", "error"):
            self.assertIn(f'Name = "{field}"', models)
            self.assertIn(field, output_models)

    def test_disassembly_does_not_guess_preceding_instruction_boundaries(self) -> None:
        service = read("VSDebuggerMCP/Services/DebuggerService.Disassembly.cs")
        models = read("VSDebuggerMCP/Protocol/DebugModels.cs")
        output_models = read("mcp_server/output_models.py")

        self.assertNotIn("requireCurrentAlignment", service)
        self.assertNotIn("maximumStartOffset", service)
        self.assertIn("header.InstructionPointer,", service)
        self.assertIn("CurrentAddress = currentAddress", service)
        self.assertIn("StartAddress = address", service)
        self.assertIn("unavailable_without_trusted_instruction_boundary", service)
        for field in ("startAddress", "precedingInstructionsStatus"):
            self.assertIn(f'Name = "{field}"', models)
            self.assertIn(field, output_models)

    def test_os_memory_and_modules_require_trusted_live_process_identity(self) -> None:
        service = read("VSDebuggerMCP/DebuggerService.cs")
        execution = read("VSDebuggerMCP/Services/DebuggerService.Execution.cs")
        memory = read("VSDebuggerMCP/Services/DebuggerService.EvaluationMemory.cs")
        inspection = read("VSDebuggerMCP/Services/DebuggerService.Inspection.cs")
        disassembly = read("VSDebuggerMCP/Services/DebuggerService.Disassembly.cs")

        self.assertIn("OpenTrustedLiveProcessHandle", service)
        self.assertIn("trustedGeneration != _sessionTracker.Generation", service)
        self.assertIn("actualStartTime != trustedStartTime", service)
        self.assertIn("RecordTrustedLiveProcessIdentity", execution)
        self.assertIn("RefreshTrustedLiveProcessIdentity", service)
        self.assertNotIn("CurrentDebuggeeHasLocalProcessIdentity", service)
        self.assertIn("Never enumerate Debugger.LocalProcesses here", service)
        self.assertIn("_pendingTrustedLiveProcessStartTimeUtcTicks", service)
        self.assertIn("debugger_expression_fallback_untrusted_session", memory)
        self.assertIn("OpenTrustedLiveProcessHandle", memory)
        self.assertIn("OpenTrustedLiveProcessHandle", inspection)
        self.assertNotIn("ValidateMemoryWithExpression", disassembly + memory)

    def test_managed_inspection_drains_and_reports_output_overflow(self) -> None:
        service = read("VSDebuggerMCP/Services/ManagedInspectionService.cs")

        self.assertIn("stdoutOverflow.Task.IsCompleted", service)
        self.assertIn("await Task.WhenAll(stdoutTask, stderrTask)", service)
        self.assertIn("throw CreateOverflowException", service)
        self.assertIn("return new ResourceLimitExceededException", service)
        self.assertIn("overflow.TrySetResult(true)", service)
        self.assertIn("if (exceeded)", service)

    def test_output_search_has_cancellation_and_total_budget(self) -> None:
        service = read("VSDebuggerMCP/Services/OutputWindowService.cs")
        capture = read("VSDebuggerMCP/Services/DebuggerService.SecondaryInspection.cs")

        self.assertIn("SearchBudgetMs = 2000", service)
        self.assertIn("DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded", service)
        self.assertIn("stopwatch.ElapsedMilliseconds >= SearchBudgetMs", service)
        self.assertIn("TimeSpan.FromMilliseconds(100)", service)
        self.assertIn('snapshot.SnapshotStatus = stable ? "best_effort" : "unstable"', capture)
        self.assertNotIn('snapshot.SnapshotStatus = stable ? "stable" : "unstable"', capture)

    def test_named_pipe_connection_is_single_request_with_short_initial_frame_timeout(self) -> None:
        server = read("VSDebuggerMCP/NamedPipeServer.cs")
        handler = server
        self.assertIn("InitialFrameIoTimeoutMs = 2000", server)
        self.assertIn("frameReadCts.CancelAfter(InitialFrameIoTimeoutMs)", handler)
        self.assertNotIn("while (pipeServer.IsConnected", handler)
        self.assertIn("DataPipeInstances = 8", server)
        self.assertIn("InterruptPipeInstances = 4", server)
        self.assertIn("RecoveryPipeInstances = 2", server)
        self.assertIn("StartListenerPool(_recoveryPipeName, true, true", server)

    def test_locals_and_exception_capture_report_incomplete_data(self) -> None:
        inspection = read("VSDebuggerMCP/Services/DebuggerService.Inspection.cs")
        variables = read("VSDebuggerMCP/Services/DebuggerService.VariableSlices.cs")
        evaluation = read("VSDebuggerMCP/Services/DebuggerService.EvaluationMemory.cs")
        models = read("VSDebuggerMCP/Protocol/DebugModels.cs")
        output_models = read("mcp_server/output_models.py")

        self.assertIn("budget.ReportCaptureError", variables)
        self.assertIn("Variable child metadata is unavailable", variables)
        self.assertIn("Variable children are incomplete", variables)
        self.assertIn("budget.IsIncomplete", inspection + evaluation + variables)
        self.assertIn("Inner exception chain was truncated", inspection)
        self.assertIn("result.HasMoreInnerExceptions = true", inspection)
        self.assertIn("expression could not be evaluated", inspection)
        self.assertIn("The debugger could not evaluate $exception", inspection)
        for field in ("capturedDepth", "maxDepth", "hasMoreInnerExceptions"):
            self.assertIn(f'Name = "{field}"', models)
            self.assertIn(field, output_models)

    def test_expression_hex_formatting_does_not_evaluate_twice(self) -> None:
        variables = read("VSDebuggerMCP/Services/DebuggerService.VariableSlices.cs")
        self.assertIn("TryFormatCapturedNumericValueAsHex(result.Value", variables)
        self.assertNotIn('GetExpression(\n                $"(void*){expression}"', variables)
        self.assertNotIn("CaptureEvaluationHexValue", variables)

    def test_full_context_exposes_cross_component_snapshot_truth(self) -> None:
        context = read("VSDebuggerMCP/Services/DebuggerService.Context.cs")
        models = read("VSDebuggerMCP/Protocol/DebugModels.cs")
        output_models = read("mcp_server/output_models.py")

        self.assertIn("CaptureFullContextBoundary", context)
        self.assertIn("unsafeEvaluationAttempted", context)
        self.assertIn('fullContextResult.SnapshotStatus = !boundaryStable', context)
        self.assertIn("!result.Modules.SnapshotStable", context)
        for field in (
            "snapshotStable", "snapshotStatus", "startDebugSessionGeneration",
            "startBreakSequence", "startContextEpoch", "endDebugSessionGeneration",
            "endBreakSequence", "endContextEpoch",
        ):
            self.assertIn(f'Name = "{field}"', models)
            self.assertIn(field, output_models)

    def test_vs_bundle_deployment_is_content_and_version_addressed(self) -> None:
        registration = read("VSDebuggerMCP/McpClientRegistration.cs")
        installer = read("VSDebuggerMCP/Install-VSIX.ps1")

        self.assertIn("bundleIdentity", registration)
        self.assertIn("Assembly.GetName().Version", registration)
        self.assertIn("expectedSignature.Substring(0, 16)", registration)
        self.assertIn('Path.Combine(bundleRoot, bundleIdentity)', registration)
        self.assertIn('Path.Combine(stagingDir, ".bundle-version")', registration)
        self.assertIn("versionedPayloadRoot", installer)
        self.assertIn("bundleIdentity", installer)
        self.assertIn(".active-bundle", registration)
        self.assertIn(".active-bundle", installer)
        self.assertIn("StableServerEntryFileName", registration)
        self.assertIn("vs_launcher.py", read("VSDebuggerMCP/VSDebuggerMCP.csproj"))
        activation = installer[installer.index("function Activate-McpServerDeployment"):]
        activation = activation[:activation.index("function Remove-PreparedDeployments")]
        self.assertLess(
            activation.index("Ensure-PlainDirectoryTree -path $bundleRoot"),
            activation.index("Move-Item -LiteralPath $stagingRoot -Destination $userMcpRoot"),
        )
        self.assertIn("[System.IO.FileAttributes]::ReparsePoint", installer)
        self.assertIn("Assert-PlainOwnedTree -path $stagingRoot", activation)
        self.assertIn("Refusing to create an MCP bundle root outside", activation)

    def test_vs_instance_access_mode_is_discoverable(self) -> None:
        models = read("VSDebuggerMCP/Protocol/DebugModels.cs")
        router = read("VSDebuggerMCP/Ipc/NamedPipeMethodRouter.cs")
        vs_bridge_py = read("mcp_server/vs_bridge.py")
        capability_module = read("mcp_server/tool_modules/capability_tools.py")
        capabilities = function_source(read("mcp_server/vs_server.py"), "debug_capabilities")

        for marker in ("allowedCapabilities", "accessMode"):
            self.assertIn(marker, models)
            self.assertIn(marker, vs_bridge_py)
        self.assertIn("_allowedCapabilities.SetEquals", router)
        self.assertIn("instanceAccess", capabilities)
        self.assertIn("effectiveInstance", capabilities)
        self.assertIn("staleSelectedTarget", capabilities)
        self.assertIn("has_pinned_selection", capabilities)
        self.assertIn("static_tool_index", capabilities)
        self.assertIn("_recommended_workflow_for_access", capabilities)
        self.assertIn("do not assume any debugger capability", capability_module)
        self.assertIn("use only actions whose required capabilities", capability_module)
        self.assertIn('access_mode == "full_access"', capability_module)

    def test_debug_bundle_default_does_not_claim_exception_capture(self) -> None:
        snapshot_tools = read("mcp_server/tool_modules/snapshot_tools.py")
        capability_tools = read("mcp_server/tool_modules/capability_tools.py")
        registration = read("VSDebuggerMCP/McpClientRegistration.cs")

        self.assertIn("默认低阻塞：只抓 status/callstack/threads", snapshot_tools)
        self.assertNotIn("默认 status/callstack/exception/threads", registration)
        self.assertIn("include_exception=true", registration)
        self.assertNotIn("status/callstack/exception/threads", capability_tools)
        self.assertIn("Exception requires include_exception=True", capability_tools)

    def test_vs_python_probe_pins_a_supported_x64_windows_executable(self) -> None:
        registration = read("VSDebuggerMCP/McpClientRegistration.cs")
        self.assertIn("struct.calcsize('P')*8", registration)
        self.assertIn("sysconfig.get_platform()", registration)
        self.assertIn('string.Equals(platform, "win_amd64"', registration)
        self.assertIn("Command = Path.GetFullPath(executable)", registration)
        self.assertIn('argsBuilder.Append("-I -S -c ")', registration)
        self.assertIn('PrefixArgs = new[] { "-I", "-S" }', registration)

    def test_vs_module_layout_parser_covers_pe_boundaries_and_truncation(self) -> None:
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")

        for marker in (
            "Missing MZ header",
            "Missing PE signature",
            "Unsupported PE optional header magic",
            "safeSectionCount = Math.Min(sectionCount, (ushort)96)",
            "ReadExports",
            "ReadImports",
            "RvaToFileOffset",
            "ReadNullTerminatedAscii",
            "maxImportFunctions",
            "maxExports",
            "result.Truncated = true",
            "ordinalFlag",
            "thunkMask",
            "0x8000000000000000UL",
            "0x80000000UL",
            "FileShare.ReadWrite | System.IO.FileShare.Delete",
        ):
            self.assertIn(marker, vs_service)

    def test_vs_native_context_is_snapshot_oriented_and_read_only(self) -> None:
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")
        server_py = read("mcp_server/vs_server.py")
        capability_tools = read("mcp_server/tool_modules/capability_tools.py")

        native_context = csharp_slice(vs_service, "public NativeContextResult CaptureNativeContext", "        public NativeContinueResult ContinueAndCaptureNative")
        self.assertIn('string instructionPointer = is64Bit.Value ? "@rip" : "@eip"', native_context)
        self.assertIn('string stackPointer = is64Bit.Value ? "@rsp" : "@esp"', native_context)
        self.assertIn('result.ArchitectureStatus = unknown ? "unknown" : "unsupported"', native_context)
        self.assertIn('"ARCHITECTURE_NOT_SUPPORTED"', native_context)
        self.assertIn("ResolveAddressWithModules(instructionPointer, addressModules)", native_context)
        self.assertIn('RetainNativeComponent(result, budget, "registers", GetRegisters())', native_context)
        self.assertIn('RetainNativeComponent(result, budget, "callstack", GetCallStack())', native_context)
        self.assertIn("GetDisassemblyAt(address", native_context)
        self.assertIn("ReadMemory(address", native_context)
        self.assertIn('RetainNativeComponent(result, budget, "modules", addressModules)', native_context)

        continue_native = csharp_slice(vs_service, "public NativeContinueResult ContinueAndCaptureNative", "        public ManagedAssemblyResult InspectManagedAssembly")
        self.assertIn("Math.Max(50, Math.Min(pollIntervalMs, 1000))", continue_native)
        self.assertIn("WaitForBreakOrDesign", continue_native)
        self.assertNotIn("Thread.Sleep", continue_native)
        self.assertIn("CaptureNativeContext(disassemblyCount, stackBytes, includeModules)", continue_native)
        self.assertIn("Timed out waiting for debugger to enter break mode", continue_native)

        self.assertIn('"includeModules": include_modules', function_source(server_py, "capture_native_context"))
        self.assertIn('"includeModules": include_modules', function_source(server_py, "continue_and_capture_native"))

    def test_no_source_managed_and_hybrid_tools_are_exposed(self) -> None:
        server_py = read("mcp_server/vs_server.py")
        capability_tools = read("mcp_server/tool_modules/capability_tools.py")
        vs_registration = read("VSDebuggerMCP/McpClientRegistration.cs")

        for tool_name in (
            "inspect_managed_assembly",
            "inspect_managed_type",
            "inspect_managed_method_il",
            "capture_hybrid_context",
            "get_breakpoint_bindings",
            "continue_and_capture_breakpoint",
        ):
            self.assertIn(f"async def {tool_name}", server_py)

        self.assertIn('"noSourceManaged"', capability_tools)
        self.assertIn('"noSourceHybrid"', capability_tools)
        self.assertIn("without source or UI automation", capability_tools)
        self.assertIn("Breakpoint-oriented dynamic loop", capability_tools)
        self.assertIn("MANAGED_INSPECTION_TIMEOUT_SECONDS = 75.0", server_py)
        self.assertIn("HYBRID_CONTEXT_TIMEOUT_SECONDS = 135.0", server_py)
        self.assertIn("tool_timeout_for_budget(timeout_seconds, HYBRID_CONTEXT_TIMEOUT_SECONDS, thread_budget_ms)", function_source(server_py, "capture_hybrid_context"))
        for tool_name in (
            "inspect_managed_assembly",
            "inspect_managed_type",
            "inspect_managed_method_il",
            "capture_hybrid_context",
            "get_breakpoint_bindings",
            "continue_and_capture_breakpoint",
        ):
            self.assertIn('preferred_ide="vs"', function_source(server_py, tool_name))
        self.assertIn("continue_and_capture_breakpoint", vs_registration)
        self.assertIn("inspect_managed_method_il", vs_registration)
        self.assertIn("No-source hybrid dynamic loop", vs_registration)

    def test_vs_no_source_managed_and_hybrid_protocol_is_routed_and_serializable(self) -> None:
        vs_pipe = read("VSDebuggerMCP/NamedPipeServer.cs")
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")
        vs_models = read("VSDebuggerMCP/Protocol/DebugModels.cs")
        vs_response = read("VSDebuggerMCP/Protocol/DebugResponse.cs")

        for method_name in (
            '"inspect_managed_assembly"',
            '"inspect_managed_type"',
            '"inspect_managed_method_il"',
            '"capture_hybrid_context"',
            '"get_breakpoint_bindings"',
            '"continue_and_capture_breakpoint"',
            '"assemblyPath"',
            '"typeName"',
            '"methodName"',
            '"overloadIndex"',
            '"maxInstructions"',
            '"includeNative"',
            '"includeBreakpoints"',
        ):
            self.assertIn(method_name, vs_pipe)

        for symbol in (
            "InspectManagedAssembly",
            "InspectManagedType",
            "InspectManagedMethodIl",
            "CaptureHybridContext",
            "GetBreakpointBindings",
            "ContinueAndCaptureBreakpoint",
            "ManagedAssemblyResult",
            "ManagedMethodIlResult",
            "HybridContextResult",
            "BreakpointCaptureResult",
        ):
            self.assertIn(symbol, vs_service + vs_models)

        for known_type in (
            "KnownType(typeof(ManagedAssemblyResult))",
            "KnownType(typeof(ManagedTypeResult))",
            "KnownType(typeof(ManagedTypeInfo))",
            "KnownType(typeof(ManagedMethodInfo))",
            "KnownType(typeof(ManagedParameterInfo))",
            "KnownType(typeof(ManagedLocalInfo))",
            "KnownType(typeof(ManagedMethodIlResult))",
            "KnownType(typeof(ManagedIlInstructionInfo))",
            "KnownType(typeof(HybridContextResult))",
            "KnownType(typeof(BreakpointCaptureResult))",
            "KnownType(typeof(BreakpointHitInfo))",
        ):
            self.assertIn(known_type, vs_response)

        for field in (
            "assemblyFullName",
            "moduleVersionId",
            "metadataTokenHex",
            "operandType",
            "resolvedMember",
            "branchTarget",
            "switchTargets",
            "nativeContext",
            "selectedStackFrameIndex",
            "continueResult",
            "hitAttributionStatus",
            "hitAttributionError",
            "hitVerificationStatus",
            "hitVerificationMessage",
            "hitBreakpoint",
            "trackedAddress",
            "trackedAddresses",
            "bindingVerificationStatus",
            "bindingWarning",
            "isRelocated",
            "nearbyProbeAttempted",
            "nearbyProbeSucceeded",
            "optimizedCodeSuspected",
            "verificationStatus",
            "verificationMessage",
        ):
            self.assertIn(field, vs_models)
        self.assertNotIn("likelyHitBreakpoint", vs_models)
        self.assertNotIn("boundAddresses", vs_models)

    def test_managed_inspection_helper_is_packaged_as_external_process(self) -> None:
        vs_project = read("VSDebuggerMCP/VSDebuggerMCP.csproj")
        helper_project = read("VSDebuggerMCP/ManagedInspectionHelper/ManagedInspectionHelper.csproj")
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")
        native_service = read("VSDebuggerMCP/Services/DebuggerService.Native.cs")
        managed_service = read("VSDebuggerMCP/Services/ManagedInspectionService.cs")
        managed_process = read("VSDebuggerMCP/Services/ManagedInspectionProcess.cs")

        self.assertIn("<OutputType>Exe</OutputType>", helper_project)
        self.assertIn("<TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>", helper_project)
        self.assertIn('<Compile Include="..\\Protocol\\DebugModels.cs">', helper_project)
        self.assertIn("<Link>Protocol\\DebugModels.cs</Link>", helper_project)
        self.assertIn('<Reference Include="System.Web.Extensions" />', helper_project)
        self.assertIn("BuildManagedInspectionHelper", vs_project)
        self.assertIn("ManagedInspectionHelper\\ManagedInspectionHelper.csproj", vs_project)
        self.assertIn("ManagedInspectionHelper\\bin\\$(Configuration)\\ManagedInspectionHelper.exe", vs_project)
        self.assertIn("<IncludeInVSIX>true</IncludeInVSIX>", vs_project)

        managed_entrypoints = csharp_slice(native_service, "public ManagedAssemblyResult InspectManagedAssembly", "        public HybridContextResult CaptureHybridContext")
        self.assertIn("_managedInspectionService.InspectAssembly", managed_entrypoints)
        self.assertIn("_managedInspectionService.InspectType", managed_entrypoints)
        self.assertIn("_managedInspectionService.InspectMethodIl", managed_entrypoints)
        self.assertIn("appcontainer_managed_metadata_and_il_read_only", managed_service)
        self.assertIn("ManagedInspectionProcess.Start", managed_service)
        self.assertIn("ProcThreadAttributeSecurityCapabilities", managed_process)
        self.assertIn("ProcThreadAttributeHandleList", managed_process)
        self.assertIn('"ManagedInspectionHelper.exe",', managed_process)
        self.assertIn('"Mono.Cecil.dll"', managed_process)
        self.assertNotIn('"*.dll"', managed_process)
        self.assertNotIn('"*.exe"', managed_process)
        self.assertIn("EnsurePlainSourceFile", managed_process)
        self.assertIn("ComputeSha256", managed_process)
        self.assertIn("staging checksum mismatch", managed_process)
        self.assertIn("unexpected payload", managed_process)
        self.assertIn("FileNotFoundException(\"ManagedInspectionHelper.exe was not found", managed_service)
        combined = vs_service + native_service + managed_service + managed_process
        self.assertNotIn("Assembly.LoadFrom", combined)
        self.assertNotIn("ReflectionTypeLoadException", combined)
        self.assertNotIn("AssemblyResolve", combined)
        self.assertNotIn("DecodeIlInstructions", combined)
        self.assertNotIn("OperandType.", combined)
        self.assertNotIn("BuildOpCodeMap", combined)

    def test_managed_inspection_helper_parses_metadata_without_loading_target_code(self) -> None:
        helper = read("VSDebuggerMCP/ManagedInspectionHelper/Program.cs")
        helper_project = read("VSDebuggerMCP/ManagedInspectionHelper/ManagedInspectionHelper.csproj")
        managed_service = read("VSDebuggerMCP/Services/ManagedInspectionService.cs")
        managed_process = read("VSDebuggerMCP/Services/ManagedInspectionProcess.cs")

        for marker in (
            "AssemblyDefinition.ReadAssembly",
            "ReaderParameters",
            "ReadWrite = false",
            "ReadingMode = ReadingMode.Immediate",
            "method.Body.Instructions",
            "instruction.Operand is Instruction[]",
            "instruction.Operand is IMetadataTokenProvider",
            "FormatMetadataOperand",
            "FormatIlOffset",
        ):
            self.assertIn(marker, helper)

        for marker in (
            "NormalizeProbeDirectories",
            "methodName, \".ctor\"",
            "methodName, \".cctor\"",
            "overloadIndex",
            "maxInstructions",
        ):
            self.assertIn(marker, helper)

        for forbidden in (
            "Assembly.LoadFrom",
            "Assembly.LoadFile",
            "Assembly.Load(",
            "ReflectionOnlyLoadFrom",
            "AppDomain.CurrentDomain.AssemblyResolve",
            "GetMethodBody()",
        ):
            self.assertNotIn(forbidden, helper)
        self.assertIn('<PackageReference Include="Mono.Cecil" Version="0.11.6" />', helper_project)

        for marker in (
            "CreateAppContainerProfile",
            "ProcThreadAttributeSecurityCapabilities",
            "ProcThreadAttributeHandleList",
            "--assembly-handle",
            "StageHelperInContainer",
        ):
            self.assertIn(marker, managed_process)

        self.assertIn("ManagedInspectionProcess.Start", managed_service)
        self.assertNotIn("Assembly.LoadFrom", managed_service + managed_process)

    def test_managed_inspection_edge_fixture_exercises_nontrivial_il(self) -> None:
        fixture_project = read("tests/fixtures/ManagedInspectionEdgeCases/ManagedInspectionEdgeCases.csproj")
        fixture_code = read("tests/fixtures/ManagedInspectionEdgeCases/HardIlCases.cs")

        self.assertIn("<OutputType>Library</OutputType>", fixture_project)
        for marker in (
            "public int DenseSwitch",
            "switch (mode)",
            "checked(value * 2)",
            "public string TokenHeavy<T>",
            "typeof(T)",
            "string.Format",
            "public int Overloaded(int value)",
            "public string Overloaded(string value)",
            "public int TryFinallyLoop",
            "finally",
            "private int Helper",
        ):
            self.assertIn(marker, fixture_code)

    def test_managed_inspection_helper_executes_edge_fixture(self) -> None:
        msbuild = find_msbuild()
        if msbuild is None:
            self.skipTest("MSBuild.exe not found")

        helper_project = ROOT / "VSDebuggerMCP/ManagedInspectionHelper/ManagedInspectionHelper.csproj"
        fixture_project = ROOT / "tests/fixtures/ManagedInspectionEdgeCases/ManagedInspectionEdgeCases.csproj"
        for project in (helper_project, fixture_project):
            completed = subprocess.run(
                [str(msbuild), str(project), "/p:Configuration=Release", "/m:1", "/v:minimal"],
                cwd=ROOT,
                text=True,
                encoding="utf-8",
                errors="replace",
                capture_output=True,
            )
            self.assertEqual(completed.returncode, 0, completed.stdout + completed.stderr)

        helper_exe = ROOT / "VSDebuggerMCP/ManagedInspectionHelper/bin/Release/ManagedInspectionHelper.exe"
        fixture_dll = ROOT / "tests/fixtures/ManagedInspectionEdgeCases/bin/Release/ManagedInspectionEdgeCases.dll"
        self.assertTrue(helper_exe.exists())
        self.assertTrue(fixture_dll.exists())

        def invoke_helper(request: dict) -> dict:
            sentinel = ROOT / "tests/fixtures/ManagedInspectionEdgeCases/bin/Release/inspection-executed.txt"
            sentinel.unlink(missing_ok=True)
            invocation_environment = os.environ.copy()
            invocation_environment["AIDB_MANAGED_INSPECTION_SENTINEL"] = str(sentinel)
            import msvcrt
            with fixture_dll.open("rb") as assembly_stream:
                assembly_handle = msvcrt.get_osfhandle(assembly_stream.fileno())
                os.set_handle_inheritable(assembly_handle, True)
                try:
                    completed = subprocess.run(
                        [str(helper_exe), "--assembly-handle", str(assembly_handle)],
                        cwd=ROOT,
                        input=json.dumps(request),
                        text=True,
                        encoding="utf-8",
                        errors="replace",
                        capture_output=True,
                        env=invocation_environment,
                        close_fds=False,
                    )
                finally:
                    os.set_handle_inheritable(assembly_handle, False)
            self.assertEqual(completed.returncode, 0, completed.stdout + completed.stderr)
            self.assertTrue(completed.stdout.strip(), completed.stderr)
            self.assertFalse(sentinel.exists(), "managed inspection executed target assembly module initialization")
            return json.loads(completed.stdout)

        def result_value(result: dict, name: str):
            pascal_name = name[:1].upper() + name[1:]
            return result.get(name, result.get(pascal_name))

        common = {
            "AssemblyPath": str(fixture_dll),
            "TypeName": "ManagedInspectionEdgeCases.HardIlCases",
            "ProbeDirectories": [str(fixture_dll.parent)],
        }
        dense_switch = invoke_helper({
            **common,
            "Operation": "inspect_managed_method_il",
            "MethodName": "DenseSwitch",
            "OverloadIndex": 0,
            "MaxInstructions": 512,
        })
        dense_instructions = result_value(dense_switch, "instructions")
        self.assertFalse(result_value(dense_switch, "partial"), result_value(dense_switch, "error"))
        self.assertTrue(any(result_value(i, "switchTargets") for i in dense_instructions))
        self.assertTrue(any(result_value(i, "branchTarget") for i in dense_instructions))

        token_heavy = invoke_helper({
            **common,
            "Operation": "inspect_managed_method_il",
            "MethodName": "TokenHeavy",
            "OverloadIndex": 0,
            "MaxInstructions": 512,
        })
        token_instructions = result_value(token_heavy, "instructions")
        self.assertTrue(any(result_value(i, "resolvedMember") for i in token_instructions))
        self.assertTrue(any(result_value(i, "operandType") == "InlineType" for i in token_instructions))

        bad_overload = invoke_helper({
            **common,
            "Operation": "inspect_managed_method_il",
            "MethodName": "Overloaded",
            "OverloadIndex": 99,
            "MaxInstructions": 512,
        })
        self.assertIn("overloadIndex 99 is out of range", result_value(bad_overload, "error") or "")

    def test_hybrid_breakpoint_capture_composes_no_source_dynamic_snapshot(self) -> None:
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")
        native_service = read("VSDebuggerMCP/Services/DebuggerService.Native.cs")
        breakpoint_service = read("VSDebuggerMCP/Services/DebuggerService.Breakpoints.cs")
        server_py = read("mcp_server/vs_server.py")

        hybrid = csharp_slice(native_service, "public HybridContextResult CaptureHybridContext", "        public BreakpointsResult GetBreakpointBindings")
        for marker in (
            'RetainHybridComponent(result, budget, "status", GetDebugStatus())',
            "SelectedStackFrameIndex = 0",
            "CaptureNativeContext(",
            "GetThreads(",
            'RetainHybridComponent(\n                        result, budget, "breakpoints", GetBreakpoints())',
            "includeNative",
            "includeThreads",
            "includeBreakpoints",
        ):
            self.assertIn(marker, hybrid)

        capture = csharp_slice(
            native_service,
            "public BreakpointCaptureResult ContinueAndCaptureBreakpoint",
            "        private NativeAddressInfo ResolveAddressInternal")
        for marker in (
            "ContinueAndWaitForBreak(",
            "includeException: false",
            "CaptureHybridContext(",
            "GetBreakpointBindings()",
            "waitResult.BreakDetected",
            "DetermineHitBreakpointExact",
            "HitAttributionStatus = \"ok\"",
            "HitAttributionStatus = \"missing\"",
            "\"No debug stop was captured\"",
            "\"Current instruction address exactly matches an enabled bound tracked address breakpoint\"",
            "\"Current source location exactly matches an enabled bound source breakpoint resolved location\"",
            "\"No enabled bound tracked address breakpoint or source breakpoint exactly matches the current stop location\"",
            "\"Multiple tracked address breakpoints match the current instruction address\"",
            "\"Multiple enabled bound source breakpoints match the current source location\"",
        ):
            self.assertIn(marker, capture)

        combined_service = vs_service + native_service + breakpoint_service
        self.assertIn("AddressBreakpointTagPrefix = \"AIDB_ADDR=\"", combined_service)
        self.assertIn("TrySetAddressBreakpointTag(breakpoint, addressText)", combined_service)
        self.assertIn("TryGetAddressBreakpointTag", combined_service)
        self.assertIn("TryParseAddressLiteral(taggedAddress", combined_service)
        self.assertIn("TryParseModuleRvaExpression(taggedAddress", combined_service)
        self.assertIn("Legacy address tag is not a literal or module+RVA and was not evaluated", combined_service)
        self.assertIn("breakpointInfo.TrackedAddresses.Add(breakpointInfo.TrackedAddress)", combined_service)
        self.assertIn("!breakpoint.Enabled", combined_service)
        self.assertIn("!breakpoint.IsBound", combined_service)
        self.assertIn("PopulateHitVerification(result)", combined_service)
        self.assertIn("hit_exact_tracked_address", combined_service)
        self.assertIn("hit_exact_source_location", combined_service)
        self.assertIn("not_hit_timeout", combined_service)
        self.assertIn("breakpoint_stop_unattributed", combined_service)
        self.assertIn("stopped_not_verified_as_breakpoint", combined_service)
        self.assertIn("FindCurrentSourceLocationBreakpointMatches", combined_service)
        self.assertNotIn("LikelyHitBreakpoint", combined_service)
        self.assertNotIn("exactMatches.Count == 0 && breakpoints.Count == 1", combined_service)

        breakpoint_tool = function_source(server_py, "continue_and_capture_breakpoint")
        self.assertIn("add_thread_capture_params", breakpoint_tool)
        self.assertIn('"includeNative": include_native', breakpoint_tool)
        self.assertIn('"includeBreakpoints": include_breakpoints', breakpoint_tool)
        self.assertIn("timeout=float(timeout_seconds + tool_timeout_for_budget(None, HYBRID_CONTEXT_TIMEOUT_SECONDS, thread_budget_ms))", breakpoint_tool)

    def test_wait_timeout_rebases_context_before_status_capture(self) -> None:
        execution_service = read("VSDebuggerMCP/Services/DebuggerService.Execution.cs")
        native_service = read("VSDebuggerMCP/Services/DebuggerService.Native.cs")
        continue_wait = csharp_slice(
            execution_service,
            "public ContinueAndWaitResult ContinueAndWaitForBreak",
            "        public DebugControlResult StepInto",
        )
        native_wait = csharp_slice(
            native_service,
            "public NativeContinueResult ContinueAndCaptureNative",
            "        public ManagedAssemblyResult InspectManagedAssembly",
        )

        self.assertIn(
            "RebaseRequestToCurrentDebuggerIdentity();\n            result.Status = ExecuteLongWaitPhase(GetDebugStatus, phaseExecutor);\n            result.StopReason",
            continue_wait,
        )
        self.assertIn(
            "RebaseRequestToCurrentDebuggerIdentity();\n              result.Status = ExecuteLongWaitPhase(GetDebugStatus, phaseExecutor);\n            result.Success = false;\n            result.TimedOut = true",
            native_wait,
        )
        for method in (continue_wait, native_wait):
            self.assertIn("result.ErrorCode = continueResult.ErrorCode", method)

    def test_breakpoint_binding_verification_surfaces_optimized_or_unbound_locations(self) -> None:
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")
        vs_models = read("VSDebuggerMCP/Protocol/DebugModels.cs")
        server_py = read("mcp_server/vs_server.py")

        for field in (
            "bindingVerificationStatus",
            "bindingWarning",
            "isRelocated",
            "nearbyProbeAttempted",
            "nearbyProbeSucceeded",
            "optimizedCodeSuspected",
            "verificationStatus",
            "verificationMessage",
        ):
            self.assertIn(field, vs_models)

        for marker in (
            "PopulateBreakpointBindingAwareness",
            "ApplyBreakpointOperationVerification",
            "MarkNearbyProbeResult",
            "IsActiveDebugSession",
            "effectiveRequestedLineNumber",
            "breakpoint.LocationType == dbgBreakpointLocationType.dbgBreakpointLocationTypeFile",
            "bound_relocated",
            "bound_exact",
            "unbound",
            "Breakpoint did not bind in the current debug session",
            "Requested source line did not bind; a nearby executable line was selected",
            "no nearby bound executable line was found",
            "Optimized code, statement folding, or source/symbol mismatch may be involved",
            "IsBreakpointBound",
            "dbgDebugMode.dbgRunMode",
            "return \"pending\"",
            "debugger.CurrentMode == dbgDebugMode.dbgBreakMode",
        ):
            self.assertIn(marker, vs_service)

        set_breakpoint = csharp_slice(vs_service, "public BreakpointOperationResult SetBreakpoint", "        public BreakpointOperationResult SetFunctionBreakpoint")
        self.assertIn("ShouldSearchNearbyBindableLine(debugger, breakpointInfo)", set_breakpoint)
        self.assertIn("ProbeNearbyBreakpointsSliced(", set_breakpoint)
        self.assertIn("NearbyProbeIdentity", set_breakpoint)
        self.assertIn("ApplyBreakpointOperationVerification(result, breakpointInfo, debugger, requireBoundInActiveSession: true)", set_breakpoint)
        self.assertIn("breakpoint_binding_verification", function_source(server_py, "debug_capabilities"))

    def test_vs_registration_instructions_include_active_debugging_workflow(self) -> None:
        vs_registration = read("VSDebuggerMCP/McpClientRegistration.cs")

        for keyword in (
            "debug_capabilities",
            "主动断点式定位",
            "attach_process",
            "start_debugging",
            "switch_stack_frame",
            "set_data_breakpoint",
            "当前不暴露 logpoint 工具",
            "debug_continue",
            "debug_break",
            "debug_step_over",
        ):
            self.assertIn(keyword, vs_registration)

    def test_heavy_debug_tools_have_explicit_timeouts(self) -> None:
        server_py = read("mcp_server/vs_server.py")
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")

        self.assertIn("BATCH_EVALUATION_TIMEOUT_SECONDS", server_py)
        self.assertIn("EVALUATION_TIMEOUT_SECONDS", server_py)
        self.assertIn("SAFE_DEBUG_BUNDLE_TIMEOUT_SECONDS", server_py)
        self.assertIn("DEBUG_BUNDLE_TIMEOUT_SECONDS", server_py)
        self.assertIn("THREADS_TIMEOUT_SECONDS", server_py)
        self.assertIn("MODULES_TIMEOUT_SECONDS", server_py)
        self.assertIn("EXCEPTION_TIMEOUT_SECONDS", server_py)
        self.assertIn("MEMORY_TIMEOUT_SECONDS", server_py)
        self.assertIn("DISASSEMBLY_TIMEOUT_SECONDS", server_py)
        self.assertIn("timeout_seconds", server_py)
        self.assertIn("clamp_tool_timeout(timeout_seconds, EVALUATION_TIMEOUT_SECONDS)", server_py)
        self.assertIn("tool_timeout_for_budget(timeout_seconds, THREADS_TIMEOUT_SECONDS, budget_ms)", server_py)
        self.assertIn("tool_timeout_for_budget(timeout_seconds, MODULES_TIMEOUT_SECONDS, budget_ms)", server_py)
        self.assertIn("clamp_tool_timeout(timeout_seconds, EXCEPTION_TIMEOUT_SECONDS)", server_py)
        self.assertIn("clamp_tool_timeout(timeout_seconds, MEMORY_TIMEOUT_SECONDS)", function_source(server_py, "read_memory"))
        search_memory = function_source(server_py, "search_memory")
        self.assertIn("tool_timeout = clamp_tool_timeout(", search_memory)
        self.assertIn("remaining_tool_timeout(deadline, MEMORY_TIMEOUT_SECONDS)", search_memory)
        self.assertIn("clamp_tool_timeout(timeout_seconds, DISASSEMBLY_TIMEOUT_SECONDS)", function_source(server_py, "get_disassembly"))
        self.assertIn("clamp_tool_timeout(timeout_seconds, DISASSEMBLY_TIMEOUT_SECONDS)", function_source(server_py, "get_disassembly_at"))
        self.assertIn("evaluation_policy", server_py)
        self.assertIn("evaluationPolicy", server_py)
        self.assertNotIn("allow_side_effects", server_py)
        self.assertIn("include_locals", server_py)
        self.assertIn("includeLocals", server_py)
        self.assertIn("ExpressionEvaluationTimeoutMs", vs_service)
        self.assertIn("ExpressionBatchBudgetMs", vs_service)
        self.assertIn("ExpressionExpansionBudgetMs", vs_service)
        self.assertIn("ExpressionExpansionMaxNodes", vs_service)
        self.assertIn("LocalsExpansionBudgetMs", vs_service)
        self.assertIn("LocalsExpansionMaxNodes", vs_service)
        self.assertNotIn("allowSideEffects", vs_service)
        self.assertIn("GetExpression(expression, false, ExpressionEvaluationTimeoutMs)", vs_service)
        self.assertIn("budget.IsIncomplete", vs_service)

    def test_bootstrap_requires_manifest_pinned_offline_wheels(self) -> None:
        bootstrap_py = read("mcp_server/bootstrap.py")
        ensure_deps = function_source(bootstrap_py, "ensure_deps")
        dependency_stamp = function_source(bootstrap_py, "_dependency_stamp")

        self.assertIn("_DEPS_ENSURED = False", bootstrap_py)
        self.assertIn("global _DEPS_ENSURED", ensure_deps)
        self.assertNotIn("pip download", bootstrap_py)
        self.assertNotIn("_pip_install_pydantic_core", bootstrap_py)
        self.assertIn("_load_and_validate_manifest(wheels_dir)", ensure_deps)
        self.assertIn("_verify_selected_wheels(selected, manifest)", ensure_deps)
        self.assertIn("hmac.compare_digest", bootstrap_py)
        self.assertIn("_dependency_stamp(selected, wheels_dir)", ensure_deps)
        self.assertIn("_get_target_dir(stamp)", ensure_deps)
        self.assertIn("_target_marker_matches(target_dir, stamp)", ensure_deps)
        self.assertIn("_find_valid_repair_target_dir(target_dir, stamp)", ensure_deps)
        self.assertIn("_allocate_repair_target_dir(target_dir, stamp)", ensure_deps)
        self.assertIn("_write_target_marker(target_dir, stamp)", ensure_deps)
        self.assertNotIn("shutil.rmtree(target_dir)", ensure_deps)
        self.assertIn("_file_sha256(manifest)", dependency_stamp)
        self.assertIn("_file_sha256(wheel)", dependency_stamp)

    def test_bootstrap_reuses_repair_dir_for_partial_target(self) -> None:
        bootstrap = load_bootstrap_module()
        stamp = "deadbeef" * 8

        with tempfile.TemporaryDirectory() as tmp:
            tmp_path = pathlib.Path(tmp)
            target_dir = tmp_path / "cp311_win_amd64_deadbeefdeadbeef"
            target_dir.mkdir()
            stale_repair_dir = tmp_path / f"{target_dir.name}_repair_old"
            stale_repair_dir.mkdir()
            (stale_repair_dir / ".bundled_deps").write_bytes(b"\xff")
            old_time = time.time() - bootstrap.REPAIR_TARGET_STALE_SECONDS - 5
            (stale_repair_dir / ".bundled_deps").touch()
            os.utime(stale_repair_dir / ".bundled_deps", (old_time, old_time))
            os.utime(stale_repair_dir, (old_time, old_time))
            in_progress_repair_dir = tmp_path / f"{target_dir.name}_repair_recent"
            in_progress_repair_dir.mkdir()
            (in_progress_repair_dir / "extracting.tmp").write_text("in progress", encoding="utf-8")
            fake_wheel = tmp_path / "fake-1.0-py3-none-any.whl"
            fake_wheel.write_bytes(b"fake")
            extracted_targets = []
            added_paths = []

            def fake_extract(_wheel, install_dir, _extracted_files, _expected_sha256=None):
                extracted_targets.append(install_dir)
                (install_dir / "installed.txt").write_text("ok", encoding="utf-8")

            bootstrap._select_wheels = lambda _wheels_dir, _manifest: [fake_wheel]
            bootstrap._load_and_validate_manifest = lambda _wheels_dir: {
                "wheels": [{
                    "file": "vendor/wheels/fake-1.0-py3-none-any.whl",
                    "sha256": "0" * 64,
                }]
            }
            bootstrap._verify_selected_wheels = lambda _selected, _manifest: None
            bootstrap._dependency_stamp = lambda _selected, _wheels_dir: stamp
            bootstrap._get_target_dir = lambda _stamp: target_dir
            bootstrap._extract_wheel = fake_extract
            bootstrap._add_sys_path = lambda install_dir, _stamp=None: added_paths.append(install_dir)
            bootstrap._validate_runtime_import_origins = lambda _install_dir: None

            for _ in range(2):
                bootstrap._DEPS_ENSURED = False
                bootstrap.ensure_deps()

            repair_dirs = sorted(
                path for path in tmp_path.iterdir()
                if path.is_dir() and path.name.startswith(f"{target_dir.name}_repair_")
            )
            self.assertFalse(stale_repair_dir.exists())
            self.assertTrue(in_progress_repair_dir.exists())
            self.assertEqual(2, len(repair_dirs), [path.name for path in tmp_path.iterdir()])
            self.assertEqual([repair_dirs[0]], extracted_targets)
            self.assertEqual([repair_dirs[0], repair_dirs[0]], added_paths)
            marker = json.loads((repair_dirs[0] / ".bundled_deps").read_text(encoding="utf-8"))
            self.assertEqual(stamp, marker["stamp"])
            self.assertIn("installed.txt", marker["files"])

            (repair_dirs[0] / "installed.txt").write_text("corrupt", encoding="utf-8")
            self.assertFalse(bootstrap._target_marker_matches(repair_dirs[0], stamp))

    def test_thread_enumeration_is_budgeted(self) -> None:
        server_py = read("mcp_server/vs_server.py")
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")
        vs_pipe = read("VSDebuggerMCP/NamedPipeServer.cs")
        vs_models = read("VSDebuggerMCP/Protocol/DebugModels.cs")

        self.assertIn("timeout_seconds: Optional[float] = None", function_source(server_py, "get_threads"))
        self.assertIn("max_frames_per_thread: Optional[int] = None", function_source(server_py, "get_threads"))
        self.assertIn("max_nodes: Optional[int] = None", function_source(server_py, "get_threads"))
        self.assertIn("budget_ms: Optional[int] = None", function_source(server_py, "get_threads"))
        self.assertIn('"maxFramesPerThread"', server_py)
        self.assertIn('"maxNodes"', server_py)
        self.assertIn('"budgetMs"', server_py)
        self.assertIn("tool_timeout_for_budget(timeout_seconds, THREADS_TIMEOUT_SECONDS, budget_ms)", server_py)
        self.assertIn("MAX_THREAD_BUDGET_SECONDS = 115.0", server_py)
        self.assertIn("elif float(budget_ms) == 0.0", server_py)
        self.assertIn('GetParam<int>(request.Params, "maxFramesPerThread", -1)', vs_pipe)
        self.assertIn("ThreadEnumerationBudgetMs", vs_service)
        self.assertIn("ThreadFramesMaxPerThread", vs_service)
        self.assertIn("NormalizeThreadFrameLimit", vs_service)
        self.assertIn("NormalizeThreadNodeLimit", vs_service)
        self.assertIn("NormalizeThreadBudgetMs", vs_service)
        self.assertIn("maxFramesPerThread == 0", vs_service)
        self.assertIn("maxNodes == 0", vs_service)
        self.assertIn("return ThreadFramesHardMaxPerThread", vs_service)
        self.assertIn("return ThreadFrameExpansionHardMaxNodes", vs_service)
        self.assertNotIn("return int.MaxValue", vs_service)
        self.assertIn("budgetMs < 0", vs_service)
        self.assertIn("budgetMs == 0", vs_service)
        self.assertIn("return DebugBundleMainThreadTimeoutMs - 5000", vs_service)
        self.assertIn("sliced_thread_snapshot", vs_service)
        self.assertIn("CaptureThreadFrame(", vs_service)
        self.assertIn("HeavyMainThreadTimeoutMs", vs_service)
        self.assertIn("AddPartialReason(partialReasons", vs_service)
        self.assertIn("DetermineThreadsSnapshotStatus", vs_service)

        self.assertIn("partial", vs_models)
        self.assertIn("error", vs_models)

    def test_thread_budget_flows_through_bundle_and_full_context(self) -> None:
        server_py = read("mcp_server/vs_server.py")
        vs_pipe = read("VSDebuggerMCP/NamedPipeServer.cs")
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")

        for body_name in ("get_full_context", "capture_debug_bundle", "continue_and_wait_for_break"):
            body = function_source(server_py, body_name)
            self.assertIn("thread_include_frames", body)
            self.assertIn("thread_max_frames_per_thread", body)
            self.assertIn("thread_max_nodes", body)
            self.assertIn("thread_budget_ms", body)

        self.assertIn("add_thread_capture_params", server_py)
        self.assertIn("tool_timeout_for_budget(timeout_seconds, default_timeout, thread_budget_ms)", server_py)
        self.assertIn('timeout=float(timeout_seconds + tool_timeout_for_budget(None, 15.0, thread_budget_ms))', server_py)

        for field in (
            '"threadIncludeFrames"',
            '"threadMaxFramesPerThread"',
            '"threadMaxNodes"',
            '"threadBudgetMs"',
        ):
            self.assertIn(field, vs_pipe)

        self.assertIn("public FullContextResult GetFullContext(", vs_service)
        self.assertIn("public DebugBundleResult CaptureDebugBundle(", vs_service)
        self.assertIn("public ContinueAndWaitResult ContinueAndWaitForBreak(", vs_service)
        self.assertIn("GetThreads(", vs_service)
        self.assertIn("threadMaxFramesPerThread", vs_service)
        self.assertIn("threadMaxNodes", vs_service)
        self.assertIn("threadBudgetMs", vs_service)

    def test_composite_tools_use_end_to_end_timeout_budget(self) -> None:
        server_py = read("mcp_server/vs_server.py")
        crash_triage = function_source(server_py, "crash_triage")
        step_and_capture = function_source(server_py, "step_and_capture")

        self.assertIn("remaining_tool_timeout", server_py)
        self.assertNotIn("return max(1.0, min(default_seconds, remaining))", server_py)
        self.assertIn("await asyncio.sleep(min(0.5, remaining))", server_py)
        self.assertIn("timeout=poll_timeout", server_py)
        self.assertNotIn("remaining_timeout = max(1.0, deadline", server_py)
        self.assertIn("deadline = asyncio.get_running_loop().time() + tool_timeout", crash_triage)
        self.assertIn("remaining_tool_timeout(deadline, DISASSEMBLY_TIMEOUT_SECONDS)", crash_triage)
        self.assertIn("remaining_tool_timeout(deadline, MEMORY_TIMEOUT_SECONDS)", crash_triage)
        self.assertIn("timeout budget was exhausted", crash_triage)
        self.assertIn("deadline = asyncio.get_running_loop().time() + tool_timeout", step_and_capture)
        self.assertIn("step_timeout = remaining_tool_timeout(deadline, tool_timeout)", step_and_capture)
        self.assertIn("bundle_timeout = remaining_tool_timeout(deadline, tool_timeout)", step_and_capture)
        self.assertIn("bundleError", step_and_capture)

    def test_empty_thread_snapshot_is_not_reported_ok(self) -> None:
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")

        self.assertIn('return threads.Threads.Count > 0 ? "ok" : "unavailable";', vs_service)
        self.assertIn('result.Threads.Count > 0 ? "best_effort" : "unavailable"', vs_service)
        self.assertIn("bundle.Threads = RetainBundleComponent(", vs_service)
        self.assertIn('new ThreadSnapshotHeader { Error = "Current debug program unavailable" }', vs_service)

    def test_vs_locals_uses_explicit_non_ui_frame_context(self) -> None:
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")
        vs_pipe = read("VSDebuggerMCP/NamedPipeServer.cs")
        models = read("VSDebuggerMCP/Protocol/DebugModels.cs")

        self.assertNotIn("private int _selectedStackFrameIndex;", vs_service)
        self.assertIn("FindThreadIndexByIdSliced(threadId, threadLookupBudgetMs)", vs_service)
        self.assertIn("ResolveThread(debugger, threadId, selectedThreadIndex)", vs_service)
        self.assertIn("thread.ID == threadId ? thread : null", vs_service)
        self.assertIn("ResolveFrame(selectedThread, frameIndex)", vs_service)
        self.assertIn("selectedFrame.Locals", vs_service)
        self.assertIn("selectedFrame.Arguments", vs_service)
        self.assertIn("StaleDebugContextException", vs_service)
        self.assertIn('"STALE_DEBUG_CONTEXT"', vs_pipe)
        self.assertIn("class DebugFrameContext", models)
        self.assertIn('DataMember(Name = "frameId")', models)

    def test_vs_com_collections_are_sliced_paginated_and_truthful(self) -> None:
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")
        router = read("VSDebuggerMCP/Ipc/NamedPipeMethodRouter.cs")
        schema = read("VSDebuggerMCP/Ipc/DebugRequestSchemaValidator.cs")
        catalog = read("VSDebuggerMCP/Protocol/DebugMethodCatalog.cs")
        server_py = read("mcp_server/vs_server.py")
        models = read("VSDebuggerMCP/Protocol/DebugModels.cs")

        self.assertNotIn("foreach (EnvDTE.Breakpoint", vs_service)
        self.assertNotIn("foreach (EnvDTE.Process", vs_service)
        self.assertNotIn("foreach (EnvDTE.Thread", vs_service)
        self.assertNotIn("_buildErrorService.Get(", vs_service)
        for marker in (
            "CaptureModuleFrame(",
            "_buildErrorService.GetItem(",
            "CaptureBreakpointAtIndex(",
            "CaptureLocalProcessIdentity(",
            "CaptureThreadIdentity(",
            "DeleteFirstBreakpoint(",
        ):
            self.assertIn(marker, vs_service)

        for field in (
            'DataMember(Name = "startIndex")',
            'DataMember(Name = "nextIndex")',
            'DataMember(Name = "hasMore")',
            'DataMember(Name = "nextThreadIndex")',
            'DataMember(Name = "nextFrameIndex")',
            'DataMember(Name = "hasMoreFrames")',
        ):
            self.assertIn(field, models)

        self.assertIn("startIndex maxNodes budgetMs", catalog)
        self.assertIn("startThreadIndex startFrameIndex", catalog)
        self.assertIn('case "startIndex"', schema)
        self.assertIn('case "startThreadIndex"', schema)
        self.assertNotIn("ValidateIntegerRange", router)
        self.assertIn("add_bounded_collection_params", server_py)
        self.assertIn('result.SnapshotStatus = countStable ? "best_effort" : "unstable";', vs_service)
        self.assertIn('result.SnapshotStatus = countAndContextStable ? "best_effort" : "unstable";', vs_service)
        self.assertIn("scanContinuationToken", catalog)
        self.assertIn('DataMember(Name = "scanContinuationToken"', models)
        self.assertIn(
            "The debugger context changed while the fallback module snapshot was captured.",
            vs_service,
        )
        self.assertIn("continue with scanContinuationToken", vs_service)

    def test_partial_side_effect_commands_keep_structured_outcomes(self) -> None:
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")
        policy = read("VSDebuggerMCP/Ipc/OperationResultPolicy.cs")
        models = read("VSDebuggerMCP/Protocol/DebugModels.cs")

        self.assertIn("process.ProcessStarted || process.CommandIssued", policy)
        self.assertIn("breakpoint.CommandIssued", policy)
        self.assertIn('DataMember(Name = "processStarted")', models)
        self.assertIn('DataMember(Name = "commandIssued")', models)
        self.assertIn('DataMember(Name = "affectedCount")', models)
        self.assertIn('DataMember(Name = "remainingCount")', models)
        self.assertIn("process_started_attach_not_completed", vs_service)
        self.assertIn("delete_all_outcome_unknown", vs_service)

    def test_vs_stack_frames_do_not_fabricate_source_from_editor_state(self) -> None:
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")

        self.assertNotIn("TryGetActiveSourceDocumentPath", vs_service)
        self.assertNotIn("TryGetActiveSourceLineNumber", vs_service)
        self.assertNotIn("IsCurrentOrSelectedStackFrame", vs_service)
        self.assertNotIn("FindSourceFileByClassName", vs_service)
        self.assertIn("IsSyntheticExecutionPointFrame", vs_service)
        self.assertIn("CaptureThreadFrame(", vs_service)
        self.assertIn("_sourceLocationAdapter.GetLocation(frame)", vs_service)
        self.assertIn("!IsSyntheticExecutionPointName(topFrame.FunctionName)", vs_service)
        self.assertIn('StartsWith("[Execution point when dump was captured"', vs_service)
        self.assertNotIn('GetExpression("__FILE__")', vs_service)
        self.assertNotIn('GetExpression("__LINE__")', vs_service)
        frame_source = vs_service[
            vs_service.index("private string GetFrameFileName"):
            vs_service.index("private (int LineNumber, bool IsReliable) GetFrameLineNumberEx")
        ]
        self.assertIn("return _sourceLocationAdapter.GetLocation(frame).FilePath;", frame_source)
        line_source = vs_service[
            vs_service.index("private (int LineNumber, bool IsReliable) GetFrameLineNumberEx"):
            vs_service.index("private int GetFrameLineNumber")
        ]
        self.assertIn("VisualStudioSourceLocation location = _sourceLocationAdapter.GetLocation(frame);", line_source)
        self.assertIn("return (location.LineNumber ?? 0, location.IsReliable);", line_source)
        self.assertNotIn("ActiveDocument", frame_source + line_source)

    def test_vs_dump_modules_use_minidump_module_list_fallback(self) -> None:
        vs_service = "\n".join([
            read("VSDebuggerMCP/DebuggerService.cs"),
            read("VSDebuggerMCP/Services/DebuggerService.Inspection.cs"),
            read("VSDebuggerMCP/Services/DebuggerService.EvaluationMemory.cs"),
        ])

        self.assertIn("GetModulesFromMinidump", vs_service)
        self.assertIn("GetActiveDumpFilePathCandidate", vs_service)
        self.assertIn("NormalizeExistingDumpPath", vs_service)
        self.assertIn("_activeDumpFilePath", vs_service)
        self.assertIn("_activeDumpFilePath = _pendingDumpFilePath", vs_service)
        self.assertIn("_activeDumpSessionGeneration = session.Generation", vs_service)
        self.assertIn("IsDumpFilePath(_activeDumpFilePath)", vs_service)
        self.assertIn("ModuleListStream", vs_service)
        self.assertIn("0x504D444D", vs_service)
        self.assertIn("ReadMinidumpCodeViewPdbPath", vs_service)
        self.assertIn("declaredModuleCount", vs_service)
        self.assertIn("maxModulesBySize", vs_service)
        self.assertIn("bounded enumeration cap of 4096", vs_service)
        self.assertIn('Address = $"0x{baseOfImage:X16}"', vs_service)
        self.assertIn("SymbolPath = ReadMinidumpCodeViewPdbPath", vs_service)

    def test_start_debugging_uses_control_timeout(self) -> None:
        server_py = read("mcp_server/vs_server.py")
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")

        start_debugging = function_source(server_py, "start_debugging")
        self.assertIn("START_DEBUGGING_TIMEOUT_SECONDS = 60.0", server_py)
        self.assertIn("timeout_seconds: Optional[float] = None", start_debugging)
        self.assertIn("clamp_tool_timeout(timeout_seconds, START_DEBUGGING_TIMEOUT_SECONDS)", start_debugging)
        self.assertIn("deadline = asyncio.get_running_loop().time() + wait_timeout", start_debugging)
        self.assertIn("wait_for_debugging_to_start(", start_debugging)
        self.assertIn("target=instance_target", start_debugging)
        self.assertIn("wait_for_pause = executable_path.lower().endswith", server_py)
        self.assertIn("Started dump debugging and paused", server_py)

        vs_start = vs_service[vs_service.index("public ProcessDebugResult StartDebugging"):]
        vs_start = vs_start[:vs_start.index("private LocalProcessLookup FindLocalProcessSliced")]
        self.assertIn("}, HeavyMainThreadTimeoutMs);", vs_start)

    def test_start_debugging_handles_active_dump_document(self) -> None:
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")
        vs_project = read("VSDebuggerMCP/VSDebuggerMCP.csproj")

        vs_start = vs_service[vs_service.index("public ProcessDebugResult StartDebugging"):]
        vs_start = vs_start[:vs_start.index("private LocalProcessLookup FindLocalProcessSliced")]
        self.assertIn("TryStartActiveDumpDebugging(debugger, result)", vs_start)
        self.assertIn("normalizedDumpPath", vs_start)
        self.assertIn("StartDumpDebuggingFromPath(debugger, result, normalizedDumpPath)", vs_start)
        self.assertIn("StartDumpDebuggingFromPath", vs_start)
        self.assertIn("OpenAndVerifyTargetDocument(dumpPath)", vs_start)
        self.assertIn("out bool outcomeUnknown", vs_start)
        self.assertIn("if (!outcomeUnknown) ClearDumpSessionState()", vs_start)
        self.assertIn("ClearDumpSessionState()", vs_start)
        self.assertIn("Requested Visual Studio to start debugging the dump file", vs_start)
        self.assertIn("VisualStudioDebuggerCommandSet", vs_service)
        self.assertIn("DebugDumpFileCommandId", vs_service)
        self.assertIn("PostExecCommand", vs_start)
        self.assertIn("official_vs_dump_command", vs_start)
        self.assertIn("debug_engine_selection_is_controlled_by_visual_studio", vs_start)
        self.assertNotIn("TryInvokeVisualStudioControl", vs_start)
        self.assertNotIn("AutomationElement", vs_start)
        self.assertIn("BeginPendingDumpSession(activeDocumentPath)", vs_start)
        self.assertNotIn("System.IO.File.Exists(activeDocumentPath)", vs_start)
        self.assertIn('string.Equals(extension, ".dmp"', vs_start)
        self.assertNotIn('<Reference Include="UIAutomationClient" />', vs_project)
        self.assertNotIn('<Reference Include="UIAutomationTypes" />', vs_project)

    def test_combined_and_rider_surfaces_no_longer_exist(self) -> None:
        for relative in (
            "mcp_server/ide_bridge.py",
            "mcp_server/server.py",
            "mcp_server/mcp_app.py",
            "mcp_server/tool_registry.py",
            "mcp_server/rider_bridge.py",
            "mcp_server/rider_trust.py",
            "mcp_server/rider_errors.py",
            "mcp_server/tool_modules/instance_tools.py",
            "RiderBuild.csproj",
            "RiderBuildDummy.cs",
            "docs/DeepReview-VS-Rider-Alignment.md",
        ):
            with self.subTest(relative=relative):
                self.assertFalse((ROOT / relative).exists(), relative)
        self.assertFalse((ROOT / "RiderDebuggerMCP").exists())

    def test_vs_bridge_preserves_stale_immutable_selection(self) -> None:
        vs_bridge_py = read("mcp_server/vs_bridge.py")
        multi_vs_tools = read("mcp_server/tool_modules/multi_vs_tools.py")

        self.assertIn("_bind_registration_snapshot(", vs_bridge_py)
        self.assertIn('code="STALE_TARGET"', vs_bridge_py)
        self.assertIn("async def _advance_selected_target(", vs_bridge_py)
        self.assertIn("if response_epoch >= current_epoch", vs_bridge_py)
        self.assertIn("error.original_target_identity = dict(target)", vs_bridge_py)
        self.assertIn("error.current_target_identity = dict(response_target)", vs_bridge_py)
        self.assertIn("advance_selection: bool = True", vs_bridge_py)
        # Multi-target dispatch must never redirect the pinned single-instance target.
        self.assertIn("advance_selection=False", multi_vs_tools)

    def test_bridge_does_not_hold_selection_lock_across_calls(self) -> None:
        vs_bridge_py = read("mcp_server/vs_bridge.py")
        vs_session_py = read("mcp_server/vs_session.py")

        self.assertIn("pipe_name = self._selected_pipe", vs_bridge_py)
        self.assertIn("selected_target = dict(self._selected_target)", vs_bridge_py)
        self.assertIn("selected_registration = (", vs_bridge_py)
        self.assertIn("registration_snapshot=selected_registration", vs_bridge_py)
        self.assertIn("async def call_pipe", vs_bridge_py)
        self.assertIn("async def call_pipe_exact", vs_bridge_py)
        self.assertIn("loop.run_in_executor", vs_bridge_py)
        self.assertIn("async with self._lock:", vs_bridge_py)
        self.assertIn("self._cached_instances = instances", vs_bridge_py)
        self.assertIn("selected_instance = self._find_best_instance(instances)", vs_bridge_py)
        self.assertIn("selected_instance.registration_snapshot()", vs_bridge_py)

        # The VS session manager is a thin delegation layer and owns no lock of its own.
        self.assertIn("return await self._bridge.call(", vs_session_py)
        self.assertIn("return await get_bridge().call(", vs_session_py)
        self.assertNotIn("self._lock", vs_session_py)

    def test_unreachable_pipes_are_diagnostic_only(self) -> None:
        vs_bridge_py = read("mcp_server/vs_bridge.py")

        self.assertIn('self.connection_status = "ok"', vs_bridge_py)
        self.assertIn('"connectionStatus": "unreachable"', vs_bridge_py)
        self.assertIn('instances = [i for i in instances if i.connection_status == "ok"]', vs_bridge_py)
        self.assertIn('return any(i.connection_status == "ok" for i in instances)', vs_bridge_py)
        self.assertIn('"lastError": str(e)', vs_bridge_py)
        self.assertIn("Multiple VS instances found or no reachable instance could be auto-selected", vs_bridge_py)
        self.assertIn("Only registrations that passed owner/process validation are visible", vs_bridge_py)

    def test_list_and_detect_preserve_explicit_selection_truth(self) -> None:
        server_py = read("mcp_server/vs_server.py")

        list_vs = function_source(server_py, "list_vs_instances")
        detect = function_source(server_py, "detect_debug_sessions")

        self.assertIn('"selected": selected is not None', list_vs)
        self.assertIn('"selectedInstance": selected', list_vs)
        self.assertIn("hasPinnedSelection", list_vs)
        self.assertIn('"unreachableCount": len(instances) - reachable_count', list_vs)
        self.assertNotIn("wouldAutoSelect", list_vs)
        self.assertNotIn("autoSelected", list_vs)

        self.assertIn("autoSelected", detect)
        self.assertIn("wouldAutoSelectInstance", detect)
        self.assertIn("if pinned is not None or not instances:", detect)
        self.assertIn("the newer selection was preserved", detect)
        self.assertIn("select one explicitly", detect)

    def test_preferred_ide_discovery_is_scoped(self) -> None:
        server_py = read("mcp_server/vs_server.py")
        vs_session_py = read("mcp_server/vs_session.py")

        self.assertIn("async def discover_instances(\n        self,\n        preferred_ide: Optional[str] = None,\n        timeout: Optional[float] = None", vs_session_py)
        self.assertIn("preferred_ide: Optional[str] = None", vs_session_py)
        self.assertIn("This MCP server supports Visual Studio only.", vs_session_py)
        self.assertIn('normalized not in ("vs", "visualstudio", "visual-studio", "visual_studio")', vs_session_py)
        self.assertIn("async def discover_instances(\n    preferred_ide: Optional[str] = None,\n    timeout: Optional[float] = None", vs_session_py)
        self.assertIn('return "vs"', vs_session_py)
        self.assertIn("discover_instances()", function_source(server_py, "list_vs_instances"))
        self.assertIn("discover_instances()", function_source(server_py, "select_vs_instance"))
        self.assertIn("discover_instances()", function_source(server_py, "detect_debug_sessions"))
        self.assertIn("preferred_ide=bridge.get_preferred_ide(),", function_source(server_py, "debug_capabilities"))

    def test_mcp_tools_are_split_into_domain_modules(self) -> None:
        server_entry = (ROOT / "mcp_server/vs_server.py").read_text(encoding="utf-8")
        module_files = sorted(
            path.stem
            for path in (ROOT / "mcp_server/tool_modules").glob("*.py")
            if path.name != "__init__.py"
        )

        self.assertIn("from .tool_modules import register_vs", server_entry)
        self.assertIn("from tool_modules import register_vs", server_entry)
        self.assertIn("register_vs()", server_entry)
        self.assertNotIn("@mcp.tool", server_entry)
        self.assertNotIn("async def get_debug_status", server_entry)
        self.assertEqual(module_files, sorted(registered_tool_module_names()))

        for source_path in (
            "mcp_server/vs_mcp_app.py",
            "mcp_server/tool_context.py",
            "mcp_server/vs_tool_registry.py",
            "mcp_server/tool_modules/capability_tools.py",
            "mcp_server/tool_modules/vs_instance_tools.py",
            "mcp_server/tool_modules/multi_vs_tools.py",
            "mcp_server/tool_modules/snapshot_tools.py",
            "mcp_server/tool_modules/native_managed_tools.py",
            "mcp_server/tool_modules/control_tools.py",
            "mcp_server/tool_modules/breakpoint_tools.py",
            "mcp_server/tool_modules/memory_tools.py",
        ):
            self.assertTrue((ROOT / source_path).is_file(), source_path)

        registry = read("mcp_server/vs_tool_registry.py")
        self.assertIn("VS_TOOL_DOMAINS", registry)
        self.assertIn('"snapshot": [', registry)
        self.assertIn('"breakpoint": [', registry)
        self.assertIn('"memory": [', registry)
        tool_modules_init = read("mcp_server/tool_modules/__init__.py")
        self.assertIn("def register_vs()", tool_modules_init)
        self.assertIn("from . import capability_tools", tool_modules_init)
        tool_context = read("mcp_server/tool_context.py")
        self.assertIn("__all__ = [", tool_context)
        self.assertIn('"format_result"', tool_context)
        self.assertIn('"handle_ide_error"', tool_context)

    def test_split_server_files_are_packaged_for_vsix(self) -> None:
        csproj_items = vsix_content_items()
        all_runtime_files = expected_packaged_mcp_files()
        vs_root_files = {
            "vs_server.py", "vs_launcher.py", "vs_bridge.py", "vs_errors.py", "vs_session.py", "vs_mcp_app.py",
            "output_models.py", "tool_context.py", "tool_runtime.py", "vs_tool_registry.py", "vs_trust.py", "bootstrap.py",
            "debug_method_manifest.json", "requirements.txt",
        }
        vs_tool_modules = {
            "tool_modules/__init__.py", "tool_modules/capability_tools.py",
            "tool_modules/vs_instance_tools.py", "tool_modules/multi_vs_tools.py",
            "tool_modules/snapshot_tools.py",
            "tool_modules/native_managed_tools.py", "tool_modules/control_tools.py",
            "tool_modules/breakpoint_tools.py", "tool_modules/memory_tools.py",
        }
        expected_vs_files = sorted(
            path for path in all_runtime_files
            if path in vs_tool_modules or path in vs_root_files
        )
        module_include = next(key for key in csproj_items if ";" in key and "tool_modules" in key)
        module_sources = set(module_include.split(";"))
        self.assertEqual(
            {r"..\mcp_server" + "\\" + path.replace("/", "\\") for path in vs_tool_modules},
            module_sources,
        )
        self.assertEqual("true", csproj_items[module_include].get("IncludeInVSIX"))
        self.assertEqual("PreserveNewest", csproj_items[module_include].get("CopyToOutputDirectory"))
        self.assertEqual(r"mcp_server\tool_modules\%(Filename)%(Extension)", csproj_items[module_include].get("Link"))

        for relative_path in expected_vs_files:
            with self.subTest(target="vsix", file=relative_path):
                if relative_path.startswith("tool_modules/") and relative_path.count("/") == 1:
                    self.assertIn(r"..\mcp_server" + "\\" + relative_path.replace("/", "\\"), module_sources)
                else:
                    include_path = r"..\mcp_server" + "\\" + relative_path.replace("/", "\\")
                    link_path = "mcp_server\\" + relative_path.replace("/", "\\")
                    self.assertIn(include_path, csproj_items)
                    self.assertEqual(link_path, csproj_items[include_path].get("Link"))
                    self.assertEqual("true", csproj_items[include_path].get("IncludeInVSIX"))
                    self.assertEqual("PreserveNewest", csproj_items[include_path].get("CopyToOutputDirectory"))

        self.assertNotIn(r"..\mcp_server\rider_bridge.py", csproj_items)
        self.assertNotIn(r"..\mcp_server\server.py", csproj_items)
        self.assertNotIn(r"..\mcp_server\ide_bridge.py", csproj_items)

    def test_split_server_supports_script_and_package_import_modes(self) -> None:
        server_entry = (ROOT / "mcp_server/vs_server.py").read_text(encoding="utf-8")
        mcp_app_py = read("mcp_server/vs_mcp_app.py")
        vs_session_py = read("mcp_server/vs_session.py")
        tool_modules_init = read("mcp_server/tool_modules/__init__.py")

        self.assertIn("from . import bootstrap", server_entry)
        self.assertIn("import bootstrap", server_entry)
        self.assertIn("from .vs_mcp_app import mcp", server_entry)
        self.assertIn("from vs_mcp_app import mcp", server_entry)
        self.assertIn("from .tool_modules import register_vs", server_entry)
        self.assertIn("from tool_modules import register_vs", server_entry)

        self.assertIn("from . import vs_bridge", vs_session_py)
        self.assertIn("import vs_bridge", vs_session_py)

        self.assertIn("bootstrap.ensure_deps()", mcp_app_py)
        self.assertIn("from . import capability_tools", tool_modules_init)

    def test_split_server_import_modes_smoke(self) -> None:
        commands = [
            (
                "script-style",
                "import sys; sys.path.insert(0, r'mcp_server'); import vs_server; print('script-style import ok')",
            ),
            (
                "package",
                "import runpy; runpy.run_module('mcp_server.vs_server', run_name='aidb_package_smoke'); print('package import ok')",
            ),
            (
                "package-register-vs",
                "from mcp_server.tool_modules import register_vs; register_vs(); print('package register_vs import ok')",
            ),
            (
                "script-style-register-vs",
                "import sys; sys.path.insert(0, r'mcp_server'); from tool_modules import register_vs; register_vs(); print('script-style register_vs import ok')",
            ),
        ]
        for mode, script in commands:
            with self.subTest(mode=mode):
                result = subprocess.run(
                    [sys.executable, "-c", script],
                    cwd=ROOT,
                    capture_output=True,
                    text=True,
                    timeout=20,
                )
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertIn("import ok", result.stdout)

    def test_github_workflow_does_not_write_actions_cache(self) -> None:
        workflow = read(".github/workflows/build.yml")

        self.assertNotIn("actions/cache", workflow)
        self.assertNotIn("Cache NuGet", workflow)
        self.assertNotIn("cache-read-only", workflow)
        # The Gradle/Rider build is gone; no step may reintroduce action caching.
        self.assertNotIn("gradle", workflow)

    def test_vs_tool_registry_matches_backend_matrix(self) -> None:
        registry = load_tool_registry()

        method_manifest = json.loads(read("mcp_server/debug_method_manifest.json"))
        self.assertEqual("aidb/2", method_manifest["protocol"])
        vs_methods = set(method_manifest["methods"])
        tool_names = discover_mcp_tool_names()

        # The whole backend catalogue is Visual Studio only.
        self.assertEqual(vs_methods, set(registry.DEBUG_METHOD_MANIFEST))
        self.assertEqual(["vs"], registry.tool_registry_summary()["methodBackends"]["attach_process"])

        # Legacy aliases resolve to their canonical Visual Studio backend method.
        self.assertEqual("delete_breakpoint", registry.LEGACY_ALIASES["clear_breakpoint"])
        self.assertEqual("set_data_breakpoint", registry.LEGACY_ALIASES["set_watchpoint"])
        self.assertEqual(("delete_breakpoint",), registry.backend_methods_for_tool("clear_breakpoint"))
        self.assertEqual(("set_data_breakpoint",), registry.backend_methods_for_tool("set_watchpoint"))
        self.assertEqual(("attach_process",), registry.backend_methods_for_tool("attach_process"))

        # Every discovered MCP tool belongs to exactly one declared VS domain.
        domain_tools = {tool for tools in registry.VS_TOOL_DOMAINS.values() for tool in tools}
        self.assertEqual(set(), set(tool_names) - domain_tools)

        summary = registry.tool_registry_summary()
        self.assertEqual("visual_studio", summary["scope"])
        self.assertIn("set_data_breakpoint", summary["vsOnlyTools"])
        self.assertIn("detach_process", summary["vsOnlyTools"])
        self.assertIn("switch_process", summary["vsOnlyTools"])
        # Legacy aliases are hidden unless explicitly enabled; the canonical name
        # is what the catalog advertises in the default configuration.
        self.assertEqual(
            os.environ.get("AIDB_ENABLE_LEGACY_ALIASES") == "1",
            summary["legacyAliasesEnabled"],
        )
        if not summary["legacyAliasesEnabled"]:
            self.assertEqual({}, summary["aliases"])
            self.assertIn("set_data_breakpoint", summary["vsOnlyTools"])
            self.assertNotIn("set_watchpoint", summary["vsOnlyTools"])
        for tool, support in summary["toolSupport"].items():
            with self.subTest(tool=tool):
                self.assertEqual(["vs"], support["supportedBackends"])
                self.assertEqual(["vs"], support["routableBackends"])
                self.assertEqual([], support["unsupportedBackends"])
                self.assertEqual("vs", support["preferredIde"])
        self.assertEqual(["vs"], summary["toolSupport"]["set_data_breakpoint"]["supportedBackends"])
        self.assertEqual(["vs"], summary["toolSupport"]["get_debug_processes"]["supportedBackends"])
        self.assertEqual(["vs"], summary["toolSupport"]["switch_process"]["routableBackends"])

        from mcp_server.tool_modules import capability_tools

        categories = capability_tools._categories_with_registry_coverage(summary)
        categorized_tools = {tool for tools in categories.values() for tool in tools}
        self.assertEqual(set(summary["toolSupport"]), categorized_tools)

    def test_vs_only_active_debugging_tools_pin_vs_backend(self) -> None:
        server_py = read("mcp_server/vs_server.py")

        for tool_name in (
            "attach_process", "get_debug_processes", "switch_process",
            "start_debugging", "detach_process",
        ):
            with self.subTest(tool_name=tool_name):
                self.assertIn('preferred_ide="vs"', function_source(server_py, tool_name))

        for alias_name, canonical_name in (
            ("list_debug_processes", "get_debug_processes"),
            ("select_process", "switch_process"),
        ):
            alias = function_source(server_py, alias_name)
            self.assertIn(f"return await {canonical_name}(", alias)
            self.assertNotIn("ide_bridge.call", alias)

        clear_breakpoint = function_source(server_py, "clear_breakpoint")
        self.assertIn("return await delete_breakpoint(file_path, line_number)", clear_breakpoint)
        self.assertNotIn("ide_bridge.call", clear_breakpoint)

    def test_vs_memory_search_contract_matches_backend_limit(self) -> None:
        server_py = read("mcp_server/vs_server.py")
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")

        search_memory = function_source(server_py, "search_memory")
        vs_execute_search = csharp_slice(vs_service, "public MemorySearchResult SearchMemory(", "        private MemorySearchResult CreateUnavailableMemorySearchResult")
        vs_parse_pattern = read("VSDebuggerMCP/Ipc/SearchPatternParser.cs")
        vs_pipe = read("VSDebuggerMCP/NamedPipeServer.cs")
        vs_schema = read("VSDebuggerMCP/Ipc/DebugRequestSchemaValidator.cs")
        self.assertIn("MAX_MEMORY_SEARCH_CHUNK_SIZE = 0x100000", server_py)
        self.assertIn("MAX_MEMORY_SEARCH_SIZE = 0x40000000", server_py)
        self.assertIn("MAX_MEMORY_SEARCH_CHUNK_SIZE", search_memory)
        self.assertIn("chunked", search_memory)
        self.assertIn("nextStartExpression", search_memory)
        self.assertIn("nextStartOffset", search_memory)
        self.assertIn("remainingBytes", search_memory)
        self.assertIn('start_offset: int = 0', search_memory)
        self.assertIn('"startOffset": start_offset', search_memory)
        self.assertIn('"startOffset": absolute_offset', search_memory)
        self.assertIn("start_offset + search_size exceeds the backend Int64 offset range", search_memory)
        self.assertIn("chunk_start = start_address if absolute_offset == 0 else", search_memory)
        self.assertIn("next_offset = max(offset + 1, covered_until - overlap)", search_memory)
        self.assertIn("newly_covered", search_memory)
        self.assertIn('backend_next_offset = chunk_result.get("nextStartOffset")', search_memory)
        self.assertIn('continuation_expression = start_address', search_memory)
        self.assertIn('"INVALID_BACKEND_CONTINUATION"', search_memory)
        self.assertIn('"startAddress": start_address', search_memory)
        self.assertIn("Search stopped after reaching max_results", search_memory)
        self.assertNotIn("10 * 1024 * 1024", search_memory)
        self.assertIn("Search active memory without choosing a dump-specific tool", search_memory)
        self.assertIn('continuation: Optional[str] = None', search_memory)
        self.assertIn('params["continuation"] = continuation', search_memory)
        self.assertIn('preferred_ide="vs"', search_memory)
        self.assertNotIn("search_dump_memory", server_py)
        self.assertIn('pattern_format: str = "auto"', search_memory)
        self.assertIn('"patternFormat": pattern_format', search_memory)
        self.assertIn('string patternFormat = GetParam<string>(request.Params, "patternFormat", "auto")', vs_pipe)
        self.assertIn('long startOffset = GetParam<long>(request.Params, "startOffset", 0)', vs_pipe)
        self.assertIn("Start offset plus search range exceeds the supported protocol range", vs_execute_search)
        self.assertIn("if (value is long longValue)", vs_pipe)
        self.assertIn("long converted = intValue", vs_pipe)
        self.assertIn('"startOffset"', vs_schema)
        self.assertIn("params.{key} must be non-negative", vs_schema)
        self.assertNotIn("ValidateLongRange", vs_pipe)
        self.assertIn("ParameterValidationException", vs_pipe)
        self.assertIn("must not be empty", vs_parse_pattern)
        self.assertIn("string.IsNullOrWhiteSpace(pattern)", vs_parse_pattern)
        self.assertIn('case "hex"', vs_parse_pattern)
        self.assertIn("LooksLikeSpacedHex", vs_parse_pattern)
        self.assertIn("Encoding.UTF8.GetBytes(pattern)", vs_parse_pattern)
        self.assertIn("contains non-ASCII characters", vs_parse_pattern)
        self.assertIn("startOffset must be non-negative", vs_execute_search)
        self.assertIn("ulong offsetValue = (ulong)startOffset", vs_execute_search)
        self.assertIn("baseAddress += offsetValue", vs_execute_search)
        self.assertIn("CaptureMemoryReadHeader()", vs_execute_search)
        self.assertIn("SearchMappedDumpMemory", vs_execute_search)
        self.assertIn("ReadMemoryExpressionPrefix", vs_execute_search)
        self.assertIn("OpenVerifiedCurrentLiveProcessHandle", vs_service)
        self.assertIn("HasSameComIdentity", vs_service)
        self.assertIn("ExpressionWordsPerUiSlice", vs_service)
        self.assertNotIn("SearchDumpMemory", vs_service)
        self.assertNotIn("pattern.Trim().Length == 2", vs_parse_pattern)
        self.assertIn("result.Truncated = true", vs_service)
        self.assertIn("result.NextStartAddress", vs_service)

    def test_vs_memory_search_safety_contracts(self) -> None:
        secondary = read("VSDebuggerMCP/Services/DebuggerService.SecondaryInspection.cs")
        evaluation = read("VSDebuggerMCP/Services/DebuggerService.EvaluationMemory.cs")
        cursor_codec = read("VSDebuggerMCP/Services/MappedDumpSearchContinuation.cs")
        memory_tools = read("mcp_server/tool_modules/memory_tools.py")
        search_loop = csharp_slice(
            secondary,
            "private MemorySearchResult SearchMemoryByReader(",
            "        private void SetMemorySearchContinuation",
        )
        verified_handle = csharp_slice(
            evaluation,
            "private IntPtr OpenVerifiedCurrentLiveProcessHandle(",
            "        private bool IsCurrentLocalProcessIdentity(",
        )

        self.assertIn("result.BytesAttempted += available", search_loop)
        self.assertNotIn("result.BytesAttempted += readSize", search_loop)
        self.assertIn(
            "Memory read made no progress; the remaining range was not skipped",
            search_loop,
        )
        self.assertNotIn("offset + readSize, requestedBytes", search_loop)
        self.assertIn("IsExpressionMemoryFallbackCapability(capability)", search_loop)
        self.assertIn("IsExpressionMemoryFallbackCapability(header.Capability)", evaluation)
        self.assertIn('"debugger_expression_dump_fallback"', evaluation)
        self.assertIn("Memory range overflows address space", evaluation)
        self.assertIn("patternBytes.Length > ExpressionFallbackMaxBytes", secondary)
        self.assertIn("cannot be evaluated through the bounded debugger-expression fallback", secondary)
        self.assertIn("RethrowRequestControlFlow(ex)", verified_handle)
        self.assertIn("IntPtr handle = IntPtr.Zero", verified_handle)
        self.assertIn("finally\n            {\n                if (handle != IntPtr.Zero)", verified_handle)
        self.assertIn("header == null || header.ProcessHandle == IntPtr.Zero", evaluation)
        self.assertIn("if (processHandle != IntPtr.Zero)\n                    {\n                        CloseHandle(processHandle);", evaluation)

        self.assertEqual(
            2,
            verified_handle.count("IsCurrentLocalProcessIdentity(debugger, current, processId)"),
        )
        self.assertLess(
            verified_handle.index("OpenProcess(desiredAccess, false, processId)"),
            verified_handle.rfind("IsCurrentLocalProcessIdentity(debugger, current, processId)"),
        )

        for token in (
            "CursorVersion = 3",
            "DumpIdentityDigest",
            "SnapshotDigest",
            "PatternDigest",
            "continuation does not match the active dump, debugger context, or search pattern",
        ):
            self.assertIn(token, cursor_codec)
        self.assertIn("restart the\n    search if any of those changes", memory_tools)
        self.assertIn("terminal_backend_no_continuation", memory_tools)

    def test_debug_bundle_safe_defaults_are_advertised(self) -> None:
        server_py = read("mcp_server/vs_server.py")
        vs_pipe = read("VSDebuggerMCP/NamedPipeServer.cs")
        vs_registration = read("VSDebuggerMCP/McpClientRegistration.cs")
        readme = read("README.md")

        for text in (
            "Safe by default",
            "include_locals/include_modules/include_registers/include_output",
            "Expressions are attempted by default with bounded IDE timeouts",
        ):
            self.assertIn(text, server_py)

        self.assertIn('"includeLocals"', vs_pipe)
        self.assertIn('"includeOutput"', vs_pipe)
        self.assertIn('"evaluationPolicy"', vs_pipe)
        # Visual Studio cannot enforce allowSideEffects, so the parameter is rejected outright.
        self.assertIn("Parameter 'allowSideEffects' was removed because Visual Studio cannot enforce it.", vs_pipe)

        for doc in (vs_registration, readme):
            self.assertIn("low-blocking", doc)
            self.assertIn("value/error/timeout", doc)

        guide = function_source(server_py, "debug_guide")
        self.assertIn("crash_triage()", guide)
        self.assertIn("step_and_capture", guide)

        self.assertIn("timeout_seconds", function_source(server_py, "get_disassembly"))
        self.assertIn("timeout_seconds", function_source(server_py, "get_disassembly_at"))

    def test_expression_calls_are_attempted_instead_of_pre_rejected(self) -> None:
        server_py = read("mcp_server/vs_server.py")
        vs_service = read("VSDebuggerMCP/DebuggerService.cs")

        single_eval = function_source(server_py, "evaluate_expression")
        self.assertNotIn("return rejection", single_eval)
        self.assertNotIn("side_effect_rejection", server_py)
        self.assertNotIn("FindPotentialSideEffectCalls", vs_service)

        for name in (
            "evaluate_expressions_batch",
            "capture_debug_bundle",
            "continue_and_wait_for_break",
        ):
            body = function_source(server_py, name)
            self.assertNotIn("return rejection", body)
            self.assertIn('"evaluationPolicy": evaluation_policy', body)

    def test_cursor_allowlist_helper_is_not_bundled_in_vsix(self) -> None:
        helper_py = read("mcp_server/cursor_allowlist_sync.py")
        vs_project = read("VSDebuggerMCP/VSDebuggerMCP.csproj")

        self.assertIn("mcpAllowedTools", helper_py)
        self.assertIn("state.vscdb", helper_py)
        self.assertNotIn("cursor_allowlist_sync.py", vs_project)

    def test_codex_registration_is_wired_for_vs(self) -> None:
        vs_package = read("VSDebuggerMCP/VSDebuggerMCPPackage.cs")
        vs_registration = read("VSDebuggerMCP/McpClientRegistration.cs")

        self.assertIn("RegisterWithCodex", vs_package)
        self.assertIn("GetCodexConfigPath", vs_registration)
        self.assertIn("RegisterWithCodex", vs_registration)
        self.assertIn("config.toml", vs_registration)
        self.assertIn("CODEX_HOME", vs_registration)


if __name__ == "__main__":
    unittest.main()
