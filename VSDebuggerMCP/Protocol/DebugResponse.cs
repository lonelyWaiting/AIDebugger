using System.Collections.Generic;
using System.Runtime.Serialization;

namespace VSDebuggerMCP.Protocol
{
    /// <summary>
    /// Response message for debugger operations over Named Pipe.
    /// </summary>
    [DataContract]
    [KnownType(typeof(DebugStatusResult))]
    [KnownType(typeof(CallStackResult))]
    [KnownType(typeof(LocalsResult))]
    [KnownType(typeof(ThreadsResult))]
    [KnownType(typeof(ModulesResult))]
    [KnownType(typeof(ExceptionResult))]
    [KnownType(typeof(EvaluateResult))]
    [KnownType(typeof(MemoryResult))]
    [KnownType(typeof(DisassemblyResult))]
    [KnownType(typeof(BuildErrorsResult))]
    [KnownType(typeof(FullContextResult))]
    [KnownType(typeof(PingResult))]
    [KnownType(typeof(InstanceInfoResult))]
    [KnownType(typeof(DebugControlResult))]
    [KnownType(typeof(ProcessDebugResult))]
    [KnownType(typeof(DebugProcessesResult))]
    [KnownType(typeof(BreakpointsResult))]
    [KnownType(typeof(BreakpointOperationResult))]
    [KnownType(typeof(RegistersResult))]
    [KnownType(typeof(WatchResult))]
    [KnownType(typeof(MemorySearchResult))]
    [KnownType(typeof(NativeAddressInfo))]
    [KnownType(typeof(NativeFunctionInfo))]
    [KnownType(typeof(ModuleLayoutResult))]
    [KnownType(typeof(ModuleSectionInfo))]
    [KnownType(typeof(ImportedModuleInfo))]
    [KnownType(typeof(ImportFunctionInfo))]
    [KnownType(typeof(ExportInfo))]
    [KnownType(typeof(NativeContextResult))]
    [KnownType(typeof(NativeContinueResult))]
    [KnownType(typeof(ManagedAssemblyResult))]
    [KnownType(typeof(ManagedTypeResult))]
    [KnownType(typeof(ManagedTypeInfo))]
    [KnownType(typeof(ManagedMethodInfo))]
    [KnownType(typeof(ManagedParameterInfo))]
    [KnownType(typeof(ManagedLocalInfo))]
    [KnownType(typeof(ManagedMethodIlResult))]
    [KnownType(typeof(ManagedIlInstructionInfo))]
    [KnownType(typeof(HybridContextResult))]
    [KnownType(typeof(BreakpointCaptureResult))]
    [KnownType(typeof(BreakpointHitInfo))]
    [KnownType(typeof(OutputWindowResult))]
    [KnownType(typeof(SearchOutputResult))]
    [KnownType(typeof(BatchEvaluateResult))]
    [KnownType(typeof(DebugBundleResult))]
    [KnownType(typeof(ContinueAndWaitResult))]
    [KnownType(typeof(CommandRequestStatusResult))]
    [KnownType(typeof(DispatcherHealthResult))]
    public class DebugResponse
    {
        [DataMember(Name = "protocol")]
        public string Protocol { get; set; }

        [DataMember(Name = "id")]
        public string Id { get; set; }

        [DataMember(Name = "status")]
        public string Status { get; set; }

        [DataMember(Name = "server")]
        public DebugServerIdentity Server { get; set; }

        [DataMember(Name = "result")]
        public object Result { get; set; }

        [DataMember(Name = "error")]
        public ErrorInfo Error { get; set; }

        public static DebugResponse Success(string id, object result)
        {
            return new DebugResponse { Id = id, Result = result };
        }

        public static DebugResponse Failure(string id, string code, string message)
        {
            return new DebugResponse
            {
                Id = id,
                Error = new ErrorInfo { Code = code, Message = message }
            };
        }
    }

    [DataContract]
    public class DebugServerIdentity
    {
        [DataMember(Name = "processId")]
        public int ProcessId { get; set; }

        [DataMember(Name = "processStartTimeUtcTicks")]
        public long ProcessStartTimeUtcTicks { get; set; }

        [DataMember(Name = "serverInstanceId")]
        public string ServerInstanceId { get; set; }

        [DataMember(Name = "protocolCatalogHash")]
        public string ProtocolCatalogHash { get; set; }

        [DataMember(Name = "debugSessionGeneration")]
        public long DebugSessionGeneration { get; set; }

        [DataMember(Name = "breakSequence")]
        public long BreakSequence { get; set; }

        [DataMember(Name = "contextEpoch")]
        public long ContextEpoch { get; set; }
    }

    [DataContract]
    public class ErrorInfo
    {
        [DataMember(Name = "code")]
        public string Code { get; set; }

        [DataMember(Name = "message")]
        public string Message { get; set; }

        [DataMember(Name = "outcomeUnknown", EmitDefaultValue = false)]
        public bool OutcomeUnknown { get; set; }

        [DataMember(Name = "requestId", EmitDefaultValue = false)]
        public string RequestId { get; set; }

        [DataMember(Name = "details", EmitDefaultValue = false)]
        public Dictionary<string, object> Details { get; set; }
    }
}
