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
    [KnownType(typeof(BreakpointsResult))]
    [KnownType(typeof(BreakpointOperationResult))]
    [KnownType(typeof(RegistersResult))]
    [KnownType(typeof(WatchResult))]
    [KnownType(typeof(MemorySearchResult))]
    [KnownType(typeof(OutputWindowResult))]
    [KnownType(typeof(SearchOutputResult))]
    public class DebugResponse
    {
        [DataMember(Name = "id")]
        public string Id { get; set; }

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
    public class ErrorInfo
    {
        [DataMember(Name = "code")]
        public string Code { get; set; }

        [DataMember(Name = "message")]
        public string Message { get; set; }
    }
}
