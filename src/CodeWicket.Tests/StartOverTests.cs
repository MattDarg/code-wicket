using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CodeWicket.Core;
using CodeWicket.Ipc;
using CodeWicket.Shell;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// "Start new conversation", the answer every resume banner offers: New, and then the message
    /// sent into the conversation New made.
    /// </summary>
    /// <remarks>
    /// <para>The two halves these pin are the ones an ending is most likely to do only half of. The
    /// RESEND is a new conversation's first message and must read as one — nothing about how the
    /// message reached the send it started from survives into it. And the NOTICES that described
    /// an attempt in progress belong to the send, so an ending takes them with it rather than leaving
    /// the transcript saying the pane is summarizing when no send exists.</para>
    /// <para>Kept apart from <see cref="RefusedResumeTests"/> and
    /// <see cref="SummaryResumeFallbackTests"/>, which pin what each banner OFFERS. These pin what the
    /// answer does, from both banners and from the endings that are not an answer at all.</para>
    /// </remarks>
    public sealed class StartOverTests : IDisposable
    {
        private const string Refusal = "Session not found: sess_old";

        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "cwkt-start-over-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch (IOException) { }
        }

        // ---- The resend is an ordinary first message ------------------------------------------------

        /// <summary>
        /// The message that started this over reached its send as an INTERRUPT — the user pressed
        /// Send now on a held batch — so it carried the framing block that tells the agent its work was
        /// cut short and it should deal with this first. That block described a turn in the conversation
        /// being LEFT. Delivered into the new one it is the first thing a brand-new agent reads, about
        /// work it never did.
        /// <para><b>Framing belongs to the gesture, and the gesture here is New.</b> Asserted on the
        /// wire AND on the bubble, because they are two different losses: the agent is lied to, and the
        /// user is shown a note saying their message interrupted something.</para>
        /// </summary>
        [Fact]
        public void TheResendIntoANewConversationCarriesNoMidTurnFraming() => RunSta(() =>
        {
            var engine = RefusingEngine();
            var vm = HeldBatchParkedOnTheRefusal(engine);

            vm.PendingResume!.ResumeFreshCommand.Execute(null);
            DrainAll();

            var prompt = Assert.Single(engine.Prompts);
            Assert.Equal("and the tests?", LastLine(prompt));
            Assert.DoesNotContain(HostPromptBlocks.MidTurnMessage.Open, prompt, StringComparison.Ordinal);
            var shown = Assert.Single(vm.Items.OfType<MessageItemViewModel>(), m => m.Text == "and the tests?");
            Assert.Null(shown.DeliveryNote);
        });

        // ---- the notice ------------------------------------------------------------------------------

        /// <summary>
        /// "Couldn't be continued" asserts an impossibility, and on this route it is false: the
        /// backend refused the reload but a recap was on offer, and the user picked a new conversation
        /// over it. Says what happened instead (user decision, 2026-09-19).
        /// </summary>
        [Fact]
        public void TheNoticeSaysTheConversationWasNotContinuedRatherThanCouldNotBe() => RunSta(() =>
        {
            var engine = RefusingEngine();
            var vm = ParkedOnTheRefusal(engine);

            Assert.True(vm.PendingResume!.AllowSummary); // a recap WAS on offer; this is the false case
            vm.PendingResume.ResumeFreshCommand.Execute(null);
            DrainAll();

            var notice = StartOverNotice(vm);
            Assert.Contains("wasn't continued here", notice.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("couldn't be continued", notice.Text, StringComparison.Ordinal);
        });

        /// <summary>
        /// The banner the user just answered carried the backend's own account of the refusal, in full,
        /// and this notice is display-only — it is never recorded, so it survives nothing. Repeating the
        /// reason a click later buys the reader nothing and costs the sentence its length (user decision,
        /// 2026-09-19). The conversation's own title is what the notice needs to name, and does.
        /// </summary>
        [Fact]
        public void TheNoticeNamesTheConversationRatherThanRepeatingTheBackendsReason() => RunSta(() =>
        {
            var engine = RefusingEngine();
            var vm = ParkedOnTheRefusal(engine);

            vm.PendingResume!.ResumeFreshCommand.Execute(null);
            DrainAll();

            var notice = StartOverNotice(vm);
            Assert.Contains("Earlier work", notice.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(Refusal, notice.Text, StringComparison.Ordinal);
        });

        /// <summary>
        /// A start-over does not hand on the session a refused reload fell through to. That is accepted
        /// rather than fixed, and this is what "accepted" means in the code: the session the
        /// refusal fell through to is thrown away, and the new conversation runs in one New opened for it.
        /// <para><b>The count alone would not say that</b> - two starts are consistent with the second one
        /// being for something else entirely - so the identity rides beside it: the conversation's recorded
        /// session is the one the SECOND start returned, and the second start asked to resume nothing. The
        /// cost this pins is one extra backend spawn on this route, and with it the user's own slow MCP
        /// servers may miss the first prompt (issue #19).</para>
        /// <para><b>No injection, and the reason is that the bug has no code to remove.</b> A handover is
        /// what a later "fix" for this would ADD, so this is a forward guard rather than a proof of a
        /// present one; both assertions read real values, and a conversation running in the fall-through
        /// session reads "fresh-1" here.</para>
        /// </summary>
        [Fact]
        public void TheFallThroughSessionIsThrownAwayRatherThanHandedToTheNewConversation() => RunSta(() =>
        {
            var engine = RefusingEngine();
            var vm = ParkedOnTheRefusal(engine);
            Assert.Equal(1, engine.StartCount); // the refusal fell through to this one

            vm.PendingResume!.ResumeFreshCommand.Execute(null);
            DrainAll();

            // The fake names each session after its start's ordinal, so the ids ARE the identity: the
            // refusal fell through to "fresh-1", and the conversation must not be recorded as running in it.
            Assert.Equal(2, engine.StartCount);
            Assert.Null(engine.Starts[1].ResumeConversationId);
            var fresh = Assert.Single(SavedConversations(), c => c.ConversationId != "conv-1");
            Assert.Equal("fresh-2", fresh.ConversationId);
        });

        /// <summary>
        /// The report's half of the same guard, and it is the check that used to assert the OPPOSITE: before
        /// the line moved it was written here and taken back by the ending, so the test read "the ending
        /// removes it". The subject changed, not the coverage - there is now nothing to remove, and what is
        /// pinned is that nothing was written.
        /// </summary>
        [Fact]
        public void StoppingBeforeThePromptLeavesNoReportThatARecapWasSent() => RunSta(() =>
        {
            var engine = new ScriptedEngine { RefuseResume = true, RefusalReason = Refusal, AllowSummarize = true }
                .WithProvider("fake", "Fake", "ResumeSession", "Mcp");
            var vm = ParkedOnTheRefusal(engine);

            vm.PendingResume!.ResumeSummaryCommand.Execute(null);
            DrainAll();
            Assert.Empty(engine.Prompts); // still waiting for our bridge to serve its tools

            vm.StopCommand.Execute(null);
            Drain();

            Assert.DoesNotContain(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.Contains("condensed recap", StringComparison.Ordinal));
            Assert.Empty(engine.Prompts);
            Assert.Equal("what changed?", vm.InputText);
        });

        // ---- An ending takes the phase notices with it ----------------------------------------------

        /// <summary>
        /// "Summarizing…" describes an attempt in progress. Stop ends the send that was making
        /// it — the message goes back to the composer with nothing recorded — and the line used to stay,
        /// describing work for a send that no longer exists, in a transcript with no message under it.
        /// </summary>
        [Fact]
        public void StoppingDuringTheSummarizeTakesItsNoticeWithIt() => RunSta(() =>
        {
            var engine = SummarizingEngine();
            var gate = new TaskCompletionSource<bool>();
            engine.GateSummarize = gate;
            var vm = Resumable(engine);

            SendAndChooseSummary(vm);
            Assert.Contains(vm.Items.OfType<NoticeItemViewModel>(), n => n.Text.StartsWith("Summarizing", StringComparison.Ordinal));

            vm.StopCommand.Execute(null);
            Drain();

            Assert.DoesNotContain(vm.Items.OfType<NoticeItemViewModel>(), n => n.Text.StartsWith("Summarizing", StringComparison.Ordinal));
            Assert.Equal("what changed?", vm.InputText);

            gate.SetResult(true);
            DrainAll();
            // And the abandoned call returning does not put it back: its result is discarded.
            Assert.DoesNotContain(vm.Items.OfType<NoticeItemViewModel>(), n => n.Text.StartsWith("Summarizing", StringComparison.Ordinal));
            Assert.Empty(engine.Prompts);
        });

        /// <summary>
        /// The same rule for the other phase notice. "Resuming this conversation…" is written before the
        /// backend has been asked — it has to be, being what the transcript says while the start is in
        /// flight — so a Stop during the start leaves a claim that a reload is happening when the send
        /// that would have done it is gone.
        /// </summary>
        [Fact]
        public void StoppingDuringTheSessionStartTakesTheResumingNoticeWithIt() => RunSta(() =>
        {
            var engine = SummarizingEngine();
            var gate = new TaskCompletionSource<bool>();
            engine.GateStart = gate;
            var vm = Resumable(engine);

            vm.InputText = "what changed?";
            vm.SendCommand.Execute(null);
            Drain();
            vm.PendingResume!.ResumeFullCommand.Execute(null);
            Drain();
            Assert.Contains(vm.Items.OfType<NoticeItemViewModel>(), n => n.Text.StartsWith("Resuming", StringComparison.Ordinal));

            vm.StopCommand.Execute(null);
            Drain();

            Assert.DoesNotContain(vm.Items.OfType<NoticeItemViewModel>(), n => n.Text.StartsWith("Resuming", StringComparison.Ordinal));
            Assert.Equal("what changed?", vm.InputText);

            gate.SetResult(true);
            DrainAll();
            Assert.DoesNotContain(vm.Items.OfType<NoticeItemViewModel>(), n => n.Text.StartsWith("Resuming", StringComparison.Ordinal));
            Assert.Empty(engine.Prompts);
        });

        /// <summary>
        /// The same rule reached by the banner's own Cancel rather than by Stop. Three routes end a send
        /// and each removes the lines separately - measured, not assumed: taking the removal out of any one
        /// of them leaves the other two green, so each needs its own check.
        /// <para>"Could not summarize" STAYS: it is the backend's account of why the banner appeared, and
        /// the one place the reason is shown once the message is back in the composer.</para>
        /// </summary>
        [Fact]
        public void CancellingTheRecapFailedBannerTakesTheSummarizingNoticeWithIt() => RunSta(() =>
        {
            var engine = SummarizingEngine();
            engine.SummarizeReturnsNothing = true;
            var vm = Resumable(engine);

            SendAndChooseSummary(vm);
            Assert.Equal("Couldn't summarize this conversation", vm.PendingResume!.Heading);

            vm.PendingResume.CancelCommand.Execute(null);
            DrainAll();

            Assert.DoesNotContain(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.StartsWith("Summarizing", StringComparison.Ordinal));
            Assert.Contains(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.Contains("summarize", StringComparison.OrdinalIgnoreCase) && n.IsError);
            Assert.Equal("what changed?", vm.InputText);
        });

        /// <summary>
        /// And the third route, which is the one that shows why this belongs to the ENDING rather than to
        /// the two gestures. Every other conversation change clears the transcript a moment later, so the
        /// removal is invisible there; the engine exiting (issue #299) clears nothing - the conversation
        /// stays readable - and left alone the pane says it is summarizing in a chat that can no longer
        /// send anything at all.
        /// </summary>
        [Fact]
        public void TheEngineExitingTakesThePhaseNoticeWithIt() => RunSta(() =>
        {
            var engine = SummarizingEngine();
            engine.GateSummarize = new TaskCompletionSource<bool>();
            var vm = Resumable(engine);

            SendAndChooseSummary(vm);
            Assert.Contains(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.StartsWith("Summarizing", StringComparison.Ordinal));

            engine.RaiseExit(new EngineExit(1, new[] { "it died" }));
            Pump();

            Assert.DoesNotContain(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.StartsWith("Summarizing", StringComparison.Ordinal));
            // The transcript itself is untouched: the EXIT's notice lands in the conversation, which stays
            // readable. Without that this would pass on a clear rather than on the removal.
            //
            // Named, not merely "an error notice". The exit now faults the held summarize as a dead pipe
            // does, so "Could not summarize the conversation" is an error notice too - and it is added
            // AFTER the point a clear would have happened, so a check for any error at all stopped
            // discriminating between the two things this line exists to tell apart.
            Assert.Contains(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.IsError && n.Text.Contains("engine is not running", StringComparison.Ordinal));
            // The other error notice, asserted so that the reason above is visible rather than asserted:
            // it is here, it is an error, and it is added after a clear would have happened - so a check
            // for "an error notice" cannot tell the two apart and this one can.
            Assert.Contains(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.IsError && n.Text.StartsWith("Could not summarize", StringComparison.Ordinal));
            Assert.Contains(vm.Items.OfType<MessageItemViewModel>(), m => m.Text.StartsWith("uuu", StringComparison.Ordinal));
        });

        // ---- an OUTCOME line is not written until it is true ----------------------------------------

        /// <summary>
        /// "A condensed recap was sent instead of the full history" is a claim about a delivery, and it was
        /// written before the session had even been started - so the send could still be stopped or retired,
        /// and the line stood over a transcript with no message under it. It is written at the COMMIT now,
        /// in the one synchronous block that records the message and issues the prompt, where the working
        /// directory and project-settings notices already are for the same reason.
        /// </summary>
        [Fact]
        public void TheRecapLineIsNotWrittenUntilThePromptGoes() => RunSta(() =>
        {
            var engine = SummarizingEngine("Mcp");
            var vm = Resumable(engine);

            SendAndChooseSummary(vm);

            // The recap is BUILT - the send is past the summarize and parked on the wait for our tools.
            Assert.Empty(engine.Prompts);
            Assert.DoesNotContain(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.StartsWith("Resumed from a summary", StringComparison.Ordinal));

            engine.ServeTools();
            PumpUntilPrompted(engine);

            var prompt = Assert.Single(engine.Prompts);
            Assert.Contains("conversation-summary", prompt, StringComparison.Ordinal);
            Assert.Contains(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.StartsWith("Resumed from a summary", StringComparison.Ordinal));
        });

        /// <summary>The same rule for the refused-reload report, which makes the same claim on the other route.</summary>
        [Fact]
        public void TheRefusedReloadReportIsNotWrittenUntilThePromptGoes() => RunSta(() =>
        {
            var engine = new ScriptedEngine { RefuseResume = true, RefusalReason = Refusal, AllowSummarize = true }
                .WithProvider("fake", "Fake", "ResumeSession", "Mcp");
            var vm = ParkedOnTheRefusal(engine);

            vm.PendingResume!.ResumeSummaryCommand.Execute(null);
            DrainAll();

            Assert.Empty(engine.Prompts);
            Assert.DoesNotContain(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.Contains("condensed recap", StringComparison.Ordinal));

            engine.ServeTools();
            PumpUntilPrompted(engine);

            Assert.Single(engine.Prompts);
            Assert.Contains(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.Contains("condensed recap", StringComparison.Ordinal));
        });

        /// <summary>
        /// The defect the timing rule removes, kept as its own check because "impossible by construction" is a
        /// claim and the construction is one edit from being undone. A send that has built its recap and is
        /// then STOPPED leaves no claim that one was sent.
        /// <para><b>Two routes reach here and they share a mechanism</b> - the send ending before it commits -
        /// which was measured rather than assumed: Stop, and a conversation change. A banner's Cancel reaches
        /// neither line, no banner following either; and the #268 refusal cannot follow the recap line at all,
        /// because the Summary strategy leaves the resume id null so a refusing backend raises nothing.</para>
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void EndingBeforeThePromptLeavesNoClaimThatARecapWasSent(bool byEngineExit) => RunSta(() =>
        {
            var engine = SummarizingEngine("Mcp");
            var vm = Resumable(engine);

            SendAndChooseSummary(vm);
            Assert.Empty(engine.Prompts);

            if (byEngineExit)
            {
                engine.RaiseExit(new EngineExit(1, new[] { "it died" }));
                Pump();
            }
            else
            {
                vm.StopCommand.Execute(null);
                Drain();
            }

            Assert.DoesNotContain(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.StartsWith("Resumed from a summary", StringComparison.Ordinal));
            Assert.Empty(engine.Prompts);
        });

        /// <summary>
        /// A send that SUCCEEDS keeps both kinds of line, and the first assertion is the load-bearing one:
        /// "Summarizing…" is written before the answer exists and so passes through the removal helper's
        /// reach, which is why the removal has to sit where a send ENDS and not where one stops being
        /// PENDING - the finally in <c>PrepareAsync</c> runs for a send about to prompt as well.
        /// <para>Asserted on the PROGRESS line since the outcome line moved to the commit: written after
        /// that finally, it is out of the helper's reach whatever the helper does, so a check pointed at it
        /// would pin nothing. Measured, not reasoned about - pointed there, the injection came back PINS
        /// NOTHING.</para>
        /// </summary>
        [Fact]
        public void ASendThatSUCCEEDEDKeepsBothItsLines() => RunSta(() =>
        {
            var engine = SummarizingEngine();
            var vm = Resumable(engine);

            SendAndChooseSummary(vm);
            DrainAll();

            Assert.Contains(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.StartsWith("Summarizing", StringComparison.Ordinal));
            Assert.Contains(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.StartsWith("Resumed from a summary", StringComparison.Ordinal));
            Assert.Single(engine.Prompts);
        });

        // ---- helpers ----------------------------------------------------------------------------------

        private List<PersistedSession> SavedConversations()
        {
            var store = new FileSessionStore(_dir);
            return store.List(_dir)
                .Select(summary => store.Load(_dir, summary.Id))
                .Where(session => session is not null)
                .Select(session => session!)
                .ToList();
        }

        private static NoticeItemViewModel StartOverNotice(ChatViewModel vm) => Assert.Single(
            vm.Items.OfType<NoticeItemViewModel>(),
            n => n.Text.Contains("Started a new conversation", StringComparison.Ordinal));

        /// <summary>A send parked on the refused-reload banner, reached the ordinary way (Enter).</summary>
        private ChatViewModel ParkedOnTheRefusal(ScriptedEngine engine)
        {
            var vm = Resumable(engine);
            vm.InputText = "what changed?";
            vm.SendCommand.Execute(null);
            Drain();
            vm.PendingResume!.ResumeFullCommand.Execute(null);
            DrainAll();
            Assert.Equal("Couldn't reload this conversation", vm.PendingResume!.Heading);
            return vm;
        }

        /// <summary>
        /// The same banner, reached by a HELD batch released with Send now — so the parked send carries
        /// the interrupt framing.
        /// </summary>
        /// <remarks>
        /// <para><b>Re-derived when a tray release began deciding for itself, because that fix destroys
        /// this setup rather than its route.</b> The batch used to reach the banner by a leak: a release ran no
        /// <c>ResumeDecider</c>, and the moved-root banner's Cancel left the full reload it had been
        /// asked about standing, so the released batch took that reload and was refused. With the
        /// release deciding and the Cancel resetting the strategy there is no silent reload, so no
        /// refusal, so no start-over — and read off the route alone this check would have gone GREEN
        /// with nothing left to assert.</para>
        /// <para>What this check needs is only that the send arriving at the banner is FRAMED, and a released
        /// batch still is: it now parks on the Choice banner carrying its framing, and the full reload
        /// it is answered with is the one that gets refused.</para>
        /// </remarks>
        private ChatViewModel HeldBatchParkedOnTheRefusal(ScriptedEngine engine)
        {
            var vm = Resumable(engine);

            vm.InputText = "what changed?";
            vm.SendCommand.Execute(null);
            Drain();
            vm.InputText = "and the tests?";
            vm.SendCommand.Execute(null); // a send is pending, so this is held
            Drain();
            Assert.Contains(vm.PendingMessages, m => m.Text == "and the tests?");

            // Backs the first send out: the tray is held, and nothing has been decided.
            vm.PendingResume!.CancelCommand.Execute(null);
            Drain();

            // Send now lifts that hold and releases the batch, which decides for itself.
            vm.SendPendingNowCommand.Execute(null);
            Drain();
            Assert.Equal("Continue this conversation?", vm.PendingResume!.Heading);

            vm.PendingResume.ResumeFullCommand.Execute(null);
            DrainAll();
            Assert.Equal("Couldn't reload this conversation", vm.PendingResume!.Heading);
            return vm;
        }

        // Drives a restored conversation onto the recap route: send, take "resume from summary".
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
        private ChatViewModel Resumable(ScriptedEngine engine, string? conversationRoot = null)
        {
            var store = new FileSessionStore(_dir);
            var session = new PersistedSession
            {
                WorkspaceRootPath = _dir,
                AgentWorkingDirectory = conversationRoot,
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

        // Refuses the reload the way the live backend did, and can summarize (so the refused banner
        // offers the recap, which is what makes "couldn't be continued" false there).
        private static ScriptedEngine RefusingEngine() =>
            new ScriptedEngine { RefuseResume = true, RefusalReason = Refusal, AllowSummarize = true }
                .WithProvider("fake", "Fake", "ResumeSession");

        // Resumes and summarizes normally: the phase-notice tests are about the attempt being in
        // FLIGHT, not about it failing.
        private static ScriptedEngine SummarizingEngine(params string[] extraCapabilities) =>
            new ScriptedEngine { AllowSummarize = true }
                .WithProvider("fake", "Fake", new[] { "ResumeSession" }.Concat(extraCapabilities).ToArray());

        private static string LastLine(string prompt)
        {
            var lines = prompt.Split('\n');
            return lines[lines.Length - 1].Trim();
        }

        /// <summary>
        /// The IDE-tools release crosses a thread-pool hop - the wait is a <c>Task.WhenAny</c> - before it
        /// reaches the dispatcher, so a fixed number of drains can run ahead of it and the prompt has not
        /// gone yet. Bounded, and only ever for asserting that something DOES happen: no amount of polling
        /// would establish that something does not.
        /// </summary>
        /// <remarks>
        /// <b>The ceiling is generous on purpose.</b> The loop leaves as soon as the prompt lands, so a
        /// longer bound costs a quiet machine nothing and buys a contended gate-matrix run the margin that
        /// puts this off the known-flaky list - which is where a short sleep-poll in front of an
        /// <c>Assert.Single</c> ends up (issue #218's shape). `PendingSendScenario.PumpUntil` is the one
        /// implementation, already trusted for exactly this by the pending-send suite.
        /// </remarks>
        private static void PumpUntilPrompted(ScriptedEngine engine) =>
            PendingSendScenario.PumpUntil(() => engine.Prompts.Count > 0, TimeSpan.FromSeconds(30));

        // The engine's exit is reported off the UI thread and marshalled back, so the assertion has to
        // wait for a hop it does not control.
        private static void Pump()
        {
            var until = DateTime.UtcNow + TimeSpan.FromMilliseconds(200);
            do
            {
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
                Thread.Sleep(5);
            }
            while (DateTime.UtcNow < until);
        }

        private static void Drain() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        // Down to ContextIdle: a start-over, the resend, a warm start and a tray release each hop
        // again, some of them at Background.
        private static void DrainAll()
        {
            for (var i = 0; i < 4; i++)
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        }

        private static void RunSta(Action action) => StaTest.Run(action, withDispatcherContext: true);
    }
}
