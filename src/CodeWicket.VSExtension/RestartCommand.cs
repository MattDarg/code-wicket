using System;
using System.ComponentModel.Design;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace CodeWicket.VSExtension
{
    /// <summary>
    /// The "Extensions &gt; Code Wicket &gt; Restart (apply settings)" command: rebuilds the chat
    /// tool window's session in place, which relaunches the engine process.
    /// </summary>
    /// <remarks>
    /// Most settings are consumed when the window opens - provider registration
    /// (KiroEnabled/ClaudeCodeEnabled), custom ACP agents, EngineEnvironment and every other CWKT_*
    /// value are forwarded as environment on the engine process at launch, so nothing short of a new
    /// engine picks them up. That's exactly what the settings descriptions mean by "applies when the
    /// chat window next opens"; this command is that reopen, without hunting for the window.
    /// <para>
    /// This used to close the frame and reopen it, on the reasoning that <see cref="ChatToolWindow"/>
    /// builds its whole graph in one initialization path and tears it down in Dispose, so the frame
    /// lifecycle already WAS the restart. That reasoning was wrong, and the command silently did
    /// nothing: VS caches tool-window panes for the package's lifetime and closing a tool window's
    /// frame only HIDES it, so the pane is never disposed (until VS shuts down) and
    /// <c>ShowToolWindowAsync</c> handed back the same live one. The engine - and the agent CLI under
    /// it - survived, while the window blinked convincingly. Confirmed 2026-07-28: with an updated
    /// agent CLI on disk, Restart kept serving the old binary and only a full VS restart picked up the
    /// new one.
    /// </para>
    /// <para>
    /// So the restart is explicit (<see cref="ChatToolWindow.RestartAsync"/>), sharing ONE teardown
    /// implementation with Dispose rather than duplicating it. The conversation isn't lost: the
    /// rebuilt window restores this workspace's most recent session, exactly as it does on open.
    /// </para>
    /// </remarks>
    internal sealed class RestartCommand
    {
        public const int CommandId = 0x0102;

        private readonly AsyncPackage _package;

        private RestartCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package;
            var commandId = new CommandID(ShowChatWindowCommand.CommandSet, CommandId);
            var command = new OleMenuCommand(Execute, commandId);
            // Nothing to restart when the window was never opened - the chat command right above it
            // in the menu is what you want then.
            command.BeforeQueryStatus += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                ((OleMenuCommand)s).Enabled = FindOpenChatWindow() is not null;
            };
            commandService.AddCommand(command);
        }

        /// <summary>Kept alive for the lifetime of the package (the registered command holds the handler).</summary>
        public static RestartCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var commandService = (OleMenuCommandService)await package.GetServiceAsync(typeof(IMenuCommandService));
            Instance = new RestartCommand(package, commandService);
        }

        private ChatToolWindow FindOpenChatWindow()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return _package.FindToolWindow(typeof(ChatToolWindow), id: 0, create: false) as ChatToolWindow;
        }

        private void Execute(object sender, EventArgs e)
        {
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync(_package.DisposalToken);

                if (FindOpenChatWindow() is not ChatToolWindow window)
                    return;

                // Bring the pane forward first: the rebuild reports its progress (and any failure) in
                // the window, which is no use behind another tab - or hidden, which is what a "closed"
                // tool window actually is.
                if (window.Frame is IVsWindowFrame frame)
                    ErrorHandler.ThrowOnFailure(frame.Show());

                // Kills the engine process (and the agent CLI under it), then re-runs initialization,
                // which is what re-reads the settings and forwards them onto the new engine.
                await window.RestartAsync();
            }).FileAndForget("code-wicket/restart");
        }
    }
}
