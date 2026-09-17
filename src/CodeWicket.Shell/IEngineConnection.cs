using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CodeWicket.Ipc;

namespace CodeWicket.Shell
{
    /// <summary>
    /// The shell-side view of the engine: session control plus the streamed agent events.
    /// Implemented by <see cref="EngineClient"/>; depended on by the UI so it can be faked in tests
    /// and re-implemented by hosts. Events are raised off the UI thread — subscribers must marshal.
    /// </summary>
    /// <summary>
    /// Implemented by an engine connection that can say where a backend WOULD run for a workspace,
    /// without opening a session (issue #59).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A SIDE interface rather than a member of <see cref="IEngineConnection"/>, following the same
    /// rule the session-side reports already use (<c>IWorkspaceRootReport</c>, <c>IModelDiscovery</c>,
    /// <c>IBackendSessionCatalog</c>): a connection with no engine behind it has no honest answer, and
    /// "returns empty" is a worse contract than "does not implement this". It also keeps the
    /// twenty-odd test doubles of <see cref="IEngineConnection"/> compiling unchanged, which matters
    /// more than it sounds - churning them all would bury this change in noise.
    /// </para>
    /// <para>
    /// A host that cannot ask degrades to the previous behaviour: it starts the session, the engine's
    /// own guard refuses the cross-root resume, and the user is told after the fact instead of before
    /// it. Worse, but not wrong - which is what makes the optional shape safe here.
    /// </para>
    /// </remarks>
    public interface IAgentRootQuery
    {
        /// <summary>Where <paramref name="request"/>'s provider would run. Never throws for an ordinary
        /// unknown - a provider that resolves no root answers with the path it was given.</summary>
        Task<ResolveAgentRootResponse> ResolveAgentRootAsync(
            ResolveAgentRootRequest request, CancellationToken cancellationToken = default);
    }

    public interface IEngineConnection
    {
        /// <summary>Raised on every streamed agent event (text/tool/edit/turn/error).</summary>
        event Action<AgentEventDto>? AgentEvent;

        /// <summary>
        /// Raised when a backend finishes discovering its real model list AFTER
        /// <see cref="ListProvidersAsync"/> already answered from last run's cache — and only when it
        /// differs from what the picker was filled with. Session-independent: the discovery happens
        /// while the window is still opening.
        /// </summary>
        event Action<ProviderModelsDto>? ProviderModelsRefreshed;

        /// <summary>Lists the backends the engine can drive and the models each advertises (for the picker).</summary>
        Task<ListProvidersResponse> ListProvidersAsync(CancellationToken cancellationToken = default);

        Task<StartSessionResponse> StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default);

        /// <summary><paramref name="attachments"/> is the message's binary content — today a pasted
        /// image (issue #118). Only send it when the session's
        /// <see cref="StartSessionResponse.SupportsImages"/> said the backend takes one.</summary>
        Task<PromptResponse> PromptAsync(
            string text,
            IReadOnlyList<PromptAttachmentDto>? attachments = null,
            CancellationToken cancellationToken = default);

        Task CancelAsync(CancellationToken cancellationToken = default);

        /// <summary>Delivers a follow-up message into the turn already running, for backends whose
        /// <see cref="StartSessionResponse.SupportsSteering"/> said they accept one.</summary>
        Task<SteerResponse> SteerAsync(
            string text,
            IReadOnlyList<PromptAttachmentDto>? attachments = null,
            CancellationToken cancellationToken = default);

        /// <summary>Switches the active session's model in place, keeping the conversation (for providers
        /// that advertise the <c>ModelSelection</c> capability).</summary>
        Task SetModelAsync(string modelId, CancellationToken cancellationToken = default);

        /// <summary>Summarizes a prior conversation transcript in an isolated engine-side pass (for
        /// "resume from summary"); does not affect the active session.</summary>
        Task<SummarizeResponse> SummarizeAsync(SummarizeRequest request, CancellationToken cancellationToken = default);

        /// <summary>Lists the conversations the backend's own CLI has stored for a workspace, so one
        /// begun in the terminal can be picked up here (issue #108). Reuses the live session's
        /// connection when it belongs to the same backend and is idle, and otherwise opens a
        /// short-lived one — either way the active session is left alone.
        /// <para>An unsupported backend answers <see cref="ListBackendSessionsResponse.Supported"/>
        /// false with a reason rather than throwing: the caller is a picker.</para></summary>
        Task<ListBackendSessionsResponse> ListBackendSessionsAsync(
            ListBackendSessionsRequest request, CancellationToken cancellationToken = default);

        /// <summary>Pulls one page of the conversation the last
        /// <see cref="StartSessionAsync"/> imported, for a session started with
        /// <see cref="StartSessionRequest.ImportHistory"/> (issue #108). Call it until as many entries
        /// as <see cref="StartSessionResponse.ImportedHistoryCount"/> have been read; the transcript
        /// is paged rather than returned with the session because a large result cannot survive the
        /// formatter — see <see cref="TakeImportedHistoryRequest"/>.</summary>
        Task<TakeImportedHistoryResponse> TakeImportedHistoryAsync(
            TakeImportedHistoryRequest request, CancellationToken cancellationToken = default);

        /// <summary>What the live session's handshake settled, for the session information panel
        /// (issue #160). Pulled when the panel opens rather than carried on
        /// <see cref="StartSessionAsync"/>, so it reads current state and so the agent version — which
        /// for some backends arrives from a probe session start does not wait for — is never captured
        /// early and left wrong.
        /// <para>Null when no session is open. That is a different answer from a session that reported
        /// nothing, and the two must not be collapsed.</para></summary>
        Task<SessionInfoResponse?> SessionInfoAsync(CancellationToken cancellationToken = default);
    }
}
