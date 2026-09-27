using System;
using System.IO;
using System.Linq;
using CodeWicket.Ipc;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Every route that changes the conversation on screen ends a pending send FIRST, before anything on
    /// screen changes, and the ended send does nothing afterwards.
    /// </summary>
    /// <remarks>
    /// <para><b>One theory per phase, over the routes that phase can actually reach.</b> That list
    /// is the same in every phase, because every control that would replace the conversation is refused while
    /// a send is pending, and says why: New, the pickers, a history
    /// open and an import are all refused while a send is pending, so what remains is a delete on screen and
    /// a workspace move. <see cref="EveryRouteThatWouldReplaceTheConversationIsRefused"/> checks the gates
    /// rather than trusting this list. Engine exit is covered by its own suite.</para>
    /// <para><b>Two halves.</b> Asserted at once, with no drain: nothing is left pending and the banner is
    /// gone. A post or an await put between the ending and the screen change fails that half. Then drained:
    /// no prompt, every saved file byte-identical, and nothing from the ended send on screen. A continuation
    /// that still acts fails that half.</para>
    /// <para><b>What these can see, now that every phase holds a send</b>. If only a send
    /// parked on a resume-failure banner held an object, then on the Choice banner, while summarizing and
    /// while waiting for the tools the immediate half could not fail whatever the route did, and the drained
    /// checks there would pin #217's epoch retirement rather than the call. Each phase holds
    /// one from Enter to End, so a late or missing <c>BeginConversationChange</c> fails the immediate half in
    /// EVERY phase rather than only on a delete while parked.</para>
    /// <para><b>And the message comes back whole</b>. Every in-app retirement hands the composer
    /// the text, the pasted image and the IDE capture, with no notice saying so - the box filling back up
    /// is the signal, as it is after a Cancel or a Stop. Asserted in the immediate half, because the
    /// give-back is part of the ending rather than something a later post does.</para>
    /// </remarks>
    public sealed class ConversationChangeOrderingTests
    {
        public enum Route
        {
            WorkspaceMove,
            DeleteOnScreen,
        }

        // What the ended send would put on screen if it went on acting.
        private static readonly string[] EndedSendNotices =
        {
            "Summarizing this conversation",
            "Resuming this conversation",
            "Resumed from a summary",
            "Could not summarize",
            "Couldn't reload this conversation",
        };

        [Theory]
        [InlineData(Route.WorkspaceMove)]
        [InlineData(Route.DeleteOnScreen)]
        public void WhileTheChoiceBannerIsUp(Route route) => RunSta(() => Check(PendingPhase.ChoiceBanner, route));

        [Theory]
        [InlineData(Route.WorkspaceMove)]
        [InlineData(Route.DeleteOnScreen)]
        public void WhileTheRecapIsBeingWritten(Route route) => RunSta(() => Check(PendingPhase.Summarizing, route));

        [Theory]
        [InlineData(Route.WorkspaceMove)]
        [InlineData(Route.DeleteOnScreen)]
        public void WhileParkedOnTheRefusedReload(Route route) => RunSta(() => Check(PendingPhase.AskingRefused, route));

        [Theory]
        [InlineData(Route.WorkspaceMove)]
        [InlineData(Route.DeleteOnScreen)]
        public void WhileWaitingForTheIdeTools(Route route) => RunSta(() => Check(PendingPhase.WaitingForTools, route));

        /// <summary>
        /// The routes left out of the theories above are left out because the pane REFUSES them, not because
        /// this file forgot them - and that is asserted per phase rather than assumed.
        /// </summary>
        /// <remarks>
        /// Each is checked twice: the control says no (so it draws disabled rather than doing nothing), and
        /// the underlying route says no as well, since <c>RelayCommand.Execute</c> does not consult
        /// <c>CanExecute</c> and the Desktop harness writes <c>SelectedProvider</c> straight through. For the
        /// pickers the second check is in the SETTER, which refuses before the change lands - refusing after
        /// it left the picker showing a backend the pane had not moved to.
        /// </remarks>
        [Theory]
        [InlineData(PendingPhase.ChoiceBanner)]
        [InlineData(PendingPhase.Summarizing)]
        [InlineData(PendingPhase.AskingRefused)]
        [InlineData(PendingPhase.WaitingForTools)]
        public void EveryRouteThatWouldReplaceTheConversationIsRefused(PendingPhase phase) => RunSta(() =>
        {
            using var s = PendingSendScenario.Reach(phase);
            var vm = s.Vm;

            Assert.True(vm.HasPendingSend);
            Assert.False(vm.NewSessionCommand.CanExecute(null), "New Session is offered while a send is pending");
            Assert.False(vm.CanChangeSelection, "the pickers are live while a send is pending");

            // A history open: refused on the row, and refused by LoadSession itself.
            var title = vm.CurrentSessionTitle;
            vm.RefreshHistory();
            var other = vm.History.Single(h => h.Id == s.OtherId);
            Assert.False(other.LoadCommand.CanExecute(null), "a history row is offered while a send is pending");
            other.LoadCommand.Execute(null);
            PendingSendScenario.Drain();
            Assert.Equal(title, vm.CurrentSessionTitle);

            // An import, which replaces the backend session before it swaps anything.
            var starts = s.Engine.StartCount;
            var import = vm.ImportBackendSessionAsync(ImportRow());
            Assert.True(import.IsCompleted);
            Assert.Equal(starts, s.Engine.StartCount);

            // And a picker write, which no click can make but the harness can.
            vm.SelectedProvider = vm.Providers.Single(p => p.Id == PendingSendScenario.OtherProvider);
            PendingSendScenario.Drain();
            Assert.DoesNotContain(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.StartsWith("Switched to", StringComparison.Ordinal));

            // Through all of it the send is still there, still answerable.
            Assert.True(vm.HasPendingSend, "a refused route ended the send anyway");
        });

        private static void Check(PendingPhase phase, Route route)
        {
            using var s = PendingSendScenario.Reach(phase, withChips: true);
            var vm = s.Vm;
            var before = s.SavedFiles();

            Take(route, s);

            // At once: no drain, no pump.
            Assert.False(vm.HasPendingSendForDiagnostics, $"{route} left a send pending on {phase}");
            Assert.Null(vm.PendingResume);

            // The whole message, in the box, with nothing announcing it. The image comes back with
            // its BYTES, which is what makes it re-sendable: the composer holds a pasted snip in memory
            // until the message actually goes out, so a give-back needs nothing on disk to be whole.
            Assert.Equal(PendingSendScenario.Message, vm.InputText);
            var image = Assert.Single(vm.PendingAttachments);
            Assert.Equal(PendingSendScenario.ImageName, image.Name);
            Assert.NotNull(image.Bytes);
            Assert.Equal(
                PendingSendScenario.ContextLabel,
                Assert.Single(vm.PendingContexts).Label);

            s.Settle();

            Assert.Empty(s.Engine.Prompts);

            var after = s.SavedFiles();
            foreach (var saved in before)
            {
                if (!after.TryGetValue(saved.Key, out var now))
                {
                    Assert.True(route == Route.DeleteOnScreen && saved.Key.Contains(s.ConversationId),
                        $"{Path.GetFileName(saved.Key)} disappeared after {route} on {phase}");
                    continue;
                }

                Assert.True(saved.Value.AsSpan().SequenceEqual(now),
                    $"{Path.GetFileName(saved.Key)} changed after {route} on {phase}");
            }

            Assert.Empty(after.Keys.Except(before.Keys));

            Assert.DoesNotContain(vm.Items.OfType<MessageItemViewModel>(), m => m.Text == PendingSendScenario.Message);
            Assert.DoesNotContain(vm.Items.OfType<NoticeItemViewModel>(),
                n => EndedSendNotices.Any(t => n.Text.Contains(t, StringComparison.Ordinal)));
        }

        private static void Take(Route route, PendingSendScenario s)
        {
            var vm = s.Vm;
            switch (route)
            {
                case Route.WorkspaceMove:
                    vm.UpdateWorkspaceRoot(s.MovedRoot, notice: null);
                    break;

                case Route.DeleteOnScreen:
                    vm.RefreshHistory();
                    vm.History.Single(h => h.IsCurrent).DeleteCommand.Execute(null);
                    break;
            }
        }

        private static BackendSessionItemViewModel ImportRow() =>
            new(new BackendSessionDto("imported-1", "Imported", null, null), "fake", "Fake", _ => { });

        private static void RunSta(Action action) => StaTest.Run(action, withDispatcherContext: true);
    }
}
