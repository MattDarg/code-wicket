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
    /// A tool call that is still running when its turn ends must be settled by the host, because no
    /// backend settles it.
    /// <para>
    /// Measured across three (<c>Console claude-steer-boundary compare</c>, <c>… kiro</c>, <c>… kiro-v3</c>):
    /// a <c>session/cancel</c> landing on an in-flight call produces <c>TurnCompleted
    /// stopReason=cancelled</c> and <em>no completion for the call at all</em> on Claude's adapter and on
    /// kiro-cli's default engine — but the same kiro-cli build under <c>--agent-engine v3</c> settles it
    /// with its own reason. Whether the backend does it is per-ENGINE, so this is a fallback rather than
    /// a correction: it touches only rows still Running, and a backend that settles keeps its wording.
    /// Left unfilled the row sits on "Running" for the rest of the session while the agent tells the
    /// user nothing ran.
    /// </para>
    /// <para>
    /// This is also what lets the two mid-turn mechanisms look the same to the user: a steered call is
    /// failed explicitly by the backend (<c>AbortError: interrupt</c>), a cancelled one is failed here.
    /// </para>
    /// </summary>
    public class InterruptedToolRowTests
    {
        /// <summary>
        /// The headline: cancel mid-call and the row settles instead of spinning forever.
        /// </summary>
        [Fact]
        public void ACallStillRunningWhenTheTurnIsCancelledIsMarkedFinished() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);

            engine.Raise(new AgentEventDto
            {
                Type = "toolStart", ToolCallId = "t1", Title = "run_tests", Kind = "other",
            });
            DrainDispatcher();

            var row = vm.Items.OfType<ToolItemViewModel>().Single();
            Assert.Equal(ToolStatus.Running, row.Status);

            // The shape both backends actually send: the turn ends, the call never reports.
            engine.Raise(new AgentEventDto { Type = "turnDone", StopReason = "cancelled" });
            DrainDispatcher();

            Assert.NotEqual(ToolStatus.Running, row.Status);
            Assert.Contains("cancelled", row.ErrorDetail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        });

        /// <summary>
        /// A call that DID report keeps what the backend said. The sweep fills a gap; it must not
        /// overwrite an answer — a successful call whose turn then ends stays successful.
        /// </summary>
        [Fact]
        public void ACallThatAlreadyReportedIsLeftAlone() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);

            engine.Raise(new AgentEventDto
            {
                Type = "toolStart", ToolCallId = "t1", Title = "build_solution", Kind = "other",
            });
            engine.Raise(new AgentEventDto
            {
                Type = "toolDone", ToolCallId = "t1", Success = true, Message = "Build succeeded",
            });
            engine.Raise(new AgentEventDto { Type = "turnDone", StopReason = "cancelled" });
            DrainDispatcher();

            var row = vm.Items.OfType<ToolItemViewModel>().Single();
            Assert.Equal(ToolStatus.Success, row.Status);
            Assert.Null(row.ErrorDetail);
        });

        /// <summary>
        /// The steered case must not be mis-described. There the backend DOES settle the call — with its
        /// own reason — ~20ms before the turn closes, so the sweep finds nothing to do and the row keeps
        /// the backend's text rather than the host's generic wording.
        /// </summary>
        [Fact]
        public void ASteerAbortedCallKeepsTheBackendsOwnReason() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);

            engine.Raise(new AgentEventDto
            {
                Type = "toolStart", ToolCallId = "t1", Title = "slow_probe", Kind = "other",
            });
            engine.Raise(new AgentEventDto
            {
                Type = "toolDone", ToolCallId = "t1", Success = false,
                ErrorText = "MCP error -32001: AbortError: interrupt",
            });
            engine.Raise(new AgentEventDto { Type = "turnDone", StopReason = "end_turn" });
            DrainDispatcher();

            var row = vm.Items.OfType<ToolItemViewModel>().Single();
            Assert.Equal(ToolStatus.Failed, row.Status);
            Assert.Contains("AbortError", row.ErrorDetail ?? string.Empty, StringComparison.Ordinal);
        });

        /// <summary>
        /// An ordinary turn end settles a stuck row too, and says only what is known. Backends omit a
        /// completion for reasons other than cancelling, and a row nobody will ever close is a bug
        /// whatever ended the turn — but calling it "cancelled" then would be a claim we cannot make.
        /// </summary>
        [Fact]
        public void AnOrdinaryTurnEndSettlesWithoutClaimingACancel() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);

            engine.Raise(new AgentEventDto
            {
                Type = "toolStart", ToolCallId = "t1", Title = "get_diagnostics", Kind = "other",
            });
            engine.Raise(new AgentEventDto { Type = "turnDone", StopReason = "end_turn" });
            DrainDispatcher();

            var row = vm.Items.OfType<ToolItemViewModel>().Single();
            Assert.NotEqual(ToolStatus.Running, row.Status);
            Assert.DoesNotContain("cancelled", row.ErrorDetail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        });

        private static ChatViewModel NewViewModel(StubEngine engine) => new(
            engine,
            new StartSessionRequest("fake", null, AppContext.BaseDirectory, "Prompt", null));

        private static void DrainDispatcher() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        // One shared, GATED implementation - see StaTest. Two STA bodies from different test
        // classes used to run concurrently against process-global WPF and clipboard state.
        private static void RunSta(Action action) => StaTest.Run(action);

        /// <summary>Drives the view-model from the event stream; never opens a backend session.</summary>
        private sealed class StubEngine : IEngineConnection
        {
            public event Action<AgentEventDto>? AgentEvent;

            public event Action<ProviderModelsDto>? ProviderModelsRefreshed { add { } remove { } }

            public void Raise(AgentEventDto ev) => AgentEvent?.Invoke(ev);

            // A fake with no handshake reports no session, which the panel renders as
            // "no agent session open yet" rather than as absent facts (issue #160).
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
                => throw new System.NotSupportedException("This stub lists no backend sessions.");

            public Task<TakeImportedHistoryResponse> TakeImportedHistoryAsync(
                TakeImportedHistoryRequest request, CancellationToken cancellationToken = default)
                => throw new System.NotSupportedException("This stub imports no history.");

            public Task<SummarizeResponse> SummarizeAsync(SummarizeRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("These tests never summarize.");
        }
    }
}
