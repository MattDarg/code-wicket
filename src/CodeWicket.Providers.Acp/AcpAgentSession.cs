using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using StreamJsonRpc;
using CodeWicket.Core;
using CodeWicket.Core.Ide;

namespace CodeWicket.Providers.Acp
{
    /// <summary>
    /// A single conversation with an ACP agent. Owns the child agent process and the JSON-RPC
    /// connection over its stdio. Each <see cref="SendAsync"/> drives one ACP <c>session/prompt</c>,
    /// streaming the resulting updates as <see cref="AgentEvent"/>s. Provider-agnostic: nothing here
    /// is Kiro-specific (see <see cref="AcpAgentProvider"/> / <see cref="AcpAgentConfig"/> for launch).
    /// </summary>
    public sealed class AcpAgentSession : IAgentSession, IModelDiscovery, IWorkspaceRootReport, IResumeFallbackReport, IBackendSessionList, IImportedHistoryReport, IProjectSettingsReport, ISessionNegotiationReport
    {
        private const int AcpProtocolVersion = 1;

        /// <summary>The conventional ACP config-option id for the model selector — used when a backend
        /// has one but hasn't told us its id yet (see the set_model fallback in SetModelAsync).</summary>
        private const string DefaultModelConfigId = "model";

        private readonly SessionOptions _sessionOptions;
        private readonly IIdeServices _ide;
        private readonly IAcpConnection _connection;

        private JsonRpc? _rpc;
        private string _sessionId = string.Empty;

        // The channel for the in-flight turn. Update callbacks write here; SendAsync reads.
        private volatile Channel<AgentEvent>? _currentTurn;

        // Held so a turn's end can retire the client target's per-turn state (the write capture).
        private AcpClientTarget? _clientTarget;

        // Set while session/load is replaying the conversation's history at us. See EmitToCurrentTurn.
        private volatile bool _loadingHistory;

        // Conversation frames the backend replayed inside the load window, whatever became of them
        // (dropped on an ordinary resume, kept on an import). The count is the only fact that tells a
        // load that worked from one Kiro v3 answers for an id it no longer holds: success, and nothing
        // replayed (issue #185). Incremented from the client target's inbound dispatch.
        private int _replayedDuringLoad;

        /// <inheritdoc/>
        public int? ReplayedHistoryCount =>
            string.IsNullOrEmpty(_sessionOptions.ResumeConversationId) ? null : _replayedDuringLoad;

        // The replayed conversation, when the host asked for it (SessionOptions.ImportHistory, issue
        // #108). Written from the inbound dispatch FIFO, which is serial, and read after the load has
        // drained — the same barrier that makes the load window trustworthy in the first place (#33).
        private readonly List<ImportedTurnEntry> _importedHistory = new List<ImportedTurnEntry>();

        // Bound on an imported conversation. The replay is a foreign backend's store, so its size is
        // not ours to assume: a measured Claude CLI session replayed 413 frames, and nothing says a
        // long-running one cannot replay far more. Stops there and says so, rather than growing until
        // the engine wire has to carry it.
        private const int MaxImportedEntries = 20_000;
        private bool _importTruncated;

        // Inbound JSON-RPC dispatch, serialized in wire order. The turn's end drains it before
        // closing the channel so the last session/update notifications can't be overtaken by the
        // session/prompt response and dropped (issue #33).
        private readonly OrderedDispatchSynchronizationContext _dispatch = new();

        // Upper bound on that drain. A wedged handler must not hang the turn forever — losing the
        // ordering guarantee is far better than never completing.
        private static readonly TimeSpan DispatchDrainTimeout = TimeSpan.FromSeconds(5);

        // The IDE-tools steering block is injected once, on the session's first prompt.
        private bool _ideToolsHintSent;

        // Model selection: some backends (Kiro) take the model as a launch flag and honour the standard
        // session/set_model. Newer ones (Claude Code) surface the model list only via the session/new
        // config options (category "model") and switch via session/set_config_option. When the session
        // saw a model config option, _modelConfigId is set and we drive that path; otherwise set_model.
        private string? _modelConfigId;
        private IReadOnlyList<ModelInfo> _discoveredModels = Array.Empty<ModelInfo>();
        private string? _currentModelId;

        // The requested model, held because the selector wasn't known yet when the session opened.
        // Kiro's v3 engine publishes its config options as a config_option_update notification AFTER
        // session/new returns (its response carries none), so the model can only be applied when that
        // frame lands. Cleared once applied. Null for backends that advertise the selector up front,
        // and for those that take the model as a launch flag (Kiro v1/v2's --model).
        private string? _pendingModelId;

        // The backend's permission mode, as it reports it (issue #269 / #270). Read from session/new's
        // `modes` and from the config option in category "mode"; updated by every current_mode_update.
        // Null means the backend has not said — which is not "ask" and not "bypass", it is not known.
        private string? _modeConfigId;
        private readonly Dictionary<string, string> _modeNames = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<string> _modeUpdates = new List<string>();

        // How the host's picker maps onto this agent's modes (AcpAgentConfig.BackendModes). Null pins
        // nothing; otherwise PinBackendModeAsync asserts the mapped mode on every open and SAYS SO when
        // that fails — it never refuses the session.
        private readonly BackendPermissionModes? _backendModes;

        // The session mode the provider asked to run in (AcpAgentProvider.RequestedSessionModeId —
        // Kiro v3's agent), applied by session/set_mode after the open. Null selects nothing.
        private readonly string? _requestedModeId;

        // The `_meta` every open carries (AcpAgentConfig.SessionMeta). Null sends none.
        private readonly JsonElement? _sessionMeta;

        // What a "mode" is on this backend, for the panel (AcpAgentConfig.ModeLabel).
        private readonly string _modeLabel;

        // Where each available mode came from, for the backends that say (Kiro v3 tags every agent
        // with `_meta.kiro.source` and its `resource.source.root`): a mode defined by the REPOSITORY
        // brings the repository's tool-trust rules with it, which is the thing worth saying out loud.
        private readonly Dictionary<string, (string Source, string? Root)> _modeOrigins =
            new Dictionary<string, (string Source, string? Root)>(StringComparer.Ordinal);

        // Optional host-side auth callback for Kiro's v3 engine (see AcpAgentConfig.AuthTokenProvider).
        private readonly Func<CancellationToken, Task<JsonElement>>? _authTokenProvider;

        // Where the agent actually runs, and how that was decided (issue #54). Supplied by the provider,
        // which resolves it once for both the process cwd and the ACP session cwd; a null argument (test
        // seams, hand-built sessions) falls back to the session's own workspace root.
        private readonly WorkspaceRootResult _workspaceRoot;

        // What the agent process has told us about itself (version, stderr, exit), so a failed turn can
        // carry the backend's own account instead of a bare protocol word — issue #82. Null for
        // sessions built without a real process (tests, the in-memory fake), which is why every read
        // of it is null-tolerant rather than guarded once at construction.
        private readonly AgentDiagnostics? _diagnostics;

        internal AcpAgentSession(
            SessionOptions sessionOptions,
            IIdeServices ide,
            IAcpConnection connection,
            Func<CancellationToken, Task<JsonElement>>? authTokenProvider = null,
            WorkspaceRootResult? workspaceRoot = null,
            AgentDiagnostics? diagnostics = null,
            IReadOnlyList<string>? projectSettingsNotices = null,
            BackendPermissionModes? backendModes = null,
            string? requestedModeId = null,
            JsonElement? sessionMeta = null,
            string? modeLabel = null)
        {
            _sessionMeta = sessionMeta;
            _modeLabel = string.IsNullOrWhiteSpace(modeLabel) ? "Mode" : modeLabel!;
            ProjectSettingsNotices = projectSettingsNotices ?? Array.Empty<string>();
            _backendModes = backendModes;
            _requestedModeId = string.IsNullOrWhiteSpace(requestedModeId) ? null : requestedModeId!.Trim();
            _sessionOptions = sessionOptions;
            _ide = ide;
            _connection = connection;
            _authTokenProvider = authTokenProvider;
            _diagnostics = diagnostics;

            // The trigger the capture never had (issue #208). Set here rather than at session start
            // so a CLI that complains while coming up is still heard - EmitToCurrentTurn routes a
            // turn-less event to the host, which is what one of these almost always is.
            if (_diagnostics is not null)
                _diagnostics.LineObserver = OnAgentStderrLine;
            _workspaceRoot = workspaceRoot ?? new WorkspaceRootResult(
                string.IsNullOrEmpty(sessionOptions.WorkspaceRootPath)
                    ? Directory.GetCurrentDirectory()
                    : sessionOptions.WorkspaceRootPath,
                false, null, "solution folder");
        }

        public string ConversationId => _sessionId;

        // Distinct problems already reported, so a backend refusing the same request over and over
        // says so once. Session-scoped: a new session is a new conversation and the user has not been
        // told anything in it yet. Its own lock - the observer runs on process-pool callbacks.
        private readonly HashSet<string> _reportedStderr = new HashSet<string>(StringComparer.Ordinal);
        private readonly object _reportedStderrGate = new object();

        /// <summary>
        /// One line of the agent CLI's stderr, as it arrives. Surfaces the ones that look like the
        /// backend refusing something, once each (issue #208).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Relayed verbatim, and nothing is gated on it.</b> The line is the backend's own account
        /// of itself, which is the whole point of #82's rule; interpreting it would mean building a
        /// taxonomy on one day's evidence. In particular a <c>retryAfterMilliseconds</c> in the body
        /// is shown as part of what the backend said and is <b>NOT turned into a deadline</b>.
        /// </para>
        /// <para>
        /// <b>Why that is not merely caution.</b> Captured 2026-09-04: six refusals advertising a
        /// one-hour window, then requests succeeding 5m16s after the last of them - kiro-cli's own
        /// internal retries, the user being away from the machine. The user, present for the whole
        /// episode, experienced it as broken well past that point and recovering later unattended.
        /// <b>So the log's "the backend answered" and the user's "it works again" are not the same
        /// event</b>, and a countdown rendered from this number would have been wrong for at least
        /// one of them - too long against the retries, too short against the person. Relaying what
        /// the backend claimed is right under either, and needs no view about which.
        /// </para>
        /// <para>Errors from this path are swallowed by <see cref="AgentDiagnostics"/>: a diagnostic
        /// must not be the thing that breaks the session it is describing.</para>
        /// </remarks>
        private void OnAgentStderrLine(string line)
        {
            if (!AgentStderrNotice.IsError(line))
                return;

            lock (_reportedStderrGate)
            {
                if (!_reportedStderr.Add(AgentStderrNotice.DedupeKey(line)))
                    return;
            }

            EmitToCurrentTurn(new AgentEvent.BackendNotice(
                AgentStderrNotice.Format(line, "engine.log"),
                "error"));
        }

        public WorkspaceRootResult WorkspaceRoot => _workspaceRoot;

        /// <inheritdoc/>
        /// <remarks>
        /// Composed by the provider, which is where the file is read - the session is handed the
        /// finished sentences rather than the settings, because it has no other use for them and a
        /// second reader would be a second answer about what the file said.
        /// </remarks>
        public IReadOnlyList<string> ProjectSettingsNotices { get; }

        /// <inheritdoc/>
        public string? ResumeFailureReason { get; private set; }

        /// <inheritdoc/>
        public IReadOnlyList<ImportedTurnEntry> ImportedHistory => _importedHistory;

        /// <summary>
        /// The agent's advertised <c>authMethods</c> from <c>initialize</c>, as a single human line —
        /// null when it advertised none. Every entry is its own instruction (Kiro's reads "Run
        /// 'kiro-cli login' in terminal to authenticate. See https://kiro.dev/docs/cli/authentication/"),
        /// which is the ONLY place an ACP agent says how to sign in. Held so an auth-shaped failure can
        /// quote it instead of leaving the user with the backend's bare protocol error.
        /// </summary>
        internal string? AuthGuidance { get; private set; }

        /// <summary>What the agent's <c>initialize</c> advertised for <c>loadSession</c>; null until
        /// the handshake completes. Feeds capability discovery for config-defined agents.</summary>
        internal bool? DiscoveredLoadSession { get; private set; }

        /// <summary>When <see cref="OpenSessionAsync"/> last opened a conversation on this connection.</summary>
        private DateTimeOffset _openedAt;

        /// <inheritdoc/>
        /// <remarks>
        /// Built fresh on every read, and that is load-bearing rather than incidental:
        /// <see cref="AgentVersionProbe"/> writes into <c>_diagnostics</c> from a background process
        /// that session start deliberately does not wait for, so a record captured once would report
        /// whatever happened to be known at that instant. See <see cref="SessionNegotiation"/>.
        /// <para><see cref="AgentDiagnostics"/> stays internal and is projected here rather than
        /// published: it is a mutable, lock-guarded accumulator with an eviction queue and an
        /// error-path contract, and exporting all of that to satisfy two string reads would be a poor
        /// trade. Same move <see cref="IWorkspaceRootReport"/> makes over its own result type.</para>
        /// </remarks>
        public SessionNegotiation Negotiation => new SessionNegotiation(
            _diagnostics?.Version,
            _diagnostics?.LogDirectory,
            DiscoveredLoadSession,
            DiscoveredSteering,
            DiscoveredImagePrompts,
            DiscoveredSessionList,
            _openedAt,
            // The mode facts (issues #269/#270), read live for the same reason the version is: the
            // mode can move after the open — a plan-mode exit, a set_mode — and "what am I talking
            // to" is a question about now. Null label = the backend published no modes.
            ModeLabel: BackendModeId is null && _modeConfigId is null && _modeNames.Count == 0 ? null : _modeLabel,
            ModeId: BackendModeId,
            ModeName: BackendModeName,
            OpenedInModeId: OpenedInModeId,
            OpenedInModeName: OpenedInModeId is { } o && _modeNames.TryGetValue(o, out var openedName) ? openedName : null,
            ModeOrigin: BackendModeId is { } id && _modeOrigins.TryGetValue(id, out var origin) ? origin.Source : null,
            ModeOriginRoot: BackendModeId is { } id2 && _modeOrigins.TryGetValue(id2, out var origin2) ? origin2.Root : null,
            ModePinFailure: BackendModePinFailure,
            RequestedModeId: _requestedModeId,
            RequestedModeFailure: RequestedModeFailure);

        /// <summary>
        /// Whether the agent's <c>initialize</c> advertised <c>_meta.steering.supported</c>; null until
        /// the handshake completes. Gates <see cref="SteerAsync"/>, and it must stay a per-session
        /// discovery rather than a per-agent assumption: the Claude adapter gained steering between
        /// 0.55.0 and 0.63.0, so the same configured backend answers differently depending on which
        /// copy is installed.
        /// </summary>
        internal bool? DiscoveredSteering { get; private set; }

        /// <summary>
        /// Whether the agent's <c>initialize</c> advertised
        /// <c>agentCapabilities.promptCapabilities.image</c>; null until the handshake completes. Gates
        /// whether a pasted image is sent as an image block or falls back to a path the agent must read
        /// (issue #118). Per-session discovery, never a per-agent assumption, for the same reason as
        /// <see cref="DiscoveredSteering"/>: it is a property of the copy on the other end of the pipe.
        /// </summary>
        internal bool? DiscoveredImagePrompts { get; private set; }

        /// <summary>
        /// Whether the agent's <c>initialize</c> advertised
        /// <c>agentCapabilities.sessionCapabilities.list</c>; null until the handshake completes. Gates
        /// the <c>session/list</c> call that enumerates the conversations the backend's own CLI has
        /// stored, so one started in the terminal can be picked up here (issue #108). Per-session
        /// discovery for the same reason as <see cref="DiscoveredSteering"/>.
        /// </summary>
        internal bool? DiscoveredSessionList { get; private set; }

        public IReadOnlyList<ModelInfo> DiscoveredModels => _discoveredModels;

        public string? CurrentModelId => _currentModelId;

        /// <summary>True once the session has opened and it exposed a model config option (so the backend
        /// supports live model selection via <c>session/set_config_option</c>). Null before initialize.
        /// Feeds ModelSelection capability discovery for config-defined agents.</summary>
        internal bool? DiscoveredModelSelection { get; private set; }

        /// <summary>Launches the CLI, performs the ACP handshake, and opens (or resumes) a session.</summary>
        internal Task InitializeAsync(CancellationToken cancellationToken) =>
            DescribingAgentFailuresAsync(async () =>
            {
                await ConnectAndHandshakeAsync(cancellationToken).ConfigureAwait(false);
                await OpenSessionAsync(cancellationToken).ConfigureAwait(false);
            });

        /// <summary>
        /// Launches the CLI and performs the ACP handshake, WITHOUT opening a conversation - the state
        /// <c>session/list</c> needs (issue #108). A session used this way can be asked what the backend
        /// has stored and then disposed, having created nothing.
        /// </summary>
        internal Task HandshakeOnlyAsync(CancellationToken cancellationToken) =>
            DescribingAgentFailuresAsync(() => ConnectAndHandshakeAsync(cancellationToken));

        /// <summary>
        /// Runs a start-up step, rewriting a JSON-RPC failure into something the chat can show. Shared
        /// by both entry points so a handshake that fails on its own reports exactly as it would have
        /// inside a full start - the failure is the agent's, not the caller's reason for handshaking.
        /// </summary>
        private async Task DescribingAgentFailuresAsync(Func<Task> step)
        {
            try
            {
                await step().ConfigureAwait(false);
            }
            catch (RemoteRpcException ex)
            {
                // A failed start surfaces to the user as "Engine error: <message>", and a JSON-RPC
                // failure's Message is only the protocol wrapper ("Internal error") — the reason rides
                // in the error `data`. Unwrap it exactly as a failed turn does, so the chat shows the
                // cause instead of a word that says nothing.
                //
                // Only a SHORT suffix here, unlike a failed turn. This path crosses the engine wire as
                // an ordinary exception, so all it has is a message — there is no Details field to
                // expand and no structured payload to put one in. The version is what gets the single
                // slot available, because that is the fact that resolved issue #82; the agent's stderr
                // for this launch is in engine.log either way.
                throw new InvalidOperationException(WithAgentIdentity(Describe(ex)), ex);
            }
        }

        /// <summary>
        /// Everything up to and including <c>initialize</c>: the connection, the client target, and the
        /// capability reads. Separated from <see cref="OpenSessionAsync"/> so a caller can handshake
        /// WITHOUT opening a session - which is what listing the backend's own stored conversations
        /// needs (issue #108), since <c>session/list</c> is answered by the agent process rather than by
        /// any session and opening one to ask would create a conversation nobody wanted.
        /// </summary>
        private async Task ConnectAndHandshakeAsync(CancellationToken cancellationToken)
        {
            var formatter = new SystemTextJsonFormatter
            {
                JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                },
            };

            var handler = new NewLineDelimitedMessageHandler(
                _connection.Sending,
                _connection.Receiving,
                formatter);

            _rpc = new JsonRpc(handler);
            // Dispatch inbound messages through our own FIFO rather than the thread pool, so a turn
            // can drain them before it closes (see _dispatch / issue #33). Must be set before
            // StartListening.
            _rpc.SynchronizationContext = _dispatch;
            _clientTarget = new AcpClientTarget(
                _ide, EmitToCurrentTurn, () => _sessionId, _authTokenProvider, OnConfigOptionsUpdated,
                OnReplayedUpdate,
                // The same directory ResolveCwd hands session/new, so a relative path the agent
                // reports is rooted against the cwd it actually measured it from.
                _workspaceRoot.Root,
                // Import is exactly the load window, and reusing it is the point: the drain in
                // OpenSessionAsync's finally is what makes the window's closing edge trustworthy
                // (#33), so a second window invented here would be the same race again.
                () => _loadingHistory && _sessionOptions.ImportHistory,
                RecordImportedEntry,
                () => _loadingHistory,
                OnModeUpdated,
                () => Interlocked.Increment(ref _replayedDuringLoad));
            _rpc.AddLocalRpcTarget(_clientTarget, new JsonRpcTargetOptions());
            _rpc.StartListening();

            var initialized = await _rpc.InvokeWithParameterObjectAsync<InitializeResult>(
                "initialize",
                new InitializeParams(
                    AcpProtocolVersion,
                    new ClientCapabilities(new FsCapabilities(ReadTextFile: true, WriteTextFile: true), Terminal: false),
                    new Implementation(Branding.AcpClientName, Branding.AcpClientVersion)),
                cancellationToken).ConfigureAwait(false);

            if (initialized.AgentCapabilities is { ValueKind: JsonValueKind.Object } agentCaps)
            {
                DiscoveredLoadSession =
                    agentCaps.TryGetProperty("loadSession", out var loadSession) &&
                    loadSession.ValueKind == JsonValueKind.True;

                DiscoveredImagePrompts = ParseImagePromptsSupported(agentCaps);
                DiscoveredSessionList = ParseSessionListSupported(agentCaps);
            }

            DiscoveredSteering = ParseSteeringSupported(initialized.Meta);

            AuthGuidance = ParseAuthGuidance(initialized.AuthMethods);
            RecordAgentIdentity(initialized);
        }

        /// <summary>
        /// Upper bound on <c>session/list</c> paging. The ACP schema defines a <c>nextCursor</c> and
        /// claude-agent-acp 0.70.0 never sends one (it returns the whole array flat), so this exists
        /// against a backend that echoes its own cursor rather than against a real page count - an
        /// unbounded loop there would hang the picker with no error anywhere.
        /// </summary>
        private const int MaxSessionListPages = 10;

        /// <summary>Companion bound to <see cref="MaxSessionListPages"/>, on entries rather than pages.</summary>
        private const int MaxSessionListEntries = 500;

        /// <inheritdoc/>
        public async Task<IReadOnlyList<BackendSessionInfo>> ListSessionsAsync(
            string? cwd, CancellationToken cancellationToken = default)
        {
            if (_rpc is null)
                throw new InvalidOperationException("Session has not been initialized.");

            // Not advertised means not asked. A MethodNotFound would be harmless, but the capability is
            // exactly what it is there to tell us and spending a round trip to disbelieve it is not.
            if (DiscoveredSessionList != true)
                return Array.Empty<BackendSessionInfo>();

            var sessions = new List<BackendSessionInfo>();
            string? cursor = null;

            for (var page = 0; page < MaxSessionListPages; page++)
            {
                // Read as a raw JsonElement rather than a typed record, for the reason
                // SetModelViaConfigOptionAsync documents: StreamJsonRpc's formatter re-deserializes a
                // stored result from a NUL-padded span, and this response grows with the user's history.
                var response = await _rpc.InvokeWithParameterObjectAsync<JsonElement>(
                    "session/list",
                    new ListSessionsParams(cwd, cursor),
                    cancellationToken).ConfigureAwait(false);

                if (response.ValueKind != JsonValueKind.Object
                    || !response.TryGetProperty("sessions", out var entries)
                    || entries.ValueKind != JsonValueKind.Array)
                {
                    break;
                }

                foreach (var entry in entries.EnumerateArray())
                {
                    // One malformed entry is not a reason to lose the rest of a user's history.
                    if (ReadSessionInfo(entry) is { } info)
                        sessions.Add(info);
                    if (sessions.Count >= MaxSessionListEntries)
                        break;
                }

                if (sessions.Count >= MaxSessionListEntries)
                {
                    System.Console.Error.WriteLine(
                        $"[acp] session/list stopped at {MaxSessionListEntries} entries; the backend has more.");
                    break;
                }

                var next = response.TryGetProperty("nextCursor", out var nextCursor)
                    && nextCursor.ValueKind == JsonValueKind.String
                        ? nextCursor.GetString()
                        : null;

                // A cursor that has not moved is a backend paging in a circle, not a next page.
                if (string.IsNullOrEmpty(next) || string.Equals(next, cursor, StringComparison.Ordinal))
                    break;

                cursor = next;
            }

            return sessions;
        }

        /// <summary>
        /// Reads one ACP <c>SessionInfo</c>. Only <c>sessionId</c> is required - an entry without one
        /// cannot be opened, so it is dropped rather than shown as a row that does nothing.
        /// </summary>
        private static BackendSessionInfo? ReadSessionInfo(JsonElement entry)
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("sessionId", out var id)
                || id.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var sessionId = id.GetString();
            if (string.IsNullOrEmpty(sessionId))
                return null;

            var title = entry.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String
                ? titleEl.GetString()
                : null;

            var cwd = entry.TryGetProperty("cwd", out var cwdEl) && cwdEl.ValueKind == JsonValueKind.String
                ? cwdEl.GetString()
                : null;

            // Left null rather than defaulted when unparseable: the host sorts on this, and a made-up
            // timestamp would order a user's conversations by our guess instead of saying it cannot.
            var updatedAt = ReadTimestamp(entry, "updatedAt");

            // Creation time, so a conversation created and never used can be recognised - the host has
            // no message count to ask for on either backend (BackendSessionFilter). ACP's own schema
            // has no createdAt, so the top-level read is speculative and costs one lookup; the field
            // that exists today is Kiro's, under _meta. Claude reports neither, which is exactly why
            // the filter refuses to act on one signal.
            var createdAt = ReadTimestamp(entry, "createdAt")
                ?? (entry.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object
                    && meta.TryGetProperty("kiro", out var kiro) && kiro.ValueKind == JsonValueKind.Object
                        ? ReadTimestamp(kiro, "createdAt")
                        : null);

            return new BackendSessionInfo(sessionId!, title, updatedAt, cwd) { CreatedAt = createdAt };
        }

        // One ISO 8601 property, or null - unparseable and absent are the same answer here, both
        // meaning "the backend did not tell us", and neither may become a value we made up.
        private static DateTimeOffset? ReadTimestamp(JsonElement owner, string property) =>
            owner.TryGetProperty(property, out var el)
            && el.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(
                el.GetString(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var parsed)
                ? parsed
                : null;

        /// <summary>
        /// Opens the conversation - <c>session/new</c>, or <c>session/load</c> when resuming - and
        /// applies the requested model. Must follow <see cref="ConnectAndHandshakeAsync"/>.
        /// </summary>
        private async Task OpenSessionAsync(CancellationToken cancellationToken)
        {
            if (_rpc is null)
                throw new InvalidOperationException("Session has not been initialized.");

            var cwd = ResolveCwd();
            var mcpServers = BuildMcpServers();
            NewSessionResult result;
            if (!string.IsNullOrEmpty(_sessionOptions.ResumeConversationId))
            {
                // session/load REPLAYS the whole conversation back as session/update notifications before
                // it responds — that is the spec'd behaviour, so a client can rebuild its UI from it. We
                // rebuild ours from our own append-only log before this call is ever made, so the
                // backend's copy is pure duplication. See EmitToCurrentTurn for what it did.
                //
                // Unless there IS no log of ours: a conversation started in the backend's terminal CLI
                // has only this replay to be rebuilt from, so SessionOptions.ImportHistory captures it
                // instead (issue #108). Same window, opposite disposal.
                //
                // The FIFO alone is not enough to bound this — see the drain in the finally below.
                _loadingHistory = true;
                try
                {
                    result = await _rpc.InvokeWithParameterObjectAsync<NewSessionResult>(
                        "session/load",
                        new LoadSessionParams(_sessionOptions.ResumeConversationId!, cwd, mcpServers, null, _sessionMeta),
                        cancellationToken).ConfigureAwait(false);
                    _sessionId = _sessionOptions.ResumeConversationId!;
                }
                catch (RemoteRpcException ex)
                {
                    // The backend doesn't know this conversation. That is an ordinary state, not a
                    // reason to refuse to start (see IResumeFallbackReport): the id can belong to a
                    // different agent engine — a Kiro v3 id replayed against its v2 store answers
                    // "Session not found: sess_…", which reached the user as a bare "Internal error" —
                    // the backend may have pruned it, or it may never have persisted because its first
                    // turn failed. The transcript is already replayed locally, so start fresh and
                    // record why; the host says the agent didn't get the earlier context.
                    ResumeFailureReason = DescribeError(ex);
                    System.Console.Error.WriteLine(
                        $"[acp] resume of '{_sessionOptions.ResumeConversationId}' failed, starting fresh: {ResumeFailureReason}");

                    result = await _rpc.InvokeWithParameterObjectAsync<NewSessionResult>(
                        "session/new",
                        new NewSessionParams(cwd, mcpServers, null, _sessionMeta),
                        cancellationToken).ConfigureAwait(false);
                    _sessionId = result.SessionId;
                }
                finally
                {
                    // The response can overtake the notifications that preceded it (issue #33), so the
                    // flag cannot be cleared on the response alone — the tail of the history would land
                    // after it and leak. Drain the inbound FIFO first, exactly as a turn's end does.
                    // Measured: without this the last frames still reach the host.
                    await DrainInboundDispatchAsync().ConfigureAwait(false);
                    _loadingHistory = false;
                }
            }
            else
            {
                result = await _rpc.InvokeWithParameterObjectAsync<NewSessionResult>(
                    "session/new",
                    new NewSessionParams(cwd, mcpServers, null, _sessionMeta),
                    cancellationToken).ConfigureAwait(false);
                _sessionId = result.SessionId;
            }

            // Stamped where the conversation actually becomes usable, not at initialize: a resume that
            // falls back to a fresh session above has handshaken once and opened twice, and it is the
            // open the user is now talking to.
            _openedAt = DateTimeOffset.Now;

            ParseModelConfigOption(result.ConfigOptions);
            ParseModeState(result.Modes, result.ConfigOptions);
            OpenedInModeId = BackendModeId;

            // Before anything else can run: the mode the backend opened in is whatever its settings
            // said, and one of those settings files is in the working tree (issue #269). No turn is in
            // flight in this window, so nothing can slip through it.
            await PinBackendModeAsync(cancellationToken).ConfigureAwait(false);
            await ApplyRequestedModeAsync(cancellationToken).ConfigureAwait(false);

            // Honour a requested model when the backend selects models via config options (Kiro v1/v2
            // instead takes --model as a launch flag, so their _modelConfigId is null and this is
            // skipped). A backend that publishes the selector later (Kiro v3) leaves the request pending
            // for OnConfigOptionsUpdated — without that it silently ran on the backend's default.
            if (!string.IsNullOrEmpty(_sessionOptions.ModelId))
            {
                // Pending is the fallback for EVERY reason the request can't be served yet, not only a
                // missing selector: a selector published with no catalogue behind it (v3 does publish
                // the two separately) would otherwise leave the request neither applied nor held.
                if (_modelConfigId is not null && ShouldApplyModel(_sessionOptions.ModelId!))
                    await SetModelViaConfigOptionAsync(_sessionOptions.ModelId!, cancellationToken).ConfigureAwait(false);
                else if (_sessionOptions.ModelId != _currentModelId)
                    _pendingModelId = _sessionOptions.ModelId;
            }
        }

        /// <summary>
        /// Files what the agent said about ITSELF at <c>initialize</c> into the diagnostics, so a later
        /// failure can name the version that produced it (issue #82, where a backend was fixed by an
        /// upgrade and the version appeared in no message and no log).
        ///
        /// <para>Two sources, both optional and both taken verbatim rather than normalized — a version
        /// string is evidence, and reformatting it only makes it harder to match against the backend's
        /// own release notes.</para>
        /// <list type="bullet">
        /// <item><c>agentInfo</c> — standard ACP, and authoritative because it describes the program on
        /// the other end of the pipe rather than whatever a second process would report. Sent by
        /// claude-agent-acp; NOT sent by kiro-cli, which is why <see cref="AgentVersionProbe"/> exists
        /// as the fallback.</item>
        /// <item>Kiro's <c>_meta.kiro.logging.logDir</c> — the CLI's OWN log directory. Backend-specific
        /// shape, decoded here in the ACP layer where the other <c>_meta</c> reads live, and surfaced
        /// on a backend-agnostic field. Worth relaying because it is the one diagnostic surface we
        /// neither own nor duplicate: a user who has exhausted our logs still has somewhere to look.</item>
        /// </list>
        /// </summary>
        private void RecordAgentIdentity(InitializeResult initialized)
        {
            if (_diagnostics is null)
                return;

            if (initialized.AgentInfo is { Name: { Length: > 0 } name } info)
            {
                _diagnostics.Version = info.Version is { Length: > 0 } version
                    ? $"{name} {version}"
                    : name;
            }

            if (initialized.AgentCapabilities is { ValueKind: JsonValueKind.Object } caps
                && caps.TryGetProperty("_meta", out var meta)
                && meta.ValueKind == JsonValueKind.Object)
            {
                // Walk every vendor bag rather than reaching for "kiro" by name: the shape
                // (_meta.<vendor>.logging.logDir) is the thing worth reading, and hard-coding the one
                // vendor we have seen it from would silently miss the next.
                foreach (var vendor in meta.EnumerateObject())
                {
                    if (vendor.Value.ValueKind == JsonValueKind.Object
                        && vendor.Value.TryGetProperty("logging", out var logging)
                        && logging.ValueKind == JsonValueKind.Object
                        && logging.TryGetProperty("logDir", out var logDir)
                        && logDir.ValueKind == JsonValueKind.String
                        && logDir.GetString() is { Length: > 0 } directory)
                    {
                        _diagnostics.LogDirectory = directory;
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// The permission mode the backend says it is in — the wire id (<c>default</c>,
        /// <c>bypassPermissions</c>; Kiro reports its agent config here instead). Null until reported.
        /// </summary>
        internal string? BackendModeId { get; private set; }

        /// <summary>The mode the backend reported when the session opened, BEFORE any pin moved it —
        /// the thing a settings file chose. What #270's panel should say beside the live mode.</summary>
        internal string? OpenedInModeId { get; private set; }

        /// <summary>The backend's display name for <see cref="BackendModeId"/>, where it gave one
        /// (Claude's <c>default</c> is named "Manual" — the name is what to print, #270).</summary>
        internal string? BackendModeName =>
            BackendModeId is { } id && _modeNames.TryGetValue(id, out var name) ? name : null;

        /// <summary>Every mode id a <c>current_mode_update</c> reported, in order — including the one
        /// our own pin provoked. A proof reads this to tell a change we asked for from one we did not.</summary>
        internal IReadOnlyList<string> BackendModeUpdates
        {
            get { lock (_modeUpdates) return _modeUpdates.ToList(); }
        }

        /// <summary>
        /// Reads the backend's permission mode from the two places ACP puts it: the session's
        /// <c>modes</c> object (<c>currentModeId</c> + <c>availableModes</c>) and the config option in
        /// category <c>mode</c>, whichever this frame carries. Best-effort: a shape mismatch leaves the
        /// mode unreported, which the pin then treats as "cannot be pinned", not as "fine".
        /// </summary>
        private void ParseModeState(JsonElement? modes, JsonElement? configOptions)
        {
            if (modes is { ValueKind: JsonValueKind.Object } m)
            {
                if (m.TryGetProperty("currentModeId", out var current) && current.ValueKind == JsonValueKind.String)
                    BackendModeId = current.GetString();
                if (m.TryGetProperty("availableModes", out var available) && available.ValueKind == JsonValueKind.Array)
                {
                    foreach (var mode in available.EnumerateArray())
                    {
                        RecordModeName(mode, "id");
                        RecordModeOrigin(mode);
                    }
                }
            }

            if (configOptions is not { ValueKind: JsonValueKind.Array } options)
                return;

            foreach (var option in options.EnumerateArray())
            {
                if (option.ValueKind != JsonValueKind.Object
                    || !option.TryGetProperty("category", out var cat) || cat.ValueKind != JsonValueKind.String
                    || cat.GetString() != "mode")
                    continue;

                _modeConfigId = option.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString()
                    : "mode";
                if (option.TryGetProperty("currentValue", out var value) && value.ValueKind == JsonValueKind.String)
                    BackendModeId = value.GetString();
                if (option.TryGetProperty("options", out var values) && values.ValueKind == JsonValueKind.Array)
                    foreach (var entry in values.EnumerateArray())
                        RecordModeName(entry, "value");
                return;
            }
        }

        /// <summary>
        /// Reads a mode's origin from its vendor bag — <c>_meta.&lt;vendor&gt;.source</c> ("bundled",
        /// "workspace", "user" on Kiro v3) plus <c>resource.source.root</c> where present. Every vendor
        /// bag is walked rather than "kiro" by name, as <see cref="RecordAgentIdentity"/> does: the
        /// shape is the thing worth reading, and the next backend to tag its modes is unlikely to
        /// spell its vendor the same way.
        /// </summary>
        private void RecordModeOrigin(JsonElement mode)
        {
            if (mode.ValueKind != JsonValueKind.Object
                || !mode.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String
                || idEl.GetString() is not { Length: > 0 } id
                || !mode.TryGetProperty("_meta", out var meta) || meta.ValueKind != JsonValueKind.Object)
                return;

            foreach (var vendor in meta.EnumerateObject())
            {
                if (vendor.Value.ValueKind != JsonValueKind.Object
                    || !vendor.Value.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.String
                    || source.GetString() is not { Length: > 0 } origin)
                    continue;

                string? root = null;
                if (vendor.Value.TryGetProperty("resource", out var resource) && resource.ValueKind == JsonValueKind.Object
                    && resource.TryGetProperty("source", out var rs) && rs.ValueKind == JsonValueKind.Object
                    && rs.TryGetProperty("root", out var rootEl) && rootEl.ValueKind == JsonValueKind.String)
                    root = rootEl.GetString();

                _modeOrigins[id] = (origin, root);
                return;
            }
        }

        private void RecordModeName(JsonElement entry, string idProperty)
        {
            if (entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty(idProperty, out var id) && id.ValueKind == JsonValueKind.String
                && entry.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                && id.GetString() is { Length: > 0 } key)
            {
                _modeNames[key] = name.GetString() ?? key;
            }
        }

        /// <summary>
        /// Why the backend's mode could not be asserted at open, or null when it was (or nothing was
        /// declared). The host-facing account is the <see cref="AgentEvent.BackendNotice"/> raised
        /// beside it; this is the same fact for the session panel and the proofs.
        /// </summary>
        internal string? BackendModePinFailure { get; private set; }

        /// <summary>
        /// Asserts the backend mode the host's picker maps to (<see cref="BackendPermissionModes.For"/>)
        /// on the open session. Always sent, never skipped because the backend already reports that
        /// mode — the report is the adapter's record, and the CLI behind it holds its own copy (the two
        /// are synced by exactly this call); asserting an already-current value is harmless.
        /// <para>
        /// <b>Fails OPEN, and says so.</b> A pin can only fail on an adapter change (the ask mode is
        /// always advertised), and refusing every session for that would take the whole tool down over
        /// an update. So the session continues and the failure is surfaced at two levels, decided by
        /// what the backend reports AFTER the attempt: still in the mapped mode — the gate is on, the
        /// assertion merely failed, a warning; in any other mode, or none — this is the #269 hole
        /// itself, so the notice says the picker has no effect on this session and names the setting
        /// to remove. Either way it is in engine.log and on <see cref="BackendModePinFailure"/>.
        /// </para>
        /// </summary>
        private async Task PinBackendModeAsync(CancellationToken cancellationToken)
        {
            if (_backendModes is null)
                return;

            var target = _backendModes.For(_sessionOptions.PermissionMode);
            var opened = BackendModeId ?? "(unreported)";
            string? failure = null;

            if (_modeConfigId is null)
            {
                failure = $"the agent published no permission-mode option, so '{target}' could not be requested.";
            }
            else
            {
                try
                {
                    var result = await _rpc!.InvokeWithParameterObjectAsync<JsonElement>(
                        "session/set_config_option",
                        new SetConfigOptionParams(_sessionId, _modeConfigId, target),
                        cancellationToken).ConfigureAwait(false);

                    if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("configOptions", out var configOptions))
                        ParseModeState(null, configOptions);

                    if (!string.Equals(BackendModeId, target, StringComparison.Ordinal))
                        failure = $"the agent reports '{BackendModeId ?? "(unreported)"}' after being asked for '{target}'.";
                }
                catch (RemoteRpcException ex)
                {
                    failure = $"the agent refused '{target}': {DescribeError(ex)}";
                }
            }

            if (failure is null)
            {
                System.Console.Error.WriteLine(
                    $"[mode] pinned '{target}' ({BackendModeName ?? target}); the agent opened in '{opened}'");
                return;
            }

            BackendModePinFailure = failure;
            var gateIsOn = string.Equals(BackendModeId, target, StringComparison.Ordinal);
            System.Console.Error.WriteLine(
                $"[mode] NOT pinned to '{target}' (opened in '{opened}', now '{BackendModeId ?? "(unreported)"}'): {failure}");

            // Two sentences the user reads, and the wording is the behaviour: the second level has to
            // say that the picker is decorative for this session and what to do about it, or a reader
            // takes "could not confirm" as a diagnostic and carries on under a gate that is off.
            var mode = BackendModeName is { } name ? $"'{name}'" : $"'{BackendModeId ?? "an unreported mode"}'";
            EmitToCurrentTurn(gateIsOn
                ? new AgentEvent.BackendNotice(
                    $"Could not confirm the agent's permission mode: {failure} It reports {mode}, which asks, so nothing changes.",
                    "warning")
                : new AgentEvent.BackendNotice(
                    $"The agent opened in permission mode {mode} and {Branding.ProductName} could not switch it to '{_modeNames.GetValueOrDefault(target, target)}': {failure} "
                    + "In this mode the agent approves its own actions, so the permission mode chosen here has no effect on this session. "
                    + (_backendModes.SettingsHint is { Length: > 0 } hint ? hint + " " : string.Empty)
                    + "Remove the setting and start a new session.",
                    "error"));
        }

        /// <summary>
        /// Why the requested session mode (<see cref="AcpAgentProvider.RequestedSessionModeId"/>) was
        /// not applied, or null when it was or none was requested. The user-facing account is the
        /// notice raised beside it.
        /// </summary>
        internal string? RequestedModeFailure { get; private set; }

        /// <summary>
        /// Selects the provider's requested session mode over the wire, and — like the permission-mode
        /// pin — never lets it cost the session. Three outcomes, each said: applied (an engine.log line,
        /// plus a WARNING when the mode is repository-defined, because its tool-trust rules then come
        /// from the repository and nothing else in the pane would say so); not offered (an ERROR notice
        /// naming the mode the session is running in instead); refused (the same, with the backend's
        /// reason). A mode already current is not re-sent, but still gets the origin warning.
        /// </summary>
        private async Task ApplyRequestedModeAsync(CancellationToken cancellationToken)
        {
            if (_requestedModeId is null)
                return;

            var requested = _requestedModeId;
            var runningAs = BackendModeName is { } current ? $"'{current}' ({BackendModeId})" : $"'{BackendModeId ?? "its default"}'";

            if (!_modeNames.ContainsKey(requested))
            {
                RequestedModeFailure = $"the backend does not offer '{requested}'";
                System.Console.Error.WriteLine($"[mode] requested agent '{requested}' not offered; running as {runningAs}");
                EmitToCurrentTurn(new AgentEvent.BackendNotice(
                    $"The agent this session was configured to run as, '{requested}', is not one the backend offers here, "
                    + $"so it is running as {runningAs}. Check the configured agent name; a workspace-defined agent is only "
                    + "offered from its own workspace.",
                    "error"));
                return;
            }

            if (!string.Equals(BackendModeId, requested, StringComparison.Ordinal))
            {
                try
                {
                    await _rpc!.InvokeWithParameterObjectAsync<JsonElement>(
                        "session/set_mode",
                        new SetModeParams(_sessionId, requested),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (RemoteRpcException ex)
                {
                    RequestedModeFailure = $"the backend refused '{requested}': {DescribeError(ex)}";
                    System.Console.Error.WriteLine($"[mode] requested agent '{requested}' refused: {DescribeError(ex)}; running as {runningAs}");
                    EmitToCurrentTurn(new AgentEvent.BackendNotice(
                        $"The backend refused to run this session as the configured agent '{requested}': {DescribeError(ex)} "
                        + $"It is running as {runningAs}.",
                        "error"));
                    return;
                }

                // The switch is confirmed by the call returning; the current_mode_update that follows
                // is the backend's own announcement and may still be in flight.
                BackendModeId = requested;
                System.Console.Error.WriteLine($"[mode] running as configured agent '{requested}' ({BackendModeName ?? requested})");
            }

            if (_modeOrigins.TryGetValue(requested, out var origin)
                && string.Equals(origin.Source, "workspace", StringComparison.OrdinalIgnoreCase))
            {
                var where = origin.Root is { Length: > 0 } root ? $" under {root}" : string.Empty;
                System.Console.Error.WriteLine($"[mode] agent '{requested}' is workspace-defined{where}");
                EmitToCurrentTurn(new AgentEvent.BackendNotice(
                    $"This session runs as '{BackendModeName ?? requested}', an agent defined by the repository{where}. "
                    + "Its tool-trust rules come from the repository, not from your settings: what it approves on its "
                    + "own never reaches the permission prompt here.",
                    "warning"));
            }
        }

        /// <summary>
        /// A <c>current_mode_update</c>: the backend's mode changed. Recorded either way; when it leaves
        /// a pinned mode it is said out loud, because a change we did not ask for is the #269 shape
        /// arriving mid-session, and nothing else in the pane would show it.
        /// </summary>
        private void OnModeUpdated(JsonElement update)
        {
            if (!update.TryGetProperty("currentModeId", out var current) || current.ValueKind != JsonValueKind.String)
                return;

            var modeId = current.GetString() ?? string.Empty;
            BackendModeId = modeId;
            lock (_modeUpdates) _modeUpdates.Add(modeId);

            var pinned = _backendModes?.For(_sessionOptions.PermissionMode);
            if (pinned is not null && !string.Equals(modeId, pinned, StringComparison.Ordinal))
                System.Console.Error.WriteLine($"[mode] backend left the pinned '{pinned}' mode for '{modeId}'");
            else
                System.Console.Error.WriteLine($"[mode] backend reports '{modeId}'");
        }

        /// <summary>True when <paramref name="modelId"/> is a known model this session isn't already on.
        /// Guards against asking a backend for a model it never advertised.</summary>
        private bool ShouldApplyModel(string modelId) =>
            modelId != _currentModelId && _discoveredModels.Any(m => m.Id == modelId);

        /// <summary>
        /// Handles a <c>config_option_update</c> notification: re-reads the model selector (which is the
        /// only way Kiro's v3 engine ever exposes one — its <c>session/new</c> response carries no
        /// models and it has no <c>session/set_model</c>) and applies a model request that arrived
        /// before the selector existed. Best-effort by design: this is a notification handler, so a
        /// failure here must not fault the session — the user can still switch from the picker.
        /// </summary>
        private void OnConfigOptionsUpdated(JsonElement update)
        {
            if (!update.TryGetProperty("configOptions", out var configOptions))
                return;

            ParseModelConfigOption(configOptions);
            ParseModeState(null, configOptions);

            if (_modelConfigId is null || _pendingModelId is null)
                return;

            var modelId = _pendingModelId;

            // Already there — the request is SATISFIED, so retire it.
            if (modelId == _currentModelId)
            {
                _pendingModelId = null;
                return;
            }

            // Not in the catalogue yet. v3 publishes the selector and its options in separate frames,
            // so this frame can carry `_modelConfigId` with `options` absent or empty — and
            // ParseModelConfigOption only assigns `_discoveredModels` when it actually collected some.
            // Consuming the request here would DROP it un-attempted: no later frame, however complete,
            // could apply it, and the session would silently run on the backend's default — the exact
            // failure this pending mechanism exists to prevent.
            if (!_discoveredModels.Any(m => m.Id == modelId))
                return;

            _pendingModelId = null; // one attempt from here: a retry loop on every update frame helps nobody

            _ = Task.Run(async () =>
            {
                try
                {
                    await SetModelViaConfigOptionAsync(modelId, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Left on the backend's default; the picker still reflects the user's choice and a
                    // manual switch (or the next session) re-applies it.
                }
            });
        }

        public async IAsyncEnumerable<AgentEvent> SendAsync(PromptInput prompt, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (_rpc is null)
                throw new InvalidOperationException("Session has not been initialized.");

            var channel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions { SingleReader = true });
            _currentTurn = channel;

            // Inject a snapshot of the editor/solution (active file, selection, open files,
            // diagnostics) ahead of the user's prompt so Kiro sees the IDE context. Best-effort.
            var blocks = new List<ContentBlock>();
            try
            {
                var snapshot = await _ide.Workspace.CaptureAsync(cancellationToken).ConfigureAwait(false);
                var context = WorkspaceContextFormatter.Format(
                    snapshot, _workspaceRoot.Root, _sessionOptions.WorkspaceRootPath);
                if (!string.IsNullOrEmpty(context))
                    blocks.Add(new ContentBlock("text", context));
            }
            catch
            {
                // Context capture is best-effort; never fail the turn over it.
            }

            // Steer the agent toward the IDE-integrated MCP tools once per session: agents with
            // capable built-ins (e.g. Claude Code's shell) otherwise reach for those instead of the
            // tools that act on the IDE's live state (build exactly as the IDE does, Error List).
            if (!_ideToolsHintSent)
            {
                _ideToolsHintSent = true;
                if (BuildIdeToolsHint() is { } hint)
                    blocks.Add(new ContentBlock("text", hint));
            }

            AddAttachments(blocks, prompt);
            blocks.Add(new ContentBlock("text", prompt.Text));
            var promptParams = new PromptParams(_sessionId, blocks.ToArray());

            // Run the prompt request concurrently with reading streamed updates; the request
            // completes when the turn ends (with a StopReason), which closes the channel.
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await _rpc
                        .InvokeWithParameterObjectAsync<PromptResult>("session/prompt", promptParams, cancellationToken)
                        .ConfigureAwait(false);
                    await DrainInboundDispatchAsync().ConfigureAwait(false);
                    // Per-turn tokens ride the response, not a notification (Claude Code); backends that
                    // don't report them leave Usage undefined and this maps to null.
                    channel.Writer.TryWrite(
                        new AgentEvent.TurnCompleted(AcpMapper.MapPromptUsage(result.Usage), result.StopReason));
                }
                catch (OperationCanceledException)
                {
                    await DrainInboundDispatchAsync().ConfigureAwait(false);
                    channel.Writer.TryWrite(new AgentEvent.TurnCompleted(Usage: null, StopReason: "cancelled"));
                }
                catch (Exception ex)
                {
                    await DrainInboundDispatchAsync().ConfigureAwait(false);
                    // Details carries what the backend can be asked about but never volunteers: its
                    // version, the error code, its own log directory, and what it wrote to stderr this
                    // session. Without diagnostics (tests, the in-memory fake) this degrades to the
                    // exception text it has always been.
                    var details = _diagnostics?.Describe(ErrorCodeOf(ex), ex.ToString()) ?? ex.ToString();
                    channel.Writer.TryWrite(new AgentEvent.SessionError(Describe(ex), details));
                }
                finally
                {
                    channel.Writer.TryComplete();

                    // Retire OUR channel, never whatever is current. TryComplete releases the consumer's
                    // WaitToReadAsync on a POOL thread (the channel is created without
                    // AllowSynchronousContinuations), so the next SendAsync can legitimately set
                    // _currentTurn between these two statements — the host releases a queued mid-turn
                    // tray as one delivery at exactly this moment, so the window is not hypothetical. A
                    // blind null then detaches that whole turn's events onto the out-of-turn path in
                    // EmitToCurrentTurn: content with no turn to belong to.
#pragma warning disable 420 // Interlocked is its own fence; the volatile read elsewhere is unaffected.
                    Interlocked.CompareExchange(ref _currentTurn, null, channel);
#pragma warning restore 420

                    // A write capture cannot outlive the turn that produced it (see AcpClientTarget).
                    // Posted through the dispatch context rather than run here, so it stays serialized
                    // with the frame handlers that read the dictionary. The drain above has already run,
                    // so every frame entitled to consume a capture has had its chance.
                    _dispatch.Post(_ => _clientTarget?.OnTurnEnded(), null);
                }
            }, CancellationToken.None);

            // ChannelReader.ReadAllAsync isn't on the netstandard2.0 surface, so drain manually.
            var reader = channel.Reader;
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var ev))
                    yield return ev;
            }
        }

        /// <summary>
        /// Waits until every inbound message that arrived before the turn's result has been handled,
        /// so the turn channel isn't closed while the last <c>session/update</c> notifications are
        /// still queued behind it (issue #33). Bounded — a stuck handler degrades to the old
        /// behaviour rather than hanging the turn.
        /// </summary>
        private async Task DrainInboundDispatchAsync()
        {
            try
            {
                var drained = _dispatch.FlushAsync();
                await Task.WhenAny(drained, Task.Delay(DispatchDrainTimeout)).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort ordering: never fail a turn over the barrier itself.
            }
        }

        /// <summary>
        /// Human-readable one-liner for a failed turn. A JSON-RPC failure from the agent arrives as a
        /// <see cref="RemoteInvocationException"/> whose <c>Message</c> is the generic protocol wrapper
        /// (e.g. "Internal error"); the real reason (e.g. "The request was throttled by the service")
        /// rides in the error <c>data</c>. Surface that so the chat shows the cause, not just the wrapper.
        /// </summary>
        private static string DescribeError(Exception ex)
        {
            if (ex is not RemoteInvocationException rie)
                return ex.Message;

            var data = rie.DeserializedErrorData ?? rie.ErrorData;
            var detail = data switch
            {
                null => null,
                string s => s,
                JsonElement je => je.ValueKind == JsonValueKind.String ? je.GetString() : je.ToString(),
                _ => data.ToString(),
            };
            return string.IsNullOrWhiteSpace(detail) ? rie.Message : $"{rie.Message}: {detail}";
        }

        /// <summary>
        /// <see cref="DescribeError"/> plus the agent's own sign-in instructions when the failure reads
        /// like an auth one. An ACP agent states how to authenticate exactly once — in its
        /// <c>initialize</c> <c>authMethods</c> — and nothing else in the pipeline knows it, so a
        /// backend that fails a turn with "Auth refresh callback failed: You are not logged in" would
        /// otherwise leave the user to guess which login it meant.
        /// </summary>
        private string Describe(Exception ex)
        {
            // A lost connection is the ONE failure whose own text says nothing a user can act on:
            // "The JSON-RPC connection with the remote party was lost before the request could
            // complete" names neither the agent, nor what happened, nor what to do — the same defect
            // issue #82 was reported for, one layer along. Seen live 2026-08-08 as the headline of a
            // real failure. The protocol text is not lost: it stays in the details panel, where it is
            // evidence rather than an announcement.
            if (ex is ConnectionLostException)
                return AgentStoppedRespondingMessage();

            var described = DescribeError(ex);
            return AuthGuidance is { Length: > 0 } guidance && LooksLikeAuthFailure(described)
                ? $"{described} — {guidance}"
                : described;
        }

        /// <summary>
        /// Plain language for a dropped connection, naming the backend and the one thing that recovers
        /// it. The version rides along when known, because "which version was that?" is the first
        /// question any report of this gets (see <see cref="AgentDiagnostics.Version"/>).
        /// </summary>
        private string AgentStoppedRespondingMessage()
        {
            var who = _diagnostics?.Version is { Length: > 0 } version
                ? $" ({version})"
                : string.Empty;
            return $"The agent stopped responding{who}. Start a new session to continue.";
        }

        /// <summary>
        /// The JSON-RPC error code, when the failure carried one. Reported alongside the version
        /// because the pair is often enough to match a backend's own changelog or issue tracker
        /// without reproducing anything — and because a code is stable across the wording changes that
        /// make error text impossible to search for.
        /// <see cref="RemoteMethodNotFoundException"/> does not derive from
        /// <see cref="RemoteInvocationException"/> (see <see cref="IsMethodNotFound"/>), so it is
        /// matched separately rather than being silently dropped.
        /// </summary>
        private static int? ErrorCodeOf(Exception ex) => ex switch
        {
            RemoteInvocationException invocation => invocation.ErrorCode,
            RemoteMethodNotFoundException => (int)StreamJsonRpc.Protocol.JsonRpcErrorCode.MethodNotFound,
            _ => null,
        };

        /// <summary>
        /// Appends the agent's version (and its exit code, if it has died) to a message that has
        /// nowhere else to carry it. No-op until the version is known — the probe is fire-and-forget,
        /// so a failure in the first moments of a launch may legitimately beat it, and "version
        /// unknown" is noise in a one-line message that a details panel can afford but this cannot.
        /// </summary>
        private string WithAgentIdentity(string message)
        {
            var suffix = _diagnostics?.ShortSuffix();
            return string.IsNullOrEmpty(suffix) ? message : $"{message} [{suffix}]";
        }

        /// <summary>Does this failure read as an authentication one? Deliberately narrow: appending
        /// sign-in instructions to an unrelated error is worse than omitting them from an auth one,
        /// because it sends the user off to fix something that isn't broken.</summary>
        private static bool LooksLikeAuthFailure(string message) =>
            Mentions(message, "auth") || Mentions(message, "log in") || Mentions(message, "logged in")
            || Mentions(message, "login") || Mentions(message, "credential")
            || Mentions(message, "unauthorized") || Mentions(message, "forbidden")
            || Mentions(message, "token expired") || Mentions(message, "expired token");

        private static bool Mentions(string haystack, string needle) =>
            haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// Flattens the agent's <c>initialize</c> <c>authMethods</c> (<c>[{id, name, description}]</c>)
        /// into one human line. Each entry's <c>description</c> is preferred because that is where an
        /// agent puts the actual instruction — Kiro's reads "Run 'kiro-cli login' in terminal to
        /// authenticate. See https://kiro.dev/docs/cli/authentication/" — falling back to the name and
        /// then the id. Null when the agent advertised none: an EMPTY array is the norm for agents that
        /// carry their own auth (Claude Code sends <c>authMethods:[]</c>), so its absence is not a
        /// signal of anything and must never be reported as one.
        /// </summary>
        private static string? ParseAuthGuidance(JsonElement? authMethods)
        {
            if (authMethods is not { ValueKind: JsonValueKind.Array } methods)
                return null;

            var parts = new List<string>();
            foreach (var method in methods.EnumerateArray())
            {
                if (method.ValueKind != JsonValueKind.Object)
                    continue;

                var text = Text(method, "description") ?? Text(method, "name") ?? Text(method, "id");
                if (text is not null)
                    parts.Add(text);
            }

            return parts.Count == 0 ? null : string.Join(" ", parts);

            static string? Text(JsonElement element, string property) =>
                element.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString())
                    ? value.GetString()!.Trim()
                    : null;
        }

        /// <summary>
        /// Reads <c>_meta.steering.supported</c> off the <c>initialize</c> result. Absent metadata, an
        /// absent <c>steering</c> object and an explicit <c>false</c> all mean the same thing — no
        /// steering — so this returns a plain bool rather than a nullable: unlike
        /// <c>loadSession</c>, there is nothing a caller could do differently for "the agent didn't
        /// say" than for "the agent said no".
        /// </summary>
        /// <summary>
        /// Appends the prompt's attachments as ACP content blocks, ahead of the user's text so the
        /// image and the sentence about it arrive adjacent and in the order the user assembled them.
        /// <para>
        /// Deliberately NOT gated on <see cref="DiscoveredImagePrompts"/>. The gate is published here
        /// and enforced by the host, because the host is the only layer that can do anything useful
        /// when the answer is no — it has the fallback (save the image and name its path). Re-checking
        /// here would only turn a message the user believes they sent into silence.
        /// </para>
        /// </summary>
        private static void AddAttachments(List<ContentBlock> blocks, PromptInput prompt)
        {
            foreach (var attachment in prompt.Attachments)
            {
                if (!string.IsNullOrEmpty(attachment.Data) && !string.IsNullOrEmpty(attachment.MimeType))
                    blocks.Add(ContentBlock.Image(attachment.MimeType, attachment.Data));
            }
        }

        /// <summary>
        /// Reads <c>promptCapabilities.image</c> off the agent's advertised capabilities. Explicitly
        /// true only — an agent that omits the section is treated as not taking images, which is the
        /// safe direction: the fallback (a path in the prompt text) works everywhere, while sending a
        /// block the agent does not model risks it being dropped silently or failing the whole prompt.
        /// </summary>
        private static bool ParseImagePromptsSupported(JsonElement agentCapabilities) =>
            agentCapabilities.TryGetProperty("promptCapabilities", out var prompt)
            && prompt.ValueKind == JsonValueKind.Object
            && prompt.TryGetProperty("image", out var image)
            && image.ValueKind == JsonValueKind.True;

        /// <summary>
        /// Reads <c>sessionCapabilities.list</c> off the agent's advertised capabilities.
        /// <para><b>The shape is the whole point of this method.</b> <c>sessionCapabilities</c> is a map
        /// of sub-OBJECTS, not of booleans — claude-agent-acp 0.70.0 sends
        /// <c>{additionalDirectories:{}, close:{}, delete:{}, fork:{}, list:{}, resume:{}}</c> — so the
        /// <c>ValueKind == JsonValueKind.True</c> spelling used by <c>loadSession</c> and
        /// <see cref="ParseImagePromptsSupported"/> a few lines away finds nothing here. It would not
        /// throw or warn: the capability would simply read false forever and the session picker would
        /// never appear, on a backend that supports it perfectly well.</para>
        /// <para>So the test is <em>declared at all</em>: present, and neither <c>null</c> nor
        /// <c>false</c>. That accepts today's empty object, accepts a future agent that sends <c>true</c>
        /// or an object with options in it, and still refuses an explicit opt-out. Unlike the image
        /// capability there is no risk in being generous — a wrong true costs one
        /// <c>MethodNotFound</c> on a call the user asked for, not a silently mangled prompt.</para>
        /// </summary>
        private static bool ParseSessionListSupported(JsonElement agentCapabilities) =>
            agentCapabilities.TryGetProperty("sessionCapabilities", out var sessionCaps)
            && sessionCaps.ValueKind == JsonValueKind.Object
            && sessionCaps.TryGetProperty("list", out var list)
            && list.ValueKind is not (JsonValueKind.Null or JsonValueKind.False or JsonValueKind.Undefined);

        private static bool ParseSteeringSupported(JsonElement? meta) =>
            meta is { ValueKind: JsonValueKind.Object } metaElement
            && metaElement.TryGetProperty("steering", out var steering)
            && steering.ValueKind == JsonValueKind.Object
            && steering.TryGetProperty("supported", out var supported)
            && supported.ValueKind == JsonValueKind.True;

        public async Task SetModelAsync(string modelId, CancellationToken cancellationToken = default)
        {
            if (_rpc is null)
                throw new InvalidOperationException("Session has not been initialized.");

            // Config-option agents (Claude Code) switch via session/set_config_option; others (Kiro)
            // use the standard session/set_model. Which one is data-driven off what session/new exposed.
            if (_modelConfigId is not null)
            {
                await SetModelViaConfigOptionAsync(modelId, cancellationToken).ConfigureAwait(false);
                return;
            }

            // Standard ACP model selection (SessionModelState / session/set_model); the empty result
            // object is ignored. Kiro's v1/v2 engines advertise the model list in session/new and honour
            // this (it's what kiro.dev/docs/cli/acp documents).
            try
            {
                await _rpc.InvokeWithParameterObjectAsync<object>(
                    "session/set_model",
                    new SetModelParams(_sessionId, modelId),
                    cancellationToken).ConfigureAwait(false);
                _currentModelId = modelId;
            }
            catch (RemoteRpcException ex) when (IsMethodNotFound(ex))
            {
                // The backend has no set_model, so it must be a config-option one whose selector we
                // haven't seen yet — Kiro v3 publishes config options in a notification that races the
                // session opening, and it answers set_model with a hard "Method not found". Try the
                // other route under its conventional id rather than failing the user's switch on a
                // timing accident; if that's wrong too, its error is the one worth reporting.
                await SetModelViaConfigOptionAsync(modelId, cancellationToken, DefaultModelConfigId)
                    .ConfigureAwait(false);
                _modelConfigId = DefaultModelConfigId; // proven to work; skip set_model from here on
            }
        }

        /// <summary>
        /// True when the agent answered "this method doesn't exist". StreamJsonRpc raises its dedicated
        /// <see cref="RemoteMethodNotFoundException"/> for -32601, and that does NOT derive from
        /// <see cref="RemoteInvocationException"/> — catching the latter alone silently misses every
        /// one. The code is still checked for backends whose error lands as a plain invocation failure.
        /// </summary>
        private static bool IsMethodNotFound(RemoteRpcException ex) =>
            ex is RemoteMethodNotFoundException ||
            (ex is RemoteInvocationException invocation &&
             invocation.ErrorCode == (int)StreamJsonRpc.Protocol.JsonRpcErrorCode.MethodNotFound);

        /// <summary>Switches the model via ACP session config options (configId "model"); refreshes the
        /// discovered list/current id from the echoed configOptions in the response.</summary>
        private async Task SetModelViaConfigOptionAsync(
            string modelId, CancellationToken cancellationToken, string? configId = null)
        {
            // Request the raw JsonElement rather than a typed record: StreamJsonRpc's SystemTextJson
            // formatter hands the already-parsed element back directly, avoiding a re-deserialize of the
            // stored result from an over-length (NUL-padded) span that trips '0x00 after a value'.
            var result = await _rpc!.InvokeWithParameterObjectAsync<JsonElement>(
                "session/set_config_option",
                new SetConfigOptionParams(_sessionId, configId ?? _modelConfigId!, modelId),
                cancellationToken).ConfigureAwait(false);

            // The call succeeding is the switch confirmation; the response echoes the full updated set,
            // which we re-parse to keep the current id (and any model-list change) in sync. Fall back to
            // the value we set if the echo is missing/unparseable.
            _currentModelId = modelId;
            if (result.ValueKind == JsonValueKind.Object &&
                result.TryGetProperty("configOptions", out var configOptions))
            {
                ParseModelConfigOption(configOptions);
            }
        }

        /// <summary>
        /// Extracts the model selector from a session's ACP config options: the entry with category
        /// "model" (or id "model") carries a select whose <c>currentValue</c> is the active model and
        /// whose options are the available models. Populates <see cref="DiscoveredModels"/>,
        /// <see cref="CurrentModelId"/>, and <c>_modelConfigId</c> (whose presence routes SetModel).
        /// Best-effort: any shape mismatch simply leaves the session on set_model / no discovery.
        /// </summary>
        private void ParseModelConfigOption(JsonElement? configOptions)
        {
            if (configOptions is not { ValueKind: JsonValueKind.Array } options)
                return;

            foreach (var option in options.EnumerateArray())
            {
                if (option.ValueKind != JsonValueKind.Object)
                    continue;

                var isModel =
                    (option.TryGetProperty("category", out var cat) && cat.ValueKind == JsonValueKind.String &&
                     cat.GetString() == "model") ||
                    (option.TryGetProperty("id", out var idProbe) && idProbe.ValueKind == JsonValueKind.String &&
                     idProbe.GetString() == "model");
                if (!isModel)
                    continue;

                _modelConfigId = option.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString()
                    : DefaultModelConfigId;
                DiscoveredModelSelection = true;

                if (option.TryGetProperty("currentValue", out var current) && current.ValueKind == JsonValueKind.String)
                    _currentModelId = current.GetString();

                if (option.TryGetProperty("options", out var values))
                {
                    var models = new List<ModelInfo>();
                    CollectModelOptions(values, models);
                    if (models.Count > 0)
                        _discoveredModels = models;
                }

                return; // only one model selector expected
            }
        }

        /// <summary>Flattens an ACP select's options (a flat option array, or groups each carrying their
        /// own options) into ModelInfos. Each leaf option is { value, name }.</summary>
        private static void CollectModelOptions(JsonElement values, List<ModelInfo> models)
        {
            if (values.ValueKind != JsonValueKind.Array)
                return;

            foreach (var entry in values.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;

                // A group nests its own "options"; a leaf carries a "value".
                if (entry.TryGetProperty("options", out var nested) && nested.ValueKind == JsonValueKind.Array)
                {
                    CollectModelOptions(nested, models);
                    continue;
                }

                if (entry.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var modelId = value.GetString()!;
                    var name = entry.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                        ? n.GetString()!
                        : modelId;
                    models.Add(new ModelInfo(modelId, name));
                }
            }
        }

        /// <summary>
        /// ACP extension request for steering. Not in the base spec — an agent opts in by advertising
        /// <c>_meta.steering.supported</c>, which is what <see cref="DiscoveredSteering"/> reads.
        /// </summary>
        private const string SteerMethod = "_session/steering";

        public async Task<SteerOutcome> SteerAsync(PromptInput prompt, CancellationToken cancellationToken = default)
        {
            if (_rpc is null)
                throw new InvalidOperationException("Session has not been initialized.");

            if (DiscoveredSteering != true)
                return SteerOutcome.NotSupported;

            // Attachments ride a steer too. The comment on SteerParams explains why the workspace-context
            // block does not — it is a per-turn snapshot the turn already paid for — but an attachment is
            // the user's own content, and the message it belongs to is this one.
            var steerBlocks = new List<ContentBlock>();
            AddAttachments(steerBlocks, prompt);
            steerBlocks.Add(new ContentBlock("text", prompt.Text));
            var promptParams = new SteerParams(_sessionId, steerBlocks.ToArray());

            // Raw JsonElement, not a typed record: StreamJsonRpc's STJ formatter deserializes typed
            // results out of a rented buffer larger than the message and reads on into the NUL padding
            // ('0x00' is invalid after a value). Asking for the already-parsed document sidesteps it.
            var response = await _rpc
                .InvokeWithParameterObjectAsync<JsonElement>(SteerMethod, promptParams, cancellationToken)
                .ConfigureAwait(false);

            return ParseSteerOutcome(response);
        }

        /// <summary>
        /// Maps the steer response's <c>outcome</c>. An unrecognised or missing value is read as
        /// <see cref="SteerOutcome.Injected"/> rather than as a failure: the protocol's contract is that
        /// the message is never dropped, so the one thing we know on any successful response is that the
        /// agent has it. Guessing "injected" costs at worst a slightly wrong UI hint; treating it as an
        /// error would tell the user their message was lost when it wasn't.
        /// </summary>
        private static SteerOutcome ParseSteerOutcome(JsonElement response) =>
            response.ValueKind == JsonValueKind.Object
            && response.TryGetProperty("outcome", out var outcome)
            && outcome.ValueKind == JsonValueKind.String
            && string.Equals(outcome.GetString(), "startedNewTurn", StringComparison.OrdinalIgnoreCase)
                ? SteerOutcome.StartedNewTurn
                : SteerOutcome.Injected;

        public async Task CancelAsync()
        {
            if (_rpc is null)
                return;
            try
            {
                await _rpc.NotifyWithParameterObjectAsync("session/cancel", new CancelParams(_sessionId)).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort: the turn may already have ended.
            }
        }

        public ValueTask DisposeAsync()
        {
            try { _rpc?.Dispose(); } catch { /* ignore */ }
            try { _connection.Dispose(); } catch { /* ignore */ }
            return default;
        }

        /// <summary>
        /// A frame the backend marked as replayed history (Kiro v3's <c>_meta.kiro.replay</c>). It has
        /// already been dropped by the time this runs — the only decision left is whether it says
        /// anything about US, and that turns on state only the session has.
        /// <para>Inside a load, this is the expected shape of a resume and there is nothing to say.
        /// <b>Outside one, our load window was wrong</b>, and that is worth a line: it is precisely
        /// issue #33's residual failure mode — a frame arriving after `_loadingHistory` cleared, which
        /// without the mark is indistinguishable from live work and gets appended behind the user's new
        /// message. The window plus the drain has held since, so this line is expected never to appear;
        /// if it does, it names the frame kind rather than leaving the next person to infer a race from
        /// a duplicated transcript.</para>
        /// </summary>
        private void OnReplayedUpdate(JsonElement update)
        {
            if (_loadingHistory)
                return;

            var kind = update.ValueKind == JsonValueKind.Object &&
                       update.TryGetProperty("sessionUpdate", out var k) &&
                       k.ValueKind == JsonValueKind.String
                ? k.GetString()
                : "(unknown)";

            System.Console.Error.WriteLine(
                $"[acp] replayed frame '{kind}' arrived with no session/load in flight — dropped " +
                "(the load window closed too early; see issue #33)");
        }

        /// <summary>
        /// Keeps one entry of a conversation being imported from the backend's own history (issue
        /// #108). Called from the inbound dispatch FIFO, so ordering is the wire's.
        /// <para>Nothing here goes near <see cref="EmitToCurrentTurn"/>, and that is the point: an
        /// import runs inside the load window, where that method drops everything anyway, and its one
        /// documented exception is not widened by anything on this path. The transcript this builds is
        /// handed over whole once the session is open, not streamed as work happening now.</para>
        /// </summary>
        private void RecordImportedEntry(ImportedTurnEntry entry)
        {
            if (_importedHistory.Count >= MaxImportedEntries)
            {
                if (!_importTruncated)
                {
                    _importTruncated = true;
                    System.Console.Error.WriteLine(
                        $"[acp] imported history stopped at {MaxImportedEntries} entries; the replay has more.");
                }

                return;
            }

            _importedHistory.Add(entry);
        }

        private void EmitToCurrentTurn(AgentEvent ev)
        {
            // A session/load in flight is replaying the conversation's HISTORY, not reporting work. It
            // arrives with no turn open, so before the turn-less routing below became unconditional (for
            // steering) it was dropped — accidentally correctly. Emitting it appends the entire
            // conversation to the transcript BEHIND the user's new message and re-records every event,
            // which tripled a resumed session's log. The transcript is already rebuilt from our own log
            // by then, so this is duplication with no upside.
            //
            // Scoped to the load itself, so it cannot mask live work: permission requests are JSON-RPC
            // requests on a separate path and are unaffected either way.
            if (_loadingHistory)
                return;

            if (_currentTurn?.Writer.TryWrite(ev) == true)
                return;

            // No turn is streaming — but "no turn of ours is open" does NOT mean "nothing legitimate can
            // arrive", so everything goes to the host rather than being dropped. Three ways this happens,
            // and only the first was ever anticipated:
            //
            //  - session status before the first prompt (the backend's MCP servers connecting right
            //    after session/new);
            //  - a steer, which is not the "inject into the running turn" its name suggests: measured
            //    live, the pre-empted turn CLOSES ~10ms after the steer is acknowledged and the steered
            //    work then runs with no turn open at all — tool calls, diffs and the reply included;
            //  - a backend that answers session/prompt before it has finished (kirodotdev/Kiro#7724).
            //
            // Dropping here is not graceful degradation. Permission requests are JSON-RPC requests on a
            // separate path, so they arrive whatever we do with events: silence here means the user is
            // asked to approve a file write whose tool row, diff and edit card were all discarded — a
            // decision made blind, with the EditProposed that fills the banner's "View diff" among the
            // events thrown away. Proven both ways by Console claude-steer-tools, whose whole timeline
            // after the turn closes collapses to a single naked permission prompt when this is narrowed.
            //
            // Cost of being unconditional: an event the host has no turn context for. That is the same
            // situation the host is already in for McpServerConnected, and it handles them identically
            // to turn output — so there is nothing here to be lost, only something to be shown late.
            _sessionOptions.OutOfTurnEvents?.Invoke(ev);
        }

        /// <summary>
        /// A one-shot prompt block listing the IDE-integrated tools this session exposes over MCP
        /// (the host's <see cref="Core.Ide.IToolCatalog"/>), asking the agent to prefer them over
        /// shell equivalents. Null when no MCP server is wired or the catalog is empty. Best-effort.
        /// </summary>
        private string? BuildIdeToolsHint()
        {
            if (_sessionOptions.McpServers.Count == 0)
                return null;

            try
            {
                var tools = _ide.Tools.Tools;
                if (tools.Count == 0)
                    return null;

                var sb = new System.Text.StringBuilder();
                sb.AppendLine(HostPromptBlocks.IdeTools.Open);
                sb.AppendLine(
                    "You are running inside an IDE that exposes integrated tools over MCP. Prefer " +
                    "these over shell equivalents: they act on the IDE's live state (the open " +
                    "solution, its build configuration, the Error List) and keep your results " +
                    "consistent with what the user sees.");
                if (tools.Any(t => t.Name == "find_symbol"))
                    sb.AppendLine(
                        "In particular: before you grep, glob or shell-search to locate a C#/VB " +
                        "type or member by name — including when a file read or path guess just " +
                        "failed — call find_symbol instead; it resolves the declaration " +
                        "semantically (even in NuGet packages and the BCL, which text search " +
                        "cannot reach). Use find_references for usages and find_implementations " +
                        "for the type hierarchy.");
                if (tools.Any(t => t.Name is "build_solution" or "run_tests" or "get_diagnostics"))
                    sb.AppendLine(
                        "For building, testing, and diagnostics, use build_solution, run_tests " +
                        "and get_diagnostics — do not shell out to 'dotnet build', 'dotnet test' " +
                        "or 'msbuild' yourself. They drive the IDE's live build configuration and " +
                        "match Test Explorer, so their results agree with what the user sees; a " +
                        "shell run uses a separate configuration and can disagree.");
                foreach (var tool in tools)
                    sb.AppendLine($"- {tool.Name}: {Summarize(tool.Description)}");
                sb.Append(HostPromptBlocks.IdeTools.Close);
                return sb.ToString();
            }
            catch
            {
                // The hint is advisory; never fail a prompt over it.
                return null;
            }
        }

        /// <summary>
        /// First sentence of a tool description, capped at a word boundary — the full text lives in
        /// the MCP schema, so this only needs to be enough to pick the right tool.
        /// </summary>
        /// <remarks>
        /// A sentence ends at ". " before a CAPITAL. Any ". " was taken as the end, so "e.g. add a missing
        /// using" handed the agent "… using the IDE's own code-fix providers — e.g." as the whole of
        /// apply_code_fix.
        /// </remarks>
        internal static string Summarize(string description)
        {
            var text = description.Replace("\r", " ").Replace("\n", " ").Trim();
            var end = FirstSentenceEnd(text);
            if (end > 0)
                text = text.Substring(0, end + 1);
            if (text.Length <= MaxSummaryLength)
                return text;

            var cut = text.LastIndexOf(' ', MaxSummaryLength);
            return text.Substring(0, cut > 0 ? cut : MaxSummaryLength) + "…";
        }

        internal const int MaxSummaryLength = 200;

        private static int FirstSentenceEnd(string text)
        {
            for (var i = text.IndexOf(". ", StringComparison.Ordinal); i > 0; i = text.IndexOf(". ", i + 1, StringComparison.Ordinal))
            {
                if (i + 2 < text.Length && char.IsUpper(text[i + 2]))
                    return i;
            }
            return -1;
        }

        /// <summary>Maps the host's provider-agnostic MCP server specs onto the ACP stdio shape.</summary>
        private McpServer[] BuildMcpServers() =>
            _sessionOptions.McpServers
                .Select(s => new McpServer(
                    s.Name,
                    s.Command,
                    s.Args.ToArray(),
                    (s.Env ?? new Dictionary<string, string>())
                        .Select(kv => new EnvVariable(kv.Key, kv.Value))
                        .ToArray()))
                .ToArray();

        // The ACP session's cwd. Always the same directory the CLI process was started in — the
        // provider resolves it once (see WorkspaceRootLocator) precisely so these can't drift apart.
        private string ResolveCwd() => _workspaceRoot.Root;
    }
}
