using System;
using System.Collections.Generic;
using EnvDTE;
using EnvDTE80;
using EnvDTE100;
using Microsoft.VisualStudio.Shell;
using VSDebuggerMCP.Protocol;
using VSDebuggerMCP.Services;

namespace VSDebuggerMCP
{
#pragma warning disable VSTHRD010 // Every EnvDTE access below is inside an ExecuteOnMainThread slice guarded by ThrowIfNotOnUIThread.
    public partial class DebuggerService
    {
        private const int VariableUiSliceTimeoutMs = 20;

        private EvaluateResult EvaluateExpressionSliced(
            string expression,
            bool expandChildren,
            int maxDepth,
            VariableCaptureBudget sharedBudget = null)
        {
            var budget = sharedBudget ?? new VariableCaptureBudget(
                ExpressionExpansionMaxNodes,
                ExpressionExpansionBudgetMs);
            bool enteredRoot = budget.TryEnterNode();
            string capturedExpression = budget.CaptureString(
                expression,
                "expression",
                out bool expressionTruncated);
            var result = new EvaluateResult
            {
                Expression = capturedExpression,
                Partial = expressionTruncated,
                CapturedBytes = budget.SerializedBytes,
                MaxCapturedBytes = VariableCaptureBudget.MaxSerializedBytes,
            };
            if (!enteredRoot)
            {
                result.IsValid = false;
                result.Partial = true;
                result.ErrorMessage = budget.CombinedReason;
                return result;
            }

            EvaluationRootCapture root;
            try
            {
                root = ExecuteOnMainThread(
                    () => CaptureEvaluationRoot(expression, expandChildren, maxDepth, budget),
                    ExpressionEvaluationTimeoutMs + VariableUiSliceTimeoutMs);
            }
            catch (Exception ex)
            {
                RethrowRequestControlFlow(ex);
                result.IsValid = false;
                result.ErrorMessage = budget.CaptureString(
                    ex.Message,
                    "evaluation error",
                    out bool errorTruncated);
                result.Partial |= errorTruncated;
                result.CapturedBytes = budget.SerializedBytes;
                return result;
            }

            result = root.Result;
            result.Expression = capturedExpression;
            result.Partial |= expressionTruncated;
            if (root.NeedsHexValue &&
                TryFormatCapturedNumericValueAsHex(result.Value, out string hexValue))
            {
                result.HexValue = budget.CaptureString(
                    hexValue, "hex value", out bool hexTruncated);
                result.Partial |= hexTruncated;
            }
            if (expandChildren && root.Children != null && root.ChildCount > 0)
            {
                result.Children = new List<VariableInfo>();
                CaptureVariableCollectionSliced(
                    root.Children,
                    root.ChildCount,
                    0,
                    Math.Max(0, Math.Min(maxDepth, ExpressionExpansionMaxDepth)),
                    budget,
                    result.Children,
                    null);
            }
            if (budget.IsIncomplete)
            {
                result.Partial = true;
                result.ErrorMessage = AppendDiagnostic(result.ErrorMessage, budget.CombinedReason);
            }
            result.CapturedBytes = budget.SerializedBytes;
            result.MaxCapturedBytes = VariableCaptureBudget.MaxSerializedBytes;
            return result;
        }

        private EvaluationRootCapture CaptureEvaluationRoot(
            string expression,
            bool expandChildren,
            int maxDepth,
            VariableCaptureBudget budget)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new EvaluateResult
            {
                MaxCapturedBytes = VariableCaptureBudget.MaxSerializedBytes,
            };
            Debugger5 debugger = Debugger;
            if (debugger == null || debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
            {
                result.IsValid = false;
                result.ErrorMessage = "Not in break mode";
                return new EvaluationRootCapture { Result = result };
            }

            MarkUnsafeEvaluationIssued();
            EnvDTE.Expression evaluated = debugger.GetExpression(
                expression,
                false,
                ExpressionEvaluationTimeoutMs);
            if (evaluated == null)
            {
                result.IsValid = false;
                result.ErrorMessage = "Expression evaluation returned no result";
                return new EvaluationRootCapture { Result = result };
            }
            result.IsValid = CaptureVariableBoolean(
                () => evaluated.IsValidValue, "validity", budget);
            result.Value = CaptureVariableField(
                () => evaluated.Value, "value", budget, out bool valueTruncated);
            result.Type = CaptureVariableField(
                () => evaluated.Type, "type", budget, out bool typeTruncated);
            result.Partial = valueTruncated || typeTruncated;
            if (!result.IsValid) result.ErrorMessage = result.Value;

            Expressions children = null;
            int childCount = 0;
            if (expandChildren && maxDepth >= 0 && budget.CanContinue())
            {
                try
                {
                    children = evaluated.DataMembers;
                    childCount = children?.Count ?? 0;
                }
                catch (Exception ex)
                {
                    RethrowRequestControlFlow(ex);
                    budget.ReportCaptureError("Expression child metadata is unavailable: " + ex.Message);
                }
            }
            result.HasChildren = childCount > 0;
            result.CapturedBytes = budget.SerializedBytes;
            return new EvaluationRootCapture
            {
                Result = result,
                Children = children,
                ChildCount = childCount,
                NeedsHexValue = result.IsValid && IsNumericType(result.Type),
            };
        }

        private static bool TryFormatCapturedNumericValueAsHex(
            string capturedValue,
            out string hexValue)
        {
            hexValue = null;
            string value = capturedValue?.Trim();
            if (string.IsNullOrEmpty(value)) return false;
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                ulong.TryParse(
                    value.Substring(2),
                    System.Globalization.NumberStyles.AllowHexSpecifier,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out _))
            {
                hexValue = value;
                return true;
            }
            if (long.TryParse(
                value,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out long signedValue))
            {
                hexValue = signedValue < 0
                    ? "0x" + unchecked((ulong)signedValue).ToString("X16")
                    : "0x" + signedValue.ToString("X");
                return true;
            }
            if (ulong.TryParse(
                value,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out ulong unsignedValue))
            {
                hexValue = "0x" + unsignedValue.ToString("X");
                return true;
            }
            return false;
        }

        private LocalsResult CaptureLocalsSliced(
            int maxDepth, int threadId, int frameIndex,
            long debugSessionGeneration, long breakSequence, long contextEpoch,
            int processId, string frameId, int selectedThreadIndex)
        {
            LocalsCaptureHeader header = ExecuteOnMainThread(() => CaptureLocalsHeader(
                maxDepth, threadId, frameIndex, debugSessionGeneration, breakSequence,
                contextEpoch, processId, frameId, selectedThreadIndex));
            LocalsResult result = header.Result;
            if (!header.Available) return result;

            var budget = new VariableCaptureBudget(LocalsExpansionMaxNodes, LocalsExpansionBudgetMs);
            CaptureVariableCollectionSliced(
                header.Locals, header.LocalCount, 0, header.MaxDepth,
                budget, result.Variables, null);
            CaptureVariableCollectionSliced(
                header.Arguments, header.ArgumentCount, 0, header.MaxDepth,
                budget, result.Variables, "[arg] ");
            if (budget.IsIncomplete)
            {
                result.Partial = true;
                result.Error = AppendDiagnostic(result.Error, budget.CombinedReason);
            }
            result.CapturedBytes = budget.SerializedBytes;
            result.MaxCapturedBytes = VariableCaptureBudget.MaxSerializedBytes;
            return result;
        }

        private LocalsCaptureHeader CaptureLocalsHeader(
            int maxDepth, int threadId, int frameIndex,
            long debugSessionGeneration, long breakSequence, long contextEpoch,
            int processId, string frameId, int selectedThreadIndex)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new LocalsResult { FrameIndex = frameIndex };
            var header = new LocalsCaptureHeader { Result = result };
            Debugger5 debugger = Debugger;
            if (debugger == null || debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
            {
                result.Error = "Not in break mode";
                return header;
            }

            ObserveDebugContext(debugger);
            StopEventSnapshot beforeEvaluation = _sessionTracker.GetStopEventSnapshot();
            if (beforeEvaluation.DebuggeeProcessId <= 0)
                throw new DebugContextNotFoundException(
                    "The current Visual Studio debuggee has no stable process identity; locals cannot be bound to a reusable frame context.");
            bool hasExplicitContext = !string.IsNullOrWhiteSpace(frameId);
            if (hasExplicitContext &&
                (debugSessionGeneration != beforeEvaluation.Generation ||
                 breakSequence != beforeEvaluation.BreakSequence ||
                 contextEpoch != beforeEvaluation.ContextEpoch ||
                 processId != beforeEvaluation.DebuggeeProcessId))
                throw new StaleDebugContextException(
                    $"Frame context {debugSessionGeneration}:{breakSequence}:{contextEpoch}:{processId} is stale; " +
                    $"the current context is {beforeEvaluation.Generation}:{beforeEvaluation.BreakSequence}:" +
                    $"{beforeEvaluation.ContextEpoch}:{beforeEvaluation.DebuggeeProcessId}.");

            EnvDTE.Thread selectedThread = ResolveThread(debugger, threadId, selectedThreadIndex);
            EnvDTE.StackFrame selectedFrame = ResolveFrame(selectedThread, frameIndex);
            if (selectedFrame == null)
                throw new DebugContextNotFoundException(
                    $"Frame {frameIndex} on thread {threadId} was not found in the current break context.");
            DebugFrameContext requestedContext = CreateFrameContext(
                beforeEvaluation, selectedThread.ID, frameIndex);
            if (hasExplicitContext &&
                (!string.Equals(frameId, requestedContext.FrameId, StringComparison.Ordinal) ||
                 threadId != requestedContext.ThreadId || frameIndex != requestedContext.FrameIndex))
                throw new StaleDebugContextException(
                    $"Frame id '{frameId}' is stale; the current frame id is '{requestedContext.FrameId}'.");

            // Locals/Arguments can evaluate user code. Invalidate before the first
            // collection access and return the post-invalidation context identity.
            MarkUnsafeEvaluationIssued();
            StopEventSnapshot afterInvalidation = _sessionTracker.GetStopEventSnapshot();
            result.Context = CreateFrameContext(afterInvalidation, selectedThread.ID, frameIndex);
            header.MaxDepth = Math.Max(0, Math.Min(maxDepth, LocalsExpansionMaxDepth));
            try
            {
                header.Locals = selectedFrame.Locals;
                header.LocalCount = header.Locals?.Count ?? 0;
            }
            catch (Exception ex)
            {
                RethrowRequestControlFlow(ex);
                result.Partial = true;
                result.Error = AppendDiagnostic(result.Error, "Frame locals are unavailable: " + ex.Message);
            }
            try
            {
                header.Arguments = selectedFrame.Arguments;
                header.ArgumentCount = header.Arguments?.Count ?? 0;
            }
            catch (Exception ex)
            {
                RethrowRequestControlFlow(ex);
                result.Partial = true;
                result.Error = AppendDiagnostic(result.Error, "Frame arguments are unavailable: " + ex.Message);
            }
            header.Available = true;
            return header;
        }

        private void CaptureVariableCollectionSliced(
            Expressions collection, int count, int depth, int maxDepth,
            VariableCaptureBudget budget, List<VariableInfo> destination,
            string rootNamePrefix)
        {
            if (collection == null || count <= 0 || destination == null) return;
            var cursors = new Stack<VariableCollectionCursor>();
            cursors.Push(new VariableCollectionCursor
            {
                Collection = collection, Count = count, NextIndex = 1,
                Depth = depth, Destination = destination, NamePrefix = rootNamePrefix,
            });
            while (cursors.Count > 0 && budget.CanContinue())
            {
                DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                VariableCollectionCursor cursor = cursors.Peek();
                if (cursor.NextIndex > cursor.Count)
                {
                    cursors.Pop();
                    continue;
                }
                int itemIndex = cursor.NextIndex++;
                VariableNodeCapture captured;
                try
                {
                    captured = ExecuteOnMainThread(
                        () => CaptureVariableNode(
                            cursor.Collection, itemIndex, cursor.Depth, maxDepth,
                            budget, cursor.NamePrefix),
                        VariableUiSliceTimeoutMs);
                }
                catch (Exception ex)
                {
                    RethrowRequestControlFlow(ex);
                    budget.ReportCaptureError(
                        $"Variable node {itemIndex} at depth {cursor.Depth} is unavailable: {ex.Message}");
                    continue;
                }
                if (captured?.Info == null) continue;
                cursor.Destination.Add(captured.Info);
                if (captured.Children != null && captured.ChildCount > 0)
                {
                    captured.Info.Children = new List<VariableInfo>();
                    cursors.Push(new VariableCollectionCursor
                    {
                        Collection = captured.Children, Count = captured.ChildCount,
                        NextIndex = 1, Depth = cursor.Depth + 1,
                        Destination = captured.Info.Children,
                    });
                }
            }
        }

        private VariableNodeCapture CaptureVariableNode(
            Expressions collection, int index, int depth, int maxDepth,
            VariableCaptureBudget budget, string namePrefix)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!budget.TryEnterNode()) return null;
            EnvDTE.Expression expression = collection?.Item(index);
            if (expression == null)
            {
                budget.ReportCaptureError($"Variable node {index} was null");
                return null;
            }
            string name = CaptureVariableField(() => expression.Name, "name", budget, out bool nameTruncated);
            string type = CaptureVariableField(() => expression.Type, "type", budget, out bool typeTruncated);
            string value = CaptureVariableField(() => expression.Value, "value", budget, out bool valueTruncated);
            bool isValid = CaptureVariableBoolean(() => expression.IsValidValue, "validity", budget);
            Expressions children = null;
            int childCount = 0;
            if (budget.CanContinue())
            {
                try
                {
                    children = expression.DataMembers;
                    childCount = children?.Count ?? 0;
                    if (childCount > 0 && depth >= maxDepth)
                    {
                        budget.ReportCaptureError(
                            $"Variable children are incomplete at maximum depth {maxDepth}");
                        children = null;
                    }
                }
                catch (Exception ex)
                {
                    RethrowRequestControlFlow(ex);
                    budget.ReportCaptureError("Variable child metadata is unavailable: " + ex.Message);
                }
            }
            if (!string.IsNullOrEmpty(namePrefix)) name = namePrefix + name;
            return new VariableNodeCapture
            {
                Info = new VariableInfo
                {
                    Name = name, Type = type, Value = value,
                    HasChildren = childCount > 0, IsReadOnly = !isValid,
                    Truncated = nameTruncated || typeTruncated || valueTruncated,
                },
                Children = children,
                ChildCount = childCount,
            };
        }

        private string CaptureVariableField(
            Func<string> getter, string field, VariableCaptureBudget budget,
            out bool truncated)
        {
            try { return budget.CaptureString(getter(), field, out truncated); }
            catch (Exception ex)
            {
                RethrowRequestControlFlow(ex);
                budget.ReportCaptureError($"Variable {field} is unavailable: {ex.Message}");
                truncated = false;
                return string.Empty;
            }
        }

        private bool CaptureVariableBoolean(
            Func<bool> getter, string field, VariableCaptureBudget budget)
        {
            try { return getter(); }
            catch (Exception ex)
            {
                RethrowRequestControlFlow(ex);
                budget.ReportCaptureError($"Variable {field} is unavailable: {ex.Message}");
                return false;
            }
        }

        private static string AppendDiagnostic(string existing, string diagnostic)
        {
            if (string.IsNullOrWhiteSpace(diagnostic)) return existing;
            return string.IsNullOrWhiteSpace(existing) ? diagnostic : existing + "; " + diagnostic;
        }

        private sealed class LocalsCaptureHeader
        {
            internal bool Available { get; set; }
            internal LocalsResult Result { get; set; }
            internal Expressions Locals { get; set; }
            internal int LocalCount { get; set; }
            internal Expressions Arguments { get; set; }
            internal int ArgumentCount { get; set; }
            internal int MaxDepth { get; set; }
        }

        private sealed class EvaluationRootCapture
        {
            internal EvaluateResult Result { get; set; }
            internal Expressions Children { get; set; }
            internal int ChildCount { get; set; }
            internal bool NeedsHexValue { get; set; }
        }

        private sealed class VariableCollectionCursor
        {
            internal Expressions Collection { get; set; }
            internal int Count { get; set; }
            internal int NextIndex { get; set; }
            internal int Depth { get; set; }
            internal List<VariableInfo> Destination { get; set; }
            internal string NamePrefix { get; set; }
        }

        private sealed class VariableNodeCapture
        {
            internal VariableInfo Info { get; set; }
            internal Expressions Children { get; set; }
            internal int ChildCount { get; set; }
        }
    }
#pragma warning restore VSTHRD010
}
