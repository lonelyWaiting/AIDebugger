package com.vsdebugger.rider.protocol

import com.google.gson.annotations.SerializedName

// Debug Status

data class DebugStatusResult(
    @SerializedName("isDebugging") val isDebugging: Boolean = false,
    @SerializedName("isPaused") val isPaused: Boolean = false,
    @SerializedName("debugMode") val debugMode: String = "",
    @SerializedName("currentProcess") val currentProcess: String? = null,
    @SerializedName("stopReason") val stopReason: String = "not_paused",
    @SerializedName("stopReasonRaw") val stopReasonRaw: String? = null,
    @SerializedName("currentLocation") val currentLocation: LocationInfo? = null
)

data class LocationInfo(
    @SerializedName("filePath") val filePath: String = "",
    @SerializedName("functionName") val functionName: String = "",
    @SerializedName("lineNumber") val lineNumber: Int = 0,
    @SerializedName("columnNumber") val columnNumber: Int = 1
)

// Call Stack

data class CallStackResult(
    @SerializedName("threadId") var threadId: Int = 0,
    @SerializedName("threadName") var threadName: String = "",
    @SerializedName("capability") var capability: String? = null,
    @SerializedName("snapshotStable") var snapshotStable: Boolean = false,
    @SerializedName("snapshotAttempts") var snapshotAttempts: Int = 0,
    @SerializedName("snapshotStatus") var snapshotStatus: String = "unavailable",
    @SerializedName("frames") val frames: MutableList<StackFrameInfo> = mutableListOf()
)

data class StackFrameInfo(
    @SerializedName("index") val index: Int = 0,
    @SerializedName("functionName") val functionName: String = "",
    @SerializedName("filePath") val filePath: String = "",
    @SerializedName("lineNumber") val lineNumber: Int = 0,
    @SerializedName("lineNumberReliable") val lineNumberReliable: Boolean = false,
    @SerializedName("columnNumber") val columnNumber: Int = 1,
    @SerializedName("module") val module: String? = null,
    @SerializedName("language") val language: String? = null,
    @SerializedName("returnType") val returnType: String? = null
)

// Locals

data class LocalsResult(
    @SerializedName("frameIndex") val frameIndex: Int = 0,
    @SerializedName("variables") val variables: MutableList<VariableInfo> = mutableListOf()
)

data class VariableInfo(
    @SerializedName("name") val name: String = "",
    @SerializedName("type") val type: String? = null,
    @SerializedName("value") val value: String? = null,
    @SerializedName("hasChildren") val hasChildren: Boolean = false,
    @SerializedName("children") val children: MutableList<VariableInfo>? = null,
    @SerializedName("isReadOnly") val isReadOnly: Boolean = false
)

// Threads

data class ThreadsResult(
    @SerializedName("currentThreadId") var currentThreadId: Int = 0,
    @SerializedName("capability") var capability: String? = null,
    @SerializedName("snapshotStable") var snapshotStable: Boolean = false,
    @SerializedName("snapshotAttempts") var snapshotAttempts: Int = 0,
    @SerializedName("snapshotStatus") var snapshotStatus: String = "unavailable",
    @SerializedName("threads") val threads: MutableList<ThreadInfo> = mutableListOf()
)

data class ThreadInfo(
    @SerializedName("id") val id: Int = 0,
    @SerializedName("name") val name: String = "",
    @SerializedName("isCurrent") val isCurrent: Boolean = false,
    @SerializedName("isFrozen") val isFrozen: Boolean = false,
    @SerializedName("location") val location: String? = null,
    @SerializedName("priority") val priority: String? = null,
    @SerializedName("suspendCount") val suspendCount: Int = 0
)

// Modules

data class ModulesResult(
    @SerializedName("modules") val modules: MutableList<ModuleInfo> = mutableListOf(),
    @SerializedName("totalCount") val totalCount: Int = 0,
    @SerializedName("capability") val capability: String? = null,
    @SerializedName("error") val error: String? = null
)

data class ModuleInfo(
    @SerializedName("name") val name: String = "",
    @SerializedName("path") val path: String? = null,
    @SerializedName("symbolsLoaded") val symbolsLoaded: Boolean = false,
    @SerializedName("symbolPath") val symbolPath: String? = null,
    @SerializedName("order") val order: Int = 0,
    @SerializedName("version") val version: String? = null,
    @SerializedName("timestamp") val timestamp: String? = null,
    @SerializedName("address") val address: String? = null,
    @SerializedName("size") val size: String? = null,
    @SerializedName("is64Bit") val is64Bit: Boolean = false,
    @SerializedName("isSystemModule") val isSystemModule: Boolean = false
)

// Exception

data class ExceptionResult(
    @SerializedName("hasException") val hasException: Boolean = false,
    @SerializedName("capability") val capability: String? = null,
    @SerializedName("type") val type: String? = null,
    @SerializedName("message") val message: String? = null,
    @SerializedName("stackTrace") val stackTrace: String? = null,
    @SerializedName("source") val source: String? = null,
    @SerializedName("innerException") val innerException: ExceptionResult? = null,
    @SerializedName("hResult") val hResult: String? = null
)

// Expression Evaluation

data class EvaluateResult(
    @SerializedName("expression") val expression: String = "",
    @SerializedName("value") val value: String? = null,
    @SerializedName("type") val type: String? = null,
    @SerializedName("isValid") val isValid: Boolean = false,
    @SerializedName("errorMessage") val errorMessage: String? = null,
    @SerializedName("hexValue") val hexValue: String? = null,
    @SerializedName("hasChildren") val hasChildren: Boolean = false,
    @SerializedName("children") val children: MutableList<VariableInfo>? = null
)

data class BatchEvaluateResult(
    @SerializedName("snapshotId") val snapshotId: String = "",
    @SerializedName("capturedAt") val capturedAt: String = "",
    @SerializedName("snapshotStable") val snapshotStable: Boolean = false,
    @SerializedName("snapshotAttempts") val snapshotAttempts: Int = 0,
    @SerializedName("snapshotStatus") val snapshotStatus: String = "unavailable",
    @SerializedName("requestedCount") val requestedCount: Int = 0,
    @SerializedName("successCount") val successCount: Int = 0,
    @SerializedName("failedCount") val failedCount: Int = 0,
    @SerializedName("partial") val partial: Boolean = false,
    @SerializedName("results") val results: MutableList<EvaluateResult> = mutableListOf(),
    @SerializedName("error") val error: String? = null
)

// Memory

data class MemoryResult(
    @SerializedName("address") val address: String = "",
    @SerializedName("status") val status: String = "unavailable",
    @SerializedName("size") val size: Int = 0,
    @SerializedName("requestedSize") val requestedSize: Int = 0,
    @SerializedName("bytesRead") val bytesRead: Int = 0,
    @SerializedName("failedReads") val failedReads: Int = 0,
    @SerializedName("partial") val partial: Boolean = false,
    @SerializedName("hexDump") val hexDump: String? = null,
    @SerializedName("asciiView") val asciiView: String? = null,
    @SerializedName("bytes") val bytes: MutableList<Byte> = mutableListOf(),
    @SerializedName("formattedView") val formattedView: String? = null,
    @SerializedName("error") val error: String? = null
)

// Disassembly

data class DisassemblyResult(
    @SerializedName("currentAddress") val currentAddress: String? = null,
    @SerializedName("instructions") val instructions: MutableList<DisassemblyInstruction> = mutableListOf(),
    @SerializedName("functionName") val functionName: String? = null,
    @SerializedName("sourceFile") val sourceFile: String? = null,
    @SerializedName("is64Bit") val is64Bit: Boolean = false,
    @SerializedName("error") val error: String? = null,
    @SerializedName("totalInstructions") val totalInstructions: Int = 0
)

data class DisassemblyInstruction(
    @SerializedName("address") val address: String? = null,
    @SerializedName("opcode") val opcode: String? = null,
    @SerializedName("instruction") val instruction: String? = null,
    @SerializedName("isCurrent") val isCurrent: Boolean = false,
    @SerializedName("sourceLine") val sourceLine: Int? = null,
    @SerializedName("symbol") val symbol: String? = null,
    @SerializedName("mnemonic") val mnemonic: String? = null,
    @SerializedName("operands") val operands: String? = null,
    @SerializedName("isCall") val isCall: Boolean = false,
    @SerializedName("isJump") val isJump: Boolean = false,
    @SerializedName("isReturn") val isReturn: Boolean = false,
    @SerializedName("branchTarget") val branchTarget: String? = null,
    @SerializedName("length") val length: Int = 0
)

// Build Errors

data class BuildErrorsResult(
    @SerializedName("errorCount") val errorCount: Int = 0,
    @SerializedName("warningCount") val warningCount: Int = 0,
    @SerializedName("items") val items: MutableList<BuildErrorItem> = mutableListOf()
)

data class BuildErrorItem(
    @SerializedName("severity") val severity: String? = null,
    @SerializedName("code") val code: String? = null,
    @SerializedName("description") val description: String? = null,
    @SerializedName("filePath") val filePath: String? = null,
    @SerializedName("line") val line: Int = 0,
    @SerializedName("column") val column: Int = 0,
    @SerializedName("project") val project: String? = null
)

// Full Context

data class FullContextResult(
    @SerializedName("status") val status: DebugStatusResult? = null,
    @SerializedName("callstack") val callstack: CallStackResult? = null,
    @SerializedName("locals") val locals: LocalsResult? = null,
    @SerializedName("exception") val exception: ExceptionResult? = null,
    @SerializedName("threads") val threads: ThreadsResult? = null,
    @SerializedName("modules") val modules: ModulesResult? = null
)

data class DebugBundleResult(
    @SerializedName("snapshotId") val snapshotId: String = "",
    @SerializedName("capturedAt") val capturedAt: String = "",
    @SerializedName("snapshotStable") val snapshotStable: Boolean = false,
    @SerializedName("snapshotAttempts") val snapshotAttempts: Int = 0,
    @SerializedName("snapshotStatus") val snapshotStatus: String = "unavailable",
    @SerializedName("partial") val partial: Boolean = false,
    @SerializedName("stopReason") val stopReason: String = "not_paused",
    @SerializedName("status") val status: DebugStatusResult? = null,
    @SerializedName("callstack") val callstack: CallStackResult? = null,
    @SerializedName("locals") val locals: LocalsResult? = null,
    @SerializedName("exception") val exception: ExceptionResult? = null,
    @SerializedName("threads") val threads: ThreadsResult? = null,
    @SerializedName("modules") val modules: ModulesResult? = null,
    @SerializedName("registers") val registers: RegistersResult? = null,
    @SerializedName("output") val output: OutputWindowResult? = null,
    @SerializedName("expressions") val expressions: BatchEvaluateResult? = null,
    @SerializedName("error") val error: String? = null
)

// Ping

data class PingResult(
    @SerializedName("status") val status: String = "ok",
    @SerializedName("timestamp") val timestamp: String = ""
)

// Instance Info

data class InstanceInfoResult(
    @SerializedName("processId") val processId: Int = 0,
    @SerializedName("pipeName") val pipeName: String = "",
    @SerializedName("solutionName") val solutionName: String? = null,
    @SerializedName("solutionPath") val solutionPath: String? = null,
    @SerializedName("isDebugging") val isDebugging: Boolean = false,
    @SerializedName("isPaused") val isPaused: Boolean = false,
    @SerializedName("debugMode") val debugMode: String? = null,
    @SerializedName("currentProcess") val currentProcess: String? = null,
    @SerializedName("vsVersion") val vsVersion: String? = null,
    @SerializedName("ideName") val ideName: String? = null,
    @SerializedName("ideVersion") val ideVersion: String? = null
)

// Debug Control

data class DebugControlResult(
    @SerializedName("action") val action: String = "",
    @SerializedName("success") val success: Boolean = false,
    @SerializedName("message") val message: String? = null,
    @SerializedName("newLocation") val newLocation: LocationInfo? = null
)

// Breakpoints

data class BreakpointsResult(
    @SerializedName("breakpoints") val breakpoints: MutableList<BreakpointInfo> = mutableListOf(),
    @SerializedName("totalCount") var totalCount: Int = 0,
    @SerializedName("error") val error: String? = null
)

data class BreakpointInfo(
    @SerializedName("name") var name: String? = null,
    @SerializedName("filePath") var filePath: String? = null,
    @SerializedName("lineNumber") var lineNumber: Int = 0,
    @SerializedName("column") var column: Int = 0,
    @SerializedName("functionName") var functionName: String? = null,
    @SerializedName("enabled") var enabled: Boolean = false,
    @SerializedName("condition") var condition: String? = null,
    @SerializedName("conditionType") var conditionType: String? = null,
    @SerializedName("hitCount") var hitCount: Int = 0,
    @SerializedName("hitCountTarget") var hitCountTarget: Int = 0,
    @SerializedName("hitCountType") var hitCountType: String? = null,
    @SerializedName("breakpointType") var breakpointType: String? = null,
    @SerializedName("tag") var tag: String? = null
)

data class BreakpointOperationResult(
    @SerializedName("operation") val operation: String = "",
    @SerializedName("success") val success: Boolean = false,
    @SerializedName("message") val message: String? = null,
    @SerializedName("breakpointInfo") val breakpointInfo: BreakpointInfo? = null
)

// Registers

data class RegistersResult(
    @SerializedName("registers") val registers: MutableList<RegisterInfo> = mutableListOf(),
    @SerializedName("is64Bit") val is64Bit: Boolean = false,
    @SerializedName("capability") val capability: String? = null,
    @SerializedName("availableCategories") val availableCategories: MutableList<String> = mutableListOf(),
    @SerializedName("error") val error: String? = null
)

data class RegisterInfo(
    @SerializedName("name") val name: String = "",
    @SerializedName("value") val value: String? = null,
    @SerializedName("hexValue") val hexValue: String? = null,
    @SerializedName("category") val category: String? = null
)

// Watch

data class WatchResult(
    @SerializedName("expression") val expression: String = "",
    @SerializedName("behavior") val behavior: String? = null,
    @SerializedName("value") val value: String? = null,
    @SerializedName("type") val type: String? = null,
    @SerializedName("isValid") val isValid: Boolean = false,
    @SerializedName("error") val error: String? = null
)

// Memory Search

data class MemorySearchResult(
    @SerializedName("pattern") val pattern: String = "",
    @SerializedName("status") val status: String = "unavailable",
    @SerializedName("patternBytes") val patternBytes: Array<String> = emptyArray(),
    @SerializedName("matches") val matches: MutableList<String> = mutableListOf(),
    @SerializedName("totalFound") val totalFound: Int = 0,
    @SerializedName("bytesSearched") val bytesSearched: Int = 0,
    @SerializedName("requestedBytes") val requestedBytes: Int = 0,
    @SerializedName("failedReads") val failedReads: Int = 0,
    @SerializedName("partial") val partial: Boolean = false,
    @SerializedName("error") val error: String? = null
)

// Output Window

data class OutputWindowResult(
    @SerializedName("pane") val pane: String = "",
    @SerializedName("capability") val capability: String? = null,
    @SerializedName("content") val content: String? = null,
    @SerializedName("lineCount") val lineCount: Int = 0,
    @SerializedName("requestedLines") val requestedLines: Int = 0,
    @SerializedName("truncated") val truncated: Boolean = false,
    @SerializedName("availablePanes") val availablePanes: MutableList<String> = mutableListOf(),
    @SerializedName("error") val error: String? = null
)

data class SearchOutputResult(
    @SerializedName("pane") val pane: String = "",
    @SerializedName("capability") val capability: String? = null,
    @SerializedName("pattern") val pattern: String = "",
    @SerializedName("isRegex") val isRegex: Boolean = false,
    @SerializedName("matches") val matches: MutableList<OutputMatch> = mutableListOf(),
    @SerializedName("totalMatches") val totalMatches: Int = 0,
    @SerializedName("truncated") val truncated: Boolean = false,
    @SerializedName("error") val error: String? = null
)

data class OutputMatch(
    @SerializedName("lineNumber") val lineNumber: Int = 0,
    @SerializedName("line") val line: String? = null,
    @SerializedName("context") val context: MutableList<String> = mutableListOf()
)
