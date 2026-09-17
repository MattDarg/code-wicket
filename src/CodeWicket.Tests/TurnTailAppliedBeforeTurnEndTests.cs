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
    /// Issue #277, the host's half. The transport hands the view-model a prompt's response only after
    /// every event before it has been RAISED (<see cref="ShellHopOrderingTests"/>), but raised is not
    /// applied: applying is a dispatcher hop, and so is the continuation that ends the turn. Which of
    /// the two runs first is the dispatcher's decision — by priority, then by order — and the
    /// continuation's priority is whatever the code that began the send was running at, because a
    /// dispatcher operation flows its own priority into the <c>SynchronizationContext</c> its awaits
    /// capture. So an ordering that held by FIFO at Normal failed, silently, for a send begun inside a
    /// Send-priority <c>Invoke</c>. The view-model now applies its queued events itself, inline, before
    /// it acts on the response, and this pins that the order is the view-model's and not the
    /// dispatcher's.
    /// </summary>
    public class TurnTailAppliedBeforeTurnEndTests
    {
        /// <summary>
        /// The send is begun at Send priority ON PURPOSE: it is the one priority above Normal, so the
        /// turn-end continuation is posted ahead of the event's Normal-priority drain and the
        /// dispatcher's FIFO cannot save the order. Without the inline apply, the reply is applied after
        /// <c>IsBusy</c> has gone false and opens the out-of-turn window over a finished turn — the #267
        /// shape with an ordinary text frame in the error's place.
        /// </summary>
        [Fact]
        public void ATurnsLastEventIsAppliedBeforeTheTurnIsDeclaredOver() => RunSta(() =>
        {
            var log = new List<string>();
            var engine = new StubEngine();
            var vm = new ChatViewModel(
                engine,
                new StartSessionRequest("fake", null, AppContext.BaseDirectory, "Prompt", null),
                diagnosticLog: log.Add);
            vm.InputText = "hello";

            Dispatcher.CurrentDispatcher.Invoke(() => vm.SendCommand.Execute(null), DispatcherPriority.Send);
            Assert.True(vm.IsBusy, "the send did not reach the engine synchronously; the test's premise is gone");

            // The reply's last chunk and the turn's end, back-to-back from off the UI thread — the
            // order the transport now guarantees — while the UI thread is not pumping, so both posts
            // are queued before either runs.
            Task.Run(() =>
            {
                engine.Raise(new AgentEventDto { Type = "text", Text = "the whole reply" });
                engine.CompleteTurn();
            }).Wait();
            DrainDispatcher();

            Assert.False(vm.IsBusy);
            Assert.False(vm.IsAgentWorkingOutOfTurn, "the turn's own last event was read as work outliving the turn");
            Assert.DoesNotContain(log, l => l.Contains("[out-of-turn] opened"));
            var reply = Assert.Single(vm.Items.OfType<MessageItemViewModel>(), m => m.Role == MessageRole.Assistant);
            Assert.Equal("the whole reply", reply.Text);
        });

        /// <summary>
        /// The same guarantee on the failing path: a turn that ends in an error carries its last frames
        /// ahead of the error card, not beneath it.
        /// </summary>
        [Fact]
        public void ATurnsLastEventIsAppliedBeforeItsErrorCard() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = new ChatViewModel(
                engine,
                new StartSessionRequest("fake", null, AppContext.BaseDirectory, "Prompt", null));
            vm.InputText = "hello";

            Dispatcher.CurrentDispatcher.Invoke(() => vm.SendCommand.Execute(null), DispatcherPriority.Send);
            Assert.True(vm.IsBusy);

            Task.Run(() =>
            {
                engine.Raise(new AgentEventDto { Type = "text", Text = "partial reply" });
                engine.FailTurn(new InvalidOperationException("the engine fell over"));
            }).Wait();
            DrainDispatcher();

            Assert.False(vm.IsBusy);
            var items = vm.Items.ToList();
            var reply = items.FindIndex(i => i is MessageItemViewModel { Role: MessageRole.Assistant });
            var card = items.FindIndex(i => i is NoticeItemViewModel { Kind: NoticeKind.Error });
            Assert.True(reply >= 0, "the partial reply was not applied at all");
            Assert.True(card >= 0, "the error card was not shown");
            Assert.True(reply < card, "the turn's last text landed below its own error card");
        });

        private static void DrainDispatcher() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        // The dispatcher context is needed because the send awaits with ConfigureAwait(true) — see
        // OutOfTurnWorkingWindowTests.RunSta. The Send-priority Invoke in each case then REPLACES that
        // context for the duration of the callback with one carrying its own priority, which is the
        // point of the test.
        private static void RunSta(Action action) => StaTest.Run(action, withDispatcherContext: true);

        private sealed class StubEngine : IEngineConnection
        {
            private TaskCompletionSource<PromptResponse>? _turn;

            public event Action<AgentEventDto>? AgentEvent;

            public event Action<ProviderModelsDto>? ProviderModelsRefreshed { add { } remove { } }

            public void Raise(AgentEventDto ev) => AgentEvent?.Invoke(ev);

            public void CompleteTurn()
            {
                var turn = _turn;
                _turn = null;
                turn?.TrySetResult(new PromptResponse("end_turn"));
            }

            public void FailTurn(Exception ex)
            {
                var turn = _turn;
                _turn = null;
                turn?.TrySetException(ex);
            }

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
                => throw new NotSupportedException("This stub lists no backend sessions.");

            public Task<TakeImportedHistoryResponse> TakeImportedHistoryAsync(
                TakeImportedHistoryRequest request, CancellationToken cancellationToken = default)
                => throw new NotSupportedException("This stub imports no history.");

            public Task<SummarizeResponse> SummarizeAsync(SummarizeRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("These tests never summarize.");
        }
    }
}
