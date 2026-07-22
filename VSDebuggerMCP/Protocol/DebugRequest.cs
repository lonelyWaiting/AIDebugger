using System.Collections.Generic;
using System.Runtime.Serialization;

namespace VSDebuggerMCP.Protocol
{
    /// <summary>
    /// Request message for debugger operations over Named Pipe.
    /// </summary>
    [DataContract]
    public class DebugRequest
    {
        [DataMember(Name = "protocol")]
        public string Protocol { get; set; }

        [DataMember(Name = "id")]
        public string Id { get; set; }

        [DataMember(Name = "method")]
        public string Method { get; set; }

        [DataMember(Name = "params")]
        public Dictionary<string, object> Params { get; set; } = new Dictionary<string, object>();

        [DataMember(Name = "meta")]
        public DebugRequestMetadata Meta { get; set; }
    }

    [DataContract]
    public class DebugRequestMetadata
    {
        [DataMember(Name = "authToken")]
        public string AuthToken { get; set; }

        [DataMember(Name = "deadlineUnixMs")]
        public long DeadlineUnixMs { get; set; }

        [DataMember(Name = "kind")]
        public string Kind { get; set; }

        [DataMember(Name = "capability")]
        public string Capability { get; set; }

        [DataMember(Name = "clientProcessId")]
        public int ClientProcessId { get; set; }

        [DataMember(Name = "clientProcessStartTimeUtcTicks")]
        public long ClientProcessStartTimeUtcTicks { get; set; }

        [DataMember(Name = "target")]
        public DebugTargetIdentity Target { get; set; }
    }

    [DataContract]
    public class DebugTargetIdentity
    {
        [DataMember(Name = "processId")]
        public int ProcessId { get; set; }

        [DataMember(Name = "processStartTimeUtcTicks")]
        public long ProcessStartTimeUtcTicks { get; set; }

        [DataMember(Name = "serverInstanceId")]
        public string ServerInstanceId { get; set; }

        [DataMember(Name = "debugSessionGeneration")]
        public long DebugSessionGeneration { get; set; }

        [DataMember(Name = "breakSequence")]
        public long BreakSequence { get; set; }

        [DataMember(Name = "contextEpoch")]
        public long ContextEpoch { get; set; }
    }
}
