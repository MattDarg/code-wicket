using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using CodeWicket.Ipc;
using CodeWicket.Shell;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The tray's hold, and the ONE gate that reads it. Stop and a banner's Cancel both hold what is
    /// queued; nothing automatic releases it until the user sends something of their own.
    /// </summary>
    /// <remarks>
    /// <para><b>The leak these exist for.</b> The hold used to be read on the two turn-end routes
    /// only. The NEXT-STEP route never read it, and it is the one Stop actually reaches: the cancelled
    /// call reports after <c>IsBusy</c> has gone false, its <c>toolDone</c> drives
    /// <c>TryReleasePending(NextStep)</c>, and in Steer mode the tray went out a moment after the user
    /// pressed Stop.</para>
    /// <para><b>The mode switch is behaviour, not the fix.</b> Stop moving the pill to Queue makes that
    /// route return at its first check - but only while the mode stays Queue, and the user may set Steer
    /// back while the messages are still held. That is why the second test here must fail both on the
    /// code before this work and on code carrying the switch alone.</para>
    /// </remarks>
    public sealed class TrayHoldTests
    {
        private const string Held = "and then run the tests";

        /// <summary>
        /// (a) Steer mode, a message held, Stop, and the stopped call reporting late. Nothing goes out,
        /// and the pill has moved to Queue.
        /// </summary>
        [Fact]
        public void AStoppedTraysLateBoundaryDoesNotReleaseIt() => RunSta(() =>
        {
            var (vm, engine) = HeldUnderSteer();

            vm.StopCommand.Execute(null);
            engine.CompleteTurn();
            DrainDispatcher();

            // The call the cancel aborted reports afterwards - measured on the wire, and the reason the
            // turn-end pre-checks alone never closed this.
            engine.Raise(new AgentEventDto { Type = "toolDone", ToolCallId = "t1" });
            DrainDispatcher();

            Assert.Single(vm.PendingMessages);
            Assert.Empty(engine.Steers);
            Assert.DoesNotContain(Held, string.Join("\n", engine.Prompts), StringComparison.Ordinal);
            Assert.Equal(PendingRelease.TurnEnd, vm.PendingReleaseMode);
        });

        /// <summary>
        /// (b) The same, with the user setting Steer back before the late boundary. This is the case the
        /// mode switch cannot cover, and the whole reason the gate is where messages are RELEASED.
        /// </summary>
        [Fact]
        public void SteerSetBackWhileHeldStillDoesNotReleaseOnALateBoundary() => RunSta(() =>
        {
            var (vm, engine) = HeldUnderSteer();

            vm.StopCommand.Execute(null);
            engine.CompleteTurn();
            DrainDispatcher();
            Assert.Equal(PendingRelease.TurnEnd, vm.PendingReleaseMode);

            // The user puts the pill back while the messages are still held. Nothing is running, so this
            // releases nothing by itself - the setter only releases into a live turn.
            vm.PendingReleaseMode = PendingRelease.NextStep;
            DrainDispatcher();
            Assert.Single(vm.PendingMessages);

            engine.Raise(new AgentEventDto { Type = "toolDone", ToolCallId = "t1" });
            DrainDispatcher();

            Assert.Single(vm.PendingMessages);
            Assert.Empty(engine.Steers);
            Assert.DoesNotContain(Held, string.Join("\n", engine.Prompts), StringComparison.Ordinal);
        });

        /// <summary>
        /// (c) With an empty tray Stop leaves the pill alone: the switch would change a setting the user
        /// can see and protect nothing.
        /// </summary>
        [Fact]
        public void StopWithAnEmptyTrayLeavesTheReleaseModeAlone() => RunSta(() =>
        {
            var engine = HeldTurnEngine(supportsSteering: true);
            var vm = StartedSession(engine);
            vm.PendingReleaseMode = PendingRelease.NextStep;

            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "t1", Title = "run_tests" });
            DrainDispatcher();

            vm.StopCommand.Execute(null);
            engine.CompleteTurn();
            DrainDispatcher();

            Assert.Empty(vm.PendingMessages);
            Assert.Equal(PendingRelease.NextStep, vm.PendingReleaseMode);
        });

        /// <summary>
        /// The hold is a delay, not a block, and ANY user send gesture lifts it - including one that
        /// lands in the tray behind the messages already there. Enter while the pane is busy goes to
        /// the tray rather than out, and it is still the user choosing to say something.
        /// </summary>
        /// <remarks>
        /// The stopped turn is deliberately left in flight here (<c>CancelCompletesTurn: false</c>): the
        /// case is the user typing between Stop and that turn's return, which is where a hold cleared
        /// only by a send that PROMPTS leaves the whole tray waiting for a second Enter.
        /// </remarks>
        [Fact]
        public void AMessageThatLandsInTheTrayLiftsTheHold() => RunSta(() =>
        {
            var (vm, engine) = HeldUnderSteer(cancelCompletesTurn: false);

            vm.StopCommand.Execute(null);
            DrainDispatcher();
            Assert.True(vm.IsBusy, "the stopped turn has already returned, so this is not the case under test");

            vm.InputText = "try something else";
            vm.SendCommand.Execute(null);
            DrainDispatcher();
            Assert.Equal(2, vm.PendingMessages.Count);

            // The stopped turn returns, and its end is the release point the whole tray now rides on.
            engine.CompleteTurn();
            DrainDispatcher();

            Assert.Empty(vm.PendingMessages);
            Assert.Contains(Held, string.Join("\n", engine.Prompts), StringComparison.Ordinal);
        });

        /// <summary>
        /// Stop and a banner's Cancel are different gestures and the tray says which happened.
        /// Cancel takes back the message the held ones follow; it did not stop the agent, and a sentence
        /// saying it did sends the user looking for a Stop they never pressed.
        /// </summary>
        [Theory]
        [InlineData(PendingPhase.ChoiceBanner)]
        [InlineData(PendingPhase.AskingRefused)]
        public void TheBannersCancelHoldsTheTrayInItsOwnWords(PendingPhase phase) => RunSta(() =>
        {
            using var s = PendingSendScenario.Reach(phase);
            var vm = s.Vm;

            vm.InputText = Held;
            vm.SendCommand.Execute(null);
            PendingSendScenario.Drain();
            Assert.Single(vm.PendingMessages);

            var mode = vm.PendingReleaseMode;
            vm.PendingResume!.CancelCommand.Execute(null);
            PendingSendScenario.DrainAll();

            Assert.Single(vm.PendingMessages);
            Assert.Empty(s.Engine.Prompts);
            Assert.Contains("took back", vm.PendingStatus, StringComparison.Ordinal);
            Assert.DoesNotContain("stopped", vm.PendingStatus, StringComparison.OrdinalIgnoreCase);
            // A Cancel means "as it was", so it leaves a setting the user can see alone.
            Assert.Equal(mode, vm.PendingReleaseMode);
        });

        /// <summary>
        /// The hold outranks whatever is still working. A cancelled turn does not end when the button is
        /// pressed - it ends when the prompt returns - and in that window the tray was describing a
        /// release that the hold had already made impossible.
        /// </summary>
        /// <remarks>
        /// <b>And the window is not always short.</b> A steer or a cancel can ORPHAN an MCP call that
        /// never answers at all, so "until the prompt returns" is unbounded in the case that matters
        /// most. The sentence read "Queued - sending when this turn ends", which is exactly what the gate
        /// prevents: they go on the user's next message, not on this turn's end.
        /// </remarks>
        [Fact]
        public void TheHeldTraySaysSoWhileTheStoppedTurnIsStillInFlight() => RunSta(() =>
        {
            var (vm, _) = HeldUnderSteer(cancelCompletesTurn: false);

            vm.StopCommand.Execute(null);
            DrainDispatcher();

            Assert.True(vm.IsBusy, "the turn has already returned, so this is not the window under test");
            Assert.Contains("you stopped", vm.PendingStatus, StringComparison.Ordinal);
            Assert.DoesNotContain("this turn ends", vm.PendingStatus, StringComparison.Ordinal);
            Assert.DoesNotContain("next step", vm.PendingStatus, StringComparison.Ordinal);
        });

        /// <summary>
        /// The same rule for the other gesture, and the route to it is not obvious. A banner's Cancel
        /// can leave a BackedOut hold on a pane that still reads as WORKING - so the hold has to outrank
        /// what is working for both reasons, not just for Stop's.
        /// </summary>
        /// <remarks>
        /// <para><b>Why it looks unreachable and is not.</b> A send's own prologue closes the out-of-turn
        /// window, so on the #84 and #268 banners - which are raised from INSIDE the turn runner - the
        /// pane is never working behind the banner. The Choice and moved-root banners are raised BEFORE
        /// the runner, so a window open when the user pressed Enter is still open when they cancel.</para>
        /// <para><b>And the window does not need a turn</b>, which is the part that makes this possible:
        /// it exists (issue #256) for work with no turn open, so it can be up with <c>IsBusy</c> false.
        /// A picker change on a prompted pane supersedes without clearing <c>_prompted</c>, which is what
        /// the window's own guard reads, and makes the next send a first continuation - so the banner and
        /// the window can be up at once. The picker is changed BEFORE the window opens, because the
        /// window itself refuses a picker change.</para>
        /// <para><b>The follow-up is held by the pending SEND, not by IsBusy</b>: nothing is busy
        /// here, and before the send object existed this Enter would have started a second send rather
        /// than landing in the tray.</para>
        /// </remarks>
        [Fact]
        public void ABackedOutHoldSaysSoWhileOutOfTurnWorkIsStillRunning() => RunSta(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "cwkt-backedout-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var engine = new ScriptedEngine { Turns = ScriptedEngine.TurnEnding.WhenCompleted };
                engine.WithProvider("fake", "Fake");
                engine.WithProvider("other", "Other");
                var vm = new ChatViewModel(
                    engine,
                    new StartSessionRequest("fake", null, dir, "Prompt", null),
                    sessionStore: new FileSessionStore(dir));
                vm.InitializeAsync(Task.FromResult(new ListProvidersResponse(new List<ProviderInfoDto>
                {
                    new("fake", "Fake", new List<ModelInfoDto> { new("auto", "auto") }, new List<string> { "ResumeSession" }),
                    new("other", "Other", new List<ModelInfoDto> { new("auto", "auto") }, new List<string> { "ResumeSession" }),
                })));
                DrainDispatcher();

                vm.InputText = "first message";
                vm.SendCommand.Execute(null);
                DrainDispatcher();
                // Over ResumeDecider's 4000-character threshold, so the next send asks how to resume.
                engine.Raise(new AgentEventDto { Type = "text", Text = new string('x', 6000) });
                DrainDispatcher();
                engine.CompleteTurn();
                DrainDispatcher();

                // Before the window opens: the window makes the pane read as working, and a picker change
                // is refused while it does.
                vm.SelectedProvider = vm.Providers.First(p => p.Id == "other");
                DrainDispatcher();

                // A turn-less live event on a prompted pane - a background task reporting back - opens the
                // out-of-turn window with no turn on the wire.
                engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "bg1", Title = "Read" });
                DrainDispatcher();
                Assert.True(vm.IsAgentWorkingOutOfTurn, "no out-of-turn window, so this proves nothing");

                vm.InputText = "second message";
                vm.SendCommand.Execute(null);
                DrainDispatcher();
                Assert.Equal("Continue this conversation?", vm.PendingResume?.Heading);
                Assert.True(vm.IsAgentWorkingOutOfTurn, "the send closed the window, so this proves nothing");

                vm.InputText = "a follow-up";
                vm.SendCommand.Execute(null);
                DrainDispatcher();
                Assert.Single(vm.PendingMessages);
                Assert.False(vm.IsBusy, "it was held by IsBusy rather than by the pending send");

                vm.PendingResume!.CancelCommand.Execute(null);
                DrainDispatcher();

                Assert.True(vm.IsAgentWorking, "nothing reads as working, so this is the case already covered");
                Assert.Contains("took back", vm.PendingStatus, StringComparison.Ordinal);
                Assert.DoesNotContain("this turn ends", vm.PendingStatus, StringComparison.Ordinal);
                Assert.DoesNotContain("next step", vm.PendingStatus, StringComparison.Ordinal);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
            }
        });

        /// <summary>
        /// A workspace move HOLDS the carried tray, and the hold is what stops a release that really
        /// would fire: the cancel that retires the turn closes the out-of-turn window, and that posts one.
        /// </summary>
        /// <remarks>
        /// <para><b>Why the hold needs its own check, and why the obvious one cannot fail.</b> Asserting
        /// "nothing was sent" after an ordinary move proves nothing about the hold: the turn the move
        /// cancels takes the turn runner's retired branch and schedules no release, so no release path
        /// fires whether the tray is held or not. A proof that cannot fail is not a proof. This builds the
        /// case where one DOES fire.</para>
        /// <para><b>The path, and it is NOT the clear — that reading is wrong and is the sort of thing a
        /// green check will not correct.</b> An open out-of-turn window makes `IsAgentWorking` true, so
        /// `retiringLiveTurn` is true and `ApplyWorkspaceRoot` calls `CancelAsync(stopping: false)`, which
        /// sets `IsAgentWorkingOutOfTurn = false` SYNCHRONOUSLY — before its first `await`, and before
        /// anything has cleared the tray. That setter is what posts `TryReleasePending(TurnEnd)`, and the
        /// post runs after the move's synchronous work, by which time the carry has put the tray back.
        /// Without the hold it delivers messages written for the workspace the user LEFT into the agent of
        /// the one they arrived at, unasked — worse than the loss the carry fixes, and the reason the two
        /// had to land together. `ClearTranscript` also closes the window, one statement before it clears
        /// the tray, but by then the value is already false: `SetProperty` returns false and posts nothing.
        /// So reordering those two lines to exercise this aims at a setter that never fires.</para>
        /// <para><b>The tray needs a pending SEND to hold behind, not out-of-turn work.</b> Enter routes
        /// on `IsBusy` deliberately, not on `IsAgentWorking`, so with only the window open a message
        /// sends. The Choice banner supplies the pending send, which is also what makes the window and a
        /// non-empty tray coexist.</para>
        /// </remarks>
        [Fact]
        public void AMoveHoldsTheCarriedTrayAgainstTheReleaseTheClosingWindowPosts() => RunSta(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "cwkt-hold-gate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var moved = Path.Combine(dir, "moved-to");
            Directory.CreateDirectory(moved);
            try
            {
                var engine = new ScriptedEngine { Turns = ScriptedEngine.TurnEnding.WhenCompleted };
                engine.WithProvider("fake", "Fake");
                engine.WithProvider("other", "Other");
                var vm = new ChatViewModel(
                    engine,
                    new StartSessionRequest("fake", null, dir, "Prompt", null),
                    sessionStore: new FileSessionStore(dir));
                vm.InitializeAsync(Task.FromResult(new ListProvidersResponse(new List<ProviderInfoDto>
                {
                    new("fake", "Fake", new List<ModelInfoDto> { new("auto", "auto") }, new List<string> { "ResumeSession" }),
                    new("other", "Other", new List<ModelInfoDto> { new("auto", "auto") }, new List<string> { "ResumeSession" }),
                })));
                DrainDispatcher();

                vm.InputText = "first message";
                vm.SendCommand.Execute(null);
                DrainDispatcher();
                // Over ResumeDecider's 4000-character threshold, so the next send asks how to resume.
                engine.Raise(new AgentEventDto { Type = "text", Text = new string('x', 6000) });
                DrainDispatcher();
                engine.CompleteTurn();
                DrainDispatcher();

                // Before the window opens: a picker change is refused while the pane reads as working.
                vm.SelectedProvider = vm.Providers.First(p => p.Id == "other");
                DrainDispatcher();

                // A turn-less live event on a prompted pane opens the out-of-turn window with no turn.
                engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "bg1", Title = "Read" });
                DrainDispatcher();
                Assert.True(vm.IsAgentWorkingOutOfTurn, "no out-of-turn window, so this proves nothing");

                // The Choice banner, which is the pending send the follow-up holds behind.
                vm.InputText = "second message";
                vm.SendCommand.Execute(null);
                DrainDispatcher();
                Assert.Equal("Continue this conversation?", vm.PendingResume?.Heading);

                vm.InputText = Held;
                vm.SendCommand.Execute(null);
                DrainDispatcher();
                Assert.Equal(Held, Assert.Single(vm.PendingMessages).Text);
                var promptsBefore = engine.Prompts.Count;

                vm.UpdateWorkspaceRoot(moved, notice: null);
                for (var i = 0; i < 6; i++)
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

                // The carry kept it, the hold says so, and the release the closing window posted was
                // refused rather than delivered into the workspace the user arrived at.
                Assert.Equal(Held, Assert.Single(vm.PendingMessages).Text);
                Assert.Contains("workspace", vm.PendingStatus, StringComparison.Ordinal);
                Assert.Equal(promptsBefore, engine.Prompts.Count);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
            }
        });

        /// <summary>Stop's own sentence, for the other half of the pair above.</summary>
        [Fact]
        public void StopSaysSoInTheTray() => RunSta(() =>
        {
            var (vm, engine) = HeldUnderSteer();

            vm.StopCommand.Execute(null);
            engine.CompleteTurn();
            DrainDispatcher();

            Assert.Contains("you stopped", vm.PendingStatus, StringComparison.Ordinal);
        });

        // A turn with a call open, the tray on Steer, and one message held in it.
        /// <summary>
        /// A workspace switch over a tray the user had already STOPPED says the workspace changed, not
        /// that they stopped — the latest reason, which is the decision this pins.
        /// </summary>
        /// <remarks>
        /// <para><b>Why a decision needs a check and the code cannot carry it alone.</b> Both sentences
        /// hold the tray identically, the gate reading any non-<c>None</c> value, so the only difference
        /// between the two answers is the wording — which means nothing about the hold's BEHAVIOUR breaks
        /// if it flips, and a flip would ship silently. The declined option was keeping "Not sent — you
        /// stopped.", on the argument that Stop is the user's own gesture while the move is something that
        /// happened to them.</para>
        /// <para><b>And it is currently true for a reason one line away from the decision.</b> The carry
        /// does not preserve a hold — <c>ClearTranscript</c> reaches <c>ClearHeldMessages</c>, which resets
        /// it, and only the messages come back — so the move always writes its reason into a tray whose
        /// hold has just been cleared. A carry that preserved the hold would invert this with nothing in
        /// the product failing, which is what this check is here to refuse.</para>
        /// <para><b>Ordering matters in the setup:</b> a message landing in the tray LIFTS a hold, so the
        /// message is held first and Stop comes second, which is also the order a user meets.</para>
        /// </remarks>
        [Fact]
        public void AMoveOverAStoppedTraySaysTheWorkspaceChangedAndNotThatYouStopped() => RunSta(() =>
        {
            var moved = Path.Combine(
                Path.GetTempPath(), "cwkt-latest-reason-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(moved);
            try
            {
                var (vm, engine) = HeldUnderSteer();

                vm.StopCommand.Execute(null);
                engine.CompleteTurn();
                DrainDispatcher();
                Assert.Contains("you stopped", vm.PendingStatus, StringComparison.Ordinal);

                vm.UpdateWorkspaceRoot(moved, notice: null);
                DrainDispatcher();

                Assert.Equal(Held, Assert.Single(vm.PendingMessages).Text);
                Assert.Contains("workspace", vm.PendingStatus, StringComparison.Ordinal);
                Assert.DoesNotContain("stopped", vm.PendingStatus, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                try { Directory.Delete(moved, recursive: true); } catch { /* best-effort */ }
            }
        });

        private static (ChatViewModel Vm, ScriptedEngine Engine) HeldUnderSteer(bool cancelCompletesTurn = true)
        {
            var engine = HeldTurnEngine(supportsSteering: true, cancelCompletesTurn);
            var vm = StartedSession(engine);
            vm.PendingReleaseMode = PendingRelease.NextStep;

            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "t1", Title = "run_tests" });
            DrainDispatcher();

            vm.InputText = Held;
            vm.SendCommand.Execute(null);
            DrainDispatcher();
            Assert.Single(vm.PendingMessages);
            return (vm, engine);
        }

        private static ChatViewModel StartedSession(ScriptedEngine engine)
        {
            var vm = new ChatViewModel(
                engine,
                new StartSessionRequest("fake", null, AppContext.BaseDirectory, "Prompt", null));

            vm.InputText = "start working";
            vm.SendCommand.Execute(null);
            DrainDispatcher();
            Assert.True(vm.IsBusy);
            engine.Prompts.Clear();
            return vm;
        }

        private static ScriptedEngine HeldTurnEngine(bool supportsSteering, bool cancelCompletesTurn = true) => new()
        {
            Turns = ScriptedEngine.TurnEnding.WhenCompleted,
            CancelCompletesTurn = cancelCompletesTurn,
            SupportsSteering = supportsSteering,
        };

        private static void DrainDispatcher() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        private static void RunSta(Action action) => StaTest.Run(action, withDispatcherContext: true);
    }
}
