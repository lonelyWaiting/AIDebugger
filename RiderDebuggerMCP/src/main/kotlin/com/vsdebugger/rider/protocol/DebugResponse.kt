package com.vsdebugger.rider.protocol

data class DebugResponse(
    val id: String? = null,
    val result: Any? = null,
    val error: DebugError? = null
) {
    companion object {
        fun success(id: String?, result: Any?): DebugResponse = DebugResponse(id = id, result = result)
        fun failure(id: String?, code: String, message: String): DebugResponse =
            DebugResponse(id = id, error = DebugError(code, message))
    }
}

data class DebugError(
    val code: String,
    val message: String
)
