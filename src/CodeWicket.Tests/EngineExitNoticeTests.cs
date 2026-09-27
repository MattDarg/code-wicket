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

        /// <summary>
        /// EngineGone: an exit with nothing in flight is named when it HAPPENS, not when the
        /// user next tries something. Once, however many routes report it.
        /// </summary>
        [Fact]
        public void AnExitWithNothingInFlightIsNamedOnceWhenItHappens() => RunSta(() =>
        {
            var engine = new DeadEngine();
            var vm = NewViewModel(engine);
            vm.InitializeAsync().GetAwaiter().GetResult();
            Pump();

            engine.RaiseExit(MissingRuntime);
            Pump();
            engine.RaiseExit(MissingRuntime);
            Pump();

            var notice = Assert.Single(vm.Items.OfType<NoticeItemViewModel>(), n => n.IsError);
            Assert.Equal(EngineExitDescription.Text(MissingRuntime, LogFile), notice.Text);
            Assert.Contains("Engine log: " + LogFile, notice.Details!);
        });

        /// <summary>
        /// After the exit, nothing that would start a session asks the dead engine for one: New and a workspace move
        /// each warm-start, and each warm start went into the dead engine and failed.
        /// </summary>
        [Fact]
        public void AfterTheExitNewAndAWorkspaceMoveStartNoSession() => RunSta(() =>
        {
            var engine = new DeadEngine();
            var vm = NewViewModel(engine);
            vm.InitializeAsync().GetAwaiter().GetResult();
            Pump();
            engine.RaiseExit(MissingRuntime);
            Pump();
            var starts = engine.Starts;

            vm.NewSessionCommand.Execute(null);
            Pump();
            var moved = Path.Combine(_root, "moved");
            Directory.CreateDirectory(moved);
            vm.UpdateWorkspaceRoot(moved, notice: null);
            Pump();

            Assert.Equal(starts, engine.Starts);
        });

        /// <summary>
        /// User decision (2026-09-15): a send after the exit reaches no engine, gives the message back to the
        /// composer, records nothing, and names the exit again, so pressing Enter is answered where the user is
        /// looking. The exit's own notice is still said once; this one answers the send.
        /// </summary>
        /// <remarks>On a pane that has already prompted, so the send needs no session start: the lifetime's
        /// refusal to start one cannot answer it, and the send would otherwise go straight to its prompt.</remarks>
        [Fact]
        public void ASendAfterTheExitGivesTheMessageBackAndNamesTheExitAgain() => RunSta(() =>
        {
            var engine = new DeadEngine();
            var vm = NewViewModel(engine);
            vm.InitializeAsync().GetAwaiter().GetResult();
            Pump();
            vm.InputText = "first";
            vm.SendCommand.Execute(null);
            Pump();
            Assert.Equal(1, engine.Prompts);

            engine.RaiseExit(MissingRuntime);
            Pump();
            var starts = engine.Starts;

            vm.InputText = "hello";
            vm.SendCommand.Execute(null);
            Pump();

            Assert.Equal(starts, engine.Starts);
            Assert.Equal(1, engine.Prompts);
            Assert.Equal("hello", vm.InputText);
            Assert.DoesNotContain(vm.Items.OfType<MessageItemViewModel>(), m => m.Role == MessageRole.User && m.Text == "hello");
            Assert.Equal(2, vm.Items.OfType<NoticeItemViewModel>()
                .Count(n => n.Text == EngineExitDescription.Text(MissingRuntime, LogFile)));
            Assert.False(vm.IsBusy);
            Assert.DoesNotContain(SavedConversations(), file => File.ReadAllText(file).Contains("hello"));
        });

        /// <summary>
        /// The exit that ends a send PARKED on a banner, which was the one in-app ending that kept the
        /// message. It never reached an agent, so it comes back whole - text, image and
        /// capture - exactly as a workspace move's retirement now hands it back. The banner goes
        /// with it: its buttons would answer a send that no longer exists.
        /// </summary>
        [Fact]
        public void AnExitWhileASendIsParkedGivesTheMessageBack() => RunSta(() =>
        {
            using var s = PendingSendScenario.Reach(PendingPhase.AskingRefused, withChips: true);
            var vm = s.Vm;

            // Reported from a pool thread, as the engine's own report is, so the hop is pumped rather
            // than assumed.
            s.Engine.RaiseExit(MissingRuntime);
            PendingSendScenario.PumpUntil(
                () => !vm.HasPendingSendForDiagnostics, TimeSpan.FromSeconds(5));

            Assert.False(vm.HasPendingSendForDiagnostics);
            Assert.Null(vm.PendingResume);
            Assert.Equal(PendingSendScenario.Message, vm.InputText);
            Assert.Equal(PendingSendScenario.ImageName, Assert.Single(vm.PendingAttachments).Name);
            Assert.Equal(PendingSendScenario.ContextLabel, Assert.Single(vm.PendingContexts).Label);
            Assert.DoesNotContain(
                vm.Items.OfType<MessageItemViewModel>(), m => m.Text == PendingSendScenario.Message);
        });

        /// <summary>
        /// The same decision where the send itself discovers the exit: its session start fails because the engine
        /// has gone. It never reached an agent, so it is given back rather than recorded as said.
        /// </summary>
        [Fact]
        public void ASendWhoseStartRunsIntoTheExitGivesTheMessageBack() => RunSta(() =>
        {
            var engine = new DeadEngine { StartFails = new EngineExitedException(MissingRuntime) };
            var vm = NewViewModel(engine);
            vm.InitializeAsync().GetAwaiter().GetResult();
            Pump();

            vm.InputText = "hello";
            vm.SendCommand.Execute(null);
            Pump();

            Assert.Equal(0, engine.Prompts);
            Assert.Equal("hello", vm.InputText);
            Assert.DoesNotContain(vm.Items.OfType<MessageItemViewModel>(), m => m.Role == MessageRole.User);
            Assert.Contains(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text == EngineExitDescription.Text(MissingRuntime, LogFile));
            Assert.False(vm.IsBusy);
            Assert.Empty(SavedConversations());
        });

        /// <summary>
        /// A prompt that runs into the exit and the exit's own report are two routes to one fact: one notice.
        /// </summary>
        [Fact]
        public void AnExitSeenByAFailedPromptAndByItsReportIsNamedOnce() => RunSta(() =>
        {
            var engine = new DeadEngine { PromptFails = new EngineExitedException(MissingRuntime) };
            var vm = NewViewModel(engine);
            vm.InitializeAsync().GetAwaiter().GetResult();

            vm.InputText = "hello";
            vm.SendCommand.Execute(null);
            Pump();
            engine.RaiseExit(MissingRuntime);
            Pump();

            Assert.Single(vm.Items.OfType<NoticeItemViewModel>(), n => n.IsError);
            Assert.False(vm.IsBusy);
        });

        private IEnumerable<string> SavedConversations()
        {
            var sessions = Path.Combine(_root, "sessions");
            return Directory.Exists(sessions)
                ? Directory.EnumerateFiles(sessions, "*.json", SearchOption.AllDirectories)
                : Enumerable.Empty<string>();
        }

        /// <summary>
        /// The notice carries the host's way back (user decision, 2026-09-15), read from what the host injects, as the
        /// log path is: the wording is shared by hosts whose recovery differs.
        /// </summary>
        [Fact]
        public void TheExitNoticeEndsWithTheHostsWayBack() => RunSta(() =>
        {
            const string Hint = "Restart this app to start it again.";
            var engine = new DeadEngine();
            var vm = NewViewModel(engine, restartHint: Hint);
            vm.InitializeAsync().GetAwaiter().GetResult();
            Pump();

            var crashed = new EngineExit(3, new[] { "[engine] unhandled" });
            engine.RaiseExit(crashed);
            Pump();

            var notice = Assert.Single(vm.Items.OfType<NoticeItemViewModel>(), n => n.IsError);
            Assert.EndsWith(Hint, notice.Text, StringComparison.Ordinal);
            Assert.Equal(EngineExitDescription.Text(crashed, LogFile, Hint), notice.Text);
        });

        private ChatViewModel NewViewModel(DeadEngine engine, string? restartHint = null) => new ChatViewModel(
            engine,
            new StartSessionRequest("fake", null, _root, "Prompt", null),
            sessionStore: new FileSessionStore(Path.Combine(_root, "sessions")),
            sessionEnvironment: () => new SessionEnvironment(
                Path.Combine(_root, "logs"), LogFile,
                Path.Combine(_root, "logs", "acp.log"),
                Path.Combine(_root, "logs", "engine-channel.log"),
                Path.Combine(_root, "logs", "render.log"),
                acpLogEnabled: false, engineChannelLogEnabled: false, renderLogEnabled: false,
                kiroAgentEngine: null,
                engineRestartHint: restartHint));

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

        private sealed class DeadEngine : IEngineConnection, IEngineExitReport
        {
            public Exception? ListFails { get; set; }

            public Exception? StartFails { get; set; }

            public int Starts { get; private set; }

            public int Prompts { get; private set; }

            public event Action<EngineExit>? Exited;

            /// <summary>Reports the exit from a pool thread, as <see cref="EngineClient"/> does.</summary>
            public void RaiseExit(EngineExit exit) => Task.Run(() => Exited?.Invoke(exit)).GetAwaiter().GetResult();

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
            {
                Starts++;
                return StartFails is not null
                    ? Task.FromException<StartSessionResponse>(StartFails)
                    : Task.FromResult(new StartSessionResponse("fresh"));
            }

            public Task<PromptResponse> PromptAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
            {
                Prompts++;
                return PromptFails is not null
                    ? Task.FromException<PromptResponse>(PromptFails)
                    : Task.FromResult(new PromptResponse("end_turn"));
            }

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
