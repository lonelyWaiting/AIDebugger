package com.vsdebugger.rider

import com.google.gson.GsonBuilder
import com.google.gson.reflect.TypeToken
import com.intellij.openapi.diagnostic.Logger
import java.io.File
import java.io.InputStream
import java.nio.charset.StandardCharsets

object CursorMcpRegistration {
    private const val McpServerName = "AIDebuggerRider"
    private const val PreferredIde = "rider"
    private val log = Logger.getInstance(CursorMcpRegistration::class.java)
    private val gson = GsonBuilder().setPrettyPrinting().create()

    private val baseFiles = listOf(
        "server.py",
        "ide_bridge.py",
        "vs_bridge.py",
        "rider_bridge.py",
        "bootstrap.py",
        "requirements.txt"
    )

    private const val ServerUseInstructions =
        "AIDebuggerRider - Rider debugging MCP\n\n" +
            "Before using any debugging tools, call detect_debug_sessions(prefer_ide=\"rider\") to locate a live session.\n" +
            "If auto-selection fails, use list_ide_instances + select_ide_instance.\n"

    fun registerWithCursor() {
        try {
            val serverPath = ensureServerFiles() ?: return
            val configPath = getCursorMcpConfigPath()
            val config = readConfig(configPath)

            val mcpServers = getOrCreateMap(config, "mcpServers")
            val normalizedPath = serverPath.replace("\\", "/")

            val serverConfig = linkedMapOf<String, Any>(
                "command" to "python",
                "args" to listOf(normalizedPath),
                "env" to mapOf("AIDB_PREFERRED_IDE" to PreferredIde),
                "serverUseInstructions" to ServerUseInstructions
            )

            val existing = mcpServers[McpServerName] as? Map<*, *>
            if (isCorrectConfig(existing, normalizedPath)) {
                log.debug("MCP server '$McpServerName' already registered.")
                return
            }

            mcpServers[McpServerName] = serverConfig
            writeConfig(configPath, config)
            log.info("Registered MCP server '$McpServerName' in Cursor config.")
        } catch (ex: Exception) {
            log.warn("Failed to register MCP server with Cursor: ${ex.message}", ex)
        }
    }

    private fun ensureServerFiles(): String? {
        val targetDir = File(System.getProperty("user.home"), ".ai-debugger/mcp_server")
        if (!targetDir.exists() && !targetDir.mkdirs()) {
            log.warn("Failed to create MCP server directory: ${targetDir.absolutePath}")
            return null
        }

        val manifestFiles = loadManifestFiles()
        val filesToCopy = (baseFiles + "vendor/wheels/manifest.json" + manifestFiles).distinct()

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

        val serverFile = File(targetDir, "server.py")
        return if (serverFile.exists()) serverFile.absolutePath else null
    }

    private fun getResourceStream(name: String): InputStream? {
        val normalized = name.replace("\\", "/")
        return CursorMcpRegistration::class.java.classLoader.getResourceAsStream(normalized)
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
}
