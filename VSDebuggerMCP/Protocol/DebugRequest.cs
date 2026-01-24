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
        [DataMember(Name = "id")]
        public string Id { get; set; }

        [DataMember(Name = "method")]
        public string Method { get; set; }

        [DataMember(Name = "params")]
        public Dictionary<string, object> Params { get; set; } = new Dictionary<string, object>();
    }
}
