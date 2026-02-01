package com.vsdebugger.rider.protocol

data class DebugRequest(
    val id: String? = null,
    val method: String? = null,
    val params: Map<String, Any?>? = null
)
