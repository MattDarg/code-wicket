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
    /// When a backend session is opened before the user has sent anything (the warm start, issue #19).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only for a pane that is certainly about to start fresh</b> (user decision, 2026-09-11):
    /// nothing to restore, New, a picker change on a fresh pane. Never after restoring a conversation.
    /// A resumable one never takes the warm session — its send resumes by <c>session/load</c> and
    /// <c>TakeWarmOrStartAsync</c> discards a warm session whose request differs — so the warm was a
    /// backend session opened and dropped, and the placeholder conversations in the backends' stores
    /// were these.
    /// </para>
    /// <para>
    /// <b>Most of those warms were accidental.</b> The provider setter's <c>finally</c> cleared the
    /// selection-suppression flag rather than restoring it, so the outer suppression that
    /// <c>InitializeAsync</c> and <c>RestoreSelection</c> wrap around it was defeated and every
    /// restore ran the selection-change handler on a not-yet-attached pane. Measured at startup:
    /// Kiro's warm at 22:33:23.9, Claude's chained behind it, for a conversation only being read.
    /// </para>
    /// </remarks>
    public sealed class WarmStartPolicyTests : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "cwkt-warm-policy-" + Guid.NewGuid().ToString("N"));

        private string Workspace => Path.Combine(_dir, "Solution");

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch (IOException) { }
        }

        [Fact]
        public void InitialisingThePickerStartsNothing() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine, EmptyStore());

            Initialize(vm);

            Assert.Empty(engine.Starts);
        });

        [Fact]
        public void RestoringAConversationStartsNothing() => RunSta(() =>
        {
            var store = EmptyStore();
            store.Save(Conversation("claude-code", resumable: true));
            var engine = new StubEngine();
            var vm = NewViewModel(engine, store);

            Initialize(vm);
            vm.RestoreMostRecentSession();
            Drain();

            Assert.Equal("claude-code", vm.SelectedProvider?.Id);
            Assert.Empty(engine.Starts);
        });

        /// <summary>Even one with nothing to resume: it was opened to be read.</summary>
        [Fact]
        public void RestoringAnUnresumableConversationStartsNothingEither() => RunSta(() =>
        {
            var store = EmptyStore();
            store.Save(Conversation("kiro", resumable: false));
            var engine = new StubEngine();
            var vm = NewViewModel(engine, store);

            Initialize(vm);
            vm.RestoreMostRecentSession();
            Drain();

            Assert.Empty(engine.Starts);
        });

        [Fact]
        public void NothingToRestoreWarmsThePickersBackendOnce() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine, EmptyStore());

            Initialize(vm);
            vm.RestoreMostRecentSession();
            Drain();

            Assert.Equal(new[] { "kiro" }, engine.Starts.Select(s => s.ProviderId));
        });

        [Fact]
        public void APickerChangeOnAFreshPaneReWarms() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine, EmptyStore());
            Initialize(vm);
            vm.RestoreMostRecentSession();
            Drain();

            vm.SelectedProvider = vm.Providers.Single(p => p.Id == "claude-code");
            Drain();

            Assert.Equal(new[] { "kiro", "claude-code" }, engine.Starts.Select(s => s.ProviderId));
        });

        [Fact]
        public void NewSessionAfterARestoreWarms() => RunSta(() =>
        {
            var store = EmptyStore();
            store.Save(Conversation("claude-code", resumable: true));
            var engine = new StubEngine();
            var vm = NewViewModel(engine, store);
            Initialize(vm);
            vm.RestoreMostRecentSession();
            Drain();
            Assert.Empty(engine.Starts);

            vm.NewSessionCommand.Execute(null);
            Drain();

            Assert.Equal(new[] { "claude-code" }, engine.Starts.Select(s => s.ProviderId));
        });

        // ---- helpers ------------------------------------------------------------------------------

        private FileSessionStore EmptyStore() => new FileSessionStore(Path.Combine(_dir, "sessions"));

        private PersistedSession Conversation(string providerId, bool resumable)
        {
            var session = new PersistedSession
            {
                WorkspaceRootPath = Workspace, Title = "Earlier", ProviderId = providerId,
                ConversationId = resumable ? "backend-conv-1" : null,
            };
            session.Log.Add(new TranscriptEntry { Role = "user", Text = "hello" });
            if (resumable)
            {
                session.Log.Add(new TranscriptEntry
                {
                    Role = "agent", Event = new AgentEventDto { Type = "text", Text = "hi" },
                });
            }
            session.Log.Add(new TranscriptEntry { Role = "agent", Event = new AgentEventDto { Type = "turnDone" } });
            return session;
        }

        private ChatViewModel NewViewModel(StubEngine engine, FileSessionStore store) =>
            new ChatViewModel(
                engine,
                new StartSessionRequest("kiro", null, Workspace, "Prompt", null),
                sessionStore: store);

        private static void Initialize(ChatViewModel vm)
        {
            var init = vm.InitializeAsync(Task.FromResult(new ListProvidersResponse(new List<ProviderInfoDto>
            {
                new("kiro", "Kiro", new List<ModelInfoDto> { new("auto", "auto") }, new List<string> { "ResumeSession" }),
                new("claude-code", "Claude Code", new List<ModelInfoDto> { new("default", "Default") }, new List<string> { "ResumeSession", "ModelSelection" }),
            })));
            Drain();
            Assert.True(init.IsCompletedSuccessfully);
        }

        private static void Drain() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        private static void RunSta(Action action) => StaTest.Run(action, withDispatcherContext: true);

        /// <summary>Starts complete at once, so a chained warm start proceeds and every start is counted.</summary>
        private sealed class StubEngine : IEngineConnection
        {
            public event Action<AgentEventDto>? AgentEvent { add { } remove { } }

            public event Action<ProviderModelsDto>? ProviderModelsRefreshed { add { } remove { } }

            public List<StartSessionRequest> Starts { get; } = new();

            public Task<SessionInfoResponse?> SessionInfoAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<SessionInfoResponse?>(null);

            public Task<ListProvidersResponse> ListProvidersAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new ListProvidersResponse(new List<ProviderInfoDto>()));

            public Task<StartSessionResponse> StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default)
            {
                Starts.Add(request);
                return Task.FromResult(new StartSessionResponse("c" + Starts.Count));
            }

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
