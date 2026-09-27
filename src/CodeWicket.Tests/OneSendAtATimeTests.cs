using System;
using System.IO;
using System.Windows.Threading;
using CodeWicket.Ipc;
using CodeWicket.Shell;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// At most one send exists at a time, from Enter until its prompt goes out, and while one exists every
    /// send gesture holds.
    /// </summary>
    /// <remarks>
    /// <para><b>The gap these close is a phase, not a route.</b> Without them the pane knew about a pending send only while
    /// one was parked on a resume-FAILURE banner (issue #84's or #268's), because that is the only phase that
    /// held an object - a completion source. The Choice banner parked its text in a field instead, so the pane
    /// read as idle: a second Enter there did not hold, it began a SECOND send, which re-entered the decider
    /// and overwrote the first message's text with its own. The first message was then unreachable - not in
    /// the tray, not in the composer, and not on the transcript.</para>
    /// <para>Asserted through the composer rather than through the delivery's own state, because that is where
    /// the loss showed: backing out has to give back the message that was actually parked.</para>
    /// </remarks>
    public sealed class OneSendAtATimeTests : IDisposable
    {
        private const string First = "what changed?";
        private const string Second = "and the tests?";

        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "cwkt-one-send-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch (IOException) { }
        }

        /// <summary>
        /// Enter while the Choice banner is up holds, as it does while a turn is running. It used to start a
        /// second send: the banner was re-raised for the new message and the first one's text was replaced, so
        /// answering the banner sent the follow-up and the message it followed was gone.
        /// </summary>
        [Fact]
        public void AMessageTypedWhileTheChoiceBannerIsUpIsHeldRatherThanReplacingIt() => RunSta(() =>
        {
            var engine = ResumingEngine();
            var vm = Resumable(engine);

            SendAndReachTheChoiceBanner(vm);

            vm.InputText = Second;
            vm.SendCommand.Execute(null);
            Drain();

            // Held, and the question still stands over it.
            Assert.Contains(vm.PendingMessages, m => m.Text == Second);
            Assert.Equal("Continue this conversation?", vm.PendingResume?.Heading);
            Assert.Empty(engine.Prompts);

            // And the message the banner is about is still the one parked: backing out returns THAT, not
            // whatever was typed after it.
            vm.PendingResume!.CancelCommand.Execute(null);
            Drain();
            Assert.Equal(First, vm.InputText);
        });

        /// <summary>
        /// The parked message comes back AHEAD of a draft typed while the banner was up, which is the later
        /// thought - one order for every give-back. This route used to ASSIGN the parked text over
        /// the composer, so the gesture that returned one message destroyed the other.
        /// </summary>
        [Fact]
        public void BackingOutPutsTheParkedMessageAheadOfADraftTypedSince() => RunSta(() =>
        {
            var engine = ResumingEngine();
            var vm = Resumable(engine);

            SendAndReachTheChoiceBanner(vm);

            // Typed and never sent, so it stays in the box rather than joining the tray.
            vm.InputText = Second;
            vm.PendingResume!.CancelCommand.Execute(null);
            Drain();

            Assert.Equal(First + Environment.NewLine + Second, vm.InputText);
        });

        /// <summary>
        /// "Send now" while a send is pending holds too, on the Choice banner as on the refused-reload one.
        /// Nothing is running to cut into - the prompt has not gone - so the cancel that
        /// gesture sends would reach whatever the engine happens to hold, and the banner would be answered for
        /// the user by a click that was not about it.
        /// </summary>
        [Fact]
        public void SendNowWhileTheChoiceBannerIsUpHoldsTheFollowUpAndCancelsNothing() => RunSta(() =>
        {
            var engine = ResumingEngine();
            var vm = Resumable(engine);

            SendAndReachTheChoiceBanner(vm);

            vm.InputText = Second;
            vm.SendCommand.Execute(null); // a send is pending, so held
            Drain();
            vm.SendPendingNowCommand.Execute(null);
            DrainAll();

            Assert.Contains(vm.PendingMessages, m => m.Text == Second);
            Assert.Equal("Continue this conversation?", vm.PendingResume?.Heading);
            Assert.Empty(engine.Prompts);
            Assert.Equal(0, engine.CancelCount);
        });

        /// <summary>
        /// The tray's own release is held too, which is the half a gate on the parked-banner wait could not
        /// reach. In Steer mode the next-step release fires the moment a message is held, and its
        /// boundary test - nothing open, and either idle or the turn has spoken - is ALREADY true here,
        /// because the pane is not busy while the Choice banner is up. So the follow-up went out on its own,
        /// as the first message of a conversation whose resume question was still on screen.
        /// </summary>
        [Fact]
        public void AFollowUpInSteerModeIsNotDeliveredWhileTheChoiceBannerIsUp() => RunSta(() =>
        {
            var engine = ResumingEngine();
            var vm = Resumable(engine);

            // Through the command rather than the enum, so this reads as the gesture a user makes.
            vm.UseNextStepReleaseCommand.Execute(null);

            SendAndReachTheChoiceBanner(vm);

            vm.InputText = Second;
            vm.SendCommand.Execute(null);
            DrainAll();

            Assert.Contains(vm.PendingMessages, m => m.Text == Second);
            Assert.Empty(engine.Prompts);
            Assert.Equal("Continue this conversation?", vm.PendingResume?.Heading);
        });

        // ---- helpers --------------------------------------------------------------------------

        // Sends the first message into a reopened conversation, which asks how to continue it.
        private static void SendAndReachTheChoiceBanner(ChatViewModel vm)
        {
            vm.InputText = First;
            vm.SendCommand.Execute(null);
            Drain();

            Assert.Equal("Continue this conversation?", vm.PendingResume?.Heading);
            // The pane is NOT busy here, which is the whole reason this phase was invisible: every gate that
            // holds a second gesture read IsBusy or the resume-failure wait, and neither is set.
            Assert.False(vm.IsBusy);
        }

        // A backend that resumes and summarizes. Turns complete immediately: nothing here is about a turn.
        private static ScriptedEngine ResumingEngine() =>
            new ScriptedEngine { AllowSummarize = true }.WithProvider("fake", "Fake", "ResumeSession");

        // A restored conversation big enough that the send-time banner offers the full-vs-summary choice
        // (ResumeDecider's threshold is 4000 chars of transcript).
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

        private static void Drain() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        private static void DrainAll()
        {
            for (var i = 0; i < 4; i++)
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        }

        private static void RunSta(Action action) => StaTest.Run(action, withDispatcherContext: true);
    }
}
