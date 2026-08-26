package com.vsdebugger.rider

import com.google.gson.Gson
import com.google.gson.JsonArray
import com.google.gson.JsonElement
import com.google.gson.JsonObject
import com.vsdebugger.rider.protocol.DebugError
import com.vsdebugger.rider.protocol.DebugRequest
import com.vsdebugger.rider.protocol.DebugResponse
import com.vsdebugger.rider.protocol.RiderMethodDescriptor
import java.nio.charset.StandardCharsets
import java.security.MessageDigest
import java.time.Instant
import java.util.UUID
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.CompletableFuture
import java.util.concurrent.RejectedExecutionException
import java.util.concurrent.ThreadFactory
import java.util.concurrent.ThreadPoolExecutor
import java.util.concurrent.TimeUnit
import java.util.concurrent.TimeoutException

class RiderRequestCoordinator internal constructor(
    private val requestHandler: (DebugRequest) -> Any?,
    private val gson: Gson,
    private val identityProvider: () -> RiderDebuggerIdentity = { RiderDebuggerIdentity() },
    private val contextAdvancer: (() -> RiderDebuggerIdentity)? = null
) : AutoCloseable {
    constructor(debuggerService: DebuggerService, gson: Gson) : this(
        debuggerService::handleMethod,
        gson,
        debuggerService::captureDebugIdentity,
        debuggerService::advanceContextEpoch
    )
    companion object {
        private const val QUEUE_CAPACITY = 128
        private const val COMMAND_CAPACITY = 4096
        private const val COMMAND_RESULT_BYTE_CAPACITY = 32L * 1024L * 1024L
        private const val MAX_RETAINED_COMMAND_RESPONSE_BYTES = 15L * 1024L * 1024L
        private const val COMMAND_ACCEPTANCE_WINDOW_MS = 24L * 60L * 60L * 1000L
        private const val COMMAND_CLOCK_SKEW_MS = 5L * 60L * 1000L
        private val IDENTITY_REFRESH_METHODS = setOf("ping", "get_instance_info", "get_debug_status")
    }

    private enum class DispatchState { ENQUEUED, RUNNING, CANCELLED }

    private inner class DispatchTask(
        private val request: DebugRequest,
        private val descriptor: RiderMethodDescriptor,
        private val command: Boolean
    ) : Runnable {
        val completion = CompletableFuture<DebugResponse>()
        private var state = DispatchState.ENQUEUED

        @Synchronized
        fun cancelNotStarted(response: DebugResponse = deadlineNotStarted(request.id!!)): Boolean {
            if (state != DispatchState.ENQUEUED) return false
            state = DispatchState.CANCELLED
            if (command) ledger.complete(request.id!!, response)
            completion.complete(response)
            return true
        }

        @Synchronized
        private fun begin(): Boolean {
            if (state != DispatchState.ENQUEUED) return false
            state = DispatchState.RUNNING
            return true
        }

        override fun run() {
            if (!begin()) return
            val requestId = request.id!!
            val started = Instant.now()
            if (command) ledger.markRunning(requestId, started)
            var handlerInvoked = false
            val response = try {
                val identity = refreshIdentity()
                validateTargetIdentity(request, descriptor, identity) ?: run {
                    setActive(requestId, descriptor.name, started, command)
                    handlerInvoked = true
                    DebugResponse.success(requestId, requestHandler(request))
                }
            } catch (ex: Exception) {
                DebugResponse.failure(requestId, "INTERNAL_ERROR", ex.message ?: "Unknown Rider debugger error")
            }
            try {
                latestIdentity = if (command && handlerInvoked) {
                    (contextAdvancer ?: identityProvider).invoke()
                } else {
                    identityProvider()
                }
                if (command) ledger.complete(requestId, response)
            } finally {
                clearActive(requestId)
                completion.complete(response)
            }
        }
    }

    private sealed class Reservation {
        data object Reserved : Reservation()
        data class Cached(val response: DebugResponse) : Reservation()
        data object InProgress : Reservation()
        data object ResultEvicted : Reservation()
        data object Conflict : Reservation()
        data object CapacityExceeded : Reservation()
    }

    private data class CommandEntry(
        val requestId: String,
        val fingerprint: String,
        val method: String,
        val enqueuedAt: Instant,
        var startedAt: Instant? = null,
        var completedAt: Instant? = null,
        var response: DebugResponse? = null,
        var responseBytes: Long = 0,
        var resultEvicted: Boolean = false,
        var cancellationRequested: Boolean = false
    )

    private inner class CommandLedger {
        private val entries = linkedMapOf<String, CommandEntry>()
        private var resultBytes = 0L

        @Synchronized
        fun reserve(request: DebugRequest): Reservation {
            purgeExpired()
            val requestId = request.id!!
            val fingerprint = fingerprint(request)
            val existing = entries[requestId]
            if (existing != null) {
                if (existing.fingerprint != fingerprint) return Reservation.Conflict
                existing.response?.let { return Reservation.Cached(it) }
                return if (existing.completedAt != null || existing.resultEvicted) {
                    Reservation.ResultEvicted
                } else {
                    Reservation.InProgress
                }
            }
            if (entries.size >= COMMAND_CAPACITY) return Reservation.CapacityExceeded
            entries[requestId] = CommandEntry(
                requestId = requestId,
                fingerprint = fingerprint,
                method = request.method!!.lowercase(),
                enqueuedAt = Instant.now()
            )
            return Reservation.Reserved
        }

        @Synchronized
        fun markRunning(requestId: String, startedAt: Instant) {
            entries[requestId]?.startedAt = startedAt
        }

        @Synchronized
        fun complete(requestId: String, response: DebugResponse) {
            val entry = entries[requestId] ?: return
            if (entry.completedAt != null) return
            val bytes = gson.toJson(response).toByteArray(StandardCharsets.UTF_8).size.toLong()
            entry.completedAt = Instant.now()
            if (bytes > MAX_RETAINED_COMMAND_RESPONSE_BYTES) {
                entry.resultEvicted = true
                return
            }
            entry.response = response
            entry.responseBytes = bytes
            resultBytes += bytes
            evictResultBodies()
        }

        @Synchronized
        fun status(requestId: String): Map<String, Any?> {
            purgeExpired()
            val entry = entries[requestId] ?: return linkedMapOf(
                "requestId" to requestId,
                "state" to "not_found"
            )
            val state = when {
                entry.completedAt == null -> if (entry.startedAt == null) "queued" else "executing"
                entry.resultEvicted -> "completed_result_evicted"
                else -> "completed"
            }
            return linkedMapOf(
                "requestId" to requestId,
                "state" to state,
                "completedAtUtc" to entry.completedAt?.toString(),
                "response" to entry.response?.let { gson.fromJson(gson.toJson(it), Map::class.java) },
                "kind" to "command",
                "startedAtUtc" to entry.startedAt?.toString(),
                "enqueuedAtUtc" to entry.enqueuedAt.toString(),
                "cancellationRequested" to entry.cancellationRequested,
                "cancellationIssued" to false,
                "sideEffectIssued" to (entry.startedAt != null),
                "effectOperation" to entry.method,
                "effectStage" to when {
                    entry.completedAt != null -> "completed"
                    entry.startedAt != null -> "issued"
                    else -> "not_started"
                }
            )
        }

        @Synchronized
        fun requestCancellation(requestId: String) {
            entries[requestId]?.cancellationRequested = true
        }

        @Synchronized
        fun metrics(): LedgerMetrics {
            purgeExpired()
            val completed = entries.values.filter { it.completedAt != null }
            val oldestAge = completed.minOfOrNull {
                System.currentTimeMillis() - it.enqueuedAt.toEpochMilli()
            }?.coerceAtLeast(0L) ?: 0L
            return LedgerMetrics(
                tombstoneCount = entries.size,
                resultCount = entries.values.count { it.response != null },
                resultBytes = resultBytes,
                oldestAgeMs = oldestAge
            )
        }

        private fun purgeExpired() {
            val cutoff = System.currentTimeMillis() - COMMAND_ACCEPTANCE_WINDOW_MS - COMMAND_CLOCK_SKEW_MS
            val expired = entries.values
                .filter { it.enqueuedAt.toEpochMilli() < cutoff && it.completedAt != null }
                .map { it.requestId }
            expired.forEach { id ->
                entries.remove(id)?.let { resultBytes -= it.responseBytes }
            }
        }

        private fun evictResultBodies() {
            if (resultBytes <= COMMAND_RESULT_BYTE_CAPACITY) return
            entries.values
                .asSequence()
                .filter { it.completedAt != null && it.response != null }
                .sortedBy { it.completedAt }
                .forEach { entry ->
                    if (resultBytes <= COMMAND_RESULT_BYTE_CAPACITY) return
                    resultBytes -= entry.responseBytes
                    entry.response = null
                    entry.responseBytes = 0
                    entry.resultEvicted = true
                }
        }
    }

    private data class LedgerMetrics(
        val tombstoneCount: Int,
        val resultCount: Int,
        val resultBytes: Long,
        val oldestAgeMs: Long
    )

    private val ledger = CommandLedger()
    private val actor = ThreadPoolExecutor(
        1,
        1,
        0L,
        TimeUnit.MILLISECONDS,
        ArrayBlockingQueue(QUEUE_CAPACITY),
        ThreadFactory { runnable ->
            Thread(runnable, "aidb-rider-debug-session-actor").apply { isDaemon = true }
        },
        ThreadPoolExecutor.AbortPolicy()
    )
    private val activeLock = Any()
    @Volatile private var latestIdentity = RiderDebuggerIdentity()
    private var activeRequestId: String? = null
    private var activeMethod: String? = null
    private var activeStartedAt: Instant? = null
    private var activeCommand = false
    private val tasks = mutableMapOf<String, DispatchTask>()

    fun dispatch(request: DebugRequest, descriptor: RiderMethodDescriptor, deadlineUnixMs: Long): DebugResponse {
        val requestId = request.id!!
        val command = descriptor.kind == "command"
        if (command) {
            when (val reservation = ledger.reserve(request)) {
                is Reservation.Cached -> return reservation.response
                Reservation.InProgress -> return failure(
                    requestId,
                    "REQUEST_IN_PROGRESS",
                    "The Rider command is already in progress; query get_command_status before retrying",
                    outcomeUnknown = true
                )
                Reservation.ResultEvicted -> return failure(
                    requestId,
                    "RESULT_EVICTED",
                    "The Rider command completed but its cached response was evicted; it will not be executed again"
                )
                Reservation.Conflict -> return failure(
                    requestId,
                    "REQUEST_ID_CONFLICT",
                    "The request ID was already used for a different Rider command"
                )
                Reservation.CapacityExceeded -> return failure(
                    requestId,
                    "COMMAND_LEDGER_CAPACITY",
                    "The Rider command ledger is full; no command was started"
                )
                Reservation.Reserved -> Unit
            }
        }
        if (descriptor.name == "get_request_status") {
            val statusId = request.params?.get("requestId") as? String
                ?: return DebugResponse.failure(requestId, "INVALID_ARGUMENT", "Parameter 'requestId' is required")
            if (!isCanonicalUuid(statusId)) {
                return DebugResponse.failure(requestId, "INVALID_ARGUMENT", "Parameter 'requestId' must be a canonical lowercase UUID")
            }
            return DebugResponse.success(requestId, ledger.status(statusId))
        }
        if (descriptor.name == "cancel_request") {
            val statusId = request.params?.get("requestId") as? String
                ?: return completeControlCommand(
                    requestId,
                    DebugResponse.failure(requestId, "INVALID_ARGUMENT", "Parameter 'requestId' is required")
                )
            if (!isCanonicalUuid(statusId)) {
                return completeControlCommand(
                    requestId,
                    DebugResponse.failure(requestId, "INVALID_ARGUMENT", "Parameter 'requestId' must be a canonical lowercase UUID")
                )
            }
            if (statusId == requestId) {
                return completeControlCommand(
                    requestId,
                    DebugResponse.failure(requestId, "INVALID_ARGUMENT", "A cancellation request cannot cancel itself")
                )
            }
            ledger.requestCancellation(statusId)
            val task = synchronized(activeLock) { tasks[statusId] }
            val cancelled = task?.cancelNotStarted(cancelledNotStarted(statusId)) ?: false
            if (cancelled) task?.let { cancelledTask ->
                actor.remove(cancelledTask)
                synchronized(activeLock) { tasks.remove(statusId, cancelledTask) }
            }
            val status = ledger.status(statusId).toMutableMap()
            status["cancellationIssued"] = cancelled
            return completeControlCommand(requestId, DebugResponse.success(requestId, status))
        }
        if (descriptor.name == "get_dispatcher_health") {
            return DebugResponse.success(requestId, health())
        }

        val task = DispatchTask(request, descriptor, command)
        synchronized(activeLock) { tasks[requestId] = task }
        try {
            actor.execute(task)
        } catch (_: RejectedExecutionException) {
            synchronized(activeLock) { tasks.remove(requestId) }
            val response = failure(requestId, "SERVER_BUSY", "The Rider debugger actor queue is full; no operation was started")
            task.cancelNotStarted(response)
            return response
        }

        val remainingMs = deadlineUnixMs - System.currentTimeMillis()
        if (remainingMs <= 0) {
            return timeoutResponse(task, requestId, command)
        }
        return try {
            task.completion.get(remainingMs, TimeUnit.MILLISECONDS)
        } catch (_: TimeoutException) {
            timeoutResponse(task, requestId, command)
        } catch (ex: Exception) {
            failure(requestId, "INTERNAL_ERROR", ex.message ?: "Rider request coordination failed")
        } finally {
            if (task.completion.isDone) synchronized(activeLock) { tasks.remove(requestId) }
        }
    }

    fun health(): Map<String, Any?> {
        val metrics = ledger.metrics()
        val snapshot = synchronized(activeLock) {
            listOf(activeRequestId, activeMethod, activeStartedAt, activeCommand)
        }
        val startedAt = snapshot[2] as? Instant
        val active = snapshot[0] as? String
        val command = snapshot[3] as Boolean
        return linkedMapOf(
            "state" to if (active == null) "healthy" else "executing",
            "activeRequestId" to active,
            "activeMethod" to snapshot[1] as? String,
            "dispatchStartedAtUtc" to startedAt?.toString(),
            "uiAdmittedAtUtc" to startedAt?.toString(),
            "poisonedAtUtc" to null,
            "elapsedMs" to (startedAt?.let { System.currentTimeMillis() - it.toEpochMilli() } ?: 0L),
            "poisonAfterMs" to 0,
            "pendingUiOperations" to (active != null),
            "sideEffectIssued" to (active != null && command),
            "effectOperation" to if (command) snapshot[1] as? String else null,
            "effectStage" to if (command && active != null) "issued" else null,
            "recoveryAction" to null,
            "activeRequestCount" to if (active == null) 0 else 1,
            "actorQueueDepth" to actor.queue.size,
            "dataListenerCount" to 1,
            "interruptListenerCount" to 0,
            "recoveryListenerCount" to 0,
            "commandTombstoneCount" to metrics.tombstoneCount,
            "commandTombstoneCapacity" to COMMAND_CAPACITY,
            "commandResultCount" to metrics.resultCount,
            "commandResultBytes" to metrics.resultBytes,
            "oldestCommandAgeMs" to metrics.oldestAgeMs,
            "queryResultCount" to 0,
            "queryResultCapacity" to 0,
            "queryResultBytes" to 0,
            "queryResultByteCapacity" to 0
        )
    }

    override fun close() {
        actor.shutdownNow()
    }

    fun identitySnapshot(): RiderDebuggerIdentity = latestIdentity

    private fun timeoutResponse(task: DispatchTask, requestId: String, command: Boolean): DebugResponse {
        if (task.cancelNotStarted()) {
            actor.remove(task)
            synchronized(activeLock) { tasks.remove(requestId, task) }
            return deadlineNotStarted(requestId)
        }
        return if (command) {
            failure(
                requestId,
                "OUTCOME_UNKNOWN",
                "Rider accepted the command but did not complete before the deadline; query get_command_status before retrying",
                outcomeUnknown = true
            )
        } else {
            failure(requestId, "DEADLINE_EXCEEDED", "Rider did not complete the query before the deadline")
        }
    }

    private fun deadlineNotStarted(requestId: String): DebugResponse = failure(
        requestId,
        "DEADLINE_EXCEEDED_NOT_STARTED",
        "The Rider request deadline elapsed before debugger execution started"
    )

    private fun cancelledNotStarted(requestId: String): DebugResponse = failure(
        requestId,
        "CANCELLED_NOT_STARTED",
        "The Rider request was cancelled before debugger execution started"
    )

    private fun completeControlCommand(requestId: String, response: DebugResponse): DebugResponse {
        ledger.markRunning(requestId, Instant.now())
        ledger.complete(requestId, response)
        return response
    }

    private fun failure(
        requestId: String,
        code: String,
        message: String,
        outcomeUnknown: Boolean = false
    ): DebugResponse = DebugResponse(
        id = requestId,
        status = "error",
        error = DebugError(code, message, outcomeUnknown, if (outcomeUnknown) requestId else null)
    )

    private fun refreshIdentity(): RiderDebuggerIdentity = identityProvider().also {
        latestIdentity = it
    }

    private fun validateTargetIdentity(
        request: DebugRequest,
        descriptor: RiderMethodDescriptor,
        current: RiderDebuggerIdentity
    ): DebugResponse? {
        if (descriptor.name in IDENTITY_REFRESH_METHODS) return null
        val target = request.meta?.target ?: return null
        val requestId = request.id!!
        if (target.debugSessionGeneration != current.debugSessionGeneration) {
            return staleIdentityFailure(
                requestId,
                "STALE_DEBUG_SESSION",
                "Rider debug-session generation changed before the request reached debugger execution",
                current
            )
        }
        if (target.breakSequence != current.breakSequence || target.contextEpoch != current.contextEpoch) {
            return staleIdentityFailure(
                requestId,
                "STALE_DEBUG_CONTEXT",
                "Rider break or debugger-context epoch changed before the request reached debugger execution",
                current
            )
        }
        return null
    }

    private fun staleIdentityFailure(
        requestId: String,
        code: String,
        message: String,
        current: RiderDebuggerIdentity
    ): DebugResponse = DebugResponse(
        id = requestId,
        status = "error",
        error = DebugError(
            code = code,
            message = message,
            details = linkedMapOf(
                "debugSessionGeneration" to current.debugSessionGeneration,
                "breakSequence" to current.breakSequence,
                "contextEpoch" to current.contextEpoch
            )
        )
    )

    private fun setActive(requestId: String, method: String, startedAt: Instant, command: Boolean) {
        synchronized(activeLock) {
            activeRequestId = requestId
            activeMethod = method
            activeStartedAt = startedAt
            activeCommand = command
        }
    }

    private fun clearActive(requestId: String) {
        synchronized(activeLock) {
            if (activeRequestId == requestId) {
                activeRequestId = null
                activeMethod = null
                activeStartedAt = null
                activeCommand = false
            }
            tasks.remove(requestId)
        }
    }

    private fun fingerprint(request: DebugRequest): String {
        val material = JsonObject().apply {
            addProperty("method", request.method!!.lowercase())
            add("params", canonicalize(gson.toJsonTree(request.params ?: emptyMap<String, Any?>())))
        }
        return MessageDigest.getInstance("SHA-256")
            .digest(gson.toJson(material).toByteArray(StandardCharsets.UTF_8))
            .joinToString("") { "%02x".format(it.toInt() and 0xff) }
    }

    private fun canonicalize(element: JsonElement): JsonElement = when {
        element.isJsonObject -> JsonObject().also { output ->
            element.asJsonObject.entrySet().sortedBy { it.key }.forEach { (key, value) ->
                output.add(key, canonicalize(value))
            }
        }
        element.isJsonArray -> JsonArray().also { output ->
            element.asJsonArray.forEach { output.add(canonicalize(it)) }
        }
        else -> element.deepCopy()
    }

    private fun isCanonicalUuid(value: String): Boolean = try {
        UUID.fromString(value).toString() == value
    } catch (_: IllegalArgumentException) {
        false
    }
}
