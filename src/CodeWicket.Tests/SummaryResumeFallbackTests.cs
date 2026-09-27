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
        /// Summary is not re-offered — it is the thing that just failed — but there IS a way out. The
        /// message is not recorded until this question is answered (F5, 2026-09-13), so backing out
        /// leaves nothing in the saved log to undo.
        /// </summary>
        [Fact]
        public void TheFailureBannerOffersAWayOutButNotAnotherSummary() => RunSta(() =>
        {
            var vm = Resumable(SameBackendEngine());

            SendAndChooseSummary(vm);

            Assert.False(vm.PendingResume!.AllowSummary);
            Assert.True(vm.PendingResume.AllowCancel);
        });

        /// <summary>
        /// Stop while the send is parked here. Stop cancels a TURN, and there is none — the prompt has
        /// not gone — so it used to do nothing, leaving the send parked and the pane busy. It now backs
        /// out as Cancel does: the message comes off the transcript and back into the input box.
        /// </summary>
        [Fact]
        public void StopWhileParkedGivesTheMessageBack() => RunSta(() =>
        {
            var engine = SameBackendEngine();
            var vm = Resumable(engine);

            SendAndChooseSummary(vm);
            vm.StopCommand.Execute(null);
            Drain();

            Assert.False(vm.HasPendingResume);
            Assert.Empty(engine.Prompts);
            Assert.False(vm.IsBusy);
            Assert.Equal("what changed?", vm.InputText);
            Assert.DoesNotContain(vm.Items.OfType<MessageItemViewModel>(), m => m.Text == "what changed?");
        });

        /// <summary>
        /// Backing out forgets the answer that led here. Kept, the next Enter reran
        /// the recap the user had just walked away from: the same failure, with no "Continue this
        /// conversation?" first.
        /// </summary>
        [Fact]
        public void TheNextMessageAfterBackingOutIsAskedFromTheStart() => RunSta(() =>
        {
            var engine = SameBackendEngine();
            var vm = Resumable(engine);

            SendAndChooseSummary(vm);
            vm.PendingResume!.CancelCommand.Execute(null);
            Drain();
            vm.SendCommand.Execute(null); // the restored text
            Drain();

            Assert.True(vm.HasPendingResume);
            Assert.Equal("Continue this conversation?", vm.PendingResume!.Heading);
            Assert.Empty(engine.Prompts);
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
        /// "Start new conversation" starts one (F5, 2026-09-13), as it does on every resume banner. It
        /// used to continue this conversation with no history of it; now the message goes into a new
        /// conversation, the screen is cleared, and this one is left in the history picker.
        /// </summary>
        [Fact]
        public void ChoosingANewConversationSendsTheMessageThere() => RunSta(() =>
        {
            var engine = SameBackendEngine();
            var vm = Resumable(engine);

            SendAndChooseSummary(vm);
            Assert.Equal("Start new conversation", vm.PendingResume!.FreshLabel);
            vm.PendingResume.ResumeFreshCommand.Execute(null);
            for (var i = 0; i < 4; i++)
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

            Assert.Null(engine.ResumedConversationId);
            Assert.Equal("what changed?", LastLine(Assert.Single(engine.Prompts)));
            Assert.DoesNotContain(vm.Items.OfType<MessageItemViewModel>(),
                m => m.Text.StartsWith("uuu", StringComparison.Ordinal));
            var notice = Assert.Single(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.Contains("Started a new conversation", StringComparison.Ordinal));
            // It names the conversation and no reason (user decision, 2026-09-19): the banner said why a
            // click earlier, and this notice is display-only. "Wasn't", because a full reload WAS on offer
            // and the user picked this over it, so "couldn't be continued" would be false.
            Assert.Contains("Earlier work", notice.Text, StringComparison.Ordinal);
            Assert.Contains("wasn't continued here", notice.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("empty summary", notice.Text, StringComparison.Ordinal);
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
        /// The parked send is released by the banner going away for ANY reason, not only by a click on it —
        /// crucially the send does not sit there for the life of the window awaiting an answer that can no
        /// longer be given, which is what a wait keyed only on the two buttons would do.
        /// </summary>
        /// <remarks>
        /// Driven by DELETING the conversation on screen rather than by New Session, which is refused while a
        /// send is pending. Delete stays ungated and reaches the same clear.
        /// </remarks>
        [Fact]
        public void ReplacingTheConversationReleasesTheParkedSendWithoutSendingIt() => RunSta(() =>
        {
            var engine = SameBackendEngine();
            var vm = Resumable(engine);

            SendAndChooseSummary(vm);
            vm.RefreshHistory();
            vm.History.Single(h => h.IsCurrent).DeleteCommand.Execute(null);
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
        private ChatViewModel Resumable(ScriptedEngine engine, string persistedProviderId = "fake")
        {
            var store = new FileSessionStore(_dir);
            var session = new PersistedSession
            {
                WorkspaceRootPath = _dir,
                ConversationId = "conv-1",
                ProviderId = persistedProviderId,
                Title = "Earlier work",
            };
            SeededConversation.AddExchange(session, new string('u', 2500), new string('a', 2500));
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

        // A backend that resumes, records what it was asked to prompt, and whose summarizer always fails -
        // by answering with nothing unless a test sets an exception. Turns complete immediately.
        private static ScriptedEngine SameBackendEngine() =>
            new ScriptedEngine { SummarizeReturnsNothing = true, AllowSummarize = true }.WithProvider("fake", "Fake", "ResumeSession");

        private static string LastLine(string prompt)
        {
            var lines = prompt.Split('\n');
            return lines[lines.Length - 1].Trim();
        }

        private static void Drain() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        private static void RunSta(Action action) => StaTest.Run(action, withDispatcherContext: true);
    }
}
