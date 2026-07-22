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
        #region Build Errors

        [SuppressMessage("Usage", "VSTHRD010", Justification = "Reads one Error List item per bounded UI slice.")]
        public BuildErrorsResult GetBuildErrors(
            bool errorsOnly = false,
            bool warningsOnly = false,
            int startIndex = 0,
            int maxNodes = -1,
            int budgetMs = -1)
        {
            if (startIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(startIndex), "startIndex must be non-negative.");
            }
            int normalizedMaxNodes = NormalizeCollectionNodeLimit(maxNodes);
            int normalizedBudgetMs = NormalizeCollectionBudgetMs(budgetMs);
            var result = new BuildErrorsResult
            {
                MaxNodes = normalizedMaxNodes,
                BudgetMs = normalizedBudgetMs,
                StartIndex = startIndex,
                MaxResultBytes = BuildErrorCaptureBudget.MaxResultBytes,
            };
            var captureBudget = new BuildErrorCaptureBudget();

            int initialCount;
            try
            {
                initialCount = ExecuteOnMainThread(_buildErrorService.GetCount);
            }
            catch (Exception ex)
            {
                RethrowRequestControlFlow(ex);
                result.Partial = true;
                result.SnapshotStatus = "unavailable";
                result.Error = $"Error List unavailable: {ex.Message}";
                return result;
            }

            result.TotalAvailable = initialCount;
            var stopwatch = Stopwatch.StartNew();
            int inspectedCount = 0;
            for (int index = startIndex + 1; index <= initialCount; index++)
            {
                DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                if (inspectedCount >= normalizedMaxNodes || stopwatch.ElapsedMilliseconds >= normalizedBudgetMs)
                {
                    result.Partial = result.Truncated = true;
                    result.Error = AppendError(
                        result.Error,
                        inspectedCount >= normalizedMaxNodes
                            ? $"Error List scan reached maxNodes={normalizedMaxNodes}"
                            : $"Error List scan budget exceeded {normalizedBudgetMs}ms");
                    break;
                }

                try
                {
                    int capturedIndex = index;
                    BuildErrorItem item = ExecuteOnMainThread(
                        () => _buildErrorService.GetItem(capturedIndex, errorsOnly, warningsOnly),
                        DefaultMainThreadTimeoutMs);
                    inspectedCount++;
                    if (item == null)
                    {
                        continue;
                    }

                    if (!captureBudget.TryConsume(item))
                    {
                        inspectedCount--;
                        result.Partial = result.Truncated = true;
                        result.Error = AppendError(
                            result.Error,
                            $"Error List result reached {BuildErrorCaptureBudget.MaxResultBytes} bytes");
                        break;
                    }

                    result.Items.Add(item);
                    if (item.Truncated) result.Partial = result.Truncated = true;
                    if (string.Equals(item.Severity, "Error", StringComparison.Ordinal)) result.ErrorCount++;
                    if (string.Equals(item.Severity, "Warning", StringComparison.Ordinal)) result.WarningCount++;
                }
                catch (Exception ex)
                {
                    RethrowRequestControlFlow(ex);
                    inspectedCount++;
                    result.Partial = true;
                    result.Error = AppendError(result.Error, $"Error List item {index}: {ex.Message}");
                }
            }

            result.CapturedCount = result.Items.Count;
            result.ResultBytes = captureBudget.ResultBytes;
            result.ScannedCount = inspectedCount;
            result.NextIndex = Math.Min(initialCount, startIndex + inspectedCount);
            result.HasMore = result.NextIndex < initialCount;
            if (result.HasMore)
            {
                result.Partial = result.Truncated = true;
            }
            int finalCount;
            try
            {
                finalCount = ExecuteOnMainThread(_buildErrorService.GetCount);
            }
            catch (Exception ex)
            {
                RethrowRequestControlFlow(ex);
                finalCount = -1;
                result.Partial = true;
                result.Error = AppendError(result.Error, $"Error List final snapshot unavailable: {ex.Message}");
            }

            bool countStable = finalCount == initialCount;
            result.SnapshotStable = false;
            if (!countStable)
            {
                result.Partial = true;
                result.Error = AppendError(result.Error, "Error List changed while items were captured");
            }
            result.SnapshotStatus = countStable ? "best_effort" : "unstable";
            return result;
        }

        #endregion

        #region Full Context

        [SuppressMessage("Usage", "VSTHRD010", Justification = "Composes debugger entrypoints that each marshal their bounded DTE slice to the UI thread.")]
        public FullContextResult GetFullContext(
            bool threadIncludeFrames = true,
            int threadMaxFramesPerThread = -1,
            int threadMaxNodes = -1,
            int threadBudgetMs = -1)
        {
            int normalizedThreadMaxFramesPerThread = NormalizeThreadFrameLimit(threadMaxFramesPerThread);
            int normalizedThreadMaxNodes = NormalizeThreadNodeLimit(threadMaxNodes);
            int normalizedThreadBudgetMs = NormalizeThreadBudgetMs(threadBudgetMs);
            var compositeBudget = new CompositeCaptureBudget();
            var fullContextResult = new FullContextResult
            {
                MaxResultBytes = CompositeCaptureBudget.MaxResultBytes,
            };
            FullContextSnapshotBoundary startBoundary = ExecuteOnMainThread(CaptureFullContextBoundary);
            fullContextResult.StartDebugSessionGeneration = startBoundary.StopEvent.Generation;
            fullContextResult.StartBreakSequence = startBoundary.StopEvent.BreakSequence;
            fullContextResult.StartContextEpoch = startBoundary.StopEvent.ContextEpoch;
            try
            {
                fullContextResult.Status = RetainFullContextComponent(
                    fullContextResult,
                    compositeBudget,
                    "status",
                    GetDebugStatus());
            }
            catch (Exception ex)
            {
                RethrowRequestControlFlow(ex);
                _log($"GetFullContext: Status failed: {ex.GetType().Name}");
                RecordFullContextError(fullContextResult, "status", ex);
            }
            if (fullContextResult.Status?.IsPaused == true)
            {
                try
                {
                    if (BeginFullContextComponent(fullContextResult, compositeBudget, "callstack"))
                    {
                        fullContextResult.Callstack = RetainFullContextComponent(
                            fullContextResult, compositeBudget, "callstack", GetCallStack());
                    }
                }
                catch (Exception ex) { RethrowRequestControlFlow(ex); _log($"GetFullContext: Callstack failed: {ex.GetType().Name}"); RecordFullContextError(fullContextResult, "callstack", ex); }
                try
                {
                    if (BeginFullContextComponent(fullContextResult, compositeBudget, "locals"))
                    {
                        fullContextResult.Locals = RetainFullContextComponent(
                            fullContextResult, compositeBudget, "locals", GetLocals(2));
                    }
                }
                catch (Exception ex) { RethrowRequestControlFlow(ex); _log($"GetFullContext: Locals failed: {ex.GetType().Name}"); RecordFullContextError(fullContextResult, "locals", ex); }
                try
                {
                    if (BeginFullContextComponent(fullContextResult, compositeBudget, "exception"))
                    {
                        fullContextResult.Exception = RetainFullContextComponent(
                            fullContextResult, compositeBudget, "exception", GetException());
                    }
                }
                catch (Exception ex) { RethrowRequestControlFlow(ex); _log($"GetFullContext: Exception failed: {ex.GetType().Name}"); RecordFullContextError(fullContextResult, "exception", ex); }
            }
            if (BeginFullContextComponent(fullContextResult, compositeBudget, "threads")) try
            {
                fullContextResult.Threads = RetainFullContextComponent(
                    fullContextResult,
                    compositeBudget,
                    "threads",
                    GetThreads(
                        threadIncludeFrames,
                        normalizedThreadMaxFramesPerThread,
                        normalizedThreadMaxNodes,
                        normalizedThreadBudgetMs));
            }
            catch (Exception ex)
            {
                RethrowRequestControlFlow(ex);
                _log($"GetFullContext: sliced thread capture failed: {ex.GetType().Name}");
                RecordFullContextError(fullContextResult, "threads", ex);
            }
            if (BeginFullContextComponent(fullContextResult, compositeBudget, "modules")) try
            {
                fullContextResult.Modules = RetainFullContextComponent(
                    fullContextResult, compositeBudget, "modules", GetModules());
            }
            catch (Exception ex)
            {
                RethrowRequestControlFlow(ex);
                _log($"GetFullContext: background module completion failed: {ex.GetType().Name}");
                RecordFullContextError(fullContextResult, "modules", ex);
            }
            AggregateFullContextComponentState(fullContextResult);
            FullContextSnapshotBoundary endBoundary = ExecuteOnMainThread(CaptureFullContextBoundary);
            fullContextResult.EndDebugSessionGeneration = endBoundary.StopEvent.Generation;
            fullContextResult.EndBreakSequence = endBoundary.StopEvent.BreakSequence;
            fullContextResult.EndContextEpoch = endBoundary.StopEvent.ContextEpoch;
            bool boundaryStable =
                startBoundary.StopEvent.Generation == endBoundary.StopEvent.Generation &&
                startBoundary.StopEvent.BreakSequence == endBoundary.StopEvent.BreakSequence &&
                startBoundary.StopEvent.ContextEpoch == endBoundary.StopEvent.ContextEpoch &&
                !string.IsNullOrEmpty(startBoundary.SnapshotToken) &&
                string.Equals(startBoundary.SnapshotToken, endBoundary.SnapshotToken, StringComparison.Ordinal);
            // get_full_context includes locals/exception evaluation while paused, so
            // even matching boundaries are only a best-effort composite snapshot.
            bool unsafeEvaluationAttempted = fullContextResult.Status?.IsPaused == true;
            fullContextResult.SnapshotStable = boundaryStable && !unsafeEvaluationAttempted &&
                !fullContextResult.Partial;
            fullContextResult.SnapshotStatus = !boundaryStable
                ? "unstable"
                : (unsafeEvaluationAttempted || fullContextResult.Partial ? "best_effort" : "ok");
            if (!boundaryStable)
            {
                RecordFullContextPartial(fullContextResult, "snapshot", "debug context changed during composite capture");
            }
            fullContextResult.ResultBytes = compositeBudget.ResultBytes;
            return fullContextResult;
        }

        private static bool BeginFullContextComponent(
            FullContextResult result,
            CompositeCaptureBudget budget,
            string component)
        {
            if (budget.CanAttemptComponent) return true;
            RecordFullContextPartial(result, component, "omitted because the composite result byte budget was exhausted");
            return false;
        }

        private static T RetainFullContextComponent<T>(
            FullContextResult result,
            CompositeCaptureBudget budget,
            string component,
            T value) where T : class
        {
            if (budget.TryConsumeComponent(component, value)) return value;
            RecordFullContextPartial(result, component, "omitted because it exceeded the remaining composite result byte budget");
            return null;
        }

        private FullContextSnapshotBoundary CaptureFullContextBoundary()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return new FullContextSnapshotBoundary
            {
                SnapshotToken = CreateDebuggerSnapshotToken(Debugger),
                StopEvent = _sessionTracker.GetStopEventSnapshot(),
            };
        }

        private static void AggregateFullContextComponentState(FullContextResult result)
        {
            if (result == null) return;
            if (result.Callstack != null &&
                (!result.Callstack.SnapshotStable ||
                 !string.Equals(result.Callstack.SnapshotStatus, "ok", StringComparison.Ordinal)))
            {
                RecordFullContextPartial(
                    result,
                    "callstack",
                    $"snapshotStatus={result.Callstack.SnapshotStatus ?? "unavailable"}");
            }
            if (result.Locals?.Partial == true)
            {
                RecordFullContextPartial(result, "locals", result.Locals.Error ?? "partial");
            }
            if (result.Exception?.Partial == true)
            {
                RecordFullContextPartial(result, "exception", result.Exception.Error ?? "partial");
            }
            if (result.Threads != null &&
                (result.Threads.Partial || !result.Threads.SnapshotStable ||
                 !string.Equals(result.Threads.SnapshotStatus, "ok", StringComparison.Ordinal)))
            {
                RecordFullContextPartial(
                    result,
                    "threads",
                    result.Threads.Error ?? $"snapshotStatus={result.Threads.SnapshotStatus ?? "unavailable"}");
            }
            if (result.Modules != null &&
                (result.Modules.Partial || !result.Modules.SnapshotStable ||
                 !string.Equals(result.Modules.SnapshotStatus, "ok", StringComparison.Ordinal)))
            {
                RecordFullContextPartial(
                    result,
                    "modules",
                    result.Modules.Error ?? $"snapshotStatus={result.Modules.SnapshotStatus ?? "unavailable"}");
            }
        }

        private sealed class FullContextSnapshotBoundary
        {
            internal string SnapshotToken { get; set; }
            internal StopEventSnapshot StopEvent { get; set; }
        }

        private static void RecordFullContextPartial(
            FullContextResult result,
            string component,
            string message)
        {
            message = BoundCompositeDiagnostic(message);
            result.Partial = true;
            if (!result.ComponentErrors.ContainsKey(component))
            {
                result.ComponentErrors[component] = message;
                result.Error = string.IsNullOrEmpty(result.Error)
                    ? $"{component}: {message}"
                    : $"{result.Error}; {component}: {message}";
            }
        }

        private static void RecordFullContextError(
            FullContextResult result,
            string component,
            Exception exception)
        {
            string message = exception?.Message ?? "Component capture failed";
            message = BoundCompositeDiagnostic(message);
            result.Partial = true;
            result.ComponentErrors[component] = message;
            result.Error = string.IsNullOrEmpty(result.Error)
                ? $"{component}: {message}"
                : $"{result.Error}; {component}: {message}";
        }

        public DebugBundleResult CaptureDebugBundle(
            List<string> expressions = null,
            bool expandChildren = false,
            int maxDepth = 2,
            int outputTailLines = 80,
            bool includeLocals = false,
            bool includeModules = false,
            bool includeRegisters = false,
            bool includeOutput = false,
            bool includeThreads = true,
            bool includeException = true,
            bool threadIncludeFrames = true,
            int threadMaxFramesPerThread = -1,
            int threadMaxNodes = -1,
            int threadBudgetMs = -1)
        {
            var normalizedExpressions = NormalizeExpressions(expressions);
            var compositeBudget = new CompositeCaptureBudget();
            string snapshotId = Guid.NewGuid().ToString("N");
            string capturedAt = DateTime.UtcNow.ToString("o");
            List<string> requestedComponents = BuildIncludedDebugBundleComponents(
                includeLocals, includeModules, includeRegisters, includeOutput,
                includeThreads, includeException, normalizedExpressions.Count);
            var bundle = new DebugBundleResult
            {
                SnapshotId = snapshotId,
                CapturedAt = capturedAt,
                SnapshotAttempts = 1,
                SnapshotStatus = "unavailable",
                MaxResultBytes = CompositeCaptureBudget.MaxResultBytes,
                IncludedComponents = new List<string>(),
                OmittedComponents = BuildOmittedDebugBundleComponents(
                    includeLocals, includeModules, includeRegisters, includeOutput,
                    includeThreads, includeException)
            };
            string tokenBefore = ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return CreateDebuggerSnapshotToken(Debugger);
            });

            try
            {
                bundle.Status = RetainBundleComponent(
                    bundle, compositeBudget, "status", GetDebugStatus());
                bundle.StopReason = bundle.Status?.StopReason ?? "not_paused";
                if (bundle.Status?.IsPaused != true)
                {
                    bundle.Error = "Not in break mode";
                    bundle.Partial = true;
                    bundle.Expressions = BuildUnavailableBatchEvaluateResult(
                        snapshotId, capturedAt, normalizedExpressions.Count, bundle.Error);
                    OmitUncapturedBundleComponents(
                        bundle, requestedComponents, "not captured because the debugger is not paused");
                    bundle.ResultBytes = compositeBudget.ResultBytes;
                    return bundle;
                }
            }
            catch (Exception ex)
            {
                RethrowRequestControlFlow(ex);
                OmitBundleComponent(bundle, "status", ex.Message);
                OmitUncapturedBundleComponents(
                    bundle, requestedComponents, "not captured because debug status was unavailable");
                bundle.ResultBytes = compositeBudget.ResultBytes;
                return bundle;
            }

            try
            {
                if (BeginBundleComponent(bundle, compositeBudget, "callstack"))
                {
                    bundle.Callstack = RetainBundleComponent(
                        bundle, compositeBudget, "callstack", GetCallStack());
                    if (bundle.Callstack != null && SnapshotCompletenessPolicy.IsIncomplete(bundle.Callstack))
                    {
                        AppendBundleError(
                            bundle,
                            "callstack",
                            bundle.Callstack?.Error ??
                                $"snapshotStatus={bundle.Callstack?.SnapshotStatus ?? "unavailable"}");
                    }
                }
            }
            catch (Exception ex) { RethrowRequestControlFlow(ex); OmitBundleComponent(bundle, "callstack", ex.Message); }
            if (includeLocals && BeginBundleComponent(bundle, compositeBudget, "locals"))
            {
                try
                {
                    bundle.Locals = RetainBundleComponent(
                        bundle,
                        compositeBudget,
                        "locals",
                        GetLocals(Math.Max(0, Math.Min(maxDepth, 4))));
                }
                catch (Exception ex) { RethrowRequestControlFlow(ex); OmitBundleComponent(bundle, "locals", ex.Message); }
            }
            if (includeException && BeginBundleComponent(bundle, compositeBudget, "exception"))
            {
                try
                {
                    bundle.Exception = RetainBundleComponent(
                        bundle, compositeBudget, "exception", GetException());
                    if (bundle.Exception?.Partial == true) AppendBundleError(bundle, "exception", bundle.Exception.Error ?? bundle.Exception.Status);
                }
                catch (Exception ex) { RethrowRequestControlFlow(ex); OmitBundleComponent(bundle, "exception", ex.Message); }
            }
            if (includeRegisters && BeginBundleComponent(bundle, compositeBudget, "registers"))
            {
                try
                {
                    bundle.Registers = RetainBundleComponent(
                        bundle, compositeBudget, "registers", GetRegisters());
                    if (bundle.Registers != null && SnapshotCompletenessPolicy.IsIncomplete(bundle.Registers))
                    {
                        AppendBundleError(
                            bundle,
                            "registers",
                            bundle.Registers?.Error ??
                                $"status={bundle.Registers?.Status ?? "unavailable"}, " +
                                $"snapshotStatus={bundle.Registers?.SnapshotStatus ?? "unavailable"}");
                    }
                }
                catch (Exception ex) { RethrowRequestControlFlow(ex); OmitBundleComponent(bundle, "registers", ex.Message); }
            }
            if (includeOutput && BeginBundleComponent(bundle, compositeBudget, "output"))
            {
                try
                {
                    bundle.Output = RetainBundleComponent(
                        bundle,
                        compositeBudget,
                        "output",
                        GetOutputWindow("Debug", Math.Max(0, Math.Min(outputTailLines, 500))));
                    if (bundle.Output != null && SnapshotCompletenessPolicy.IsIncomplete(bundle.Output))
                    {
                        AppendBundleError(
                            bundle,
                            "output",
                            bundle.Output?.Error ??
                                $"snapshotStatus={bundle.Output?.SnapshotStatus ?? "unavailable"}");
                    }
                }
                catch (Exception ex) { RethrowRequestControlFlow(ex); OmitBundleComponent(bundle, "output", ex.Message); }
            }
            if (includeThreads && BeginBundleComponent(bundle, compositeBudget, "threads"))
            {
                try
                {
                    bundle.Threads = RetainBundleComponent(
                        bundle,
                        compositeBudget,
                        "threads",
                        GetThreads(
                            threadIncludeFrames,
                            NormalizeThreadFrameLimit(threadMaxFramesPerThread),
                            NormalizeThreadNodeLimit(threadMaxNodes),
                            NormalizeThreadBudgetMs(threadBudgetMs)));
                    if (bundle.Threads != null && SnapshotCompletenessPolicy.IsIncomplete(bundle.Threads))
                    {
                        AppendBundleError(
                            bundle,
                            "threads",
                            bundle.Threads?.Error ??
                                $"snapshotStatus={bundle.Threads?.SnapshotStatus ?? "unavailable"}");
                    }
                }
                catch (Exception ex) { RethrowRequestControlFlow(ex); OmitBundleComponent(bundle, "threads", ex.Message); }
            }
            if (includeModules && BeginBundleComponent(bundle, compositeBudget, "modules"))
            {
                try
                {
                    bundle.Modules = RetainBundleComponent(
                        bundle, compositeBudget, "modules", GetModules());
                    if (bundle.Modules != null && SnapshotCompletenessPolicy.IsIncomplete(bundle.Modules))
                    {
                        AppendBundleError(
                            bundle,
                            "modules",
                            bundle.Modules?.Error ??
                                $"snapshotStatus={bundle.Modules?.SnapshotStatus ?? "unavailable"}");
                    }
                }
                catch (Exception ex) { RethrowRequestControlFlow(ex); OmitBundleComponent(bundle, "modules", ex.Message); }
            }
            if (normalizedExpressions.Count == 0)
            {
                bundle.Expressions = new BatchEvaluateResult
                {
                    SnapshotId = snapshotId,
                    CapturedAt = capturedAt,
                    SnapshotStable = true,
                    SnapshotAttempts = 1,
                    SnapshotStatus = "ok",
                    RequestedCount = 0
                };
            }
            else if (BeginBundleComponent(bundle, compositeBudget, "expressions")) try
            {
                var expressionsResult = EvaluateExpressionsBatch(
                    normalizedExpressions, expandChildren, maxDepth);
                bundle.Expressions = RetainBundleComponent(
                    bundle, compositeBudget, "expressions", expressionsResult);
                if (!string.IsNullOrEmpty(bundle.Expressions?.Error)) AppendBundleError(bundle, "expressions", bundle.Expressions.Error);
            }
            catch (Exception ex)
            {
                RethrowRequestControlFlow(ex);
                bundle.Expressions = BuildUnavailableBatchEvaluateResult(
                    snapshotId, capturedAt, normalizedExpressions.Count, ex.Message);
                bundle.Expressions = null;
                OmitBundleComponent(bundle, "expressions", ex.Message);
            }

            bool unsafeEvaluationAttempted =
                includeLocals || includeException || normalizedExpressions.Count > 0;
            bool componentUnstable = HasUnstableDebugBundleComponent(bundle);
            if (unsafeEvaluationAttempted)
            {
                bundle.SnapshotStable = false;
            }
            else
            {
                string tokenAfter = ExecuteOnMainThread(() =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    return CreateDebuggerSnapshotToken(Debugger);
                });
                bundle.SnapshotStable = !componentUnstable &&
                    !string.IsNullOrEmpty(tokenBefore) &&
                    string.Equals(tokenBefore, tokenAfter, StringComparison.Ordinal);
            }
            bundle.Partial = IsDebugBundlePartial(bundle) || !bundle.SnapshotStable;
            bundle.SnapshotStatus = bundle.SnapshotStable
                ? (bundle.Partial ? "partial" : "ok")
                : "unstable";
            bundle.ResultBytes = compositeBudget.ResultBytes;
            return bundle;
        }

        private static bool BeginBundleComponent(
            DebugBundleResult bundle,
            CompositeCaptureBudget budget,
            string component)
        {
            if (budget.CanAttemptComponent) return true;
            OmitBundleComponent(bundle, component, "composite result byte budget was exhausted");
            return false;
        }

        private static T RetainBundleComponent<T>(
            DebugBundleResult bundle,
            CompositeCaptureBudget budget,
            string component,
            T value) where T : class
        {
            if (budget.TryConsumeComponent(component, value))
            {
                if (bundle.IncludedComponents != null &&
                    !bundle.IncludedComponents.Contains(component, StringComparer.OrdinalIgnoreCase))
                    bundle.IncludedComponents.Add(component);
                bundle.OmittedComponents?.RemoveAll(valueName =>
                    string.Equals(valueName, component, StringComparison.OrdinalIgnoreCase));
                return value;
            }
            OmitBundleComponent(bundle, component, "component exceeded the remaining composite result byte budget");
            return null;
        }

        private static void OmitBundleComponent(
            DebugBundleResult bundle,
            string component,
            string reason)
        {
            bundle.IncludedComponents?.RemoveAll(value =>
                string.Equals(value, component, StringComparison.OrdinalIgnoreCase));
            if (bundle.OmittedComponents != null &&
                !bundle.OmittedComponents.Contains(component, StringComparer.OrdinalIgnoreCase))
            {
                bundle.OmittedComponents.Add(component);
            }
            AppendBundleError(bundle, component, reason);
        }

        private static void OmitUncapturedBundleComponents(
            DebugBundleResult bundle,
            IEnumerable<string> requestedComponents,
            string reason)
        {
            foreach (string component in requestedComponents ?? Enumerable.Empty<string>())
            {
                if (bundle.IncludedComponents != null &&
                    bundle.IncludedComponents.Contains(component, StringComparer.OrdinalIgnoreCase))
                    continue;
                if (bundle.OmittedComponents != null &&
                    bundle.OmittedComponents.Contains(component, StringComparer.OrdinalIgnoreCase))
                    continue;
                OmitBundleComponent(bundle, component, reason);
            }
        }

        private static void AppendBundleError(DebugBundleResult bundle, string component, string message)
        {
            bundle.Partial = true;
            string detail = BoundCompositeDiagnostic(component + ": " + message);
            string combined = string.IsNullOrEmpty(bundle.Error) ? detail : bundle.Error + "; " + detail;
            bundle.Error = BoundCompositeDiagnostic(combined, 64 * 1024);
        }

        private static string BoundCompositeDiagnostic(string value, int maxBytes = 8 * 1024)
        {
            return OutputContentBudget.FitPrefix(value ?? string.Empty, maxBytes, out _);
        }

        private List<string> BuildIncludedDebugBundleComponents(
            bool includeLocals,
            bool includeModules,
            bool includeRegisters,
            bool includeOutput,
            bool includeThreads,
            bool includeException,
            int expressionCount)
        {
            var components = new List<string> { "status", "callstack" };
            if (includeException) components.Add("exception");
            if (includeThreads) components.Add("threads");
            if (includeLocals) components.Add("locals");
            if (includeModules) components.Add("modules");
            if (includeRegisters) components.Add("registers");
            if (includeOutput) components.Add("output");
            if (expressionCount > 0) components.Add("expressions");
            return components;
        }

        private List<string> BuildOmittedDebugBundleComponents(
            bool includeLocals,
            bool includeModules,
            bool includeRegisters,
            bool includeOutput,
            bool includeThreads,
            bool includeException)
        {
            var components = new List<string>();
            if (!includeException) components.Add("exception");
            if (!includeThreads) components.Add("threads");
            if (!includeLocals) components.Add("locals");
            if (!includeModules) components.Add("modules");
            if (!includeRegisters) components.Add("registers");
            if (!includeOutput) components.Add("output");
            return components;
        }

        private bool BundleIncludes(DebugBundleResult bundle, string component)
        {
            return bundle.IncludedComponents != null &&
                bundle.IncludedComponents.Contains(component, StringComparer.OrdinalIgnoreCase);
        }

        private BatchEvaluateResult BuildUnavailableBatchEvaluateResult(string snapshotId, string capturedAt, int requestedCount, string error)
        {
            return new BatchEvaluateResult
            {
                SnapshotId = snapshotId,
                CapturedAt = capturedAt,
                RequestedCount = requestedCount,
                SuccessCount = 0,
                FailedCount = requestedCount,
                Partial = requestedCount > 0,
                SnapshotStatus = "unavailable",
                Error = error
            };
        }

        private bool IsDebugBundlePartial(DebugBundleResult bundle)
        {
            if (bundle == null)
            {
                return true;
            }

            if (!string.IsNullOrEmpty(bundle.Error))
            {
                return true;
            }

            if (SnapshotCompletenessPolicy.IsIncomplete(bundle.Callstack))
            {
                return true;
            }

            if (BundleIncludes(bundle, "threads") &&
                SnapshotCompletenessPolicy.IsIncomplete(bundle.Threads))
            {
                return true;
            }

            if (BundleIncludes(bundle, "locals") && (bundle.Locals == null || bundle.Locals.Partial))
            {
                return true;
            }

            if ((BundleIncludes(bundle, "modules") && SnapshotCompletenessPolicy.IsIncomplete(bundle.Modules)) ||
                (BundleIncludes(bundle, "registers") && SnapshotCompletenessPolicy.IsIncomplete(bundle.Registers)) ||
                (BundleIncludes(bundle, "output") && SnapshotCompletenessPolicy.IsIncomplete(bundle.Output)))
            {
                return true;
            }

            if (BundleIncludes(bundle, "exception") &&
                (bundle.Exception == null || bundle.Exception.Partial ||
                 string.Equals(bundle.Exception.Status, "failed", StringComparison.Ordinal) ||
                 string.Equals(bundle.Exception.Status, "unavailable", StringComparison.Ordinal)))
            {
                return true;
            }

            if (BundleIncludes(bundle, "expressions") &&
                bundle.Expressions != null &&
                (bundle.Expressions.Partial || bundle.Expressions.FailedCount > 0))
            {
                return true;
            }

            return false;
        }

        private bool HasUnstableDebugBundleComponent(DebugBundleResult bundle)
        {
            if (bundle == null) return true;
            if (BundleIncludes(bundle, "callstack") && SnapshotCompletenessPolicy.IsUnstable(bundle.Callstack))
                return true;
            if (BundleIncludes(bundle, "threads") && SnapshotCompletenessPolicy.IsUnstable(bundle.Threads))
                return true;
            if (BundleIncludes(bundle, "modules") && SnapshotCompletenessPolicy.IsUnstable(bundle.Modules))
                return true;
            if (BundleIncludes(bundle, "registers") && SnapshotCompletenessPolicy.IsUnstable(bundle.Registers))
                return true;
            if (BundleIncludes(bundle, "output") && SnapshotCompletenessPolicy.IsUnstable(bundle.Output))
                return true;
            return false;
        }

        #endregion

        #region Instance Info

        /// <summary>
        /// Gets information about this Visual Studio instance for multi-instance support.
        /// </summary>
        public InstanceInfoResult GetInstanceInfo(string pipeName)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;

                var result = new InstanceInfoResult
                {
                    ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id,
                    PipeName = pipeName,
                    IsDebugging = debugger.CurrentMode != dbgDebugMode.dbgDesignMode,
                    IsPaused = debugger.CurrentMode == dbgDebugMode.dbgBreakMode,
                    DebugMode = GetDebugModeString(debugger.CurrentMode)
                };

                // Get solution info
                try
                {
                    if (_dte.Solution != null && !string.IsNullOrEmpty(_dte.Solution.FullName))
                    {
                        result.SolutionPath = _dte.Solution.FullName;
                        result.SolutionName = System.IO.Path.GetFileNameWithoutExtension(_dte.Solution.FullName);
                    }
                    else
                    {
                        result.SolutionName = "(No solution open)";
                    }
                }
                catch
                {
                    result.SolutionName = "(Unknown)";
                }

                // Get current process name if debugging
                if (result.IsDebugging && debugger.CurrentProcess != null)
                {
                    try
                    {
                        result.CurrentProcess = debugger.CurrentProcess.Name;
                    }
                    catch (Exception ex)
                    {
                        _log($"[GetInstanceInfo] Error getting current process: {ex.GetType().Name}");
                    }
                }

                // Get VS version
                try
                {
                    result.VsVersion = _dte.Version;
                }
                catch (Exception ex)
                {
                    _log($"[GetInstanceInfo] Error getting VS version: {ex.GetType().Name}");
                }

                return result;
            });
        }

        #endregion
    }
}
