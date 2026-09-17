using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CodeWicket.Ipc;
using CodeWicket.Shell;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The out-of-turn working window — the estimate that keeps Stop, the working bar, the pickers and
    /// New Session honest while steered work runs with no turn of ours open.
    /// <para>
    /// What is pinned here is that the window can OPEN from the event stream, not merely be extended by
    /// it. A window that can only be extended — returning unless it is already open, closing on ten
    /// seconds of quiet — lets a single stretch of model thinking end the busy state for the rest of
    /// the steered work, and every tool call, edit and reply after it arrives beneath a pane insisting
    /// the agent is idle. Being able to re-open is also what makes the short post-reply window safe —
    /// guessing "that was the last word" then costs a blink instead of the remainder of the turn.
    /// </para>
    /// </summary>
    public class OutOfTurnWorkingWindowTests
    {
        /// <summary>
        /// An event arriving with no turn of ours running IS out-of-turn work, whether or not this
        /// session has ever steered — the same reasoning the ACP layer applies to routing. This also
        /// covers a backend answering session/prompt early (kirodotdev/Kiro#7724).
        /// <para>
        /// It is raised against a session that has been PROMPTED, and that is not scaffolding: the
        /// window's whole subject is work we are waiting on, so a session nobody has spoken to cannot
        /// be doing any. Written without the send, this and the roster case below asserted the correct
        /// outcome from a state that could not happen — until kiro-cli 2.21.1 made it happen, opening
        /// every session with a housekeeping tool call of its own (see the session-open case below).
        /// </para>
        /// </summary>
        [Fact]
        public void ATurnLessEventOpensTheWindow() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = Prompted(engine);

            Assert.False(vm.IsAgentWorkingOutOfTurn);

            engine.Raise(new AgentEventDto { Type = "text", Text = "working on it" });
            DrainDispatcher();

            Assert.True(vm.IsAgentWorkingOutOfTurn);
            Assert.True(vm.IsAgentWorking);
        });

        /// <summary>
        /// The regression that started this: once closed, the window has to be able to open again. Stop
        /// is used to close it because it is the one deterministic way to — the alternative is waiting out
        /// a real quiet timer, which would put a 15-second sleep in the suite to test a branch that has
        /// nothing to do with elapsed time.
        /// </summary>
        [Fact]
        public void TheWindowOpensAgainAfterItHasClosed() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = Prompted(engine);

            engine.Raise(new AgentEventDto { Type = "text", Text = "first" });
            DrainDispatcher();
            Assert.True(vm.IsAgentWorkingOutOfTurn);

            vm.StopCommand.Execute(null);
            DrainDispatcher();
            Assert.False(vm.IsAgentWorkingOutOfTurn);

            // The agent kept going regardless — which is exactly the case the window exists for.
            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "t1", Title = "Write" });
            DrainDispatcher();

            Assert.True(vm.IsAgentWorkingOutOfTurn);
        });

        /// <summary>
        /// Usage frames trail a finished reply (measured on the wire in issue #70), so counting them as
        /// work would hold the bar up after the agent had stopped — and would defeat the short window
        /// that follows reply text, since a usage frame lands right after it.
        /// <para>
        /// Raised on a PROMPTED session. On an unprompted one the "nothing before a prompt" guard
        /// returns first, and this passed with the frame exclusion deleted — it pinned nothing from the
        /// day that guard landed.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData("usage")]
        [InlineData("turnDone")]
        public void TelemetryDoesNotOpenTheWindow(string type) => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = Prompted(engine);

            engine.Raise(new AgentEventDto { Type = type });
            DrainDispatcher();

            Assert.False(vm.IsAgentWorkingOutOfTurn);
        });

        /// <summary>
        /// A turn that ends in an error has ENDED (issue #267). The error is that turn's own terminal
        /// frame — it has one emission site, inside the turn's channel — so it is never evidence of work
        /// outliving the turn, whichever side of the shell hop's race it lands on (#277).
        /// <para>
        /// This is the losing side of that race, the one the capture showed: the prompt's response has
        /// already cleared <c>IsBusy</c> when the error is applied. It opened the full 45-second window
        /// on a throttling failure already on screen — working bar, a Stop that stopped nothing, and the
        /// backend picker locked until it timed out.
        /// </para>
        /// </summary>
        [Fact]
        public void ATurnsTrailingErrorDoesNotOpenTheWindow() => RunSta(() =>
        {
            var log = new List<string>();
            var engine = new StubEngine();
            var vm = Prompted(engine, log.Add);

            engine.Raise(new AgentEventDto
            {
                Type = "error",
                Message = "The model you've selected is experiencing a high volume of traffic.",
            });
            DrainDispatcher();

            Assert.False(vm.IsAgentWorkingOutOfTurn);
            Assert.False(vm.IsAgentWorking);
            Assert.DoesNotContain(log, l => l.Contains("[out-of-turn] opened"));

            // Excluded from the WINDOW, not from the transcript: the error card is the whole report.
            var card = Assert.IsType<NoticeItemViewModel>(vm.Items.Last());
            Assert.Equal(NoticeKind.Error, card.Kind);
        });

        /// <summary>
        /// The session announcing itself is not the agent working. Measured on a Kiro v2 wire log: opening
        /// the chat window produced four <c>_kiro.dev/mcp/server_initialized</c> frames (two servers, twice
        /// over the warm start) and an empty sub-agent roster, all before the user had sent anything — and
        /// every one of them is turn-less, so the pane put up the working bar and offered Stop for a
        /// 45-second quiet window with nothing running and nothing to stop.
        /// </summary>
        [Fact]
        public void AConnectingMcpServerDoesNotOpenTheWindow() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);

            engine.Raise(new AgentEventDto { Type = "mcpServerConnected", Title = "code-wicket" });
            engine.Raise(new AgentEventDto { Type = "subagents", Subagents = new List<SubagentDto>() });

            // The roster and our own bridge's handshake are the same announcement one layer over
            // (issue #122): they arrive turn-less and BEFORE session/new returns, and a v3 session
            // sends the roster four times over that window. Without the exclusion this reinstates the
            // 45-second phantom exactly, on a pane the user has not yet typed into.
            engine.Raise(new AgentEventDto
            {
                Type = "mcpRoster",
                McpRoster = new McpRosterDto(
                    new[] { new McpServerDto("aws-mcp", false, "connecting", "oauth", null, null) },
                    NamesWholeConfiguredSet: true),
            });
            engine.Raise(new AgentEventDto
            {
                Type = "mcpBridge",
                McpBridge = new McpBridgeDto(true, 9, null, 0),
            });
            DrainDispatcher();

            Assert.False(vm.IsAgentWorkingOutOfTurn);
            Assert.False(vm.IsAgentWorking);
        });

        /// <summary>
        /// ...but a roster with sub-agents in it IS work, and the point of the exclusion is that it names
        /// the announcement rather than the frame. Kiro v2 runs each sub-agent as its own session, so this
        /// roster is the only thing reporting them while the orchestrating turn is not ours.
        /// </summary>
        [Fact]
        public void APopulatedSubagentRosterStillOpensTheWindow() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = Prompted(engine);

            engine.Raise(new AgentEventDto
            {
                Type = "subagents",
                Subagents = new List<SubagentDto>
                {
                    new("sub-1", "reviewer", "reviews the diff", "working", null, null),
                },
            });
            DrainDispatcher();

            Assert.True(vm.IsAgentWorkingOutOfTurn);
        });

        /// <summary>
        /// A session opening is not the agent working, however it announces itself — and a backend may
        /// announce it with a TOOL CALL, which is the strongest evidence of work there is.
        /// <para>
        /// Measured, not imagined: kiro-cli 2.21.1 opens every session, new or loaded, with a turn-less
        /// <c>fetch_cloud_config</c> ("Fetching your cloud config"). It put the working bar and a Stop
        /// button over a chat nobody had typed into, for the full 45-second quiet window. The frame
        /// exclusions above cannot reach it — a tool call is exactly what they are written to let
        /// through — so the discriminator is the STATE: this conversation has not been prompted, so
        /// nothing arriving on it can be work we are waiting on.
        /// </para>
        /// <para>
        /// It surfaced through the mid-turn workspace switch (issue #217), that being the one path
        /// where the warm start comes last; everywhere else a restore or a clear runs afterwards and
        /// closes the window on its way past, which is why a phantom present on every session open had
        /// never been seen.
        /// </para>
        /// </summary>
        [Fact]
        public void ASessionOpeningWithAToolCallDoesNotOpenTheWindow() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine); // warm-started: no prompt has ever been sent on it

            engine.Raise(new AgentEventDto
            {
                Type = "toolStart",
                ToolCallId = "85d6786c",
                Title = "Fetching your cloud config",
                Kind = "other",
            });
            engine.Raise(new AgentEventDto { Type = "toolDone", ToolCallId = "85d6786c" });
            DrainDispatcher();

            Assert.False(vm.IsAgentWorkingOutOfTurn);
            Assert.False(vm.IsAgentWorking);
        });

        /// <summary>
        /// An open tool call holds the window against silence — a build emits one <c>toolStart</c> and
        /// then nothing for minutes, and giving up on it is the failure that matters. This is the half
        /// that must keep working, and it is here so the two cases below it are not just an absence.
        /// </summary>
        [Fact]
        public void AnOpenCallHoldsTheWindowThroughSilence() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = Prompted(engine);
            vm.OutOfTurnWorkingWindow = TimeSpan.FromMilliseconds(20);

            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "build", Title = "Build" });
            Pump(TimeSpan.FromMilliseconds(200)); // many times the window, and no completion is coming

            Assert.True(vm.IsAgentWorkingOutOfTurn);
        });

        /// <summary>
        /// The window says what it did, because <c>acp.log</c> cannot.
        /// <para>
        /// That log records every frame in both directions and stamps them, and it was still not enough
        /// to answer "why did the bar stay up after every reply" — it shows what ARRIVED, never what the
        /// host made of it, so reconstructing the window from it means inferring both the mapping and
        /// the turn boundary. The 2026-09-07 capture could not distinguish "nothing armed the window"
        /// from "something did and I read the frame wrong", which is why that report is parked rather
        /// than fixed. The opening line names the frame, which is the fact that was missing.
        /// </para>
        /// <para>
        /// Asserted rather than eyeballed for the reason the repo already applies to refusal wording: an
        /// instrument nobody has read is not evidence. A window that will not close shows as an opening
        /// line with no closing one, so both halves have to be there.
        /// </para>
        /// </summary>
        [Fact]
        public void TheWindowNamesWhatOpenedItAndWhatItCostToClose() => RunSta(() =>
        {
            var log = new List<string>();
            var engine = new StubEngine();
            var vm = Prompted(engine, log.Add);

            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "call-7", Title = "Build" });
            DrainDispatcher();

            var opened = Assert.Single(log, l => l.Contains("[out-of-turn] opened"));
            Assert.Contains("toolStart", opened);   // which frame, not merely that one arrived
            Assert.Contains("call-7", opened);
            Assert.Contains("45", opened);          // and which of the three windows it is on

            vm.StopCommand.Execute(null);
            DrainDispatcher();

            var closed = Assert.Single(log, l => l.Contains("[out-of-turn] closed"));
            Assert.Contains("call(s) still open", closed);

            // One line per transition, not per event: an extension is not an episode.
            engine.Raise(new AgentEventDto { Type = "text", Text = "a" });
            engine.Raise(new AgentEventDto { Type = "text", Text = "b" });
            DrainDispatcher();
            Assert.Equal(2, log.Count(l => l.Contains("[out-of-turn] opened")));
        });

        // ---- helpers --------------------------------------------------------------------------

        /// <summary>
        /// Runs the dispatcher for <paramref name="duration"/>. The window is a timer, so a single drain
        /// would only prove nothing had happened YET — which is what a broken ceiling looks like too.
        /// </summary>
        private static void Pump(TimeSpan? duration = null)
        {
            if (duration is null)
            {
                DrainDispatcher();
                return;
            }
            var until = DateTime.UtcNow + duration.Value;
            do
            {
                DrainDispatcher();
                Thread.Sleep(5);
            }
            while (DateTime.UtcNow < until);
        }

        private static ChatViewModel NewViewModel(StubEngine engine, Action<string>? log = null) => new(
            engine,
            new StartSessionRequest("fake", null, AppContext.BaseDirectory, "Prompt", null),
            diagnosticLog: log);

        /// <summary>
        /// A view-model whose session has been prompted and whose turn has ended — the state every
        /// case about out-of-turn WORK has to start from, because work is only ever work we asked for.
        /// The turn is completed here so <c>IsBusy</c> is false: a live turn reports the agent as
        /// working by itself, which would make every assertion below true for the wrong reason.
        /// </summary>
        private static ChatViewModel Prompted(StubEngine engine, Action<string>? log = null)
        {
            var vm = NewViewModel(engine, log);
            vm.InputText = "hello";
            vm.SendCommand.Execute(null);
            DrainDispatcher();
            engine.CompleteTurn();
            DrainDispatcher();
            Assert.False(vm.IsBusy);
            return vm;
        }

        /// <summary>
        /// Pumps anything the view-model posted rather than ran inline, so an assertion never races a
        /// dispatched update.
        /// </summary>
        private static void DrainDispatcher() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        // One shared, GATED implementation - see StaTest. Two STA bodies from different test
        // classes used to run concurrently against process-global WPF and clipboard state.
        //
        // The dispatcher context is needed by Prompted: a send awaits with ConfigureAwait(true), so
        // without one its continuation resumes on the pool and DrainDispatcher never sees the turn end.
        // It was absent while nothing here opened a session, and its absence is a RACE rather than a
        // failure - two of the three cases using the helper passed anyway.
        private static void RunSta(Action action) => StaTest.Run(action, withDispatcherContext: true);

        /// <summary>
        /// Drives the view-model from the event stream, which is the one direction that needs no
        /// backend — plus the bare minimum to OPEN a session and end its turn, which the work cases
        /// need because "has this conversation been prompted?" is now part of the rule they pin.
        /// </summary>
        private sealed class StubEngine : IEngineConnection
        {
            private TaskCompletionSource<PromptResponse>? _turn;

            public event Action<AgentEventDto>? AgentEvent;

            public event Action<ProviderModelsDto>? ProviderModelsRefreshed { add { } remove { } }

            public void Raise(AgentEventDto ev) => AgentEvent?.Invoke(ev);

            /// <summary>Ends the turn the last prompt opened, so the pane is idle again.</summary>
            public void CompleteTurn()
            {
                var turn = _turn;
                _turn = null;
                turn?.TrySetResult(new PromptResponse("end_turn"));
            }

            // A fake with no handshake reports no session, which the panel renders as
            // "no agent session open yet" rather than as absent facts (issue #160).
            public Task<SessionInfoResponse?> SessionInfoAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<SessionInfoResponse?>(null);

            public Task<ListProvidersResponse> ListProvidersAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new ListProvidersResponse(new List<ProviderInfoDto>()));

            public Task<StartSessionResponse> StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new StartSessionResponse("fresh"));

            public Task<PromptResponse> PromptAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
            {
                _turn = new TaskCompletionSource<PromptResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                return _turn.Task;
            }

            public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<SteerResponse> SteerAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("These tests never steer.");

            public Task SetModelAsync(string modelId, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<ListBackendSessionsResponse> ListBackendSessionsAsync(
                ListBackendSessionsRequest request, CancellationToken cancellationToken = default)
                => throw new System.NotSupportedException("This stub lists no backend sessions.");

            public Task<TakeImportedHistoryResponse> TakeImportedHistoryAsync(
                TakeImportedHistoryRequest request, CancellationToken cancellationToken = default)
                => throw new System.NotSupportedException("This stub imports no history.");

            public Task<SummarizeResponse> SummarizeAsync(SummarizeRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("These tests never summarize.");
        }
    }
}
