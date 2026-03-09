using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Collections.Generic;

namespace VSDebuggerMCP
{
    /// <summary>
    /// Handles automatic registration of the MCP server in MCP client configurations (Cursor, Claude Desktop).
    /// </summary>
    public static class McpClientRegistration
    {
        private const string McpServerNameVs = "AIDebuggerVS";
        private const string LegacyMcpServerName = "AIDebugger";
        
        // Client display names for logging
        private const string ClientNameCursor = "Cursor";
        private const string ClientNameClaudeDesktop = "Claude Desktop";
        private const string ClientNameOpenCode = "OpenCode";
        private const string CursorAllowListSyncScript = "cursor_allowlist_sync.py";

        /// <summary>
        /// Server use instructions that help Cursor AI understand when and how to use this MCP.
        /// This is critical for AI to automatically invoke debugging tools when users ask about crashes, errors, etc.
        /// </summary>
        private static readonly string ServerUseInstructionsTemplate = @"AIDebugger{0} - {0}调试信息获取工具

## 何时使用此MCP

当用户提到以下关键词或场景时，应主动使用此MCP获取调试信息：

### 崩溃/异常分析（最常见场景）
- 关键词：崩溃、crash、异常、exception、错误、error、闪退、挂起、hang、死循环
- 推荐操作：优先调用 capture_debug_bundle（必要时附带 expressions）获取稳定快照；退化场景再用 get_full_context 或 get_exception + get_callstack

### 调试分析
- 关键词：调试、debug、断点、breakpoint、单步、step、堆栈、stack、变量、variable
- 推荐操作：先 get_debug_status 判断 stopReason，再按需 get_callstack/get_locals；多表达式分析优先 evaluate_expressions_batch

### 主动检测调试现场（推荐）
- 在调用任何调试工具前，先调用 detect_debug_sessions(prefer_ide=""{1}"") 检测是否有 VS/Rider 调试现场
- 若自动选择失败，使用 list_ide_instances + select_ide_instance 手动选择

### 编译问题
- 关键词：编译错误、build error、编译失败、无法编译、syntax error、链接错误
- 推荐操作：调用 get_build_errors 获取错误列表

### 内存/底层分析
- 关键词：内存、memory、指针、pointer、地址、address、反汇编、disassembly、寄存器
- 推荐操作：使用 read_memory、get_disassembly、get_registers 等

## 推荐工作流程

### 崩溃分析标准流程：
1. 调用 capture_debug_bundle 一次性获取稳定上下文（含 snapshotId/partial/snapshotStatus）
2. 根据返回的文件路径，使用 Read 工具读取相关源代码
3. 分析异常信息和堆栈，定位问题根因
4. 如需更多信息，优先 evaluate_expressions_batch；仅单条场景再用 evaluate_expression

### 断点调试流程：
1. get_debug_status 确认调试状态
2. get_callstack 查看调用链
3. get_locals 查看当前变量
4. evaluate_expression 求值特定表达式

## 重要注意事项

- 支持多个IDE实例，会自动选择处于断点暂停状态的实例
- 如果有多个实例且无法自动选择，使用 list_ide_instances 查看，然后 select_ide_instance 选择
- 调试控制工具（如 debug_step_over）会改变程序状态，需谨慎使用
- 首选 capture_debug_bundle 作为分析起点；当 IDE 不支持完整快照时再退化到 get_full_context";

        private static string BuildServerUseInstructions(string ideLabel, string preferredIde)
        {
            return string.Format(ServerUseInstructionsTemplate, ideLabel, preferredIde);
        }

        private class PythonRuntimeInfo
        {
            public string Command { get; set; }
            public string[] PrefixArgs { get; set; }
            public string VersionText { get; set; }
        }

        private static bool IsSupportedPythonVersion(int major, int minor)
        {
            return major == 3 && minor >= 10;
        }

        private static bool TryResolvePythonRuntime(Action<string> log, out PythonRuntimeInfo runtime)
        {
            runtime = null;
            log = log ?? ((s) => { });

            // Use "py -3" to automatically select the latest Python 3.x on Windows.
            // This avoids hardcoding version-specific candidates (py -3.14, py -3.13, etc.)
            // and automatically adapts when new Python versions are installed.
            // Fallback to "python" / "python3" for non-Windows or environments without the py launcher.
            var candidates = new[]
            {
                new { Command = "py", Args = new[] { "-3" } },
                new { Command = "python3", Args = new string[0] },
                new { Command = "python", Args = new string[0] }
            };

            foreach (var candidate in candidates)
            {
                if (TryProbePythonRuntime(candidate.Command, candidate.Args, log, out runtime))
                {
                    return true;
                }
            }

            log("No compatible Python runtime found. Please install Python 3.10+ x64.");
            return false;
        }

        private static bool TryProbePythonRuntime(string command, string[] prefixArgs, Action<string> log, out PythonRuntimeInfo runtime)
        {
            runtime = null;
            log = log ?? ((s) => { });

            try
            {
                var argsBuilder = new StringBuilder();
                if (prefixArgs != null && prefixArgs.Length > 0)
                {
                    argsBuilder.Append(string.Join(" ", prefixArgs));
                    argsBuilder.Append(" ");
                }
                argsBuilder.Append("--version");

                var startInfo = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = argsBuilder.ToString(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        return false;
                    }

                    var output = process.StandardOutput.ReadToEnd();
                    var error = process.StandardError.ReadToEnd();
                    process.WaitForExit(5000);

                    if (process.ExitCode != 0)
                    {
                        return false;
                    }

                    var versionText = string.IsNullOrEmpty(output) ? error : output;
                    var trimmed = versionText.Trim();
                    var match = Regex.Match(trimmed, @"Python\s+(\d+)\.(\d+)\.(\d+)");
                    if (!match.Success)
                    {
                        log($"Python runtime check returned an unrecognized version string: {trimmed}");
                        return false;
                    }

                    var major = int.Parse(match.Groups[1].Value);
                    var minor = int.Parse(match.Groups[2].Value);
                    if (!IsSupportedPythonVersion(major, minor))
                    {
                        log($"Ignoring unsupported Python runtime: {trimmed} (requires 3.10+)");
                        return false;
                    }

                    runtime = new PythonRuntimeInfo
                    {
                        Command = command,
                        PrefixArgs = prefixArgs ?? new string[0],
                        VersionText = trimmed
                    };

                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Gets the path to the MCP server Python script.
        /// Primary location is ~/.ai-debugger/mcp_server (stable path).
        /// If the stable path doesn't exist, attempts to seed from the VSIX extension directory.
        /// </summary>
        public static string GetMcpServerPath()
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            
            // Primary location: ~/.ai-debugger/mcp_server (stable path)
            var stablePath = Path.Combine(userProfile, ".ai-debugger", "mcp_server", "server.py");
            var normalizedStablePath = Path.GetFullPath(stablePath);
            
            if (File.Exists(normalizedStablePath))
            {
                return normalizedStablePath;
            }

            // Stable path doesn't exist – try to seed from the VSIX extension directory or dev layout
            var extensionDir = GetExtensionDirectory();
            var sourceDirectories = new[]
            {
                // Extension directory (bundled with VSIX)
                Path.Combine(extensionDir, "mcp_server"),
                // Development layout: running from VSDebuggerMCP/bin/Debug
                Path.GetFullPath(Path.Combine(extensionDir, "..", "..", "..", "mcp_server")),
            };

            foreach (var sourceDir in sourceDirectories)
            {
                var sourceServer = Path.Combine(sourceDir, "server.py");
                if (File.Exists(sourceServer))
                {
                    if (TrySeedServerFiles(sourceDir, Path.GetDirectoryName(normalizedStablePath)))
                    {
                        return normalizedStablePath;
                    }
                    // Seeding failed, fall back to using source directly
                    return Path.GetFullPath(sourceServer);
                }
            }

            // Legacy user profile location
            var legacyPath = Path.Combine(userProfile, ".vs-debugger-mcp", "mcp_server", "server.py");
            var normalizedLegacy = Path.GetFullPath(legacyPath);
            if (File.Exists(normalizedLegacy))
            {
                return normalizedLegacy;
            }

            // Return the stable path even if it doesn't exist (will show meaningful error)
            return normalizedStablePath;
        }

        /// <summary>
        /// Copies all MCP server files from a source directory to the stable target directory.
        /// </summary>
        private static bool TrySeedServerFiles(string sourceDir, string targetDir)
        {
            try
            {
                if (Directory.Exists(targetDir))
                {
                    Directory.Delete(targetDir, true);
                }
                CopyDirectoryRecursive(sourceDir, targetDir);
                return File.Exists(Path.Combine(targetDir, "server.py"));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Recursively copies a directory and all its contents.
        /// </summary>
        private static void CopyDirectoryRecursive(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var destFile = Path.Combine(targetDir, Path.GetFileName(file));
                File.Copy(file, destFile, true);
            }

            foreach (var subDir in Directory.GetDirectories(sourceDir))
            {
                var destSubDir = Path.Combine(targetDir, Path.GetFileName(subDir));
                CopyDirectoryRecursive(subDir, destSubDir);
            }
        }

        /// <summary>
        /// Gets the directory where this extension is installed.
        /// </summary>
        private static string GetExtensionDirectory()
        {
            return Path.GetDirectoryName(typeof(McpClientRegistration).Assembly.Location);
        }

        private static string GetBundledMcpServerFilePath(string relativePath)
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var stablePath = Path.Combine(userProfile, ".ai-debugger", "mcp_server", relativePath);
            var normalizedStablePath = Path.GetFullPath(stablePath);
            if (File.Exists(normalizedStablePath))
            {
                return normalizedStablePath;
            }

            var extensionDir = GetExtensionDirectory();
            var candidatePaths = new[]
            {
                Path.Combine(extensionDir, "mcp_server", relativePath),
                Path.GetFullPath(Path.Combine(extensionDir, "..", "..", "..", "mcp_server", relativePath)),
            };

            foreach (var candidatePath in candidatePaths)
            {
                if (File.Exists(candidatePath))
                {
                    return Path.GetFullPath(candidatePath);
                }
            }

            return normalizedStablePath;
        }

        private static string QuoteProcessArgument(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            if (value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            {
                return value;
            }

            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static void TrySyncCursorAllowList(string serverName, Action<string> log)
        {
            log = log ?? ((s) => { });

            try
            {
                if (!TryResolvePythonRuntime(log, out var pythonRuntime))
                {
                    log("[Cursor] Skipping MCP allow list sync because no compatible Python runtime was found.");
                    return;
                }

                var scriptPath = GetBundledMcpServerFilePath(CursorAllowListSyncScript);
                if (!File.Exists(scriptPath))
                {
                    log($"[Cursor] Skipping MCP allow list sync because '{CursorAllowListSyncScript}' was not found at {scriptPath}");
                    return;
                }

                var argsBuilder = new StringBuilder();
                foreach (var prefixArg in pythonRuntime.PrefixArgs)
                {
                    if (argsBuilder.Length > 0)
                    {
                        argsBuilder.Append(' ');
                    }
                    argsBuilder.Append(QuoteProcessArgument(prefixArg));
                }
                if (argsBuilder.Length > 0)
                {
                    argsBuilder.Append(' ');
                }
                argsBuilder.Append(QuoteProcessArgument(scriptPath));
                argsBuilder.Append(" --server ");
                argsBuilder.Append(QuoteProcessArgument(serverName));

                var startInfo = new ProcessStartInfo
                {
                    FileName = pythonRuntime.Command,
                    Arguments = argsBuilder.ToString(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        log("[Cursor] Failed to start MCP allow list sync helper.");
                        return;
                    }

                    var output = process.StandardOutput.ReadToEnd().Trim();
                    var error = process.StandardError.ReadToEnd().Trim();
                    process.WaitForExit(15000);

                    if (process.ExitCode == 0)
                    {
                        log(string.IsNullOrEmpty(output)
                            ? "[Cursor] MCP allow list sync completed."
                            : $"[Cursor] MCP allow list sync completed: {output}");
                    }
                    else
                    {
                        log(string.IsNullOrEmpty(error)
                            ? $"[Cursor] MCP allow list sync failed with exit code {process.ExitCode}."
                            : $"[Cursor] MCP allow list sync failed: {error}");
                    }
                }
            }
            catch (Exception ex)
            {
                log($"[Cursor] MCP allow list sync failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the path to Cursor's mcp.json configuration file.
        /// </summary>
        public static string GetCursorMcpConfigPath()
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(userProfile, ".cursor", "mcp.json");
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
            var result = RegisterToConfig(configPath, ClientNameCursor, log, createDirIfMissing: true);
            if (result.Success)
            {
                TrySyncCursorAllowList(McpServerNameVs, log);
            }
            return result;
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
                    Directory.CreateDirectory(openCodeDir);
                    log($"[{ClientNameOpenCode}] Created directory: {openCodeDir}");
                }

                var serverPath = GetMcpServerPath();

                if (!TryResolvePythonRuntime(log, out var pythonRuntime))
                {
                    result.Success = false;
                    result.Message = "No compatible Python runtime found. Please install Python 3.10-3.13 x64.";
                    return result;
                }

                log($"[{ClientNameOpenCode}] Config path: {configPath}");
                log($"[{ClientNameOpenCode}] MCP server path: {serverPath}");
                log($"[{ClientNameOpenCode}] Python runtime: {pythonRuntime.VersionText} via '{pythonRuntime.Command}'");

                // Use a named mutex to prevent concurrent read-modify-write from multiple IDE instances
                WithConfigFileLock(configPath, log, () =>
                {
                // Read existing config or create new one
                Dictionary<string, object> config;
                if (File.Exists(configPath))
                {
                    var existingJson = File.ReadAllText(configPath, Encoding.UTF8);
                    var serializer = new JavaScriptSerializer();
                    config = serializer.Deserialize<Dictionary<string, object>>(existingJson)
                             ?? new Dictionary<string, object>();
                    log($"[{ClientNameOpenCode}] Read existing config");
                }
                else
                {
                    config = new Dictionary<string, object>();
                    log($"[{ClientNameOpenCode}] Creating new config");
                }

                // Ensure "mcp" object exists (OpenCode uses "mcp" instead of "mcpServers")
                Dictionary<string, object> mcpServers;
                if (config.ContainsKey("mcp") && config["mcp"] is Dictionary<string, object>)
                {
                    mcpServers = (Dictionary<string, object>)config["mcp"];
                }
                else
                {
                    mcpServers = new Dictionary<string, object>();
                    config["mcp"] = mcpServers;
                }

                // Use forward slashes for cross-platform compatibility in JSON
                var normalizedPath = serverPath.Replace("\\", "/");

                var entryName = McpServerNameVs;
                var preferredIde = "vs";

                // Check if already correctly registered
                if (mcpServers.ContainsKey(entryName))
                {
                    var existingConfig = mcpServers[entryName] as Dictionary<string, object>;
                    if (IsCorrectOpenCodeConfig(existingConfig, pythonRuntime, normalizedPath, preferredIde))
                    {
                        log($"[{ClientNameOpenCode}] MCP server already correctly registered");
                        result.Success = true;
                        result.AlreadyRegistered = true;
                        result.Message = "Already registered";
                        return;
                    }
                    log($"[{ClientNameOpenCode}] MCP server '{entryName}' exists but needs update...");
                }

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
                mcpServers[entryName] = serverConfig;

                // Remove legacy entry if present
                if (mcpServers.ContainsKey(LegacyMcpServerName))
                {
                    log($"[{ClientNameOpenCode}] Removing legacy MCP server entry '{LegacyMcpServerName}'.");
                    mcpServers.Remove(LegacyMcpServerName);
                }

                // Write updated config
                var serializer2 = new JavaScriptSerializer();
                var json = FormatJson(serializer2.Serialize(config));
                File.WriteAllText(configPath, json, Encoding.UTF8);

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
                    result.Message = "No compatible Python runtime found. Please install Python 3.10-3.13 x64.";
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
                        Directory.CreateDirectory(configDir);
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
                Dictionary<string, object> config;
                if (File.Exists(configPath))
                {
                    var existingJson = File.ReadAllText(configPath, Encoding.UTF8);
                    var serializer = new JavaScriptSerializer();
                    config = serializer.Deserialize<Dictionary<string, object>>(existingJson) 
                             ?? new Dictionary<string, object>();
                    log("Read existing config");
                }
                else
                {
                    config = new Dictionary<string, object>();
                    log("Creating new config");
                }

                // Ensure mcpServers object exists
                Dictionary<string, object> mcpServers;
                if (config.ContainsKey("mcpServers") && config["mcpServers"] is Dictionary<string, object>)
                {
                    mcpServers = (Dictionary<string, object>)config["mcpServers"];
                }
                else
                {
                    mcpServers = new Dictionary<string, object>();
                    config["mcpServers"] = mcpServers;
                }

                // Use forward slashes for cross-platform compatibility in JSON
                var normalizedPath = serverPath.Replace("\\", "/");

                var entryName = McpServerNameVs;
                var preferredIde = "vs";
                var instructions = BuildServerUseInstructions("VS", "vs");

                bool allCorrect = true;
                bool isCorrect = false;
                if (mcpServers.ContainsKey(entryName))
                {
                    var existingConfig = mcpServers[entryName] as Dictionary<string, object>;
                    isCorrect = IsCorrectServerConfig(existingConfig, pythonRuntime, normalizedPath, preferredIde);
                    if (!isCorrect)
                    {
                        log($"MCP server '{entryName}' exists but needs update...");
                    }
                }
                else
                {
                    allCorrect = false;
                }

                if (!isCorrect)
                {
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
                    mcpServers[entryName] = serverConfig;
                    allCorrect = false;
                }

                if (mcpServers.ContainsKey(LegacyMcpServerName))
                {
                    log($"Removing legacy MCP server entry '{LegacyMcpServerName}'.");
                    mcpServers.Remove(LegacyMcpServerName);
                    allCorrect = false;
                }

                if (allCorrect)
                {
                    log($"MCP server already correctly registered");
                    result.Success = true;
                    result.AlreadyRegistered = true;
                    result.Message = "Already registered";
                    return;
                }

                // Write updated config
                var serializer2 = new JavaScriptSerializer();
                var json = FormatJson(serializer2.Serialize(config));
                File.WriteAllText(configPath, json, Encoding.UTF8);

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
                var existingJson = File.ReadAllText(configPath, Encoding.UTF8);
                var serializer = new JavaScriptSerializer();
                var config = serializer.Deserialize<Dictionary<string, object>>(existingJson);

                if (config == null || !config.ContainsKey("mcp"))
                {
                    log("No mcp in config, nothing to unregister");
                    return;
                }

                var mcpServers = config["mcp"] as Dictionary<string, object>;
                if (mcpServers == null)
                {
                    log("mcp is not a valid object, nothing to unregister");
                    return;
                }

                bool removed = false;
                foreach (var name in new[] { McpServerNameVs, LegacyMcpServerName })
                {
                    if (mcpServers.ContainsKey(name))
                    {
                        mcpServers.Remove(name);
                        removed = true;
                        log($"Removed MCP server '{name}' from config");
                    }
                }

                if (!removed)
                {
                    log("No AIDebugger MCP servers found in config, nothing to unregister");
                    return;
                }

                var json = FormatJson(serializer.Serialize(config));
                File.WriteAllText(configPath, json, Encoding.UTF8);

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
                var existingJson = File.ReadAllText(configPath, Encoding.UTF8);
                var serializer = new JavaScriptSerializer();
                var config = serializer.Deserialize<Dictionary<string, object>>(existingJson);

                if (config == null || !config.ContainsKey("mcpServers"))
                {
                    log("No mcpServers in config, nothing to unregister");
                    return;
                }

                var mcpServers = config["mcpServers"] as Dictionary<string, object>;
                if (mcpServers == null)
                {
                    log("mcpServers is not a valid object, nothing to unregister");
                    return;
                }

                bool removed = false;
                foreach (var name in new[] { McpServerNameVs, LegacyMcpServerName })
                {
                    if (mcpServers.ContainsKey(name))
                    {
                        mcpServers.Remove(name);
                        removed = true;
                        log($"Removed MCP server '{name}' from config");
                    }
                }

                if (!removed)
                {
                    log("No AIDebugger MCP servers found in config, nothing to unregister");
                    return;
                }

                var json = FormatJson(serializer.Serialize(config));
                File.WriteAllText(configPath, json, Encoding.UTF8);

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

        /// <summary>
        /// Creates a named Mutex based on a config file path to prevent concurrent read-modify-write.
        /// Multiple VS/Rider instances may try to register/unregister simultaneously against the same config file.
        /// </summary>
        private static Mutex GetConfigFileMutex(string configPath)
        {
            // Use a sanitized version of the absolute path as the mutex name
            var normalizedPath = Path.GetFullPath(configPath).ToLowerInvariant().Replace('\\', '/');
            var mutexName = "Global\\AIDebugger_Config_" + normalizedPath.GetHashCode().ToString("X8");
            return new Mutex(false, mutexName);
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
                    acquired = mutex.WaitOne(TimeSpan.FromSeconds(10));
                    if (!acquired)
                    {
                        log("Warning: Could not acquire config file lock within 10 seconds, proceeding anyway");
                    }
                    action();
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
    }

    /// <summary>
    /// Result of MCP registration attempt.
    /// </summary>
    public class RegistrationResult
    {
        public bool Success { get; set; }
        public bool AlreadyRegistered { get; set; }
        public bool Skipped { get; set; }
        public string Message { get; set; }
        public string ConfigPath { get; set; }
        public string ServerPath { get; set; }
    }
}
