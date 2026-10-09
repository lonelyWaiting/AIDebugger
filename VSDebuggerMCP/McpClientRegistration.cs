using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Collections.Generic;
using Microsoft.Win32;
using VSDebuggerMCP.Ipc;

namespace VSDebuggerMCP
{
    /// <summary>
    /// Handles explicit, user-invoked registration of the MCP server in MCP client configurations
    /// (Cursor, Codex, Claude Desktop, Claude Code, OpenCode).
    /// </summary>
    public static partial class McpClientRegistration
    {
        private const string McpServerNameVs = "AIDebuggerVS";
        private const string LegacyMcpServerName = "AIDebugger";
        
        // Client display names for logging
        private const string ClientNameCursor = "Cursor";
        private const string ClientNameCodex = "Codex";
        private const string ClientNameClaudeDesktop = "Claude Desktop";
        private const string ClientNameClaudeCode = "Claude Code";
        private const string ClientNameOpenCode = "OpenCode";
        private const string BundleSignatureFileName = ".bundle-signature";
        private const string SourceLauncherFileName = "vs_launcher.py";
        private const string StableServerEntryFileName = "vs_server.py";
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
- 推荐操作：优先调用 capture_debug_bundle() 获取低阻塞（low-blocking）快照（默认 status/callstack/threads）；异常信息需显式 include_exception=true 并获得 debug.evaluate.unsafe；只有确实需要时才显式开启 include_locals/include_modules/include_registers/include_output；需要完整线程栈时显式传 thread_max_frames_per_thread=0、thread_max_nodes=0、thread_budget_ms=0 或较大的 thread_budget_ms，并给足 timeout_seconds

### 调试分析
- 关键词：调试、debug、断点、breakpoint、单步、step、堆栈、stack、变量、variable
- 推荐操作：先 get_debug_status 判断 stopReason，再按需 get_callstack/get_locals；多表达式分析优先 evaluate_expressions_batch
- 表达式求值会尝试执行请求并返回 IDE 的明确 value/error/timeout；UE 调试中如果 GetName() 这类函数调用失败或不适合调用，再优先读取字段、地址、FName 原始值或内存进行无调用解析
- 如果不确定该用哪个工具，先调用 debug_capabilities 查看按场景分类的工具索引

### 主动检测调试现场（推荐）
- 在调用任何调试工具前，先调用 detect_debug_sessions() 检测是否有 Visual Studio 调试现场
- 若自动选择失败，使用 list_vs_instances + select_vs_instance 手动选择
- 若需同时查询多个已认证 VS，使用 dispatch_vs_instances(method=""capture_debug_bundle_safe"", process_ids=[...])；它不会改变当前选择。命令型 method 必须显式传 allow_commands=true

### 主动断点式定位
- 关键词：attach、附加进程、启动调试、start debugging、设置断点、清除断点、watchpoint、数据断点
- 推荐操作：无会话时先 attach_process(process_name=""UnrealEditor.exe"") 或 start_debugging；按位置用 set_breakpoint(file_path,line_number)，按函数用 set_function_breakpoint；需要线程/栈帧切换时用 get_threads + switch_thread、get_callstack + switch_stack_frame
- 默认只暴露 canonical 工具名：debug_continue、debug_break、debug_step_over、debug_step_into、debug_step_out；统一入口 debug_control(action) 可用于 continue/break/stop/step/detach
- For stop debugging / terminate debug session, use debug_stop or debug_control(action=""stop""). To detach without terminating the process, use detach_process or debug_control(action=""detach"").
- For a multi-process Visual Studio debug session, call get_debug_processes first, then switch_process(process_handle=...) while paused. The opaque handle binds session generation, PID, and OS creation time; it never attaches a new local process.
- set_breakpoint/set_function_breakpoint 支持 condition、condition_type、hit_count、hit_count_type；删除断点使用 delete_breakpoint
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
1. detect_debug_sessions() 或 list_vs_instances/select_vs_instance 确认 VS 实例
2. 若没有调试会话，attach_process 或 start_debugging
3. set_breakpoint / set_function_breakpoint / set_data_breakpoint 设置断点或 watchpoint
4. continue_and_wait_for_break 或 debug_continue 放行复现
5. get_callstack/get_threads 后按需 switch_stack_frame/switch_thread
6. evaluate_expressions_batch 或 evaluate_expression 求值特定表达式

## 重要注意事项

- 支持多个 Visual Studio 实例，会自动选择处于断点暂停状态的实例
- 如果有多个实例且无法自动选择，使用 list_vs_instances 查看，然后 select_vs_instance 选择
- 同时采集多个 VS 时使用 dispatch_vs_instances；显式 PID 列表采用精确目标路由，不会覆盖单实例选择
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
            public string platform { get; set; }
            public WheelManifestEntry[] wheels { get; set; }
        }

        private class WheelManifestEntry
        {
            public string file { get; set; }
            public string sha256 { get; set; }
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
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using (RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view))
                        using (RegistryKey core = baseKey.OpenSubKey(@"SOFTWARE\Python\PythonCore"))
                        {
                            if (core == null) continue;
                            foreach (string versionName in core.GetSubKeyNames())
                            {
                                if (!TryParseSupportedRegistryVersion(
                                    versionName, supportedMinorVersions, out _, out _)) continue;
                                using (RegistryKey install = core.OpenSubKey(versionName + @"\InstallPath"))
                                {
                                    if (install == null) continue;
                                    string executable = Convert.ToString(install.GetValue("ExecutablePath"));
                                    if (string.IsNullOrWhiteSpace(executable))
                                    {
                                        string directory = Convert.ToString(install.GetValue(null));
                                        if (!string.IsNullOrWhiteSpace(directory))
                                            executable = Path.Combine(directory, "python.exe");
                                    }
                                    AddAbsolutePythonCandidate(candidates, seen, executable);
                                }
                            }
                        }
                    }
                    catch (Exception ex) when (
                        ex is UnauthorizedAccessException || ex is IOException || ex is System.Security.SecurityException)
                    {
                        // An inaccessible registry view is not a runtime candidate.
                    }
                }
            }
            return candidates;
        }

        private static bool TryParseSupportedRegistryVersion(
            string versionName,
            IList<int> supportedMinorVersions,
            out int major,
            out int minor)
        {
            major = 0;
            minor = 0;
            string[] parts = (versionName ?? string.Empty).Split('.');
            return parts.Length >= 2 && int.TryParse(parts[0], out major) &&
                int.TryParse(parts[1], out minor) &&
                IsSupportedPythonVersion(major, minor, supportedMinorVersions);
        }

        private static void AddAbsolutePythonCandidate(
            List<PythonRuntimeCandidate> candidates,
            HashSet<string> seen,
            string executable)
        {
            if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathRooted(executable)) return;
            string fullPath;
            try { fullPath = Path.GetFullPath(executable); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return;
            }
            if (!IsPlainPythonExecutable(fullPath) || !seen.Add(fullPath)) return;
            if (IsCurrentProcessElevated() && !IsTrustedForElevatedSetup(fullPath)) return;
            candidates.Add(new PythonRuntimeCandidate
            {
                Command = fullPath,
                PrefixArgs = new string[0],
            });
        }

        private static bool IsPlainPythonExecutable(string executable)
        {
            try
            {
                if (!Path.IsPathRooted(executable) ||
                    !string.Equals(Path.GetFileName(executable), "python.exe", StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(executable) ||
                    (File.GetAttributes(executable) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    return false;
                DirectoryInfo directory = new FileInfo(executable).Directory;
                while (directory != null)
                {
                    if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                    directory = directory.Parent;
                }
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                return false;
            }
        }

        private static bool IsCurrentProcessElevated()
        {
            return ProcessIntegrityValidator.IsCurrentProcessElevated();
        }

        private static bool IsTrustedForElevatedSetup(string executable)
        {
            string fullPath = Path.GetFullPath(executable);
            bool protectedLocation = IsUnderDirectory(fullPath, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)) ||
                IsUnderDirectory(fullPath, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)) ||
                IsUnderDirectory(fullPath, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            if (!protectedLocation) return false;
            return AuthenticodeTrustValidator.IsTrustedFileSignedBy(
                fullPath, "Python Software Foundation");
        }

        private static bool IsUnderDirectory(string path, string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) return false;
            string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryProbePythonRuntime(string command, string[] prefixArgs, IList<int> supportedMinorVersions, Action<string> log, out PythonRuntimeInfo runtime)
        {
            runtime = null;
            log = log ?? ((s) => { });

            try
            {
                var argsBuilder = new StringBuilder();
                if (!Path.IsPathRooted(command) || !IsPlainPythonExecutable(command))
                {
                    log("Rejected a non-absolute or reparse-point Python runtime candidate.");
                    return false;
                }
                if (prefixArgs != null && prefixArgs.Length > 0)
                {
                    argsBuilder.Append(string.Join(" ", prefixArgs));
                    argsBuilder.Append(" ");
                }
                const string probeScript = "import json,struct,sys,sysconfig;print(json.dumps({'version':list(sys.version_info[:3]),'bits':struct.calcsize('P')*8,'platform':sysconfig.get_platform(),'executable':sys.executable}))";
                argsBuilder.Append("-I -S -c ");
                argsBuilder.Append(QuoteProcessArgument(probeScript));

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

                    var outputBuilder = new StringBuilder();
                    var errorBuilder = new StringBuilder();
                    using (var outputClosed = new ManualResetEvent(false))
                    using (var errorClosed = new ManualResetEvent(false))
                    {
                        process.OutputDataReceived += (_, eventArgs) =>
                        {
                            if (eventArgs.Data == null) outputClosed.Set();
                            else outputBuilder.AppendLine(eventArgs.Data);
                        };
                        process.ErrorDataReceived += (_, eventArgs) =>
                        {
                            if (eventArgs.Data == null) errorClosed.Set();
                            else errorBuilder.AppendLine(eventArgs.Data);
                        };
                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();

                        if (!process.WaitForExit(5000))
                        {
                            try
                            {
                                process.Kill();
                                process.WaitForExit(1000);
                            }
                            catch { /* best-effort cleanup of a hung runtime probe */ }
                            log("Python runtime check timed out after 5 seconds.");
                            return false;
                        }
                        if (!outputClosed.WaitOne(1000) || !errorClosed.WaitOne(1000))
                        {
                            log("Python runtime output did not close cleanly.");
                            return false;
                        }
                    }

                    var output = outputBuilder.ToString();
                    var error = errorBuilder.ToString();

                    if (process.ExitCode != 0)
                    {
                        return false;
                    }

                    var serializer = new JavaScriptSerializer();
                    var probe = serializer.Deserialize<Dictionary<string, object>>(output.Trim());
                    if (probe == null || !probe.ContainsKey("version") || !probe.ContainsKey("bits") ||
                        !probe.ContainsKey("platform") || !probe.ContainsKey("executable"))
                    {
                        log("Python runtime check returned an incomplete identity response.");
                        return false;
                    }

                    string launchedImage;
                    try { launchedImage = Path.GetFullPath(process.MainModule.FileName); }
                    catch
                    {
                        try { process.Kill(); } catch { }
                        return false;
                    }
                    if (!string.Equals(launchedImage, Path.GetFullPath(command), StringComparison.OrdinalIgnoreCase))
                    {
                        try { process.Kill(); } catch { }
                        log("Python runtime probe launched an unexpected executable image.");
                        return false;
                    }

                    var version = probe["version"] as System.Collections.IList;
                    if (version == null || version.Count < 3)
                    {
                        log("Python runtime check returned an invalid version tuple.");
                        return false;
                    }
                    var major = Convert.ToInt32(version[0]);
                    var minor = Convert.ToInt32(version[1]);
                    var patch = Convert.ToInt32(version[2]);
                    if (!IsSupportedPythonVersion(major, minor, supportedMinorVersions))
                    {
                        log($"Ignoring unsupported Python {major}.{minor} runtime (requires {DescribeSupportedPythonVersions(supportedMinorVersions)}).");
                        return false;
                    }
                    var bits = Convert.ToInt32(probe["bits"]);
                    var platform = Convert.ToString(probe["platform"]);
                    var executable = Convert.ToString(probe["executable"]);
                    if (bits != 64 || !string.Equals(platform, "win_amd64", StringComparison.OrdinalIgnoreCase))
                    {
                        log($"Ignoring non-x64 Windows Python runtime ({bits}-bit, platform '{platform}').");
                        return false;
                    }
                    if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable) ||
                        !string.Equals(Path.GetFullPath(executable), Path.GetFullPath(command), StringComparison.OrdinalIgnoreCase))
                    {
                        log("Python runtime check did not return a valid executable path.");
                        return false;
                    }

                    runtime = new PythonRuntimeInfo
                    {
                        Command = Path.GetFullPath(executable),
                        // -I excludes PYTHONPATH, the current directory and user site;
                        // -S additionally prevents global site/.pth execution. The
                        // verified launcher inserts only the authenticated bundle.
                        PrefixArgs = new[] { "-I", "-S" },
                        VersionText = $"Python {major}.{minor}.{patch} x64"
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
        /// Each VSIX content/version deploys to an immutable versioned directory so an
        /// older VS instance cannot overwrite the runtime selected by a newer instance.
        /// </summary>
        public static string GetMcpServerPath()
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var stablePath = Path.Combine(userProfile, ".ai-debugger", "mcp_server_vs", StableServerEntryFileName);
            var normalizedStablePath = Path.GetFullPath(stablePath);

            if (TryEnsureBundledServerFiles(null, out var syncedServerPath, returnStableEntry: true))
            {
                return syncedServerPath;
            }

            throw new InvalidOperationException(
                $"The current VS MCP server bundle could not be deployed and validated at {normalizedStablePath}. " +
                "Existing stale payloads are not used for client registration.");
        }

        private static bool TryEnsureBundledServerFiles(Action<string> log, out string serverPath, bool returnStableEntry = false)
        {
            log = log ?? ((s) => { });
            using (var mutex = new Mutex(false, "Local\\AIDebugger_VSBundleDeployment"))
            {
                bool acquired = false;
                try
                {
                    try
                    {
                        acquired = mutex.WaitOne(TimeSpan.FromSeconds(30));
                    }
                    catch (AbandonedMutexException)
                    {
                        acquired = true;
                    }

                    if (!acquired)
                    {
                        serverPath = null;
                        log("Timed out waiting for another Visual Studio instance to finish deploying the MCP bundle.");
                        return false;
                    }

                    if (!TryEnsureBundledServerFilesCore(log, out serverPath))
                    {
                        return false;
                    }
                    if (returnStableEntry)
                    {
                        serverPath = EnsureStableLauncher(serverPath);
                    }
                    return true;
                }
                finally
                {
                    if (acquired)
                    {
                        try { mutex.ReleaseMutex(); }
                        catch (ApplicationException) { /* mutex ownership was lost */ }
                    }
                }
            }
        }

        private static string QuoteProcessArgument(string value)
        {
            if (value == null) return "\"\"";
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static bool TryEnsureBundledServerFilesCore(Action<string> log, out string serverPath)
        {
            serverPath = null;
            log = log ?? ((s) => { });
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (!TryFindBundledMcpServerSourceDirectory(out var sourceDir))
            {
                serverPath = null;
                log("The current extension's bundled MCP server source directory was not found.");
                return false;
            }

            try
            {
                var expectedSignature = ComputeDirectorySignature(sourceDir);
                if (string.IsNullOrEmpty(expectedSignature))
                {
                    serverPath = null;
                    log("The current extension's MCP server bundle signature could not be computed.");
                    return false;
                }
                string bundleVersion = typeof(McpClientRegistration).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
                string bundleIdentity = bundleVersion.Replace('.', '_') + "_" + expectedSignature.Substring(0, 16);
                var aidbRoot = BundlePathPolicy.EnsurePlainChildDirectory(userProfile, ".ai-debugger");
                var bundleRoot = BundlePathPolicy.EnsurePlainChildDirectory(aidbRoot, "mcp_server_vs");
                var targetDir = Path.Combine(bundleRoot, bundleIdentity);
                serverPath = Path.GetFullPath(Path.Combine(targetDir, "vs_server.py"));
                Directory.CreateDirectory(targetDir);

                var signaturePath = Path.Combine(targetDir, BundleSignatureFileName);
                var versionPath = Path.Combine(targetDir, ".bundle-version");
                var serverFilesInSync =
                    Directory.Exists(targetDir) &&
                    BundlePathPolicy.IsValidVsBundleTree(targetDir, true) &&
                    File.Exists(serverPath) &&
                    AllBundledServerFilesExist(sourceDir, targetDir) &&
                    !string.IsNullOrEmpty(expectedSignature) &&
                    File.Exists(signaturePath) &&
                    File.Exists(versionPath) &&
                    string.Equals(File.ReadAllText(versionPath, Encoding.UTF8).Trim(), bundleVersion, StringComparison.Ordinal) &&
                    string.Equals(File.ReadAllText(signaturePath, Encoding.UTF8).Trim(), expectedSignature, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(ComputeDirectorySignature(targetDir), expectedSignature, StringComparison.Ordinal) &&
                    ValidateWheelManifest(targetDir);

                if (serverFilesInSync)
                {
                    return true;
                }

                var bundleParent = Path.GetDirectoryName(Path.GetFullPath(targetDir));
                var updateId = Guid.NewGuid().ToString("N");
                var stagingDir = Path.Combine(bundleParent, ".mcp_server_vs." + bundleIdentity + "." + updateId + ".staging");
                var backupDir = Path.Combine(bundleParent, ".mcp_server_vs." + bundleIdentity + "." + updateId + ".backup");
                CopyVsBundleFiles(sourceDir, stagingDir);
                if (!string.IsNullOrEmpty(expectedSignature))
                {
                    WriteTextAtomically(
                        Path.Combine(stagingDir, BundleSignatureFileName),
                        expectedSignature,
                        new UTF8Encoding(false));
                    WriteTextAtomically(
                        Path.Combine(stagingDir, ".bundle-version"),
                        bundleVersion,
                        new UTF8Encoding(false));
                }

                if (!File.Exists(Path.Combine(stagingDir, "vs_server.py")) ||
                    !BundlePathPolicy.IsValidVsBundleTree(stagingDir, true) ||
                    !AllBundledServerFilesExist(sourceDir, stagingDir) ||
                    !string.Equals(ComputeDirectorySignature(stagingDir), expectedSignature, StringComparison.Ordinal) ||
                    !ValidateWheelManifest(stagingDir))
                {
                    DeleteOwnedBundleDirectory(stagingDir, bundleParent);
                    throw new InvalidOperationException("Staged VS MCP server bundle failed validation");
                }

                try
                {
                    if (Directory.Exists(targetDir))
                    {
                        Directory.Move(targetDir, backupDir);
                    }
                    Directory.Move(stagingDir, targetDir);
                }
                catch
                {
                    if (!Directory.Exists(targetDir) && Directory.Exists(backupDir))
                    {
                        Directory.Move(backupDir, targetDir);
                    }
                    throw;
                }
                finally
                {
                    DeleteOwnedBundleDirectory(stagingDir, bundleParent);
                }

                DeleteOwnedBundleDirectory(backupDir, bundleParent);

                return File.Exists(serverPath) &&
                    BundlePathPolicy.IsValidVsBundleTree(targetDir, true) &&
                    AllBundledServerFilesExist(sourceDir, targetDir) &&
                    File.Exists(Path.Combine(targetDir, ".bundle-version")) &&
                    string.Equals(File.ReadAllText(Path.Combine(targetDir, ".bundle-version"), Encoding.UTF8).Trim(), bundleVersion, StringComparison.Ordinal) &&
                    string.Equals(ComputeDirectorySignature(targetDir), expectedSignature, StringComparison.Ordinal) &&
                    ValidateWheelManifest(targetDir);
            }
            catch (Exception ex)
            {
                log($"Failed to refresh bundled MCP server files: {ex.Message}");
                return false;
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
                if (File.Exists(Path.Combine(candidate, "vs_server.py")))
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
            var sourceFiles = GetVsBundleFiles(sourceDir);
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
            return BundleSignature.Compute(sourceDir, GetVsBundleFiles(sourceDir));
        }

        private static string EnsureStableLauncher(string deployedServerPath)
        {
            string bundleDir = Path.GetDirectoryName(Path.GetFullPath(deployedServerPath));
            ActivateBundleWithoutDowngrade(bundleDir);
            string root = Path.GetDirectoryName(bundleDir);
            string activeMarker = Path.Combine(root, ".active-bundle");
            if (!BundlePathPolicy.IsPlainFile(activeMarker))
            {
                throw new InvalidOperationException("The active VS MCP bundle marker is not a plain file.");
            }
            string activeName = File.ReadAllText(activeMarker, Encoding.UTF8).Trim();
            string activeDir = Path.GetFullPath(Path.Combine(root, activeName));
            if (!string.Equals(Path.GetDirectoryName(activeDir), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) ||
                !IsValidDeployedBundle(activeDir))
            {
                throw new InvalidOperationException("The active VS MCP bundle failed validation.");
            }
            string sourceLauncher = Path.Combine(activeDir, SourceLauncherFileName);
            if (!File.Exists(sourceLauncher))
            {
                throw new InvalidOperationException("The deployed VS MCP bundle is missing its stable launcher.");
            }
            string targetLauncher = Path.Combine(root, StableServerEntryFileName);
            int sourceVersion = ReadLauncherVersion(sourceLauncher);
            if (sourceVersion < 0)
            {
                throw new InvalidOperationException("The active VS MCP bundle has an invalid launcher.");
            }
            if (!BundlePathPolicy.IsPlainFile(targetLauncher) ||
                !FilesHaveSameSha256(sourceLauncher, targetLauncher))
            {
                WriteBytesAtomicallyNoBackup(targetLauncher, File.ReadAllBytes(sourceLauncher));
            }
            if (!BundlePathPolicy.IsPlainFile(targetLauncher) ||
                ReadLauncherVersion(targetLauncher) != sourceVersion ||
                !FilesHaveSameSha256(sourceLauncher, targetLauncher))
            {
                throw new InvalidOperationException("The stable VS MCP launcher failed validation.");
            }
            return Path.GetFullPath(targetLauncher);
        }

        private static bool FilesHaveSameSha256(string left, string right)
        {
            try
            {
                using (var sha = SHA256.Create())
                using (var leftStream = File.OpenRead(left))
                using (var rightStream = File.OpenRead(right))
                {
                    byte[] leftHash = sha.ComputeHash(leftStream);
                    sha.Initialize();
                    byte[] rightHash = sha.ComputeHash(rightStream);
                    if (leftHash.Length != rightHash.Length) return false;
                    int difference = 0;
                    for (int index = 0; index < leftHash.Length; index++) difference |= leftHash[index] ^ rightHash[index];
                    return difference == 0;
                }
            }
            catch { return false; }
        }

        private static void ActivateBundleWithoutDowngrade(string bundleDir)
        {
            string root = Path.GetDirectoryName(bundleDir);
            string marker = Path.Combine(root, ".active-bundle");
            string candidateName = Path.GetFileName(bundleDir);
            Version candidateVersion = ReadBundleVersion(bundleDir);
            if (candidateVersion == null)
            {
                throw new InvalidOperationException("The deployed VS MCP bundle has an invalid version marker.");
            }

            Version activeVersion = null;
            if (BundlePathPolicy.IsPlainFile(marker))
            {
                string activeName = File.ReadAllText(marker, Encoding.UTF8).Trim();
                if (Regex.IsMatch(activeName, @"^[0-9_]+_[0-9a-f]{16}$"))
                {
                    string activeDir = Path.GetFullPath(Path.Combine(root, activeName));
                    if (string.Equals(Path.GetDirectoryName(activeDir), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                    {
                        activeVersion = IsValidDeployedBundle(activeDir) ? ReadBundleVersion(activeDir) : null;
                    }
                }
            }
            if (activeVersion == null || candidateVersion >= activeVersion)
            {
                WriteBytesAtomicallyNoBackup(marker, new UTF8Encoding(false).GetBytes(candidateName));
            }
            CleanupInactiveBundles(root);
        }

        private static Version ReadBundleVersion(string bundleDir)
        {
            try
            {
                Version version;
                return Version.TryParse(File.ReadAllText(Path.Combine(bundleDir, ".bundle-version"), Encoding.UTF8).Trim(), out version)
                    ? version
                    : null;
            }
            catch { return null; }
        }

        private static bool IsValidDeployedBundle(string bundleDir)
        {
            try
            {
                if (!Directory.Exists(bundleDir) ||
                    !File.Exists(Path.Combine(bundleDir, "vs_server.py")) ||
                    !File.Exists(Path.Combine(bundleDir, SourceLauncherFileName)))
                {
                    return false;
                }
                string signature = File.ReadAllText(Path.Combine(bundleDir, BundleSignatureFileName), Encoding.UTF8).Trim();
                Version version = ReadBundleVersion(bundleDir);
                string expectedName = version == null
                    ? null
                    : version.ToString().Replace('.', '_') + "_" + signature.Substring(0, Math.Min(16, signature.Length));
                if (version == null || !Regex.IsMatch(signature, @"^[0-9a-fA-F]{64}$") ||
                    !string.Equals(Path.GetFileName(bundleDir), expectedName, StringComparison.Ordinal))
                {
                    return false;
                }
                return BundlePathPolicy.IsValidVsBundleTree(bundleDir, true) &&
                    string.Equals(ComputeDirectorySignature(bundleDir), signature, StringComparison.OrdinalIgnoreCase) &&
                    ValidateWheelManifest(bundleDir);
            }
            catch { return false; }
        }

        private static void CleanupInactiveBundles(string bundleRoot)
        {
            try
            {
                string root = Path.GetFullPath(bundleRoot).TrimEnd(Path.DirectorySeparatorChar);
                string marker = Path.Combine(root, ".active-bundle");
                string activeName = BundlePathPolicy.IsPlainFile(marker)
                    ? File.ReadAllText(marker, Encoding.UTF8).Trim()
                    : string.Empty;
                var inactive = new List<DirectoryInfo>();
                foreach (string directory in Directory.GetDirectories(root))
                {
                    var info = new DirectoryInfo(directory);
                    if (!Regex.IsMatch(info.Name, @"^[0-9_]+_[0-9a-f]{16}$") ||
                        string.Equals(info.Name, activeName, StringComparison.Ordinal) ||
                        (info.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }
                    inactive.Add(info);
                }
                inactive.Sort((left, right) => right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc));
                DateTime cutoff = DateTime.UtcNow.AddDays(-30);
                for (int index = 3; index < inactive.Count; index++)
                {
                    if (inactive[index].LastWriteTimeUtc >= cutoff) continue;
                    string full = inactive[index].FullName.TrimEnd(Path.DirectorySeparatorChar);
                    if (!string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase)) continue;
                    try { BundlePathPolicy.DeletePlainTree(full); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    catch (InvalidOperationException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static int ReadLauncherVersion(string path)
        {
            try
            {
                var match = Regex.Match(File.ReadAllText(path, Encoding.UTF8), @"(?m)^LAUNCHER_VERSION\s*=\s*(\d+)\s*$");
                return match.Success ? int.Parse(match.Groups[1].Value) : -1;
            }
            catch { return -1; }
        }

        private static string GetRelativePath(string rootPath, string fullPath)
        {
            return BundlePathPolicy.GetRelativePath(rootPath, fullPath);
        }

        /// <summary>
        /// Copies only the Visual Studio MCP runtime surface.
        /// </summary>
        private static void CopyVsBundleFiles(string sourceDir, string targetDir)
        {
            BundlePathPolicy.CopyVsBundleFiles(sourceDir, targetDir);
        }

        private static bool ValidateWheelManifest(string bundleRoot)
        {
            string fullRoot = Path.GetFullPath(bundleRoot).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            string manifestPath = Path.Combine(bundleRoot, "vendor", "wheels", "manifest.json");
            if (!File.Exists(manifestPath)) return false;

            var serializer = new JavaScriptSerializer();
            var manifest = serializer.Deserialize<WheelsManifest>(File.ReadAllText(manifestPath, Encoding.UTF8));
            if (manifest?.wheels == null || manifest.wheels.Length == 0 ||
                !string.Equals(manifest.platform, "win_amd64", StringComparison.OrdinalIgnoreCase)) return false;
            var declaredWheels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var wheel in manifest.wheels)
            {
                if (string.IsNullOrWhiteSpace(wheel?.file) || string.IsNullOrWhiteSpace(wheel.sha256)) return false;
                string normalizedWheel = wheel.file.Replace('\\', '/');
                if (!Regex.IsMatch(normalizedWheel, @"^vendor/wheels/[^/]+\.whl$", RegexOptions.IgnoreCase) ||
                    !declaredWheels.Add(normalizedWheel)) return false;
                string wheelPath = Path.GetFullPath(Path.Combine(
                    bundleRoot,
                    normalizedWheel.Replace('/', Path.DirectorySeparatorChar)));
                if (!wheelPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(wheelPath)) return false;
                using (var stream = File.OpenRead(wheelPath))
                using (var sha256 = SHA256.Create())
                {
                    string actual = BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                    if (!string.Equals(actual, wheel.sha256, StringComparison.OrdinalIgnoreCase)) return false;
                }
            }
            string wheelRoot = Path.Combine(bundleRoot, "vendor", "wheels");
            string[] actualWheels = Directory.GetFiles(wheelRoot, "*.whl", SearchOption.TopDirectoryOnly);
            if (actualWheels.Length != declaredWheels.Count) return false;
            foreach (string actualWheel in actualWheels)
            {
                string relative = "vendor/wheels/" + Path.GetFileName(actualWheel);
                if (!declaredWheels.Contains(relative)) return false;
            }
            return true;
        }

        private static void DeleteOwnedBundleDirectory(string directory, string expectedParent)
        {
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(expectedParent))
            {
                return;
            }

            string fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            string parentWithoutSeparator = Path.GetFullPath(expectedParent).TrimEnd(Path.DirectorySeparatorChar);
            string expectedOwnedParent = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".ai-debugger",
                "mcp_server_vs")).TrimEnd(Path.DirectorySeparatorChar);
            string aidbParent = Path.GetDirectoryName(parentWithoutSeparator);
            if (!string.Equals(parentWithoutSeparator, expectedOwnedParent, StringComparison.OrdinalIgnoreCase) ||
                !BundlePathPolicy.IsPlainDirectory(aidbParent) ||
                !BundlePathPolicy.IsPlainDirectory(parentWithoutSeparator))
            {
                throw new InvalidOperationException("Refusing to delete through an untrusted VS MCP bundle parent");
            }
            string fullParent = parentWithoutSeparator +
                Path.DirectorySeparatorChar;
            if (!fullDirectory.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(fullDirectory).StartsWith(".mcp_server_vs.", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to delete a directory outside the owned VS MCP bundle staging area");
            }

            if (Directory.Exists(fullDirectory))
            {
                BundlePathPolicy.DeletePlainTree(fullDirectory);
            }
        }

        private static string[] GetVsBundleFiles(string sourceDir)
        {
            return BundlePathPolicy.GetVsBundleFiles(sourceDir);
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
            var stablePath = Path.Combine(userProfile, ".ai-debugger", "mcp_server_vs", relativePath);
            var normalizedStablePath = Path.GetFullPath(stablePath);

            if (TryEnsureBundledServerFiles(null, out var stableServerPath))
            {
                var syncedPath = Path.Combine(Path.GetDirectoryName(stableServerPath), relativePath);
                if (File.Exists(syncedPath))
                {
                    return Path.GetFullPath(syncedPath);
                }
            }

            throw new InvalidOperationException(
                $"The current VS MCP bundle resource '{relativePath}' could not be deployed and validated at {normalizedStablePath}.");
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
