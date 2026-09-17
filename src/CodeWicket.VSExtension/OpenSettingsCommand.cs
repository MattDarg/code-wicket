using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace CodeWicket.VSExtension
{
    /// <summary>
    /// The "Extensions &gt; Code Wicket &gt; Settings..." command: opens the Visual Studio settings
    /// UI, where our settings appear under the "Code Wicket" category.
    /// </summary>
    /// <remarks>
    /// It opens the settings window, not our page specifically. On VS 2026 our settings are a
    /// unified-settings *external region* (CodeWicket.registration.json), and unified settings
    /// exposes no documented deep-link: the ToolsOptions command's argument is a Tools&gt;Options
    /// *page GUID*, which an external region doesn't have. On VS 2022 the same settings render as
    /// the classic pages in SettingsPages.cs, and the command opens the window there too. So the point
    /// of this command is discoverability - it answers "where do the
    /// settings live?" - and it can be upgraded in place if a navigation API ever appears.
    /// </remarks>
    internal sealed class OpenSettingsCommand
    {
        public const int CommandId = 0x0101;

        private readonly AsyncPackage _package;

        private OpenSettingsCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package;
            var commandId = new CommandID(ShowChatWindowCommand.CommandSet, CommandId);
            commandService.AddCommand(new MenuCommand(Execute, commandId));
        }

        /// <summary>Kept alive for the lifetime of the package (the registered command holds the handler).</summary>
        public static OpenSettingsCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var commandService = (OleMenuCommandService)await package.GetServiceAsync(typeof(IMenuCommandService));
            Instance = new OpenSettingsCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync(_package.DisposalToken);
                var shell = (IVsUIShell)await _package.GetServiceAsync(typeof(SVsUIShell));
                if (shell is null)
                    throw new NotSupportedException("SVsUIShell is unavailable; cannot open the settings window.");

                // PostExecCommand (not Exec) so the modal settings window opens after this command
                // handler returns, rather than pumping inside it.
                var commandSet = VSConstants.GUID_VSStandardCommandSet97;
                object noPage = null;
                ErrorHandler.ThrowOnFailure(shell.PostExecCommand(
                    ref commandSet, (uint)VSConstants.VSStd97CmdID.ToolsOptions, 0, ref noPage));
            }).FileAndForget("code-wicket/opensettings");
        }
    }
}
