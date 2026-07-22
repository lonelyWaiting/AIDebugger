using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;
using VSDebuggerMCP.Ipc;
using VSDebuggerMCP.Protocol;
using VSDebuggerMCP.Services;


namespace VSDebuggerMCP
{
    public partial class NamedPipeServer
    {
        [SuppressMessage(
            "Usage",
            "VSTHRD010",
            Justification = "HandleMethod routes to self-marshalling DebuggerService entrypoints and must run on the per-session actor worker.")]
        private async Task<string> ProcessRequestAsync(
            string requestJson,
            int clientProcessId,
            bool controlOnly = false,
            bool recoveryOnly = false)
        {
            DebugRequest request = null;
            try
            {
                StrictJsonRequestValidator.Validate(requestJson);
                request = _serializer.Deserialize<DebugRequest>(requestJson);
            }
            catch (Exception ex)
            {
                _log($"Failed to parse request: {ex.GetType().Name}");
                return SerializeResponse(DebugResponse.Failure(null, "PARSE_ERROR", "Request is not valid JSON"));
            }

            DebugMethodDescriptor validatedDescriptor;
            try
            {
                validatedDescriptor = ValidateRequestEnvelopeImmutable(request, clientProcessId);
            }
            catch (ProtocolRequestException ex)
            {
                return SerializeResponse(DebugResponse.Failure(request?.Id, ex.Code, ex.Message));
            }

            if (recoveryOnly && !validatedDescriptor.ControlPlane)
            {
                return SerializeResponse(DebugResponse.Failure(
                    request.Id,
                    "RECOVERY_PIPE_METHOD_REQUIRED",
                    "The dedicated recovery pipe accepts only cancellation, request-status and health methods."));
            }
            if (controlOnly && !recoveryOnly && !validatedDescriptor.ControlPipeEligible)
            {
                return SerializeResponse(DebugResponse.Failure(
                    request.Id,
                    "CONTROL_PIPE_METHOD_REQUIRED",
                    "The dedicated control pipe accepts only authenticated control-plane methods."));
            }

            bool isCommand = string.Equals(GetMethodKind(request.Method), "command", StringComparison.Ordinal);
            string clientLane = CreateClientLane(request.Meta);
            string activeRequestKey = CreateClientRequestKey(clientLane, request.Id);
            string fingerprint = null;
            CommandReservationLease commandLease = null;
            if (isCommand)
            {
                if (!UuidV7RequestIdPolicy.TryValidate(
                    request.Id,
                    DateTime.UtcNow,
                    CommandDeduplicationWindow,
                    CommandRequestFutureSkew,
                    out string requestIdErrorCode,
                    out string requestIdErrorMessage))
                {
                    return SerializeResponse(DebugResponse.Failure(
                        request.Id,
                        requestIdErrorCode,
                        requestIdErrorMessage));
                }
                fingerprint = ComputeCommandFingerprint(request);
                CommandReservationStatus reservation = _commandResults.ReserveOrLookup(
                    clientLane,
                    request.Id,
                    fingerprint,
                    out string cachedResponse,
                    out commandLease);
                if (reservation == CommandReservationStatus.Conflict)
                {
                    return SerializeResponse(DebugResponse.Failure(
                        request.Id,
                        "REQUEST_ID_CONFLICT",
                        "The request id was already used for a different command payload"));
                }
                if (reservation == CommandReservationStatus.Cached)
                {
                    // The cached response identity belongs to the debugger context that
                    // produced the result. Replaying it with a newer identity would make
                    // old data appear safe for a replacement session.
                    return cachedResponse;
                }
                if (reservation == CommandReservationStatus.InProgress)
                {
                    return SerializeResponse(DebugResponse.Failure(
                        request.Id,
                        "REQUEST_IN_PROGRESS",
                        "A command with this request id is already in progress."));
                }
                if (reservation == CommandReservationStatus.ResultEvicted)
                {
                    return SerializeResponse(DebugResponse.Failure(
                        request.Id,
                        "COMPLETED_RESULT_EVICTED",
                        "The command already completed in this server instance, but its response body was evicted; it will not be executed again."));
                }
                if (reservation == CommandReservationStatus.CapacityExceeded)
                {
                    return SerializeResponse(DebugResponse.Failure(
                        request.Id,
                        "COMMAND_LEDGER_WINDOW_CAPACITY_EXCEEDED",
                        "The 24-hour command-id deduplication window is at capacity. Wait for the oldest command ids to expire or restart Visual Studio before issuing more commands."));
                }
            }


            try
            {
                // Exact completed-command lookups above intentionally precede all
                // mutable deadline/session/context checks. A response-loss replay must
                // remain recoverable after the original command changes debugger state.
                ValidateRequestMutableIdentity(request, validatedDescriptor);
            }
            catch (ProtocolRequestException ex)
            {
                if (isCommand) _commandResults.ReleaseReservation(commandLease);
                return SerializeResponse(DebugResponse.Failure(request?.Id, ex.Code, ex.Message));
            }

            var activeRequest = new ActiveRequest
            {
                Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token),
                Fingerprint = fingerprint,
                Kind = request.Meta.Kind,
                EnqueuedUtc = DateTime.UtcNow,
            };
            if (!TryRegisterActiveRequest(activeRequestKey, activeRequest))
            {
                activeRequest.Cancellation.Dispose();
                if (isCommand)
                {
                    _commandResults.ReleaseReservation(commandLease);
                }
                return SerializeResponse(DebugResponse.Failure(
                    request.Id,
                    "REQUEST_IN_PROGRESS",
                    "A request with this id is already in progress"));
            }

            string response = null;
            DebugServerIdentity completionIdentity = null;
            SemaphoreSlim dispatchGate = null;
            bool dispatchGateHeld = false;
            bool sideEffectIssued = false;
            Task<object> deferredInvocation = null;
            LongRunningDebugOperationLease operationAdmissionLease = null;
            try
            {
                DebugMethodDescriptor methodDescriptor = DebugMethodCatalog.Get(request.Method);
                if (methodDescriptor.LongRunningWait)
                {
                    if (!_longRunningOperationGate.TryAcquire(
                        request.Id,
                        methodDescriptor.Name,
                        out operationAdmissionLease))
                    {
                        _longRunningOperationGate.TryGetConflict(
                            methodDescriptor.Name,
                            methodDescriptor.Kind,
                            out string activeLongMethod);
                        throw new DebuggerOperationFailedException(
                            "DEBUG_OPERATION_IN_PROGRESS",
                            $"Long-running debugger operation '{activeLongMethod}' is already active. Cancel it or use break/stop before starting another mutation.");
                    }
                }
                else if (!_longRunningOperationGate.TryAcquireCommand(
                    methodDescriptor.Name,
                    methodDescriptor.Kind,
                    out operationAdmissionLease,
                    out string activeConflictMethod))
                {
                    throw new DebuggerOperationFailedException(
                        "DEBUG_OPERATION_IN_PROGRESS",
                        $"Debugger command '{methodDescriptor.Name}' conflicts with active wait operation '{activeConflictMethod}'. Cancel the wait or use break/stop first.");
                }

                bool useDebugSessionActor = !methodDescriptor.ControlPlane &&
                    !methodDescriptor.OfflineInspection &&
                    !methodDescriptor.LongRunningWait;
                if (methodDescriptor.OfflineInspection)
                {
                    dispatchGate = _inspectionDispatchGate;
                    int queueWaitMs = RemainingRequestMilliseconds(request.Meta.DeadlineUnixMs);
                    if (queueWaitMs <= 0 ||
                        !await dispatchGate.WaitAsync(queueWaitMs, activeRequest.Token))
                    {
                        throw new MainThreadDispatchTimeoutException(
                            "Request deadline expired while waiting in the server dispatch queue; the operation was not started.");
                    }
                    dispatchGateHeld = true;
                }

                Func<object> invoke = () =>
                {
                    if (!activeRequest.TryMarkRunning())
                    {
                        throw new RequestCancelledNotStartedException(
                            "Request was cancelled while queued and the debugger operation was not started.");
                    }
                    object invocationResult = null;
                    using (DebuggerRequestContext.Push(
                        request.Meta.DeadlineUnixMs,
                        request.Id,
                        request.Meta.Kind,
                        request.Meta.Target.DebugSessionGeneration,
                        methodDescriptor.RequiresStableDebugSession,
                        request.Meta.Target.BreakSequence,
                        request.Meta.Target.ContextEpoch,
                        methodDescriptor.RequiresStableDebugContext,
                        activeRequest.Cancellation.Token,
                        methodDescriptor.MayEvaluateUserCode,
                        activeRequest.MarkSideEffectIssued,
                        (operation, stage) =>
                        {
                            activeRequest.MarkEffectStage(operation, stage);
                            UiOperationCircuit.ProcessWide.RecordEffectStage(
                                request.Id,
                                operation,
                                stage);
                        },
                        methodDescriptor.Name))
                    {
                        if (!methodDescriptor.LongRunningWait)
                        {
                            _debuggerService.EnsureRequiredState(methodDescriptor.RequiredState);
                        }
                        try
                        {
                            Func<Func<object>, object> phaseExecutor = null;
                            if (methodDescriptor.LongRunningWait)
                            {
                                phaseExecutor = phase => ExecuteLongRunningActorPhase(
                                    phase,
                                    request,
                                    methodDescriptor,
                                    activeRequest);
                            }
                            invocationResult = HandleMethod(request, clientLane, phaseExecutor);
                            ThrowIfOperationFailed(invocationResult);
                        }
                        finally
                        {
                            sideEffectIssued = DebuggerRequestContext.SideEffectIssued;
                            if (invocationResult is DebugStatusResult debugStatus)
                            {
                                DebuggerRequestContext.RebaseDebuggerIdentity(
                                    debugStatus.DebugSessionGeneration,
                                    debugStatus.BreakSequence,
                                    debugStatus.ContextEpoch);
                            }
                            else if (methodDescriptor.SessionRefresh &&
                                !DebuggerRequestContext.HasCompletionCandidate)
                            {
                                _debuggerService.RebaseRequestToCurrentDebuggerIdentity();
                            }
                            completionIdentity = CreateServerIdentity(
                                DebuggerRequestContext.CaptureCompletionIdentity());
                        }
                    }
                    return invocationResult;
                };

                object result = null;
                Task<object> boundedInvocation = null;
                string pendingStage = null;
                string queueName = null;
                if (useDebugSessionActor)
                {
                    boundedInvocation = _debugSessionActor.EnqueueAsync(
                        invoke,
                        request.Meta.DeadlineUnixMs,
                        activeRequest.Token);
                    pendingStage = "actor_invocation_pending";
                    queueName = "debugger actor";
                }
                else if (methodDescriptor.LongRunningWait)
                {
                    boundedInvocation = Task.Run(invoke, activeRequest.Token);
                    pendingStage = "long_wait_invocation_pending";
                    queueName = "long-running operation worker";
                }

                if (boundedInvocation != null)
                {
                    int remainingMs = RemainingRequestMilliseconds(request.Meta.DeadlineUnixMs);
                    Task completed;
                    using (var deadlineCancellation = new CancellationTokenSource())
                    {
                        Task deadlineTask = Task.Delay(Math.Max(0, remainingMs), deadlineCancellation.Token);
                        completed = await Task.WhenAny(boundedInvocation, deadlineTask);
                        if (ReferenceEquals(completed, boundedInvocation))
                        {
                            deadlineCancellation.Cancel();
                        }
                    }
                    if (ReferenceEquals(completed, boundedInvocation) || boundedInvocation.IsCompleted)
                    {
                        result = await boundedInvocation;
                    }
                    else if (activeRequest.TryCancelIfNotStarted())
                    {
                        ObserveFault(boundedInvocation);
                        response = SerializeResponse(CreateFailureResponse(
                            request.Id,
                            "DEADLINE_EXCEEDED_NOT_STARTED",
                            $"Request deadline expired before the {queueName} started the operation.",
                            null,
                            methodDescriptor.LongRunningWait ? "long_wait_queue" : "actor_queue"), completionIdentity);
                    }
                    else
                    {
                        activeRequest.MarkOutcomePending();
                        deferredInvocation = boundedInvocation;
                        bool acceptedSideEffect = sideEffectIssued || activeRequest.SideEffectIssued;
                        response = SerializeResponse(CreateFailureResponse(
                            request.Id,
                            isCommand ? "OUTCOME_UNKNOWN" : "DEADLINE_EXCEEDED",
                            isCommand
                                ? "The command is still executing in Visual Studio after the response deadline. Its request id remains outcome_pending; query get_request_status for the definitive final response and do not retry it with a new id."
                                : "The query is still finishing in Visual Studio after its response deadline.",
                            null,
                            pendingStage,
                            acceptedSideEffect), completionIdentity);
                    }
                }
                else
                {
                    result = invoke();
                }
                if (response == null)
                {
                    response = SerializeResponse(
                        DebugResponse.Success(request.Id, result),
                        completionIdentity,
                        request);
                }
            }
            catch (ParameterValidationException ex)
            {
                response = SerializeResponse(DebugResponse.Failure(request.Id, "INVALID_ARGUMENT", ex.Message), completionIdentity);
            }
            catch (UnsupportedEvaluationPolicyException ex)
            {
                response = SerializeResponse(DebugResponse.Failure(
                    request.Id,
                    "EVALUATION_POLICY_NOT_SUPPORTED",
                    ex.Message), completionIdentity);
            }
            catch (DebuggerStateRequirementException ex)
            {
                response = SerializeResponse(DebugResponse.Failure(request.Id, ex.Code, ex.Message), completionIdentity);
            }
            catch (DebuggerOperationFailedException ex)
            {
                DebugResponse failure = CreateFailureResponse(
                    request.Id, ex.Code, ex.Message, ex.Details, "operation_result");
                response = SerializeResponse(failure, completionIdentity);
            }
            catch (NotSupportedException ex)
            {
                response = SerializeResponse(DebugResponse.Failure(
                    request.Id,
                    "ENGINE_NOT_SUPPORTED",
                    string.IsNullOrWhiteSpace(ex.Message)
                        ? $"The active Visual Studio debugger engine does not support '{request.Method}'."
                        : ex.Message), completionIdentity);
            }
            catch (DebuggerDispatchPoisonedException ex)
            {
                response = SerializeResponse(CreateFailureResponse(
                    request.Id,
                    "DEBUGGER_DISPATCH_POISONED",
                    ex.Message,
                    CreateDispatcherHealthDetails(ex.Health),
                    "dispatcher_poisoned",
                    ex.Health?.SideEffectIssued == true), completionIdentity);
            }
            catch (MainThreadDispatchTimeoutException ex)
            {
                string code = RequestOutcomePolicy.ClassifyNotStartedFailure(
                    isCommand, sideEffectIssued, "DEADLINE_EXCEEDED_NOT_STARTED");
                response = SerializeResponse(CreateFailureResponse(
                    request.Id,
                    code,
                    code == "OUTCOME_UNKNOWN"
                        ? "The command entered the debugger actor before a later UI dispatch timed out; its final outcome is unknown. Query get_request_status before retrying."
                        : ex.Message,
                    null,
                    "ui_dispatch",
                    sideEffectIssued), completionIdentity);
            }
            catch (StaleDebugSessionException ex)
            {
                response = SerializeResponse(DebugResponse.Failure(request.Id, "STALE_DEBUG_SESSION", ex.Message), completionIdentity);
            }
            catch (StaleDebugContextException ex)
            {
                response = SerializeResponse(DebugResponse.Failure(request.Id, "STALE_DEBUG_CONTEXT", ex.Message), completionIdentity);
            }
            catch (DebugContextNotFoundException ex)
            {
                response = SerializeResponse(DebugResponse.Failure(request.Id, "DEBUG_CONTEXT_NOT_FOUND", ex.Message), completionIdentity);
            }
            catch (ResourceLimitExceededException ex)
            {
                response = SerializeResponse(DebugResponse.Failure(request.Id, "RESOURCE_LIMIT_EXCEEDED", ex.Message), completionIdentity);
            }
            catch (TimeoutException ex)
            {
                string code = isCommand && sideEffectIssued ? "OUTCOME_UNKNOWN" : "DEADLINE_EXCEEDED";
                response = SerializeResponse(CreateFailureResponse(
                    request.Id,
                    code,
                    code == "OUTCOME_UNKNOWN"
                        ? "The command was accepted before the operation timed out; query get_request_status before retrying."
                        : ex.Message,
                    null,
                    "operation_timeout",
                    sideEffectIssued), completionIdentity);
            }
            catch (RequestCancelledNotStartedException ex)
            {
                string code = RequestOutcomePolicy.ClassifyNotStartedFailure(
                    isCommand, sideEffectIssued, "CANCELLED_NOT_STARTED");
                response = SerializeResponse(CreateFailureResponse(
                    request.Id,
                    code,
                    code == "OUTCOME_UNKNOWN"
                        ? "The command entered the debugger actor before a later dispatch was cancelled; its final outcome is unknown. Query get_request_status before retrying."
                        : ex.Message,
                    null,
                    "dispatch_cancellation",
                    sideEffectIssued), completionIdentity);
            }
            catch (OperationCanceledException)
            {
                string code = isCommand && sideEffectIssued ? "OUTCOME_UNKNOWN" : "CANCELLED_NOT_STARTED";
                string message = code == "OUTCOME_UNKNOWN"
                    ? "The command was accepted and cancellation was requested; query get_request_status before retrying."
                    : "The request was cancelled before the debugger operation started.";
                response = SerializeResponse(CreateFailureResponse(
                    request.Id,
                    code,
                    message,
                    null,
                    "operation_cancellation",
                    sideEffectIssued), completionIdentity);
            }
            catch (Exception ex)
            {
                _log($"Error processing method {request.Method}: {ex.GetType().Name}");
                string code = isCommand && sideEffectIssued ? "OUTCOME_UNKNOWN" : "INTERNAL_ERROR";
                response = SerializeResponse(CreateFailureResponse(
                    request.Id,
                    code,
                    code == "OUTCOME_UNKNOWN"
                        ? "The command was accepted but its final outcome could not be determined; query get_request_status before retrying."
                        : "Debugger operation failed. See the Visual Studio Activity Log for details.",
                    null,
                    "internal_failure",
                    sideEffectIssued), completionIdentity);
            }
            finally
            {
                if (dispatchGateHeld)
                {
                    dispatchGate.Release();
                }
                if (deferredInvocation != null)
                {
                    CompleteAfterDeferredInvocation(
                        deferredInvocation,
                        request,
                        request.Id,
                        activeRequestKey,
                        activeRequest,
                        isCommand ? commandLease : null,
                        () => completionIdentity,
                        () => sideEffectIssued || activeRequest.SideEffectIssued,
                        operationAdmissionLease);
                    operationAdmissionLease = null;
                }
                else
                {
                    if (isCommand && response != null)
                    {
                        _commandResults.Complete(commandLease, response);
                    }
                    else if (!isCommand && response != null)
                    {
                        _queryResults.Complete(request.Id, clientLane, response);
                    }
                    CompleteActiveRequest(activeRequestKey, activeRequest);
                }
                operationAdmissionLease?.Dispose();
            }
            return response;
        }

        [SuppressMessage(
            "Usage",
            "VSTHRD002",
            Justification = "A split-phase wait runs on a dedicated thread-pool task; this synchronous boundary serializes only its short actor phase and never blocks the VS UI thread.")]
        private object ExecuteLongRunningActorPhase(
            Func<object> phase,
            DebugRequest request,
            DebugMethodDescriptor descriptor,
            ActiveRequest activeRequest)
        {
            DebuggerIdentitySnapshot expected = DebuggerRequestContext.CaptureIdentity() ??
                new DebuggerIdentitySnapshot
                {
                    DebugSessionGeneration = request.Meta.Target.DebugSessionGeneration,
                    BreakSequence = request.Meta.Target.BreakSequence,
                    ContextEpoch = request.Meta.Target.ContextEpoch,
                };
            ActorPhaseOutcome outcome = _debugSessionActor.EnqueueAsync(
                () =>
                {
                    var captured = new ActorPhaseOutcome();
                    using (DebuggerRequestContext.Push(
                        request.Meta.DeadlineUnixMs,
                        request.Id,
                        request.Meta.Kind,
                        expected.DebugSessionGeneration,
                        descriptor.RequiresStableDebugSession,
                        expected.BreakSequence,
                        expected.ContextEpoch,
                        descriptor.RequiresStableDebugContext,
                        activeRequest.Cancellation.Token,
                        descriptor.MayEvaluateUserCode,
                        activeRequest.MarkSideEffectIssued,
                        (operation, stage) =>
                        {
                            activeRequest.MarkEffectStage(operation, stage);
                            UiOperationCircuit.ProcessWide.RecordEffectStage(
                                request.Id,
                                operation,
                                stage);
                        },
                        descriptor.Name))
                    {
                        try
                        {
                            captured.Value = phase();
                        }
                        catch (Exception ex)
                        {
                            captured.Error = ex;
                        }
                        finally
                        {
                            captured.SideEffectIssued = DebuggerRequestContext.SideEffectIssued;
                            captured.EffectOperation = DebuggerRequestContext.EffectOperation;
                            captured.EffectStage = DebuggerRequestContext.EffectStage;
                            captured.CompletionIdentity = DebuggerRequestContext.CaptureCompletionIdentity();
                        }
                    }
                    return captured;
                },
                request.Meta.DeadlineUnixMs,
                activeRequest.Token).GetAwaiter().GetResult();

            if (outcome.SideEffectIssued)
            {
                CommandEffectExecutor.RecordIssued(
                    string.IsNullOrWhiteSpace(outcome.EffectOperation)
                        ? "long_running_actor_phase"
                        : outcome.EffectOperation);
                if (string.Equals(outcome.EffectStage, "confirmed", StringComparison.Ordinal))
                {
                    CommandEffectExecutor.RecordConfirmed(outcome.EffectOperation);
                }
            }
            if (outcome.CompletionIdentity != null)
            {
                DebuggerRequestContext.RebaseDebuggerIdentity(
                    outcome.CompletionIdentity.DebugSessionGeneration,
                    outcome.CompletionIdentity.BreakSequence,
                    outcome.CompletionIdentity.ContextEpoch);
            }
            if (outcome.Error != null) ExceptionDispatchInfo.Capture(outcome.Error).Throw();
            return outcome.Value;
        }

        private sealed class ActorPhaseOutcome
        {
            internal object Value { get; set; }
            internal Exception Error { get; set; }
            internal bool SideEffectIssued { get; set; }
            internal string EffectOperation { get; set; }
            internal string EffectStage { get; set; }
            internal DebuggerIdentitySnapshot CompletionIdentity { get; set; }
        }

        private static int RemainingRequestMilliseconds(long deadlineUnixMs)
        {
            if (deadlineUnixMs <= 0)
            {
                return int.MaxValue;
            }
            long nowUnixMs = (long)(DateTime.UtcNow -
                new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            long remaining = deadlineUnixMs - nowUnixMs;
            return remaining <= 0 ? 0 : (int)Math.Min(int.MaxValue, remaining);
        }

        private static string CreateClientLane(DebugRequestMetadata meta)
        {
            return meta.ClientProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" +
                meta.ClientProcessStartTimeUtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string CreateClientRequestKey(string clientLane, string requestId)
        {
            return ClientLaneRecovery.CreateRequestKey(clientLane, requestId);
        }

        private static DebugResponse CreateFailureResponse(
            string requestId,
            string code,
            string message,
            Dictionary<string, object> details,
            string stage,
            bool sideEffectIssued = false)
        {
            DebugResponse response = DebugResponse.Failure(requestId, code, message);
            if (!string.Equals(code, "OUTCOME_UNKNOWN", StringComparison.Ordinal))
            {
                return response;
            }

            response.Error.OutcomeUnknown = true;
            response.Error.RequestId = requestId;
            response.Error.Details = details == null
                ? new Dictionary<string, object>()
                : new Dictionary<string, object>(details, StringComparer.Ordinal);
            if (!sideEffectIssued &&
                response.Error.Details.TryGetValue("sideEffectIssued", out object issuedValue) &&
                issuedValue is bool issued && issued)
            {
                sideEffectIssued = true;
            }
            response.Error.Details["sideEffectIssued"] = sideEffectIssued;
            response.Error.Details["operationStarted"] = true;
            response.Error.Details["stage"] = stage;
            return response;
        }

        private string EnforceMethodResultLimit(
            DebugRequest request,
            string response,
            DebugServerIdentity completionIdentity)
        {
            var descriptor = DebugMethodCatalog.Get(request.Method);
            int responseBytes = Encoding.UTF8.GetByteCount(response);
            int effectiveLimit = descriptor == null
                ? MaxResponseMessageSize
                : Math.Min(descriptor.MaxResultBytes, MaxResponseMessageSize);
            if (responseBytes > effectiveLimit)
            {
                if (descriptor != null && string.Equals(descriptor.Kind, "command", StringComparison.Ordinal))
                {
                    DebugResponse compact = DebugResponse.Failure(
                        request.Id,
                        "RESULT_TRUNCATED_AFTER_COMMAND",
                        $"Command '{descriptor.Name}' completed, but its result exceeded the " +
                        $"{effectiveLimit}-byte response budget. The command was not replayed.");
                    compact.Error.RequestId = request.Id;
                    compact.Error.Details = CommandResponseCompactor.ExtractOutcome(
                        response,
                        descriptor.Name,
                        responseBytes);
                    return SerializeResponse(compact, completionIdentity);
                }

                return SerializeResponse(DebugResponse.Failure(
                    request.Id,
                    "RESOURCE_LIMIT_EXCEEDED",
                    $"Method '{descriptor?.Name ?? request.Method}' exceeded its {effectiveLimit}-byte response budget."),
                    completionIdentity);
            }
            return response;
        }

        private static void ThrowIfOperationFailed(object result)
        {
            if (!(result is IOperationResult operation) || operation.Success ||
                OperationResultPolicy.ShouldReturnStructuredResult(result))
            {
                return;
            }

            string message = string.IsNullOrWhiteSpace(operation.Message)
                ? "Visual Studio rejected the debugger operation."
                : operation.Message;
            string code = string.IsNullOrWhiteSpace(operation.ErrorCode)
                ? "DEBUG_OPERATION_FAILED"
                : operation.ErrorCode;
            bool commandIssued = OperationResultPolicy.WasCommandIssued(result);
            code = OperationResultPolicy.NormalizeFailureCode(result, code);
            throw new DebuggerOperationFailedException(code, message, new Dictionary<string, object>
            {
                ["commandIssued"] = commandIssued,
                ["sideEffectIssued"] = commandIssued,
                ["success"] = operation.Success,
                ["errorCode"] = operation.ErrorCode,
                ["resultType"] = result.GetType().Name,
            });
        }

        private bool IsControlPlaneRequest(string requestJson)
        {
            try
            {
                DebugRequest request = _serializer.Deserialize<DebugRequest>(requestJson);
                return DebugMethodCatalog.Get(request?.Method)?.ControlPlane == true;
            }
            catch
            {
                return true;
            }
        }

        private bool IsOfflineInspectionRequest(string requestJson)
        {
            try
            {
                DebugRequest request = _serializer.Deserialize<DebugRequest>(requestJson);
                return DebugMethodCatalog.Get(request?.Method)?.OfflineInspection == true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryRegisterActiveRequest(string requestId, ActiveRequest request)
        {
            lock (_activeRequestsLock)
            {
                if (_activeRequests.ContainsKey(requestId))
                {
                    return false;
                }
                _activeRequests.Add(requestId, request);
                return true;
            }
        }

        private void CompleteActiveRequest(string requestId, ActiveRequest request)
        {
            lock (_activeRequestsLock)
            {
                if (_activeRequests.TryGetValue(requestId, out ActiveRequest current) &&
                    ReferenceEquals(current, request))
                {
                    _activeRequests.Remove(requestId);
                }
            }
            request.Complete();
        }

        private void CompleteAfterDeferredInvocation(
            Task<object> invocation,
            DebugRequest request,
            string requestId,
            string activeRequestKey,
            ActiveRequest activeRequest,
            CommandReservationLease commandLease,
            Func<DebugServerIdentity> getCompletionIdentity,
            Func<bool> getSideEffectIssued,
            IDisposable completionLease)
        {
            _ = FinalizeDeferredInvocationAsync(
                invocation,
                request,
                requestId,
                activeRequestKey,
                activeRequest,
                commandLease,
                getCompletionIdentity,
                getSideEffectIssued,
                completionLease);
        }

        [SuppressMessage(
            "Usage",
            "VSTHRD003",
            Justification = "This continuation is the explicit lifetime owner of an actor task started by ProcessRequestAsync after the transport deadline expires.")]
        private async Task FinalizeDeferredInvocationAsync(
            Task<object> invocation,
            DebugRequest request,
            string requestId,
            string activeRequestKey,
            ActiveRequest activeRequest,
            CommandReservationLease commandLease,
            Func<DebugServerIdentity> getCompletionIdentity,
            Func<bool> getSideEffectIssued,
            IDisposable completionLease)
        {
            string finalResponse;
            try
            {
                object result = await invocation.ConfigureAwait(false);
                finalResponse = SerializeResponse(
                    DebugResponse.Success(requestId, result),
                    getCompletionIdentity(),
                    request);
            }
            catch (Exception exception)
            {
                Exception error = exception is AggregateException aggregate
                    ? aggregate.GetBaseException()
                    : exception;
                finalResponse = SerializeDeferredFailure(
                    request,
                    error,
                    getCompletionIdentity(),
                    getSideEffectIssued());
            }
            finally
            {
                // Completion is performed below after the definitive response is built.
            }
            try
            {
                if (commandLease != null)
                {
                    _commandResults.Complete(commandLease, finalResponse);
                }
                else
                {
                    _queryResults.Complete(
                        requestId,
                        CreateClientLane(request.Meta),
                        finalResponse);
                }
                CompleteActiveRequest(activeRequestKey, activeRequest);
            }
            finally
            {
                completionLease?.Dispose();
            }
        }

        private string SerializeDeferredFailure(
            DebugRequest request,
            Exception exception,
            DebugServerIdentity completionIdentity,
            bool sideEffectIssued)
        {
            DebugResponse failure;
            if (exception is ParameterValidationException parameter)
                failure = DebugResponse.Failure(request.Id, "INVALID_ARGUMENT", parameter.Message);
            else if (exception is UnsupportedEvaluationPolicyException evaluation)
                failure = DebugResponse.Failure(request.Id, "EVALUATION_POLICY_NOT_SUPPORTED", evaluation.Message);
            else if (exception is DebuggerStateRequirementException state)
                failure = DebugResponse.Failure(request.Id, state.Code, state.Message);
            else if (exception is DebuggerOperationFailedException operation)
                failure = CreateFailureResponse(
                    request.Id, operation.Code, operation.Message, operation.Details, "operation_result");
            else if (exception is NotSupportedException unsupported)
                failure = DebugResponse.Failure(
                    request.Id,
                    "ENGINE_NOT_SUPPORTED",
                    string.IsNullOrWhiteSpace(unsupported.Message)
                        ? $"The active Visual Studio debugger engine does not support '{request.Method}'."
                        : unsupported.Message);
            else if (exception is DebuggerDispatchPoisonedException poisoned)
                failure = CreateFailureResponse(
                    request.Id,
                    "DEBUGGER_DISPATCH_POISONED",
                    poisoned.Message,
                    CreateDispatcherHealthDetails(poisoned.Health),
                    "dispatcher_poisoned",
                    poisoned.Health?.SideEffectIssued == true);
            else if (exception is StaleDebugSessionException)
                failure = DebugResponse.Failure(request.Id, "STALE_DEBUG_SESSION", exception.Message);
            else if (exception is StaleDebugContextException)
                failure = DebugResponse.Failure(request.Id, "STALE_DEBUG_CONTEXT", exception.Message);
            else if (exception is DebugContextNotFoundException)
                failure = DebugResponse.Failure(request.Id, "DEBUG_CONTEXT_NOT_FOUND", exception.Message);
            else if (exception is ResourceLimitExceededException)
                failure = DebugResponse.Failure(request.Id, "RESOURCE_LIMIT_EXCEEDED", exception.Message);
            else if (exception is RequestCancelledNotStartedException)
                failure = CreateFailureResponse(
                    request.Id,
                    sideEffectIssued ? "OUTCOME_UNKNOWN" : "CANCELLED_NOT_STARTED",
                    exception.Message,
                    null,
                    "deferred_cancellation",
                    sideEffectIssued);
            else if (exception is MainThreadDispatchTimeoutException || exception is TimeoutException)
                failure = CreateFailureResponse(
                    request.Id,
                    sideEffectIssued ? "OUTCOME_UNKNOWN" : "DEADLINE_EXCEEDED",
                    exception.Message,
                    null,
                    "deferred_timeout",
                    sideEffectIssued);
            else if (exception is OperationCanceledException)
                failure = CreateFailureResponse(
                    request.Id,
                    sideEffectIssued ? "OUTCOME_UNKNOWN" : "CANCELLED_NOT_STARTED",
                    sideEffectIssued
                        ? "The accepted command was cancelled after a side effect may have been issued."
                        : "The request was cancelled before the debugger operation started.",
                    null,
                    "deferred_cancellation",
                    sideEffectIssued);
            else
            {
                _log($"Deferred debugger invocation {request.Method} failed: {exception.GetType().Name}");
                failure = CreateFailureResponse(
                    request.Id,
                    sideEffectIssued ? "OUTCOME_UNKNOWN" : "INTERNAL_ERROR",
                    sideEffectIssued
                        ? "The complete debugger invocation ended after a side effect may have been issued, but its effect could not be proven."
                        : "Debugger operation failed. See the Visual Studio Activity Log for details.",
                    null,
                    "deferred_internal_failure",
                    sideEffectIssued);
            }
            return SerializeResponse(failure, completionIdentity);
        }

        private static Dictionary<string, object> CreateDispatcherHealthDetails(
            DispatcherHealthSnapshot health)
        {
            if (health == null) return null;
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["state"] = health.State,
                ["activeRequestId"] = health.ActiveRequestId,
                ["activeMethod"] = health.ActiveMethod,
                ["uiAdmittedAtUtc"] = health.UiAdmissionUtc?.ToString("O"),
                ["poisonedAtUtc"] = health.PoisonedUtc?.ToString("O"),
                ["elapsedMs"] = health.ElapsedMs,
                ["poisonAfterMs"] = health.PoisonAfterMs,
                ["sideEffectIssued"] = health.SideEffectIssued,
                ["effectOperation"] = health.EffectOperation,
                ["effectStage"] = health.EffectStage,
                ["recoveryAction"] = "restart_visual_studio",
            };
        }

        private static void ObserveFault(Task task)
        {
            _ = task.ContinueWith(
                completed =>
                {
                    if (completed.IsFaulted)
                    {
                        AggregateException ignored = completed.Exception;
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private string ComputeCommandFingerprint(DebugRequest request)
        {
            var target = request.Meta.Target;
            string material = string.Join("\n", new[]
            {
                request.Method.ToLowerInvariant(),
                CanonicalJson.Serialize(request.Params ?? new Dictionary<string, object>()),
                target.ServerInstanceId ?? string.Empty,
                target.DebugSessionGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
                target.BreakSequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                target.ContextEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
            using (var sha256 = SHA256.Create())
            {
                return Convert.ToBase64String(sha256.ComputeHash(Encoding.UTF8.GetBytes(material)));
            }
        }

        private CommandRequestStatusResult GetCommandRequestStatus(string clientLane, string requestId)
        {
            if (!Guid.TryParseExact(requestId, "D", out Guid parsedRequestId) ||
                !string.Equals(parsedRequestId.ToString("D"), requestId, StringComparison.Ordinal))
            {
                throw new ParameterValidationException("Parameter 'requestId' must be a canonical lowercase UUID.");
            }

            lock (_activeRequestsLock)
            {
                if (ClientLaneRecovery.TryFindDeadLaneRequest(
                    _activeRequests,
                    clientLane,
                    requestId,
                    IsClientLaneAlive,
                    out _,
                    out ActiveRequest active))
                {
                    ActiveRequestSnapshot snapshot = active.Snapshot();
                    return new CommandRequestStatusResult
                    {
                        RequestId = requestId,
                        State = snapshot.State,
                        Kind = snapshot.Kind,
                        EnqueuedAtUtc = snapshot.EnqueuedUtc.ToString("O"),
                        StartedAtUtc = snapshot.StartedUtc?.ToString("O"),
                        CancellationRequested = snapshot.CancellationRequested,
                        SideEffectIssued = snapshot.SideEffectIssued,
                        EffectOperation = snapshot.EffectOperation,
                        EffectStage = snapshot.EffectStage,
                    };
                }
            }

            if (_commandResults.TryGetById(
                clientLane,
                requestId,
                out string response,
                out DateTime completedUtc,
                out bool resultEvicted))
            {
                return new CommandRequestStatusResult
                {
                    RequestId = requestId,
                    Kind = "command",
                    State = resultEvicted ? "completed_result_evicted" : "completed",
                    CompletedAtUtc = completedUtc.ToString("O"),
                    Response = resultEvicted
                        ? null
                        : _serializer.DeserializeObject(response) as Dictionary<string, object>,
                };
            }

            if (_queryResults.TryGet(
                requestId,
                out string queryResponse,
                out DateTime queryCompletedUtc))
            {
                return new CommandRequestStatusResult
                {
                    RequestId = requestId,
                    Kind = "query",
                    State = "completed",
                    CompletedAtUtc = queryCompletedUtc.ToString("O"),
                    Response = _serializer.DeserializeObject(queryResponse) as Dictionary<string, object>,
                };
            }

            return new CommandRequestStatusResult
            {
                RequestId = requestId,
                State = "not_found",
            };
        }

        private CommandRequestStatusResult CancelRequest(string clientLane, string requestId)
        {
            if (!Guid.TryParseExact(requestId, "D", out Guid parsedRequestId) ||
                !string.Equals(parsedRequestId.ToString("D"), requestId, StringComparison.Ordinal))
            {
                throw new ParameterValidationException("Parameter 'requestId' must be a canonical lowercase UUID.");
            }

            ActiveRequest activeRequest = null;
            lock (_activeRequestsLock)
            {
                if (ClientLaneRecovery.TryFindDeadLaneRequest(
                    _activeRequests,
                    clientLane,
                    requestId,
                    IsClientLaneAlive,
                    out _,
                    out ActiveRequest active))
                {
                    activeRequest = active;
                }
            }
            if (activeRequest != null)
            {
                // Cancellation callbacks may run synchronously. Never invoke them while the
                // active-request registry lock is held.
                bool cancellationIssued = activeRequest.TryCancel(
                    () => CommandEffectExecutor.RecordIssued("request.cancel"));
                ActiveRequestSnapshot snapshot = activeRequest.Snapshot();
                return new CommandRequestStatusResult
                {
                    RequestId = requestId,
                    State = snapshot.State,
                    Kind = snapshot.Kind,
                    EnqueuedAtUtc = snapshot.EnqueuedUtc.ToString("O"),
                    StartedAtUtc = snapshot.StartedUtc?.ToString("O"),
                    CancellationRequested = snapshot.CancellationRequested,
                    CancellationIssued = cancellationIssued,
                    SideEffectIssued = snapshot.SideEffectIssued,
                    EffectOperation = snapshot.EffectOperation,
                    EffectStage = snapshot.EffectStage,
                };
            }
            return GetCommandRequestStatus(clientLane, requestId);
        }

    }

    internal sealed class DebuggerOperationFailedException : Exception
    {
        internal DebuggerOperationFailedException(
            string code,
            string message,
            Dictionary<string, object> details = null) : base(message)
        {
            Code = code;
            Details = details;
        }

        internal string Code { get; }
        internal Dictionary<string, object> Details { get; }
    }
}
