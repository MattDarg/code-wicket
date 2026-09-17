using System.Collections.Generic;

namespace CodeWicket.Core
{
    /// <summary>
    /// A single item in the streamed output of an agent turn. This is a closed hierarchy
    /// (the constructor is private, so the only cases are the nested records below), which
    /// lets consumers switch over it exhaustively.
    /// </summary>
    /// <remarks>
    /// Output only. Anything that needs a response from the host (permission prompts,
    /// filesystem reads/writes, tool invocations) is modelled as a callback on
    /// <see cref="CodeWicket.Core.Ide.IIdeServices"/>, not as an event.
    /// </remarks>
    public abstract record AgentEvent
    {
        private AgentEvent()
        {
        }

        /// <summary>A chunk of assistant-visible text.</summary>
        public sealed record AssistantTextDelta(string Text) : AgentEvent;

        /// <summary>A chunk of the model's reasoning/thinking, when the backend surfaces it.</summary>
        public sealed record ThinkingDelta(string Text) : AgentEvent;

        /// <summary>
        /// The agent began a tool call. <paramref name="RawInputJson"/> is the raw arguments as JSON text.
        /// <paramref name="ToolName"/> is the backend's namespaced name when the call is an MCP tool
        /// (<c>@server/tool</c> / <c>mcp__server__tool</c>), null for a backend built-in — which tells a
        /// host that <paramref name="RawInputJson"/> follows a THIRD PARTY's schema, so no key in it can
        /// be assumed to mean what the same key means on a built-in (issue #131).
        /// <para><paramref name="ParentToolCallId"/> is the sub-agent invocation this call was made
        /// BY — null means the agent the user is talking to made it, which is the unchanged case and
        /// the one an unrecognised backend must land in. Hosts nest the row under that parent; a
        /// parent they don't know is not an error, it is a top-level row (issue #125).</para>
        /// <para><paramref name="IsSubagentLaunch"/> marks the call that STARTS a sub-agent (Claude's
        /// <c>Task</c>, Kiro v3's <c>invoke_subagent_*</c>) — the row other calls nest under. It is a
        /// fact about the call, not about the backend: both spellings normalize onto it here so no host
        /// learns either one.</para>
        /// </summary>
        public sealed record ToolCallStarted(
            string ToolCallId, string Title, string? Kind, string? RawInputJson, string? ToolName = null,
            string? ParentToolCallId = null, bool IsSubagentLaunch = false) : AgentEvent;

        /// <summary>
        /// Enrichment for an in-flight tool call. Some backends (e.g. the Claude Code adapter) start
        /// a tool call with a placeholder title and empty input, then follow up with the real title
        /// and arguments once the input has finished streaming; hosts should merge these fields into
        /// the already-rendered row for <paramref name="ToolCallId"/>. Null fields mean "unchanged".
        /// <para><paramref name="ParentToolCallId"/> and <paramref name="IsSubagentLaunch"/> carry the
        /// same meaning as on <see cref="ToolCallStarted"/>. They repeat here because a backend may not
        /// have decided either by the time it opens the call — Claude's <c>Task</c> opens as a bare
        /// <c>{}</c> and streams its arguments in afterwards.</para>
        /// </summary>
        public sealed record ToolCallUpdated(
            string ToolCallId, string? Title, string? Kind, string? RawInputJson, string? ToolName = null,
            string? ParentToolCallId = null, bool IsSubagentLaunch = false) : AgentEvent;

        /// <summary>Incremental progress for an in-flight tool call.</summary>
        public sealed record ToolCallProgress(string ToolCallId, string? Message) : AgentEvent;

        /// <summary>
        /// A chunk of live output from an in-flight tool call (a running shell command's
        /// stdout/stderr, streamed as it happens — Kiro sends these as status-less
        /// <c>tool_call_update</c> content entries). Hosts append it to the running row so the user
        /// can watch the command; the completion's <see cref="ToolCallCompleted.ResultText"/> is the
        /// authoritative superset and replaces the accumulated live text. Transient — not part of the
        /// durable transcript (the completion carries the record).
        /// </summary>
        public sealed record ToolCallOutputChunk(string ToolCallId, string Text) : AgentEvent;

        /// <summary>
        /// A tool call finished. <paramref name="ResultText"/> is a human-readable result (a command's
        /// stdout, etc.), if any. <paramref name="ErrorText"/> is a separate error stream (a command's
        /// stderr) surfaced apart from the result because it's a distinct stream, not positioned relative
        /// to the result — null when there's none. <paramref name="Success"/> reflects semantic success
        /// (a shell command that ran but exited non-zero is a failure even though the call "completed").
        /// </summary>
        /// <remarks>
        /// <paramref name="LaunchedInBackground"/> is the one case where the backend's own "completed"
        /// must NOT be shown as one: an asynchronous sub-agent reports the call finished the instant it
        /// is LAUNCHED, and its work then runs — and streams child tool calls — for minutes afterwards.
        /// Measured on claude-agent-acp 0.70.0, the launch is marked structurally
        /// (<c>_meta.claudeCode.toolResponse.status == "async_launched"</c>), so this is read off the
        /// wire rather than sniffed out of the result text. Nothing here says when the sub-agent
        /// FINISHED, because no backend tells us: the returning report is announced with no id tying it
        /// to the agent that sent it, and return order is not launch order (measured: launched 1,2,3 →
        /// returned 1,3,2), so a host must not guess. It renders a distinct "launched" state and waits
        /// (issue #125).
        /// <para>
        /// <paramref name="HostAuthored"/> says this completion is the HOST's own delivery of its own
        /// tool result — not a frame the agent sent, and not the agent's echo of one. It exists because
        /// <see cref="CodeWicket.Core.Ide.StructuredToolResult"/>'s marker is a routing hint and NOT
        /// authentication: the text it inspects is the result text of ANY tool, so an agent whose shell
        /// or MCP tool prints our marker would earn a host-styled test-results or breakpoint card for a
        /// run that never happened — the artifact the user reads to decide whether the agent's changes
        /// are safe to accept. Provenance cannot be recovered from the bytes, so it is carried BESIDE
        /// them, set only where the host holds the result it produced itself
        /// (<c>ShellRpcTarget.ToolsInvokeAsync</c>). Default false: an event that does not say so is not
        /// ours, and nothing on the wire can set it — there is no DTO-to-event reverse mapping.
        /// </para>
        /// </remarks>
        public sealed record ToolCallCompleted(
            string ToolCallId, bool Success, string? ResultText, string? ErrorText = null,
            bool LaunchedInBackground = false, bool HostAuthored = false) : AgentEvent;

        /// <summary>
        /// The agent changed a file. The write may have been routed through
        /// <see cref="CodeWicket.Core.Ide.IEditApplier"/> or performed by the agent itself; either
        /// way this event carries the before/after text so the host can render a clickable diff
        /// (<see cref="CodeWicket.Core.Ide.IEditApplier.ShowDiffPreviewAsync"/>).
        /// <paramref name="ToolCallId"/> ties the edit to its tool call when known: backends may
        /// re-send the same file's diff as the call finalizes (e.g. a provisional "before" repaired
        /// on the update), and hosts use the id+path to update the earlier row instead of adding one.
        /// <paramref name="Line"/> is the 1-based line the backend reported the edit at (ACP
        /// <c>locations[].line</c>), when supplied — used to disambiguate/sanity-check where a
        /// hunk-only diff is spliced back into the full file for preview; null when unreported.
        /// <paramref name="Intent"/> is the agent's stated reason for the edit (the tool call's
        /// <c>__tool_use_purpose</c>/<c>description</c>), and <paramref name="Operation"/> the backend's
        /// edit-operation name (Kiro's <c>strReplace</c>/<c>create</c>/…); both null when unsupplied.
        /// Normalized here (out of the backend-specific rawInput) so the host shows them as detail
        /// without decoding provider shapes — the diff text itself stays for the native diff viewer.
        /// </summary>
        /// <remarks>
        /// <paramref name="ParentToolCallId"/> is the sub-agent invocation that made this edit, with the
        /// same meaning as on <see cref="ToolCallStarted"/>. It matters only for the edit that renders
        /// as its OWN card: where the backend opens a real tool call (Claude's <c>Edit</c>) the diff
        /// folds into that row, which is already nested, and this is never consulted. Where it does not
        /// (Kiro v3 carries the diff on the opening frame, so no row is ever built) the card is the only
        /// representation there is — and without this it would show a sub-agent's change as though the
        /// main agent had made it (issue #125).
        /// </remarks>
        public sealed record EditProposed(
            string Path, string OldText, string NewText, string? ToolCallId = null, int? Line = null,
            string? Intent = null, string? Operation = null, string? ParentToolCallId = null) : AgentEvent;

        /// <summary>
        /// The agent's task/plan list was created or updated. Backends that expose a plan (e.g. Kiro's
        /// task-list tool) send the full list on every change, so the host renders a single live
        /// checklist that ticks items off rather than a row per update. An <em>empty</em>
        /// <paramref name="Items"/> list means the plan finished (Kiro disposes the task list when its
        /// last item completes): the host marks every item on the current card completed.
        /// </summary>
        public sealed record PlanUpdated(string? Title, IReadOnlyList<PlanItem> Items) : AgentEvent;

        /// <summary>
        /// A snapshot of the backend's sub-agent roster (e.g. Kiro's parallel "agent crew": each
        /// sub-agent runs as its own ACP session multiplexed over the same connection). Backends send
        /// the current set on every change, so hosts render one live card upserted by
        /// <see cref="SubagentInfo.SessionId"/> — an empty snapshot means the crew was disposed, NOT
        /// that the rows should vanish, so hosts never remove rows.
        /// </summary>
        public sealed record SubagentsUpdated(IReadOnlyList<SubagentInfo> Agents) : AgentEvent;

        /// <summary>
        /// The final result a sub-agent returned to its orchestrating agent (the one bounded,
        /// user-meaningful artifact of a sub-agent's session — its intermediate streaming is
        /// deliberately not surfaced). May be re-sent as the sub-agent's wrap-up call finalizes;
        /// hosts update in place by <paramref name="SubagentSessionId"/>.
        /// </summary>
        public sealed record SubagentResult(string SubagentSessionId, string Text) : AgentEvent;

        /// <summary>
        /// One of the backend's own MCP servers finished connecting and its tools became available
        /// (e.g. Kiro's <c>_kiro.dev/mcp/server_initialized</c>). Servers connect asynchronously after
        /// the session opens — a slow one (remote, proxied) can miss the first prompt's tool snapshot —
        /// so hosts surface these as status notices: their position in the transcript shows which
        /// prompts each server's tools could have served.
        /// </summary>
        public sealed record McpServerConnected(string ServerName) : AgentEvent;

        /// <summary>
        /// The backend's view of the user's own MCP servers, as a whole. Distinct from
        /// <see cref="McpServerConnected"/> and deliberately not a widening of it: that event is the
        /// issue-#19 readiness NOTICE, which must fire exactly once per server and whose value is its
        /// position in the transcript, whereas this repeats by design as states change.
        /// <para><b>Live state. Never recorded, never replayed</b> — a restored transcript must not
        /// claim a connection it does not have.</para>
        /// </summary>
        public sealed record McpRosterUpdated(McpRoster Roster) : AgentEvent;

        /// <summary>
        /// What OUR OWN MCP bridge is doing — the one MCP fact that needs no backend cooperation,
        /// because we host it (see the engine's <c>McpToolServer</c>). It is therefore the only MCP
        /// information available on a backend that reports none of its own, such as Claude Code.
        /// <para>Live state, on the same terms as <see cref="McpRosterUpdated"/>.</para>
        /// </summary>
        public sealed record McpBridgeStatusUpdated(McpBridgeStatus Bridge) : AgentEvent;

        /// <summary>A recoverable or fatal error within the session.</summary>
        public sealed record SessionError(string Message, string? Details) : AgentEvent;

        /// <summary>
        /// Something the BACKEND asked us to tell the user - a refusal, a limit, a compaction - as
        /// opposed to something that failed here (issues #208, #85).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Deliberately not <see cref="SessionError"/>.</b> That says the session hit an error; this
        /// says the backend has something to report, which is often neither fatal nor even a problem.
        /// A rate limit is a delay, a compaction is routine housekeeping, and a monthly usage limit is
        /// a hard stop - and all three arrived, in the field, as silence.
        /// </para>
        /// <para>
        /// <b><see cref="Level"/> is the backend's own word, relayed rather than interpreted</b>, and
        /// null when it said nothing. Hosts map what they recognise and must degrade an unknown level
        /// to informational rather than dropping the notice: dropping is the defect this event exists
        /// to fix, and reinstating it for anything unfamiliar would be that defect with a smaller
        /// blast radius. Nothing may be GATED on it - one observed value is not a taxonomy.
        /// </para>
        /// <para>
        /// <b>Rendered as the host speaking, never as the agent.</b> Kiro structures these for us,
        /// which is the whole advantage over Claude announcing a compaction as ordinary assistant
        /// prose; presenting them as something the agent said would throw that away and make them
        /// indistinguishable from a conversation ABOUT compaction.
        /// </para>
        /// </remarks>
        public sealed record BackendNotice(string Message, string? Level = null, string? Details = null) : AgentEvent;

        /// <summary>The agent finished a turn and is awaiting the next prompt.</summary>
        public sealed record TurnCompleted(UsageReport? Usage, string? StopReason) : AgentEvent;

        /// <summary>
        /// Fresh consumption figures. Arrives repeatedly <em>during</em> a turn (both backends stream it
        /// as the context fills), so it is its own event rather than something only
        /// <see cref="TurnCompleted"/> carries. Each report is a snapshot, not a delta: a host replaces
        /// what it holds rather than accumulating. Fields absent from the backend stay null, so merging
        /// with the previous report is the host's choice — the mapper never invents a value.
        /// </summary>
        public sealed record UsageUpdated(UsageReport Usage) : AgentEvent;

        /// <summary>
        /// A background sub-agent reported back. <b>Deliberately carries nothing</b>, because the wire
        /// carries nothing: Claude announces a returning task with
        /// <c>_meta._claude/origin:{kind:"task-notification"}</c> on a usage frame, which names no agent,
        /// and the launch-side <c>agentId</c> never reappears (issue #125).
        /// </summary>
        /// <remarks>
        /// <b>So it is a COUNT, not an identity, and that is enough for the one claim worth making.</b>
        /// Measured across three captures: launches and returns match in number but not in order
        /// (launched 1,2,3 and returned 1,3,2), so settling a particular row on a particular
        /// notification would mislabel two rows of three. But when the returns have caught up with the
        /// launches, <em>every</em> outstanding task has finished — which is true of each row
        /// individually without attributing anything to any of them.
        /// <para>
        /// <b>It must not be treated as turn-scoped.</b> Measured: a turn ended with two of three
        /// returned and the third arrived afterwards, and another capture opened with three returns
        /// belonging to the previous turn's launches. So the count spans turns, and a host that resets
        /// it per turn will strand rows as running forever — the bug this exists to fix.
        /// </para>
        /// </remarks>
        public sealed record BackgroundTaskReturned : AgentEvent;
    }

    /// <summary>
    /// One sub-agent in an <see cref="AgentEvent.SubagentsUpdated"/> roster snapshot.
    /// <paramref name="Status"/> is the backend's raw state ("working"/"terminated" for Kiro) —
    /// passed through rather than mapped, so an unrecognised state degrades to display-only.
    /// <paramref name="Group"/> is the crew label when the backend groups sub-agents.
    /// </summary>
    public sealed record SubagentInfo(
        string SessionId, string Name, string? Description, string Status, string? StatusMessage, string? Group);

    /// <summary>One entry in an agent <see cref="AgentEvent.PlanUpdated"/> plan/task list.</summary>
    public sealed record PlanItem(string Id, string Description, PlanItemStatus Status);

    /// <summary>
    /// Progress of a <see cref="PlanItem"/>. Mirrors the ACP <c>plan</c> entry status; backends that
    /// only report done/not-done (e.g. Kiro's task list) use just Pending/Completed.
    /// </summary>
    public enum PlanItemStatus
    {
        Pending,
        InProgress,
        Completed,
    }

    /// <summary>
    /// What a backend reports about consumption. Every member is optional because the backends report
    /// genuinely different things and no field is common to all of them — see the per-backend wire
    /// shapes captured in the memory note <c>backend-usage-telemetry</c>.
    /// <para>
    /// The ONLY measure both currently supply is <see cref="ContextPercent"/> (Kiro sends a percentage
    /// directly; Claude sends <see cref="ContextUsedTokens"/>/<see cref="ContextWindowTokens"/>, which
    /// the mapper divides). That is why it — and not cost or per-turn tokens — is what an
    /// always-visible indicator can be built on; everything else is per-backend detail, and a host
    /// must render each field only when it is present rather than assuming a zero.
    /// </para>
    /// </summary>
    public sealed record UsageReport
    {
        /// <summary>Share of the context window consumed, 0-100. The one cross-backend measure.</summary>
        public double? ContextPercent { get; init; }

        /// <summary>Context tokens in use, when the backend reports absolutes (Claude) rather than only a percentage.</summary>
        public int? ContextUsedTokens { get; init; }

        /// <summary>Total size of the context window, when reported.</summary>
        public int? ContextWindowTokens { get; init; }

        /// <summary>Prompt tokens for the completed turn, excluding cache reads/writes.</summary>
        public int? InputTokens { get; init; }

        /// <summary>Completion tokens for the completed turn.</summary>
        public int? OutputTokens { get; init; }

        /// <summary>Tokens served from the prompt cache — billed differently, so kept apart from <see cref="InputTokens"/>.</summary>
        public int? CachedReadTokens { get; init; }

        /// <summary>Tokens written to the prompt cache for the turn.</summary>
        public int? CachedWriteTokens { get; init; }

        /// <summary>The backend's own total for the turn. Not derived — reported, so it can be shown as-is.</summary>
        public int? TotalTokens { get; init; }

        /// <summary>Spend so far, in <see cref="CostCurrency"/>. Believed cumulative for the session (Claude).</summary>
        public double? Cost { get; init; }

        /// <summary>Currency of <see cref="Cost"/> ("USD"), or a unit like "Credit" for backends that meter differently.</summary>
        public string? CostCurrency { get; init; }

        /// <summary>Rate-limit state ("allowed" when unconstrained); a non-allowed value is worth surfacing loudly.</summary>
        public string? RateLimitStatus { get; init; }

        /// <summary>Which limit window <see cref="RateLimitResetsAt"/> refers to (Claude's "five_hour").</summary>
        public string? RateLimitType { get; init; }

        /// <summary>When the current limit window resets, as a Unix timestamp in seconds.</summary>
        public long? RateLimitResetsAt { get; init; }

        /// <summary>
        /// What is filling the context window, by category — Kiro only, and the reason its report is
        /// worth showing despite carrying no cost. Ordered as the backend gave it.
        /// </summary>
        public IReadOnlyList<UsageBreakdownEntry>? Breakdown { get; init; }

        /// <summary>Percentage at which the backend will auto-summarize the conversation (Kiro: 80).</summary>
        public double? SummarizeThresholdPercent { get; init; }

        /// <summary>Percentage at which the backend will truncate the conversation (Kiro: 95).</summary>
        public double? TruncateThresholdPercent { get; init; }
    }

    /// <summary>One category of context consumption, e.g. "Tool definitions" at 4241 tokens / 0.4%.</summary>
    public sealed record UsageBreakdownEntry(string Label, int? Tokens, double? Percent);

    /// <summary>
    /// The backend's report of one of the user's own MCP servers.
    /// <para><b>A null means "not reported", never zero</b> — the <see cref="UsageReport"/> rule. The
    /// backends differ sharply in what they say: Kiro v3 gives names, states, tool lists and auth
    /// types; Kiro's default engine names only servers that have already come up; Claude Code says
    /// nothing at all. A host must render each field only where it is present, or it will state a
    /// confident wrong answer about the user's own configuration.</para>
    /// </summary>
    /// <param name="Name">The server's name as the backend spells it.</param>
    /// <param name="IsConnected">
    /// Whether the backend reported this server as connected, decided by the SAME predicate that
    /// gates the <see cref="AgentEvent.McpServerConnected"/> notice, so the notice and the roster can
    /// never disagree about which servers are working.
    /// </param>
    /// <param name="RawStatus">
    /// The backend's own word for the state, passed through unaltered. Present so an uncaptured state
    /// can be shown honestly rather than being forced into a boolean.
    /// </param>
    /// <param name="AuthType">How the server authenticates, where stated — an oauth server's delay is
    /// an auth wait rather than a slow start, which is the difference between "be patient" and "go and
    /// sign in".</param>
    /// <param name="ToolCount">How many tools the server defines, or null where unreported.</param>
    /// <param name="ToolNames">The tools it defines, or null where unreported.</param>
    public sealed record McpServerStatus(
        string Name,
        bool IsConnected,
        string? RawStatus,
        string? AuthType,
        int? ToolCount,
        IReadOnlyList<string>? ToolNames);

    /// <summary>
    /// The user's MCP servers as one backend reported them.
    /// </summary>
    /// <param name="Servers">The servers this report names, in the order the backend gave them.</param>
    /// <param name="NamesWholeConfiguredSet">
    /// Whether <paramref name="Servers"/> is the user's <b>whole configured set</b> — i.e. whether a
    /// denominator may be printed. True only where the backend names every server, including ones
    /// still connecting (Kiro v3 does, before <c>session/new</c> even returns).
    /// <para><b>This is a statement about the FRAME, not about the engine</b>, so it is set where the
    /// shape is known and never inferred later from a provider id.</para>
    /// <para>False is not a lesser version of true. Kiro's default engine reports a server only
    /// <em>when it comes up</em>, which means "1 server connected" and "1 of 5 servers connected, 4 of
    /// them dead" produce byte-identical evidence — so a host must print no total at all. A
    /// denominator you cannot see is not a denominator you may print.</para>
    /// <para>It also decides how a host folds the snapshot: true replaces the list wholesale, false
    /// upserts by name and <b>never removes</b>, an accreting report having no way to say "gone".</para>
    /// </param>
    public sealed record McpRoster(
        IReadOnlyList<McpServerStatus> Servers,
        bool NamesWholeConfiguredSet);

    /// <summary>
    /// What our own MCP bridge is doing. Unlike <see cref="McpServerStatus"/> these are figures <b>we
    /// counted ourselves</b>, so <paramref name="ToolCalls"/> is not nullable: a zero here is a fact,
    /// where a zero from a backend would be a guess. <paramref name="ToolsServed"/> stays nullable for
    /// the one state that is genuinely unknown — the client handshook but has not asked for the
    /// catalog yet, which is exactly the zero-tool failure the engine.log pair was added to catch.
    /// </summary>
    public sealed record McpBridgeStatus(
        bool Connected,
        int? ToolsServed,
        IReadOnlyList<string>? ToolNames,
        int ToolCalls);
}
