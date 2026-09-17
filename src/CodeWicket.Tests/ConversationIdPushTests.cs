using System;
using System.Collections.Generic;
using System.IO;
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
    /// The host has to be told which conversation is running, so breakpoints the agent sets are tagged
    /// with it and <c>clear_breakpoints</c> can tell this conversation's from an earlier one's (#73).
    /// <para>
    /// The value is pushed from the setter of the conversation field rather than from each of the six
    /// places that assign it — restore, import, delete, a workspace change, New, and the lazy create on
    /// the first message. These tests drive the observable ends of that funnel, because the failure it
    /// prevents is silent: a stale id files breakpoints under the previous conversation, and clearing
    /// then declines to remove ones it did in fact set.
    /// </para>
    /// </summary>
    public sealed class ConversationIdPushTests : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "cwkt-convid-" + Guid.NewGuid().ToString("N"));

        private FileSessionStore Store => new FileSessionStore(_dir);

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch (IOException) { }
        }

        [Fact]
        public void TheFirstMessagePushesTheNewConversationsId()
        {
            RunSta(() =>
            {
                var pushed = new List<string?>();
                var engine = new StubEngine();
                var vm = NewViewModel(engine, pushed);

                Assert.Empty(pushed);

                vm.InputText = "why is Items empty?";
                vm.SendCommand.Execute(null);
                Drain();
                engine.CompleteTurn();
                Drain();

                // The conversation is created lazily on the first recorded message, through the same
                // setter — a compound assignment, which is exactly the case a per-call-site push forgets.
                Assert.NotEmpty(pushed);
                Assert.False(string.IsNullOrWhiteSpace(pushed[pushed.Count - 1]));
            });
        }

        /// <summary>
        /// New session drops the identity, and the host must hear about it. Left stale, the next
        /// breakpoints would be tagged with the conversation the user had just walked away from.
        /// </summary>
        [Fact]
        public void StartingANewSessionPushesNull()
        {
            RunSta(() =>
            {
                var pushed = new List<string?>();
                var engine = new StubEngine();
                var vm = NewViewModel(engine, pushed);

                vm.InputText = "first";
                vm.SendCommand.Execute(null);
                Drain();
                engine.CompleteTurn();
                Drain();
                var opened = pushed[pushed.Count - 1];
                Assert.False(string.IsNullOrWhiteSpace(opened));

                vm.NewSessionCommand.Execute(null);
                Drain();

                Assert.Null(pushed[pushed.Count - 1]);
            });
        }

        /// <summary>
        /// A second conversation must not inherit the first one's id, or "clear what THIS conversation
        /// set" would reach into the previous one's breakpoints.
        /// </summary>
        [Fact]
        public void ASecondConversationGetsItsOwnId()
        {
            RunSta(() =>
            {
                var pushed = new List<string?>();
                var engine = new StubEngine();
                var vm = NewViewModel(engine, pushed);

                vm.InputText = "first";
                vm.SendCommand.Execute(null);
                Drain();
                engine.CompleteTurn();
                Drain();
                var first = pushed[pushed.Count - 1];

                vm.NewSessionCommand.Execute(null);
                Drain();

                vm.InputText = "second";
                vm.SendCommand.Execute(null);
                Drain();
                engine.CompleteTurn();
                Drain();
                var second = pushed[pushed.Count - 1];

                Assert.False(string.IsNullOrWhiteSpace(first));
                Assert.False(string.IsNullOrWhiteSpace(second));
                Assert.NotEqual(first, second);
            });
        }

        // ---- helpers --------------------------------------------------------------------------

        private ChatViewModel NewViewModel(StubEngine engine, List<string?> pushed) =>
            new ChatViewModel(
                engine,
                new StartSessionRequest("fake", null, AppContext.BaseDirectory, "Prompt", null),
                sessionStore: Store,
                setConversationId: id => pushed.Add(id));

        private static void Drain() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        private static void RunSta(Action action) => StaTest.Run(action, withDispatcherContext: true);

        /// <summary>A turn that completes when the test says so, and nothing else.</summary>
        private sealed class StubEngine : IEngineConnection
        {
            private TaskCompletionSource<PromptResponse>? _turn;

            public event Action<AgentEventDto>? AgentEvent { add { } remove { } }

            public event Action<ProviderModelsDto>? ProviderModelsRefreshed { add { } remove { } }

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
                => Task.FromResult(new StartSessionResponse("c1"));

            public Task<PromptResponse> PromptAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
            {
                _turn = new TaskCompletionSource<PromptResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                return _turn.Task;
            }

            public Task CancelAsync(CancellationToken cancellationToken = default)
            {
                CompleteTurn();
                return Task.CompletedTask;
            }

            public Task<SteerResponse> SteerAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
            {
                CompleteTurn();
                return Task.FromResult(new SteerResponse(nameof(CodeWicket.Core.SteerOutcome.Injected)));
            }

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
