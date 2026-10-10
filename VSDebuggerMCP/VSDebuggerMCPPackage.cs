using System;
using System.ComponentModel.Design;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VSDebuggerMCP.Protocol;
using VSDebuggerMCP.Services;
using VSDebuggerMCP.Ipc;
using Task = System.Threading.Tasks.Task;

namespace VSDebuggerMCP
{
    /// <summary>
    /// VS Package that provides debugger information via Named Pipe for MCP integration.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideBindingPath]
    public sealed class VSDebuggerMCPPackage : AsyncPackage
    {
        public const string PackageGuidString = "475DEBBA-1C22-4878-9824-DEF0379A1042";
        private static readonly Guid CommandSetGuid = new Guid("B524E80E-2159-4CB6-B020-A9BAA88DC707");
        private const int StartServerCommandId = 0x0100;
        private const int SetupClientsCommandId = 0x0101;
        private const int StartFullAccessServerCommandId = 0x0102;
        private const int StopServerCommandId = 0x0103;
        private const int UninstallClientsCommandId = 0x0104;
        private const int StartAutomationTestServerCommandId = 0x0105;
        private const string AutomationTestNonceVariable = "AIDB_LIVE_SMOKE_NONCE";
        private const string AutomationTestRootSuffixVariable = "AIDB_LIVE_SMOKE_ROOT_SUFFIX";
        private const string LauncherIdVariable = "AIDB_LAUNCH_ID";
        private const string LauncherCapabilitiesVariable = "AIDB_LAUNCH_CAPABILITIES";
        private const string LauncherExpiresVariable = "AIDB_LAUNCH_EXPIRES_UNIX_MS";
        private const long LauncherRequestMaximumLifetimeMs = 10 * 60 * 1000;

        private DTE2 _dte;
        private DebuggerService _debuggerService;
        private NamedPipeServer _pipeServer;
        private IVsOutputWindowPane _outputPane;
        private Task _serverStopTask;
        private readonly CancelableSingleFlight _clientSetup = new CancelableSingleFlight();
        private bool _shutdownStarted;

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
                await RegisterCommandsAsync();

                // The debugger service, including the debugger-event hooks it installs, is
                // created lazily when an MCP server actually starts, so loading this package
                // in an ordinary Visual Studio session stays cheap.
                LogMessage("AIDebugger package loaded. The MCP server starts only on an explicit request.");
            }
            catch (Exception ex)
            {
                LogError($"Failed to initialize AIDebugger: {ex.Message}");
            }
        }

        private async Task RegisterCommandsAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var commandService = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService == null)
            {
                throw new InvalidOperationException("Visual Studio menu command service is unavailable");
            }

            commandService.AddCommand(new MenuCommand(
                (_, __) => StartServer(configureCapabilities: false),
                new CommandID(CommandSetGuid, StartServerCommandId)));
            commandService.AddCommand(new OleMenuCommand(
                StartFullAccessServerOrLauncher,
                new CommandID(CommandSetGuid, StartFullAccessServerCommandId)));
            commandService.AddCommand(new MenuCommand(
                (_, __) => RequestClientSetup(),
                new CommandID(CommandSetGuid, SetupClientsCommandId)));
            commandService.AddCommand(new MenuCommand(
                (_, __) => RequestStopServer(),
                new CommandID(CommandSetGuid, StopServerCommandId)));
            commandService.AddCommand(new MenuCommand(
                (_, __) => RequestClientUninstall(),
                new CommandID(CommandSetGuid, UninstallClientsCommandId)));
            commandService.AddCommand(new OleMenuCommand(
                StartAutomationTestServer,
                new CommandID(CommandSetGuid, StartAutomationTestServerCommandId)));
        }

        private void RequestClientSetup()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!EnsureNonElevatedSecurityBoundary("configure MCP clients")) return;
            IReadOnlyCollection<string> selected = SelectClientsForConfiguration(uninstall: false);
            if (selected == null || selected.Count == 0) return;
            if (!_clientSetup.TryStart(
                cancellationToken => Task.Run(
                    () => AutoRegisterMcpClients(cancellationToken, selected),
                    cancellationToken),
                out _))
            {
                LogMessage("AIDebugger MCP client setup is already running or the package is shutting down.");
            }
        }

        private void RequestClientUninstall()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!EnsureNonElevatedSecurityBoundary("remove MCP client entries")) return;
            IReadOnlyCollection<string> selected = SelectClientsForConfiguration(uninstall: true);
            if (selected == null || selected.Count == 0) return;
            if (!_clientSetup.TryStart(
                cancellationToken => Task.Run(
                    () => AutoUnregisterMcpClients(cancellationToken, selected),
                    cancellationToken),
                out _))
            {
                LogMessage("AIDebugger MCP client configuration is already running or the package is shutting down.");
            }
        }

        private IReadOnlyCollection<string> SelectClientsForConfiguration(bool uninstall)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var plans = new[]
            {
                new ClientConfigurationPlan("Cursor", McpClientRegistration.GetCursorMcpConfigPath()),
                new ClientConfigurationPlan("Codex", McpClientRegistration.GetCodexConfigPath()),
                new ClientConfigurationPlan("Claude Desktop", McpClientRegistration.GetClaudeDesktopConfigPath()),
                new ClientConfigurationPlan("Claude Code", McpClientRegistration.GetClaudeCodeUserConfigPath()),
                new ClientConfigurationPlan("OpenCode", McpClientRegistration.GetOpenCodeConfigPath()),
            };
            var selected = new List<string>();
            string verb = uninstall ? "Remove the AIDebugger-owned entry from" : "Add or update only the AIDebugger-owned entry in";
            foreach (ClientConfigurationPlan plan in plans)
            {
                int answer = VsShellUtilities.ShowMessageBox(
                    this,
                    $"{verb} {plan.Name}?\n\nExact configuration path:\n{plan.Path}\n\n" +
                    "Yes selects this client, No skips it, Cancel aborts without writing any configuration.",
                    "AIDebugger MCP Client Configuration Preview",
                    OLEMSGICON.OLEMSGICON_QUERY,
                    OLEMSGBUTTON.OLEMSGBUTTON_YESNOCANCEL,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND);
                if (answer == (int)VSConstants.MessageBoxResult.IDCANCEL) return null;
                if (answer == (int)VSConstants.MessageBoxResult.IDYES) selected.Add(plan.Name);
            }
            if (selected.Count == 0)
            {
                LogMessage("No MCP clients selected; no configuration files were changed.");
                return selected;
            }
            int confirmation = VsShellUtilities.ShowMessageBox(
                this,
                $"{(uninstall ? "Remove" : "Configure")} the AIDebugger-owned entry for:\n\n" +
                    string.Join("\n", selected) + "\n\nNo unowned configuration keys will be changed.",
                "Confirm AIDebugger MCP Client Changes",
                OLEMSGICON.OLEMSGICON_QUERY,
                OLEMSGBUTTON.OLEMSGBUTTON_YESNO,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND);
            return confirmation == (int)VSConstants.MessageBoxResult.IDYES ? selected : null;
        }

        private void RequestStopServer()
        {
            if (_serverStopTask != null && !_serverStopTask.IsCompleted)
            {
                LogMessage("AIDebugger MCP server shutdown is already in progress.");
                return;
            }
            _serverStopTask = StopServerAsync();
        }

        private void StartServer(bool configureCapabilities)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!EnsureNonElevatedSecurityBoundary("start the MCP server")) return;
            if (_pipeServer != null || (_serverStopTask != null && !_serverStopTask.IsCompleted))
            {
                LogMessage("AIDebugger MCP server is already running for this Visual Studio instance.");
                return;
            }

            try
            {
                IReadOnlyCollection<string> capabilities = configureCapabilities
                    ? SelectServerCapabilities()
                    : new[] { "server.control", "debug.read" };
                if (capabilities == null) return;
                StartServerWithCapabilities(capabilities);
            }
            catch (Exception ex)
            {
                LogError($"Failed to start the MCP server: {ex.GetType().Name}");
            }
        }

        private void StartAutomationTestServer(object sender, EventArgs eventArgs)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string suppliedNonce = (eventArgs as OleMenuCmdEventArgs)?.InValue as string;
            string expectedNonce = Environment.GetEnvironmentVariable(AutomationTestNonceVariable);
            string expectedRootSuffix = Environment.GetEnvironmentVariable(AutomationTestRootSuffixVariable);
            Environment.SetEnvironmentVariable(AutomationTestNonceVariable, null);
            Environment.SetEnvironmentVariable(AutomationTestRootSuffixVariable, null);

            bool rootSuffixShapeValid = !string.IsNullOrEmpty(expectedRootSuffix) &&
                Regex.IsMatch(expectedRootSuffix, "\\A[A-Za-z0-9]{8,48}\\z", RegexOptions.CultureInvariant);
            bool isolatedRoot = rootSuffixShapeValid && (_dte?.RegistryRoot ?? string.Empty)
                .EndsWith(expectedRootSuffix, StringComparison.OrdinalIgnoreCase);
            bool nonceShapeValid = !string.IsNullOrEmpty(expectedNonce) &&
                Regex.IsMatch(expectedNonce, "\\A[0-9a-fA-F]{64}\\z", RegexOptions.CultureInvariant);
            bool nonceMatches = nonceShapeValid &&
                !string.IsNullOrEmpty(suppliedNonce) &&
                string.Equals(expectedNonce, suppliedNonce, StringComparison.Ordinal);
            if (!isolatedRoot || !nonceMatches)
            {
                LogError("Rejected the hidden automation server command: an Experimental Instance and a valid one-time smoke-test nonce are required.");
                return;
            }
            if (!EnsureNonElevatedSecurityBoundary("start the automation test MCP server")) return;
            if (_pipeServer != null || (_serverStopTask != null && !_serverStopTask.IsCompleted))
            {
                LogError("Rejected the hidden automation server command because an MCP server is already running.");
                return;
            }

            try
            {
                StartServerWithCapabilities(DebugMethodCatalog.AllCapabilities);
            }
            catch (Exception ex)
            {
                LogError($"Failed to start the automation test MCP server: {ex.GetType().Name}");
            }
        }

        /// <summary>
        /// <summary>
        /// Handles the full-access command. A user click keeps the interactive capability
        /// prompts; the AIDebugger launcher instead passes the one-time launch id this
        /// instance was started with and gets a non-interactive start.
        /// The long-standing command name is reused on purpose: a command added to the
        /// .vsct is not resolvable until Visual Studio refreshes its command table, which
        /// is keyed per extension version and therefore never happens for a rebuilt VSIX
        /// that keeps its git-derived version.
        /// </summary>
        private void StartFullAccessServerOrLauncher(object sender, EventArgs eventArgs)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string launchId = Environment.GetEnvironmentVariable(LauncherIdVariable);
            string requestedCapabilities = Environment.GetEnvironmentVariable(LauncherCapabilitiesVariable);
            string expiresAt = Environment.GetEnvironmentVariable(LauncherExpiresVariable);
            // The launcher proves intent with the one-time launch id it put in this
            // process's environment. A command argument is not used: Visual Studio
            // rejects arguments for commands that do not carry the AllowParams flag.
            bool launcherRequested = !string.IsNullOrEmpty(launchId) ||
                !string.IsNullOrEmpty(requestedCapabilities) ||
                !string.IsNullOrEmpty(expiresAt);
            if (!launcherRequested)
            {
                // An ordinary menu click: unchanged behaviour.
                StartServer(configureCapabilities: true);
                return;
            }
            // Consume immediately: the variables must not survive into any process this
            // Visual Studio starts later, such as a debuggee.
            Environment.SetEnvironmentVariable(LauncherIdVariable, null);
            Environment.SetEnvironmentVariable(LauncherCapabilitiesVariable, null);
            Environment.SetEnvironmentVariable(LauncherExpiresVariable, null);

            long nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (!long.TryParse(expiresAt, NumberStyles.Integer, CultureInfo.InvariantCulture, out long expiresUnixMs) ||
                expiresUnixMs <= nowUnixMs ||
                expiresUnixMs > nowUnixMs + LauncherRequestMaximumLifetimeMs)
            {
                LogError("Rejected the launcher start request because it is expired or outlives its permitted window.");
                return;
            }
            IReadOnlyCollection<string> capabilities;
            if (!TryParseLauncherCapabilities(requestedCapabilities, out capabilities))
            {
                LogError("Rejected the launcher start request because its capability set is missing, unknown, or malformed.");
                return;
            }
            if (_pipeServer != null || (_serverStopTask != null && !_serverStopTask.IsCompleted))
            {
                LogMessage("Ignored the launcher start request because an MCP server is already running for this Visual Studio instance.");
                return;
            }
            if (!EnsureNonElevatedSecurityBoundary("start the MCP server", showDialog: false)) return;
            try
            {
                StartServerWithCapabilities(capabilities);
                LogMessage("Launcher-requested MCP server started.");
            }
            catch (Exception ex)
            {
                LogError($"Failed to start the launcher-requested MCP server: {ex.GetType().Name}");
            }
        }

        private static bool TryParseLauncherCapabilities(
            string requested,
            out IReadOnlyCollection<string> capabilities)
        {
            capabilities = Array.Empty<string>();
            if (string.IsNullOrWhiteSpace(requested)) return false;

            var known = new HashSet<string>(DebugMethodCatalog.AllCapabilities, StringComparer.Ordinal);
            var selected = new HashSet<string>(StringComparer.Ordinal) { "server.control", "debug.read" };
            bool anyRequested = false;
            foreach (string entry in requested.Split(','))
            {
                string name = entry.Trim();
                if (name.Length == 0) continue;
                // An unknown name is rejected outright rather than silently dropped: a
                // launcher and an extension with mismatched capability vocabularies must
                // fail loudly instead of granting a narrower server than was requested.
                if (name.Length > 64 || !known.Contains(name)) return false;
                anyRequested = true;
                selected.Add(name);
            }
            if (!anyRequested) return false;

            capabilities = new List<string>(selected);
            return true;
        }

        private DebuggerService EnsureDebuggerService()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_debuggerService == null)
            {
                _debuggerService = new DebuggerService(_dte, LogMessage);
            }
            return _debuggerService;
        }

        private void StartServerWithCapabilities(IReadOnlyCollection<string> capabilities)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var server = new NamedPipeServer(EnsureDebuggerService(), LogMessage, capabilities);
            server.Start();
            _pipeServer = server;
            var effective = new HashSet<string>(server.AllowedCapabilities, StringComparer.Ordinal);
            var fullSet = new HashSet<string>(DebugMethodCatalog.AllCapabilities, StringComparer.Ordinal);
            string profile = effective.SetEquals(fullSet)
                ? "full_access"
                : (effective.SetEquals(new[] { "server.control", "debug.read" })
                    ? "read_only"
                    : "custom");
            LogMessage(
                $"AIDebugger MCP server started with effective access profile '{profile}' and capabilities: " +
                string.Join(", ", server.AllowedCapabilities));
        }

        private IReadOnlyCollection<string> SelectServerCapabilities()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var selected = new List<string> { "server.control", "debug.read" };
            var choices = new[]
            {
                new CapabilityChoice("debug.memory.read", "Read target-process memory and search its contents."),
                new CapabilityChoice("debug.binary.inspect", "Parse explicitly requested managed assembly metadata inside the no-capability AppContainer."),
                new CapabilityChoice("debug.breakpoint.write", "Create, change, enable, or remove breakpoints."),
                new CapabilityChoice("debug.context.write", "Change the selected debug thread/frame or next statement."),
                new CapabilityChoice("debug.evaluate.unsafe", "Evaluate expressions that may run target code or have side effects."),
                new CapabilityChoice("debug.execution.control", "Continue, pause, step, stop, detach, or run to a location."),
                new CapabilityChoice("debug.session.write", "Start debugging or attach Visual Studio to a process."),
            };
            foreach (CapabilityChoice choice in choices)
            {
                int answer = VsShellUtilities.ShowMessageBox(
                    this,
                    $"Grant this server-session capability?\n\n{choice.Name}\n{choice.Description}\n\n" +
                    "Yes grants it until the server is stopped. No leaves it disabled. Cancel aborts without starting the server.",
                    "Configure AIDebugger Session Capabilities",
                    OLEMSGICON.OLEMSGICON_QUERY,
                    OLEMSGBUTTON.OLEMSGBUTTON_YESNOCANCEL,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND);
                if (answer == (int)VSConstants.MessageBoxResult.IDCANCEL) return null;
                if (answer == (int)VSConstants.MessageBoxResult.IDYES) selected.Add(choice.Name);
            }
            int confirmation = VsShellUtilities.ShowMessageBox(
                this,
                "Start AIDebugger for this Visual Studio process with these session-scoped capabilities?\n\n" +
                    string.Join("\n", selected),
                "Confirm AIDebugger Session Capabilities",
                OLEMSGICON.OLEMSGICON_QUERY,
                OLEMSGBUTTON.OLEMSGBUTTON_YESNO,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND);
            return confirmation == (int)VSConstants.MessageBoxResult.IDYES ? selected : null;
        }

        private bool EnsureNonElevatedSecurityBoundary(string operation, bool showDialog = true)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!ProcessIntegrityValidator.IsCurrentProcessElevated()) return true;
            string message =
                $"AIDebugger refuses to {operation} from an elevated Visual Studio process. " +
                "Its MCP Python runtime and client configuration are per-user files; crossing the elevation boundary would be unsafe. " +
                "Restart Visual Studio without elevation.";
            LogError(message);
            if (!showDialog)
            {
                // A launcher-driven start has no user in front of the shell yet, and the
                // launcher reports the failure back through the missing registration file.
                return false;
            }
            VsShellUtilities.ShowMessageBox(
                this,
                message,
                "AIDebugger Elevation Boundary",
                OLEMSGICON.OLEMSGICON_CRITICAL,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            return false;
        }

        private async Task StopServerAsync()
        {
            NamedPipeServer server = _pipeServer;
            if (server == null)
            {
                LogMessage("AIDebugger MCP server is not running.");
                return;
            }
            try
            {
                await server.StopAsync();
            }
            finally
            {
                try
                {
                    server.Dispose();
                }
                finally
                {
                    if (ReferenceEquals(_pipeServer, server))
                    {
                        _pipeServer = null;
                    }
                }
            }
            LogMessage("AIDebugger MCP server stopped; it can now be restarted with a different access mode.");
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

        [SuppressMessage(
            "Usage",
            "VSTHRD010",
            Justification = "IVsOutputWindowPane.OutputStringThreadSafe is the VS SDK thread-safe logging API.")]
        private void LogMessage(string message)
        {
            try
            {
                // IVsOutputWindowPane.OutputStringThreadSafe is explicitly safe from
                // pipe worker threads and posts to the pane without a synchronous UI wait.
                _outputPane?.OutputStringThreadSafe($"[AIDebugger] {DateTime.Now:HH:mm:ss} - {message}\n");
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
        /// Explicitly registers this extension with MCP clients after the user invokes the setup command
        /// (Cursor, Codex, Claude Desktop, Claude Code, OpenCode).
        /// </summary>
        private void AutoRegisterMcpClients(
            CancellationToken cancellationToken,
            IReadOnlyCollection<string> selectedClients)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Step 1: Check if Python is available
                LogMessage("Checking Python installation...");
                if (!McpClientRegistration.IsPythonAvailable(LogMessage))
                {
                    LogError("Python is not installed or not in PATH. Please install " + McpClientRegistration.GetSupportedPythonVersionRequirement(LogMessage) + " and add it to PATH.");
                    return;
                }

                // Step 2: Use bundled Python dependencies
                LogMessage("Using bundled Python dependencies (no pip install required).");

                foreach (string client in selectedClients)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    LogMessage($"[{client}] Applying explicitly confirmed MCP registration...");
                    RegistrationResult result;
                    switch (client)
                    {
                        case "Cursor": result = McpClientRegistration.RegisterWithCursor(LogMessage); break;
                        case "Codex": result = McpClientRegistration.RegisterWithCodex(LogMessage); break;
                        case "Claude Desktop": result = McpClientRegistration.RegisterWithClaudeDesktop(LogMessage); break;
                        case "Claude Code": result = McpClientRegistration.RegisterWithClaudeCode(LogMessage); break;
                        case "OpenCode": result = McpClientRegistration.RegisterWithOpenCode(LogMessage); break;
                        default: continue;
                    }
                    HandleRegistrationResult(result, client);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                LogMessage("MCP client setup was cancelled during package shutdown.");
            }
            catch (Exception ex)
            {
                LogError($"Error during MCP registration: {ex.Message}");
            }
        }

        private void AutoUnregisterMcpClients(
            CancellationToken cancellationToken,
            IReadOnlyCollection<string> selectedClients)
        {
            foreach (string client in selectedClients)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool success;
                switch (client)
                {
                    case "Cursor": success = McpClientRegistration.UnregisterFromCursor(LogMessage); break;
                    case "Codex": success = McpClientRegistration.UnregisterFromCodex(LogMessage); break;
                    case "Claude Desktop": success = McpClientRegistration.UnregisterFromClaudeDesktop(LogMessage); break;
                    case "Claude Code": success = McpClientRegistration.UnregisterFromClaudeCode(LogMessage); break;
                    case "OpenCode": success = McpClientRegistration.UnregisterFromOpenCode(LogMessage); break;
                    default: continue;
                }
                LogMessage(success
                    ? $"[{client}] AIDebugger-owned MCP entries removed; unowned settings were preserved."
                    : $"[{client}] ERROR: failed to remove AIDebugger-owned MCP entries.");
            }
        }

        private sealed class ClientConfigurationPlan
        {
            internal ClientConfigurationPlan(string name, string path)
            {
                Name = name;
                Path = path;
            }

            internal string Name { get; }
            internal string Path { get; }
        }

        private sealed class CapabilityChoice
        {
            internal CapabilityChoice(string name, string description)
            {
                Name = name;
                Description = description;
            }

            internal string Name { get; }
            internal string Description { get; }
        }

        private void HandleRegistrationResult(RegistrationResult result, string clientName)
        {
            if (result.Success)
            {
                if (result.Skipped)
                {
                    LogMessage($"[{clientName}] {result.Message}");
                }
                else if (result.AlreadyRegistered)
                {
                    LogMessage($"[{clientName}] AIDebugger is already registered.");
                }
                else
                {
                    LogMessage($"[{clientName}] ============================================");
                    LogMessage($"[{clientName}] AIDebugger has been registered!");
                    LogMessage($"[{clientName}] Config: {result.ConfigPath}");
                    LogMessage($"[{clientName}] Please restart {clientName} to use the tools.");
                    LogMessage($"[{clientName}] ============================================");
                }
            }
            else
            {
                LogError($"[{clientName}] Failed to register: {result.Message}");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_shutdownStarted)
            {
                _shutdownStarted = true;
                // AsyncPackage has a synchronous Dispose boundary. Perform only the
                // synchronous cancellation/ownership transfer here; never make Visual
                // Studio shutdown wait for a client process, pipe worker, actor item, or
                // an EnvDTE call that may no longer return.
                Task setupTask = _clientSetup.CancelAndGetTaskAsync();
                Task setupLifetimeTask = DisposeClientSetupAfterCompletionAsync(_clientSetup, setupTask);

                NamedPipeServer server = _pipeServer;
                _pipeServer = null;
                Task serverTask = Task.CompletedTask;
                if (server != null)
                {
                    serverTask = server.StopAsync();
                    server.Dispose();
                }
                _serverStopTask = serverTask;

                DebuggerService debuggerService = _debuggerService;
                _debuggerService = null;
                Task serviceLifetimeTask = DisposeDebuggerServiceAfterServerAsync(serverTask, debuggerService);

                // Observe every asynchronous tail without accessing package services or
                // logging after base.Dispose has invalidated them.
                _ = ObserveShutdownTailAsync(setupLifetimeTask, serviceLifetimeTask);
            }

            base.Dispose(disposing);
        }

        [SuppressMessage("Usage", "VSTHRD003", Justification = "Nonblocking package-shutdown ownership tail observes and then disposes the externally-started setup task.")]
        private static async Task DisposeClientSetupAfterCompletionAsync(
            CancelableSingleFlight clientSetup,
            Task setupTask)
        {
            try { await setupTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch { }
            finally { clientSetup.Dispose(); }
        }

        [SuppressMessage("Usage", "VSTHRD003", Justification = "Nonblocking package-shutdown ownership tail observes the externally-started server task before disposing the debugger service.")]
        private static async Task DisposeDebuggerServiceAfterServerAsync(
            Task serverTask,
            DebuggerService debuggerService)
        {
            try { await serverTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch { }
            if (debuggerService == null) return;

            try { await debuggerService.WaitForPendingUiOperationsAsync().ConfigureAwait(false); }
            catch { }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            debuggerService.Dispose();
        }

        private static async Task ObserveShutdownTailAsync(params Task[] tasks)
        {
            foreach (Task task in tasks)
            {
                if (task == null) continue;
                try { await task.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch { }
            }
        }
    }
}
