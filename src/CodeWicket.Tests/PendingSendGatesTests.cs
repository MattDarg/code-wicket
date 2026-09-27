using System;
using System.Linq;
using CodeWicket.Ipc;
using CodeWicket.Shell;
using CodeWicket.Shell.Sessions;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// While a send is pending, the controls that would replace the conversation under it are refused, and
    /// every one of them says why.
    /// </summary>
    /// <remarks>
    /// <para><b>Refusing them is what shrinks the conversation-change route list</b> to delete, a workspace
    /// move and an engine exit. The banner's Cancel is the one way out that puts things back; New or a picker
    /// change with a message mid-question is a second, lossy one.</para>
    /// <para><b>And a control disabled with no reason reads as broken</b>, which is why the reason is asserted
    /// beside the refusal rather than left to review. The history rows were worse than silent before: their
    /// commands had no CanExecute at all, so the row drew enabled and the click simply did nothing.</para>
    /// </remarks>
    public sealed class PendingSendGatesTests
    {
        public static TheoryData<PendingPhase, string> BlockedPhases() => new()
        {
            // Waiting on the user: a banner is on screen and its Cancel is the way out.
            { PendingPhase.ChoiceBanner, SendPhases.AnswerTheQuestion },
            { PendingPhase.AskingRefused, SendPhases.AnswerTheQuestion },
            // Waiting on us: no banner, so the only two things the user can do are wait or Stop.
            { PendingPhase.Summarizing, SendPhases.WaitOrStop },
            { PendingPhase.WaitingForTools, SendPhases.WaitOrStop },
        };

        /// <summary>
        /// New, the two pickers and the history rows are all refused, in every phase and not only the busy
        /// ones. The Choice banner is the case that was open: nothing is busy there, so New, the pickers, a
        /// history open and an import were all live over a message the user had not finished sending.
        /// </summary>
        [Theory]
        [MemberData(nameof(BlockedPhases))]
        public void EveryControlThatWouldReplaceTheConversationIsRefusedAndSaysWhy(PendingPhase phase, string reason) =>
            RunSta(() =>
            {
                using var s = PendingSendScenario.Reach(phase);
                var vm = s.Vm;

                Assert.Equal(reason, vm.PendingSendBlockReason);
                Assert.False(vm.NewSessionCommand.CanExecute(null), "New Session is offered while a send is pending");
                Assert.False(vm.CanChangeSelection, "the pickers are live while a send is pending");

                vm.RefreshHistory();
                var other = vm.History.Single(h => h.Id == s.OtherId);
                Assert.False(other.LoadCommand.CanExecute(null), "a history row is offered while a send is pending");
            });

        /// <summary>
        /// An import replaces the backend session before it swaps anything, so it must not run over a pending
        /// send. It was refused only while the pane was BUSY - and on the Choice banner it is not, so an
        /// import there tore down the session the parked send was about to use.
        /// </summary>
        [Fact]
        public void AnImportIsRefusedWhileTheChoiceBannerIsUp() => RunSta(() =>
        {
            using var s = PendingSendScenario.Reach(PendingPhase.ChoiceBanner);
            var vm = s.Vm;
            var starts = s.Engine.StartCount;

            var import = vm.ImportBackendSessionAsync(new BackendSessionItemViewModel(
                new BackendSessionDto("imported-1", "Imported", null, null), "fake", "Fake", _ => { }));

            Assert.True(import.IsCompleted);
            Assert.Equal(starts, s.Engine.StartCount);
            Assert.Equal("Continue this conversation?", vm.PendingResume?.Heading);
        });

        /// <summary>
        /// With nothing pending there is no reason to give, and nothing is refused on its account. The
        /// property is null rather than empty so a binding can hide on it.
        /// </summary>
        [Fact]
        public void WithNoSendPendingNothingIsRefusedAndNothingSaysWhy() => RunSta(() =>
        {
            using var s = PendingSendScenario.Reach(PendingPhase.ChoiceBanner);
            var vm = s.Vm;

            vm.PendingResume!.CancelCommand.Execute(null);
            PendingSendScenario.Drain();

            Assert.Null(vm.PendingSendBlockReason);
            Assert.True(vm.NewSessionCommand.CanExecute(null));
            Assert.True(vm.CanChangeSelection);
        });

        /// <summary>
        /// The working bar says the agent is working, so it goes down while a resume banner is waiting on the
        /// user: by the time #268 asks, the reload has been refused and nothing is running. It stays up while the send waits on US, where Stop is the only way out.
        /// </summary>
        [Theory]
        [InlineData(PendingPhase.AskingRefused, false)]
        [InlineData(PendingPhase.Summarizing, true)]
        [InlineData(PendingPhase.WaitingForTools, true)]
        public void TheWorkingBarIsDownWhileABannerWaitsOnTheUser(PendingPhase phase, bool expected) => RunSta(() =>
        {
            using var s = PendingSendScenario.Reach(phase);

            Assert.Equal(expected, s.Vm.ShowWorkingBar);
            // Stop follows the bar, so no hidden control is left live behind it.
            Assert.Equal(expected, s.Vm.StopCommand.CanExecute(null));
        });

        /// <summary>
        /// A picker change is refused while a send is pending, and the send still answers (scoped review,
        /// 2026-09-14). The control is disabled, so no click gets here - but the property is public
        /// and the Desktop harness writes it directly, which reaches a state no click can.
        /// </summary>
        /// <remarks>
        /// Refusing it is what keeps this from being NEW breakage: neither dropping branch cleared the banner
        /// before, so a parked send used to SURVIVE a picker change. Ending it there instead would leave the
        /// banner up over dead buttons, with the pane going idle and the message on screen unrecorded.
        /// </remarks>
        [Theory]
        [InlineData(PendingPhase.ChoiceBanner)]
        [InlineData(PendingPhase.AskingRefused)]
        public void APickerChangeIsRefusedWhileASendIsPending(PendingPhase phase) => RunSta(() =>
        {
            using var s = PendingSendScenario.Reach(phase);
            var vm = s.Vm;
            var heading = vm.PendingResume?.Heading;

            vm.SelectedProvider = vm.Providers.Single(p => p.Id == PendingSendScenario.OtherProvider);
            PendingSendScenario.Drain();

            Assert.DoesNotContain(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.StartsWith("Switched to", StringComparison.Ordinal));
            Assert.True(vm.HasPendingSend, "the picker change ended the send it was refused for");
            Assert.Equal(heading, vm.PendingResume?.Heading);
        });

        /// <summary>
        /// The direction the bar must NOT be hidden in: a permission banner. A turn is live behind one - the
        /// agent is blocked on us, not finished - so the bar stays up and Stop keeps its meaning, which is a
        /// legitimate answer to being asked for permission (Deny refuses one action and lets the agent try
        /// another).
        /// </summary>
        /// <remarks>
        /// Asserted rather than inferred. The rule reads "working, and not waiting on the USER", and it is
        /// only a pending SEND that counts as waiting on the user; a permission banner is not one. That
        /// distinction held here by reasoning alone, and the injection for this rule pins just the "down"
        /// direction, so nothing failed if the bar were keyed on banners in general.
        /// </remarks>
        [Fact]
        public void APermissionBannerKeepsTheWorkingBarAndItsStop() => RunSta(() =>
        {
            var engine = new ScriptedEngine { Turns = ScriptedEngine.TurnEnding.WhenCompleted }
                .WithProvider("fake", "Fake");
            var vm = new ChatViewModel(engine, new StartSessionRequest("fake", null, TempDir(), "Prompt", null));
            vm.InitializeAsync().GetAwaiter().GetResult();

            vm.InputText = "do the thing";
            vm.SendCommand.Execute(null);
            PendingSendScenario.DrainAll();
            Assert.True(vm.IsBusy, "the turn never went live, so this proves nothing");

            var decision = vm.RequestPermissionAsync(new PermissionRequestDto(
                "t1", "Write Program.cs", "edit", null, null,
                new[] { new PermissionOptionDto("allow", "Allow", "allow_once") }));
            PendingSendScenario.Drain();
            Assert.NotNull(vm.PendingPermission);

            // No send is pending - the turn has prompted - so nothing here is "waiting on the user".
            Assert.False(vm.HasPendingSend);
            Assert.True(vm.ShowWorkingBar, "the working bar went down behind a permission banner");
            Assert.True(vm.StopCommand.CanExecute(null), "Stop went dead behind a permission banner");

            vm.PendingPermission!.Options[0].Command.Execute(null);
            PendingSendScenario.Drain();
            engine.CompleteTurn();
            PendingSendScenario.DrainAll();
            Assert.True(decision.IsCompleted);
        });

        /// <summary>
        /// A refused picker write SAYS the value did not change, so a two-way bound control that has already
        /// moved snaps back (scoped review, 2026-09-16).
        /// </summary>
        /// <remarks>
        /// <para>Returning silently is the same lie as the defect the guard itself fixes, in the other
        /// direction: the combo would go on showing a backend the pane refused to switch to. A sharp edge
        /// rather than a live bug today - the pickers are disabled while a send is pending and the direct
        /// writers are unbound - which is exactly why it is asserted rather than left to be discovered by
        /// whoever next binds one two-way.</para>
        /// <para><b>Written after the fix.</b> Verified load-bearing by removing the pending-send guard from
        /// the picker setters, which carries the refusal and its notification together: this test fails, with
        /// <c>APickerChangeIsRefusedWhileASendIsPending</c> and
        /// <c>ConversationChangeOrderingTests.EveryRouteThatWouldReplaceTheConversationIsRefused</c> - three
        /// tests, seven cases counting each theory's phases, and nothing else in the suite (2026-09-27).</para>
        /// </remarks>
        [Fact]
        public void ARefusedPickerWriteSaysTheValueDidNotChange() => RunSta(() =>
        {
            using var s = PendingSendScenario.Reach(PendingPhase.ChoiceBanner);
            var vm = s.Vm;

            var raised = 0;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ChatViewModel.SelectedProvider))
                    raised++;
            };

            var before = vm.SelectedProvider;
            vm.SelectedProvider = vm.Providers.Single(p => p.Id == PendingSendScenario.OtherProvider);
            PendingSendScenario.Drain();

            Assert.Same(before, vm.SelectedProvider);
            Assert.True(
                raised > 0,
                "the refusal was silent, so a bound picker keeps showing the backend the pane refused");
        });

        private static string TempDir()
        {
            var dir = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "cwkt-gates-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>
        /// A history row's command reads <c>IsBusy</c>, so a turn STARTING must re-query it. Opening the
        /// picker rebuilds the rows, which hides this in the common case; what it does not cover is a turn
        /// beginning while the popup is already open, where the row stays drawn enabled and the click does
        /// nothing - the "refused with no reason" state these gates exist to remove.
        /// </summary>
        /// <remarks>
        /// Asserted on the EVENT, not on <c>CanExecute</c>: the predicate is evaluated live, so it already
        /// returns the right answer. What was missing is anything telling the control to ask again.
        /// </remarks>
        /// <remarks>
        /// <para><b>It has to reach a real TURN, and driven from the Choice banner it never did</b>.
        /// Backing out there leaves the conversation resumable and the strategy Fresh, so
        /// the next send parked on the banner again: <c>SendCoreAsync</c> was never entered and
        /// <c>IsBusy</c> never went true. Both assertions were satisfied by the pending-send route alone
        /// (Phase = Deciding raises the same refresh), so the <c>IsBusy</c> setter's refresh - the thing this
        /// test names - could be deleted with the suite green.</para>
        /// <para>A transcript below <c>ResumeDecider</c>'s threshold resumes silently, so there is no banner
        /// and the send goes straight to a turn. The busy assertion is what keeps this honest: without it the
        /// test passes again the moment the send parks on anything.</para>
        /// </remarks>
        [Fact]
        public void AHistoryRowIsReQueriedWhenATurnStarts() => RunSta(() =>
        {
            var dir = TempDir();
            var store = new FileSessionStore(dir);

            var older = new PersistedSession
            {
                WorkspaceRootPath = dir,
                ProviderId = "fake",
                Title = "Something else",
                UpdatedUtc = DateTime.UtcNow.AddHours(-1),
            };
            older.Log.Add(new TranscriptEntry { Role = "user", Text = "an older question" });
            store.Save(older);

            // Small enough that the decider resumes it silently: no banner, so the send reaches a turn.
            var current = new PersistedSession
            {
                WorkspaceRootPath = dir,
                ConversationId = "conv-1",
                ProviderId = "fake",
                Title = "Earlier work",
            };
            current.Log.Add(new TranscriptEntry { Role = "user", Text = "hello" });
            store.Save(current);

            var engine = new ScriptedEngine { Turns = ScriptedEngine.TurnEnding.WhenCompleted }
                .WithProvider("fake", "Fake", "ResumeSession");
            var vm = new ChatViewModel(
                engine, new StartSessionRequest("fake", null, dir, "Prompt", null), sessionStore: store);
            vm.InitializeAsync().GetAwaiter().GetResult();
            vm.RestoreMostRecentSession();
            PendingSendScenario.Drain();

            vm.RefreshHistory();
            var row = vm.History.Single(h => h.Id == older.Id);
            Assert.True(row.LoadCommand.CanExecute(null), "the pane is idle, so the row is offered");

            // The picker is open and the rows are built; now a turn begins.
            var reQueried = 0;
            row.LoadCommand.CanExecuteChanged += (_, _) => reQueried++;
            vm.InputText = "off we go";
            vm.SendCommand.Execute(null);
            PendingSendScenario.DrainAll();

            Assert.True(vm.IsBusy, "the send never reached a turn, so this test would prove nothing");
            Assert.Empty(vm.PendingMessages);
            Assert.True(reQueried > 0, "a turn started and the open picker's rows were never asked again");
            Assert.False(row.LoadCommand.CanExecute(null));

            engine.CompleteTurn();
            PendingSendScenario.DrainAll();
        });

        private static void RunSta(Action action) => StaTest.Run(action, withDispatcherContext: true);
    }
}
