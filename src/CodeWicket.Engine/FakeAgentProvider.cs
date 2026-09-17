using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CodeWicket.Core;
using CodeWicket.Core.Ide;

namespace CodeWicket.Engine
{
    /// <summary>
    /// A provider with no real backend, used to exercise the engine↔shell boundary: its session
    /// calls every <see cref="IIdeServices"/> facet (context, permission, edit) so the IPC
    /// marshaling can be verified without Kiro.
    /// </summary>
    public sealed class FakeAgentProvider : IAgentProvider, IBackendSessionCatalog
    {
        public string ProviderId => "fake";

        public string DisplayName => "Fake (in-process)";

        // Two rated models so the rate-multiplier rendering and the live model-switch path are both
        // exercisable offline (Desktop/smoke) without a real backend.
        public IReadOnlyList<ModelInfo> Models { get; } = new[]
        {
            new ModelInfo("fake-fast", "Fake fast") { RateMultiplier = 0.4 },
            new ModelInfo("fake-smart", "Fake smart") { RateMultiplier = 1.3 },
        };

        public AgentCapabilities Capabilities =>
            AgentCapabilities.ToolCalls | AgentCapabilities.ClientFileSystem
            | AgentCapabilities.Cancellation | AgentCapabilities.ResumeSession
            | AgentCapabilities.ModelSelection | AgentCapabilities.Steering;

        /// <summary>
        /// A canned CLI store, so the whole <c>engine/listBackendSessions</c> round trip - request DTO,
        /// provider resolution, mapping, response DTO - is exercisable with no backend installed
        /// (issue #108). The fake is the only provider that can answer this offline, which is what makes
        /// the engine hop testable rather than only the ACP half.
        /// <para>The first two entries differ in every field the host might order or group by: distinct
        /// ids, distinct titles, and times an hour apart with the NEWER one first, so a mapping that
        /// dropped or transposed <see cref="BackendSessionInfo.UpdatedAt"/> shows up as a visibly wrong
        /// order rather than as two rows that look alike.</para>
        /// <para>The THIRD deliberately repeats the first's title, because the backends this stands in
        /// for produce that routinely and a fake that cannot reproduce it makes the host's answer
        /// unreachable offline. Claude auto-titles from the opening exchange, so two conversations begun
        /// with the same first message are given the same name - measured 2026-08-26 on the real store,
        /// two live sessions both called "Issue #108 plan", 581 and 2485 records, both resumable. Rows
        /// alike in every visible field cannot be told apart at all, so the host marks the collision
        /// with a short id; without an entry like this, nothing offline would ever draw one.</para>
        /// <para>Times are <see cref="DateTimeOffset.UtcNow"/>-relative rather than fixed constants: a
        /// hardcoded date drifts into the distant past and a picker sorting "recent first" would then
        /// look correct while sorting nothing.</para>
        /// </summary>
        public Task<BackendSessionListResult> ListBackendSessionsAsync(
            string workspaceRootPath, AgentWorkspaceScope scope, IIdeServices ide,
            CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            var sessions = new[]
            {
                new BackendSessionInfo(
                    "fake-session-recent", "Wire up the settings page", now.AddMinutes(-12), workspaceRootPath),
                new BackendSessionInfo(
                    "fake-session-older", "Investigate the failing build", now.AddHours(-1).AddMinutes(-12), workspaceRootPath),
                new BackendSessionInfo(
                    "fake-session-twin", "Wire up the settings page", now.AddHours(-2), workspaceRootPath),

                // Created and never used - what a chat window opened and not prompted leaves behind in
                // the backend's own store (#19's warm start). Both signals the filter needs are here
                // and they must AGREE: the backend's own "not named yet" title, and a createdAt within
                // a breath of the updatedAt. Modelled on Kiro v3's wire, measured 2026-09-03 - "New
                // Session", 16 ms apart.
                new BackendSessionInfo(
                    "fake-session-unused", "New Session", now.AddMinutes(-3), workspaceRootPath)
                {
                    CreatedAt = now.AddMinutes(-3).AddMilliseconds(-16),
                },

                // The control, and it is the half that makes the pair mean anything: a REAL
                // conversation whose two timestamps are also close together. Only the title separates
                // it from the row above, so a filter that dropped the title check and kept the clock
                // would hide this one - and nothing about a shorter list would look wrong.
                new BackendSessionInfo(
                    "fake-session-brief", "Quick question about the build", now.AddMinutes(-4), workspaceRootPath)
                {
                    CreatedAt = now.AddMinutes(-4).AddMilliseconds(-40),
                },
            };

            return Task.FromResult(BackendSessionListResult.Found(sessions));
        }

        /// <inheritdoc/>
        // In-process, so there is no CLI to root anywhere and no upward walk to do: the listing runs
        // wherever it was asked from. Reported anyway rather than left unimplemented, so the engine's
        // warm-path root check is exercised offline on the one provider that can answer a listing
        // without a backend installed.
        public WorkspaceRootResult ResolveListingRoot(string workspaceRootPath, AgentWorkspaceScope scope) =>
            new(workspaceRootPath, false, null, "in-process backend: the workspace root is used as-is");

        public Task<IAgentSession> StartSessionAsync(SessionOptions options, IIdeServices ide, CancellationToken cancellationToken = default)
        {
            // Mirror a backend's own MCP server announcing readiness right after session/new — no
            // prompt is streaming yet, so this exercises the out-of-turn status wire (SessionOptions
            // sink → shell/onAgentEvent → transcript notice) offline, the way Kiro's
            // _kiro.dev/mcp/server_initialized arrives on a pre-warmed session.
            options.OutOfTurnEvents?.Invoke(new AgentEvent.McpServerConnected("fake-mcp"));

            // Our own MCP bridge, in the two states a real one passes through: the pipe host exists but
            // no agent has handshaken, then the agent has connected and taken the catalog. The fake
            // declares no AgentCapabilities.Mcp — so the engine starts no real pipe host for it — and
            // these stand in for what McpPipeHost would push. (The real host→observer path is covered
            // engine-side by McpHandshakeLogTests, over a live JSON-RPC pair.)
            options.OutOfTurnEvents?.Invoke(new AgentEvent.McpBridgeStatusUpdated(
                new McpBridgeStatus(Connected: false, ToolsServed: null, ToolNames: null, ToolCalls: 0)));
            options.OutOfTurnEvents?.Invoke(new AgentEvent.McpBridgeStatusUpdated(
                new McpBridgeStatus(
                    Connected: true, ToolsServed: 2, ToolNames: new[] { "build_solution", "run_tests" }, ToolCalls: 1)));

            // The roster panel's two halves (issue #122), driven the way a real v3 session drives them:
            // a complete roster arriving mid-connect, then a second snapshot with everything up. Two
            // frames rather than one, because the interesting assertion is the TRANSITION — a single
            // frame cannot tell a panel that updates from one that latched on its first value.
            options.OutOfTurnEvents?.Invoke(new AgentEvent.McpRosterUpdated(new McpRoster(
                new[]
                {
                    new McpServerStatus("fake-mcp", true, "connected", null, 2, new[] { "echo", "reverse" }),
                    new McpServerStatus("slow-mcp", false, "connecting", "oauth", null, null),
                },
                NamesWholeConfiguredSet: true)));
            options.OutOfTurnEvents?.Invoke(new AgentEvent.McpRosterUpdated(new McpRoster(
                new[]
                {
                    new McpServerStatus("fake-mcp", true, "connected", null, 2, new[] { "echo", "reverse" }),
                    new McpServerStatus("slow-mcp", true, "connected", "oauth", 3, new[] { "a", "b", "c" }),
                },
                NamesWholeConfiguredSet: true)));

            return Task.FromResult<IAgentSession>(
                new FakeSession(
                    ide, options.ResumeConversationId, options.ImportHistory, options.OutOfTurnEvents));
        }

        // Deliberately NOT IBackendSessionList: the fake's whole value to the CLI-pickup checks is that
        // its store is the PROVIDER's, reached down the cold path with nothing installed. A session that
        // could answer a listing would take that path over the moment a session was open - which is
        // exactly what the Desktop --smoke does - and every one of those checks would silently start
        // measuring the warm path instead. The warm path has its own double, in the tests.
        private sealed class FakeSession
            : IAgentSession, IImportedHistoryReport, ISessionNegotiationReport, IResumeFallbackReport
        {
            /// <summary>
            /// A resume id spelled with this in it is DISCLAIMED rather than echoed: the session opens
            /// on a fresh id and reports why, the way kiro-cli v2 answers an id its store has never
            /// held (issue #268). Keyed on the id and not on a flag because that is the one fact the
            /// real backend decides on, and because it leaves every other offline check untouched —
            /// nothing else in the tree spells a conversation this way.
            /// </summary>
            internal const string DisclaimedIdMarker = "-disclaimed";

            /// <summary>
            /// A resume id spelled with this in it is ACCEPTED and replays nothing: the load reports
            /// success on the requested id with no history behind it, the way Kiro v3 answers an id
            /// it no longer holds (issue #185, measured by <c>Console resume-unknown-id</c>). The
            /// silent counterpart of <see cref="DisclaimedIdMarker"/>, and the only offline way to
            /// drive the host's post-hoc check. Any other resumed id is reported as fully replayed.
            /// </summary>
            internal const string EmptyReplayMarker = "-empty";

            private const string DisclaimedReason =
                "Failed to start session: Session not found";

            private readonly IIdeServices _ide;
            private readonly string? _resumedFrom;
            private readonly Action<AgentEvent>? _outOfTurnEvents;

            // Steered messages awaiting the running turn's reply. A queue rather than a single slot
            // because nothing stops the user steering twice before the turn gets to either.
            private readonly ConcurrentQueue<string> _steered = new();
            // Every steer this session has taken, either outcome. "[slow-reply]" waits on it: the
            // queue above is empty again by the time a "[race]" steer has been answered.
            private int _steerRequests;

            public FakeSession(
                IIdeServices ide, string? resumedFrom, bool importHistory,
                Action<AgentEvent>? outOfTurnEvents)
            {
                _ide = ide;
                // A disclaimed id was never adopted, so it is not what this session resumed from —
                // dropping it here is what makes every reader below agree: no echo in the reply, no
                // scripted import, and the new id in ConversationId.
                var disclaimed = resumedFrom is { Length: > 0 }
                    && resumedFrom.Contains(DisclaimedIdMarker, StringComparison.Ordinal);
                _resumedFrom = disclaimed ? null : resumedFrom;
                ResumeFailureReason = disclaimed ? DisclaimedReason + ": " + resumedFrom : null;
                _outOfTurnEvents = outOfTurnEvents;
                ImportedHistory = importHistory && !string.IsNullOrEmpty(_resumedFrom)
                    ? ScriptedImport(_resumedFrom!)
                    : Array.Empty<ImportedTurnEntry>();
                // Null with no resume asked for; zero where the fake models the silent backend, or
                // where the id was disclaimed (a refused load replays nothing either); otherwise the
                // scripted history's own length, the fake standing in for a backend that carries.
                ReplayedHistoryCount = string.IsNullOrEmpty(resumedFrom)
                    ? null
                    : disclaimed || resumedFrom!.Contains(EmptyReplayMarker, StringComparison.Ordinal)
                        ? 0
                        : ScriptedImport(resumedFrom!).Count;
            }

            /// <inheritdoc/>
            public string? ResumeFailureReason { get; }

            /// <inheritdoc/>
            public int? ReplayedHistoryCount { get; }

            // Echoes the id it was asked to resume, rather than a constant. A resumed session IS that
            // conversation, so a fixed id makes every import report the same conversation back - and
            // the import path could not be driven offline at all, since nothing downstream could tell
            // which conversation the transcript it pulled belonged to.
            public string ConversationId => string.IsNullOrEmpty(_resumedFrom) ? "fake-conv-1" : _resumedFrom!;

            /// <inheritdoc/>
            public IReadOnlyList<ImportedTurnEntry> ImportedHistory { get; }

            /// <summary>
            /// A deliberately MIXED handshake answer (issue #160): one capability offered, one
            /// refused, one never mentioned. The fake exists to exercise every facet, and this is the
            /// one surface whose three states must stay distinguishable all the way to the panel - a
            /// fake that reported all-true or all-false would let the wire collapse a null into a
            /// false with every offline check still green.
            /// <para>Built on read, like the real one, so a probe landing late is modelled too.</para>
            /// </summary>
            public SessionNegotiation Negotiation => new SessionNegotiation(
                AgentProgram: "fake-agent 1.0",
                AgentLogDirectory: null,
                SupportsResume: true,
                SupportsSteering: true,
                SupportsImages: false,
                SupportsSessionList: null,
                OpenedAt: _openedAt,
                // A mode story with every field distinct (issues #269/#270): opened in one mode, now
                // in another, origin stated — so the wire test can see each one arrive rather than a
                // null that would be indistinguishable from a dropped field.
                ModeLabel: "Mode",
                ModeId: "default",
                ModeName: "Manual",
                OpenedInModeId: "bypassPermissions",
                OpenedInModeName: "Bypass Permissions",
                ModeOrigin: "bundled");

            private readonly DateTimeOffset _openedAt = DateTimeOffset.Now;

            /// <summary>
            /// A conversation as a backend would replay it during <c>session/load</c> (issue #108) —
            /// the only way the import wire (SessionOptions.ImportHistory -> IImportedHistoryReport ->
            /// engine/takeImportedHistory) is drivable with no CLI installed.
            /// <para>Four entries covering the shapes a host renders differently: a user prompt, a
            /// tool call carrying rawInput (so the row has a subtitle and a target to decode), an edit
            /// (its own card rather than a row), and the call's completion. A single-kind script would
            /// pass a mapping that dropped everything but text.</para>
            /// </summary>
            private static IReadOnlyList<ImportedTurnEntry> ScriptedImport(string conversationId) => new[]
            {
                ImportedTurnEntry.User($"Add a retry to the uploader (imported from {conversationId})"),
                ImportedTurnEntry.Agent(new AgentEvent.ToolCallStarted(
                    "imported-tool-1", "Read Uploader.cs", "read",
                    RawInputJson: "{\"path\":\"src/Uploader.cs\",\"description\":\"Read the uploader\"}")),
                ImportedTurnEntry.Agent(new AgentEvent.EditProposed(
                    "src/Uploader.cs", "var r = Send();", "var r = Retry(() => Send());",
                    ToolCallId: "imported-tool-1")),
                ImportedTurnEntry.Agent(new AgentEvent.ToolCallCompleted(
                    "imported-tool-1", Success: true, ResultText: "Applied the retry wrapper.")),
            };

            public async IAsyncEnumerable<AgentEvent> SendAsync(PromptInput prompt, [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                // A summarization pass (engine/summarize): return a short canned summary only — no tools,
                // no edits — so the isolated summary round-trip is observable offline.
                if (prompt.Text.Contains("Summarize the following conversation"))
                {
                    yield return new AgentEvent.AssistantTextDelta("Fake summary: the user and assistant set up the project and wrote a file; key points condensed here.");
                    yield return new AgentEvent.TurnCompleted(Usage: null, StopReason: "end_turn");
                    yield break;
                }

                // A deliberately slow tool call, and nothing else (issue #70). Holding a mid-turn message
                // exists to protect work in progress — a steer aborts the tool call it lands on — so
                // proving it needs a call that stays open long enough for a host to type into. Every
                // other tool call in this fake completes on the next line.
                // The backend telling the user something (issues #208, #85). Scripted here because
                // none of the three real routes can be triggered on demand: a usage limit costs money
                // and a day's throttling, auto-compaction is gated on context size (~1M tokens), and
                // Kiro does not expose /compact over ACP at all. So a developer who wants to LOOK at
                // one - the wording, the level, whether it interrupts the reply - has no way to make
                // it happen against a real backend. The frames themselves are pinned separately by
                // `Console notice-replay`, which drives the captured bytes through the real transport;
                // this is for the eye, that is for the gate.
                if (prompt.Text.Contains("[notices]"))
                {
                    yield return new AgentEvent.AssistantTextDelta("Working on it");

                    // Mid-reply on purpose. A compaction arrives inside a turn that then carries on
                    // (measured on Kiro: 214 frames before that turn ended), so this is where the
                    // "does the notice split the message in two" question is actually asked.
                    yield return new AgentEvent.BackendNotice(
                        "The agent condensed this conversation to free up context. It now works from a "
                        + "summary of the earlier messages plus the recent ones, so detail still visible "
                        + "above may no longer be in its memory.");

                    yield return new AgentEvent.AssistantTextDelta(" — and here is the rest of the reply.\n\n");

                    // The backend's own word for severity, relayed. "warning" is not escalated: of the
                    // two levels ever captured this one is a delay, and the severity the user needs is
                    // in the sentence.
                    yield return new AgentEvent.BackendNotice(
                        "The selected model is experiencing high load, rate limiting applied. "
                        + "Consider switching models.",
                        "warning");

                    // An errorType as the level, which the host escalates on.
                    yield return new AgentEvent.BackendNotice(
                        "You've reached your monthly usage limit. Please return next month to continue building.",
                        "UsageLimitReachedError");

                    // A level nobody has ever seen must render, not vanish - dropping is the defect
                    // this whole path exists to fix.
                    yield return new AgentEvent.BackendNotice(
                        "A level this host has never seen before must still be shown.",
                        "something-nobody-has-seen");

                    // One with detail, which collapses behind the expander #82 built.
                    yield return new AgentEvent.BackendNotice(
                        "[ERROR] [KRS] HTTP 429 requestId=fake-0001 body={\"reason\":\"CREDIT_CONSUMPTION_RATE_EXCEEDED\"}",
                        "error",
                        "The whole line is in engine.log.");

                    yield return new AgentEvent.TurnCompleted(Usage: null, StopReason: "end_turn");
                    yield break;
                }

                if (prompt.Text.Contains("[slow-tool]"))
                {
                    yield return new AgentEvent.AssistantTextDelta("Running the tests now.\n\n");
                    yield return new AgentEvent.ToolCallStarted("slow1", "Running: run_tests", "execute", RawInputJson: null);

                    // A steer arriving DURING the call destroys it. Measured live against
                    // claude-agent-acp: a steer sent while run_tests was running returned
                    // "AbortError: interrupt" to the agent and the run was lost, with nothing surfacing
                    // that to the user — the tool row simply stopped. Modelled here because it is the
                    // entire reason a mid-turn message is held rather than sent, and without it a host
                    // that steers immediately would still pass a "the call finished" check.
                    var interrupted = false;
                    for (var i = 0; i < 100 && !interrupted; i++)
                    {
                        await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                        interrupted = !_steered.IsEmpty;
                    }

                    if (interrupted)
                    {
                        yield return new AgentEvent.ToolCallCompleted(
                            "slow1", Success: false, ResultText: "AbortError: interrupt");
                    }
                    else
                    {
                        yield return new AgentEvent.ToolCallCompleted("slow1", Success: true, ResultText: "42 passed");
                    }

                    // Give a host that held a message across this boundary time to deliver it, so the
                    // steered text lands inside this turn rather than trailing after it.
                    for (var i = 0; i < 100 && _steered.IsEmpty; i++)
                        await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                    while (_steered.TryDequeue(out var afterBoundary))
                        yield return new AgentEvent.AssistantTextDelta($"\n\n[steered mid-turn] {afterBoundary}");

                    yield return new AgentEvent.TurnCompleted(Usage: null, StopReason: "end_turn");
                    yield break;
                }

                // When the host resumed a prior conversation, echo the id so the resume wire path
                // (StartSession → SessionOptions.ResumeConversationId → provider) is observable offline.
                if (!string.IsNullOrEmpty(_resumedFrom))
                    yield return new AgentEvent.AssistantTextDelta($"[resumed {_resumedFrom}] ");

                // When the host resumed from a summary, the first prompt carries a conversation-summary
                // block instead of a backend id — echo a marker so that path is observable offline too.
                // Matched on the tag NAME rather than on a whole open tag: the block is written fenced,
                // its name carrying a per-send nonce nothing on this side can predict.
                if (prompt.Text.Contains("<" + HostPromptBlocks.ConversationSummary.Name, StringComparison.Ordinal))
                    yield return new AgentEvent.AssistantTextDelta("[summary-resume] ");

                yield return new AgentEvent.AssistantTextDelta("Fake agent exercising IDE services over IPC. ");

                // A reply that has BEGUN and then waits for a steer (issue #273). The ordinary turn
                // streams its whole reply inside one dispatcher tick, so a host that steers only once
                // the turn has produced content — which is now the rule — finds the turn already over
                // by the time it looks. This holds the turn open after its first delta, which is
                // exactly the state the steer proof needs to steer into: begun, and nothing running.
                // Bounded, so a host that never steers still gets its turn back.
                if (prompt.Text.Contains("[slow-reply]"))
                {
                    var seen = Volatile.Read(ref _steerRequests);
                    for (var i = 0; i < 150 && Volatile.Read(ref _steerRequests) == seen; i++)
                        await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                }

                // Capture editor context (marshals to the shell).
                var snapshot = await _ide.Workspace.CaptureAsync(cancellationToken).ConfigureAwait(false);
                yield return new AgentEvent.AssistantTextDelta($"[solution={snapshot.SolutionName}]\n\n");

                // A markdown sample streamed as separate deltas, so the assistant bubble exercises the
                // markdown renderer AND its re-parse-on-delta path (each delta grows the same message).
                yield return new AgentEvent.AssistantTextDelta("## Markdown demo\n\nHere's some **bold**, *italic*, and ");
                yield return new AgentEvent.AssistantTextDelta("`inline code`, plus a [link](https://example.com).\n\n");
                yield return new AgentEvent.AssistantTextDelta("- first item\n- second item\n- third item\n\n");
                yield return new AgentEvent.AssistantTextDelta("```csharp\n// answer\nvar x = 42;\nConsole.WriteLine($\"x = {x}\");\n```\n\n");
                yield return new AgentEvent.AssistantTextDelta("```sql\nSELECT Name, COUNT(*) FROM Users -- popular names\nWHERE Age > 21 GROUP BY Name\n```\n\n");
                yield return new AgentEvent.AssistantTextDelta("> A short block quote to finish.\n\n");

                // Colour-emoji + richer-markdown sampler: a status table with emoji (the original
                // monochrome-✅ bug), sequences that need real segmentation (ZWJ family, skin tone,
                // flag, keycap), a task list, and a data:-URI image — everything the higher-fidelity
                // rendering must show in colour in the Desktop --screenshot artifact.
                yield return new AgentEvent.AssistantTextDelta("| Check | Result |\n|---|---|\n| Build | ✅ |\n| Tests | ❌ |\n\n");
                yield return new AgentEvent.AssistantTextDelta("Emoji sampler: 🚀 👍🏽 👩‍👩‍👧‍👧 🇬🇧 1️⃣ ❤️\n\n");
                yield return new AgentEvent.AssistantTextDelta("- [x] render colour emoji\n- [ ] world domination\n\n");
                yield return new AgentEvent.AssistantTextDelta("![demo image](data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAADAAAAAgCAYAAABU1PscAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAABKSURBVFhH7c8xDQAgFMTQU4g8HOAPB58dA80lHd7SrVn7TrP8oY0DNAdoDtAcoDlAc4DmAC1zMs0coDlAc4DmAM0BmgM0B2j1Aw/TxWLxBcipyQAAAABJRU5ErkJggg==)\n\n");

                // A non-mutating read step first (exercises the per-kind tool icon) — opened with a
                // placeholder title/empty input and enriched on an update, the way the Claude Code
                // adapter streams tool calls, so the row's in-place title/detail merge is exercised.
                yield return new AgentEvent.ToolCallStarted("r1", "Read File", "read", RawInputJson: "{}");
                // The enriching update carries a Claude-style "description" intent → row subtitle.
                yield return new AgentEvent.ToolCallUpdated("r1", "Read notes.txt", Kind: null,
                    RawInputJson: "{\"file_path\":\"notes.txt\",\"description\":\"Read the notes file to check its contents\"}");
                yield return new AgentEvent.ToolCallCompleted("r1", Success: true, ResultText: "1\tfake notes content");

                // A Kiro-style batched read: one call, several files in an "operations" array (plus a
                // directory entry that must be skipped) — exercises the multi-target row (issue #20):
                // clicking it opens every file, not just the first.
                yield return new AgentEvent.ToolCallStarted("r2", "Reading 2 files", "read",
                    RawInputJson: "{\"operations\":[{\"mode\":\"Directory\",\"path\":\".\"},{\"path\":\"a.txt\"},{\"path\":\"b.txt\"}]}");
                yield return new AgentEvent.ToolCallCompleted("r2", Success: true, ResultText: "read 2 files");

                // Kiro's v3 engine (captured live, explicit nulls and all): the SAME generic title on the
                // open and again on the completion, with the path only ever in the arguments. Nothing
                // enriches it, so the row has to name the file itself — and keep naming it once that
                // second frame resets the title (issue #102).
                const string v3ReadInput = "{\"path\":\"config.json\",\"offset\":null,\"limit\":null}";
                yield return new AgentEvent.ToolCallStarted("r3", "Read File", "read", RawInputJson: v3ReadInput);
                yield return new AgentEvent.ToolCallUpdated("r3", "Read File", Kind: null, RawInputJson: v3ReadInput);
                yield return new AgentEvent.ToolCallCompleted("r3", Success: true, ResultText: "fake config contents");

                // Two sub-agent fan-outs (issue #125), because the two flavours end differently and a
                // fix built against either alone leaves the other exactly as wrong as it was.
                //
                // SYNCHRONOUS: the row completes when the sub-agent does, and its completion carries the
                // deliverable. Children arrive BEFORE that completion, so they nest into a row that is
                // still running.
                yield return new AgentEvent.ToolCallStarted("sa1", "Task", "think", RawInputJson: "{}",
                    ToolName: null, ParentToolCallId: null, IsSubagentLaunch: true);
                yield return new AgentEvent.ToolCallUpdated("sa1", "Summarize test methods", Kind: null,
                    RawInputJson: "{\"description\":\"Summarize test methods\",\"subagent_type\":\"Explore\"," +
                                  "\"prompt\":\"List the test methods in this repo and what each covers.\"}",
                    ToolName: null, ParentToolCallId: null, IsSubagentLaunch: true);
                yield return new AgentEvent.ToolCallStarted("sa1-c1", "Find `**/*Tests.cs`", "search",
                    RawInputJson: "{\"pattern\":\"**/*Tests.cs\"}", ToolName: null, ParentToolCallId: "sa1");
                yield return new AgentEvent.ToolCallCompleted("sa1-c1", Success: true, ResultText: "ChatViewModelTests.cs");
                yield return new AgentEvent.ToolCallStarted("sa1-c2", "Read ChatViewModelTests.cs", "read",
                    RawInputJson: "{\"file_path\":\"ChatViewModelTests.cs\"}", ToolName: null, ParentToolCallId: "sa1");
                yield return new AgentEvent.ToolCallCompleted("sa1-c2", Success: true, ResultText: "1\t[Fact] public void Maps()");
                yield return new AgentEvent.ToolCallCompleted("sa1", Success: true,
                    ResultText: "One test class, covering the event mapping.");

                // ASYNCHRONOUS: the backend reports this call "completed" the instant it is LAUNCHED,
                // and the sub-agent's calls arrive AFTERWARDS — so the row must not be showing a green
                // tick by the time they do, and nothing ever tells us when it really ends.
                yield return new AgentEvent.ToolCallStarted("sa2", "Task", "think", RawInputJson: "{}",
                    ToolName: null, ParentToolCallId: null, IsSubagentLaunch: true);
                yield return new AgentEvent.ToolCallUpdated("sa2", "Map solution projects", Kind: null,
                    RawInputJson: "{\"description\":\"Map solution projects\",\"subagent_type\":\"Explore\"," +
                                  "\"run_in_background\":true,\"prompt\":\"Find every .csproj and report its target framework.\"}",
                    ToolName: null, ParentToolCallId: null, IsSubagentLaunch: true);
                // The launch receipt. ResultText is null exactly as the mapper leaves it: what the real
                // backend returns here is a block of internal metadata asking not to be quoted.
                yield return new AgentEvent.ToolCallCompleted("sa2", Success: true, ResultText: null,
                    ErrorText: null, LaunchedInBackground: true);
                yield return new AgentEvent.ToolCallStarted("sa2-c1", "Find `**/*.csproj`", "search",
                    RawInputJson: "{\"pattern\":\"**/*.csproj\"}", ToolName: null, ParentToolCallId: "sa2");
                yield return new AgentEvent.ToolCallCompleted("sa2-c1", Success: true, ResultText: "12 projects");
                yield return new AgentEvent.ToolCallStarted("sa2-c2", "Read Directory.Build.props", "read",
                    RawInputJson: "{\"file_path\":\"Directory.Build.props\"}", ToolName: null, ParentToolCallId: "sa2");
                yield return new AgentEvent.ToolCallCompleted("sa2-c2", Success: true, ResultText: "net10.0");

                // A plan/task list (exercises the live checklist card + its three states): the second
                // item is in-progress now and ticks off after the edit below.
                yield return new AgentEvent.PlanUpdated("Fake task list", new[]
                {
                    new PlanItem("1", "Read workspace files", PlanItemStatus.Completed),
                    new PlanItem("2", "Write notes.txt", PlanItemStatus.InProgress),
                });

                // A run_tests result, so the host's test-results card is exercised: a pass/fail summary
                // plus a failing test with a source location (clickable to "go to source" in the VSIX).
                // Emitted before the permission gate so it's in the transcript when --screenshot snapshots.
                // The resultsFile is the host's card dedupe key — unique per turn, like a real run's
                // guid'd TRX path (a constant here would suppress the card on every prompt after the first).
                var workspace = _ide.Workspace.RootPath ?? "C:\\proj";
                var testRunJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    resultKind = "testRun",
                    succeeded = false,
                    total = 5,
                    passed = 3,
                    failed = 1,
                    skipped = 1,
                    failureCount = 1,
                    truncatedFailures = false,
                    failures = new[]
                    {
                        new
                        {
                            // Shaped like a Reqnroll/SpecFlow failure — the case the row's second jump
                            // exists for: it threw in a Then step definition, while the test worth reading
                            // is the scenario in the .feature (the tool resolves that from the generated
                            // code-behind's #line frames). Exercises "Go to scenario" offline.
                            name = "Add two numbers",
                            outcome = "Failed",
                            message = "Assert.Equal() Failure: Expected: 5, Actual: 4",
                            file = System.IO.Path.Combine(workspace, "Steps", "CalculatorSteps.cs"),
                            line = 26,
                            testFile = System.IO.Path.Combine(workspace, "Features", "Calculator.feature"),
                            testLine = 9,
                        },
                    },
                    resultsFile = $"results-{System.Guid.NewGuid():N}.trx",
                });
                yield return new AgentEvent.ToolCallStarted("t2", "Running: run_tests", "execute", RawInputJson: null);
                // Mirror the real delivery order: the shell side-channel (ShellRpcTarget.ToolsInvokeAsync)
                // fires the payload FIRST under the synthetic "cwkt-testrun" id — while the tool call is
                // still in flight, and never on the real row id — then the agent's echo (Claude) completes
                // the real row with the same payload. The host must show ONE card and still declutter the
                // row from the echo; a single-delivery fake would hide an ordering bug here (and did).
                // Both deliveries carry the payload WHOLE, which is what the wire now produces: the echo
                // used to be cut mid-JSON here, modelling AcpMapper's display clamp, so the smoke only
                // ever exercised the parse-failure fallback and the clean path went uncovered while it
                // threw on every real test run (issue #83). The fallback is pinned by unit test instead
                // (TestRunPayloadClampTests), where a malformed payload can be built deliberately.
                // Only the side-channel copy is marked HostAuthored, mirroring the real hosts exactly:
                // the shell stamps the result its own tool catalog returned, and the agent's echo is
                // just text the backend sent back. The host builds its card from the marked one alone,
                // so a fake that marked both would let a broken gate pass this check.
                yield return new AgentEvent.ToolCallCompleted(
                    "cwkt-testrun", Success: false, ResultText: testRunJson, HostAuthored: true);
                yield return new AgentEvent.ToolCallCompleted("t2", Success: false, ResultText: testRunJson);

                // A set_breakpoint result (issue #73), so the breakpoint card is exercised offline: a
                // conditional stop, a TRACEPOINT (prints and continues — the row that must not look like
                // a stop), and one that failed to bind, which is the partial-success case the payload
                // reports rather than failing the whole call.
                //
                // Delivered TWICE for the same reason as the test run: the shell side-channel fires first
                // under its own synthetic id (which is how the card reaches the chat on a backend that
                // never echoes MCP results), then the agent's echo completes the real row. One card must
                // appear, and the row must still be decluttered by the second delivery — the ordering bug
                // a single-delivery fake would hide.
                var breakpointsJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    resultKind = "breakpoints",
                    action = "set",
                    // Unique per turn, like the tool's own: a constant would suppress the card on every
                    // prompt after the first.
                    requestId = System.Guid.NewGuid().ToString("N").Substring(0, 8),
                    summary = "Set 2 of 3 breakpoints; 1 failed.",
                    set = 2,
                    failed = 1,
                    breakpoints = new object[]
                    {
                        new
                        {
                            file = System.IO.Path.Combine(workspace, "Orders", "OrderProcessor.cs"),
                            line = 47,
                            condition = "order.Items.Count == 0",
                            reason = "the empty-order path is the only one that reaches the null deref",
                            status = "set",
                        },
                        new
                        {
                            file = System.IO.Path.Combine(workspace, "Orders", "OrderProcessor.cs"),
                            line = 62,
                            printMessage = "item {i}: total={running.Total}",
                            reason = "trace the running total without stopping the loop",
                            tracepoint = true,
                            status = "set",
                        },
                        new
                        {
                            file = System.IO.Path.Combine(workspace, "Orders", "Generated.g.cs"),
                            line = 5,
                            reason = "confirm the generated overload is the one being called",
                            status = "failed",
                            error = "Visual Studio accepted the request but created no breakpoint — check the line is an executable statement in a file that is part of the build.",
                        },
                    },
                });
                yield return new AgentEvent.ToolCallStarted("t3", "Running: set_breakpoint", "other", RawInputJson: null);
                // Marked on the side-channel delivery only, for the reason given at the test run above.
                yield return new AgentEvent.ToolCallCompleted(
                    "cwkt-breakpoints", Success: true, ResultText: breakpointsJson, HostAuthored: true);
                yield return new AgentEvent.ToolCallCompleted("t3", Success: true, ResultText: breakpointsJson);

                // A sub-agent crew (exercises the crew card): roster snapshots upsert rows in place
                // (spawn → working → terminated, the way Kiro's _kiro.dev/subagent/list_update
                // streams them), one row's final result attaches and is expandable as markdown.
                yield return new AgentEvent.SubagentsUpdated(new[]
                {
                    new SubagentInfo("sub-1", "read_config_files", "Read and return the config files", "working", "Running", "crew-Fake parallel readers"),
                    new SubagentInfo("sub-2", "read_test_files", "Read and return the test files", "working", "Running", "crew-Fake parallel readers"),
                });
                yield return new AgentEvent.SubagentResult("sub-1", "The config contains:\n\n```json\n{ \"setting\": true }\n```");
                yield return new AgentEvent.SubagentsUpdated(new[]
                {
                    new SubagentInfo("sub-1", "read_config_files", "Read and return the config files", "terminated", null, "crew-Fake parallel readers"),
                    new SubagentInfo("sub-2", "read_test_files", "Read and return the test files", "terminated", null, "crew-Fake parallel readers"),
                });

                // A shell-command permission (kind "execute") so the editable "always allow" command
                // rule is exercised offline (the banner pre-fills "git status", user can glob it).
                // Kiro-style rawInput: the intent rides in "__tool_use_purpose" → row subtitle.
                yield return new AgentEvent.ToolCallStarted("c1", "Running: git status", "execute",
                    RawInputJson: "{\"command\":\"git status\",\"__tool_use_purpose\":\"Check the working tree status before committing\"}");
                var cmdDecision = await _ide.Permissions.RequestAsync(
                    new PermissionRequest("c1", "Running: git status", "execute", Detail: null, Command: "git status",
                        new[]
                        {
                            new PermissionOption("allow", "Allow", PermissionOptionKind.AllowOnce),
                            new PermissionOption("allow-always", "Allow always", PermissionOptionKind.AllowAlways),
                            new PermissionOption("reject", "Reject", PermissionOptionKind.RejectOnce),
                        }),
                    cancellationToken).ConfigureAwait(false);
                // Live output chunks while the command "runs" (Kiro streams a shell command's stdout as
                // status-less tool_call_update content) — the row must accumulate them, and the null-
                // result completion below must NOT wipe them (real watched output beats a blank row).
                if (!cmdDecision.Cancelled && cmdDecision.OptionId != "reject")
                {
                    yield return new AgentEvent.ToolCallOutputChunk("c1", "On branch main");
                    yield return new AgentEvent.ToolCallOutputChunk("c1", "nothing to commit, working tree clean");
                }
                yield return new AgentEvent.ToolCallCompleted(
                    "c1", Success: !cmdDecision.Cancelled && cmdDecision.OptionId != "reject", ResultText: null);

                yield return new AgentEvent.ToolCallStarted("t1", "Write notes.txt", "edit", RawInputJson: null);

                // Ask permission (marshals to the shell), then write through the host (marshals too).
                // Offer the full option set — allow / allow-always / reject — so the banner and the
                // policy's remember-always + reject paths are exercised against the fake host too.
                var decision = await _ide.Permissions.RequestAsync(
                    new PermissionRequest("t1", "Write notes.txt", "edit", Detail: null, Command: null,
                        new[]
                        {
                            new PermissionOption("allow", "Allow", PermissionOptionKind.AllowOnce),
                            new PermissionOption("allow-always", "Allow always", PermissionOptionKind.AllowAlways),
                            new PermissionOption("reject", "Reject", PermissionOptionKind.RejectOnce),
                        }),
                    cancellationToken).ConfigureAwait(false);

                // Honor a reject the way a real agent would: only write on an allow decision.
                var allowed = !decision.Cancelled &&
                    (decision.OptionId == "allow" || decision.OptionId == "allow-always");
                if (allowed)
                {
                    const string content = "Written by the fake provider through the host's IEditApplier over IPC.\n";
                    await _ide.Edits.WriteTextFileAsync("notes.txt", content, cancellationToken).ConfigureAwait(false);
                    // Surface the edit in the transcript (as a real ACP agent's edit mirror would) —
                    // twice, the way the Claude Code adapter finalizes a write: a provisional diff
                    // with no "before" first, then the real one. The host must show ONE row (the
                    // final diff), keyed by tool call + path.
                    yield return new AgentEvent.EditProposed("notes.txt", string.Empty, content, ToolCallId: "t1");
                    yield return new AgentEvent.EditProposed("notes.txt", "old fake notes\n", content, ToolCallId: "t1");
                }

                yield return new AgentEvent.ToolCallCompleted("t1", Success: allowed, ResultText: null);

                // A Kiro-style edit: the diff arrives at tool_call start, so no tool row ever opens
                // and the transcript gets a standalone edit card. The call's completion must still
                // reach that card and flip its status — the pencil goes green (issue #46; the
                // completion used to be dropped because no tool row matched the id).
                if (allowed)
                {
                    yield return new AgentEvent.EditProposed(
                        "kiro-notes.txt", "old kiro notes\n", "new kiro notes\n", ToolCallId: "k1");
                    yield return new AgentEvent.ToolCallCompleted("k1", Success: true, ResultText: null);
                }

                // Finish the plan the way Kiro does (issue #17): the last completion arrives as an
                // EMPTY list (Kiro disposes its task list when the final item completes) — the host
                // must tick every remaining item off, not drop the update.
                if (allowed)
                    yield return new AgentEvent.PlanUpdated(null, System.Array.Empty<PlanItem>());

                // Consumption, in the two halves the real backends split it across: a mid-turn snapshot
                // carrying context fill + cost (Claude's usage_update shape, plus Kiro's breakdown and
                // thresholds so one fake exercises both backends' fields), then the per-turn token split
                // on completion. The host must MERGE these — a host that replaced wholesale would blank
                // the context figure the moment the turn ended.
                yield return new AgentEvent.UsageUpdated(new UsageReport
                {
                    ContextPercent = 21.4,
                    ContextUsedTokens = 21400,
                    ContextWindowTokens = 100000,
                    Cost = 0.42,
                    CostCurrency = "USD",
                    SummarizeThresholdPercent = 80,
                    TruncateThresholdPercent = 95,
                    Breakdown = new[]
                    {
                        new UsageBreakdownEntry("Your prompts", 4356, 4.4),
                        new UsageBreakdownEntry("Tool definitions", 4241, 4.2),
                    },
                });

                // Anything steered into this turn is answered before it ends, which is the shape a real
                // injected steer takes: the message lands mid-turn and the agent folds its response into
                // the turn already streaming, rather than opening a second one.
                while (_steered.TryDequeue(out var steered))
                    yield return new AgentEvent.AssistantTextDelta($"\n\n[steered mid-turn] {steered}");

                yield return new AgentEvent.TurnCompleted(
                    new UsageReport { InputTokens = 3869, OutputTokens = 370, CachedReadTokens = 28112, TotalTokens = 32351 },
                    StopReason: "end_turn");
            }

            public Task<SteerOutcome> SteerAsync(PromptInput prompt, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _steerRequests);

                // Two outcomes, selected by a marker in the text the way the summarize and
                // summary-resume paths above are, because they exercise genuinely different plumbing.
                // A steer that LOST the race to the turn's end reaches the host only through the
                // out-of-turn sink — there is no turn of ours left to carry it — so it needs its own
                // offline path or that branch is only ever proven against a live backend.
                if (prompt.Text.Contains("[race]"))
                {
                    _outOfTurnEvents?.Invoke(new AgentEvent.AssistantTextDelta(
                        $"\n\n[steer started a new turn] {prompt.Text}"));
                    return Task.FromResult(SteerOutcome.StartedNewTurn);
                }

                _steered.Enqueue(prompt.Text);
                return Task.FromResult(SteerOutcome.Injected);
            }

            public Task CancelAsync() => Task.CompletedTask;

            public Task SetModelAsync(string modelId, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public ValueTask DisposeAsync() => default;
        }
    }
}
