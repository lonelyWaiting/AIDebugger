using System;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using VSDebuggerMCP.Protocol;

namespace VSDebuggerMCP.Services
{
    internal sealed class BuildErrorService
    {
        private readonly DTE2 _dte;
        private readonly Action<string> _log;

        internal BuildErrorService(DTE2 dte, Action<string> log)
        {
            _dte = dte ?? throw new ArgumentNullException(nameof(dte));
            _log = log ?? (_ => { });
        }

        internal int GetCount()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return _dte.ToolWindows.ErrorList.ErrorItems.Count;
        }

        internal BuildErrorItem GetItem(int index, bool errorsOnly, bool warningsOnly)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var item = _dte.ToolWindows.ErrorList.ErrorItems.Item(index);
            bool isError = item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelHigh;
            bool isWarning = item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelMedium ||
                             item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelLow;
            if ((errorsOnly && !isError) || (warningsOnly && !isWarning))
            {
                return null;
            }

            string description = BuildErrorCaptureBudget.BoundDescription(
                item.Description,
                out bool descriptionTruncated);
            string filePath = BuildErrorCaptureBudget.BoundPath(
                item.FileName,
                out bool filePathTruncated);
            string project = BuildErrorCaptureBudget.BoundProject(
                item.Project,
                out bool projectTruncated);
            return new BuildErrorItem
            {
                Severity = isError ? "Error" : "Warning",
                Code = BuildErrorCodeParser.Extract(description),
                Description = description,
                FilePath = filePath,
                Line = item.Line,
                Column = item.Column,
                Project = project,
                Truncated = descriptionTruncated || filePathTruncated || projectTruncated,
            };
        }

    }
}
