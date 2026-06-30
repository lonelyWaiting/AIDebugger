using System.Collections.Generic;
using System.Runtime.Serialization;

namespace VSDebuggerMCP.Protocol
{
    #region Debug Status

    [DataContract]
    public class DebugStatusResult
    {
        [DataMember(Name = "isDebugging")]
        public bool IsDebugging { get; set; }

        [DataMember(Name = "isPaused")]
        public bool IsPaused { get; set; }

        [DataMember(Name = "debugMode")]
        public string DebugMode { get; set; }

        [DataMember(Name = "currentProcess")]
        public string CurrentProcess { get; set; }

        [DataMember(Name = "stopReason")]
        public string StopReason { get; set; } = "not_paused";

        [DataMember(Name = "stopReasonRaw")]
        public string StopReasonRaw { get; set; }

        [DataMember(Name = "currentLocation")]
        public LocationInfo CurrentLocation { get; set; }
    }

    [DataContract]
    public class LocationInfo
    {
        [DataMember(Name = "filePath")]
        public string FilePath { get; set; }

        [DataMember(Name = "functionName")]
        public string FunctionName { get; set; }

        [DataMember(Name = "lineNumber")]
        public int LineNumber { get; set; }

        [DataMember(Name = "columnNumber")]
        public int ColumnNumber { get; set; }
    }

    #endregion

    #region Call Stack

    [DataContract]
    public class CallStackResult
    {
        [DataMember(Name = "threadId")]
        public int ThreadId { get; set; }

        [DataMember(Name = "threadName")]
        public string ThreadName { get; set; }

        [DataMember(Name = "capability")]
        public string Capability { get; set; }

        [DataMember(Name = "snapshotStable")]
        public bool SnapshotStable { get; set; }

        [DataMember(Name = "snapshotAttempts")]
        public int SnapshotAttempts { get; set; }

        [DataMember(Name = "snapshotStatus")]
        public string SnapshotStatus { get; set; } = "unavailable";

        [DataMember(Name = "frames")]
        public List<StackFrameInfo> Frames { get; set; } = new List<StackFrameInfo>();
    }

    [DataContract]
    public class StackFrameInfo
    {
        [DataMember(Name = "index")]
        public int Index { get; set; }

        [DataMember(Name = "functionName")]
        public string FunctionName { get; set; }

        [DataMember(Name = "filePath")]
        public string FilePath { get; set; }

        [DataMember(Name = "lineNumber")]
        public int LineNumber { get; set; }

        /// <summary>
        /// Indicates whether the line number is reliable.
        /// Line numbers are only reliable for the current stack frame.
        /// </summary>
        [DataMember(Name = "lineNumberReliable")]
        public bool LineNumberReliable { get; set; }

        [DataMember(Name = "columnNumber")]
        public int ColumnNumber { get; set; }

        [DataMember(Name = "module")]
        public string Module { get; set; }

        [DataMember(Name = "language")]
        public string Language { get; set; }

        [DataMember(Name = "returnType")]
        public string ReturnType { get; set; }

        [DataMember(Name = "arguments")]
        public List<VariableInfo> Arguments { get; set; }
    }

    #endregion

    #region Locals

    [DataContract]
    public class LocalsResult
    {
        [DataMember(Name = "frameIndex")]
        public int FrameIndex { get; set; }

        [DataMember(Name = "variables")]
        public List<VariableInfo> Variables { get; set; } = new List<VariableInfo>();

        [DataMember(Name = "partial")]
        public bool Partial { get; set; }

        [DataMember(Name = "error")]
        public string Error { get; set; }
    }

    [DataContract]
    public class VariableInfo
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "type")]
        public string Type { get; set; }

        [DataMember(Name = "value")]
        public string Value { get; set; }

        [DataMember(Name = "hasChildren")]
        public bool HasChildren { get; set; }

        [DataMember(Name = "children")]
        public List<VariableInfo> Children { get; set; }

        [DataMember(Name = "isReadOnly")]
        public bool IsReadOnly { get; set; }
    }

    #endregion

    #region Threads

    [DataContract]
    public class ThreadsResult
    {
        [DataMember(Name = "currentThreadId")]
        public int CurrentThreadId { get; set; }

        [DataMember(Name = "capability")]
        public string Capability { get; set; }

        [DataMember(Name = "snapshotStable")]
        public bool SnapshotStable { get; set; }

        [DataMember(Name = "snapshotAttempts")]
        public int SnapshotAttempts { get; set; }

        [DataMember(Name = "snapshotStatus")]
        public string SnapshotStatus { get; set; } = "unavailable";

        [DataMember(Name = "partial")]
        public bool Partial { get; set; }

        [DataMember(Name = "error")]
        public string Error { get; set; }

        [DataMember(Name = "threads")]
        public List<ThreadInfo> Threads { get; set; } = new List<ThreadInfo>();
    }

    [DataContract]
    public class ThreadInfo
    {
        [DataMember(Name = "id")]
        public int Id { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "isCurrent")]
        public bool IsCurrent { get; set; }

        [DataMember(Name = "isFrozen")]
        public bool IsFrozen { get; set; }

        [DataMember(Name = "location")]
        public string Location { get; set; }

        [DataMember(Name = "priority")]
        public string Priority { get; set; }

        [DataMember(Name = "suspendCount")]
        public int SuspendCount { get; set; }

        [DataMember(Name = "frames")]
        public List<StackFrameInfo> Frames { get; set; }
    }

    #endregion

    #region Modules

    [DataContract]
    public class ModulesResult
    {
        [DataMember(Name = "modules")]
        public List<ModuleInfo> Modules { get; set; } = new List<ModuleInfo>();

        [DataMember(Name = "totalCount")]
        public int TotalCount { get; set; }

        [DataMember(Name = "capability")]
        public string Capability { get; set; }

        [DataMember(Name = "error")]
        public string Error { get; set; }
    }

    [DataContract]
    public class ModuleInfo
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "path")]
        public string Path { get; set; }

        [DataMember(Name = "symbolsLoaded")]
        public bool SymbolsLoaded { get; set; }

        [DataMember(Name = "symbolPath")]
        public string SymbolPath { get; set; }

        [DataMember(Name = "order")]
        public int Order { get; set; }

        [DataMember(Name = "version")]
        public string Version { get; set; }

        [DataMember(Name = "timestamp")]
        public string Timestamp { get; set; }

        [DataMember(Name = "address")]
        public string Address { get; set; }

        [DataMember(Name = "size")]
        public string Size { get; set; }

        [DataMember(Name = "sizeBytes")]
        public ulong SizeBytes { get; set; }

        [DataMember(Name = "is64Bit")]
        public bool Is64Bit { get; set; }

        [DataMember(Name = "isSystemModule")]
        public bool IsSystemModule { get; set; }
    }

    #endregion

    #region Native Debugging

    [DataContract]
    public class NativeAddressInfo
    {
        [DataMember(Name = "expression")]
        public string Expression { get; set; }

        [DataMember(Name = "status")]
        public string Status { get; set; } = "unavailable";

        [DataMember(Name = "virtualAddress")]
        public string VirtualAddress { get; set; }

        [DataMember(Name = "addressValue")]
        public ulong AddressValue { get; set; }

        [DataMember(Name = "moduleName")]
        public string ModuleName { get; set; }

        [DataMember(Name = "modulePath")]
        public string ModulePath { get; set; }

        [DataMember(Name = "moduleBase")]
        public string ModuleBase { get; set; }

        [DataMember(Name = "moduleBaseValue")]
        public ulong ModuleBaseValue { get; set; }

        [DataMember(Name = "moduleSizeBytes")]
        public ulong ModuleSizeBytes { get; set; }

        [DataMember(Name = "rva")]
        public string Rva { get; set; }

        [DataMember(Name = "rvaValue")]
        public ulong RvaValue { get; set; }

        [DataMember(Name = "sectionName")]
        public string SectionName { get; set; }

        [DataMember(Name = "sectionRva")]
        public string SectionRva { get; set; }

        [DataMember(Name = "sectionOffset")]
        public string SectionOffset { get; set; }

        [DataMember(Name = "nearestSymbol")]
        public string NearestSymbol { get; set; }

        [DataMember(Name = "function")]
        public NativeFunctionInfo Function { get; set; }

        [DataMember(Name = "functionStatus")]
        public string FunctionStatus { get; set; } = "missing";

        [DataMember(Name = "functionError")]
        public string FunctionError { get; set; }

        [DataMember(Name = "error")]
        public string Error { get; set; }
    }

    [DataContract]
    public class NativeFunctionInfo
    {
        [DataMember(Name = "startAddress")]
        public string StartAddress { get; set; }

        [DataMember(Name = "startAddressValue")]
        public ulong StartAddressValue { get; set; }

        [DataMember(Name = "startRva")]
        public string StartRva { get; set; }

        [DataMember(Name = "endAddress")]
        public string EndAddress { get; set; }

        [DataMember(Name = "endAddressValue")]
        public ulong EndAddressValue { get; set; }

        [DataMember(Name = "endRva")]
        public string EndRva { get; set; }

        [DataMember(Name = "sizeBytes")]
        public ulong SizeBytes { get; set; }

        [DataMember(Name = "addressOffset")]
        public string AddressOffset { get; set; }

        [DataMember(Name = "nearestSymbol")]
        public string NearestSymbol { get; set; }

        [DataMember(Name = "confidence")]
        public string Confidence { get; set; }

        [DataMember(Name = "reason")]
        public string Reason { get; set; }
    }

    [DataContract]
    public class ModuleLayoutResult
    {
        [DataMember(Name = "module")]
        public ModuleInfo Module { get; set; }

        [DataMember(Name = "status")]
        public string Status { get; set; } = "unavailable";

        [DataMember(Name = "is64Bit")]
        public bool Is64Bit { get; set; }

        [DataMember(Name = "entryPointRva")]
        public string EntryPointRva { get; set; }

        [DataMember(Name = "entryPointVa")]
        public string EntryPointVa { get; set; }

        [DataMember(Name = "imageBase")]
        public string ImageBase { get; set; }

        [DataMember(Name = "imageSizeBytes")]
        public ulong ImageSizeBytes { get; set; }

        [DataMember(Name = "sections")]
        public List<ModuleSectionInfo> Sections { get; set; } = new List<ModuleSectionInfo>();

        [DataMember(Name = "imports")]
        public List<ImportedModuleInfo> Imports { get; set; } = new List<ImportedModuleInfo>();

        [DataMember(Name = "exports")]
        public List<ExportInfo> Exports { get; set; } = new List<ExportInfo>();

        [DataMember(Name = "truncated")]
        public bool Truncated { get; set; }

        [DataMember(Name = "error")]
        public string Error { get; set; }
    }

    [DataContract]
    public class ModuleSectionInfo
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "rva")]
        public string Rva { get; set; }

        [DataMember(Name = "virtualAddress")]
        public string VirtualAddress { get; set; }

        [DataMember(Name = "virtualSize")]
        public ulong VirtualSize { get; set; }

        [DataMember(Name = "rawSize")]
        public ulong RawSize { get; set; }

        [DataMember(Name = "rawPointer")]
        public string RawPointer { get; set; }

        [DataMember(Name = "characteristics")]
        public string Characteristics { get; set; }
    }

    [DataContract]
    public class ImportedModuleInfo
    {
        [DataMember(Name = "moduleName")]
        public string ModuleName { get; set; }

        [DataMember(Name = "functions")]
        public List<ImportFunctionInfo> Functions { get; set; } = new List<ImportFunctionInfo>();

        [DataMember(Name = "truncated")]
        public bool Truncated { get; set; }
    }

    [DataContract]
    public class ImportFunctionInfo
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "ordinal")]
        public ulong Ordinal { get; set; }

        [DataMember(Name = "thunkRva")]
        public string ThunkRva { get; set; }
    }

    [DataContract]
    public class ExportInfo
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "ordinal")]
        public int Ordinal { get; set; }

        [DataMember(Name = "rva")]
        public string Rva { get; set; }

        [DataMember(Name = "virtualAddress")]
        public string VirtualAddress { get; set; }
    }

    [DataContract]
    public class NativeContextResult
    {
        [DataMember(Name = "snapshotId")]
        public string SnapshotId { get; set; }

        [DataMember(Name = "capturedAt")]
        public string CapturedAt { get; set; }

        [DataMember(Name = "status")]
        public DebugStatusResult Status { get; set; }

        [DataMember(Name = "currentAddress")]
        public NativeAddressInfo CurrentAddress { get; set; }

        [DataMember(Name = "stackPointer")]
        public NativeAddressInfo StackPointer { get; set; }

        [DataMember(Name = "registers")]
        public RegistersResult Registers { get; set; }

        [DataMember(Name = "callstack")]
        public CallStackResult Callstack { get; set; }

        [DataMember(Name = "disassembly")]
        public DisassemblyResult Disassembly { get; set; }

        [DataMember(Name = "stackMemory")]
        public MemoryResult StackMemory { get; set; }

        [DataMember(Name = "modules")]
        public ModulesResult Modules { get; set; }

        [DataMember(Name = "partial")]
        public bool Partial { get; set; }

        [DataMember(Name = "error")]
        public string Error { get; set; }
    }

    [DataContract]
    public class NativeContinueResult
    {
        [DataMember(Name = "action")]
        public string Action { get; set; } = "ContinueAndCaptureNative";

        [DataMember(Name = "success")]
        public bool Success { get; set; }

        [DataMember(Name = "message")]
        public string Message { get; set; }

        [DataMember(Name = "waitTimeoutMs")]
        public int WaitTimeoutMs { get; set; }

        [DataMember(Name = "pollIntervalMs")]
        public int PollIntervalMs { get; set; }

        [DataMember(Name = "waitedMs")]
        public int WaitedMs { get; set; }

        [DataMember(Name = "issuedContinue")]
        public bool IssuedContinue { get; set; }

        [DataMember(Name = "timedOut")]
        public bool TimedOut { get; set; }

        [DataMember(Name = "breakDetected")]
        public bool BreakDetected { get; set; }

        [DataMember(Name = "status")]
        public DebugStatusResult Status { get; set; }

        [DataMember(Name = "context")]
        public NativeContextResult Context { get; set; }
    }

    [DataContract]
    public class ManagedAssemblyResult
    {
        [DataMember(Name = "capability")]
        public string Capability { get; set; } = "managed_metadata_and_il_read_only";

        [DataMember(Name = "assemblyPath")]
        public string AssemblyPath { get; set; }

        [DataMember(Name = "assemblyFullName")]
        public string AssemblyFullName { get; set; }

        [DataMember(Name = "moduleVersionId")]
        public string ModuleVersionId { get; set; }

        [DataMember(Name = "typeFilter")]
        public string TypeFilter { get; set; }

        [DataMember(Name = "totalTypeCount")]
        public int TotalTypeCount { get; set; }

        [DataMember(Name = "matchedTypeCount")]
        public int MatchedTypeCount { get; set; }

        [DataMember(Name = "types")]
        public List<ManagedTypeInfo> Types { get; set; } = new List<ManagedTypeInfo>();

        [DataMember(Name = "partial")]
        public bool Partial { get; set; }

        [DataMember(Name = "error")]
        public string Error { get; set; }
    }

    [DataContract]
    public class ManagedTypeResult
    {
        [DataMember(Name = "capability")]
        public string Capability { get; set; } = "managed_metadata_and_il_read_only";

        [DataMember(Name = "assemblyPath")]
        public string AssemblyPath { get; set; }

        [DataMember(Name = "typeName")]
        public string TypeName { get; set; }

        [DataMember(Name = "type")]
        public ManagedTypeInfo Type { get; set; }

        [DataMember(Name = "error")]
        public string Error { get; set; }
    }

    [DataContract]
    public class ManagedTypeInfo
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "fullName")]
        public string FullName { get; set; }

        [DataMember(Name = "namespaceName")]
        public string NamespaceName { get; set; }

        [DataMember(Name = "assemblyQualifiedName")]
        public string AssemblyQualifiedName { get; set; }

        [DataMember(Name = "baseType")]
        public string BaseType { get; set; }

        [DataMember(Name = "isPublic")]
        public bool IsPublic { get; set; }

        [DataMember(Name = "isInterface")]
        public bool IsInterface { get; set; }

        [DataMember(Name = "isEnum")]
        public bool IsEnum { get; set; }

        [DataMember(Name = "isValueType")]
        public bool IsValueType { get; set; }

        [DataMember(Name = "methods")]
        public List<ManagedMethodInfo> Methods { get; set; } = new List<ManagedMethodInfo>();

        [DataMember(Name = "partial")]
        public bool Partial { get; set; }
    }

    [DataContract]
    public class ManagedMethodInfo
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "signature")]
        public string Signature { get; set; }

        [DataMember(Name = "declaringType")]
        public string DeclaringType { get; set; }

        [DataMember(Name = "returnType")]
        public string ReturnType { get; set; }

        [DataMember(Name = "metadataToken")]
        public int MetadataToken { get; set; }

        [DataMember(Name = "metadataTokenHex")]
        public string MetadataTokenHex { get; set; }

        [DataMember(Name = "isPublic")]
        public bool IsPublic { get; set; }

        [DataMember(Name = "isStatic")]
        public bool IsStatic { get; set; }

        [DataMember(Name = "isConstructor")]
        public bool IsConstructor { get; set; }

        [DataMember(Name = "parameters")]
        public List<ManagedParameterInfo> Parameters { get; set; } = new List<ManagedParameterInfo>();
    }

    [DataContract]
    public class ManagedParameterInfo
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "type")]
        public string Type { get; set; }

        [DataMember(Name = "position")]
        public int Position { get; set; }
    }

    [DataContract]
    public class ManagedLocalInfo
    {
        [DataMember(Name = "index")]
        public int Index { get; set; }

        [DataMember(Name = "type")]
        public string Type { get; set; }

        [DataMember(Name = "isPinned")]
        public bool IsPinned { get; set; }
    }

    [DataContract]
    public class ManagedMethodIlResult
    {
        [DataMember(Name = "capability")]
        public string Capability { get; set; } = "managed_metadata_and_il_read_only";

        [DataMember(Name = "assemblyPath")]
        public string AssemblyPath { get; set; }

        [DataMember(Name = "typeName")]
        public string TypeName { get; set; }

        [DataMember(Name = "methodName")]
        public string MethodName { get; set; }

        [DataMember(Name = "overloadIndex")]
        public int OverloadIndex { get; set; }

        [DataMember(Name = "matchedOverloadCount")]
        public int MatchedOverloadCount { get; set; }

        [DataMember(Name = "method")]
        public ManagedMethodInfo Method { get; set; }

        [DataMember(Name = "locals")]
        public List<ManagedLocalInfo> Locals { get; set; } = new List<ManagedLocalInfo>();

        [DataMember(Name = "instructions")]
        public List<ManagedIlInstructionInfo> Instructions { get; set; } = new List<ManagedIlInstructionInfo>();

        [DataMember(Name = "totalInstructionCount")]
        public int TotalInstructionCount { get; set; }

        [DataMember(Name = "partial")]
        public bool Partial { get; set; }

        [DataMember(Name = "error")]
        public string Error { get; set; }
    }

    [DataContract]
    public class ManagedIlInstructionInfo
    {
        [DataMember(Name = "offset")]
        public int Offset { get; set; }

        [DataMember(Name = "offsetHex")]
        public string OffsetHex { get; set; }

        [DataMember(Name = "opcode")]
        public string Opcode { get; set; }

        [DataMember(Name = "operandType")]
        public string OperandType { get; set; }

        [DataMember(Name = "operand")]
        public string Operand { get; set; }

        [DataMember(Name = "token")]
        public int Token { get; set; }

        [DataMember(Name = "tokenHex")]
        public string TokenHex { get; set; }

        [DataMember(Name = "resolvedMember")]
        public string ResolvedMember { get; set; }

        [DataMember(Name = "branchTarget")]
        public string BranchTarget { get; set; }

        [DataMember(Name = "switchTargets")]
        public List<string> SwitchTargets { get; set; }
    }

    [DataContract]
    public class HybridContextResult
    {
        [DataMember(Name = "snapshotId")]
        public string SnapshotId { get; set; }

        [DataMember(Name = "capturedAt")]
        public string CapturedAt { get; set; }

        [DataMember(Name = "status")]
        public DebugStatusResult Status { get; set; }

        [DataMember(Name = "currentThreadId")]
        public int CurrentThreadId { get; set; }

        [DataMember(Name = "selectedStackFrameIndex")]
        public int SelectedStackFrameIndex { get; set; }

        [DataMember(Name = "nativeContext")]
        public NativeContextResult NativeContext { get; set; }

        [DataMember(Name = "threads")]
        public ThreadsResult Threads { get; set; }

        [DataMember(Name = "breakpoints")]
        public BreakpointsResult Breakpoints { get; set; }

        [DataMember(Name = "partial")]
        public bool Partial { get; set; }

        [DataMember(Name = "error")]
        public string Error { get; set; }
    }

    [DataContract]
    public class BreakpointCaptureResult
    {
        [DataMember(Name = "action")]
        public string Action { get; set; } = "ContinueAndCaptureBreakpoint";

        [DataMember(Name = "success")]
        public bool Success { get; set; }

        [DataMember(Name = "message")]
        public string Message { get; set; }

        [DataMember(Name = "waitTimeoutMs")]
        public int WaitTimeoutMs { get; set; }

        [DataMember(Name = "pollIntervalMs")]
        public int PollIntervalMs { get; set; }

        [DataMember(Name = "waitedMs")]
        public int WaitedMs { get; set; }

        [DataMember(Name = "issuedContinue")]
        public bool IssuedContinue { get; set; }

        [DataMember(Name = "timedOut")]
        public bool TimedOut { get; set; }

        [DataMember(Name = "breakDetected")]
        public bool BreakDetected { get; set; }

        [DataMember(Name = "stopReason")]
        public string StopReason { get; set; }

        [DataMember(Name = "status")]
        public DebugStatusResult Status { get; set; }

        [DataMember(Name = "continueResult")]
        public ContinueAndWaitResult ContinueResult { get; set; }

        [DataMember(Name = "breakpoints")]
        public BreakpointsResult Breakpoints { get; set; }

        [DataMember(Name = "hybridContext")]
        public HybridContextResult HybridContext { get; set; }

        [DataMember(Name = "hitAttributionStatus")]
        public string HitAttributionStatus { get; set; } = "missing";

        [DataMember(Name = "hitAttributionError")]
        public string HitAttributionError { get; set; }

        [DataMember(Name = "hitVerificationStatus")]
        public string HitVerificationStatus { get; set; } = "not_checked";

        [DataMember(Name = "hitVerificationMessage")]
        public string HitVerificationMessage { get; set; }

        [DataMember(Name = "hitBreakpoint")]
        public BreakpointHitInfo HitBreakpoint { get; set; }
    }

    [DataContract]
    public class BreakpointHitInfo
    {
        [DataMember(Name = "confidence")]
        public string Confidence { get; set; }

        [DataMember(Name = "reason")]
        public string Reason { get; set; }

        [DataMember(Name = "currentAddress")]
        public NativeAddressInfo CurrentAddress { get; set; }

        [DataMember(Name = "breakpoint")]
        public BreakpointInfo Breakpoint { get; set; }
    }

    #endregion

    #region Exception

    [DataContract]
    public class ExceptionResult
    {
        [DataMember(Name = "hasException")]
        public bool HasException { get; set; }

        [DataMember(Name = "capability")]
        public string Capability { get; set; }

        [DataMember(Name = "type")]
        public string Type { get; set; }

        [DataMember(Name = "message")]
        public string Message { get; set; }

        [DataMember(Name = "stackTrace")]
        public string StackTrace { get; set; }

        [DataMember(Name = "source")]
        public string Source { get; set; }

        [DataMember(Name = "innerException")]
        public ExceptionResult InnerException { get; set; }

        [DataMember(Name = "hResult")]
        public string HResult { get; set; }
    }

    #endregion

    #region Expression Evaluation

    [DataContract]
    public class EvaluateResult
    {
        [DataMember(Name = "expression")]
        public string Expression { get; set; }

        [DataMember(Name = "value")]
        public string Value { get; set; }

        [DataMember(Name = "type")]
        public string Type { get; set; }

        [DataMember(Name = "isValid")]
        public bool IsValid { get; set; }

        [DataMember(Name = "errorMessage")]
        public string ErrorMessage { get; set; }

        [DataMember(Name = "hexValue")]
        public string HexValue { get; set; }

        [DataMember(Name = "hasChildren")]
        public bool HasChildren { get; set; }

        [DataMember(Name = "children")]
        public List<VariableInfo> Children { get; set; }
    }

    [DataContract]
    public class BatchEvaluateResult
    {
        [DataMember(Name = "snapshotId")]
        public string SnapshotId { get; set; }

        [DataMember(Name = "capturedAt")]
        public string CapturedAt { get; set; }

        [DataMember(Name = "snapshotStable")]
        public bool SnapshotStable { get; set; }

        [DataMember(Name = "snapshotAttempts")]
        public int SnapshotAttempts { get; set; }

        [DataMember(Name = "snapshotStatus")]
        public string SnapshotStatus { get; set; } = "unavailable";

        [DataMember(Name = "requestedCount")]
        public int RequestedCount { get; set; }

        [DataMember(Name = "successCount")]
        public int SuccessCount { get; set; }

        [DataMember(Name = "failedCount")]
        public int FailedCount { get; set; }

        [DataMember(Name = "partial")]
        public bool Partial { get; set; }

        [DataMember(Name = "results")]
        public List<EvaluateResult> Results { get; set; } = new List<EvaluateResult>();

        [DataMember(Name = "error")]
        public string Error { get; set; }
    }

    #endregion

    #region Memory

    [DataContract]
    public class MemoryResult
    {
        [DataMember(Name = "address")]
        public string Address { get; set; }

        [DataMember(Name = "status")]
        public string Status { get; set; } = "unavailable";

        [DataMember(Name = "size")]
        public int Size { get; set; }

        [DataMember(Name = "requestedSize")]
        public int RequestedSize { get; set; }

        [DataMember(Name = "bytesRead")]
        public int BytesRead { get; set; }

        [DataMember(Name = "failedReads")]
        public int FailedReads { get; set; }

        [DataMember(Name = "partial")]
        public bool Partial { get; set; }

        [DataMember(Name = "hexDump")]
        public string HexDump { get; set; }

        [DataMember(Name = "asciiView")]
        public string AsciiView { get; set; }

        [DataMember(Name = "bytes")]
        public List<byte> Bytes { get; set; }

        [DataMember(Name = "formattedView")]
        public string FormattedView { get; set; }

        [DataMember(Name = "error")]
        public string Error { get; set; }
    }

    #endregion

    #region Disassembly

    [DataContract]
    public class DisassemblyResult
    {
        [DataMember(Name = "currentAddress")]
        public string CurrentAddress { get; set; }

        [DataMember(Name = "instructions")]
        public List<DisassemblyInstruction> Instructions { get; set; } = new List<DisassemblyInstruction>();

        [DataMember(Name = "functionName")]
        public string FunctionName { get; set; }

        [DataMember(Name = "sourceFile")]
        public string SourceFile { get; set; }

        [DataMember(Name = "is64Bit")]
        public bool Is64Bit { get; set; }

        [DataMember(Name = "error")]
        public string Error { get; set; }

        [DataMember(Name = "totalInstructions")]
        public int TotalInstructions { get; set; }
    }

    [DataContract]
    public class DisassemblyInstruction
    {
        [DataMember(Name = "address")]
        public string Address { get; set; }

        [DataMember(Name = "opcode")]
        public string Opcode { get; set; }

        [DataMember(Name = "instruction")]
        public string Instruction { get; set; }

        [DataMember(Name = "isCurrent")]
        public bool IsCurrent { get; set; }

        [DataMember(Name = "sourceLine")]
        public int? SourceLine { get; set; }

        [DataMember(Name = "symbol")]
        public string Symbol { get; set; }

        /// <summary>
        /// The instruction mnemonic (e.g., "mov", "call", "jmp").
        /// </summary>
        [DataMember(Name = "mnemonic")]
        public string Mnemonic { get; set; }

        /// <summary>
        /// The instruction operands (e.g., "[rsp+8], rbx").
        /// </summary>
        [DataMember(Name = "operands")]
        public string Operands { get; set; }

        /// <summary>
        /// True if this is a CALL instruction.
        /// </summary>
        [DataMember(Name = "isCall")]
        public bool IsCall { get; set; }

        /// <summary>
        /// True if this is a JMP or conditional branch instruction.
        /// </summary>
        [DataMember(Name = "isJump")]
        public bool IsJump { get; set; }

        /// <summary>
        /// True if this is a RET instruction.
        /// </summary>
        [DataMember(Name = "isReturn")]
        public bool IsReturn { get; set; }

        /// <summary>
        /// Target address for branch/call instructions (null if indirect or not a branch).
        /// </summary>
        [DataMember(Name = "branchTarget")]
        public string BranchTarget { get; set; }

        /// <summary>
        /// Length of this instruction in bytes.
        /// </summary>
        [DataMember(Name = "length")]
        public int Length { get; set; }
    }

    #endregion

    #region Build Errors

    [DataContract]
    public class BuildErrorsResult
    {
        [DataMember(Name = "errorCount")]
        public int ErrorCount { get; set; }

        [DataMember(Name = "warningCount")]
        public int WarningCount { get; set; }

        [DataMember(Name = "items")]
        public List<BuildErrorItem> Items { get; set; } = new List<BuildErrorItem>();
    }

    [DataContract]
    public class BuildErrorItem
    {
        [DataMember(Name = "severity")]
        public string Severity { get; set; }

        [DataMember(Name = "code")]
        public string Code { get; set; }

        [DataMember(Name = "description")]
        public string Description { get; set; }

        [DataMember(Name = "filePath")]
        public string FilePath { get; set; }

        [DataMember(Name = "line")]
        public int Line { get; set; }

        [DataMember(Name = "column")]
        public int Column { get; set; }

        [DataMember(Name = "project")]
        public string Project { get; set; }
    }

    #endregion

    #region Full Context

    [DataContract]
    public class FullContextResult
    {
        [DataMember(Name = "status")]
        public DebugStatusResult Status { get; set; }

        [DataMember(Name = "callstack")]
        public CallStackResult Callstack { get; set; }

        [DataMember(Name = "locals")]
        public LocalsResult Locals { get; set; }

        [DataMember(Name = "exception")]
        public ExceptionResult Exception { get; set; }

        [DataMember(Name = "threads")]
        public ThreadsResult Threads { get; set; }

        [DataMember(Name = "modules")]
        public ModulesResult Modules { get; set; }
    }

    [DataContract]
    public class DebugBundleResult
    {
        [DataMember(Name = "snapshotId")]
        public string SnapshotId { get; set; }

        [DataMember(Name = "capturedAt")]
        public string CapturedAt { get; set; }

        [DataMember(Name = "snapshotStable")]
        public bool SnapshotStable { get; set; }

        [DataMember(Name = "snapshotAttempts")]
        public int SnapshotAttempts { get; set; }

        [DataMember(Name = "snapshotStatus")]
        public string SnapshotStatus { get; set; } = "unavailable";

        [DataMember(Name = "partial")]
        public bool Partial { get; set; }

        [DataMember(Name = "includedComponents")]
        public List<string> IncludedComponents { get; set; } = new List<string>();

        [DataMember(Name = "omittedComponents")]
        public List<string> OmittedComponents { get; set; } = new List<string>();

        [DataMember(Name = "stopReason")]
        public string StopReason { get; set; } = "not_paused";

        [DataMember(Name = "status")]
        public DebugStatusResult Status { get; set; }

        [DataMember(Name = "callstack")]
        public CallStackResult Callstack { get; set; }

        [DataMember(Name = "locals")]
        public LocalsResult Locals { get; set; }

        [DataMember(Name = "exception")]
        public ExceptionResult Exception { get; set; }

        [DataMember(Name = "threads")]
        public ThreadsResult Threads { get; set; }

        [DataMember(Name = "modules")]
        public ModulesResult Modules { get; set; }

        [DataMember(Name = "registers")]
        public RegistersResult Registers { get; set; }

        [DataMember(Name = "output")]
        public OutputWindowResult Output { get; set; }

        [DataMember(Name = "expressions")]
        public BatchEvaluateResult Expressions { get; set; }

        [DataMember(Name = "error")]
        public string Error { get; set; }
    }

    #endregion

    #region Ping

    [DataContract]
    public class PingResult
    {
        [DataMember(Name = "status")]
        public string Status { get; set; }

        [DataMember(Name = "timestamp")]
        public string Timestamp { get; set; }
    }

    #endregion

    #region Instance Info

    /// <summary>
    /// Information about a Visual Studio instance for multi-instance support.
    /// </summary>
    [DataContract]
    public class InstanceInfoResult
    {
        [DataMember(Name = "processId")]
        public int ProcessId { get; set; }

        [DataMember(Name = "pipeName")]
        public string PipeName { get; set; }

        [DataMember(Name = "solutionName")]
        public string SolutionName { get; set; }

        [DataMember(Name = "solutionPath")]
        public string SolutionPath { get; set; }

        [DataMember(Name = "isDebugging")]
        public bool IsDebugging { get; set; }

        [DataMember(Name = "isPaused")]
        public bool IsPaused { get; set; }

        [DataMember(Name = "debugMode")]
        public string DebugMode { get; set; }

        [DataMember(Name = "currentProcess")]
        public string CurrentProcess { get; set; }

        [DataMember(Name = "vsVersion")]
        public string VsVersion { get; set; }
    }

    #endregion

    #region Debug Control

    [DataContract]
    public class DebugControlResult
    {
        [DataMember(Name = "action")]
        public string Action { get; set; }

        [DataMember(Name = "success")]
        public bool Success { get; set; }

        [DataMember(Name = "message")]
        public string Message { get; set; }

        [DataMember(Name = "newLocation")]
        public LocationInfo NewLocation { get; set; }
    }

    [DataContract]
    public class ProcessDebugResult
    {
        [DataMember(Name = "action")]
        public string Action { get; set; }

        [DataMember(Name = "success")]
        public bool Success { get; set; }

        [DataMember(Name = "message")]
        public string Message { get; set; }

        [DataMember(Name = "processId")]
        public int ProcessId { get; set; }

        [DataMember(Name = "processName")]
        public string ProcessName { get; set; }

        [DataMember(Name = "executablePath")]
        public string ExecutablePath { get; set; }

        [DataMember(Name = "workingDirectory")]
        public string WorkingDirectory { get; set; }

        [DataMember(Name = "arguments")]
        public string Arguments { get; set; }

        [DataMember(Name = "status")]
        public DebugStatusResult Status { get; set; }
    }

    [DataContract]
    public class ContinueAndWaitResult
    {
        [DataMember(Name = "action")]
        public string Action { get; set; }

        [DataMember(Name = "success")]
        public bool Success { get; set; }

        [DataMember(Name = "message")]
        public string Message { get; set; }

        [DataMember(Name = "waitTimeoutMs")]
        public int WaitTimeoutMs { get; set; }

        [DataMember(Name = "pollIntervalMs")]
        public int PollIntervalMs { get; set; }

        [DataMember(Name = "waitedMs")]
        public int WaitedMs { get; set; }

        [DataMember(Name = "issuedContinue")]
        public bool IssuedContinue { get; set; }

        [DataMember(Name = "timedOut")]
        public bool TimedOut { get; set; }

        [DataMember(Name = "breakDetected")]
        public bool BreakDetected { get; set; }

        [DataMember(Name = "stopReason")]
        public string StopReason { get; set; } = "not_paused";

        [DataMember(Name = "status")]
        public DebugStatusResult Status { get; set; }

        [DataMember(Name = "bundle")]
        public DebugBundleResult Bundle { get; set; }
    }

    #endregion

    #region Breakpoints

    [DataContract]
    public class BreakpointsResult
    {
        [DataMember(Name = "breakpoints")]
        public List<BreakpointInfo> Breakpoints { get; set; } = new List<BreakpointInfo>();

        [DataMember(Name = "totalCount")]
        public int TotalCount { get; set; }

        [DataMember(Name = "error")]
        public string Error { get; set; }
    }

    [DataContract]
    public class BreakpointInfo
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "filePath")]
        public string FilePath { get; set; }

        [DataMember(Name = "lineNumber")]
        public int LineNumber { get; set; }

        [DataMember(Name = "column")]
        public int Column { get; set; }

        [DataMember(Name = "functionName")]
        public string FunctionName { get; set; }

        [DataMember(Name = "enabled")]
        public bool Enabled { get; set; }

        [DataMember(Name = "condition")]
        public string Condition { get; set; }

        [DataMember(Name = "conditionType")]
        public string ConditionType { get; set; }

        [DataMember(Name = "hitCount")]
        public int HitCount { get; set; }

        [DataMember(Name = "hitCountTarget")]
        public int HitCountTarget { get; set; }

        [DataMember(Name = "hitCountType")]
        public string HitCountType { get; set; }

        [DataMember(Name = "breakpointType")]
        public string BreakpointType { get; set; }

        [DataMember(Name = "tag")]
        public string Tag { get; set; }

        [DataMember(Name = "requestedFilePath")]
        public string RequestedFilePath { get; set; }

        [DataMember(Name = "requestedLineNumber")]
        public int RequestedLineNumber { get; set; }

        [DataMember(Name = "requestedCondition")]
        public string RequestedCondition { get; set; }

        [DataMember(Name = "resolvedFilePath")]
        public string ResolvedFilePath { get; set; }

        [DataMember(Name = "resolvedLineNumber")]
        public int ResolvedLineNumber { get; set; }

        [DataMember(Name = "resolvedColumn")]
        public int ResolvedColumn { get; set; }

        [DataMember(Name = "resolvedFunctionName")]
        public string ResolvedFunctionName { get; set; }

        [DataMember(Name = "trackedAddress")]
        public NativeAddressInfo TrackedAddress { get; set; }

        [DataMember(Name = "trackedAddresses")]
        public List<NativeAddressInfo> TrackedAddresses { get; set; } = new List<NativeAddressInfo>();

        [DataMember(Name = "bindingStatus")]
        public string BindingStatus { get; set; }

        [DataMember(Name = "boundChildrenCount")]
        public int BoundChildrenCount { get; set; }

        [DataMember(Name = "isBound")]
        public bool IsBound { get; set; }

        [DataMember(Name = "bindingVerificationStatus")]
        public string BindingVerificationStatus { get; set; }

        [DataMember(Name = "bindingWarning")]
        public string BindingWarning { get; set; }

        [DataMember(Name = "isRelocated")]
        public bool IsRelocated { get; set; }

        [DataMember(Name = "nearbyProbeAttempted")]
        public bool NearbyProbeAttempted { get; set; }

        [DataMember(Name = "nearbyProbeSucceeded")]
        public bool NearbyProbeSucceeded { get; set; }

        [DataMember(Name = "optimizedCodeSuspected")]
        public bool OptimizedCodeSuspected { get; set; }

        [DataMember(Name = "conditionApplied")]
        public bool ConditionApplied { get; set; }
    }

    [DataContract]
    public class BreakpointOperationResult
    {
        [DataMember(Name = "operation")]
        public string Operation { get; set; }

        [DataMember(Name = "success")]
        public bool Success { get; set; }

        [DataMember(Name = "message")]
        public string Message { get; set; }

        [DataMember(Name = "verificationStatus")]
        public string VerificationStatus { get; set; }

        [DataMember(Name = "verificationMessage")]
        public string VerificationMessage { get; set; }

        [DataMember(Name = "breakpointInfo")]
        public BreakpointInfo BreakpointInfo { get; set; }

        [DataMember(Name = "address")]
        public NativeAddressInfo Address { get; set; }
    }

    #endregion

    #region Registers

    [DataContract]
    public class RegistersResult
    {
        [DataMember(Name = "registers")]
        public List<RegisterInfo> Registers { get; set; } = new List<RegisterInfo>();

        [DataMember(Name = "is64Bit")]
        public bool Is64Bit { get; set; }

        [DataMember(Name = "capability")]
        public string Capability { get; set; }

        [DataMember(Name = "availableCategories")]
        public List<string> AvailableCategories { get; set; } = new List<string>();

        [DataMember(Name = "error")]
        public string Error { get; set; }
    }

    [DataContract]
    public class RegisterInfo
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "value")]
        public string Value { get; set; }

        [DataMember(Name = "hexValue")]
        public string HexValue { get; set; }

        [DataMember(Name = "category")]
        public string Category { get; set; }
    }

    #endregion

    #region Watch

    [DataContract]
    public class WatchResult
    {
        [DataMember(Name = "expression")]
        public string Expression { get; set; }

        [DataMember(Name = "behavior")]
        public string Behavior { get; set; }

        [DataMember(Name = "value")]
        public string Value { get; set; }

        [DataMember(Name = "type")]
        public string Type { get; set; }

        [DataMember(Name = "isValid")]
        public bool IsValid { get; set; }

        [DataMember(Name = "error")]
        public string Error { get; set; }
    }

    #endregion

    #region Memory Search

    [DataContract]
    public class MemorySearchResult
    {
        [DataMember(Name = "pattern")]
        public string Pattern { get; set; }

        [DataMember(Name = "status")]
        public string Status { get; set; } = "unavailable";

        [DataMember(Name = "patternBytes")]
        public string[] PatternBytes { get; set; }

        [DataMember(Name = "matches")]
        public List<string> Matches { get; set; } = new List<string>();

        [DataMember(Name = "totalFound")]
        public int TotalFound { get; set; }

        [DataMember(Name = "bytesSearched")]
        public int BytesSearched { get; set; }

        [DataMember(Name = "requestedBytes")]
        public int RequestedBytes { get; set; }

        [DataMember(Name = "failedReads")]
        public int FailedReads { get; set; }

        [DataMember(Name = "partial")]
        public bool Partial { get; set; }

        [DataMember(Name = "truncated")]
        public bool Truncated { get; set; }

        [DataMember(Name = "nextStartAddress")]
        public string NextStartAddress { get; set; }

        [DataMember(Name = "error")]
        public string Error { get; set; }
    }

    #endregion

    #region Output Window

    [DataContract]
    public class OutputWindowResult
    {
        [DataMember(Name = "pane")]
        public string Pane { get; set; }

        [DataMember(Name = "capability")]
        public string Capability { get; set; }

        [DataMember(Name = "content")]
        public string Content { get; set; }

        [DataMember(Name = "lineCount")]
        public int LineCount { get; set; }

        [DataMember(Name = "requestedLines")]
        public int RequestedLines { get; set; }

        [DataMember(Name = "truncated")]
        public bool Truncated { get; set; }

        [DataMember(Name = "availablePanes")]
        public List<string> AvailablePanes { get; set; } = new List<string>();

        [DataMember(Name = "error")]
        public string Error { get; set; }
    }

    [DataContract]
    public class SearchOutputResult
    {
        [DataMember(Name = "pane")]
        public string Pane { get; set; }

        [DataMember(Name = "capability")]
        public string Capability { get; set; }

        [DataMember(Name = "pattern")]
        public string Pattern { get; set; }

        [DataMember(Name = "isRegex")]
        public bool IsRegex { get; set; }

        [DataMember(Name = "matches")]
        public List<OutputMatch> Matches { get; set; } = new List<OutputMatch>();

        [DataMember(Name = "totalMatches")]
        public int TotalMatches { get; set; }

        [DataMember(Name = "truncated")]
        public bool Truncated { get; set; }

        [DataMember(Name = "error")]
        public string Error { get; set; }
    }

    [DataContract]
    public class OutputMatch
    {
        [DataMember(Name = "lineNumber")]
        public int LineNumber { get; set; }

        [DataMember(Name = "line")]
        public string Line { get; set; }

        [DataMember(Name = "context")]
        public List<string> Context { get; set; } = new List<string>();
    }

    #endregion
}
