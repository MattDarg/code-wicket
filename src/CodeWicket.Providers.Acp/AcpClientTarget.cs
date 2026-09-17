using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StreamJsonRpc;
using CodeWicket.Core;
using CodeWicket.Core.Ide;

namespace CodeWicket.Providers.Acp
{
    /// <summary>
    /// Handles the agent -> client side of ACP: streamed session updates, permission requests,
    /// and the client-owned filesystem. Update payloads are emitted as <see cref="AgentEvent"/>s;
    /// permission and filesystem calls are bridged to the host's <see cref="IIdeServices"/>.
    /// </summary>
    internal sealed class AcpClientTarget
    {
        private readonly IIdeServices _ide;
        private readonly Action<AgentEvent> _emit;

        // The primary session's id (a callback: the target is wired before session/new returns).
        // Kiro multiplexes sub-agent sessions over the SAME connection, each streaming session/update
        // under its own sessionId — without discriminating, parallel sub-agents' chunks concatenate
        // into the main transcript as interleaved garbage.
        private readonly Func<string?> _primarySessionId;

        // Session-scoped cache of file contents the agent has read/written, keyed by path. Lets
        // AcpMapper recover a real "before" when an agent reports a degenerate whole-file edit
        // (e.g. Kiro's "create" sends oldText == newText). Updates are dispatched serially by
        // StreamJsonRpc (sync void handler), so a plain dictionary is safe.
        private readonly Dictionary<string, string> _contentCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Writes WE performed on the agent's behalf (ACP fs/write_text_file), keyed by resolved absolute
        // path. The agent reports its own diff on the tool call afterwards, but that's its claim about the
        // change and for a surgical edit carries only the hunk; this is the transformation that actually
        // hit the file, so AcpMapper prefers it when building the EditProposed (see ApplyWriteCapture).
        // Entries are consumed on use — exactly one enrichable frame follows each write (the completed
        // tool_call_update), so popping keeps a capture from being re-applied to an unrelated later edit
        // of the same file, and a miss just falls back to the agent's text.
        //
        // "Exactly one" is the ordinary case, not a guarantee, and the exception is documented: a Kiro v3
        // write of empty content leaves the preview carrying only `file`, AcpMapper's guard rejects it,
        // and the call falls through to a generic tool row with no diff (the #178 shape). Nothing then
        // consumes the capture. Keyed on PATH ALONE with no expiry, that entry would wait — turns, even
        // conversations — for the next edit of the same file, and then replace BOTH sides of a diff
        // describing a change it has nothing to do with: the card, the "Open file" line and the diff the
        // user reviews would all be about the older write. So the capture is cleared at the turn
        // boundary (OnTurnEnded): a capture that survived its own turn is by definition unconsumed, and
        // dropping it costs only the enrichment, which is what a miss already degrades to.
        private readonly Dictionary<string, FileWriteResult> _writeCapture =
            new Dictionary<string, FileWriteResult>(StringComparer.OrdinalIgnoreCase);

        // Session-scoped cache of the full shell command per tool call, populated from tool_call frames'
        // rawInput.command. Lets a later request_permission recover the whole command when its own frame
        // carries none (Kiro sends no rawInput on the permission request, only a truncated command title).
        // toolCallIds are opaque + case-sensitive, so Ordinal. Serial dispatch → a plain dictionary is safe.
        private readonly Dictionary<string, string> _commandByToolCallId =
            new Dictionary<string, string>(StringComparer.Ordinal);

        // Session-scoped cache of each tool call's display-ready raw input, keyed by toolCallId — the
        // args side of the command cache above. Kiro's request_permission carries no rawInput, but v3's
        // preceding tool_call does (for MCP-forwarded calls too), so the banner can show the arguments.
        private readonly Dictionary<string, string> _rawInputByToolCallId =
            new Dictionary<string, string>(StringComparer.Ordinal);

        // Session-scoped map of a Kiro v3 sub-agent's subtask id to the invoke_subagent_* row that
        // started it, so the sub-agent's own tool calls can be nested under the row the user sees
        // instead of being dropped (issue #125). Claude needs no equivalent — it names the parent call
        // on every child frame.
        private readonly Dictionary<string, string> _subagentParents =
            new Dictionary<string, string>(StringComparer.Ordinal);

        // Tool calls known to have been launched in the BACKGROUND, so the "completed" frame that
        // follows can be read as the launch receipt it is rather than as work that finished. The fact
        // and the frame it applies to arrive separately, which is why this is remembered rather than
        // read off the completion.
        private readonly HashSet<string> _backgroundLaunches = new HashSet<string>(StringComparer.Ordinal);

        // Tool calls whose generic row was retired into the plan card on their OPENING frame, so a later
        // frame cannot re-decide that and leave a start with no completion (issue #190). Same shape and
        // the same reason as the set above: the decision and the frame it binds arrive separately.
        private readonly HashSet<string> _planToolCalls = new HashSet<string>(StringComparer.Ordinal);

        // MCP servers already announced as connected, so Kiro's re-sent server_initialized frames
        // (observed live: the same server twice around session/new) surface one notice each. Not
        // filtered by session id — the notifications start arriving BEFORE session/new returns, when
        // the primary session id isn't known yet. Serial dispatch → a plain set is safe.
        private readonly HashSet<string> _announcedMcpServers =
            new HashSet<string>(StringComparer.Ordinal);

        // Optional host-side auth callback for Kiro's v3 engine (see AcpAgentConfig.AuthTokenProvider).
        private readonly Func<CancellationToken, Task<JsonElement>>? _authTokenProvider;

        // Raised with the whole config_option_update frame: some backends (Kiro v3) publish the
        // session's config options — the model selector among them — as a notification AFTER
        // session/new rather than in its response. The session owns what to do with them.
        private readonly Action<JsonElement>? _onConfigOptions;

        // Raised with a current_mode_update frame: the backend's permission mode changed, whether we
        // asked (the #269 pin) or not (a plan-mode exit, or a mode a settings file switched under us).
        // Session state like the config options, and routed the same way.
        private readonly Action<JsonElement>? _onModeUpdate;
        private readonly Action? _onHistoryReplayed;

        // Raised with a session/update frame the backend has MARKED as replayed history (see
        // AcpMapper.IsReplayedUpdate). Nothing is emitted for it — the session owns what to make of
        // one, because whether it is expected depends on state only the session has (is a load in
        // flight?), and that judgement is not the client target's to make.
        private readonly Action<JsonElement>? _onReplayedUpdate;

        // Whether the frames arriving now are a conversation being IMPORTED — the replay of a
        // session/load the host asked us to keep, because there is no log of ours to rebuild it from
        // (issue #108). Asked per frame rather than latched, since only the session knows when its
        // load window opens and closes. Null means no import is ever active, which is every session
        // that did not ask for one.
        private readonly Func<bool>? _isImporting;

        // Whether a session/load is replaying the conversation at us right now - ANY load, not only one
        // we are importing. The client target cannot infer this: a replay is byte-identical to live work
        // on a backend that does not mark it, which is every backend except Kiro v3.
        private readonly Func<bool>? _isLoadingHistory;

        // Where an imported conversation goes. Entries, not events: a replay is history being
        // recorded, and nothing about it belongs in the turn stream (see ImportUpdate).
        private readonly Action<ImportedTurnEntry>? _onImportedEntry;

        // The import's OWN copies of the five caches AcpMapper.Map consumes. Separate objects, not a
        // discipline: an imported frame is a dead session's work, and the live caches exist to answer
        // a LIVE session/request_permission. One shared dictionary is all it would take for a replayed
        // `git push --force` to describe the permission banner for a command the agent is not about to
        // run — the same hazard the replay drop is placed before the live caches for (issue #126), and
        // the reason there is deliberately NO import counterpart to _commandByToolCallId or
        // _rawInputByToolCallId: those two feed permissions and nothing else, so an import has no
        // business filling them at all.
        private readonly Dictionary<string, string> _importContentCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, FileWriteResult> _importWriteCapture =
            new Dictionary<string, FileWriteResult>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _importSubagentParents =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> _importBackgroundLaunches = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _importPlanToolCalls = new HashSet<string>(StringComparer.Ordinal);

        // The ACP session's cwd — the origin the AGENT measures its relative paths from, which is not
        // necessarily the solution root (issue #54). Every path we key on is normalized against it, so
        // the relative spelling a model happens to use and the absolute file:// URI the same edit is
        // finalized with land on ONE key (see AcpMapper.NormalizeLocalPath).
        private readonly string? _agentRoot;

        public AcpClientTarget(
            IIdeServices ide,
            Action<AgentEvent> emit,
            Func<string?> primarySessionId,
            Func<CancellationToken, Task<JsonElement>>? authTokenProvider = null,
            Action<JsonElement>? onConfigOptions = null,
            Action<JsonElement>? onReplayedUpdate = null,
            string? agentRoot = null,
            Func<bool>? isImporting = null,
            Action<ImportedTurnEntry>? onImportedEntry = null,
            Func<bool>? isLoadingHistory = null,
            Action<JsonElement>? onModeUpdate = null,
            Action? onHistoryReplayed = null)
        {
            _onModeUpdate = onModeUpdate;
            _onHistoryReplayed = onHistoryReplayed;
            _ide = ide;
            _emit = emit;
            _primarySessionId = primarySessionId;
            _authTokenProvider = authTokenProvider;
            _onConfigOptions = onConfigOptions;
            _onReplayedUpdate = onReplayedUpdate;
            _agentRoot = agentRoot;
            _isImporting = isImporting;
            _isLoadingHistory = isLoadingHistory;
            _onImportedEntry = onImportedEntry;
        }

        [JsonRpcMethod("_kiro/auth/getAccessToken", UseSingleObjectParameterDeserialization = true)]
        public Task<JsonElement> OnGetAccessTokenAsync(JsonElement parameters = default)
        {
            // Kiro's v3 agent engine delegates auth refresh to its ACP host: it calls this before
            // every model interaction and expects {accessToken, expiresAt, profileArn?, ...} back.
            // Without a configured provider, answer method-not-found — exactly what an agent saw
            // before this method existed, so unconfigured backends are unaffected.
            if (_authTokenProvider is null)
                throw new LocalRpcException("no auth token provider configured for this agent")
                {
                    ErrorCode = (int)StreamJsonRpc.Protocol.JsonRpcErrorCode.MethodNotFound,
                };

            return _authTokenProvider(CancellationToken.None);
        }

        // Takes the raw element, not the typed record: binding the record makes the formatter
        // re-deserialize from a NUL-padded span and throw on frames that don't fill it exactly, and a
        // notification swallows that — the frame just disappears. See SessionUpdateParams.
        [JsonRpcMethod("session/update", UseSingleObjectParameterDeserialization = true)]
        public void OnSessionUpdate(JsonElement rawParameters)
        {
            OnSessionUpdate(SessionUpdateParams.From(rawParameters));
        }

        internal void OnSessionUpdate(SessionUpdateParams parameters)
        {
            // A sub-agent session's stream never renders into the main transcript: surface only its
            // final result (Kiro's wrap-up call carries it as rawInput.taskResult) and drop the rest —
            // the roster card (OnSubagentListUpdate) is where its liveness shows. Only requests need
            // answering for a sub-agent to make progress (permissions/fs below stay unfiltered);
            // updates are notifications, so dropping them blocks nothing.
            var primary = _primarySessionId();
            if (!string.IsNullOrEmpty(primary) &&
                !string.Equals(parameters.SessionId, primary, StringComparison.Ordinal))
            {
                if (AcpMapper.ExtractSubagentResult(parameters.SessionId, parameters.Update) is { } result)
                    _emit(result);
                return;
            }

            // Session config options (mode / model / backend-specific selects). Nothing in the
            // transcript consumes these — they're session state, so they go straight to the session
            // and no further (AcpMapper has no case for the kind either).
            //
            // Ahead of the replay and import checks below, because this is CURRENT session state and
            // neither of those is a home for it: Kiro v3 doesn't mark it as replayed (measured — the
            // mark is on the frames a transcript would render), so today the ordering changes nothing,
            // and that is exactly why it is worth pinning. An engine that started marking it, or a
            // conversation opened for import, would otherwise route the model selector into a
            // transcript and the session would never learn its own model.
            if (parameters.Update.ValueKind == JsonValueKind.Object &&
                parameters.Update.TryGetProperty("sessionUpdate", out var updateKind) &&
                updateKind.ValueKind == JsonValueKind.String &&
                updateKind.GetString() == "config_option_update")
            {
                _onConfigOptions?.Invoke(parameters.Update);
                return;
            }

            if (parameters.Update.ValueKind == JsonValueKind.Object &&
                parameters.Update.TryGetProperty("sessionUpdate", out var modeKind) &&
                modeKind.ValueKind == JsonValueKind.String &&
                modeKind.GetString() == "current_mode_update")
            {
                _onModeUpdate?.Invoke(parameters.Update);
                return;
            }

            // Counted ahead of both load-window disposals below, whichever one applies, so a resume
            // the backend answered with nothing is told apart from one it answered in full (issue
            // #185). Only conversation frames count: an empty load still announces its commands.
            if (_isLoadingHistory?.Invoke() == true && AcpMapper.IsConversationContent(parameters.Update))
                _onHistoryReplayed?.Invoke();

            // A conversation being IMPORTED: the replay of a session/load whose history is the only
            // account there is, because it was started outside the IDE (issue #108). Keyed on the MODE
            // rather than on the frame, so Kiro's marked frames and Claude's unmarked ones — measured,
            // a 413-frame replay with no marker of any kind — take one path instead of two.
            if (_isImporting?.Invoke() == true)
            {
                ImportUpdate(parameters.Update);
                return;
            }

            // A load we are NOT importing: the conversation is being replayed only because the backend
            // always replays it, and the host already rebuilt this transcript from its own log. The
            // events are discarded downstream by EmitToCurrentTurn's load window either way, so nothing
            // is lost by stopping here - and what stopping here BUYS is that the caches below never see
            // them.
            //
            // That mattered more than it looked. The marked-frame drop underneath is the per-frame
            // defence, and it only fires on a backend that marks: Kiro v3 does, Claude sends no mark at
            // all (measured - 413 replayed frames, not one marked). So on Claude every ordinary resume
            // primed _commandByToolCallId and _rawInputByToolCallId with the commands of a conversation
            // that had ended, and the permission banner reads exactly those. Nothing surfaced it,
            // because the transcript half was already being dropped correctly one layer down.
            //
            // Keyed on the load window rather than on the frame for the same reason the import gate is:
            // a fact about what the connection is doing beats a mark only one vendor sends.
            if (_isLoadingHistory?.Invoke() == true)
                return;

            // History, not work: a frame the backend itself marked as part of a session/load replay.
            // Dropped here rather than downstream so it reaches NOTHING — not the transcript, and not
            // the command/rawInput caches below either, which exist to answer a LIVE permission
            // request and would otherwise be primed from a tool call that ran in a previous session.
            // The load window (AcpAgentSession._loadingHistory) still does its own job; this is the
            // per-frame fact, which is what covers a replay frame arriving after that window closes.
            if (AcpMapper.IsReplayedUpdate(parameters.Update))
            {
                _onReplayedUpdate?.Invoke(parameters.Update);
                return;
            }

            // Edits arrive as standard ACP diff content on tool calls (agents write files themselves
            // rather than calling the client fs/write); AcpMapper turns those into EditProposed events
            // carrying before/after for a host-rendered diff. The content cache lets it repair a
            // degenerate "create" diff into a real one. See AcpMapper.ExtractDiffEdits.
            // Remember the full command for this tool call so a following request_permission can show it
            // even when Kiro's permission frame omits it (see AcpMapper.CacheToolCommand).
            AcpMapper.CacheToolCommand(parameters.Update, _commandByToolCallId);
            AcpMapper.CacheToolRawInput(parameters.Update, _rawInputByToolCallId);
            AcpMapper.CacheSubagentParent(parameters.Update, _subagentParents);
            AcpMapper.CacheBackgroundLaunch(parameters.Update, _backgroundLaunches);
            AcpMapper.CachePlanToolCall(parameters.Update, _planToolCalls);

            // Kiro v3 runs sub-agents in THIS (primary) session rather than multiplexing them into
            // separate sessions like v2, tagging each sub-agent frame with _meta.kiro.agentSubtaskId.
            // Its TOOL CALLS now nest under the invoke_subagent row that started them (issue #125) —
            // the same shape Claude's parentToolUseId gets — so only the sub-agent's PROSE is still
            // dropped. That half of the old rule stands on its own evidence: a sub-agent's streamed
            // summary both duplicates its row's deliverable and concatenates into the main agent's
            // message, which is the interleaving that made parallel crews unreadable. The caches above
            // already ran, so a sub-agent's own permission request can still resolve its command/args.
            if (AcpMapper.IsSubagentInternalUpdate(parameters.Update) &&
                !AcpMapper.IsToolCallFrame(parameters.Update))
                return;

            foreach (var ev in AcpMapper.Map(
                parameters.Update, _contentCache, _writeCapture, _agentRoot, _subagentParents,
                _backgroundLaunches, _planToolCalls))
                _emit(ev);
        }

        /// <summary>
        /// Retires the per-turn state a finished turn can no longer explain — today just the write
        /// capture, whose staleness rule is on its declaration.
        /// <para>
        /// Called by <c>AcpAgentSession</c> from the prompt task's <c>finally</c>, POSTED through the
        /// dispatch context rather than run on that thread: <see cref="_writeCapture"/> is a plain
        /// dictionary that is safe only because StreamJsonRpc dispatches updates serially, and clearing
        /// it from the turn's own thread would be the one write that isn't. It runs after the turn's
        /// drain (#33), so every frame entitled to consume a capture has already had it.
        /// </para>
        /// </summary>
        internal void OnTurnEnded() => _writeCapture.Clear();

        /// <summary>
        /// Turns one frame of an imported replay into transcript entries. Nothing here reaches
        /// <c>_emit</c>: an import is history being RECORDED, not work being reported, and a turn-less
        /// event routed to the host would arrive as live agent activity mid-load.
        /// <para>The mapping is otherwise the live one, deliberately — the same
        /// <see cref="AcpMapper.Map"/> against the same shapes, so an imported tool row, diff or plan
        /// is the row a live turn would have produced and the host needs no second render path. Only
        /// two things differ: the caches are the import's own (see their declaration), and a user
        /// message becomes an entry of its own, since <see cref="AcpMapper.Map"/> has no case for one
        /// and must never grow one.</para>
        /// </summary>
        private void ImportUpdate(JsonElement update)
        {
            if (_onImportedEntry is null)
                return;

            if (AcpMapper.TryReadUserMessageText(update, out var userText))
            {
                _onImportedEntry(ImportedTurnEntry.User(userText));
                return;
            }

            // The parentage caches Map reads from, primed exactly as the live path primes them — a
            // sub-agent's calls nest in an imported transcript for the same reason they nest in a live
            // one (issue #125), and a background launch that is not remembered reads as work that
            // finished. Neither touches the permission caches.
            AcpMapper.CacheSubagentParent(update, _importSubagentParents);
            AcpMapper.CacheBackgroundLaunch(update, _importBackgroundLaunches);
            AcpMapper.CachePlanToolCall(update, _importPlanToolCalls);

            // A Kiro v3 sub-agent's own PROSE stays dropped here as it is live: it duplicates the row's
            // deliverable and jams into the main agent's message. Its tool calls are kept and nested.
            if (AcpMapper.IsSubagentInternalUpdate(update) && !AcpMapper.IsToolCallFrame(update))
                return;

            foreach (var ev in AcpMapper.Map(
                update, _importContentCache, _importWriteCapture, _agentRoot,
                _importSubagentParents, _importBackgroundLaunches, _importPlanToolCalls))
            {
                _onImportedEntry(ImportedTurnEntry.Agent(ev));
            }
        }

        [JsonRpcMethod("_kiro.dev/subagent/list_update", UseSingleObjectParameterDeserialization = true)]
        public void OnSubagentListUpdate(JsonElement parameters)
        {
            // Kiro's sub-agent roster extension: the current crew on every change. An unusable/empty
            // frame maps to null (Kiro sends an empty roster when the crew is disposed; the card keeps
            // its final states rather than being wiped).
            if (AcpMapper.MapSubagentList(parameters) is { } roster)
                _emit(roster);
        }

        [JsonRpcMethod("_kiro.dev/metadata", UseSingleObjectParameterDeserialization = true)]
        public void OnMetadata(JsonElement parameters)
        {
            // Newer kiro-cli builds report context fill here rather than on a session_info_update frame
            // (AcpMapper.TryMapKiroMetadata). Filter to the primary session — a sub-agent's metadata
            // must not drive the main context indicator (v2 multiplexes sub-agent sessions onto this
            // same connection). An unknown primary id yet (frames can precede session/new returning)
            // means it IS the primary, so don't drop it.
            var primary = _primarySessionId();
            if (!string.IsNullOrEmpty(primary) &&
                parameters.ValueKind == JsonValueKind.Object &&
                parameters.TryGetProperty("sessionId", out var sid) &&
                sid.ValueKind == JsonValueKind.String &&
                !string.Equals(sid.GetString(), primary, StringComparison.Ordinal))
            {
                return;
            }

            if (AcpMapper.TryMapKiroMetadata(parameters, out var usage))
                _emit(new AgentEvent.UsageUpdated(usage));
        }

        [JsonRpcMethod("_kiro.dev/mcp/server_initialized", UseSingleObjectParameterDeserialization = true)]
        public void OnMcpServerInitialized(JsonElement parameters)
        {
            // Kiro's own MCP servers connect asynchronously after session/new (a slow one lands mid- or
            // post-turn, having missed the first prompt's tool snapshot). Surface each server once so
            // the transcript shows which prompts its tools could have served.
            if (AcpMapper.MapMcpServerInitialized(parameters) is { } serverName &&
                _announcedMcpServers.Add(serverName))
            {
                _emit(new AgentEvent.McpServerConnected(serverName));
            }

            // The roster is emitted OUTSIDE the dedupe guard above, deliberately. That set is a NOTICE
            // concept — one line per server per session — whereas the roster must reflect the wire, and
            // the host folds an incomplete roster by upserting on name, which is idempotent. Gating this
            // on the guard would mean a re-sent frame silently stopped updating the panel.
            if (AcpMapper.MapInitializedServerRoster(parameters) is { } roster)
                _emit(new AgentEvent.McpRosterUpdated(roster));
        }

        /// <summary>
        /// Kiro speaking to the user directly (issue #208). Had no handler at all, and a notification
        /// with no handler is dropped without an error anywhere - so the symptom was pure absence: a
        /// pane stalled for twenty minutes while the backend's explanation sat unread one layer below
        /// the transcript.
        /// </summary>
        [JsonRpcMethod("_kiro/system/notify", UseSingleObjectParameterDeserialization = true)]
        public void OnSystemNotify(JsonElement parameters)
        {
            if (AcpMapper.MapSystemNotify(parameters) is { } notice)
                _emit(notice);
        }

        [JsonRpcMethod("_kiro/mcp/status", UseSingleObjectParameterDeserialization = true)]
        public void OnMcpStatus(JsonElement parameters)
        {
            // The same fact as server_initialized above, reported the way Kiro's v3 engine reports it
            // (issue #97): a snapshot of the whole roster with each server's state, re-sent as states
            // change, and no per-server "initialized" notification anywhere. v3 renamed its extension
            // namespace too (`_kiro/…`, not `_kiro.dev/…`), so this is a second method rather than a
            // widened one — and both engines stay live, since which one is running is the user's setting.
            // Dedupe is shared with v2 on purpose: one notice per server per session either way.
            foreach (var serverName in AcpMapper.MapConnectedMcpServers(parameters))
            {
                if (_announcedMcpServers.Add(serverName))
                    _emit(new AgentEvent.McpServerConnected(serverName));
            }

            // Notices first, roster after: the notice's value is its POSITION in the transcript (issue
            // #19), so its ordering relative to everything else must not move, while the roster is
            // chrome and order-free. Emitted on every frame — a snapshot that repeats is the whole
            // point of this notification, and the host replaces its list wholesale from it.
            if (AcpMapper.MapMcpRoster(parameters) is { } roster)
                _emit(new AgentEvent.McpRosterUpdated(roster));
        }

        [JsonRpcMethod("session/request_permission", UseSingleObjectParameterDeserialization = true)]
        public async Task<RequestPermissionResult> OnRequestPermissionAsync(RequestPermissionParams parameters)
        {
            // A request on a session other than the primary is a v2 sub-agent's (v2 multiplexes
            // them; requests are deliberately never filtered by session, since the sub-agent needs
            // the answer). The banner says so - it is the only attribution v2 offers.
            var request = AcpMapper.ToPermissionRequest(
                parameters, _commandByToolCallId, _rawInputByToolCallId, _agentRoot,
                AcpMapper.IsSubagentSessionRequest(parameters.SessionId, _primarySessionId()));
            var decision = await _ide.Permissions.RequestAsync(request).ConfigureAwait(false);

            if (decision.Cancelled)
                return new RequestPermissionResult(new PermissionOutcome { Outcome = "cancelled" });

            return new RequestPermissionResult(new PermissionOutcome { Outcome = "selected", OptionId = decision.OptionId });
        }

        [JsonRpcMethod("fs/read_text_file", UseSingleObjectParameterDeserialization = true)]
        public async Task<ReadTextFileResult> OnReadTextFileAsync(ReadTextFileParams parameters)
        {
            var content = await _ide.Edits
                .ReadTextFileAsync(parameters.Path, parameters.Line, parameters.Limit)
                .ConfigureAwait(false);
            return new ReadTextFileResult(content);
        }

        [JsonRpcMethod("fs/write_text_file", UseSingleObjectParameterDeserialization = true)]
        public async Task<WriteTextFileResult> OnWriteTextFileAsync(WriteTextFileParams parameters)
        {
            var result = await _ide.Edits
                .WriteTextFileAsync(parameters.Path, parameters.Content)
                .ConfigureAwait(false);

            // Key on the applier's RESOLVED path, normalized exactly as the mapper normalizes the agent's:
            // the incoming path may be relative (Kiro v3 sends rawInput.path as "hello.txt") while the
            // reported diff's is an absolute file:// URI, and a key mismatch would silently disable the
            // enrichment. Cheap and correctness-critical, so it's done here rather than at lookup.
            // The applier resolves a relative path with Path.Combine, which leaves the agent's POSIX
            // separators in place ("C:\ws\dotnet/proj/File.cs") — canonicalizing here is what makes that
            // meet the backslashed spelling the finalized diff arrives in.
            if (result is not null)
            {
                var key = AcpMapper.NormalizeLocalPath(result.ResolvedPath, _agentRoot);
                _writeCapture[key] = result;

                // Half of the "is the capture ever needed?" evidence — see AcpMapper.LogCaptureOutcome for
                // the other half and why both are logged. A `stored` with no following `applied` for the
                // same path is the signal that matters most: the capture went unused, so either the two
                // path forms failed to meet or no post-write frame carried an edit at all.
                Console.Error.WriteLine(
                    $"[edit-capture] stored path={key} oursLen={result.OldText.Length}/{result.NewText.Length}");
            }

            return new WriteTextFileResult();
        }
    }
}
