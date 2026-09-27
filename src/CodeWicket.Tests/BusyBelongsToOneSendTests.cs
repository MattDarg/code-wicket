using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Only the send that took the pane may give it back. A send ended by a conversation change still has a
    /// call in flight, and when that call returns its turn runner must not clear a LATER send's busy state.
    /// </summary>
    /// <remarks>
    /// <para><b>Reachable only because the ending frees the pane.</b> Freeing the pane at the ending is what
    /// lets the user start a second send while the first one's summarize is still out. Were the pane kept
    /// busy until that call returned, the two sends could never overlap and the shared flag would have one
    /// writer at a time.</para>
    /// <para><b>Why the epoch is the wrong guard.</b> A workspace move mid-turn retires a COMMITTED turn,
    /// whose token is stale by the time its finally runs - and that finally must still free the pane, because
    /// the ending deliberately left IsBusy to the turn. Keyed on the token, the pane would stay busy for good.
    /// Ownership is the axis that separates the two: the ticket says which send the busy state belongs to.</para>
    /// <para><b>What the assertions have to be.</b> Not "no overlapping starts" - that passes either way. The
    /// damage is to the pane the user is looking at: the working bar vanishes mid-turn, Stop goes dead, and
    /// with IsBusy and HasPendingSend both false the next Enter begins a SECOND concurrent send instead of
    /// holding.</para>
    /// </remarks>
    public sealed class BusyBelongsToOneSendTests
    {
        [Fact]
        public void AnEndedSendsReturnDoesNotFreeThePaneForTheSendThatReplacedIt() => RunSta(() =>
        {
            using var s = PendingSendScenario.Reach(PendingPhase.Summarizing);
            var vm = s.Vm;
            var engine = s.Engine;

            // The move ends send #1 and frees the pane, with its summarize still out.
            vm.UpdateWorkspaceRoot(s.MovedRoot, notice: null);
            Assert.False(vm.IsBusy);
            Assert.False(engine.GateSummarize!.Task.IsCompleted);

            // Send #2, in the new workspace, with its turn held OPEN - which is what the earlier check could
            // not do: a turn that completes inside the same dispatcher tick is over before the abandoned
            // summarize returns, so nothing of send #1's ever lands on a live turn.
            engine.Turns = ScriptedEngine.TurnEnding.WhenCompleted;
            vm.InputText = "a fresh question";
            vm.SendCommand.Execute(null);
            PendingSendScenario.DrainAll();

            Assert.True(vm.IsBusy, "send #2 never took the pane, so this proves nothing");
            Assert.Single(engine.Prompts);

            // Now the abandoned summarize answers: send #1 discards it and runs its finally.
            engine.GateSummarize.TrySetResult(true);
            PendingSendScenario.DrainAll();

            // Send #2's turn is still on the wire, so the pane is still its.
            Assert.True(vm.IsBusy, "the ended send's return freed the pane while another send's turn was live");
            Assert.True(vm.ShowWorkingBar, "the working bar went down mid-turn");
            Assert.True(vm.StopCommand.CanExecute(null), "Stop went dead while the agent was working");

            // And the gesture that follows is held, not a second concurrent send.
            vm.InputText = "and another";
            vm.SendCommand.Execute(null);
            PendingSendScenario.DrainAll();
            Assert.Contains(vm.PendingMessages, m => m.Text == "and another");
            Assert.Single(engine.Prompts);

            engine.CompleteTurn();
            s.Settle();
        });

        /// <summary>
        /// The other direction, which a token-based guard would break: a workspace move retires a COMMITTED
        /// turn, and that turn's finally is the only thing that frees the pane - the ending leaves IsBusy
        /// alone there, because the turn is genuinely still on the wire.
        /// </summary>
        [Fact]
        public void ARetiredCommittedTurnStillFreesThePaneWhenItReturns() => RunSta(() =>
        {
            using var s = PendingSendScenario.Reach(PendingPhase.WaitingForTools);
            var vm = s.Vm;
            var engine = s.Engine;

            // Let the first send commit and get its prompt on the wire, held open.
            engine.Turns = ScriptedEngine.TurnEnding.WhenCompleted;
            engine.ServeTools();
            // The tools' release crosses a thread-pool hop before it reaches the dispatcher (the wait is a
            // Task.WhenAny), so a fixed number of drains can run ahead of it. Measured: ten solo runs green,
            // and the same code red in the setup of a contended injection sweep. Pump, bounded.
            PendingSendScenario.PumpUntil(() => engine.Prompts.Count == 1, TimeSpan.FromSeconds(3));
            Assert.Single(engine.Prompts);
            Assert.True(vm.IsBusy);
            Assert.False(vm.HasPendingSend, "the send has prompted, so nothing is pending any more");

            // The move retires that turn. Nothing was pending, so the ending leaves the pane to it.
            vm.UpdateWorkspaceRoot(s.MovedRoot, notice: null);
            PendingSendScenario.DrainAll();
            Assert.True(vm.IsBusy, "the pane was freed while a committed turn was still on the wire");

            // Its return is what frees the pane.
            engine.CompleteTurn();
            s.Settle();
            Assert.False(vm.IsBusy, "the retired turn returned and the pane stayed busy for good");
        });

        private static void RunSta(Action action) => StaTest.Run(action, withDispatcherContext: true);
    }
}
