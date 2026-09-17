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
    /// What a send does when the backend REFUSES to reload the conversation and the provider falls
    /// through to a fresh session (issue #268). Captured live 2026-09-10 against kiro-cli-chat 2.21.1:
    /// a conversation created under the v3 agent engine, reopened with v2 running, answered
    /// <c>session/load</c> with "Session not found: sess_…" — and 6 ms later a <c>session/new</c> went
    /// out, and 1.9 s after that the user's "Can you retry?" went into it. Nothing was asked.
    ///
    /// <para><b>These are the mirror of <see cref="SummaryResumeFallbackTests"/>, and they exist
    /// because that fix stopped at the route it was filed about.</b> Both paths reach the identical
    /// state — a fresh session, a transcript full of history the agent cannot see, a message about to
    /// go out — and only one of them asked. So, as there, the assertions are on the OFFER and not only
    /// on the outcome: every one of them would pass on "it didn't crash", and what makes them
    /// load-bearing is that they require the choice to exist and require the send to be still waiting
    /// when it does.</para>
    ///
    /// <para>The two banners are not the same banner. There the full reload is what is left and the
    /// recap is what failed; here it is the other way round — which is why the recap is a real second
    /// answer here (it is built from OUR copy of the transcript, so it does not need the backend to
    /// have the conversation it has just disclaimed) and why the full reload is not re-offered.</para>
    /// </summary>
    public sealed class RefusedResumeTests : IDisposable
    {
        private const string Refusal = "Session not found: sess_old";

        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "cwkt-refused-resume-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch (IOException) { }
        }

        /// <summary>
        /// The defect itself. The send stops on a banner rather than putting an anaphoric message —
        /// "can you retry?" — into a session that has never heard of what it refers to. Asserted as
        /// "nothing has been prompted yet", because a prompt already on the wire could not be taken
        /// back by any answer the user then gave.
        /// </summary>
        [Fact]
        public void ARefusedReloadStopsAndAsksBeforeThePromptGoes() => RunSta(() =>
        {
            var engine = RefusingEngine();
            var vm = Resumable(engine);

            SendAndChooseFull(vm);

            Assert.True(vm.HasPendingResume);
            Assert.Equal("Couldn't reload this conversation", vm.PendingResume!.Heading);
            Assert.Empty(engine.Prompts);
            // The session it fell through to is open — that fall-through is correct and stays. What
            // was missing is only the ask, so this is what separates the fix from "don't start one".
            Assert.Equal(1, engine.StartCount);
        });

        /// <summary>
        /// The backend's own account of the refusal is what the user is deciding on, so it reaches the
        /// banner rather than only a notice they have to go looking for. Same rule as issue #82: a
        /// backend failure carries the backend's own words.
        ///
        /// <para>And it is carried APART from our explanation, so the view can draw it in the error
        /// colour (in Visual Studio, 2026-09-12). Run into the muted paragraph it read as a footnote — the one
        /// failure in the text, set in the quietest thing on the banner — while it is the fact being
        /// decided on and the one that gets pasted into a bug report. Asserted both ways round,
        /// because a `Reason` that also stayed inside `Detail` would draw it twice.</para>
        /// </summary>
        [Fact]
        public void TheBackendsReasonIsOnTheBannerApartFromOurOwnWords() => RunSta(() =>
        {
            var vm = Resumable(RefusingEngine());

            SendAndChooseFull(vm);

            Assert.Contains(Refusal, vm.PendingResume!.Reason ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(Refusal, vm.PendingResume.Detail, StringComparison.Ordinal);
        });

        /// <summary>
        /// The "its earlier messages are reloaded into the agent's context" line is written before the
        /// backend has been asked — it has to be, being what the transcript says while the start is in
        /// flight — so a refusal has to withdraw it. Left standing it contradicts the banner directly
        /// above the composer, at the one moment the user is reading both in order to decide.
        /// </summary>
        [Fact]
        public void TheReloadingNoticeIsWithdrawnBeforeTheUserIsAsked() => RunSta(() =>
        {
            var vm = Resumable(RefusingEngine());

            SendAndChooseFull(vm);

            Assert.True(vm.HasPendingResume);
            Assert.DoesNotContain(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.Contains("are reloaded into the agent", StringComparison.Ordinal));
        });

        /// <summary>
        /// The recap is a real second answer here — it is built from our own saved transcript by an
        /// out-of-band engine call, so it does not need the conversation the backend just disclaimed.
        /// Choosing it sends the recap WITH the message, into the session already open.
        /// </summary>
        [Fact]
        public void ChoosingTheRecapSendsItWithTheMessageIntoTheOpenSession() => RunSta(() =>
        {
            var engine = RefusingEngine();
            var vm = Resumable(engine);

            SendAndChooseFull(vm);
            Assert.True(vm.PendingResume!.AllowSummary);
            vm.PendingResume.ResumeSummaryCommand.Execute(null);
            Drain();

            var prompt = Assert.Single(engine.Prompts);
            Assert.Contains("conversation-summary", prompt, StringComparison.Ordinal);
            Assert.Contains("recap of the earlier work", prompt, StringComparison.Ordinal);
            Assert.Equal("what changed?", LastLine(prompt));
            // One session throughout: the recap rides the first prompt of the session the refusal
            // fell through to, rather than starting a second one beside it.
            Assert.Equal(1, engine.StartCount);
        });

        /// <summary>
        /// And the transcript then says what the agent actually got. The notice is one notice either
        /// way, worded for the outcome — plain here, because the agent DOES have an account of the
        /// messages above and the refusal is a fact about how it got there, not a warning.
        /// </summary>
        [Fact]
        public void TheRecapOutcomeIsReportedPlainlyRatherThanAsALossOfHistory() => RunSta(() =>
        {
            var vm = Resumable(RefusingEngine());

            SendAndChooseFull(vm);
            vm.PendingResume!.ResumeSummaryCommand.Execute(null);
            Drain();

            var notice = Assert.Single(
                vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.Contains("Couldn't reload this conversation", StringComparison.Ordinal));
            Assert.Contains("condensed recap", notice.Text, StringComparison.Ordinal);
            Assert.Contains(Refusal, notice.Text, StringComparison.Ordinal);
            Assert.NotEqual(NoticeKind.Error, notice.Kind);
        });

        /// <summary>
        /// Sending anyway is still available and still does exactly what used to happen — the
        /// difference is that it is now chosen rather than defaulted into. It keeps the red notice,
        /// because on this outcome the transcript really does keep showing a conversation the agent
        /// cannot see, which is the notice that has to survive being scrolled past.
        /// </summary>
        [Fact]
        public void SendingAnywayGoesWithNoHistoryAndKeepsSayingSoInRed() => RunSta(() =>
        {
            var engine = RefusingEngine();
            var vm = Resumable(engine);

            SendAndChooseFull(vm);
            vm.PendingResume!.ResumeFreshCommand.Execute(null);
            Drain();

            var prompt = Assert.Single(engine.Prompts);
            Assert.DoesNotContain("conversation-summary", prompt, StringComparison.Ordinal);
            var notice = Assert.Single(
                vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.Contains("Couldn't reload this conversation", StringComparison.Ordinal));
            Assert.Contains("doesn't have the messages above it", notice.Text, StringComparison.Ordinal);
            Assert.Equal(NoticeKind.Error, notice.Kind);
        });

        /// <summary>
        /// The button says what it will do. By the time this banner is up the fresh session exists —
        /// it is what the refusal fell through to — so the label the other banner uses, "Start fresh",
        /// would describe a step that has already happened and imply there is still something to back
        /// out of. There isn't: no Cancel here, and no full reload, which is the thing that just failed.
        /// </summary>
        [Fact]
        public void TheBannerOffersNeitherAWayOutNorTheReloadThatJustFailed() => RunSta(() =>
        {
            var vm = Resumable(RefusingEngine());

            SendAndChooseFull(vm);

            Assert.False(vm.PendingResume!.AllowCancel);
            Assert.False(vm.PendingResume.AllowFull);
            Assert.True(vm.PendingResume.AllowFresh);
            Assert.Equal("Send anyway", vm.PendingResume.FreshLabel);
        });

        /// <summary>
        /// The two failures can arrive in sequence: the recap fails, the user answers that banner with
        /// the full reload instead, and the backend refuses that too. The second banner must not then
        /// offer the recap — it is the thing that already failed on this very send, which is the one
        /// button the first banner deliberately does not draw for exactly this reason. What is left is
        /// a confirmation, and it is still asked, because "your history did not go" is the part the
        /// user has to hear before the message does.
        /// </summary>
        [Fact]
        public void ARecapThatAlreadyFailedThisSendIsNotOfferedAgain() => RunSta(() =>
        {
            var engine = RefusingEngine();
            engine.SummarizeReturnsNothing = true;
            var vm = Resumable(engine);

            // The ordinary banner → summary → the summarizer fails → the #84 banner → full reload →
            // refused → this one.
            vm.InputText = "what changed?";
            vm.SendCommand.Execute(null);
            Drain();
            vm.PendingResume!.ResumeSummaryCommand.Execute(null);
            Drain();
            Assert.Equal("Couldn't summarize this conversation", vm.PendingResume!.Heading);
            vm.PendingResume.ResumeFullCommand.Execute(null);
            Drain();

            Assert.Equal("Couldn't reload this conversation", vm.PendingResume!.Heading);
            Assert.False(vm.PendingResume.AllowSummary);
            Assert.True(vm.PendingResume.AllowFresh);
            Assert.Empty(engine.Prompts);
        });

        /// <summary>
        /// A recap chosen and then not produced needs no third banner — the only remaining answer is
        /// the one already on offer. What it does need is for the notice to report what HAPPENED
        /// rather than what was picked: the message went with no history, so it is the red one, beside
        /// the summarizer's own account of why.
        /// </summary>
        [Fact]
        public void ARecapChosenAndThenNotProducedReportsTheOutcomeItActuallyHad() => RunSta(() =>
        {
            var engine = RefusingEngine();
            var vm = Resumable(engine);

            SendAndChooseFull(vm);
            engine.SummarizeReturnsNothing = true;
            vm.PendingResume!.ResumeSummaryCommand.Execute(null);
            Drain();

            var prompt = Assert.Single(engine.Prompts);
            Assert.DoesNotContain("conversation-summary", prompt, StringComparison.Ordinal);
            Assert.Contains(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.Contains("empty summary", StringComparison.Ordinal));
            Assert.Contains(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Kind == NoticeKind.Error
                     && n.Text.Contains("doesn't have the messages above it", StringComparison.Ordinal));
            Assert.False(vm.HasPendingResume);
        });

        /// <summary>
        /// The parked send is released by the banner going away for ANY reason, not only by a click on
        /// it — the property setter's rule, which this path inherits by using the same wait. New
        /// Session replaces the conversation the message belonged to, so nothing is sent, and the send
        /// does not sit there for the life of the window awaiting an answer that can no longer be given.
        /// </summary>
        [Fact]
        public void ReplacingTheConversationReleasesTheParkedSendWithoutSendingIt() => RunSta(() =>
        {
            var engine = RefusingEngine();
            var vm = Resumable(engine);

            SendAndChooseFull(vm);
            vm.NewSessionCommand.Execute(null);
            Drain();

            Assert.False(vm.HasPendingResume);
            Assert.Empty(engine.Prompts);
            // The send's finally ran: the pane is usable again rather than stuck reporting work.
            Assert.False(vm.IsBusy);
        });

        /// <summary>
        /// A resume that is NOT refused asks nothing at all. The banner is keyed on the refusal the
        /// provider reported, so an ordinary resume goes straight out — which is the regression this
        /// pins, a fix of this shape being easy to write as "ask on every restored conversation".
        /// </summary>
        [Fact]
        public void AnAcceptedReloadAsksNothing() => RunSta(() =>
        {
            var engine = RefusingEngine();
            engine.RefuseResume = false;
            var vm = Resumable(engine);

            SendAndChooseFull(vm);

            Assert.False(vm.HasPendingResume);
            Assert.Equal("conv-1", engine.ResumedConversationId);
            Assert.Single(engine.Prompts);
            Assert.DoesNotContain(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.Contains("Couldn't reload this conversation", StringComparison.Ordinal));
        });

        // ---- helpers --------------------------------------------------------------------------

        // Drives a restored conversation to the point of refusal: send, take the "resume full context"
        // branch at the ordinary banner, and let the backend disclaim the conversation.
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

        private static StubEngine RefusingEngine() => new();

        private static string LastLine(string prompt)
        {
            var lines = prompt.Split('\n');
            return lines[lines.Length - 1].Trim();
        }

        private static void Drain() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        private static void RunSta(Action action) => StaTest.Run(action, withDispatcherContext: true);

        /// <summary>
        /// A backend that refuses the reload the way the live one did — a fresh session on a new id,
        /// with the refusal reported beside it — and whose summarizer works unless told otherwise.
        /// Turns complete immediately: nothing here is about a turn.
        /// </summary>
        private sealed class StubEngine : IEngineConnection
        {
            public List<string> Prompts { get; } = new();

            public string? ResumedConversationId { get; private set; }

            public int StartCount { get; private set; }

            /// <summary>Answers <c>session/load</c> with a fall-through, as kiro-cli v2 did for a v3 id.</summary>
            public bool RefuseResume { get; set; } = true;

            public bool SummarizeReturnsNothing { get; set; }

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
                StartCount++;
                ResumedConversationId = request.ResumeConversationId;
                var refused = RefuseResume && request.ResumeConversationId is { Length: > 0 };
                return Task.FromResult(new StartSessionResponse(
                    refused ? "conv-new" : request.ResumeConversationId ?? "conv-new",
                    ResumeFailureReason: refused ? Refusal : null));
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
                => Task.FromResult(new SummarizeResponse(
                    SummarizeReturnsNothing ? string.Empty : "A recap of the earlier work."));
        }
    }
}
