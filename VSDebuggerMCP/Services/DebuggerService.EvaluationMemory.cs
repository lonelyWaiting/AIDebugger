using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using EnvDTE;
using EnvDTE80;
using EnvDTE100;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VSDebuggerMCP.Protocol;
using VSDebuggerMCP.Services;

namespace VSDebuggerMCP
{
    public partial class DebuggerService
    {
        private const int ExpressionFallbackMaxBytes = 256;

        #region Expression Evaluation

        [SuppressMessage("Usage", "VSTHRD010", Justification = "Expression roots and children are captured through bounded UI slices.")]
        public EvaluateResult EvaluateExpression(string expression, bool expandChildren = false, int maxDepth = 2)
        {
            return EvaluateExpressionSliced(expression, expandChildren, maxDepth);
        }

        public BatchEvaluateResult EvaluateExpressionsBatch(List<string> expressions, bool expandChildren = false, int maxDepth = 2)
        {
            var normalizedExpressions = NormalizeExpressions(expressions);
            string snapshotId = Guid.NewGuid().ToString("N");
            string capturedAt = DateTime.UtcNow.ToString("o");
            var result = new BatchEvaluateResult
            {
                SnapshotId = snapshotId,
                CapturedAt = capturedAt,
                RequestedCount = normalizedExpressions.Count,
                SnapshotAttempts = 1,
                SnapshotStatus = "unavailable",
                Partial = true
            };
            if (normalizedExpressions.Count == 0)
            {
                result.Error = "No expressions provided";
                return result;
            }

            EvaluationSnapshotHeader header = ExecuteOnMainThread(CaptureEvaluationSnapshotHeader);
            if (!header.Available)
            {
                result.Error = header.Error;
                return result;
            }

            var batchBudget = new VariableCaptureBudget(
                ExpressionExpansionMaxNodes * Math.Max(1, normalizedExpressions.Count),
                ExpressionBatchBudgetMs);
            var stopwatch = Stopwatch.StartNew();
            string budgetExceededMessage = $"Skipped because expression batch budget exceeded {ExpressionBatchBudgetMs}ms";
            bool omittedForTimeBudget = false;
            foreach (string expression in normalizedExpressions)
            {
                DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                if (stopwatch.ElapsedMilliseconds >= ExpressionBatchBudgetMs)
                {
                    omittedForTimeBudget = true;
                    break;
                }

                try
                {
                    string capturedExpression = expression;
                    result.Results.Add(EvaluateExpressionSliced(
                        capturedExpression,
                        expandChildren,
                        maxDepth,
                        batchBudget));
                }
                catch (Exception ex)
                {
                    RethrowRequestControlFlow(ex);
                    result.Results.Add(CreateFailedEvaluationResult(expression, ex.Message, batchBudget));
                }
            }

            string tokenAfter = ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return CreateDebuggerSnapshotToken(Debugger);
            });
            result.SuccessCount = result.Results.Count(item => item.IsValid);
            result.FailedCount = result.Results.Count - result.SuccessCount;
            result.CapturedBytes = batchBudget.SerializedBytes;
            result.MaxCapturedBytes = VariableCaptureBudget.MaxSerializedBytes;
            result.Partial = result.FailedCount > 0 ||
                result.Results.Count < result.RequestedCount ||
                result.Results.Any(item => item.Partial);
            bool debuggerLocationStable = !string.IsNullOrEmpty(header.SnapshotToken) &&
                string.Equals(header.SnapshotToken, tokenAfter, StringComparison.Ordinal);
            // engine_default may invoke user code. Location equality is only a
            // best-effort observation and must never be advertised as a strong snapshot.
            result.SnapshotStable = false;
            result.SnapshotStatus = debuggerLocationStable ? "best_effort" : "unstable";
            if (omittedForTimeBudget)
            {
                result.Error = $"Expression batch budget exceeded {ExpressionBatchBudgetMs}ms";
            }
            return result;
        }

        private EvaluationSnapshotHeader CaptureEvaluationSnapshotHeader()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var debugger = Debugger;
            if (debugger == null || debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
            {
                return new EvaluationSnapshotHeader { Error = "Not in break mode" };
            }
            return new EvaluationSnapshotHeader
            {
                Available = true,
                SnapshotToken = CreateDebuggerSnapshotToken(debugger)
            };
        }

        private sealed class EvaluationSnapshotHeader
        {
            internal bool Available { get; set; }
            internal string Error { get; set; }
            internal string SnapshotToken { get; set; }
        }

        private void MergeModuleInfo(
            HashSet<string> moduleSet,
            Dictionary<string, ModuleInfo> moduleInfoDict,
            ModuleInfo moduleInfo,
            bool addIfMissing)
        {
            if (moduleInfo == null || string.IsNullOrEmpty(moduleInfo.Path))
            {
                return;
            }

            if (!moduleSet.Contains(moduleInfo.Path))
            {
                if (!addIfMissing)
                {
                    return;
                }

                moduleSet.Add(moduleInfo.Path);
                moduleInfoDict[moduleInfo.Path] = moduleInfo;
                return;
            }

            var existing = moduleInfoDict[moduleInfo.Path];
            if (string.IsNullOrEmpty(existing.Address) && !string.IsNullOrEmpty(moduleInfo.Address))
            {
                existing.Address = moduleInfo.Address;
            }
            if (string.IsNullOrEmpty(existing.Size) && !string.IsNullOrEmpty(moduleInfo.Size))
            {
                existing.Size = moduleInfo.Size;
            }
            if (existing.SizeBytes == 0 && moduleInfo.SizeBytes != 0)
            {
                existing.SizeBytes = moduleInfo.SizeBytes;
            }
            if (string.IsNullOrEmpty(existing.SymbolPath) && !string.IsNullOrEmpty(moduleInfo.SymbolPath))
            {
                existing.SymbolPath = moduleInfo.SymbolPath;
            }
            if (string.IsNullOrEmpty(existing.Timestamp) && !string.IsNullOrEmpty(moduleInfo.Timestamp))
            {
                existing.Timestamp = moduleInfo.Timestamp;
            }
            if (string.IsNullOrEmpty(existing.Version) && !string.IsNullOrEmpty(moduleInfo.Version))
            {
                existing.Version = moduleInfo.Version;
            }
            if ((string.IsNullOrEmpty(existing.Architecture) || existing.Architecture == "unknown") &&
                !string.IsNullOrEmpty(moduleInfo.Architecture))
            {
                existing.Architecture = moduleInfo.Architecture;
            }
            existing.Is64Bit = existing.Is64Bit == true || moduleInfo.Is64Bit == true
                ? (bool?)true
                : (existing.Is64Bit == false && moduleInfo.Is64Bit == false ? (bool?)false : null);
        }

        private string GetActiveDumpFilePathCandidate()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            StopEventSnapshot session = _sessionTracker.GetStopEventSnapshot();
            if (string.Equals(session.Mode, "design", StringComparison.Ordinal))
            {
                ClearDumpSessionState();
                return null;
            }

            if (IsDumpFilePath(_pendingDumpFilePath) &&
                session.Generation != _pendingDumpStartGeneration)
            {
                _activeDumpFilePath = _pendingDumpFilePath;
                _activeDumpSessionGeneration = session.Generation;
                _pendingDumpFilePath = null;
                _pendingDumpStartGeneration = 0;
            }

            if (_activeDumpSessionGeneration == session.Generation &&
                IsDumpFilePath(_activeDumpFilePath))
            {
                return _activeDumpFilePath;
            }

            // A path from an earlier session, or merely an active .dmp document,
            // is never evidence that the current debugger is reading that dump.
            ClearDumpSessionState();
            return null;
        }

        private void BeginPendingDumpSession(string dumpPath)
        {
            ClearTrustedLiveProcessIdentity(clearPending: true);
            _activeDumpFilePath = null;
            _activeDumpSessionGeneration = 0;
            _pendingDumpFilePath = dumpPath;
            _pendingDumpStartGeneration = _sessionTracker.Generation;
        }

        private void ClearDumpSessionState()
        {
            _activeDumpFilePath = null;
            _activeDumpSessionGeneration = 0;
            _pendingDumpFilePath = null;
            _pendingDumpStartGeneration = 0;
        }

        private string NormalizeExistingDumpPath(string candidate)
        {
            if (!IsDumpFilePath(candidate))
            {
                return null;
            }
            try
            {
                string normalized = System.IO.Path.GetFullPath(candidate);
                return System.IO.File.Exists(normalized) ? normalized : null;
            }
            catch
            {
                return null;
            }
        }

        private ModuleSourceCapture GetModulesFromMinidump(
            string dumpPath,
            int startIndex,
            int maxNodes,
            ModuleCaptureBudget budget)
        {
            var capture = new ModuleSourceCapture();
            if (string.IsNullOrWhiteSpace(dumpPath) || !System.IO.File.Exists(dumpPath))
            {
                return capture;
            }

            using (var stream = new System.IO.FileStream(dumpPath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
            using (var reader = new System.IO.BinaryReader(stream, Encoding.UTF8))
            {
                if (stream.Length < 32)
                {
                    return capture;
                }

                uint signature = reader.ReadUInt32();
                if (signature != 0x504D444D) // "MDMP"
                {
                    return capture;
                }

                reader.ReadUInt32(); // version
                uint streamCount = reader.ReadUInt32();
                uint directoryRva = reader.ReadUInt32();
                reader.ReadUInt32(); // checksum
                reader.ReadUInt32(); // timestamp
                reader.ReadUInt64(); // flags

                if (streamCount > 4096 || directoryRva == 0 || directoryRva >= stream.Length)
                {
                    return capture;
                }

                uint moduleListRva = 0;
                uint moduleListSize = 0;
                uint systemInfoRva = 0;
                uint systemInfoSize = 0;
                stream.Position = directoryRva;
                for (uint i = 0; i < streamCount; i++)
                {
                    DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                    if (stream.Position + 12 > stream.Length)
                    {
                        return capture;
                    }

                    uint streamType = reader.ReadUInt32();
                    uint dataSize = reader.ReadUInt32();
                    uint rva = reader.ReadUInt32();
                    if (streamType == 4) // ModuleListStream
                    {
                        moduleListSize = dataSize;
                        moduleListRva = rva;
                    }
                    else if (streamType == 7) // SystemInfoStream
                    {
                        systemInfoSize = dataSize;
                        systemInfoRva = rva;
                    }
                }

                capture.Architecture = ReadMinidumpArchitecture(reader, systemInfoRva, systemInfoSize);
                capture.Is64Bit = TargetArchitecturePolicy.Is64Bit(capture.Architecture);

                if (moduleListRva == 0 || moduleListRva >= stream.Length || moduleListSize < 4)
                {
                    return capture;
                }

                stream.Position = moduleListRva;
                uint declaredModuleCount = reader.ReadUInt32();
                uint maxModulesBySize = (moduleListSize - 4) / 108;
                uint moduleCount = Math.Min(
                    Math.Min(declaredModuleCount, maxModulesBySize),
                    4096);
                capture.TotalCount = checked((int)Math.Min(declaredModuleCount, int.MaxValue));
                if (declaredModuleCount > moduleCount)
                {
                    capture.Partial = true;
                    capture.Error = declaredModuleCount > maxModulesBySize
                        ? $"minidump declares {declaredModuleCount} modules but its module stream contains at most {maxModulesBySize}"
                        : $"minidump declares {declaredModuleCount} modules, exceeding the bounded enumeration cap of 4096";
                }
                capture.NextIndex = Math.Min(startIndex, capture.TotalCount);

                for (uint i = 0; i < moduleCount; i++)
                {
                    DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                    if (stream.Position + 108 > stream.Length)
                    {
                        capture.Partial = true;
                        capture.Error = "minidump module list is truncated";
                        capture.NextIndex = capture.TotalCount;
                        capture.HasMore = false;
                        break;
                    }

                    ulong baseOfImage = reader.ReadUInt64();
                    uint sizeOfImage = reader.ReadUInt32();
                    reader.ReadUInt32(); // checksum
                    uint timeDateStamp = reader.ReadUInt32();
                    uint moduleNameRva = reader.ReadUInt32();
                    stream.Position += 52; // VS_FIXEDFILEINFO
                    uint cvRecordSize = reader.ReadUInt32();
                    uint cvRecordRva = reader.ReadUInt32();
                    stream.Position += 8; // MiscRecord
                    stream.Position += 16; // Reserved0/Reserved1

                    if (i < (uint)startIndex)
                    {
                        continue;
                    }
                    if (capture.Modules.Count >= maxNodes)
                    {
                        capture.HasMore = true;
                        capture.NextIndex = checked((int)i);
                        break;
                    }

                    string modulePath = ReadMinidumpString(reader, moduleNameRva);
                    if (string.IsNullOrWhiteSpace(modulePath))
                    {
                        capture.NextIndex = checked((int)i + 1);
                        continue;
                    }

                    var module = new ModuleInfo
                    {
                        Path = modulePath,
                        Name = SafeGetFileName(modulePath),
                        Address = $"0x{baseOfImage:X16}",
                        Size = FormatSize(sizeOfImage),
                        SizeBytes = sizeOfImage,
                        SymbolPath = ReadMinidumpCodeViewPdbPath(reader, cvRecordRva, cvRecordSize),
                        SymbolsLoaded = false,
                        IsSystemModule = IsSystemModule(modulePath),
                        Architecture = TargetArchitecturePolicy.ToProtocolName(capture.Architecture),
                        Is64Bit = capture.Is64Bit,
                        Timestamp = FormatPeTimestamp(timeDateStamp),
                        Version = null
                    };
                    if (!budget.TryConsume(module))
                    {
                        capture.HasMore = true;
                        capture.NextIndex = checked((int)i);
                        break;
                    }
                    capture.Modules.Add(module);
                    capture.NextIndex = checked((int)i + 1);
                }
                capture.HasMore = capture.HasMore || capture.NextIndex < checked((int)moduleCount);
            }

            return capture;
        }

        private TargetMachineArchitecture ReadMinidumpTargetArchitecture(string dumpPath)
        {
            string normalized = NormalizeExistingDumpPath(dumpPath);
            if (string.IsNullOrEmpty(normalized)) return TargetMachineArchitecture.Unknown;
            try
            {
                using (var stream = new FileStream(
                    normalized,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    if (stream.Length < 32 || reader.ReadUInt32() != 0x504D444D)
                        return TargetMachineArchitecture.Unknown;
                    reader.ReadUInt32();
                    uint streamCount = reader.ReadUInt32();
                    uint directoryRva = reader.ReadUInt32();
                    reader.ReadUInt32();
                    reader.ReadUInt32();
                    reader.ReadUInt64();
                    if (streamCount > 4096 || directoryRva == 0 ||
                        (ulong)directoryRva + (ulong)streamCount * 12UL > (ulong)stream.Length)
                        return TargetMachineArchitecture.Unknown;
                    stream.Position = directoryRva;
                    for (uint index = 0; index < streamCount; index++)
                    {
                        DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                        uint streamType = reader.ReadUInt32();
                        uint dataSize = reader.ReadUInt32();
                        uint rva = reader.ReadUInt32();
                        if (streamType == 7)
                            return ReadMinidumpArchitecture(reader, rva, dataSize);
                    }
                }
            }
            catch (Exception ex)
            {
                RethrowRequestControlFlow(ex);
                _log($"ReadMinidumpTargetArchitecture failed: {ex.GetType().Name}");
            }
            return TargetMachineArchitecture.Unknown;
        }

        private static TargetMachineArchitecture ReadMinidumpArchitecture(
            System.IO.BinaryReader reader,
            uint systemInfoRva,
            uint systemInfoSize)
        {
            if (systemInfoRva == 0 || systemInfoSize < 2 ||
                systemInfoRva >= reader.BaseStream.Length ||
                (ulong)systemInfoRva + 2UL > (ulong)reader.BaseStream.Length)
            {
                return TargetMachineArchitecture.Unknown;
            }

            long previousPosition = reader.BaseStream.Position;
            try
            {
                reader.BaseStream.Position = systemInfoRva;
                return TargetArchitecturePolicy.FromMinidumpProcessorArchitecture(
                    reader.ReadUInt16());
            }
            finally
            {
                reader.BaseStream.Position = previousPosition;
            }
        }

        private string ReadMinidumpString(System.IO.BinaryReader reader, uint rva)
        {
            if (rva == 0 || rva >= reader.BaseStream.Length)
            {
                return null;
            }

            long previousPosition = reader.BaseStream.Position;
            try
            {
                reader.BaseStream.Position = rva;
                uint byteLength = reader.ReadUInt32();
                if (byteLength == 0 || byteLength > 64 * 1024 || reader.BaseStream.Position + byteLength > reader.BaseStream.Length)
                {
                    return null;
                }

                return Encoding.Unicode.GetString(reader.ReadBytes((int)byteLength));
            }
            catch
            {
                return null;
            }
            finally
            {
                reader.BaseStream.Position = previousPosition;
            }
        }

        private string ReadMinidumpCodeViewPdbPath(System.IO.BinaryReader reader, uint rva, uint size)
        {
            if (rva == 0 || size < 24 || size > 4096 || rva >= reader.BaseStream.Length || (ulong)rva + size > (ulong)reader.BaseStream.Length)
            {
                return null;
            }

            long previousPosition = reader.BaseStream.Position;
            try
            {
                reader.BaseStream.Position = rva;
                var bytes = reader.ReadBytes((int)size);
                if (bytes.Length < 24 || bytes[0] != (byte)'R' || bytes[1] != (byte)'S' || bytes[2] != (byte)'D' || bytes[3] != (byte)'S')
                {
                    return null;
                }

                int end = Array.IndexOf(bytes, (byte)0, 24);
                if (end < 0)
                {
                    end = bytes.Length;
                }

                return Encoding.UTF8.GetString(bytes, 24, end - 24);
            }
            catch
            {
                return null;
            }
            finally
            {
                reader.BaseStream.Position = previousPosition;
            }
        }

        private string SafeGetFileName(string path)
        {
            try
            {
                return System.IO.Path.GetFileName(path);
            }
            catch
            {
                return path;
            }
        }

        private static EvaluateResult CreateFailedEvaluationResult(
            string expression,
            string error,
            VariableCaptureBudget budget)
        {
            budget.TryEnterNode();
            string boundedExpression = budget.CaptureString(
                expression,
                "expression",
                out bool expressionTruncated);
            string boundedError = budget.CaptureString(
                error,
                "evaluation error",
                out bool errorTruncated);
            return new EvaluateResult
            {
                Expression = boundedExpression,
                IsValid = false,
                ErrorMessage = boundedError,
                Partial = expressionTruncated || errorTruncated || budget.IsIncomplete,
                CapturedBytes = budget.SerializedBytes,
                MaxCapturedBytes = VariableCaptureBudget.MaxSerializedBytes,
            };
        }

        private bool IsNumericType(string type)
        {
            if (string.IsNullOrEmpty(type))
            {
                return false;
            }

            // Strip pointer/reference/array suffixes and whitespace for matching
            string cleanType = type.TrimEnd(' ', '*', '&', '[', ']');

            var numericTypes = new HashSet<string>(StringComparer.Ordinal)
            {
                "int", "uint", "long", "ulong", "short", "ushort", "byte", "sbyte",
                "IntPtr", "UIntPtr", "nint", "nuint",
                "System.Int32", "System.UInt32", "System.Int64", "System.UInt64",
                "System.Int16", "System.UInt16", "System.Byte", "System.SByte",
                "System.IntPtr", "System.UIntPtr"
            };
            return numericTypes.Contains(cleanType);
        }

        private List<string> NormalizeExpressions(IEnumerable<string> expressions)
        {
            var result = new List<string>();
            if (expressions == null)
            {
                return result;
            }

            foreach (var expression in expressions)
            {
                if (string.IsNullOrWhiteSpace(expression))
                {
                    continue;
                }

                result.Add(expression.Trim());
                if (result.Count >= 100)
                {
                    break;
                }
            }

            return result;
        }

        #endregion

        #region Memory

        public MemoryResult ReadMemory(string address, int size = 256)
        {
            int requestedSize = Math.Max(1, Math.Min(size, 4096));
            var unavailable = CreateUnavailableMemoryResult(address, requestedSize);
            if (!TryParseAddressLiteral(address, out ulong baseAddress))
            {
                unavailable.Error = "Address must be a hexadecimal or decimal literal";
                return unavailable;
            }
            if ((ulong)(requestedSize - 1) > ulong.MaxValue - baseAddress)
            {
                unavailable.Error = "Memory range overflows address space";
                return unavailable;
            }
            MemoryReadHeader header = CaptureMemoryReadHeader();
            if (header == null)
            {
                unavailable.Error = "Not in break mode";
                return unavailable;
            }

            // A dump's recorded PID can already belong to an unrelated live process. Never
            // use ReadProcessMemory for a confirmed dump session; only the debugger engine
            // can authoritatively serve that snapshot's address space.
            string dumpPath = NormalizeExistingDumpPath(header.DumpPathCandidate);
            bool isDumpSession = !string.IsNullOrEmpty(dumpPath);
            byte[] memory = null;
            bool trustedLiveReadAttempted = header.ProcessHandle != IntPtr.Zero;
            try
            {
                if (isDumpSession)
                {
                    memory = ReadRawMinidumpMemory(dumpPath, baseAddress, requestedSize);
                    if (memory != null && memory.Length > 0)
                    {
                        header.Capability = "raw_minidump_memory_read";
                    }
                }
                else if (header.ProcessHandle != IntPtr.Zero)
                {
                    // The handle was opened while the DTE current-process identity was
                    // pinned.  A single post-read snapshot check is enough to reject a
                    // context change and avoids an extra UI round trip per read.
                    memory = ReadProcessMemoryContiguous(header.ProcessHandle, baseAddress, requestedSize);
                }
            }
            finally
            {
                CloseMemoryReadHandle(header);
            }
            if (memory == null || memory.Length == 0)
            {
                memory = ReadMemoryExpressionPrefix(baseAddress, requestedSize);
                header.Capability = isDumpSession
                    ? "debugger_expression_dump_fallback"
                    : (trustedLiveReadAttempted
                        ? "debugger_expression_fallback_after_live_read_failure"
                        : "debugger_expression_fallback_untrusted_session");
            }
            string tokenAfter = ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return CreateDebuggerSnapshotToken(Debugger);
            });
            MemoryResult result = BuildMemoryResult(
                address,
                requestedSize,
                baseAddress,
                baseAddress > uint.MaxValue,
                memory);
            result.Capability = header.Capability;
            if (result.Partial && memory != null && memory.Length > 0 &&
                IsExpressionMemoryFallbackCapability(header.Capability))
            {
                result.HasMore = true;
                result.RemainingBytes = requestedSize - memory.Length;
                result.NextAddress = FormatAddress(baseAddress + (ulong)memory.Length);
                result.Error = $"Debugger-engine fallback returned a bounded {memory.Length}-byte prefix; " +
                    $"continue at {result.NextAddress} for the remaining {result.RemainingBytes} bytes.";
            }
            if (!string.Equals(header.SnapshotToken, tokenAfter, StringComparison.Ordinal))
            {
                var unstable = CreateUnavailableMemoryResult(address, requestedSize);
                unstable.Status = "unstable";
                unstable.Partial = true;
                unstable.Capability = header.Capability;
                unstable.Error = "Debugger context changed while memory was captured; captured bytes were discarded";
                return unstable;
            }
            return result;
        }

        private static bool IsExpressionMemoryFallbackCapability(string capability)
        {
            return !string.IsNullOrEmpty(capability) &&
                (capability.StartsWith("debugger_expression_fallback", StringComparison.Ordinal) ||
                 string.Equals(capability, "debugger_expression_dump_fallback", StringComparison.Ordinal));
        }

        /// <summary>
        /// Captures one paused debugger identity and, when this is a known live
        /// session, pins its verified OS process handle.  A confirmed dump never
        /// receives a live handle: its recorded PID can have been reused by an
        /// unrelated process.
        /// </summary>
        private MemoryReadHeader CaptureMemoryReadHeader(bool includeTrustedLiveHandle = true)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                if (debugger == null || debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    return null;
                }

                string dumpPathCandidate = GetActiveDumpFilePathCandidate();
                int processId = debugger.CurrentProcess?.ProcessID ?? 0;
                bool isDumpCandidate = IsDumpFilePath(dumpPathCandidate);
                bool isTrustedLiveProcess = false;
                bool isVerifiedCurrentLiveProcess = false;
                IntPtr processHandle = IntPtr.Zero;
                try
                {
                    if (includeTrustedLiveHandle && !isDumpCandidate && processId > 0)
                    {
                        processHandle = OpenTrustedLiveProcessHandle(
                            processId,
                            PROCESS_VM_READ | PROCESS_QUERY_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION);
                        isTrustedLiveProcess = processHandle != IntPtr.Zero;
                        if (!isTrustedLiveProcess)
                        {
                            processHandle = OpenVerifiedCurrentLiveProcessHandle(
                                debugger,
                                processId,
                                PROCESS_VM_READ | PROCESS_QUERY_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION);
                            isVerifiedCurrentLiveProcess = processHandle != IntPtr.Zero;
                        }
                    }
                    return new MemoryReadHeader
                    {
                        ProcessId = processId,
                        SnapshotToken = CreateDebuggerSnapshotToken(debugger),
                        DumpPathCandidate = dumpPathCandidate,
                        // The OS handle, if present, pins PID identity while the
                        // background read runs and prevents PID reuse from changing the
                        // target between capture and ReadProcessMemory.
                        ProcessHandle = processHandle,
                        Capability = isDumpCandidate
                            ? "debugger_engine_dump_read"
                            : (isTrustedLiveProcess
                                ? "trusted_live_process_read"
                                : (isVerifiedCurrentLiveProcess
                                    ? "verified_live_process_read"
                                    : "debugger_expression_fallback_untrusted_session")),
                    };
                }
                catch
                {
                    if (processHandle != IntPtr.Zero)
                    {
                        CloseHandle(processHandle);
                    }
                    throw;
                }
            });
        }

        /// <summary>
        /// Establishes an ephemeral direct-read handle for a manually started
        /// local debug session. PID equality alone is unsafe because a dump can
        /// preserve a PID that a later process has reused. The DTE current process
        /// must be the same COM identity as a local-process entry before and
        /// after the OS handle is opened. The second check closes the PID-reuse
        /// window before that handle is recorded as trusted. Failure falls back
        /// safely to the debugger engine.
        /// </summary>
        private IntPtr OpenVerifiedCurrentLiveProcessHandle(
            Debugger5 debugger,
            int processId,
            uint desiredAccess)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (debugger == null || processId <= 0)
            {
                return IntPtr.Zero;
            }

            IntPtr handle = IntPtr.Zero;
            try
            {
                EnvDTE.Process current = debugger.CurrentProcess;
                if (!IsCurrentLocalProcessIdentity(debugger, current, processId))
                {
                    return IntPtr.Zero;
                }

                handle = OpenProcess(desiredAccess, false, processId);
                if (handle == IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }
                if (!TryGetProcessStartTimeUtcTicks(handle, out long startTimeUtcTicks) ||
                    startTimeUtcTicks <= 0 ||
                    !IsCurrentLocalProcessIdentity(debugger, current, processId))
                {
                    return IntPtr.Zero;
                }

                // The handle now pins the verified process object while the direct
                // memory read runs, and its creation time protects later reuse.
                BindTrustedLiveProcessIdentity(
                    processId,
                    startTimeUtcTicks,
                    _sessionTracker.Generation);
                IntPtr verifiedHandle = handle;
                handle = IntPtr.Zero;
                return verifiedHandle;
            }
            catch (Exception ex)
            {
                RethrowRequestControlFlow(ex);
                // VS transports can expose non-local or transient process entries.
                // Never let an identity probe turn a read-only request into a failure.
            }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    CloseHandle(handle);
                }
            }

            return IntPtr.Zero;
        }

        private bool IsCurrentLocalProcessIdentity(
            Debugger5 debugger,
            EnvDTE.Process expectedCurrent,
            int processId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (debugger == null || expectedCurrent == null || processId <= 0 ||
                debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
            {
                return false;
            }

            EnvDTE.Process current = debugger.CurrentProcess;
            if (current == null || current.ProcessID != processId ||
                !HasSameComIdentity(expectedCurrent, current))
            {
                return false;
            }

            // Resolve the collection once. Reading debugger.LocalProcesses again per probe
            // costs ~200 ms through the automation layer, which made this gate take minutes
            // on a machine with hundreds of processes.
            EnvDTE.Processes localProcesses = debugger.LocalProcesses;
            if (localProcesses == null)
            {
                return false;
            }

            // The DTE current process names its local entry, so a single lookup confirms the
            // common case; it only ever confirms, never rejects, and the bounded walk below
            // still covers entries the name lookup cannot resolve.
            string currentName = expectedCurrent.Name;
            if (!string.IsNullOrWhiteSpace(currentName))
            {
                try
                {
                    EnvDTE.Process byName = localProcesses.Item(currentName);
                    if (byName != null && byName.ProcessID == processId &&
                        HasSameComIdentity(expectedCurrent, byName))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Fall through to the bounded walk.
                }
            }

            const int MaxLocalProcessIdentityProbeCount = 256;
            int count = localProcesses.Count;
            if (count <= 0 || count > MaxLocalProcessIdentityProbeCount)
            {
                return false;
            }

            for (int index = 1; index <= count; index++)
            {
                if ((index & 0x1F) == 0)
                {
                    DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                }

                EnvDTE.Process candidate = localProcesses.Item(index);
                if (candidate != null && candidate.ProcessID == processId &&
                    HasSameComIdentity(expectedCurrent, candidate))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasSameComIdentity(object left, object right)
        {
            if (left == null || right == null)
            {
                return false;
            }

            IntPtr leftUnknown = IntPtr.Zero;
            IntPtr rightUnknown = IntPtr.Zero;
            try
            {
                leftUnknown = Marshal.GetIUnknownForObject(left);
                rightUnknown = Marshal.GetIUnknownForObject(right);
                return leftUnknown != IntPtr.Zero && leftUnknown == rightUnknown;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (leftUnknown != IntPtr.Zero)
                {
                    Marshal.Release(leftUnknown);
                }
                if (rightUnknown != IntPtr.Zero)
                {
                    Marshal.Release(rightUnknown);
                }
            }
        }

        private static void CloseMemoryReadHandle(MemoryReadHeader header)
        {
            if (header == null || header.ProcessHandle == IntPtr.Zero)
            {
                return;
            }

            CloseHandle(header.ProcessHandle);
            header.ProcessHandle = IntPtr.Zero;
        }

        /// <summary>
        /// The debugger-engine fallback is intentionally bounded, but it must not
        /// schedule one UI request per four-byte word.  The old 35ms dispatch
        /// slices routinely expired before VS could dequeue them in manually
        /// started live sessions.  Read a small prefix in grouped UI slices instead.
        /// </summary>
        [SuppressMessage("Usage", "VSTHRD010", Justification = "Each debugger-expression group is invoked only inside ExecuteOnMainThread; the byte aggregation runs off-thread.")]
        private byte[] ReadMemoryExpressionPrefix(ulong baseAddress, int requestedSize)
        {
            const int ExpressionWordsPerUiSlice = 8;
            const int ExpressionFallbackBudgetMs = 1500;

            int boundedBytes = Math.Min(Math.Max(0, requestedSize), ExpressionFallbackMaxBytes);
            if (boundedBytes == 0)
            {
                return Array.Empty<byte>();
            }

            var bytes = new List<byte>(boundedBytes);
            var budget = Stopwatch.StartNew();
            while (bytes.Count < boundedBytes && budget.ElapsedMilliseconds < ExpressionFallbackBudgetMs)
            {
                DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                ulong sliceAddress = baseAddress + (ulong)bytes.Count;
                int sliceRemaining = boundedBytes - bytes.Count;
                byte[] slice = ExecuteOnMainThread(
                    () => ReadMemoryWords(
                        Debugger,
                        sliceAddress,
                        sliceRemaining,
                        ExpressionWordsPerUiSlice),
                    ExpressionEvaluationTimeoutMs);
                if (slice == null || slice.Length == 0)
                {
                    break;
                }

                bytes.AddRange(slice);
                if (slice.Length < Math.Min(sliceRemaining, ExpressionWordsPerUiSlice * sizeof(uint)))
                {
                    break;
                }
            }

            return bytes.ToArray();
        }

        private byte[] ReadMemoryWords(
            Debugger5 debugger,
            ulong address,
            int remaining,
            int maxWords)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (debugger == null || debugger.CurrentMode != dbgDebugMode.dbgBreakMode ||
                remaining <= 0 || maxWords <= 0)
            {
                return Array.Empty<byte>();
            }

            var bytes = new List<byte>(Math.Min(remaining, maxWords * sizeof(uint)));
            for (int wordIndex = 0; wordIndex < maxWords && bytes.Count < remaining; wordIndex++)
            {
                DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                byte[] word = ReadMemoryWord(
                    debugger,
                    address + (ulong)bytes.Count,
                    remaining - bytes.Count);
                if (word == null || word.Length == 0)
                {
                    break;
                }

                bytes.AddRange(word);
                if (word.Length < sizeof(uint))
                {
                    break;
                }
            }
            return bytes.ToArray();
        }

        private byte[] ReadRawMinidumpMemory(string dumpPath, ulong address, int requestedSize)
        {
            if (string.IsNullOrEmpty(dumpPath) || requestedSize <= 0)
            {
                return null;
            }

            try
            {
                using (var reader = MinidumpMemoryReader.Open(dumpPath))
                {
                    return reader.Read(address, requestedSize);
                }
            }
            catch (Exception ex)
            {
                RethrowRequestControlFlow(ex);
                _log($"ReadRawMinidumpMemory failed: {ex.GetType().Name}");
                return null;
            }
        }

        /// <summary>
        /// Enumerate the virtual-address ranges physically present in the dump
        /// selected by the active Visual Studio session.  This is metadata-only:
        /// it never scans the dump payload or opens an arbitrary caller path.
        /// </summary>
        public DumpMemoryRangesResult GetDumpMemoryRanges(
            int startIndex = 0,
            int maxNodes = -1,
            int budgetMs = -1)
        {
            if (startIndex < 0)
            {
                throw new DebuggerParameterValidationException(
                    "startIndex must be non-negative.");
            }

            int normalizedMaxNodes = NormalizeCollectionNodeLimit(maxNodes);
            int normalizedBudgetMs = NormalizeCollectionBudgetMs(budgetMs);
            var result = new DumpMemoryRangesResult
            {
                Capability = "raw_minidump_memory_map_unavailable",
                StartIndex = startIndex,
                MaxNodes = normalizedMaxNodes,
                BudgetMs = normalizedBudgetMs,
                SnapshotStatus = "unavailable",
            };

            MemoryReadHeader header = ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                if (debugger == null || debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    return null;
                }

                string dumpPathCandidate = GetActiveDumpFilePathCandidate();
                return new MemoryReadHeader
                {
                    SnapshotToken = CreateDebuggerSnapshotToken(debugger),
                    DumpPathCandidate = dumpPathCandidate,
                };
            });
            if (header == null)
            {
                result.Error = "Not in break mode";
                return result;
            }

            string dumpPath = NormalizeExistingDumpPath(header.DumpPathCandidate);
            if (string.IsNullOrEmpty(dumpPath))
            {
                result.Error = "The active Visual Studio session is not an opened dump";
                return result;
            }

            result.DumpPath = dumpPath;
            try
            {
                using (var reader = MinidumpMemoryReader.Open(dumpPath))
                {
                    result.Capability = "raw_minidump_memory_map";
                    result.Source = reader.Source;
                    result.RangeCount = reader.Ranges.Count;
                    result.TotalMappedBytes = checked((long)reader.TotalMappedBytes);
                    int begin = Math.Min(startIndex, reader.Ranges.Count);
                    result.StartIndex = begin;
                    var stopwatch = Stopwatch.StartNew();
                    for (int index = begin; index < reader.Ranges.Count; index++)
                    {
                        DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                        if (stopwatch.ElapsedMilliseconds >= normalizedBudgetMs)
                        {
                            result.HasMore = true;
                            result.Partial = true;
                            result.NextIndex = index;
                            result.Error = $"Dump memory-range enumeration exceeded its {normalizedBudgetMs}ms budget";
                            break;
                        }
                        if (result.Ranges.Count >= normalizedMaxNodes)
                        {
                            result.HasMore = true;
                            result.Partial = true;
                            result.NextIndex = index;
                            break;
                        }

                        MinidumpMemoryRange range = reader.Ranges[index];
                        result.Ranges.Add(new DumpMemoryRangeInfo
                        {
                            StartAddress = FormatAddress(range.StartAddress),
                            EndAddressExclusive = FormatAddress(range.EndAddress),
                            SizeBytes = checked((long)range.Size),
                        });
                    }

                    if (!result.HasMore)
                    {
                        result.Status = "ok";
                        result.SnapshotStatus = "best_effort";
                    }
                    else
                    {
                        result.Status = "partial";
                        result.SnapshotStatus = "best_effort";
                    }
                }
            }
            catch (Exception ex)
            {
                RethrowRequestControlFlow(ex);
                result.Status = "unavailable";
                result.Partial = true;
                result.Error = $"Could not parse active minidump memory ranges: {ex.Message}";
                _log($"GetDumpMemoryRanges failed: {ex.GetType().Name}");
                return result;
            }

            if (!IsMemorySnapshotCurrent(header))
            {
                result.Status = "unstable";
                result.Partial = true;
                result.HasMore = false;
                result.NextIndex = null;
                result.Ranges.Clear();
                result.RangeCount = 0;
                result.TotalMappedBytes = 0;
                result.Source = null;
                result.SnapshotStatus = "unstable";
                result.Error = "Debugger context changed while dump memory ranges were captured; ranges were discarded";
            }

            return result;
        }

        private bool IsMemorySnapshotCurrent(MemoryReadHeader header)
        {
            if (header == null)
            {
                return false;
            }
            string current = ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return CreateDebuggerSnapshotToken(Debugger);
            });
            return !string.IsNullOrEmpty(header.SnapshotToken) &&
                string.Equals(header.SnapshotToken, current, StringComparison.Ordinal);
        }

        private byte[] ReadMemoryWord(Debugger5 debugger, ulong address, int remaining)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (debugger == null || debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
            {
                return Array.Empty<byte>();
            }
            EnvDTE.Expression expression = debugger.GetExpression(
                $"*(unsigned int*)(0x{address:X})",
                false,
                25);
            if (expression == null || !expression.IsValidValue)
            {
                return Array.Empty<byte>();
            }
            uint? value = ParseUInt(expression.Value);
            if (!value.HasValue)
            {
                return Array.Empty<byte>();
            }
            return BitConverter.GetBytes(value.Value).Take(Math.Min(4, remaining)).ToArray();
        }

        private MemoryResult BuildMemoryResult(
            string requestedAddress,
            int requestedSize,
            ulong baseAddress,
            bool is64Bit,
            byte[] memory)
        {
            var result = CreateUnavailableMemoryResult(requestedAddress, requestedSize);
            result.Address = is64Bit ? $"0x{baseAddress:X16}" : $"0x{baseAddress:X8}";
            if (memory == null || memory.Length == 0)
            {
                return result;
            }
            result.Bytes = memory.ToList();
            result.Size = memory.Length;
            result.BytesRead = memory.Length;
            result.FailedReads = Math.Max(0, requestedSize - memory.Length);
            result.Partial = result.FailedReads > 0;
            result.Status = result.Partial ? "partial" : "ok";
            result.HexDump = string.Join(" ", memory.Select(value => value.ToString("X2")));
            result.AsciiView = new string(memory.Select(value => value >= 32 && value < 127 ? (char)value : '.').ToArray());
            var formatted = new StringBuilder();
            for (int offset = 0; offset < memory.Length; offset += 16)
            {
                byte[] line = memory.Skip(offset).Take(16).ToArray();
                formatted.AppendFormat(
                    "{0}+{1:X4}: {2} | {3}",
                    result.Address,
                    offset,
                    string.Join(" ", line.Select(value => value.ToString("X2"))),
                    new string(line.Select(value => value >= 32 && value < 127 ? (char)value : '.').ToArray()));
                if (offset + line.Length < memory.Length) formatted.AppendLine();
            }
            result.FormattedView = formatted.ToString();
            return result;
        }

        private sealed class MemoryReadHeader
        {
            internal int ProcessId { get; set; }
            internal string SnapshotToken { get; set; }
            internal string DumpPathCandidate { get; set; }
            internal IntPtr ProcessHandle { get; set; }
            internal string Capability { get; set; }
        }

        private MemoryResult CreateUnavailableMemoryResult(string address, int requestedSize)
        {
            return new MemoryResult
            {
                Address = address,
                Status = "unavailable",
                RequestedSize = requestedSize,
                Size = 0,
                BytesRead = 0,
                FailedReads = requestedSize
                ,Capability = "debugger_memory_read_unavailable"
            };
        }

        #endregion
    }
}
