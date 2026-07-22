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
using System.Threading.Tasks;
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
        #region Native Debugging

        [SuppressMessage("Usage", "VSTHRD010", Justification = "Composes a self-marshalling DTE address capture with off-UI module enrichment.")]
        public NativeAddressInfo ResolveAddress(string addressExpression)
        {
            return ResolveAddressWithModules(addressExpression, GetModules());
        }

        [SuppressMessage("Usage", "VSTHRD010", Justification = "Marshals only the raw expression capture and performs PE enrichment off the UI thread.")]
        private NativeAddressInfo ResolveAddressWithModules(string addressExpression, ModulesResult modules)
        {
            NativeAddressInfo raw = ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return ResolveAddressInternal(Debugger, addressExpression, modules);
            }, Math.Max(ExpressionEvaluationTimeoutMs, 250));
            if (raw == null || raw.AddressValue == 0 || !string.IsNullOrEmpty(raw.Error))
            {
                return raw;
            }
            return BuildNativeAddressInfo(null, raw.Expression, raw.AddressValue, modules);
        }

        [SuppressMessage("Usage", "VSTHRD010", Justification = "GetModules marshals its EnvDTE capture to the UI thread and completes PE parsing off-thread.")]
        public ModuleLayoutResult GetModuleLayout(string moduleIdentifier, int maxImportFunctions = 256, int maxExports = 512)
        {
            string trimmed = moduleIdentifier?.Trim().Trim('"');
            ModuleInfo module = null;
            if (!string.IsNullOrEmpty(trimmed) && System.IO.File.Exists(trimmed))
            {
                module = new ModuleInfo
                {
                    Name = SafeGetFileName(trimmed),
                    Path = System.IO.Path.GetFullPath(trimmed),
                };
            }
            else
            {
                module = FindModuleByIdentifier(GetModules(), trimmed);
            }
            if (module == null)
            {
                return new ModuleLayoutResult
                {
                    Status = "unavailable",
                    Error = $"Module not found: {moduleIdentifier}"
                };
            }

            return BuildModuleLayoutFromFile(
                module,
                Math.Max(0, Math.Min(maxImportFunctions, 4096)),
                Math.Max(0, Math.Min(maxExports, 4096)));
        }

        public BreakpointOperationResult SetAddressBreakpoint(
            string addressExpression,
            string condition = null,
            int hitCount = 0,
            string hitCountType = null,
            string conditionType = null)
        {
            string normalizedAddressExpression = addressExpression?.Trim();
            if (!TryParseAddressLiteral(normalizedAddressExpression, out ulong literalAddress) &&
                !TryParseModuleRvaExpression(normalizedAddressExpression, out _, out _))
            {
                return new BreakpointOperationResult
                {
                    Operation = "SetAddress",
                    Success = false,
                    ErrorCode = "INVALID_ARGUMENT",
                    Outcome = "set_not_started",
                    Message = "Address breakpoints accept only an absolute address or module+RVA; resolve other expressions explicitly first"
                };
            }

            if (literalAddress == 0 &&
                !TryParseModuleRvaExpression(normalizedAddressExpression, out _, out _))
            {
                return new BreakpointOperationResult
                {
                    Operation = "SetAddress",
                    Success = false,
                    ErrorCode = "INVALID_ARGUMENT",
                    Outcome = "set_not_started",
                    Message = "Address breakpoint must be non-zero"
                };
            }

            NativeAddressInfo resolvedAddress = ResolveAddress(normalizedAddressExpression);
            return ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var debugger = Debugger;
                var result = new BreakpointOperationResult { Operation = "SetAddress" };
                string normalizedCondition = NormalizeBreakpointCondition(condition);
                var breakpointConditionType = ParseBreakpointConditionType(conditionType);
                var breakpointHitCountType = ParseHitCountType(hitCountType, hitCount);
                int normalizedHitCount = Math.Max(0, hitCount);

                if (debugger == null || debugger.CurrentMode == dbgDebugMode.dbgDesignMode)
                {
                    result.Success = false;
                    result.ErrorCode = "NOT_DEBUGGING";
                    result.Message = "No active debug session";
                    return result;
                }

                result.Address = resolvedAddress;
                if (resolvedAddress.AddressValue == 0 || !string.IsNullOrEmpty(resolvedAddress.Error))
                {
                    result.Success = false;
                    result.Message = resolvedAddress.Error ?? $"Could not resolve address: {addressExpression}";
                    return result;
                }

                try
                {
                    string addressText = resolvedAddress.VirtualAddress;
                    result.CommandIssued = true;
                    DebuggerRequestContext.MarkSideEffectIssued();
                    EnvDTE.Breakpoints breakpoints = debugger.Breakpoints.Add(
                        string.Empty,
                        string.Empty,
                        0,
                        0,
                        normalizedCondition ?? string.Empty,
                        breakpointConditionType,
                        "C++",
                        string.Empty,
                        1,
                        addressText,
                        normalizedHitCount,
                        breakpointHitCountType);

                    if (breakpoints == null || breakpoints.Count == 0)
                    {
                        result.Success = false;
                        result.ErrorCode = "OUTCOME_UNKNOWN";
                        result.Message = $"Failed to set address breakpoint at {addressText}";
                        return result;
                    }

                    var breakpoint = breakpoints.Item(1);
                    result.AffectedCount = 1;
                    TrySetAddressBreakpointTag(breakpoint, addressText);
                    IsBreakpointBound(debugger, breakpoint);
                    var breakpointInfo = BuildBreakpointInfo(breakpoint, debugger, null, 0, normalizedCondition);
                    if (!breakpointInfo.ConditionApplied)
                    {
                        SafeDeleteBreakpoint(breakpoint);
                        result.Success = false;
                        result.ErrorCode = "OUTCOME_UNKNOWN";
                        result.Outcome = "created_then_condition_cleanup_attempted";
                        result.Message = $"Address breakpoint condition was not applied at {addressText}";
                        return result;
                    }

                    result.Success = true;
                    result.Outcome = "created";
                    result.BreakpointInfo = breakpointInfo;
                    result.Message = $"Address breakpoint set at {addressText}";
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

        [SuppressMessage("Usage", "VSTHRD010", Justification = "Composite components self-marshal bounded EnvDTE slices; aggregation runs off the UI thread.")]
        public NativeContextResult CaptureNativeContext(int disassemblyCount = 32, int stackBytes = 256, bool includeModules = true)
        {
            var budget = new CompositeCaptureBudget();
            var result = new NativeContextResult
            {
                SnapshotId = Guid.NewGuid().ToString("N"),
                CapturedAt = DateTime.UtcNow.ToString("o"),
                MaxResultBytes = CompositeCaptureBudget.MaxResultBytes,
            };
            try
            {
                result.Status = RetainNativeComponent(result, budget, "status", GetDebugStatus());
            }
            catch (Exception ex)
            {
                RethrowRequestControlFlow(ex);
                AppendNativeContextError(result, "status", ex.Message);
            }
            if (result.Status?.IsPaused != true)
            {
                result.Partial = true;
                AppendNativeContextError(result, "status", "Not in break mode");
                result.ResultBytes = budget.ResultBytes;
                return result;
            }

            TargetMachineArchitecture architecture = DetectTargetArchitecture();
            result.Architecture = TargetArchitecturePolicy.ToProtocolName(architecture);
            bool? is64Bit = TargetArchitecturePolicy.Is64Bit(architecture);
            if (!TargetArchitecturePolicy.IsIcedSupported(architecture))
            {
                result.Partial = true;
                bool unknown = architecture == TargetMachineArchitecture.Unknown;
                result.ArchitectureStatus = unknown ? "unknown" : "unsupported";
                result.ErrorCode = unknown
                    ? "TARGET_ARCHITECTURE_UNKNOWN"
                    : "ARCHITECTURE_NOT_SUPPORTED";
                AppendNativeContextError(
                    result,
                    "architecture",
                    unknown
                        ? "Target architecture could not be proven from a trusted process or dump source; native register selection was not guessed."
                        : $"Native context capture currently supports x86/x64, not {result.Architecture}.");
                result.ResultBytes = budget.ResultBytes;
                return result;
            }
            result.ArchitectureStatus = "supported";
            string instructionPointer = is64Bit.Value ? "@rip" : "@eip";
            string stackPointer = is64Bit.Value ? "@rsp" : "@esp";
            ModulesResult addressModules = null;
            if (includeModules)
            {
                try { addressModules = GetModules(maxNodes: 256); }
                catch (Exception ex) { RethrowRequestControlFlow(ex); AppendNativeContextError(result, "modules", ex.Message); }
            }

            if (BeginNativeComponent(result, budget, "currentAddress")) try
            {
                result.CurrentAddress = RetainNativeComponent(
                    result, budget, "currentAddress", ResolveAddressWithModules(instructionPointer, addressModules));
                if (!string.IsNullOrEmpty(result.CurrentAddress?.Error))
                    AppendNativeContextError(result, "currentAddress", result.CurrentAddress.Error);
            }
            catch (Exception ex) { RethrowRequestControlFlow(ex); AppendNativeContextError(result, "currentAddress", ex.Message); }

            if (BeginNativeComponent(result, budget, "stackPointer")) try
            {
                result.StackPointer = RetainNativeComponent(
                    result, budget, "stackPointer", ResolveAddressWithModules(stackPointer, addressModules));
                if (!string.IsNullOrEmpty(result.StackPointer?.Error))
                    AppendNativeContextError(result, "stackPointer", result.StackPointer.Error);
            }
            catch (Exception ex) { RethrowRequestControlFlow(ex); AppendNativeContextError(result, "stackPointer", ex.Message); }

            if (BeginNativeComponent(result, budget, "registers")) try
            {
                result.Registers = RetainNativeComponent(result, budget, "registers", GetRegisters());
                if (result.Registers != null && SnapshotCompletenessPolicy.IsIncomplete(result.Registers))
                {
                    AppendNativeContextError(
                        result,
                        "registers",
                        result.Registers?.Error ??
                            $"status={result.Registers?.Status ?? "unavailable"}, " +
                            $"snapshotStatus={result.Registers?.SnapshotStatus ?? "unavailable"}");
                }
            }
            catch (Exception ex) { RethrowRequestControlFlow(ex); AppendNativeContextError(result, "registers", ex.Message); }

            if (BeginNativeComponent(result, budget, "callstack")) try
            {
                result.Callstack = RetainNativeComponent(result, budget, "callstack", GetCallStack());
                if (result.Callstack != null && SnapshotCompletenessPolicy.IsIncomplete(result.Callstack))
                {
                    AppendNativeContextError(
                        result,
                        "callstack",
                        result.Callstack.Error ?? $"snapshotStatus={result.Callstack.SnapshotStatus ?? "unavailable"}");
                }
            }
            catch (Exception ex) { RethrowRequestControlFlow(ex); AppendNativeContextError(result, "callstack", ex.Message); }

            if (BeginNativeComponent(result, budget, "disassembly")) try
            {
                string address = result.CurrentAddress?.VirtualAddress;
                if (!string.IsNullOrEmpty(address))
                {
                    result.Disassembly = RetainNativeComponent(
                        result,
                        budget,
                        "disassembly",
                        GetDisassemblyAt(address, Math.Max(1, Math.Min(disassemblyCount, 256))));
                    if (result.Disassembly != null && SnapshotCompletenessPolicy.IsIncomplete(result.Disassembly))
                    {
                        AppendNativeContextError(
                            result,
                            "disassembly",
                            result.Disassembly?.Error ?? "disassembly capture was partial or truncated");
                    }
                }
            }
            catch (Exception ex) { RethrowRequestControlFlow(ex); AppendNativeContextError(result, "disassembly", ex.Message); }

            if (BeginNativeComponent(result, budget, "stackMemory")) try
            {
                string address = result.StackPointer?.VirtualAddress ?? stackPointer;
                result.StackMemory = RetainNativeComponent(
                    result,
                    budget,
                    "stackMemory",
                    ReadMemory(address, Math.Max(16, Math.Min(stackBytes, 4096))));
                if (!string.IsNullOrEmpty(result.StackMemory?.Error))
                    AppendNativeContextError(result, "stackMemory", result.StackMemory.Error);
                else if (result.StackMemory?.Partial == true)
                    AppendNativeContextError(result, "stackMemory", "memory read was partial");
            }
            catch (Exception ex) { RethrowRequestControlFlow(ex); AppendNativeContextError(result, "stackMemory", ex.Message); }

            if (includeModules && addressModules != null && BeginNativeComponent(result, budget, "modules"))
            {
                result.Modules = RetainNativeComponent(result, budget, "modules", addressModules);
                if (result.Modules != null && SnapshotCompletenessPolicy.IsIncomplete(result.Modules))
                {
                    AppendNativeContextError(
                        result,
                        "modules",
                        result.Modules.Error ?? $"snapshotStatus={result.Modules.SnapshotStatus ?? "unavailable"}");
                }
            }

            result.Partial = result.Partial || result.CurrentAddress == null ||
                result.Registers == null || result.Disassembly == null || result.StackMemory == null;
            result.ResultBytes = budget.ResultBytes;
            return result;
        }

        private static bool BeginNativeComponent(
            NativeContextResult result,
            CompositeCaptureBudget budget,
            string component)
        {
            if (budget.CanAttemptComponent) return true;
            OmitNativeComponent(result, component, "composite result byte budget was exhausted");
            return false;
        }

        private static T RetainNativeComponent<T>(
            NativeContextResult result,
            CompositeCaptureBudget budget,
            string component,
            T value) where T : class
        {
            if (budget.TryConsumeComponent(component, value)) return value;
            OmitNativeComponent(result, component, "component exceeded the remaining composite result byte budget");
            return null;
        }

        private static void OmitNativeComponent(
            NativeContextResult result,
            string component,
            string reason)
        {
            if (!result.OmittedComponents.Contains(component, StringComparer.OrdinalIgnoreCase))
                result.OmittedComponents.Add(component);
            AppendNativeContextError(result, component, reason);
        }

        private static void AppendNativeContextError(
            NativeContextResult result,
            string component,
            string message)
        {
            result.Partial = true;
            string detail = BoundNativeDiagnostic(component + ": " + message);
            result.Error = BoundNativeDiagnostic(
                string.IsNullOrEmpty(result.Error) ? detail : result.Error + "; " + detail,
                64 * 1024);
        }

        [SuppressMessage("Usage", "VSTHRD010", Justification = "Composes self-marshalling debugger operations with an off-UI event wait.")]
        public NativeContinueResult ContinueAndCaptureNative(
            int timeoutMs = 120000,
            int pollIntervalMs = 200,
            int disassemblyCount = 32,
            int stackBytes = 256,
              bool includeModules = true,
              Func<Func<object>, object> phaseExecutor = null)
        {
            int normalizedTimeoutMs = Math.Max(1000, Math.Min(timeoutMs, 30 * 60 * 1000));
            int normalizedPollIntervalMs = Math.Max(50, Math.Min(pollIntervalMs, 1000));
            var initialEvent = _sessionTracker.GetStopEventSnapshot();
              var initialStatus = ExecuteLongWaitPhase(GetDebugStatus, phaseExecutor);
            var result = new NativeContinueResult
            {
                WaitTimeoutMs = normalizedTimeoutMs,
                PollIntervalMs = normalizedPollIntervalMs,
                Status = initialStatus
            };

            if (initialStatus == null || !initialStatus.IsDebugging)
            {
                result.Success = false;
                result.ErrorCode = "NOT_DEBUGGING";
                result.Message = "No active debug session";
                return result;
            }

            if (initialStatus.IsPaused)
            {
                  var continueResult = ExecuteLongWaitPhase(Continue, phaseExecutor);
                result.ContinueOperation = continueResult;
                result.IssuedContinue = continueResult.CommandIssued;
                if (!continueResult.Success)
                {
                    result.Success = false;
                    result.ErrorCode = continueResult.ErrorCode;
                    result.Message = continueResult.Message;
                    RebaseRequestToCurrentDebuggerIdentity();
                      result.Status = ExecuteLongWaitPhase(GetDebugStatus, phaseExecutor);
                    return result;
                }
            }

            int effectiveWaitMs = DebuggerRequestContext.RemainingMilliseconds(normalizedTimeoutMs);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            StopEventSnapshot stopEvent = _sessionTracker.WaitForBreakOrDesign(
                initialEvent.Generation,
                initialEvent.BreakSequence,
                effectiveWaitMs,
                DebuggerRequestContext.CancellationToken);
            result.WaitedMs = (int)Math.Min(sw.ElapsedMilliseconds, int.MaxValue);

            if (stopEvent != null && string.Equals(stopEvent.Mode, "design", StringComparison.Ordinal))
            {
                DebuggerRequestContext.RebaseDebuggerIdentity(
                    stopEvent.Generation,
                    stopEvent.BreakSequence,
                    stopEvent.ContextEpoch);
                  result.Status = ExecuteLongWaitPhase(GetDebugStatus, phaseExecutor);
                result.Success = false;
                result.ErrorCode = "DEBUG_SESSION_ENDED";
                result.Message = "Debug session ended before hitting a breakpoint";
                return result;
            }

            if (stopEvent != null && stopEvent.Generation != initialEvent.Generation)
            {
                DebuggerRequestContext.RebaseDebuggerIdentity(
                    stopEvent.Generation,
                    stopEvent.BreakSequence,
                    stopEvent.ContextEpoch);
                  result.Status = ExecuteLongWaitPhase(GetDebugStatus, phaseExecutor);
                result.Success = false;
                result.ErrorCode = "STALE_DEBUG_SESSION";
                result.Message = "Debug session changed while waiting; no native context was captured from the replacement session";
                return result;
            }

            if (stopEvent != null && string.Equals(stopEvent.Mode, "break", StringComparison.Ordinal))
            {
                DebuggerRequestContext.RebaseDebuggerIdentity(
                    stopEvent.Generation,
                    stopEvent.BreakSequence,
                    stopEvent.ContextEpoch);
                  result.Context = ExecuteLongWaitPhase(
                      () => CaptureNativeContext(disassemblyCount, stackBytes, includeModules),
                      phaseExecutor);
                result.Success = true;
                result.BreakDetected = true;
                result.TimedOut = false;
                  result.Status = result.Context?.Status ?? ExecuteLongWaitPhase(GetDebugStatus, phaseExecutor);
                result.Message = "Debug stop detected and native context captured";
                return result;
            }

            RebaseRequestToCurrentDebuggerIdentity();
              result.Status = ExecuteLongWaitPhase(GetDebugStatus, phaseExecutor);
            result.Success = false;
            result.TimedOut = true;
            result.ErrorCode = "DEBUG_WAIT_TIMEOUT";
            result.Message = "Timed out waiting for debugger to enter break mode";
            return result;
        }

        public ManagedAssemblyResult InspectManagedAssembly(
            string assemblyPath,
            string typeFilter = null,
            int maxTypes = 128,
            int maxMethodsPerType = 32)
        {
            return _managedInspectionService.InspectAssembly(
                assemblyPath, typeFilter, maxTypes, maxMethodsPerType);
        }

        public ManagedTypeResult InspectManagedType(string assemblyPath, string typeName, int maxMethods = 32)
        {
            return _managedInspectionService.InspectType(assemblyPath, typeName, maxMethods);
        }

        public ManagedMethodIlResult InspectManagedMethodIl(
            string assemblyPath,
            string typeName,
            string methodName,
            int overloadIndex = 0,
            int maxInstructions = 512)
        {
            return _managedInspectionService.InspectMethodIl(
                assemblyPath, typeName, methodName, overloadIndex, maxInstructions);
        }

        public HybridContextResult CaptureHybridContext(
            int disassemblyCount = 32,
            int stackBytes = 256,
            bool includeNative = true,
            bool includeModules = true,
            bool includeThreads = true,
            bool includeBreakpoints = true,
            bool threadIncludeFrames = true,
            int threadMaxFramesPerThread = -1,
            int threadMaxNodes = -1,
            int threadBudgetMs = -1)
        {
            var budget = new CompositeCaptureBudget();
            var result = new HybridContextResult
            {
                SnapshotId = Guid.NewGuid().ToString("N"),
                CapturedAt = DateTime.UtcNow.ToString("o"),
                SelectedStackFrameIndex = 0,
                MaxResultBytes = CompositeCaptureBudget.MaxResultBytes,
            };
            try
            {
                result.Status = RetainHybridComponent(result, budget, "status", GetDebugStatus());
            }
            catch (Exception ex) { RethrowRequestControlFlow(ex); AppendHybridContextError(result, "status", ex.Message); }
            try
            {
                result.CurrentThreadId = ExecuteOnMainThread(() =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    return Debugger?.CurrentThread?.ID ?? 0;
                });
            }
            catch (Exception ex) { RethrowRequestControlFlow(ex); AppendHybridContextError(result, "currentThread", ex.Message); }

            // Breakpoint bindings are captured before larger optional components
            // because exact hit attribution depends on them.
            if (includeBreakpoints && BeginHybridComponent(result, budget, "breakpoints"))
            {
                try
                {
                    result.Breakpoints = RetainHybridComponent(
                        result, budget, "breakpoints", GetBreakpoints());
                    if (result.Breakpoints != null && SnapshotCompletenessPolicy.IsIncomplete(result.Breakpoints))
                    {
                        AppendHybridContextError(
                            result,
                            "breakpoints",
                            result.Breakpoints.Error ??
                                $"snapshotStatus={result.Breakpoints.SnapshotStatus ?? "unavailable"}");
                    }
                }
                catch (Exception ex) { RethrowRequestControlFlow(ex); AppendHybridContextError(result, "breakpoints", ex.Message); }
            }
            if (includeNative && BeginHybridComponent(result, budget, "nativeContext"))
            {
                try
                {
                    result.NativeContext = RetainHybridComponent(
                        result,
                        budget,
                        "nativeContext",
                        CaptureNativeContext(disassemblyCount, stackBytes, includeModules));
                    if (result.NativeContext?.Partial == true)
                        AppendHybridContextError(result, "nativeContext", result.NativeContext.Error ?? "partial");
                }
                catch (Exception ex) { RethrowRequestControlFlow(ex); AppendHybridContextError(result, "nativeContext", ex.Message); }
            }
            if (includeThreads && BeginHybridComponent(result, budget, "threads"))
            {
                try
                {
                    result.Threads = RetainHybridComponent(
                        result,
                        budget,
                        "threads",
                        GetThreads(
                            threadIncludeFrames,
                            threadMaxFramesPerThread,
                            threadMaxNodes,
                            threadBudgetMs));
                    if (result.Threads != null && SnapshotCompletenessPolicy.IsIncomplete(result.Threads))
                    {
                        AppendHybridContextError(
                            result,
                            "threads",
                            result.Threads.Error ??
                                $"snapshotStatus={result.Threads.SnapshotStatus ?? "unavailable"}");
                    }
                }
                catch (Exception ex) { RethrowRequestControlFlow(ex); AppendHybridContextError(result, "threads", ex.Message); }
            }
            result.ResultBytes = budget.ResultBytes;
            return result;
        }

        private static bool BeginHybridComponent(
            HybridContextResult result,
            CompositeCaptureBudget budget,
            string component)
        {
            if (budget.CanAttemptComponent) return true;
            OmitHybridComponent(result, component, "composite result byte budget was exhausted");
            return false;
        }

        private static T RetainHybridComponent<T>(
            HybridContextResult result,
            CompositeCaptureBudget budget,
            string component,
            T value) where T : class
        {
            if (budget.TryConsumeComponent(component, value)) return value;
            OmitHybridComponent(result, component, "component exceeded the remaining composite result byte budget");
            return null;
        }

        private static void OmitHybridComponent(
            HybridContextResult result,
            string component,
            string reason)
        {
            if (!result.OmittedComponents.Contains(component, StringComparer.OrdinalIgnoreCase))
                result.OmittedComponents.Add(component);
            AppendHybridContextError(result, component, reason);
        }

        private static void AppendHybridContextError(
            HybridContextResult result,
            string component,
            string message)
        {
            result.Partial = true;
            string detail = BoundNativeDiagnostic(component + ": " + message);
            result.Error = BoundNativeDiagnostic(
                string.IsNullOrEmpty(result.Error) ? detail : result.Error + "; " + detail,
                64 * 1024);
        }

        private static string BoundNativeDiagnostic(string value, int maxBytes = 8 * 1024)
        {
            return OutputContentBudget.FitPrefix(value ?? string.Empty, maxBytes, out _);
        }

        [SuppressMessage("Usage", "VSTHRD010", Justification = "Delegates to the self-marshalling GetBreakpoints entrypoint.")]
        public BreakpointsResult GetBreakpointBindings(int startIndex = 0, int maxNodes = -1, int budgetMs = -1)
        {
            return GetBreakpoints(startIndex, maxNodes, budgetMs);
        }

        [SuppressMessage("Usage", "VSTHRD010", Justification = "Composes self-marshalling debugger operations with an off-UI event wait.")]
        public BreakpointCaptureResult ContinueAndCaptureBreakpoint(
            int timeoutMs = 120000,
            int pollIntervalMs = 200,
            int disassemblyCount = 32,
            int stackBytes = 256,
            bool includeNative = true,
            bool includeModules = true,
            bool includeThreads = true,
            bool includeBreakpoints = true,
            bool threadIncludeFrames = true,
            int threadMaxFramesPerThread = -1,
            int threadMaxNodes = -1,
            int threadBudgetMs = -1,
            Func<Func<object>, object> phaseExecutor = null)
        {
            int normalizedTimeoutMs = Math.Max(1000, Math.Min(timeoutMs, 30 * 60 * 1000));
            int normalizedPollIntervalMs = Math.Max(50, Math.Min(pollIntervalMs, 1000));
            var waitResult = ContinueAndWaitForBreak(
                expressions: null,
                expandChildren: false,
                maxDepth: 0,
                outputTailLines: 0,
                timeoutMs: normalizedTimeoutMs,
                pollIntervalMs: normalizedPollIntervalMs,
                includeLocals: false,
                includeModules: false,
                includeRegisters: false,
                includeOutput: false,
                includeThreads: includeThreads,
                includeException: false,
                threadIncludeFrames: threadIncludeFrames,
                threadMaxFramesPerThread: threadMaxFramesPerThread,
                threadMaxNodes: threadMaxNodes,
                threadBudgetMs: threadBudgetMs,
                phaseExecutor: phaseExecutor,
                captureBundle: false);

            var result = new BreakpointCaptureResult
            {
                ContinueResult = waitResult,
                Success = waitResult.Success,
                Message = waitResult.Message,
                ErrorCode = waitResult.ErrorCode,
                WaitTimeoutMs = normalizedTimeoutMs,
                PollIntervalMs = normalizedPollIntervalMs,
                WaitedMs = waitResult.WaitedMs,
                IssuedContinue = waitResult.IssuedContinue,
                TimedOut = waitResult.TimedOut,
                BreakDetected = waitResult.BreakDetected,
                StopReason = waitResult.StopReason,
                Status = waitResult.Status
            };

            if (waitResult.BreakDetected)
            {
                result.HybridContext = ExecuteLongWaitPhase(() => CaptureHybridContext(
                        disassemblyCount,
                        stackBytes,
                        includeNative,
                        includeModules,
                        includeThreads,
                        includeBreakpoints,
                        threadIncludeFrames,
                        threadMaxFramesPerThread,
                        threadMaxNodes,
                        threadBudgetMs),
                    phaseExecutor);
                result.Status = result.HybridContext?.Status ?? result.Status;
                result.StopReason = result.Status?.StopReason ?? result.StopReason;
                result.Breakpoints = result.HybridContext?.Breakpoints;
                result.HitBreakpoint = DetermineHitBreakpointExact(result.HybridContext, out string hitAttributionError);
                // Keep breakpoint bindings at the documented top-level location,
                // but do not serialize the same potentially multi-megabyte page twice.
                if (result.HybridContext != null)
                {
                    result.HybridContext.Breakpoints = null;
                }
                if (result.HitBreakpoint != null)
                {
                    result.HitAttributionStatus = "ok";
                }
                else
                {
                    bool incompleteBreakpoints = result.Breakpoints?.Partial == true ||
                        result.Breakpoints?.HasMore == true;
                    result.HitAttributionStatus = incompleteBreakpoints ? "incomplete" : "missing";
                    result.HitAttributionError = incompleteBreakpoints
                        ? $"Breakpoint attribution is incomplete; continue get_breakpoint_bindings at start_index={result.Breakpoints.NextIndex}. {hitAttributionError}"
                        : hitAttributionError;
                }
            }

            if (includeBreakpoints && result.Breakpoints == null &&
                (!result.BreakDetected || result.HybridContext == null))
            {
                try
                {
                    result.Breakpoints = ExecuteLongWaitPhase(() => GetBreakpointBindings(), phaseExecutor);
                }
                catch (Exception ex)
                {
                    RethrowRequestControlFlow(ex);
                    result.Message = string.IsNullOrEmpty(result.Message)
                        ? $"Breakpoint binding capture failed: {ex.Message}"
                        : $"{result.Message}; breakpoint binding capture failed: {ex.Message}";
                }
            }

            if (!result.BreakDetected && string.IsNullOrEmpty(result.HitAttributionError))
            {
                result.HitAttributionStatus = "missing";
                result.HitAttributionError = "No debug stop was captured";
            }

            PopulateHitVerification(result);
            return result;
        }

        private BreakpointHitInfo DetermineHitBreakpointExact(HybridContextResult context, out string missingReason)
        {
            missingReason = null;
            var currentAddress = context?.NativeContext?.CurrentAddress;
            var breakpoints = context?.Breakpoints?.Breakpoints;
            if (breakpoints == null || breakpoints.Count == 0)
            {
                missingReason = "No breakpoint binding data is available";
                return null;
            }

            var exactMatches = new List<BreakpointInfo>();
            if (currentAddress != null && currentAddress.AddressValue != 0)
            {
                foreach (var breakpoint in breakpoints)
                {
                    if (breakpoint == null ||
                        !breakpoint.Enabled ||
                        !breakpoint.IsBound ||
                        breakpoint.TrackedAddresses == null)
                    {
                        continue;
                    }

                    foreach (var trackedAddress in breakpoint.TrackedAddresses)
                    {
                        if (trackedAddress != null &&
                            trackedAddress.AddressValue != 0 &&
                            trackedAddress.AddressValue == currentAddress.AddressValue)
                        {
                            exactMatches.Add(breakpoint);
                            break;
                        }
                    }
                }
            }

            if (exactMatches.Count == 1)
            {
                return new BreakpointHitInfo
                {
                    Confidence = "exact",
                    Reason = "Current instruction address exactly matches an enabled bound tracked address breakpoint",
                    CurrentAddress = currentAddress,
                    Breakpoint = exactMatches[0]
                };
            }

            if (exactMatches.Count > 1)
            {
                missingReason = "Multiple tracked address breakpoints match the current instruction address";
                return null;
            }

            var sourceMatches = FindCurrentSourceLocationBreakpointMatches(context, breakpoints);
            if (sourceMatches.Count == 1)
            {
                return new BreakpointHitInfo
                {
                    Confidence = "exact_source_location",
                    Reason = "Current source location exactly matches an enabled bound source breakpoint resolved location",
                    CurrentAddress = currentAddress,
                    Breakpoint = sourceMatches[0]
                };
            }

            if (sourceMatches.Count > 1)
            {
                missingReason = "Multiple enabled bound source breakpoints match the current source location";
                return null;
            }

            missingReason = currentAddress == null || currentAddress.AddressValue == 0
                ? "Current instruction address is unavailable, and no enabled bound source breakpoint exactly matches the current source location"
                : "No enabled bound tracked address breakpoint or source breakpoint exactly matches the current stop location";
            return null;
        }

        private List<BreakpointInfo> FindCurrentSourceLocationBreakpointMatches(HybridContextResult context, List<BreakpointInfo> breakpoints)
        {
            var matches = new List<BreakpointInfo>();
            LocationInfo currentLocation = GetCurrentSourceLocation(context);
            if (currentLocation == null || string.IsNullOrEmpty(currentLocation.FilePath) ||
                currentLocation.LineNumber.GetValueOrDefault() <= 0)
            {
                return matches;
            }

            foreach (var breakpoint in breakpoints)
            {
                if (breakpoint == null || !breakpoint.Enabled || !breakpoint.IsBound)
                {
                    continue;
                }

                string breakpointFile = !string.IsNullOrEmpty(breakpoint.ResolvedFilePath)
                    ? breakpoint.ResolvedFilePath
                    : breakpoint.FilePath;
                int breakpointLine = breakpoint.ResolvedLineNumber > 0
                    ? breakpoint.ResolvedLineNumber
                    : breakpoint.LineNumber;
                if (string.IsNullOrEmpty(breakpointFile) || breakpointLine <= 0)
                {
                    continue;
                }

                if (string.Equals(breakpointFile, currentLocation.FilePath, StringComparison.OrdinalIgnoreCase) &&
                    breakpointLine == currentLocation.LineNumber)
                {
                    matches.Add(breakpoint);
                }
            }

            return matches;
        }

        private LocationInfo GetCurrentSourceLocation(HybridContextResult context)
        {
            var currentLocation = context?.Status?.CurrentLocation;
            if (currentLocation != null && !string.IsNullOrEmpty(currentLocation.FilePath) &&
                currentLocation.LineNumber.GetValueOrDefault() > 0)
            {
                return currentLocation;
            }

            var currentThread = context?.Threads?.Threads?.FirstOrDefault(thread => thread != null && thread.IsCurrent);
            var frame = currentThread?.Frames?.FirstOrDefault(f =>
                f != null &&
                f.Index == 0 &&
                f.LineNumberReliable == true &&
                !string.IsNullOrEmpty(f.FilePath) &&
                f.LineNumber.GetValueOrDefault() > 0);
            if (frame == null)
            {
                return null;
            }

            return new LocationInfo
            {
                FilePath = frame.FilePath,
                FunctionName = frame.FunctionName,
                LineNumber = frame.LineNumber,
                ColumnNumber = frame.ColumnNumber,
                LineNumberReliable = frame.LineNumberReliable,
                SourceLocationStatus = frame.SourceLocationStatus,
            };
        }

        private void PopulateHitVerification(BreakpointCaptureResult result)
        {
            if (result == null)
            {
                return;
            }

            if (result.HitBreakpoint != null)
            {
                if (string.Equals(result.HitBreakpoint.Confidence, "exact_source_location", StringComparison.OrdinalIgnoreCase))
                {
                    result.HitVerificationStatus = "hit_exact_source_location";
                    result.HitVerificationMessage = "Current source location exactly matched one enabled bound source breakpoint resolved location.";
                    return;
                }

                result.HitVerificationStatus = "hit_exact_tracked_address";
                result.HitVerificationMessage = "Current instruction address exactly matched one tracked address breakpoint.";
                return;
            }

            if (!result.BreakDetected)
            {
                result.HitVerificationStatus = result.TimedOut ? "not_hit_timeout" : "not_hit";
                result.HitVerificationMessage = result.HitAttributionError ?? result.Message ?? "No debug stop was captured.";
                return;
            }

            if (string.Equals(result.HitAttributionStatus, "incomplete", StringComparison.OrdinalIgnoreCase))
            {
                result.HitVerificationStatus = "breakpoint_attribution_incomplete";
                result.HitVerificationMessage = result.HitAttributionError;
                return;
            }

            string stopReason = string.IsNullOrEmpty(result.StopReason) ? result.Status?.StopReason : result.StopReason;
            if (string.Equals(stopReason, "breakpoint", StringComparison.OrdinalIgnoreCase))
            {
                result.HitVerificationStatus = "breakpoint_stop_unattributed";
                result.HitVerificationMessage = result.HitAttributionError ??
                    "The debugger stopped for a breakpoint, but no tracked address breakpoint matched the current instruction address.";
                return;
            }

            result.HitVerificationStatus = "stopped_not_verified_as_breakpoint";
            result.HitVerificationMessage = string.IsNullOrEmpty(stopReason)
                ? "The debugger stopped, but the stop reason is unavailable and exact breakpoint attribution is missing."
                : $"The debugger stopped for '{stopReason}', not for a verified tracked address breakpoint.";
        }

        private NativeAddressInfo ResolveAddressInternal(
            Debugger5 debugger,
            string addressExpression,
            ModulesResult modules = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string expression = string.IsNullOrWhiteSpace(addressExpression) ? null : addressExpression.Trim();
            var result = new NativeAddressInfo
            {
                Expression = expression
            };

            if (string.IsNullOrEmpty(expression))
            {
                result.Error = "Address expression is required";
                return result;
            }

            if (TryParseModuleRvaExpression(expression, out string moduleIdentifier, out ulong rva))
            {
                var module = FindModuleByIdentifier(modules, moduleIdentifier);
                if (module == null)
                {
                    result.Error = $"Module not found for module+RVA expression: {moduleIdentifier}";
                    return result;
                }

                ulong moduleBase = ParseAddress(module.Address);
                if (moduleBase == 0)
                {
                    result.Error = $"Module base address is unavailable: {moduleIdentifier}";
                    return result;
                }

                if (rva > ulong.MaxValue - moduleBase)
                {
                    result.Error = "module+RVA expression overflows address space";
                    return result;
                }

                ulong resolvedValue = moduleBase + rva;
                return new NativeAddressInfo
                {
                    Expression = expression,
                    AddressValue = resolvedValue,
                    VirtualAddress = FormatAddress(resolvedValue),
                    Status = "address_only"
                };
            }

            ulong addressValue;
            if (!TryParseAddressLiteral(expression, out addressValue))
            {
                if (debugger == null || debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    result.Error = "Expression-based address resolution requires break mode";
                    return result;
                }

                try
                {
                    MarkUnsafeEvaluationIssued();
                    var addressExpr = debugger.GetExpression(expression, false, ExpressionEvaluationTimeoutMs);
                    if (addressExpr == null || !addressExpr.IsValidValue)
                    {
                        result.Error = $"Invalid address expression: {expression}";
                        return result;
                    }

                    addressValue = ParseAddress(addressExpr.Value);
                }
                catch (Exception ex)
                {
                    result.Error = ex.Message;
                    return result;
                }
            }

            if (addressValue == 0)
            {
                result.Error = $"Could not resolve address: {expression}";
                return result;
            }

            return new NativeAddressInfo
            {
                Expression = expression,
                AddressValue = addressValue,
                VirtualAddress = FormatAddress(addressValue),
                Status = "address_only"
            };
        }

        private NativeAddressInfo BuildNativeAddressInfo(
            Debugger5 debugger,
            string expression,
            ulong addressValue,
            ModulesResult modules,
            ModuleInfo explicitModule = null)
        {
            var result = new NativeAddressInfo
            {
                Expression = expression,
                AddressValue = addressValue,
                VirtualAddress = FormatAddress(addressValue),
                Status = "address_only"
            };

            var module = explicitModule ?? FindContainingModule(modules, addressValue);
            if (module == null)
            {
                return result;
            }

            ulong moduleBase = ParseAddress(module.Address);
            ulong moduleSize = module.SizeBytes;
            if (moduleBase == 0)
            {
                result.FunctionStatus = "missing";
                result.FunctionError = "Module base address is unavailable";
                return result;
            }

            result.Status = "ok";
            result.ModuleName = module.Name;
            result.ModulePath = module.Path;
            result.ModuleBase = FormatAddress(moduleBase);
            result.ModuleBaseValue = moduleBase;
            result.ModuleSizeBytes = moduleSize;
            result.RvaValue = addressValue >= moduleBase ? addressValue - moduleBase : 0;
            result.Rva = FormatRva(result.RvaValue);

            try
            {
                var layout = BuildModuleLayoutFromFile(module, maxImportFunctions: 0, maxExports: 512);
                if (result.ModuleSizeBytes == 0 && layout.ImageSizeBytes != 0)
                {
                    result.ModuleSizeBytes = layout.ImageSizeBytes;
                }

                if (explicitModule != null &&
                    result.ModuleSizeBytes != 0 &&
                    result.RvaValue >= result.ModuleSizeBytes)
                {
                    result.Status = "invalid";
                    result.Error = "module+RVA is outside the module image size";
                    result.FunctionStatus = "missing";
                    result.FunctionError = result.Error;
                    return result;
                }

                var section = FindSectionForRva(layout.Sections, result.RvaValue);
                if (section != null)
                {
                    ulong sectionRva = ParseAddress(section.Rva);
                    result.SectionName = section.Name;
                    result.SectionRva = section.Rva;
                    result.SectionOffset = FormatRva(result.RvaValue - sectionRva);
                }

                var export = layout.Exports
                    .Select(e => new { Export = e, Rva = ParseAddress(e.Rva) })
                    .Where(e => e.Rva <= result.RvaValue)
                    .OrderByDescending(e => e.Rva)
                    .FirstOrDefault();
                if (export != null && !string.IsNullOrEmpty(export.Export.Name))
                {
                    ulong delta = result.RvaValue - export.Rva;
                    result.NearestSymbol = delta == 0
                        ? export.Export.Name
                        : $"{export.Export.Name}+0x{delta:X}";
                    if (delta == 0)
                    {
                        result.Function = BuildExactExportFunctionAttribution(addressValue, moduleBase, module, export.Export);
                        result.FunctionStatus = "ok";
                    }
                }

                if (result.Function == null)
                {
                    result.FunctionStatus = "missing";
                    result.FunctionError = string.IsNullOrEmpty(result.NearestSymbol)
                        ? "No exact native function symbol is available for this address"
                        : "Only nearestSymbol+offset is available; exact native function attribution is missing";
                }
            }
            catch (Exception ex)
            {
                _log($"ResolveAddress layout enrichment failed: {ex.GetType().Name}");
                result.FunctionStatus = "missing";
                result.FunctionError = "Native function attribution unavailable: " + ex.Message;
            }

            return result;
        }

        private NativeFunctionInfo BuildExactExportFunctionAttribution(
            ulong addressValue,
            ulong moduleBase,
            ModuleInfo module,
            ExportInfo export)
        {
            if (export == null || string.IsNullOrEmpty(export.Name))
            {
                return null;
            }

            ulong rva = ParseAddress(export.Rva);
            return new NativeFunctionInfo
            {
                StartAddress = FormatAddress(addressValue),
                StartAddressValue = addressValue,
                StartRva = FormatRva(rva),
                AddressOffset = FormatRva(0),
                NearestSymbol = export.Name,
                Confidence = "exact",
                Reason = $"Exact PE export match in {module?.Name}; function size and end address are not inferred"
            };
        }

        private bool TryParseModuleRvaExpression(string expression, out string moduleIdentifier, out ulong rva)
        {
            moduleIdentifier = null;
            rva = 0;
            if (string.IsNullOrWhiteSpace(expression))
            {
                return false;
            }

            int plusIndex = expression.LastIndexOf('+');
            if (plusIndex <= 0 || plusIndex >= expression.Length - 1)
            {
                return false;
            }

            string left = expression.Substring(0, plusIndex).Trim().Trim('"');
            string right = expression.Substring(plusIndex + 1).Trim();
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
            {
                return false;
            }

            if (!TryParseAddressLiteral(right, out rva))
            {
                return false;
            }

            moduleIdentifier = left;
            return true;
        }

        private bool TryParseAddressLiteral(string value, out ulong address)
        {
            address = 0;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string trimmed = value.Trim();
            if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                string hex = trimmed.Substring(2);
                return ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out address);
            }

            if (trimmed.EndsWith("h", StringComparison.OrdinalIgnoreCase))
            {
                return ulong.TryParse(trimmed.Substring(0, trimmed.Length - 1), System.Globalization.NumberStyles.HexNumber, null, out address);
            }

            if (ulong.TryParse(trimmed, out address))
            {
                return true;
            }

            return Regex.IsMatch(trimmed, @"\A[0-9A-Fa-f]+\z") &&
                   ulong.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber, null, out address);
        }

        private ModuleInfo FindModuleByIdentifier(ModulesResult modules, string identifier)
        {
            if (modules?.Modules == null || string.IsNullOrWhiteSpace(identifier))
            {
                return null;
            }

            string normalized = identifier.Trim().Trim('"');
            ulong baseAddress;
            bool identifierIsAddress = TryParseAddressLiteral(normalized, out baseAddress);
            string normalizedFileName = SafeGetFileName(normalized);

            foreach (var module in modules.Modules)
            {
                if (module == null)
                {
                    continue;
                }

                if (identifierIsAddress && ParseAddress(module.Address) == baseAddress)
                {
                    return module;
                }

                if (string.Equals(module.Name, normalized, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(module.Path, normalized, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(SafeGetFileName(module.Path), normalized, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(module.Name, normalizedFileName, StringComparison.OrdinalIgnoreCase))
                {
                    return module;
                }
            }

            return null;
        }

        private ModuleInfo FindContainingModule(ModulesResult modules, ulong address)
        {
            if (modules?.Modules == null)
            {
                return null;
            }

            return modules.Modules
                .Select(m => new { Module = m, Base = ParseAddress(m.Address), Size = m.SizeBytes })
                .Where(m => m.Base != 0 && m.Size != 0 && address >= m.Base && address - m.Base < m.Size)
                .OrderByDescending(m => m.Base)
                .Select(m => m.Module)
                .FirstOrDefault();
        }

        private ModuleLayoutResult BuildModuleLayoutFromFile(ModuleInfo module, int maxImportFunctions, int maxExports)
        {
            var result = new ModuleLayoutResult
            {
                Module = module,
                Status = "unavailable"
            };

            if (module == null || string.IsNullOrWhiteSpace(module.Path) || !System.IO.File.Exists(module.Path))
            {
                result.Error = "Module file is unavailable";
                return result;
            }

            try
            {
                using (var stream = new System.IO.FileStream(module.Path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete))
                using (var reader = new System.IO.BinaryReader(stream, Encoding.UTF8))
                {
                    if (stream.Length < 0x100)
                    {
                        result.Error = "File is too small to be a PE image";
                        return result;
                    }

                    ushort mz = ReadUInt16At(reader, 0);
                    if (mz != 0x5A4D)
                    {
                        result.Error = "Missing MZ header";
                        return result;
                    }

                    uint peOffset = ReadUInt32At(reader, 0x3C);
                    if (peOffset == 0 || peOffset > stream.Length - 0x108)
                    {
                        result.Error = "Invalid PE header offset";
                        return result;
                    }

                    uint signature = ReadUInt32At(reader, peOffset);
                    if (signature != 0x00004550)
                    {
                        result.Error = "Missing PE signature";
                        return result;
                    }

                    ushort sectionCount = ReadUInt16At(reader, peOffset + 6);
                    ushort optionalHeaderSize = ReadUInt16At(reader, peOffset + 20);
                    long optionalHeaderOffset = peOffset + 24;
                    ushort magic = ReadUInt16At(reader, optionalHeaderOffset);
                    bool is64Bit = magic == 0x20B;
                    if (!is64Bit && magic != 0x10B)
                    {
                        result.Error = $"Unsupported PE optional header magic: 0x{magic:X}";
                        return result;
                    }

                    uint entryPointRva = ReadUInt32At(reader, optionalHeaderOffset + 16);
                    ulong imageBase = is64Bit
                        ? ReadUInt64At(reader, optionalHeaderOffset + 24)
                        : ReadUInt32At(reader, optionalHeaderOffset + 28);
                    uint imageSize = ReadUInt32At(reader, optionalHeaderOffset + 56);
                    long dataDirectoryOffset = optionalHeaderOffset + (is64Bit ? 112 : 96);
                    uint exportDirectoryRva = optionalHeaderSize >= (is64Bit ? 120 : 104) ? ReadUInt32At(reader, dataDirectoryOffset) : 0;
                    uint importDirectoryRva = optionalHeaderSize >= (is64Bit ? 128 : 112) ? ReadUInt32At(reader, dataDirectoryOffset + 8) : 0;

                    ulong moduleBase = ParseAddress(module.Address);
                    if (moduleBase == 0)
                    {
                        moduleBase = imageBase;
                    }

                    result.Is64Bit = is64Bit;
                    result.EntryPointRva = FormatRva(entryPointRva);
                    result.EntryPointVa = moduleBase != 0 ? FormatAddress(moduleBase + entryPointRva) : null;
                    result.ImageBase = FormatAddress(imageBase);
                    result.ImageSizeBytes = imageSize;
                    if (module.SizeBytes == 0 && imageSize != 0)
                    {
                        module.SizeBytes = imageSize;
                        module.Size = FormatSize(imageSize);
                    }

                    long sectionTableOffset = optionalHeaderOffset + optionalHeaderSize;
                    int safeSectionCount = Math.Min(sectionCount, (ushort)96);
                    for (int i = 0; i < safeSectionCount; i++)
                    {
                        long sectionOffset = sectionTableOffset + (i * 40);
                        if (sectionOffset + 40 > stream.Length)
                        {
                            result.Truncated = true;
                            break;
                        }

                        string name = ReadSectionName(reader, sectionOffset);
                        uint virtualSize = ReadUInt32At(reader, sectionOffset + 8);
                        uint virtualAddress = ReadUInt32At(reader, sectionOffset + 12);
                        uint rawSize = ReadUInt32At(reader, sectionOffset + 16);
                        uint rawPointer = ReadUInt32At(reader, sectionOffset + 20);
                        uint characteristics = ReadUInt32At(reader, sectionOffset + 36);
                        result.Sections.Add(new ModuleSectionInfo
                        {
                            Name = name,
                            Rva = FormatRva(virtualAddress),
                            VirtualAddress = moduleBase != 0 ? FormatAddress(moduleBase + virtualAddress) : null,
                            VirtualSize = virtualSize,
                            RawSize = rawSize,
                            RawPointer = FormatRva(rawPointer),
                            Characteristics = $"0x{characteristics:X8}"
                        });
                    }

                    if (exportDirectoryRva != 0 && maxExports > 0)
                    {
                        ReadExports(reader, result, exportDirectoryRva, moduleBase, maxExports);
                    }

                    if (importDirectoryRva != 0 && maxImportFunctions > 0)
                    {
                        ReadImports(reader, result, importDirectoryRva, maxImportFunctions, is64Bit);
                    }
                }

                result.Status = "ok";
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }

            return result;
        }

        private void ReadExports(System.IO.BinaryReader reader, ModuleLayoutResult result, uint exportDirectoryRva, ulong moduleBase, int maxExports)
        {
            long exportOffset = RvaToFileOffset(exportDirectoryRva, result.Sections);
            if (!IsReadableRange(reader, exportOffset, 40))
            {
                return;
            }

            uint ordinalBase = ReadUInt32At(reader, exportOffset + 16);
            uint functionCount = ReadUInt32At(reader, exportOffset + 20);
            uint nameCount = ReadUInt32At(reader, exportOffset + 24);
            uint functionTableRva = ReadUInt32At(reader, exportOffset + 28);
            uint nameTableRva = ReadUInt32At(reader, exportOffset + 32);
            uint ordinalTableRva = ReadUInt32At(reader, exportOffset + 36);
            long nameTableOffset = RvaToFileOffset(nameTableRva, result.Sections);
            long ordinalTableOffset = RvaToFileOffset(ordinalTableRva, result.Sections);
            long functionTableOffset = RvaToFileOffset(functionTableRva, result.Sections);

            uint safeNameCount = Math.Min(nameCount, (uint)Math.Max(0, maxExports));
            for (uint i = 0; i < safeNameCount; i++)
            {
                if (!IsReadableRange(reader, nameTableOffset + (i * 4), 4) ||
                    !IsReadableRange(reader, ordinalTableOffset + (i * 2), 2))
                {
                    result.Truncated = true;
                    break;
                }

                uint nameRva = ReadUInt32At(reader, nameTableOffset + (i * 4));
                ushort ordinalIndex = ReadUInt16At(reader, ordinalTableOffset + (i * 2));
                if (ordinalIndex >= functionCount || !IsReadableRange(reader, functionTableOffset + (ordinalIndex * 4), 4))
                {
                    continue;
                }

                uint functionRva = ReadUInt32At(reader, functionTableOffset + (ordinalIndex * 4));
                string name = ReadNullTerminatedAscii(reader, RvaToFileOffset(nameRva, result.Sections), 512);
                result.Exports.Add(new ExportInfo
                {
                    Name = name,
                    Ordinal = (int)(ordinalBase + ordinalIndex),
                    Rva = FormatRva(functionRva),
                    VirtualAddress = moduleBase != 0 ? FormatAddress(moduleBase + functionRva) : null
                });
            }

            if (nameCount > safeNameCount)
            {
                result.Truncated = true;
            }
        }

        private void ReadImports(System.IO.BinaryReader reader, ModuleLayoutResult result, uint importDirectoryRva, int maxImportFunctions, bool is64Bit)
        {
            long descriptorOffset = RvaToFileOffset(importDirectoryRva, result.Sections);
            int totalFunctions = 0;
            const int maxImportModules = 128;

            for (int moduleIndex = 0; moduleIndex < maxImportModules; moduleIndex++)
            {
                long currentDescriptor = descriptorOffset + (moduleIndex * 20);
                if (!IsReadableRange(reader, currentDescriptor, 20))
                {
                    result.Truncated = true;
                    break;
                }

                uint originalFirstThunk = ReadUInt32At(reader, currentDescriptor);
                uint nameRva = ReadUInt32At(reader, currentDescriptor + 12);
                uint firstThunk = ReadUInt32At(reader, currentDescriptor + 16);
                if (originalFirstThunk == 0 && nameRva == 0 && firstThunk == 0)
                {
                    break;
                }

                string moduleName = ReadNullTerminatedAscii(reader, RvaToFileOffset(nameRva, result.Sections), 512);
                var importedModule = new ImportedModuleInfo { ModuleName = moduleName };
                uint thunkRva = originalFirstThunk != 0 ? originalFirstThunk : firstThunk;
                int thunkSize = is64Bit ? 8 : 4;
                ulong ordinalFlag = is64Bit ? 0x8000000000000000UL : 0x80000000UL;
                ulong thunkMask = is64Bit ? 0x7FFFFFFFFFFFFFFFUL : 0x7FFFFFFFUL;

                for (int thunkIndex = 0; thunkIndex < 4096; thunkIndex++)
                {
                    if (totalFunctions >= maxImportFunctions)
                    {
                        importedModule.Truncated = true;
                        result.Truncated = true;
                        break;
                    }

                    long thunkOffset = RvaToFileOffset(thunkRva + (uint)(thunkIndex * thunkSize), result.Sections);
                    if (!IsReadableRange(reader, thunkOffset, thunkSize))
                    {
                        importedModule.Truncated = true;
                        result.Truncated = true;
                        break;
                    }

                    ulong thunkValue = is64Bit ? ReadUInt64At(reader, thunkOffset) : ReadUInt32At(reader, thunkOffset);
                    if (thunkValue == 0)
                    {
                        break;
                    }

                    var function = new ImportFunctionInfo
                    {
                        ThunkRva = FormatRva(thunkRva + (uint)(thunkIndex * thunkSize))
                    };
                    if ((thunkValue & ordinalFlag) != 0)
                    {
                        function.Ordinal = thunkValue & 0xFFFF;
                    }
                    else
                    {
                        uint hintNameRva = (uint)(thunkValue & thunkMask);
                        long hintNameOffset = RvaToFileOffset(hintNameRva, result.Sections);
                        if (IsReadableRange(reader, hintNameOffset, 2))
                        {
                            function.Name = ReadNullTerminatedAscii(reader, hintNameOffset + 2, 512);
                        }
                    }

                    importedModule.Functions.Add(function);
                    totalFunctions++;
                }

                result.Imports.Add(importedModule);
                if (totalFunctions >= maxImportFunctions)
                {
                    break;
                }
            }
        }

        private ModuleSectionInfo FindSectionForRva(IEnumerable<ModuleSectionInfo> sections, ulong rva)
        {
            if (sections == null)
            {
                return null;
            }

            foreach (var section in sections)
            {
                ulong sectionRva = ParseAddress(section.Rva);
                ulong sectionSize = Math.Max(section.VirtualSize, section.RawSize);
                if (sectionSize == 0)
                {
                    continue;
                }

                if (rva >= sectionRva && rva < sectionRva + sectionSize)
                {
                    return section;
                }
            }

            return null;
        }

        private long RvaToFileOffset(uint rva, IEnumerable<ModuleSectionInfo> sections)
        {
            if (sections != null)
            {
                foreach (var section in sections)
                {
                    ulong sectionRva = ParseAddress(section.Rva);
                    ulong rawPointer = ParseAddress(section.RawPointer);
                    ulong span = Math.Max(section.VirtualSize, section.RawSize);
                    if (span == 0)
                    {
                        continue;
                    }

                    if ((ulong)rva >= sectionRva && (ulong)rva < sectionRva + span)
                    {
                        return (long)(rawPointer + ((ulong)rva - sectionRva));
                    }
                }
            }

            return rva;
        }

        private bool IsReadableRange(System.IO.BinaryReader reader, long offset, long size)
        {
            return offset >= 0 && size >= 0 && offset <= reader.BaseStream.Length && size <= reader.BaseStream.Length - offset;
        }

        private ushort ReadUInt16At(System.IO.BinaryReader reader, long offset)
        {
            reader.BaseStream.Position = offset;
            return reader.ReadUInt16();
        }

        private uint ReadUInt32At(System.IO.BinaryReader reader, long offset)
        {
            reader.BaseStream.Position = offset;
            return reader.ReadUInt32();
        }

        private ulong ReadUInt64At(System.IO.BinaryReader reader, long offset)
        {
            reader.BaseStream.Position = offset;
            return reader.ReadUInt64();
        }

        private string ReadSectionName(System.IO.BinaryReader reader, long offset)
        {
            reader.BaseStream.Position = offset;
            var bytes = reader.ReadBytes(8);
            int length = Array.IndexOf(bytes, (byte)0);
            if (length < 0)
            {
                length = bytes.Length;
            }

            return Encoding.ASCII.GetString(bytes, 0, length);
        }

        private string ReadNullTerminatedAscii(System.IO.BinaryReader reader, long offset, int maxBytes)
        {
            if (!IsReadableRange(reader, offset, 1))
            {
                return null;
            }

            reader.BaseStream.Position = offset;
            var bytes = new List<byte>();
            int safeMax = Math.Max(1, Math.Min(maxBytes, 4096));
            while (bytes.Count < safeMax && reader.BaseStream.Position < reader.BaseStream.Length)
            {
                byte value = reader.ReadByte();
                if (value == 0)
                {
                    break;
                }
                bytes.Add(value);
            }

            return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
        }

        private string FormatAddress(ulong address)
        {
            return $"0x{address:X16}";
        }

        private string FormatRva(ulong rva)
        {
            return $"0x{rva:X}";
        }

        #endregion
    }
}
