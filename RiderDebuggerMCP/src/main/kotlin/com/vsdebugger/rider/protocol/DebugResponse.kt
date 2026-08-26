package com.vsdebugger.rider.protocol

data class DebugResponse(
    val protocol: String? = null,
    val id: String? = null,
    val status: String? = null,
    val server: DebugServerIdentity? = null,
    val result: Any? = null,
    val error: DebugError? = null
) {
    companion object {
        fun success(id: String?, result: Any?): DebugResponse =
            DebugResponse(id = id, status = "ok", result = result)
        fun failure(id: String?, code: String, message: String): DebugResponse =
            DebugResponse(id = id, status = "error", error = DebugError(code, message))
    }
}

data class DebugError(
    val code: String,
    val message: String,
    val outcomeUnknown: Boolean = false,
    val requestId: String? = null,
    val details: Map<String, Any?>? = null
)

data class DebugServerIdentity(
    val processId: Int,
    val processStartTimeUtcTicks: Long,
    val serverInstanceId: String,
    val protocolCatalogHash: String,
    val debugSessionGeneration: Long = 0,
    val breakSequence: Long = 0,
    val contextEpoch: Long = 0
)
