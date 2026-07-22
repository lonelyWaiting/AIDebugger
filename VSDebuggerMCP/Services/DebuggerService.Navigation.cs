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
        #region Stack Frame Navigation

        /// <summary>
        /// Switch to a specific stack frame.
        /// </summary>
        public DebugControlResult SwitchStackFrame(int frameIndex)
        {
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new DebugControlResult { Action = "SwitchStackFrame" };

                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    result.Success = false;
                    result.Message = "Not in break mode";
                    return result;
                }

                try
                {
                    var thread = debugger.CurrentThread;
                    if (thread != null && frameIndex < thread.StackFrames.Count)
                    {
                        // Stack frames are 1-indexed in DTE
                        var frame = thread.StackFrames.Item(frameIndex + 1) as EnvDTE.StackFrame;
                        if (frame != null)
                        {
                            result.CommandIssued = true;
                            DebuggerRequestContext.MarkSideEffectIssued();
                            debugger.CurrentStackFrame = frame;
                            _sessionTracker.InvalidateContext("switch_stack_frame");
                            RebaseRequestToCurrentDebuggerIdentity();
                            result.Success = true;
                            result.Outcome = "stack_frame_selected";
                            result.Message = $"Switched to frame {frameIndex}: {frame.FunctionName}";
                            result.NewLocation = CreateSourceLocation(frame);
                        }
                        else
                        {
                            result.Success = false;
                            result.Message = "Invalid frame";
                        }
                    }
                    else
                    {
                        result.Success = false;
                        result.Message = $"Frame index {frameIndex} out of range";
                    }
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.ErrorCode = "OUTCOME_UNKNOWN";
                    result.Outcome = result.CommandIssued ? "switch_stack_frame_outcome_unknown" : "switch_stack_frame_not_started";
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        /// <summary>
        /// Switch to a specific thread.
        /// </summary>
        public DebugControlResult SwitchThread(int threadId, int threadLookupBudgetMs = -1)
        {
            int threadIndex;
            try
            {
                threadIndex = FindThreadIndexByIdSliced(threadId, threadLookupBudgetMs);
            }
            catch (DebugContextNotFoundException ex)
            {
                return new DebugControlResult
                {
                    Action = "SwitchThread",
                    Success = false,
                    ErrorCode = "DEBUG_CONTEXT_NOT_FOUND",
                    Message = ex.Message,
                };
            }

            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new DebugControlResult { Action = "SwitchThread" };

                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    result.Success = false;
                    result.Message = "Not in break mode";
                    return result;
                }

                try
                {
                    EnvDTE.Thread thread = debugger.CurrentProgram?.Threads?.Item(threadIndex);
                    if (thread == null || thread.ID != threadId)
                    {
                        throw new StaleDebugContextException(
                            $"Thread {threadId} changed before it could be selected.");
                    }

                    result.CommandIssued = true;
                    DebuggerRequestContext.MarkSideEffectIssued();
                    debugger.CurrentThread = thread;
                    _sessionTracker.InvalidateContext("switch_thread");
                    RebaseRequestToCurrentDebuggerIdentity();
                    result.Success = true;
                    result.Outcome = "thread_selected";
                    result.Message = $"Switched to thread {threadId}";

                    if (thread.StackFrames.Count > 0)
                    {
                        var topFrame = thread.StackFrames.Item(1) as EnvDTE.StackFrame;
                        if (topFrame != null)
                        {
                            result.NewLocation = CreateSourceLocation(topFrame);
                        }
                    }
                }
                catch (Exception ex)
                {
                    RethrowRequestControlFlow(ex);
                    result.Success = false;
                    result.ErrorCode = "OUTCOME_UNKNOWN";
                    result.Outcome = result.CommandIssued ? "switch_thread_outcome_unknown" : "switch_thread_not_started";
                    result.Message = ex.Message;
                }

                return result;
            });
        }

        #endregion
    }
}
