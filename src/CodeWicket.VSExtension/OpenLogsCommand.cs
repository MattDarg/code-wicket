using System;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using CodeWicket.Shell;
using Task = System.Threading.Tasks.Task;

namespace CodeWicket.VSExtension
{
    /// <summary>
    /// The "Extensions &gt; Code Wicket &gt; Open Logs Folder" command: reveals
    /// <c>%LOCALAPPDATA%\code-wicket\logs</c> in File Explorer.
    /// </summary>
    /// <remarks>
    /// That directory is what a bug report needs: <c>engine.log</c>, the raw ACP/MCP byte tees, the
    /// startup-error log, and the rolled-aside history of prior runs (retention is owned by
    /// <c>Core.DiagnosticLog</c>). Getting there by hand means knowing both the path and that it's
    /// under LocalAppData rather than the roaming config directory, so this is purely a shortcut.
    /// <para>
    /// The directory is created if absent rather than reporting "nothing to see": logs are written
    /// lazily, so on a clean install the folder legitimately may not exist yet, and an empty folder is
    /// a truer answer than an error. Always enabled for the same reason - unlike Restart there's no
    /// live object to depend on, and the startup-error log is written by the very failure that would
    /// stop a chat window from existing.
    /// </para>
    /// </remarks>
    internal sealed class OpenLogsCommand
    {
        public const int CommandId = 0x0103;

        private readonly AsyncPackage _package;

        private OpenLogsCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package;
            var commandId = new CommandID(ShowChatWindowCommand.CommandSet, CommandId);
            commandService.AddCommand(new MenuCommand(Execute, commandId));
        }

        /// <summary>Kept alive for the lifetime of the package (the registered command holds the handler).</summary>
        public static OpenLogsCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var commandService = (OleMenuCommandService)await package.GetServiceAsync(typeof(IMenuCommandService));
            Instance = new OpenLogsCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            // RunAsync/FileAndForget like its siblings, so a failure lands in the activity log instead
            // of throwing out of the command dispatcher. No UI thread needed - ShellExecute doesn't
            // touch VS state - but the whole handler is trivial, so it stays on the calling thread.
            _package.JoinableTaskFactory.RunAsync(() =>
            {
                var directory = ExtensionConfig.LogDirectory;
                Directory.CreateDirectory(directory);

                // UseShellExecute with the directory as the target: Explorer opens it, and there's no
                // command line to quote (the path contains the user name, so it may contain spaces).
                // Start returns null when an existing Explorer window handles it - `using` tolerates that.
                using (Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true }))
                {
                }

                return Task.CompletedTask;
            }).FileAndForget("code-wicket/openlogs");
        }
    }
}
