package com.vsdebugger.rider

import com.google.gson.GsonBuilder
import com.intellij.openapi.Disposable
import com.sun.jna.Native
import com.sun.jna.Pointer
import com.sun.jna.platform.win32.Kernel32
import com.sun.jna.platform.win32.WinBase
import com.sun.jna.platform.win32.WinDef
import com.sun.jna.platform.win32.WinNT
import com.sun.jna.ptr.IntByReference
import com.sun.jna.ptr.PointerByReference
import com.vsdebugger.rider.protocol.DebugRequest
import com.vsdebugger.rider.protocol.DebugResponse
import java.io.ByteArrayOutputStream
import java.nio.charset.StandardCharsets
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

class NamedPipeServer(
    private val debuggerService: DebuggerService,
    private val log: (String) -> Unit
) : Disposable {
    companion object {
        private const val PIPE_PREFIX = "AIDebuggerRider"
        private const val MAX_BUFFER = 1024 * 1024
        private const val PIPE_TIMEOUT_MS = 5000
        private const val SDDL_REVISION_1 = 1
    }

    private val running = AtomicBoolean(false)
    private val acceptor = Executors.newSingleThreadExecutor()
    private val connections = Executors.newCachedThreadPool()
    private val gson = GsonBuilder().serializeNulls().create()
    private val pipeName = "${PIPE_PREFIX}_${ProcessHandle.current().pid()}"
    private val activeHandles = ConcurrentHashMap.newKeySet<WinNT.HANDLE>()

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
        log("Starting Rider named pipe server on \\\\.\\pipe\\$pipeName")
        acceptor.submit { serverLoop() }
    }

    fun stop() {
        if (!running.compareAndSet(true, false)) return
        closeActiveHandles()
        acceptor.shutdownNow()
        connections.shutdownNow()
        log("Named pipe server stopped.")
    }

    override fun dispose() {
        stop()
    }

    private fun serverLoop() {
        while (running.get()) {
            val security = createPipeSecurity()
            val handle = Kernel32.INSTANCE.CreateNamedPipe(
                "\\\\.\\pipe\\$pipeName",
                WinBase.PIPE_ACCESS_DUPLEX,
                WinBase.PIPE_TYPE_BYTE or WinBase.PIPE_READMODE_BYTE or WinBase.PIPE_WAIT,
                WinBase.PIPE_UNLIMITED_INSTANCES,
                4096,
                4096,
                PIPE_TIMEOUT_MS,
                security?.attributes
            )
            freePipeSecurity(security)

            if (handle == WinBase.INVALID_HANDLE_VALUE) {
                log("Failed to create named pipe. Error=${Kernel32.INSTANCE.GetLastError()}")
                return
            }

            activeHandles.add(handle)
            try {
                val connected = Kernel32.INSTANCE.ConnectNamedPipe(handle, null)
                val lastError = Kernel32.INSTANCE.GetLastError()
                if (!connected && lastError != WinError.ERROR_PIPE_CONNECTED) {
                    log("Failed to connect named pipe. Error=$lastError")
                    closeHandle(handle)
                    continue
                }

                connections.submit { handleConnection(handle) }
            } catch (ex: Exception) {
                log("Pipe error: ${ex.message}")
                closeHandle(handle)
            } finally {
            }
        }
    }

    private fun handleConnection(handle: WinNT.HANDLE) {
        try {
            val connection = PipeConnection(handle)
            while (running.get()) {
                val line = connection.readLine() ?: break
                if (line.isBlank()) continue

                val response = processRequest(line)
                connection.writeLine(response)
            }
        } catch (ex: Exception) {
            log("Pipe connection error: ${ex.message}")
        } finally {
            closeHandle(handle)
        }
    }

    private fun processRequest(requestJson: String): String {
        val request = try {
            gson.fromJson(requestJson, DebugRequest::class.java)
        } catch (ex: Exception) {
            return gson.toJson(DebugResponse.failure(null, "PARSE_ERROR", "Failed to parse request: ${ex.message}"))
        }

        if (request.method.isNullOrBlank()) {
            return gson.toJson(DebugResponse.failure(request.id, "INVALID_REQUEST", "Missing method"))
        }

        return try {
            val result = debuggerService.handleMethod(request)
            gson.toJson(DebugResponse.success(request.id, result))
        } catch (ex: Exception) {
            gson.toJson(DebugResponse.failure(request.id, "INTERNAL_ERROR", ex.message ?: "Unknown error"))
        }
    }

    private fun isWindows(): Boolean {
        return System.getProperty("os.name").lowercase().contains("win")
    }

    private fun isStrictPipeSecurityEnabled(): Boolean {
        val value = System.getenv("AIDB_PIPE_SECURITY")?.trim()?.lowercase() ?: return false
        return value == "strict" || value == "same_user" || value == "owner_only"
    }

    private data class PipeSecurity(val attributes: WinBase.SECURITY_ATTRIBUTES, val descriptor: Pointer)

    private interface Advapi32Ex : com.sun.jna.win32.StdCallLibrary {
        fun ConvertStringSecurityDescriptorToSecurityDescriptor(
            sddl: String,
            revision: Int,
            securityDescriptor: PointerByReference,
            size: IntByReference?
        ): Boolean
    }

    private val advapiEx: Advapi32Ex by lazy {
        Native.load("Advapi32", Advapi32Ex::class.java)
    }

    private fun createPipeSecurity(): PipeSecurity? {
        val strict = isStrictPipeSecurityEnabled()
        val sddlCandidates = if (strict) {
            listOf(
                // Owner-only access for stronger local isolation.
                "D:(A;;GA;;;OW)"
            )
        } else {
            listOf(
                "D:(A;;GA;;;WD)S:(ML;;NW;;;ME)",
                "D:(A;;GA;;;WD)S:(ML;;NW;;;LW)",
                "D:(A;;GA;;;WD)"
            )
        }

        var chosenSddl: String? = null
        var securityDescriptor: PointerByReference? = null
        for (sddl in sddlCandidates) {
            val descriptor = PointerByReference()
            val ok = advapiEx.ConvertStringSecurityDescriptorToSecurityDescriptor(
                sddl,
                SDDL_REVISION_1,
                descriptor,
                null
            )
            if (ok && descriptor.value != null) {
                chosenSddl = sddl
                securityDescriptor = descriptor
                break
            }
        }

        if (securityDescriptor == null || securityDescriptor.value == null) {
            log("Failed to create security descriptor. Error=${Kernel32.INSTANCE.GetLastError()}")
            return null
        }

        if (chosenSddl != sddlCandidates.first()) {
            log("Using fallback pipe security descriptor: $chosenSddl")
        }
        if (strict) {
            log("Named pipe security mode: strict (same-user/owner-only).")
        }

        val attributes = WinBase.SECURITY_ATTRIBUTES()
        attributes.dwLength = WinDef.DWORD(attributes.size().toLong())
        attributes.lpSecurityDescriptor = securityDescriptor.value
        attributes.bInheritHandle = false
        attributes.write()

        return PipeSecurity(attributes, securityDescriptor.value)
    }

    private fun freePipeSecurity(security: PipeSecurity?) {
        if (security == null) return
        Kernel32.INSTANCE.LocalFree(security.descriptor)
    }

    private fun closeActiveHandles() {
        val handles = activeHandles.toList()
        handles.forEach { handle ->
            closeHandle(handle)
        }
    }

    private fun closeHandle(handle: WinNT.HANDLE) {
        if (!activeHandles.remove(handle)) {
            return
        }
        tryCancelIo(handle)
        Kernel32.INSTANCE.DisconnectNamedPipe(handle)
        Kernel32.INSTANCE.CloseHandle(handle)
    }

    private fun tryCancelIo(handle: WinNT.HANDLE) {
        try {
            val method = Kernel32::class.java.methods.firstOrNull {
                it.name == "CancelIoEx" && it.parameterTypes.size == 2
            }
            method?.invoke(Kernel32.INSTANCE, handle, Pointer.NULL)
        } catch (_: Exception) {
            // Best-effort cancel to unblock reads during shutdown.
        }
    }

    private class PipeConnection(private val handle: WinNT.HANDLE) {
        private val pending = ByteArrayOutputStream()

        fun readLine(): String? {
            while (true) {
                val existing = pending.toByteArray()
                val newlineIndex = indexOf(existing, '\n'.code.toByte())
                if (newlineIndex >= 0) {
                    val lineBytes = existing.copyOfRange(0, newlineIndex)
                    val remaining = existing.copyOfRange(newlineIndex + 1, existing.size)
                    pending.reset()
                    pending.write(remaining)
                    return String(lineBytes, StandardCharsets.UTF_8)
                }

                val buffer = ByteArray(4096)
                val bytesRead = IntByReference()
                val success = Kernel32.INSTANCE.ReadFile(handle, buffer, buffer.size, bytesRead, null)
                if (!success || bytesRead.value <= 0) {
                    return null
                }
                pending.write(buffer, 0, bytesRead.value)

                if (pending.size() > MAX_BUFFER) {
                    return null
                }
            }
        }

        fun writeLine(text: String) {
            val bytes = (text + "\n").toByteArray(StandardCharsets.UTF_8)
            val bytesWritten = IntByReference()
            Kernel32.INSTANCE.WriteFile(handle, bytes, bytes.size, bytesWritten, null)
        }

        private fun indexOf(buffer: ByteArray, target: Byte): Int {
            for (i in buffer.indices) {
                if (buffer[i] == target) return i
            }
            return -1
        }
    }

    private object WinError {
        const val ERROR_PIPE_CONNECTED = 535
    }
}
