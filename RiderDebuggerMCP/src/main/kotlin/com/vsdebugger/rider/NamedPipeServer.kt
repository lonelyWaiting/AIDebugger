package com.vsdebugger.rider

import com.google.gson.GsonBuilder
import com.google.gson.JsonElement
import com.google.gson.JsonParser
import com.google.gson.stream.JsonReader
import com.google.gson.stream.JsonToken
import com.intellij.openapi.Disposable
import com.sun.jna.Native
import com.sun.jna.Pointer
import com.sun.jna.WString
import com.sun.jna.platform.win32.Advapi32
import com.sun.jna.platform.win32.Advapi32Util
import com.sun.jna.platform.win32.Kernel32
import com.sun.jna.platform.win32.WinBase
import com.sun.jna.platform.win32.WinDef
import com.sun.jna.platform.win32.WinNT
import com.sun.jna.ptr.IntByReference
import com.sun.jna.ptr.PointerByReference
import com.vsdebugger.rider.protocol.DebugRequest
import com.vsdebugger.rider.protocol.DebugResponse
import com.vsdebugger.rider.protocol.DebugServerIdentity
import com.vsdebugger.rider.protocol.RiderMethodCatalog
import java.io.StringReader
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.charset.CodingErrorAction
import java.nio.charset.StandardCharsets
import java.security.MessageDigest
import java.util.Locale
import java.util.UUID
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors
import java.util.concurrent.ThreadPoolExecutor
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

class NamedPipeServer(
    private val debuggerService: DebuggerService,
    private val log: (String) -> Unit
) : Disposable {
    companion object {
        private const val PIPE_PREFIX = "AIDebuggerRider"
        private const val MAX_REQUEST_BYTES = 4 * 1024 * 1024
        private const val MAX_RESPONSE_BYTES = 16 * 1024 * 1024
        private const val MAX_JSON_DEPTH = 64
        private const val MAX_FUTURE_DEADLINE_MS = 15 * 60 * 1000L
        private const val PIPE_TIMEOUT_MS = 5000
        private const val MAX_CONNECTIONS = 16
        private const val SDDL_REVISION_1 = 1
        private const val UUID7_ACCEPTANCE_WINDOW_MS = 24L * 60L * 60L * 1000L
        private const val UUID7_FUTURE_SKEW_MS = 5L * 60L * 1000L
    }

    private interface Advapi32Ex : com.sun.jna.win32.StdCallLibrary {
        fun ConvertStringSecurityDescriptorToSecurityDescriptorW(
            sddl: WString,
            revision: Int,
            securityDescriptor: PointerByReference,
            size: IntByReference?
        ): Boolean
    }

    private interface Kernel32Ex : com.sun.jna.win32.StdCallLibrary {
        fun GetNamedPipeClientProcessId(pipe: WinNT.HANDLE, processId: IntByReference): Boolean
    }

    private val running = AtomicBoolean(false)
    private val acceptor = Executors.newSingleThreadExecutor { runnable ->
        Thread(runnable, "aidb-rider-pipe-acceptor").apply { isDaemon = true }
    }
    private val connections = ThreadPoolExecutor(
        MAX_CONNECTIONS,
        MAX_CONNECTIONS,
        60L,
        TimeUnit.SECONDS,
        ArrayBlockingQueue(MAX_CONNECTIONS),
        { runnable -> Thread(runnable, "aidb-rider-pipe-client").apply { isDaemon = true } },
        ThreadPoolExecutor.AbortPolicy()
    )
    private val gson = GsonBuilder().serializeNulls().disableHtmlEscaping().create()
    private val coordinator = RiderRequestCoordinator(debuggerService, gson)
    private val pipeName = "${PIPE_PREFIX}_${ProcessHandle.current().pid()}"
    private val activeHandles = ConcurrentHashMap.newKeySet<WinNT.HANDLE>()
    private val advapiEx: Advapi32Ex by lazy { Native.load("Advapi32", Advapi32Ex::class.java) }
    private val kernel32Ex: Kernel32Ex by lazy { Native.load("Kernel32", Kernel32Ex::class.java) }
    @Volatile private var registration: RiderInstanceRegistration? = null

    init {
        debuggerService.setPipeName(pipeName)
    }

    fun getPipeName(): String = pipeName

    fun start() {
        if (!running.compareAndSet(false, true)) return
        if (!isWindows()) {
            log("Named pipes are only supported on Windows. Server not started.")
            running.set(false)
            return
        }
        try {
            createPipeSecurity().also(::freePipeSecurity)
            registration = RiderInstanceRegistration(pipeName, log)
        } catch (ex: Exception) {
            running.set(false)
            log("Rider debugger server failed closed during security setup: ${ex.javaClass.simpleName}")
            return
        }
        log("Starting authenticated Rider AIDB/2 server on \\\\.\\pipe\\$pipeName")
        log("Named pipe and instance registration security: current-user owner only.")
        acceptor.submit { serverLoop() }
    }

    fun stop() {
        val wasRunning = running.getAndSet(false)
        closeActiveHandles()
        registration?.close()
        registration = null
        coordinator.close()
        acceptor.shutdownNow()
        connections.shutdownNow()
        if (wasRunning) log("Named pipe server stopped.")
    }

    override fun dispose() {
        stop()
    }

    private fun serverLoop() {
        while (running.get()) {
            val security = try {
                createPipeSecurity()
            } catch (ex: Exception) {
                log("Owner-only pipe security creation failed; Rider server stopped: ${ex.javaClass.simpleName}")
                running.set(false)
                break
            }
            val handle = try {
                Kernel32.INSTANCE.CreateNamedPipe(
                    "\\\\.\\pipe\\$pipeName",
                    WinBase.PIPE_ACCESS_DUPLEX,
                    WinBase.PIPE_TYPE_BYTE or WinBase.PIPE_READMODE_BYTE or WinBase.PIPE_WAIT,
                    WinBase.PIPE_UNLIMITED_INSTANCES,
                    64 * 1024,
                    64 * 1024,
                    PIPE_TIMEOUT_MS,
                    security.attributes
                )
            } finally {
                freePipeSecurity(security)
            }

            if (handle == WinBase.INVALID_HANDLE_VALUE) {
                log("Failed to create owner-only Rider named pipe. Error=${Kernel32.INSTANCE.GetLastError()}")
                running.set(false)
                break
            }

            activeHandles.add(handle)
            try {
                val connected = Kernel32.INSTANCE.ConnectNamedPipe(handle, null)
                val lastError = Kernel32.INSTANCE.GetLastError()
                if (!connected && lastError != WinError.ERROR_PIPE_CONNECTED) {
                    if (running.get()) log("Failed to connect Rider named pipe. Error=$lastError")
                    closeHandle(handle)
                    continue
                }
                connections.submit { handleConnection(handle) }
            } catch (ex: Exception) {
                if (running.get()) log("Rider pipe error: ${ex.javaClass.simpleName}")
                closeHandle(handle)
            }
        }
        registration?.close()
        registration = null
    }

    private fun handleConnection(handle: WinNT.HANDLE) {
        try {
            val clientProcessId = IntByReference()
            if (!kernel32Ex.GetNamedPipeClientProcessId(handle, clientProcessId) || clientProcessId.value <= 0) {
                throw IllegalStateException("Unable to verify Rider pipe client process")
            }
            val connection = PipeConnection(handle)
            while (running.get()) {
                val frame = connection.readFrame() ?: break
                val response = processRequest(frame, clientProcessId.value)
                connection.writeFrame(gson.toJson(response).toByteArray(StandardCharsets.UTF_8))
            }
        } catch (ex: Exception) {
            if (running.get()) log("Rider pipe connection closed: ${ex.javaClass.simpleName}")
        } finally {
            closeHandle(handle)
        }
    }

    private fun processRequest(requestBytes: ByteArray, actualClientProcessId: Int): DebugResponse {
        var requestId: String? = null
        return try {
            val requestJson = decodeStrictUtf8(requestBytes)
            validateStrictJson(requestJson)
            val json = JsonParser.parseString(requestJson)
            validateEnvelopeShape(json)
            val request = gson.fromJson(json, DebugRequest::class.java)
            requestId = request.id
            val descriptor = validateRequest(request, actualClientProcessId)
            decorate(coordinator.dispatch(request, descriptor, request.meta!!.deadlineUnixMs!!))
        } catch (ex: ProtocolException) {
            decorate(DebugResponse.failure(requestId, ex.code, ex.message ?: "Invalid Rider request"))
        } catch (ex: Exception) {
            decorate(DebugResponse.failure(requestId, "INTERNAL_ERROR", ex.message ?: "Unknown Rider server error"))
        }
    }

    private fun validateRequest(request: DebugRequest, actualClientProcessId: Int) =
        RiderMethodCatalog.get(request.method)?.also { descriptor ->
            if (request.protocol != RiderMethodCatalog.protocol) {
                throw ProtocolException("PROTOCOL_MISMATCH", "Unsupported Rider debugger protocol")
            }
            val requestId = request.id ?: throw ProtocolException("INVALID_REQUEST", "Missing request id")
            if (!isCanonicalUuid(requestId)) {
                throw ProtocolException("INVALID_REQUEST_ID", "Request id must be a canonical lowercase UUID")
            }
            if (descriptor.kind == "command" && !isAcceptedUuid7(requestId)) {
                throw ProtocolException(
                    "INVALID_REQUEST_ID",
                    "Rider command ids must be UUIDv7 values inside the 24-hour acceptance window"
                )
            }
            val currentRegistration = registration
                ?: throw ProtocolException("SERVER_UNAVAILABLE", "Rider instance registration is unavailable")
            val meta = request.meta ?: throw ProtocolException("INVALID_REQUEST", "Missing request metadata")
            val tokenMatches = MessageDigest.isEqual(
                meta.authToken?.toByteArray(StandardCharsets.UTF_8) ?: ByteArray(0),
                currentRegistration.authToken.toByteArray(StandardCharsets.UTF_8)
            )
            if (!tokenMatches) throw ProtocolException("AUTH_FAILED", "Rider request authentication failed")
            val deadline = meta.deadlineUnixMs
                ?: throw ProtocolException("INVALID_REQUEST", "Missing request deadline")
            val now = System.currentTimeMillis()
            if (deadline <= now) throw ProtocolException("DEADLINE_EXCEEDED_NOT_STARTED", "Request deadline already elapsed")
            if (deadline - now > MAX_FUTURE_DEADLINE_MS) {
                throw ProtocolException("INVALID_REQUEST", "Request deadline exceeds the Rider server limit")
            }
            if (meta.kind != descriptor.kind || meta.capability != descriptor.capabilities.first()) {
                throw ProtocolException("PROTOCOL_MISMATCH", "Rider request method metadata does not match the catalog")
            }
            if (meta.clientProcessId != actualClientProcessId) {
                throw ProtocolException("INVALID_CLIENT", "Rider pipe client process identity mismatch")
            }
            val clientStart = try {
                RiderInstanceRegistration.processStartTimeUtcTicks(actualClientProcessId)
            } catch (_: Exception) {
                throw ProtocolException("INVALID_CLIENT", "Rider pipe client process is unavailable")
            }
            if (meta.clientProcessStartTimeUtcTicks != clientStart) {
                throw ProtocolException("INVALID_CLIENT", "Rider pipe client process start time mismatch")
            }
            val target = meta.target ?: throw ProtocolException("INVALID_TARGET", "Missing Rider target identity")
            if (target.processId != ProcessHandle.current().pid().toInt() ||
                target.processStartTimeUtcTicks != currentRegistration.processStartTimeUtcTicks ||
                target.serverInstanceId != currentRegistration.serverInstanceId) {
                throw ProtocolException("STALE_TARGET", "Rider target identity no longer matches the selected instance")
            }
        } ?: throw ProtocolException("METHOD_NOT_FOUND", "Unknown Rider debugger method")

    private fun decorate(response: DebugResponse): DebugResponse {
        val current = registration
        val debuggerIdentity = coordinator.identitySnapshot()
        val identity = current?.let {
            DebugServerIdentity(
                processId = ProcessHandle.current().pid().toInt(),
                processStartTimeUtcTicks = it.processStartTimeUtcTicks,
                serverInstanceId = it.serverInstanceId,
                protocolCatalogHash = RiderMethodCatalog.catalogHash,
                debugSessionGeneration = debuggerIdentity.debugSessionGeneration,
                breakSequence = debuggerIdentity.breakSequence,
                contextEpoch = debuggerIdentity.contextEpoch
            )
        }
        return response.copy(protocol = RiderMethodCatalog.protocol, server = identity)
    }

    private fun validateEnvelopeShape(json: JsonElement) {
        if (!json.isJsonObject) throw ProtocolException("INVALID_REQUEST", "Rider request must be a JSON object")
        val root = json.asJsonObject
        rejectUnknownFields(root.keySet(), setOf("protocol", "id", "method", "params", "meta"), "request")
        val params = root["params"]
        if (params == null || !params.isJsonObject) throw ProtocolException("INVALID_REQUEST", "Request params must be an object")
        val meta = root["meta"]
        if (meta == null || !meta.isJsonObject) throw ProtocolException("INVALID_REQUEST", "Request meta must be an object")
        rejectUnknownFields(
            meta.asJsonObject.keySet(),
            setOf("authToken", "deadlineUnixMs", "kind", "capability", "clientProcessId", "clientProcessStartTimeUtcTicks", "target"),
            "request meta"
        )
        val target = meta.asJsonObject["target"]
        if (target == null || !target.isJsonObject) throw ProtocolException("INVALID_REQUEST", "Request target must be an object")
        rejectUnknownFields(
            target.asJsonObject.keySet(),
            setOf("processId", "processStartTimeUtcTicks", "serverInstanceId", "debugSessionGeneration", "breakSequence", "contextEpoch"),
            "request target"
        )
    }

    private fun rejectUnknownFields(actual: Set<String>, allowed: Set<String>, context: String) {
        val unknown = actual - allowed
        if (unknown.isNotEmpty()) throw ProtocolException("INVALID_REQUEST", "$context contains unknown fields")
    }

    private fun decodeStrictUtf8(bytes: ByteArray): String = try {
        StandardCharsets.UTF_8.newDecoder()
            .onMalformedInput(CodingErrorAction.REPORT)
            .onUnmappableCharacter(CodingErrorAction.REPORT)
            .decode(ByteBuffer.wrap(bytes))
            .toString()
    } catch (_: Exception) {
        throw ProtocolException("INVALID_UTF8", "Rider request is not valid UTF-8")
    }

    private fun validateStrictJson(json: String) {
        val reader = JsonReader(StringReader(json)).apply { isLenient = false }
        try {
            readJsonValue(reader, 0)
            if (reader.peek() != JsonToken.END_DOCUMENT) {
                throw ProtocolException("PARSE_ERROR", "Rider request has trailing JSON content")
            }
        } catch (ex: ProtocolException) {
            throw ex
        } catch (_: Exception) {
            throw ProtocolException("PARSE_ERROR", "Failed to parse Rider request")
        }
    }

    private fun readJsonValue(reader: JsonReader, depth: Int) {
        if (depth > MAX_JSON_DEPTH) throw ProtocolException("INVALID_REQUEST", "Rider request exceeds JSON depth limit")
        when (reader.peek()) {
            JsonToken.BEGIN_OBJECT -> {
                reader.beginObject()
                val names = mutableSetOf<String>()
                while (reader.hasNext()) {
                    val name = reader.nextName()
                    if (!names.add(name)) throw ProtocolException("INVALID_REQUEST", "Rider request contains duplicate JSON properties")
                    readJsonValue(reader, depth + 1)
                }
                reader.endObject()
            }
            JsonToken.BEGIN_ARRAY -> {
                reader.beginArray()
                while (reader.hasNext()) readJsonValue(reader, depth + 1)
                reader.endArray()
            }
            JsonToken.STRING -> reader.nextString()
            JsonToken.NUMBER -> reader.nextString()
            JsonToken.BOOLEAN -> reader.nextBoolean()
            JsonToken.NULL -> reader.nextNull()
            else -> throw ProtocolException("PARSE_ERROR", "Rider request contains invalid JSON")
        }
    }

    private fun isCanonicalUuid(value: String): Boolean = try {
        UUID.fromString(value).toString() == value
    } catch (_: IllegalArgumentException) {
        false
    }

    private fun isAcceptedUuid7(value: String): Boolean {
        val parsed = try { UUID.fromString(value) } catch (_: IllegalArgumentException) { return false }
        if (parsed.version() != 7 || parsed.variant() != 2) return false
        val compact = value.replace("-", "")
        val timestamp = compact.substring(0, 12).toLongOrNull(16) ?: return false
        val now = System.currentTimeMillis()
        return timestamp >= now - UUID7_ACCEPTANCE_WINDOW_MS && timestamp <= now + UUID7_FUTURE_SKEW_MS
    }

    private fun isWindows(): Boolean = System.getProperty("os.name").lowercase(Locale.ROOT).contains("win")

    private data class PipeSecurity(val attributes: WinBase.SECURITY_ATTRIBUTES, val descriptor: Pointer)

    private fun createPipeSecurity(): PipeSecurity {
        val token = WinNT.HANDLEByReference()
        if (!Advapi32.INSTANCE.OpenProcessToken(Kernel32.INSTANCE.GetCurrentProcess(), WinNT.TOKEN_QUERY, token)) {
            throw IllegalStateException("Unable to determine current Windows user SID")
        }
        val sid = try {
            Advapi32Util.getTokenAccount(token.value).sidString
        } finally {
            Kernel32.INSTANCE.CloseHandle(token.value)
        }
        val descriptor = PointerByReference()
        val sddl = "D:P(A;;GA;;;$sid)"
        if (!advapiEx.ConvertStringSecurityDescriptorToSecurityDescriptorW(
                WString(sddl),
                SDDL_REVISION_1,
                descriptor,
                null
            ) || descriptor.value == null) {
            throw IllegalStateException("Unable to create owner-only pipe ACL. Error=${Kernel32.INSTANCE.GetLastError()}")
        }
        val attributes = WinBase.SECURITY_ATTRIBUTES()
        attributes.dwLength = WinDef.DWORD(attributes.size().toLong())
        attributes.lpSecurityDescriptor = descriptor.value
        attributes.bInheritHandle = false
        attributes.write()
        return PipeSecurity(attributes, descriptor.value)
    }

    private fun freePipeSecurity(security: PipeSecurity) {
        Kernel32.INSTANCE.LocalFree(security.descriptor)
    }

    private fun closeActiveHandles() {
        activeHandles.toList().forEach(::closeHandle)
    }

    private fun closeHandle(handle: WinNT.HANDLE) {
        if (!activeHandles.remove(handle)) return
        tryCancelIo(handle)
        Kernel32.INSTANCE.DisconnectNamedPipe(handle)
        Kernel32.INSTANCE.CloseHandle(handle)
    }

    private fun tryCancelIo(handle: WinNT.HANDLE) {
        try {
            Kernel32::class.java.methods.firstOrNull {
                it.name == "CancelIoEx" && it.parameterTypes.size == 2
            }?.invoke(Kernel32.INSTANCE, handle, Pointer.NULL)
        } catch (_: Exception) {
            // Best-effort cancellation only during shutdown.
        }
    }

    private class PipeConnection(private val handle: WinNT.HANDLE) {
        fun readFrame(): ByteArray? {
            val prefix = readExact(4) ?: return null
            val length = ByteBuffer.wrap(prefix).order(ByteOrder.BIG_ENDIAN).int
            if (length <= 0 || length > MAX_REQUEST_BYTES) {
                throw IllegalStateException("Rider request frame exceeds the size limit")
            }
            return readExact(length) ?: throw IllegalStateException("Rider request frame ended early")
        }

        fun writeFrame(payload: ByteArray) {
            if (payload.size > MAX_RESPONSE_BYTES) throw IllegalStateException("Rider response exceeds the size limit")
            val frame = ByteBuffer.allocate(4 + payload.size)
                .order(ByteOrder.BIG_ENDIAN)
                .putInt(payload.size)
                .put(payload)
                .array()
            var offset = 0
            while (offset < frame.size) {
                val written = IntByReference()
                val chunk = frame.copyOfRange(offset, frame.size)
                if (!Kernel32.INSTANCE.WriteFile(handle, chunk, chunk.size, written, null) || written.value <= 0) {
                    throw IllegalStateException("Failed to write Rider response frame")
                }
                offset += written.value
            }
        }

        private fun readExact(size: Int): ByteArray? {
            val output = ByteArray(size)
            var offset = 0
            while (offset < size) {
                val buffer = ByteArray(minOf(64 * 1024, size - offset))
                val read = IntByReference()
                val success = Kernel32.INSTANCE.ReadFile(handle, buffer, buffer.size, read, null)
                if (!success || read.value <= 0) return if (offset == 0) null else throw IllegalStateException("Rider frame ended early")
                buffer.copyInto(output, offset, 0, read.value)
                offset += read.value
            }
            return output
        }
    }

    private class ProtocolException(val code: String, message: String) : Exception(message)

    private object WinError {
        const val ERROR_PIPE_CONNECTED = 535
    }
}
