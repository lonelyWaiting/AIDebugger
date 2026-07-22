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
        #region Breakpoints

        /// <summary>
        /// Get all breakpoints.
        /// </summary>
        [SuppressMessage("Usage", "VSTHRD010", Justification = "Reads one breakpoint and at most one bound child per bounded UI slice.")]
        public BreakpointsResult GetBreakpoints(int startIndex = 0, int maxNodes = -1, int budgetMs = -1)
        {
            if (startIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(startIndex), "startIndex must be non-negative.");
            }
            int normalizedMaxNodes = NormalizeCollectionNodeLimit(maxNodes);
            int normalizedBudgetMs = NormalizeCollectionBudgetMs(budgetMs);
            BreakpointSliceHeader header = ExecuteOnMainThread(CaptureBreakpointSliceHeader);
            var result = new BreakpointsResult
            {
                MaxNodes = normalizedMaxNodes,
                BudgetMs = normalizedBudgetMs,
                MaxResultBytes = BreakpointCaptureBudget.MaxResultBytes,
                TotalAvailable = header.Count,
                StartIndex = startIndex,
            };
            var captureBudget = new BreakpointCaptureBudget();
            if (!header.Available)
            {
                result.SnapshotStatus = "unavailable";
                result.Error = header.Error ?? "Debugger unavailable";
                return result;
            }

            var stopwatch = Stopwatch.StartNew();
            int inspectedCount = 0;
            for (int index = startIndex + 1; index <= header.Count; index++)
            {
                DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                if (inspectedCount >= normalizedMaxNodes ||
                    stopwatch.ElapsedMilliseconds >= normalizedBudgetMs)
                {
                    result.Partial = result.Truncated = true;
                    result.Error = AppendError(
                        result.Error,
                        inspectedCount >= normalizedMaxNodes
                            ? $"breakpoint scan reached maxNodes={normalizedMaxNodes}"
                            : $"breakpoint scan budget exceeded {normalizedBudgetMs}ms");
                    break;
                }

                try
                {
                    int capturedIndex = index;
                    BreakpointInfo breakpoint = ExecuteOnMainThread(
                        () => CaptureBreakpointAtIndex(capturedIndex),
                        DefaultMainThreadTimeoutMs);
                    if (breakpoint == null)
                    {
                        result.Partial = true;
                        result.Error = AppendError(result.Error, $"breakpoint item {index} became unavailable");
                    }
                    else
                    {
                        if (!captureBudget.TryConsume(breakpoint))
                        {
                            result.Partial = result.Truncated = true;
                            result.Error = AppendError(
                                result.Error,
                                $"breakpoint result reached {BreakpointCaptureBudget.MaxResultBytes} bytes");
                            break;
                        }
                        result.Breakpoints.Add(breakpoint);
                        if (breakpoint.Truncated) result.Partial = result.Truncated = true;
                    }
                }
                catch (Exception ex)
                {
                    RethrowRequestControlFlow(ex);
                    result.Partial = true;
                    result.Error = AppendError(result.Error, $"breakpoint item {index}: {ex.Message}");
                }
                inspectedCount++;
            }

            BreakpointSliceHeader finalHeader;
            try
            {
                finalHeader = ExecuteOnMainThread(CaptureBreakpointSliceHeader);
            }
            catch (Exception ex)
            {
                RethrowRequestControlFlow(ex);
                finalHeader = new BreakpointSliceHeader();
                result.Partial = true;
                result.Error = AppendError(result.Error, $"breakpoint final snapshot unavailable: {ex.Message}");
            }

            result.TotalCount = result.Breakpoints.Count;
            result.ResultBytes = captureBudget.ResultBytes;
            result.ScannedCount = inspectedCount;
            result.NextIndex = Math.Min(header.Count, startIndex + inspectedCount);
            result.HasMore = result.NextIndex < header.Count;
            if (result.HasMore)
            {
                result.Partial = result.Truncated = true;
            }
            bool countAndContextStable = finalHeader.Available &&
                finalHeader.Count == header.Count &&
                string.Equals(finalHeader.SnapshotToken, header.SnapshotToken, StringComparison.Ordinal);
            result.SnapshotStable = false;
            if (!countAndContextStable)
            {
                result.Partial = true;
                result.Error = AppendError(result.Error, "debugger or breakpoint collection changed while breakpoints were captured");
            }
            result.SnapshotStatus = countAndContextStable ? "best_effort" : "unstable";
            return result;
        }

        private BreakpointSliceHeader CaptureBreakpointSliceHeader()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var debugger = Debugger;
            if (debugger == null)
            {
                return new BreakpointSliceHeader { Error = "Debugger unavailable" };
            }

            return new BreakpointSliceHeader
            {
                Available = true,
                Count = debugger.Breakpoints?.Count ?? 0,
                SnapshotToken = CreateDebuggerSnapshotToken(debugger),
            };
        }

        private BreakpointInfo CaptureBreakpointAtIndex(int index)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var debugger = Debugger;
            var breakpoint = debugger?.Breakpoints?.Item(index);
            if (breakpoint == null) return null;
            BreakpointInfo info = BuildBreakpointInfo(breakpoint, debugger);
            BreakpointCaptureBudget.BoundFields(info);
            return info;
        }

        private sealed class BreakpointSliceHeader
        {
            internal bool Available { get; set; }
            internal int Count { get; set; }
            internal string SnapshotToken { get; set; }
            internal string Error { get; set; }
        }

        /// <summary>
        /// Set a breakpoint at a file location.
        /// </summary>
        public BreakpointOperationResult SetBreakpoint(
            string filePath,
            int lineNumber,
            string condition = null,
            int hitCount = 0,
            string hitCountType = null,
            string conditionType = null)
        {
            var locationPreflight = PrepareBreakpointLocation(filePath, lineNumber);
            if (!locationPreflight.IsValid)
            {
                return new BreakpointOperationResult
                {
                    Operation = "Set",
                    Success = false,
                    Message = locationPreflight.ErrorMessage
                };
            }

            filePath = locationPreflight.FilePath;
            bool shouldProbeNearby = false;
            NearbyProbeIdentity probeIdentity = null;
            string originalTag = "AIDB_FILE=" + Guid.NewGuid().ToString("N");
            BreakpointOperationResult operationResult = ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new BreakpointOperationResult { Operation = "Set" };
                string normalizedCondition = NormalizeBreakpointCondition(condition);
                string breakpointLanguage = GetBreakpointLanguage(filePath);
                var breakpointConditionType = ParseBreakpointConditionType(conditionType);
                var breakpointHitCountType = ParseHitCountType(hitCountType, hitCount);
                int normalizedHitCount = Math.Max(0, hitCount);

                try
                {
                    result.CommandIssued = true;
                    var breakpoint = AddFileBreakpoint(
                        debugger,
                        filePath,
                        lineNumber,
                        normalizedCondition,
                        breakpointLanguage,
                        breakpointConditionType,
                        normalizedHitCount,
                        breakpointHitCountType);
                    if (breakpoint == null)
                    {
                        result.Success = false;
                        result.ErrorCode = "OUTCOME_UNKNOWN";
                        result.Message = "Failed to set breakpoint";
                        return result;
                    }
                    result.AffectedCount = 1;

                    try
                    {
                        CommandEffectExecutor.Issue(
                            "breakpoint.assign_cleanup_tag",
                            () =>
                            {
                                ThreadHelper.ThrowIfNotOnUIThread();
                                breakpoint.Tag = originalTag;
                            });
                    }
                    catch { originalTag = null; }

                    IsBreakpointBound(debugger, breakpoint);
                    var breakpointInfo = BuildBreakpointInfo(breakpoint, debugger, filePath, lineNumber, normalizedCondition);

                    if (!breakpointInfo.ConditionApplied)
                    {
                        SafeDeleteBreakpoint(breakpoint);
                        result.Success = false;
                        result.ErrorCode = "OUTCOME_UNKNOWN";
                        result.Outcome = "created_then_condition_cleanup_attempted";
                        result.Message = $"Breakpoint condition was not applied at {filePath}:{lineNumber}";
                        return result;
                    }

                    shouldProbeNearby = !string.IsNullOrEmpty(originalTag) &&
                        ShouldSearchNearbyBindableLine(debugger, breakpointInfo);
                    if (shouldProbeNearby)
                    {
                        probeIdentity = new NearbyProbeIdentity
                        {
                            SnapshotToken = CreateDebuggerSnapshotToken(debugger),
                            DebugSessionGeneration = DebugSessionGeneration,
                            BreakSequence = BreakSequence,
                            ContextEpoch = ContextEpoch,
                        };
                    }

                    result.Success = true;
                    result.Outcome = shouldProbeNearby ? "created_unbound_probe_pending" : "created";
                    result.BreakpointInfo = breakpointInfo;
                    result.Message = BuildSetBreakpointMessage(breakpointInfo, filePath, lineNumber);
                    ApplyBreakpointOperationVerification(result, breakpointInfo, debugger, requireBoundInActiveSession: true);
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.ErrorCode = "OUTCOME_UNKNOWN";
                    result.Outcome = result.CommandIssued ? "set_outcome_unknown" : "set_not_started";
                    result.Message = ex.Message;
                }

                return result;
            });

            if (!operationResult.Success || !shouldProbeNearby || operationResult.BreakpointInfo == null)
            {
                return operationResult;
            }

            return ProbeNearbyBreakpointsSliced(
                operationResult,
                originalTag,
                probeIdentity,
                filePath,
                lineNumber,
                NormalizeBreakpointCondition(condition),
                GetBreakpointLanguage(filePath),
                ParseBreakpointConditionType(conditionType),
                Math.Max(0, hitCount),
                ParseHitCountType(hitCountType, hitCount),
                locationPreflight.LineCount);
        }

        /// <summary>
        /// Set a breakpoint at a function.
        /// </summary>
        public BreakpointOperationResult SetFunctionBreakpoint(
            string functionName,
            string condition = null,
            int hitCount = 0,
            string hitCountType = null,
            string conditionType = null)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new BreakpointOperationResult { Operation = "SetFunction" };
                string normalizedCondition = NormalizeBreakpointCondition(condition);
                var breakpointConditionType = ParseBreakpointConditionType(conditionType);
                var breakpointHitCountType = ParseHitCountType(hitCountType, hitCount);
                int normalizedHitCount = Math.Max(0, hitCount);

                try
                {
                    result.CommandIssued = true;
                    EnvDTE.Breakpoints bps = CommandEffectExecutor.Issue(
                        "breakpoint.add_function",
                        () =>
                        {
                            ThreadHelper.ThrowIfNotOnUIThread();
                            return debugger.Breakpoints.Add(
                                functionName,
                                "",
                                0,
                                0,
                                normalizedCondition ?? string.Empty,
                                breakpointConditionType,
                                string.Empty,
                                string.Empty,
                                1,
                                string.Empty,
                                normalizedHitCount,
                                breakpointHitCountType);
                        });

                    if (bps != null && bps.Count > 0)
                    {
                        var bp = bps.Item(1);
                        result.AffectedCount = 1;
                        IsBreakpointBound(debugger, bp);
                        var breakpointInfo = BuildBreakpointInfo(bp, debugger, null, 0, normalizedCondition);
                        if (!breakpointInfo.ConditionApplied)
                        {
                            SafeDeleteBreakpoint(bp);
                            result.Success = false;
                            result.ErrorCode = "OUTCOME_UNKNOWN";
                            result.Outcome = "created_then_condition_cleanup_attempted";
                            result.Message = $"Function breakpoint condition was not applied on {functionName}";
                            return result;
                        }

                        result.Success = true;
                        result.Outcome = "created";
                        result.Message = $"Function breakpoint set on {functionName}";
                        result.BreakpointInfo = breakpointInfo;
                        ApplyBreakpointOperationVerification(result, breakpointInfo, debugger, requireBoundInActiveSession: true);
                    }
                    else
                    {
                        result.Success = false;
                        result.ErrorCode = "OUTCOME_UNKNOWN";
                        result.Outcome = "set_outcome_unknown";
                        result.Message = "Failed to set function breakpoint";
                    }
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.ErrorCode = "OUTCOME_UNKNOWN";
                    result.Outcome = result.CommandIssued ? "set_outcome_unknown" : "set_not_started";
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Set a native data breakpoint/watchpoint for an expression or address.
        /// </summary>
        public BreakpointOperationResult SetDataBreakpoint(
            string dataExpression,
            int byteCount = 1,
            string condition = null,
            int hitCount = 0,
            string hitCountType = null,
            string conditionType = null)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new BreakpointOperationResult { Operation = "SetData" };
                string normalizedExpression = string.IsNullOrWhiteSpace(dataExpression) ? null : dataExpression.Trim();
                string normalizedCondition = NormalizeBreakpointCondition(condition);
                int normalizedByteCount = Math.Max(1, Math.Min(byteCount, 1024));
                int normalizedHitCount = Math.Max(0, hitCount);
                var breakpointConditionType = ParseBreakpointConditionType(conditionType);
                var breakpointHitCountType = ParseHitCountType(hitCountType, hitCount);

                if (string.IsNullOrEmpty(normalizedExpression))
                {
                    result.Success = false;
                    result.Message = "data_expression is required";
                    return result;
                }

                try
                {
                    result.CommandIssued = true;
                    EnvDTE.Breakpoints bps = CommandEffectExecutor.Issue(
                        "breakpoint.add_data",
                        () =>
                        {
                            ThreadHelper.ThrowIfNotOnUIThread();
                            return debugger.Breakpoints.Add(
                                string.Empty,
                                string.Empty,
                                0,
                                0,
                                normalizedCondition ?? string.Empty,
                                breakpointConditionType,
                                string.Empty,
                                normalizedExpression,
                                normalizedByteCount,
                                string.Empty,
                                normalizedHitCount,
                                breakpointHitCountType);
                        });

                    if (bps == null || bps.Count == 0)
                    {
                        result.Success = false;
                        result.ErrorCode = "OUTCOME_UNKNOWN";
                        result.Message = "Failed to set data breakpoint";
                        return result;
                    }

                    var breakpoint = bps.Item(1);
                    result.AffectedCount = 1;
                    IsBreakpointBound(debugger, breakpoint);
                    var breakpointInfo = BuildBreakpointInfo(breakpoint, debugger, null, 0, normalizedCondition);
                    if (!breakpointInfo.ConditionApplied)
                    {
                        SafeDeleteBreakpoint(breakpoint);
                        result.Success = false;
                        result.ErrorCode = "OUTCOME_UNKNOWN";
                        result.Outcome = "created_then_condition_cleanup_attempted";
                        result.Message = $"Data breakpoint condition was not applied for {normalizedExpression}";
                        return result;
                    }

                    result.Success = true;
                    result.Outcome = "created";
                    result.Message = $"Data breakpoint set for {normalizedExpression} ({normalizedByteCount} byte(s))";
                    result.BreakpointInfo = breakpointInfo;
                    ApplyBreakpointOperationVerification(result, breakpointInfo, debugger, requireBoundInActiveSession: true);
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.ErrorCode = "OUTCOME_UNKNOWN";
                    result.Outcome = result.CommandIssued ? "set_outcome_unknown" : "set_not_started";
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Delete a breakpoint.
        /// </summary>
        public BreakpointOperationResult DeleteBreakpoint(
            string filePath,
            int lineNumber,
            int breakpointLookupBudgetMs = -1)
        {
            int breakpointIndex = FindBreakpointIndexSliced(filePath, lineNumber, breakpointLookupBudgetMs);
            if (breakpointIndex <= 0)
            {
                return new BreakpointOperationResult
                {
                    Operation = "Delete",
                    Success = false,
                    ErrorCode = "TARGET_NOT_FOUND",
                    Message = "Breakpoint not found",
                    Outcome = "not_found",
                };
            }

            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new BreakpointOperationResult { Operation = "Delete" };

                try
                {
                    EnvDTE.Breakpoint bp = debugger.Breakpoints?.Item(breakpointIndex);
                    if (!BreakpointLocationMatches(bp, filePath, lineNumber))
                    {
                        throw new StaleDebugSessionException(
                            "Breakpoint collection changed before the breakpoint could be deleted.");
                    }
                    result.CommandIssued = true;
                    CommandEffectExecutor.Issue("breakpoint.delete", bp.Delete);
                    result.AffectedCount = 1;
                    result.Success = true;
                    result.Outcome = "deleted";
                    result.Message = $"Breakpoint deleted at {filePath}:{lineNumber}";
                }
                catch (Exception ex)
                {
                    RethrowRequestControlFlow(ex);
                    result.Success = false;
                    result.ErrorCode = "OUTCOME_UNKNOWN";
                    result.Outcome = result.CommandIssued ? "delete_outcome_unknown" : "delete_not_started";
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Delete all breakpoints.
        /// </summary>
        public BreakpointOperationResult DeleteAllBreakpoints()
        {
            var result = new BreakpointOperationResult { Operation = "DeleteAll" };
            try
            {
                int remaining = ExecuteOnMainThread(() =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    return Debugger?.Breakpoints?.Count ?? 0;
                });
                while (remaining > 0)
                {
                    DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                    remaining = ExecuteOnMainThread(() => DeleteFirstBreakpoint(result));
                }
                result.RemainingCount = 0;
                result.Success = true;
                result.Outcome = "deleted_all";
                result.Message = $"Deleted {result.AffectedCount} breakpoints";
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Partial = result.AffectedCount > 0;
                result.ErrorCode = result.CommandIssued ? "OUTCOME_UNKNOWN" : "DEBUG_OPERATION_FAILED";
                result.Outcome = result.CommandIssued ? "delete_all_outcome_unknown" : "delete_all_not_started";
                result.Message = $"Deleted {result.AffectedCount} breakpoint(s) before the operation stopped: {ex.Message}";
                try
                {
                    result.RemainingCount = ExecuteOnMainThread(() =>
                    {
                        ThreadHelper.ThrowIfNotOnUIThread();
                        return Debugger?.Breakpoints?.Count ?? 0;
                    });
                }
                catch { result.RemainingCount = -1; }
                return result;
            }
        }

        /// <summary>
        /// Enable or disable a breakpoint.
        /// </summary>
        public BreakpointOperationResult EnableBreakpoint(
            string filePath,
            int lineNumber,
            bool enabled,
            int breakpointLookupBudgetMs = -1)
        {
            int breakpointIndex = FindBreakpointIndexSliced(filePath, lineNumber, breakpointLookupBudgetMs);
            if (breakpointIndex <= 0)
            {
                return new BreakpointOperationResult
                {
                    Operation = enabled ? "Enable" : "Disable",
                    Success = false,
                    ErrorCode = "TARGET_NOT_FOUND",
                    Message = "Breakpoint not found",
                    Outcome = "not_found",
                };
            }

            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new BreakpointOperationResult { Operation = enabled ? "Enable" : "Disable" };

                try
                {
                    EnvDTE.Breakpoint bp = debugger.Breakpoints?.Item(breakpointIndex);
                    if (!BreakpointLocationMatches(bp, filePath, lineNumber))
                    {
                        throw new StaleDebugSessionException(
                            "Breakpoint collection changed before the breakpoint could be updated.");
                    }
                    result.CommandIssued = true;
                    CommandEffectExecutor.Issue(
                        enabled ? "breakpoint.enable" : "breakpoint.disable",
                        () =>
                        {
                            ThreadHelper.ThrowIfNotOnUIThread();
                            bp.Enabled = enabled;
                        });
                    result.AffectedCount = 1;
                    result.Success = true;
                    result.Outcome = enabled ? "enabled" : "disabled";
                    result.Message = $"Breakpoint {(enabled ? "enabled" : "disabled")} at {filePath}:{lineNumber}";
                }
                catch (Exception ex)
                {
                    RethrowRequestControlFlow(ex);
                    result.Success = false;
                    result.ErrorCode = "OUTCOME_UNKNOWN";
                    result.Outcome = result.CommandIssued ? "enable_outcome_unknown" : "enable_not_started";
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        private int DeleteFirstBreakpoint(BreakpointOperationResult result)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var breakpoints = Debugger?.Breakpoints;
            int count = breakpoints?.Count ?? 0;
            if (count <= 0) return 0;
            EnvDTE.Breakpoint breakpoint = breakpoints.Item(1);
            if (breakpoint == null) throw new InvalidOperationException("Breakpoint item 1 is unavailable.");
            result.CommandIssued = true;
            CommandEffectExecutor.Issue("breakpoint.delete_all_item", breakpoint.Delete);
            result.AffectedCount++;
            return Debugger?.Breakpoints?.Count ?? Math.Max(0, count - 1);
        }

        [SuppressMessage("Usage", "VSTHRD010", Justification = "Reads one breakpoint location per bounded UI slice.")]
        private int FindBreakpointIndexSliced(string filePath, int lineNumber, int budgetMs)
        {
            BreakpointLocationSnapshot first = CaptureBreakpointLocationSnapshot(budgetMs);
            BreakpointLocationIdentity match = first.Items.FirstOrDefault(
                item => BreakpointLocationMatches(item, filePath, lineNumber));
            if (match != null) return match.Index;

            BreakpointLocationSnapshot confirmation = CaptureBreakpointLocationSnapshot(budgetMs);
            if (!BreakpointLocationSnapshotsEqual(first, confirmation))
            {
                throw new StaleDebugSessionException(
                    "Breakpoint collection changed while locating the requested breakpoint.");
            }
            return 0;
        }

        [SuppressMessage("Usage", "VSTHRD010", Justification = "CaptureBreakpointLocation is invoked only through ExecuteOnMainThread, one item per slice.")]
        private BreakpointLocationSnapshot CaptureBreakpointLocationSnapshot(int budgetMs)
        {
            int normalizedBudgetMs = NormalizeCollectionBudgetMs(budgetMs);
            BreakpointSliceHeader header = ExecuteOnMainThread(CaptureBreakpointSliceHeader);
            var snapshot = new BreakpointLocationSnapshot();
            var stopwatch = Stopwatch.StartNew();
            for (int index = 1; index <= header.Count; index++)
            {
                DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                if (stopwatch.ElapsedMilliseconds >= normalizedBudgetMs)
                {
                    throw new ResourceLimitExceededException(
                        $"Breakpoint lookup exceeded its {normalizedBudgetMs}ms budget; retry with a larger breakpoint_lookup_budget_ms.");
                }
                int capturedIndex = index;
                BreakpointLocationIdentity item = ExecuteOnMainThread(
                    () => CaptureBreakpointLocation(capturedIndex),
                    DefaultMainThreadTimeoutMs);
                if (item == null)
                {
                    throw new StaleDebugSessionException("Breakpoint collection changed while being enumerated.");
                }
                snapshot.Items.Add(item);
            }
            return snapshot;
        }

        private BreakpointLocationIdentity CaptureBreakpointLocation(int index)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                EnvDTE.Breakpoint breakpoint = Debugger?.Breakpoints?.Item(index);
                return breakpoint == null ? null : new BreakpointLocationIdentity
                {
                    Index = index,
                    FilePath = breakpoint.File,
                    LineNumber = breakpoint.FileLine,
                };
            }
            catch { return null; }
        }

        private static bool BreakpointLocationMatches(EnvDTE.Breakpoint breakpoint, string filePath, int lineNumber)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return breakpoint != null && breakpoint.FileLine == lineNumber &&
                string.Equals(breakpoint.File, filePath, StringComparison.OrdinalIgnoreCase);
        }

        private static bool BreakpointLocationMatches(BreakpointLocationIdentity breakpoint, string filePath, int lineNumber)
        {
            return breakpoint != null && breakpoint.LineNumber == lineNumber &&
                string.Equals(breakpoint.FilePath, filePath, StringComparison.OrdinalIgnoreCase);
        }

        private static bool BreakpointLocationSnapshotsEqual(
            BreakpointLocationSnapshot left,
            BreakpointLocationSnapshot right)
        {
            if (left.Items.Count != right.Items.Count) return false;
            for (int index = 0; index < left.Items.Count; index++)
            {
                if (!BreakpointLocationMatches(
                    right.Items[index], left.Items[index].FilePath, left.Items[index].LineNumber)) return false;
            }
            return true;
        }

        private sealed class BreakpointLocationSnapshot
        {
            internal List<BreakpointLocationIdentity> Items { get; } = new List<BreakpointLocationIdentity>();
        }

        private sealed class BreakpointLocationIdentity
        {
            internal int Index { get; set; }
            internal string FilePath { get; set; }
            internal int LineNumber { get; set; }
        }

        private BreakpointInfo BuildBreakpointInfo(EnvDTE.Breakpoint breakpoint, Debugger5 debugger, string requestedFilePath = null, int requestedLineNumber = 0, string requestedCondition = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var boundChildren = GetBreakpointChildrenSnapshot(breakpoint);
            var resolvedBreakpoint = boundChildren.First ?? breakpoint;
            bool isFileBreakpoint = breakpoint.LocationType == dbgBreakpointLocationType.dbgBreakpointLocationTypeFile;
            string effectiveRequestedFilePath = !string.IsNullOrEmpty(requestedFilePath)
                ? requestedFilePath
                : (isFileBreakpoint && !string.IsNullOrEmpty(breakpoint.File) ? breakpoint.File : null);
            int effectiveRequestedLineNumber = requestedLineNumber > 0
                ? requestedLineNumber
                : (isFileBreakpoint ? breakpoint.FileLine : 0);
            var breakpointInfo = new BreakpointInfo
            {
                Name = breakpoint.Name,
                FilePath = breakpoint.File,
                LineNumber = breakpoint.FileLine,
                Column = breakpoint.FileColumn,
                FunctionName = breakpoint.FunctionName,
                Enabled = breakpoint.Enabled,
                Condition = breakpoint.Condition,
                ConditionType = GetConditionTypeString(breakpoint),
                HitCount = breakpoint.CurrentHits,
                HitCountTarget = breakpoint.HitCountTarget,
                HitCountType = GetHitCountTypeString(breakpoint),
                BreakpointType = GetBreakpointTypeString(breakpoint.LocationType),
                Tag = breakpoint.Tag,
                RequestedFilePath = effectiveRequestedFilePath,
                RequestedLineNumber = effectiveRequestedLineNumber,
                RequestedCondition = requestedCondition,
                ResolvedFilePath = resolvedBreakpoint?.File,
                ResolvedLineNumber = resolvedBreakpoint?.FileLine ?? 0,
                ResolvedColumn = resolvedBreakpoint?.FileColumn ?? 0,
                ResolvedFunctionName = resolvedBreakpoint?.FunctionName,
                BindingStatus = GetBreakpointBindingStatus(debugger, boundChildren.Count),
                BoundChildrenCount = boundChildren.Count,
                IsBound = boundChildren.Count > 0,
                ConditionApplied = IsConditionApplied(requestedCondition, breakpoint.Condition)
            };

            string taggedAddress = TryGetAddressBreakpointTag(breakpointInfo.Tag);
            if (!string.IsNullOrEmpty(taggedAddress) && debugger != null && debugger.CurrentMode != dbgDebugMode.dbgDesignMode)
            {
                try
                {
                    bool isLiteral = TryParseAddressLiteral(taggedAddress, out _);
                    bool isModuleRva = TryParseModuleRvaExpression(taggedAddress, out _, out _);
                    if (!isLiteral && !isModuleRva)
                    {
                        breakpointInfo.TrackedAddress = new NativeAddressInfo
                        {
                            Expression = taggedAddress,
                            Status = "unresolved",
                            Error = "Legacy address tag is not a literal or module+RVA and was not evaluated"
                        };
                    }
                    else
                    {
                        breakpointInfo.TrackedAddress = ResolveAddressInternal(
                            debugger,
                            taggedAddress,
                            modules: null);
                    }
                    if (breakpointInfo.TrackedAddress != null && string.IsNullOrEmpty(breakpointInfo.TrackedAddress.Error))
                    {
                        breakpointInfo.TrackedAddresses.Add(breakpointInfo.TrackedAddress);
                    }
                }
                catch (Exception ex)
                {
                    _log($"BuildBreakpointInfo address tag resolution failed: {ex.GetType().Name}");
                }
            }

            PopulateBreakpointBindingAwareness(breakpointInfo, debugger);
            return breakpointInfo;
        }

        private void PopulateBreakpointBindingAwareness(BreakpointInfo breakpointInfo, Debugger5 debugger)
        {
            if (breakpointInfo == null)
            {
                return;
            }

            bool hasRequestedSourceLocation =
                !string.IsNullOrEmpty(breakpointInfo.RequestedFilePath) &&
                breakpointInfo.RequestedLineNumber > 0;
            bool resolvedLocationDiffers =
                hasRequestedSourceLocation &&
                breakpointInfo.IsBound &&
                (
                    (breakpointInfo.ResolvedLineNumber > 0 && breakpointInfo.ResolvedLineNumber != breakpointInfo.RequestedLineNumber) ||
                    (!string.IsNullOrEmpty(breakpointInfo.ResolvedFilePath) &&
                     !string.Equals(breakpointInfo.ResolvedFilePath, breakpointInfo.RequestedFilePath, StringComparison.OrdinalIgnoreCase)) ||
                    (breakpointInfo.LineNumber > 0 && breakpointInfo.LineNumber != breakpointInfo.RequestedLineNumber)
                );

            breakpointInfo.IsRelocated = resolvedLocationDiffers;
            if (breakpointInfo.IsBound)
            {
                breakpointInfo.BindingVerificationStatus = breakpointInfo.IsRelocated
                    ? "bound_relocated"
                    : (hasRequestedSourceLocation ? "bound_exact" : "bound");
                if (breakpointInfo.IsRelocated)
                {
                    breakpointInfo.OptimizedCodeSuspected = true;
                    breakpointInfo.BindingWarning =
                        "Requested breakpoint location did not bind exactly; the breakpoint was bound at a different executable location. Optimized code, statement folding, or source/symbol mismatch may be involved.";
                }
                return;
            }

            if (string.Equals(breakpointInfo.BindingStatus, "pending", StringComparison.OrdinalIgnoreCase))
            {
                breakpointInfo.BindingVerificationStatus = "pending";
                breakpointInfo.BindingWarning = "Breakpoint is pending and has not been bound to executable code yet.";
                return;
            }

            if (string.Equals(breakpointInfo.BindingStatus, "unbound", StringComparison.OrdinalIgnoreCase))
            {
                breakpointInfo.BindingVerificationStatus = "unbound";
                // Source-file guesses are computed before UI dispatch. For breakpoints
                // discovered from VS itself, do not perform disk I/O while holding the UI
                // thread; the warning below truthfully leaves the cause unresolved.
                breakpointInfo.OptimizedCodeSuspected = false;
                breakpointInfo.BindingWarning =
                    "Breakpoint did not bind in the current debug session. The source location may be optimized away, symbols may be missing, the module may not be loaded, or the source may not match the debuggee.";
                return;
            }

            breakpointInfo.BindingVerificationStatus = string.IsNullOrEmpty(breakpointInfo.BindingStatus)
                ? "unknown"
                : breakpointInfo.BindingStatus;
        }

        private void MarkNearbyProbeResult(BreakpointInfo breakpointInfo, bool attempted, bool succeeded)
        {
            if (breakpointInfo == null)
            {
                return;
            }

            breakpointInfo.NearbyProbeAttempted = attempted;
            breakpointInfo.NearbyProbeSucceeded = attempted && succeeded;
            if (attempted && succeeded)
            {
                breakpointInfo.IsRelocated = true;
                breakpointInfo.OptimizedCodeSuspected = true;
                breakpointInfo.BindingVerificationStatus = "bound_relocated";
                breakpointInfo.BindingWarning =
                    "Requested source line did not bind; a nearby executable line was selected and bound instead. This commonly happens with optimized or statement-folded code.";
            }
            else if (attempted)
            {
                breakpointInfo.BindingWarning =
                    "Requested source line did not bind, and no nearby executable line could be found in the bounded probe range.";
            }
        }

        private void ApplyBreakpointOperationVerification(
            BreakpointOperationResult result,
            BreakpointInfo breakpointInfo,
            Debugger5 debugger,
            bool requireBoundInActiveSession)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (result == null || breakpointInfo == null)
            {
                return;
            }

            result.VerificationStatus = breakpointInfo.BindingVerificationStatus;
            result.VerificationMessage = breakpointInfo.BindingWarning;
            if (!requireBoundInActiveSession || !IsActiveDebugSession(debugger))
            {
                return;
            }

            if (!breakpointInfo.IsBound &&
                string.Equals(breakpointInfo.BindingStatus, "unbound", StringComparison.OrdinalIgnoreCase))
            {
                result.Success = true;
                string verificationMessage = string.IsNullOrEmpty(breakpointInfo.BindingWarning)
                    ? "Breakpoint did not bind in the current debug session."
                    : breakpointInfo.BindingWarning;
                result.Message = string.IsNullOrEmpty(result.Message)
                    ? verificationMessage
                    : $"{result.Message}; {verificationMessage}";
            }
        }

        private void TrySetAddressBreakpointTag(EnvDTE.Breakpoint breakpoint, string addressText)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (breakpoint == null || string.IsNullOrWhiteSpace(addressText))
            {
                return;
            }

            try
            {
                CommandEffectExecutor.Issue(
                    "breakpoint.assign_address_tag",
                    () =>
                    {
                        ThreadHelper.ThrowIfNotOnUIThread();
                        breakpoint.Tag = AddressBreakpointTagPrefix + addressText.Trim();
                    });
            }
            catch (Exception ex)
            {
                _log($"TrySetAddressBreakpointTag failed: {ex.GetType().Name}");
            }
        }

        private string TryGetAddressBreakpointTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag) ||
                !tag.StartsWith(AddressBreakpointTagPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string value = tag.Substring(AddressBreakpointTagPrefix.Length).Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }

        private BreakpointChildrenSnapshot GetBreakpointChildrenSnapshot(EnvDTE.Breakpoint breakpoint)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var snapshot = new BreakpointChildrenSnapshot();
            if (breakpoint == null)
            {
                return snapshot;
            }

            EnvDTE.Breakpoints breakpointChildren = null;
            try
            {
                breakpointChildren = breakpoint.Children;
            }
            catch
            {
                return snapshot;
            }

            if (breakpointChildren == null)
            {
                return snapshot;
            }

            try
            {
                snapshot.Count = breakpointChildren.Count;
                if (snapshot.Count > 0)
                {
                    snapshot.First = breakpointChildren.Item(1);
                }
            }
            catch (Exception ex)
            {
                _log($"[GetBreakpointChildrenSnapshot] Error reading children: {ex.GetType().Name}");
            }

            return snapshot;
        }

        private int GetBreakpointChildCount(EnvDTE.Breakpoint breakpoint)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return GetBreakpointChildrenSnapshot(breakpoint).Count;
        }

        private sealed class BreakpointChildrenSnapshot
        {
            internal int Count { get; set; }
            internal EnvDTE.Breakpoint First { get; set; }
        }

        private string GetBreakpointBindingStatus(Debugger5 debugger, int boundChildrenCount)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (boundChildrenCount > 0)
            {
                return boundChildrenCount > 1 ? "bound_multiple" : "bound";
            }

            if (debugger == null)
            {
                return "unknown";
            }

            try
            {
                var mode = debugger.CurrentMode;
                if (mode == dbgDebugMode.dbgDesignMode || mode == dbgDebugMode.dbgRunMode)
                {
                    return "pending";
                }

                return mode == dbgDebugMode.dbgBreakMode ? "unbound" : "unknown";
            }
            catch
            {
                return "unknown";
            }
        }

        private bool IsConditionApplied(string requestedCondition, string actualCondition)
        {
            string normalizedRequested = NormalizeBreakpointCondition(requestedCondition);
            if (string.IsNullOrEmpty(normalizedRequested))
            {
                return true;
            }

            string normalizedActual = NormalizeBreakpointCondition(actualCondition);
            return string.Equals(normalizedRequested, normalizedActual, StringComparison.Ordinal);
        }

        private string NormalizeBreakpointCondition(string condition)
        {
            return string.IsNullOrWhiteSpace(condition) ? null : condition.Trim();
        }

        private dbgBreakpointConditionType ParseBreakpointConditionType(string conditionType)
        {
            if (string.IsNullOrWhiteSpace(conditionType))
            {
                return dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue;
            }

            string normalized = conditionType.Trim().Replace("-", "_").ToLowerInvariant();
            switch (normalized)
            {
                case "changed":
                case "when_changed":
                case "whenchanged":
                    return dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenChanged;
                case "true":
                case "when_true":
                case "whentrue":
                    return dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue;
                default:
                    throw new DebuggerParameterValidationException("Unsupported breakpoint condition type.");
            }
        }

        private dbgHitCountType ParseHitCountType(string hitCountType, int hitCount)
        {
            if (hitCount <= 0)
            {
                return dbgHitCountType.dbgHitCountTypeNone;
            }

            if (string.IsNullOrWhiteSpace(hitCountType))
            {
                return dbgHitCountType.dbgHitCountTypeEqual;
            }

            string normalized = hitCountType.Trim().Replace("-", "_").ToLowerInvariant();
            switch (normalized)
            {
                case "greater_or_equal":
                case "greater_than_or_equal":
                case "greater_equal":
                case ">=":
                    return dbgHitCountType.dbgHitCountTypeGreaterOrEqual;
                case "multiple":
                case "multiple_of":
                    return dbgHitCountType.dbgHitCountTypeMultiple;
                case "equal":
                case "equals":
                case "==":
                    return dbgHitCountType.dbgHitCountTypeEqual;
                default:
                    throw new DebuggerParameterValidationException("Unsupported breakpoint hit-count type.");
            }
        }

        private string GetConditionTypeString(EnvDTE.Breakpoint breakpoint)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                return breakpoint.ConditionType.ToString();
            }
            catch
            {
                return null;
            }
        }

        private string GetHitCountTypeString(EnvDTE.Breakpoint breakpoint)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                return breakpoint.HitCountType.ToString();
            }
            catch
            {
                return null;
            }
        }

        private EnvDTE.Breakpoint AddFileBreakpoint(
            Debugger5 debugger,
            string filePath,
            int lineNumber,
            string condition,
            string language,
            dbgBreakpointConditionType conditionType,
            int hitCount,
            dbgHitCountType hitCountType)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            EnvDTE.Breakpoints breakpoints = CommandEffectExecutor.Issue(
                "breakpoint.add_file",
                () =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    return debugger.Breakpoints.Add(
                        string.Empty,
                        filePath,
                        lineNumber,
                        1,
                        condition ?? string.Empty,
                        conditionType,
                        language ?? string.Empty,
                        string.Empty,
                        1,
                        string.Empty,
                        Math.Max(0, hitCount),
                        hitCountType);
                });

            return breakpoints != null && breakpoints.Count > 0 ? breakpoints.Item(1) : null;
        }

        private string GetBreakpointLanguage(string filePath)
        {
            string extension = System.IO.Path.GetExtension(filePath)?.ToLowerInvariant();
            switch (extension)
            {
                case ".cs":
                case ".csx":
                    return "C#";
                case ".vb":
                    return "Basic";
                case ".fs":
                    return "F#";
                case ".c":
                case ".cc":
                case ".cpp":
                case ".cxx":
                case ".h":
                case ".hpp":
                case ".hxx":
                case ".inl":
                    return "C++";
                default:
                    return null;
            }
        }

        private bool IsBreakpointBound(Debugger5 debugger, EnvDTE.Breakpoint breakpoint)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (breakpoint == null || debugger == null)
            {
                return false;
            }

            try
            {
                if (debugger.CurrentMode == dbgDebugMode.dbgDesignMode)
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }

            return GetBreakpointChildCount(breakpoint) > 0;
        }

        private bool IsActiveDebugSession(Debugger5 debugger)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (debugger == null)
            {
                return false;
            }

            try
            {
                return debugger.CurrentMode != dbgDebugMode.dbgDesignMode;
            }
            catch
            {
                return false;
            }
        }

        private bool ShouldSearchNearbyBindableLine(Debugger5 debugger, BreakpointInfo breakpointInfo)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (breakpointInfo == null || breakpointInfo.IsBound || debugger == null)
            {
                return false;
            }

            if (!string.Equals(breakpointInfo.BindingStatus, "unbound", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                return debugger.CurrentMode == dbgDebugMode.dbgBreakMode &&
                       !string.IsNullOrEmpty(breakpointInfo.FilePath) &&
                       breakpointInfo.LineNumber > 0;
            }
            catch
            {
                return false;
            }
        }

        [SuppressMessage("Usage", "VSTHRD010", Justification = "Each candidate add/bind/delete probe and tag lookup is separately marshalled to a bounded UI slice.")]
        private BreakpointOperationResult ProbeNearbyBreakpointsSliced(
            BreakpointOperationResult result,
            string originalTag,
            NearbyProbeIdentity probeIdentity,
            string filePath,
            int requestedLineNumber,
            string condition,
            string language,
            dbgBreakpointConditionType conditionType,
            int hitCount,
            dbgHitCountType hitCountType,
            int lineCount)
        {
            if (lineCount <= 0)
            {
                MarkNearbyProbeResult(result.BreakpointInfo, attempted: true, succeeded: false);
                result.Outcome = "created_unbound_no_nearby_match";
                return result;
            }

            foreach (int candidateLine in EnumerateNearbyBreakpointLines(requestedLineNumber, lineCount))
            {
                try
                {
                    DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                }
                catch (Exception ex) when (ex is OperationCanceledException || ex is TimeoutException)
                {
                    MarkNearbyProbeResult(result.BreakpointInfo, attempted: true, succeeded: false);
                    result.Success = true;
                    result.Partial = true;
                    result.ErrorCode = "NEARBY_PROBE_ABORTED";
                    result.Outcome = "created_original_nearby_probe_aborted";
                    result.Message = $"Original breakpoint was created; nearby probing stopped before another candidate was attempted: {ex.Message}";
                    return result;
                }
                NearbyProbeCapture capture;
                try
                {
                    int capturedLine = candidateLine;
                    capture = ExecuteOnMainThread(
                        () => ProbeNearbyBreakpointCandidate(
                            filePath,
                            requestedLineNumber,
                            capturedLine,
                            probeIdentity,
                            condition,
                            language,
                            conditionType,
                            hitCount,
                            hitCountType),
                        DefaultMainThreadTimeoutMs);
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Partial = true;
                    result.AffectedCount = 2;
                    result.ErrorCode = "OUTCOME_UNKNOWN";
                    result.Outcome = "created_original_nearby_probe_outcome_unknown";
                    result.Message = $"Original breakpoint was created, but nearby candidate cleanup could not be confirmed; a candidate may also remain: {ex.Message}";
                    return result;
                }

                if (capture?.ConditionNotApplied == true)
                {
                    MarkNearbyProbeResult(result.BreakpointInfo, attempted: true, succeeded: false);
                    result.Success = true;
                    result.Partial = true;
                    result.ErrorCode = "BREAKPOINT_CONDITION_NOT_APPLIED";
                    result.Outcome = "created_original_nearby_condition_rejected";
                    result.Message = "Original conditioned breakpoint was retained; a bound nearby candidate was removed because Visual Studio did not apply its condition";
                    return result;
                }

                if (capture?.BreakpointInfo == null)
                {
                    if (capture?.ContextChanged == true)
                    {
                        MarkNearbyProbeResult(result.BreakpointInfo, attempted: true, succeeded: false);
                        result.Success = true;
                        result.Partial = true;
                        result.ErrorCode = "STALE_DEBUG_CONTEXT";
                        result.Outcome = "created_original_nearby_probe_stale_context";
                        result.Message = "Original breakpoint was created; nearby probing stopped because the debug context changed";
                        return result;
                    }
                    continue;
                }

                bool contextStillCurrent = ExecuteOnMainThread(
                    () => IsNearbyProbeContextCurrent(probeIdentity),
                    DefaultMainThreadTimeoutMs);
                if (!contextStillCurrent)
                {
                    result.Success = false;
                    result.Partial = true;
                    result.AffectedCount = 2;
                    result.ErrorCode = "STALE_DEBUG_CONTEXT";
                    result.Outcome = "created_original_and_relocated_context_changed";
                    result.BreakpointInfo = capture.BreakpointInfo;
                    result.Message = "A nearby breakpoint was created, but debug context changed before the original could be removed; both breakpoints were retained";
                    return result;
                }

                try
                {
                    DeleteBreakpointByTagSliced(originalTag, probeIdentity);
                }
                catch (StaleDebugContextException ex)
                {
                    result.Success = false;
                    result.Partial = true;
                    result.AffectedCount = 2;
                    result.ErrorCode = "STALE_DEBUG_CONTEXT";
                    result.Outcome = "created_original_and_relocated_context_changed";
                    result.BreakpointInfo = capture.BreakpointInfo;
                    result.Message = $"A nearby breakpoint was created, but the original was retained because debug context changed: {ex.Message}";
                    return result;
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Partial = true;
                    result.AffectedCount = 2;
                    result.ErrorCode = "OUTCOME_UNKNOWN";
                    result.Outcome = "created_original_and_relocated_cleanup_failed";
                    result.BreakpointInfo = capture.BreakpointInfo;
                    result.Message = $"A nearby bound breakpoint was created, but the original unbound breakpoint could not be removed: {ex.Message}";
                    return result;
                }

                MarkNearbyProbeResult(capture.BreakpointInfo, attempted: true, succeeded: true);
                result.Success = true;
                result.Partial = false;
                result.AffectedCount = 1;
                result.Outcome = "created_relocated";
                result.BreakpointInfo = capture.BreakpointInfo;
                result.Message = BuildSetBreakpointMessage(capture.BreakpointInfo, filePath, requestedLineNumber);
                return result;
            }

            MarkNearbyProbeResult(result.BreakpointInfo, attempted: true, succeeded: false);
            result.Success = true;
            result.Outcome = "created_unbound_no_nearby_match";
            result.Message = $"Breakpoint was created at {filePath}:{requestedLineNumber}, but did not bind in the current session; no nearby bound executable line was found";
            return result;
        }

        private NearbyProbeCapture ProbeNearbyBreakpointCandidate(
            string filePath,
            int requestedLineNumber,
            int candidateLine,
            NearbyProbeIdentity probeIdentity,
            string condition,
            string language,
            dbgBreakpointConditionType conditionType,
            int hitCount,
            dbgHitCountType hitCountType)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!IsNearbyProbeContextCurrent(probeIdentity))
            {
                return new NearbyProbeCapture { ContextChanged = true };
            }
            var debugger = Debugger;
            EnvDTE.Breakpoint candidate = AddFileBreakpoint(
                debugger,
                filePath,
                candidateLine,
                condition,
                language,
                conditionType,
                hitCount,
                hitCountType);
            if (candidate == null) return new NearbyProbeCapture();

            string candidateTag = "AIDB_NEARBY=" + Guid.NewGuid().ToString("N");
            try
            {
                CommandEffectExecutor.Issue(
                    "breakpoint.assign_probe_tag",
                    () =>
                    {
                        ThreadHelper.ThrowIfNotOnUIThread();
                        candidate.Tag = candidateTag;
                    });
            }
            catch (Exception ex)
            {
                try
                {
                    CommandEffectExecutor.Issue(
                        "breakpoint.delete_untagged_probe",
                        candidate.Delete);
                }
                catch { }
                throw new InvalidOperationException(
                    "Nearby breakpoint candidate could not be assigned a cleanup identity.",
                    ex);
            }

            IsBreakpointBound(debugger, candidate);
            if (GetBreakpointChildCount(candidate) <= 0)
            {
                DeleteBreakpointCandidateStrict(debugger, candidate, candidateTag);
                return new NearbyProbeCapture();
            }

            BreakpointInfo info = BuildBreakpointInfo(
                candidate,
                debugger,
                filePath,
                requestedLineNumber,
                condition);
            if (!info.ConditionApplied)
            {
                DeleteBreakpointCandidateStrict(debugger, candidate, candidateTag);
                return new NearbyProbeCapture { ConditionNotApplied = true };
            }
            return new NearbyProbeCapture { BreakpointInfo = info };
        }

        private static void DeleteBreakpointCandidateStrict(
            Debugger5 debugger,
            EnvDTE.Breakpoint candidate,
            string expectedTag)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            CommandEffectExecutor.Issue("breakpoint.delete_probe", candidate.Delete);
            EnvDTE.Breakpoints breakpoints = debugger?.Breakpoints;
            int count = breakpoints?.Count ?? 0;
            for (int index = 1; index <= count; index++)
            {
                EnvDTE.Breakpoint current = breakpoints.Item(index);
                if (string.Equals(current?.Tag, expectedTag, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Visual Studio returned from breakpoint deletion but the candidate still exists.");
                }
            }
        }

        [SuppressMessage("Usage", "VSTHRD010", Justification = "Each tag lookup/delete callback is executed through ExecuteOnMainThread.")]
        private void DeleteBreakpointByTagSliced(string tag, NearbyProbeIdentity probeIdentity)
        {
            if (string.IsNullOrEmpty(tag)) throw new InvalidOperationException("Original breakpoint tag is unavailable.");
            BreakpointSliceHeader header = ExecuteOnMainThread(CaptureBreakpointSliceHeader);
            for (int index = 1; index <= header.Count; index++)
            {
                DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                int capturedIndex = index;
                bool deleted = ExecuteOnMainThread(
                    () => DeleteBreakpointByTagAtIndex(capturedIndex, tag, probeIdentity));
                if (deleted) return;
            }
            throw new StaleDebugSessionException("Original breakpoint changed before relocated-breakpoint cleanup.");
        }

        private bool DeleteBreakpointByTagAtIndex(
            int index,
            string expectedTag,
            NearbyProbeIdentity probeIdentity)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!IsNearbyProbeContextCurrent(probeIdentity))
            {
                throw new StaleDebugContextException(
                    "Debug context changed before relocated-breakpoint cleanup.");
            }
            try
            {
                EnvDTE.Breakpoint breakpoint = Debugger?.Breakpoints?.Item(index);
                if (breakpoint == null || !string.Equals(breakpoint.Tag, expectedTag, StringComparison.Ordinal))
                {
                    return false;
                }
                CommandEffectExecutor.Issue("breakpoint.delete_by_tag", breakpoint.Delete);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private sealed class NearbyProbeCapture
        {
            internal BreakpointInfo BreakpointInfo { get; set; }
            internal bool ContextChanged { get; set; }
            internal bool ConditionNotApplied { get; set; }
        }

        private bool IsNearbyProbeContextCurrent(NearbyProbeIdentity identity)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return identity != null && Debugger?.CurrentMode == dbgDebugMode.dbgBreakMode &&
                identity.DebugSessionGeneration == DebugSessionGeneration &&
                identity.BreakSequence == BreakSequence &&
                identity.ContextEpoch == ContextEpoch &&
                string.Equals(identity.SnapshotToken, CreateDebuggerSnapshotToken(Debugger), StringComparison.Ordinal);
        }

        private sealed class NearbyProbeIdentity
        {
            internal string SnapshotToken { get; set; }
            internal long DebugSessionGeneration { get; set; }
            internal long BreakSequence { get; set; }
            internal long ContextEpoch { get; set; }
        }

        private IEnumerable<int> EnumerateNearbyBreakpointLines(int requestedLineNumber, int lineCount)
        {
            if (requestedLineNumber < 1 || lineCount < 1)
            {
                yield break;
            }

            for (int distance = 1; distance <= BreakpointProbeMaxDistance; distance++)
            {
                int forwardLine = requestedLineNumber + distance;
                if (forwardLine >= 1 && forwardLine <= lineCount)
                {
                    yield return forwardLine;
                }

                int backwardLine = requestedLineNumber - distance;
                if (backwardLine >= 1 && backwardLine <= lineCount)
                {
                    yield return backwardLine;
                }
            }
        }

        private BreakpointLocationPreflight PrepareBreakpointLocation(string filePath, int lineNumber)
        {
            SourceLocationPreflight source = InspectSourceLocation(filePath, lineNumber, requireExecutableText: false);
            if (!source.IsValid)
            {
                return BreakpointLocationPreflight.Invalid(source.ErrorMessage);
            }

            return new BreakpointLocationPreflight
            {
                IsValid = true,
                FilePath = source.FilePath,
                LineCount = source.LineCount
            };
        }

        private sealed class BreakpointLocationPreflight
        {
            public bool IsValid { get; set; }
            public string ErrorMessage { get; set; }
            public string FilePath { get; set; }
            public int LineCount { get; set; }

            public static BreakpointLocationPreflight Invalid(string errorMessage)
            {
                return new BreakpointLocationPreflight
                {
                    IsValid = false,
                    ErrorMessage = errorMessage
                };
            }
        }

        private void SafeDeleteBreakpoint(EnvDTE.Breakpoint breakpoint)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (breakpoint == null)
            {
                return;
            }

            try
            {
                CommandEffectExecutor.Issue("breakpoint.cleanup", breakpoint.Delete);
            }
            catch (Exception ex)
            {
                _log($"[SafeDeleteBreakpoint] Failed to delete breakpoint: {ex.GetType().Name}");
            }
        }

        private string BuildSetBreakpointMessage(BreakpointInfo breakpointInfo, string requestedFilePath, int requestedLineNumber)
        {
            if (breakpointInfo == null)
            {
                return $"Breakpoint set at {requestedFilePath}:{requestedLineNumber}";
            }

            if (breakpointInfo.BindingStatus == "pending")
            {
                return $"Breakpoint created at {breakpointInfo.FilePath}:{breakpointInfo.LineNumber} and is pending binding";
            }

            if (breakpointInfo.IsBound &&
                !string.IsNullOrEmpty(breakpointInfo.ResolvedFilePath) &&
                breakpointInfo.ResolvedLineNumber > 0 &&
                (breakpointInfo.ResolvedLineNumber != requestedLineNumber ||
                 !string.Equals(breakpointInfo.ResolvedFilePath, requestedFilePath, StringComparison.OrdinalIgnoreCase)))
            {
                return $"Breakpoint requested at {requestedFilePath}:{requestedLineNumber} and bound to {breakpointInfo.ResolvedFilePath}:{breakpointInfo.ResolvedLineNumber}";
            }

            if (breakpointInfo.LineNumber != requestedLineNumber)
            {
                return $"Breakpoint moved from {requestedFilePath}:{requestedLineNumber} to {breakpointInfo.FilePath}:{breakpointInfo.LineNumber}";
            }

            return $"Breakpoint set at {breakpointInfo.FilePath}:{breakpointInfo.LineNumber}";
        }

        private string GetBreakpointTypeString(dbgBreakpointLocationType type)
        {
            switch (type)
            {
                case dbgBreakpointLocationType.dbgBreakpointLocationTypeFile: return "File";
                case dbgBreakpointLocationType.dbgBreakpointLocationTypeFunction: return "Function";
                case dbgBreakpointLocationType.dbgBreakpointLocationTypeAddress: return "Address";
                case dbgBreakpointLocationType.dbgBreakpointLocationTypeData: return "Data";
                default: return "Unknown";
            }
        }

        #endregion
    }
}
