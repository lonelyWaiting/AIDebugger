using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using EnvDTE;
using EnvDTE80;
using EnvDTE100;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VSDebuggerMCP.Ipc;
using VSDebuggerMCP.Protocol;
using VSDebuggerMCP.Services;

namespace VSDebuggerMCP
{
    public partial class DebuggerService
    {
        #region Registers

        /// <summary>
        /// Get CPU registers.
        /// </summary>
        [SuppressMessage("Usage", "VSTHRD010", Justification = "Architecture and individual registers are captured through bounded UI slices.")]
        public RegistersResult GetRegisters()
        {
            var result = new RegistersResult
            {
                Capability = "sliced_x86_x64_register_snapshot"
            };
            TargetMachineArchitecture architecture = DetectTargetArchitecture();
            result.Architecture = TargetArchitecturePolicy.ToProtocolName(architecture);
            bool? is64Bit = TargetArchitecturePolicy.Is64Bit(architecture);
            if (!TargetArchitecturePolicy.IsIcedSupported(architecture))
            {
                bool unknown = architecture == TargetMachineArchitecture.Unknown;
                throw new DebuggerStateRequirementException(
                    unknown ? "TARGET_ARCHITECTURE_UNKNOWN" : "ARCHITECTURE_NOT_SUPPORTED",
                    unknown
                        ? "Target architecture is unavailable; registers were not guessed from the Visual Studio host architecture"
                        : $"The register set for {result.Architecture} is not supported by this VS adapter");
            }
            result.Is64Bit = is64Bit.Value;
            result.ArchitectureStatus = "supported";
            IReadOnlyList<string> registerNames = RegisterCapturePolicy.BuildRegisterNames(architecture);
            string snapshotTokenBefore = ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return CreateDebuggerSnapshotToken(Debugger);
            });

            var errors = new List<string>();
            var unavailableRegisters = new List<string>();
            int captureErrorCount = 0;
            foreach (string registerName in registerNames)
            {
                DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                try
                {
                    string capturedName = registerName;
                    RegisterInfo register = ExecuteOnMainThread(() => CaptureRegister(capturedName, architecture));
                    if (register != null)
                    {
                        result.Registers.Add(register);
                    }
                    else
                    {
                        unavailableRegisters.Add(registerName);
                    }
                }
                catch (Exception ex)
                {
                    RethrowRequestControlFlow(ex);
                    captureErrorCount++;
                    unavailableRegisters.Add(registerName);
                    if (errors.Count < 4) errors.Add(registerName + ": " + ex.Message);
                }
            }
            if (captureErrorCount > errors.Count)
                errors.Add($"{captureErrorCount - errors.Count} additional register capture error(s)");
            string snapshotTokenAfter = ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return CreateDebuggerSnapshotToken(Debugger);
            });
            RegisterCapturePolicy.Complete(
                result,
                registerNames,
                unavailableRegisters,
                !string.IsNullOrEmpty(snapshotTokenBefore) &&
                    string.Equals(snapshotTokenBefore, snapshotTokenAfter, StringComparison.Ordinal),
                errors.Count == 0 ? null : string.Join("; ", errors));
            return result;
        }

        private RegisterInfo CaptureRegister(
            string registerName,
            TargetMachineArchitecture architecture)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var expression = Debugger?.GetExpression("@" + registerName, false, 250);
            if (expression == null || !expression.IsValidValue)
            {
                return null;
            }
            string value = expression.Value;
            return new RegisterInfo
            {
                Name = registerName.ToUpperInvariant(),
                Value = value,
                HexValue = RegisterCapturePolicy.FormatScalarValueAsHex(
                    architecture, registerName, value),
                Category = registerName.StartsWith("xmm", StringComparison.OrdinalIgnoreCase)
                    ? "SSE"
                    : (new[] { "cs", "ds", "es", "fs", "gs", "ss" }.Contains(registerName)
                        ? "Segment"
                        : GetRegisterCategory(registerName))
            };
        }

        private string GetRegisterCategory(string reg)
        {
            reg = reg.ToLower();
            if (reg.Contains("ip") || reg.Contains("rip") || reg.Contains("eip"))
                return "Instruction Pointer";
            if (reg.Contains("sp") || reg.Contains("bp"))
                return "Stack";
            if (reg.Contains("flags"))
                return "Flags";
            if (reg.StartsWith("r") && reg.Length <= 3)
                return "General Purpose";
            return "General Purpose";
        }

        #endregion

        #region Watch

        /// <summary>
        /// Add a watch expression.
        /// </summary>
        public WatchResult AddWatch(string expression)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new WatchResult
                {
                    Expression = expression,
                    Behavior = "persisted_watch_without_pre_evaluation",
                    EvaluationStatus = "not_started"
                };

                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    result.Success = false;
                    result.Error = "Not in break mode";
                    result.Message = result.Error;
                    result.ErrorCode = "NOT_PAUSED";
                    result.Outcome = "watch_not_started";
                    return result;
                }

                try
                {
                    // Debug.AddWatch can evaluate the expression in the Watch window.
                    // Do not pre-evaluate it here: doing so could execute user code twice.
                    // Mark the possible side effect before dispatch so every timeout,
                    // cancellation or COM failure remains non-replayable.
                    result.CommandIssued = true;
                    result.EvaluationIssued = true;
                    result.EvaluationStatus = "watch_window_evaluation_may_run";
                    MarkUnsafeEvaluationIssued();
                    _dte.ExecuteCommand("Debug.AddWatch", expression);
                    result.Success = true;
                    result.Outcome = "watch_persisted";
                    result.Message = "Watch expression was added without a separate pre-evaluation";
                }
                catch (Exception ex)
                {
                    RethrowRequestControlFlow(ex);
                    throw;
                }

                return result;
            });
        }

        #endregion

        #region Memory Search

        /// <summary>
        /// Search memory without making the caller select a dump-specific API.
        /// A known active dump is searched through its mapped minidump bytes; an
        /// active live process searches the requested numeric range.  Omitting
        /// startAddress is meaningful only for a dump and scans its mapped ranges
        /// with the opaque continuation returned by a prior page.
        /// </summary>
        [SuppressMessage("Usage", "VSTHRD010", Justification = "The initial/final debugger identity checks are bounded UI slices; raw process and dump reads run off the UI thread.")]
        public MemorySearchResult SearchMemory(
            string startAddress,
            string pattern,
            int maxResults = 100,
            int searchSize = 0x10000,
            string patternFormat = "auto",
            long startOffset = 0,
            string continuation = null)
        {
            int requestedBytes = Math.Max(1, Math.Min(searchSize, 0x100000));
            int resultLimit = Math.Max(1, Math.Min(maxResults, 10000));
            var unavailable = CreateUnavailableMemorySearchResult(pattern, requestedBytes);
            byte[] patternBytes = SearchPatternParser.Parse(pattern, patternFormat);
            bool hasStartAddress = !string.IsNullOrWhiteSpace(startAddress);
            ulong baseAddress = 0;
            if (hasStartAddress && !TryParseAddressLiteral(startAddress, out baseAddress))
            {
                unavailable.Error = "Invalid start address";
                return unavailable;
            }
            if (!hasStartAddress && startOffset != 0)
            {
                unavailable.Error = "startOffset requires startAddress";
                return unavailable;
            }
            if (hasStartAddress)
            {
                if (startOffset < 0)
                {
                    unavailable.Error = "startOffset must be non-negative";
                    return unavailable;
                }
                if (startOffset > long.MaxValue - (requestedBytes - 1))
                {
                    unavailable.Error = "Start offset plus search range exceeds the supported protocol range";
                    return unavailable;
                }
                ulong offsetValue = (ulong)startOffset;
                if (baseAddress > ulong.MaxValue - offsetValue)
                {
                    unavailable.Error = "Start offset overflows resolved address";
                    return unavailable;
                }
                baseAddress += offsetValue;
                if ((ulong)(requestedBytes - 1) > ulong.MaxValue - baseAddress)
                {
                    unavailable.Error = "Search range overflows address space";
                    return unavailable;
                }
            }

            MemoryReadHeader header = CaptureMemoryReadHeader();
            if (header == null)
            {
                unavailable.Error = "Not in break mode";
                return unavailable;
            }

            MemorySearchResult result;
            string dumpPath = NormalizeExistingDumpPath(header.DumpPathCandidate);
            try
            {
                if (!string.IsNullOrEmpty(dumpPath))
                {
                    result = SearchMappedDumpMemory(
                        dumpPath,
                        header.SnapshotToken,
                        hasStartAddress,
                        baseAddress,
                        pattern,
                        patternBytes,
                        resultLimit,
                        requestedBytes,
                        startOffset,
                        continuation);
                }
                else if (!string.IsNullOrWhiteSpace(continuation))
                {
                    result = CreateUnavailableMemorySearchResult(pattern, requestedBytes);
                    result.Error = "The continuation belongs to a mapped dump search, but the active target is live";
                }
                else if (!hasStartAddress)
                {
                    result = CreateUnavailableMemorySearchResult(pattern, requestedBytes);
                    result.Error = "startAddress is required when the active target is not a recognized dump";
                }
                else if (header.ProcessHandle != IntPtr.Zero)
                {
                    // Keep one verified handle for the entire search.  The previous
                    // implementation re-captured DTE state for every 4KiB chunk,
                    // which made large live searches prone to UI-dispatch timeouts.
                    result = SearchMemoryByReader(
                        pattern,
                        patternBytes,
                        baseAddress,
                        startOffset,
                        requestedBytes,
                        resultLimit,
                        (address, size) => ReadProcessMemoryContiguous(header.ProcessHandle, address, size),
                        header.Capability);
                }
                else
                {
                    // Do not open an unverified process from a manually started
                    // session: a dump's recorded PID can be reused.  The bounded
                    // debugger-engine fallback now runs in grouped UI slices, rather
                    // than one 35ms dispatch per word.
                    if (patternBytes.Length > ExpressionFallbackMaxBytes)
                    {
                        result = CreateUnavailableMemorySearchResult(pattern, requestedBytes);
                        result.Capability = "debugger_expression_fallback_untrusted_session";
                        result.Partial = true;
                        result.Error = $"Search patterns longer than {ExpressionFallbackMaxBytes} bytes " +
                            "cannot be evaluated through the bounded debugger-expression fallback";
                    }
                    else
                    {
                        bool fallbackReadIssued = false;
                        result = SearchMemoryByReader(
                            pattern,
                            patternBytes,
                            baseAddress,
                            startOffset,
                            requestedBytes,
                            resultLimit,
                            (address, size) =>
                            {
                                if (fallbackReadIssued)
                                {
                                    return Array.Empty<byte>();
                                }
                                fallbackReadIssued = true;
                                return ReadMemoryExpressionPrefix(address, size);
                            },
                            "debugger_expression_fallback_untrusted_session");
                    }
                }
            }
            catch (Exception ex)
            {
                RethrowRequestControlFlow(ex);
                result = CreateUnavailableMemorySearchResult(pattern, requestedBytes);
                result.Capability = !string.IsNullOrEmpty(dumpPath)
                    ? "raw_minidump_memory_search_unavailable"
                    : header.Capability;
                result.Partial = true;
                result.Error = !string.IsNullOrEmpty(dumpPath)
                    ? $"Could not search active minidump memory: {ex.Message}"
                    : $"Could not search active memory: {ex.Message}";
                _log($"SearchMemory failed: {ex.GetType().Name}");
            }
            finally
            {
                CloseMemoryReadHandle(header);
            }

            if (!IsMemorySnapshotCurrent(header))
            {
                var unstable = CreateUnavailableMemorySearchResult(pattern, requestedBytes);
                unstable.Capability = result.Capability;
                unstable.Source = result.Source;
                unstable.Status = "unstable";
                unstable.Partial = true;
                unstable.Error = "Debugger context changed while memory was searched; matches were discarded";
                return unstable;
            }

            return result;
        }

        private MemorySearchResult SearchMappedDumpMemory(
            string dumpPath,
            string snapshotToken,
            bool hasStartAddress,
            ulong baseAddress,
            string pattern,
            byte[] patternBytes,
            int resultLimit,
            int requestedBytes,
            long startOffset,
            string continuation)
        {
            using (var reader = MinidumpMemoryReader.Open(dumpPath))
            {
                if (hasStartAddress)
                {
                    if (!string.IsNullOrWhiteSpace(continuation))
                    {
                        var invalid = CreateUnavailableMemorySearchResult(pattern, requestedBytes);
                        invalid.Capability = "raw_minidump_memory_search";
                        invalid.Source = reader.Source;
                        invalid.Error = "continuation cannot be combined with startAddress";
                        return invalid;
                    }

                    return SearchMemoryByReader(
                        pattern,
                        patternBytes,
                        baseAddress,
                        startOffset,
                        requestedBytes,
                        resultLimit,
                        (address, size) => reader.Read(address, size),
                        "raw_minidump_memory_search",
                        reader.Source);
                }

                string dumpIdentity = MappedDumpSearchContinuationCodec.CreateDumpIdentity(dumpPath);
                if (!MappedDumpSearchContinuationCodec.TryDecode(
                    continuation,
                    patternBytes.Length,
                    dumpIdentity,
                    snapshotToken,
                    patternBytes,
                    out int rangeIndex,
                    out ulong rangeOffset,
                    out int continuationCarryBytes,
                    out string continuationError))
                {
                    var invalid = CreateUnavailableMemorySearchResult(pattern, requestedBytes);
                    invalid.Capability = "raw_minidump_memory_search";
                    invalid.Source = reader.Source;
                    invalid.Error = continuationError;
                    return invalid;
                }

                MinidumpMemorySearchPage page = reader.Search(
                    patternBytes,
                    resultLimit,
                    requestedBytes,
                    rangeIndex,
                    rangeOffset,
                    continuationCarryBytes);
                var result = CreateUnavailableMemorySearchResult(pattern, requestedBytes);
                result.PatternBytes = patternBytes.Select(value => value.ToString("X2")).ToArray();
                result.Capability = "raw_minidump_memory_search";
                result.Source = reader.Source;
                result.Matches = page.Matches.Select(FormatAddress).ToList();
                result.TotalFound = result.Matches.Count;
                result.BytesSearched = page.BytesSearched;
                result.BytesAttempted = page.BytesAttempted;
                result.FailedReads = Math.Max(0, page.BytesAttempted - page.BytesSearched);
                result.UnreadableBytes = result.FailedReads;
                result.UnattemptedBytes = page.HasMore
                    ? Math.Max(0, requestedBytes - page.BytesAttempted)
                    : 0;
                result.Partial = page.Partial;
                result.Truncated = page.Truncated;
                result.HasMore = page.HasMore;
                result.Error = page.Error;
                if (page.NextRangeIndex.HasValue && page.NextRangeOffset.HasValue)
                {
                    if (page.NextRangeOffset.Value > long.MaxValue)
                    {
                        result.Partial = true;
                        result.HasMore = false;
                        result.Error = "Mapped memory continuation offset exceeds the supported protocol range";
                    }
                    else
                    {
                        result.Continuation = MappedDumpSearchContinuationCodec.Create(
                            page.NextRangeIndex.Value,
                            checked((long)page.NextRangeOffset.Value),
                            page.NextCarryBytes,
                            dumpIdentity,
                            snapshotToken,
                            patternBytes);
                    }
                }
                result.Status = result.BytesSearched == 0 && !result.HasMore &&
                    !string.IsNullOrEmpty(result.Error)
                    ? "unavailable"
                    : (result.Partial ? "partial" : "ok");
                return result;
            }
        }

        private MemorySearchResult SearchMemoryByReader(
            string pattern,
            byte[] patternBytes,
            ulong baseAddress,
            long startOffset,
            int requestedBytes,
            int resultLimit,
            Func<ulong, int, byte[]> read,
            string capability,
            string source = null)
        {
            var result = CreateUnavailableMemorySearchResult(pattern, requestedBytes);
            result.PatternBytes = patternBytes.Select(value => value.ToString("X2")).ToArray();
            result.Capability = capability;
            result.Source = source;

            const int chunkSize = 64 * 1024;
            var carry = new List<byte>(Math.Max(0, patternBytes.Length - 1));
            int nextCandidateOffset = 0;
            for (int offset = 0; offset < requestedBytes; offset += chunkSize)
            {
                DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                int readSize = Math.Min(chunkSize, requestedBytes - offset);
                byte[] bytes = read(baseAddress + (ulong)offset, readSize) ?? Array.Empty<byte>();
                int available = Math.Min(bytes.Length, readSize);
                // A short result from the debugger-expression fallback, or an
                // unsuccessful direct read, does not prove that the rest of this
                // requested chunk is unreadable. Count only bytes actually returned
                // as attempted so the remaining interval stays explicitly
                // inconclusive instead of being reported as a failed read.
                result.BytesAttempted += available;
                result.BytesSearched += available;
                if (available == 0)
                {
                    result.Partial = true;
                    result.Error = "Memory read made no progress; the remaining range was not skipped";
                    break;
                }

                int combinedBaseOffset = offset - carry.Count;
                var combined = new List<byte>(carry.Count + available);
                combined.AddRange(carry);
                combined.AddRange(bytes.Take(available));
                int lastCandidateExclusive = Math.Min(
                    Math.Max(0, requestedBytes - patternBytes.Length + 1),
                    combinedBaseOffset + Math.Max(0, combined.Count - patternBytes.Length + 1));
                for (int candidateOffset = Math.Max(nextCandidateOffset, combinedBaseOffset);
                    candidateOffset < lastCandidateExclusive;
                    candidateOffset++)
                {
                    int index = candidateOffset - combinedBaseOffset;
                    bool matched = true;
                    for (int patternIndex = 0; patternIndex < patternBytes.Length; patternIndex++)
                    {
                        if (combined[index + patternIndex] != patternBytes[patternIndex])
                        {
                            matched = false;
                            break;
                        }
                    }
                    if (!matched)
                    {
                        continue;
                    }

                    ulong matchAddress = baseAddress + (ulong)candidateOffset;
                    result.Matches.Add(FormatAddress(matchAddress));
                    if (result.Matches.Count >= resultLimit)
                    {
                        int relativeNext = candidateOffset + 1;
                        bool hasMoreCandidates = relativeNext <= requestedBytes - patternBytes.Length;
                        result.Truncated = hasMoreCandidates;
                        result.Partial |= hasMoreCandidates;
                        if (hasMoreCandidates)
                        {
                            result.NextStartAddress = FormatAddress(matchAddress + 1);
                            result.NextStartOffset = startOffset <= long.MaxValue - relativeNext
                                ? (long?)(startOffset + relativeNext)
                                : null;
                            result.HasMore = true;
                        }
                        break;
                    }
                }

                nextCandidateOffset = Math.Max(nextCandidateOffset, lastCandidateExclusive);
                int carryCount = Math.Min(patternBytes.Length - 1, combined.Count);
                carry = carryCount > 0
                    ? combined.Skip(combined.Count - carryCount).Take(carryCount).ToList()
                    : new List<byte>();
                if (result.Truncated)
                {
                    break;
                }
                if (available < readSize)
                {
                    result.Partial = true;
                    int continuationOffset = offset + available;
                    if (IsExpressionMemoryFallbackCapability(capability))
                    {
                        // This fallback stopped at its intentional byte budget, not
                        // at an unreadable virtual-memory boundary. Revisit the last
                        // pattern-width bytes so a match spanning two pages is not
                        // lost on the next bounded request.
                        continuationOffset = Math.Max(
                            offset,
                            continuationOffset - Math.Max(0, patternBytes.Length - 1));
                    }
                    SetMemorySearchContinuation(
                        result,
                        startOffset,
                        baseAddress,
                        continuationOffset,
                        requestedBytes);
                    break;
                }
            }

            result.TotalFound = result.Matches.Count;
            result.UnreadableBytes = Math.Max(0, result.BytesAttempted - result.BytesSearched);
            result.FailedReads = result.UnreadableBytes;
            result.UnattemptedBytes = Math.Max(0, requestedBytes - result.BytesAttempted);
            result.Partial = result.Partial || result.UnreadableBytes > 0 ||
                result.UnattemptedBytes > 0 || result.Truncated;
            result.Status = result.BytesSearched == 0 ? "unavailable" : (result.Partial ? "partial" : "ok");
            if ((result.UnreadableBytes > 0 || result.UnattemptedBytes > 0) && string.IsNullOrEmpty(result.Error))
            {
                result.Error = $"Searched {result.BytesSearched} of {result.BytesAttempted} attempted bytes; " +
                    $"{result.UnreadableBytes} were unreadable and {result.UnattemptedBytes} remain unattempted";
            }
            return result;
        }

        private void SetMemorySearchContinuation(
            MemorySearchResult result,
            long startOffset,
            ulong baseAddress,
            int relativeNext,
            int requestedBytes)
        {
            if (relativeNext >= requestedBytes) return;
            result.NextStartAddress = FormatAddress(baseAddress + (ulong)relativeNext);
            result.NextStartOffset = startOffset <= long.MaxValue - relativeNext
                ? (long?)(startOffset + relativeNext)
                : null;
            result.HasMore = true;
        }

        private MemorySearchResult CreateUnavailableMemorySearchResult(string pattern, int searchSize)
        {
            return new MemorySearchResult
            {
                Pattern = pattern,
                Status = "unavailable",
                RequestedBytes = Math.Max(1, Math.Min(searchSize, 0x100000)),
                UnattemptedBytes = Math.Max(1, Math.Min(searchSize, 0x100000))
            };
        }

        #endregion

        #region Output Window

        [SuppressMessage("Usage", "VSTHRD010", Justification = "All output-pane DTE reads are dispatched in bounded ExecuteOnMainThread slices.")]
        public OutputWindowResult GetOutputWindow(
            string pane = "Debug",
            int tailLines = 50,
            int endCharacter = -1,
            int maxBytes = OutputContentBudget.DefaultMaxSerializedContentBytes)
        {
            OutputPaneSnapshot snapshot;
            if (tailLines > 0 && endCharacter < 0)
            {
                snapshot = ExecuteOnMainThread(() =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    return _outputWindowService.CaptureTail(pane, tailLines);
                });
            }
            else
            {
                snapshot = CaptureOutputPaneInSlices(pane, endCharacter);
            }
            return _outputWindowService.BuildResult(snapshot, tailLines, maxBytes);
        }

        [SuppressMessage("Usage", "VSTHRD010", Justification = "All output-pane DTE reads are dispatched in bounded ExecuteOnMainThread slices.")]
        public SearchOutputResult SearchOutput(
            string pattern,
            string pane = "Debug",
            bool isRegex = false,
            int contextLines = 2,
            int maxMatches = 20,
            int startLine = 0,
            int endCharacter = -1,
            int expectedCaptureStartCharacter = -1)
        {
            OutputPaneSnapshot snapshot = CaptureOutputPaneInSlices(pane, endCharacter);
            if ((endCharacter >= 0 && snapshot.CaptureEndCharacter != endCharacter) ||
                (expectedCaptureStartCharacter >= 0 &&
                 snapshot.CaptureStartCharacter != expectedCaptureStartCharacter))
            {
                return _outputWindowService.BuildStaleSearchCursorResult(
                    snapshot,
                    pattern,
                    isRegex,
                    startLine,
                    "The output snapshot anchor changed; restart search_output from start_line=0 without a cursor");
            }
            return _outputWindowService.Search(snapshot, pattern, isRegex, contextLines, maxMatches, startLine);
        }

        [SuppressMessage("Usage", "VSTHRD010", Justification = "Every DTE access is enclosed by ExecuteOnMainThread; slice combination runs off the UI thread.")]
        private OutputPaneSnapshot CaptureOutputPaneInSlices(string pane, int endCharacter)
        {
            OutputPaneSnapshot lastSnapshot = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var snapshot = ExecuteOnMainThread(() =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    return _outputWindowService.CaptureMetadata(pane);
                });
                if (snapshot.Error != null || snapshot.TotalCharacters <= 0)
                {
                    return snapshot;
                }

                int captureEnd = endCharacter < 0
                    ? snapshot.TotalCharacters
                    : Math.Min(endCharacter, snapshot.TotalCharacters);
                int captureStart = Math.Max(
                    0,
                    captureEnd - OutputWindowService.MaxSnapshotCharacters);
                var slices = new List<OutputPaneSlice>();
                bool stable = true;
                for (int offset = captureStart; offset < captureEnd;)
                {
                    DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                    int requested = Math.Min(
                        OutputWindowService.MaxUiSliceCharacters,
                        captureEnd - offset);
                    int capturedOffset = offset;
                    OutputPaneSlice slice = ExecuteOnMainThread(() =>
                    {
                        ThreadHelper.ThrowIfNotOnUIThread();
                        return _outputWindowService.CaptureSlice(pane, capturedOffset, requested);
                    });
                    slices.Add(slice);
                    if (slice.StartCharacter != capturedOffset || slice.Content.Length != requested)
                    {
                        stable = false;
                        break;
                    }
                    offset += requested;
                }
                snapshot = _outputWindowService.CombineSlices(snapshot, slices, captureStart);
                snapshot.CaptureEndCharacter = captureEnd;
                OutputPaneSnapshot endMetadata = ExecuteOnMainThread(() =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    return _outputWindowService.CaptureMetadata(pane);
                });
                stable = stable && endMetadata.Error == null &&
                    string.Equals(snapshot.Pane, endMetadata.Pane, StringComparison.Ordinal) &&
                    endMetadata.TotalCharacters >= snapshot.TotalCharacters;

                if (stable && snapshot.Content.Length > 0)
                {
                    int anchorLength = Math.Min(256, snapshot.Content.Length);
                    OutputPaneSlice startAnchor = ExecuteOnMainThread(() =>
                    {
                        ThreadHelper.ThrowIfNotOnUIThread();
                        return _outputWindowService.CaptureSlice(pane, captureStart, anchorLength);
                    });
                    int endAnchorOffset = captureEnd - anchorLength;
                    OutputPaneSlice endAnchor = ExecuteOnMainThread(() =>
                    {
                        ThreadHelper.ThrowIfNotOnUIThread();
                        return _outputWindowService.CaptureSlice(pane, endAnchorOffset, anchorLength);
                    });
                    stable = startAnchor.StartCharacter == captureStart &&
                        endAnchor.StartCharacter == endAnchorOffset &&
                        string.Equals(
                            startAnchor.Content,
                            snapshot.Content.Substring(0, anchorLength),
                            StringComparison.Ordinal) &&
                        string.Equals(
                            endAnchor.Content,
                            snapshot.Content.Substring(snapshot.Content.Length - anchorLength),
                            StringComparison.Ordinal);
                }

                // EnvDTE exposes no output-pane content generation/version. Even
                // matching length plus first/last anchors cannot prove that a middle
                // slice was not replaced between UI dispatches. Preserve the checks
                // to detect known instability, but never advertise a multi-dispatch
                // capture as strongly stable.
                snapshot.SnapshotStable = false;
                snapshot.SnapshotStatus = stable ? "best_effort" : "unstable";
                snapshot.Partial = true;
                snapshot.Truncated = snapshot.Truncated || !stable;
                lastSnapshot = snapshot;
                if (stable) return snapshot;
            }
            return lastSnapshot ?? new OutputPaneSnapshot
            {
                RequestedPane = pane,
                SnapshotStable = false,
                SnapshotStatus = "unstable",
                Partial = true,
                Error = "Output pane changed while it was being captured.",
            };
        }

        #endregion
    }
}
