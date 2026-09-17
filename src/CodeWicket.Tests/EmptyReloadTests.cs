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
    /// A full reload that the backend reports as a success and restores nothing is treated as the
    /// refusal it is (issue #185). Measured 2026-09-12 (<c>Console resume-unknown-id kiro v3</c>):
    /// Kiro v3 answers an id it no longer holds — pruned, or from another engine — with a successful
    /// <c>session/load</c> that replays no messages, so nothing populated <c>ResumeFailureReason</c>
    /// and the user read their whole transcript beside an agent that had never heard of it. The
    /// default engine and Claude Code both refuse such an id, which issue #268 already handles; this
    /// is the same ask reached by the silent route, and it is decided on what the backend REPLAYED
    /// rather than on what it said.
    ///
    /// <para>These pin the offer, not only the outcome, on the same argument <c>RefusedResumeTests</c>
    /// makes: every assertion below would pass on "it didn't crash"; what makes them load-bearing is
    /// that they require the send to be parked when the count is zero and NOT parked when it is
    /// positive or unreported.</para>
    /// </summary>
    public sealed class EmptyReloadTests : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "cwkt-empty-reload-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch (IOException) { }
        }

        /// <summary>The defect: success reported, nothing replayed, and the send stops to ask.</summary>
        [Fact]
        public void AReloadThatReplayedNothingStopsAndAsks() => RunSta(() =>
        {
            var engine = new StubEngine { ReplayedOnResume = 0 };
            var vm = Resumable(engine);

            SendAndChooseFull(vm);

            Assert.True(vm.HasPendingResume);
            Assert.Equal("Couldn't reload this conversation", vm.PendingResume!.Heading);
            Assert.Contains("replayed none", vm.PendingResume.Reason ?? string.Empty, StringComparison.Ordinal);
            Assert.True(vm.PendingResume.AllowSummary);
            Assert.False(string.IsNullOrEmpty(vm.PendingResume.SummaryTooltip));
            Assert.Empty(engine.Prompts);
        });

        /// <summary>
        /// The control that keeps this from being "ask on every resume": a load that replayed the
        /// conversation goes straight through, as it always did.
        /// </summary>
        [Fact]
        public void AReloadThatReplayedTheConversationAsksNothing() => RunSta(() =>
        {
            var engine = new StubEngine { ReplayedOnResume = 12 };
            var vm = Resumable(engine);

            SendAndChooseFull(vm);

            Assert.False(vm.HasPendingResume);
            Assert.Single(engine.Prompts);
        });

        /// <summary>
        /// Null means the backend did not say, and a claim cannot be built on it — the rule every
        /// nullable count in this codebase carries. A provider that predates the count keeps the old
        /// behaviour rather than being called empty.
        /// </summary>
        [Fact]
        public void AnUnreportedCountMakesNoClaim() => RunSta(() =>
        {
            var engine = new StubEngine { ReplayedOnResume = null };
            var vm = Resumable(engine);

            SendAndChooseFull(vm);

            Assert.False(vm.HasPendingResume);
            Assert.Single(engine.Prompts);
        });

        /// <summary>
        /// Answered with the recap, the message goes into the session the backend opened with an
        /// account of the messages above it, and the notice says so plainly.
        /// </summary>
        [Fact]
        public void ChoosingTheRecapSendsItAndSaysWhy() => RunSta(() =>
        {
            var engine = new StubEngine { ReplayedOnResume = 0 };
            var vm = Resumable(engine);

            SendAndChooseFull(vm);
            vm.PendingResume!.ResumeSummaryCommand.Execute(null);
            Drain();

            var prompt = Assert.Single(engine.Prompts);
            Assert.Contains("conversation-summary", prompt, StringComparison.Ordinal);
            var notice = Assert.Single(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.Contains("recap", StringComparison.Ordinal)
                     && n.Text.Contains("replayed none", StringComparison.Ordinal));
            Assert.NotEqual(NoticeKind.Error, notice.Kind);
        });

        /// <summary>
        /// Sent anyway, the notice is red: the transcript keeps showing a conversation the agent
        /// cannot see, so this is the line that has to survive being scrolled past.
        /// </summary>
        [Fact]
        public void SendingAnywayLeavesARedNoticeQuotingWhatWasChecked() => RunSta(() =>
        {
            var engine = new StubEngine { ReplayedOnResume = 0 };
            var vm = Resumable(engine);

            SendAndChooseFull(vm);
            vm.PendingResume!.ResumeFreshCommand.Execute(null);
            Drain();

            Assert.Single(engine.Prompts);
            var notice = Assert.Single(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.Contains("doesn't have the messages above it", StringComparison.Ordinal));
            Assert.Equal(NoticeKind.Error, notice.Kind);
            Assert.Contains(ChatViewModel.EmptyReloadReason, notice.Text, StringComparison.Ordinal);
        });

        /// <summary>The pure decision, so the precedence is pinned: the backend's own refusal wins
        /// over the count, a zero count is a claim only over a transcript with something to replay,
        /// and no resume means no claim whatever the count says.</summary>
        [Fact]
        public void TheDecisionPrefersTheBackendsOwnWordsAndClaimsOnlyWhatItCanCheck()
        {
            Assert.Equal("Session not found", ChatViewModel.ReloadFailure(
                new StartSessionResponse("c", ResumeFailureReason: "Session not found", ReplayedHistoryCount: 5),
                "c", "User: hi"));
            Assert.Equal(ChatViewModel.EmptyReloadReason, ChatViewModel.ReloadFailure(
                new StartSessionResponse("c", ReplayedHistoryCount: 0), "c", "User: hi"));
            Assert.Null(ChatViewModel.ReloadFailure(
                new StartSessionResponse("c", ReplayedHistoryCount: 0), "c", string.Empty));
            Assert.Null(ChatViewModel.ReloadFailure(
                new StartSessionResponse("c", ReplayedHistoryCount: 0), resumeId: null, "User: hi"));
            Assert.Null(ChatViewModel.ReloadFailure(
                new StartSessionResponse("c", ReplayedHistoryCount: 3), "c", "User: hi"));
        }

        // ---- helpers --------------------------------------------------------------------------

        private static void SendAndChooseFull(ChatViewModel vm)
        {
            vm.InputText = "what changed?";
            vm.SendCommand.Execute(null);
            Drain();

            Assert.True(vm.HasPendingResume);
            vm.PendingResume!.ResumeFullCommand.Execute(null);
            Drain();
        }

        // A restored conversation big enough that the send-time banner offers the full-vs-summary
        // choice (ResumeDecider's threshold is 4000 chars of transcript).
        private ChatViewModel Resumable(StubEngine engine)
        {
            var store = new FileSessionStore(_dir);
            var session = new PersistedSession
            {
                WorkspaceRootPath = _dir,
                ConversationId = "conv-1",
                ProviderId = "fake",
                Title = "Earlier work",
            };
            session.Log.Add(new TranscriptEntry { Role = "user", Text = new string('u', 2500) });
            session.Log.Add(new TranscriptEntry
            {
                Role = "assistant",
                Event = new AgentEventDto { Type = "text", Text = new string('a', 2500) },
            });
            store.Save(session);

            var vm = new ChatViewModel(
                engine,
                new StartSessionRequest("fake", null, _dir, "Prompt", null),
                sessionStore: store);
            vm.InitializeAsync().GetAwaiter().GetResult();
            vm.RestoreMostRecentSession();
            Drain();
            return vm;
        }

        private static void Drain() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        private static void RunSta(Action action) => StaTest.Run(action, withDispatcherContext: true);

        /// <summary>
        /// A backend that ACCEPTS the reload on the requested id and reports how much it replayed —
        /// the v3 shape — with a summarizer that works. Turns complete immediately.
        /// </summary>
        private sealed class StubEngine : IEngineConnection
        {
            public List<string> Prompts { get; } = new();

            /// <summary>What a resumed start reports as replayed; null = the backend does not say.</summary>
            public int? ReplayedOnResume { get; set; }

            public event Action<AgentEventDto>? AgentEvent { add { } remove { } }

            public event Action<ProviderModelsDto>? ProviderModelsRefreshed { add { } remove { } }

            public Task<SessionInfoResponse?> SessionInfoAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<SessionInfoResponse?>(null);

            public Task<ListProvidersResponse> ListProvidersAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new ListProvidersResponse(new List<ProviderInfoDto>
                {
                    new ProviderInfoDto(
                        "fake", "Fake", new List<ModelInfoDto>(), new List<string> { "ResumeSession" }),
                }));

            public Task<StartSessionResponse> StartSessionAsync(
                StartSessionRequest request, CancellationToken cancellationToken = default)
            {
                var resumed = request.ResumeConversationId is { Length: > 0 };
                return Task.FromResult(new StartSessionResponse(
                    request.ResumeConversationId ?? "conv-new",
                    ReplayedHistoryCount: resumed ? ReplayedOnResume : null));
            }

            public Task<PromptResponse> PromptAsync(
                string text, IReadOnlyList<PromptAttachmentDto>? attachments = null,
                CancellationToken cancellationToken = default)
            {
                Prompts.Add(text);
                return Task.FromResult(new PromptResponse("end_turn"));
            }

            public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<SteerResponse> SteerAsync(
                string text, IReadOnlyList<PromptAttachmentDto>? attachments = null,
                CancellationToken cancellationToken = default)
                => throw new NotSupportedException("These tests never steer.");

            public Task SetModelAsync(string modelId, CancellationToken cancellationToken = default)
                => Task.CompletedTask;

            public Task<ListBackendSessionsResponse> ListBackendSessionsAsync(
                ListBackendSessionsRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new ListBackendSessionsResponse(
                    false, "not supported", Array.Empty<BackendSessionDto>()));

            public Task<TakeImportedHistoryResponse> TakeImportedHistoryAsync(
                TakeImportedHistoryRequest request, CancellationToken cancellationToken = default)
                => throw new NotSupportedException("These tests import nothing.");

            public Task<SummarizeResponse> SummarizeAsync(
                SummarizeRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new SummarizeResponse("the earlier work, recapped"));
        }
    }
}
