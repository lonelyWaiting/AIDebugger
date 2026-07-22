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
        /// Search for a pattern in memory using optimized batch reading.
        /// Performance: Reads memory in chunks instead of byte-by-byte, reducing IPC calls from O(n) to O(n/4).
        /// Automatically handles thread context issues by trying to switch to user code thread if needed.
        /// </summary>
        [SuppressMessage("Usage", "VSTHRD010", Justification = "ReadMemory self-marshals bounded debugger validation/fallback slices; byte search runs off-thread.")]
        public MemorySearchResult SearchMemory(string startAddress, string pattern, int maxResults = 100, int searchSize = 0x10000, string patternFormat = "auto", long startOffset = 0)
        {
            int requestedBytes = Math.Max(1, Math.Min(searchSize, 0x100000));
            int resultLimit = Math.Max(1, Math.Min(maxResults, 10000));
            var result = CreateUnavailableMemorySearchResult(pattern, requestedBytes);
            byte[] patternBytes = SearchPatternParser.Parse(pattern, patternFormat);
            if (!TryParseAddressLiteral(startAddress, out ulong baseAddress))
            {
                result.Error = "Invalid start address";
                return result;
            }
            ulong offsetValue = startOffset > 0 ? (ulong)startOffset : 0UL;
            if (baseAddress > ulong.MaxValue - offsetValue)
            {
                result.Error = "Start offset overflows resolved address";
                return result;
            }
            baseAddress += offsetValue;
            if ((ulong)(requestedBytes - 1) > ulong.MaxValue - baseAddress)
            {
                result.Error = "Search range overflows address space";
                return result;
            }
            result.PatternBytes = patternBytes.Select(value => value.ToString("X2")).ToArray();

            const int chunkSize = 4096;
            var carry = new List<byte>(Math.Max(0, patternBytes.Length - 1));
            int nextCandidateOffset = 0;
            for (int offset = 0; offset < requestedBytes; offset += chunkSize)
            {
                DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                int readSize = Math.Min(chunkSize, requestedBytes - offset);
                result.BytesAttempted += readSize;
                ulong chunkAddress = baseAddress + (ulong)offset;
                MemoryResult chunk = ReadMemory(FormatAddress(chunkAddress), readSize);
                if (string.IsNullOrEmpty(result.Capability))
                {
                    result.Capability = chunk.Capability;
                }
                else if (!string.Equals(result.Capability, chunk.Capability, StringComparison.Ordinal))
                {
                    result.Capability = "mixed_memory_read_strategies";
                }
                int available = Math.Min(chunk.Bytes?.Count ?? 0, readSize);
                result.BytesSearched += available;
                if (available == 0)
                {
                    result.Partial = true;
                    SetMemorySearchContinuation(
                        result, startOffset, baseAddress, offset + readSize, requestedBytes);
                    break;
                }

                int combinedBaseOffset = offset - carry.Count;
                var combined = new List<byte>(carry.Count + available);
                combined.AddRange(carry);
                combined.AddRange(chunk.Bytes.Take(available));
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
                    if (!matched) continue;
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
                if (result.Truncated) break;
                if (available < readSize)
                {
                    result.Partial = true;
                    SetMemorySearchContinuation(
                        result,
                        startOffset,
                        baseAddress,
                        chunk.HasMore ? offset + available : offset + readSize,
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
