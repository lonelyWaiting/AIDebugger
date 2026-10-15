using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Collections.Generic;

namespace VSDebuggerMCP
{
    public static partial class McpClientRegistration
    {
        /// <summary>
        /// Installs required Python packages for the MCP server.
        /// </summary>
        /// <returns>True if all packages were installed successfully.</returns>
        public static bool InstallPythonDependencies(Action<string> log = null)
        {
            log = log ?? ((s) => { });
            log("Bundled Python dependencies are included. No pip install is required.");
            return true;
        }

        /// <summary>
        /// Checks if Python is available on the system.
        /// </summary>
        public static bool IsPythonAvailable(Action<string> log = null)
        {
            log = log ?? ((s) => { });
            if (TryResolvePythonRuntime(log, out var runtime))
            {
                log($"Compatible Python found: {runtime.VersionText}");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Unregisters the AIDebugger server from Cursor's configuration.
        /// </summary>
        public static bool UnregisterFromCursor(Action<string> log = null)
        {
            var configPath = GetCursorMcpConfigPath();
            return UnregisterFromConfig(configPath, ClientNameCursor, log);
        }

        /// <summary>
        /// Unregisters the AIDebugger server from Claude Desktop's configuration.
        /// </summary>
        public static bool UnregisterFromClaudeDesktop(Action<string> log = null)
        {
            var configPath = GetClaudeDesktopConfigPath();
            return UnregisterFromConfig(configPath, ClientNameClaudeDesktop, log);
        }

        /// <summary>Unregisters the owned entry from Claude Code's user configuration.</summary>
        public static bool UnregisterFromClaudeCode(Action<string> log = null)
        {
            return UnregisterFromConfig(GetClaudeCodeUserConfigPath(), ClientNameClaudeCode, log);
        }

        /// <summary>
        /// Unregisters the AIDebugger server from Codex's configuration.
        /// </summary>
        public static bool UnregisterFromCodex(Action<string> log = null)
        {
            log = log ?? ((s) => { });
            var configPath = GetCodexConfigPath();

            try
            {
                if (!File.Exists(configPath))
                {
                    log($"{ClientNameCodex} config does not exist, nothing to unregister");
                    return true;
                }

                WithConfigFileLock(configPath, log, () =>
                {
                    var existingToml = ReadTextFromPlainFile(configPath, Encoding.UTF8);
                    TomlConfigEditor.RejectUnsupportedOwnedAssignments(
                        existingToml,
                        $"mcp_servers.{McpServerNameVs}",
                        $"mcp_servers.{LegacyMcpServerName}");
                    var updatedToml = RemoveTomlSection(existingToml, $"mcp_servers.{McpServerNameVs}.env");
                    updatedToml = RemoveTomlSection(updatedToml, $"mcp_servers.{McpServerNameVs}");
                    updatedToml = RemoveTomlSection(updatedToml, $"mcp_servers.{LegacyMcpServerName}.env");
                    updatedToml = RemoveTomlSection(updatedToml, $"mcp_servers.{LegacyMcpServerName}");

                    if (NormalizeLineEndings(existingToml).Trim() == NormalizeLineEndings(updatedToml).Trim())
                    {
                        log("No AIDebugger MCP servers found in config, nothing to unregister");
                        return;
                    }

                    WriteTextAtomically(configPath, updatedToml.TrimEnd() + Environment.NewLine,
                        new UTF8Encoding(false), existingToml, true);
                    log($"Successfully unregistered AIDebugger from {ClientNameCodex}");
                });

                return true;
            }
            catch (Exception ex)
            {
                log($"Error unregistering from {ClientNameCodex}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Unregisters the AIDebugger server from OpenCode's configuration.
        /// </summary>
        public static bool UnregisterFromOpenCode(Action<string> log = null)
        {
            var configPath = GetOpenCodeConfigPath();
            return UnregisterFromOpenCodeConfig(configPath, log);
        }

        /// <summary>
        /// Core unregistration logic for OpenCode configuration file.
        /// OpenCode uses "mcp" key instead of "mcpServers".
        /// </summary>
        private static bool UnregisterFromOpenCodeConfig(string configPath, Action<string> log)
        {
            log = log ?? ((s) => { });

            try
            {
                if (!File.Exists(configPath))
                {
                    log($"{ClientNameOpenCode} config does not exist, nothing to unregister");
                    return true;
                }

                WithConfigFileLock(configPath, log, () =>
                {
                var existingJson = ReadTextFromPlainFile(configPath, Encoding.UTF8);
                string updatedJson = existingJson;
                bool removed = false;
                foreach (var name in new[] { McpServerNameVs, LegacyMcpServerName })
                {
                    updatedJson = JsonConfigEditor.RemoveOwnedEntry(updatedJson, "mcp", name, out bool removedEntry);
                    if (removedEntry)
                    {
                        removed = true;
                        log($"Removed MCP server '{name}' from config");
                    }
                }

                if (!removed)
                {
                    log("No AIDebugger MCP servers found in config, nothing to unregister");
                    return;
                }

                WriteTextAtomically(configPath, updatedJson, Encoding.UTF8, existingJson, true);

                log($"Successfully unregistered AIDebugger from {ClientNameOpenCode}");
                }); // end WithConfigFileLock

                return true;
            }
            catch (Exception ex)
            {
                log($"Error unregistering from {ClientNameOpenCode}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Core unregistration logic for any MCP client configuration file.
        /// </summary>
        private static bool UnregisterFromConfig(string configPath, string clientName, Action<string> log)
        {
            log = log ?? ((s) => { });
            bool success = true;

            try
            {
                if (!File.Exists(configPath))
                {
                    log($"{clientName} config does not exist, nothing to unregister");
                    return true;
                }

                WithConfigFileLock(configPath, log, () =>
                {
                var existingJson = ReadTextFromPlainFile(configPath, Encoding.UTF8);
                string updatedJson = existingJson;
                bool removed = false;
                foreach (var name in new[] { McpServerNameVs, LegacyMcpServerName })
                {
                    updatedJson = JsonConfigEditor.RemoveOwnedEntry(updatedJson, "mcpServers", name, out bool removedEntry);
                    if (removedEntry)
                    {
                        removed = true;
                        log($"Removed MCP server '{name}' from config");
                    }
                }

                if (!removed)
                {
                    log("No AIDebugger MCP servers found in config, nothing to unregister");
                    return;
                }

                WriteTextAtomically(configPath, updatedJson, Encoding.UTF8, existingJson, true);

                log($"Successfully unregistered AIDebugger from {clientName}");
                }); // end WithConfigFileLock

                return success;
            }
            catch (Exception ex)
            {
                log($"Error unregistering from {clientName}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Format JSON with indentation for readability.
        /// </summary>
        private static string FormatJson(string json)
        {
            // Simple JSON formatter
            var sb = new StringBuilder();
            int indent = 0;
            bool inString = false;
            char prevChar = '\0';

            foreach (char c in json)
            {
                if (c == '"' && prevChar != '\\')
                {
                    inString = !inString;
                    sb.Append(c);
                }
                else if (!inString)
                {
                    switch (c)
                    {
                        case '{':
                        case '[':
                            sb.Append(c);
                            sb.AppendLine();
                            indent++;
                            sb.Append(new string(' ', indent * 2));
                            break;
                        case '}':
                        case ']':
                            sb.AppendLine();
                            indent--;
                            sb.Append(new string(' ', indent * 2));
                            sb.Append(c);
                            break;
                        case ',':
                            sb.Append(c);
                            sb.AppendLine();
                            sb.Append(new string(' ', indent * 2));
                            break;
                        case ':':
                            sb.Append(c);
                            sb.Append(' ');
                            break;
                        default:
                            if (!char.IsWhiteSpace(c))
                            {
                                sb.Append(c);
                            }
                            break;
                    }
                }
                else
                {
                    sb.Append(c);
                }
                prevChar = c;
            }

            return sb.ToString();
        }

        private static string BuildCodexTomlEntry(string entryName, PythonRuntimeInfo runtime, string normalizedPath, string preferredIde)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[mcp_servers.{entryName}]");
            // Codex treats optional MCP servers with a short catalog grace window; without
            // required=true the Python bridge often misses tool registration on resume.
            sb.AppendLine("enabled = true");
            sb.AppendLine("required = true");
            sb.AppendLine("startup_timeout_sec = 20");
            sb.AppendLine("tool_timeout_sec = 120");
            sb.AppendLine("default_tools_approval_mode = \"auto\"");
            sb.AppendLine($"command = \"{EscapeTomlString(runtime.Command)}\"");
            sb.AppendLine("args = [");

            foreach (var prefixArg in runtime.PrefixArgs ?? new string[0])
            {
                sb.AppendLine($"  \"{EscapeTomlString(prefixArg)}\",");
            }

            sb.AppendLine($"  \"{EscapeTomlString(normalizedPath)}\"");
            sb.AppendLine("]");
            sb.AppendLine();
            sb.AppendLine($"[mcp_servers.{entryName}.env]");
            sb.AppendLine($"AIDB_PREFERRED_IDE = \"{EscapeTomlString(preferredIde)}\"");
            sb.AppendLine("PYTHONIOENCODING = \"utf-8\"");
            return sb.ToString().TrimEnd();
        }

        private static string UpsertCodexTomlEntry(string existingToml, string entryName, string expectedBlock)
        {
            var updatedToml = existingToml ?? string.Empty;
            TomlConfigEditor.RejectUnsupportedOwnedAssignments(
                updatedToml,
                $"mcp_servers.{entryName}",
                $"mcp_servers.{LegacyMcpServerName}");
            updatedToml = RemoveTomlSection(updatedToml, $"mcp_servers.{entryName}.env");
            updatedToml = RemoveTomlSection(updatedToml, $"mcp_servers.{entryName}");
            updatedToml = RemoveTomlSection(updatedToml, $"mcp_servers.{LegacyMcpServerName}.env");
            updatedToml = RemoveTomlSection(updatedToml, $"mcp_servers.{LegacyMcpServerName}");
            updatedToml = updatedToml.TrimEnd();

            if (!string.IsNullOrEmpty(updatedToml))
            {
                updatedToml += Environment.NewLine + Environment.NewLine;
            }

            updatedToml += expectedBlock + Environment.NewLine;
            return updatedToml;
        }

        private static bool ContainsNormalizedTomlBlock(string existingToml, string expectedBlock)
        {
            if (string.IsNullOrWhiteSpace(existingToml) || string.IsNullOrWhiteSpace(expectedBlock))
            {
                return false;
            }

            var normalizedExisting = NormalizeLineEndings(existingToml);
            var normalizedExpected = NormalizeLineEndings(expectedBlock).Trim();
            return normalizedExisting.IndexOf(normalizedExpected, StringComparison.Ordinal) >= 0;
        }

        private static bool HasTomlSection(string content, string sectionName)
        {
            if (string.IsNullOrEmpty(content))
            {
                return false;
            }

            return TomlConfigEditor.HasSection(content, sectionName);
        }

        private static string RemoveTomlSection(string content, string sectionName)
        {
            if (string.IsNullOrEmpty(content))
            {
                return string.Empty;
            }

            return TomlConfigEditor.RemoveSection(content, sectionName);
        }

        private static string NormalizeLineEndings(string value)
        {
            return (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        }

        private static string EscapeTomlString(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static bool IsCorrectServerConfig(Dictionary<string, object> existingConfig, PythonRuntimeInfo runtime, string normalizedPath, string preferredIde)
        {
            if (existingConfig == null)
            {
                return false;
            }

            var existingCommand = existingConfig.ContainsKey("command") ? existingConfig["command"]?.ToString() : "";
            if (!string.Equals(existingCommand, runtime.Command, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var existingArgs = existingConfig.ContainsKey("args") ? existingConfig["args"] : null;
            var existingArgValues = new List<string>();
            if (existingArgs is object[] argsArray)
            {
                foreach (var arg in argsArray)
                {
                    existingArgValues.Add(arg?.ToString() ?? string.Empty);
                }
            }
            else if (existingArgs is System.Collections.ArrayList argsList)
            {
                foreach (var arg in argsList)
                {
                    existingArgValues.Add(arg?.ToString() ?? string.Empty);
                }
            }
            else
            {
                return false;
            }

            var expectedArgValues = new List<string>();
            expectedArgValues.AddRange(runtime.PrefixArgs ?? new string[0]);
            expectedArgValues.Add(normalizedPath);

            if (existingArgValues.Count != expectedArgValues.Count)
            {
                return false;
            }

            for (int i = 0; i < expectedArgValues.Count; i++)
            {
                if (!string.Equals(existingArgValues[i], expectedArgValues[i], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            var hasServerUseInstructions = existingConfig.ContainsKey("serverUseInstructions")
                && !string.IsNullOrEmpty(existingConfig["serverUseInstructions"]?.ToString());

            if (!hasServerUseInstructions)
            {
                return false;
            }

            if (!existingConfig.ContainsKey("env"))
            {
                return false;
            }

            var envObj = existingConfig["env"];
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

        private static bool IsCorrectClaudeCodeConfig(Dictionary<string, object> existingConfig, PythonRuntimeInfo runtime, string normalizedPath, string preferredIde)
        {
            if (existingConfig == null)
            {
                return false;
            }

            var existingType = existingConfig.ContainsKey("type") ? existingConfig["type"]?.ToString() : "";
            if (!string.Equals(existingType, "stdio", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var existingCommand = existingConfig.ContainsKey("command") ? existingConfig["command"]?.ToString() : "";
            if (!string.Equals(existingCommand, runtime.Command, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var existingArgs = existingConfig.ContainsKey("args") ? existingConfig["args"] : null;
            var existingArgValues = new List<string>();
            if (existingArgs is object[] argsArray)
            {
                foreach (var arg in argsArray)
                {
                    existingArgValues.Add(arg?.ToString() ?? string.Empty);
                }
            }
            else if (existingArgs is System.Collections.ArrayList argsList)
            {
                foreach (var arg in argsList)
                {
                    existingArgValues.Add(arg?.ToString() ?? string.Empty);
                }
            }
            else
            {
                return false;
            }

            var expectedArgValues = new List<string>();
            expectedArgValues.AddRange(runtime.PrefixArgs ?? new string[0]);
            expectedArgValues.Add(normalizedPath);

            if (existingArgValues.Count != expectedArgValues.Count)
            {
                return false;
            }

            for (int i = 0; i < expectedArgValues.Count; i++)
            {
                if (!string.Equals(existingArgValues[i], expectedArgValues[i], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            if (!existingConfig.ContainsKey("env"))
            {
                return false;
            }

            var envObj = existingConfig["env"];
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
        /// Creates a named Mutex based on a config file path to prevent concurrent read-modify-write.
        /// Multiple Visual Studio instances may try to register/unregister simultaneously against the same config file.
        /// </summary>
        private static Mutex GetConfigFileMutex(string configPath)
        {
            var normalizedPath = Path.GetFullPath(configPath).ToLowerInvariant().Replace('\\', '/');
            string stableHash;
            using (var sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(normalizedPath));
                stableHash = BitConverter.ToString(hash, 0, 16).Replace("-", string.Empty);
            }
            var currentSid = WindowsIdentity.GetCurrent().User;
            if (currentSid == null) throw new InvalidOperationException("Unable to identify the config-lock owner");
            string sidHash;
            using (var sha256 = SHA256.Create())
            {
                sidHash = BitConverter.ToString(
                    sha256.ComputeHash(Encoding.UTF8.GetBytes(currentSid.Value)), 0, 8).Replace("-", string.Empty);
            }
            var security = new MutexSecurity();
            security.SetOwner(currentSid);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new MutexAccessRule(currentSid, MutexRights.FullControl, AccessControlType.Allow));
            var mutexName = "Global\\AIDebugger_Config_" + sidHash + "_" + stableHash;
            bool created;
            return new Mutex(false, mutexName, out created, security);
        }

        private static void WriteTextAtomically(string path, string contents, Encoding encoding)
        {
            WriteTextAtomicallyCore(path, contents, encoding, null, false, false);
        }

        private static void WriteTextAtomically(
            string path,
            string contents,
            Encoding encoding,
            string expectedOriginal,
            bool expectedFileExists)
        {
            WriteTextAtomicallyCore(path, contents, encoding, expectedOriginal, expectedFileExists, true);
        }

        private static void WriteTextAtomicallyCore(
            string path,
            string contents,
            Encoding encoding,
            string expectedOriginal,
            bool expectedFileExists,
            bool compareAndSwap)
        {
            string fullPath = ConfigPathPolicy.EnsureSafeFilePath(path, createDirectory: true);
            string directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directory))
            {
                throw new InvalidOperationException("Config path has no parent directory");
            }
            string temporaryPath = Path.Combine(
                directory,
                "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, encoding))
                {
                    writer.Write(contents ?? string.Empty);
                    writer.Flush();
                    stream.Flush(true);
                }
                ConfigPathPolicy.ValidatePlainDirectoryTree(directory);
                if (!BundlePathPolicy.IsPlainFile(temporaryPath))
                {
                    throw new InvalidOperationException("Config staging file is not a plain file");
                }
                ConfigPathPolicy.ValidateFinalFileEntry(fullPath);

                if (compareAndSwap)
                {
                    bool existsNow = File.Exists(fullPath);
                    if (existsNow != expectedFileExists)
                        throw new ConfigFileChangedException("The client config was created or removed by another process");
                    if (existsNow)
                    {
                        string current = ReadTextFromPlainFile(fullPath, encoding);
                        if (!string.Equals(current, expectedOriginal, StringComparison.Ordinal))
                            throw new ConfigFileChangedException("The client config changed while AIDebugger was preparing its update");
                    }
                }

                if (File.Exists(fullPath))
                {
                    string backupPath = fullPath + ".aidb.bak";
                    ConfigPathPolicy.ValidateFinalFileEntry(backupPath);
                    if (File.Exists(backupPath))
                    {
                        if (!BundlePathPolicy.IsPlainFile(backupPath))
                            throw new InvalidOperationException("Config backup is not a plain file");
                        File.Delete(backupPath);
                    }
                    File.Replace(temporaryPath, fullPath, backupPath, true);
                    if (compareAndSwap)
                    {
                        string replacedContents = ReadTextFromPlainFile(backupPath, encoding);
                        if (!string.Equals(replacedContents, expectedOriginal, StringComparison.Ordinal))
                        {
                            // File.Replace is atomic, but a third party can write again after
                            // our replace. Never overwrite that newer edit while trying to put
                            // the first conflicting version back. If the active file is still
                            // our exact payload, the second File.Replace preserves whatever is
                            // current at the instant of rollback in failedAttempt; a racing edit
                            // is retained as a conflict artifact rather than deleted.
                            string activeContents = ReadTextFromPlainFile(fullPath, encoding);
                            if (!string.Equals(activeContents, contents ?? string.Empty, StringComparison.Ordinal))
                            {
                                throw new ConfigFileChangedException(
                                    "The client config changed again after atomic replacement; both the active file and the prior external version in the AIDebugger backup were preserved");
                            }
                            string failedAttempt = fullPath + ".aidb.failed." + Guid.NewGuid().ToString("N");
                            File.Replace(backupPath, fullPath, failedAttempt, true);
                            if (File.Exists(failedAttempt))
                            {
                                string displacedContents = ReadTextFromPlainFile(failedAttempt, encoding);
                                if (string.Equals(displacedContents, contents ?? string.Empty, StringComparison.Ordinal))
                                {
                                    File.Delete(failedAttempt);
                                }
                                else
                                {
                                    RestoreNewestConfigConflictAsActive(
                                        fullPath,
                                        failedAttempt,
                                        replacedContents,
                                        encoding);
                                }
                            }
                            throw new ConfigFileChangedException(
                                "The client config changed during atomic replacement; the external version was restored");
                        }
                    }
                    if (!BundlePathPolicy.IsPlainFile(backupPath))
                    {
                        throw new InvalidOperationException("Config backup is not a plain file");
                    }
                    foreach (string legacyBackup in Directory.GetFiles(
                        directory,
                        Path.GetFileName(fullPath) + ".aidb.*.bak",
                        SearchOption.TopDirectoryOnly))
                    {
                        if (string.Equals(legacyBackup, backupPath, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!BundlePathPolicy.IsPlainFile(legacyBackup))
                            throw new InvalidOperationException("Legacy config backup is not a plain file");
                        File.Delete(legacyBackup);
                    }
                }
                else
                {
                    try { File.Move(temporaryPath, fullPath); }
                    catch (IOException) when (compareAndSwap && File.Exists(fullPath))
                    {
                        throw new ConfigFileChangedException("The client config was concurrently created");
                    }
                }
                ConfigPathPolicy.ValidatePlainDirectoryTree(directory);
                if (!BundlePathPolicy.IsPlainFile(fullPath))
                {
                    throw new InvalidOperationException("Atomic config write did not produce a plain file");
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        private static void RestoreNewestConfigConflictAsActive(
            string fullPath,
            string candidatePath,
            string expectedActiveContents,
            Encoding encoding)
        {
            // Each atomic replacement captures a still-newer concurrent edit in
            // the displaced artifact. Promote that captured edit and repeat until
            // the displaced version is the one we expected to be active. This
            // makes the last observed external edit active without deleting any
            // conflict artifact.
            string candidateContents = ReadTextFromPlainFile(candidatePath, encoding);
            for (int attempt = 0; attempt < 16; attempt++)
            {
                string displacedPath = fullPath + ".aidb.conflict." + Guid.NewGuid().ToString("N");
                File.Replace(candidatePath, fullPath, displacedPath, true);
                string displacedContents = ReadTextFromPlainFile(displacedPath, encoding);
                if (string.Equals(displacedContents, expectedActiveContents, StringComparison.Ordinal))
                {
                    return;
                }

                expectedActiveContents = candidateContents;
                candidatePath = displacedPath;
                candidateContents = displacedContents;
            }
            throw new ConfigFileChangedException(
                "The client config changed continuously during conflict recovery; every observed version was preserved for manual recovery",
                retryable: false);
        }

        private static string ReadTextFromPlainFile(string path, Encoding encoding)
        {
            string fullPath = ConfigPathPolicy.EnsureSafeFilePath(path, createDirectory: false);
            if (!BundlePathPolicy.IsPlainFile(fullPath))
            {
                throw new FileNotFoundException("Config file is missing or is not a plain file", fullPath);
            }
            string contents = File.ReadAllText(fullPath, encoding);
            ConfigPathPolicy.EnsureSafeFilePath(fullPath, createDirectory: false);
            if (!BundlePathPolicy.IsPlainFile(fullPath))
            {
                throw new InvalidOperationException("Config file changed while it was being read");
            }
            return contents;
        }

        private static void WriteBytesAtomicallyNoBackup(string path, byte[] contents)
        {
            string fullPath = ConfigPathPolicy.EnsureSafeFilePath(path, createDirectory: true);
            string directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directory)) throw new InvalidOperationException("Path has no parent directory");
            string temporaryPath = Path.Combine(directory, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    byte[] payload = contents ?? new byte[0];
                    stream.Write(payload, 0, payload.Length);
                    stream.Flush(true);
                }
                ConfigPathPolicy.ValidatePlainDirectoryTree(directory);
                if (!BundlePathPolicy.IsPlainFile(temporaryPath))
                {
                    throw new InvalidOperationException("Atomic staging file is not a plain file");
                }
                FileAttributes existingAttributes = 0;
                bool existingEntry = false;
                try
                {
                    existingAttributes = File.GetAttributes(fullPath);
                    existingEntry = true;
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }

                if (existingEntry &&
                    (existingAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    throw new InvalidOperationException("Refusing to replace a directory or reparse point with a file");
                }

                if (existingEntry) File.Replace(temporaryPath, fullPath, null, true);
                else File.Move(temporaryPath, fullPath);
                if (!BundlePathPolicy.IsPlainFile(fullPath))
                {
                    throw new InvalidOperationException("Atomic write did not produce a plain file");
                }
                ConfigPathPolicy.ValidatePlainDirectoryTree(directory);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        /// <summary>
        /// Safely reads, modifies, and writes a config file under a named mutex.
        /// The modifyAction receives the parsed config dictionary and should return true if changes were made.
        /// </summary>
        private static void WithConfigFileLock(string configPath, Action<string> log, Action action)
        {
            using (var mutex = GetConfigFileMutex(configPath))
            {
                bool acquired = false;
                try
                {
                    try
                    {
                        acquired = mutex.WaitOne(TimeSpan.FromSeconds(10));
                    }
                    catch (AbandonedMutexException)
                    {
                        acquired = true;
                        log("Recovered an abandoned MCP client config lock; validating and continuing the atomic update.");
                    }
                    if (!acquired)
                    {
                        throw new TimeoutException("Could not acquire the MCP client config lock within 10 seconds");
                    }
                    ConfigPathPolicy.EnsureSafeFilePath(configPath, createDirectory: true);
                    for (int attempt = 0; ; attempt++)
                    {
                        try
                        {
                            action();
                            break;
                        }
                        catch (ConfigFileChangedException ex) when (ex.Retryable && attempt < 2)
                        {
                            log("MCP client config changed concurrently; re-reading and merging the owned entry.");
                        }
                    }
                }
                finally
                {
                    if (acquired)
                    {
                        try { mutex.ReleaseMutex(); }
                        catch (ApplicationException) { /* mutex not owned */ }
                    }
                }
            }
        }

        private sealed class ConfigFileChangedException : IOException
        {
            internal ConfigFileChangedException(string message, bool retryable = true) : base(message)
            {
                Retryable = retryable;
            }

            internal bool Retryable { get; }
        }
    }
}
