using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using CodeWicket.Core;
using Task = System.Threading.Tasks.Task;

namespace CodeWicket.VSExtension
{
    /// <summary>
    /// The "View &gt; Code Wicket Chat" command: shows the tool window. Directly under View rather
    /// than View &gt; Other Windows - it's the product's primary window, and it's where Copilot Chat
    /// sits. Also placed in Extensions &gt; Code Wicket via a <c>CommandPlacement</c>.
    /// </summary>
    internal sealed class ShowChatWindowCommand
    {
        public static readonly Guid CommandSet = new Guid("2a3dd172-f5cc-4371-ada2-233c0ef2448d");
        public const int CommandId = 0x0100;

        private readonly AsyncPackage _package;

        private ShowChatWindowCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package;
            var commandId = new CommandID(CommandSet, CommandId);
            commandService.AddCommand(new MenuCommand(Execute, commandId));
        }

        /// <summary>Kept alive for the lifetime of the package (the registered command holds the handler).</summary>
        public static ShowChatWindowCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var commandService = (OleMenuCommandService)await package.GetServiceAsync(typeof(IMenuCommandService));
            Instance = new ShowChatWindowCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync(_package.DisposalToken);
                var window = await _package.ShowToolWindowAsync(
                    typeof(ChatToolWindow), id: 0, create: true, cancellationToken: _package.DisposalToken);
                if (window?.Frame is null)
                    throw new NotSupportedException($"Cannot create the {Branding.ProductName} tool window.");

                // An already-open pane may be docked behind another tab, where ShowToolWindowAsync
                // leaves it: Show() brings it forward and gives it activation. This is what makes the
                // keyboard shortcut a way *in* rather than a no-op when the window already exists.
                if (window.Frame is IVsWindowFrame frame)
                    ErrorHandler.ThrowOnFailure(frame.Show());
                (window as ChatToolWindow)?.FocusInput();
            }).FileAndForget("code-wicket/showchatwindow");
        }
    }
}
