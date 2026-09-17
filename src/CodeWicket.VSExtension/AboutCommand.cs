using System;
using System.ComponentModel.Design;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace CodeWicket.VSExtension
{
    /// <summary>
    /// The "Extensions &gt; Code Wicket &gt; About" command: shows the version and build details
    /// (see <see cref="AboutDialog"/>).
    /// </summary>
    /// <remarks>
    /// Always enabled, like Open Logs Folder and unlike Restart: there's no live object to depend on,
    /// and "which version is this?" is exactly the question asked when the chat window won't start.
    /// </remarks>
    internal sealed class AboutCommand
    {
        public const int CommandId = 0x0104;

        private readonly AsyncPackage _package;

        private AboutCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package;
            var commandId = new CommandID(ShowChatWindowCommand.CommandSet, CommandId);
            commandService.AddCommand(new MenuCommand(Execute, commandId));
        }

        /// <summary>Kept alive for the lifetime of the package (the registered command holds the handler).</summary>
        public static AboutCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var commandService = (OleMenuCommandService)await package.GetServiceAsync(typeof(IMenuCommandService));
            Instance = new AboutCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync(_package.DisposalToken);
                var shell = await _package.GetServiceAsync(typeof(SVsShell)) as IVsShell;
                // A null shell only costs the Visual Studio row; the dialog still reports our own build.
                AboutDialog.Show(shell);
            }).FileAndForget("code-wicket/about");
        }
    }
}
