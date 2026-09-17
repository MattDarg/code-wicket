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
    /// Usage is live backend state: never recorded, never replayed, and never drawn as history on the
    /// live ring.
    /// <para>
    /// The rule was written in AGENTS.md and in a comment directly above the code that broke it -
    /// <c>Record</c>'s exclusion list named the MCP events and <c>toolOutput</c> and omitted
    /// <c>usage</c>. Measured on the real store before the fix: <b>1,571</b> recorded usage events
    /// across <b>27 of 29</b> sessions, 14% of the whole store by bytes, and the third most common
    /// event type on disk. Worse than the bulk, <see cref="ChatViewModel"/>'s LoadSession cleared the
    /// ring and then replayed those events six lines later, so opening any saved conversation
    /// immediately drew a dead session's context figure as live state - reported from the field.
    /// </para>
    /// <para>What survives is one header field, <see cref="PersistedSession.LastContextPercent"/>.</para>
    /// </summary>
    public class UsageIsNotRecordedTests : IDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "cwkt-usage-" + Guid.NewGuid().ToString("N"));

        // The store buckets by workspace root, so the saved session and the view-model's request must
        // name the same one or the picker lists nothing and every assertion below is vacuous.
        private static readonly string Workspace = AppContext.BaseDirectory;

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* scratch */ }
        }

        // ---- the reported bug -------------------------------------------------------------------

        /// <summary>
        /// The field report: selecting a history item made the context ring change straight away, with
        /// no prompt sent. Driven through the picker's own command, which is the gesture the user made.
        /// </summary>
        [Fact]
        public void OpeningASavedConversationDoesNotPutItsOldContextFigureOnTheRing() => RunSta(() =>
        {
            var store = new FileSessionStore(_root);
            store.Save(SessionWithRecordedUsage(percent: 39.8329));

            var vm = NewViewModel(new StubEngine(), store);
            vm.RefreshHistory();
            DrainDispatcher();

            Assert.Single(vm.History).LoadCommand.Execute(null);
            DrainDispatcher();

            // The transcript restored, and the ring stayed empty: the figure belongs to a backend this
            // conversation is not connected to. HasUsage is what the ring binds its visibility to.
            Assert.NotEmpty(vm.Items);
            Assert.Null(vm.Usage);
            Assert.False(vm.HasUsage);
        });

        // ---- the recording half -----------------------------------------------------------------

        /// <summary>A live usage event must not reach the persisted log.</summary>
        [Fact]
        public void ALiveUsageEventIsNotWrittenToTheLog() => RunSta(() =>
        {
            var store = new FileSessionStore(_root);
            store.Save(SessionWithRecordedUsage(percent: null));

            var engine = new StubEngine();
            var vm = NewViewModel(engine, store);
            vm.RefreshHistory();
            Assert.Single(vm.History).LoadCommand.Execute(null);
            DrainDispatcher();

            engine.Raise(new AgentEventDto
            {
                Type = "usage",
                Usage = new UsageDto { ContextPercent = 42.5 },
            });
            // Checkpoints are boundary-driven; turnDone is what flushes the log to disk.
            engine.Raise(new AgentEventDto { Type = "turnDone", StopReason = "end_turn" });
            DrainDispatcher();

            var saved = Reload(store);
            Assert.DoesNotContain(saved.Log, e => e.Event?.Type == "usage");

            // ...but the figure itself survived, as one header field rather than a stream of events.
            Assert.NotNull(saved.LastContextPercent);
            Assert.Equal(42.5, saved.LastContextPercent!.Value, 3);
        });

        // ---- the list that decides both ---------------------------------------------------------

        [Fact]
        public void LiveBackendStateNamesTheEventsThatLieWhenReplayed()
        {
            Assert.True(ChatViewModel.IsLiveBackendState("usage"));
            Assert.True(ChatViewModel.IsLiveBackendState("mcpRoster"));
            Assert.True(ChatViewModel.IsLiveBackendState("mcpServerConnected"));
            Assert.True(ChatViewModel.IsLiveBackendState("mcpBridge"));
        }

        /// <summary>
        /// <c>toolOutput</c> is excluded from RECORDING for a different reason - the completion's
        /// result is a durable superset - but it is conversation content rather than a claim about the
        /// connection, so an old log carrying it (from a turn that never completed) must still render.
        /// Merging the two lists would drop that output silently.
        /// </summary>
        [Fact]
        public void ToolOutputIsNotTreatedAsLiveBackendState()
        {
            Assert.False(ChatViewModel.IsLiveBackendState("toolOutput"));
            Assert.False(ChatViewModel.IsLiveBackendState("text"));
            Assert.False(ChatViewModel.IsLiveBackendState("toolDone"));
        }

        // ---- the row ----------------------------------------------------------------------------

        [Fact]
        public void TheRowStatesTheContextFigure_AndFallsBackToSizeWithoutOne()
        {
            var withFill = Row(new SessionSummary(
                "id", "T", DateTime.UtcNow, 3, 2048, Array.Empty<string>(), null, 39.8329));
            Assert.Contains("40% context", withFill.Subtitle);
            Assert.DoesNotContain("KB", withFill.Subtitle);

            var without = Row(new SessionSummary(
                "id", "T", DateTime.UtcNow, 3, 2048, Array.Empty<string>()));
            Assert.Contains("KB", without.Subtitle);
            Assert.DoesNotContain("context", without.Subtitle);
        }

        /// <summary>Never reported is not zero - the UsageReport rule, on the picker row.</summary>
        [Fact]
        public void LastContextPercentIsNullWhenTheBackendNeverReportedOne()
        {
            var store = new FileSessionStore(_root);
            store.Save(SessionWithRecordedUsage(percent: null));
            Assert.Null(Assert.Single(store.List(Workspace)).LastContextPercent);
        }

        // ---- helpers ----------------------------------------------------------------------------

        // A conversation as an OLDER build wrote it: usage events in the log, which is what every
        // existing file on disk looks like. Assistant text keeps the restore off the warm-start path.
        private static PersistedSession SessionWithRecordedUsage(double? percent)
        {
            var session = new PersistedSession { Id = "s", WorkspaceRootPath = Workspace, Title = "Saved" };
            session.Log.Add(new TranscriptEntry { Role = "user", Text = "hello" });
            session.Log.Add(new TranscriptEntry
            {
                Role = "agent",
                Event = new AgentEventDto { Type = "text", Text = "an answer" },
            });
            if (percent is { } p)
            {
                session.Log.Add(new TranscriptEntry
                {
                    Role = "agent",
                    Event = new AgentEventDto { Type = "usage", Usage = new UsageDto { ContextPercent = p } },
                });
            }

            return session;
        }

        private static PersistedSession Reload(FileSessionStore store) =>
            store.Load(Workspace, "s") ?? throw new InvalidOperationException("session vanished");

        private static SessionSummaryViewModel Row(SessionSummary summary) =>
            new(summary, isCurrent: false, _ => { }, _ => { }, (_, _) => { });

        private static ChatViewModel NewViewModel(StubEngine engine, ISessionStore store) => new(
            engine,
            new StartSessionRequest("fake", null, AppContext.BaseDirectory, "Prompt", null),
            sessionStore: store);

        private static void DrainDispatcher() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        private static void RunSta(Action action) => StaTest.Run(action);

        /// <summary>Drives the view-model from the event stream; never opens a backend session.</summary>
        private sealed class StubEngine : IEngineConnection
        {
            public event Action<AgentEventDto>? AgentEvent;

            public event Action<ProviderModelsDto>? ProviderModelsRefreshed { add { } remove { } }

            public void Raise(AgentEventDto ev) => AgentEvent?.Invoke(ev);

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
                => throw new NotSupportedException("This stub lists no backend sessions.");

            public Task<TakeImportedHistoryResponse> TakeImportedHistoryAsync(
                TakeImportedHistoryRequest request, CancellationToken cancellationToken = default)
                => throw new NotSupportedException("This stub imports no history.");

            public Task<SummarizeResponse> SummarizeAsync(SummarizeRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("These tests never summarize.");
        }
    }
}
