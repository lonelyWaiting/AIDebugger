package com.vsdebugger.rider.protocol

data class DebugRequest(
    val protocol: String? = null,
    val id: String? = null,
    val method: String? = null,
    val params: Map<String, Any?>? = null,
    val meta: DebugRequestMetadata? = null
)

data class DebugRequestMetadata(
    val authToken: String? = null,
    val deadlineUnixMs: Long? = null,
    val kind: String? = null,
    val capability: String? = null,
    val clientProcessId: Int? = null,
    val clientProcessStartTimeUtcTicks: Long? = null,
    val target: DebugTargetIdentity? = null
)

data class DebugTargetIdentity(
    val processId: Int? = null,
    val processStartTimeUtcTicks: Long? = null,
    val serverInstanceId: String? = null,
    val debugSessionGeneration: Long? = null,
    val breakSequence: Long? = null,
    val contextEpoch: Long? = null
)
