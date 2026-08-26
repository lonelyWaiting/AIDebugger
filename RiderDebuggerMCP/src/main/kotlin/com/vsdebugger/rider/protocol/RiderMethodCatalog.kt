package com.vsdebugger.rider.protocol

import com.google.gson.Gson
import java.security.MessageDigest

data class RiderMethodDescriptor(
    val name: String,
    val kind: String,
    val capabilities: List<String>,
    val controlPlane: Boolean
)

object RiderMethodCatalog {
    private const val RESOURCE_NAME = "debug_method_manifest.json"
    private val supportedMethods = setOf(
        "add_watch", "break", "cancel_request", "capture_debug_bundle", "continue",
        "continue_and_wait_for_break", "delete_all_breakpoints", "delete_breakpoint", "detach",
        "enable_breakpoint", "evaluate_expression", "evaluate_expressions_batch", "get_breakpoints",
        "get_build_errors", "get_callstack", "get_debug_status", "get_disassembly",
        "get_disassembly_at", "get_dispatcher_health", "get_errors_only", "get_exception",
        "get_full_context", "get_instance_info", "get_locals", "get_modules", "get_output_window",
        "get_registers", "get_request_status", "get_threads", "get_warnings_only", "ping",
        "read_memory", "run_to_cursor", "search_memory", "search_output", "set_breakpoint",
        "set_function_breakpoint", "set_next_statement", "step_into", "step_out", "step_over",
        "stop", "switch_stack_frame", "switch_thread"
    )
    private val manifestBytes = RiderMethodCatalog::class.java.classLoader
        .getResourceAsStream(RESOURCE_NAME)
        ?.use { it.readBytes() }
        ?: throw IllegalStateException("Embedded debugger method manifest is missing: $RESOURCE_NAME")

    private data class Manifest(
        val protocol: String? = null,
        val methods: Map<String, Entry>? = null
    )

    private data class Entry(
        val kind: String? = null,
        val capabilities: List<String>? = null,
        val controlPlane: Boolean = false
    )

    const val protocol = "aidb/2"
    val catalogHash: String = MessageDigest.getInstance("SHA-256")
        .digest(manifestBytes)
        .joinToString("") { "%02x".format(it.toInt() and 0xff) }

    private val methods: Map<String, RiderMethodDescriptor> = run {
        val manifest = Gson().fromJson(String(manifestBytes, Charsets.UTF_8), Manifest::class.java)
        require(manifest.protocol == protocol) { "Debugger method manifest uses an unsupported protocol" }
        val entries = requireNotNull(manifest.methods) { "Debugger method manifest has no methods" }
        require(entries.isNotEmpty()) { "Debugger method manifest has no methods" }
        require(entries.keys.containsAll(supportedMethods)) {
            "Debugger method manifest is missing Rider methods"
        }
        entries.filterKeys { it in supportedMethods }.mapValues { (name, entry) ->
            val kind = entry.kind
            val capabilities = entry.capabilities
            require(name.isNotBlank() && (kind == "query" || kind == "command")) {
                "Debugger method manifest contains an invalid entry: $name"
            }
            require(!capabilities.isNullOrEmpty() && capabilities.all { it.isNotBlank() }) {
                "Debugger method manifest contains invalid capabilities: $name"
            }
            RiderMethodDescriptor(name, kind, capabilities, entry.controlPlane)
        }
    }

    val allCapabilities: List<String> = methods.values
        .flatMap { it.capabilities }
        .distinct()
        .sorted()

    fun get(method: String?): RiderMethodDescriptor? = methods[method?.lowercase()]
}
