using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Collections.Generic;

namespace VSDebuggerMCP
{
    /// <summary>
    /// Handles automatic registration of the MCP server in Cursor's mcp.json configuration.
    /// </summary>
    public static class CursorMcpRegistration
    {
        private const string McpServerNameVs = "AIDebuggerVS";
        private const string LegacyMcpServerName = "AIDebugger";

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
            return major == 3 && minor >= 10 && minor <= 13;
        }

        private static bool TryResolvePythonRuntime(Action<string> log, out PythonRuntimeInfo runtime)
        {
            runtime = null;
            log = log ?? ((s) => { });

            var candidates = new[]
            {
                new { Command = "py", Args = new[] { "-3.13" } },
                new { Command = "py", Args = new[] { "-3.12" } },
                new { Command = "py", Args = new[] { "-3.11" } },
                new { Command = "py", Args = new[] { "-3.10" } },
                new { Command = "python", Args = new string[0] }
            };

            foreach (var candidate in candidates)
            {
                if (TryProbePythonRuntime(candidate.Command, candidate.Args, log, out runtime))
                {
                    return true;
                }
            }

            log("No compatible Python runtime found. Please install Python 3.10-3.13 x64.");
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
                        log($"Ignoring unsupported Python runtime: {trimmed} (requires 3.10-3.13)");
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
        /// Primary location is ~/.ai-debugger/mcp_server (stable path seeded by Install-VSIX.ps1).
        /// </summary>
        public static string GetMcpServerPath()
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            
            // Primary location: ~/.ai-debugger/mcp_server (stable path, seeded by Install-VSIX.ps1)
            var stablePath = Path.Combine(userProfile, ".ai-debugger", "mcp_server", "server.py");
            var normalizedStablePath = Path.GetFullPath(stablePath);
            
            if (File.Exists(normalizedStablePath))
            {
                return normalizedStablePath;
            }
            
            // Fallback locations for development scenarios
            var extensionDir = GetExtensionDirectory();
            var fallbackPaths = new[]
            {
                // Extension directory (bundled with VSIX, if available)
                Path.Combine(extensionDir, "mcp_server", "server.py"),
                // Parent directory (development: running from VSDebuggerMCP/bin/Debug)
                Path.Combine(extensionDir, "..", "..", "..", "mcp_server", "server.py"),
                // Legacy user profile location
                Path.Combine(userProfile, ".vs-debugger-mcp", "mcp_server", "server.py"),
            };

            foreach (var path in fallbackPaths)
            {
                var normalizedPath = Path.GetFullPath(path);
                if (File.Exists(normalizedPath))
                {
                    return normalizedPath;
                }
            }

            // Return the stable path even if it doesn't exist (will show meaningful error)
            return normalizedStablePath;
        }

        /// <summary>
        /// Gets the directory where this extension is installed.
        /// </summary>
        private static string GetExtensionDirectory()
        {
            return Path.GetDirectoryName(typeof(CursorMcpRegistration).Assembly.Location);
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
        /// Registers the AIDebugger server in Cursor's configuration.
        /// </summary>
        /// <returns>True if registration was successful or already registered, false on error.</returns>
        public static RegistrationResult RegisterWithCursor(Action<string> log = null)
        {
            log = log ?? ((s) => { });
            var result = new RegistrationResult();

            try
            {
                var configPath = GetCursorMcpConfigPath();
                var serverPath = GetMcpServerPath();

                if (!TryResolvePythonRuntime(log, out var pythonRuntime))
                {
                    result.Success = false;
                    result.Message = "No compatible Python runtime found. Please install Python 3.10-3.13 x64.";
                    return result;
                }
                
                log($"Cursor MCP config path: {configPath}");
                log($"MCP server path: {serverPath}");
                log($"Selected Python runtime: {pythonRuntime.VersionText} via '{pythonRuntime.Command}'");

                // Ensure the .cursor directory exists
                var cursorDir = Path.GetDirectoryName(configPath);
                if (!Directory.Exists(cursorDir))
                {
                    Directory.CreateDirectory(cursorDir);
                    log($"Created .cursor directory: {cursorDir}");
                }

                // Read existing config or create new one
                Dictionary<string, object> config;
                if (File.Exists(configPath))
                {
                    var existingJson = File.ReadAllText(configPath, Encoding.UTF8);
                    var serializer = new JavaScriptSerializer();
                    config = serializer.Deserialize<Dictionary<string, object>>(existingJson) 
                             ?? new Dictionary<string, object>();
                    log("Read existing mcp.json config");
                }
                else
                {
                    config = new Dictionary<string, object>();
                    log("Creating new mcp.json config");
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
                        { "env", new Dictionary<string, object> { { "AIDB_PREFERRED_IDE", preferredIde } } },
                        { "serverUseInstructions", instructions }
                    };
                    mcpServers[entryName] = serverConfig;
                    allCorrect = false;
                }

                if (mcpServers.ContainsKey(LegacyMcpServerName))
                {
                    log($"Removing legacy MCP server entry '{LegacyMcpServerName}' to keep only VS entry.");
                    mcpServers.Remove(LegacyMcpServerName);
                    allCorrect = false;
                }

                if (allCorrect)
                {
                    log("MCP servers are already correctly registered in Cursor");
                    result.Success = true;
                    result.AlreadyRegistered = true;
                    result.Message = "Already registered";
                    return result;
                }

                // Write updated config
                var serializer2 = new JavaScriptSerializer();
                var json = FormatJson(serializer2.Serialize(config));
                File.WriteAllText(configPath, json, Encoding.UTF8);

                log($"Successfully registered '{McpServerNameVs}' in Cursor MCP config");
                var commandLine = new StringBuilder();
                commandLine.Append(pythonRuntime.Command);
                foreach (var prefixArg in pythonRuntime.PrefixArgs)
                {
                    commandLine.Append(" ");
                    commandLine.Append(prefixArg);
                }
                commandLine.Append(" ");
                commandLine.Append(normalizedPath);
                log($"Server command: {commandLine}");
                
                result.Success = true;
                result.AlreadyRegistered = false;
                result.Message = $"Successfully registered. Please restart Cursor to use the AIDebugger tools.";
                result.ConfigPath = configPath;
                result.ServerPath = serverPath;

                return result;
            }
            catch (Exception ex)
            {
                log($"Error registering with Cursor: {ex.Message}");
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
            log = log ?? ((s) => { });

            try
            {
                var configPath = GetCursorMcpConfigPath();
                
                if (!File.Exists(configPath))
                {
                    log("Cursor MCP config does not exist, nothing to unregister");
                    return true;
                }

                var existingJson = File.ReadAllText(configPath, Encoding.UTF8);
                var serializer = new JavaScriptSerializer();
                var config = serializer.Deserialize<Dictionary<string, object>>(existingJson);

                if (config == null || !config.ContainsKey("mcpServers"))
                {
                    log("No mcpServers in config, nothing to unregister");
                    return true;
                }

                var mcpServers = config["mcpServers"] as Dictionary<string, object>;
                if (mcpServers == null)
                {
                    log("mcpServers is not a valid object, nothing to unregister");
                    return true;
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
                    return true;
                }

                var json = FormatJson(serializer.Serialize(config));
                File.WriteAllText(configPath, json, Encoding.UTF8);

                log($"Successfully unregistered AIDebugger VS MCP server from Cursor MCP config");
                return true;
            }
            catch (Exception ex)
            {
                log($"Error unregistering from Cursor: {ex.Message}");
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
    }

    /// <summary>
    /// Result of MCP registration attempt.
    /// </summary>
    public class RegistrationResult
    {
        public bool Success { get; set; }
        public bool AlreadyRegistered { get; set; }
        public string Message { get; set; }
        public string ConfigPath { get; set; }
        public string ServerPath { get; set; }
    }
}
