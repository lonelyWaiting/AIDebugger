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
- 推荐操作：首先调用 get_full_context 或 get_exception + get_callstack 获取完整的崩溃现场

### 调试分析
- 关键词：调试、debug、断点、breakpoint、单步、step、堆栈、stack、变量、variable
- 推荐操作：根据需求使用 get_debug_status、get_callstack、get_locals 等

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
1. 调用 get_full_context 一次性获取所有上下文（状态、堆栈、变量、异常、线程）
2. 根据返回的文件路径，使用 Read 工具读取相关源代码
3. 分析异常信息和堆栈，定位问题根因
4. 如需更多信息，使用 evaluate_expression 检查特定变量或表达式

### 断点调试流程：
1. get_debug_status 确认调试状态
2. get_callstack 查看调用链
3. get_locals 查看当前变量
4. evaluate_expression 求值特定表达式

## 重要注意事项

- 支持多个IDE实例，会自动选择处于断点暂停状态的实例
- 如果有多个实例且无法自动选择，使用 list_ide_instances 查看，然后 select_ide_instance 选择
- 调试控制工具（如 debug_step_over）会改变程序状态，需谨慎使用
- get_full_context 是最高效的起点，一次调用获取全部信息";

        private static string BuildServerUseInstructions(string ideLabel, string preferredIde)
        {
            return string.Format(ServerUseInstructionsTemplate, ideLabel, preferredIde);
        }
        
        /// <summary>
        /// Gets the path to the MCP server Python script.
        /// The Python files are bundled with the VSIX extension.
        /// </summary>
        public static string GetMcpServerPath()
        {
            // The MCP server files are bundled in the VSIX and installed alongside the extension
            var extensionDir = GetExtensionDirectory();
            
            // Primary location: mcp_server folder in the extension directory (bundled with VSIX)
            var bundledPath = Path.Combine(extensionDir, "mcp_server", "server.py");
            var normalizedBundledPath = Path.GetFullPath(bundledPath);
            
            if (File.Exists(normalizedBundledPath))
            {
                return normalizedBundledPath;
            }
            
            // Fallback locations for development scenarios
            var fallbackPaths = new[]
            {
                // Parent directory (development: running from VSDebuggerMCP/bin/Debug)
                Path.Combine(extensionDir, "..", "..", "..", "mcp_server", "server.py"),
                // User profile location (manual installation)
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ai-debugger", "mcp_server", "server.py"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vs-debugger-mcp", "mcp_server", "server.py"),
            };

            foreach (var path in fallbackPaths)
            {
                var normalizedPath = Path.GetFullPath(path);
                if (File.Exists(normalizedPath))
                {
                    return normalizedPath;
                }
            }

            // Return the bundled path even if it doesn't exist (will show meaningful error)
            return normalizedBundledPath;
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
                
                log($"Cursor MCP config path: {configPath}");
                log($"MCP server path: {serverPath}");

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
                    isCorrect = IsCorrectServerConfig(existingConfig, normalizedPath, preferredIde);
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
                    var serverConfig = new Dictionary<string, object>
                    {
                        { "command", "python" },
                        { "args", new object[] { normalizedPath } },
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
                log($"Server command: python {normalizedPath}");
                
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
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null) return false;
                    var output = process.StandardOutput.ReadToEnd();
                    var error = process.StandardError.ReadToEnd();
                    process.WaitForExit(5000);
                    
                    var version = string.IsNullOrEmpty(output) ? error : output;
                    if (process.ExitCode == 0)
                    {
                        var trimmed = version.Trim();
                        var match = Regex.Match(trimmed, @"Python\s+(\d+)\.(\d+)\.(\d+)");
                        if (match.Success)
                        {
                            var major = int.Parse(match.Groups[1].Value);
                            var minor = int.Parse(match.Groups[2].Value);
                            if (major > 3 || (major == 3 && minor >= 10))
                            {
                                log($"Python found: {trimmed}");
                                return true;
                            }
                            log($"Python version {major}.{minor} is not supported. Please install Python 3.10+.");
                            return false;
                        }

                        log($"Python found but version could not be parsed: {trimmed}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                log($"Python not found: {ex.Message}");
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

        private static bool IsCorrectServerConfig(Dictionary<string, object> existingConfig, string normalizedPath, string preferredIde)
        {
            if (existingConfig == null)
            {
                return false;
            }

            var existingCommand = existingConfig.ContainsKey("command") ? existingConfig["command"]?.ToString() : "";
            if (existingCommand != "python")
            {
                return false;
            }

            var existingArgs = existingConfig.ContainsKey("args") ? existingConfig["args"] : null;
            string firstArg = null;
            if (existingArgs is object[] argsArray && argsArray.Length > 0)
            {
                firstArg = argsArray[0]?.ToString();
            }
            else if (existingArgs is System.Collections.ArrayList argsList && argsList.Count > 0)
            {
                firstArg = argsList[0]?.ToString();
            }

            if (!string.Equals(firstArg, normalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
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
