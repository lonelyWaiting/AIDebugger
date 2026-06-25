
package com.vsdebugger.rider

import com.github.icedland.iced.x86.FlowControl
import com.github.icedland.iced.x86.dec.ByteArrayCodeReader
import com.github.icedland.iced.x86.dec.Decoder
import com.github.icedland.iced.x86.fmt.StringOutput
import com.github.icedland.iced.x86.fmt.intel.IntelFormatter
import com.intellij.analysis.problemsView.FileProblem
import com.intellij.analysis.problemsView.ProblemsCollector
import com.intellij.execution.ui.ConsoleView
import com.intellij.openapi.application.ApplicationInfo
import com.intellij.openapi.project.Project
import com.intellij.openapi.project.ProjectManager
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.openapi.vfs.VirtualFileManager
import com.intellij.openapi.editor.colors.TextAttributesKey
import com.intellij.ui.ColoredText
import com.intellij.ui.ColoredTextContainer
import com.intellij.ui.SimpleTextAttributes
import com.intellij.xdebugger.XDebugSession
import com.intellij.xdebugger.XDebuggerManager
import com.intellij.xdebugger.XSourcePosition
import com.intellij.xdebugger.breakpoints.XBreakpoint
import com.intellij.xdebugger.breakpoints.XBreakpointProperties
import com.intellij.xdebugger.breakpoints.XBreakpointType
import com.intellij.xdebugger.breakpoints.XLineBreakpoint
import com.intellij.xdebugger.breakpoints.XLineBreakpointType
import com.intellij.xdebugger.evaluation.XDebuggerEvaluator
import com.intellij.xdebugger.frame.XCompositeNode
import com.intellij.xdebugger.frame.XExecutionStack
import com.intellij.xdebugger.frame.XFullValueEvaluator
import com.intellij.xdebugger.frame.XStackFrame
import com.intellij.xdebugger.frame.XSuspendContext
import com.intellij.xdebugger.frame.XValue
import com.intellij.xdebugger.frame.XValueContainer
import com.intellij.xdebugger.frame.XValueChildrenList
import com.intellij.xdebugger.frame.XValueGroup
import com.intellij.xdebugger.frame.XDebuggerTreeNodeHyperlink
import com.intellij.xdebugger.frame.XValueNode
import com.intellij.xdebugger.frame.XValuePlace
import com.intellij.xdebugger.frame.presentation.XValuePresentation
import com.intellij.xdebugger.impl.XSourcePositionImpl
import com.intellij.xdebugger.impl.breakpoints.XExpressionImpl
import com.vsdebugger.rider.protocol.*
import java.io.File
import java.nio.charset.StandardCharsets
import java.time.Instant
import java.util.Base64
import java.util.Locale
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference
import javax.swing.Icon

class DebuggerService {
    private var pipeName: String = ""
    private var selectedThreadId: Int? = null
    private var selectedFrameIndex: Int? = null
    private val snapshotMaxAttempts = 2
    private val snapshotRetryDelayMs = 40L
    private val variableExpansionMaxNodes = 128
    private val variableExpansionBudgetMs = 1500L
    private val variableExpansionMaxDepth = 4
    private val threadEnumerationBudgetMs = 2500L
    private val threadFrameExpansionMaxNodes = 128
    private val threadFramesMaxPerThread = 16

    fun setPipeName(value: String) {
        pipeName = value
    }

    fun handleMethod(request: DebugRequest): Any? {
        val method = request.method?.lowercase(Locale.ROOT) ?: return null
        return when (method) {
            "get_debug_status" -> getDebugStatus()
            "get_callstack" -> getCallStack()
            "get_locals" -> {
                val depth = getParam(request.params, "depth", 2)
                getLocals(depth)
            }
            "get_threads" -> {
                val includeFrames = getParam(request.params, "includeFrames", true)
                val maxFramesPerThread = getParam(request.params, "maxFramesPerThread", -1)
                val maxNodes = getParam(request.params, "maxNodes", -1)
                val budgetMs = getParam(request.params, "budgetMs", -1)
                getThreads(includeFrames, maxFramesPerThread, maxNodes, budgetMs)
            }
            "get_modules" -> getModules()
            "get_exception" -> getException()
            "evaluate_expression" -> {
                val expression = getParam(request.params, "expression", "")
                val expand = getParam(request.params, "expandChildren", false)
                val maxDepth = getParam(request.params, "maxDepth", 2)
                val allowSideEffects = getParam(request.params, "allowSideEffects", false)
                evaluateExpression(expression, expand, maxDepth, allowSideEffects)
            }
            "evaluate_expressions_batch" -> {
                val expressions = getStringListParam(request.params, "expressions")
                val expand = getParam(request.params, "expandChildren", false)
                val maxDepth = getParam(request.params, "maxDepth", 2)
                val allowSideEffects = getParam(request.params, "allowSideEffects", false)
                evaluateExpressionsBatch(expressions, expand, maxDepth, allowSideEffects)
            }
            "capture_debug_bundle" -> {
                val expressions = getStringListParam(request.params, "expressions")
                val expand = getParam(request.params, "expandChildren", false)
                val maxDepth = getParam(request.params, "maxDepth", 2)
                val outputTailLines = getParam(request.params, "outputTailLines", 80)
                val includeLocals = getParam(request.params, "includeLocals", false)
                val includeModules = getParam(request.params, "includeModules", false)
                val includeRegisters = getParam(request.params, "includeRegisters", false)
                val includeOutput = getParam(request.params, "includeOutput", false)
                val includeThreads = getParam(request.params, "includeThreads", true)
                val includeException = getParam(request.params, "includeException", true)
                val allowSideEffects = getParam(request.params, "allowSideEffects", false)
                val threadIncludeFrames = getParam(request.params, "threadIncludeFrames", true)
                val threadMaxFramesPerThread = getParam(request.params, "threadMaxFramesPerThread", -1)
                val threadMaxNodes = getParam(request.params, "threadMaxNodes", -1)
                val threadBudgetMs = getParam(request.params, "threadBudgetMs", -1)
                captureDebugBundle(
                    expressions,
                    expand,
                    maxDepth,
                    outputTailLines,
                    includeLocals,
                    includeModules,
                    includeRegisters,
                    includeOutput,
                    includeThreads,
                    includeException,
                    allowSideEffects,
                    threadIncludeFrames,
                    threadMaxFramesPerThread,
                    threadMaxNodes,
                    threadBudgetMs
                )
            }
            "read_memory" -> {
                val address = getParam(request.params, "address", "")
                val size = getParam(request.params, "size", 256)
                readMemory(address, size)
            }
            "get_disassembly" -> {
                val linesBefore = getParam(request.params, "linesBefore", 20)
                val linesAfter = getParam(request.params, "linesAfter", 20)
                getDisassembly(linesBefore, linesAfter)
            }
            "get_disassembly_at" -> {
                val address = getParam(request.params, "address", "")
                val count = getParam(request.params, "count", 50)
                getDisassemblyAt(address, count)
            }
            "get_build_errors" -> getBuildErrors()
            "get_errors_only" -> getBuildErrors(errorsOnly = true)
            "get_warnings_only" -> getBuildErrors(warningsOnly = true)
            "get_full_context" -> {
                val threadIncludeFrames = getParam(request.params, "threadIncludeFrames", true)
                val threadMaxFramesPerThread = getParam(request.params, "threadMaxFramesPerThread", -1)
                val threadMaxNodes = getParam(request.params, "threadMaxNodes", -1)
                val threadBudgetMs = getParam(request.params, "threadBudgetMs", -1)
                getFullContext(threadIncludeFrames, threadMaxFramesPerThread, threadMaxNodes, threadBudgetMs)
            }
            "ping" -> PingResult(status = "ok", timestamp = Instant.now().toString())
            "get_instance_info" -> getInstanceInfo(getParam(request.params, "pipeName", ""))
            "continue" -> debugContinue()
            "continue_and_wait_for_break" -> {
                val expressions = getStringListParam(request.params, "expressions")
                val expand = getParam(request.params, "expandChildren", false)
                val maxDepth = getParam(request.params, "maxDepth", 2)
                val outputTailLines = getParam(request.params, "outputTailLines", 80)
                val timeoutMs = getParam(request.params, "timeoutMs", 120000)
                val pollIntervalMs = getParam(request.params, "pollIntervalMs", 200)
                val includeLocals = getParam(request.params, "includeLocals", false)
                val includeModules = getParam(request.params, "includeModules", false)
                val includeRegisters = getParam(request.params, "includeRegisters", false)
                val includeOutput = getParam(request.params, "includeOutput", false)
                val includeThreads = getParam(request.params, "includeThreads", true)
                val includeException = getParam(request.params, "includeException", true)
                val allowSideEffects = getParam(request.params, "allowSideEffects", false)
                val threadIncludeFrames = getParam(request.params, "threadIncludeFrames", true)
                val threadMaxFramesPerThread = getParam(request.params, "threadMaxFramesPerThread", -1)
                val threadMaxNodes = getParam(request.params, "threadMaxNodes", -1)
                val threadBudgetMs = getParam(request.params, "threadBudgetMs", -1)
                continueAndWaitForBreak(
                    expressions,
                    expand,
                    maxDepth,
                    outputTailLines,
                    timeoutMs,
                    pollIntervalMs,
                    includeLocals,
                    includeModules,
                    includeRegisters,
                    includeOutput,
                    includeThreads,
                    includeException,
                    allowSideEffects,
                    threadIncludeFrames,
                    threadMaxFramesPerThread,
                    threadMaxNodes,
                    threadBudgetMs
                )
            }
            "step_into" -> debugStepInto()
            "step_over" -> debugStepOver()
            "step_out" -> debugStepOut()
            "break" -> debugBreak()
            "stop" -> debugStop()
            "detach" -> debugDetach()
            "run_to_cursor" -> {
                val filePath = getParam(request.params, "filePath", "")
                val lineNumber = getParam(request.params, "lineNumber", 0)
                debugRunToCursor(filePath, lineNumber)
            }
            "set_next_statement" -> {
                val filePath = getParam(request.params, "filePath", "")
                val lineNumber = getParam(request.params, "lineNumber", 0)
                debugSetNextStatement(filePath, lineNumber)
            }
            "get_breakpoints" -> getBreakpoints()
            "set_breakpoint" -> {
                val filePath = getParam(request.params, "filePath", "")
                val lineNumber = getParam(request.params, "lineNumber", 0)
                val condition = getParam<String?>(request.params, "condition", null)
                setBreakpoint(filePath, lineNumber, condition)
            }
            "set_function_breakpoint" -> {
                val functionName = getParam(request.params, "functionName", "")
                val condition = getParam<String?>(request.params, "condition", null)
                setFunctionBreakpoint(functionName, condition)
            }
            "delete_breakpoint" -> {
                val filePath = getParam(request.params, "filePath", "")
                val lineNumber = getParam(request.params, "lineNumber", 0)
                deleteBreakpoint(filePath, lineNumber)
            }
            "delete_all_breakpoints" -> deleteAllBreakpoints()
            "enable_breakpoint" -> {
                val filePath = getParam(request.params, "filePath", "")
                val lineNumber = getParam(request.params, "lineNumber", 0)
                val enabled = getParam(request.params, "enabled", true)
                enableBreakpoint(filePath, lineNumber, enabled)
            }
            "get_registers" -> getRegisters()
            "add_watch" -> {
                val expression = getParam(request.params, "expression", "")
                addWatch(expression)
            }
            "search_memory" -> {
                val startAddress = getParam(request.params, "startAddress", "")
                val pattern = getParam(request.params, "pattern", "")
                val maxResults = getParam(request.params, "maxResults", 100)
                val searchSize = getParam(request.params, "searchSize", 0x10000)
                searchMemory(startAddress, pattern, maxResults, searchSize)
            }
            "switch_stack_frame" -> {
                val frameIndex = getParam(request.params, "frameIndex", 0)
                switchStackFrame(frameIndex)
            }
            "switch_thread" -> {
                val threadId = getParam(request.params, "threadId", 0)
                switchThread(threadId)
            }
            "get_output_window" -> {
                val pane = getParam(request.params, "pane", "Debug")
                val tailLines = getParam(request.params, "tailLines", 50)
                getOutputWindow(pane, tailLines)
            }
            "search_output" -> {
                val pattern = getParam(request.params, "pattern", "")
                val pane = getParam(request.params, "pane", "Debug")
                val isRegex = getParam(request.params, "isRegex", false)
                val contextLines = getParam(request.params, "contextLines", 2)
                val maxMatches = getParam(request.params, "maxMatches", 20)
                searchOutput(pattern, pane, isRegex, contextLines, maxMatches)
            }
            else -> throw UnsupportedOperationException("Unknown method: ${request.method}")
        }
    }

    private fun getDebugStatus(): DebugStatusResult {
        val session = currentSession() ?: return DebugStatusResult()
        val isPaused = session.isSuspended
        val isDebugging = !session.isSuspended || session.suspendContext != null
        val mode = if (isPaused) "Break" else if (isDebugging) "Running" else "Design"
        val location = getCurrentLocation(session)
        val processName = session.runProfile?.name
        val (stopReason, stopReasonRaw) = determineStopReason(session)

        return DebugStatusResult(
            isDebugging = isDebugging,
            isPaused = isPaused,
            debugMode = mode,
            currentProcess = processName,
            stopReason = stopReason,
            stopReasonRaw = stopReasonRaw,
            currentLocation = location
        )
    }

    private fun determineStopReason(session: XDebugSession): Pair<String, String?> {
        if (!session.isSuspended) {
            return "not_paused" to null
        }

        val rawReason = readRawStopReason(session)
        val mappedFromRaw = mapStopReason(rawReason)
        if (mappedFromRaw != "pause_unknown") {
            return mappedFromRaw to rawReason
        }

        if (tryHasCurrentBreakpoint(session)) {
            return "breakpoint" to rawReason
        }

        if (tryHasCurrentException(session)) {
            return "exception" to rawReason
        }

        return "pause_unknown" to rawReason
    }

    private fun readRawStopReason(session: XDebugSession): String? {
        val candidates = listOf("getSuspendReason", "getPauseReason", "getStopReason", "getCurrentReason")
        for (name in candidates) {
            try {
                val method = session.javaClass.methods.firstOrNull { it.name == name && it.parameterCount == 0 } ?: continue
                val value = method.invoke(session) ?: continue
                return value.toString()
            } catch (_: Exception) {
            }
        }
        return null
    }

    private fun mapStopReason(rawReason: String?): String {
        val normalized = rawReason?.trim()?.lowercase(Locale.ROOT) ?: return "pause_unknown"
        return when {
            "exception" in normalized -> "exception"
            "step" in normalized -> "step"
            "breakpoint" in normalized || "tracepoint" in normalized -> "breakpoint"
            "user" in normalized || "pause" in normalized || "break" in normalized -> "user_break"
            else -> "pause_unknown"
        }
    }

    private fun tryHasCurrentBreakpoint(session: XDebugSession): Boolean {
        val candidates = listOf("getCurrentBreakpoint", "getActiveBreakpoint", "getBreakpoint")
        for (name in candidates) {
            try {
                val method = session.javaClass.methods.firstOrNull { it.name == name && it.parameterCount == 0 } ?: continue
                val value = method.invoke(session)
                if (value != null) {
                    return true
                }
            } catch (_: Exception) {
            }
        }
        return false
    }

    private fun tryHasCurrentException(session: XDebugSession): Boolean {
        return try {
            val evaluator = getEvaluator(session) ?: return false
            val eval = evaluateValue(evaluator, "\$exception", session.currentPosition)
            eval.value != null
        } catch (_: Exception) {
            false
        }
    }

    private fun getCallStack(): CallStackResult {
        var bestEffort = CallStackResult(
            snapshotStatus = "unavailable",
            capability = "partial_frame_metadata"
        )

        for (attempt in 1..snapshotMaxAttempts) {
            val session = currentSession() ?: break
            if (!session.isSuspended) {
                bestEffort.snapshotAttempts = attempt
                bestEffort.snapshotStatus = "unavailable"
                return bestEffort
            }
            try {
                val suspendContext = session.suspendContext ?: break
                val tokenBefore = suspendSnapshotToken(session, suspendContext)
                val snapshot = buildCallStackResult(suspendContext)
                val currentContext = session.suspendContext
                val stable = currentContext != null &&
                    session.isSuspended &&
                    tokenBefore == suspendSnapshotToken(session, currentContext)

                snapshot.snapshotAttempts = attempt
                snapshot.snapshotStable = stable
                snapshot.snapshotStatus = if (stable) {
                    "ok"
                } else if (snapshot.frames.isNotEmpty()) {
                    "unstable"
                } else {
                    "unavailable"
                }
                bestEffort = snapshot
                if (stable) {
                    return snapshot
                }
            } catch (ex: Exception) {
                bestEffort.snapshotAttempts = attempt
                if (bestEffort.frames.isNotEmpty()) {
                    bestEffort.snapshotStatus = "unstable"
                }
            }
            if (attempt < snapshotMaxAttempts) {
                Thread.sleep(snapshotRetryDelayMs)
            }
        }

        return bestEffort
    }

    private fun getLocals(maxDepth: Int = 2): LocalsResult {
        val session = currentSession() ?: return LocalsResult()
        return buildLocalsResult(session, maxDepth)
    }

    private fun buildLocalsResult(session: XDebugSession, maxDepth: Int): LocalsResult {
        val frame = getSelectedFrame(session) ?: return LocalsResult()
        val budget = VariableExpansionBudget(variableExpansionMaxNodes, variableExpansionBudgetMs)
        val variables = collectVariables(frame, 0, maxDepth.coerceIn(0, variableExpansionMaxDepth), budget)
        return LocalsResult(
            frameIndex = selectedFrameIndex ?: 0,
            variables = variables.toMutableList(),
            partial = budget.isExceeded,
            error = budget.reason
        )
    }

    private fun getThreads(
        includeFrames: Boolean = true,
        maxFramesPerThread: Int = -1,
        maxNodes: Int = -1,
        budgetMs: Int = -1
    ): ThreadsResult {
        val normalizedMaxFramesPerThread = normalizeThreadFrameLimit(maxFramesPerThread)
        val normalizedMaxNodes = normalizeThreadNodeLimit(maxNodes)
        val normalizedBudgetMs = normalizeThreadBudgetMs(budgetMs)
        var bestEffort = ThreadsResult(
            snapshotStatus = "unavailable",
            capability = "partial_thread_metadata"
        )

        for (attempt in 1..snapshotMaxAttempts) {
            val session = currentSession() ?: return bestEffort
            if (!session.isSuspended) {
                bestEffort.snapshotAttempts = attempt
                bestEffort.snapshotStatus = "unavailable"
                return bestEffort
            }
            try {
                val suspendContext = session.suspendContext ?: return bestEffort
                val tokenBefore = suspendSnapshotToken(session, suspendContext)
                val snapshot = buildThreadsResult(
                    suspendContext,
                    includeFrames,
                    normalizedMaxFramesPerThread,
                    normalizedMaxNodes,
                    normalizedBudgetMs
                )
                val currentContext = session.suspendContext
                val stable = currentContext != null &&
                    session.isSuspended &&
                    tokenBefore == suspendSnapshotToken(session, currentContext)

                snapshot.snapshotAttempts = attempt
                snapshot.snapshotStable = stable
                snapshot.snapshotStatus = determineThreadsSnapshotStatus(snapshot, stable)
                bestEffort = snapshot
                if (stable) {
                    return snapshot
                }
            } catch (ex: Exception) {
                bestEffort.snapshotAttempts = attempt
                if (bestEffort.threads.isNotEmpty()) {
                    bestEffort.snapshotStatus = "unstable"
                }
            }
            if (attempt < snapshotMaxAttempts) {
                Thread.sleep(snapshotRetryDelayMs)
            }
        }

        return bestEffort
    }

    private fun getModules(): ModulesResult {
        val capability = "managed_appdomain_only"
        val session = currentSession() ?: return ModulesResult(capability = capability, error = "No debug session")
        val evaluator = getEvaluator(session) ?: return ModulesResult(capability = capability, error = "No evaluator available")
        val eval = evaluateValue(evaluator, "System.AppDomain.CurrentDomain.GetAssemblies()", session.currentPosition)
        if (eval.value == null) {
            return ModulesResult(capability = capability, error = eval.errorMessage ?: "Unable to enumerate modules")
        }

        val modules = mutableListOf<ModuleInfo>()
        val children = collectVariables(eval.value, 0, 2)
        children.forEachIndexed { index, variable ->
            val fullName = extractVariableChildValue(variable, "FullName")
            val location = extractVariableChildValue(variable, "Location", "CodeBase")
            val moduleName = fullName?.substringBefore(",")?.trim()
                ?: variable.value?.trim('"')
                ?: variable.name
            val version = Regex("Version=([^,]+)", RegexOption.IGNORE_CASE).find(fullName ?: "")
                ?.groupValues?.getOrNull(1)
            val path = location?.takeIf { it.contains(":\\") || it.contains("/") }
            modules.add(
                ModuleInfo(
                    name = moduleName,
                    path = path,
                    symbolsLoaded = !path.isNullOrBlank(),
                    order = index,
                    version = version,
                    isSystemModule = isSystemPath(path)
                )
            )
        }

        return ModulesResult(modules = modules, totalCount = modules.size, capability = capability)
    }

    private fun getException(): ExceptionResult {
        val defaultCapability = "limited_exception_details"
        val session = currentSession() ?: return ExceptionResult(hasException = false, capability = defaultCapability)
        val evaluator = getEvaluator(session) ?: return ExceptionResult(hasException = false, capability = defaultCapability)
        val result = evaluateExpression("\$exception", expandChildren = true, maxDepth = 3)
        if (!result.isValid || result.value == null) {
            return ExceptionResult(hasException = false, capability = defaultCapability)
        }

        val stackTrace = extractVariableValueByName(result.children, "StackTrace")
        val source = extractVariableValueByName(result.children, "Source")
        val hResult = extractVariableValueByName(result.children, "HResult")
        val inner = toInnerException(extractVariableByName(result.children, "InnerException"))
        val capability = if (!stackTrace.isNullOrBlank() || !source.isNullOrBlank() || !hResult.isNullOrBlank() || inner != null) {
            "expanded_exception_details"
        } else {
            defaultCapability
        }

        return ExceptionResult(
            hasException = true,
            capability = capability,
            type = result.type,
            message = result.value,
            stackTrace = stackTrace,
            source = source,
            innerException = inner,
            hResult = hResult
        )
    }

    private fun evaluateExpression(expression: String, expandChildren: Boolean, maxDepth: Int, allowSideEffects: Boolean = false): EvaluateResult {
        val session = currentSession() ?: return EvaluateResult(expression = expression, isValid = false, errorMessage = "No debug session")
        val evaluator = getEvaluator(session) ?: return EvaluateResult(expression = expression, isValid = false, errorMessage = "No evaluator available")
        return evaluateExpressionCore(expression, expandChildren, maxDepth, session, evaluator, allowSideEffects)
    }

    private fun evaluateExpressionsBatch(expressions: List<String>, expandChildren: Boolean, maxDepth: Int, allowSideEffects: Boolean = false): BatchEvaluateResult {
        val snapshotId = java.util.UUID.randomUUID().toString().replace("-", "")
        val capturedAt = Instant.now().toString()
        return evaluateExpressionsBatchInternal(
            normalizeExpressions(expressions),
            expandChildren,
            maxDepth,
            allowSideEffects,
            snapshotId,
            capturedAt
        )
    }

    private fun evaluateExpressionsBatchInternal(
        normalizedExpressions: List<String>,
        expandChildren: Boolean,
        maxDepth: Int,
        allowSideEffects: Boolean,
        snapshotId: String,
        capturedAt: String
    ): BatchEvaluateResult {
        val normalizedDepth = maxDepth.coerceIn(0, 4)
        var bestEffort = BatchEvaluateResult(
            snapshotId = snapshotId,
            capturedAt = capturedAt,
            requestedCount = normalizedExpressions.size,
            snapshotStatus = "unavailable",
            partial = true
        )

        if (normalizedExpressions.isEmpty()) {
            return buildUnavailableBatchEvaluateResult(
                snapshotId = snapshotId,
                capturedAt = capturedAt,
                requestedCount = 0,
                error = "No expressions provided"
            )
        }

        for (attempt in 1..snapshotMaxAttempts) {
            val session = currentSession() ?: return bestEffort.copy(
                snapshotAttempts = attempt,
                error = "No debug session"
            )
            if (!session.isSuspended) {
                return bestEffort.copy(
                    snapshotAttempts = attempt,
                    error = "Not in break mode"
                )
            }

            try {
                val suspendContext = session.suspendContext ?: return bestEffort.copy(
                    snapshotAttempts = attempt,
                    error = "No suspend context"
                )
                val evaluator = getEvaluator(session)
                    ?: return bestEffort.copy(snapshotAttempts = attempt, error = "No evaluator available")
                val tokenBefore = suspendSnapshotToken(session, suspendContext)
                val snapshot = buildBatchEvaluateSnapshot(
                    normalizedExpressions = normalizedExpressions,
                    expandChildren = expandChildren,
                    maxDepth = normalizedDepth,
                    allowSideEffects = allowSideEffects,
                    session = session,
                    evaluator = evaluator,
                    snapshotId = snapshotId,
                    capturedAt = capturedAt
                )
                val currentContext = session.suspendContext
                val stable = currentContext != null &&
                    session.isSuspended &&
                    tokenBefore == suspendSnapshotToken(session, currentContext)
                val status = if (stable) {
                    if (snapshot.partial) "partial" else "ok"
                } else {
                    "unstable"
                }
                val withSnapshotState = snapshot.copy(
                    snapshotStable = stable,
                    snapshotAttempts = attempt,
                    snapshotStatus = status
                )
                bestEffort = withSnapshotState
                if (stable) {
                    return withSnapshotState
                }
            } catch (ex: Exception) {
                bestEffort = bestEffort.copy(
                    snapshotAttempts = attempt,
                    error = ex.message ?: bestEffort.error
                )
                if (bestEffort.results.isNotEmpty()) {
                    bestEffort = bestEffort.copy(snapshotStatus = "unstable")
                }
            }

            if (attempt < snapshotMaxAttempts) {
                Thread.sleep(snapshotRetryDelayMs)
            }
        }

        return bestEffort
    }

    private fun buildBatchEvaluateSnapshot(
        normalizedExpressions: List<String>,
        expandChildren: Boolean,
        maxDepth: Int,
        allowSideEffects: Boolean,
        session: XDebugSession,
        evaluator: XDebuggerEvaluator,
        snapshotId: String,
        capturedAt: String
    ): BatchEvaluateResult {
        val results = normalizedExpressions.map { expression ->
            evaluateExpressionCore(expression, expandChildren, maxDepth, session, evaluator, allowSideEffects)
        }.toMutableList()
        val successCount = results.count { it.isValid }
        val failedCount = results.size - successCount
        val partial = failedCount > 0 ||
            results.any { it.errorMessage?.contains("Expansion truncated") == true }

        return BatchEvaluateResult(
            snapshotId = snapshotId,
            capturedAt = capturedAt,
            requestedCount = normalizedExpressions.size,
            successCount = successCount,
            failedCount = failedCount,
            partial = partial,
            results = results
        )
    }

    private fun buildUnavailableBatchEvaluateResult(
        snapshotId: String,
        capturedAt: String,
        requestedCount: Int,
        error: String
    ): BatchEvaluateResult {
        return BatchEvaluateResult(
            snapshotId = snapshotId,
            capturedAt = capturedAt,
            snapshotStatus = "unavailable",
            requestedCount = requestedCount,
            successCount = 0,
            failedCount = requestedCount,
            partial = requestedCount > 0,
            error = error
        )
    }

    private fun evaluateExpressionCore(
        expression: String,
        expandChildren: Boolean,
        maxDepth: Int,
        session: XDebugSession,
        evaluator: XDebuggerEvaluator,
        allowSideEffects: Boolean = false
    ): EvaluateResult {
        val eval = evaluateValue(evaluator, expression, session.currentPosition)
        if (eval.value == null) {
            return EvaluateResult(expression = expression, isValid = false, errorMessage = eval.errorMessage)
        }

        val presentation = describeValue(eval.value)
        val budget = newVariableExpansionBudget()
        val children = if (expandChildren && presentation.hasChildren) {
            collectVariables(eval.value, 0, maxDepth.coerceIn(0, variableExpansionMaxDepth), budget).toMutableList()
        } else {
            null
        }

        return EvaluateResult(
            expression = expression,
            value = presentation.value,
            type = presentation.type,
            isValid = true,
            errorMessage = budget.reason,
            hasChildren = presentation.hasChildren,
            children = children
        )
    }

    private fun normalizeExpressions(expressions: List<String>?): List<String> {
        if (expressions.isNullOrEmpty()) {
            return emptyList()
        }
        return expressions
            .map { it.trim() }
            .filter { it.isNotEmpty() }
            .take(100)
    }

    private fun readMemory(address: String, size: Int): MemoryResult {
        val normalizedSize = size.coerceIn(1, 4096)
        var bestEffort = buildUnavailableMemoryResult(address, normalizedSize)

        for (attempt in 1..snapshotMaxAttempts) {
            val session = currentSession() ?: return bestEffort
            if (!session.isSuspended) {
                return bestEffort
            }

            try {
                val suspendContext = session.suspendContext ?: return bestEffort
                val tokenBefore = suspendSnapshotToken(session, suspendContext)
                val snapshot = readMemoryOnce(address, normalizedSize, session)
                val currentContext = session.suspendContext
                val stable = currentContext != null &&
                    session.isSuspended &&
                    tokenBefore == suspendSnapshotToken(session, currentContext)

                if (stable) {
                    return snapshot
                }

                bestEffort = if (snapshot.bytesRead > 0) {
                    snapshot.copy(status = "unstable")
                } else {
                    snapshot
                }
            } catch (_: Exception) {
                // Keep best effort unavailable/partial result and retry once.
            }

            if (attempt < snapshotMaxAttempts) {
                Thread.sleep(snapshotRetryDelayMs)
            }
        }

        return bestEffort
    }

    private fun buildUnavailableMemoryResult(address: String, requestedSize: Int): MemoryResult {
        return MemoryResult(
            address = address,
            status = "unavailable",
            requestedSize = requestedSize,
            failedReads = requestedSize
        )
    }

    private fun readMemoryOnce(address: String, normalizedSize: Int, session: XDebugSession): MemoryResult {
        val evaluator = getEvaluator(session) ?: return buildUnavailableMemoryResult(address, normalizedSize)
        val addressExpr = resolveAddressExpression(address, evaluator, session.currentPosition)
            ?: return buildUnavailableMemoryResult(address, normalizedSize)
        val baseAddress = parseNumber(addressExpr)
            ?: return buildUnavailableMemoryResult(addressExpr, normalizedSize)

        val snapshot = readMemorySnapshot(baseAddress, normalizedSize, evaluator, session.currentPosition)
        if (snapshot.bytesRead == 0) {
            return buildUnavailableMemoryResult("0x" + formatAddressValue(baseAddress), normalizedSize)
        }

        val bytesArray = snapshot.bytes
        val bytes = bytesArray.toMutableList()
        val hexDump = bytesArray.joinToString(" ") { String.format("%02X", it.toInt() and 0xFF) }
        val asciiView = bytesArray.map { b ->
            val c = b.toInt().toChar()
            if (c.isLetterOrDigit() || c in " .-_:;!?") c else 0x2E.toChar()
        }.joinToString("")

        return MemoryResult(
            address = "0x" + formatAddressValue(baseAddress),
            status = if (snapshot.partial) "partial" else "ok",
            size = snapshot.bytesRead,
            requestedSize = normalizedSize,
            bytesRead = snapshot.bytesRead,
            failedReads = snapshot.failedReads,
            partial = snapshot.partial,
            bytes = bytes,
            hexDump = hexDump,
            asciiView = asciiView,
            formattedView = formatMemoryRows(baseAddress, bytesArray)
        )
    }

    private fun getDisassembly(linesBefore: Int, linesAfter: Int): DisassemblyResult {
        val session = currentSession() ?: return DisassemblyResult(error = "No debug session")
        if (!session.isSuspended) {
            return DisassemblyResult(error = "Not in break mode")
        }
        val evaluator = getEvaluator(session) ?: return DisassemblyResult(error = "No evaluator available")
        val position = session.currentPosition ?: return DisassemblyResult(error = "No source position")

        val ipInfo = getInstructionPointer(evaluator, position)
            ?: return DisassemblyResult(error = "Unable to read instruction pointer")
        val (ip, is64) = ipInfo
        val sourceLine = getSelectedFrame(session)?.let { getFrameLocation(it)?.lineNumber }

        val native = buildNativeDisassemblyWindow(
            instructionPointer = ip,
            linesBefore = linesBefore,
            linesAfter = linesAfter,
            evaluator = evaluator,
            position = position,
            is64Bit = is64,
            sourceLine = sourceLine
        )

        if (native.error != null) {
            val fallback = buildSourceDisassembly(position, linesBefore, linesAfter)
            return fallback.copy(
                error = "Native disassembly unavailable: ${native.error}",
                is64Bit = is64
            )
        }

        val frame = getSelectedFrame(session)
        return native.copy(
            functionName = frame?.let { getFrameName(it) },
            sourceFile = position.file.path,
            is64Bit = is64
        )
    }

    private fun getDisassemblyAt(address: String, count: Int): DisassemblyResult {
        val session = currentSession() ?: return DisassemblyResult(error = "No debug session")
        if (!session.isSuspended) {
            return DisassemblyResult(error = "Not in break mode")
        }
        val evaluator = getEvaluator(session) ?: return DisassemblyResult(error = "No evaluator available")
        val position = session.currentPosition

        val addressExpr = resolveAddressExpression(address, evaluator, position)
            ?: return DisassemblyResult(error = "Invalid address")
        val baseAddress = parseNumber(addressExpr)
            ?: return DisassemblyResult(error = "Invalid address")

        val ipInfo = position?.let { getInstructionPointer(evaluator, it) }
        val is64 = ipInfo?.second ?: true

        return buildNativeDisassemblyAt(
            address = baseAddress,
            count = count.coerceAtLeast(1),
            evaluator = evaluator,
            position = position,
            is64Bit = is64,
            sourceLine = null
        )
    }

    private fun getBuildErrors(errorsOnly: Boolean = false, warningsOnly: Boolean = false): BuildErrorsResult {
        val project = currentProject() ?: return BuildErrorsResult()
        val collector = ProblemsCollector.getInstance(project)
        val items = mutableListOf<BuildErrorItem>()
        val problems = mutableListOf<FileProblem>()

        collector.getProblemFiles().forEach { file ->
            collector.getFileProblems(file).forEach { problem ->
                if (problem is FileProblem) {
                    problems.add(problem)
                }
            }
        }

        problems.forEach { problem ->
            val description = problem.description ?: problem.text
            val severity = classifyProblemSeverity(problem, description)
            val isError = severity == "Error"
            val isWarning = severity == "Warning"

            if (errorsOnly && !isError) return@forEach
            if (warningsOnly && !isWarning) return@forEach

            val filePath = problem.file.path
            val code = extractErrorCode(description)
            val projectName = problem.provider?.project?.name

            items.add(
                BuildErrorItem(
                    severity = severity,
                    code = code,
                    description = description,
                    filePath = filePath,
                    line = problem.line.coerceAtLeast(0) + 1,
                    column = problem.column.coerceAtLeast(0) + 1,
                    project = projectName
                )
            )
        }

        val errorCount = items.count { it.severity == "Error" }
        val warningCount = items.count { it.severity == "Warning" }

        return BuildErrorsResult(errorCount = errorCount, warningCount = warningCount, items = items)
    }

    private fun getFullContext(
        threadIncludeFrames: Boolean = true,
        threadMaxFramesPerThread: Int = -1,
        threadMaxNodes: Int = -1,
        threadBudgetMs: Int = -1
    ): FullContextResult {
        return FullContextResult(
            status = getDebugStatus(),
            callstack = getCallStack(),
            locals = getLocals(),
            exception = getException(),
            threads = getThreads(threadIncludeFrames, threadMaxFramesPerThread, threadMaxNodes, threadBudgetMs),
            modules = getModules()
        )
    }

    private fun captureDebugBundle(
        expressions: List<String>,
        expandChildren: Boolean,
        maxDepth: Int,
        outputTailLines: Int,
        includeLocals: Boolean = false,
        includeModules: Boolean = false,
        includeRegisters: Boolean = false,
        includeOutput: Boolean = false,
        includeThreads: Boolean = true,
        includeException: Boolean = true,
        allowSideEffects: Boolean = false,
        threadIncludeFrames: Boolean = true,
        threadMaxFramesPerThread: Int = -1,
        threadMaxNodes: Int = -1,
        threadBudgetMs: Int = -1
    ): DebugBundleResult {
        val normalizedExpressions = normalizeExpressions(expressions)
        val normalizedDepth = maxDepth.coerceIn(0, 4)
        val normalizedTailLines = outputTailLines.coerceIn(0, 500)
        val normalizedThreadMaxFramesPerThread = normalizeThreadFrameLimit(threadMaxFramesPerThread)
        val normalizedThreadMaxNodes = normalizeThreadNodeLimit(threadMaxNodes)
        val normalizedThreadBudgetMs = normalizeThreadBudgetMs(threadBudgetMs)
        val snapshotId = java.util.UUID.randomUUID().toString().replace("-", "")
        val capturedAt = Instant.now().toString()
        val includedComponents = buildIncludedDebugBundleComponents(
            includeLocals,
            includeModules,
            includeRegisters,
            includeOutput,
            includeThreads,
            includeException,
            normalizedExpressions.size
        )
        val omittedComponents = buildOmittedDebugBundleComponents(
            includeLocals,
            includeModules,
            includeRegisters,
            includeOutput,
            includeThreads,
            includeException
        )
        var bestEffort = DebugBundleResult(
            snapshotId = snapshotId,
            capturedAt = capturedAt,
            snapshotStatus = "unavailable",
            partial = true,
            includedComponents = includedComponents,
            omittedComponents = omittedComponents
        )

        for (attempt in 1..snapshotMaxAttempts) {
            val session = currentSession() ?: return bestEffort.copy(
                snapshotAttempts = attempt,
                status = DebugStatusResult(),
                stopReason = "not_paused",
                expressions = buildUnavailableBatchEvaluateResult(
                    snapshotId = snapshotId,
                    capturedAt = capturedAt,
                    requestedCount = normalizedExpressions.size,
                    error = "No debug session"
                ),
                error = "No debug session"
            )

            val status = getDebugStatus()
            if (!session.isSuspended) {
                return bestEffort.copy(
                    snapshotAttempts = attempt,
                    stopReason = status.stopReason,
                    status = status,
                    expressions = buildUnavailableBatchEvaluateResult(
                        snapshotId = snapshotId,
                        capturedAt = capturedAt,
                        requestedCount = normalizedExpressions.size,
                        error = "Not in break mode"
                    ),
                    error = "Not in break mode"
                )
            }

            try {
                val suspendContext = session.suspendContext ?: return bestEffort.copy(
                    snapshotAttempts = attempt,
                    status = status,
                    stopReason = status.stopReason,
                    expressions = buildUnavailableBatchEvaluateResult(
                        snapshotId = snapshotId,
                        capturedAt = capturedAt,
                        requestedCount = normalizedExpressions.size,
                        error = "No suspend context"
                    ),
                    error = "No suspend context"
                )
                val tokenBefore = suspendSnapshotToken(session, suspendContext)

                val callstack = buildCallStackResult(suspendContext).also {
                    it.snapshotAttempts = 1
                    it.snapshotStable = true
                    it.snapshotStatus = if (it.frames.isNotEmpty()) "ok" else "unavailable"
                }
                val locals = if (includeLocals) buildLocalsResult(session, normalizedDepth) else null
                val exception = if (includeException) getException() else null
                val threads = if (includeThreads) {
                    buildThreadsResult(
                        suspendContext,
                        threadIncludeFrames,
                        normalizedThreadMaxFramesPerThread,
                        normalizedThreadMaxNodes,
                        normalizedThreadBudgetMs
                    ).also {
                        it.snapshotAttempts = 1
                        it.snapshotStable = true
                        it.snapshotStatus = determineThreadsSnapshotStatus(it, true)
                    }
                } else {
                    null
                }
                val modules = if (includeModules) getModules() else null
                val registers = if (includeRegisters) getRegisters() else null
                val output = if (includeOutput) getOutputWindow("Debug", normalizedTailLines) else null
                val evaluator = getEvaluator(session)
                val expressionBatch = when {
                    normalizedExpressions.isEmpty() -> BatchEvaluateResult(
                        snapshotId = snapshotId,
                        capturedAt = capturedAt,
                        snapshotStable = true,
                        snapshotAttempts = 1,
                        snapshotStatus = "ok",
                        requestedCount = 0,
                        successCount = 0,
                        failedCount = 0,
                        partial = false
                    )
                    evaluator == null -> buildUnavailableBatchEvaluateResult(
                        snapshotId = snapshotId,
                        capturedAt = capturedAt,
                        requestedCount = normalizedExpressions.size,
                        error = "No evaluator available"
                    )
                    else -> buildBatchEvaluateSnapshot(
                        normalizedExpressions = normalizedExpressions,
                        expandChildren = expandChildren,
                        maxDepth = normalizedDepth,
                        allowSideEffects = allowSideEffects,
                        session = session,
                        evaluator = evaluator,
                        snapshotId = snapshotId,
                        capturedAt = capturedAt
                    )
                }

                val currentContext = session.suspendContext
                val stable = currentContext != null &&
                    session.isSuspended &&
                    tokenBefore == suspendSnapshotToken(session, currentContext)
                val expressionStatus = if (stable) {
                    if (expressionBatch.partial) "partial" else "ok"
                } else {
                    "unstable"
                }
                val expressionSnapshot = expressionBatch.copy(
                    snapshotStable = stable,
                    snapshotAttempts = attempt,
                    snapshotStatus = expressionStatus
                )
                val partial = isBundlePartial(
                    callstack,
                    locals,
                    threads,
                    modules,
                    registers,
                    output,
                    expressionBatch,
                    includedComponents
                )
                val snapshotStatus = if (stable) {
                    if (partial) "partial" else "ok"
                } else {
                    "unstable"
                }

                val errorParts = mutableListOf<String>()
                locals?.error?.let { if (it.isNotBlank()) errorParts.add("locals: $it") }
                modules?.error?.let { if (it.isNotBlank()) errorParts.add("modules: $it") }
                registers?.error?.let { if (it.isNotBlank()) errorParts.add("registers: $it") }
                output?.error?.let { if (it.isNotBlank()) errorParts.add("output: $it") }
                threads?.error?.let { if (it.isNotBlank()) errorParts.add("threads: $it") }
                expressionBatch.error?.let { if (it.isNotBlank()) errorParts.add("expressions: $it") }

                val snapshot = DebugBundleResult(
                    snapshotId = snapshotId,
                    capturedAt = capturedAt,
                    snapshotStable = stable,
                    snapshotAttempts = attempt,
                    snapshotStatus = snapshotStatus,
                    partial = partial,
                    includedComponents = includedComponents,
                    omittedComponents = omittedComponents,
                    stopReason = status.stopReason,
                    status = status,
                    callstack = callstack,
                    locals = locals,
                    exception = exception,
                    threads = threads,
                    modules = modules,
                    registers = registers,
                    output = output,
                    expressions = expressionSnapshot,
                    error = if (errorParts.isEmpty()) null else errorParts.joinToString("; ")
                )
                bestEffort = snapshot
                if (stable) {
                    return snapshot
                }
            } catch (ex: Exception) {
                bestEffort = bestEffort.copy(
                    snapshotAttempts = attempt,
                    status = status,
                    stopReason = status.stopReason,
                    error = ex.message ?: bestEffort.error
                )
                if (bestEffort.status != null) {
                    bestEffort = bestEffort.copy(snapshotStatus = "unstable")
                }
            }

            if (attempt < snapshotMaxAttempts) {
                Thread.sleep(snapshotRetryDelayMs)
            }
        }

        return bestEffort
    }

    private fun isBundlePartial(
        callstack: CallStackResult,
        locals: LocalsResult?,
        threads: ThreadsResult?,
        modules: ModulesResult?,
        registers: RegistersResult?,
        output: OutputWindowResult?,
        expressions: BatchEvaluateResult,
        includedComponents: List<String>
    ): Boolean {
        if (callstack.snapshotStatus != "ok") return true
        if ("threads" in includedComponents && (threads == null || threads.snapshotStatus != "ok" || threads.partial)) return true
        if ("locals" in includedComponents && (locals == null || locals.partial)) return true
        if ("modules" in includedComponents && !modules?.error.isNullOrBlank()) return true
        if ("registers" in includedComponents && !registers?.error.isNullOrBlank()) return true
        if ("output" in includedComponents && !output?.error.isNullOrBlank()) return true
        if ("expressions" in includedComponents && (expressions.partial || expressions.failedCount > 0)) return true
        return false
    }

    private fun buildIncludedDebugBundleComponents(
        includeLocals: Boolean,
        includeModules: Boolean,
        includeRegisters: Boolean,
        includeOutput: Boolean,
        includeThreads: Boolean,
        includeException: Boolean,
        expressionCount: Int
    ): List<String> {
        val components = mutableListOf("status", "callstack")
        if (includeException) components.add("exception")
        if (includeThreads) components.add("threads")
        if (includeLocals) components.add("locals")
        if (includeModules) components.add("modules")
        if (includeRegisters) components.add("registers")
        if (includeOutput) components.add("output")
        if (expressionCount > 0) components.add("expressions")
        return components
    }

    private fun buildOmittedDebugBundleComponents(
        includeLocals: Boolean,
        includeModules: Boolean,
        includeRegisters: Boolean,
        includeOutput: Boolean,
        includeThreads: Boolean,
        includeException: Boolean
    ): List<String> {
        val components = mutableListOf<String>()
        if (!includeException) components.add("exception")
        if (!includeThreads) components.add("threads")
        if (!includeLocals) components.add("locals")
        if (!includeModules) components.add("modules")
        if (!includeRegisters) components.add("registers")
        if (!includeOutput) components.add("output")
        return components
    }

    private fun getInstanceInfo(pipeName: String): InstanceInfoResult {
        val project = currentProject()
        val session = project?.let { currentSession(it) }
        val status = session?.let { getDebugStatus() } ?: DebugStatusResult()
        val appInfo = ApplicationInfo.getInstance()
        return InstanceInfoResult(
            processId = ProcessHandle.current().pid().toInt(),
            pipeName = pipeName.ifBlank { this.pipeName },
            solutionName = project?.name,
            solutionPath = project?.basePath,
            isDebugging = status.isDebugging,
            isPaused = status.isPaused,
            debugMode = status.debugMode,
            currentProcess = status.currentProcess,
            ideName = appInfo.fullApplicationName,
            ideVersion = appInfo.fullVersion
        )
    }

    private fun debugContinue(): DebugControlResult = executeDebugAction("Continue") { session ->
        session.resume()
    }

    private fun continueAndWaitForBreak(
        expressions: List<String>,
        expandChildren: Boolean,
        maxDepth: Int,
        outputTailLines: Int,
        timeoutMs: Int,
        pollIntervalMs: Int,
        includeLocals: Boolean = false,
        includeModules: Boolean = false,
        includeRegisters: Boolean = false,
        includeOutput: Boolean = false,
        includeThreads: Boolean = true,
        includeException: Boolean = true,
        allowSideEffects: Boolean = false,
        threadIncludeFrames: Boolean = true,
        threadMaxFramesPerThread: Int = -1,
        threadMaxNodes: Int = -1,
        threadBudgetMs: Int = -1
    ): ContinueAndWaitResult {
        val normalizedTimeoutMs = timeoutMs.coerceIn(1000, 30 * 60 * 1000)
        val normalizedPollIntervalMs = pollIntervalMs.coerceIn(50, 1000)
        val initialStatus = getDebugStatus()
        var result = ContinueAndWaitResult(
            action = "ContinueAndWaitForBreak",
            waitTimeoutMs = normalizedTimeoutMs,
            pollIntervalMs = normalizedPollIntervalMs,
            status = initialStatus,
            stopReason = initialStatus.stopReason
        )

        if (!initialStatus.isDebugging) {
            return result.copy(success = false, message = "No debug session")
        }

        if (initialStatus.isPaused) {
            val continueResult = debugContinue()
            result = result.copy(issuedContinue = continueResult.success)
            if (!continueResult.success) {
                val status = getDebugStatus()
                return result.copy(
                    success = false,
                    message = continueResult.message,
                    status = status,
                    stopReason = status.stopReason
                )
            }
        }

        val startedAt = System.nanoTime()
        while (elapsedMillis(startedAt) < normalizedTimeoutMs) {
            val status = getDebugStatus()
            val waitedMs = elapsedMillis(startedAt)
            result = result.copy(
                status = status,
                stopReason = status.stopReason,
                waitedMs = waitedMs
            )

            if (!status.isDebugging) {
                return result.copy(success = false, message = "Debug session ended before hitting a breakpoint")
            }

            if (status.isPaused) {
                val bundle = captureDebugBundle(
                    expressions,
                    expandChildren,
                    maxDepth,
                    outputTailLines,
                    includeLocals,
                    includeModules,
                    includeRegisters,
                    includeOutput,
                    includeThreads,
                    includeException,
                    allowSideEffects,
                    threadIncludeFrames,
                    threadMaxFramesPerThread,
                    threadMaxNodes,
                    threadBudgetMs
                )
                val bundleStatus = bundle.status ?: status
                return result.copy(
                    success = true,
                    message = "Debug stop detected and bundle captured",
                    waitedMs = elapsedMillis(startedAt),
                    breakDetected = true,
                    timedOut = false,
                    stopReason = bundle.stopReason.ifBlank { bundleStatus.stopReason },
                    status = bundleStatus,
                    bundle = bundle
                )
            }

            Thread.sleep(normalizedPollIntervalMs.toLong())
        }

        val finalStatus = getDebugStatus()
        return result.copy(
            success = false,
            message = "Timed out waiting for debugger to enter break mode",
            waitedMs = normalizedTimeoutMs,
            timedOut = true,
            stopReason = finalStatus.stopReason,
            status = finalStatus
        )
    }

    private fun debugStepInto(): DebugControlResult = executeDebugAction("StepInto") { session ->
        session.stepInto()
    }

    private fun debugStepOver(): DebugControlResult = executeDebugAction("StepOver") { session ->
        session.stepOver(false)
    }

    private fun debugStepOut(): DebugControlResult = executeDebugAction("StepOut") { session ->
        session.stepOut()
    }

    private fun debugBreak(): DebugControlResult = executeDebugAction("Break") { session ->
        session.pause()
    }

    private fun debugStop(): DebugControlResult = executeDebugAction("Stop") { session ->
        session.stop()
    }

    private fun debugDetach(): DebugControlResult {
        currentSession() ?: return DebugControlResult(action = "Detach", success = false, message = "No debug session")
        return DebugControlResult(
            action = "Detach",
            success = false,
            message = "Detach is not supported by the Rider MCP bridge; use debug_stop to stop the session."
        )
    }

    private fun debugRunToCursor(filePath: String, lineNumber: Int): DebugControlResult {
        val project = currentProject() ?: return DebugControlResult(action = "RunToCursor", success = false, message = "No open project")
        val session = currentSession(project) ?: return DebugControlResult(action = "RunToCursor", success = false, message = "No debug session")
        val file = LocalFileSystem.getInstance().findFileByPath(filePath)
            ?: return DebugControlResult(action = "RunToCursor", success = false, message = "File not found: $filePath")

        val position = XSourcePositionImpl.create(file, (lineNumber - 1).coerceAtLeast(0))
            ?: return DebugControlResult(action = "RunToCursor", success = false, message = "Unable to create source position")

        return try {
            session.runToPosition(position, false)
            DebugControlResult(action = "RunToCursor", success = true, message = "Ok", newLocation = getCurrentLocation(session))
        } catch (ex: Exception) {
            DebugControlResult(action = "RunToCursor", success = false, message = ex.message)
        }
    }

    private fun debugSetNextStatement(filePath: String, lineNumber: Int): DebugControlResult {
        val project = currentProject() ?: return DebugControlResult(action = "SetNextStatement", success = false, message = "No open project")
        val session = currentSession(project) ?: return DebugControlResult(action = "SetNextStatement", success = false, message = "No debug session")
        val file = LocalFileSystem.getInstance().findFileByPath(filePath)
            ?: return DebugControlResult(action = "SetNextStatement", success = false, message = "File not found: $filePath")
        val position = XSourcePositionImpl.create(file, (lineNumber - 1).coerceAtLeast(0))
            ?: return DebugControlResult(action = "SetNextStatement", success = false, message = "Unable to create source position")

        val success = tryInvokeSetNextStatement(session, position)
        return DebugControlResult(
            action = "SetNextStatement",
            success = success,
            message = if (success) "Ok" else "SetNextStatement not supported",
            newLocation = if (success) getCurrentLocation(session) else null
        )
    }

    private fun getBreakpoints(): BreakpointsResult {
        val project = currentProject() ?: return BreakpointsResult(error = "No open project")
        val manager = XDebuggerManager.getInstance(project).breakpointManager
        val all = manager.allBreakpoints
        val result = BreakpointsResult()

        all.forEach { bp ->
            result.breakpoints.add(toBreakpointInfo(bp))
        }

        result.totalCount = result.breakpoints.size
        return result
    }

    private fun setBreakpoint(filePath: String, lineNumber: Int, condition: String?): BreakpointOperationResult {
        val project = currentProject() ?: return BreakpointOperationResult(operation = "SetBreakpoint", success = false, message = "No open project")
        val manager = XDebuggerManager.getInstance(project).breakpointManager
        val file = LocalFileSystem.getInstance().findFileByPath(filePath)
            ?: return BreakpointOperationResult(operation = "SetBreakpoint", success = false, message = "File not found: $filePath")
        val line = (lineNumber - 1).coerceAtLeast(0)

        @Suppress("UNCHECKED_CAST")
        val type = findLineBreakpointType(file, line, project) as? XLineBreakpointType<XBreakpointProperties<Any>>
            ?: return BreakpointOperationResult(operation = "SetBreakpoint", success = false, message = "No line breakpoint type for file")
        val props = type.createBreakpointProperties(file, line)
        val bp = manager.addLineBreakpoint(type, file.url, line, props)

        if (!condition.isNullOrBlank()) {
            bp.setConditionExpression(XExpressionImpl.fromText(condition))
        }

        return BreakpointOperationResult(operation = "SetBreakpoint", success = true, message = "Ok", breakpointInfo = toBreakpointInfo(bp))
    }

    private fun setFunctionBreakpoint(functionName: String, condition: String?): BreakpointOperationResult {
        val project = currentProject() ?: return BreakpointOperationResult(operation = "SetFunctionBreakpoint", success = false, message = "No open project")
        val manager = XDebuggerManager.getInstance(project).breakpointManager
        val type = findFunctionBreakpointType() ?: return BreakpointOperationResult(
            operation = "SetFunctionBreakpoint",
            success = false,
            message = "Function breakpoint type not available"
        )

        @Suppress("UNCHECKED_CAST")
        val typedType = type as XBreakpointType<XBreakpoint<XBreakpointProperties<Any>>, XBreakpointProperties<Any>>
        val properties = typedType.createProperties()
            ?: return BreakpointOperationResult(
                operation = "SetFunctionBreakpoint",
                success = false,
                message = "Function breakpoint properties not supported"
            )
        applyFunctionName(properties, functionName)
        val bp = manager.addBreakpoint(typedType, properties)
        if (!condition.isNullOrBlank()) {
            bp.setConditionExpression(XExpressionImpl.fromText(condition))
        }

        return BreakpointOperationResult(operation = "SetFunctionBreakpoint", success = true, message = "Ok", breakpointInfo = toBreakpointInfo(bp))
    }

    private fun deleteBreakpoint(filePath: String, lineNumber: Int): BreakpointOperationResult {
        val project = currentProject() ?: return BreakpointOperationResult(operation = "DeleteBreakpoint", success = false, message = "No open project")
        val manager = XDebuggerManager.getInstance(project).breakpointManager
        val file = LocalFileSystem.getInstance().findFileByPath(filePath)
            ?: return BreakpointOperationResult(operation = "DeleteBreakpoint", success = false, message = "File not found")
        val line = (lineNumber - 1).coerceAtLeast(0)

        @Suppress("UNCHECKED_CAST")
        val type = findLineBreakpointType(file, line, project) as? XLineBreakpointType<XBreakpointProperties<Any>>
            ?: return BreakpointOperationResult(operation = "DeleteBreakpoint", success = false, message = "No line breakpoint type")

        val bp = manager.allBreakpoints
            .filterIsInstance<XLineBreakpoint<*>>()
            .firstOrNull { it.type == type && it.fileUrl == file.url && it.line == line }
            ?: return BreakpointOperationResult(operation = "DeleteBreakpoint", success = false, message = "Breakpoint not found")
        manager.removeBreakpoint(bp)
        return BreakpointOperationResult(operation = "DeleteBreakpoint", success = true, message = "Ok")
    }

    private fun deleteAllBreakpoints(): BreakpointOperationResult {
        val project = currentProject() ?: return BreakpointOperationResult(operation = "DeleteAllBreakpoints", success = false, message = "No open project")
        val manager = XDebuggerManager.getInstance(project).breakpointManager
        manager.allBreakpoints.forEach { manager.removeBreakpoint(it) }
        return BreakpointOperationResult(operation = "DeleteAllBreakpoints", success = true, message = "Ok")
    }

    private fun enableBreakpoint(filePath: String, lineNumber: Int, enabled: Boolean): BreakpointOperationResult {
        val project = currentProject() ?: return BreakpointOperationResult(operation = "EnableBreakpoint", success = false, message = "No open project")
        val manager = XDebuggerManager.getInstance(project).breakpointManager
        val file = LocalFileSystem.getInstance().findFileByPath(filePath)
            ?: return BreakpointOperationResult(operation = "EnableBreakpoint", success = false, message = "File not found")
        val line = (lineNumber - 1).coerceAtLeast(0)

        @Suppress("UNCHECKED_CAST")
        val type = findLineBreakpointType(file, line, project) as? XLineBreakpointType<XBreakpointProperties<Any>>
            ?: return BreakpointOperationResult(operation = "EnableBreakpoint", success = false, message = "No line breakpoint type")

        val bp = manager.allBreakpoints
            .filterIsInstance<XLineBreakpoint<*>>()
            .firstOrNull { it.type == type && it.fileUrl == file.url && it.line == line }
            ?: return BreakpointOperationResult(operation = "EnableBreakpoint", success = false, message = "Breakpoint not found")
        bp.setEnabled(enabled)
        return BreakpointOperationResult(operation = "EnableBreakpoint", success = true, message = "Ok", breakpointInfo = toBreakpointInfo(bp))
    }

    private fun getRegisters(): RegistersResult {
        val capability = "general_registers_plus_best_effort_extended"
        val categories = mutableSetOf<String>()
        val session = currentSession() ?: return RegistersResult(
            capability = capability,
            availableCategories = mutableListOf("General Purpose"),
            error = "No debug session"
        )
        val evaluator = getEvaluator(session) ?: return RegistersResult(
            capability = capability,
            availableCategories = mutableListOf("General Purpose"),
            error = "No evaluator available"
        )
        val registers = mutableListOf<RegisterInfo>()
        var is64 = false

        val candidates64 = listOf("rax", "rbx", "rcx", "rdx", "rsi", "rdi", "rbp", "rsp", "rip", "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15", "rflags")
        val candidates32 = listOf("eax", "ebx", "ecx", "edx", "esi", "edi", "ebp", "esp", "eip", "eflags")
        val segmentRegs = listOf("cs", "ds", "es", "fs", "gs", "ss")

        fun tryRegister(name: String): String? {
            val expr = "@$name"
            val eval = evaluateValue(evaluator, expr, session.currentPosition)
            return eval.value?.let { describeValue(it).value }
        }

        fun addRegister(name: String, value: String?, category: String) {
            if (value.isNullOrBlank()) return
            categories.add(category)
            registers.add(
                RegisterInfo(
                    name = name.uppercase(Locale.ROOT),
                    value = value,
                    hexValue = value,
                    category = category
                )
            )
        }

        candidates64.forEach { reg ->
            val value = tryRegister(reg)
            if (!value.isNullOrBlank()) {
                is64 = true
                addRegister(reg, value, registerCategory(reg))
            }
        }

        if (!is64) {
            candidates32.forEach { reg ->
                val value = tryRegister(reg)
                addRegister(reg, value, registerCategory(reg))
            }
        }

        segmentRegs.forEach { reg ->
            val value = tryRegister(reg)
            addRegister(reg, value, "Segment")
        }

        // Best effort: not every engine exposes SSE registers.
        for (i in 0 until 16) {
            val reg = "xmm$i"
            val value = tryRegister(reg)
            addRegister(reg, value, "SSE")
        }

        if (categories.isEmpty()) {
            categories.add("General Purpose")
        }

        return RegistersResult(
            registers = registers,
            is64Bit = is64,
            capability = capability,
            availableCategories = categories.sorted().toMutableList()
        )
    }

    private fun addWatch(expression: String): WatchResult {
        val behavior = "evaluate_only"
        val session = currentSession()
            ?: return WatchResult(expression = expression, behavior = behavior, isValid = false, error = "No debug session")
        val evaluator = getEvaluator(session)
            ?: return WatchResult(expression = expression, behavior = behavior, isValid = false, error = "No evaluator available")
        val eval = evaluateValue(evaluator, expression, session.currentPosition)
        if (eval.value == null) {
            return WatchResult(expression = expression, behavior = behavior, isValid = false, error = eval.errorMessage)
        }
        val presentation = describeValue(eval.value)
        return WatchResult(
            expression = expression,
            behavior = behavior,
            value = presentation.value,
            type = presentation.type,
            isValid = true
        )
    }

    private fun searchMemory(startAddress: String, pattern: String, maxResults: Int, searchSize: Int): MemorySearchResult {
        val bytesToScan = searchSize.coerceIn(1, 0x100000)
        val normalizedMaxResults = maxResults.coerceIn(1, 10000)
        var bestEffort = buildUnavailableMemorySearchResult(pattern, bytesToScan)

        for (attempt in 1..snapshotMaxAttempts) {
            val session = currentSession() ?: return bestEffort
            if (!session.isSuspended) {
                return bestEffort
            }

            try {
                val suspendContext = session.suspendContext ?: return bestEffort
                val tokenBefore = suspendSnapshotToken(session, suspendContext)
                val snapshot = searchMemoryOnce(
                    startAddress = startAddress,
                    pattern = pattern,
                    maxResults = normalizedMaxResults,
                    bytesToScan = bytesToScan,
                    session = session
                )
                val currentContext = session.suspendContext
                val stable = currentContext != null &&
                    session.isSuspended &&
                    tokenBefore == suspendSnapshotToken(session, currentContext)

                if (stable) {
                    return snapshot
                }

                bestEffort = if (snapshot.bytesSearched > 0) {
                    snapshot.copy(status = "unstable")
                } else {
                    snapshot
                }
            } catch (_: Exception) {
                // Keep best effort unavailable/partial result and retry once.
            }

            if (attempt < snapshotMaxAttempts) {
                Thread.sleep(snapshotRetryDelayMs)
            }
        }

        return bestEffort
    }

    private fun buildUnavailableMemorySearchResult(pattern: String, bytesToScan: Int): MemorySearchResult {
        return MemorySearchResult(
            pattern = pattern,
            status = "unavailable",
            requestedBytes = bytesToScan,
            failedReads = bytesToScan
        )
    }

    private fun searchMemoryOnce(
        startAddress: String,
        pattern: String,
        maxResults: Int,
        bytesToScan: Int,
        session: XDebugSession
    ): MemorySearchResult {
        val evaluator = getEvaluator(session) ?: return buildUnavailableMemorySearchResult(pattern, bytesToScan)
        val addressExpr = resolveAddressExpression(startAddress, evaluator, session.currentPosition)
            ?: return buildUnavailableMemorySearchResult(pattern, bytesToScan)
        val baseAddress = parseNumber(addressExpr)
            ?: return buildUnavailableMemorySearchResult(pattern, bytesToScan)

        val patternBytes = parsePattern(pattern)
        if (patternBytes.isEmpty()) {
            return buildUnavailableMemorySearchResult(pattern, bytesToScan)
        }
        val formattedPattern = patternBytes.map { String.format("%02X", it.toInt() and 0xFF) }.toTypedArray()

        val snapshot = readMemorySnapshot(baseAddress, bytesToScan, evaluator, session.currentPosition)
        if (snapshot.bytesRead == 0) {
            return buildUnavailableMemorySearchResult(pattern, bytesToScan).copy(patternBytes = formattedPattern)
        }
        val bytes = snapshot.bytes

        val matches = mutableListOf<String>()
        var i = 0
        while (i <= bytes.size - patternBytes.size && matches.size < maxResults) {
            var match = true
            for (j in patternBytes.indices) {
                if (bytes[i + j] != patternBytes[j]) {
                    match = false
                    break
                }
            }
            if (match) {
                val address = baseAddress + i
                matches.add("0x" + formatAddressValue(address))
            }
            i++
        }

        return MemorySearchResult(
            pattern = pattern,
            status = if (snapshot.partial) "partial" else "ok",
            patternBytes = formattedPattern,
            matches = matches,
            totalFound = matches.size,
            bytesSearched = bytes.size,
            requestedBytes = bytesToScan,
            failedReads = snapshot.failedReads,
            partial = snapshot.partial
        )
    }

    private fun switchStackFrame(frameIndex: Int): DebugControlResult {
        selectedFrameIndex = frameIndex
        val session = currentSession() ?: return DebugControlResult(action = "SwitchStackFrame", success = false, message = "No debug session")
        val suspendContext = session.suspendContext ?: return DebugControlResult(action = "SwitchStackFrame", success = false, message = "Not paused")
        val stack = getSelectedStack(suspendContext) ?: return DebugControlResult(action = "SwitchStackFrame", success = false, message = "No stack")
        val frames = collectStackFrames(stack)
        if (frameIndex !in frames.indices) {
            return DebugControlResult(action = "SwitchStackFrame", success = false, message = "Frame index out of range")
        }
        val frame = frames[frameIndex]
        return try {
            session.setCurrentStackFrame(stack, frame, true)
            DebugControlResult(action = "SwitchStackFrame", success = true, message = "Ok", newLocation = getFrameLocation(frame))
        } catch (ex: Exception) {
            DebugControlResult(action = "SwitchStackFrame", success = false, message = ex.message)
        }
    }

    private fun switchThread(threadId: Int): DebugControlResult {
        selectedThreadId = threadId
        selectedFrameIndex = 0
        val session = currentSession() ?: return DebugControlResult(action = "SwitchThread", success = false, message = "No debug session")
        val suspendContext = session.suspendContext ?: return DebugControlResult(action = "SwitchThread", success = false, message = "Not paused")
        val stack = suspendContext.executionStacks?.firstOrNull { threadId(it) == threadId }
            ?: return DebugControlResult(action = "SwitchThread", success = false, message = "Thread not found")
        val frame = stack.topFrame ?: collectStackFrames(stack).firstOrNull()
        if (frame != null) {
            return try {
                session.setCurrentStackFrame(stack, frame, true)
                DebugControlResult(action = "SwitchThread", success = true, message = "Ok", newLocation = getFrameLocation(frame))
            } catch (ex: Exception) {
                DebugControlResult(action = "SwitchThread", success = false, message = ex.message)
            }
        }
        return DebugControlResult(action = "SwitchThread", success = false, message = "No stack frame")
    }

    private val knownErrorCodePrefixes = listOf(
        "CS",    // C# compiler errors
        "CA",    // Code Analysis warnings
        "IDE",   // IDE analyzers
        "BC",    // Visual Basic compiler
        "NU",    // NuGet errors
        "SA",    // StyleCop analyzers
        "RCS",   // Roslynator analyzers
        "S",     // SonarQube analyzers
        "DOC",   // Documentation analyzers
        "C",     // C/C++ compiler errors
        "LNK",   // Linker errors
        "MSB",   // MSBuild errors
        "NETSDK" // .NET SDK errors
    )

    private fun extractErrorCode(description: String?): String? {
        if (description.isNullOrBlank()) return null

        val prefixMatch = Regex("^(?:error\\s+|warning\\s+)?([A-Z]{1,6}\\d{4,5}):", RegexOption.IGNORE_CASE)
            .find(description)
        if (prefixMatch != null) {
            val code = prefixMatch.groupValues[1].uppercase(Locale.ROOT)
            if (isKnownErrorCode(code)) return code
        }

        for (prefix in knownErrorCodePrefixes) {
            val pattern = Regex("\\b(${Regex.escape(prefix)}\\d{4,5})\\b", RegexOption.IGNORE_CASE)
            val match = pattern.find(description)
            if (match != null) {
                return match.groupValues[1].uppercase(Locale.ROOT)
            }
        }

        val fallback = Regex("^([A-Z]{1,6}\\d{4,5})\\b").find(description)
        return fallback?.groupValues?.get(1)?.uppercase(Locale.ROOT)
    }

    private fun isKnownErrorCode(code: String): Boolean {
        return knownErrorCodePrefixes.any { code.startsWith(it, ignoreCase = true) }
    }

    private fun classifyProblemSeverity(problem: FileProblem, description: String?): String {
        val iconName = problem.icon?.javaClass?.name?.lowercase(Locale.ROOT) ?: ""
        if (iconName.contains("error")) return "Error"
        if (iconName.contains("warning") || iconName.contains("warn")) return "Warning"

        val group = problem.group?.lowercase(Locale.ROOT) ?: ""
        if (group.contains("error")) return "Error"
        if (group.contains("warning") || group.contains("warn")) return "Warning"

        val normalized = description?.trim()?.lowercase(Locale.ROOT) ?: ""
        return when {
            normalized.startsWith("error") || normalized.contains(" error ") -> "Error"
            normalized.startsWith("warning") || normalized.contains(" warn ") -> "Warning"
            normalized.contains("warning") -> "Warning"
            normalized.contains("error") -> "Error"
            else -> "Warning"
        }
    }

    private fun getOutputWindow(pane: String, tailLines: Int): OutputWindowResult {
        val capability = "limited_to_debug_console"
        val requestedPane = pane.ifBlank { "Debug" }
        if (!requestedPane.equals("Debug", ignoreCase = true)) {
            return OutputWindowResult(
                pane = requestedPane,
                capability = capability,
                availablePanes = mutableListOf("Debug"),
                error = "Pane '$requestedPane' is not supported in Rider. Only 'Debug' is available."
            )
        }

        val session = currentSession()
            ?: return OutputWindowResult(pane = "Debug", capability = capability, availablePanes = mutableListOf("Debug"), error = "No debug session")
        val console = session.consoleView
            ?: return OutputWindowResult(pane = "Debug", capability = capability, availablePanes = mutableListOf("Debug"), error = "No console")
        val text = getConsoleText(console)
            ?: return OutputWindowResult(pane = "Debug", capability = capability, availablePanes = mutableListOf("Debug"), error = "Console does not expose text")
        val lines = text.split("\n")
        val count = if (tailLines <= 0) lines.size else tailLines.coerceAtMost(lines.size)
        val slice = if (tailLines <= 0) lines else lines.takeLast(count)
        return OutputWindowResult(
            pane = "Debug",
            capability = capability,
            content = slice.joinToString("\n"),
            lineCount = lines.size,
            requestedLines = tailLines,
            truncated = lines.size > count,
            availablePanes = mutableListOf("Debug")
        )
    }

    private fun searchOutput(pattern: String, pane: String, isRegex: Boolean, contextLines: Int, maxMatches: Int): SearchOutputResult {
        val capability = "limited_to_debug_console"
        val output = getOutputWindow(pane, 0)
        if (output.content == null) {
            return SearchOutputResult(pane = pane, capability = capability, pattern = pattern, isRegex = isRegex, error = output.error)
        }
        val lines = output.content.split("\n")
        val matches = mutableListOf<OutputMatch>()
        val regex = if (isRegex) {
            try {
                Regex(pattern)
            } catch (ex: Exception) {
                return SearchOutputResult(
                    pane = pane,
                    capability = capability,
                    pattern = pattern,
                    isRegex = isRegex,
                    error = "Invalid regex: ${ex.message}"
                )
            }
        } else {
            null
        }

        lines.forEachIndexed { index, line ->
            val matched = if (regex != null) regex.containsMatchIn(line) else line.contains(pattern, ignoreCase = true)
            if (matched) {
                val start = (index - contextLines).coerceAtLeast(0)
                val end = (index + contextLines).coerceAtMost(lines.size - 1)
                val context = lines.subList(start, end + 1)
                matches.add(OutputMatch(lineNumber = index + 1, line = line, context = context.toMutableList()))
            }
            if (matches.size >= maxMatches) return@forEachIndexed
        }

        return SearchOutputResult(
            pane = pane,
            capability = capability,
            pattern = pattern,
            isRegex = isRegex,
            matches = matches,
            totalMatches = matches.size,
            truncated = matches.size >= maxMatches
        )
    }
    private fun elapsedMillis(startedAtNanos: Long): Int {
        val elapsed = (System.nanoTime() - startedAtNanos) / 1_000_000L
        return elapsed.coerceAtMost(Int.MAX_VALUE.toLong()).toInt()
    }

    private fun executeDebugAction(action: String, operation: (XDebugSession) -> Unit): DebugControlResult {
        val session = currentSession() ?: return DebugControlResult(action = action, success = false, message = "No debug session")
        return try {
            operation(session)
            DebugControlResult(action = action, success = true, message = "Ok", newLocation = getCurrentLocation(session))
        } catch (ex: Exception) {
            DebugControlResult(action = action, success = false, message = ex.message)
        }
    }

    private fun getCurrentLocation(session: XDebugSession): LocationInfo? {
        val frame = getSelectedFrame(session) ?: session.currentStackFrame
        if (frame != null) {
            return getFrameLocation(frame)
        }
        val position = session.currentPosition ?: session.topFramePosition
        if (position != null) {
            return LocationInfo(
                filePath = position.file.path,
                functionName = "",
                lineNumber = position.line + 1,
                columnNumber = 1
            )
        }
        return null
    }

    private fun buildCallStackResult(suspendContext: XSuspendContext): CallStackResult {
        val stack = getSelectedStack(suspendContext)
        val frames = stack?.let { collectStackFrames(it) } ?: emptyList()
        val result = CallStackResult(capability = "partial_frame_metadata")
        if (stack != null) {
            result.threadId = threadId(stack)
            result.threadName = stack.displayName
        }
        val maxArgumentFrames = 5
        frames.forEachIndexed { index, frame ->
            val location = getFrameLocation(frame)
            val frameName = getFrameName(frame)
            val inferredLanguage = inferLanguage(location?.filePath, frameName)
            val arguments = if (index < maxArgumentFrames) {
                try {
                    val vars = collectVariables(frame, 0, 1)
                    vars.toMutableList().takeIf { it.isNotEmpty() }
                } catch (_: Exception) {
                    null
                }
            } else {
                null
            }
            result.frames.add(
                StackFrameInfo(
                    index = index,
                    functionName = frameName,
                    filePath = location?.filePath ?: "",
                    lineNumber = location?.lineNumber ?: 0,
                    lineNumberReliable = location != null,
                    columnNumber = location?.columnNumber ?: 1,
                    module = parseModuleFromFrameName(frameName),
                    language = inferredLanguage,
                    returnType = null,
                    arguments = arguments
                )
            )
        }
        return result
    }

    private fun buildThreadsResult(
        suspendContext: XSuspendContext,
        includeFrames: Boolean = true,
        maxFramesPerThread: Int = threadFramesMaxPerThread,
        maxNodes: Int = threadFrameExpansionMaxNodes,
        budgetMs: Long = threadEnumerationBudgetMs
    ): ThreadsResult {
        val stacks = suspendContext.executionStacks?.toList() ?: emptyList()
        val active = suspendContext.activeExecutionStack
        val result = ThreadsResult(capability = "partial_thread_metadata")
        val budget = VariableExpansionBudget(maxNodes, budgetMs)
        val partialReasons = mutableListOf<String>()

        if (stacks.isEmpty()) {
            result.error = "No execution stacks available"
            return result
        }

        for (stack in stacks) {
            if (!budget.canContinue()) {
                break
            }

            val id = threadId(stack)
            val location = getStackLocation(stack)
            val threadFrames = if (includeFrames) {
                try {
                    val collected = collectStackFramesWithStatus(stack, maxFramesPerThread, budget.remainingMs())
                    collected.partialReason?.let { addPartialReason(partialReasons, "thread $id: $it") }
                    val frameInfos = mutableListOf<StackFrameInfo>()

                    for ((index, frame) in collected.frames.withIndex()) {
                        if (!budget.tryEnterNode()) {
                            break
                        }

                        try {
                            val loc = getFrameLocation(frame)
                            val frameName = getFrameName(frame)
                            frameInfos.add(
                                StackFrameInfo(
                                    index = index,
                                    functionName = frameName,
                                    filePath = loc?.filePath ?: "",
                                    lineNumber = loc?.lineNumber ?: 0,
                                    lineNumberReliable = loc != null,
                                    columnNumber = loc?.columnNumber ?: 1,
                                    module = parseModuleFromFrameName(frameName),
                                    language = inferLanguage(loc?.filePath, frameName),
                                    returnType = null
                                )
                            )
                        } catch (ex: Exception) {
                            addPartialReason(partialReasons, "thread $id frame $index: ${ex.message ?: "unavailable"}")
                        }
                    }

                    frameInfos.takeIf { it.isNotEmpty() }
                } catch (ex: Exception) {
                    addPartialReason(partialReasons, "thread $id: ${ex.message ?: "frame enumeration failed"}")
                    null
                }
            } else {
                null
            }
            result.threads.add(
                ThreadInfo(
                    id = id,
                    name = stack.displayName,
                    isCurrent = stack == active,
                    isFrozen = false,
                    location = location,
                    priority = "unknown",
                    suspendCount = -1,
                    frames = threadFrames?.toMutableList()
                )
            )
        }
        result.currentThreadId = selectedThreadId ?: (active?.let { threadId(it) } ?: 0)
        if (budget.isExceeded) {
            result.partial = true
            result.error = budget.reason
        } else if (partialReasons.isNotEmpty()) {
            result.partial = true
            result.error = partialReasons.joinToString("; ")
        }
        return result
    }

    private fun determineThreadsSnapshotStatus(threads: ThreadsResult?, stable: Boolean): String {
        if (threads == null) return "unavailable"
        if (!stable) return if (threads.threads.isNotEmpty()) "unstable" else "unavailable"
        if (threads.partial) return "partial"
        return if (threads.threads.isNotEmpty()) "ok" else "unavailable"
    }

    private fun suspendSnapshotToken(session: XDebugSession, suspendContext: XSuspendContext): String {
        val active = suspendContext.activeExecutionStack
        val activeId = active?.let { threadId(it) } ?: 0
        val frame = getSelectedFrame(session)
        val frameName = frame?.let { getFrameName(it) } ?: ""
        val frameLine = frame?.sourcePosition?.line?.plus(1) ?: 0
        val position = session.currentPosition ?: session.topFramePosition
        val filePath = position?.file?.path ?: ""
        val line = position?.line?.plus(1) ?: 0
        return "$activeId|$filePath|$line|$frameName|$frameLine"
    }

    private fun getSelectedStack(suspendContext: XSuspendContext): XExecutionStack? {
        val stacks = suspendContext.executionStacks?.toList() ?: emptyList()
        if (stacks.isEmpty()) return null
        val selected = selectedThreadId
        if (selected != null) {
            val match = stacks.firstOrNull { threadId(it) == selected }
            if (match != null) return match
        }
        return suspendContext.activeExecutionStack ?: stacks.firstOrNull()
    }

    private fun getSelectedFrame(session: XDebugSession): XStackFrame? {
        val suspendContext = session.suspendContext ?: return session.currentStackFrame
        val stack = getSelectedStack(suspendContext) ?: return session.currentStackFrame
        val frames = collectStackFrames(stack)
        val index = (selectedFrameIndex ?: 0).coerceAtLeast(0)
        return frames.getOrNull(index) ?: stack.topFrame ?: session.currentStackFrame
    }

    private data class StackFrameCollection(
        val frames: List<XStackFrame>,
        val partialReason: String? = null
    )

    private fun collectStackFrames(stack: XExecutionStack): List<XStackFrame> =
        collectStackFramesWithStatus(stack, 256, 2000L).frames

    private fun collectStackFramesWithStatus(
        stack: XExecutionStack,
        maxFrames: Int,
        budgetMs: Long
    ): StackFrameCollection {
        val frames = mutableListOf<XStackFrame>()
        val latch = CountDownLatch(1)
        val partialReason = AtomicReference<String?>(null)
        val frameLimit = if (maxFrames <= 0) Int.MAX_VALUE else maxFrames
        val timeoutMs = budgetMs.coerceAtLeast(1L)

        stack.computeStackFrames(0, object : XExecutionStack.XStackFrameContainer {
            override fun addStackFrames(stackFrames: MutableList<out XStackFrame>, last: Boolean) {
                val remaining = frameLimit - frames.size
                if (remaining > 0) {
                    frames.addAll(stackFrames.take(remaining))
                }
                if (!last && frames.size >= frameLimit) {
                    partialReason.compareAndSet(
                        null,
                        "Thread frame enumeration truncated after $frameLimit frames per thread"
                    )
                    latch.countDown()
                    return
                }
                if (last) {
                    latch.countDown()
                }
            }

            override fun errorOccurred(errorMessage: String) {
                partialReason.compareAndSet(null, errorMessage.ifBlank { "Stack frame enumeration failed" })
                latch.countDown()
            }

            override fun isObsolete(): Boolean = false
        })

        val completed = latch.await(timeoutMs, TimeUnit.MILLISECONDS)
        if (!completed) {
            partialReason.compareAndSet(null, "Stack frame enumeration timed out after ${timeoutMs}ms")
        }
        return StackFrameCollection(frames.toList(), partialReason.get())
    }

    private fun normalizeThreadFrameLimit(maxFramesPerThread: Int): Int = when {
        maxFramesPerThread < 0 -> threadFramesMaxPerThread
        maxFramesPerThread == 0 -> Int.MAX_VALUE
        else -> maxFramesPerThread.coerceAtMost(4096)
    }

    private fun normalizeThreadNodeLimit(maxNodes: Int): Int = when {
        maxNodes < 0 -> threadFrameExpansionMaxNodes
        maxNodes == 0 -> Int.MAX_VALUE
        else -> maxNodes.coerceAtMost(100000)
    }

    private fun normalizeThreadBudgetMs(budgetMs: Int): Long = when {
        budgetMs < 0 -> threadEnumerationBudgetMs
        budgetMs == 0 -> 115000L
        else -> budgetMs.coerceAtMost(115000).toLong()
    }

    private fun addPartialReason(partialReasons: MutableList<String>, reason: String) {
        if (reason.isBlank()) return
        if (partialReasons.size < 8) {
            partialReasons.add(reason)
        } else if (partialReasons.size == 8) {
            partialReasons.add("additional thread/frame errors omitted")
        }
    }

    private class VariableExpansionBudget(
        private val maxNodes: Int,
        private val maxElapsedMs: Long
    ) {
        private val startedAt = System.nanoTime()
        private var nodes = 0
        var reason: String? = null
            private set

        val isExceeded: Boolean
            get() = reason != null

        fun tryEnterNode(): Boolean {
            if (!canContinue()) return false
            nodes += 1
            return true
        }

        fun canContinue(): Boolean {
            if (reason != null) return false
            if (nodes >= maxNodes) {
                reason = "Expansion truncated after $maxNodes nodes"
                return false
            }
            val elapsedMs = (System.nanoTime() - startedAt) / 1_000_000L
            if (elapsedMs >= maxElapsedMs) {
                reason = "Expansion truncated after ${maxElapsedMs}ms"
                return false
            }
            return true
        }

        fun remainingMs(): Long {
            if (reason != null) return 1L
            val elapsedMs = (System.nanoTime() - startedAt) / 1_000_000L
            return (maxElapsedMs - elapsedMs).coerceAtLeast(1L)
        }
    }

    private fun newVariableExpansionBudget(): VariableExpansionBudget =
        VariableExpansionBudget(variableExpansionMaxNodes, variableExpansionBudgetMs)

    private fun collectVariables(
        container: XValueContainer,
        currentDepth: Int,
        maxDepth: Int,
        budget: VariableExpansionBudget = newVariableExpansionBudget()
    ): List<VariableInfo> {
        if (currentDepth > maxDepth || !budget.canContinue()) return emptyList()
        val result = mutableListOf<VariableInfo>()
        val latch = CountDownLatch(1)

        container.computeChildren(object : XCompositeNode {
            override fun addChildren(children: XValueChildrenList, last: Boolean) {
                result.addAll(convertChildren(children, currentDepth, maxDepth, budget))
                if (last) {
                    latch.countDown()
                }
            }

            override fun tooManyChildren(remaining: Int) {
                latch.countDown()
            }

            override fun setAlreadySorted(alreadySorted: Boolean) {}

            override fun setErrorMessage(errorMessage: String) {
                latch.countDown()
            }

            override fun setErrorMessage(errorMessage: String, link: XDebuggerTreeNodeHyperlink?) {
                latch.countDown()
            }

            override fun setMessage(
                message: String,
                icon: Icon?,
                attributes: SimpleTextAttributes,
                link: XDebuggerTreeNodeHyperlink?
            ) {
                latch.countDown()
            }

            override fun isObsolete(): Boolean = false
        })

        latch.await(2, TimeUnit.SECONDS)
        return result
    }

    private fun convertChildren(
        children: XValueChildrenList,
        currentDepth: Int,
        maxDepth: Int,
        budget: VariableExpansionBudget
    ): List<VariableInfo> {
        val result = mutableListOf<VariableInfo>()

        children.topGroups?.forEach { group ->
            if (!budget.canContinue()) return result
            describeGroup(group, currentDepth, maxDepth, budget)?.let { result.add(it) }
        }

        for (i in 0 until children.size()) {
            if (!budget.canContinue()) break
            val name = children.getName(i)
            val value = children.getValue(i)
            describeValue(name, value, currentDepth, maxDepth, budget)?.let { result.add(it) }
        }

        children.bottomGroups?.forEach { group ->
            if (!budget.canContinue()) return result
            describeGroup(group, currentDepth, maxDepth, budget)?.let { result.add(it) }
        }

        return result
    }

    private fun describeGroup(
        group: XValueGroup,
        currentDepth: Int,
        maxDepth: Int,
        budget: VariableExpansionBudget
    ): VariableInfo? {
        if (!budget.tryEnterNode()) return null
        val children = if (currentDepth < maxDepth && budget.canContinue()) {
            collectVariables(group, currentDepth + 1, maxDepth, budget).toMutableList()
        } else {
            null
        }
        return VariableInfo(
            name = group.name,
            value = group.comment,
            type = "Group",
            hasChildren = !children.isNullOrEmpty(),
            children = children,
            isReadOnly = true
        )
    }

    private data class ValuePresentationResult(
        val value: String?,
        val type: String?,
        val hasChildren: Boolean,
        val isReadOnly: Boolean
    )

    private fun describeValue(value: XValue): ValuePresentationResult {
        var capturedValue: String? = null
        var capturedType: String? = null
        var hasChildren = false
        var presentation: XValuePresentation? = null
        val latch = CountDownLatch(1)

        value.computePresentation(object : XValueNode {
            override fun setPresentation(icon: Icon?, valueText: String?, type: String, hasChildrenValue: Boolean) {
                capturedValue = valueText
                capturedType = type
                hasChildren = hasChildrenValue
                latch.countDown()
            }

            override fun setPresentation(icon: Icon?, presentationValue: XValuePresentation, hasChildrenValue: Boolean) {
                presentation = presentationValue
                hasChildren = hasChildrenValue
                latch.countDown()
            }

            override fun setFullValueEvaluator(evaluator: XFullValueEvaluator) {}

            override fun isObsolete(): Boolean = false
        }, XValuePlace.TREE)

        latch.await(2, TimeUnit.SECONDS)

        if (presentation != null && capturedValue.isNullOrBlank()) {
            capturedValue = renderPresentation(presentation!!)
            capturedType = presentation!!.type
        }

        if (capturedValue.isNullOrBlank()) {
            capturedValue = value.toString()
        }

        val isReadOnly = value.modifier == null
        return ValuePresentationResult(
            value = capturedValue,
            type = capturedType,
            hasChildren = hasChildren,
            isReadOnly = isReadOnly
        )
    }

    private fun describeValue(
        name: String,
        value: XValue,
        currentDepth: Int,
        maxDepth: Int,
        budget: VariableExpansionBudget
    ): VariableInfo? {
        if (!budget.tryEnterNode()) return null
        val presentation = describeValue(value)
        val children = if (presentation.hasChildren && currentDepth < maxDepth && budget.canContinue()) {
            collectVariables(value, currentDepth + 1, maxDepth, budget).toMutableList()
        } else {
            null
        }

        val displayName = if (name.isNotBlank()) name else (value.evaluationExpression ?: name)

        return VariableInfo(
            name = displayName,
            type = presentation.type,
            value = presentation.value,
            hasChildren = presentation.hasChildren,
            children = children,
            isReadOnly = presentation.isReadOnly
        )
    }

    private fun renderPresentation(presentation: XValuePresentation): String {
        val builder = StringBuilder()
        presentation.renderValue(object : XValuePresentation.XValueTextRenderer {
            override fun renderValue(value: String) {
                builder.append(value)
            }

            override fun renderStringValue(value: String) {
                builder.append(value)
            }

            override fun renderNumericValue(value: String) {
                builder.append(value)
            }

            override fun renderKeywordValue(value: String) {
                builder.append(value)
            }

            override fun renderValue(value: String, key: TextAttributesKey) {
                builder.append(value)
            }

            override fun renderStringValue(value: String, additionalSpecialCharsToHighlight: String?, maxLength: Int) {
                builder.append(value)
            }

            override fun renderComment(comment: String) {
                if (builder.isEmpty()) {
                    builder.append(comment)
                }
            }

            override fun renderSpecialSymbol(symbol: String) {
                builder.append(symbol)
            }

            override fun renderError(error: String) {
                builder.append(error)
            }
        })
        return builder.toString()
    }

    private fun getFrameLocation(frame: XStackFrame): LocationInfo? {
        val position = frame.sourcePosition ?: return null
        return LocationInfo(
            filePath = position.file.path,
            functionName = getFrameName(frame),
            lineNumber = position.line + 1,
            columnNumber = 1
        )
    }

    private fun getStackLocation(stack: XExecutionStack): String? {
        val top = stack.topFrame ?: return null
        val location = getFrameLocation(top) ?: return null
        return "${location.functionName} (${location.filePath}:${location.lineNumber})"
    }

    private fun getFrameName(frame: XStackFrame): String {
        frame.equalityObject?.let { return it.toString() }
        val builder = StringBuilder()
        try {
            frame.customizePresentation(object : ColoredTextContainer {
                override fun append(text: String, attributes: SimpleTextAttributes) {
                    builder.append(text)
                }

                override fun append(text: String, attributes: SimpleTextAttributes, tag: Any?) {
                    builder.append(text)
                }

                override fun append(coloredText: ColoredText) {
                    coloredText.fragments().forEach { fragment ->
                        builder.append(fragment.fragmentText())
                    }
                }

                override fun setIcon(icon: Icon?) {}

                override fun setToolTipText(text: String?) {}
            })
        } catch (_: Exception) {
        }
        return builder.toString().ifBlank { frame.toString() }
    }

    private fun parseModuleFromFrameName(frameName: String): String? {
        val normalized = frameName.trim()
        if (normalized.isEmpty()) return null
        val bangIndex = normalized.indexOf('!')
        if (bangIndex > 0) {
            return normalized.substring(0, bangIndex)
        }
        return null
    }

    private fun inferLanguage(filePath: String?, frameName: String): String? {
        val lowerPath = filePath?.lowercase(Locale.ROOT) ?: ""
        return when {
            lowerPath.endsWith(".cs") -> "C#"
            lowerPath.endsWith(".vb") -> "VB"
            lowerPath.endsWith(".fs") -> "F#"
            lowerPath.endsWith(".cpp") || lowerPath.endsWith(".cc") || lowerPath.endsWith(".cxx") -> "C++"
            lowerPath.endsWith(".c") -> "C"
            lowerPath.endsWith(".rs") -> "Rust"
            lowerPath.endsWith(".go") -> "Go"
            lowerPath.endsWith(".java") -> "Java"
            lowerPath.endsWith(".kt") || lowerPath.endsWith(".kts") -> "Kotlin"
            frameName.contains("::") -> "C++"
            else -> null
        }
    }

    private fun currentProject(): Project? {
        return ProjectManager.getInstance().openProjects.firstOrNull { !it.isDisposed }
    }

    private fun currentSession(): XDebugSession? {
        val projects = ProjectManager.getInstance().openProjects
        for (project in projects) {
            if (project.isDisposed) continue
            val session = currentSession(project)
            if (session != null) return session
        }
        return null
    }

    private fun currentSession(project: Project): XDebugSession? {
        return XDebuggerManager.getInstance(project).currentSession
    }

    private fun threadId(stack: XExecutionStack): Int {
        val nativeMethod = stack.javaClass.methods.firstOrNull { method ->
            method.name == "getNativeThreadId" && method.parameterCount == 0
        }
        if (nativeMethod != null) {
            val value = nativeMethod.invoke(stack)
            if (value is Number) {
                return value.toInt()
            }
        }
        val display = stack.displayName
        val match = Regex("\\d+").find(display ?: "")
        return match?.value?.toIntOrNull() ?: stack.hashCode()
    }

    private fun normalizeVariableValue(value: String?): String? {
        val normalized = value?.trim()?.trim('"')
        if (normalized.isNullOrBlank()) return null
        if (normalized.equals("null", ignoreCase = true)) return null
        return normalized
    }

    private fun extractVariableByName(children: List<VariableInfo>?, vararg names: String): VariableInfo? {
        if (children.isNullOrEmpty()) return null
        val candidates = names.map { it.trim() }.filter { it.isNotEmpty() }
        if (candidates.isEmpty()) return null

        return children.firstOrNull { variable ->
            val rawName = variable.name.trim()
            candidates.any { candidate ->
                rawName.equals(candidate, ignoreCase = true) ||
                    rawName.endsWith(".$candidate", ignoreCase = true)
            }
        }
    }

    private fun extractVariableValueByName(children: List<VariableInfo>?, vararg names: String): String? {
        val variable = extractVariableByName(children, *names) ?: return null
        return normalizeVariableValue(variable.value)
    }

    private fun extractVariableChildValue(variable: VariableInfo, vararg names: String): String? {
        return extractVariableValueByName(variable.children, *names)
    }

    private fun toInnerException(variable: VariableInfo?, depth: Int = 0): ExceptionResult? {
        if (variable == null || depth >= 10) return null
        val message = extractVariableChildValue(variable, "Message") ?: normalizeVariableValue(variable.value)
        if (message.isNullOrBlank()) return null

        val nestedInner = extractVariableByName(variable.children, "InnerException")
        val innerResult = toInnerException(nestedInner, depth + 1)

        return ExceptionResult(
            hasException = true,
            capability = "limited_exception_details",
            type = variable.type,
            message = message,
            stackTrace = extractVariableChildValue(variable, "StackTrace"),
            source = extractVariableChildValue(variable, "Source"),
            hResult = extractVariableChildValue(variable, "HResult"),
            innerException = innerResult
        )
    }

    private fun isSystemPath(path: String?): Boolean {
        val normalized = path?.lowercase(Locale.ROOT) ?: return false
        return normalized.contains("\\windows\\") ||
            normalized.contains("/windows/") ||
            normalized.contains("\\system32\\") ||
            normalized.contains("/system32/") ||
            normalized.contains("\\syswow64\\") ||
            normalized.contains("/syswow64/")
    }

    private fun registerCategory(name: String): String {
        val normalized = name.lowercase(Locale.ROOT)
        return when {
            normalized == "rip" || normalized == "eip" || normalized.contains("ip") -> "Instruction Pointer"
            normalized == "rsp" || normalized == "esp" || normalized == "rbp" || normalized == "ebp" -> "Stack"
            normalized.contains("flag") -> "Flags"
            else -> "General Purpose"
        }
    }

    private data class EvaluationResult(
        val value: XValue?,
        val errorMessage: String?
    )

    private fun getEvaluator(session: XDebugSession): XDebuggerEvaluator? {
        val frame = getSelectedFrame(session) ?: session.currentStackFrame
        val evaluator = frame?.evaluator
        if (evaluator != null) return evaluator
        return session.debugProcess.evaluator
    }

    private fun evaluateValue(evaluator: XDebuggerEvaluator, expression: String, position: XSourcePosition?): EvaluationResult {
        val latch = CountDownLatch(1)
        val valueRef = AtomicReference<XValue?>(null)
        val errorRef = AtomicReference<String?>(null)

        evaluator.evaluate(expression, object : XDebuggerEvaluator.XEvaluationCallback {
            override fun evaluated(value: XValue) {
                valueRef.set(value)
                latch.countDown()
            }

            override fun errorOccurred(errorMessage: String) {
                errorRef.set(errorMessage)
                latch.countDown()
            }

            @Suppress("UNUSED")
            fun invalidExpression(message: String) {
                errorRef.set(message)
                latch.countDown()
            }
        }, position)

        latch.await(2, TimeUnit.SECONDS)
        return EvaluationResult(valueRef.get(), errorRef.get())
    }

    private fun resolveAddressExpression(address: String, evaluator: XDebuggerEvaluator, position: XSourcePosition?): String? {
        val trimmed = address.trim()
        if (trimmed.isBlank()) return null

        if (trimmed.startsWith("0x", ignoreCase = true)) return trimmed

        val numeric = parseNumber(trimmed)
        if (numeric != null) {
            return "0x" + numeric.toString(16)
        }

        val eval = evaluateValue(evaluator, trimmed, position)
        val evalValue = eval.value?.let { describeValue(it).value }
        val parsed = parseNumber(evalValue)
        if (parsed != null) {
            return "0x" + parsed.toString(16)
        }

        return trimmed
    }

    private fun parseNumber(value: String?): Long? {
        if (value.isNullOrBlank()) return null
        val text = value.trim().trim('"')

        // Only parse values that clearly start with a numeric literal.
        // This avoids mis-parsing arbitrary strings like "ptr1" as address 1.
        val hexMatch = Regex("^0x([0-9a-fA-F]+)", RegexOption.IGNORE_CASE).find(text)
        if (hexMatch != null) {
            val hex = hexMatch.groupValues[1]
            return try {
                java.lang.Long.parseUnsignedLong(hex, 16)
            } catch (_: Exception) {
                null
            }
        }

        val decMatch = Regex("^[+-]?\\d+").find(text)
        return decMatch?.value?.toLongOrNull()
    }

    private fun parsePattern(pattern: String): ByteArray {
        val trimmed = pattern.trim().trim('"')
        if (trimmed.isBlank()) return byteArrayOf()

        val tokens = trimmed.split(Regex("\\s+")).filter { it.isNotBlank() }
        if (tokens.isNotEmpty() && tokens.all { it.matches(Regex("[0-9a-fA-F]{2}")) }) {
            val bytes = ByteArray(tokens.size)
            tokens.forEachIndexed { index, token ->
                bytes[index] = token.toInt(16).toByte()
            }
            return bytes
        }

        return trimmed.toByteArray(StandardCharsets.UTF_8)
    }

    private data class DecodedInstruction(
        val address: Long,
        val instruction: DisassemblyInstruction
    )

    private fun buildNativeDisassemblyWindow(
        instructionPointer: Long,
        linesBefore: Int,
        linesAfter: Int,
        evaluator: XDebuggerEvaluator,
        position: XSourcePosition?,
        is64Bit: Boolean,
        sourceLine: Int?
    ): DisassemblyResult {
        val safeBefore = linesBefore.coerceAtLeast(0)
        val safeAfter = linesAfter.coerceAtLeast(0)

        var preBytes = ((safeBefore + 4) * 16).coerceAtLeast(64)
        var postBytes = ((safeAfter + 8) * 16).coerceAtLeast(128)
        val maxWindow = 4096
        if (preBytes + postBytes > maxWindow) {
            val scale = maxWindow.toDouble() / (preBytes + postBytes).toDouble()
            preBytes = (preBytes * scale).toInt().coerceAtLeast(32)
            postBytes = maxWindow - preBytes
        }

        val startAddress = if (instructionPointer > preBytes) instructionPointer - preBytes else 0L
        val bytes = readMemoryBytes(startAddress, preBytes + postBytes, evaluator, position)
            ?: return DisassemblyResult(error = "Failed to read memory for disassembly")

        val decoded = decodeInstructions(bytes, startAddress, is64Bit, safeBefore + safeAfter + 64)
        if (decoded.isEmpty()) {
            return DisassemblyResult(error = "No instructions decoded")
        }

        var currentIndex = decoded.indexOfFirst { it.address == instructionPointer }
        if (currentIndex < 0) {
            currentIndex = decoded.indexOfLast { it.address <= instructionPointer }
        }
        if (currentIndex < 0) currentIndex = 0

        val startIndex = (currentIndex - safeBefore).coerceAtLeast(0)
        val endIndex = (currentIndex + safeAfter + 1).coerceAtMost(decoded.size)

        val instructions = mutableListOf<DisassemblyInstruction>()
        for (i in startIndex until endIndex) {
            val entry = decoded[i]
            val isCurrent = i == currentIndex
            instructions.add(
                entry.instruction.copy(
                    isCurrent = isCurrent,
                    sourceLine = if (isCurrent) sourceLine else null
                )
            )
        }

        return DisassemblyResult(
            currentAddress = formatAddress(instructionPointer, is64Bit),
            instructions = instructions,
            is64Bit = is64Bit,
            totalInstructions = instructions.size
        )
    }

    private fun buildNativeDisassemblyAt(
        address: Long,
        count: Int,
        evaluator: XDebuggerEvaluator,
        position: XSourcePosition?,
        is64Bit: Boolean,
        sourceLine: Int?
    ): DisassemblyResult {
        val maxInstructions = count.coerceIn(1, 256)
        val bytesToRead = (maxInstructions * 16).coerceAtMost(4096)
        val bytes = readMemoryBytes(address, bytesToRead, evaluator, position)
            ?: return DisassemblyResult(error = "Failed to read memory for disassembly")

        val decoded = decodeInstructions(bytes, address, is64Bit, maxInstructions)
        if (decoded.isEmpty()) {
            return DisassemblyResult(error = "No instructions decoded")
        }

        val instructions = decoded.take(maxInstructions).map { entry ->
            val isCurrent = entry.address == address
            entry.instruction.copy(
                isCurrent = isCurrent,
                sourceLine = if (isCurrent) sourceLine else null
            )
        }.toMutableList()

        return DisassemblyResult(
            currentAddress = formatAddress(address, is64Bit),
            instructions = instructions,
            is64Bit = is64Bit,
            totalInstructions = instructions.size
        )
    }

    private fun decodeInstructions(
        bytes: ByteArray,
        startAddress: Long,
        is64Bit: Boolean,
        maxInstructions: Int
    ): List<DecodedInstruction> {
        val reader = ByteArrayCodeReader(bytes)
        val decoder = Decoder(if (is64Bit) 64 else 32, reader, startAddress)
        val formatter = IntelFormatter()
        val output = StringOutput()
        val result = mutableListOf<DecodedInstruction>()

        while (reader.canReadByte() && result.size < maxInstructions) {
            val startPos = reader.position
            val instruction = decoder.decode()
            val endPos = reader.position
            if (instruction.isInvalid) break
            if (endPos <= startPos) break

            output.reset()
            formatter.format(instruction, output)
            val formatted = output.toStringAndReset()
            val (mnemonic, operands) = splitMnemonicOperands(formatted)

            val opcodeBytes = bytes.copyOfRange(startPos, endPos)
            val opcode = formatBytes(opcodeBytes)

            val flow = instruction.flowControl
            val isCall = flow == FlowControl.CALL || flow == FlowControl.INDIRECT_CALL ||
                instruction.isCallNear || instruction.isCallFar || instruction.isCallNearIndirect || instruction.isCallFarIndirect
            val isJump = flow == FlowControl.CONDITIONAL_BRANCH ||
                flow == FlowControl.UNCONDITIONAL_BRANCH || flow == FlowControl.INDIRECT_BRANCH
            val isReturn = flow == FlowControl.RETURN

            val branchTarget = if (isCall || isJump) {
                val target = instruction.nearBranchTarget
                if (target != 0L) formatAddressValue(target) else null
            } else {
                null
            }

            val address = instruction.ip
            val disassembly = DisassemblyInstruction(
                address = formatAddress(address, is64Bit),
                opcode = opcode,
                instruction = formatted,
                mnemonic = mnemonic,
                operands = operands,
                isCall = isCall,
                isJump = isJump,
                isReturn = isReturn,
                branchTarget = branchTarget,
                length = instruction.length
            )
            result.add(DecodedInstruction(address, disassembly))
        }

        return result
    }

    private fun splitMnemonicOperands(formatted: String): Pair<String?, String?> {
        val trimmed = formatted.trim()
        if (trimmed.isBlank()) return null to null
        val space = trimmed.indexOf(' ')
        return if (space > 0) {
            trimmed.substring(0, space) to trimmed.substring(space + 1).trim()
        } else {
            trimmed to null
        }
    }

    private fun getInstructionPointer(
        evaluator: XDebuggerEvaluator,
        position: XSourcePosition?
    ): Pair<Long, Boolean>? {
        fun evalRegister(expr: String): Long? {
            val eval = evaluateValue(evaluator, expr, position)
            val value = eval.value?.let { describeValue(it).value }
            return parseNumber(value)
        }

        val rip = evalRegister("@rip")
        if (rip != null) return rip to true
        val eip = evalRegister("@eip")
        if (eip != null) return eip to false
        return null
    }

    private data class MemoryReadSnapshot(
        val bytes: ByteArray,
        val requestedSize: Int,
        val bytesRead: Int,
        val failedReads: Int,
        val partial: Boolean
    )

    private fun readMemorySnapshot(
        address: Long,
        size: Int,
        evaluator: XDebuggerEvaluator,
        position: XSourcePosition?
    ): MemoryReadSnapshot {
        val readSize = size.coerceIn(1, 0x100000)
        val addressHex = formatAddressValue(address)
        val collected = ArrayList<Byte>(readSize)
        var offset = 0

        while (offset < readSize) {
            val remaining = readSize - offset
            val chunk = when {
                remaining >= 256 -> 256
                remaining >= 64 -> 64
                remaining >= 16 -> 16
                else -> remaining
            }

            var block = tryReadMemoryBlock(addressHex, offset, chunk, evaluator, position)
            if (block == null || block.isEmpty()) {
                if (chunk > 1) {
                    val fallbackSize = minOf(16, remaining)
                    block = tryReadMemoryBlock(addressHex, offset, fallbackSize, evaluator, position)
                }
                if (block == null || block.isEmpty()) {
                    block = tryReadMemoryBlock(addressHex, offset, 1, evaluator, position)
                }
            }

            if (block == null || block.isEmpty()) {
                break
            }

            block.forEach { collected.add(it) }
            offset += block.size
        }

        val bytes = collected.toByteArray()
        val failed = (readSize - bytes.size).coerceAtLeast(0)
        return MemoryReadSnapshot(
            bytes = bytes,
            requestedSize = readSize,
            bytesRead = bytes.size,
            failedReads = failed,
            partial = failed > 0
        )
    }

    private fun tryReadMemoryBlock(
        addressHex: String,
        offset: Int,
        size: Int,
        evaluator: XDebuggerEvaluator,
        position: XSourcePosition?
    ): ByteArray? {
        if (size <= 0) return null
        val expr =
            "System.Convert.ToBase64String(System.Linq.Enumerable.Range($offset, $size).Select(i => " +
                "System.Runtime.InteropServices.Marshal.ReadByte((System.IntPtr)(0x$addressHex), i)).ToArray())"
        val eval = evaluateValue(evaluator, expr, position)
        val raw = eval.value?.let { describeValue(it).value }?.trim()
        val base64 = raw?.trim('"')
        if (base64.isNullOrBlank()) {
            return null
        }
        return try {
            val decoded = Base64.getDecoder().decode(base64)
            if (decoded.isEmpty()) null else decoded
        } catch (_: Exception) {
            null
        }
    }

    private fun readMemoryBytes(
        address: Long,
        size: Int,
        evaluator: XDebuggerEvaluator,
        position: XSourcePosition?
    ): ByteArray? {
        val snapshot = readMemorySnapshot(address, size.coerceIn(1, 4096), evaluator, position)
        return if (snapshot.bytesRead > 0) snapshot.bytes else null
    }

    private fun formatBytes(bytes: ByteArray): String {
        return bytes.joinToString(" ") { b -> String.format("%02X", b.toInt() and 0xFF) }
    }

    private fun formatMemoryRows(baseAddress: Long, bytes: ByteArray): String {
        if (bytes.isEmpty()) return ""
        val rows = StringBuilder()
        val bytesPerRow = 16
        var offset = 0
        while (offset < bytes.size) {
            val count = minOf(bytesPerRow, bytes.size - offset)
            val lineBytes = bytes.copyOfRange(offset, offset + count)
            val hex = lineBytes.joinToString(" ") { String.format("%02X", it.toInt() and 0xFF) }
            val ascii = lineBytes.map { b ->
                val c = (b.toInt() and 0xFF).toChar()
                if (c in '\u0020'..'\u007E') c else '.'
            }.joinToString("")
            rows.append("0x")
                .append(formatAddressValue(baseAddress + offset.toLong()))
                .append(": ")
                .append(hex)
                .append(" | ")
                .append(ascii)
                .append('\n')
            offset += count
        }
        return rows.toString().trimEnd()
    }

    private fun formatAddress(address: Long, is64Bit: Boolean): String {
        val width = if (is64Bit) 16 else 8
        val hex = formatAddressValue(address).padStart(width, '0')
        return "0x$hex"
    }

    private fun formatAddressValue(address: Long): String {
        return java.lang.Long.toUnsignedString(address, 16).uppercase(Locale.ROOT)
    }

    private fun buildSourceDisassembly(
        position: XSourcePosition,
        linesBefore: Int,
        linesAfter: Int,
        lineOverride: Int? = null
    ): DisassemblyResult {
        val path = position.file.path
        val file = File(path)
        if (!file.exists()) {
            return DisassemblyResult(error = "Source file not found: $path")
        }

        val lines = file.readLines(StandardCharsets.UTF_8)
        val currentLine = (lineOverride ?: (position.line + 1)).coerceIn(1, lines.size)
        val start = (currentLine - 1 - linesBefore).coerceAtLeast(0)
        val end = (currentLine - 1 + linesAfter).coerceAtMost(lines.size - 1)

        val instructions = mutableListOf<DisassemblyInstruction>()
        for (index in start..end) {
            val lineNo = index + 1
            val address = "0x" + lineNo.toString(16)
            instructions.add(
                DisassemblyInstruction(
                    address = address,
                    opcode = "src",
                    instruction = lines[index].trim(),
                    isCurrent = lineNo == currentLine,
                    sourceLine = lineNo
                )
            )
        }

        val frame = currentSession()?.let { getSelectedFrame(it) }

        return DisassemblyResult(
            currentAddress = "0x" + currentLine.toString(16),
            instructions = instructions,
            functionName = frame?.let { getFrameName(it) },
            sourceFile = path,
            totalInstructions = instructions.size
        )
    }

    private fun findLineBreakpointType(file: VirtualFile, line: Int, project: Project): XLineBreakpointType<out XBreakpointProperties<*>>? {
        val types = XBreakpointType.EXTENSION_POINT_NAME.extensionList
        val match = types.filterIsInstance<XLineBreakpointType<*>>().firstOrNull { type ->
            type.canPutAt(file, line, project)
        }
        @Suppress("UNCHECKED_CAST")
        return match as? XLineBreakpointType<out XBreakpointProperties<*>>
    }

    private fun findFunctionBreakpointType(): XBreakpointType<*, *>? {
        val types = XBreakpointType.EXTENSION_POINT_NAME.extensionList
        return types.firstOrNull { type ->
            val id = type.id.lowercase(Locale.ROOT)
            val title = type.title.lowercase(Locale.ROOT)
            id.contains("function") || id.contains("method") || title.contains("function") || title.contains("method")
        }
    }

    private fun applyFunctionName(properties: XBreakpointProperties<*>, functionName: String) {
        val setters = listOf("setFunctionName", "setMethodName", "setFunction", "setMethod", "setName")
        for (setter in setters) {
            val method = properties.javaClass.methods.firstOrNull { it.name == setter && it.parameterCount == 1 }
            if (method != null) {
                method.invoke(properties, functionName)
                return
            }
        }

        val fields = listOf("functionName", "methodName", "function", "method", "name")
        for (fieldName in fields) {
            try {
                val field = properties.javaClass.getDeclaredField(fieldName)
                field.isAccessible = true
                field.set(properties, functionName)
                return
            } catch (_: Exception) {
            }
        }
    }

    private fun toBreakpointInfo(bp: XBreakpoint<*>): BreakpointInfo {
        val condition = bp.conditionExpression?.expression
        val enabled = bp.isEnabled
        val type = bp.type
        val info = BreakpointInfo(
            enabled = enabled,
            condition = condition,
            breakpointType = type.title
        )

        if (bp is XLineBreakpoint<*>) {
            val fileUrl = bp.fileUrl
            val file = VirtualFileManager.getInstance().findFileByUrl(fileUrl)
            info.filePath = file?.path ?: bp.presentableFilePath
            info.lineNumber = bp.line + 1
            val lineType = type as? XLineBreakpointType<XBreakpointProperties<Any>>
            if (lineType != null) {
                @Suppress("UNCHECKED_CAST")
                val lineBp = bp as XLineBreakpoint<XBreakpointProperties<Any>>
                info.column = lineType.getColumn(lineBp)
                info.name = lineType.getDisplayText(lineBp)
            } else {
                info.column = 0
                info.name = type.title
            }
        } else {
            val props = bp.properties
            info.functionName = props?.let { prop ->
                val getters = listOf("getFunctionName", "getMethodName", "getFunction", "getMethod", "getName")
                for (getter in getters) {
                    val method = prop.javaClass.methods.firstOrNull { it.name == getter && it.parameterCount == 0 }
                    if (method != null) {
                        return@let method.invoke(prop)?.toString()
                    }
                }
                null
            }
            info.name = type.title
        }

        return info
    }

    private fun getConsoleText(console: ConsoleView): String? {
        return try {
            val method = console.javaClass.methods.firstOrNull { it.name == "getText" && it.parameterCount == 0 }
            method?.invoke(console) as? String
        } catch (_: Exception) {
            null
        }
    }

    private fun tryInvokeSetNextStatement(session: XDebugSession, position: XSourcePosition): Boolean {
        val process = session.debugProcess
        val candidates = listOf("setNextStatement", "setExecutionPoint", "setNextStatementPosition")
        for (name in candidates) {
            val method = process.javaClass.methods.firstOrNull {
                it.name == name && it.parameterCount == 1 && XSourcePosition::class.java.isAssignableFrom(it.parameterTypes[0])
            }
            if (method != null) {
                method.invoke(process, position)
                return true
            }
        }
        return false
    }

    private fun getStringListParam(params: Map<String, Any?>?, key: String): List<String> {
        val value = params?.get(key) ?: return emptyList()
        return when (value) {
            is String -> listOf(value)
            is Collection<*> -> value.mapNotNull { it?.toString() }
            is Array<*> -> value.mapNotNull { it?.toString() }
            else -> emptyList()
        }
    }

    private inline fun <reified T> getParam(params: Map<String, Any?>?, key: String, default: T): T {
        val value = params?.get(key) ?: return default
        @Suppress("UNCHECKED_CAST")
        return when (T::class) {
            Int::class -> (value as? Number)?.toInt() as? T ?: default
            Long::class -> (value as? Number)?.toLong() as? T ?: default
            Boolean::class -> (value as? Boolean) as? T ?: default
            String::class -> value.toString() as T
            else -> value as? T ?: default
        }
    }
}
