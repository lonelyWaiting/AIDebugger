
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

    fun setPipeName(value: String) {
        pipeName = value
    }

    fun handleMethod(request: DebugRequest): Any? {
        val method = request.method?.lowercase(Locale.ROOT) ?: return null
        return when (method) {
            "get_debug_status" -> getDebugStatus()
            "get_callstack" -> getCallStack()
            "get_locals" -> getLocals()
            "get_threads" -> getThreads()
            "get_modules" -> getModules()
            "get_exception" -> getException()
            "evaluate_expression" -> {
                val expression = getParam(request.params, "expression", "")
                val expand = getParam(request.params, "expandChildren", false)
                val maxDepth = getParam(request.params, "maxDepth", 2)
                evaluateExpression(expression, expand, maxDepth)
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
            "get_full_context" -> getFullContext()
            "ping" -> PingResult(status = "ok", timestamp = Instant.now().toString())
            "get_instance_info" -> getInstanceInfo(getParam(request.params, "pipeName", ""))
            "continue" -> debugContinue()
            "step_into" -> debugStepInto()
            "step_over" -> debugStepOver()
            "step_out" -> debugStepOut()
            "break" -> debugBreak()
            "stop" -> debugStop()
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

        return DebugStatusResult(
            isDebugging = isDebugging,
            isPaused = isPaused,
            debugMode = mode,
            currentProcess = processName,
            currentLocation = location
        )
    }

    private fun getCallStack(): CallStackResult {
        val session = currentSession() ?: return CallStackResult()
        val suspendContext = session.suspendContext ?: return CallStackResult()
        val stack = getSelectedStack(suspendContext)
        val frames = stack?.let { collectStackFrames(it) } ?: emptyList()

        val result = CallStackResult()
        if (stack != null) {
            result.threadId = threadId(stack)
            result.threadName = stack.displayName
        }

        frames.forEachIndexed { index, frame ->
            val location = getFrameLocation(frame)
            result.frames.add(
                StackFrameInfo(
                    index = index,
                    functionName = getFrameName(frame),
                    filePath = location?.filePath ?: "",
                    lineNumber = location?.lineNumber ?: 0,
                    lineNumberReliable = location != null,
                    columnNumber = location?.columnNumber ?: 1
                )
            )
        }

        return result
    }

    private fun getLocals(): LocalsResult {
        val session = currentSession() ?: return LocalsResult()
        val frame = getSelectedFrame(session) ?: return LocalsResult()
        val variables = collectVariables(frame, 0, 2)
        return LocalsResult(frameIndex = selectedFrameIndex ?: 0, variables = variables.toMutableList())
    }

    private fun getThreads(): ThreadsResult {
        val session = currentSession() ?: return ThreadsResult()
        val suspendContext = session.suspendContext ?: return ThreadsResult()
        val stacks = suspendContext.executionStacks?.toList() ?: emptyList()
        val active = suspendContext.activeExecutionStack

        val result = ThreadsResult()
        stacks.forEach { stack ->
            val id = threadId(stack)
            val location = getStackLocation(stack)
            result.threads.add(
                ThreadInfo(
                    id = id,
                    name = stack.displayName,
                    isCurrent = stack == active,
                    isFrozen = false,
                    location = location
                )
            )
        }

        result.currentThreadId = selectedThreadId ?: (active?.let { threadId(it) } ?: 0)
        return result
    }

    private fun getModules(): ModulesResult {
        val session = currentSession() ?: return ModulesResult(error = "No debug session")
        val evaluator = getEvaluator(session) ?: return ModulesResult(error = "No evaluator available")
        val eval = evaluateValue(evaluator, "System.AppDomain.CurrentDomain.GetAssemblies()", session.currentPosition)
        if (eval.value == null) {
            return ModulesResult(error = eval.errorMessage ?: "Unable to enumerate modules")
        }

        val modules = mutableListOf<ModuleInfo>()
        val children = collectVariables(eval.value, 0, 1)
        children.forEachIndexed { index, variable ->
            val name = variable.value ?: variable.name
            val path = variable.value?.takeIf { it.contains(":\\") || it.contains("/") }
            modules.add(
                ModuleInfo(
                    name = name ?: variable.name,
                    path = path,
                    order = index
                )
            )
        }

        return ModulesResult(modules = modules, totalCount = modules.size)
    }

    private fun getException(): ExceptionResult {
        val session = currentSession() ?: return ExceptionResult(hasException = false)
        val evaluator = getEvaluator(session) ?: return ExceptionResult(hasException = false)
        val result = evaluateExpression("\$exception", expandChildren = true, maxDepth = 2)
        if (!result.isValid || result.value == null) {
            return ExceptionResult(hasException = false)
        }
        return ExceptionResult(
            hasException = true,
            type = result.type,
            message = result.value
        )
    }

    private fun evaluateExpression(expression: String, expandChildren: Boolean, maxDepth: Int): EvaluateResult {
        val session = currentSession() ?: return EvaluateResult(expression = expression, isValid = false, errorMessage = "No debug session")
        val evaluator = getEvaluator(session) ?: return EvaluateResult(expression = expression, isValid = false, errorMessage = "No evaluator available")
        val eval = evaluateValue(evaluator, expression, session.currentPosition)
        if (eval.value == null) {
            return EvaluateResult(expression = expression, isValid = false, errorMessage = eval.errorMessage)
        }

        val presentation = describeValue(eval.value)
        val children = if (expandChildren && presentation.hasChildren) {
            collectVariables(eval.value, 0, maxDepth).toMutableList()
        } else {
            null
        }

        return EvaluateResult(
            expression = expression,
            value = presentation.value,
            type = presentation.type,
            isValid = true,
            hasChildren = presentation.hasChildren,
            children = children
        )
    }

    private fun readMemory(address: String, size: Int): MemoryResult {
        val session = currentSession() ?: return MemoryResult(address = address, size = size)
        val evaluator = getEvaluator(session) ?: return MemoryResult(address = address, size = size)
        val normalizedSize = size.coerceIn(1, 4096)
        val addressExpr = resolveAddressExpression(address, evaluator, session.currentPosition)
        if (addressExpr == null) {
            return MemoryResult(address = address, size = normalizedSize)
        }

        val bytes = mutableListOf<Byte>()
        for (offset in 0 until normalizedSize) {
            val expr = "System.Runtime.InteropServices.Marshal.ReadByte((System.IntPtr)($addressExpr), $offset)"
            val eval = evaluateValue(evaluator, expr, session.currentPosition)
            val value = eval.value?.let { describeValue(it).value }
            val parsed = parseNumber(value)
            bytes.add((parsed ?: 0).toByte())
        }

        val hexDump = bytes.joinToString(" ") { String.format("%02X", it) }
        val asciiView = bytes.map { b ->
            val c = b.toInt().toChar()
            if (c.isLetterOrDigit() || c in " .-_:;!?") c else 0x2E.toChar()
        }.joinToString("")

        return MemoryResult(
            address = addressExpr,
            size = normalizedSize,
            bytes = bytes,
            hexDump = hexDump,
            asciiView = asciiView
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

    private fun getFullContext(): FullContextResult {
        return FullContextResult(
            status = getDebugStatus(),
            callstack = getCallStack(),
            locals = getLocals(),
            exception = getException(),
            threads = getThreads(),
            modules = getModules()
        )
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
        val session = currentSession() ?: return RegistersResult(error = "No debug session")
        val evaluator = getEvaluator(session) ?: return RegistersResult(error = "No evaluator available")
        val registers = mutableListOf<RegisterInfo>()
        var is64 = false

        val candidates64 = listOf("rax", "rbx", "rcx", "rdx", "rsi", "rdi", "rbp", "rsp", "rip", "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15", "rflags")
        val candidates32 = listOf("eax", "ebx", "ecx", "edx", "esi", "edi", "ebp", "esp", "eip", "eflags")

        fun tryRegister(name: String): String? {
            val expr = "@$name"
            val eval = evaluateValue(evaluator, expr, session.currentPosition)
            return eval.value?.let { describeValue(it).value }
        }

        candidates64.forEach { reg ->
            val value = tryRegister(reg)
            if (!value.isNullOrBlank()) {
                is64 = true
                registers.add(RegisterInfo(name = reg.uppercase(Locale.ROOT), value = value, hexValue = value))
            }
        }

        if (!is64) {
            candidates32.forEach { reg ->
                val value = tryRegister(reg)
                if (!value.isNullOrBlank()) {
                    registers.add(RegisterInfo(name = reg.uppercase(Locale.ROOT), value = value, hexValue = value))
                }
            }
        }

        return RegistersResult(registers = registers, is64Bit = is64)
    }

    private fun addWatch(expression: String): WatchResult {
        val session = currentSession() ?: return WatchResult(expression = expression, isValid = false, error = "No debug session")
        val evaluator = getEvaluator(session) ?: return WatchResult(expression = expression, isValid = false, error = "No evaluator available")
        val eval = evaluateValue(evaluator, expression, session.currentPosition)
        if (eval.value == null) {
            return WatchResult(expression = expression, isValid = false, error = eval.errorMessage)
        }
        val presentation = describeValue(eval.value)
        return WatchResult(expression = expression, value = presentation.value, type = presentation.type, isValid = true)
    }

    private fun searchMemory(startAddress: String, pattern: String, maxResults: Int, searchSize: Int): MemorySearchResult {
        val session = currentSession() ?: return MemorySearchResult(pattern = pattern, error = "No debug session")
        val evaluator = getEvaluator(session) ?: return MemorySearchResult(pattern = pattern, error = "No evaluator available")
        val addressExpr = resolveAddressExpression(startAddress, evaluator, session.currentPosition)
            ?: return MemorySearchResult(pattern = pattern, error = "Invalid address")

        val bytesToScan = searchSize.coerceIn(1, 0x100000)
        val bytes = ByteArray(bytesToScan)
        for (offset in 0 until bytesToScan) {
            val expr = "System.Runtime.InteropServices.Marshal.ReadByte((System.IntPtr)($addressExpr), $offset)"
            val eval = evaluateValue(evaluator, expr, session.currentPosition)
            val value = eval.value?.let { describeValue(it).value }
            val parsed = parseNumber(value) ?: 0
            bytes[offset] = parsed.toByte()
        }

        val patternBytes = parsePattern(pattern)
        if (patternBytes.isEmpty()) {
            return MemorySearchResult(pattern = pattern, error = "Invalid pattern")
        }

        val matches = mutableListOf<String>()
        val baseAddress = parseNumber(addressExpr) ?: 0L
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
                matches.add("0x" + address.toString(16))
            }
            i++
        }

        return MemorySearchResult(
            pattern = pattern,
            patternBytes = patternBytes.map { String.format("%02X", it) }.toTypedArray(),
            matches = matches,
            totalFound = matches.size,
            bytesSearched = bytes.size
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
        val session = currentSession() ?: return OutputWindowResult(pane = pane, error = "No debug session")
        val console = session.consoleView ?: return OutputWindowResult(pane = pane, error = "No console")
        val text = getConsoleText(console) ?: return OutputWindowResult(pane = pane, error = "Console does not expose text")
        val lines = text.split("\n")
        val count = if (tailLines <= 0) lines.size else tailLines.coerceAtMost(lines.size)
        val slice = if (tailLines <= 0) lines else lines.takeLast(count)
        return OutputWindowResult(
            pane = pane,
            content = slice.joinToString("\n"),
            lineCount = lines.size,
            requestedLines = tailLines,
            truncated = lines.size > count,
            availablePanes = mutableListOf("Debug")
        )
    }

    private fun searchOutput(pattern: String, pane: String, isRegex: Boolean, contextLines: Int, maxMatches: Int): SearchOutputResult {
        val output = getOutputWindow(pane, 0)
        if (output.content == null) {
            return SearchOutputResult(pane = pane, pattern = pattern, isRegex = isRegex, error = output.error)
        }
        val lines = output.content.split("\n")
        val matches = mutableListOf<OutputMatch>()
        val regex = if (isRegex) {
            try {
                Regex(pattern)
            } catch (ex: Exception) {
                return SearchOutputResult(pane = pane, pattern = pattern, isRegex = isRegex, error = "Invalid regex: ${ex.message}")
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
            pattern = pattern,
            isRegex = isRegex,
            matches = matches,
            totalMatches = matches.size,
            truncated = matches.size >= maxMatches
        )
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

    private fun collectStackFrames(stack: XExecutionStack): List<XStackFrame> {
        val frames = mutableListOf<XStackFrame>()
        val latch = CountDownLatch(1)

        stack.computeStackFrames(0, object : XExecutionStack.XStackFrameContainer {
            override fun addStackFrames(stackFrames: MutableList<out XStackFrame>, last: Boolean) {
                frames.addAll(stackFrames)
                if (last || frames.size >= 256) {
                    latch.countDown()
                }
            }

            override fun errorOccurred(errorMessage: String) {
                latch.countDown()
            }

            override fun isObsolete(): Boolean = false
        })

        latch.await(2, TimeUnit.SECONDS)
        return if (frames.size > 256) frames.take(256) else frames
    }

    private fun collectVariables(container: XValueContainer, currentDepth: Int, maxDepth: Int): List<VariableInfo> {
        if (currentDepth > maxDepth) return emptyList()
        val result = mutableListOf<VariableInfo>()
        val latch = CountDownLatch(1)

        container.computeChildren(object : XCompositeNode {
            override fun addChildren(children: XValueChildrenList, last: Boolean) {
                result.addAll(convertChildren(children, currentDepth, maxDepth))
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

    private fun convertChildren(children: XValueChildrenList, currentDepth: Int, maxDepth: Int): List<VariableInfo> {
        val result = mutableListOf<VariableInfo>()

        children.topGroups?.forEach { group ->
            result.add(describeGroup(group, currentDepth, maxDepth))
        }

        for (i in 0 until children.size()) {
            val name = children.getName(i)
            val value = children.getValue(i)
            result.add(describeValue(name, value, currentDepth, maxDepth))
        }

        children.bottomGroups?.forEach { group ->
            result.add(describeGroup(group, currentDepth, maxDepth))
        }

        return result
    }

    private fun describeGroup(group: XValueGroup, currentDepth: Int, maxDepth: Int): VariableInfo {
        val children = if (currentDepth < maxDepth) collectVariables(group, currentDepth + 1, maxDepth).toMutableList() else null
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

    private fun describeValue(name: String, value: XValue, currentDepth: Int, maxDepth: Int): VariableInfo {
        val presentation = describeValue(value)
        val children = if (presentation.hasChildren && currentDepth < maxDepth) {
            collectVariables(value, currentDepth + 1, maxDepth).toMutableList()
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
        val text = value.trim()

        val hexMatch = Regex("0x[0-9a-fA-F]+").find(text)
        if (hexMatch != null) {
            val hex = hexMatch.value.removePrefix("0x")
            return try {
                java.lang.Long.parseUnsignedLong(hex, 16)
            } catch (_: Exception) {
                null
            }
        }

        val decMatch = Regex("-?\\d+").find(text)
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

    private fun readMemoryBytes(
        address: Long,
        size: Int,
        evaluator: XDebuggerEvaluator,
        position: XSourcePosition?
    ): ByteArray? {
        val readSize = size.coerceIn(1, 4096)
        val addressHex = formatAddressValue(address)
        val expr = "System.Convert.ToBase64String(System.Linq.Enumerable.Range(0, $readSize).Select(i => System.Runtime.InteropServices.Marshal.ReadByte((System.IntPtr)(0x$addressHex), i)).ToArray())"
        val eval = evaluateValue(evaluator, expr, position)
        val raw = eval.value?.let { describeValue(it).value }?.trim()
        val base64 = raw?.trim('"')
        if (!base64.isNullOrBlank()) {
            try {
                val decoded = Base64.getDecoder().decode(base64)
                if (decoded.isNotEmpty()) {
                    return decoded
                }
            } catch (_: Exception) {
            }
        }

        val bytes = ByteArray(readSize)
        var actual = 0
        for (offset in 0 until readSize) {
            val byteExpr = "System.Runtime.InteropServices.Marshal.ReadByte((System.IntPtr)(0x$addressHex), $offset)"
            val byteEval = evaluateValue(evaluator, byteExpr, position)
            val value = byteEval.value?.let { describeValue(it).value }
            val parsed = parseNumber(value)
            if (parsed == null) {
                break
            }
            bytes[offset] = parsed.toByte()
            actual++
        }

        return if (actual > 0) bytes.copyOf(actual) else null
    }

    private fun formatBytes(bytes: ByteArray): String {
        return bytes.joinToString(" ") { b -> String.format("%02X", b.toInt() and 0xFF) }
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
