using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using CodeWicket.Core;
using Task = System.Threading.Tasks.Task;

namespace CodeWicket.Ide
{
    /// <summary>
    /// Surfaces ACP permission requests as a VS message box: a simple Yes/No prompt, and the fallback
    /// <c>UiPermissionRouter</c> uses when no chat view is attached to answer in its banner.
    /// </summary>
    public sealed class VsPermissionHandler : IPermissionHandler
    {
        private readonly IServiceProvider _serviceProvider;

        public VsPermissionHandler(IServiceProvider serviceProvider) => _serviceProvider = serviceProvider;

        public async Task<PermissionDecision> RequestAsync(PermissionRequest request, CancellationToken cancellationToken = default)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var allow = request.Options.FirstOrDefault(o =>
                o.Kind is PermissionOptionKind.AllowOnce or PermissionOptionKind.AllowAlways);
            var reject = request.Options.FirstOrDefault(o =>
                o.Kind is PermissionOptionKind.RejectOnce or PermissionOptionKind.RejectAlways);

            var message = string.IsNullOrEmpty(request.Detail)
                ? request.Title
                : request.Title + "\n\n" + request.Detail;

            var result = VsShellUtilities.ShowMessageBox(
                _serviceProvider,
                message,
                Branding.ProductName,
                OLEMSGICON.OLEMSGICON_QUERY,
                OLEMSGBUTTON.OLEMSGBUTTON_YESNO,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);

            var allowed = result == (int)VSConstants.MessageBoxResult.IDYES;
            var chosen = allowed ? allow : reject;
            return new PermissionDecision(chosen?.OptionId ?? (allowed ? "allow" : "reject"));
        }
    }
}
