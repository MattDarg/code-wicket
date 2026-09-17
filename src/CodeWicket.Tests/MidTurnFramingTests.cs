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
    /// The short framing block sent ahead of a mid-turn message, telling the agent WHICH gesture
    /// delivered it.
    /// <para>
    /// This is text the user did not write, so what is pinned here is mostly restraint: that it says
    /// only what their own gesture said, that the two gestures say different things, that an ordinary
    /// turn ending adds nothing at all, and that the user's transcript and saved log keep their words
    /// and only their words.
    /// </para>
    /// <para>
    /// It exists because ACP has no field for "this is an aside, not a redirect" — Enter, Ctrl+Enter and
    /// Ctrl+Shift+Enter are three different intents that otherwise arrive as identical bare text, so the
    /// choice is lost. Measured live: with the framing, an agent asked mid-task answered the interjection
    /// AND resumed the work it had been interrupted from; the delivery path there was a cancel plus a
    /// prompt, which erases every trace that it was mid-task at all.
    /// </para>
    /// </summary>
    public class MidTurnFramingTests
    {
        [Fact]
        public void AnAsideTellsTheAgentToCarryOn() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsSteering = true };
            var vm = StartedSession(engine);
            RaiseToolStart(engine);

            vm.InputText = "what's 2+2?";
            vm.SteerCommand.Execute(null);          // Ctrl+Enter — the aside
            DrainDispatcher();
            engine.Raise(new AgentEventDto { Type = "toolDone", ToolCallId = "t1", Success = true });
            DrainDispatcher();

            var sent = Assert.Single(engine.Steers);
            Assert.Contains("<mid-turn-message>", sent, StringComparison.Ordinal);
            Assert.Contains("carry on", sent, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("what's 2+2?", sent, StringComparison.Ordinal);
        });

        [Fact]
        public void AnInterruptTellsTheAgentTheWorkWasCutShort() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsSteering = true };
            var vm = StartedSession(engine);
            RaiseToolStart(engine);

            vm.InputText = "stop, wrong branch";
            vm.SendCommand.Execute(null);
            DrainDispatcher();
            vm.SendPendingNowCommand.Execute(null);   // the red button
            DrainDispatcher();

            var sent = Assert.Single(engine.Steers);
            Assert.Contains("interrupted", sent, StringComparison.OrdinalIgnoreCase);
            // The half a model gets wrong on its own: measured, an agent told the user "nothing ran"
            // when our tool had in fact started, because the cancel left the call unreported.
            Assert.Contains("do not assume it never ran", sent, StringComparison.OrdinalIgnoreCase);
            // It IS told to carry on afterwards — an interrupt is still a steer, just an abrupt one, and
            // measured, a model given only "pick it up if it still makes sense" simply doesn't: it
            // answered the question and abandoned a second step it had never started. What must NOT
            // appear is the aside's claim about which button was pressed.
            // It restates the gesture and the facts our own transport destroyed, and stops there.
            // "Interrupted rather than stopped" is what the button meant, so the work is not abandoned;
            // what to DO about it is the model's call and both backends make it well. Prescribing
            // the decision ("continue the untouched steps without asking") would break the
            // rule this block is bounded by — the user's gesture never said it.
            Assert.Contains("rather than stopped", sent, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("your judgement", sent, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("without asking", sent, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("chose to wait", sent, StringComparison.OrdinalIgnoreCase);
        });

        /// <summary>
        /// An interrupt on a backend that cannot steer must still be reported as an interrupt.
        /// <para>
        /// It very nearly wasn't. The interrupt is delivered by cancelling the turn, and that cancel
        /// aborts the running tool call — whose completion fires the boundary release, which sets the
        /// deferred delivery to "aside". So the gesture overwrote itself on the way out, and the agent
        /// was told the user had chosen to wait when they had chosen not to. Caught in Visual Studio comparing
        /// the two backends' wire logs for one gesture, not by any check here.
        /// </para>
        /// </summary>
        [Fact]
        public void AnInterruptSurvivesItsOwnCancelOnABackendWithoutSteering() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsSteering = false, CancelCompletesTurn = false };
            var vm = StartedSession(engine);
            RaiseToolStart(engine);

            // Steer mode, so the boundary release is armed and will race the interrupt's cancel.
            vm.InputText = "queued first";
            vm.SteerCommand.Execute(null);
            DrainDispatcher();

            vm.SendPendingNowCommand.Execute(null);   // the red button
            DrainDispatcher();
            // The cancel kills the call in flight; its completion is what used to downgrade the gesture.
            engine.Raise(new AgentEventDto { Type = "toolDone", ToolCallId = "t1", Success = false });
            DrainDispatcher();
            engine.CompleteTurn();
            DrainUntil(() => engine.Prompts.Count > 0);

            var sent = Assert.Single(engine.Prompts);
            Assert.Contains("interrupted", sent, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("chose to wait", sent, StringComparison.OrdinalIgnoreCase);

            // ...and exactly ONE cancel for one press. The aborted call's own completion re-enters the
            // release, which used to issue a second — harmless on the wire, but a v3 log showed it and a
            // gesture that cancels twice is a gesture we do not understand.
            Assert.Equal(1, engine.Cancels);
        });

        /// <summary>
        /// The framing goes on the wire and nowhere else. If it reached the transcript or the saved log
        /// it would come back on a resume as something the user appears to have written — words in their
        /// mouth, permanently, and indistinguishable from their own.
        /// </summary>
        [Fact]
        public void TheUsersTranscriptKeepsOnlyTheirOwnWords() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsSteering = true };
            var vm = StartedSession(engine);
            RaiseToolStart(engine);

            vm.InputText = "what's 2+2?";
            vm.SteerCommand.Execute(null);
            DrainDispatcher();
            engine.Raise(new AgentEventDto { Type = "toolDone", ToolCallId = "t1", Success = true });
            DrainDispatcher();

            var shown = vm.Items.OfType<MessageItemViewModel>().Last(m => m.IsUser);
            Assert.Equal("what's 2+2?", shown.Text);
            Assert.DoesNotContain("<mid-turn-message>", shown.Text, StringComparison.Ordinal);

            // But it is not a secret either: the bubble carries a note saying what the agent was told.
            Assert.NotNull(shown.DeliveryNote);
            Assert.Contains("safe point", shown.DeliveryNote!, StringComparison.OrdinalIgnoreCase);
        });

        private static void RaiseToolStart(StubEngine engine)
        {
            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "t1", Title = "run_tests" });
            DrainDispatcher();
        }

        private static ChatViewModel StartedSession(StubEngine engine)
        {
            var vm = new ChatViewModel(
                engine, new StartSessionRequest("fake", null, AppContext.BaseDirectory, "Prompt", null));
            vm.InputText = "start working";
            vm.SendCommand.Execute(null);
            DrainDispatcher();
            Assert.True(vm.IsBusy);
            engine.Prompts.Clear();
            return vm;
        }

        private static void DrainDispatcher() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        /// <summary>
        /// Drains until the awaited turn's continuation has actually landed. The stub completes its turn
        /// with <c>RunContinuationsAsynchronously</c>, so resolving the task hands off to the pool and the
        /// dispatcher post can arrive after a single drain has already returned.
        /// </summary>
        private static void DrainUntil(Func<bool> done)
        {
            for (var i = 0; i < 200 && !done(); i++)
            {
                DrainDispatcher();
                Thread.Sleep(5);
            }

            DrainDispatcher();
        }


        // One shared, GATED implementation - see StaTest. Two STA bodies from different test
        // classes used to run concurrently against process-global WPF and clipboard state.
        private static void RunSta(Action action) => StaTest.Run(action);

        private sealed class StubEngine : IEngineConnection
        {
            private TaskCompletionSource<PromptResponse>? _turn;

            public event Action<AgentEventDto>? AgentEvent;

            public event Action<ProviderModelsDto>? ProviderModelsRefreshed { add { } remove { } }

            public bool SupportsSteering { get; set; }

            public List<string> Prompts { get; } = new();

            public List<string> Steers { get; } = new();

            public void Raise(AgentEventDto ev) => AgentEvent?.Invoke(ev);

            public void CompleteTurn() => _turn?.TrySetResult(new PromptResponse("end_turn"));

            // A fake with no handshake reports no session, which the panel renders as
            // "no agent session open yet" rather than as absent facts (issue #160).
            public Task<SessionInfoResponse?> SessionInfoAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<SessionInfoResponse?>(null);

            public Task<ListProvidersResponse> ListProvidersAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new ListProvidersResponse(new List<ProviderInfoDto>()));

            public Task<StartSessionResponse> StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new StartSessionResponse("conv", SupportsSteering: SupportsSteering));

            public Task<PromptResponse> PromptAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
            {
                Prompts.Add(text);
                _turn = new TaskCompletionSource<PromptResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                return _turn.Task;
            }

            public int Cancels { get; private set; }

            /// <summary>
            /// False models the real ordering: session/cancel is a notification, the aborted call
            /// reports its failure, and the turn ends after that. With the turn ending inside the
            /// cancel there is no window for the completion to arrive in — which is exactly the window
            /// the duplicate-cancel bug lived in, so a stub that closes it cannot see the bug.
            /// </summary>
            public bool CancelCompletesTurn { get; set; } = true;

            public Task CancelAsync(CancellationToken cancellationToken = default)
            {
                Cancels++;
                if (CancelCompletesTurn)
                    CompleteTurn();
                return Task.CompletedTask;
            }

            public Task<SteerResponse> SteerAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
            {
                Steers.Add(text);
                return Task.FromResult(new SteerResponse("injected"));
            }

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
