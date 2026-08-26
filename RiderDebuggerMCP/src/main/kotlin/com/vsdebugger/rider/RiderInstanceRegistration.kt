package com.vsdebugger.rider

import com.google.gson.GsonBuilder
import com.sun.jna.Native
import com.sun.jna.WString
import com.sun.jna.platform.win32.Advapi32
import com.sun.jna.platform.win32.Advapi32Util
import com.sun.jna.platform.win32.Kernel32
import com.sun.jna.platform.win32.WinBase
import com.sun.jna.platform.win32.WinNT
import com.sun.jna.ptr.IntByReference
import com.sun.jna.ptr.PointerByReference
import com.vsdebugger.rider.protocol.RiderMethodCatalog
import java.nio.charset.StandardCharsets
import java.nio.file.Files
import java.nio.file.LinkOption
import java.nio.file.Path
import java.nio.file.StandardCopyOption
import java.nio.file.attribute.BasicFileAttributes
import java.security.SecureRandom
import java.time.Instant
import java.util.Base64
import java.util.UUID

class RiderInstanceRegistration(
    val pipeName: String,
    private val log: (String) -> Unit
) : AutoCloseable {
    companion object {
        private const val SDDL_REVISION_1 = 1
        private const val PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
        private const val FILETIME_TO_DOTNET_TICKS = 504_911_232_000_000_000L

        fun instantToDotNetTicks(value: Instant): Long =
            Math.addExact(
                Math.multiplyExact(value.epochSecond, 10_000_000L),
                value.nano / 100L + 621_355_968_000_000_000L
            )

        fun processStartTimeUtcTicks(processId: Int): Long {
            require(processId > 0) { "A positive process id is required" }
            val handle = Kernel32.INSTANCE.OpenProcess(
                PROCESS_QUERY_LIMITED_INFORMATION,
                false,
                processId
            ) ?: throw IllegalStateException("Unable to open process $processId for identity validation")
            try {
                val creation = WinBase.FILETIME()
                val exit = WinBase.FILETIME()
                val kernel = WinBase.FILETIME()
                val user = WinBase.FILETIME()
                if (!Kernel32.INSTANCE.GetProcessTimes(handle, creation, exit, kernel, user)) {
                    throw IllegalStateException(
                        "Unable to read process $processId creation time. Error=${Kernel32.INSTANCE.GetLastError()}"
                    )
                }
                val fileTime =
                    ((creation.dwHighDateTime.toLong() and 0xffffffffL) shl 32) or
                        (creation.dwLowDateTime.toLong() and 0xffffffffL)
                return Math.addExact(fileTime, FILETIME_TO_DOTNET_TICKS)
            } finally {
                Kernel32.INSTANCE.CloseHandle(handle)
            }
        }
    }

    private interface Advapi32Ex : com.sun.jna.win32.StdCallLibrary {
        fun ConvertStringSecurityDescriptorToSecurityDescriptorW(
            sddl: WString,
            revision: Int,
            securityDescriptor: PointerByReference,
            size: IntByReference?
        ): Boolean
    }

    private val advapiEx: Advapi32Ex = Native.load("Advapi32", Advapi32Ex::class.java)
    private val processId = ProcessHandle.current().pid().toInt()
    val processStartTimeUtcTicks: Long = processStartTimeUtcTicks(processId)
    val serverInstanceId: String = UUID.randomUUID().toString().replace("-", "")
    val authToken: String = ByteArray(32).also { SecureRandom().nextBytes(it) }
        .let { Base64.getEncoder().encodeToString(it) }

    private val registrationDirectory: Path
    private val registrationPath: Path
    private var closed = false

    init {
        val localAppData = System.getenv("LOCALAPPDATA")?.takeIf { it.isNotBlank() }
            ?: throw IllegalStateException("LOCALAPPDATA is unavailable")
        val localRoot = Path.of(localAppData).toAbsolutePath().normalize()
        requirePlainDirectory(localRoot, "LOCALAPPDATA")
        val aidbRoot = ensureOwnerOnlyChildDirectory(localRoot, "AIDebugger")
        registrationDirectory = ensureOwnerOnlyChildDirectory(aidbRoot, "instances")
        registrationPath = registrationDirectory.resolve("$pipeName.json").toAbsolutePath().normalize()
        require(registrationPath.parent == registrationDirectory) {
            "Instance registration path escaped its trusted directory"
        }

        val payload = linkedMapOf<String, Any>(
            "protocol" to RiderMethodCatalog.protocol,
            "pipeName" to pipeName,
            "processId" to processId,
            "processStartTimeUtcTicks" to processStartTimeUtcTicks,
            "serverInstanceId" to serverInstanceId,
            "protocolCatalogHash" to RiderMethodCatalog.catalogHash,
            "authToken" to authToken,
            "capabilities" to RiderMethodCatalog.allCapabilities
        )
        writeAtomicallyOwnerOnly(
            registrationPath,
            GsonBuilder().disableHtmlEscaping().create().toJson(payload)
        )
    }

    override fun close() {
        if (closed) return
        closed = true
        try {
            if (!isPlainFile(registrationPath)) return
            val payload = GsonBuilder().create().fromJson(
                Files.readString(registrationPath, StandardCharsets.UTF_8),
                Map::class.java
            )
            if (payload["serverInstanceId"] == serverInstanceId && isPlainFile(registrationPath)) {
                Files.deleteIfExists(registrationPath)
            }
        } catch (ex: Exception) {
            log("Failed to remove Rider instance registration: ${ex.javaClass.simpleName}")
        }
    }

    private fun ensureOwnerOnlyChildDirectory(parent: Path, childName: String): Path {
        require(childName.isNotBlank() && childName != "." && childName != ".." &&
            !childName.contains('/') && !childName.contains('\\')) {
            "A direct child directory name is required"
        }
        requirePlainDirectory(parent, "instance registration parent")
        val path = parent.resolve(childName).toAbsolutePath().normalize()
        require(path.parent == parent) { "Instance registration directory is not a direct child" }
        if (!Files.exists(path, LinkOption.NOFOLLOW_LINKS)) {
            Files.createDirectory(path)
        }
        requirePlainDirectory(path, "instance registration directory")
        applyOwnerOnlyAcl(path, directory = true)
        requirePlainDirectory(path, "instance registration directory after ACL setup")
        return path
    }

    private fun writeAtomicallyOwnerOnly(path: Path, content: String) {
        require(path.parent == registrationDirectory && isPlainDirectory(registrationDirectory)) {
            "Instance registration path is outside its trusted directory"
        }
        if (Files.exists(path, LinkOption.NOFOLLOW_LINKS) && !isPlainFile(path)) {
            throw IllegalStateException("Existing Rider registration is not a plain file")
        }
        val temporary = registrationDirectory.resolve("${path.fileName}.${UUID.randomUUID().toString().replace("-", "")}.tmp")
        try {
            Files.newOutputStream(temporary).use { stream ->
                stream.write(content.toByteArray(StandardCharsets.UTF_8))
                stream.flush()
            }
            require(isPlainFile(temporary)) { "Rider registration staging path is unsafe" }
            applyOwnerOnlyAcl(temporary, directory = false)
            require(isPlainDirectory(registrationDirectory) && isPlainFile(temporary)) {
                "Rider registration path changed during ACL setup"
            }
            try {
                Files.move(
                    temporary,
                    path,
                    StandardCopyOption.ATOMIC_MOVE,
                    StandardCopyOption.REPLACE_EXISTING
                )
            } catch (_: java.nio.file.AtomicMoveNotSupportedException) {
                Files.move(temporary, path, StandardCopyOption.REPLACE_EXISTING)
            }
            require(isPlainFile(path)) { "Rider registration write did not produce a plain file" }
        } finally {
            Files.deleteIfExists(temporary)
        }
    }

    private fun applyOwnerOnlyAcl(path: Path, directory: Boolean) {
        val token = WinNT.HANDLEByReference()
        if (!Advapi32.INSTANCE.OpenProcessToken(
                Kernel32.INSTANCE.GetCurrentProcess(),
                WinNT.TOKEN_QUERY,
                token
            )) {
            throw IllegalStateException("Unable to determine current Windows user SID")
        }
        val sid = try {
            Advapi32Util.getTokenAccount(token.value).sidString
        } finally {
            Kernel32.INSTANCE.CloseHandle(token.value)
        }
        val flags = if (directory) "OICI" else ""
        val descriptorRef = PointerByReference()
        val sddl = "D:P(A;$flags;FA;;;$sid)"
        if (!advapiEx.ConvertStringSecurityDescriptorToSecurityDescriptorW(
                WString(sddl),
                SDDL_REVISION_1,
                descriptorRef,
                null
            ) || descriptorRef.value == null) {
            throw IllegalStateException("Unable to create owner-only ACL. Error=${Kernel32.INSTANCE.GetLastError()}")
        }
        try {
            val securityInformation = WinNT.DACL_SECURITY_INFORMATION or
                WinNT.PROTECTED_DACL_SECURITY_INFORMATION
            if (!Advapi32.INSTANCE.SetFileSecurity(path.toString(), securityInformation, descriptorRef.value)) {
                throw IllegalStateException("Unable to apply owner-only ACL. Error=${Kernel32.INSTANCE.GetLastError()}")
            }
        } finally {
            Kernel32.INSTANCE.LocalFree(descriptorRef.value)
        }
    }

    private fun requirePlainDirectory(path: Path, description: String) {
        if (!isPlainDirectory(path)) {
            throw IllegalStateException("$description is missing or is a reparse point")
        }
    }

    private fun isPlainDirectory(path: Path): Boolean = try {
        val attributes = Files.readAttributes(path, BasicFileAttributes::class.java, LinkOption.NOFOLLOW_LINKS)
        attributes.isDirectory && !attributes.isOther && !Files.isSymbolicLink(path)
    } catch (_: Exception) {
        false
    }

    private fun isPlainFile(path: Path): Boolean = try {
        val attributes = Files.readAttributes(path, BasicFileAttributes::class.java, LinkOption.NOFOLLOW_LINKS)
        attributes.isRegularFile && !attributes.isOther && !Files.isSymbolicLink(path)
    } catch (_: Exception) {
        false
    }
}
