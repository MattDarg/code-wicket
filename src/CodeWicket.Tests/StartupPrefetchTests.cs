using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeWicket.Ipc;
using CodeWicket.Shell;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// <c>engine/listProviders</c> cost 2044 ms in a collected session against 11 ms for the very next
    /// call on the same connection, so the VS host issues it right after launching the engine and hands
    /// the in-flight task to <see cref="ChatViewModel.InitializeAsync"/> instead of paying for it in
    /// series after the chat view is built.
    ///
    /// Worth pinning because the regression is INVISIBLE: dropping the parameter and calling the
    /// engine here anyway still populates the pickers, still passes every other test, and still shows
    /// a working window — it just quietly puts the two seconds back, and moves the warm session start
    /// (issue #19) that same distance later. Nothing on screen says so.
    /// </summary>
    public sealed class StartupPrefetchTests
    {
        [Fact]
        public void A_prefetched_catalog_is_used_and_the_engine_is_not_asked_again()
        {
            RunSta(() =>
            {
                var engine = new CountingEngine(new ListProvidersResponse(new List<ProviderInfoDto>
                {
                    new("kiro", "Kiro", new List<ModelInfoDto>(), new List<string>()),
                }));

                // What the host does: ask before the view-model exists, hand over the running task.
                var prefetched = engine.ListProvidersAsync();
                Assert.Equal(1, engine.Calls);

                var vm = new ChatViewModel(engine, new StartSessionRequest("kiro", null, ".", "Prompt", null));
                vm.InitializeAsync(prefetched).GetAwaiter().GetResult();

                Assert.Equal(1, engine.Calls); // the second call is the whole cost this avoids
                Assert.Equal("kiro", Assert.Single(vm.Providers).Id);
            });
        }

        [Fact]
        public void Without_a_prefetch_the_view_model_still_asks_for_itself()
        {
            RunSta(() =>
            {
                var engine = new CountingEngine(new ListProvidersResponse(new List<ProviderInfoDto>
                {
                    new("kiro", "Kiro", new List<ModelInfoDto>(), new List<string>()),
                }));

                // The Desktop host and every other caller pass nothing — that path must keep working.
                var vm = new ChatViewModel(engine, new StartSessionRequest("kiro", null, ".", "Prompt", null));
                vm.InitializeAsync().GetAwaiter().GetResult();

                Assert.Equal(1, engine.Calls);
                Assert.Equal("kiro", Assert.Single(vm.Providers).Id);
            });
        }

        [Fact]
        public void A_prefetch_that_failed_is_reported_rather_than_thrown()
        {
            RunSta(() =>
            {
                var engine = new CountingEngine(new ListProvidersResponse(new List<ProviderInfoDto>()));

                // The host awaits the same task first (to close its timing phase) and swallows there,
                // so the view-model is the only thing that reports it. A faulted task awaited twice
                // rethrows both times — this pins that the second await still lands in the notice path
                // rather than escaping into the fire-and-forget that owns it.
                var failed = Task.FromException<ListProvidersResponse>(new InvalidOperationException("engine gone"));

                var vm = new ChatViewModel(engine, new StartSessionRequest("kiro", null, ".", "Prompt", null));
                vm.InitializeAsync(failed).GetAwaiter().GetResult();

                Assert.Equal(0, engine.Calls); // must not silently fall back and hide the failure
                var notice = Assert.Single(vm.Items.OfType<NoticeItemViewModel>());
                Assert.True(notice.IsError);
                Assert.Contains("engine gone", notice.Text);
            });
        }

        // ---- helpers ------------------------------------------------------------------------------

        private sealed class CountingEngine : IEngineConnection
        {
            private readonly ListProvidersResponse _response;

            public CountingEngine(ListProvidersResponse response) => _response = response;

            public int Calls { get; private set; }

            public event Action<AgentEventDto>? AgentEvent { add { } remove { } }

            public event Action<ProviderModelsDto>? ProviderModelsRefreshed { add { } remove { } }

            // A fake with no handshake reports no session, which the panel renders as
            // "no agent session open yet" rather than as absent facts (issue #160).
            public Task<SessionInfoResponse?> SessionInfoAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<SessionInfoResponse?>(null);

            public Task<ListProvidersResponse> ListProvidersAsync(CancellationToken cancellationToken = default)
            {
                Calls++;
                return Task.FromResult(_response);
            }

            public Task<StartSessionResponse> StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new StartSessionResponse("conv", WorkingDirectory: request.WorkspaceRootPath));

            public Task<PromptResponse> PromptAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("Initialize must not prompt.");

            public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<SteerResponse> SteerAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("Initialize must not steer.");

            public Task SetModelAsync(string modelId, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<ListBackendSessionsResponse> ListBackendSessionsAsync(
                ListBackendSessionsRequest request, CancellationToken cancellationToken = default)
                => throw new System.NotSupportedException("This stub lists no backend sessions.");

            public Task<TakeImportedHistoryResponse> TakeImportedHistoryAsync(
                TakeImportedHistoryRequest request, CancellationToken cancellationToken = default)
                => throw new System.NotSupportedException("This stub imports no history.");

            public Task<SummarizeResponse> SummarizeAsync(SummarizeRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("Initialize must not summarize.");
        }

        // One shared, GATED implementation - see StaTest. Two STA bodies from different test
        // classes used to run concurrently against process-global WPF and clipboard state.
        private static void RunSta(Action action) => StaTest.Run(action);
    }
}
