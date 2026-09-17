using System;
using System.Threading;
using System.Threading.Tasks;
using CodeWicket.Core;
using CodeWicket.Core.Ide;
using CodeWicket.Ipc;

namespace CodeWicket.Shell
{
    /// <summary>
    /// An <see cref="IPermissionHandler"/> that routes the prompt to the chat UI (an inline banner)
    /// when a UI prompt is attached, and otherwise falls back to a host-provided handler (the VS
    /// message box, or auto-allow). The permission round-trip already terminates in the shell —
    /// the same process as the chat tool window — so no extra IPC is needed: the agent stays blocked
    /// on the ACP request while the user answers in the banner. The request/decision cross to the UI
    /// as the existing wire DTOs so the UI need not depend on Core.
    /// </summary>
    public sealed class UiPermissionRouter : IPermissionHandler
    {
        private readonly IPermissionHandler _fallback;

        public UiPermissionRouter(IPermissionHandler fallback) => _fallback = fallback;

        /// <summary>
        /// The UI prompt: given the request, returns the user's decision. Set by the host once the
        /// chat view-model exists. Null = no UI attached, so use the fallback handler.
        /// </summary>
        public Func<PermissionRequestDto, CancellationToken, Task<PermissionDecisionDto>>? Prompt { get; set; }

        public async Task<PermissionDecision> RequestAsync(PermissionRequest request, CancellationToken cancellationToken = default)
        {
            var prompt = Prompt;
            if (prompt is null)
                return await _fallback.RequestAsync(request, cancellationToken).ConfigureAwait(false);

            var decision = await prompt(DtoMapping.ToDto(request), cancellationToken).ConfigureAwait(false);
            return DtoMapping.ToDecision(decision);
        }
    }

    /// <summary>
    /// Wraps an <see cref="IIdeServices"/> but substitutes its <see cref="IIdeServices.Permissions"/>,
    /// so the host can route permission prompts (e.g. to the chat banner) without rebuilding the
    /// underlying VS-backed services.
    /// </summary>
    public sealed class PermissionOverrideIdeServices : IIdeServices
    {
        public PermissionOverrideIdeServices(IIdeServices inner, IPermissionHandler permissions)
        {
            Workspace = inner.Workspace;
            Edits = inner.Edits;
            Tools = inner.Tools;
            Permissions = permissions;
        }

        public IWorkspaceContext Workspace { get; }
        public IEditApplier Edits { get; }
        public IToolCatalog Tools { get; }
        public IPermissionHandler Permissions { get; }
    }
}
