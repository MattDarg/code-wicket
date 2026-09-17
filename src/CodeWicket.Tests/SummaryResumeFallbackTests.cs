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
    /// What a send does when the summary resume the user chose cannot be produced (issue #84, the
    /// interim behaviour).
    ///
    /// <para><b>The defect these pin is one of omission, which is why they assert on the offer and not
    /// only on the outcome.</b> The old path caught the failure, added a notice and carried straight on
    /// into a fresh session — so the user asked for a recap, was told the recap failed, and then got
    /// the one remaining option taken on their behalf without being asked, with a full resume sitting
    /// available beside it. Every assertion below would have passed on "it didn't crash"; what makes
    /// them load-bearing is that they require the CHOICE to exist and require the send to be still
    /// waiting when it does.</para>
    /// </summary>
    public sealed class SummaryResumeFallbackTests : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "cwkt-resume-fallback-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch (IOException) { }
        }

        /// <summary>
        /// The whole point: the send stops and asks, rather than reaching the backend having quietly
        /// decided for itself. Asserted as "nothing has been prompted yet", because a send that had
        /// already gone out could not be taken back by any answer the user then gave.
        /// </summary>
        [Fact]
        public void AFailedSummaryStopsAndOffersTheTwoRealChoices() => RunSta(() =>
        {
            var engine = SameBackendEngine();
            var vm = Resumable(engine);

            SendAndChooseSummary(vm);

            Assert.True(vm.HasPendingResume);
            Assert.Equal("Couldn't summarize this conversation", vm.PendingResume!.Heading);
            Assert.True(vm.PendingResume.AllowFull);
            Assert.True(vm.PendingResume.AllowFresh);
            Assert.Empty(engine.Prompts);
        });

        /// <summary>
        /// Summary is not re-offered — it is the thing that just failed — and there is no way out: by
        /// this point the message is in the transcript and in the saved log, so the only open question
        /// is which context it goes out with.
        /// </summary>
        [Fact]
        public void TheFailureBannerOffersNeitherAnotherSummaryNorACancel() => RunSta(() =>
        {
            var vm = Resumable(SameBackendEngine());

            SendAndChooseSummary(vm);

            Assert.False(vm.PendingResume!.AllowSummary);
            Assert.False(vm.PendingResume.AllowCancel);
        });

        /// <summary>
        /// Choosing full context resumes the backend conversation for real — the send it parked is the
        /// one that goes, carrying the resume id, rather than a fresh session being started beside it.
        /// </summary>
        [Fact]
        public void ChoosingFullContextResumesTheConversationAndReleasesTheSend() => RunSta(() =>
        {
            var engine = SameBackendEngine();
            var vm = Resumable(engine);

            SendAndChooseSummary(vm);
            vm.PendingResume!.ResumeFullCommand.Execute(null);
            Drain();

            Assert.Equal("conv-1", engine.ResumedConversationId);
            Assert.Equal(new[] { "what changed?" }, engine.Prompts.Select(LastLine).ToArray());
            Assert.False(vm.HasPendingResume);
        });

        /// <summary>
        /// Starting fresh is still available and still does what it always did — the difference is that
        /// it is now chosen rather than defaulted into. It says so in the transcript, because the
        /// message goes out to an agent with no memory of everything above it.
        /// </summary>
        [Fact]
        public void ChoosingFreshSendsWithNoHistoryAndSaysSo() => RunSta(() =>
        {
            var engine = SameBackendEngine();
            var vm = Resumable(engine);

            SendAndChooseSummary(vm);
            vm.PendingResume!.ResumeFreshCommand.Execute(null);
            Drain();

            Assert.Null(engine.ResumedConversationId);
            Assert.Single(engine.Prompts);
            Assert.Contains(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.Contains("no history of it", StringComparison.Ordinal));
        });

        /// <summary>
        /// A cross-backend conversation cannot reload its full context anywhere, so the banner does not
        /// offer it. The question is then a confirmation rather than a choice — and it is still asked,
        /// because "your recap did not go" is the part the user has to hear before the message does.
        /// </summary>
        [Fact]
        public void ACrossBackendConversationIsOfferedOnlyFresh() => RunSta(() =>
        {
            var vm = Resumable(SameBackendEngine(), persistedProviderId: "other");

            SendAndChooseSummary(vm);

            Assert.True(vm.HasPendingResume);
            Assert.False(vm.PendingResume!.AllowFull);
            Assert.True(vm.PendingResume.AllowFresh);
        });

        /// <summary>
        /// A summarize call that succeeds and answers with nothing is still a failure. It used to be the
        /// silent one — the throwing path said why, this one said nothing at all — so it now both
        /// reports itself and reaches the same offer.
        /// </summary>
        [Fact]
        public void AnEmptySummaryIsReportedRatherThanPassedOverInSilence() => RunSta(() =>
        {
            var vm = Resumable(SameBackendEngine());

            SendAndChooseSummary(vm);

            Assert.Contains(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.Contains("empty summary", StringComparison.Ordinal));
        });

        /// <summary>
        /// The backend's own account of the failure survives to the banner: the notice above it is what
        /// says WHY, and the user is choosing on the strength of it.
        /// </summary>
        [Fact]
        public void AThrownSummarizeKeepsTheBackendsReasonAndStillOffersTheChoice() => RunSta(() =>
        {
            var engine = SameBackendEngine();
            engine.SummarizeThrows = new InvalidOperationException("context window exceeded");
            var vm = Resumable(engine);

            SendAndChooseSummary(vm);

            Assert.Contains(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.Contains("context window exceeded", StringComparison.Ordinal));
            Assert.True(vm.HasPendingResume);
        });

        /// <summary>
        /// The parked send is released by the banner going away for ANY reason, not only by a click on
        /// it. New Session replaces the conversation the message belonged to, so nothing is sent — and
        /// crucially the send does not sit there for the life of the window awaiting an answer that can
        /// no longer be given, which is what a wait keyed only on the two buttons would do.
        /// </summary>
        [Fact]
        public void ReplacingTheConversationReleasesTheParkedSendWithoutSendingIt() => RunSta(() =>
        {
            var engine = SameBackendEngine();
            var vm = Resumable(engine);

            SendAndChooseSummary(vm);
            vm.NewSessionCommand.Execute(null);
            Drain();

            Assert.False(vm.HasPendingResume);
            Assert.Empty(engine.Prompts);
            // The send's finally ran: the pane is usable again rather than stuck reporting work.
            Assert.False(vm.IsBusy);
        });

        // ---- helpers --------------------------------------------------------------------------

        // Drives a restored conversation to the point of failure: send, take the "resume from summary"
        // branch at the ordinary banner, and let the summarize come back with nothing.
        private static void SendAndChooseSummary(ChatViewModel vm)
        {
            vm.InputText = "what changed?";
            vm.SendCommand.Execute(null);
            Drain();

            Assert.True(vm.HasPendingResume);
            vm.PendingResume!.ResumeSummaryCommand.Execute(null);
            Drain();
        }

        // A restored conversation big enough that the send-time banner offers the full-vs-summary
        // choice (ResumeDecider's threshold is 4000 chars of transcript).
        private ChatViewModel Resumable(StubEngine engine, string persistedProviderId = "fake")
        {
            var store = new FileSessionStore(_dir);
            var session = new PersistedSession
            {
                WorkspaceRootPath = _dir,
                ConversationId = "conv-1",
                ProviderId = persistedProviderId,
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

        private static StubEngine SameBackendEngine() => new();

        private static string LastLine(string prompt)
        {
            var lines = prompt.Split('\n');
            return lines[lines.Length - 1].Trim();
        }

        private static void Drain() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        private static void RunSta(Action action) => StaTest.Run(action, withDispatcherContext: true);

        /// <summary>
        /// A backend that resumes, records what it was asked to prompt, and whose summarizer always
        /// fails — the condition under test. Turns complete immediately: nothing here is about a turn.
        /// </summary>
        private sealed class StubEngine : IEngineConnection
        {
            public List<string> Prompts { get; } = new();

            public string? ResumedConversationId { get; private set; }

            public Exception? SummarizeThrows { get; set; }

            public event Action<AgentEventDto>? AgentEvent { add { } remove { } }

            public event Action<ProviderModelsDto>? ProviderModelsRefreshed { add { } remove { } }

            // A fake with no handshake reports no session, which the panel renders as
            // "no agent session open yet" rather than as absent facts (issue #160).
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
                ResumedConversationId = request.ResumeConversationId;
                return Task.FromResult(new StartSessionResponse(request.ResumeConversationId ?? "conv-new"));
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

            /// <summary>Always fails — by exception when one is set, otherwise by answering with nothing.</summary>
            public Task<SummarizeResponse> SummarizeAsync(
                SummarizeRequest request, CancellationToken cancellationToken = default)
                => SummarizeThrows is not null
                    ? Task.FromException<SummarizeResponse>(SummarizeThrows)
                    : Task.FromResult(new SummarizeResponse(string.Empty));
        }
    }
}
