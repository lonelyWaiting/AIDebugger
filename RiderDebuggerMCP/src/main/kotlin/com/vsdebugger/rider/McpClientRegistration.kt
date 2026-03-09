package com.vsdebugger.rider

import com.google.gson.GsonBuilder
import com.google.gson.reflect.TypeToken
import com.intellij.openapi.diagnostic.Logger
import java.io.File
import java.io.InputStream
import java.nio.charset.StandardCharsets
import java.security.MessageDigest
import java.util.concurrent.TimeUnit

object McpClientRegistration {
    private const val McpServerName = "AIDebuggerRider"
    private const val PreferredIde = "rider"
    private val log = Logger.getInstance(McpClientRegistration::class.java)
    private val gson = GsonBuilder().setPrettyPrinting().create()
    
    // Client display names for logging
    private const val ClientNameCursor = "Cursor"
    private const val ClientNameClaudeDesktop = "Claude Desktop"
    private const val ClientNameOpenCode = "OpenCode"
    private const val CursorAllowListSyncScript = "cursor_allowlist_sync.py"

    private val baseFiles = listOf(
        "server.py",
        "ide_bridge.py",
        "vs_bridge.py",
        "rider_bridge.py",
        "bootstrap.py",
        "cursor_allowlist_sync.py",
        "requirements.txt"
    )

    private const val ServerUseInstructions =
        "AIDebuggerRider - Rider debugging MCP\n\n" +
            "Before using any debugging tools, call detect_debug_sessions(prefer_ide=\"rider\") to locate a live session.\n" +
            "If auto-selection fails, use list_ide_instances + select_ide_instance.\n" +
            "For crash/bug diagnosis, prefer capture_debug_bundle first (optionally with expressions).\n" +
            "For repro workflows, use continue_and_wait_for_break to resume and auto-capture the next stop.\n" +
            "Use get_debug_status.stopReason to branch analysis flow (breakpoint/exception/step/user_break).\n" +
            "When checking multiple values, prefer evaluate_expressions_batch to avoid drift.\n"

    fun registerWithCursor() {
        try {
            val pythonCommand = resolvePythonCommand()
            if (pythonCommand == null) {
                log.warn("Python 3.10+ is required for MCP server registration. Skipping Cursor registration.")
                return
            }

            val serverPath = ensureServerFiles() ?: return
            val configPath = getCursorMcpConfigPath()
            registerToConfig(configPath, serverPath, ClientNameCursor, createDirIfMissing = true)
            syncCursorAllowList(serverPath, pythonCommand)
        } catch (ex: Exception) {
            log.warn("Failed to register MCP server with Cursor: ${ex.message}", ex)
        }
    }

    fun registerWithClaudeDesktop() {
        try {
            if (!isPythonAvailable()) {
                log.warn("Python 3.10+ is required for MCP server registration. Skipping Claude Desktop registration.")
                return
            }

            val serverPath = ensureServerFiles() ?: return
            val configPath = getClaudeDesktopConfigPath()
            registerToConfig(configPath, serverPath, ClientNameClaudeDesktop, createDirIfMissing = true)
        } catch (ex: Exception) {
            log.warn("Failed to register MCP server with Claude Desktop: ${ex.message}", ex)
        }
    }

    fun registerWithOpenCode() {
        try {
            if (!isPythonAvailable()) {
                log.warn("Python 3.10+ is required for MCP server registration. Skipping OpenCode registration.")
                return
            }

            val serverPath = ensureServerFiles() ?: return
            val configPath = getOpenCodeConfigPath()
            val configFile = File(configPath)

            // Ensure the config directory exists, create if missing
            val configDir = configFile.parentFile
            if (configDir != null && !configDir.exists()) {
                configDir.mkdirs()
                log.info("[$ClientNameOpenCode] Created directory: ${configDir.absolutePath}")
            }

            registerToOpenCodeConfig(configPath, serverPath)
        } catch (ex: Exception) {
            log.warn("Failed to register MCP server with OpenCode: ${ex.message}", ex)
        }
    }

    private fun registerToOpenCodeConfig(configPath: String, serverPath: String) {
        val configFile = File(configPath)
        val normalizedPath = serverPath.replace("\\", "/")

        // Read existing config or create new one
        val config = if (configFile.exists()) {
            readConfig(configPath)
        } else {
            mutableMapOf()
        }

        // OpenCode uses "mcp" key instead of "mcpServers"
        val mcpServers = getOrCreateMap(config, "mcp")

        // Build OpenCode-specific server config
        // OpenCode uses "command" as array: ["python", "path/to/server.py"]
        val serverConfig = linkedMapOf<String, Any>(
            "type" to "local",
            "command" to listOf("python", normalizedPath),
            "enabled" to true,
            "environment" to mapOf("AIDB_PREFERRED_IDE" to PreferredIde, "PYTHONIOENCODING" to "utf-8")
        )

        val existing = mcpServers[McpServerName] as? Map<*, *>
        if (isCorrectOpenCodeConfig(existing, normalizedPath)) {
            log.debug("[$ClientNameOpenCode] MCP server '$McpServerName' already registered.")
            return
        }

        mcpServers[McpServerName] = serverConfig
        writeConfig(configPath, config)
        log.info("[$ClientNameOpenCode] Registered MCP server '$McpServerName'.")
    }

    private fun isCorrectOpenCodeConfig(existing: Map<*, *>?, normalizedPath: String): Boolean {
        if (existing == null) return false
        
        // Check type
        val type = existing["type"]?.toString() ?: return false
        if (type != "local") return false

        // Check enabled
        val enabled = existing["enabled"]
        if (enabled != true) return false

        // Check command array
        val command = existing["command"]
        val commandList = when (command) {
            is List<*> -> command
            is Array<*> -> command.toList()
            else -> return false
        }
        if (commandList.size < 2) return false
        if (commandList[0]?.toString() != "python") return false
        if (!normalizedPath.equals(commandList.last()?.toString(), ignoreCase = true)) return false

        // Check environment
        val env = existing["environment"] as? Map<*, *> ?: return false
        val preferred = env["AIDB_PREFERRED_IDE"]?.toString() ?: return false
        return preferred.equals(PreferredIde, ignoreCase = true)
    }

    private fun registerToConfig(configPath: String, serverPath: String, clientName: String, createDirIfMissing: Boolean) {
        val configFile = File(configPath)
        val configDir = configFile.parentFile
        
        if (configDir != null && !configDir.exists()) {
            if (createDirIfMissing) {
                configDir.mkdirs()
                log.info("[$clientName] Created directory: ${configDir.absolutePath}")
            } else {
                log.debug("[$clientName] Directory does not exist, skipping.")
                return
            }
        }

        val config = readConfig(configPath)
        val mcpServers = getOrCreateMap(config, "mcpServers")
        val normalizedPath = serverPath.replace("\\", "/")

        val serverConfig = linkedMapOf<String, Any>(
            "command" to "python",
            "args" to listOf(normalizedPath),
            "env" to mapOf("AIDB_PREFERRED_IDE" to PreferredIde, "PYTHONIOENCODING" to "utf-8"),
            "serverUseInstructions" to ServerUseInstructions
        )

        val existing = mcpServers[McpServerName] as? Map<*, *>
        if (isCorrectConfig(existing, normalizedPath)) {
            log.debug("[$clientName] MCP server '$McpServerName' already registered.")
            return
        }

        mcpServers[McpServerName] = serverConfig
        writeConfig(configPath, config)
        log.info("[$clientName] Registered MCP server '$McpServerName'.")
    }

    private fun ensureServerFiles(): String? {
        val targetDir = File(System.getProperty("user.home"), ".ai-debugger/mcp_server")
        if (!targetDir.exists() && !targetDir.mkdirs()) {
            log.warn("Failed to create MCP server directory: ${targetDir.absolutePath}")
            return null
        }

        val manifestFiles = loadManifestFiles()
        val filesToCopy = (baseFiles + "vendor/wheels/manifest.json" + manifestFiles).distinct()
        val signatureFile = File(targetDir, ".bundle-signature")
        val bundleSignature = computeBundleSignature(manifestFiles)
        if (!bundleSignature.isNullOrBlank() &&
            signatureFile.exists() &&
            signatureFile.readText(StandardCharsets.UTF_8).trim() == bundleSignature &&
            allServerFilesExist(targetDir, filesToCopy)
        ) {
            val existingServer = File(targetDir, "server.py")
            if (existingServer.exists()) {
                return existingServer.absolutePath
            }
        }

        for (fileName in filesToCopy) {
            val target = File(targetDir, fileName)
            val parent = target.parentFile
            if (parent != null && !parent.exists()) {
                parent.mkdirs()
            }
            val resource = getResourceStream(fileName)
            if (resource == null) {
                log.warn("Missing MCP resource: $fileName")
                return null
            }
            resource.use { input ->
                target.outputStream().use { output ->
                    input.copyTo(output)
                }
            }
        }

        if (!bundleSignature.isNullOrBlank()) {
            signatureFile.writeText(bundleSignature, StandardCharsets.UTF_8)
        }

        val serverFile = File(targetDir, "server.py")
        return if (serverFile.exists()) serverFile.absolutePath else null
    }

    private fun allServerFilesExist(targetDir: File, files: List<String>): Boolean {
        return files.all { relativePath -> File(targetDir, relativePath).exists() }
    }

    private fun computeBundleSignature(manifestFiles: List<String>): String? {
        return try {
            val digest = MessageDigest.getInstance("SHA-256")
            val seedFiles = (baseFiles + "vendor/wheels/manifest.json").distinct()
            for (fileName in seedFiles) {
                val bytes = readResourceBytes(fileName) ?: return null
                digest.update(bytes)
            }
            // Wheel list changes when bundled wheel set changes.
            manifestFiles.sorted().forEach { file ->
                digest.update(file.toByteArray(StandardCharsets.UTF_8))
                digest.update('\n'.code.toByte())
            }
            digest.digest().joinToString("") { "%02x".format(it) }
        } catch (ex: Exception) {
            log.warn("Failed to compute MCP bundle signature: ${ex.message}", ex)
            null
        }
    }

    private fun readResourceBytes(name: String): ByteArray? {
        val resource = getResourceStream(name) ?: return null
        return resource.use { it.readBytes() }
    }

    private fun getResourceStream(name: String): InputStream? {
        val normalized = name.replace("\\", "/")
        return McpClientRegistration::class.java.classLoader.getResourceAsStream(normalized)
    }

    private data class WheelsManifest(val wheels: List<WheelEntry> = emptyList())
    private data class WheelEntry(val file: String = "")

    private fun loadManifestFiles(): List<String> {
        val resource = getResourceStream("vendor/wheels/manifest.json") ?: run {
            log.warn("Missing wheels manifest; bundled dependencies may be incomplete.")
            return emptyList()
        }
        return try {
            val text = resource.bufferedReader(StandardCharsets.UTF_8).use { it.readText() }
            val manifest = gson.fromJson(text, WheelsManifest::class.java) ?: return emptyList()
            manifest.wheels.mapNotNull { entry ->
                val name = entry.file.trim()
                if (name.isEmpty()) null else name
            }
        } catch (ex: Exception) {
            log.warn("Failed to parse wheels manifest: ${ex.message}", ex)
            emptyList()
        }
    }

    private fun getCursorMcpConfigPath(): String {
        val userHome = System.getProperty("user.home")
        return File(File(userHome, ".cursor"), "mcp.json").absolutePath
    }

    private fun getClaudeDesktopConfigPath(): String {
        val appData = System.getenv("APPDATA")
            ?: File(System.getProperty("user.home"), "AppData/Roaming").absolutePath
        return File(File(appData, "Claude"), "claude_desktop_config.json").absolutePath
    }

    private fun getOpenCodeConfigPath(): String {
        val userHome = System.getProperty("user.home")
        return File(File(File(userHome, ".config"), "opencode"), "opencode.json").absolutePath
    }

    private fun readConfig(path: String): MutableMap<String, Any> {
        val file = File(path)
        if (!file.exists()) {
            return mutableMapOf()
        }
        val text = file.readText(StandardCharsets.UTF_8)
        val type = object : TypeToken<MutableMap<String, Any>>() {}.type
        val parsed = gson.fromJson<MutableMap<String, Any>>(text, type)
        return parsed ?: mutableMapOf()
    }

    private fun writeConfig(path: String, config: MutableMap<String, Any>) {
        val file = File(path)
        val dir = file.parentFile
        if (dir != null && !dir.exists()) {
            dir.mkdirs()
        }
        file.writeText(gson.toJson(config), StandardCharsets.UTF_8)
    }

    private fun getOrCreateMap(config: MutableMap<String, Any>, key: String): MutableMap<String, Any> {
        val existing = config[key]
        if (existing is MutableMap<*, *>) {
            @Suppress("UNCHECKED_CAST")
            return existing as MutableMap<String, Any>
        }
        val created = mutableMapOf<String, Any>()
        config[key] = created
        return created
    }

    private fun isCorrectConfig(existing: Map<*, *>?, normalizedPath: String): Boolean {
        if (existing == null) return false
        val command = existing["command"]?.toString() ?: return false
        if (command != "python") return false
        val args = existing["args"]
        val firstArg = when (args) {
            is List<*> -> args.firstOrNull()?.toString()
            is Array<*> -> args.firstOrNull()?.toString()
            else -> null
        }
        if (!normalizedPath.equals(firstArg, ignoreCase = true)) return false
        val env = existing["env"] as? Map<*, *> ?: return false
        val preferred = env["AIDB_PREFERRED_IDE"]?.toString() ?: return false
        return preferred.equals(PreferredIde, ignoreCase = true)
    }

    private fun syncCursorAllowList(serverPath: String, pythonCommand: String) {
        try {
            val scriptPath = File(File(serverPath).parentFile, CursorAllowListSyncScript)
            if (!scriptPath.exists()) {
                log.warn("[$ClientNameCursor] Skipping MCP allow list sync because ${scriptPath.absolutePath} does not exist.")
                return
            }

            val process = ProcessBuilder(
                pythonCommand,
                scriptPath.absolutePath,
                "--server",
                McpServerName,
            )
                .redirectErrorStream(true)
                .start()

            if (!process.waitFor(15, TimeUnit.SECONDS)) {
                process.destroyForcibly()
                log.warn("[$ClientNameCursor] MCP allow list sync timed out.")
                return
            }

            val output = process.inputStream.bufferedReader(StandardCharsets.UTF_8).use { it.readText() }.trim()
            if (process.exitValue() == 0) {
                if (output.isBlank()) {
                    log.info("[$ClientNameCursor] MCP allow list sync completed.")
                } else {
                    log.info("[$ClientNameCursor] MCP allow list sync completed: $output")
                }
            } else {
                if (output.isBlank()) {
                    log.warn("[$ClientNameCursor] MCP allow list sync failed with exit code ${process.exitValue()}.")
                } else {
                    log.warn("[$ClientNameCursor] MCP allow list sync failed: $output")
                }
            }
        } catch (ex: Exception) {
            log.warn("[$ClientNameCursor] MCP allow list sync failed: ${ex.message}", ex)
        }
    }

    private fun isPythonAvailable(): Boolean {
        return resolvePythonCommand() != null
    }

    private fun resolvePythonCommand(): String? {
        return when {
            checkPythonCommand("python") -> "python"
            checkPythonCommand("python3") -> "python3"
            else -> null
        }
    }

    private fun checkPythonCommand(command: String): Boolean {
        return try {
            val process = ProcessBuilder(command, "--version")
                .redirectErrorStream(true)
                .start()
            if (!process.waitFor(5, TimeUnit.SECONDS)) {
                process.destroyForcibly()
                return false
            }
            if (process.exitValue() != 0) {
                return false
            }
            val output = process.inputStream.bufferedReader(StandardCharsets.UTF_8).use { it.readText() }.trim()
            val match = Regex("Python\\s+(\\d+)\\.(\\d+)\\.(\\d+)", RegexOption.IGNORE_CASE).find(output)
            if (match != null) {
                val major = match.groupValues[1].toIntOrNull() ?: return false
                val minor = match.groupValues[2].toIntOrNull() ?: return false
                return major == 3 && minor >= 10
            }
            false
        } catch (_: Exception) {
            false
        }
    }
}
