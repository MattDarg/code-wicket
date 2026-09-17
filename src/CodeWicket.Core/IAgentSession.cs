using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CodeWicket.Core
{
    /// <summary>
    /// A live conversation with an agent backend. A session owns the backend process/connection
    /// and is disposed when the conversation ends.
    /// </summary>
    public interface IAgentSession : IAsyncDisposable
    {
        /// <summary>Stable id for this conversation, usable to resume later via <see cref="SessionOptions.ResumeConversationId"/>.</summary>
        string ConversationId { get; }

        /// <summary>
        /// Sends a prompt and streams the resulting agent turn as a sequence of <see cref="AgentEvent"/>s.
        /// The sequence completes with a <see cref="AgentEvent.TurnCompleted"/> when the turn ends.
        /// </summary>
        IAsyncEnumerable<AgentEvent> SendAsync(PromptInput prompt, CancellationToken cancellationToken = default);

        /// <summary>Requests cancellation of the in-flight turn, if any.</summary>
        Task CancelAsync();

        /// <summary>
        /// Switches the model used for the rest of this conversation, without ending it. Only meaningful
        /// when the provider advertises <see cref="AgentCapabilities.ModelSelection"/>; other backends
        /// may no-op or throw. Call between turns (not while a prompt is streaming).
        /// </summary>
        Task SetModelAsync(string modelId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Delivers a follow-up message into the turn that is currently running, so the user can
        /// course-correct without cancelling. Only meaningful when the provider advertises
        /// <see cref="AgentCapabilities.Steering"/>; backends without it return
        /// <see cref="SteerOutcome.NotSupported"/> and change nothing.
        /// <para>
        /// The steered message's output does NOT come back through this call — it streams as ordinary
        /// session updates, either into the running turn's <see cref="SendAsync"/> sequence
        /// (<see cref="SteerOutcome.Injected"/>) or out-of-turn
        /// (<see cref="SteerOutcome.StartedNewTurn"/>). The returned outcome says only where the
        /// message landed.
        /// </para>
        /// </summary>
        Task<SteerOutcome> SteerAsync(PromptInput prompt, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Where a steered message landed. Both real outcomes are successes: the protocol requires that a
    /// steer which arrives just as the turn ends is neither dropped nor reported as an error, so the
    /// caller never has to handle that race itself — only to know which of the two happened.
    /// </summary>
    public enum SteerOutcome
    {
        /// <summary>The backend does not support steering; nothing was sent.</summary>
        NotSupported = 0,

        /// <summary>Delivered into the running turn. Its output streams as part of that turn.</summary>
        Injected = 1,

        /// <summary>
        /// The turn ended before the steer arrived, so the backend started a fresh turn with the
        /// message. Its output arrives with no turn of ours open — see the out-of-turn sink, which
        /// must already be listening, because the updates can precede this outcome on the wire.
        /// </summary>
        StartedNewTurn = 2,
    }

    /// <summary>
    /// Implemented by sessions whose backend only reveals its model list once the session is open
    /// (e.g. Claude Code, whose models arrive in the ACP <c>session/new</c> config options and depend
    /// on the user's Claude configuration). The engine surfaces these back to the host so the picker
    /// can show the real, live model list rather than a static seed. Sessions with a statically-known
    /// model list (e.g. Kiro) need not implement this.
    /// </summary>
    public interface IModelDiscovery
    {
        /// <summary>Models the backend reported for this session; empty when none were discovered.</summary>
        IReadOnlyList<ModelInfo> DiscoveredModels { get; }

        /// <summary>The model the session is currently running, if the backend reported one.</summary>
        string? CurrentModelId { get; }
    }

    /// <summary>
    /// Implemented by sessions that resolve their own working directory rather than simply running in
    /// <see cref="SessionOptions.WorkspaceRootPath"/> — see <see cref="WorkspaceRootLocator"/>. The
    /// engine surfaces this to the host so the chat can (a) root the relative paths the agent emits
    /// against the directory the agent is actually in and (b) tell the user when the two differ.
    /// </summary>
    public interface IWorkspaceRootReport
    {
        /// <summary>How the session's working directory was resolved. Never null once the session is open.</summary>
        WorkspaceRootResult WorkspaceRoot { get; }
    }

    /// <summary>
    /// Implemented by sessions that read a project's checked-in settings file (issue #59) and have
    /// something to say to the user about it - a key a repository may never set, a value that was
    /// refused, or a file that could not be parsed.
    /// </summary>
    /// <remarks>
    /// A side interface for the same reason as <see cref="IWorkspaceRootReport"/>: it is a fact about
    /// how this session came to be, known once the session is open, and of no interest to a host whose
    /// backend has no such notion. Empty for the overwhelmingly common case of a project with no
    /// settings file at all, which is why "does not implement this" and "implements it and found
    /// nothing" never need telling apart.
    /// <para>
    /// Deliberately sentences rather than structured data. The host's only job with these is to render
    /// them, and composing them in Core (<c>ProjectSettingsReport</c>) keeps the wording testable
    /// instead of spreading string literals across the engine and the view-model.
    /// </para>
    /// </remarks>
    public interface IProjectSettingsReport
    {
        /// <summary>What the user should be told about this project's settings file. Never null.</summary>
        IReadOnlyList<string> ProjectSettingsNotices { get; }
    }

    /// <summary>
    /// Implemented by sessions that can start fresh when a requested resume turns out to be
    /// unavailable. A stored conversation id the backend no longer knows is an ordinary state — it can
    /// belong to a different agent engine, the backend may have pruned it, or it may never have
    /// persisted because its first turn failed — so refusing to start is the wrong answer: the host has
    /// already replayed the transcript locally and only the BACKEND's copy of the history is lost. The
    /// engine surfaces this so the chat can say so, rather than leaving the user to infer it from the
    /// agent having forgotten the conversation.
    /// </summary>
    public interface IResumeFallbackReport
    {
        /// <summary>Why the resume fell back to a fresh session; null when no resume was requested or
        /// it succeeded.</summary>
        string? ResumeFailureReason { get; }

        /// <summary>
        /// How many MESSAGE frames the backend replayed while loading the requested conversation —
        /// chunks of either side's text, or thoughts; never announcements, and never tool calls, since
        /// a session opening is one and lands inside the load window on Kiro v3. Null when no resume
        /// was requested; <b>zero when the load reported success and nothing came back</b>, which is
        /// the answer <see cref="ResumeFailureReason"/> cannot give (issue #185).
        /// <para>Measured 2026-09-12 (<c>Console resume-unknown-id</c>): Kiro v3 answers an id it no
        /// longer holds — pruned, or from another engine — with a successful <c>session/load</c> that
        /// replays nothing, so the user reads their whole transcript beside an agent that has never
        /// heard of it. The default Kiro engine and Claude Code both refuse such an id with an error.
        /// A count is the one fact that separates the silent case from a load that worked, on any
        /// backend and without knowing which one it is.</para>
        /// </summary>
        int? ReplayedHistoryCount { get; }
    }

    /// <summary>
    /// Implemented by sessions that can hand back the conversation the backend replayed while it
    /// loaded, for a session opened with <see cref="SessionOptions.ImportHistory"/> (issue #108).
    /// <para>Reported the same way <see cref="IResumeFallbackReport"/> is, and for the same reason: it
    /// is a fact about how this session came to be, known once the session is open and of no interest
    /// to a host that did not ask for it. Empty when import was off, when the backend replayed
    /// nothing, or when no resume was requested at all — none of which the reader needs to tell apart,
    /// since all three mean "there is no imported transcript here".</para>
    /// </summary>
    public interface IImportedHistoryReport
    {
        /// <summary>The replayed conversation in wire order, oldest first. Never null.</summary>
        IReadOnlyList<ImportedTurnEntry> ImportedHistory { get; }
    }

    /// <summary>
    /// Implemented by sessions that can enumerate the conversations the backend's own CLI has stored
    /// for a working directory (ACP <c>session/list</c>), so one started in the terminal can be picked
    /// up in the IDE (issue #108).
    /// <para>Deliberately answered by a session rather than by a provider, even though no conversation
    /// is involved: the call needs a completed <c>initialize</c> handshake and nothing else, and the
    /// session is what owns a handshake. A provider that wants to offer this opens a short-lived
    /// session, handshakes WITHOUT opening a conversation, asks, and disposes.</para>
    /// </summary>
    public interface IBackendSessionList
    {
        /// <summary>
        /// Conversations the backend has stored for <paramref name="cwd"/>, newest first where the
        /// backend reports times. Empty - never an exception - when the backend did not advertise the
        /// capability: a picker with nothing to show is a fine outcome, a broken popup is not.
        /// <para><b>Always pass a cwd, and an ABSOLUTE one.</b> Null is not "no filter": read out of
        /// Kiro's v3 agent, an absent cwd selects workspace-DISCOVERY semantics and walks every
        /// workspace bucket on the machine, so a caller expecting "this folder" would silently offer
        /// the user every conversation they have ever had anywhere. A relative or empty cwd is worse
        /// than useless - the same engine rejects it with InvalidParams rather than returning an empty
        /// list. Callers resolve the agent's own root (issue #54) and hand it over whole.</para>
        /// </summary>
        Task<IReadOnlyList<BackendSessionInfo>> ListSessionsAsync(string? cwd, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Implemented by sessions that can say what their handshake settled, for the host's session
    /// information panel (issue #160). Reported the same way <see cref="IWorkspaceRootReport"/> is,
    /// and probed the same way — a session that cannot answer simply does not implement it, and the
    /// host renders those rows as unknown rather than as absent facts.
    /// <para><b>The implementation must build its answer on every read.</b> See
    /// <see cref="SessionNegotiation"/> for why: the agent's version can arrive after the handshake
    /// finishes, so a cached record silently reports whatever happened to be known at one instant.</para>
    /// </summary>
    public interface ISessionNegotiationReport
    {
        /// <summary>What this session's handshake settled. Never null once the session is open.</summary>
        SessionNegotiation Negotiation { get; }
    }

    /// <summary>
    /// One conversation in the backend CLI's own store. Everything but the id is optional because the
    /// backend decides what it keeps: <paramref name="Title"/> is whatever it chose to name the
    /// conversation (Claude generates a summary; Kiro takes the first user message verbatim), and
    /// <paramref name="UpdatedAt"/> is absent on a backend that does not track it - in which case the
    /// host must not invent an order.
    /// </summary>
    public sealed record BackendSessionInfo(string Id, string? Title, DateTimeOffset? UpdatedAt, string? Cwd)
    {
        /// <summary>
        /// When the conversation was created, where the backend reports it. Absent on a backend that
        /// does not — claude-agent-acp holds the SDK's <c>createdAt</c> and drops it before the wire,
        /// so this is null for every Claude row and populated from <c>_meta.kiro.createdAt</c> on
        /// Kiro v3. Read only so that a conversation created and never used can be recognised; see
        /// <see cref="BackendSessionFilter"/>.
        /// </summary>
        public DateTimeOffset? CreatedAt { get; init; }
    }

    /// <summary>
    /// Recognises a conversation the backend's store holds that was <b>created and never used</b>
    /// (issue #108 follow-on).
    /// <para><b>Most of them are ours.</b> The warm start (#19) opens a session on every chat-window
    /// open so the backend's own MCP servers are up before the first prompt, and a window the user
    /// never prompts leaves that session behind: real in the backend's store, empty, and never saved
    /// here — so the dedupe against our saved transcripts cannot reach it. Measured on this repo's
    /// workspace 2026-09-03, Kiro v3: <b>9 of 31</b> listed conversations.</para>
    /// <para><b>Neither backend sends a message count</b>, so this cannot be asked directly.
    /// <c>session/list</c> carries `sessionId`, `cwd`, `title`, `updatedAt` and — Kiro only —
    /// `_meta.kiro`; the Claude adapter forwards 4 of `SDKSessionInfo`'s 10 fields and `fileSize`,
    /// `firstPrompt` and `createdAt` are all among the six it drops. What is left is two weak signals,
    /// and this requires <b>both</b>.</para>
    /// </summary>
    /// <remarks>
    /// <para><b>Why both, and why neither alone.</b> The title alone is a <i>display string</i>, and
    /// resting a decision on one is the mistake the section's own heading made for three months
    /// ("From the CLI" was true only by accident of a store split). The gap alone rests on
    /// <c>updatedAt</c>, which the backends fill from a file mtime under a field the protocol
    /// documents as last activity — a sync pass moves it. Requiring both means a hide needs two
    /// independent agreements, and it means the failure directions are the safe ones: a moved mtime
    /// widens the gap and <i>shows</i> a row, a renamed or localized placeholder <i>shows</i> a row.
    /// Neither can lose a conversation, and the host offers to show the hidden ones regardless.</para>
    /// <para><b>A null title counts as a placeholder</b> — it says the backend never named this, which
    /// is the same signal in its strongest form. That is only safe because of the AND: Claude leaves a
    /// title absent until its summary has been generated, and Claude reports no <c>CreatedAt</c> at
    /// all, so no Claude row can ever be hidden by this.</para>
    /// </remarks>
    public static class BackendSessionFilter
    {
        /// <summary>
        /// How close <c>createdAt</c> and <c>updatedAt</c> have to be to mean "nothing happened after
        /// this was created". Measured, the empty rows sat at <b>~16 ms</b> and the used ones at a
        /// second and above, so this is generous by two orders of magnitude on purpose: it is the
        /// corroborator, not the test, and the title carries the claim.
        /// </summary>
        public static readonly TimeSpan UnusedWindow = TimeSpan.FromSeconds(5);

        // What a backend calls a conversation it has not named yet. Kiro's, verified on the wire
        // 2026-09-03; Claude has none, naming a session only once it has something to summarize.
        // Compared trimmed and case-insensitively, and deliberately a SET rather than one string, so
        // a second backend's placeholder is an entry rather than a branch.
        private static readonly string[] Placeholders = { "New Session" };

        /// <summary>
        /// True when this row was created and never used. Both signals must agree; see the type's
        /// remarks for why neither is trusted alone.
        /// </summary>
        public static bool IsUnused(string? title, DateTimeOffset? createdAt, DateTimeOffset? updatedAt)
        {
            // No creation time means no second opinion, so nothing is hidden. That is the whole of
            // Claude's behaviour here, and it is a refusal rather than a gap to be filled: with one
            // signal this would be a display string deciding on its own.
            if (createdAt is not { } created || updatedAt is not { } updated)
                return false;

            if (!IsPlaceholderTitle(title))
                return false;

            // Absolute, so a clock that reports the two in the wrong order is still "no gap" rather
            // than a large negative one that would read as heavily used.
            var gap = updated - created;
            return (gap < TimeSpan.Zero ? gap.Negate() : gap) < UnusedWindow;
        }

        /// <summary>True when the title is a backend's own "not named yet", or there is none.</summary>
        public static bool IsPlaceholderTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return true;

            foreach (var placeholder in Placeholders)
            {
                if (string.Equals(title!.Trim(), placeholder, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
