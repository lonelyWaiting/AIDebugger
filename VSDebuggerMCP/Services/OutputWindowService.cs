using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using VSDebuggerMCP.Protocol;

namespace VSDebuggerMCP.Services
{
    internal sealed class OutputWindowService
    {
        internal const int MaxSnapshotCharacters = 4 * 1024 * 1024;
        internal const int MaxUiSliceCharacters = 64 * 1024;
        internal const int MaxAvailablePanes = 128;
        internal const int MaxPaneNameCharacters = 256;
        private const int SearchBudgetMs = 2000;
        private readonly DTE2 _dte;
        private readonly Action<string> _log;

        internal OutputWindowService(DTE2 dte, Action<string> log)
        {
            _dte = dte ?? throw new ArgumentNullException(nameof(dte));
            _log = log ?? (_ => { });
        }

        internal OutputPaneSnapshot CaptureTail(string requestedPane, int tailLines)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var snapshot = new OutputPaneSnapshot { RequestedPane = requestedPane, SnapshotStable = true, SnapshotStatus = "stable" };
            try
            {
                var outputWindow = _dte.ToolWindows.OutputWindow;
                if (outputWindow == null)
                {
                    MarkUnavailable(snapshot, "Output window not available");
                    return snapshot;
                }

                foreach (OutputWindowPane pane in outputWindow.OutputWindowPanes)
                {
                    try { snapshot.AvailablePanes.Add(pane.Name); }
                    catch { }
                }

                OutputWindowPane target = FindPane(outputWindow, requestedPane);
                if (target == null)
                {
                    MarkUnavailable(snapshot, $"Output pane '{requestedPane}' not found");
                    return snapshot;
                }

                snapshot.Pane = target.Name;
                var document = target.TextDocument;
                if (document == null)
                {
                    MarkUnavailable(snapshot, "Could not access output pane text");
                    return snapshot;
                }

                var end = document.EndPoint.CreateEditPoint();
                int totalCharacters = Math.Max(0, end.AbsoluteCharOffset - 1);
                snapshot.TotalCharacters = totalCharacters;
                var start = document.EndPoint.CreateEditPoint();
                if (tailLines > 0) start.LineUp(Math.Max(1, tailLines));
                int desiredCharacters = Math.Max(0, end.AbsoluteCharOffset - start.AbsoluteCharOffset);
                if (desiredCharacters > MaxUiSliceCharacters)
                {
                    start = document.EndPoint.CreateEditPoint();
                    start.CharLeft(MaxUiSliceCharacters);
                }
                int capturedCharacters = Math.Max(0, end.AbsoluteCharOffset - start.AbsoluteCharOffset);
                snapshot.Content = start.GetText(end) ?? string.Empty;
                snapshot.CaptureStartCharacter = Math.Max(0, start.AbsoluteCharOffset - 1);
                snapshot.CaptureEndCharacter = totalCharacters;
                snapshot.FirstLineNumber = Math.Max(1, start.Line);
                snapshot.TotalLineCount = Math.Max(0, end.Line);
                snapshot.Truncated = start.AbsoluteCharOffset > 1;
            }
            catch (Exception ex)
            {
                _log($"Output pane snapshot failed: {ex.GetType().Name}");
                snapshot.Error = "Failed to capture the Visual Studio output pane.";
                snapshot.SnapshotStable = false;
                snapshot.SnapshotStatus = "error";
            }
            return snapshot;
        }

        internal OutputPaneSnapshot CaptureMetadata(string requestedPane)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var snapshot = new OutputPaneSnapshot { RequestedPane = requestedPane, SnapshotStable = true, SnapshotStatus = "stable" };
            try
            {
                var outputWindow = _dte.ToolWindows.OutputWindow;
                if (outputWindow == null) { MarkUnavailable(snapshot, "Output window not available"); return snapshot; }
                foreach (OutputWindowPane pane in outputWindow.OutputWindowPanes)
                {
                    try { snapshot.AvailablePanes.Add(pane.Name); } catch { }
                }
                OutputWindowPane target = FindPane(outputWindow, requestedPane);
                if (target == null) { MarkUnavailable(snapshot, $"Output pane '{requestedPane}' not found"); return snapshot; }
                snapshot.Pane = target.Name;
                var document = target.TextDocument;
                if (document == null) { MarkUnavailable(snapshot, "Could not access output pane text"); return snapshot; }
                var end = document.EndPoint.CreateEditPoint();
                snapshot.TotalCharacters = Math.Max(0, end.AbsoluteCharOffset - 1);
                snapshot.TotalLineCount = Math.Max(0, end.Line);
            }
            catch (Exception ex)
            {
                _log($"Output pane metadata failed: {ex.GetType().Name}");
                snapshot.Error = "Failed to inspect the Visual Studio output pane.";
                snapshot.SnapshotStable = false;
                snapshot.SnapshotStatus = "error";
            }
            return snapshot;
        }

        private static void MarkUnavailable(OutputPaneSnapshot snapshot, string error)
        {
            snapshot.Error = error;
            snapshot.Partial = true;
            snapshot.SnapshotStable = false;
            snapshot.SnapshotStatus = "unavailable";
        }

        internal OutputPaneSlice CaptureSlice(string requestedPane, int startCharacter, int maxCharacters)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (startCharacter < 0 || maxCharacters < 1 || maxCharacters > MaxUiSliceCharacters)
                throw new ArgumentOutOfRangeException(nameof(maxCharacters));
            var outputWindow = _dte.ToolWindows.OutputWindow;
            OutputWindowPane target = FindPane(outputWindow, requestedPane);
            var document = target?.TextDocument;
            if (document == null) throw new InvalidOperationException("Output pane is unavailable");
            int totalCharacters = Math.Max(0, document.EndPoint.AbsoluteCharOffset - 1);
            int actualStart = Math.Min(startCharacter, totalCharacters);
            int count = Math.Min(maxCharacters, totalCharacters - actualStart);
            var start = document.StartPoint.CreateEditPoint();
            if (actualStart > 0) start.CharRight(actualStart);
            var end = start.CreateEditPoint();
            if (count > 0) end.CharRight(count);
            return new OutputPaneSlice
            {
                StartCharacter = actualStart,
                FirstLineNumber = Math.Max(1, start.Line),
                Content = count > 0 ? (start.GetText(end) ?? string.Empty) : string.Empty,
            };
        }

        internal OutputPaneSnapshot CombineSlices(
            OutputPaneSnapshot metadata,
            IList<OutputPaneSlice> slices,
            int captureStart)
        {
            var content = new StringBuilder();
            foreach (OutputPaneSlice slice in slices) content.Append(slice.Content);
            metadata.Content = content.ToString();
            metadata.FirstLineNumber = slices.Count > 0 ? slices[0].FirstLineNumber : 1;
            metadata.Truncated = captureStart > 0;
            metadata.CaptureStartCharacter = captureStart;
            metadata.CaptureEndCharacter = captureStart + metadata.Content.Length;
            return metadata;
        }

        internal OutputWindowResult BuildResult(
            OutputPaneSnapshot snapshot,
            int tailLines,
            int maxSerializedContentBytes)
        {
            var result = new OutputWindowResult
            {
                Pane = snapshot.Pane ?? snapshot.RequestedPane,
                Capability = "multi_pane_output",
                RequestedLines = tailLines,
                AvailablePanes = BoundAvailablePanes(snapshot.AvailablePanes),
                Error = snapshot.Error,
                Truncated = snapshot.Truncated,
                LineCount = snapshot.TotalLineCount,
                SnapshotStable = snapshot.SnapshotStable,
                SnapshotStatus = snapshot.SnapshotStatus,
                Partial = snapshot.Partial,
                MaxSerializedContentBytes = maxSerializedContentBytes,
                CaptureEndCharacter = snapshot.CaptureEndCharacter,
            };
            if (snapshot.Error != null) return result;

            string content = snapshot.Content ?? string.Empty;
            int omitted = OutputContentBudget.FindTailStart(content, tailLines);
            if (omitted > 0)
            {
                content = content.Substring(omitted);
                result.Truncated = true;
            }

            OutputContentSlice bounded = OutputContentBudget.FitSuffix(
                content,
                maxSerializedContentBytes);
            omitted += bounded.OmittedLeadingCharacters;
            result.Content = bounded.Content;
            result.ContentUtf8Bytes = bounded.ContentUtf8Bytes;
            result.SerializedContentBytes = bounded.SerializedContentBytes;
            result.OmittedLeadingCharacters = omitted;
            result.FirstCharacterOffset = snapshot.CaptureStartCharacter + omitted;
            result.HasMore = result.FirstCharacterOffset > 0;
            result.NextEndCharacter = result.HasMore ? (int?)result.FirstCharacterOffset : null;
            result.Truncated = result.Truncated || bounded.OmittedLeadingCharacters > 0;
            result.Partial = result.Partial || result.Truncated;
            return result;
        }

        internal SearchOutputResult Search(
            OutputPaneSnapshot snapshot,
            string pattern,
            bool isRegex,
            int contextLines,
            int maxMatches,
            int startLine)
        {
            var result = new SearchOutputResult
            {
                Pane = snapshot.Pane ?? snapshot.RequestedPane,
                Capability = "multi_pane_output",
                Pattern = pattern,
                IsRegex = isRegex,
                Error = snapshot.Error,
                Truncated = snapshot.Truncated,
                SnapshotStable = snapshot.SnapshotStable,
                SnapshotStatus = snapshot.SnapshotStatus,
                Partial = snapshot.Partial,
                StartLine = startLine,
                MaxResultBytes = OutputSearchBudget.MaxSerializedResultBytes,
                MaxResultNodes = OutputSearchBudget.MaxResultNodes,
                CaptureStartCharacter = snapshot.CaptureStartCharacter,
                CaptureEndCharacter = snapshot.CaptureEndCharacter,
                FirstLineNumber = snapshot.FirstLineNumber,
            };
            if (snapshot.Error != null) return result;
            if (string.IsNullOrEmpty(pattern))
            {
                result.Error = "Pattern cannot be empty";
                return result;
            }

            Regex regex = null;
            if (isRegex)
            {
                try { regex = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)); }
                catch (ArgumentException ex)
                {
                    result.Error = "Invalid regex pattern: " + ex.Message;
                    return result;
                }
            }

            string[] lines = SplitLines(snapshot.Content ?? string.Empty);
            int firstLine = Math.Min(Math.Max(0, startLine), lines.Length);
            var outputBudget = new OutputSearchBudget();
            var boundedLineCache = new Dictionary<int, string>();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            for (int index = firstLine; index < lines.Length; index++)
            {
                if ((index & 0x1F) == 0)
                {
                    global::VSDebuggerMCP.DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                    if (stopwatch.ElapsedMilliseconds >= SearchBudgetMs)
                    {
                        result.Error = $"Output search exceeded its {SearchBudgetMs}ms total search budget";
                        return FinalizeSearchResult(result, outputBudget, firstLine, index, true);
                    }
                }
                bool matched;
                try
                {
                    matched = isRegex
                        ? regex.IsMatch(lines[index])
                        : lines[index].IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
                }
                catch (RegexMatchTimeoutException)
                {
                    result.Error = "Regex matching exceeded the 100ms per-line safety limit.";
                    return FinalizeSearchResult(result, outputBudget, firstLine, index, true);
                }

                if (!matched) continue;
                if (result.Matches.Count >= maxMatches)
                    return FinalizeSearchResult(result, outputBudget, firstLine, index, true);

                int actualLine = snapshot.FirstLineNumber + index;
                string boundedLine = GetBoundedSearchLine(lines, boundedLineCache, index);
                var match = new OutputMatch { LineNumber = actualLine, Line = boundedLine };
                int contextStart = Math.Max(0, index - contextLines);
                int contextEnd = Math.Min(lines.Length - 1, index + contextLines);
                for (int contextIndex = contextStart; contextIndex <= contextEnd; contextIndex++)
                {
                    if (contextIndex == index) continue;
                    if (match.Context.Count >= OutputSearchBudget.MaxContextNodesPerMatch)
                    {
                        match.ContextTruncated = true;
                        result.Partial = true;
                        result.Truncated = true;
                        break;
                    }
                    match.Context.Add($"L{snapshot.FirstLineNumber + contextIndex}: " +
                        GetBoundedSearchLine(lines, boundedLineCache, contextIndex));
                }
                if (!outputBudget.TryConsumeMatch(match.Line, match.Context))
                    return FinalizeSearchResult(result, outputBudget, firstLine, index, true);
                result.Matches.Add(match);
                result.TotalMatches++;
            }
            return FinalizeSearchResult(result, outputBudget, firstLine, lines.Length, false);
        }

        internal SearchOutputResult BuildStaleSearchCursorResult(
            OutputPaneSnapshot snapshot,
            string pattern,
            bool isRegex,
            int startLine,
            string error)
        {
            return new SearchOutputResult
            {
                Pane = snapshot?.Pane ?? snapshot?.RequestedPane,
                Capability = "multi_pane_output",
                Pattern = pattern,
                IsRegex = isRegex,
                Error = error,
                ErrorCode = "STALE_OUTPUT_CURSOR",
                Truncated = true,
                SnapshotStable = false,
                SnapshotStatus = "stale_cursor",
                Partial = true,
                StartLine = startLine,
                CaptureStartCharacter = snapshot?.CaptureStartCharacter ?? 0,
                CaptureEndCharacter = snapshot?.CaptureEndCharacter ?? 0,
                FirstLineNumber = snapshot?.FirstLineNumber ?? 1,
                MaxResultBytes = OutputSearchBudget.MaxSerializedResultBytes,
                MaxResultNodes = OutputSearchBudget.MaxResultNodes,
            };
        }

        private static string GetBoundedSearchLine(
            string[] lines,
            IDictionary<int, string> cache,
            int index)
        {
            if (!cache.TryGetValue(index, out string bounded))
            {
                bounded = OutputSearchBudget.BoundLine(lines[index]);
                cache[index] = bounded;
            }
            return bounded;
        }

        private static SearchOutputResult FinalizeSearchResult(
            SearchOutputResult result,
            OutputSearchBudget budget,
            int startLine,
            int nextLine,
            bool hasMore)
        {
            result.ScannedLines = Math.Max(0, nextLine - startLine);
            result.HasMore = hasMore;
            result.NextLine = hasMore ? (int?)nextLine : null;
            result.ResultBytes = budget.SerializedBytes;
            result.ResultNodes = budget.Nodes;
            result.Truncated = result.Truncated || hasMore;
            result.Partial = result.Partial || hasMore;
            return result;
        }

        private static OutputWindowPane FindPane(OutputWindow outputWindow, string requestedPane)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { return outputWindow.OutputWindowPanes.Item(requestedPane); }
            catch
            {
                foreach (OutputWindowPane pane in outputWindow.OutputWindowPanes)
                {
                    try
                    {
                        if (pane.Name.Equals(requestedPane, StringComparison.OrdinalIgnoreCase)) return pane;
                    }
                    catch { }
                }
                return null;
            }
        }

        private static string[] SplitLines(string content)
        {
            return content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        }

        private static List<string> BoundAvailablePanes(IList<string> panes)
        {
            var result = new List<string>();
            if (panes == null) return result;
            for (int index = 0; index < panes.Count && result.Count < MaxAvailablePanes; index++)
            {
                string pane = panes[index] ?? string.Empty;
                result.Add(pane.Length <= MaxPaneNameCharacters
                    ? pane
                    : pane.Substring(0, MaxPaneNameCharacters));
            }
            return result;
        }
    }

    internal sealed class OutputPaneSnapshot
    {
        internal string RequestedPane { get; set; }
        internal string Pane { get; set; }
        internal string Content { get; set; }
        internal int FirstLineNumber { get; set; } = 1;
        internal int TotalLineCount { get; set; }
        internal bool Truncated { get; set; }
        internal string Error { get; set; }
        internal int TotalCharacters { get; set; }
        internal int CaptureStartCharacter { get; set; }
        internal int CaptureEndCharacter { get; set; }
        internal bool SnapshotStable { get; set; } = true;
        internal string SnapshotStatus { get; set; } = "stable";
        internal bool Partial { get; set; }
        internal List<string> AvailablePanes { get; } = new List<string>();
    }

    internal sealed class OutputPaneSlice
    {
        internal int StartCharacter { get; set; }
        internal int FirstLineNumber { get; set; }
        internal string Content { get; set; }
    }
}
