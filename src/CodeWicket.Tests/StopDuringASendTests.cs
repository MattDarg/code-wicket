using System;
using System.Linq;
using CodeWicket.Ipc;
using CodeWicket.Shell;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Stop while a send is pending and NO turn is on the wire: it ends the send and gives the message
    /// back, it does not reach the engine, and the abandoned send does not carry on.
    /// </summary>
    /// <remarks>
    /// <para><b>Found by running it in Visual Studio (2026-09-17), not offline.</b> The working bar is up
    /// while the recap is being written - correctly, a call of ours is out - so Stop is offered. It was
    /// wired to cancel a TURN, and there is none: the prompt has not gone. Stop did nothing.</para>
    /// <para><b>And "nothing" understates it.</b> Falling through to <c>_engine.CancelAsync()</c> with no
    /// turn outstanding lands the cancel on whatever the engine holds, which can be another conversation's
    /// work going on off screen (issue #256) - the reason the banner branch beside it refuses to cancel.
    /// The summarize cannot be cancelled either: <c>EngineService.SummarizeAsync</c> takes no token,
    /// whatever the shell interface offers. So the only honest meaning for Stop here is the one it already
    /// has on the banners: end the send, hand the message back, and discard the call when it returns.</para>
    /// </remarks>
    public sealed class StopDuringASendTests
    {
        [Fact]
        public void StopWhileTheRecapIsBeingWrittenBacksOutTheSend() => RunSta(() =>
        {
            using var s = PendingSendScenario.Reach(PendingPhase.Summarizing);
            var vm = s.Vm;
            var engine = s.Engine;

            // The bar is up and Stop is offered - that part is right, and is what makes the rest a defect.
            Assert.True(vm.ShowWorkingBar, "no working bar while the recap is being written");
            Assert.True(vm.StopCommand.CanExecute(null), "Stop is not offered while the recap is written");

            vm.StopCommand.Execute(null);
            PendingSendScenario.DrainAll();

            Assert.False(vm.HasPendingSendForDiagnostics, "the send survived Stop");
            Assert.False(vm.IsBusy, "Stop left the pane busy with nothing it could stop");
            Assert.Equal(PendingSendScenario.Message, vm.InputText);

            // The half that is not merely useless: with no turn on the wire this cancel reaches whatever
            // the engine holds, which may be another conversation's work (issue #256).
            Assert.Equal(0, engine.CancelCount);

            // Now release the abandoned summarize DIRECTLY, not through Settle(): Settle breaks its pump
            // loop on !IsBusy, and Stop has already freed the pane - so it returns before the send can
            // resume, and anything asserted after it is vacuous. Measured: with that shape the injection
            // came back PINS-NOTHING even with every guard removed.
            //
            // Then PUMP FOR THE BAD THING and assert it did not happen. A non-event cannot be polled for,
            // so the ceiling is spent deliberately. A stopped send that carried on would resume here,
            // START A SESSION and prompt - having already handed the message back to the composer, so the
            // user would see it returned AND sent. The session start is the earlier symptom of the two.
            engine.GateSummarize!.TrySetResult(true);
            PendingSendScenario.PumpUntil(
                () => engine.Prompts.Count > 0 || engine.StartCount > 0, TimeSpan.FromSeconds(2));

            Assert.Equal(0, engine.StartCount);
            // And it was ABANDONED, not crashed. Without the guards the resumed send reaches
            // Items.IndexOf(userMessage) for a message the give-back has already removed, so it dies
            // on an Insert(-1, ...) that the turn runner catches and reports. That stops the prompt
            // too, which is why a test asserting only "no prompt" greens with every guard removed -
            // it cannot tell a clean ending from a thrown one. The user would see the difference.
            Assert.DoesNotContain(vm.Items.OfType<NoticeItemViewModel>(), n => n.Kind == NoticeKind.Error);
            Assert.Empty(engine.Prompts);
            Assert.Equal(PendingSendScenario.Message, vm.InputText);
        });

        /// <summary>
        /// A stopped send returning LATE must not end the send begun after it. Stop ends a send without
        /// moving the epoch, so an epoch check cannot tell the two apart (scoped review, 2026-09-17).
        /// </summary>
        /// <remarks>
        /// <para>The five in-body checkpoints were given a <c>StillPending</c> discriminator for exactly this
        /// reason, and <c>PrepareAsync</c>'s finally was left keyed on the epoch. So the stopped send's
        /// finally cleared the PHASE of the send that owns the field now.</para>
        /// <para><b>What the user sees is a banner with nothing behind it.</b> The phase is what
        /// <c>HasPendingSend</c> reads, so clearing it stops the tray holding while B's banner is still on
        /// screen: the next Enter starts a SECOND send that re-enters the decider and overwrites B's text,
        /// and New, both pickers and the history rows re-enable mid-send. The clobber also opens
        /// <c>TryReleasePending</c>'s own gate on the way past.</para>
        /// </remarks>
        [Fact]
        public void AStoppedSendsLateReturnDoesNotEndTheSendBegunAfterIt() => RunSta(() =>
        {
            using var s = PendingSendScenario.Reach(PendingPhase.Summarizing);
            var vm = s.Vm;
            var engine = s.Engine;

            // A is stopped while its recap is being written. The epoch does not move: nothing about the
            // conversation changed, so a send begun now is begun under the same token.
            vm.StopCommand.Execute(null);
            PendingSendScenario.DrainAll();
            Assert.False(vm.HasPendingSendForDiagnostics, "A survived Stop");

            // B: the message Stop handed back is sent again, and parks on the Choice banner.
            vm.SendCommand.Execute(null);
            PendingSendScenario.Drain();
            Assert.Equal("Continue this conversation?", vm.PendingResume?.Heading);
            Assert.True(vm.HasPendingSendForDiagnostics, "B is not pending, so this proves nothing");

            // A's summarize comes back about 20 seconds later, measured. Pump FOR the bad thing - B's
            // phase being cleared - and then assert it did not happen.
            engine.GateSummarize!.TrySetResult(true);
            PendingSendScenario.PumpUntil(
                () => !vm.HasPendingSendForDiagnostics, TimeSpan.FromSeconds(2));

            Assert.True(
                vm.HasPendingSendForDiagnostics,
                "A's late return ended B: B's banner is on screen with nothing holding the tray behind it");
            Assert.NotNull(vm.PendingResume);
            Assert.Empty(engine.Prompts);
        });

        /// <summary>
        /// Stop with nothing of this conversation's outstanding does not reach the engine at all.
        /// </summary>
        /// <remarks>
        /// <para><b>A tripwire, and it says so.</b> Through the working bar this is unreachable: the bar
        /// is up only for out-of-turn work or a busy pane, and the two branches above the cancel answer
        /// the busy cases. So the rule is upheld today by <c>StopPendingSend</c>'s guard and by the bar's
        /// own definition rather than by anything at this call - which is the shape in which a defect became
        /// live with nothing failing, because the guard lived somewhere else (scoped review,
        /// 2026-09-17).</para>
        /// <para><b>What it guards is not hypothetical.</b> A cancel with nothing outstanding lands on
        /// whatever the engine holds, which can be another conversation's work going on off screen
        /// (issue #256) - damage where the person pressing Stop is not looking. Driven through the
        /// command directly, as the Desktop harness drives it, since <c>CanExecute</c> is exactly the
        /// thing this must not depend on.</para>
        /// </remarks>
        [Fact]
        public void StopWithNothingOutstandingDoesNotReachTheEngine() => RunSta(() =>
        {
            var engine = new ScriptedEngine();
            var vm = new ChatViewModel(
                engine,
                new StartSessionRequest("fake", null, AppContext.BaseDirectory, "Prompt", null));
            PendingSendScenario.Drain();

            Assert.False(vm.ShowWorkingBar, "the bar is up, so this is not the case under test");

            vm.StopCommand.Execute(null);
            PendingSendScenario.DrainAll();

            Assert.Equal(0, engine.CancelCount);
        });

        private static void RunSta(Action action) => StaTest.Run(action, withDispatcherContext: true);
    }
}
