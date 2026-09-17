using System;
using System.Collections.Generic;
using System.IO;
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
    /// Issue #299, the pane's half: a dead engine was two red lines of StreamJsonRpc — "Could not load
    /// providers: The JSON-RPC connection … lost" on load, "Engine error: …" on every send — with
    /// nothing on screen saying ".NET". The exit's own account now REPLACES those, rather than being
    /// added beside them, and carries .NET's words and the log path in the details.
    /// </summary>
    public sealed class EngineExitNoticeTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "cwkt-engine-exit-notice-" + Guid.NewGuid().ToString("N"));

        private string LogFile => Path.Combine(_root, "logs", "engine.log");

        private static readonly EngineExit MissingRuntime = new EngineExit(
            EngineExitDescription.FrameworkMissingFailure,
            new[] { "You must install or update .NET to run this application.", "https://aka.ms/dotnet-core-applaunch?framework=Microsoft.NETCore.App" });

        public EngineExitNoticeTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        [Fact]
        public void AnEngineThatNeverStartedIsNamedOnLoad_InPlaceOfTheLostConnection() => RunSta(() =>
        {
            var engine = new DeadEngine { ListFails = new EngineExitedException(MissingRuntime) };
            var vm = NewViewModel(engine);

            vm.InitializeAsync().GetAwaiter().GetResult();

            var notice = Assert.Single(vm.Items.OfType<NoticeItemViewModel>());
            Assert.True(notice.IsError);
            Assert.Equal(EngineExitDescription.Text(MissingRuntime, LogFile), notice.Text);
            Assert.DoesNotContain("Could not load providers", notice.Text);
            Assert.True(notice.HasDetails);
            Assert.Contains("https://aka.ms/dotnet-core-applaunch?framework=Microsoft.NETCore.App", notice.Details!);
            Assert.Contains("Engine log: " + LogFile, notice.Details!);
        });

        /// <summary>Anything that is not an exit is reported exactly as it was.</summary>
        [Fact]
        public void AnyOtherLoadFailureKeepsItsPrefix() => RunSta(() =>
        {
            var vm = NewViewModel(new DeadEngine { ListFails = new InvalidOperationException("boom") });

            vm.InitializeAsync().GetAwaiter().GetResult();

            var notice = Assert.Single(vm.Items.OfType<NoticeItemViewModel>());
            Assert.Equal("Could not load providers: boom", notice.Text);
            Assert.False(notice.HasDetails);
        });

        [Fact]
        public void ASendToAnExitedEngineSaysSo_NotEngineError() => RunSta(() =>
        {
            var engine = new DeadEngine { PromptFails = new EngineExitedException(MissingRuntime) };
            var vm = NewViewModel(engine);
            vm.InitializeAsync().GetAwaiter().GetResult();

            vm.InputText = "hello";
            vm.SendCommand.Execute(null);
            Pump();

            var notice = Assert.Single(vm.Items.OfType<NoticeItemViewModel>(), n => n.IsError);
            Assert.Equal(EngineExitDescription.Text(MissingRuntime, LogFile), notice.Text);
            Assert.DoesNotContain(vm.Items.OfType<NoticeItemViewModel>(), n => n.Text.Contains("Engine error"));
            Assert.False(vm.IsBusy);
        });

        private ChatViewModel NewViewModel(DeadEngine engine) => new ChatViewModel(
            engine,
            new StartSessionRequest("fake", null, _root, "Prompt", null),
            sessionStore: new FileSessionStore(Path.Combine(_root, "sessions")),
            sessionEnvironment: () => new SessionEnvironment(
                Path.Combine(_root, "logs"), LogFile,
                Path.Combine(_root, "logs", "acp.log"),
                Path.Combine(_root, "logs", "engine-channel.log"),
                Path.Combine(_root, "logs", "render.log"),
                acpLogEnabled: false, engineChannelLogEnabled: false, renderLogEnabled: false,
                kiroAgentEngine: null));

        private static void Pump()
        {
            var until = DateTime.UtcNow + TimeSpan.FromMilliseconds(200);
            do
            {
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
                Thread.Sleep(5);
            }
            while (DateTime.UtcNow < until);
        }

        private static void RunSta(Action action) => StaTest.Run(action, withDispatcherContext: true);

        private sealed class DeadEngine : IEngineConnection
        {
            public Exception? ListFails { get; set; }

            public Exception? PromptFails { get; set; }

            public event Action<AgentEventDto>? AgentEvent { add { } remove { } }

            public event Action<ProviderModelsDto>? ProviderModelsRefreshed { add { } remove { } }

            public Task<SessionInfoResponse?> SessionInfoAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<SessionInfoResponse?>(null);

            public Task<ListProvidersResponse> ListProvidersAsync(CancellationToken cancellationToken = default)
                => ListFails is not null
                    ? Task.FromException<ListProvidersResponse>(ListFails)
                    : Task.FromResult(new ListProvidersResponse(new List<ProviderInfoDto>
                    {
                        new ProviderInfoDto("fake", "Fake", new List<ModelInfoDto>(), new List<string>()),
                    }));

            public Task<StartSessionResponse> StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new StartSessionResponse("fresh"));

            public Task<PromptResponse> PromptAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
                => PromptFails is not null
                    ? Task.FromException<PromptResponse>(PromptFails)
                    : throw new InvalidOperationException("This stub only fails prompts.");

            public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<SteerResponse> SteerAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();

            public Task SetModelAsync(string modelId, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<ListBackendSessionsResponse> ListBackendSessionsAsync(
                ListBackendSessionsRequest request, CancellationToken cancellationToken = default)
                => throw new NotSupportedException("This stub lists no backend sessions.");

            public Task<TakeImportedHistoryResponse> TakeImportedHistoryAsync(
                TakeImportedHistoryRequest request, CancellationToken cancellationToken = default)
                => throw new NotSupportedException("This stub imports no history.");

            public Task<SummarizeResponse> SummarizeAsync(SummarizeRequest request, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();
        }
    }
}
