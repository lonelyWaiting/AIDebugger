using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Collections.Generic;

namespace VSDebuggerMCP
{
    public static partial class McpClientRegistration
    {
        /// <summary>
        /// Gets the path to Cursor's mcp.json configuration file.
        /// </summary>
        public static string GetCursorMcpConfigPath()
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(userProfile, ".cursor", "mcp.json");
        }

        /// <summary>
        /// Gets the path to Codex's configuration file.
        /// Windows default: %USERPROFILE%\.codex\config.toml
        /// Respects CODEX_HOME when defined.
        /// </summary>
        public static string GetCodexConfigPath()
        {
            var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(codexHome))
            {
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                codexHome = Path.Combine(userProfile, ".codex");
            }

            return Path.Combine(codexHome, "config.toml");
        }

        /// <summary>
        /// Gets the path to Claude Desktop's configuration file (Windows only).
        /// </summary>
        public static string GetClaudeDesktopConfigPath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "Claude", "claude_desktop_config.json");
        }

        /// <summary>
        /// Gets the path to Claude Code's user-scoped configuration file.
        /// Anthropic stores user-scoped Claude Code MCP servers in ~/.claude.json.
        /// </summary>
        public static string GetClaudeCodeUserConfigPath()
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(userProfile, ".claude.json");
        }

        /// <summary>
        /// Gets the path to OpenCode's configuration file.
        /// Windows: %USERPROFILE%\.config\opencode\opencode.json
        /// </summary>
        public static string GetOpenCodeConfigPath()
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(userProfile, ".config", "opencode", "opencode.json");
        }

        /// <summary>
        /// Registers the AIDebugger server in Cursor's configuration.
        /// </summary>
        /// <returns>True if registration was successful or already registered, false on error.</returns>
        public static RegistrationResult RegisterWithCursor(Action<string> log = null)
        {
            log = log ?? ((s) => { });
            var configPath = GetCursorMcpConfigPath();
            return RegisterToConfig(configPath, ClientNameCursor, log, createDirIfMissing: true);
        }

        /// <summary>
        /// Registers the AIDebugger server in Codex's TOML configuration.
        /// Creates the config directory and file if they don't exist.
        /// </summary>
        public static RegistrationResult RegisterWithCodex(Action<string> log = null)
        {
            var configPath = GetCodexConfigPath();
            return RegisterToCodexConfig(configPath, ClientNameCodex, log, createDirIfMissing: true);
        }

        /// <summary>
        /// Registers the AIDebugger server in Claude Desktop's configuration.
        /// Creates the config directory and file if they don't exist.
        /// </summary>
        /// <returns>Registration result.</returns>
        public static RegistrationResult RegisterWithClaudeDesktop(Action<string> log = null)
        {
            var configPath = GetClaudeDesktopConfigPath();
            return RegisterToConfig(configPath, ClientNameClaudeDesktop, log, createDirIfMissing: true);
        }

        /// <summary>
        /// Registers the AIDebugger server in Claude Code's user-scoped configuration.
        /// Claude Code uses ~/.claude.json for user-scoped MCP servers on all projects.
        /// </summary>
        public static RegistrationResult RegisterWithClaudeCode(Action<string> log = null)
        {
            var configPath = GetClaudeCodeUserConfigPath();
            return RegisterToClaudeCodeConfig(configPath, ClientNameClaudeCode, log, createDirIfMissing: true);
        }

        /// <summary>
        /// Registers the AIDebugger server in OpenCode's configuration.
        /// OpenCode uses a different JSON format: "mcp" key instead of "mcpServers",
        /// "command" as array, "environment" instead of "env", and requires "type" and "enabled" fields.
        /// Creates the config directory and file if they don't exist.
        /// </summary>
        /// <returns>Registration result.</returns>
        public static RegistrationResult RegisterWithOpenCode(Action<string> log = null)
        {
            log = log ?? ((s) => { });
            var configPath = GetOpenCodeConfigPath();
            var result = new RegistrationResult();

            try
            {
                // Ensure the config directory exists, create if missing
                var openCodeDir = Path.GetDirectoryName(configPath);
                if (!Directory.Exists(openCodeDir))
                {
                    ConfigPathPolicy.EnsurePlainDirectoryTree(openCodeDir);
                    log($"[{ClientNameOpenCode}] Created directory: {openCodeDir}");
                }

                var serverPath = GetMcpServerPath();

                if (!TryResolvePythonRuntime(log, out var pythonRuntime))
                {
                    result.Success = false;
                    result.Message = "No compatible Python runtime found. Please install " + GetSupportedPythonVersionRequirement(log) + ".";
                    return result;
                }

                log($"[{ClientNameOpenCode}] Config path: {configPath}");
                log($"[{ClientNameOpenCode}] MCP server path: {serverPath}");
                log($"[{ClientNameOpenCode}] Python runtime: {pythonRuntime.VersionText} via '{pythonRuntime.Command}'");

                // Use a named mutex to prevent concurrent read-modify-write from multiple IDE instances
                WithConfigFileLock(configPath, log, () =>
                {
                // Read existing config or create new one
                string existingJson;
                bool configExisted = File.Exists(configPath);
                if (configExisted)
                {
                    existingJson = ReadTextFromPlainFile(configPath, Encoding.UTF8);
                    log($"[{ClientNameOpenCode}] Read existing config");
                }
                else
                {
                    existingJson = "{}";
                    log($"[{ClientNameOpenCode}] Creating new config");
                }

                // Use forward slashes for cross-platform compatibility in JSON
                var normalizedPath = serverPath.Replace("\\", "/");

                var entryName = McpServerNameVs;
                var preferredIde = "vs";

                // Build OpenCode-specific server config
                // OpenCode uses "command" as array: ["python", "path/to/server.py"] or ["py", "-3.12", "path/to/server.py"]
                var commandArray = new List<object>();
                commandArray.Add(pythonRuntime.Command);
                foreach (var prefixArg in pythonRuntime.PrefixArgs)
                {
                    commandArray.Add(prefixArg);
                }
                commandArray.Add(normalizedPath);

                var serverConfig = new Dictionary<string, object>
                {
                    { "type", "local" },
                    { "command", commandArray.ToArray() },
                    { "enabled", true },
                    { "environment", new Dictionary<string, object> { { "AIDB_PREFERRED_IDE", preferredIde }, { "PYTHONIOENCODING", "utf-8" } } }
                };
                // Write updated config
                var json = JsonConfigEditor.UpsertOwnedEntry(
                    existingJson,
                    "mcp",
                    entryName,
                    JsonConfigEditor.SerializeUtf8Json(serverConfig));
                json = JsonConfigEditor.RemoveOwnedEntry(json, "mcp", LegacyMcpServerName, out _);
                if (string.Equals(json, existingJson, StringComparison.Ordinal))
                {
                    result.Success = true;
                    result.AlreadyRegistered = true;
                    result.Message = "Already registered";
                    return;
                }
                WriteTextAtomically(configPath, json, Encoding.UTF8, existingJson, configExisted);

                log($"[{ClientNameOpenCode}] Successfully registered '{McpServerNameVs}'");

                result.Success = true;
                result.AlreadyRegistered = false;
                result.Message = $"Successfully registered. Please restart {ClientNameOpenCode} to use the AIDebugger tools.";
                result.ConfigPath = configPath;
                result.ServerPath = serverPath;
                }); // end WithConfigFileLock

                return result;
            }
            catch (Exception ex)
            {
                log($"[{ClientNameOpenCode}] Error registering: {ex.Message}");
                result.Success = false;
                result.Message = $"Failed to register: {ex.Message}";
                return result;
            }
        }

        /// <summary>
        /// Registers the AIDebugger server in Claude Code's user-scoped configuration file.
        /// Claude Code uses the same top-level "mcpServers" object, but stdio entries should
        /// explicitly set type="stdio" and do not use Cursor-specific server instructions.
        /// </summary>
        private static RegistrationResult RegisterToClaudeCodeConfig(string configPath, string clientName, Action<string> log, bool createDirIfMissing)
        {
            log = log ?? ((s) => { });
            var result = new RegistrationResult();

            try
            {
                var serverPath = GetMcpServerPath();

                if (!TryResolvePythonRuntime(log, out var pythonRuntime))
                {
                    result.Success = false;
                    result.Message = "No compatible Python runtime found. Please install " + GetSupportedPythonVersionRequirement(log) + ".";
                    return result;
                }

                log($"Config path: {configPath}");
                log($"MCP server path: {serverPath}");
                log($"Python runtime: {pythonRuntime.VersionText} via '{pythonRuntime.Command}'");

                var configDir = Path.GetDirectoryName(configPath);
                if (!string.IsNullOrEmpty(configDir) && !Directory.Exists(configDir))
                {
                    if (createDirIfMissing)
                    {
                        ConfigPathPolicy.EnsurePlainDirectoryTree(configDir);
                        log($"Created directory: {configDir}");
                    }
                    else
                    {
                        result.Success = true;
                        result.Skipped = true;
                        result.Message = $"{clientName} directory does not exist";
                        return result;
                    }
                }

                WithConfigFileLock(configPath, log, () =>
                {
                    string existingJson;
                    bool configExisted = File.Exists(configPath);
                    if (configExisted)
                    {
                        existingJson = ReadTextFromPlainFile(configPath, Encoding.UTF8);
                        log("Read existing config");
                    }
                    else
                    {
                        existingJson = "{}";
                        log("Creating new config");
                    }

                    var normalizedPath = serverPath.Replace("\\", "/");
                    var entryName = McpServerNameVs;
                    var preferredIde = "vs";

                    var commandArgs = new List<object>();
                    foreach (var prefixArg in pythonRuntime.PrefixArgs)
                    {
                        commandArgs.Add(prefixArg);
                    }
                    commandArgs.Add(normalizedPath);
                    var serverConfig = new Dictionary<string, object>
                    {
                        { "type", "stdio" },
                        { "command", pythonRuntime.Command },
                        { "args", commandArgs.ToArray() },
                        { "env", new Dictionary<string, object> { { "AIDB_PREFERRED_IDE", preferredIde }, { "PYTHONIOENCODING", "utf-8" } } }
                    };

                    var json = JsonConfigEditor.UpsertOwnedEntry(
                        existingJson,
                        "mcpServers",
                        entryName,
                        JsonConfigEditor.SerializeUtf8Json(serverConfig));
                    json = JsonConfigEditor.RemoveOwnedEntry(json, "mcpServers", LegacyMcpServerName, out _);
                    if (string.Equals(json, existingJson, StringComparison.Ordinal))
                    {
                        result.Success = true;
                        result.AlreadyRegistered = true;
                        result.Message = "Already registered";
                        return;
                    }
                    WriteTextAtomically(configPath, json, Encoding.UTF8, existingJson, configExisted);

                    log($"Successfully registered '{McpServerNameVs}'");

                    result.Success = true;
                    result.AlreadyRegistered = false;
                    result.Message = $"Successfully registered. Please restart {clientName} to use the AIDebugger tools.";
                    result.ConfigPath = configPath;
                    result.ServerPath = serverPath;
                });

                return result;
            }
            catch (Exception ex)
            {
                log($"Error registering with {clientName}: {ex.Message}");
                result.Success = false;
                result.Message = $"Failed to register: {ex.Message}";
                return result;
            }
        }

        /// <summary>
        /// Registers the AIDebugger server in Codex's TOML configuration file.
        /// Codex uses nested TOML tables under [mcp_servers.&lt;name&gt;].
        /// </summary>
        private static RegistrationResult RegisterToCodexConfig(string configPath, string clientName, Action<string> log, bool createDirIfMissing)
        {
            log = log ?? ((s) => { });
            var result = new RegistrationResult();

            try
            {
                var serverPath = GetMcpServerPath();

                if (!TryResolvePythonRuntime(log, out var pythonRuntime))
                {
                    result.Success = false;
                    result.Message = "No compatible Python runtime found. Please install " + GetSupportedPythonVersionRequirement(log) + ".";
                    return result;
                }

                log($"Config path: {configPath}");
                log($"MCP server path: {serverPath}");
                log($"Python runtime: {pythonRuntime.VersionText} via '{pythonRuntime.Command}'");

                var configDir = Path.GetDirectoryName(configPath);
                if (!string.IsNullOrEmpty(configDir) && !Directory.Exists(configDir))
                {
                    if (createDirIfMissing)
                    {
                        ConfigPathPolicy.EnsurePlainDirectoryTree(configDir);
                        log($"Created directory: {configDir}");
                    }
                    else
                    {
                        result.Success = true;
                        result.Skipped = true;
                        result.Message = $"{clientName} directory does not exist";
                        return result;
                    }
                }

                WithConfigFileLock(configPath, log, () =>
                {
                    bool configExisted = File.Exists(configPath);
                    var existingToml = configExisted
                        ? ReadTextFromPlainFile(configPath, Encoding.UTF8)
                        : string.Empty;
                    var normalizedPath = serverPath.Replace("\\", "/");
                    var expectedBlock = BuildCodexTomlEntry(McpServerNameVs, pythonRuntime, normalizedPath, "vs");

                    var hasLegacySections =
                        HasTomlSection(existingToml, $"mcp_servers.{LegacyMcpServerName}") ||
                        HasTomlSection(existingToml, $"mcp_servers.{LegacyMcpServerName}.env");

                    if (!hasLegacySections && ContainsNormalizedTomlBlock(existingToml, expectedBlock))
                    {
                        log("MCP server already correctly registered");
                        result.Success = true;
                        result.AlreadyRegistered = true;
                        result.Message = "Already registered";
                        return;
                    }

                    var updatedToml = UpsertCodexTomlEntry(existingToml, McpServerNameVs, expectedBlock);
                    WriteTextAtomically(
                        configPath,
                        updatedToml,
                        new UTF8Encoding(false),
                        existingToml,
                        configExisted);

                    log($"Successfully registered '{McpServerNameVs}'");

                    result.Success = true;
                    result.AlreadyRegistered = false;
                    result.Message = $"Successfully registered. Please restart {clientName} to use the AIDebugger tools.";
                    result.ConfigPath = configPath;
                    result.ServerPath = serverPath;
                });

                return result;
            }
            catch (Exception ex)
            {
                log($"Error registering with {clientName}: {ex.Message}");
                result.Success = false;
                result.Message = $"Failed to register: {ex.Message}";
                return result;
            }
        }

        /// <summary>
        /// Checks if the existing OpenCode config is correct.
        /// OpenCode uses "command" as array and "environment" instead of "env".
        /// </summary>
        private static bool IsCorrectOpenCodeConfig(Dictionary<string, object> existingConfig, PythonRuntimeInfo runtime, string normalizedPath, string preferredIde)
        {
            if (existingConfig == null)
            {
                return false;
            }

            // Check type
            if (!existingConfig.ContainsKey("type") || existingConfig["type"]?.ToString() != "local")
            {
                return false;
            }

            // Check enabled
            if (!existingConfig.ContainsKey("enabled"))
            {
                return false;
            }
            var enabled = existingConfig["enabled"];
            if (!(enabled is bool && (bool)enabled))
            {
                return false;
            }

            // Check command array: ["python", "path"] or ["py", "-3.12", "path"]
            var existingCommand = existingConfig.ContainsKey("command") ? existingConfig["command"] : null;
            var existingCommandValues = new List<string>();
            if (existingCommand is object[] cmdArray)
            {
                foreach (var cmd in cmdArray)
                {
                    existingCommandValues.Add(cmd?.ToString() ?? string.Empty);
                }
            }
            else if (existingCommand is System.Collections.ArrayList cmdList)
            {
                foreach (var cmd in cmdList)
                {
                    existingCommandValues.Add(cmd?.ToString() ?? string.Empty);
                }
            }
            else
            {
                return false;
            }

            // Expected command array
            var expectedCommandValues = new List<string>();
            expectedCommandValues.Add(runtime.Command);
            expectedCommandValues.AddRange(runtime.PrefixArgs ?? new string[0]);
            expectedCommandValues.Add(normalizedPath);

            if (existingCommandValues.Count != expectedCommandValues.Count)
            {
                return false;
            }

            for (int i = 0; i < expectedCommandValues.Count; i++)
            {
                if (!string.Equals(existingCommandValues[i], expectedCommandValues[i], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            // Check environment (OpenCode uses "environment" instead of "env")
            if (!existingConfig.ContainsKey("environment"))
            {
                return false;
            }

            var envObj = existingConfig["environment"];
            if (envObj is Dictionary<string, object> envDict)
            {
                if (!envDict.ContainsKey("AIDB_PREFERRED_IDE"))
                {
                    return false;
                }
                var envValue = envDict["AIDB_PREFERRED_IDE"]?.ToString();
                return string.Equals(envValue, preferredIde, StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        /// <summary>
        /// Core registration logic for any MCP client configuration file.
        /// </summary>
        private static RegistrationResult RegisterToConfig(string configPath, string clientName, Action<string> log, bool createDirIfMissing)
        {
            log = log ?? ((s) => { });
            var result = new RegistrationResult();

            try
            {
                var serverPath = GetMcpServerPath();

                if (!TryResolvePythonRuntime(log, out var pythonRuntime))
                {
                    result.Success = false;
                    result.Message = "No compatible Python runtime found. Please install " + GetSupportedPythonVersionRequirement(log) + ".";
                    return result;
                }

                log($"Config path: {configPath}");
                log($"MCP server path: {serverPath}");
                log($"Python runtime: {pythonRuntime.VersionText} via '{pythonRuntime.Command}'");

                // Ensure the config directory exists
                var configDir = Path.GetDirectoryName(configPath);
                if (!Directory.Exists(configDir))
                {
                    if (createDirIfMissing)
                    {
                        ConfigPathPolicy.EnsurePlainDirectoryTree(configDir);
                        log($"Created directory: {configDir}");
                    }
                    else
                    {
                        result.Success = true;
                        result.Skipped = true;
                        result.Message = $"{clientName} directory does not exist";
                        return result;
                    }
                }

                // Use a named mutex to prevent concurrent read-modify-write from multiple IDE instances
                WithConfigFileLock(configPath, log, () =>
                {
                // Read existing config or create new one
                string existingJson;
                bool configExisted = File.Exists(configPath);
                if (configExisted)
                {
                    existingJson = ReadTextFromPlainFile(configPath, Encoding.UTF8);
                    log("Read existing config");
                }
                else
                {
                    existingJson = "{}";
                    log("Creating new config");
                }

                // Use forward slashes for cross-platform compatibility in JSON
                var normalizedPath = serverPath.Replace("\\", "/");

                var entryName = McpServerNameVs;
                var preferredIde = "vs";
                var instructions = BuildServerUseInstructions("VS", "vs");

                var commandArgs = new List<object>();
                foreach (var prefixArg in pythonRuntime.PrefixArgs)
                {
                    commandArgs.Add(prefixArg);
                }
                commandArgs.Add(normalizedPath);
                var serverConfig = new Dictionary<string, object>
                {
                    { "command", pythonRuntime.Command },
                    { "args", commandArgs.ToArray() },
                    { "env", new Dictionary<string, object> { { "AIDB_PREFERRED_IDE", preferredIde }, { "PYTHONIOENCODING", "utf-8" } } },
                    { "serverUseInstructions", instructions }
                };

                // Write updated config
                var json = JsonConfigEditor.UpsertOwnedEntry(
                    existingJson,
                    "mcpServers",
                    entryName,
                    JsonConfigEditor.SerializeUtf8Json(serverConfig));
                json = JsonConfigEditor.RemoveOwnedEntry(json, "mcpServers", LegacyMcpServerName, out _);
                if (string.Equals(json, existingJson, StringComparison.Ordinal))
                {
                    result.Success = true;
                    result.AlreadyRegistered = true;
                    result.Message = "Already registered";
                    return;
                }
                WriteTextAtomically(configPath, json, Encoding.UTF8, existingJson, configExisted);

                log($"Successfully registered '{McpServerNameVs}'");

                result.Success = true;
                result.AlreadyRegistered = false;
                result.Message = $"Successfully registered. Please restart {clientName} to use the AIDebugger tools.";
                result.ConfigPath = configPath;
                result.ServerPath = serverPath;
                }); // end WithConfigFileLock

                return result;
            }
            catch (Exception ex)
            {
                log($"Error registering: {ex.Message}");
                result.Success = false;
                result.Message = $"Failed to register: {ex.Message}";
                return result;
            }
        }

    }
}
