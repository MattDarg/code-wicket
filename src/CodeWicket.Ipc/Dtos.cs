using System;
using System.Collections.Generic;

namespace CodeWicket.Ipc
{
    // Wire DTOs for the engine <-> shell boundary. Flat and version-tolerant; mapped to/from
    // the Core types by DtoMapping. AgentEvent is flattened to a single discriminated DTO.

    /// <summary>Opens a session. <paramref name="ImportHistory"/> asks the engine to KEEP the
    /// conversation the backend replays while it loads <paramref name="ResumeConversationId"/>,
    /// instead of dropping it (issue #108) — for a conversation begun in the backend's own terminal
    /// CLI, where that replay is the only account there is. Meaningless without a resume id, and off
    /// for every ordinary one: an ordinary resume already has the transcript from our log, and
    /// ingesting the backend's copy on top of it appends the whole conversation a second time.
    /// <para>The session it opens is an ORDINARY LIVE ONE. The import performed the
    /// <c>session/load</c>, so the conversation is already loaded backend-side and the user carries
    /// straight on typing with MCP wired as normal — a throwaway session would make them pay for the
    /// load twice.</para></summary>
    /// <param name="ResumeWorkingDirectory">The directory the conversation being resumed was last
    /// RUN in, or null when this is not a resume, or when it predates the field being recorded.
    /// <para><b>It exists because a backend can answer a bad resume with SUCCESS.</b> Kiro scopes
    /// its sessions by working directory: asked to load a real session id from a directory that
    /// session does not belong to, it returns a brand new EMPTY session wearing the requested id -
    /// measured, with that moment as its <c>createdAt</c> and the title "New Session". The load
    /// reports success, so nothing populates <c>ResumeFailureReason</c> and nothing is shown: the
    /// user gets their whole transcript on screen and an agent that has never heard of it.</para>
    /// <para>A project settings file can move that directory under an existing conversation
    /// (issue #59), which is what made this reachable. Carried on the REQUEST so the engine can
    /// refuse next to where it resolves the root - the check and the thing it guards then cannot
    /// disagree, and no session is opened that we would have to tear down.</para></param>
    public sealed record StartSessionRequest(
        string? ProviderId,
        string? ModelId,
        string WorkspaceRootPath,
        string PermissionMode,
        string? ResumeConversationId,
        bool ImportHistory = false,
        string? ResumeWorkingDirectory = null,
        string? PinnedWorkingDirectory = null);

    /// <summary>
    /// Asks where a provider WOULD run for a workspace, without opening a session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A query, deliberately: the host needs this <b>before</b> it records anything. A conversation
    /// whose recorded working directory no longer matches where the agent would run cannot be resumed
    /// (the backend answers such a load with a fresh empty session rather than an error - see
    /// <c>ResumeRootGuard</c>), and the right response is to open a NEW conversation rather than append
    /// to one that will not reload. Learning that after the send has begun is too late: the message is
    /// already in the old transcript by then, and unwinding it would mean deleting a recorded message
    /// to reach a state the user never asked for (issue #84's reasoning, in reverse).
    /// </para>
    /// <para>
    /// It resolves through the provider's own <c>ResolveListingRoot</c>, so this answer and the one the
    /// session start uses cannot disagree.
    /// </para>
    /// </remarks>
    public sealed record ResolveAgentRootRequest(string? ProviderId, string WorkspaceRootPath);

    /// <param name="Root">Where that provider would run. Empty only if the request named no usable path.</param>
    /// <param name="Reason">How it was decided, for a log line - the same string the session start reports.</param>
    public sealed record ResolveAgentRootResponse(string Root, string Reason);

    /// <summary>The started session's id, plus any model list the backend only reveals once open
    /// (Claude Code discovers its models in <c>session/new</c>); null <paramref name="Models"/> means
    /// the provider's advertised list stands. <paramref name="CurrentModelId"/> is the active model.
    /// <para><paramref name="WorkingDirectory"/> is where the agent is actually running — normally the
    /// solution folder, but it can sit above it when the backend's workspace marker does (issue #54).
    /// The host roots the relative paths the agent emits against it, and tells the user when it differs
    /// from the solution. <paramref name="WorkspaceRootReason"/> is the locator's account of how that
    /// was decided, for the log.</para>
    /// <para><paramref name="ResumeFailureReason"/> is set when a requested resume was refused by the
    /// backend and the session started fresh instead — the host says so, because the transcript still
    /// shows the earlier conversation while the agent no longer remembers any of it.</para>
    /// <para><paramref name="ImportedHistoryCount"/> is how many entries the backend's replay yielded
    /// for a session opened with <see cref="StartSessionRequest.ImportHistory"/>, and NULL means no
    /// import was asked for — never zero. Zero is its own answer ("asked, and the backend replayed
    /// nothing"), which is the `UsageReport` rule on another field: a null means not reported.
    /// The transcript itself is deliberately NOT here — see
    /// <see cref="TakeImportedHistoryRequest"/> for why it is pulled in pages.</para></summary>
    public sealed record StartSessionResponse(
        string ConversationId,
        IReadOnlyList<ModelInfoDto>? Models = null,
        string? CurrentModelId = null,
        string? WorkingDirectory = null,
        string? WorkspaceMarkerPath = null,
        string? WorkspaceRootReason = null,
        string? ResumeFailureReason = null,
        bool SupportsSteering = false,
        bool SupportsImages = false,
        int? ImportedHistoryCount = null,
        bool ImportedHistoryTruncated = false,
        IReadOnlyList<string>? ProjectSettingsNotices = null,
        int? ReplayedHistoryCount = null);

    /// <summary>
    /// What the live session's handshake settled, for the host's session information panel
    /// (issue #160). Pulled on demand rather than carried on <see cref="StartSessionResponse"/>: that
    /// record is already eleven positional params and is being appended to elsewhere, and a panel
    /// claiming to say what the session IS should read current state rather than a snapshot that
    /// happens to still be true. It also lets the agent version arrive late — the probe that supplies
    /// it for kiro-cli is fire-and-forget, so session start does not wait for it.
    ///
    /// <para><b>Every capability is a <see cref="bool"/>? and a null means NOT REPORTED, never
    /// "no"</b> — the <c>UsageReport</c> rule. Widening one of these to a plain <c>bool</c> compiles,
    /// serializes, and silently turns "the agent never said" into "the agent said no", which is the
    /// one failure this whole surface exists to prevent. Pinned by a round-trip test.</para>
    ///
    /// <para>A null response means there is no live session, which is a different answer again from a
    /// session that reported nothing — the host renders it as "no agent session open yet".</para>
    /// </summary>
    /// <param name="AgentProgram">What the agent said it is, VERBATIM — see
    /// <see cref="CodeWicket.Core.SessionNegotiation"/> for why this must never be re-labelled as
    /// the product name.</param>
    /// <param name="AgentLogDirectory">The backend's OWN log directory, when it names one.</param>
    /// <param name="OpenedAtUtc">Round-trip ("o") format, or null. When the connection was opened —
    /// not how old the conversation is.</param>
    public sealed record SessionInfoResponse(
        string? AgentProgram = null,
        string? AgentLogDirectory = null,
        bool? SupportsResume = null,
        bool? SupportsSteering = null,
        bool? SupportsImages = null,
        bool? SupportsSessionList = null,
        string? OpenedAtUtc = null,
        // The session's mode facts (issues #269/#270): every one nullable, null meaning "not
        // reported" and never "none". Mirrors Core.SessionNegotiation field for field — a field added
        // there and to the panel but not here is dropped in transit in silence, which is what the
        // round-trip test in SessionInfoWireTests exists to catch.
        string? ModeLabel = null,
        string? ModeId = null,
        string? ModeName = null,
        string? OpenedInModeId = null,
        string? OpenedInModeName = null,
        string? ModeOrigin = null,
        string? ModeOriginRoot = null,
        string? ModePinFailure = null,
        string? RequestedModeId = null,
        string? RequestedModeFailure = null,
        // Which backend the LIVE session belongs to — engine-known, not a handshake fact, so it has
        // no counterpart on Core.SessionNegotiation (SessionInfoFieldParityTests lists it). It is what
        // stops the panel mixing one backend's facts with another's rows: the picker can have moved
        // while the engine still holds the previous backend's warm session.
        string? ProviderId = null,
        string? ProviderDisplayName = null);

    /// <summary>A backend the engine can drive, plus the models it advertises — for the picker.</summary>
    /// <summary>
    /// <paramref name="ResumeCommand"/> is how this backend's CLI resumes a conversation, with
    /// <c>{id}</c> where the id goes (issue #108). It rides the provider list rather than a call of its
    /// own because it is static per backend, and the shell needs it to decide whether to OFFER the
    /// "continue in terminal" affordance at all - a question asked while a menu opens, which is no
    /// place for a round trip. Null means this backend has no verified resume command and the
    /// affordance is hidden.
    /// </summary>
    public sealed record ProviderInfoDto(
        string Id,
        string DisplayName,
        IReadOnlyList<ModelInfoDto> Models,
        IReadOnlyList<string> Capabilities,
        string? ResumeCommand = null);

    public sealed record ModelInfoDto(string Id, string DisplayName, double? RateMultiplier = null);

    public sealed record ListProvidersResponse(IReadOnlyList<ProviderInfoDto> Providers);

    /// <summary>
    /// A backend's model list, discovered after <c>engine/listProviders</c> already answered from last
    /// run's cache. Pushed rather than polled because the discovery is a CLI shell-out we deliberately
    /// no longer make the picker wait for, and Kiro's list cannot ride
    /// <see cref="StartSessionResponse.Models"/> — its <c>session/new</c> carries none (it is a
    /// pre-session shell-out, verified on the wire).
    /// </summary>
    public sealed record ProviderModelsDto(string ProviderId, IReadOnlyList<ModelInfoDto> Models);

    public sealed record PromptRequest(string Text, IReadOnlyList<PromptAttachmentDto>? Attachments = null);

    /// <summary>
    /// Binary content sent with a prompt — today a pasted image, which is the one thing that has no
    /// path to reference (issue #118). <paramref name="Data"/> is base64 of the raw bytes, no
    /// <c>data:</c> prefix.
    /// <para><paramref name="Name"/> is for the user's eye only — the chip in the composer and the
    /// thumbnail's tooltip. It does NOT cross to the agent: ACP's image block has no name field, and
    /// inventing one in the prompt text would be us narrating the user's attachment for them.</para>
    /// </summary>
    public sealed record PromptAttachmentDto(string Name, string MimeType, string Data);

    /// <summary>Switch the active session's model in place (ACP <c>session/set_model</c>), keeping the
    /// conversation. Only used for providers advertising the <c>ModelSelection</c> capability.</summary>
    public sealed record SetModelRequest(string ModelId);

    public sealed record PromptResponse(string StopReason);

    /// <summary>
    /// Deliver a follow-up message into the turn already running, rather than waiting for it to end
    /// (ACP <c>_session/steering</c>). Only for sessions whose
    /// <see cref="StartSessionResponse.SupportsSteering"/> said the backend accepts it.
    /// </summary>
    public sealed record SteerRequest(string Text, IReadOnlyList<PromptAttachmentDto>? Attachments = null);

    /// <summary>
    /// Where a steered message landed — the <c>SteerOutcome</c> name, crossing as a string so the
    /// enum stays Core's. <c>StartedNewTurn</c> means the steer lost the race to the turn's end and
    /// the backend opened a fresh turn for it, whose output arrives out-of-turn rather than on the
    /// prompt the host is awaiting.
    /// </summary>
    public sealed record SteerResponse(string Outcome);

    /// <summary>Ask the engine to summarize a prior conversation transcript in an isolated pass (no
    /// active session touched), for "resume from summary". <paramref name="Transcript"/> is the plain
    /// User/Assistant text the shell reconstructs from its saved log.
    /// <para><paramref name="SourceWorkingDirectory"/> is the directory the transcript's relative
    /// paths were written against — the SOURCE conversation's agent root (issue #184). It is not the
    /// summarizer's own root: the summarize pass runs at the TARGET provider's, and for the same
    /// solution the two differ wherever one backend widens to a workspace marker (issue #54) and the
    /// other does not, which is exactly the provider switch the summary route exists to carry.</para></summary>
    public sealed record SummarizeRequest(
        string? ProviderId, string? ModelId, string WorkspaceRootPath, string Transcript,
        string? SourceWorkingDirectory);

    public sealed record SummarizeResponse(string Summary);

    /// <summary>Ask a backend what its own CLI has stored for a workspace, before and without any
    /// session of ours (issue #108), so a conversation begun in the terminal can be picked up here.
    /// <para><paramref name="ProviderId"/> is optional for the reason
    /// <see cref="SummarizeRequest"/>'s is: null means the engine's default backend, so a host that
    /// has not resolved a provider yet still gets an answer rather than an error.</para></summary>
    public sealed record ListBackendSessionsRequest(string? ProviderId, string WorkspaceRootPath);

    /// <summary>
    /// What the backend has stored, or why nothing is listed.
    /// <para><paramref name="Supported"/> false with an empty list and <paramref name="Supported"/>
    /// true with an empty list are DIFFERENT answers — "this backend cannot tell you" against "this
    /// backend has nothing here" — and the host says something different for each. Collapsing them is
    /// the same mistake as reading a null usage figure as zero, and it is the one the wire has to
    /// carry rather than let the shell re-derive: an empty list is the shape BOTH failures take.</para>
    /// <para><paramref name="Reason"/> is the backend's own words when it refused (not installed,
    /// signed out, no session list advertised) — a sentence to show under an empty list, so it is
    /// non-null exactly when <paramref name="Supported"/> is false.</para>
    /// </summary>
    public sealed record ListBackendSessionsResponse(
        bool Supported,
        string? Reason,
        IReadOnlyList<BackendSessionDto> Sessions);

    /// <summary>
    /// One conversation in the backend CLI's own store. Everything but the id is optional because the
    /// backend decides what it keeps, and a null here means NOT REPORTED — never a default the host
    /// could mistake for a value.
    /// <para><paramref name="UpdatedUtc"/> absent means the backend does not track a time, in which
    /// case the host must not invent an order; it crosses as UTC because a <c>DateTime</c> carries no
    /// offset and a local-time reading on the far side would silently shift every row.</para>
    /// <para><paramref name="Title"/> is whatever the backend chose to name the conversation (Claude
    /// generates a summary; Kiro takes the first user message verbatim), and <paramref name="Cwd"/> is
    /// the working directory it was stored against — worth showing, because a backend keying its store
    /// by path is how a picker ends up looking empty for the wrong root (issue #54).</para>
    /// </summary>
    /// <param name="CreatedUtc">
    /// When the backend says the conversation was created, or null where it does not say — Claude
    /// reports none, Kiro v3 carries it under <c>_meta.kiro</c>. It is here for one purpose: with
    /// <paramref name="UpdatedUtc"/> it recognises a conversation created and never used
    /// (<c>Core.BackendSessionFilter</c>), which neither backend will answer directly because neither
    /// sends a message count. <b>A field added here and to the record but not to the mapping is
    /// dropped in transit in silence</b> — the same three-places rule the workspace snapshot records.
    /// </param>
    public sealed record BackendSessionDto(
        string Id, string? Title, DateTime? UpdatedUtc, string? Cwd, DateTime? CreatedUtc = null);

    /// <summary>
    /// Pulls one page of the conversation an import captured (issue #108). The engine holds it from
    /// the <c>engine/startSession</c> that produced it until the next session start or teardown, so
    /// the host reads pages until <paramref name="Offset"/> reaches
    /// <see cref="TakeImportedHistoryResponse.Total"/>. Reading does not consume: a page can be asked
    /// for twice and answers the same, which is what lets a failed page be retried.
    /// <para><b>Why the transcript is PULLED rather than returned with the session.</b> A
    /// StreamJsonRpc result past a size threshold comes back through
    /// <c>SystemTextJsonFormatter</c> deserialized from a NUL-padded span and throws
    /// <c>0x00 after a value</c> — the same defect
    /// <c>AcpAgentSession.SetModelViaConfigOptionAsync</c> documents, and it is size-triggered, so a
    /// 413-frame session (measured, and the MEDIAN case rather than the worst) would sit right on
    /// it. Notifications are wrong for the opposite reason: a response can overtake the notifications
    /// that preceded it (#33), so a pushed transcript can arrive after the host has already decided
    /// there is none.</para>
    /// </summary>
    public sealed record TakeImportedHistoryRequest(int Offset, int Limit);

    /// <summary>One page of an imported conversation, plus the whole length so the caller knows when
    /// to stop asking. <paramref name="Total"/> is the count for the WHOLE import, not the page.</summary>
    public sealed record TakeImportedHistoryResponse(IReadOnlyList<ImportedEntryDto> Entries, int Total);

    /// <summary>
    /// One entry of an imported conversation, shaped to match the shell's persisted
    /// <c>TranscriptEntry</c>: <paramref name="Role"/> is <c>user</c> or <c>agent</c>, a user prompt
    /// carries <paramref name="Text"/>, and an agent entry carries <paramref name="Event"/> — the same
    /// <see cref="AgentEventDto"/> a live turn would have streamed. An import is a transcript we did
    /// not record, so what it produces has to be indistinguishable from one we did, or the host needs
    /// a second render path.
    /// <para>The list is already coalesced engine-side: one message the user sent can replay as
    /// several chunks, so the entries here are 1:1 with messages rather than with frames.</para>
    /// </summary>
    public sealed record ImportedEntryDto(string Role, string? Text, AgentEventDto? Event);

    public sealed record AgentEventDto
    {
        /// <summary>text | thinking | toolStart | toolUpdate | toolProgress | toolOutput | toolDone | edit | plan | subagents | subagentResult | mcpServerConnected | mcpRoster | mcpBridge | usage | backgroundTaskReturned | error | notice | turnDone</summary>
        public string Type { get; init; } = string.Empty;
        public string? Text { get; init; }
        public string? ToolCallId { get; init; }
        public string? Title { get; init; }
        public string? Kind { get; init; }
        public string? RawInputJson { get; init; }

        /// <summary>
        /// The namespaced MCP tool name on a tool-call event (<c>@server/tool</c> /
        /// <c>mcp__server__tool</c>); null for a backend built-in. Marks <see cref="RawInputJson"/> as a
        /// third party's schema, so the host reads no meaning into its key names (issue #131). Also the
        /// subject a permission rule names — see <c>PermissionRequestDto.ToolName</c>.
        /// </summary>
        public string? ToolName { get; init; }

        /// <summary>
        /// For Type == "toolStart"/"toolUpdate"/"edit": the sub-agent invocation that made this call, or null
        /// for the main agent's own work. Hosts nest the row under that parent; an unknown parent is a
        /// top-level row, never a dropped one (issue #125). Persisted, so a restored transcript nests
        /// the same way the live one did — there is no second code path.
        /// </summary>
        public string? ParentToolCallId { get; init; }

        /// <summary>
        /// For Type == "toolStart"/"toolUpdate": this call STARTS a sub-agent (Claude's Task, Kiro v3's
        /// invoke_subagent_*) — it is the row other calls nest under.
        /// </summary>
        public bool? IsSubagentLaunch { get; init; }

        /// <summary>
        /// For Type == "toolDone": the call did not finish, it was LAUNCHED — an async sub-agent whose
        /// work runs on afterwards. Hosts show a distinct state rather than a green tick, and nothing
        /// tells us when it ends (see <c>Core.AgentEvent.ToolCallCompleted</c>).
        /// </summary>
        public bool? LaunchedInBackground { get; init; }

        /// <summary>
        /// For Type == "toolDone": this completion is the HOST's own delivery of its own tool result —
        /// the shell side-channel forwarding what <c>IToolCatalog.InvokeAsync</c> just returned — rather
        /// than a frame the agent sent or its echo of one. It is what lets the host build a
        /// test-results / breakpoint card from a payload instead of trusting the payload's own marker
        /// to say who wrote it (see <c>Core.AgentEvent.ToolCallCompleted</c>).
        /// <para>
        /// <b>Three states, and the third is a date.</b> True = ours. False = a frame we did not
        /// author, refused as a card. NULL = the entry was written before this field existed: unlike the
        /// neighbouring flags above, a false is written out rather than folded to null, precisely so
        /// that ABSENCE in a persisted log dates that log to before the flag and nothing else can
        /// produce it. A live frame always carries the field — the engine stamps every completion in
        /// <c>DtoMapping</c> and the side-channel sets it by hand — so a null can only come off disk.
        /// </para>
        /// </summary>
        public bool? HostAuthored { get; init; }

        public string? Message { get; init; }

        /// <summary>
        /// For Type == "notice": the backend's own severity word, verbatim, or null when it gave none.
        /// Hosts map what they recognise and show anything else as informational - see
        /// <c>Core.AgentEvent.BackendNotice</c> for why an unknown level must never mean "drop it".
        /// </summary>
        public string? NoticeLevel { get; init; }
        /// <summary>For Type == "toolDone": a separate error stream (a command's stderr), shown apart from Message.</summary>
        public string? ErrorText { get; init; }

        /// <summary>
        /// For Type == "toolDone": how the call came to be permitted (issue: permission state on tool
        /// rows). Stamped SHELL-side — the engine never sends it, because permissions terminate in the
        /// shell — onto the event that already marks the call finished, so persistence and replay cost
        /// one field rather than a second log entry per call.
        /// <para>
        /// NULL means "no record", which is NOT the same as <c>notRequested</c>: a conversation saved
        /// before this existed has no record, and so does history replayed from a resumed CLI session,
        /// which never carried our decisions. A row that we know nobody asked about carries an explicit
        /// <c>notRequested</c> instead.
        /// </para>
        /// </summary>
        public PermissionOutcomeDto? Permission { get; init; }
        public bool? Success { get; init; }
        public string? Path { get; init; }
        public string? OldText { get; init; }
        public string? NewText { get; init; }
        /// <summary>For Type == "edit": the 1-based line the backend reported the edit at, when known (ACP locations[].line).</summary>
        public int? EditLine { get; init; }
        /// <summary>For Type == "edit": the agent's stated reason for the edit (its "why"), when supplied.</summary>
        public string? EditIntent { get; init; }
        /// <summary>For Type == "edit": the backend's edit-operation name (Kiro's strReplace/create/…), when supplied.</summary>
        public string? EditOperation { get; init; }
        public string? Details { get; init; }
        public string? StopReason { get; init; }

        /// <summary>
        /// For Type == "usage" (a mid-turn snapshot) and "turnDone" (per-turn totals): what the backend
        /// reported about consumption. Null when it reported nothing — which is the normal case for a
        /// backend that only reports one of the two.
        /// </summary>
        public UsageDto? Usage { get; init; }

        /// <summary>For Type == "plan": the full task list (Title carries the plan's description).</summary>
        public IReadOnlyList<PlanItemDto>? PlanItems { get; init; }

        /// <summary>For Type == "subagents": the sub-agent roster snapshot (upsert by SessionId; never remove).</summary>
        public IReadOnlyList<SubagentDto>? Subagents { get; init; }

        /// <summary>For Type == "subagentResult": which sub-agent's final result <see cref="Text"/> carries.</summary>
        public string? SubagentSessionId { get; init; }

        /// <summary>For Type == "mcpRoster": the user's own MCP servers as the backend reported them.</summary>
        public McpRosterDto? McpRoster { get; init; }

        /// <summary>For Type == "mcpBridge": what our own MCP bridge is doing.</summary>
        public McpBridgeDto? McpBridge { get; init; }
    }

    /// <summary>
    /// Flat mirror of <c>Core.UsageReport</c>. Every field optional: the backends report different
    /// subsets and only the context percentage is common to both, so a null means "not reported" and
    /// must not be rendered as a zero.
    /// </summary>
    public sealed record UsageDto
    {
        public double? ContextPercent { get; init; }
        public int? ContextUsedTokens { get; init; }
        public int? ContextWindowTokens { get; init; }
        public int? InputTokens { get; init; }
        public int? OutputTokens { get; init; }
        public int? CachedReadTokens { get; init; }
        public int? CachedWriteTokens { get; init; }
        public int? TotalTokens { get; init; }
        public double? Cost { get; init; }
        public string? CostCurrency { get; init; }
        public string? RateLimitStatus { get; init; }
        public string? RateLimitType { get; init; }
        public long? RateLimitResetsAt { get; init; }
        public IReadOnlyList<UsageBreakdownDto>? Breakdown { get; init; }
        public double? SummarizeThresholdPercent { get; init; }
        public double? TruncateThresholdPercent { get; init; }
    }

    /// <summary>One category of context consumption (Kiro only).</summary>
    public sealed record UsageBreakdownDto(string Label, int? Tokens, double? Percent);

    /// <summary>
    /// Flat mirror of <c>Core.McpRoster</c> — the user's own MCP servers.
    /// <para><paramref name="NamesWholeConfiguredSet"/> is the "no false denominator" rule crossing the
    /// wire: true only where the backend named every configured server (Kiro v3), so a host may print
    /// "1 of 2". Where it is false the backend reports a server only once it has come up, and a total
    /// must not be printed at all — "1 server connected" and "1 of 5, four of them dead" are then the
    /// same evidence. It also decides the fold: true replaces the list, false upserts and never removes.</para>
    /// </summary>
    public sealed record McpRosterDto(
        IReadOnlyList<McpServerDto> Servers,
        bool NamesWholeConfiguredSet);

    /// <summary>
    /// One of the user's MCP servers. Every field but the name and the connected flag is optional and a
    /// null means "not reported", never zero — the <see cref="UsageDto"/> rule. Backends differ sharply
    /// in how much they say, so a host renders each field only where it is present.
    /// </summary>
    public sealed record McpServerDto(
        string Name,
        bool IsConnected,
        string? RawStatus,
        string? AuthType,
        int? ToolCount,
        IReadOnlyList<string>? ToolNames);

    /// <summary>
    /// Flat mirror of <c>Core.McpBridgeStatus</c> — our own bridge, the one MCP fact available on every
    /// backend because we host it. <paramref name="ToolCalls"/> is not nullable: it is a figure we
    /// counted, so zero is a fact rather than a guess. <paramref name="ToolsServed"/> stays nullable for
    /// the state that is genuinely unknown — handshook, but the catalog not yet asked for.
    /// </summary>
    public sealed record McpBridgeDto(
        bool Connected,
        int? ToolsServed,
        IReadOnlyList<string>? ToolNames,
        int ToolCalls);

    /// <summary><paramref name="Status"/> is the backend's raw state ("working"/"terminated" for Kiro).</summary>
    public sealed record SubagentDto(
        string SessionId, string Name, string? Description, string Status, string? StatusMessage, string? Group);

    /// <summary><paramref name="Status"/> is the Core PlanItemStatus name: Pending | InProgress | Completed.</summary>
    public sealed record PlanItemDto(string Id, string Description, string Status);

    public sealed record ReadFileRequest(string Path, int? Line, int? Limit);

    public sealed record ReadFileResponse(string Content);

    public sealed record WriteFileRequest(string Path, string Content);

    /// <summary>
    /// What the shell's write actually did (Core <c>FileWriteResult</c> over the wire): the resolved
    /// absolute path plus the exact content either side of the write. Lets the engine surface an
    /// authoritative diff for an edit the host applied, rather than one reconstructed from the agent's
    /// reported hunk. All fields nullable so a host that can't report degrades to "no capture" rather
    /// than to empty strings, which would read as a real (whole-file-deleted) diff.
    /// </summary>
    public sealed record WriteFileResponse(string? ResolvedPath, string? OldText, string? NewText);

    public sealed record WorkspaceSnapshotDto
    {
        public string? SolutionName { get; init; }
        public string? ActiveFilePath { get; init; }
        public TextSelectionDto? Selection { get; init; }
        public IReadOnlyList<string> OpenFilePaths { get; init; } = Array.Empty<string>();
        public IReadOnlyList<DiagnosticDto> Diagnostics { get; init; } = Array.Empty<DiagnosticDto>();

        /// <summary>Totals over EVERY diagnostic, not just the ones in <see cref="Diagnostics"/> — the
        /// block states them so a truncated list cannot read as complete (issue #95).</summary>
        public int TotalErrorCount { get; init; }
        public int TotalWarningCount { get; init; }
        public int OmittedDiagnosticCount { get; init; }

        /// <summary>
        /// The debugger, when there is a session (issue #73). Null when there is not.
        /// </summary>
        /// <remarks>
        /// It has to be HERE, and the omission is instructive: the snapshot is built IDE-side and
        /// rendered ENGINE-side, so a field present in <c>WorkspaceSnapshot</c> and in the formatter but
        /// missing from this DTO is dropped in transit, silently. It shipped that way, and a timing line
        /// proving the IDE side built the value read as proof the block was emitted - which it was not.
        /// Anything added to the snapshot must be added in three places, not two.
        /// </remarks>
        public DebugSessionDto? DebugSession { get; init; }
    }

    /// <summary>The ambient debugger state — see <see cref="WorkspaceSnapshotDto.DebugSession"/>.</summary>
    public sealed record DebugSessionDto
    {
        public bool IsStopped { get; init; }
        public string? File { get; init; }
        public int Line { get; init; }
        public string? Method { get; init; }
        public string? Reason { get; init; }
        public string? Thread { get; init; }
        public int? ProcessId { get; init; }
        public int StopNumber { get; init; }
    }

    public sealed record TextSelectionDto(string FilePath, int StartLine, int StartColumn, int EndLine, int EndColumn, string? Text);

    public sealed record DiagnosticDto(string FilePath, int Line, int Column, string Severity, string Message, string? Code);

    public sealed record ToolDescriptorDto(string Name, string Description, string InputSchemaJson);

    public sealed record ToolListResponse(IReadOnlyList<ToolDescriptorDto> Tools);

    public sealed record InvokeToolRequest(string Name, string ArgumentsJson);

    public sealed record ToolResultDto(bool IsError, string ContentJson);

    /// <summary>
    /// The wire form of <c>Core.PermissionOutcome</c>. <see cref="Kind"/> is a STRING rather than an
    /// enum so a value written by a newer build degrades to "unrecognised" on an older one instead of
    /// throwing while a saved transcript is being replayed.
    /// </summary>
    /// <param name="Kind">notRequested | userAllowed | userDenied | ruleAllowed | ruleDenied | modeAllowed.</param>
    /// <param name="Rule">The rule or mode that decided it, for the row's detail. Null when the user decided.</param>
    /// <param name="RulePersisted">The rule is saved in config, rather than remembered for this session only.</param>
    /// <param name="CautionPrompted">
    /// The caution tier (AlwaysPromptCommands) forced the prompt, overriding an allow rule or the mode.
    /// Recorded but given no chrome of its own: live, the banner already calls it out loudly, so this
    /// exists for the RESTORED view, where it answers "why was I asked despite my rule".
    /// </param>
    public sealed record PermissionOutcomeDto(
        string Kind,
        string? Rule = null,
        bool RulePersisted = false,
        bool CautionPrompted = false,
        string? CautionReason = null);

    public sealed record PermissionRequestDto(
        string ToolCallId,
        string Title,
        string? Kind,
        string? Detail,
        string? Command,
        IReadOnlyList<PermissionOptionDto> Options,
        string? ToolName = null,
        string? Path = null,
        string? FlaggedFragment = null,
        string? FlaggedReason = null,
        string? RuleNote = null,
        bool FromSubagentSession = false);

    public sealed record PermissionOptionDto(string OptionId, string Label, string Kind);

    public sealed record PermissionDecisionDto(
        string OptionId, bool Cancelled, string? RememberCommand = null, bool PersistRemembered = false,
        string? RememberPath = null, string? RememberTool = null);
}
