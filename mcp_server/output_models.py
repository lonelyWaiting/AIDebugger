"""Strict structured-output contracts for every public AIDebugger MCP tool."""

from __future__ import annotations

import re
from functools import lru_cache
from typing import Any, Dict, List, Literal, Optional, Type

from pydantic import BaseModel, ConfigDict, Field, create_model


OpaqueJsonObject = Dict[str, Any]


class StrictPayload(BaseModel):
    """Top-level protocol DTOs reject accidental or misspelled response fields."""

    model_config = ConfigDict(extra="forbid", strict=True)


class LocationPayload(StrictPayload):
    filePath: Optional[str] = None
    functionName: Optional[str] = None
    lineNumber: Optional[int] = None
    columnNumber: Optional[int] = None
    lineNumberReliable: Optional[bool] = None
    sourceLocationStatus: Optional[Literal["available", "path_only", "unavailable"]] = None


class FrameContextPayload(StrictPayload):
    debugSessionGeneration: Optional[int] = None
    breakSequence: Optional[int] = None
    contextEpoch: Optional[int] = None
    processId: Optional[int] = None
    threadId: Optional[int] = None
    frameIndex: Optional[int] = None
    frameId: Optional[str] = None


class VariablePayload(StrictPayload):
    name: Optional[str] = None
    type: Optional[str] = None
    value: Optional[Any] = None
    hasChildren: Optional[bool] = None
    children: Optional[List["VariablePayload"]] = None
    isReadOnly: Optional[bool] = None
    truncated: bool = False


class StackFramePayload(StrictPayload):
    context: Optional[FrameContextPayload] = None
    index: Optional[int] = None
    functionName: Optional[str] = None
    filePath: Optional[str] = None
    lineNumber: Optional[int] = None
    lineNumberReliable: Optional[bool] = None
    sourceLocationStatus: Optional[Literal["available", "path_only", "unavailable"]] = None
    columnNumber: Optional[int] = None
    module: Optional[str] = None
    language: Optional[str] = None
    returnType: Optional[str] = None
    arguments: Optional[List[VariablePayload]] = None
    truncated: Optional[bool] = None
    error: Optional[str] = None


class ThreadPayload(StrictPayload):
    id: Optional[int] = None
    name: Optional[str] = None
    isCurrent: Optional[bool] = None
    isFrozen: Optional[bool] = None
    location: Optional[str] = None
    priority: Optional[str] = None
    suspendCount: Optional[int] = None
    frames: Optional[List[StackFramePayload]] = None
    truncated: Optional[bool] = None


class ModulePayload(StrictPayload):
    name: Optional[str] = None
    path: Optional[str] = None
    symbolsLoaded: Optional[bool] = None
    symbolPath: Optional[str] = None
    order: Optional[int] = None
    version: Optional[str] = None
    timestamp: Optional[str] = None
    address: Optional[str] = None
    size: Optional[str] = None
    sizeBytes: Optional[int] = None
    is64Bit: Optional[bool] = None
    architecture: Optional[str] = None
    isSystemModule: Optional[bool] = None
    truncated: Optional[bool] = None


class IDEInstancePayload(StrictPayload):
    ide: Optional[str] = None
    pipeName: Optional[str] = None
    processId: Optional[int] = None
    processStartTimeUtcTicks: Optional[int] = None
    serverInstanceId: Optional[str] = None
    solutionName: Optional[str] = None
    solutionPath: Optional[str] = None
    isDebugging: Optional[bool] = None
    isPaused: Optional[bool] = None
    debugMode: Optional[str] = None
    currentProcess: Optional[str] = None
    ideName: Optional[str] = None
    ideVersion: Optional[str] = None
    connectionStatus: Optional[str] = None
    lastError: Optional[str] = None
    allowedCapabilities: Optional[List[str]] = None
    accessMode: Optional[str] = None
    isSelected: Optional[bool] = None


class InstanceSummaryPayload(StrictPayload):
    paused: int
    debugging: int
    idle: int
    unreachable: int


class EvaluateItemPayload(StrictPayload):
    evaluationPolicy: Optional[str] = None
    sideEffectGuarantee: Optional[str] = None
    expression: Optional[str] = None
    value: Optional[Any] = None
    type: Optional[str] = None
    isValid: Optional[bool] = None
    errorMessage: Optional[str] = None
    hexValue: Optional[str] = None
    hasChildren: Optional[bool] = None
    children: Optional[List[VariablePayload]] = None
    partial: Optional[bool] = None
    capturedBytes: Optional[int] = None
    maxCapturedBytes: Optional[int] = None


class RegisterPayload(StrictPayload):
    name: Optional[str] = None
    value: Optional[str] = None
    hexValue: Optional[str] = None
    category: Optional[str] = None


class DisassemblyInstructionPayload(StrictPayload):
    address: Optional[str] = None
    opcode: Optional[str] = None
    instruction: Optional[str] = None
    isCurrent: Optional[bool] = None
    sourceLine: Optional[int] = None
    symbol: Optional[str] = None
    mnemonic: Optional[str] = None
    operands: Optional[str] = None
    isCall: Optional[bool] = None
    isJump: Optional[bool] = None
    isReturn: Optional[bool] = None
    branchTarget: Optional[str] = None
    length: Optional[int] = None


class BuildErrorItemPayload(StrictPayload):
    severity: Optional[str] = None
    code: Optional[str] = None
    description: Optional[str] = None
    filePath: Optional[str] = None
    line: Optional[int] = None
    column: Optional[int] = None
    project: Optional[str] = None
    truncated: Optional[bool] = None


class OutputMatchPayload(StrictPayload):
    lineNumber: Optional[int] = None
    line: Optional[str] = None
    context: Optional[List[str]] = None
    contextTruncated: bool = False


class NativeFunctionPayload(StrictPayload):
    startAddress: Optional[str] = None
    startAddressValue: Optional[int] = None
    startRva: Optional[str] = None
    endAddress: Optional[str] = None
    endAddressValue: Optional[int] = None
    endRva: Optional[str] = None
    sizeBytes: Optional[int] = None
    addressOffset: Optional[str] = None
    nearestSymbol: Optional[str] = None
    confidence: Optional[str] = None
    reason: Optional[str] = None


class NativeAddressDetailPayload(StrictPayload):
    expression: Optional[str] = None
    status: Optional[str] = None
    virtualAddress: Optional[str] = None
    addressValue: Optional[int] = None
    moduleName: Optional[str] = None
    modulePath: Optional[str] = None
    moduleBase: Optional[str] = None
    moduleBaseValue: Optional[int] = None
    moduleSizeBytes: Optional[int] = None
    rva: Optional[str] = None
    rvaValue: Optional[int] = None
    sectionName: Optional[str] = None
    sectionRva: Optional[str] = None
    sectionOffset: Optional[str] = None
    nearestSymbol: Optional[str] = None
    function: Optional[NativeFunctionPayload] = None
    functionStatus: Optional[str] = None
    functionError: Optional[str] = None
    error: Optional[str] = None


class ModuleSectionPayload(StrictPayload):
    name: Optional[str] = None
    rva: Optional[str] = None
    virtualAddress: Optional[str] = None
    virtualSize: Optional[int] = None
    rawSize: Optional[int] = None
    rawPointer: Optional[str] = None
    characteristics: Optional[str] = None


class ImportFunctionPayload(StrictPayload):
    name: Optional[str] = None
    ordinal: Optional[int] = None
    thunkRva: Optional[str] = None


class ImportedModulePayload(StrictPayload):
    moduleName: Optional[str] = None
    functions: Optional[List[ImportFunctionPayload]] = None
    truncated: Optional[bool] = None


class ExportPayload(StrictPayload):
    name: Optional[str] = None
    ordinal: Optional[int] = None
    rva: Optional[str] = None
    virtualAddress: Optional[str] = None


class ManagedParameterPayload(StrictPayload):
    name: Optional[str] = None
    type: Optional[str] = None
    position: Optional[int] = None


class ManagedMethodPayload(StrictPayload):
    name: Optional[str] = None
    signature: Optional[str] = None
    declaringType: Optional[str] = None
    returnType: Optional[str] = None
    metadataToken: Optional[int] = None
    metadataTokenHex: Optional[str] = None
    isPublic: Optional[bool] = None
    isStatic: Optional[bool] = None
    isConstructor: Optional[bool] = None
    parameters: Optional[List[ManagedParameterPayload]] = None


class ManagedTypeInfoPayload(StrictPayload):
    name: Optional[str] = None
    fullName: Optional[str] = None
    namespaceName: Optional[str] = None
    assemblyQualifiedName: Optional[str] = None
    baseType: Optional[str] = None
    isPublic: Optional[bool] = None
    isInterface: Optional[bool] = None
    isEnum: Optional[bool] = None
    isValueType: Optional[bool] = None
    methods: Optional[List[ManagedMethodPayload]] = None
    partial: Optional[bool] = None


class ManagedLocalPayload(StrictPayload):
    index: Optional[int] = None
    type: Optional[str] = None
    isPinned: Optional[bool] = None


class ManagedIlInstructionPayload(StrictPayload):
    offset: Optional[int] = None
    offsetHex: Optional[str] = None
    opcode: Optional[str] = None
    operandType: Optional[str] = None
    operand: Optional[str] = None
    token: Optional[int] = None
    tokenHex: Optional[str] = None
    resolvedMember: Optional[str] = None
    branchTarget: Optional[str] = None
    switchTargets: Optional[List[str]] = None


class BreakpointPayload(StrictPayload):
    name: Optional[str] = None
    filePath: Optional[str] = None
    lineNumber: Optional[int] = None
    column: Optional[int] = None
    functionName: Optional[str] = None
    enabled: Optional[bool] = None
    condition: Optional[str] = None
    conditionType: Optional[str] = None
    hitCount: Optional[int] = None
    hitCountTarget: Optional[int] = None
    hitCountType: Optional[str] = None
    breakpointType: Optional[str] = None
    tag: Optional[str] = None
    requestedFilePath: Optional[str] = None
    requestedLineNumber: Optional[int] = None
    requestedCondition: Optional[str] = None
    resolvedFilePath: Optional[str] = None
    resolvedLineNumber: Optional[int] = None
    resolvedColumn: Optional[int] = None
    resolvedFunctionName: Optional[str] = None
    trackedAddress: Optional[NativeAddressDetailPayload] = None
    trackedAddresses: Optional[List[NativeAddressDetailPayload]] = None
    bindingStatus: Optional[str] = None
    boundChildrenCount: Optional[int] = None
    isBound: Optional[bool] = None
    bindingVerificationStatus: Optional[str] = None
    bindingWarning: Optional[str] = None
    isRelocated: Optional[bool] = None
    nearbyProbeAttempted: Optional[bool] = None
    nearbyProbeSucceeded: Optional[bool] = None
    optimizedCodeSuspected: Optional[bool] = None
    conditionApplied: Optional[bool] = None
    truncated: Optional[bool] = None


class BreakpointHitPayload(StrictPayload):
    confidence: Optional[str] = None
    reason: Optional[str] = None
    currentAddress: Optional[NativeAddressDetailPayload] = None
    breakpoint: Optional[BreakpointPayload] = None


class MemorySearchChunkPayload(StrictPayload):
    startAddress: str
    requestedBytes: int
    bytesSearched: int
    bytesAttempted: Optional[int] = None
    unreadableBytes: Optional[int] = None
    unattemptedBytes: Optional[int] = None
    status: str
    partial: bool
    truncated: bool
    nextStartAddress: Optional[str] = None
    matchCount: int
    capability: Optional[str] = None
    error: Optional[str] = None


class CapabilityPayload(StrictPayload):
    # The capability registry is intentionally extensible and method keyed.
    toolRegistry: OpaqueJsonObject
    categoryNames: List[str]
    recommendedWorkflow: List[str]
    categories: Dict[str, List[str]]
    capabilityNotes: Dict[str, str]
    catalogScope: str
    instanceAccess: List[IDEInstancePayload]
    effectiveInstance: Optional[IDEInstancePayload] = None
    staleSelectedTarget: Optional[OpaqueJsonObject] = None
    accessDiscoveryError: Optional[str] = None


class InstanceListPayload(StrictPayload):
    instanceCount: int
    reachableCount: Optional[int] = None
    unreachableCount: Optional[int] = None
    instances: List[IDEInstancePayload]
    selected: bool
    hasPinnedSelection: Optional[bool] = None
    selectedReachable: Optional[bool] = None
    hint: Optional[str] = None
    summary: Optional[InstanceSummaryPayload] = None
    wouldAutoSelect: Optional[str] = None
    preferredIde: Optional[str] = None
    autoSelected: Optional[IDEInstancePayload] = None
    selectedInstance: Optional[IDEInstancePayload] = None
    staleSelectedTarget: Optional[IDEInstancePayload] = None
    wouldAutoSelectInstance: Optional[IDEInstancePayload] = None
    message: Optional[str] = None
    selectionStatus: Optional[Literal["selection_changed", "no_reachable_instance", "stale_selection"]] = None


class InstanceSelectionPayload(StrictPayload):
    success: bool
    message: str
    selected: Optional[bool] = None
    selectedInstance: Optional[IDEInstancePayload] = None
    errorCode: Optional[Literal["SELECTION_CHANGED"]] = None


class CommandStatusPayload(StrictPayload):
    requestId: str
    state: str
    completedAtUtc: Optional[str] = None
    response: Optional[Any] = None
    kind: Optional[str] = None
    startedAtUtc: Optional[str] = None
    enqueuedAtUtc: Optional[str] = None
    cancellationRequested: Optional[bool] = None
    cancellationIssued: Optional[bool] = None
    sideEffectIssued: Optional[bool] = None


class DebugStatusPayload(StrictPayload):
    isDebugging: bool
    isPaused: bool
    debugMode: str
    currentProcess: Optional[str] = None
    stopReason: Optional[str] = None
    stopReasonRaw: Optional[str] = None
    stopReasonSource: Optional[str] = None
    stopReasonConfidence: Optional[str] = None
    debugSessionGeneration: Optional[int] = None
    breakSequence: Optional[int] = None
    contextEpoch: Optional[int] = None
    currentProcessId: Optional[int] = None
    currentLocation: Optional[LocationPayload] = None


class CallStackPayload(StrictPayload):
    frames: List[StackFramePayload]
    threadId: int
    debugSessionGeneration: Optional[int] = None
    breakSequence: Optional[int] = None
    contextEpoch: Optional[int] = None
    processId: Optional[int] = None
    threadName: Optional[str] = None
    capability: Optional[str] = None
    snapshotStable: Optional[bool] = None
    snapshotAttempts: Optional[int] = None
    snapshotStatus: Optional[str] = None
    expectedFrameCount: Optional[int] = None
    capturedFrameCount: Optional[int] = None
    startIndex: int
    scannedFrameCount: int
    nextIndex: int
    hasMore: bool
    maxFrames: int
    budgetMs: int
    resultBytes: int
    maxResultBytes: int
    partial: Optional[bool] = None
    error: Optional[str] = None


class LocalsPayload(StrictPayload):
    variables: List[VariablePayload]
    frameIndex: int
    context: Optional[FrameContextPayload] = None
    partial: Optional[bool] = None
    capturedBytes: int
    maxCapturedBytes: int
    error: Optional[str] = None


class EvaluationPayload(StrictPayload):
    expression: str
    isValid: bool
    evaluationPolicy: Optional[str] = None
    sideEffectGuarantee: Optional[str] = None
    value: Optional[Any] = None
    type: Optional[str] = None
    errorMessage: Optional[str] = None
    partial: Optional[bool] = None
    hexValue: Optional[str] = None
    hasChildren: Optional[bool] = None
    children: Optional[List[VariablePayload]] = None
    capturedBytes: int
    maxCapturedBytes: int


class BatchEvaluationPayload(StrictPayload):
    results: List[EvaluateItemPayload]
    requestedCount: int
    successCount: int
    failedCount: int
    snapshotId: Optional[str] = None
    capturedAt: Optional[str] = None
    snapshotStable: Optional[bool] = None
    snapshotAttempts: Optional[int] = None
    snapshotStatus: Optional[str] = None
    partial: Optional[bool] = None
    capturedBytes: int
    maxCapturedBytes: int
    error: Optional[str] = None


class ThreadsPayload(StrictPayload):
    threads: List[ThreadPayload]
    currentThreadId: int
    capability: Optional[str] = None
    snapshotStable: Optional[bool] = None
    snapshotAttempts: Optional[int] = None
    snapshotStatus: Optional[str] = None
    partial: Optional[bool] = None
    startThreadIndex: int
    startFrameIndex: int
    nextThreadIndex: int
    nextFrameIndex: int
    hasMore: bool
    capturedNodes: int
    maxNodes: int
    budgetMs: int
    resultBytes: int
    maxResultBytes: int
    error: Optional[str] = None


class ModulesPayload(StrictPayload):
    modules: List[ModulePayload]
    totalCount: int
    capability: Optional[str] = None
    error: Optional[str] = None
    partial: Optional[bool] = None
    truncated: Optional[bool] = None
    snapshotStable: Optional[bool] = None
    snapshotStatus: Optional[str] = None
    capturedStackFrames: Optional[int] = None
    scannedStackFrames: Optional[int] = None
    maxFramesScanned: Optional[int] = None
    returnedModuleCount: Optional[int] = None
    uniqueModulesDiscovered: Optional[int] = None
    totalStackFrames: Optional[int] = None
    maxNodes: Optional[int] = None
    budgetMs: Optional[int] = None
    startThreadIndex: Optional[int] = None
    startFrameIndex: Optional[int] = None
    nextThreadIndex: Optional[int] = None
    nextFrameIndex: Optional[int] = None
    hasMoreFrames: Optional[bool] = None
    totalAvailable: int
    startModuleIndex: int
    nextModuleIndex: int
    hasMoreModules: bool
    moduleSnapshotId: Optional[str] = None
    scanContinuationToken: Optional[str] = None
    resultBytes: int
    maxResultBytes: int


class ExceptionPayload(StrictPayload):
    hasException: bool
    status: Optional[str] = None
    partial: Optional[bool] = None
    truncated: Optional[bool] = None
    capturedBytes: int
    maxCapturedBytes: int
    error: Optional[str] = None
    capturedDepth: Optional[int] = None
    maxDepth: Optional[int] = None
    hasMoreInnerExceptions: Optional[bool] = None
    capability: Optional[str] = None
    type: Optional[str] = None
    message: Optional[str] = None
    stackTrace: Optional[str] = None
    source: Optional[str] = None
    innerException: Optional["ExceptionPayload"] = None
    hResult: Optional[str] = None


class BuildErrorsPayload(StrictPayload):
    errorCount: int
    warningCount: int
    items: List[BuildErrorItemPayload]
    totalAvailable: Optional[int] = None
    capturedCount: Optional[int] = None
    startIndex: Optional[int] = None
    scannedCount: Optional[int] = None
    nextIndex: Optional[int] = None
    hasMore: Optional[bool] = None
    partial: Optional[bool] = None
    truncated: Optional[bool] = None
    snapshotStable: Optional[bool] = None
    snapshotStatus: Optional[str] = None
    maxNodes: Optional[int] = None
    budgetMs: Optional[int] = None
    resultBytes: int
    maxResultBytes: int
    error: Optional[str] = None


class OutputWindowPayload(StrictPayload):
    pane: str
    content: str
    lineCount: int
    requestedLines: int
    truncated: bool
    capability: Optional[str] = None
    availablePanes: Optional[List[str]] = None
    error: Optional[str] = None
    snapshotStable: bool
    snapshotStatus: str
    partial: bool
    contentUtf8Bytes: int
    serializedContentBytes: int
    maxSerializedContentBytes: int
    omittedLeadingCharacters: int
    firstCharacterOffset: int
    captureEndCharacter: int
    hasMore: bool
    nextEndCharacter: Optional[int] = None


class SearchOutputPayload(StrictPayload):
    pane: str
    pattern: str
    isRegex: bool
    matches: List[OutputMatchPayload]
    totalMatches: int
    truncated: bool
    capability: Optional[str] = None
    error: Optional[str] = None
    snapshotStable: bool
    snapshotStatus: str
    partial: bool
    startLine: int
    captureStartCharacter: int
    captureEndCharacter: int
    firstLineNumber: int
    scannedLines: int
    nextLine: Optional[int] = None
    hasMore: bool
    resultBytes: int
    maxResultBytes: int
    resultNodes: int
    maxResultNodes: int
    errorCode: Optional[str] = None


class MemoryReadPayload(StrictPayload):
    address: str
    status: str
    requestedSize: int
    bytesRead: int
    failedReads: int
    partial: bool
    hasMore: Optional[bool] = None
    nextAddress: Optional[str] = None
    remainingBytes: Optional[int] = None
    capability: Optional[str] = None
    size: Optional[int] = None
    hexDump: Optional[str] = None
    asciiView: Optional[str] = None
    bytes: Optional[List[int]] = None
    formattedView: Optional[str] = None
    error: Optional[str] = None


class MemorySearchPayload(StrictPayload):
    pattern: str
    status: str
    matches: List[str]
    totalFound: int
    bytesSearched: int
    bytesAttempted: int
    requestedBytes: int
    failedReads: int
    unreadableBytes: int
    unattemptedBytes: int
    partial: bool
    capability: Optional[str] = None
    patternBytes: Optional[List[str]] = None
    truncated: Optional[bool] = None
    nextStartAddress: Optional[str] = None
    hasMore: bool
    patternFormat: Optional[str] = None
    chunked: Optional[bool] = None
    chunkSize: Optional[int] = None
    chunkCount: Optional[int] = None
    chunks: Optional[List[MemorySearchChunkPayload]] = None
    nextStartExpression: Optional[str] = None
    nextStartOffset: Optional[int] = None
    remainingBytes: Optional[int] = None
    error: Optional[str] = None
    errorCode: Optional[str] = None
    continuationRequiresRefresh: Optional[bool] = None


class DisassemblyPayload(StrictPayload):
    instructions: List[DisassemblyInstructionPayload]
    currentAddress: Optional[str] = None
    startAddress: Optional[str] = None
    precedingInstructionsStatus: Optional[str] = None
    functionName: Optional[str] = None
    sourceFile: Optional[str] = None
    is64Bit: Optional[bool] = None
    architecture: Optional[str] = None
    architectureStatus: Optional[str] = None
    totalInstructions: Optional[int] = None
    requestedInstructions: Optional[int] = None
    requestedBytes: Optional[int] = None
    bytesRead: Optional[int] = None
    partial: Optional[bool] = None
    truncated: Optional[bool] = None
    hasMore: Optional[bool] = None
    nextAddress: Optional[str] = None
    error: Optional[str] = None
    errorCode: Optional[str] = None


class RegistersPayload(StrictPayload):
    registers: List[RegisterPayload]
    is64Bit: Optional[bool] = None
    architecture: Optional[str] = None
    architectureStatus: Optional[str] = None
    status: Optional[str] = None
    partial: Optional[bool] = None
    snapshotStatus: Optional[str] = None
    requestedRegisterCount: Optional[int] = None
    capturedRegisterCount: Optional[int] = None
    unavailableRegisters: Optional[List[str]] = None
    capability: Optional[str] = None
    availableCategories: Optional[List[str]] = None
    error: Optional[str] = None
    errorCode: Optional[str] = None


class WatchPayload(StrictPayload):
    expression: str
    isValid: Optional[bool] = None
    success: Optional[bool] = None
    message: Optional[str] = None
    commandIssued: Optional[bool] = None
    evaluationIssued: Optional[bool] = None
    evaluationStatus: Optional[str] = None
    outcome: Optional[str] = None
    behavior: Optional[str] = None
    value: Optional[Any] = None
    type: Optional[str] = None
    error: Optional[str] = None
    errorCode: Optional[str] = None


class NativeAddressPayload(StrictPayload):
    expression: str
    status: str
    virtualAddress: Optional[str] = None
    addressValue: Optional[int] = None
    moduleName: Optional[str] = None
    modulePath: Optional[str] = None
    moduleBase: Optional[str] = None
    moduleBaseValue: Optional[int] = None
    moduleSizeBytes: Optional[int] = None
    rva: Optional[str] = None
    rvaValue: Optional[int] = None
    sectionName: Optional[str] = None
    sectionRva: Optional[str] = None
    sectionOffset: Optional[str] = None
    nearestSymbol: Optional[str] = None
    function: Optional[NativeFunctionPayload] = None
    functionStatus: Optional[str] = None
    functionError: Optional[str] = None
    error: Optional[str] = None


class ModuleLayoutPayload(StrictPayload):
    status: str
    sections: List[ModuleSectionPayload]
    imports: List[ImportedModulePayload]
    exports: List[ExportPayload]
    module: Optional[ModulePayload] = None
    is64Bit: Optional[bool] = None
    entryPointRva: Optional[str] = None
    entryPointVa: Optional[str] = None
    imageBase: Optional[str] = None
    imageSizeBytes: Optional[int] = None
    truncated: Optional[bool] = None
    error: Optional[str] = None


class NativeContextPayload(StrictPayload):
    architecture: Optional[str] = None
    architectureStatus: Optional[str] = None
    errorCode: Optional[str] = None
    resultBytes: int
    maxResultBytes: int
    omittedComponents: List[str]
    snapshotId: str
    capturedAt: str
    status: Optional[DebugStatusPayload] = None
    currentAddress: Optional[NativeAddressDetailPayload] = None
    stackPointer: Optional[NativeAddressDetailPayload] = None
    registers: Optional[RegistersPayload] = None
    callstack: Optional[CallStackPayload] = None
    disassembly: Optional[DisassemblyPayload] = None
    stackMemory: Optional[MemoryReadPayload] = None
    modules: Optional[ModulesPayload] = None
    partial: bool
    error: Optional[str] = None


class ManagedAssemblyPayload(StrictPayload):
    capability: str
    assemblyPath: str
    types: List[ManagedTypeInfoPayload]
    assemblyFullName: Optional[str] = None
    moduleVersionId: Optional[str] = None
    typeFilter: Optional[str] = None
    totalTypeCount: Optional[int] = None
    matchedTypeCount: Optional[int] = None
    partial: bool
    error: Optional[str] = None


class ManagedTypePayload(StrictPayload):
    capability: str
    assemblyPath: str
    typeName: str
    type: Optional[ManagedTypeInfoPayload] = None
    error: Optional[str] = None


class ManagedMethodIlPayload(StrictPayload):
    capability: str
    assemblyPath: str
    typeName: str
    methodName: str
    overloadIndex: int
    matchedOverloadCount: int
    locals: List[ManagedLocalPayload]
    instructions: List[ManagedIlInstructionPayload]
    totalInstructionCount: int
    partial: bool
    method: Optional[ManagedMethodPayload] = None
    error: Optional[str] = None


class OperationPayload(StrictPayload):
    success: bool
    message: str
    errorCode: Optional[str] = None


class DebugControlPayload(OperationPayload):
    action: Optional[str] = None
    commandIssued: Optional[bool] = None
    uiNavigationIssued: Optional[bool] = None
    outcome: Optional[str] = None
    newLocation: Optional[LocationPayload] = None


class ProcessDebugPayload(OperationPayload):
    action: Optional[str] = None
    capability: Optional[str] = None
    behavior: Optional[str] = None
    processStarted: Optional[bool] = None
    commandIssued: Optional[bool] = None
    uiNavigationIssued: Optional[bool] = None
    attachCompleted: Optional[bool] = None
    outcome: Optional[str] = None
    processId: Optional[int] = None
    processStartTimeUtcTicks: Optional[int] = None
    processName: Optional[str] = None
    executablePath: Optional[str] = None
    workingDirectory: Optional[str] = None
    arguments: Optional[str] = None
    status: Optional[DebugStatusPayload] = None
    lastPollError: Optional[str] = None
    lastPollErrorCode: Optional[str] = None
    partial: Optional[bool] = None
    targetIdentity: Optional[Dict[str, Any]] = None
    debuggerActivationStatus: Optional[Literal["active", "timed_out", "stale_target", "not_requested"]] = None
    followUpTimedOut: Optional[bool] = None
    retryCommand: Optional[bool] = None


class BreakpointOperationPayload(OperationPayload):
    operation: Optional[str] = None
    verificationStatus: Optional[str] = None
    verificationMessage: Optional[str] = None
    breakpointInfo: Optional[BreakpointPayload] = None
    address: Optional[NativeAddressDetailPayload] = None
    commandIssued: Optional[bool] = None
    partial: Optional[bool] = None
    affectedCount: Optional[int] = None
    remainingCount: Optional[int] = None
    outcome: Optional[str] = None


class ContextSelectionPayload(OperationPayload):
    action: Optional[str] = None
    commandIssued: Optional[bool] = None
    outcome: Optional[str] = None
    newLocation: Optional[LocationPayload] = None


class ContinuePayload(OperationPayload):
    action: Optional[str] = None
    status: Optional[DebugStatusPayload] = None
    waitTimeoutMs: Optional[int] = None
    pollIntervalMs: Optional[int] = None
    waitStrategy: Optional[str] = None
    waitedMs: Optional[int] = None
    issuedContinue: Optional[bool] = None
    continueOperation: Optional[DebugControlPayload] = None
    timedOut: Optional[bool] = None
    breakDetected: Optional[bool] = None
    stopReason: Optional[str] = None
    bundle: Optional["DebugBundlePayload"] = None
    context: Optional[NativeContextPayload] = None
    continueResult: Optional["ContinuePayload"] = None
    breakpoints: Optional["BreakpointsPayload"] = None
    hybridContext: Optional["HybridContextPayload"] = None
    hitAttributionStatus: Optional[str] = None
    hitAttributionError: Optional[str] = None
    hitVerificationStatus: Optional[str] = None
    hitVerificationMessage: Optional[str] = None
    hitBreakpoint: Optional[BreakpointHitPayload] = None


class BreakpointsPayload(StrictPayload):
    breakpoints: List[BreakpointPayload]
    totalCount: int
    error: Optional[str] = None
    totalAvailable: Optional[int] = None
    startIndex: Optional[int] = None
    scannedCount: Optional[int] = None
    nextIndex: Optional[int] = None
    hasMore: Optional[bool] = None
    partial: Optional[bool] = None
    truncated: Optional[bool] = None
    snapshotStable: Optional[bool] = None
    snapshotStatus: Optional[str] = None
    maxNodes: Optional[int] = None
    budgetMs: Optional[int] = None
    resultBytes: int
    maxResultBytes: int


class FullContextPayload(StrictPayload):
    snapshotStable: Optional[bool] = None
    snapshotStatus: Optional[str] = None
    startDebugSessionGeneration: Optional[int] = None
    startBreakSequence: Optional[int] = None
    startContextEpoch: Optional[int] = None
    endDebugSessionGeneration: Optional[int] = None
    endBreakSequence: Optional[int] = None
    endContextEpoch: Optional[int] = None
    status: Optional[DebugStatusPayload] = None
    callstack: Optional[CallStackPayload] = None
    locals: Optional[LocalsPayload] = None
    exception: Optional[ExceptionPayload] = None
    threads: Optional[ThreadsPayload] = None
    modules: Optional[ModulesPayload] = None
    partial: Optional[bool] = None
    resultBytes: int
    maxResultBytes: int
    componentErrors: Optional[Dict[str, str]] = None
    error: Optional[str] = None


class DebugBundlePayload(StrictPayload):
    snapshotId: str
    capturedAt: str
    snapshotStable: bool
    snapshotAttempts: int
    snapshotStatus: str
    partial: bool
    resultBytes: int
    maxResultBytes: int
    includedComponents: List[str]
    omittedComponents: List[str]
    stopReason: str
    status: Optional[DebugStatusPayload] = None
    callstack: Optional[CallStackPayload] = None
    locals: Optional[LocalsPayload] = None
    exception: Optional[ExceptionPayload] = None
    threads: Optional[ThreadsPayload] = None
    modules: Optional[ModulesPayload] = None
    registers: Optional[RegistersPayload] = None
    output: Optional[OutputWindowPayload] = None
    expressions: Optional[BatchEvaluationPayload] = None
    error: Optional[str] = None


class CrashTriagePayload(StrictPayload):
    kind: str
    bundle: DebugBundlePayload
    disassembly: Optional[DisassemblyPayload] = None
    disassemblyError: Optional[str] = None
    stackMemory: Optional[MemoryReadPayload] = None
    stackMemoryAddressExpression: Optional[str] = None
    stackMemoryAddress: Optional[str] = None
    stackMemoryError: Optional[str] = None


class StepCapturePayload(StrictPayload):
    action: str
    step: Optional[DebugControlPayload] = None
    bundle: Optional[DebugBundlePayload] = None
    success: Optional[bool] = None
    message: Optional[str] = None
    bundleError: Optional[str] = None
    bundleErrorCode: Optional[str] = None
    bundleRequestId: Optional[str] = None
    bundleOutcomeUnknown: Optional[bool] = None


class HybridContextPayload(StrictPayload):
    resultBytes: int
    maxResultBytes: int
    omittedComponents: List[str]
    snapshotId: str
    capturedAt: str
    status: Optional[DebugStatusPayload] = None
    currentThreadId: int
    selectedStackFrameIndex: int
    nativeContext: Optional[NativeContextPayload] = None
    threads: Optional[ThreadsPayload] = None
    breakpoints: Optional[BreakpointsPayload] = None
    partial: bool
    error: Optional[str] = None


_MODEL_BY_TOOL: Dict[str, Type[StrictPayload]] = {
    "debug_capabilities": CapabilityPayload,
    "get_command_status": CommandStatusPayload,
    "cancel_debug_request": CommandStatusPayload,
    **{name: InstanceListPayload for name in (
        "list_vs_instances", "list_ide_instances", "detect_debug_sessions",
    )},
    **{name: InstanceSelectionPayload for name in (
        "select_ide_instance", "clear_ide_selection", "select_vs_instance", "clear_vs_selection",
    )},
    "get_debug_status": DebugStatusPayload,
    "get_callstack": CallStackPayload,
    "get_call_stack": CallStackPayload,
    "get_locals": LocalsPayload,
    "evaluate_expression": EvaluationPayload,
    "evaluate_expressions_batch": BatchEvaluationPayload,
    "get_threads": ThreadsPayload,
    "list_threads": ThreadsPayload,
    "get_modules": ModulesPayload,
    "get_exception": ExceptionPayload,
    "get_build_errors": BuildErrorsPayload,
    "get_errors_only": BuildErrorsPayload,
    "get_warnings_only": BuildErrorsPayload,
    "get_debug_output": OutputWindowPayload,
    "search_output": SearchOutputPayload,
    "get_full_context": FullContextPayload,
    "capture_debug_bundle": DebugBundlePayload,
    "crash_triage": CrashTriagePayload,
    "step_and_capture": StepCapturePayload,
    "resolve_address": NativeAddressPayload,
    "get_module_layout": ModuleLayoutPayload,
    "capture_native_context": NativeContextPayload,
    "inspect_managed_assembly": ManagedAssemblyPayload,
    "inspect_managed_type": ManagedTypePayload,
    "inspect_managed_method_il": ManagedMethodIlPayload,
    "capture_hybrid_context": HybridContextPayload,
    "continue_and_wait_for_break": ContinuePayload,
    "continue_and_capture_native": ContinuePayload,
    "continue_and_capture_breakpoint": ContinuePayload,
    "get_breakpoints": BreakpointsPayload,
    "get_breakpoint_bindings": BreakpointsPayload,
    "read_memory": MemoryReadPayload,
    "search_memory": MemorySearchPayload,
    "get_disassembly": DisassemblyPayload,
    "get_disassembly_at": DisassemblyPayload,
    "get_registers": RegistersPayload,
    "add_watch": WatchPayload,
    **{name: ProcessDebugPayload for name in (
        "attach_process", "start_debugging",
    )},
    **{name: DebugControlPayload for name in (
        "debug_control", "debug_continue", "continue",
        "debug_step_over", "step_over", "debug_step_into", "step_into", "debug_step_out", "step_out",
        "debug_break", "pause", "debug_stop", "stop_debugging", "detach_process", "detach_debugger",
        "debug_run_to_cursor", "debug_set_next_statement",
    )},
    **{name: BreakpointOperationPayload for name in (
        "set_breakpoint", "set_function_breakpoint",
        "set_data_breakpoint", "set_address_breakpoint", "set_watchpoint", "delete_breakpoint",
        "clear_breakpoint", "delete_all_breakpoints", "enable_breakpoint",
    )},
    **{name: ContextSelectionPayload for name in (
        "switch_stack_frame",
        "select_stack_frame", "switch_thread", "select_thread",
    )},
}


def _verify_registry_coverage() -> None:
    vs_scope = __import__("os").environ.get("AIDB_SERVER_SCOPE") == "vs"
    if not vs_scope:
        try:
            try:
                from .tool_registry import TOOLS
            except ImportError:
                from tool_registry import TOOLS
            tools = set(TOOLS)
        except ImportError:
            vs_scope = True
    if vs_scope:
        try:
            from .vs_tool_registry import VS_TOOL_DOMAINS
        except ImportError:
            from vs_tool_registry import VS_TOOL_DOMAINS
        tools = {name for names in VS_TOOL_DOMAINS.values() for name in names}

    missing = sorted(tools - set(_MODEL_BY_TOOL))
    stale = [] if vs_scope else sorted(set(_MODEL_BY_TOOL) - tools)
    if missing or stale:
        raise RuntimeError(f"MCP output-model registry mismatch: missing={missing}, stale={stale}")


_verify_registry_coverage()


@lru_cache(maxsize=None)
def output_model_for(tool_name: str) -> Type[BaseModel]:
    try:
        payload_model = _MODEL_BY_TOOL[tool_name]
    except KeyError as exc:
        raise KeyError(f"No structured output model registered for MCP tool {tool_name!r}") from exc

    title = "".join(part.capitalize() for part in re.split(r"[^a-zA-Z0-9]+", tool_name) if part)
    return create_model(
        f"{title or 'AIDebugger'}Output",
        __config__=ConfigDict(extra="forbid", strict=True),
        result=(payload_model, Field(..., description=f"Typed result for {tool_name}.")),
    )
