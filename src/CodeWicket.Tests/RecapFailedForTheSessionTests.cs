using System;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using CodeWicket.Ipc;
using CodeWicket.Shell;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// A recap that has FAILED is not offered again while the same session lives, whichever route it
    /// failed on, and the way back is a new session (user decision, 2026-09-16).
    /// </summary>
    /// <remarks>
    /// <para><b>The edge this closes.</b> The fact lived in two places with different lifetimes: a local
    /// inside one send, and a flag on the session set ONLY through the refused-reload route. So a recap
    /// that failed on the pure #84 route marked nothing - back out, send again, and the Choice banner
    /// offered the recap that had just failed, as a recommended answer. #84 and #268 answered the same
    /// question differently.</para>
    /// <para><b>Transient is deliberately not told from permanent</b> (user, explicitly). A rate limit and
    /// a version mismatch arrive identically, and guessing wrong permanently removes a working feature
    /// from a conversation. So a transient failure costs a reopen, not the recap.</para>
    /// <para><b>And there is no "try the recap again" button anywhere.</b> That is the retry #84 refused:
    /// it puts the thing that just failed where people click by reflex, and nothing has changed since.</para>
    /// </remarks>
    public sealed class RecapFailedForTheSessionTests : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "cwkt-recap-failed-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch (IOException) { }
        }

        /// <summary>
        /// The edge itself: a recap fails on the #84 route, the user backs out, and the next send's
        /// Choice banner must not offer the recap again.
        /// </summary>
        /// <remarks>
        /// Asserted on the Choice banner specifically - heading and all - because that is the banner the
        /// old code reached with no memory of the failure. The send is still ASKED (the conversation is
        /// still resumable in full), so this is about which answers are on it, not about skipping it.
        /// </remarks>
        [Fact]
        public void ARecapThatFailedIsNotOfferedOnTheNextSendsChoiceBanner() => RunSta(() =>
        {
            var engine = FailingRecapEngine();
            var vm = Resumable(engine);

            SendAndChooseSummary(vm);
            Assert.Equal("Couldn't summarize this conversation", vm.PendingResume!.Heading);
            vm.PendingResume.CancelCommand.Execute(null);
            Drain();

            vm.SendCommand.Execute(null); // the restored text
            Drain();

            Assert.Equal("Continue this conversation?", vm.PendingResume!.Heading);
            Assert.False(
                vm.PendingResume.AllowSummary,
                "the Choice banner offered the recap that had already failed on this session");
            Assert.True(vm.PendingResume.AllowFull, "the full reload still works and is what is left");
            Assert.Empty(engine.Prompts);
        });

        /// <summary>
        /// The way back, which is the half that usually goes untested: reopening the conversation is a new
        /// session, so the recap is offered again. Without this, a withholding that never lifted would
        /// pass every other test here.
        /// </summary>
        [Fact]
        public void ReopeningTheConversationOffersTheRecapAgain() => RunSta(() =>
        {
            var engine = FailingRecapEngine();
            var other = SaveOther();
            var vm = Resumable(engine);
            var original = CurrentId(vm);

            SendAndChooseSummary(vm);
            vm.PendingResume!.CancelCommand.Execute(null);
            Drain();

            // Away and back: the session the failure belonged to is gone.
            Open(vm, other);
            Open(vm, original);

            vm.InputText = "what changed?";
            vm.SendCommand.Execute(null);
            Drain();

            Assert.Equal("Continue this conversation?", vm.PendingResume!.Heading);
            Assert.True(
                vm.PendingResume.AllowSummary,
                "reopening the conversation is a new session, so the recap is on offer again");
        });

        /// <summary>
        /// The other way back that is observable: a backend change. The conversation stays on screen with
        /// its transcript, so the next send still asks - and the recap is on offer again, because the next
        /// send starts a session on the new backend.
        /// </summary>
        /// <remarks>
        /// <b>New is deliberately not tested here, and cannot be.</b> After New there is no earlier
        /// transcript to recap, so no resume banner is reachable at all and "the recap is offered again"
        /// has nothing to observe. Asserting it would mean reaching into the lifetime for a flag, which
        /// pins the implementation rather than the behaviour the rule is about.
        /// </remarks>
        [Fact]
        public void ChangingTheBackendOffersTheRecapAgain() => RunSta(() =>
        {
            var engine = FailingRecapEngine().WithProvider("other", "Other");
            var vm = Resumable(engine);

            SendAndChooseSummary(vm);
            vm.PendingResume!.CancelCommand.Execute(null);
            Drain();

            vm.SelectedProvider = vm.Providers.Single(p => p.Id == "other");
            Drain();

            vm.InputText = "what changed?";
            vm.SendCommand.Execute(null);
            Drain();

            Assert.True(vm.HasPendingResume, "a cross-backend continuation still asks before it sends");
            Assert.True(
                vm.PendingResume!.AllowSummary,
                "a backend change starts a new session, so the recap is on offer again");
        });

        /// <summary>
        /// #84's own banner says what has happened and the way back, in THIS SESSION's terms. Wording is
        /// behaviour here, so it is asserted rather than left to review.
        /// </summary>
        [Fact]
        public void TheRecapFailedBannerNamesTheWayBack() => RunSta(() =>
        {
            var vm = Resumable(FailingRecapEngine());

            SendAndChooseSummary(vm);

            Assert.Equal("Couldn't summarize this conversation", vm.PendingResume!.Heading);
            Assert.Contains(
                ResumeChoiceViewModel.RecapWayBack, vm.PendingResume.Detail, StringComparison.Ordinal);
        });

        /// <summary>
        /// #268's banner withholds the recap on the same session fact, and says WHY rather than drawing one
        /// button fewer — while the backend's own account of the refusal keeps its separate line (#82).
        /// </summary>
        /// <remarks>
        /// The distinction this pins: "no recap is offered because one failed" is not "there is no recap to
        /// send", which is what this banner said for both. One is a fact about this session that the user
        /// can act on; the other is a fact about the conversation that they cannot.
        /// </remarks>
        [Fact]
        public void TheRefusedReloadBannerSaysWhyTheRecapIsWithheld() => RunSta(() =>
        {
            var engine = FailingRecapEngine();
            engine.RefuseResume = true;
            engine.RefusalReason = "Session not found: sess_old";
            var vm = Resumable(engine);

            SendAndChooseSummary(vm);
            vm.PendingResume!.ResumeFullCommand.Execute(null);
            Drain();

            Assert.Equal("Couldn't reload this conversation", vm.PendingResume!.Heading);
            Assert.False(vm.PendingResume.AllowSummary, "the recap that failed was offered again");
            Assert.Contains(
                ResumeChoiceViewModel.RecapWithheld, vm.PendingResume.Detail, StringComparison.Ordinal);
            Assert.Contains(
                "Session not found", vm.PendingResume.Reason ?? string.Empty, StringComparison.Ordinal);
        });

        /// <summary>
        /// And the Choice banner explains the absence too, on the next send. A button that is simply gone
        /// reads as a bug; this one is gone for a reason the user can act on.
        /// </summary>
        [Fact]
        public void TheChoiceBannerSaysWhyNoRecapIsOffered() => RunSta(() =>
        {
            var vm = Resumable(FailingRecapEngine());

            SendAndChooseSummary(vm);
            vm.PendingResume!.CancelCommand.Execute(null);
            Drain();
            vm.SendCommand.Execute(null);
            Drain();

            Assert.Equal("Continue this conversation?", vm.PendingResume!.Heading);
            Assert.Contains(
                ResumeChoiceViewModel.RecapWithheld, vm.PendingResume.Detail, StringComparison.Ordinal);
        });

        /// <summary>
        /// A conversation that can only ever continue from a recap must still have an answer that SENDS
        /// once that recap has failed (scoped review of the recap rule, 2026-09-16).
        /// </summary>
        /// <remarks>
        /// <para>The state: a restored conversation whose backend differs from the picker cannot reload its
        /// full context, so the decider returns PromptSummaryOnly and the Choice banner draws with
        /// <c>allowFull: false</c>. Withhold the recap as well and the banner's three answering buttons are
        /// all hidden - <c>Choice</c> hardcodes <c>allowFresh: false</c> - leaving Cancel alone. Every later
        /// send lands on the same banner, so the conversation cannot be sent to at all.</para>
        /// <para><b>Asserted on the state, not the route.</b> What matters is not which button appears but
        /// that at least one answer sends: a banner whose only answer is Cancel is a dead end however it was
        /// reached. Withholding the recap was safe before this rule only because the flag was hardcoded true.</para>
        /// </remarks>
        [Fact]
        public void ACrossBackendConversationWhoseRecapFailedStillHasAWayToSend() => RunSta(() =>
        {
            var engine = FailingRecapEngine();
            var vm = Resumable(engine, persistedProviderId: "other");

            SendAndChooseSummary(vm);
            Assert.Equal("Couldn't summarize this conversation", vm.PendingResume!.Heading);
            vm.PendingResume.CancelCommand.Execute(null);
            Drain();

            vm.SendCommand.Execute(null); // the restored text
            Drain();

            Assert.Equal("Continue this conversation?", vm.PendingResume!.Heading);
            Assert.False(vm.PendingResume.AllowSummary, "the recap that failed was offered again");
            Assert.True(
                vm.PendingResume.AllowFull || vm.PendingResume.AllowSummary || vm.PendingResume.AllowFresh,
                "the banner's only answer is Cancel, so this conversation can never be sent to again");
        });

        /// <summary>
        /// The Choice banner's "Start new conversation" carries the TRAY across, being exactly New
        /// followed by the send, as the refused-reload banner's own start-over does.
        /// </summary>
        /// <remarks>
        /// New clears the held messages on its way through <c>ClearTranscript</c>, so a follow-up typed while
        /// the banner was up has to be put back by hand. Rescuing only the PARKED message destroys the held
        /// one with no notice, no composer text and no transcript row - the same silent-loss shape the parked
        /// message's own rescue exists for. Put back BEFORE the resend, or a turn that ends at once releases
        /// an empty tray and strands it.
        /// </remarks>
        [Fact]
        public void TheChoiceBannersFreshAnswerCarriesHeldMessages() => RunSta(() =>
        {
            const string Follow = "and check the tests too";

            var engine = FailingRecapEngine();
            // So a carried picture rides the prompt as an attachment rather than as the text fallback a
            // backend without image support gets - the count below has to be able to see it on the wire.
            engine.SupportsImages = true;
            var vm = Resumable(engine, persistedProviderId: "other");

            SendAndChooseSummary(vm);
            vm.PendingResume!.CancelCommand.Execute(null);
            Drain();
            vm.SendCommand.Execute(null); // the restored text: the Choice banner, with fresh the only answer
            Drain();
            Assert.Equal("Continue this conversation?", vm.PendingResume!.Heading);
            Assert.True(vm.PendingResume.AllowFresh, "no fresh answer to take, so this proves nothing");

            // A follow-up typed while the banner is up lands in the TRAY, behind the parked send - with
            // its chips, which the tray takes OFF the composer as it holds the message. So they ride the
            // carry or they go with it, and nothing on screen says which.
            vm.PendingAttachments.Add(new AttachmentViewModel(
                "snip.png", "image/png", new byte[] { 1, 2, 3 }, filePath: null, remove: _ => { }));
            vm.InputText = Follow;
            vm.SendCommand.Execute(null);
            Drain();
            Assert.Equal(Follow, Assert.Single(vm.PendingMessages).Text);
            Assert.Equal("snip.png", Assert.Single(Assert.Single(vm.PendingMessages).Attachments).Name);

            vm.PendingResume!.ResumeFreshCommand.Execute(null);
            for (var i = 0; i < 4; i++)
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

            // A COUNT, and the two places are counted TOGETHER because whether the resend's turn ended
            // inside the pumps is not what is under test. Exactly one: zero is the defect, and it is
            // silent - no notice, no composer text, no transcript row, the pane entirely healthy - while
            // two would be a carry that also left the message behind.
            var held = vm.PendingMessages.Count(m => m.Text == Follow);
            var sent = engine.Prompts.Count(p => p.Contains(Follow, StringComparison.Ordinal));
            Assert.True(
                held + sent == 1,
                $"the held message is in the tray {held} time(s) and on the wire {sent} time(s); exactly one is right");
            // The PICTURE counted the same way, and unconditionally. Asserting it only on the held
            // branch made it skip whenever the resend's turn ended inside the pumps - which is most
            // runs - so the check measured less than it claimed and an injection rebuilding the carried
            // messages from their TEXT came back PINS NOTHING. Counted on both sides, it cannot skip.
            var heldPictures = vm.PendingMessages
                .Where(m => m.Text == Follow)
                .Sum(m => m.Attachments.Count(a => a.Name == "snip.png"));
            var sentPictures = engine.PromptAttachments.Sum(a => a.Count);
            Assert.True(
                heldPictures + sentPictures == 1,
                $"the picture is on the held row {heldPictures} time(s) and on the wire {sentPictures} "
                + "time(s); exactly one is right, and zero is the tray carry dropping it in silence");

            // And the PARKED message is carried, not copied. This route clears the transcript, which ends
            // the parked send and hands its message back to the composer - so it has to leave the
            // field before the clear, or the message is both sent into the new conversation and left in
            // the box for the user to send again.
            Assert.Equal(string.Empty, vm.InputText);
        });

        /// <summary>
        /// A backend change discharges the failed recap on a PROMPTED pane too.
        /// The way back is the user's gesture, not the pane's internal state.
        /// </summary>
        /// <remarks>
        /// The state the comment on <c>Supersede</c> claimed was unreachable: <c>Supersede</c> KEEPS
        /// <c>_prompted</c>, and on the #84 route the recap fails before any session start, so backing out
        /// leaves a prompted pane carrying a failed recap with no <c>Commit</c> in between. The clear was
        /// wired only into the unprompted branch, so the second backend change never discharged it.
        /// </remarks>
        [Fact]
        public void ChangingTheBackendOnAPromptedPaneOffersTheRecapAgain() => RunSta(() =>
        {
            // Three backends, and the third is load-bearing: flipping BACK to the first would re-adopt the
            // very session this pane superseded (ChatViewModel's IsSupersededSession branch), which sets
            // prompted and clears superseded - so the next send is no longer a first continuation, no banner
            // appears, and the test would be asserting against a null. Re-adopting is also the one picker
            // change that should NOT discharge the failure: it is the same session, so a recap that failed on
            // it is still this session's.
            var engine = FailingRecapEngine().WithProvider("other", "Other").WithProvider("third", "Third");
            var vm = Resumable(engine);

            // Prompt once, so the pane is PROMPTED rather than merely open.
            vm.PendingResume?.CancelCommand.Execute(null);
            vm.InputText = "first";
            vm.SendCommand.Execute(null);
            Drain();
            if (vm.PendingResume is { } opening)
            {
                opening.ResumeFullCommand.Execute(null);
                Drain();
            }

            Assert.Single(engine.Prompts);

            // A backend change supersedes it - still prompted - and the next send asks again.
            vm.SelectedProvider = vm.Providers.Single(p => p.Id == PendingSendScenario.OtherProvider);
            Drain();
            vm.InputText = "second";
            vm.SendCommand.Execute(null);
            Drain();

            Assert.Equal("Continue this conversation?", vm.PendingResume!.Heading);
            vm.PendingResume.ResumeSummaryCommand.Execute(null);
            Drain();
            Assert.Equal("Couldn't summarize this conversation", vm.PendingResume!.Heading);
            vm.PendingResume.CancelCommand.Execute(null);
            Drain();

            // A second backend change, to one the pane has never held: a new session there, so the recap is
            // on offer again. Not back to "fake", which would re-adopt rather than supersede.
            vm.SelectedProvider = vm.Providers.Single(p => p.Id == "third");
            Drain();
            vm.InputText = "third";
            vm.SendCommand.Execute(null);
            Drain();

            Assert.Equal("Continue this conversation?", vm.PendingResume!.Heading);
            Assert.True(
                vm.PendingResume.AllowSummary,
                "a backend change on a prompted pane never discharged the failed recap");
        });

        // ---- helpers --------------------------------------------------------------------------

        private static void SendAndChooseSummary(ChatViewModel vm)
        {
            vm.InputText = "what changed?";
            vm.SendCommand.Execute(null);
            Drain();

            Assert.True(vm.HasPendingResume);
            vm.PendingResume!.ResumeSummaryCommand.Execute(null);
            Drain();
        }

        // A restored conversation big enough that the send-time banner offers full-vs-summary
        // (ResumeDecider's threshold is 4000 chars of transcript).
        private ChatViewModel Resumable(ScriptedEngine engine, string persistedProviderId = "fake")
        {
            var store = new FileSessionStore(_dir);
            var session = new PersistedSession
            {
                WorkspaceRootPath = _dir,
                ConversationId = "conv-1",
                // A conversation whose backend differs from the picker cannot reload its full context, so
                // the decider returns PromptSummaryOnly and the Choice banner draws with no full-reload
                // button - the shape that left the conversation unsendable.
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

        // A second, older conversation to leave and come back through.
        private string SaveOther()
        {
            var session = new PersistedSession
            {
                WorkspaceRootPath = _dir,
                ProviderId = "fake",
                Title = "Something else",
                UpdatedUtc = DateTime.UtcNow.AddHours(-1),
            };
            session.Log.Add(new TranscriptEntry { Role = "user", Text = "an older question" });
            new FileSessionStore(_dir).Save(session);
            return session.Id;
        }

        // A backend that resumes and whose summarizer always comes back with nothing.
        private static ScriptedEngine FailingRecapEngine() =>
            new ScriptedEngine { SummarizeReturnsNothing = true, AllowSummarize = true }
                .WithProvider("fake", "Fake", "ResumeSession");

        private static string CurrentId(ChatViewModel vm)
        {
            vm.RefreshHistory();
            return vm.History.Single(h => h.IsCurrent).Id;
        }

        private static void Open(ChatViewModel vm, string sessionId)
        {
            vm.RefreshHistory();
            vm.History.Single(h => h.Id == sessionId).LoadCommand.Execute(null);
            Drain();
        }

        private static void Drain() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        private static void RunSta(Action action) => StaTest.Run(action, withDispatcherContext: true);
    }
}
