using System;
using System.Linq;
using CodeWicket.Core;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// A send ended by a conversation change stops holding the pane AT ONCE, whatever it was still waiting
    /// on (user decision, 2026-09-15).
    /// </summary>
    /// <remarks>
    /// <para><b>Measured in Visual Studio:</b> a workspace move during a summary resume left the working bar
    /// up for about 20 seconds - until the summarizer returned - for a send that no longer existed. The pane
    /// was busy on behalf of nothing: the send had been retired, its result would be discarded on arrival,
    /// and the only thing still tying the two together was that <c>IsBusy</c> was cleared by the turn
    /// runner's finally, which cannot run until the await it is wrapped around returns.</para>
    /// <para><b>The consequence is what the second test is about.</b> Once the bar clears, the user can send
    /// again while the old call is still in flight - so the two must not collide, and the late result must
    /// not be applied to the conversation that replaced it.</para>
    /// </remarks>
    public sealed class SendEndingFreesThePaneTests
    {
        /// <summary>
        /// The move ends the send, so the pane is free in that same step - not one dispatcher post later and
        /// not when the summarizer eventually answers. Asserted with no drain and with the summarize still
        /// held, which is the whole point: nothing has returned.
        /// </summary>
        [Fact]
        public void AConversationChangeFreesThePaneWhileTheSummarizeIsStillOut() => RunSta(() =>
        {
            using var s = PendingSendScenario.Reach(PendingPhase.Summarizing);
            var vm = s.Vm;
            Assert.True(vm.IsBusy, "the send holds the pane while the recap is written");
            Assert.False(s.Engine.GateSummarize!.Task.IsCompleted, "the summarize must still be out");

            vm.UpdateWorkspaceRoot(s.MovedRoot, notice: null);

            // At once: no drain, no pump, and the summarizer has still not answered.
            Assert.False(vm.IsBusy, "the pane stayed busy for a send the move had already ended");
            Assert.False(vm.ShowWorkingBar, "the working bar stayed up for a send that no longer exists");
            Assert.False(s.Engine.GateSummarize.Task.IsCompleted);

            s.Settle();
        });

        /// <summary>
        /// The check the freeing owes. With the pane free, the user can send in the new
        /// workspace while the old summarize is still in flight - so the new send's session start must not
        /// land in the engine beside another, and the old recap must not be applied when it arrives.
        /// </summary>
        /// <remarks>
        /// Session starts already chain (the lifetime records every start's outcome, and the whole take counts
        /// as the start in flight), but a summarize is not a start, so nothing ordered it against one. What
        /// makes this safe is the check after every await rather than ordering: the ended send finds its token stale and drops the recap on return.
        /// </remarks>
        [Fact]
        public void ANewSendBesideTheInFlightSummarizeNeitherOverlapsNorAppliesTheStaleRecap() => RunSta(() =>
        {
            using var s = PendingSendScenario.Reach(PendingPhase.Summarizing);
            var vm = s.Vm;

            vm.UpdateWorkspaceRoot(s.MovedRoot, notice: null);
            Assert.False(vm.IsBusy);

            // The user sends in the new workspace, with the old summarize still held.
            vm.InputText = "a fresh question";
            vm.SendCommand.Execute(null);
            PendingSendScenario.DrainAll();

            // Only now does the abandoned summarize answer.
            s.Engine.GateSummarize!.TrySetResult(true);
            s.Settle();

            Assert.Equal(0, s.Engine.OverlappingStarts);
            var prompt = Assert.Single(s.Engine.Prompts);
            Assert.DoesNotContain(HostPromptBlocks.ConversationSummary.Open, prompt, StringComparison.Ordinal);
            Assert.Contains("a fresh question", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.Contains("Resumed from a summary", StringComparison.Ordinal));
            Assert.DoesNotContain(vm.Items.OfType<MessageItemViewModel>(),
                m => m.Text == PendingSendScenario.Message);
        });

        private static void RunSta(Action action) => StaTest.Run(action, withDispatcherContext: true);
    }
}
