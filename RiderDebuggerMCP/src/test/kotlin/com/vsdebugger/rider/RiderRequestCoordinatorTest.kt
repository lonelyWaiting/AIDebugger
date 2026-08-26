package com.vsdebugger.rider

import com.google.gson.GsonBuilder
import com.vsdebugger.rider.protocol.DebugRequest
import com.vsdebugger.rider.protocol.DebugRequestMetadata
import com.vsdebugger.rider.protocol.DebugResponse
import com.vsdebugger.rider.protocol.DebugTargetIdentity
import com.vsdebugger.rider.protocol.RiderMethodCatalog
import java.util.UUID
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class RiderRequestCoordinatorTest {
    @Test
    fun instanceRegistrationIsWrittenAndRemovedAtomically() {
        val pipeName = "AIDebuggerRiderTest_${ProcessHandle.current().pid()}_${UUID.randomUUID().toString().replace("-", "")}"
        val path = java.nio.file.Path.of(
            System.getenv("LOCALAPPDATA"),
            "AIDebugger",
            "instances",
            "$pipeName.json"
        )
        val registration = RiderInstanceRegistration(pipeName) { }
        try {
            assertTrue(java.nio.file.Files.isRegularFile(path))
            val payload = java.nio.file.Files.readString(path)
            assertTrue(payload.contains(registration.serverInstanceId))
            assertTrue(payload.contains(RiderMethodCatalog.catalogHash))
        } finally {
            registration.close()
        }
        assertEquals(false, java.nio.file.Files.exists(path))
    }

    @Test
    fun catalogHashMatchesBundledManifest() {
        val bytes = javaClass.classLoader.getResourceAsStream("debug_method_manifest.json")!!.use { it.readBytes() }
        val expected = java.security.MessageDigest.getInstance("SHA-256")
            .digest(bytes)
            .joinToString("") { "%02x".format(it.toInt() and 0xff) }
        assertEquals(64, RiderMethodCatalog.catalogHash.length)
        assertEquals(expected, RiderMethodCatalog.catalogHash)
        assertEquals(
            621_355_968_000_000_000L,
            RiderInstanceRegistration.instantToDotNetTicks(java.time.Instant.EPOCH)
        )
    }

    @Test
    fun commandTimeoutIsRecoverableAndNeverReplayed() {
        val entered = CountDownLatch(1)
        val release = CountDownLatch(1)
        val calls = AtomicInteger()
        val coordinator = RiderRequestCoordinator({
            calls.incrementAndGet()
            entered.countDown()
            release.await(5, TimeUnit.SECONDS)
            mapOf("success" to true, "message" to "continued")
        }, GsonBuilder().serializeNulls().create())

        try {
            val requestId = uuid7()
            val request = DebugRequest(id = requestId, method = "continue", params = emptyMap())
            val result = coordinator.dispatch(
                request,
                requireNotNull(RiderMethodCatalog.get("continue")),
                System.currentTimeMillis() + 100
            )
            assertTrue(entered.await(1, TimeUnit.SECONDS))
            assertEquals("OUTCOME_UNKNOWN", result.error?.code)
            assertEquals(requestId, result.error?.requestId)

            release.countDown()
            val completed = waitForCompleted(coordinator, requestId)
            assertEquals("completed", completed["state"])

            val duplicate = coordinator.dispatch(
                request,
                requireNotNull(RiderMethodCatalog.get("continue")),
                System.currentTimeMillis() + 1_000
            )
            assertEquals(null, duplicate.error)
            assertEquals(1, calls.get())
        } finally {
            release.countDown()
            coordinator.close()
        }
    }

    @Test
    fun actorSerializesDebuggerAccess() {
        val concurrent = AtomicInteger()
        val maximum = AtomicInteger()
        val coordinator = RiderRequestCoordinator({
            val active = concurrent.incrementAndGet()
            maximum.updateAndGet { maxOf(it, active) }
            Thread.sleep(40)
            concurrent.decrementAndGet()
            mapOf("success" to true)
        }, GsonBuilder().serializeNulls().create())
        val executor = java.util.concurrent.Executors.newFixedThreadPool(4)
        try {
            val futures = (0 until 4).map {
                executor.submit<DebugResponse> {
                    coordinator.dispatch(
                        DebugRequest(id = uuid7(), method = "continue", params = emptyMap()),
                        requireNotNull(RiderMethodCatalog.get("continue")),
                        System.currentTimeMillis() + 2_000
                    )
                }
            }
            futures.forEach { assertEquals(null, it.get(3, TimeUnit.SECONDS).error) }
            assertEquals(1, maximum.get())
        } finally {
            executor.shutdownNow()
            coordinator.close()
        }
    }

    @Test
    fun queuedCommandCanBeCancelledWithoutExecution() {
        val entered = CountDownLatch(1)
        val release = CountDownLatch(1)
        val calls = AtomicInteger()
        val coordinator = RiderRequestCoordinator({
            calls.incrementAndGet()
            entered.countDown()
            release.await(5, TimeUnit.SECONDS)
            mapOf("success" to true)
        }, GsonBuilder().serializeNulls().create())
        val executor = java.util.concurrent.Executors.newFixedThreadPool(2)
        try {
            val firstId = uuid7()
            val secondId = uuid7()
            val first = executor.submit<DebugResponse> {
                coordinator.dispatch(
                    DebugRequest(id = firstId, method = "continue", params = emptyMap()),
                    requireNotNull(RiderMethodCatalog.get("continue")),
                    System.currentTimeMillis() + 3_000
                )
            }
            assertTrue(entered.await(1, TimeUnit.SECONDS))
            val second = executor.submit<DebugResponse> {
                coordinator.dispatch(
                    DebugRequest(id = secondId, method = "continue", params = emptyMap()),
                    requireNotNull(RiderMethodCatalog.get("continue")),
                    System.currentTimeMillis() + 3_000
                )
            }
            repeat(50) {
                if ((coordinator.health()["actorQueueDepth"] as Int) > 0) return@repeat
                Thread.sleep(10)
            }
            val cancel = coordinator.dispatch(
                DebugRequest(
                    id = uuid7(),
                    method = "cancel_request",
                    params = mapOf("requestId" to secondId)
                ),
                requireNotNull(RiderMethodCatalog.get("cancel_request")),
                System.currentTimeMillis() + 1_000
            )
            assertEquals(true, (cancel.result as Map<*, *>)["cancellationIssued"])
            assertEquals("CANCELLED_NOT_STARTED", second.get(1, TimeUnit.SECONDS).error?.code)
            assertEquals(0, coordinator.health()["actorQueueDepth"])
            assertEquals(1, calls.get())
            release.countDown()
            assertEquals(null, first.get(1, TimeUnit.SECONDS).error)
        } finally {
            release.countDown()
            executor.shutdownNow()
            coordinator.close()
        }
    }

    @Test
    fun staleDebuggerIdentityIsRejectedAtActorAdmissionAndCommandsAdvanceContext() {
        val identity = AtomicReference(RiderDebuggerIdentity(1, 2, 3))
        val calls = AtomicInteger()
        val coordinator = RiderRequestCoordinator(
            requestHandler = {
                calls.incrementAndGet()
                mapOf("success" to true)
            },
            gson = GsonBuilder().serializeNulls().create(),
            identityProvider = { identity.get() },
            contextAdvancer = {
                identity.updateAndGet { current ->
                    current.copy(contextEpoch = current.contextEpoch + 1)
                }
            }
        )
        try {
            val accepted = coordinator.dispatch(
                request("get_locals", RiderDebuggerIdentity(1, 2, 3)),
                requireNotNull(RiderMethodCatalog.get("get_locals")),
                System.currentTimeMillis() + 1_000
            )
            assertEquals(null, accepted.error)
            assertEquals(1, calls.get())

            identity.set(RiderDebuggerIdentity(1, 2, 4))
            val stale = coordinator.dispatch(
                request("get_locals", RiderDebuggerIdentity(1, 2, 3)),
                requireNotNull(RiderMethodCatalog.get("get_locals")),
                System.currentTimeMillis() + 1_000
            )
            assertEquals("STALE_DEBUG_CONTEXT", stale.error?.code)
            assertEquals(1, calls.get())

            val command = coordinator.dispatch(
                request("continue", RiderDebuggerIdentity(1, 2, 4), uuid7()),
                requireNotNull(RiderMethodCatalog.get("continue")),
                System.currentTimeMillis() + 1_000
            )
            assertEquals(null, command.error)
            assertEquals(2, calls.get())
            assertEquals(5, coordinator.identitySnapshot().contextEpoch)
        } finally {
            coordinator.close()
        }
    }

    @Test
    fun oversizedCompletedCommandKeepsTombstoneWithoutUnsendableRecoveryBody() {
        val coordinator = RiderRequestCoordinator({
            mapOf("payload" to "x".repeat(16 * 1024 * 1024))
        }, GsonBuilder().serializeNulls().create())
        try {
            val requestId = uuid7()
            val request = DebugRequest(id = requestId, method = "continue", params = emptyMap())
            val completed = coordinator.dispatch(
                request,
                requireNotNull(RiderMethodCatalog.get("continue")),
                System.currentTimeMillis() + 5_000
            )
            assertEquals(null, completed.error)

            val status = coordinator.dispatch(
                DebugRequest(
                    id = UUID.randomUUID().toString(),
                    method = "get_request_status",
                    params = mapOf("requestId" to requestId)
                ),
                requireNotNull(RiderMethodCatalog.get("get_request_status")),
                System.currentTimeMillis() + 1_000
            ).result as Map<*, *>
            assertEquals("completed_result_evicted", status["state"])
            assertEquals(null, status["response"])

            val duplicate = coordinator.dispatch(
                request,
                requireNotNull(RiderMethodCatalog.get("continue")),
                System.currentTimeMillis() + 1_000
            )
            assertEquals("RESULT_EVICTED", duplicate.error?.code)
        } finally {
            coordinator.close()
        }
    }

    private fun waitForCompleted(
        coordinator: RiderRequestCoordinator,
        requestId: String
    ): Map<*, *> {
        repeat(50) {
            val response = coordinator.dispatch(
                DebugRequest(
                    id = UUID.randomUUID().toString(),
                    method = "get_request_status",
                    params = mapOf("requestId" to requestId)
                ),
                requireNotNull(RiderMethodCatalog.get("get_request_status")),
                System.currentTimeMillis() + 1_000
            )
            val status = response.result as Map<*, *>
            if (status["state"] == "completed") return status
            Thread.sleep(20)
        }
        throw AssertionError("Command did not reach a recoverable completed state")
    }

    private fun uuid7(): String {
        val timestamp = System.currentTimeMillis() and ((1L shl 48) - 1)
        val random = UUID.randomUUID().leastSignificantBits and ((1L shl 62) - 1)
        val most = (timestamp shl 16) or (7L shl 12) or (random ushr 50 and 0xfff)
        val least = Long.MIN_VALUE or (random and Long.MAX_VALUE)
        return UUID(most, least).toString()
    }

    private fun request(
        method: String,
        identity: RiderDebuggerIdentity,
        requestId: String = UUID.randomUUID().toString()
    ): DebugRequest = DebugRequest(
        id = requestId,
        method = method,
        params = emptyMap(),
        meta = DebugRequestMetadata(
            clientProcessId = 10,
            clientProcessStartTimeUtcTicks = 20,
            target = DebugTargetIdentity(
                debugSessionGeneration = identity.debugSessionGeneration,
                breakSequence = identity.breakSequence,
                contextEpoch = identity.contextEpoch
            )
        )
    )
}
