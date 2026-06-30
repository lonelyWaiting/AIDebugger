using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Collections.Generic;

namespace VSDebuggerMCP
{
    /// <summary>
    /// Handles automatic registration of the MCP server in MCP client configurations
    /// (Cursor, Codex, Claude Desktop, Claude Code, OpenCode).
    /// </summary>
    public static class McpClientRegistration
    {
        private const string McpServerNameVs = "AIDebuggerVS";
        private const string LegacyMcpServerName = "AIDebugger";
        
        // Client display names for logging
        private const string ClientNameCursor = "Cursor";
        private const string ClientNameCodex = "Codex";
        private const string ClientNameClaudeDesktop = "Claude Desktop";
        private const string ClientNameClaudeCode = "Claude Code";
        private const string ClientNameOpenCode = "OpenCode";
        private const string CursorAllowListSyncScript = "cursor_allowlist_sync.py";
        private const string BundleSignatureFileName = ".bundle-signature";
        private static readonly int[] DefaultSupportedPythonMinorVersions = { 14, 13, 12, 11, 10 };

        /// <summary>
        /// Server use instructions that help Cursor AI understand when and how to use this MCP.
        /// This is critical for AI to automatically invoke debugging tools when users ask about crashes, errors, etc.
        /// </summary>
        private static readonly string ServerUseInstructionsTemplate = @"AIDebugger{0} - {0}调试信息获取工具

## 何时使用此MCP

当用户提到以下关键词或场景时，应主动使用此MCP获取调试信息：

### 崩溃/异常分析（最常见场景）
- 关键词：崩溃、crash、异常、exception、错误、error、闪退、挂起、hang、死循环
- 推荐操作：优先调用 capture_debug_bundle() 获取低阻塞（low-blocking）快照（默认 status/callstack/exception/threads）；只有确实需要时才显式开启 include_locals/include_modules/include_registers/include_output；需要完整线程栈时显式传 thread_max_frames_per_thread=0、thread_max_nodes=0、thread_budget_ms=0 或较大的 thread_budget_ms，并给足 timeout_seconds

### 调试分析
- 关键词：调试、debug、断点、breakpoint、单步、step、堆栈、stack、变量、variable
- 推荐操作：先 get_debug_status 判断 stopReason，再按需 get_callstack/get_locals；多表达式分析优先 evaluate_expressions_batch
- 表达式求值会尝试执行请求并返回 IDE 的明确 value/error/timeout；UE 调试中如果 GetName() 这类函数调用失败或不适合调用，再优先读取字段、地址、FName 原始值或内存进行无调用解析
- 如果不确定该用哪个工具，先调用 debug_capabilities 查看按场景分类的工具索引

### 主动检测调试现场（推荐）
- 在调用任何调试工具前，先调用 detect_debug_sessions(prefer_ide=""{1}"") 检测是否有 VS/Rider 调试现场
- 若自动选择失败，使用 list_ide_instances + select_ide_instance 手动选择

### 主动断点式定位
- 关键词：attach、附加进程、启动调试、start debugging、设置断点、清除断点、watchpoint、数据断点
- 推荐操作：无会话时先 attach_process(process_name=""UnrealEditor.exe"") 或 start_debugging；按位置用 set_breakpoint(file_path,line_number)，按函数用 set_function_breakpoint；需要线程/栈帧切换时用 get_threads/list_threads + select_thread、get_callstack/get_call_stack + select_stack_frame
- 调试控制短名可用：continue、pause、step_over、step_into、step_out；兼容旧名 debug_continue、debug_break、debug_step_over、debug_step_into、debug_step_out；统一入口 debug_control(action) 也可用于 continue/pause/stop/step/detach
- For stop debugging / stop debug / terminate debug session, use debug_stop, stop_debugging, or debug_control(action=""stop""). For Visual Studio detach without terminating the process, use detach_process or debug_control(action=""detach""); Rider detach is not exposed as supported.
- set_breakpoint/set_function_breakpoint 支持 condition、condition_type、hit_count、hit_count_type；clear_breakpoint 是 delete_breakpoint 的别名
- 当前不暴露 logpoint 工具：VS EnvDTE 没有可靠公开的非中断 tracepoint action/message API；需要时改用源码 UE_LOG/OutputDebugString 或条件断点+手动求值

### 编译问题
- 关键词：编译错误、build error、编译失败、无法编译、syntax error、链接错误
- 推荐操作：调用 get_build_errors 获取错误列表

### 内存/底层分析
- 关键词：内存、memory、指针、pointer、地址、address、反汇编、disassembly、寄存器
- 推荐操作：使用 read_memory、get_disassembly、get_registers 等
- No-source native: use resolve_address, get_module_layout, set_address_breakpoint, capture_native_context, and continue_and_capture_native. No write-memory or patching tool is exposed.
- No-source managed: use inspect_managed_assembly, inspect_managed_type, and inspect_managed_method_il to read metadata and decode IL from a target DLL/EXE without source or UI automation.
- No-source hybrid dynamic loop: use get_breakpoint_bindings, capture_hybrid_context, and continue_and_capture_breakpoint to correlate breakpoint binding, native register/disassembly/stack data, and managed thread frames.

## 推荐工作流程

### 崩溃分析标准流程：
1. 调用 capture_debug_bundle 获取低阻塞（low-blocking）稳定上下文（含 snapshotId/partial/snapshotStatus）
2. 根据返回的文件路径，使用 Read 工具读取相关源代码
3. 分析异常信息和堆栈，定位问题根因
4. 如需更多信息，按需开启 include_locals/include_output 或使用 evaluate_expressions_batch；需要完整线程帧时提升 thread_* 预算；仅单条场景再用 evaluate_expression

### 断点调试流程：
1. detect_debug_sessions(prefer_ide=""{1}"") 或 list_vs_instances/select_vs_instance 确认 VS 实例
2. 若没有调试会话，attach_process 或 start_debugging
3. set_breakpoint / set_function_breakpoint / set_data_breakpoint 设置断点或 watchpoint
4. continue_and_wait_for_break 或 continue 放行复现
5. get_callstack/get_threads 后按需 select_stack_frame/select_thread
6. evaluate_expressions_batch 或 evaluate_expression 求值特定表达式

## 重要注意事项

- 支持多个IDE实例，会自动选择处于断点暂停状态的实例
- 如果有多个实例且无法自动选择，使用 list_ide_instances 查看，然后 select_ide_instance 选择
- 调试控制工具（如 debug_step_over）会改变程序状态，需谨慎使用
- 首选 capture_debug_bundle 的低阻塞默认模式作为分析起点；不要默认使用 get_full_context 或完整 include_* 快照";

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

        private class PythonRuntimeCandidate
        {
            public string Command { get; set; }
            public string[] PrefixArgs { get; set; }
        }

        private class WheelsManifest
        {
            public string[] python { get; set; }
        }

        private static bool IsSupportedPythonVersion(int major, int minor, IList<int> supportedMinorVersions)
        {
            return major == 3 && supportedMinorVersions != null && supportedMinorVersions.Contains(minor);
        }

        public static string GetSupportedPythonVersionRequirement(Action<string> log = null)
        {
            return DescribeSupportedPythonVersions(GetSupportedPythonMinorVersions(log));
        }

        private static bool TryResolvePythonRuntime(Action<string> log, out PythonRuntimeInfo runtime)
        {
            runtime = null;
            log = log ?? ((s) => { });
            var supportedMinorVersions = GetSupportedPythonMinorVersions(log);
            var candidates = BuildPythonRuntimeCandidates(supportedMinorVersions);

            foreach (var candidate in candidates)
            {
                if (TryProbePythonRuntime(candidate.Command, candidate.PrefixArgs, supportedMinorVersions, log, out runtime))
                {
                    return true;
                }
            }

            log($"No compatible Python runtime found. Please install {DescribeSupportedPythonVersions(supportedMinorVersions)}.");
            return false;
        }

        private static List<PythonRuntimeCandidate> BuildPythonRuntimeCandidates(IList<int> supportedMinorVersions)
        {
            var candidates = new List<PythonRuntimeCandidate>();

            if (supportedMinorVersions != null)
            {
                foreach (var minor in supportedMinorVersions)
                {
                    candidates.Add(new PythonRuntimeCandidate
                    {
                        Command = "py",
                        PrefixArgs = new[] { $"-3.{minor}" }
                    });
                }
            }

            candidates.Add(new PythonRuntimeCandidate
            {
                Command = "python3",
                PrefixArgs = new string[0]
            });
            candidates.Add(new PythonRuntimeCandidate
            {
                Command = "python",
                PrefixArgs = new string[0]
            });

            return candidates;
        }

        private static bool TryProbePythonRuntime(string command, string[] prefixArgs, IList<int> supportedMinorVersions, Action<string> log, out PythonRuntimeInfo runtime)
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
                    if (!IsSupportedPythonVersion(major, minor, supportedMinorVersions))
                    {
                        log($"Ignoring unsupported Python runtime: {trimmed} (requires {DescribeSupportedPythonVersions(supportedMinorVersions)})");
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

        private static List<int> GetSupportedPythonMinorVersions(Action<string> log)
        {
            log = log ?? ((s) => { });
            var manifestPath = GetBundledMcpServerFilePath(Path.Combine("vendor", "wheels", "manifest.json"));
            if (!File.Exists(manifestPath))
            {
                return new List<int>(DefaultSupportedPythonMinorVersions);
            }

            try
            {
                var serializer = new JavaScriptSerializer();
                var manifest = serializer.Deserialize<WheelsManifest>(File.ReadAllText(manifestPath, Encoding.UTF8));
                if (manifest?.python == null || manifest.python.Length == 0)
                {
                    return new List<int>(DefaultSupportedPythonMinorVersions);
                }

                var versions = new List<int>();
                foreach (var pyTag in manifest.python)
                {
                    if (string.IsNullOrWhiteSpace(pyTag))
                    {
                        continue;
                    }

                    var match = Regex.Match(pyTag.Trim(), @"^cp3(\d{2})$");
                    if (!match.Success)
                    {
                        continue;
                    }

                    var minor = int.Parse(match.Groups[1].Value);
                    if (!versions.Contains(minor))
                    {
                        versions.Add(minor);
                    }
                }

                if (versions.Count == 0)
                {
                    return new List<int>(DefaultSupportedPythonMinorVersions);
                }

                versions.Sort();
                versions.Reverse();
                return versions;
            }
            catch (Exception ex)
            {
                log($"Failed to parse bundled wheel manifest '{manifestPath}': {ex.Message}");
                return new List<int>(DefaultSupportedPythonMinorVersions);
            }
        }

        private static string DescribeSupportedPythonVersions(IList<int> supportedMinorVersions)
        {
            if (supportedMinorVersions == null || supportedMinorVersions.Count == 0)
            {
                return "Python 3.10+ x64";
            }

            var ordered = new List<int>(supportedMinorVersions);
            ordered.Sort();

            var isContiguousRange = true;
            for (int i = 1; i < ordered.Count; i++)
            {
                if (ordered[i] != ordered[i - 1] + 1)
                {
                    isContiguousRange = false;
                    break;
                }
            }

            if (isContiguousRange)
            {
                return $"Python 3.{ordered[0]}-3.{ordered[ordered.Count - 1]} x64";
            }

            var parts = new List<string>();
            foreach (var minor in ordered)
            {
                parts.Add($"3.{minor}");
            }

            return $"Python {string.Join(", ", parts.ToArray())} x64";
        }
        /// <summary>
        /// Gets the path to the MCP server Python script.
        /// Primary location is ~/.ai-debugger/mcp_server (stable path).
        /// If the stable path bundle is stale, refresh it from the VSIX extension directory.
        /// </summary>
        public static string GetMcpServerPath()
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var stablePath = Path.Combine(userProfile, ".ai-debugger", "mcp_server", "server.py");
            var normalizedStablePath = Path.GetFullPath(stablePath);

            if (TryEnsureBundledServerFiles(null, out var syncedServerPath))
            {
                return syncedServerPath;
            }

            if (File.Exists(normalizedStablePath))
            {
                return normalizedStablePath;
            }

            if (TryFindBundledMcpServerSourceDirectory(out var sourceDir))
            {
                return Path.GetFullPath(Path.Combine(sourceDir, "server.py"));
            }

            var legacyPath = Path.Combine(userProfile, ".vs-debugger-mcp", "mcp_server", "server.py");
            var normalizedLegacy = Path.GetFullPath(legacyPath);
            if (File.Exists(normalizedLegacy))
            {
                return normalizedLegacy;
            }

            return normalizedStablePath;
        }

        private static bool TryEnsureBundledServerFiles(Action<string> log, out string serverPath)
        {
            log = log ?? ((s) => { });
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var targetDir = Path.Combine(userProfile, ".ai-debugger", "mcp_server");
            serverPath = Path.GetFullPath(Path.Combine(targetDir, "server.py"));

            if (!TryFindBundledMcpServerSourceDirectory(out var sourceDir))
            {
                return File.Exists(serverPath);
            }

            try
            {
                Directory.CreateDirectory(targetDir);

                var expectedSignature = ComputeDirectorySignature(sourceDir);
                var signaturePath = Path.Combine(targetDir, BundleSignatureFileName);
                var serverFilesInSync =
                    Directory.Exists(targetDir) &&
                    File.Exists(serverPath) &&
                    AllBundledServerFilesExist(sourceDir, targetDir) &&
                    !string.IsNullOrEmpty(expectedSignature) &&
                    File.Exists(signaturePath) &&
                    string.Equals(File.ReadAllText(signaturePath, Encoding.UTF8).Trim(), expectedSignature, StringComparison.OrdinalIgnoreCase);

                if (serverFilesInSync)
                {
                    return true;
                }

                if (Directory.Exists(targetDir))
                {
                    Directory.Delete(targetDir, true);
                }

                CopyDirectoryRecursive(sourceDir, targetDir);
                if (!string.IsNullOrEmpty(expectedSignature))
                {
                    File.WriteAllText(signaturePath, expectedSignature, Encoding.UTF8);
                }

                return File.Exists(serverPath);
            }
            catch (Exception ex)
            {
                log($"Failed to refresh bundled MCP server files: {ex.Message}");
                return File.Exists(serverPath);
            }
        }

        private static bool TryFindBundledMcpServerSourceDirectory(out string sourceDir)
        {
            var extensionDir = GetExtensionDirectory();
            var sourceDirectories = new[]
            {
                Path.Combine(extensionDir, "mcp_server"),
                Path.GetFullPath(Path.Combine(extensionDir, "..", "..", "..", "mcp_server")),
            };

            foreach (var candidate in sourceDirectories)
            {
                if (File.Exists(Path.Combine(candidate, "server.py")))
                {
                    sourceDir = Path.GetFullPath(candidate);
                    return true;
                }
            }

            sourceDir = null;
            return false;
        }

        private static bool AllBundledServerFilesExist(string sourceDir, string targetDir)
        {
            var sourceFiles = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
            foreach (var sourceFile in sourceFiles)
            {
                var relativePath = GetRelativePath(sourceDir, sourceFile);
                if (!File.Exists(Path.Combine(targetDir, relativePath)))
                {
                    return false;
                }
            }

            return true;
        }

        private static string ComputeDirectorySignature(string sourceDir)
        {
            var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            using (var sha = SHA256.Create())
            {
                foreach (var file in files)
                {
                    AppendHashBytes(sha, Encoding.UTF8.GetBytes(GetRelativePath(sourceDir, file).Replace("\\", "/")));
                    AppendHashBytes(sha, new byte[] { (byte)'\n' });

                    using (var stream = File.OpenRead(file))
                    {
                        var buffer = new byte[81920];
                        int read;
                        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            sha.TransformBlock(buffer, 0, read, null, 0);
                        }
                    }

                    AppendHashBytes(sha, new byte[] { (byte)'\n' });
                }

                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private static void AppendHashBytes(HashAlgorithm hash, byte[] bytes)
        {
            hash.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        private static string GetRelativePath(string rootPath, string fullPath)
        {
            var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(fullPath);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFileName(full);
            }

            return full.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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

            if (TryEnsureBundledServerFiles(null, out var stableServerPath))
            {
                var syncedPath = Path.Combine(Path.GetDirectoryName(stableServerPath), relativePath);
                if (File.Exists(syncedPath))
                {
                    return Path.GetFullPath(syncedPath);
                }
            }

            if (File.Exists(normalizedStablePath))
            {
                return normalizedStablePath;
            }

            if (TryFindBundledMcpServerSourceDirectory(out var sourceDir))
            {
                var bundledPath = Path.GetFullPath(Path.Combine(sourceDir, relativePath));
                if (File.Exists(bundledPath))
                {
                    return bundledPath;
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
            var result = RegisterToConfig(configPath, ClientNameCursor, log, createDirIfMissing: true);
            if (result.Success)
            {
                TrySyncCursorAllowList(McpServerNameVs, log);
            }
            return result;
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
                    Directory.CreateDirectory(openCodeDir);
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

                WithConfigFileLock(configPath, log, () =>
                {
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

                    var normalizedPath = serverPath.Replace("\\", "/");
                    var entryName = McpServerNameVs;
                    var preferredIde = "vs";

                    bool allCorrect = true;
                    bool isCorrect = false;
                    if (mcpServers.ContainsKey(entryName))
                    {
                        var existingConfig = mcpServers[entryName] as Dictionary<string, object>;
                        isCorrect = IsCorrectClaudeCodeConfig(existingConfig, pythonRuntime, normalizedPath, preferredIde);
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
                            { "type", "stdio" },
                            { "command", pythonRuntime.Command },
                            { "args", commandArgs.ToArray() },
                            { "env", new Dictionary<string, object> { { "AIDB_PREFERRED_IDE", preferredIde }, { "PYTHONIOENCODING", "utf-8" } } }
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
                        log("MCP server already correctly registered");
                        result.Success = true;
                        result.AlreadyRegistered = true;
                        result.Message = "Already registered";
                        return;
                    }

                    var serializer2 = new JavaScriptSerializer();
                    var json = FormatJson(serializer2.Serialize(config));
                    File.WriteAllText(configPath, json, Encoding.UTF8);

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

                WithConfigFileLock(configPath, log, () =>
                {
                    var existingToml = File.Exists(configPath)
                        ? File.ReadAllText(configPath, Encoding.UTF8)
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
                    File.WriteAllText(configPath, updatedToml, new UTF8Encoding(false));

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
                    var existingToml = File.ReadAllText(configPath, Encoding.UTF8);
                    var updatedToml = RemoveTomlSection(existingToml, $"mcp_servers.{McpServerNameVs}.env");
                    updatedToml = RemoveTomlSection(updatedToml, $"mcp_servers.{McpServerNameVs}");
                    updatedToml = RemoveTomlSection(updatedToml, $"mcp_servers.{LegacyMcpServerName}.env");
                    updatedToml = RemoveTomlSection(updatedToml, $"mcp_servers.{LegacyMcpServerName}");

                    if (NormalizeLineEndings(existingToml).Trim() == NormalizeLineEndings(updatedToml).Trim())
                    {
                        log("No AIDebugger MCP servers found in config, nothing to unregister");
                        return;
                    }

                    File.WriteAllText(configPath, updatedToml.TrimEnd() + Environment.NewLine, new UTF8Encoding(false));
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

        private static string BuildCodexTomlEntry(string entryName, PythonRuntimeInfo runtime, string normalizedPath, string preferredIde)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[mcp_servers.{entryName}]");
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

            return Regex.IsMatch(
                content,
                @"(?m)^[ \t]*\[" + Regex.Escape(sectionName) + @"\][^\r\n]*$");
        }

        private static string RemoveTomlSection(string content, string sectionName)
        {
            if (string.IsNullOrEmpty(content))
            {
                return string.Empty;
            }

            var pattern = @"(?ms)^[ \t]*\[" + Regex.Escape(sectionName) + @"\][^\r\n]*\r?\n(?:^(?![ \t]*\[).*\r?\n?)*";
            return Regex.Replace(content, pattern, string.Empty);
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
