using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;
using VSDebuggerMCP.Ipc;
using VSDebuggerMCP.Protocol;
using VSDebuggerMCP.Services;


namespace VSDebuggerMCP
{
    public partial class NamedPipeServer
    {
        [SuppressMessage(
            "Usage",
            "VSTHRD010",
            Justification = "All DebuggerService IPC entrypoints marshal their EnvDTE critical sections through ExecuteOnMainThread.")]
        private object HandleMethod(
            DebugRequest request,
            string clientLane,
            Func<Func<object>, object> longWaitPhaseExecutor = null)
        {
            switch (request.Method.ToLowerInvariant())
            {
                case "get_debug_status":
                    return _debuggerService.GetDebugStatus();

                case "get_dispatcher_health":
                    {
                        DispatcherHealthResult health = _debuggerService.GetDispatcherHealth();
                        CommandLedgerSnapshot ledger = _commandResults.Snapshot();
                        QueryJournalSnapshot queries = _queryResults.Snapshot();
                        lock (_activeRequestsLock)
                        {
                            health.ActiveRequestCount = _activeRequests.Count;
                        }
                        health.ActorQueueDepth = _debugSessionActor.QueueDepth;
                        health.DataListenerCount = DataPipeInstances;
                        health.InterruptListenerCount = InterruptPipeInstances;
                        health.RecoveryListenerCount = RecoveryPipeInstances;
                        health.CommandTombstoneCount = ledger.TombstoneCount;
                        health.CommandTombstoneCapacity = ledger.TombstoneCapacity;
                        health.CommandResultCount = ledger.RetainedResponseCount;
                        health.CommandResultBytes = ledger.RetainedResponseBytes;
                        health.OldestCommandAgeMs = ledger.OldestEntryAgeMs;
                        health.QueryResultCount = queries.EntryCount;
                        health.QueryResultCapacity = queries.EntryCapacity;
                        health.QueryResultBytes = queries.RetainedBytes;
                        health.QueryResultByteCapacity = queries.ByteCapacity;
                        return health;
                    }

                case "get_debug_processes":
                    {
                        int startIndex = GetParam<int>(request.Params, "startIndex", 0);
                        int maxNodes = GetParam<int>(request.Params, "maxNodes", -1);
                        int budgetMs = GetParam<int>(request.Params, "budgetMs", -1);
                        return _debuggerService.GetDebugProcesses(startIndex, maxNodes, budgetMs);
                    }

                case "get_request_status":
                    {
                        string requestId = GetParam<string>(request.Params, "requestId", "");
                        return GetCommandRequestStatus(clientLane, requestId);
                    }

                case "cancel_request":
                    {
                        string requestId = GetParam<string>(request.Params, "requestId", "");
                        if (ControlRequestValidator.IsSelfCancellation(request.Id, requestId))
                        {
                            throw new ParameterValidationException(
                                "A cancel_request command cannot target its own request id.");
                        }
                        return CancelRequest(clientLane, requestId);
                    }

                case "get_callstack":
                    return _debuggerService.GetCallStack(
                        GetParam<int>(request.Params, "startIndex", 0),
                        GetParam<int>(request.Params, "maxFrames", CallStackCaptureBudget.DefaultMaxFrames),
                        GetParam<int>(request.Params, "budgetMs", CallStackCaptureBudget.DefaultBudgetMs));

                case "get_locals":
                    {
                        int localsDepth = GetParam<int>(request.Params, "depth", 2);
                        int threadId = GetParam<int>(request.Params, "threadId", 0);
                        int frameIndex = GetParam<int>(request.Params, "frameIndex", 0);
                        long generation = GetParam<long>(request.Params, "debugSessionGeneration", 0);
                        long breakSequence = GetParam<long>(request.Params, "breakSequence", 0);
                        long contextEpoch = GetParam<long>(request.Params, "contextEpoch", 0);
                        int processId = GetParam<int>(request.Params, "processId", 0);
                        string frameId = GetParam<string>(request.Params, "frameId", "");
                        int threadLookupBudgetMs = GetParam<int>(request.Params, "threadLookupBudgetMs", -1);
                        return _debuggerService.GetLocals(
                            localsDepth,
                            threadId,
                            frameIndex,
                            generation,
                            breakSequence,
                            contextEpoch,
                            processId,
                            frameId,
                            threadLookupBudgetMs);
                    }

                case "get_threads":
                    {
                        bool includeFrames = GetParam<bool>(request.Params, "includeFrames", true);
                        int maxFramesPerThread = GetParam<int>(request.Params, "maxFramesPerThread", -1);
                        int maxNodes = GetParam<int>(request.Params, "maxNodes", -1);
                        int budgetMs = GetParam<int>(request.Params, "budgetMs", -1);
                        int startThreadIndex = GetParam<int>(request.Params, "startThreadIndex", 1);
                        int startFrameIndex = GetParam<int>(request.Params, "startFrameIndex", 0);
                        if (includeFrames && maxNodes == 1)
                        {
                            throw new ParameterValidationException(
                                "Parameter 'maxNodes' must be 0 or at least 2 when includeFrames=true; " +
                                "one node cannot contain both a thread header and a frame and would not advance the cursor.");
                        }
                        return _debuggerService.GetThreads(
                            includeFrames,
                            maxFramesPerThread,
                            maxNodes,
                            budgetMs,
                            startThreadIndex,
                            startFrameIndex);
                    }

                case "get_modules":
                    {
                        int maxNodes = GetParam<int>(request.Params, "maxNodes", -1);
                        int budgetMs = GetParam<int>(request.Params, "budgetMs", -1);
                        int startModuleIndex = GetParam<int>(request.Params, "startModuleIndex", 0);
                        int maxFramesScanned = GetParam<int>(request.Params, "maxFramesScanned", -1);
                        string moduleSnapshotId = GetParam<string>(request.Params, "moduleSnapshotId", null);
                        string scanContinuationToken = GetParam<string>(request.Params, "scanContinuationToken", null);
                        return _debuggerService.GetModules(
                            maxNodes,
                            budgetMs,
                            1,
                            0,
                            startModuleIndex,
                            maxFramesScanned,
                            moduleSnapshotId,
                            scanContinuationToken);
                    }

                case "get_exception":
                    return _debuggerService.GetException();

                case "evaluate_expression":
                    {
                        string expression = GetParam<string>(request.Params, "expression", "");
                        bool expandChildren = GetParam<bool>(request.Params, "expandChildren", false);
                        int maxDepth = GetParam<int>(request.Params, "maxDepth", 2);
                        ValidateEvaluationPolicy(request.Params);
                        return _debuggerService.EvaluateExpression(expression, expandChildren, maxDepth);
                    }

                case "evaluate_expressions_batch":
                    {
                        var expressions = GetStringListParam(request.Params, "expressions");
                        bool expandChildren = GetParam<bool>(request.Params, "expandChildren", false);
                        int maxDepth = GetParam<int>(request.Params, "maxDepth", 2);
                        ValidateEvaluationPolicy(request.Params);
                        return _debuggerService.EvaluateExpressionsBatch(expressions, expandChildren, maxDepth);
                    }

                case "capture_debug_bundle_safe":
                    {
                        int outputTailLines = GetParam<int>(request.Params, "outputTailLines", 80);
                        bool includeModules = GetParam<bool>(request.Params, "includeModules", false);
                        bool includeOutput = GetParam<bool>(request.Params, "includeOutput", false);
                        bool includeThreads = GetParam<bool>(request.Params, "includeThreads", true);
                        bool threadIncludeFrames = GetParam<bool>(request.Params, "threadIncludeFrames", true);
                        int threadMaxFramesPerThread = GetParam<int>(request.Params, "threadMaxFramesPerThread", -1);
                        int threadMaxNodes = GetParam<int>(request.Params, "threadMaxNodes", -1);
                        int threadBudgetMs = GetParam<int>(request.Params, "threadBudgetMs", -1);
                        return _debuggerService.CaptureDebugBundle(
                            new List<string>(), false, 0, outputTailLines,
                            false, includeModules, false, includeOutput,
                            includeThreads, false, threadIncludeFrames,
                            threadMaxFramesPerThread, threadMaxNodes, threadBudgetMs);
                    }

                case "capture_debug_bundle":
                    {
                        var expressions = GetStringListParam(request.Params, "expressions");
                        bool expandChildren = GetParam<bool>(request.Params, "expandChildren", false);
                        int maxDepth = GetParam<int>(request.Params, "maxDepth", 2);
                        int outputTailLines = GetParam<int>(request.Params, "outputTailLines", 80);
                        bool includeLocals = GetParam<bool>(request.Params, "includeLocals", false);
                        bool includeModules = GetParam<bool>(request.Params, "includeModules", false);
                        bool includeRegisters = GetParam<bool>(request.Params, "includeRegisters", false);
                        bool includeOutput = GetParam<bool>(request.Params, "includeOutput", false);
                        bool includeThreads = GetParam<bool>(request.Params, "includeThreads", true);
                        bool includeException = GetParam<bool>(request.Params, "includeException", true);
                        ValidateEvaluationPolicy(request.Params);
                        bool threadIncludeFrames = GetParam<bool>(request.Params, "threadIncludeFrames", true);
                        int threadMaxFramesPerThread = GetParam<int>(request.Params, "threadMaxFramesPerThread", -1);
                        int threadMaxNodes = GetParam<int>(request.Params, "threadMaxNodes", -1);
                        int threadBudgetMs = GetParam<int>(request.Params, "threadBudgetMs", -1);
                        return _debuggerService.CaptureDebugBundle(
                            expressions,
                            expandChildren,
                            maxDepth,
                            outputTailLines,
                            includeLocals,
                            includeModules,
                            includeRegisters,
                            includeOutput,
                            includeThreads,
                            includeException,
                            threadIncludeFrames,
                            threadMaxFramesPerThread,
                            threadMaxNodes,
                            threadBudgetMs);
                    }

                case "read_memory":
                    {
                        string address = GetParam<string>(request.Params, "address", "");
                        ValidateAddressLiteral("address", address);
                        int size = GetParam<int>(request.Params, "size", 256);
                        return _debuggerService.ReadMemory(address, size);
                    }

                case "get_dump_memory_ranges":
                    {
                        int startIndex = GetParam<int>(request.Params, "startIndex", 0);
                        int maxNodes = GetParam<int>(request.Params, "maxNodes", -1);
                        int budgetMs = GetParam<int>(request.Params, "budgetMs", -1);
                        return _debuggerService.GetDumpMemoryRanges(startIndex, maxNodes, budgetMs);
                    }

                case "get_disassembly":
                    {
                        int linesBefore = GetParam<int>(request.Params, "linesBefore", 20);
                        int linesAfter = GetParam<int>(request.Params, "linesAfter", 20);
                        return _debuggerService.GetDisassembly(linesBefore, linesAfter);
                    }

                case "get_disassembly_at":
                    {
                        string address = GetParam<string>(request.Params, "address", "");
                        ValidateAddressLiteral("address", address);
                        int count = GetParam<int>(request.Params, "count", 50);
                        return _debuggerService.GetDisassemblyAt(address, count);
                    }

                case "resolve_address":
                    {
                        string address = GetParam<string>(request.Params, "address", "");
                        return _debuggerService.ResolveAddress(address);
                    }

                case "get_module_layout":
                    {
                        string module = GetParam<string>(request.Params, "module", "");
                        int maxImportFunctions = GetParam<int>(request.Params, "maxImportFunctions", 256);
                        int maxExports = GetParam<int>(request.Params, "maxExports", 512);
                        return _debuggerService.GetModuleLayout(module, maxImportFunctions, maxExports);
                    }

                case "capture_native_context":
                    {
                        int disassemblyCount = GetParam<int>(request.Params, "disassemblyCount", 32);
                        int stackBytes = GetParam<int>(request.Params, "stackBytes", 256);
                        bool includeModules = GetParam<bool>(request.Params, "includeModules", true);
                        return _debuggerService.CaptureNativeContext(disassemblyCount, stackBytes, includeModules);
                    }

                case "continue_and_capture_native":
                    {
                        int timeoutMs = GetParam<int>(request.Params, "timeoutMs", 120000);
                        int pollIntervalMs = GetParam<int>(request.Params, "pollIntervalMs", 200);
                        int disassemblyCount = GetParam<int>(request.Params, "disassemblyCount", 32);
                        int stackBytes = GetParam<int>(request.Params, "stackBytes", 256);
                        bool includeModules = GetParam<bool>(request.Params, "includeModules", true);
                        return _debuggerService.ContinueAndCaptureNative(
                            timeoutMs,
                            pollIntervalMs,
                            disassemblyCount,
                            stackBytes,
                            includeModules,
                            longWaitPhaseExecutor);
                    }

                case "inspect_managed_assembly":
                    {
                        string assemblyPath = GetParam<string>(request.Params, "assemblyPath", "");
                        string typeFilter = GetParam<string>(request.Params, "typeFilter", null);
                        int maxTypes = GetParam<int>(request.Params, "maxTypes", 128);
                        int maxMethodsPerType = GetParam<int>(request.Params, "maxMethodsPerType", 32);
                        return _debuggerService.InspectManagedAssembly(assemblyPath, typeFilter, maxTypes, maxMethodsPerType);
                    }

                case "inspect_managed_type":
                    {
                        string assemblyPath = GetParam<string>(request.Params, "assemblyPath", "");
                        string typeName = GetParam<string>(request.Params, "typeName", "");
                        int maxMethods = GetParam<int>(request.Params, "maxMethods", 32);
                        return _debuggerService.InspectManagedType(assemblyPath, typeName, maxMethods);
                    }

                case "inspect_managed_method_il":
                    {
                        string assemblyPath = GetParam<string>(request.Params, "assemblyPath", "");
                        string typeName = GetParam<string>(request.Params, "typeName", "");
                        string methodName = GetParam<string>(request.Params, "methodName", "");
                        int overloadIndex = GetParam<int>(request.Params, "overloadIndex", 0);
                        int maxInstructions = GetParam<int>(request.Params, "maxInstructions", 512);
                        return _debuggerService.InspectManagedMethodIl(assemblyPath, typeName, methodName, overloadIndex, maxInstructions);
                    }

                case "capture_hybrid_context":
                    {
                        int disassemblyCount = GetParam<int>(request.Params, "disassemblyCount", 32);
                        int stackBytes = GetParam<int>(request.Params, "stackBytes", 256);
                        bool includeNative = GetParam<bool>(request.Params, "includeNative", true);
                        bool includeModules = GetParam<bool>(request.Params, "includeModules", true);
                        bool includeThreads = GetParam<bool>(request.Params, "includeThreads", true);
                        bool includeBreakpoints = GetParam<bool>(request.Params, "includeBreakpoints", true);
                        bool threadIncludeFrames = GetParam<bool>(request.Params, "threadIncludeFrames", true);
                        int threadMaxFramesPerThread = GetParam<int>(request.Params, "threadMaxFramesPerThread", -1);
                        int threadMaxNodes = GetParam<int>(request.Params, "threadMaxNodes", -1);
                        int threadBudgetMs = GetParam<int>(request.Params, "threadBudgetMs", -1);
                        return _debuggerService.CaptureHybridContext(
                            disassemblyCount,
                            stackBytes,
                            includeNative,
                            includeModules,
                            includeThreads,
                            includeBreakpoints,
                            threadIncludeFrames,
                            threadMaxFramesPerThread,
                            threadMaxNodes,
                            threadBudgetMs);
                    }

                case "get_breakpoint_bindings":
                    {
                        int startIndex = GetParam<int>(request.Params, "startIndex", 0);
                        int maxNodes = GetParam<int>(request.Params, "maxNodes", -1);
                        int budgetMs = GetParam<int>(request.Params, "budgetMs", -1);
                        return _debuggerService.GetBreakpointBindings(startIndex, maxNodes, budgetMs);
                    }

                case "continue_and_capture_breakpoint":
                    {
                        int timeoutMs = GetParam<int>(request.Params, "timeoutMs", 120000);
                        int pollIntervalMs = GetParam<int>(request.Params, "pollIntervalMs", 200);
                        int disassemblyCount = GetParam<int>(request.Params, "disassemblyCount", 32);
                        int stackBytes = GetParam<int>(request.Params, "stackBytes", 256);
                        bool includeNative = GetParam<bool>(request.Params, "includeNative", true);
                        bool includeModules = GetParam<bool>(request.Params, "includeModules", true);
                        bool includeThreads = GetParam<bool>(request.Params, "includeThreads", true);
                        bool includeBreakpoints = GetParam<bool>(request.Params, "includeBreakpoints", true);
                        bool threadIncludeFrames = GetParam<bool>(request.Params, "threadIncludeFrames", true);
                        int threadMaxFramesPerThread = GetParam<int>(request.Params, "threadMaxFramesPerThread", -1);
                        int threadMaxNodes = GetParam<int>(request.Params, "threadMaxNodes", -1);
                        int threadBudgetMs = GetParam<int>(request.Params, "threadBudgetMs", -1);
                        return _debuggerService.ContinueAndCaptureBreakpoint(
                            timeoutMs,
                            pollIntervalMs,
                            disassemblyCount,
                            stackBytes,
                            includeNative,
                            includeModules,
                            includeThreads,
                            includeBreakpoints,
                            threadIncludeFrames,
                            threadMaxFramesPerThread,
                            threadMaxNodes,
                            threadBudgetMs,
                            longWaitPhaseExecutor);
                    }

                case "get_build_errors":
                    {
                        int startIndex = GetParam<int>(request.Params, "startIndex", 0);
                        int maxNodes = GetParam<int>(request.Params, "maxNodes", -1);
                        int budgetMs = GetParam<int>(request.Params, "budgetMs", -1);
                        return _debuggerService.GetBuildErrors(startIndex: startIndex, maxNodes: maxNodes, budgetMs: budgetMs);
                    }

                case "get_errors_only":
                    {
                        int startIndex = GetParam<int>(request.Params, "startIndex", 0);
                        int maxNodes = GetParam<int>(request.Params, "maxNodes", -1);
                        int budgetMs = GetParam<int>(request.Params, "budgetMs", -1);
                        return _debuggerService.GetBuildErrors(errorsOnly: true, startIndex: startIndex, maxNodes: maxNodes, budgetMs: budgetMs);
                    }

                case "get_warnings_only":
                    {
                        int startIndex = GetParam<int>(request.Params, "startIndex", 0);
                        int maxNodes = GetParam<int>(request.Params, "maxNodes", -1);
                        int budgetMs = GetParam<int>(request.Params, "budgetMs", -1);
                        return _debuggerService.GetBuildErrors(warningsOnly: true, startIndex: startIndex, maxNodes: maxNodes, budgetMs: budgetMs);
                    }

                case "get_full_context":
                    {
                        bool threadIncludeFrames = GetParam<bool>(request.Params, "threadIncludeFrames", true);
                        int threadMaxFramesPerThread = GetParam<int>(request.Params, "threadMaxFramesPerThread", -1);
                        int threadMaxNodes = GetParam<int>(request.Params, "threadMaxNodes", -1);
                        int threadBudgetMs = GetParam<int>(request.Params, "threadBudgetMs", -1);
                        return _debuggerService.GetFullContext(
                            threadIncludeFrames,
                            threadMaxFramesPerThread,
                            threadMaxNodes,
                            threadBudgetMs);
                    }

                case "ping":
                    return new PingResult { Status = "ok", Timestamp = DateTime.UtcNow.ToString("o") };

                case "get_instance_info":
                    {
                        InstanceInfoResult instance = _debuggerService.GetInstanceInfo(_pipeName);
                        instance.AllowedCapabilities = _allowedCapabilities
                            .OrderBy(value => value, StringComparer.Ordinal)
                            .ToArray();
                        instance.AccessMode = _allowedCapabilities.SetEquals(DebugMethodCatalog.AllCapabilities)
                            ? "full_access"
                            : (_allowedCapabilities.SetEquals(new[] { "server.control", "debug.read" })
                                ? "read_only"
                                : "custom");
                        return instance;
                    }

                // Debug Control
                case "attach_process":
                    {
                        int processId = GetParam<int>(request.Params, "processId", 0);
                        string processName = GetParam<string>(request.Params, "processName", null);
                        bool hasProcessId = request.Params != null && request.Params.ContainsKey("processId");
                        bool hasProcessName = request.Params != null && request.Params.ContainsKey("processName");
                        if (hasProcessId == hasProcessName)
                            throw new ParameterValidationException(
                                "Exactly one of 'processId' or 'processName' is required.");
                        return _debuggerService.AttachProcess(processId, processName);
                    }

                case "start_debugging":
                    {
                        string executablePath = GetParam<string>(request.Params, "executablePath", null);
                        string workingDirectory = GetParam<string>(request.Params, "workingDirectory", null);
                        string arguments = GetParam<string>(request.Params, "arguments", null);
                        bool attach = GetParam<bool>(request.Params, "attach", true);
                        bool hasExecutable = !string.IsNullOrWhiteSpace(executablePath);
                        bool isDump = hasExecutable && IsDumpDebugTarget(executablePath);
                        bool hasLaunchOnly = request.Params != null &&
                            (request.Params.ContainsKey("workingDirectory") ||
                             request.Params.ContainsKey("arguments") ||
                             request.Params.ContainsKey("attach"));
                        if ((!hasExecutable || isDump) && hasLaunchOnly)
                            throw new ParameterValidationException(
                                "workingDirectory, arguments, and attach require a non-dump executablePath.");
                        return _debuggerService.StartDebugging(executablePath, workingDirectory, arguments, attach);
                    }

                case "continue":
                    return _debuggerService.Continue();

                case "continue_and_wait_for_break_safe":
                case "continue_and_wait_for_break":
                    {
                        bool safeCapture = string.Equals(
                            request.Method,
                            "continue_and_wait_for_break_safe",
                            StringComparison.OrdinalIgnoreCase);
                        var expressions = safeCapture
                            ? new List<string>()
                            : GetStringListParam(request.Params, "expressions");
                        bool expandChildren = GetParam<bool>(request.Params, "expandChildren", false);
                        int maxDepth = GetParam<int>(request.Params, "maxDepth", 2);
                        int outputTailLines = GetParam<int>(request.Params, "outputTailLines", 80);
                        int timeoutMs = GetParam<int>(request.Params, "timeoutMs", 120000);
                        int pollIntervalMs = GetParam<int>(request.Params, "pollIntervalMs", 200);
                        bool includeLocals = safeCapture ? false : GetParam<bool>(request.Params, "includeLocals", false);
                        bool includeModules = GetParam<bool>(request.Params, "includeModules", false);
                        bool includeRegisters = safeCapture ? false : GetParam<bool>(request.Params, "includeRegisters", false);
                        bool includeOutput = GetParam<bool>(request.Params, "includeOutput", false);
                        bool includeThreads = GetParam<bool>(request.Params, "includeThreads", true);
                        bool includeException = safeCapture ? false : GetParam<bool>(request.Params, "includeException", true);
                        if (!safeCapture) ValidateEvaluationPolicy(request.Params);
                        bool threadIncludeFrames = GetParam<bool>(request.Params, "threadIncludeFrames", true);
                        int threadMaxFramesPerThread = GetParam<int>(request.Params, "threadMaxFramesPerThread", -1);
                        int threadMaxNodes = GetParam<int>(request.Params, "threadMaxNodes", -1);
                        int threadBudgetMs = GetParam<int>(request.Params, "threadBudgetMs", -1);
                        return _debuggerService.ContinueAndWaitForBreak(
                            expressions,
                            expandChildren,
                            maxDepth,
                            outputTailLines,
                            timeoutMs,
                            pollIntervalMs,
                            includeLocals,
                            includeModules,
                            includeRegisters,
                            includeOutput,
                            includeThreads,
                            includeException,
                            threadIncludeFrames,
                            threadMaxFramesPerThread,
                            threadMaxNodes,
                            threadBudgetMs,
                            longWaitPhaseExecutor);
                    }

                case "step_into":
                    return _debuggerService.StepInto(GetParam<int>(request.Params, "waitTimeoutMs", 2000));

                case "step_over":
                    return _debuggerService.StepOver(GetParam<int>(request.Params, "waitTimeoutMs", 2000));

                case "step_out":
                    return _debuggerService.StepOut(GetParam<int>(request.Params, "waitTimeoutMs", 2000));

                case "break":
                    return _debuggerService.Break();

                case "stop":
                    return _debuggerService.Stop();

                case "detach":
                    return _debuggerService.Detach();

                case "run_to_cursor":
                    {
                        string filePath = GetParam<string>(request.Params, "filePath", "");
                        int lineNumber = GetParam<int>(request.Params, "lineNumber", 0);
                        return _debuggerService.RunToCursor(filePath, lineNumber);
                    }

                case "set_next_statement":
                    {
                        string filePath = GetParam<string>(request.Params, "filePath", "");
                        int lineNumber = GetParam<int>(request.Params, "lineNumber", 0);
                        return _debuggerService.SetNextStatement(filePath, lineNumber);
                    }

                // Breakpoints
                case "get_breakpoints":
                    {
                        int startIndex = GetParam<int>(request.Params, "startIndex", 0);
                        int maxNodes = GetParam<int>(request.Params, "maxNodes", -1);
                        int budgetMs = GetParam<int>(request.Params, "budgetMs", -1);
                        return _debuggerService.GetBreakpoints(startIndex, maxNodes, budgetMs);
                    }

                case "set_breakpoint":
                    {
                        string filePath = GetParam<string>(request.Params, "filePath", "");
                        int lineNumber = GetParam<int>(request.Params, "lineNumber", 0);
                        string condition = GetParam<string>(request.Params, "condition", null);
                        int hitCount = GetParam<int>(request.Params, "hitCount", 0);
                        string hitCountType = GetParam<string>(request.Params, "hitCountType", null);
                        string conditionType = GetParam<string>(request.Params, "conditionType", null);
                        return _debuggerService.SetBreakpoint(filePath, lineNumber, condition, hitCount, hitCountType, conditionType);
                    }

                case "set_function_breakpoint":
                    {
                        string functionName = GetParam<string>(request.Params, "functionName", "");
                        string condition = GetParam<string>(request.Params, "condition", null);
                        int hitCount = GetParam<int>(request.Params, "hitCount", 0);
                        string hitCountType = GetParam<string>(request.Params, "hitCountType", null);
                        string conditionType = GetParam<string>(request.Params, "conditionType", null);
                        return _debuggerService.SetFunctionBreakpoint(functionName, condition, hitCount, hitCountType, conditionType);
                    }

                case "set_data_breakpoint":
                case "set_watchpoint":
                    {
                        string dataExpression = GetParam<string>(request.Params, "dataExpression", "");
                        int byteCount = GetParam<int>(request.Params, "byteCount", 1);
                        string condition = GetParam<string>(request.Params, "condition", null);
                        int hitCount = GetParam<int>(request.Params, "hitCount", 0);
                        string hitCountType = GetParam<string>(request.Params, "hitCountType", null);
                        string conditionType = GetParam<string>(request.Params, "conditionType", null);
                        return _debuggerService.SetDataBreakpoint(dataExpression, byteCount, condition, hitCount, hitCountType, conditionType);
                    }

                case "set_address_breakpoint":
                    {
                        string address = GetParam<string>(request.Params, "address", "");
                        string condition = GetParam<string>(request.Params, "condition", null);
                        int hitCount = GetParam<int>(request.Params, "hitCount", 0);
                        string hitCountType = GetParam<string>(request.Params, "hitCountType", null);
                        string conditionType = GetParam<string>(request.Params, "conditionType", null);
                        return _debuggerService.SetAddressBreakpoint(address, condition, hitCount, hitCountType, conditionType);
                    }

                case "delete_breakpoint":
                case "clear_breakpoint":
                    {
                        string filePath = GetParam<string>(request.Params, "filePath", "");
                        int lineNumber = GetParam<int>(request.Params, "lineNumber", 0);
                        int breakpointLookupBudgetMs = GetParam<int>(request.Params, "breakpointLookupBudgetMs", -1);
                        return _debuggerService.DeleteBreakpoint(filePath, lineNumber, breakpointLookupBudgetMs);
                    }

                case "delete_data_breakpoint":
                    {
                        string dataBreakpointId = GetParam<string>(request.Params, "dataBreakpointId", "");
                        int breakpointLookupBudgetMs = GetParam<int>(request.Params, "breakpointLookupBudgetMs", -1);
                        return _debuggerService.DeleteDataBreakpoint(dataBreakpointId, breakpointLookupBudgetMs);
                    }

                case "delete_all_breakpoints":
                    return _debuggerService.DeleteAllBreakpoints();

                case "enable_breakpoint":
                    {
                        string filePath = GetParam<string>(request.Params, "filePath", "");
                        int lineNumber = GetParam<int>(request.Params, "lineNumber", 0);
                        bool enabled = GetParam<bool>(request.Params, "enabled", true);
                        int breakpointLookupBudgetMs = GetParam<int>(request.Params, "breakpointLookupBudgetMs", -1);
                        return _debuggerService.EnableBreakpoint(filePath, lineNumber, enabled, breakpointLookupBudgetMs);
                    }

                // Registers
                case "get_registers":
                    return _debuggerService.GetRegisters();

                // Watch
                case "add_watch":
                    {
                        string expression = GetParam<string>(request.Params, "expression", "");
                        WatchResult result = _debuggerService.AddWatch(expression);
                        if (!string.IsNullOrWhiteSpace(result.ErrorCode))
                        {
                            throw new DebuggerOperationFailedException(result.ErrorCode, result.Error);
                        }
                        return result;
                    }

                // Memory Search
                case "search_memory":
                    {
                        string startAddress = GetParam<string>(request.Params, "startAddress", "");
                        if (!string.IsNullOrWhiteSpace(startAddress))
                        {
                            ValidateAddressLiteral("startAddress", startAddress);
                        }
                        string pattern = GetParam<string>(request.Params, "pattern", "");
                        int maxResults = GetParam<int>(request.Params, "maxResults", 100);
                        int searchSize = GetParam<int>(request.Params, "searchSize", 0x10000);
                        string patternFormat = GetParam<string>(request.Params, "patternFormat", "auto");
                        long startOffset = GetParam<long>(request.Params, "startOffset", 0);
                        string continuation = GetParam<string>(request.Params, "continuation", "");
                        return _debuggerService.SearchMemory(
                            startAddress,
                            pattern,
                            maxResults,
                            searchSize,
                            patternFormat,
                            startOffset,
                            continuation);
                    }

                // Debug Context Navigation
                case "switch_stack_frame":
                    {
                        int frameIndex = GetParam<int>(request.Params, "frameIndex", 0);
                        return _debuggerService.SwitchStackFrame(frameIndex);
                    }

                case "switch_process":
                    {
                        string processHandle = GetParam<string>(request.Params, "processHandle", null);
                        int processId = GetParam<int>(request.Params, "processId", 0);
                        int processLookupBudgetMs = GetParam<int>(request.Params, "processLookupBudgetMs", -1);
                        return _debuggerService.SwitchProcess(
                            processHandle,
                            processId,
                            processLookupBudgetMs);
                    }

                case "switch_thread":
                    {
                        int threadId = GetParam<int>(request.Params, "threadId", 0);
                        int threadLookupBudgetMs = GetParam<int>(request.Params, "threadLookupBudgetMs", -1);
                        return _debuggerService.SwitchThread(threadId, threadLookupBudgetMs);
                    }

                // Output Window
                case "get_output_window":
                    {
                        string pane = GetParam<string>(request.Params, "pane", "Debug");
                        int tailLines = GetParam<int>(request.Params, "tailLines", 50);
                        int endCharacter = GetParam<int>(request.Params, "endCharacter", -1);
                        int maxBytes = GetParam<int>(
                            request.Params,
                            "maxBytes",
                            OutputContentBudget.DefaultMaxSerializedContentBytes);
                        return _debuggerService.GetOutputWindow(pane, tailLines, endCharacter, maxBytes);
                    }

                case "search_output":
                    {
                        string pattern = GetParam<string>(request.Params, "pattern", "");
                        string pane = GetParam<string>(request.Params, "pane", "Debug");
                        bool isRegex = GetParam<bool>(request.Params, "isRegex", false);
                        int contextLines = GetParam<int>(request.Params, "contextLines", 2);
                        int maxMatches = GetParam<int>(request.Params, "maxMatches", 20);
                        int startLine = GetParam<int>(request.Params, "startLine", 0);
                        int endCharacter = GetParam<int>(request.Params, "endCharacter", -1);
                        int expectedCaptureStartCharacter = GetParam<int>(request.Params, "expectedCaptureStartCharacter", -1);
                        return _debuggerService.SearchOutput(
                            pattern, pane, isRegex, contextLines, maxMatches,
                            startLine, endCharacter, expectedCaptureStartCharacter);
                    }

                default:
                    throw new NotSupportedException($"Unknown method: {request.Method}");
            }
        }


        private T GetParam<T>(Dictionary<string, object> parameters, string key, T defaultValue)
        {
            // DebugRequestSchemaValidator is the single owner of parameter
            // semantics and ranges. The router performs only lossless JSON type
            // extraction after that immutable preflight has succeeded.
            if (parameters == null || !parameters.ContainsKey(key))
            {
                return defaultValue;
            }

            object value = parameters[key];
            if (value == null)
            {
                if (typeof(T) == typeof(string) && object.Equals(defaultValue, null))
                {
                    return defaultValue;
                }
                throw new ParameterValidationException($"Parameter '{key}' must not be null.");
            }

            try
            {
                if (typeof(T) == typeof(int))
                {
                    if (value is int intValue)
                    {
                        return (T)(object)intValue;
                    }
                    if (value is long longValue && longValue >= int.MinValue && longValue <= int.MaxValue)
                    {
                        int converted = (int)longValue;
                        return (T)(object)converted;
                    }
                    throw new InvalidCastException();
                }
                if (typeof(T) == typeof(long))
                {
                    if (value is long longValue)
                    {
                        return (T)(object)longValue;
                    }
                    if (value is int intValue)
                    {
                        long converted = intValue;
                        return (T)(object)converted;
                    }
                    throw new InvalidCastException();
                }
                if (typeof(T) == typeof(bool))
                {
                    if (value is bool boolValue)
                    {
                        return (T)(object)boolValue;
                    }
                    throw new InvalidCastException();
                }
                if (typeof(T) == typeof(string))
                {
                    if (value is string stringValue)
                    {
                        return (T)(object)stringValue;
                    }
                    throw new InvalidCastException();
                }

                return (T)value;
            }
            catch (Exception ex) when (ex is InvalidCastException || ex is OverflowException || ex is FormatException)
            {
                throw new ParameterValidationException(
                    $"Parameter '{key}' must be a {GetJsonTypeName(typeof(T))}.");
            }
        }

        private static bool IsDumpDebugTarget(string path)
        {
            string extension = Path.GetExtension(path ?? string.Empty);
            return string.Equals(extension, ".dmp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".mdmp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".hdmp", StringComparison.OrdinalIgnoreCase);
        }

        private void ValidateEvaluationPolicy(Dictionary<string, object> parameters)
        {
            if (parameters != null && parameters.ContainsKey("allowSideEffects"))
            {
                throw new ParameterValidationException(
                    "Parameter 'allowSideEffects' was removed because Visual Studio cannot enforce it. " +
                    "Use evaluationPolicy='engine_default'.");
            }

            string policy = GetParam<string>(parameters, "evaluationPolicy", "engine_default");
            if (!string.Equals(policy, "engine_default", StringComparison.Ordinal))
            {
                throw new UnsupportedEvaluationPolicyException(
                    "Visual Studio does not expose a reliable side-effect-free EnvDTE evaluation mode. " +
                    "Only 'engine_default' is supported.");
            }
        }

    }
}
