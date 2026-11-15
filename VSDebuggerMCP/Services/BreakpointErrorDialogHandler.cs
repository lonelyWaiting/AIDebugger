using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell;

namespace VSDebuggerMCP.Services
{
    internal static class BreakpointErrorDialogHandler
    {
        private const string VisualStudioMessageBox =
            "Microsoft.VisualStudio.PlatformUI.Packages.ShellService.View.MessageBox";
        private static bool _registered;
        private static Action<string> _log;
        private static DispatcherTimer _dialogSweep;
        internal static string LastAcknowledgedError { get; private set; }

        internal static void Enable(Action<string> log)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!_registered)
            {
                EventManager.RegisterClassHandler(
                    typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
                _dialogSweep = new DispatcherTimer(DispatcherPriority.Send)
                {
                    Interval = TimeSpan.FromMilliseconds(50),
                };
                _dialogSweep.Tick += CheckModalWindows;
                _registered = true;
            }
            _log = log;
            _dialogSweep.Start();
        }

        internal static void Disable()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _log = null;
            _dialogSweep?.Stop();
            LastAcknowledgedError = null;
        }

        private static void OnWindowLoaded(object sender, RoutedEventArgs args)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (sender is Window window) TryAcknowledge(window);
        }

        private static void CheckModalWindows(object sender, EventArgs args)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var windows = Application.Current?.Windows;
            if (windows == null) return;
            for (int index = windows.Count - 1; index >= 0; index--)
            {
                if (TryAcknowledge(windows[index])) return;
            }
        }

        private static bool TryAcknowledge(Window window)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var log = _log;
            if (log == null || !window.IsVisible ||
                (window.GetType().FullName != VisualStudioMessageBox &&
                 AutomationProperties.GetAutomationId(window) != "Microsoft.VisualStudio.MessageBox"))
            {
                return false;
            }

            try
            {
                // VS binds its message body to this property. Read it only for the
                // shell's message-box type and acknowledge only a known debugger error.
                object model = window.DataContext;
                string message = (model?.GetType().GetProperty("Text")?.GetValue(model) as string)?.TrimStart();
                if (message == null ||
                    !(message.StartsWith("The following breakpoint cannot be set:", StringComparison.Ordinal) ||
                      message.StartsWith("Some of the breakpoints cannot be set.", StringComparison.Ordinal)))
                {
                    return false;
                }
                object buttons = model.GetType().GetProperty("OleButtons")?.GetValue(model);
                if (!Equals(buttons, OLEMSGBUTTON.OLEMSGBUTTON_OK)) return false;
                if (!(window.FindName("OKButton") is ButtonBase button) ||
                    !button.IsEnabled || button.Command == null ||
                    !button.Command.CanExecute(button.CommandParameter))
                {
                    return false;
                }

                // Loaded runs during the first layout, before presenting the dialog.
                // Keep the real OK action: a condition failure still stops the debuggee.
                double opacity = window.Opacity;
                window.Opacity = 0;
                try
                {
                    button.Command.Execute(button.CommandParameter);
                }
                finally
                {
                    if (window.IsVisible) window.Opacity = opacity;
                }
                if (window.IsVisible) return false;
                LastAcknowledgedError = message.Length > 4096 ? message.Substring(0, 4096) : message;
                log("Acknowledged Visual Studio breakpoint error: " + message);
                return true;
            }
            catch (Exception error)
            {
                log($"Could not acknowledge Visual Studio breakpoint error: {error.Message}");
                return false;
            }
        }
    }
}
