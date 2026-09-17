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
    /// The first prompt waits for the backend to have taken our IDE tools, and says so when it could
    /// not (issue #19's other half, measured 2026-09-11 with <c>Console mcp-ready</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kiro v3 returns <c>session/new</c> and connects its MCP servers afterwards — our bridge served
    /// <c>tools/list</c> 70–85 ms after the start returned — so a prompt sent at once can reach an
    /// agent that has none of our tools. Claude's adapter connects them BEFORE the start returns, and
    /// never announces an MCP server at all, which is why the gate is on OUR served signal and not on
    /// anything the backend says.
    /// </para>
    /// </remarks>
    public sealed class IdeToolsGateTests
    {
        [Fact]
        public void TheFirstPromptWaitsForTheBridgeToServeTheTools() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine, mcp: true);

            vm.InputText = "what tools do you have?";
            vm.SendCommand.Execute(null);
            Drain();

            Assert.Empty(engine.Prompts); // held: the session opened, the tools have not been served

            engine.Raise(Bridge(toolsServed: 17));
            // The release crosses a thread-pool hop (Task.WhenAny) before it reaches the dispatcher,
            // so a single drain can run ahead of it; pump, bounded.
            PumpUntil(() => engine.Prompts.Count == 1, TimeSpan.FromSeconds(3));

            Assert.Single(engine.Prompts);
            Assert.DoesNotContain(vm.Items.OfType<NoticeItemViewModel>(), n => n.Text == ChatViewModel.IdeToolsNotConfirmedNotice);
        });

        [Fact]
        public void AWarmSessionThatAlreadyServedIsNotWaitedOn() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine, mcp: true);
            vm.WarmStartSession();
            Drain();
            engine.Raise(Bridge(toolsServed: 17)); // the warm session's bridge came up while the user typed
            Drain();

            vm.InputText = "hello";
            vm.SendCommand.Execute(null);
            PumpUntil(() => engine.Prompts.Count == 1, TimeSpan.FromSeconds(3));

            Assert.Single(engine.Prompts);
        });

        /// <summary>A backend that takes no bridge would otherwise wait the whole timeout for a
        /// signal that cannot come — on every first send.</summary>
        [Fact]
        public void ABackendWithoutTheBridgeIsNotWaitedOn() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine, mcp: false);

            vm.InputText = "hello";
            vm.SendCommand.Execute(null);
            Drain();

            Assert.Single(engine.Prompts);
        });

        /// <summary>The timeout sends anyway and SAYS so: silence is the failure this replaces.</summary>
        [Fact]
        public void OnTimeoutTheMessageGoesWithANoticeAboveIt() => RunSta(() =>
        {
            var engine = new StubEngine();
            var log = new List<string>();
            var vm = NewViewModel(engine, mcp: true, log: log.Add);
            vm.IdeToolsWait = TimeSpan.FromMilliseconds(50);

            vm.InputText = "hello";
            vm.SendCommand.Execute(null);
            PumpUntil(() => engine.Prompts.Count == 1, TimeSpan.FromSeconds(3));

            Assert.Single(engine.Prompts);
            var notice = vm.Items.OfType<NoticeItemViewModel>().Single(n => n.Text == ChatViewModel.IdeToolsNotConfirmedNotice);
            var user = vm.Items.OfType<MessageItemViewModel>().Single(m => m.Role == MessageRole.User);
            Assert.True(vm.Items.IndexOf(notice) < vm.Items.IndexOf(user), "the notice sits above the message it is about");
            Assert.Contains(log, l => l.Contains("not confirmed", StringComparison.Ordinal));
        });

        // ---- helpers ------------------------------------------------------------------------------

        private static AgentEventDto Bridge(int toolsServed) => new()
        {
            Type = "mcpBridge",
            McpBridge = new McpBridgeDto(Connected: true, ToolsServed: toolsServed, ToolNames: null, ToolCalls: 0),
        };

        private static ChatViewModel NewViewModel(StubEngine engine, bool mcp, Action<string>? log = null)
        {
            var vm = new ChatViewModel(
                engine,
                new StartSessionRequest("kiro", null, AppContext.BaseDirectory, "Prompt", null),
                diagnosticLog: log);
            var caps = new List<string> { "ResumeSession" };
            if (mcp) caps.Add("Mcp");
            var init = vm.InitializeAsync(Task.FromResult(new ListProvidersResponse(new List<ProviderInfoDto>
            {
                new("kiro", "Kiro", new List<ModelInfoDto> { new("auto", "auto") }, caps),
            })));
            Drain();
            Assert.True(init.IsCompletedSuccessfully);
            return vm;
        }

        private static void Drain() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        private static void PumpUntil(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!condition() && DateTime.UtcNow < deadline)
            {
                Drain();
                Thread.Sleep(10);
            }
        }

        private static void RunSta(Action action) => StaTest.Run(action, withDispatcherContext: true);

        private sealed class StubEngine : IEngineConnection
        {
            public event Action<AgentEventDto>? AgentEvent;

            public event Action<ProviderModelsDto>? ProviderModelsRefreshed { add { } remove { } }

            public List<string> Prompts { get; } = new();

            public void Raise(AgentEventDto ev) => AgentEvent?.Invoke(ev);

            public Task<SessionInfoResponse?> SessionInfoAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<SessionInfoResponse?>(null);

            public Task<ListProvidersResponse> ListProvidersAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new ListProvidersResponse(new List<ProviderInfoDto>()));

            public Task<StartSessionResponse> StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new StartSessionResponse("c1"));

            public Task<PromptResponse> PromptAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
            {
                Prompts.Add(text);
                return new TaskCompletionSource<PromptResponse>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
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
