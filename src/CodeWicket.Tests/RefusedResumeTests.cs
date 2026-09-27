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
        /// "Start new conversation" does what it says (F5, 2026-09-13). It was "Send anyway": the
        /// message went into the session the refusal fell through to and the transcript stayed, under a
        /// red notice that the agent had none of it. Now it is New, then the message sent there: the
        /// screen is cleared, the conversation that could not be reloaded is left untouched on disk,
        /// and the notice says why the switch happened.
        /// </summary>
        [Fact]
        public void StartingANewConversationClearsTheTranscriptAndSendsThere() => RunSta(() =>
        {
            var engine = RefusingEngine();
            var vm = Resumable(engine);

            SendAndChooseFull(vm);
            vm.PendingResume!.ResumeFreshCommand.Execute(null);
            DrainAll();

            var prompt = Assert.Single(engine.Prompts);
            Assert.DoesNotContain("conversation-summary", prompt, StringComparison.Ordinal);
            Assert.Equal("what changed?", LastLine(prompt));

            // The earlier conversation is off the screen, and this message is the new one's first.
            Assert.DoesNotContain(vm.Items.OfType<MessageItemViewModel>(),
                m => m.Text.StartsWith("uuu", StringComparison.Ordinal) || m.Text.StartsWith("aaa", StringComparison.Ordinal));
            Assert.Contains(vm.Items.OfType<MessageItemViewModel>(), m => m.Text == "what changed?");
            var notice = Assert.Single(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.Contains("Started a new conversation", StringComparison.Ordinal));
            Assert.Contains("Earlier work", notice.Text, StringComparison.Ordinal);
            // The backend's reason is on the BANNER the user just answered, and nowhere else: this notice
            // is display-only and names none (user decision, 2026-09-19).
            Assert.DoesNotContain(Refusal, notice.Text, StringComparison.Ordinal);

            // New's own path: the fall-through session is replaced by a warm one, which the send takes.
            Assert.Equal(2, engine.StartCount);

            // On disk: the earlier conversation exactly as it was, and the message in the new one.
            var saved = SavedConversations();
            var earlier = Assert.Single(saved, c => c.ConversationId == "conv-1");
            Assert.DoesNotContain(earlier.Log, e => e.Text == "what changed?");
            var fresh = Assert.Single(saved, c => c.ConversationId != "conv-1");
            Assert.Contains(fresh.Log, e => e.Role == "user" && e.Text == "what changed?");
        });

        /// <summary>
        /// A message typed while the banner was up was held for this send's turn end - and that turn
        /// end is the unwinding send, in the conversation being left. It follows the message into the
        /// new conversation instead of being delivered into the old one or dropped by New.
        /// <para>Asserted as DELIVERED, there, second. The first version accepted
        /// "still in the tray" as well, which a start-over that stranded the tray also satisfied.</para>
        /// </summary>
        [Fact]
        public void AMessageHeldWhileTheBannerWasUpFollowsIntoTheNewConversation() => RunSta(() =>
        {
            var engine = RefusingEngine();
            var vm = Resumable(engine);

            SendAndChooseFull(vm);
            vm.InputText = "and the tests?";
            vm.SendCommand.Execute(null); // busy, so held
            Drain();
            Assert.Contains(vm.PendingMessages, m => m.Text == "and the tests?");

            vm.PendingResume!.ResumeFreshCommand.Execute(null);
            DrainAll();

            Assert.Equal(new[] { "what changed?", "and the tests?" }, engine.Prompts.Select(LastLine).ToArray());
            Assert.Empty(vm.PendingMessages);
            var saved = SavedConversations();
            var earlier = Assert.Single(saved, c => c.ConversationId == "conv-1");
            Assert.DoesNotContain(earlier.Log, e => e.Text == "and the tests?");
            var fresh = Assert.Single(saved, c => c.ConversationId != "conv-1");
            Assert.Contains(fresh.Log, e => e.Role == "user" && e.Text == "and the tests?");
        });

        /// <summary>
        /// The button says what it will do. By the time this banner is up the fresh session exists —
        /// it is what the refusal fell through to — so the label the other banner uses, "Start fresh",
        /// would describe a step that has already happened - so it now starts a new conversation and is
        /// labelled for that. No full reload, the thing that just failed; and a Cancel, because the
        /// message is not recorded until this is answered (F5, 2026-09-13).
        /// </summary>
        [Fact]
        public void TheBannerOffersAWayOutButNotTheReloadThatJustFailed() => RunSta(() =>
        {
            var vm = Resumable(RefusingEngine());

            SendAndChooseFull(vm);

            Assert.True(vm.PendingResume!.AllowCancel);
            Assert.False(vm.PendingResume.AllowFull);
            Assert.True(vm.PendingResume.AllowFresh);
            Assert.Equal("Start new conversation", vm.PendingResume.FreshLabel);
        });

        // Every conversation saved under this workspace (or another root), read back from disk.
        private List<PersistedSession> SavedConversations(string? root = null)
        {
            var store = new FileSessionStore(_dir);
            var workspace = root ?? _dir;
            return store.List(workspace)
                .Select(summary => store.Load(workspace, summary.Id))
                .Where(session => session is not null)
                .Select(session => session!)
                .ToList();
        }

        // Every user message in every conversation saved under this workspace (or another root).
        private IEnumerable<string?> SavedUserTexts(string? root = null) =>
            SavedConversations(root)
                .SelectMany(session => session.Log)
                .Where(entry => entry.Role == "user")
                .Select(entry => entry.Text)
                .ToList();

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
        /// A recap chosen and then not produced asks again, with the recap withheld.
        /// It used to send with no history, on the reasoning that that was the only other answer on
        /// offer. Since the fresh answer became "Start new conversation" it is not, so taking it
        /// silently would be the #84 defect again. The summarizer's own account of why stays above.
        /// </summary>
        [Fact]
        public void ARecapChosenAndThenNotProducedAsksAgainWithoutIt() => RunSta(() =>
        {
            var engine = RefusingEngine();
            var vm = Resumable(engine);

            SendAndChooseFull(vm);
            engine.SummarizeReturnsNothing = true;
            vm.PendingResume!.ResumeSummaryCommand.Execute(null);
            DrainAll();

            Assert.Empty(engine.Prompts);
            Assert.True(vm.HasPendingResume);
            Assert.Equal("Couldn't reload this conversation", vm.PendingResume!.Heading);
            Assert.False(vm.PendingResume.AllowSummary);
            Assert.True(vm.PendingResume.AllowFresh);
            Assert.Contains(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.Contains("empty summary", StringComparison.Ordinal));
        });

        /// <summary>
        /// The parked send is released by the banner going away for ANY reason, not only by a click on it, so
        /// the send does not sit there for the life of the window awaiting an answer that can no longer be
        /// given.
        /// </summary>
        /// <remarks>
        /// Driven by DELETING the conversation on screen rather than by New Session. New is refused while a
        /// send is pending and says so in the method as well as in its CanExecute, so
        /// executing it here would assert that a REFUSED route releases the send, which is not a rule anyone
        /// wants. Delete stays ungated - deleting is a deliberate act - and reaches the same clear.
        /// </remarks>
        [Fact]
        public void ReplacingTheConversationReleasesTheParkedSendWithoutSendingIt() => RunSta(() =>
        {
            var engine = RefusingEngine();
            var vm = Resumable(engine);

            SendAndChooseFull(vm);
            vm.RefreshHistory();
            vm.History.Single(h => h.IsCurrent).DeleteCommand.Execute(null);
            Drain();

            Assert.False(vm.HasPendingResume);
            Assert.Empty(engine.Prompts);
            // The send's finally ran: the pane is usable again rather than stuck reporting work.
            Assert.False(vm.IsBusy);
        });

        /// <summary>
        /// Stop while the send is parked on this banner (F5, 2026-09-13). The send holds IsBusy, so the
        /// pane draws a Stop button — and Stop did nothing. CancelAsync clears permission banners and
        /// cancels the backend's turn, but there is no turn (the prompt has not gone), and it left up
        /// the one thing that releases the wait: this banner. So the send stayed parked and the pane
        /// stayed busy with nothing running.
        /// </summary>
        [Fact]
        public void StopWhileParkedOnTheBannerGivesTheMessageBack() => RunSta(() =>
        {
            var engine = RefusingEngine();
            var vm = Resumable(engine);

            SendAndChooseFull(vm);
            // NOT offered: by the time this banner asks, the reload
            // has been refused and nothing is running, so the bar that carries Stop is down and the banner's
            // own Cancel is the way out. Invoked anyway - which a host still can - it must back out rather
            // than do nothing, which is what it did when this was measured in Visual Studio.
            Assert.False(vm.StopCommand.CanExecute(null), "Stop is offered over a banner with nothing running");
            vm.StopCommand.Execute(null);
            Drain();

            Assert.False(vm.HasPendingResume);
            Assert.Empty(engine.Prompts);
            Assert.False(vm.IsBusy);
            // And nothing more: no turn is on the wire, so there is nothing to
            // cancel, and a cancel would reach whatever the engine holds.
            Assert.Equal(0, engine.CancelCount);
            // Taken back rather than left looking sent: off the transcript, into the input box.
            Assert.Equal("what changed?", vm.InputText);
            Assert.DoesNotContain(vm.Items.OfType<MessageItemViewModel>(), m => m.Text == "what changed?");
        });

        /// <summary>The banner's Cancel is the same way out.</summary>
        [Fact]
        public void CancelOnTheBannerGivesTheMessageBackToo() => RunSta(() =>
        {
            var engine = RefusingEngine();
            var vm = Resumable(engine);

            SendAndChooseFull(vm);
            vm.PendingResume!.CancelCommand.Execute(null);
            Drain();

            Assert.False(vm.HasPendingResume);
            Assert.Empty(engine.Prompts);
            Assert.False(vm.IsBusy);
            Assert.Equal("what changed?", vm.InputText);
        });

        /// <summary>
        /// Nothing is in the saved log while the question is open, which is what makes backing out
        /// clean: a message the user takes back must not stay behind looking sent and reappear on the
        /// next reopen. It is recorded once the banner is answered.
        /// </summary>
        [Fact]
        public void TheMessageIsRecordedOnlyOnceTheBannerIsAnswered() => RunSta(() =>
        {
            var vm = Resumable(RefusingEngine());

            SendAndChooseFull(vm);
            Assert.DoesNotContain(SavedUserTexts(), t => t == "what changed?");

            vm.PendingResume!.ResumeFreshCommand.Execute(null);
            DrainAll();
            Assert.Contains(SavedUserTexts(), t => t == "what changed?");
        });

        /// <summary>Images go back with the text: the composer is where they were taken from.</summary>
        [Fact]
        public void BackingOutRestoresTheImagesAsWell() => RunSta(() =>
        {
            var vm = Resumable(RefusingEngine());
            vm.AttachImage("capture.png", "image/png", new byte[] { 1, 2, 3 });

            SendAndChooseFull(vm);
            Assert.Empty(vm.PendingAttachments);
            vm.PendingResume!.CancelCommand.Execute(null);
            Drain();

            var restored = Assert.Single(vm.PendingAttachments);
            Assert.Equal("capture.png", restored.Name);
        });

        /// <summary>
        /// After backing out, the session the refusal fell through to is still the live one. The next
        /// message must be asked the same question rather than going into that empty session unasked,
        /// and must not retry the reload that already failed.
        /// </summary>
        [Fact]
        public void TheNextMessageAfterBackingOutAsksAgainWithoutRetryingTheReload() => RunSta(() =>
        {
            var engine = RefusingEngine();
            var vm = Resumable(engine);

            SendAndChooseFull(vm);
            vm.PendingResume!.CancelCommand.Execute(null);
            Drain();

            vm.SendCommand.Execute(null); // the restored text
            Drain();

            // Every send runs the decider from scratch, so the ordinary question comes
            // first rather than the send being routed straight back to the refusal it backed out of.
            Assert.True(vm.HasPendingResume);
            Assert.Equal("Continue this conversation?", vm.PendingResume!.Heading);
            Assert.Empty(engine.Prompts);
            Assert.Equal(1, engine.StartCount);

            // Asking for the full reload again meets the refusal REMEMBERED on the session: the same banner,
            // and no second start, because the reload that already failed is not retried.
            vm.PendingResume.ResumeFullCommand.Execute(null);
            Drain();
            Assert.Equal("Couldn't reload this conversation", vm.PendingResume!.Heading);
            Assert.Empty(engine.Prompts);
            Assert.Equal(1, engine.StartCount);

            vm.PendingResume.ResumeFreshCommand.Execute(null);
            DrainAll();
            Assert.Equal("what changed?", LastLine(Assert.Single(engine.Prompts)));
            // Never the reload again: the second start is New's warm session, which the resend takes.
            Assert.Equal(2, engine.StartCount);
            Assert.Contains(SavedUserTexts(), t => t == "what changed?");
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

        /// <summary>
        /// A workspace move while the session start is still in flight retires the send (issue #217).
        /// The start then returns into a pane that belongs to another solution:
        /// nothing may be recorded there or in the conversation that was left, and the returned session
        /// must not be adopted. Adopted, the new root's pane counts as started on the old root's session,
        /// so the warm start the move promised never happens and the next send prompts that session.
        /// </summary>
        [Fact]
        public void AWorkspaceMoveDuringTheStartRecordsNothingAndAdoptsNothing() => RunSta(() =>
        {
            var engine = RefusingEngine();
            engine.RefuseResume = false;
            engine.GateStart = new TaskCompletionSource<bool>();
            var vm = Resumable(engine);
            var moved = MovedRoot();

            SendAndChooseFull(vm);
            Assert.Equal(1, engine.StartCount); // in flight
            vm.UpdateWorkspaceRoot(moved, notice: null);
            Drain();

            var gate = engine.GateStart;
            engine.GateStart = null;
            gate.SetResult(true);
            DrainAll();

            Assert.Empty(engine.Prompts);
            Assert.DoesNotContain(SavedUserTexts(), t => t == "what changed?");
            Assert.DoesNotContain(SavedUserTexts(moved), t => t == "what changed?");
            // The retired send's return is what warm-starts the new root, and it only does so for a
            // pane that was left unstarted.
            Assert.Equal(2, engine.StartCount);
        });

        /// <summary>
        /// "Start new conversation" answered, and a workspace move landing before the answered send has
        /// run on. The start-over used to be posted from inside the send and ran
        /// regardless, so the message went to the new solution's agent as the first message of a
        /// conversation there. It now runs from the send's finally, which drops it with the rest of a
        /// retired send.
        /// </summary>
        [Fact]
        public void AWorkspaceMoveBeforeTheNewConversationStartsDropsTheSend() => RunSta(() =>
        {
            var engine = RefusingEngine();
            var vm = Resumable(engine);
            var moved = MovedRoot();

            SendAndChooseFull(vm);
            vm.PendingResume!.ResumeFreshCommand.Execute(null);
            vm.UpdateWorkspaceRoot(moved, notice: null); // the answered send has not run on yet
            DrainAll();

            Assert.Empty(engine.Prompts);
            Assert.DoesNotContain(SavedUserTexts(moved), t => t == "what changed?");
            Assert.DoesNotContain(vm.Items.OfType<MessageItemViewModel>(), m => m.Text == "what changed?");
        });

        /// <summary>
        /// A workspace move while the send is parked on the banner gives the message back. Dropping
        /// it was considered first, chosen on
        /// the ground that a message written for one solution means little in the next, but a typed,
        /// unsent DRAFT already crosses a move untouched, and dropping took the pasted image and the IDE
        /// capture with it in silence. The cost - a message that may no longer fit - is accepted and
        /// visible, and the user's own Cancel and Stop on this very banner both give back.
        /// <para>No notice says so: the box filling back up is the signal, as it is after a Cancel.</para>
        /// </summary>
        [Fact]
        public void AWorkspaceMoveWhileParkedGivesTheMessageBack() => RunSta(() =>
        {
            var engine = RefusingEngine();
            var vm = Resumable(engine);

            SendAndChooseFull(vm);
            vm.UpdateWorkspaceRoot(MovedRoot(), notice: null);
            DrainAll();

            Assert.False(vm.HasPendingResume);
            Assert.Empty(engine.Prompts);
            Assert.False(vm.IsBusy);
            Assert.Equal("what changed?", vm.InputText);
            Assert.DoesNotContain(vm.Items.OfType<MessageItemViewModel>(), m => m.Text == "what changed?");
        });

        /// <summary>
        /// "Send now" while the send is parked holds the follow-up rather than cutting in.
        /// Nothing is running to cut into, the prompt not having gone, so the cancel that
        /// gesture sends reached whatever the engine held.
        /// </summary>
        [Fact]
        public void SendNowWhileParkedHoldsTheFollowUpAndCancelsNothing() => RunSta(() =>
        {
            var engine = RefusingEngine();
            var vm = Resumable(engine);

            SendAndChooseFull(vm);
            vm.InputText = "and the tests?";
            vm.SendCommand.Execute(null); // busy, so held
            Drain();
            vm.SendPendingNowCommand.Execute(null);
            DrainAll();

            Assert.True(vm.HasPendingResume);
            Assert.Contains(vm.PendingMessages, m => m.Text == "and the tests?");
            Assert.Empty(engine.Prompts);
            Assert.Equal(0, engine.CancelCount);
        });

        /// <summary>
        /// Backing out holds a follow-up typed while the banner was up, as Stop holds the tray.
        /// The unwinding send's finally is a turn end, and its release sent the follow-up
        /// ahead of the message now back in the composer, which it was written after.
        /// </summary>
        [Fact]
        public void BackingOutKeepsAFollowUpHeldBehindTheMessageItFollows() => RunSta(() =>
        {
            var engine = RefusingEngine();
            var vm = Resumable(engine);

            SendAndChooseFull(vm);
            vm.InputText = "and the tests?";
            vm.SendCommand.Execute(null); // busy, so held
            Drain();
            vm.PendingResume!.CancelCommand.Execute(null);
            DrainAll();

            Assert.False(vm.HasPendingResume);
            Assert.Contains(vm.PendingMessages, m => m.Text == "and the tests?");
            Assert.Empty(engine.Prompts);
            Assert.Equal("what changed?", vm.InputText);
        });

        /// <summary>
        /// Backing out puts the message AHEAD of anything typed or attached while the banner was up,
        /// which is the later thought. And an image-only message, with no text to put ahead, does not
        /// start the draft with a blank line.
        /// </summary>
        [Fact]
        public void BackingOutPutsTheMessageAheadOfWhatWasAddedSince() => RunSta(() =>
        {
            var vm = Resumable(RefusingEngine());
            vm.AttachImage("first.png", "image/png", new byte[] { 1, 2, 3 });
            vm.SendCommand.Execute(null); // the image alone
            Drain();
            vm.PendingResume!.ResumeFullCommand.Execute(null);
            Drain();
            Assert.Equal("Couldn't reload this conversation", vm.PendingResume!.Heading);

            vm.InputText = "a draft";
            vm.AttachImage("later.png", "image/png", new byte[] { 4, 5, 6 });
            vm.PendingResume.CancelCommand.Execute(null);
            Drain();

            Assert.Equal("a draft", vm.InputText);
            Assert.Equal(new[] { "first.png", "later.png" }, vm.PendingAttachments.Select(a => a.Name).ToArray());
        });

        /// <summary>
        /// Backing out of the refused banner remembers that the recap had already failed on that send,
        /// so the next send's banner withholds it too. The second route to this
        /// banner was a copy of the first and had lost the gate: it offered the recap that had just
        /// failed, as the recommended answer.
        /// </summary>
        [Fact]
        public void TheNextMessageAfterBackingOutStillWithholdsTheRecapThatFailed() => RunSta(() =>
        {
            var engine = RefusingEngine();
            engine.SummarizeReturnsNothing = true;
            var vm = Resumable(engine);

            vm.InputText = "what changed?";
            vm.SendCommand.Execute(null);
            Drain();
            vm.PendingResume!.ResumeSummaryCommand.Execute(null);
            Drain();
            vm.PendingResume!.ResumeFullCommand.Execute(null);
            Drain();
            Assert.Equal("Couldn't reload this conversation", vm.PendingResume!.Heading);
            vm.PendingResume.CancelCommand.Execute(null);
            Drain();

            vm.SendCommand.Execute(null); // the restored text
            Drain();

            // The decider runs from scratch, so the ordinary question comes first.
            Assert.Equal("Continue this conversation?", vm.PendingResume!.Heading);
            vm.PendingResume.ResumeFullCommand.Execute(null);
            Drain();

            // And the recap that failed on the send which MET the refusal is still withheld: the flag rides
            // on the session alongside the refusal, so it survives the back-out with it.
            Assert.Equal("Couldn't reload this conversation", vm.PendingResume!.Heading);
            Assert.False(vm.PendingResume.AllowSummary);
            Assert.Empty(engine.Prompts);
        });

        /// <summary>
        /// A backed-out refusal belongs to the session it was found on, and does not follow the user
        /// into another conversation. It was cleared only with the transcript, which
        /// opening another conversation from history does not clear, so that conversation's second
        /// message was stopped by the first one's refusal.
        /// </summary>
        [Fact]
        public void ABackedOutRefusalDoesNotFollowIntoAnotherConversation() => RunSta(() =>
        {
            var engine = RefusingEngine();
            var other = SaveOther();
            var vm = Resumable(engine);

            SendAndChooseFull(vm);
            vm.PendingResume!.CancelCommand.Execute(null);
            Drain();

            Open(vm, other);
            Send(vm, "first");
            Send(vm, "second");

            Assert.False(vm.HasPendingResume);
            Assert.Equal(new[] { "first", "second" }, engine.Prompts.Select(LastLine).ToArray());
        });

        /// <summary>
        /// And it DOES survive a round trip through history. Reopening the
        /// conversation the live session belongs to re-attaches through a newly loaded object (issue
        /// #256), so the question is tied to the conversation's id - not to the object, and not to
        /// anything a conversation swap clears - or the next message goes into the empty session unasked.
        /// </summary>
        [Fact]
        public void ABackedOutRefusalSurvivesReopeningItsOwnConversation() => RunSta(() =>
        {
            var engine = RefusingEngine();
            var other = SaveOther();
            var vm = Resumable(engine);
            var original = CurrentId(vm);

            SendAndChooseFull(vm);
            vm.PendingResume!.CancelCommand.Execute(null);
            Drain();

            Open(vm, other);
            Open(vm, original);
            vm.InputText = "what changed?";
            vm.SendCommand.Execute(null);
            Drain();

            // The decider runs from scratch; asking to reload again is what meets the refusal.
            Assert.True(vm.HasPendingResume);
            Assert.Equal("Continue this conversation?", vm.PendingResume!.Heading);
            vm.PendingResume.ResumeFullCommand.Execute(null);
            Drain();

            // Still the refusal, and still one session: the fact is the SESSION's, so it survived the round
            // trip through history that re-adopts it through a freshly loaded object (issue #256).
            Assert.Equal("Couldn't reload this conversation", vm.PendingResume!.Heading);
            Assert.Empty(engine.Prompts);
            Assert.Equal(1, engine.StartCount);
        });

        /// <summary>
        /// The message is recorded where it was shown. A first send into a reopened
        /// conversation records only once the banner is answered, and the session opened meanwhile can
        /// already have said something, drawn BELOW the message. Appended after that, the saved log
        /// replayed the message beneath what followed it.
        /// </summary>
        [Fact]
        public void AMessageRecordedAfterTheBannerKeepsItsPlaceInTheLog() => RunSta(() =>
        {
            var engine = RefusingEngine();
            var vm = Resumable(engine);

            SendAndChooseFull(vm);
            engine.Raise(new AgentEventDto { Type = "text", Text = "Session ready." });
            Drain();
            vm.PendingResume!.ResumeSummaryCommand.Execute(null);
            DrainAll();

            Assert.Single(engine.Prompts);
            var log = Assert.Single(SavedConversations()).Log;
            var message = log.FindIndex(e => e.Role == "user" && e.Text == "what changed?");
            var ready = log.FindIndex(e => e.Event is { Type: "text" } ev && ev.Text == "Session ready.");
            Assert.True(message >= 0 && ready >= 0, $"message at {message}, the session's text at {ready}");
            Assert.True(message < ready, $"message at {message}, the session's text at {ready}");
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
        private ChatViewModel Resumable(ScriptedEngine engine)
        {
            var store = new FileSessionStore(_dir);
            var session = new PersistedSession
            {
                WorkspaceRootPath = _dir,
                ConversationId = "conv-1",
                ProviderId = "fake",
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

        // A backend that refuses the reload the way the live one did - a fresh session on a new id, with
        // the refusal reported beside it - and whose summarizer works unless told otherwise. Turns
        // complete immediately: nothing here is about a turn.
        private static ScriptedEngine RefusingEngine() =>
            new ScriptedEngine { RefuseResume = true, RefusalReason = Refusal, AllowSummarize = true }.WithProvider("fake", "Fake", "ResumeSession");

        // A second, small conversation under the same workspace that never reached a backend, so opening
        // it asks nothing and its first send starts a fresh session. An hour older, so the restore still
        // picks the conversation under test.
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

        // The solution a workspace move lands in.
        private string MovedRoot()
        {
            var moved = Path.Combine(_dir, "moved");
            Directory.CreateDirectory(moved);
            return moved;
        }

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

        private static void Send(ChatViewModel vm, string text)
        {
            vm.InputText = text;
            vm.SendCommand.Execute(null);
            DrainAll();
        }

        private static string LastLine(string prompt)
        {
            var lines = prompt.Split('\n');
            return lines[lines.Length - 1].Trim();
        }

        private static void Drain() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        // Down to ContextIdle: a start-over, the resend, a warm start and a tray release each hop
        // again, some of them at Background. Repeated for the same reason.
        private static void DrainAll()
        {
            for (var i = 0; i < 4; i++)
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        }

        private static void RunSta(Action action) => StaTest.Run(action, withDispatcherContext: true);
    }
}
