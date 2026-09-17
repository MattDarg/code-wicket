using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using CodeWicket.Core;
using CodeWicket.Ide;
using Task = System.Threading.Tasks.Task;

namespace CodeWicket.VSExtension
{
    /// <summary>
    /// "Send Debug Context to Code Wicket" — takes the call stack and locals the debugger is showing and
    /// puts them in the chat composer as a chip (issue #73, rung 2).
    ///
    /// <para>Placed twice, because there are two places a user is standing when they want it: the
    /// <b>Debug menu</b>, where every other break-mode action lives, and the <b>editor's context
    /// menu</b>, beside Run To Cursor — which is where the pointer already is when they are looking
    /// at the line that stopped.</para>
    ///
    /// <para><b>Nothing is sent.</b> The gesture fills the composer; the user still writes their
    /// question and presses Enter, and can read exactly what will go and remove it. That is the whole
    /// shape of the feature and it is why this is a menu item rather than a "ask the agent about
    /// this" one-shot.</para>
    ///
    /// <para>The label nonetheless reads <i>Send</i>, deliberately, and the mismatch with the line
    /// above is a chosen one rather than an oversight to correct: <i>Attach</i> describes the
    /// mechanism, <i>Send</i> describes the intent the user arrived with, and the pane is brought up
    /// with the chip visible in the composer, so what actually happened is on screen a moment later.
    /// Do not "fix" this back without the same conversation.</para>
    /// </summary>
    internal sealed class AddDebugContextCommand
    {
        public static readonly Guid CommandSet = new Guid("2a3dd172-f5cc-4371-ada2-233c0ef2448d");
        public const int CommandId = 0x0105;

        private readonly AsyncPackage _package;

        private AddDebugContextCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package;
            var command = new OleMenuCommand(Execute, new CommandID(CommandSet, CommandId));
            command.BeforeQueryStatus += OnBeforeQueryStatus;
            commandService.AddCommand(command);
        }

        /// <summary>Kept alive for the lifetime of the package (the registered command holds the handler).</summary>
        public static AddDebugContextCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var commandService = (OleMenuCommandService)await package.GetServiceAsync(typeof(IMenuCommandService));
            Instance = new AddDebugContextCommand(package, commandService);
        }

        /// <summary>
        /// Greys the command out when the debugger is not stopped — the VS-native answer, and what
        /// every built-in debug command does. An enabled item that answers "not in break mode" has
        /// already misled the user with its own appearance.
        /// <para>It does NOT make the null answer unreachable, and must not be read as doing so: break
        /// mode can end between this query and the click (the program continues on another thread, the
        /// user presses F5), so the view-model still reports when the capture came back empty. This is
        /// the appearance; that is the guard.</para>
        /// <para>Reads the same predicate the composer's own menu does — <see cref="VsDebugState"/>
        /// through <see cref="VsIdeServices.IsDebuggerStopped"/> — so the two surfaces cannot disagree
        /// about whether the gesture is on offer. One cheap COM property, on a thread that is already
        /// the UI thread.</para>
        /// </summary>
        private void OnBeforeQueryStatus(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (sender is not OleMenuCommand command)
                return;

            // Visible always, enabled only in break mode. Hiding it would make the gesture
            // undiscoverable in the state the user is in BEFORE they need it, which is when they would
            // have gone looking.
            command.Visible = true;
            command.Enabled = IsDebuggerStopped();
        }

        private static bool IsDebuggerStopped()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE80.DTE2;
                return dte is not null && VsDebugState.IsInBreakMode(dte);
            }
            catch (Exception)
            {
                // No DTE (a shell still starting, or shutting down): not stopped as far as we can tell.
                return false;
            }
        }

        private void Execute(object sender, EventArgs e)
        {
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync(_package.DisposalToken);

                // The pane is SHOWN, not merely found. The chip lands in a composer the user is about
                // to type into, so leaving the window closed would put their evidence somewhere they
                // cannot see it — and this is often the first thing they do in a debugging session.
                var window = await _package.ShowToolWindowAsync(
                    typeof(ChatToolWindow), id: 0, create: true, cancellationToken: _package.DisposalToken);
                if (window?.Frame is IVsWindowFrame frame)
                    ErrorHandler.ThrowOnFailure(frame.Show());

                if (window is not ChatToolWindow chat || !chat.AddDebugContext())
                {
                    // A window that has only just been created is still building its view-model, so
                    // there is nothing to attach to yet. Said out loud rather than swallowed: a menu
                    // click that silently does nothing is the failure this whole issue is about.
                    VsShellUtilities.ShowMessageBox(
                        _package,
                        Branding.ProductName + " is still starting. Try again once the chat window is ready.",
                        Branding.ProductName,
                        OLEMSGICON.OLEMSGICON_INFO,
                        OLEMSGBUTTON.OLEMSGBUTTON_OK,
                        OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                }
            }).FileAndForget("code-wicket/adddebugcontext");
        }
    }
}
