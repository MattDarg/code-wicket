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
    /// A session OPENING is not the agent working, and the pane has to say so in both of the places it
    /// speaks: the busy state (pinned in <see cref="OutOfTurnWorkingWindowTests"/>) and the transcript.
    ///
    /// <para>kiro-cli 2.21.1 opens every session — new or loaded — with a turn-less tool call of its
    /// own, <c>fetch_cloud_config</c> / "Fetching your cloud config" (wire capture 2026-09-07). The row
    /// stays: it is a real tool call, the CLI shows it as one, and suppressing it would mean deciding
    /// which of a backend's calls are real. What changes is that it does not look like work the user's
    /// prompt caused — it finished GREEN, which is exactly what made it read that way.</para>
    ///
    /// <para><b>Drawn and recorded are one decision here, not two.</b> A row marked live and then
    /// persisted unmarked would come back from a reload looking like ordinary work — the same call
    /// rendering two ways depending on when you looked at it. So these frames are not recorded at all,
    /// which is also the fix for where they were being recorded TO: see the second case.</para>
    /// </summary>
    public sealed class SessionOpeningRowsTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "cwkt-tests", Guid.NewGuid().ToString("N"));

        public SessionOpeningRowsTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }

        [Fact]
        public void ACallMadeBeforeTheFirstPromptIsMarkedAsSessionSetup() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);

            OpenSession(engine);

            var row = Assert.Single(vm.Items.OfType<ToolItemViewModel>());
            Assert.True(row.IsSessionSetup);
            Assert.Equal("Fetching your cloud config", row.Title);

            // The glyph is the carrier, and it has to differ from the one this same call would draw as
            // ordinary work — the row is otherwise identical, deliberately, since the status colour is
            // the status channel and saying two things there is what read as "inactive".
            Assert.NotEqual(new ToolItemViewModel("x", "Fetching your cloud config", "other").KindGlyph, row.KindGlyph);
        });

        /// <summary>
        /// The leak this was checked for, and it was real. <c>LoadSession</c> sets <c>_persisted</c> and
        /// then warm-starts, so on a restored conversation with nothing left to resume the NEW session's
        /// opening frames were appended to the log of a conversation they had nothing to do with — and
        /// checkpointed there, so it grew by a housekeeping row every time it was opened. On a genuinely
        /// new conversation <c>_persisted</c> is still null and the same frames were dropped: the two
        /// paths disagreed about identical events, which is the part that makes this a defect rather
        /// than a preference.
        /// </summary>
        [Fact]
        public void ASessionOpeningCallIsNotAppendedToTheConversationOnScreen() => RunSta(() =>
        {
            var store = new FileSessionStore(_root);
            var engine = new StubEngine();

            // A saved conversation, then a second pane restoring it — which is where _persisted is set
            // while nothing has been asked of the session behind it.
            var first = NewViewModel(engine, store);
            Prompt(first, engine, "hello");

            // Its own engine, and that is not tidiness: a view-model stays subscribed to the event
            // stream for its whole life, so a shared one would deliver these frames to `first` as well —
            // where the session HAS been prompted, so it records them, into the same saved conversation.
            // The test would then fail for a reason that cannot happen in the product, which hosts one
            // pane per engine.
            var reopenedEngine = new StubEngine();
            var reopened = NewViewModel(reopenedEngine, store);
            reopened.RestoreMostRecentSession();
            Drain();
            Assert.Contains(reopened.Items.OfType<MessageItemViewModel>(), m => m.Text.Contains("hello"));

            OpenSession(reopenedEngine);

            // Drawn — the row is there and marked — but not in the log.
            Assert.True(Assert.Single(reopened.Items.OfType<ToolItemViewModel>()).IsSessionSetup);

            var saved = store.List(_root).Select(s => store.Load(_root, s.Id)).Single();
            Assert.DoesNotContain(
                saved!.Log,
                e => e.Event is { } ev && ev.Type is "toolStart" or "toolDone");
        });

        /// <summary>
        /// The trap in the whole approach: a replayed transcript is rebuilt with no session started and
        /// no turn running, which is indistinguishable from a session opening if the flag is read where
        /// the row is built. It is passed in from <c>ApplyLive</c> instead, so replay — the only other
        /// caller of <c>Apply</c> — takes the default and every restored row stays ordinary work.
        /// Without this the first reload would grey out an entire conversation.
        /// </summary>
        [Fact]
        public void AReplayedRowIsNotMarkedAsSessionSetup() => RunSta(() =>
        {
            var store = new FileSessionStore(_root);
            var engine = new StubEngine();

            var first = NewViewModel(engine, store);
            first.InputText = "do some work";
            first.SendCommand.Execute(null);
            Drain();

            // Inside the turn, so this is the agent working and IS recorded.
            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "t1", Title = "Read File", Kind = "read" });
            engine.Raise(new AgentEventDto { Type = "toolDone", ToolCallId = "t1" });
            Drain();
            engine.CompleteTurn();
            Drain();

            Assert.False(Assert.Single(first.Items.OfType<ToolItemViewModel>()).IsSessionSetup);

            var reopened = NewViewModel(new StubEngine(), store);
            reopened.RestoreMostRecentSession();
            Drain();

            var replayed = Assert.Single(reopened.Items.OfType<ToolItemViewModel>());
            Assert.Equal("Read File", replayed.Title);
            Assert.False(replayed.IsSessionSetup);
        });

        /// <summary>
        /// The reported shape: a Claude Code conversation opening with a <b>Kiro</b> tool call in it.
        /// </summary>
        /// <remarks>
        /// From a live pane (engine.log 2026-09-08): the workspace moved to a solution with no saved
        /// history of its own, so there was nothing to restore and the selection carried over from the
        /// solution before it — the pane warm-started kiro and kiro announced itself. Switching the
        /// picker to Claude Code replaced the session and kept the transcript, which is what every
        /// other route to a new session does not do, so the setup row was the one thing on screen that
        /// nothing cleared. The new session's roster notice then landed underneath it.
        /// </remarks>
        [Fact]
        public void SwitchingBackendBeforeTheFirstPromptDropsTheOldSessionsOpeningRows() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);
            AddProviders(vm);

            vm.SelectedProvider = vm.Providers.Single(p => p.Id == "kiro");
            Drain();

            OpenSession(engine);   // kiro's warm session announces itself
            Assert.Single(vm.Items.OfType<ToolItemViewModel>());

            vm.SelectedProvider = vm.Providers.Single(p => p.Id == "claude-code");
            Drain();

            Assert.Empty(vm.Items.OfType<ToolItemViewModel>());
        });

        /// <summary>
        /// And after a prompt too, which is the case that looks like history and is not. These rows are
        /// never recorded, so one kept here would be gone from the same conversation after a reload —
        /// the same call showing or not depending on when you looked, which is the argument that made
        /// them display-only to begin with.
        /// </summary>
        [Fact]
        public void SwitchingBackendMidConversationDropsThemToo() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);
            AddProviders(vm);
            vm.SelectedProvider = vm.Providers.Single(p => p.Id == "kiro");
            Drain();

            OpenSession(engine);
            Prompt(vm, engine, "hello");
            Assert.Single(vm.Items.OfType<ToolItemViewModel>());

            vm.SelectedProvider = vm.Providers.Single(p => p.Id == "claude-code");
            Drain();

            Assert.Empty(vm.Items.OfType<ToolItemViewModel>());

            // The conversation itself is untouched — the switch keeps it deliberately, and this is what
            // stops the fix being "clear more".
            Assert.Contains(vm.Items.OfType<MessageItemViewModel>(), m => m.Text.Contains("hello"));
            Assert.Contains(vm.Items.OfType<NoticeItemViewModel>(), n => n.Text.Contains("Switched to"));
        });

        /// <summary>
        /// The session's own announcements go with the row. An MCP server connecting on a warm session
        /// is drawn as a notice and never recorded (live backend state belongs to a connection, not a
        /// transcript), so after a picker change it was the one thing on screen describing nothing —
        /// and the next warm start added a second set beneath it. The inconsistency was reported from
        /// the pane: "the 'Fetching your cloud config' row disappears, but the MCP notices remain".
        /// </summary>
        [Fact]
        public void SwitchingBackendBeforeTheFirstPromptDropsTheOldSessionsAnnouncementsToo() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);
            AddProviders(vm);
            vm.SelectedProvider = vm.Providers.Single(p => p.Id == "kiro");
            Drain();

            OpenSession(engine);
            engine.Raise(new AgentEventDto { Type = "mcpServerConnected", Title = "aws-mcp" });
            Drain();
            var announcement = Assert.Single(vm.Items.OfType<NoticeItemViewModel>(), n => n.Text.Contains("aws-mcp"));
            Assert.True(announcement.IsSessionSetup);

            vm.SelectedProvider = vm.Providers.Single(p => p.Id == "claude-code");
            Drain();

            Assert.DoesNotContain(vm.Items.OfType<NoticeItemViewModel>(), n => n.Text.Contains("aws-mcp"));
        });

        /// <summary>
        /// After a prompt the announcement's POSITION is the information (issue #19: it landed after
        /// your message, so that prompt's tool snapshot was taken before this server was up), and a
        /// switch leaves it where it was. Without this the fix above is satisfiable by dropping every
        /// MCP notice.
        /// </summary>
        [Fact]
        public void AnAnnouncementAfterAPromptIsNotSetupAndSurvivesASwitch() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);
            AddProviders(vm);
            vm.SelectedProvider = vm.Providers.Single(p => p.Id == "kiro");
            Drain();

            Prompt(vm, engine, "hello");
            engine.Raise(new AgentEventDto { Type = "mcpServerConnected", Title = "gitlab" });
            Drain();
            var late = Assert.Single(vm.Items.OfType<NoticeItemViewModel>(), n => n.Text.Contains("gitlab"));
            Assert.False(late.IsSessionSetup);

            vm.SelectedProvider = vm.Providers.Single(p => p.Id == "claude-code");
            Drain();

            Assert.Contains(vm.Items.OfType<NoticeItemViewModel>(), n => n.Text.Contains("gitlab"));
        });

        /// <summary>
        /// A row the agent drew for the user's own prompt is work, and a backend switch does not erase
        /// what happened. Without this the fix above is satisfiable by dropping every tool row.
        /// </summary>
        [Fact]
        public void ASwitchKeepsTheWorkTheAgentActuallyDid() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);
            AddProviders(vm);
            vm.SelectedProvider = vm.Providers.Single(p => p.Id == "kiro");
            Drain();

            vm.InputText = "do some work";
            vm.SendCommand.Execute(null);
            Drain();
            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "t1", Title = "Read File", Kind = "read" });
            engine.Raise(new AgentEventDto { Type = "toolDone", ToolCallId = "t1" });
            Drain();
            engine.CompleteTurn();
            Drain();

            vm.SelectedProvider = vm.Providers.Single(p => p.Id == "claude-code");
            Drain();

            Assert.Equal("Read File", Assert.Single(vm.Items.OfType<ToolItemViewModel>()).Title);
        });

        // ---- helpers --------------------------------------------------------------------------

        /// <summary>The two frames kiro-cli sends on every session open, as they arrive on the wire.</summary>
        private static void OpenSession(StubEngine engine)
        {
            engine.Raise(new AgentEventDto
            {
                Type = "toolStart",
                ToolCallId = "85d6786c",
                Title = "Fetching your cloud config",
                Kind = "other",
            });
            engine.Raise(new AgentEventDto { Type = "toolDone", ToolCallId = "85d6786c" });
            Drain();
        }

        /// <summary>A picker with both backends in it, as the catalog fills it.</summary>
        private static void AddProviders(ChatViewModel vm)
        {
            vm.Providers.Add(new ProviderItemViewModel(
                "kiro", "Kiro", new List<ModelItemViewModel>(), true, true, null));
            vm.Providers.Add(new ProviderItemViewModel(
                "claude-code", "Claude Code", new List<ModelItemViewModel>(), true, true, null));
        }

        private static void Prompt(ChatViewModel vm, StubEngine engine, string text)
        {
            vm.InputText = text;
            vm.SendCommand.Execute(null);
            Drain();
            engine.CompleteTurn();
            Drain();
        }

        private ChatViewModel NewViewModel(StubEngine engine, FileSessionStore? store = null) => new(
            engine,
            new StartSessionRequest("fake", null, _root, "Prompt", null),
            sessionStore: store);

        private static void Drain() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        // One shared, GATED implementation - see StaTest. The dispatcher context is what brings a send's
        // ConfigureAwait(true) continuations back to this thread; without it Drain never sees the turn.
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

            public Task<SessionInfoResponse?> SessionInfoAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<SessionInfoResponse?>(null);

            public Task<ListProvidersResponse> ListProvidersAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new ListProvidersResponse(new List<ProviderInfoDto>()));

            public Task<StartSessionResponse> StartSessionAsync(
                StartSessionRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new StartSessionResponse(request.ResumeConversationId ?? "fresh"));

            public Task<PromptResponse> PromptAsync(
                string text, IReadOnlyList<PromptAttachmentDto>? attachments = null,
                CancellationToken cancellationToken = default)
            {
                _turn = new TaskCompletionSource<PromptResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                return _turn.Task;
            }

            public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<SteerResponse> SteerAsync(
                string text, IReadOnlyList<PromptAttachmentDto>? attachments = null,
                CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("These tests never steer.");

            public Task SetModelAsync(string modelId, CancellationToken cancellationToken = default)
                => Task.CompletedTask;

            public Task<ListBackendSessionsResponse> ListBackendSessionsAsync(
                ListBackendSessionsRequest request, CancellationToken cancellationToken = default)
                => throw new NotSupportedException("This stub lists no backend sessions.");

            public Task<TakeImportedHistoryResponse> TakeImportedHistoryAsync(
                TakeImportedHistoryRequest request, CancellationToken cancellationToken = default)
                => throw new NotSupportedException("This stub imports no history.");

            public Task<SummarizeResponse> SummarizeAsync(
                SummarizeRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("These tests never summarize.");
        }
    }
}
