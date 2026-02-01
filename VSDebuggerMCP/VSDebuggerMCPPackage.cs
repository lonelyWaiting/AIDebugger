using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace VSDebuggerMCP
{
    /// <summary>
    /// VS Package that provides debugger information via Named Pipe for MCP integration.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideBindingPath]
    public sealed class VSDebuggerMCPPackage : AsyncPackage
    {
        public const string PackageGuidString = "475DEBBA-1C22-4878-9824-DEF0379A1042";

        private DTE2 _dte;
        private DebuggerService _debuggerService;
        private NamedPipeServer _pipeServer;
        private IVsOutputWindowPane _outputPane;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);

            // Switch to main thread for DTE access
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            try
            {
                // Get DTE instance
                _dte = await GetServiceAsync(typeof(DTE)) as DTE2;
                if (_dte == null)
                {
                    LogError("Failed to get DTE instance");
                    return;
                }

                // Create output pane for logging
                await CreateOutputPaneAsync();

                // Initialize debugger service
                _debuggerService = new DebuggerService(_dte, LogMessage);

                // Start Named Pipe server
                _pipeServer = new NamedPipeServer(_debuggerService, LogMessage);
                _pipeServer.Start();

                LogMessage("AIDebugger initialized successfully. Named Pipe server is running.");

                // Auto-register with Cursor MCP configuration
                await Task.Run(() => AutoRegisterWithCursor());
            }
            catch (Exception ex)
            {
                LogError($"Failed to initialize AIDebugger: {ex.Message}");
            }
        }

        private async Task CreateOutputPaneAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();

            var outputWindow = await GetServiceAsync(typeof(SVsOutputWindow)) as IVsOutputWindow;
            if (outputWindow != null)
            {
                var paneGuid = new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890");
                outputWindow.CreatePane(ref paneGuid, "AIDebugger", 1, 1);
                outputWindow.GetPane(ref paneGuid, out _outputPane);
            }
        }

        private void LogMessage(string message)
        {
            try
            {
                ThreadHelper.JoinableTaskFactory.Run(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    _outputPane?.OutputStringThreadSafe($"[AIDebugger] {DateTime.Now:HH:mm:ss} - {message}\n");
                });
            }
            catch
            {
                // Ignore logging errors
            }
        }

        private void LogError(string message)
        {
            LogMessage($"ERROR: {message}");
        }

        /// <summary>
        /// Automatically registers this extension with Cursor's MCP configuration.
        /// </summary>
        private void AutoRegisterWithCursor()
        {
            try
            {
                // Step 1: Check if Python is available
                LogMessage("Checking Python installation...");
                if (!CursorMcpRegistration.IsPythonAvailable(LogMessage))
                {
                    LogError("Python is not installed or not in PATH. Please install Python 3.10+ and add it to PATH.");
                    return;
                }

                // Step 2: Use bundled Python dependencies
                LogMessage("Using bundled Python dependencies (no pip install required).");

                // Step 3: Register with Cursor
                LogMessage("Checking Cursor MCP registration...");
                var result = CursorMcpRegistration.RegisterWithCursor(LogMessage);
                
                if (result.Success)
                {
                    if (result.AlreadyRegistered)
                    {
                        LogMessage("AIDebugger is already registered in Cursor.");
                    }
                    else
                    {
                        LogMessage("==============================================");
                        LogMessage("AIDebugger has been registered with Cursor!");
                        LogMessage($"Config file: {result.ConfigPath}");
                        LogMessage($"Server script: {result.ServerPath}");
                        LogMessage("Please restart Cursor to use the debugging tools.");
                        LogMessage("==============================================");
                    }
                }
                else
                {
                    LogError($"Failed to register with Cursor: {result.Message}");
                }
            }
            catch (Exception ex)
            {
                LogError($"Error during Cursor MCP registration: {ex.Message}");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _pipeServer?.Stop();
                _pipeServer?.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
