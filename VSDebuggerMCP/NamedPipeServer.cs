using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Serialization.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using VSDebuggerMCP.Protocol;

namespace VSDebuggerMCP
{
    /// <summary>
    /// Named Pipe server for MCP communication.
    /// Uses JSON-RPC style protocol over a named pipe.
    /// Each VS instance creates a unique pipe with its process ID.
    /// </summary>
    public class NamedPipeServer : IDisposable
    {
        private const string PipeNamePrefix = "AIDebuggerVS";
        private const int MaxConnections = 10;
        private const int MaxMessageSize = 10 * 1024 * 1024; // 10 MB per message
        
        // Unique pipe name for this VS instance (includes process ID)
        private readonly string _pipeName;

        private readonly DebuggerService _debuggerService;
        private readonly Action<string> _log;
        private readonly CancellationTokenSource _cts;
        private readonly JavaScriptSerializer _serializer;
        private readonly List<Task> _connectionTasks;
        private volatile bool _isRunning;
        private volatile bool _disposed;

        /// <summary>
        /// Gets the unique pipe name for this VS instance.
        /// </summary>
        public string PipeName => _pipeName;

        public NamedPipeServer(DebuggerService debuggerService, Action<string> log)
        {
            _debuggerService = debuggerService ?? throw new ArgumentNullException(nameof(debuggerService));
            _log = log ?? ((s) => { });
            _cts = new CancellationTokenSource();
            _serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            _connectionTasks = new List<Task>();
            
            // Create unique pipe name with process ID
            int processId = System.Diagnostics.Process.GetCurrentProcess().Id;
            _pipeName = $"{PipeNamePrefix}_{processId}";
        }

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;

            _log($"Starting Named Pipe server on \\\\.\\pipe\\{_pipeName}");

            // Start multiple listeners for concurrent connections
            for (int i = 0; i < MaxConnections; i++)
            {
                var task = Task.Run(() => ListenForConnectionsAsync(_cts.Token));
                _connectionTasks.Add(task);
            }
        }

        public void Stop()
        {
            if (!_isRunning) return;
            _isRunning = false;

            _log("Stopping Named Pipe server...");
            _cts.Cancel();

            // Wait for all connection tasks to complete (with timeout)
            try
            {
                var allTasks = Task.WhenAll(_connectionTasks.ToArray());
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5));
                // Wait for either all tasks to complete or timeout
                var completedTask = Task.WhenAny(allTasks, timeoutTask).ConfigureAwait(false).GetAwaiter().GetResult();
                // Observe the result to prevent unobserved task exceptions
                if (completedTask == allTasks)
                {
                    try { allTasks.GetAwaiter().GetResult(); } catch { }
                }
            }
            catch (AggregateException)
            {
                // Ignore cancellation exceptions
            }
            catch (OperationCanceledException)
            {
                // Expected
            }

            _connectionTasks.Clear();
            _log("Named Pipe server stopped.");
        }

        private async Task ListenForConnectionsAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream pipeServer = null;

                try
                {
                    // Create pipe security that allows access from non-admin processes
                    // This is critical when VS runs as admin but Cursor runs as normal user
                    var pipeSecurity = new PipeSecurity();
                    bool strictPipeSecurity = IsStrictPipeSecurityEnabled();
                    
                    if (strictPipeSecurity)
                    {
                        // Strict mode: only current user can connect.
                        pipeSecurity.AddAccessRule(new PipeAccessRule(
                            WindowsIdentity.GetCurrent().User,
                            PipeAccessRights.FullControl,
                            AccessControlType.Allow));
                    }
                    else
                    {
                        // Default compatibility mode: allow cross-privilege local clients.
                        pipeSecurity.AddAccessRule(new PipeAccessRule(
                            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                            AccessControlType.Allow));

                        pipeSecurity.AddAccessRule(new PipeAccessRule(
                            WindowsIdentity.GetCurrent().User,
                            PipeAccessRights.FullControl,
                            AccessControlType.Allow));
                    }

                    pipeServer = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        MaxConnections,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        inBufferSize: 4096,
                        outBufferSize: 4096,
                        pipeSecurity: pipeSecurity);

                    _log("Waiting for client connection...");

                    // Wait for connection with cancellation support
                    await WaitForConnectionAsync(pipeServer, cancellationToken);

                    if (cancellationToken.IsCancellationRequested)
                    {
                        pipeServer.Dispose();
                        break;
                    }

                    _log("Client connected.");

                    // Handle the connection
                    await HandleConnectionAsync(pipeServer, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // Expected when stopping
                    break;
                }
                catch (IOException ex)
                {
                    _log($"IO error: {ex.Message}");
                }
                catch (Exception ex)
                {
                    _log($"Error in pipe listener: {ex.Message}");
                }
                finally
                {
                    pipeServer?.Dispose();
                }
            }
        }

        private static async Task WaitForConnectionAsync(NamedPipeServerStream pipeServer, CancellationToken cancellationToken)
        {
            if (pipeServer == null)
            {
                throw new ArgumentNullException(nameof(pipeServer));
            }

            if (pipeServer.IsConnected)
            {
                return;
            }

            using (cancellationToken.Register(() =>
            {
                try
                {
                    pipeServer.Dispose();
                }
                catch
                {
                    // Ignore cleanup races during cancellation.
                }
            }))
            {
                try
                {
                    await Task.Factory.FromAsync(
                        pipeServer.BeginWaitForConnection,
                        pipeServer.EndWaitForConnection,
                        null);
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                catch (IOException) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            }
        }

        private async Task HandleConnectionAsync(NamedPipeServerStream pipeServer, CancellationToken cancellationToken)
        {
            try
            {
                var reader = new StreamReader(pipeServer, Encoding.UTF8, true, 4096, leaveOpen: true);

                // Use UTF-8 without BOM - the BOM causes Python's json.loads to fail
                var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                var writer = new StreamWriter(pipeServer, utf8NoBom, 4096, leaveOpen: true);

                try
                {
                    // NOTE: Do NOT set writer.AutoFlush = true here!
                    // It causes deadlock with async NamedPipeServerStream when client has pending writes.
                    // Instead, call FlushAsync() explicitly after each write.

                    while (pipeServer.IsConnected && !cancellationToken.IsCancellationRequested)
                    {
                        // Read request with size limit to prevent OOM from malicious/buggy clients
                        string requestLine = await ReadLineWithLimitAsync(reader, MaxMessageSize);
                        if (requestLine == null)
                        {
                            break;
                        }

                        _log($"Received request: {requestLine.Substring(0, Math.Min(100, requestLine.Length))}...");

                        // Process request
                        string response = ProcessRequest(requestLine);

                        // Send response and flush explicitly
                        await writer.WriteLineAsync(response);
                        await writer.FlushAsync();
                        _log($"Sent response: {response.Substring(0, Math.Min(100, response.Length))}...");
                    }
                }
                finally
                {
                    reader.Dispose();
                    writer.Dispose();
                }
            }
            catch (IOException)
            {
                // Client disconnected
                _log("Client disconnected.");
            }
            catch (Exception ex)
            {
                _log($"Error handling connection: {ex.Message}");
            }
        }

        /// <summary>
        /// Reads a line from the stream with a maximum size limit.
        /// Returns null on end-of-stream or empty input. Throws IOException if limit exceeded.
        /// </summary>
        private static async Task<string> ReadLineWithLimitAsync(StreamReader reader, int maxSize)
        {
            var sb = new StringBuilder();
            var buffer = new char[4096];
            
            while (true)
            {
                int ch = reader.Peek();
                if (ch == -1)
                {
                    // End of stream
                    return sb.Length > 0 ? sb.ToString() : null;
                }

                int read = await reader.ReadAsync(buffer, 0, Math.Min(buffer.Length, maxSize - sb.Length + 1));
                if (read == 0)
                {
                    return sb.Length > 0 ? sb.ToString() : null;
                }

                for (int i = 0; i < read; i++)
                {
                    char c = buffer[i];
                    if (c == '\n')
                    {
                        // End of line found - return what we have (trim trailing \r if present)
                        string result = sb.ToString();
                        if (result.Length > 0 && result[result.Length - 1] == '\r')
                            result = result.Substring(0, result.Length - 1);
                        return result.Length > 0 ? result : null;
                    }
                    
                    sb.Append(c);
                    
                    if (sb.Length > maxSize)
                    {
                        throw new IOException($"Message exceeds maximum size of {maxSize} bytes");
                    }
                }
            }
        }

        private string ProcessRequest(string requestJson)
        {
            DebugRequest request = null;
            try
            {
                request = _serializer.Deserialize<DebugRequest>(requestJson);
            }
            catch (Exception ex)
            {
                return SerializeResponse(DebugResponse.Failure(null, "PARSE_ERROR", $"Failed to parse request: {ex.Message}"));
            }

            if (request == null || string.IsNullOrEmpty(request.Method))
            {
                return SerializeResponse(DebugResponse.Failure(request?.Id, "INVALID_REQUEST", "Missing method"));
            }

            try
            {
                object result = HandleMethod(request);
                return SerializeResponse(DebugResponse.Success(request.Id, result));
            }
            catch (Exception ex)
            {
                _log($"Error processing method {request.Method}: {ex.Message}");
                return SerializeResponse(DebugResponse.Failure(request.Id, "INTERNAL_ERROR", ex.Message));
            }
        }

        private object HandleMethod(DebugRequest request)
        {
            switch (request.Method.ToLowerInvariant())
            {
                case "get_debug_status":
                    return _debuggerService.GetDebugStatus();

                case "get_callstack":
                    return _debuggerService.GetCallStack();

                case "get_locals":
                    int localsDepth = GetParam<int>(request.Params, "depth", 2);
                    return _debuggerService.GetLocals(localsDepth);

                case "get_threads":
                    {
                        bool includeFrames = GetParam<bool>(request.Params, "includeFrames", true);
                        int maxFramesPerThread = GetParam<int>(request.Params, "maxFramesPerThread", -1);
                        int maxNodes = GetParam<int>(request.Params, "maxNodes", -1);
                        int budgetMs = GetParam<int>(request.Params, "budgetMs", -1);
                        return _debuggerService.GetThreads(includeFrames, maxFramesPerThread, maxNodes, budgetMs);
                    }

                case "get_modules":
                    return _debuggerService.GetModules();

                case "get_exception":
                    return _debuggerService.GetException();

                case "evaluate_expression":
                    {
                        string expression = GetParam<string>(request.Params, "expression", "");
                        bool expandChildren = GetParam<bool>(request.Params, "expandChildren", false);
                        int maxDepth = GetParam<int>(request.Params, "maxDepth", 2);
                        bool allowSideEffects = GetParam<bool>(request.Params, "allowSideEffects", false);
                        return _debuggerService.EvaluateExpression(expression, expandChildren, maxDepth, allowSideEffects);
                    }

                case "evaluate_expressions_batch":
                    {
                        var expressions = GetStringListParam(request.Params, "expressions");
                        bool expandChildren = GetParam<bool>(request.Params, "expandChildren", false);
                        int maxDepth = GetParam<int>(request.Params, "maxDepth", 2);
                        bool allowSideEffects = GetParam<bool>(request.Params, "allowSideEffects", false);
                        return _debuggerService.EvaluateExpressionsBatch(expressions, expandChildren, maxDepth, allowSideEffects);
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
                        bool allowSideEffects = GetParam<bool>(request.Params, "allowSideEffects", false);
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
                            allowSideEffects,
                            threadIncludeFrames,
                            threadMaxFramesPerThread,
                            threadMaxNodes,
                            threadBudgetMs);
                    }

                case "read_memory":
                    {
                        string address = GetParam<string>(request.Params, "address", "");
                        int size = GetParam<int>(request.Params, "size", 256);
                        return _debuggerService.ReadMemory(address, size);
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
                        return _debuggerService.ContinueAndCaptureNative(timeoutMs, pollIntervalMs, disassemblyCount, stackBytes, includeModules);
                    }

                case "inspect_managed_assembly":
                    {
                        string assemblyPath = GetParam<string>(request.Params, "assemblyPath", "");
                        string typeFilter = GetParam<string>(request.Params, "typeFilter", null);
                        int maxTypes = GetParam<int>(request.Params, "maxTypes", 256);
                        int maxMethodsPerType = GetParam<int>(request.Params, "maxMethodsPerType", 64);
                        return _debuggerService.InspectManagedAssembly(assemblyPath, typeFilter, maxTypes, maxMethodsPerType);
                    }

                case "inspect_managed_type":
                    {
                        string assemblyPath = GetParam<string>(request.Params, "assemblyPath", "");
                        string typeName = GetParam<string>(request.Params, "typeName", "");
                        int maxMethods = GetParam<int>(request.Params, "maxMethods", 512);
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
                    return _debuggerService.GetBreakpointBindings();

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
                            threadBudgetMs);
                    }

                case "get_build_errors":
                    return _debuggerService.GetBuildErrors();

                case "get_errors_only":
                    return _debuggerService.GetBuildErrors(errorsOnly: true);

                case "get_warnings_only":
                    return _debuggerService.GetBuildErrors(warningsOnly: true);

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
                    return _debuggerService.GetInstanceInfo(_pipeName);

                // Debug Control
                case "attach_process":
                    {
                        int processId = GetParam<int>(request.Params, "processId", 0);
                        string processName = GetParam<string>(request.Params, "processName", null);
                        return _debuggerService.AttachProcess(processId, processName);
                    }

                case "start_debugging":
                    {
                        string executablePath = GetParam<string>(request.Params, "executablePath", null);
                        string workingDirectory = GetParam<string>(request.Params, "workingDirectory", null);
                        string arguments = GetParam<string>(request.Params, "arguments", null);
                        bool attach = GetParam<bool>(request.Params, "attach", true);
                        return _debuggerService.StartDebugging(executablePath, workingDirectory, arguments, attach);
                    }

                case "continue":
                    return _debuggerService.Continue();

                case "continue_and_wait_for_break":
                    {
                        var expressions = GetStringListParam(request.Params, "expressions");
                        bool expandChildren = GetParam<bool>(request.Params, "expandChildren", false);
                        int maxDepth = GetParam<int>(request.Params, "maxDepth", 2);
                        int outputTailLines = GetParam<int>(request.Params, "outputTailLines", 80);
                        int timeoutMs = GetParam<int>(request.Params, "timeoutMs", 120000);
                        int pollIntervalMs = GetParam<int>(request.Params, "pollIntervalMs", 200);
                        bool includeLocals = GetParam<bool>(request.Params, "includeLocals", false);
                        bool includeModules = GetParam<bool>(request.Params, "includeModules", false);
                        bool includeRegisters = GetParam<bool>(request.Params, "includeRegisters", false);
                        bool includeOutput = GetParam<bool>(request.Params, "includeOutput", false);
                        bool includeThreads = GetParam<bool>(request.Params, "includeThreads", true);
                        bool includeException = GetParam<bool>(request.Params, "includeException", true);
                        bool allowSideEffects = GetParam<bool>(request.Params, "allowSideEffects", false);
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
                            allowSideEffects,
                            threadIncludeFrames,
                            threadMaxFramesPerThread,
                            threadMaxNodes,
                            threadBudgetMs);
                    }

                case "step_into":
                    return _debuggerService.StepInto();

                case "step_over":
                    return _debuggerService.StepOver();

                case "step_out":
                    return _debuggerService.StepOut();

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
                    return _debuggerService.GetBreakpoints();

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
                        return _debuggerService.DeleteBreakpoint(filePath, lineNumber);
                    }

                case "delete_all_breakpoints":
                    return _debuggerService.DeleteAllBreakpoints();

                case "enable_breakpoint":
                    {
                        string filePath = GetParam<string>(request.Params, "filePath", "");
                        int lineNumber = GetParam<int>(request.Params, "lineNumber", 0);
                        bool enabled = GetParam<bool>(request.Params, "enabled", true);
                        return _debuggerService.EnableBreakpoint(filePath, lineNumber, enabled);
                    }

                // Registers
                case "get_registers":
                    return _debuggerService.GetRegisters();

                // Watch
                case "add_watch":
                    {
                        string expression = GetParam<string>(request.Params, "expression", "");
                        return _debuggerService.AddWatch(expression);
                    }

                // Memory Search
                case "search_memory":
                    {
                        string startAddress = GetParam<string>(request.Params, "startAddress", "");
                        string pattern = GetParam<string>(request.Params, "pattern", "");
                        int maxResults = GetParam<int>(request.Params, "maxResults", 100);
                        int searchSize = GetParam<int>(request.Params, "searchSize", 0x10000);
                        string patternFormat = GetParam<string>(request.Params, "patternFormat", "auto");
                        long startOffset = GetParam<long>(request.Params, "startOffset", 0);
                        return _debuggerService.SearchMemory(startAddress, pattern, maxResults, searchSize, patternFormat, startOffset);
                    }

                // Stack Frame Navigation
                case "switch_stack_frame":
                    {
                        int frameIndex = GetParam<int>(request.Params, "frameIndex", 0);
                        return _debuggerService.SwitchStackFrame(frameIndex);
                    }

                case "switch_thread":
                    {
                        int threadId = GetParam<int>(request.Params, "threadId", 0);
                        return _debuggerService.SwitchThread(threadId);
                    }

                // Output Window
                case "get_output_window":
                    {
                        string pane = GetParam<string>(request.Params, "pane", "Debug");
                        int tailLines = GetParam<int>(request.Params, "tailLines", 50);
                        return _debuggerService.GetOutputWindow(pane, tailLines);
                    }

                case "search_output":
                    {
                        string pattern = GetParam<string>(request.Params, "pattern", "");
                        string pane = GetParam<string>(request.Params, "pane", "Debug");
                        bool isRegex = GetParam<bool>(request.Params, "isRegex", false);
                        int contextLines = GetParam<int>(request.Params, "contextLines", 2);
                        int maxMatches = GetParam<int>(request.Params, "maxMatches", 20);
                        return _debuggerService.SearchOutput(pattern, pane, isRegex, contextLines, maxMatches);
                    }

                default:
                    throw new NotSupportedException($"Unknown method: {request.Method}");
            }
        }

        private T GetParam<T>(Dictionary<string, object> parameters, string key, T defaultValue)
        {
            if (parameters == null || !parameters.ContainsKey(key))
            {
                return defaultValue;
            }

            try
            {
                object value = parameters[key];
                if (value == null)
                {
                    return defaultValue;
                }

                // Handle type conversions
                if (typeof(T) == typeof(int))
                {
                    return (T)(object)Convert.ToInt32(value);
                }
                if (typeof(T) == typeof(long))
                {
                    return (T)(object)Convert.ToInt64(value);
                }
                if (typeof(T) == typeof(bool))
                {
                    return (T)(object)Convert.ToBoolean(value);
                }
                if (typeof(T) == typeof(string))
                {
                    return (T)(object)value.ToString();
                }

                return (T)value;
            }
            catch
            {
                return defaultValue;
            }
        }

        private List<string> GetStringListParam(Dictionary<string, object> parameters, string key)
        {
            var result = new List<string>();
            if (parameters == null || !parameters.TryGetValue(key, out object value) || value == null)
            {
                return result;
            }

            if (value is string single)
            {
                if (!string.IsNullOrWhiteSpace(single))
                {
                    result.Add(single);
                }
                return result;
            }

            if (value is System.Collections.IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item == null)
                    {
                        continue;
                    }

                    string expression = item.ToString();
                    if (!string.IsNullOrWhiteSpace(expression))
                    {
                        result.Add(expression);
                    }
                }
            }

            return result;
        }

        private string SerializeResponse(DebugResponse response)
        {
            try
            {
                // Use DataContractJsonSerializer to respect DataMember(Name = "...") attributes
                // This ensures lowercase property names like "result" instead of "Result"
                var serializer = new DataContractJsonSerializer(typeof(DebugResponse));
                using (var ms = new MemoryStream())
                {
                    serializer.WriteObject(ms, response);
                    return Encoding.UTF8.GetString(ms.ToArray());
                }
            }
            catch (Exception ex)
            {
                _log($"Error serializing response: {ex.Message}");
                // Fallback to simple error JSON with proper escaping
                string safeId = EscapeJsonString(response?.Id);
                string safeMessage = EscapeJsonString(ex.Message);
                return $"{{\"id\":\"{safeId}\",\"error\":{{\"code\":\"SERIALIZE_ERROR\",\"message\":\"{safeMessage}\"}}}}";
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();
            _cts.Dispose();
        }

        private static bool IsStrictPipeSecurityEnabled()
        {
            var value = Environment.GetEnvironmentVariable("AIDB_PIPE_SECURITY");
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            return value.Equals("strict", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("same_user", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("owner_only", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Escapes a string for safe inclusion in a JSON string value.
        /// Handles quotes, backslashes, newlines, tabs, and other control characters.
        /// </summary>
        private static string EscapeJsonString(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value ?? "";

            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < ' ')
                            sb.AppendFormat("\\u{0:X4}", (int)c);
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
