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

namespace VSDebuggerMCP
{
    /// <summary>
    /// Named Pipe server for MCP communication.
    /// Uses the authenticated AIDB/2 protocol over a length-prefixed named pipe.
    /// Each VS instance creates a unique pipe with its process ID.
    /// </summary>
    public partial class NamedPipeServer : IDisposable
    {
        private const string ProtocolVersion = "aidb/2";
        private const string PipeNamePrefix = "AIDebuggerVS";
        private const int DataPipeInstances = 8;
        private const int InterruptPipeInstances = 4;
        private const int RecoveryPipeInstances = 2;
        private const int MaxRequestMessageSize = 4 * 1024 * 1024;
        private const int MaxResponseMessageSize = 16 * 1024 * 1024;
        private const int MaxMethodNameLength = 128;
        private const int FrameIoTimeoutMs = 10000;
        private const int InitialFrameIoTimeoutMs = 2000;
        private const int MaxExpressionCount = 100;
        private const int MaxExpressionLength = 16 * 1024;
        private const int MaxCachedCommandResults = 512;
        private const int MaxQueuedDebuggerRequests = 128;
        private static readonly TimeSpan CachedCommandResultTtl = TimeSpan.FromMinutes(10);
        
        // Unique pipe name for this VS instance (includes process ID)
        private readonly string _pipeName;
        private readonly string _controlPipeName;
        private readonly string _recoveryPipeName;
        private readonly int _processId;
        private readonly long _processStartTimeUtcTicks;
        private readonly string _serverInstanceId;
        private readonly InstanceRegistration _instanceRegistration;

        private readonly DebuggerService _debuggerService;
        private readonly Action<string> _log;
        private readonly CancellationTokenSource _cts;
        private readonly JavaScriptSerializer _serializer;
        private readonly List<Task> _connectionTasks;
        private readonly DebugSessionActor _debugSessionActor;
        private readonly LongRunningDebugOperationGate _longRunningOperationGate;
        private readonly SemaphoreSlim _inspectionDispatchGate;
        private readonly object _activeRequestsLock = new object();
        private readonly CommandResultStore _commandResults;
        private readonly Dictionary<string, ActiveRequest> _activeRequests;
        private readonly HashSet<string> _allowedCapabilities;
        private volatile bool _isRunning;
        private volatile bool _disposed;
        private int _transportResourcesDisposed;
        private readonly object _lifetimeLock = new object();
        private Task _stopTask;

        /// <summary>
        /// Gets the unique pipe name for this VS instance.
        /// </summary>
        public string PipeName => _pipeName;
        public IReadOnlyCollection<string> AllowedCapabilities =>
            _allowedCapabilities.OrderBy(value => value, StringComparer.Ordinal).ToArray();

        public NamedPipeServer(
            DebuggerService debuggerService,
            Action<string> log,
            IEnumerable<string> requestedCapabilities = null)
        {
            _debuggerService = debuggerService ?? throw new ArgumentNullException(nameof(debuggerService));
            _log = log ?? ((s) => { });
            _cts = new CancellationTokenSource();
            _serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            _connectionTasks = new List<Task>();
            _debugSessionActor = new DebugSessionActor(MaxQueuedDebuggerRequests);
            _longRunningOperationGate = new LongRunningDebugOperationGate();
            _inspectionDispatchGate = new SemaphoreSlim(2, 2);
            _commandResults = new CommandResultStore(
                MaxCachedCommandResults,
                CachedCommandResultTtl);
            _activeRequests = new Dictionary<string, ActiveRequest>(StringComparer.Ordinal);
            _allowedCapabilities = ResolveAllowedCapabilities(requestedCapabilities);
            
            // Create unique pipe name with process ID
            using (var process = System.Diagnostics.Process.GetCurrentProcess())
            {
                _processId = process.Id;
                _processStartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
            }
            _pipeName = $"{PipeNamePrefix}_{_processId}";
            _controlPipeName = _pipeName + "_control";
            _recoveryPipeName = _pipeName + "_recovery";
            _serverInstanceId = Guid.NewGuid().ToString("N");
            _instanceRegistration = new InstanceRegistration(
                ProtocolVersion,
                _pipeName,
                _controlPipeName,
                _recoveryPipeName,
                _processId,
                _processStartTimeUtcTicks,
                _serverInstanceId,
                _allowedCapabilities.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                _log);
        }

        private static bool IsClientLaneAlive(string clientLane)
        {
            string[] parts = (clientLane ?? string.Empty).Split(':');
            if (parts.Length != 2 ||
                !int.TryParse(parts[0], out int processId) || processId <= 0 ||
                !long.TryParse(parts[1], out long expectedStartTicks) || expectedStartTicks <= 0)
                return true;
            try
            {
                using (var process = System.Diagnostics.Process.GetProcessById(processId))
                {
                    return !process.HasExited &&
                        process.StartTime.ToUniversalTime().Ticks == expectedStartTicks;
                }
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch { return true; }
        }

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;

            _log($"Starting Named Pipe server on \\\\.\\pipe\\{_pipeName}");
            _log("Named Pipe security: current-user owner only.");

            StartListenerPool(_pipeName, false, false, DataPipeInstances);
            // Interrupts may touch the debugger/UI and therefore use their own pool.
            StartListenerPool(_controlPipeName, true, false, InterruptPipeInstances);
            // Cancellation, request-status and health never share listener or client
            // worker capacity with an interrupt that can block in the debugger.
            StartListenerPool(_recoveryPipeName, true, true, RecoveryPipeInstances);
        }

        private void StartListenerPool(
            string pipeName,
            bool controlOnly,
            bool recoveryOnly,
            int instanceCount)
        {
            for (int index = 0; index < instanceCount; index++)
            {
                _connectionTasks.Add(Task.Run(() => ListenForConnectionsAsync(
                    pipeName,
                    controlOnly,
                    recoveryOnly,
                    instanceCount,
                    _cts.Token)));
            }
        }

        public void Stop()
        {
            _ = StopAsync();
        }

        public Task StopAsync()
        {
            lock (_lifetimeLock)
            {
                if (_stopTask != null)
                {
                    return _stopTask;
                }

                _isRunning = false;
                _log("Stopping Named Pipe server...");
                _cts.Cancel();
                _instanceRegistration.Dispose();
                ActiveRequest[] active;
                lock (_activeRequestsLock)
                {
                    active = _activeRequests.Values.ToArray();
                }
                foreach (ActiveRequest request in active)
                {
                    request.TryCancel();
                }
                _debugSessionActor.Stop();
                _stopTask = CompleteStopAsync(_connectionTasks.ToArray());
                return _stopTask;
            }
        }

        private async Task CompleteStopAsync(Task[] workers)
        {
            try
            {
                await Task.WhenAll(workers);
            }
            catch (OperationCanceledException)
            {
                // Expected during normal shutdown.
            }
            finally
            {
                try
                {
                    await _debugSessionActor.Completion;
                }
                catch (OperationCanceledException)
                {
                    // Expected during shutdown.
                }
                DisposeTransportResources();
            }
        }

        private void DisposeTransportResources()
        {
            if (Interlocked.Exchange(ref _transportResourcesDisposed, 1) != 0)
            {
                return;
            }

            _debugSessionActor.Dispose();
            _inspectionDispatchGate.Dispose();
            _cts.Dispose();
            _log("Named Pipe server stopped.");
        }

        private async Task ListenForConnectionsAsync(
            string pipeName,
            bool controlOnly,
            bool recoveryOnly,
            int maxConnections,
            CancellationToken cancellationToken)
        {
            int consecutiveFailures = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream pipeServer = null;
                int retryDelayMs = 0;

                try
                {
                    var pipeSecurity = PipeSecurityFactory.Create();

                    pipeServer = new NamedPipeServerStream(
                        pipeName,
                        PipeDirection.InOut,
                        maxConnections,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        inBufferSize: 4096,
                        outBufferSize: 4096,
                        pipeSecurity: pipeSecurity);

                    _log("Waiting for client connection...");

                    // Wait for connection with cancellation support
                    await WaitForConnectionAsync(pipeServer, cancellationToken);
                    consecutiveFailures = 0;

                    if (cancellationToken.IsCancellationRequested)
                    {
                        pipeServer.Dispose();
                        break;
                    }

                    _log("Client connected.");

                    // Handle the connection
                    await HandleConnectionAsync(pipeServer, controlOnly, recoveryOnly, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // Expected when stopping
                    break;
                }
                catch (IOException ex)
                {
                    _log($"Pipe listener IO error: {ex.GetType().Name}");
                    retryDelayMs = ComputeListenerRetryDelay(++consecutiveFailures);
                }
                catch (Exception ex)
                {
                    _log($"Pipe listener error: {ex.GetType().Name}");
                    retryDelayMs = ComputeListenerRetryDelay(++consecutiveFailures);
                }
                finally
                {
                    pipeServer?.Dispose();
                }
                if (retryDelayMs > 0)
                {
                    try
                    {
                        await Task.Delay(retryDelayMs, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        private static int ComputeListenerRetryDelay(int consecutiveFailures)
        {
            int exponent = Math.Min(Math.Max(0, consecutiveFailures - 1), 6);
            return Math.Min(2000, 25 << exponent);
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

        [SuppressMessage(
            "Usage",
            "VSTHRD010",
            Justification = "ProcessRequest invokes only self-marshalling DebuggerService entrypoints; pipe framing remains off the UI thread.")]
        private async Task HandleConnectionAsync(
            NamedPipeServerStream pipeServer,
            bool controlOnly,
            bool recoveryOnly,
            CancellationToken cancellationToken)
        {
            try
            {
                var codec = new LengthPrefixedFrameCodec(pipeServer);
                EnsureLocalPipeClient(pipeServer);
                int clientProcessId = GetNamedPipeClientProcessIdVerified(pipeServer);

                // One connection carries exactly one request/response. With one
                // server instance per public name this prevents an unauthenticated
                // idle client (or a client retaining a post-response handle) from
                // monopolizing the data/control lane indefinitely.
                string requestLine;
                using (var frameReadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    frameReadCts.CancelAfter(InitialFrameIoTimeoutMs);
                    requestLine = await codec.ReadFrameAsync(MaxRequestMessageSize, frameReadCts.Token);
                }
                if (requestLine == null) return;

                _log($"Received request ({Encoding.UTF8.GetByteCount(requestLine)} bytes).");

                string response = await ProcessRequestAsync(
                    requestLine,
                    clientProcessId,
                    controlOnly,
                    recoveryOnly);

                using (var frameWriteCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    frameWriteCts.CancelAfter(FrameIoTimeoutMs);
                    await codec.WriteFrameAsync(response, MaxResponseMessageSize, frameWriteCts.Token);
                }
                _log($"Sent response ({Encoding.UTF8.GetByteCount(response)} bytes).");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _log("Client frame IO exceeded the 10-second transport deadline.");
            }
            catch (IOException)
            {
                // Client disconnected
                _log("Client disconnected.");
            }
            catch (Exception ex)
            {
                _log($"Connection handler error: {ex.GetType().Name}");
            }
        }


        private string SerializeResponse(
            DebugResponse response,
            DebugServerIdentity serverIdentity = null,
            DebugRequest completedRequest = null)
        {
            DebugServerIdentity effectiveIdentity = serverIdentity ?? CreateServerIdentity(
                _debuggerService.CaptureCurrentDebuggerIdentity());
            try
            {
                response.Protocol = ProtocolVersion;
                response.Status = response.Error == null ? "ok" : "error";
                response.Server = effectiveIdentity;

                string serialized = DebugResponseJsonSerializer.Serialize(response);
                if (completedRequest != null)
                {
                    return EnforceMethodResultLimit(
                        completedRequest,
                        serialized,
                        effectiveIdentity);
                }
                if (Encoding.UTF8.GetByteCount(serialized) <= MaxResponseMessageSize)
                {
                    return serialized;
                }

                if (response?.Error?.Code == "RESOURCE_LIMIT_EXCEEDED")
                {
                    return serialized;
                }

                return SerializeResponse(DebugResponse.Failure(
                    response?.Id,
                    "RESOURCE_LIMIT_EXCEEDED",
                    $"Response exceeds maximum size of {MaxResponseMessageSize} bytes"),
                    effectiveIdentity);
            }
            catch (Exception ex)
            {
                _log($"Error serializing response: {ex.GetType().Name}");
                if (completedRequest != null &&
                    string.Equals(GetMethodKind(completedRequest.Method), "command", StringComparison.Ordinal))
                {
                    var completedFailure = DebugResponse.Failure(
                        completedRequest.Id,
                        "RESULT_SERIALIZATION_FAILED_AFTER_COMMAND",
                        $"Command '{completedRequest.Method}' completed, but its result could not be serialized. The command was not replayed.");
                    completedFailure.Error.RequestId = completedRequest.Id;
                    completedFailure.Error.Details = new Dictionary<string, object>
                    {
                        ["commandCompleted"] = true,
                        ["method"] = completedRequest.Method,
                    };
                    return SerializeResponse(completedFailure, effectiveIdentity);
                }
                string safeId = EscapeJsonString(response?.Id);
                return "{" +
                    $"\"protocol\":\"{ProtocolVersion}\"," +
                    $"\"id\":\"{safeId}\"," +
                    "\"status\":\"error\"," +
                    "\"server\":{" +
                    $"\"processId\":{_processId}," +
                    $"\"processStartTimeUtcTicks\":{_processStartTimeUtcTicks}," +
                    $"\"serverInstanceId\":\"{EscapeJsonString(_serverInstanceId)}\"," +
                    $"\"debugSessionGeneration\":{effectiveIdentity.DebugSessionGeneration}," +
                    $"\"breakSequence\":{effectiveIdentity.BreakSequence}," +
                    $"\"contextEpoch\":{effectiveIdentity.ContextEpoch}" +
                    "}," +
                    "\"error\":{\"code\":\"SERIALIZE_ERROR\",\"message\":\"Response serialization failed; see the Visual Studio Activity Log.\"}}";
            }
        }

        private DebugServerIdentity CreateServerIdentity(DebuggerIdentitySnapshot debuggerIdentity)
        {
            return new DebugServerIdentity
            {
                ProcessId = _processId,
                ProcessStartTimeUtcTicks = _processStartTimeUtcTicks,
                ServerInstanceId = _serverInstanceId,
                DebugSessionGeneration = debuggerIdentity?.DebugSessionGeneration ?? 0,
                BreakSequence = debuggerIdentity?.BreakSequence ?? 0,
                ContextEpoch = debuggerIdentity?.ContextEpoch ?? 0,
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();
            _instanceRegistration.Dispose();
            if (!_isRunning && _connectionTasks.Count == 0)
            {
                DisposeTransportResources();
            }
        }

        private sealed class ProtocolRequestException : Exception
        {
            public ProtocolRequestException(string code, string message) : base(message)
            {
                Code = code;
            }

            public string Code { get; }
        }

        private static int GetNamedPipeClientProcessIdVerified(NamedPipeServerStream pipeServer)
        {
            if (pipeServer == null || !pipeServer.IsConnected ||
                !GetNamedPipeClientProcessId(pipeServer.SafePipeHandle, out uint processId) ||
                processId == 0 || processId > int.MaxValue)
            {
                throw new IOException("Unable to identify the named-pipe client process");
            }
            return (int)processId;
        }

        private static void EnsureLocalPipeClient(NamedPipeServerStream pipeServer)
        {
            if (pipeServer == null || !pipeServer.IsConnected)
            {
                throw new IOException("Named-pipe client is not connected");
            }
            var computerName = new StringBuilder(256);
            if (!GetNamedPipeClientComputerName(
                pipeServer.SafePipeHandle,
                computerName,
                (uint)computerName.Capacity))
            {
                throw new UnauthorizedAccessException("Unable to verify that the named-pipe client is local");
            }
            string actual = computerName.ToString().Trim().TrimStart('\\');
            if (!string.Equals(actual, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException("Remote named-pipe clients are not permitted");
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeClientProcessId(
            SafePipeHandle pipe,
            out uint clientProcessId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeClientComputerName(
            SafePipeHandle pipe,
            StringBuilder clientComputerName,
            uint clientComputerNameLength);

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
