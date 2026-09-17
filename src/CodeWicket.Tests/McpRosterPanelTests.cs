using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CodeWicket.Core.Ide;
using CodeWicket.Ipc;
using CodeWicket.Shell;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The MCP roster panel (issue #122) — the strip's second detail panel, beside the context ring.
    /// <para>
    /// What is pinned here is the honesty of the panel rather than its layout, because every failure
    /// this feature can have is a confident wrong answer about the user's OWN configuration. Three
    /// backends report three different amounts: Kiro v3 names the whole configured set before
    /// <c>session/new</c> returns, Kiro's default engine names a server only once it has come up, and
    /// Claude Code says nothing about MCP at all. Rendering the second or third as "0 servers" — or
    /// printing a denominator where none is knowable — states something false about the user's setup,
    /// which is worse than the silence it replaces.
    /// </para>
    /// </summary>
    public class McpRosterPanelTests
    {
        /// <summary>
        /// A complete roster is a SNAPSHOT: the second frame replaces the first, so a server the user
        /// removed from their config actually disappears. This is the assertion that separates snapshot
        /// from accretion — with an upsert, the count could only ever grow.
        /// </summary>
        [Fact]
        public void ACompleteRosterReplacesRatherThanAccumulating() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);

            engine.Raise(Roster(true, Server("aws-mcp", true, tools: 9), Server("gitlab", true, tools: 4)));
            DrainDispatcher();
            Assert.Equal("2 of 2 connected", ValueFor(vm, "Agent MCP servers"));

            engine.Raise(Roster(true, Server("aws-mcp", true, tools: 9)));
            DrainDispatcher();

            Assert.Equal("1 of 1 connected", ValueFor(vm, "Agent MCP servers"));
            Assert.DoesNotContain(vm.McpRows, r => r.Label == "gitlab");
        });

        /// <summary>
        /// An INCOMPLETE roster accretes and never removes: the default engine announces one server at
        /// a time, so treating its frame as a snapshot would delete every server but the one just named.
        /// <para>And it prints no denominator anywhere. That engine reports a server only when it comes
        /// up, so "1 connected" and "1 connected, four others dead" are byte-identical evidence — a
        /// denominator you cannot see is not a denominator you may print.</para>
        /// </summary>
        [Fact]
        public void AnIncompleteRosterAccumulatesAndStatesNoTotal() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);

            engine.Raise(Roster(false, Server("aws-mcp", true)));
            engine.Raise(Roster(false, Server("gitlab", true)));
            DrainDispatcher();

            Assert.Equal("2 connected", ValueFor(vm, "Agent MCP servers"));
            Assert.Contains(vm.McpRows, r => r.Label == "aws-mcp");
            Assert.Contains(vm.McpRows, r => r.Label == "gitlab");

            // Negatively: nothing in the panel may read as "n of m", and the absence of a total is
            // stated rather than left for the reader to assume the list is complete.
            Assert.DoesNotContain(vm.McpRows, r => r.Value.Contains(" of "));
            Assert.Contains(vm.McpRows, r => r.Value.Contains("already connected"));

            // The badge is subject to the same rule: it says a connection is in progress, with no
            // arithmetic it cannot support.
            engine.Raise(Roster(false, Server("slow", false)));
            DrainDispatcher();
            Assert.Equal("connecting", vm.McpPendingLabel);
        });

        /// <summary>
        /// A backend that reports no MCP status of its own gets a SHORTER panel, not a zero. Claude Code
        /// never mentions MCP after <c>initialize</c> and ACP defines no status notification, so "0
        /// servers" would be a claim about the user's configuration that we have no evidence for.
        /// <para>The panel is still worth opening, which is the whole case for the affordance being
        /// always available: our own bridge needs no backend cooperation, so there is always something
        /// truthful to show.</para>
        /// </summary>
        [Fact]
        public void ABackendThatReportsNothingSaysSoRatherThanZero() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);

            engine.Raise(Bridge(connected: true, toolsServed: 9));
            DrainDispatcher();

            Assert.True(vm.CanShowMcpStatus);
            Assert.Equal("not reported by this backend", ValueFor(vm, "Agent MCP servers"));

            // No count of the user's servers appears anywhere — not as a zero, not as a total.
            Assert.DoesNotContain(vm.McpRows, r => r.Value.Contains("0 "));
            Assert.DoesNotContain(vm.McpRows, r => r.Value.Contains(" of "));

            // ...but our own half is fully reported, which is why the door is worth opening here.
            Assert.Equal("connected · 9 tools", ValueFor(vm, IdeMcpServer.Name));
        });

        /// <summary>
        /// An unconnected server is "not connected yet", never "failed". No failure state has ever been
        /// captured on either engine, so naming one would invent a diagnosis — and the difference
        /// matters: "failed" sends the user to fix a server that is merely slow, or waiting on an oauth
        /// sign-in it has told us about.
        /// </summary>
        [Fact]
        public void APendingServerIsNotDescribedAsFailed() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);

            engine.Raise(Roster(true, Server("aws-mcp", false, auth: "oauth"), Server("gitlab", true, tools: 4)));
            DrainDispatcher();

            var pending = vm.McpRows.Single(r => r.Label == "aws-mcp");
            Assert.Contains("not connected yet", pending.Value);
            Assert.Contains("oauth", pending.Value);
            Assert.DoesNotContain(vm.McpRows, r => r.Value.Contains("fail", StringComparison.OrdinalIgnoreCase));

            Assert.Equal("1 of 2 connected", ValueFor(vm, "Agent MCP servers"));
            Assert.Equal("1 of 2", vm.McpPendingLabel);
            Assert.True(vm.McpIsPending);
        });

        /// <summary>
        /// Our own bridge appears in the backend's own roster (Kiro v3 lists it beside the user's
        /// servers), so it must be pulled out of their section. Left in, it inflates
        /// every total and puts a server in the user's list that they never configured — and on a
        /// two-server setup that is the difference between "1 of 1" and "1 of 2 connected".
        /// </summary>
        [Fact]
        public void OurOwnBridgeIsNotCountedAmongTheUsersServers() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);

            engine.Raise(Roster(true, Server("aws-mcp", true, tools: 9), Server(IdeMcpServer.Name, true, tools: 16)));
            DrainDispatcher();

            Assert.Equal("1 of 1 connected", ValueFor(vm, "Agent MCP servers"));

            // ONCE, not never. Our own row is now named by the server name too, so "the name is
            // absent" stopped being a usable discriminator the moment the label changed — it could no
            // longer tell a leak into the agent's section from our own section naming itself, and it
            // failed on the honest row. A count still separates them: one occurrence is ours, two is
            // the leak this guards.
            Assert.Single(vm.McpRows, r => r.Label == IdeMcpServer.Name);
            Assert.False(vm.McpRows.Single(r => r.Label == IdeMcpServer.Name).IsHeading);

            // Every spelling the predicate accepts, because the split must not depend on which one the
            // backend happened to use — v3 normalizes server names to underscores.
            foreach (var spelling in new[] { IdeMcpServer.Name.Replace('-', '_') })
            {
                engine.Raise(Roster(true, Server("aws-mcp", true, tools: 9), Server(spelling, true, tools: 16)));
                DrainDispatcher();
                Assert.Equal("1 of 1 connected", ValueFor(vm, "Agent MCP servers"));
            }
        });

        /// <summary>
        /// A handshake with no <c>tools/list</c> is NOT zero tools. That distinction is the entire point
        /// of the two unconditional <c>engine.log</c> lines (issue #122 part 3) — the failure that has
        /// actually happened here is a client that connects and registers nothing — so the panel must
        /// not collapse it to "0 tools", which is the one rendering that would hide it.
        /// </summary>
        [Fact]
        public void AHandshakeWithoutAToolsListIsNotReportedAsZeroTools() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);

            engine.Raise(Bridge(connected: false, toolsServed: null));
            DrainDispatcher();
            Assert.Equal("not connected yet", ValueFor(vm, IdeMcpServer.Name));

            engine.Raise(Bridge(connected: true, toolsServed: null));
            DrainDispatcher();
            Assert.Equal("connected · tools not requested yet", ValueFor(vm, IdeMcpServer.Name));

            // A genuine zero is a different sentence, and it stays sayable.
            engine.Raise(Bridge(connected: true, toolsServed: 0));
            DrainDispatcher();
            Assert.Equal("connected · 0 tools", ValueFor(vm, IdeMcpServer.Name));
        });

        /// <summary>
        /// The affordance is gated on our bridge existing, not on the backend saying anything — and it
        /// must not latch. The engine starts the pipe host inside <c>StartSessionAsync</c> and the
        /// session is warm-started before the user types (issue #19), so a value computed once at
        /// session-open would be false for exactly the window this panel exists to cover.
        /// </summary>
        [Fact]
        public void TheAffordanceAppearsWhenTheHostComesUpRatherThanLatchingAtSessionOpen() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);

            Assert.False(vm.CanShowMcpStatus);

            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

            engine.Raise(Bridge(connected: false, toolsServed: null));
            DrainDispatcher();

            Assert.True(vm.CanShowMcpStatus);
            Assert.Contains(nameof(ChatViewModel.CanShowMcpStatus), raised);
        });

        /// <summary>
        /// Live state, but keyed on the BACKEND SESSION: starting one drops the previous session's
        /// roster, so a new connection never inherits the old one's servers.
        /// </summary>
        /// <remarks>
        /// <b>This test used to drive New Session and assert the roster was gone, and that was pinning
        /// a bug.</b> It read as the obvious sibling of the usage rule — live state, cleared when the
        /// conversation restarts — and the two are not the same: usage is re-reported every turn, while
        /// the roster is announced once per backend session. New Session very often REUSES the warm
        /// session (#19), and against a reused session the old assertion demanded that we forget facts
        /// that were still true and that nothing would repeat. Green the whole time; found in Visual Studio.
        /// <para>What is actually required is that starting a session clears it, which is what this
        /// drives now — and <see cref="ANewConversationOnAReusedSessionKeepsTheRoster"/> holds the
        /// other side, so neither can be satisfied by simply never clearing or always clearing.</para>
        /// </remarks>
        [Fact]
        public void StartingABackendSessionDropsThePreviousRoster() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);

            engine.Raise(Bridge(connected: true, toolsServed: 9));
            engine.Raise(Roster(true, Server("aws-mcp", true, tools: 9)));
            DrainDispatcher();
            Assert.True(vm.CanShowMcpStatus);

            vm.ClearMcpState();
            DrainDispatcher();

            Assert.False(vm.CanShowMcpStatus);
            Assert.False(vm.IsMcpOpen);
            Assert.Equal(string.Empty, vm.McpPendingLabel);
        });

        /// <summary>
        /// A server's tools are there to be revealed, and are not there until they are. Collapsed by
        /// default because the panel answers "what is connected" at a glance; the tools are the second
        /// question, and a roster that opened every server would bury the first answer under them.
        /// </summary>
        [Fact]
        public void AServersToolsAppearOnlyWhenItIsOpened() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);

            engine.Raise(Roster(true, Server("aws-mcp", true, tools: 3)));
            DrainDispatcher();

            var server = vm.McpRows.Single(r => r.Label == "aws-mcp");
            Assert.True(server.IsExpandable);
            Assert.False(server.IsExpanded);
            Assert.DoesNotContain(vm.McpRows, r => r.Label == "t1");

            vm.ToggleMcpRow(server);

            Assert.True(vm.McpRows.Single(r => r.Label == "aws-mcp").IsExpanded);
            var tool = vm.McpRows.Single(r => r.Label == "t1");
            Assert.Equal(1, tool.Depth);
            Assert.Equal(string.Empty, tool.Value);
            Assert.Equal(3, vm.McpRows.Count(r => r.Depth == 1));

            // And closes again, on the same gesture rather than a second one.
            vm.ToggleMcpRow(vm.McpRows.Single(r => r.Label == "aws-mcp"));
            Assert.DoesNotContain(vm.McpRows, r => r.Depth == 1);
        });

        /// <summary>
        /// <b>An open server stays open while the roster repeats.</b> This is the whole reason the
        /// expansion state lives on the view-model keyed by server name rather than on the row object:
        /// a v3 roster is a SNAPSHOT that arrives several times during a warm start, and every frame
        /// rebuilds the rows. State carried on a row would be discarded on the next frame, so the tree
        /// would shut itself while the user was reading it — and nothing about the first frame would
        /// show it, which is why this drives more than one.
        /// </summary>
        [Fact]
        public void AnOpenServerSurvivesTheSnapshotsThatRepeat() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);

            engine.Raise(Roster(true, Server("aws-mcp", true, tools: 2), Server("other", true, tools: 1)));
            DrainDispatcher();
            vm.ToggleMcpRow(vm.McpRows.Single(r => r.Label == "aws-mcp"));
            Assert.True(vm.McpRows.Single(r => r.Label == "aws-mcp").IsExpanded);

            // The same frame again, then one where the roster has genuinely changed underneath it.
            engine.Raise(Roster(true, Server("aws-mcp", true, tools: 2), Server("other", true, tools: 1)));
            DrainDispatcher();
            Assert.True(vm.McpRows.Single(r => r.Label == "aws-mcp").IsExpanded);

            engine.Raise(Roster(true, Server("aws-mcp", true, tools: 2), Server("other", false)));
            DrainDispatcher();
            Assert.True(vm.McpRows.Single(r => r.Label == "aws-mcp").IsExpanded);
            Assert.Contains(vm.McpRows, r => r.Label == "t1" && r.Depth == 1);

            // The one that was never opened is still shut, so "survives" is not "opens everything".
            Assert.False(vm.McpRows.Single(r => r.Label == "other").IsExpanded);
        });

        /// <summary>
        /// A server that reported a tool COUNT but no names has nothing to reveal, so it offers no
        /// chevron rather than one that opens onto an empty list — the roster's own "a null is not a
        /// zero" rule, in the one place where the user can act on it.
        /// </summary>
        [Fact]
        public void AServerThatNamedNoToolsOffersNothingToOpen() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);

            engine.Raise(Roster(true, new McpServerDto("quiet-mcp", true, "connected", null, 4, null)));
            DrainDispatcher();

            var row = vm.McpRows.Single(r => r.Label == "quiet-mcp");
            Assert.Equal("connected \u00b7 4 tools", row.Value);
            Assert.False(row.IsExpandable);

            // And the toggle refuses it, so a caller wiring the whole row cannot open the unopenable.
            vm.ToggleMcpRow(row);
            Assert.DoesNotContain(vm.McpRows, r => r.Depth == 1);
        });

        /// <summary>
        /// The hover summary says the same thing whether or not a server has been opened. A tooltip
        /// that answered differently depending on a gesture made inside the panel it previews would be
        /// reporting the reader's own state back at them, and one server with thirty tools would bury
        /// the summary the tooltip exists to give.
        /// </summary>
        [Fact]
        public void TheHoverSummaryIsUnchangedByOpeningAServer() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);

            engine.Raise(Roster(true, Server("aws-mcp", true, tools: 3)));
            DrainDispatcher();

            var before = vm.McpDetail;
            vm.ToggleMcpRow(vm.McpRows.Single(r => r.Label == "aws-mcp"));

            Assert.Equal(before, vm.McpDetail);
            Assert.DoesNotContain("t1", vm.McpDetail);
        });

        /// <summary>
        /// <b>A new conversation on a REUSED backend session keeps the roster.</b> Found in Visual Studio, and it
        /// is the whole reason the MCP clear hangs off the session-start boundary rather than off the
        /// transcript.
        /// </summary>
        /// <remarks>
        /// <c>StartNewSession</c> clears the transcript unconditionally and then calls
        /// <c>WarmStartSession</c>, which returns early and REUSES the warm session when the request is
        /// unchanged (#19). Clearing the roster there threw away facts about a session that was still
        /// live with exactly those servers connected — and nothing re-announces them, because Kiro
        /// sends its roster around <c>session/new</c> and our bridge publishes on handshake,
        /// <c>tools/list</c> and <c>tools/call</c>. The panel then reported "not reported by this
        /// backend" against a backend that had reported.
        /// <para>The assertion is deliberately on <see cref="ChatViewModel.CanShowMcpStatus"/> AND the
        /// rows: the button disappearing is what the user sees, and the panel's contents are what makes
        /// it a lie rather than merely a gap.</para>
        /// </remarks>
        [Fact]
        public void ANewConversationOnAReusedSessionKeepsTheRoster() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);

            engine.Raise(Bridge(true, 16));
            engine.Raise(Roster(true, Server("aws-mcp", true, tools: 8), Server("other", true, tools: 1)));
            DrainDispatcher();
            Assert.True(vm.CanShowMcpStatus);
            Assert.Equal("2 of 2 connected", ValueFor(vm, "Agent MCP servers"));

            // New Session. The engine stub starts nothing, which is exactly the reused-warm-session
            // case: the transcript is emptied and no session/new goes out.
            vm.NewSessionCommand.Execute(null);
            DrainDispatcher();

            Assert.True(vm.CanShowMcpStatus);
            Assert.Equal("2 of 2 connected", ValueFor(vm, "Agent MCP servers"));
            Assert.DoesNotContain(vm.McpRows, r => r.Value == "not reported by this backend");
        });

        // ---- helpers --------------------------------------------------------------------------

        private static string ValueFor(ChatViewModel vm, string label) =>
            vm.McpRows.Single(r => r.Label == label).Value;

        private static McpServerDto Server(string name, bool connected, string? auth = null, int? tools = null) =>
            new(name, connected, connected ? "connected" : "connecting", auth, tools,
                tools is { } n ? Enumerable.Range(1, n).Select(i => "t" + i).ToArray() : null);

        private static AgentEventDto Roster(bool complete, params McpServerDto[] servers) =>
            new() { Type = "mcpRoster", McpRoster = new McpRosterDto(servers, complete) };

        private static AgentEventDto Bridge(bool connected, int? toolsServed) =>
            new() { Type = "mcpBridge", McpBridge = new McpBridgeDto(connected, toolsServed, null, 0) };

        private static ChatViewModel NewViewModel(StubEngine engine) => new(
            engine,
            new StartSessionRequest("fake", null, AppContext.BaseDirectory, "Prompt", null));

        private static void DrainDispatcher() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        private static void RunSta(Action action) => StaTest.Run(action);

        /// <summary>Answers nothing: these tests drive the view-model from the event stream only.</summary>
        private sealed class StubEngine : IEngineConnection
        {
            public event Action<AgentEventDto>? AgentEvent;

            public event Action<ProviderModelsDto>? ProviderModelsRefreshed { add { } remove { } }

            public void Raise(AgentEventDto ev) => AgentEvent?.Invoke(ev);

            // No handshake, so no session to describe (issue #160). Null rather than an empty
            // response: the panel draws "no agent session open yet" from the absence, and a blank
            // response would instead claim a session whose every fact was missing.
            public Task<SessionInfoResponse?> SessionInfoAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<SessionInfoResponse?>(null);

            public Task<ListProvidersResponse> ListProvidersAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new ListProvidersResponse(new List<ProviderInfoDto>()));

            public Task<StartSessionResponse> StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("These tests never open a session.");

            public Task<PromptResponse> PromptAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("These tests never prompt.");

            public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<SteerResponse> SteerAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("These tests never steer.");

            public Task SetModelAsync(string modelId, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<ListBackendSessionsResponse> ListBackendSessionsAsync(
                ListBackendSessionsRequest request, CancellationToken cancellationToken = default)
                => throw new NotSupportedException("This stub lists no backend sessions.");

            public Task<TakeImportedHistoryResponse> TakeImportedHistoryAsync(
                TakeImportedHistoryRequest request, CancellationToken cancellationToken = default)
                => throw new NotSupportedException("This stub imports no history.");

            public Task<SummarizeResponse> SummarizeAsync(SummarizeRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("These tests never summarize.");
        }
    }
}
