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
    /// A warm session's opening frames must not be drawn into a transcript that belongs to another
    /// backend.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Measured at startup, 2026-09-11.</b> The default backend (Kiro) began warming at 22:33:23.9;
    /// the last conversation (Claude Code's) was restored by 22:33:24.2; Kiro's KAS came up at
    /// 22:33:27 and its opening frames — the <c>fetch_cloud_config</c> setup row and two MCP-connected
    /// notices — reached the host then, and were drawn at the foot of Claude's transcript. The saved
    /// log had none of them (922 entries, zero matches), so it was not a replay: the events were live,
    /// nobody owned them (a warm session has no conversation), and the picker had already moved.
    /// </para>
    /// <para>
    /// The gate is on the ENGINE's provider, not the warm request's: by the time the old session's
    /// late frames arrive the warm request already names the new choice, and the engine still holds the
    /// old session until the chained start replaces it. And it is gated on the owner being null, so an
    /// adopted session the picker later moves away from keeps delivering — a late background return
    /// belongs to the conversation on screen (issue #256).
    /// </para>
    /// </remarks>
    public sealed class AbandonedWarmSessionTests : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "cwkt-abandoned-warm-" + Guid.NewGuid().ToString("N"));

        private string Workspace => Path.Combine(_dir, "Solution");

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch (IOException) { }
        }

        [Fact]
        public void OpeningFramesOfAWarmSessionThePickerLeftAreNotDrawn() => RunSta(() =>
        {
            var store = new FileSessionStore(Path.Combine(_dir, "sessions"));
            store.Save(ClaudeConversation());
            var log = new List<string>();
            var engine = new StubEngine();
            var vm = NewViewModel(engine, store, log.Add);

            // Startup: the picker comes up on the default backend and its warm start is issued; the
            // engine holds that (still-opening) session.
            Initialize(vm);
            Assert.Equal("kiro", vm.SelectedProvider?.Id);
            vm.WarmStartSession();
            Drain();
            Assert.Equal(new[] { "kiro" }, engine.Starts.Select(s => s.ProviderId));

            // Then the last conversation is restored, which moves the picker to its backend.
            vm.RestoreMostRecentSession();
            Drain();
            Assert.Equal("claude-code", vm.SelectedProvider?.Id);
            var rowsAfterRestore = vm.Items.Count;

            // Now Kiro's opening frames arrive, three seconds late in the field.
            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "k1", Title = "Fetching your cloud config", Kind = "other" });
            engine.Raise(new AgentEventDto { Type = "toolDone", ToolCallId = "k1", Success = true });
            engine.Raise(new AgentEventDto { Type = "mcpServerConnected", Title = "aws-mcp" });
            Drain();

            Assert.Equal(rowsAfterRestore, vm.Items.Count);
            Assert.DoesNotContain(vm.Items.OfType<ToolItemViewModel>(), t => t.Title == "Fetching your cloud config");
            Assert.DoesNotContain(vm.Items.OfType<NoticeItemViewModel>(), n => n.Text.Contains("aws-mcp", StringComparison.Ordinal));

            // Said once, in engine.log, with both backends named — the instrument for the next report.
            var line = Assert.Single(log, l => l.Contains("abandoned", StringComparison.Ordinal));
            Assert.Contains("'kiro'", line, StringComparison.Ordinal);
            Assert.Contains("'claude-code'", line, StringComparison.Ordinal);
        });

        /// <summary>The control: a warm session for the backend the picker IS on draws its opening
        /// frames as before — the setup row and the notices are real, and #19 put them there on purpose.</summary>
        [Fact]
        public void OpeningFramesOfTheWarmSessionThePickerIsOnAreDrawn() => RunSta(() =>
        {
            var store = new FileSessionStore(Path.Combine(_dir, "sessions")); // nothing to restore
            var log = new List<string>();
            var engine = new StubEngine();
            var vm = NewViewModel(engine, store, log.Add);

            Initialize(vm);
            vm.RestoreMostRecentSession(); // nothing saved: warms the picker's backend
            Drain();
            Assert.Equal(new[] { "kiro" }, engine.Starts.Select(s => s.ProviderId));

            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "k1", Title = "Fetching your cloud config", Kind = "other" });
            engine.Raise(new AgentEventDto { Type = "mcpServerConnected", Title = "aws-mcp" });
            Drain();

            Assert.Contains(vm.Items.OfType<ToolItemViewModel>(), t => t.Title == "Fetching your cloud config");
            Assert.Contains(vm.Items.OfType<NoticeItemViewModel>(), n => n.Text.Contains("aws-mcp", StringComparison.Ordinal));
            Assert.DoesNotContain(log, l => l.Contains("abandoned", StringComparison.Ordinal));
        });

        // ---- helpers ------------------------------------------------------------------------------

        private PersistedSession ClaudeConversation()
        {
            var session = new PersistedSession
            {
                WorkspaceRootPath = Workspace, Title = "Commit your changes please", ProviderId = "claude-code",
            };
            session.Log.Add(new TranscriptEntry { Role = "user", Text = "Commit your changes please" });
            session.Log.Add(new TranscriptEntry
            {
                Role = "agent",
                Event = new AgentEventDto { Type = "text", Text = "Three commits." },
            });
            session.Log.Add(new TranscriptEntry { Role = "agent", Event = new AgentEventDto { Type = "turnDone" } });
            return session;
        }

        private ChatViewModel NewViewModel(StubEngine engine, FileSessionStore store, Action<string> log) =>
            new ChatViewModel(
                engine,
                new StartSessionRequest("kiro", null, Workspace, "Prompt", null),
                sessionStore: store,
                diagnosticLog: log);

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

        /// <summary>Every session start stays PENDING until the test completes it, which is how the
        /// field sequence looked: Kiro's start took twelve seconds, and the frames in question arrived
        /// during them.</summary>
        private sealed class StubEngine : IEngineConnection
        {
            public event Action<AgentEventDto>? AgentEvent;

            public event Action<ProviderModelsDto>? ProviderModelsRefreshed { add { } remove { } }

            public List<StartSessionRequest> Starts { get; } = new();

            public void Raise(AgentEventDto ev) => AgentEvent?.Invoke(ev);

            public Task<SessionInfoResponse?> SessionInfoAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<SessionInfoResponse?>(null);

            public Task<ListProvidersResponse> ListProvidersAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new ListProvidersResponse(new List<ProviderInfoDto>()));

            public Task<StartSessionResponse> StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default)
            {
                Starts.Add(request);
                return new TaskCompletionSource<StartSessionResponse>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
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
