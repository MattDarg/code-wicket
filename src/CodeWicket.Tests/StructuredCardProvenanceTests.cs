using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CodeWicket.Core;
using CodeWicket.Core.Ide;
using CodeWicket.Ipc;
using CodeWicket.Shell;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Who a structured payload CAME FROM, which is what decides whether it may draw a card.
    /// <para>
    /// The marker in <see cref="StructuredToolResult"/> routes a payload; it does not authenticate one.
    /// The text it matches is the result text of ANY tool, so an agent whose shell or MCP tool simply
    /// PRINTS <c>{"resultKind":"testRun"…}</c> used to be handed the host's own chrome — a green
    /// "N tests — all passed" card, or a breakpoint card — for a run that never happened. That card is
    /// the artifact the user reads when deciding whether to accept the agent's changes, so the forgery
    /// is worth more than the bytes it costs.
    /// </para>
    /// <para>
    /// The fix carries provenance beside the payload instead of inferring it from the payload:
    /// <c>ShellRpcTarget.ToolsInvokeAsync</c> — the one place a result is known to be ours, because our
    /// own tool catalog just returned it — stamps <c>HostAuthored</c>, and the view-model builds a card
    /// from that flag alone. Everything else about the delivery is unchanged: the agent's echo still
    /// declutters its own tool row, because a one-line summary of the agent's own bytes on the agent's
    /// own row is what that row always showed.
    /// </para>
    /// </summary>
    public sealed class StructuredCardProvenanceTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "cwkt-card-provenance-" + Guid.NewGuid().ToString("N"));

        public StructuredCardProvenanceTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        // --- the forgery ------------------------------------------------------------------------------

        /// <summary>
        /// The finding itself: a tool result the AGENT produced, wearing our marker, must not become a
        /// test-results card however convincing its contents are.
        /// </summary>
        [Fact]
        public void AnAgentsOwnTestRunPayloadDrawsNoCard() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);

            // An ordinary shell call - the agent chose the tool, the arguments and every byte of the
            // output. Only the first 22 characters of that output are doing the work here.
            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "t1", Title = "run_command", Kind = "execute" });
            engine.Raise(new AgentEventDto
            {
                Type = "toolDone", ToolCallId = "t1", Success = true, Message = TestRunPayload(),
                HostAuthored = false,
            });
            DrainDispatcher();

            Assert.Empty(vm.Items.OfType<TestRunResultItemViewModel>());
        });

        /// <summary>
        /// The same for the breakpoint card, which is gated at the same point for the same reason: it
        /// claims a change was made to the user's IDE, and the gutter is the only other place that shows.
        /// </summary>
        [Fact]
        public void AnAgentsOwnBreakpointPayloadDrawsNoCard() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);

            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "t1", Title = "run_command", Kind = "execute" });
            engine.Raise(new AgentEventDto
            {
                Type = "toolDone", ToolCallId = "t1", Success = true, Message = BreakpointsPayload(),
                HostAuthored = false,
            });
            DrainDispatcher();

            Assert.Empty(vm.Items.OfType<BreakpointCardItemViewModel>());
        });

        // --- and the control, without which the two above are satisfied by a card that never renders ---

        /// <summary>
        /// The host's own delivery still cards both kinds. A check that cannot separate the fix from a
        /// card path that has simply stopped working pins neither.
        /// </summary>
        [Fact]
        public void TheHostsOwnDeliveryStillDrawsBothCards() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);

            // The shell side-channel's shape exactly: the synthetic id, no tool row of its own, and the
            // flag stamped where the result was produced.
            engine.Raise(new AgentEventDto
            {
                Type = "toolDone", ToolCallId = "cwkt-testrun", Success = true, Message = TestRunPayload(),
                HostAuthored = true,
            });
            engine.Raise(new AgentEventDto
            {
                Type = "toolDone", ToolCallId = "cwkt-breakpoints", Success = true, Message = BreakpointsPayload(),
                HostAuthored = true,
            });
            DrainDispatcher();

            var testRun = Assert.Single(vm.Items.OfType<TestRunResultItemViewModel>());
            Assert.Equal(5, testRun.Total);
            var breakpoints = Assert.Single(vm.Items.OfType<BreakpointCardItemViewModel>());
            Assert.Single(breakpoints.Rows);
        });

        /// <summary>
        /// The gate sits AHEAD of the card dedupe, and this is why. A refused copy that had already
        /// claimed the run's key would delete the genuine card that follows it — turning a check on
        /// what the agent may DRAW into a way to erase what the host drew, which is the same lie by the
        /// other route.
        /// </summary>
        [Fact]
        public void AForgedCopyCannotConsumeTheRealRunsDedupeKey() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);

            // The agent gets in first, having guessed (or read back) the results file the real run uses.
            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "t1", Title = "run_command", Kind = "execute" });
            engine.Raise(new AgentEventDto
            {
                Type = "toolDone", ToolCallId = "t1", Success = true, Message = TestRunPayload(),
                HostAuthored = false,
            });
            engine.Raise(new AgentEventDto
            {
                Type = "toolDone", ToolCallId = "cwkt-testrun", Success = true, Message = TestRunPayload(),
                HostAuthored = true,
            });
            DrainDispatcher();

            Assert.Single(vm.Items.OfType<TestRunResultItemViewModel>());
        });

        // --- where the flag is set -------------------------------------------------------------------

        /// <summary>
        /// The one production site. The shell stamps the result its own tool catalog just returned, on
        /// the thread that asked for it — everything downstream is that one assertion being carried,
        /// never re-derived.
        /// </summary>
        [Fact]
        public async Task TheShellSideChannelStampsTheResultItProduced()
        {
            var delivered = new List<AgentEventDto>();
            var target = new ShellRpcTarget(
                new FakeIdeServices(new FixedToolCatalog(TestRunPayload())), ev => delivered.Add(ev), _ => { });

            await target.ToolsInvokeAsync(new InvokeToolRequest("run_tests", "{}"));

            var sent = Assert.Single(delivered);
            Assert.True(sent.HostAuthored);
        }

        // --- the logs that already exist --------------------------------------------------------------

        /// <summary>
        /// A conversation saved BEFORE the flag existed keeps its cards. Every genuine delivery already
        /// on disk carries no flag at all, and refusing those would take the clickable failure list and
        /// the breakpoint rows out of every restored conversation — and on a backend that never echoes
        /// MCP results, the whole run, since the card was its only representation. That is a legitimate
        /// input turned away, not the forgery the flag exists to stop.
        /// </summary>
        [Fact]
        public void AConversationSavedBeforeTheFlagKeepsItsCards() => RunSta(() =>
        {
            var store = new FileSessionStore(Path.Combine(_root, "legacy"));
            var workspace = Path.Combine(_root, "Solution");

            var session = new PersistedSession { WorkspaceRootPath = workspace, Title = "Older" };
            session.Log.Add(new TranscriptEntry { Role = "user", Text = "run the tests" });
            // Exactly what an older build wrote: no hostAuthored field on either delivery, so the two
            // are indistinguishable in the log and the run is deduped by its resultsFile as it was live.
            session.Log.Add(new TranscriptEntry
            {
                Role = "agent",
                Event = new AgentEventDto
                {
                    Type = "toolDone", ToolCallId = "cwkt-testrun", Success = true, Message = TestRunPayload(),
                },
            });
            session.Log.Add(new TranscriptEntry
            {
                Role = "agent",
                Event = new AgentEventDto { Type = "toolStart", ToolCallId = "t2", Title = "run_tests", Kind = "other" },
            });
            session.Log.Add(new TranscriptEntry
            {
                Role = "agent",
                Event = new AgentEventDto
                {
                    Type = "toolDone", ToolCallId = "t2", Success = true, Message = TestRunPayload(),
                },
            });
            store.Save(session);

            var vm = new ChatViewModel(
                new StubEngine(),
                new StartSessionRequest("kiro", null, workspace, "Prompt", null),
                sessionStore: store);
            vm.RestoreMostRecentSession();

            Assert.Single(vm.Items.OfType<TestRunResultItemViewModel>());
        });

        /// <summary>
        /// ...and the concession stops at absence. An entry recorded AFTER this shipped says whether it
        /// was ours, and one that says it was not is refused on replay exactly as it was live — or a
        /// forgery would simply wait for the conversation to be reopened.
        /// </summary>
        [Fact]
        public void AnEntryRecordedAsNotOursIsStillRefusedOnReplay() => RunSta(() =>
        {
            var store = new FileSessionStore(Path.Combine(_root, "current"));
            var workspace = Path.Combine(_root, "Solution2");

            var session = new PersistedSession { WorkspaceRootPath = workspace, Title = "Newer" };
            session.Log.Add(new TranscriptEntry { Role = "user", Text = "check the tests" });
            session.Log.Add(new TranscriptEntry
            {
                Role = "agent",
                Event = new AgentEventDto { Type = "toolStart", ToolCallId = "t1", Title = "run_command", Kind = "execute" },
            });
            session.Log.Add(new TranscriptEntry
            {
                Role = "agent",
                Event = new AgentEventDto
                {
                    Type = "toolDone", ToolCallId = "t1", Success = true, Message = TestRunPayload(),
                    HostAuthored = false,
                },
            });
            store.Save(session);

            var vm = new ChatViewModel(
                new StubEngine(),
                new StartSessionRequest("kiro", null, workspace, "Prompt", null),
                sessionStore: store);
            vm.RestoreMostRecentSession();

            Assert.Empty(vm.Items.OfType<TestRunResultItemViewModel>());
            // The row itself is untouched by any of this: it still summarizes its own output.
            var row = Assert.Single(vm.Items.OfType<ToolItemViewModel>());
            Assert.Contains("5 tests", row.OutputDetail ?? string.Empty, StringComparison.Ordinal);
        });

        // --- fixtures ---------------------------------------------------------------------------------

        /// <summary>A minimal run_tests payload, marker first, carrying the run's dedupe key.</summary>
        private static string TestRunPayload(string resultsFile = "C:\\ws\\TestResults\\run-1.trx") =>
            "{\"resultKind\":\"testRun\",\"succeeded\":true,\"total\":5,\"passed\":5,\"failed\":0,"
            + "\"skipped\":0,\"failures\":[],\"resultsFile\":\"" + resultsFile.Replace("\\", "\\\\") + "\"}";

        /// <summary>A minimal breakpoints payload, marker first, carrying its own dedupe key.</summary>
        private static string BreakpointsPayload(string requestId = "b7f1c0d2") =>
            "{\"resultKind\":\"breakpoints\",\"requestId\":\"" + requestId + "\",\"action\":\"set\","
            + "\"summary\":\"Set 1 breakpoint.\",\"set\":1,\"failed\":0,"
            + "\"breakpoints\":[{\"file\":\"C:\\\\ws\\\\A.cs\",\"line\":10,\"status\":\"set\"}]}";

        private static ChatViewModel NewViewModel(StubEngine engine) => new(
            engine,
            new StartSessionRequest("fake", null, AppContext.BaseDirectory, "Prompt", null));

        private static void DrainDispatcher() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        // One shared, GATED implementation - see StaTest. Two STA bodies from different test
        // classes used to run concurrently against process-global WPF and clipboard state.
        private static void RunSta(Action action) => StaTest.Run(action);

        private sealed class FixedToolCatalog : IToolCatalog
        {
            private readonly string _json;

            public FixedToolCatalog(string json) => _json = json;

            public IReadOnlyList<ToolDescriptor> Tools { get; } =
                new[] { new ToolDescriptor("run_tests", "runs the solution's tests", "{}") };

            public Task<ToolResult> InvokeAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default) =>
                Task.FromResult(new ToolResult(IsError: false, _json));
        }

        private sealed class FakeIdeServices : IIdeServices
        {
            public FakeIdeServices(IToolCatalog tools) => Tools = tools;
            public IWorkspaceContext Workspace => throw new NotSupportedException();
            public IEditApplier Edits => throw new NotSupportedException();
            public IToolCatalog Tools { get; }
            public IPermissionHandler Permissions => throw new NotSupportedException();
        }

        /// <summary>Drives the view-model from the event stream; never opens a backend session.</summary>
        private sealed class StubEngine : IEngineConnection
        {
            public event Action<AgentEventDto>? AgentEvent;

            public event Action<ProviderModelsDto>? ProviderModelsRefreshed { add { } remove { } }

            public void Raise(AgentEventDto ev) => AgentEvent?.Invoke(ev);

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
