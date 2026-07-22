using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.VisualStudio.Shell;
using VSDebuggerMCP.Protocol;

namespace VSDebuggerMCP.Services
{
    internal interface IManagedInspectionService
    {
        ManagedAssemblyResult InspectAssembly(string assemblyPath, string typeFilter, int maxTypes, int maxMethodsPerType);
        ManagedTypeResult InspectType(string assemblyPath, string typeName, int maxMethods);
        ManagedMethodIlResult InspectMethodIl(
            string assemblyPath,
            string typeName,
            string methodName,
            int overloadIndex,
            int maxInstructions);
    }

    /// <summary>
    /// Owns the complete offline managed-metadata boundary: path normalization,
    /// AppContainer helper lifetime, bounded protocol I/O, and failure redaction.
    /// It has no EnvDTE or debugger-session dependency and can be exercised without VS.
    /// </summary>
    internal sealed class ManagedInspectionService : IManagedInspectionService
    {
        private const int HelperTimeoutMs = 60000;
        private const long MaxAssemblyBytes = 64L * 1024L * 1024L;
        private const int MaxStdoutCharacters = 4 * 1024 * 1024;
        private const int MaxStderrCharacters = 64 * 1024;
        private const string Capability = "appcontainer_managed_metadata_and_il_read_only";
        private readonly Action<string> _log;

        internal ManagedInspectionService(Action<string> log)
        {
            _log = log ?? (_ => { });
        }

        public ManagedAssemblyResult InspectAssembly(
            string assemblyPath,
            string typeFilter,
            int maxTypes,
            int maxMethodsPerType)
        {
            string path = NormalizeAssemblyPath(assemblyPath);
            string filter = string.IsNullOrWhiteSpace(typeFilter) ? null : typeFilter.Trim();
            try
            {
                return Run<ManagedAssemblyResult>(new HelperRequest
                {
                    Operation = "inspect_managed_assembly",
                    AssemblyPath = path,
                    TypeFilter = filter,
                    MaxTypes = Math.Max(1, Math.Min(maxTypes, 128)),
                    MaxMethodsPerType = Math.Max(0, Math.Min(maxMethodsPerType, 32)),
                });
            }
            catch (Exception exception)
            {
                RethrowControlFlow(exception);
                return new ManagedAssemblyResult
                {
                    AssemblyPath = path,
                    TypeFilter = filter,
                    Capability = Capability,
                    Partial = true,
                    Error = DescribeFailure(exception),
                };
            }
        }

        public ManagedTypeResult InspectType(string assemblyPath, string typeName, int maxMethods)
        {
            string path = NormalizeAssemblyPath(assemblyPath);
            string normalizedType = string.IsNullOrWhiteSpace(typeName) ? null : typeName.Trim();
            try
            {
                return Run<ManagedTypeResult>(new HelperRequest
                {
                    Operation = "inspect_managed_type",
                    AssemblyPath = path,
                    TypeName = normalizedType,
                    MaxMethods = Math.Max(0, Math.Min(maxMethods, 32)),
                });
            }
            catch (Exception exception)
            {
                RethrowControlFlow(exception);
                return new ManagedTypeResult
                {
                    AssemblyPath = path,
                    TypeName = normalizedType,
                    Capability = Capability,
                    Error = DescribeFailure(exception),
                };
            }
        }

        public ManagedMethodIlResult InspectMethodIl(
            string assemblyPath,
            string typeName,
            string methodName,
            int overloadIndex,
            int maxInstructions)
        {
            string path = NormalizeAssemblyPath(assemblyPath);
            string normalizedType = string.IsNullOrWhiteSpace(typeName) ? null : typeName.Trim();
            string normalizedMethod = string.IsNullOrWhiteSpace(methodName) ? null : methodName.Trim();
            int normalizedOverload = Math.Max(0, overloadIndex);
            try
            {
                return Run<ManagedMethodIlResult>(new HelperRequest
                {
                    Operation = "inspect_managed_method_il",
                    AssemblyPath = path,
                    TypeName = normalizedType,
                    MethodName = normalizedMethod,
                    OverloadIndex = normalizedOverload,
                    MaxInstructions = Math.Max(1, Math.Min(maxInstructions, 2048)),
                });
            }
            catch (Exception exception)
            {
                RethrowControlFlow(exception);
                return new ManagedMethodIlResult
                {
                    AssemblyPath = path,
                    TypeName = normalizedType,
                    MethodName = normalizedMethod,
                    OverloadIndex = normalizedOverload,
                    Capability = Capability,
                    Partial = true,
                    Error = DescribeFailure(exception),
                };
            }
        }

        private T Run<T>(HelperRequest request) where T : class
        {
            if (string.IsNullOrWhiteSpace(request?.AssemblyPath) || !File.Exists(request.AssemblyPath))
                throw new FileNotFoundException("Managed assembly was not found", request?.AssemblyPath);
            long assemblyBytes = new FileInfo(request.AssemblyPath).Length;
            if (assemblyBytes <= 0 || assemblyBytes > MaxAssemblyBytes)
                throw new InvalidOperationException(
                    $"Managed assembly size {assemblyBytes} bytes is outside the allowed range 1..{MaxAssemblyBytes}.");

            string helperPath = FindHelperPath();
            if (string.IsNullOrEmpty(helperPath))
                throw new FileNotFoundException("ManagedInspectionHelper.exe was not found next to the VS extension assembly");

            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            string requestJson = serializer.Serialize(request);
            using (var inspectionProcess = ManagedInspectionProcess.Start(helperPath, request.AssemblyPath))
            {
                Process process = inspectionProcess.Process;
                if (process == null) throw new InvalidOperationException("Failed to start ManagedInspectionHelper.exe");
                string stdout = null;
                string stderr = null;
                try
                {
                    ThreadHelper.JoinableTaskFactory.Run(async () =>
                    {
                        var stdoutOverflow = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        var stderrOverflow = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        Task<string> stdoutTask = ReadBoundedTextAsync(
                            inspectionProcess.StandardOutput, MaxStdoutCharacters, stdoutOverflow);
                        Task<string> stderrTask = ReadBoundedTextAsync(
                            inspectionProcess.StandardError, MaxStderrCharacters, stderrOverflow);
                        await inspectionProcess.StandardInput.WriteAsync(requestJson);
                        await inspectionProcess.StandardInput.FlushAsync();
                        inspectionProcess.StandardInput.Close();

                        var stopwatch = Stopwatch.StartNew();
                        int timeoutMs = DebuggerRequestContext.RemainingMilliseconds(HelperTimeoutMs);
                        while (!process.WaitForExit(Math.Min(100, Math.Max(1, timeoutMs - (int)stopwatch.ElapsedMilliseconds))))
                        {
                            DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                            if (stdoutOverflow.Task.IsCompleted || stderrOverflow.Task.IsCompleted)
                            {
                                TryKill(process);
                                await Task.WhenAll(stdoutTask, stderrTask);
                                throw CreateOverflowException(stdoutOverflow.Task.IsCompleted);
                            }
                            if (stopwatch.ElapsedMilliseconds >= timeoutMs)
                                throw new TimeoutException($"Managed inspection helper timed out after {timeoutMs}ms");
                        }
                        stdout = await stdoutTask;
                        stderr = await stderrTask;
                        if (stdoutOverflow.Task.IsCompleted || stderrOverflow.Task.IsCompleted)
                            throw CreateOverflowException(stdoutOverflow.Task.IsCompleted);
                    });
                }
                catch
                {
                    TryKill(process);
                    throw;
                }

                if (process.ExitCode != 0)
                {
                    string category = GetHelperErrorCategory(stderr);
                    _log($"Managed inspection helper failed: exitCode={process.ExitCode}, category={category}");
                    throw new InvalidOperationException(
                        $"Managed inspection helper failed with exit code {process.ExitCode} ({category}).");
                }
                if (string.IsNullOrWhiteSpace(stdout))
                    throw new InvalidOperationException("Managed inspection helper returned no JSON");
                T result = serializer.Deserialize<T>(stdout);
                return result ?? throw new InvalidOperationException("Managed inspection helper returned invalid JSON");
            }
        }

        private static async Task<string> ReadBoundedTextAsync(
            StreamReader reader,
            int maxCharacters,
            TaskCompletionSource<bool> overflow)
        {
            var result = new System.Text.StringBuilder(Math.Min(maxCharacters, 16 * 1024));
            var buffer = new char[4096];
            bool exceeded = false;
            while (true)
            {
                int read = await reader.ReadAsync(buffer, 0, buffer.Length);
                if (read == 0) return result.ToString();
                if (exceeded) continue;
                if (result.Length > maxCharacters - read)
                {
                    exceeded = true;
                    overflow.TrySetResult(true);
                    continue;
                }
                result.Append(buffer, 0, read);
            }
        }

        private static ResourceLimitExceededException CreateOverflowException(bool stdoutOverflow)
        {
            return new ResourceLimitExceededException(stdoutOverflow
                ? $"Managed inspection helper stdout exceeds {MaxStdoutCharacters} characters"
                : $"Managed inspection helper stderr exceeds {MaxStderrCharacters} characters");
        }

        private static void TryKill(Process process)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch { }
        }

        private static string GetHelperErrorCategory(string stderr)
        {
            string firstLine = (stderr ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            const string prefix = "MANAGED_INSPECTION_FAILED:";
            if (string.IsNullOrWhiteSpace(firstLine) || !firstLine.StartsWith(prefix, StringComparison.Ordinal))
                return "unspecified";
            string category = firstLine.Substring(prefix.Length);
            return category.Length <= 64 && category.All(ch => char.IsLetterOrDigit(ch) || ch == '_')
                ? category
                : "unspecified";
        }

        private static string FindHelperPath()
        {
            string assemblyDirectory;
            try { assemblyDirectory = Path.GetDirectoryName(typeof(ManagedInspectionService).Assembly.Location); }
            catch { assemblyDirectory = null; }
            if (string.IsNullOrEmpty(assemblyDirectory)) return null;
            foreach (string relative in new[]
            {
                "ManagedInspectionHelper.exe",
                Path.Combine("tools", "ManagedInspectionHelper.exe"),
                Path.Combine("ManagedInspectionHelper", "ManagedInspectionHelper.exe"),
            })
            {
                string candidate = Path.Combine(assemblyDirectory, relative);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static string NormalizeAssemblyPath(string assemblyPath)
        {
            if (string.IsNullOrWhiteSpace(assemblyPath)) return null;
            string trimmed = assemblyPath.Trim().Trim('"');
            try { return Path.GetFullPath(trimmed); }
            catch { return trimmed; }
        }

        private static string DescribeFailure(Exception exception)
        {
            if (exception is FileNotFoundException) return "Managed assembly or inspection helper was not found.";
            if (exception is UnauthorizedAccessException) return "Managed assembly access was denied.";
            if (exception is BadImageFormatException) return "The requested file is not a supported managed assembly.";
            return "Managed inspection failed. See the Visual Studio Activity Log for the failure category.";
        }

        private static void RethrowControlFlow(Exception exception)
        {
            if (exception is OperationCanceledException ||
                exception is TimeoutException ||
                exception is ResourceLimitExceededException ||
                exception is StaleDebugSessionException ||
                exception is StaleDebugContextException)
            {
                ExceptionDispatchInfo.Capture(exception).Throw();
            }
        }

        private sealed class HelperRequest
        {
            public string Operation { get; set; }
            public string AssemblyPath { get; set; }
            public string TypeFilter { get; set; }
            public string TypeName { get; set; }
            public string MethodName { get; set; }
            public int MaxTypes { get; set; }
            public int MaxMethodsPerType { get; set; }
            public int MaxMethods { get; set; }
            public int OverloadIndex { get; set; }
            public int MaxInstructions { get; set; }
        }
    }
}
