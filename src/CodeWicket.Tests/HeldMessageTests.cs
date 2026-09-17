using System;
using System.Collections.Generic;
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
    /// Messages typed while the agent is working (issue #70): held in the tray above the composer and
    /// released at a point that doesn't destroy work in progress.
    /// <para>
    /// The reason any of this exists is measured, not stylistic. A steer is a hard interrupt on whatever
    /// the agent is doing — one sent during a <c>run_tests</c> call returned <c>AbortError: interrupt</c>
    /// to the agent and the run was lost, with nothing surfacing that to the user. So what these tests
    /// pin is mostly about restraint: that a message does NOT go out while a tool call is running, and
    /// that the several paths which end a turn can't smuggle it out early.
    /// </para>
    /// </summary>
    public class HeldMessageTests
    {
        /// <summary>
        /// The core of the feature. Mid-turn Enter used to fire a steer immediately; now it parks the
        /// message while the agent is inside a tool call, which is the case the interrupt would cost
        /// something.
        /// </summary>
        [Fact]
        public void AMessageTypedDuringAToolCallIsHeldRatherThanSteered() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsSteering = true };
            var vm = StartedSession(engine);

            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "t1", Title = "run_tests" });
            DrainDispatcher();

            vm.InputText = "actually, skip the integration tests";
            vm.SteerCommand.Execute(null);
            DrainDispatcher();

            Assert.Empty(engine.Steers);
            Assert.Single(vm.PendingMessages);
            Assert.True(vm.HasPendingMessages);
            Assert.Equal("actually, skip the integration tests", vm.PendingMessages[0].Text);
            // The wait names what it's waiting for; "waiting" with no object reads as a hang.
            Assert.Contains("run_tests", vm.PendingStatus);
        });

        /// <summary>
        /// The release point itself: the moment the last open tool call closes. Exact on the wire
        /// (<c>tool_call</c> opens, <c>tool_call_update{completed}</c> closes) rather than a timer.
        /// </summary>
        [Fact]
        public void ItGoesOutWhenTheToolCallCompletes() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsSteering = true };
            var vm = StartedSession(engine);

            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "t1", Title = "run_tests" });
            DrainDispatcher();
            vm.InputText = "skip the integration tests";
            vm.SteerCommand.Execute(null);
            DrainDispatcher();
            Assert.Empty(engine.Steers);

            engine.Raise(new AgentEventDto { Type = "toolDone", ToolCallId = "t1", Success = true });
            DrainDispatcher();

            Assert.Single(engine.Steers);
            Assert.Contains("skip the integration tests", engine.Steers[0], StringComparison.Ordinal);
            Assert.Empty(vm.PendingMessages);
        });

        /// <summary>
        /// Claude runs up to two tool calls at once in the saved logs, so one of them finishing is not a
        /// safe point — the other is still running and is exactly what a steer would abort. This is the
        /// check that fails if the release is driven off "a tool finished" rather than "none are open".
        /// </summary>
        [Fact]
        public void OneOfTwoToolCallsFinishingIsNotASafePoint() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsSteering = true };
            var vm = StartedSession(engine);

            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "t1", Title = "run_tests" });
            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "t2", Title = "build_solution" });
            DrainDispatcher();
            vm.InputText = "hold on";
            vm.SteerCommand.Execute(null);
            DrainDispatcher();

            engine.Raise(new AgentEventDto { Type = "toolDone", ToolCallId = "t1", Success = true });
            DrainDispatcher();

            Assert.Empty(engine.Steers);
            Assert.Single(vm.PendingMessages);

            engine.Raise(new AgentEventDto { Type = "toolDone", ToolCallId = "t2", Success = true });
            DrainDispatcher();

            Assert.Single(engine.Steers);
            Assert.Contains("hold on", engine.Steers[0], StringComparison.Ordinal);
        });

        /// <summary>
        /// Plain Enter queues for the turn's END, even with nothing running. The old default was the
        /// next step, which meant a message meant for "when you're done" could go out seconds later at
        /// the first boundary — sending sooner than the user intended, which is the one thing the tray
        /// exists to prevent. Ctrl+Enter is how you ask for sooner.
        /// </summary>
        [Fact]
        public void PlainEnterWaitsForTheTurnEvenWithNothingRunning() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsSteering = true };
            var vm = StartedSession(engine);

            engine.Raise(new AgentEventDto { Type = "text", Text = "counting: 1, 2, 3" });
            DrainDispatcher();

            vm.InputText = "stop counting";
            vm.SendCommand.Execute(null);
            DrainDispatcher();

            Assert.Empty(engine.Steers);
            Assert.Single(vm.PendingMessages);
            Assert.Equal(PendingRelease.TurnEnd, vm.PendingReleaseMode);

            engine.CompleteTurn();
            DrainDispatcher();

            Assert.Contains(engine.Prompts, p => p.Contains("stop counting", StringComparison.Ordinal));
        });

        /// <summary>
        /// Ctrl+Enter promotes the tray to Steer. With nothing running the next safe point is now, so
        /// the message goes straight out — but it is a promotion, not an interrupt: see the tool-call
        /// case below, where it still waits.
        /// </summary>
        [Fact]
        public void CtrlEnterWithNothingRunningGoesStraightOut() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsSteering = true };
            var vm = StartedSession(engine);

            engine.Raise(new AgentEventDto { Type = "text", Text = "counting: 1, 2, 3" });
            DrainDispatcher();

            vm.InputText = "stop counting";
            vm.SteerCommand.Execute(null);
            DrainDispatcher();

            Assert.Single(engine.Steers);
            Assert.Contains("stop counting", engine.Steers[0], StringComparison.Ordinal);
            Assert.Empty(vm.PendingMessages);
        });

        /// <summary>
        /// Kiro exposes neither steer nor queue over ACP, so the delivery is an ordinary prompt rather
        /// than a steer. Mid-turn Enter used to be dead for its entire turn (the send command was gated
        /// on the backend supporting steering). The tray defaults to "next step" here exactly as it does
        /// on a steering backend — what differs is only how that release is reached.
        /// </summary>
        [Fact]
        public void WithoutSteeringItStillHoldsAndGoesAsAPrompt() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsSteering = false };
            var vm = StartedSession(engine);

            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "t1", Title = "build" });
            DrainDispatcher();

            vm.InputText = "and then run the tests";
            vm.SendCommand.Execute(null);
            DrainDispatcher();

            Assert.Single(vm.PendingMessages);
            Assert.Equal(PendingRelease.TurnEnd, vm.PendingReleaseMode);

            // The turn ends on its own, with the call still open: the turn-end trigger takes the whole
            // tray, and the delivery is an ordinary prompt rather than a steer.
            engine.CompleteTurn();
            DrainDispatcher();

            Assert.Empty(engine.Steers);
            Assert.Contains(engine.Prompts, p => p.Contains("and then run the tests", StringComparison.Ordinal));
            Assert.Empty(vm.PendingMessages);

            // A turn ending on its own is not an interruption, so there is no gesture to transmit and
            // nothing to explain: the message goes as the user wrote it, with no framing block. The
            // framing is bounded by what the user's own gesture said, and here it said nothing.
            Assert.DoesNotContain(
                engine.Prompts, p => p.Contains("<mid-turn-message>", StringComparison.Ordinal));
        });

        /// <summary>
        /// The parity change: "next step" is honoured on a backend that cannot steer, by ending the turn
        /// at the boundary and delivering there.
        /// <para>
        /// Measured equivalence is what licenses this (`Console claude-steer-boundary`): a steer at the
        /// next step destroys the call it lands on and drops the rest of the turn anyway, so waiting for
        /// a tool boundary and then cancelling produces the same outcome for the user. Before this, a
        /// Kiro user's held message waited out the whole turn while a Claude user's went at the next
        /// boundary — a difference in behaviour with nothing behind it but the mechanism.
        /// </para>
        /// </summary>
        /// <summary>
        /// Ctrl+Enter delivers the message it was typed with. It is the whole gesture — "send these
        /// sooner" is about the one in the box as much as the ones already queued — and it was the one
        /// message that did not go.
        /// </summary>
        /// <remarks>
        /// The promotion was applied BEFORE the message was held, so that <c>HoldMessage</c> would see
        /// the new mode. But the <c>PendingReleaseMode</c> setter releases too — it has to, so the
        /// toggle works when the user flips it with a tray already waiting — so on a boundary that has
        /// already passed it fired the tray WITHOUT this message. <c>DeliverPendingAsync</c> then sets
        /// <c>_deliveringPending</c> synchronously, so the <c>HoldMessage</c> that followed queued
        /// behind the user's own gesture and waited for the next boundary. They pressed Ctrl+Enter on a
        /// sentence and watched the previous ones overtake it.
        /// </remarks>
        [Fact]
        public void SteeringSoonerDeliversTheMessageItWasTypedWith() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsSteering = true };
            var vm = StartedSession(engine);

            // The turn has begun its reply, so a boundary is reachable (issue #273): before the first
            // content frame the promotion below would hold both messages, which is a different check.
            engine.Raise(new AgentEventDto { Type = "text", Text = "on it" });
            DrainDispatcher();

            // Queued in the default mode, with nothing running — so "next step" is already now, which
            // is the state that makes the setter's own release fire.
            vm.InputText = "first";
            vm.SendCommand.Execute(null);
            DrainDispatcher();
            Assert.Single(vm.PendingMessages);
            Assert.Empty(engine.Steers);

            vm.InputText = "second";
            vm.SteerCommand.Execute(null);
            DrainDispatcher();

            // ONE delivery carrying both, and that is the assertion which separates the two orderings.
            // "Did it get out eventually" does not: against a stub whose SteerAsync completes
            // synchronously, the broken order releases the tray, finishes, and HoldMessage then releases
            // the new message on its own — so both arrive and the check passes over its own bug. What
            // differs is the SHAPE, which is also the documented rule: a promotion takes the whole tray
            // as one delivery, this message included.
            Assert.Empty(vm.PendingMessages);
            var delivered = Assert.Single(engine.Steers);
            Assert.Contains("first", delivered, StringComparison.Ordinal);
            Assert.Contains("second", delivered, StringComparison.Ordinal);
        });

        [Fact]
        public void WithoutSteeringNextStepEndsTheTurnAtTheBoundaryAndDelivers() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsSteering = false };
            var vm = StartedSession(engine);

            // A call is running, so the boundary has not arrived: the message must wait.
            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "t1", Title = "build" });
            DrainDispatcher();

            vm.InputText = "and then run the tests";
            vm.SteerCommand.Execute(null);
            DrainDispatcher();

            Assert.Single(vm.PendingMessages);
            Assert.Equal(PendingRelease.NextStep, vm.PendingReleaseMode);
            Assert.Equal(0, engine.Cancels);

            // The boundary arrives. With no steering the only way in is to end the turn.
            engine.Raise(new AgentEventDto { Type = "toolDone", ToolCallId = "t1", Success = true });
            DrainDispatcher();

            Assert.Equal(1, engine.Cancels);
            Assert.Empty(engine.Steers);

            // ...and the turn ending delivers it, as an ordinary prompt.
            engine.CompleteTurn();
            DrainDispatcher();

            Assert.Contains(engine.Prompts, p => p.Contains("and then run the tests", StringComparison.Ordinal));
            Assert.Empty(vm.PendingMessages);
        });

        /// <summary>
        /// An asynchronous sub-agent reports its call "completed" the moment it is LAUNCHED, and then
        /// works for minutes — so that frame is the start of a fan-out, not a gap between steps. Taken
        /// at face value it empties the open-call set and fires the "next step" release, delivering a
        /// held message into the middle of exactly the burst of work the user was waiting out. Same
        /// root cause as the green tick the row used to show (issue #125), fixed in the same place.
        /// </summary>
        [Fact]
        public void ABackgroundSubagentLaunchIsNotANextStepBoundary() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsSteering = false };
            var vm = StartedSession(engine);

            engine.Raise(new AgentEventDto
            {
                Type = "toolStart", ToolCallId = "task1", Title = "Task", IsSubagentLaunch = true,
            });
            DrainDispatcher();

            vm.InputText = "and check the tests too";
            vm.SteerCommand.Execute(null);
            DrainDispatcher();

            Assert.Equal(PendingRelease.NextStep, vm.PendingReleaseMode);

            // The launch receipt. The sub-agent has not done anything yet.
            engine.Raise(new AgentEventDto
            {
                Type = "toolDone", ToolCallId = "task1", Success = true, LaunchedInBackground = true,
            });
            DrainDispatcher();

            Assert.Single(vm.PendingMessages);   // still held
            Assert.Equal(0, engine.Cancels);     // and the turn was not cut short to deliver it
        });

        /// <summary>
        /// Stop ends the turn, and the turn ending is the release trigger — so without an explicit
        /// suppression, Stop would halt the work and then immediately send everything the user had
        /// waiting. That is the one outcome the button promises not to produce, and it is the reason
        /// Stop and "cancel so this message can go" are different calls internally.
        /// </summary>
        [Fact]
        public void StopDoesNotSendWhatIsHeld() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsSteering = false };
            var vm = StartedSession(engine);

            // Something must actually be RUNNING for a message to be held: with no call open the
            // next step is now, and the message is released immediately on every backend (a steer
            // where one exists, an ended turn where it does not). These tests are about what
            // happens to a message that IS waiting.
            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "t1", Title = "build" });
            DrainDispatcher();

            vm.InputText = "and then run the tests";
            vm.SendCommand.Execute(null);
            DrainDispatcher();

            vm.StopCommand.Execute(null);
            engine.CompleteTurn();
            DrainDispatcher();

            Assert.Empty(engine.Steers);
            Assert.DoesNotContain("and then run the tests", engine.Prompts);
            // Held, not destroyed: the user typed it, so the tray is the only honest place for it.
            Assert.Single(vm.PendingMessages);
            Assert.Contains("stopped", vm.PendingStatus, StringComparison.OrdinalIgnoreCase);
        });

        /// <summary>
        /// The suppression is a delay, not a permanent block: once the user sends something of their own
        /// the pause they asked for is over, and anything still held rides on that turn's end. Without
        /// the reset the tray would be stranded for the rest of the session.
        /// </summary>
        [Fact]
        public void AfterAStopTheNextSendCarriesWhatIsStillHeld() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsSteering = false };
            var vm = StartedSession(engine);

            // Something must actually be RUNNING for a message to be held: with no call open the
            // next step is now, and the message is released immediately on every backend (a steer
            // where one exists, an ended turn where it does not). These tests are about what
            // happens to a message that IS waiting.
            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "t1", Title = "build" });
            DrainDispatcher();

            vm.InputText = "and then run the tests";
            vm.SendCommand.Execute(null);
            DrainDispatcher();
            vm.StopCommand.Execute(null);
            engine.CompleteTurn();
            DrainDispatcher();
            Assert.Single(vm.PendingMessages);

            vm.InputText = "let's try something else";
            vm.SendCommand.Execute(null);
            DrainDispatcher();
            engine.CompleteTurn();
            DrainDispatcher();

            Assert.Contains(engine.Prompts, p => p.Contains("and then run the tests", StringComparison.Ordinal));
            Assert.Empty(vm.PendingMessages);
        });

        /// <summary>
        /// Everything released together goes as one delivery. They were all typed before the release
        /// point, so they are one thought — and delivering them separately would mean several
        /// interruptions in a row where the user made one.
        /// </summary>
        [Fact]
        public void EverythingHeldIsDeliveredAsOneMessage() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsSteering = true };
            var vm = StartedSession(engine);

            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "t1", Title = "run_tests" });
            DrainDispatcher();
            vm.InputText = "skip the slow ones";
            vm.SteerCommand.Execute(null);
            vm.InputText = "and update the changelog";
            vm.SteerCommand.Execute(null);
            DrainDispatcher();
            Assert.Equal(2, vm.PendingMessages.Count);

            engine.Raise(new AgentEventDto { Type = "toolDone", ToolCallId = "t1", Success = true });
            DrainDispatcher();

            Assert.Single(engine.Steers);
            Assert.Contains("skip the slow ones\n\nand update the changelog", engine.Steers[0], StringComparison.Ordinal);
        });

        /// <summary>
        /// A turn ending takes the whole tray, not just the messages pointed at it. A "next step" message
        /// whose boundary never arrived — the turn had no further tool calls — must not be stranded by
        /// the turn finishing first.
        /// </summary>
        [Fact]
        public void ATurnEndingCollectsMessagesWaitingForANextStepThatNeverCame() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsSteering = true };
            var vm = StartedSession(engine);

            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "t1", Title = "run_tests" });
            DrainDispatcher();
            vm.InputText = "one more thing";
            vm.SteerCommand.Execute(null);
            DrainDispatcher();
            Assert.Equal(PendingRelease.NextStep, vm.PendingReleaseMode);

            // The turn ends with the tool call still open — no boundary ever arrives for it.
            engine.CompleteTurn();
            DrainDispatcher();

            Assert.Contains(engine.Prompts, p => p.Contains("one more thing", StringComparison.Ordinal));
            Assert.Empty(vm.PendingMessages);
        });

        /// <summary>
        /// The tray has to work on the turn AFTER it released one (issue #253). A send does not return
        /// until its turn ends, so a re-entrancy guard held across the await was still up at exactly the
        /// moment the next turn's release fired — and the second message sat there while the pane, which
        /// only knew that nothing was running, told the user the turn had been stopped.
        /// <para>
        /// Deterministic, not a race: the release is posted from <c>SendCoreAsync</c>'s finally and the
        /// delivery's own continuation is queued behind it, so the release always ran first and always
        /// lost.
        /// </para>
        /// </summary>
        [Fact]
        public void AMessageQueuedOnATurnATrayReleaseStartedStillGoesAtItsEnd() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsSteering = true };
            var vm = StartedSession(engine);
            Assert.Equal(PendingRelease.TurnEnd, vm.PendingReleaseMode);

            // First turn: queue one, and let the turn end release it. That delivery starts turn two.
            vm.InputText = "message A";
            vm.SendCommand.Execute(null);
            DrainDispatcher();
            engine.CompleteTurn();
            DrainDispatcher();
            Assert.Contains(engine.Prompts, p => p.Contains("message A", StringComparison.Ordinal));
            Assert.True(vm.IsBusy);

            // Second turn: queue another, and let this one end on its own too.
            vm.InputText = "message B";
            vm.SendCommand.Execute(null);
            DrainDispatcher();
            Assert.Single(vm.PendingMessages);

            engine.CompleteTurn();
            DrainDispatcher();

            Assert.Contains(engine.Prompts, p => p.Contains("message B", StringComparison.Ordinal));
            Assert.Empty(vm.PendingMessages);
        });

        /// <summary>
        /// The same guard, on the other trigger. The next-step boundary is a different release point but
        /// the same gate, so a tray-started turn silently swallowed a Ctrl+Enter message too — which is
        /// what says the fault was the guard's SCOPE rather than anything about queueing.
        /// </summary>
        [Fact]
        public void ANextStepBoundaryStillReleasesOnATurnATrayReleaseStarted() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsSteering = true };
            var vm = StartedSession(engine);

            vm.InputText = "message A";
            vm.SendCommand.Execute(null);
            DrainDispatcher();
            engine.CompleteTurn();          // releases A, whose delivery starts the second turn
            DrainDispatcher();
            Assert.True(vm.IsBusy);

            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "t1", Title = "run_tests" });
            DrainDispatcher();
            vm.InputText = "message B";
            vm.SteerCommand.Execute(null);
            DrainDispatcher();
            Assert.Single(vm.PendingMessages);

            engine.Raise(new AgentEventDto { Type = "toolDone", ToolCallId = "t1" });
            DrainDispatcher();

            Assert.Contains(engine.Steers, s => s.Contains("message B", StringComparison.Ordinal));
            Assert.Empty(vm.PendingMessages);
        });

        /// <summary>Remove drops a held message without ever sending it.</summary>
        [Fact]
        public void RemovingAHeldMessageDiscardsIt() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsSteering = true };
            var vm = StartedSession(engine);

            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "t1", Title = "run_tests" });
            DrainDispatcher();
            vm.InputText = "never mind";
            vm.SendCommand.Execute(null);
            DrainDispatcher();

            vm.PendingMessages[0].RemoveCommand.Execute(null);
            engine.Raise(new AgentEventDto { Type = "toolDone", ToolCallId = "t1", Success = true });
            DrainDispatcher();

            Assert.Empty(engine.Steers);
            Assert.Empty(vm.PendingMessages);
            Assert.False(vm.HasPendingMessages);
        });

        /// <summary>
        /// Ctrl+Enter is a promotion, not an interrupt: with a call running it still waits for the
        /// boundary. Nothing reachable from the keyboard destroys work — measured, an interrupt kills
        /// the MCP call in flight and drops the rest of the turn, so it costs a deliberate click on the
        /// row instead.
        /// </summary>
        [Fact]
        public void CtrlEnterPromotesButStillWaitsForTheToolCall() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsSteering = true };
            var vm = StartedSession(engine);

            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "t1", Title = "run_tests" });
            DrainDispatcher();

            vm.InputText = "stop, that's the wrong branch";
            vm.SteerCommand.Execute(null);
            DrainDispatcher();

            Assert.Empty(engine.Steers);
            Assert.Single(vm.PendingMessages);
            Assert.Equal(PendingRelease.NextStep, vm.PendingReleaseMode);

            engine.Raise(new AgentEventDto { Type = "toolDone", ToolCallId = "t1", Success = true });
            DrainDispatcher();

            Assert.Single(engine.Steers);
            Assert.Contains("stop, that's the wrong branch", engine.Steers[0], StringComparison.Ordinal);
        });

        /// <summary>
        /// The release point belongs to the tray, so Ctrl+Enter upgrades what is ALREADY queued along
        /// with the new message. "I want these sooner" is rarely about only the one being typed.
        /// </summary>
        [Fact]
        public void CtrlEnterUpgradesMessagesAlreadyQueued() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsSteering = true };
            var vm = StartedSession(engine);

            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "t1", Title = "run_tests" });
            DrainDispatcher();

            vm.InputText = "FIRST";
            vm.SendCommand.Execute(null);          // plain Enter -> queued for the turn's end
            DrainDispatcher();
            Assert.Equal(PendingRelease.TurnEnd, vm.PendingReleaseMode);

            vm.InputText = "SECOND";
            vm.SteerCommand.Execute(null);          // Ctrl+Enter -> promotes the whole tray
            DrainDispatcher();
            Assert.Equal(PendingRelease.NextStep, vm.PendingReleaseMode);
            Assert.Equal(2, vm.PendingMessages.Count);

            engine.Raise(new AgentEventDto { Type = "toolDone", ToolCallId = "t1", Success = true });
            DrainDispatcher();

            // Both, in typed order, as one delivery — the batch rule is unchanged.
            Assert.Single(engine.Steers);
            Assert.Contains("FIRST\n\nSECOND", engine.Steers[0], StringComparison.Ordinal);
            Assert.Empty(vm.PendingMessages);
        });

        /// <summary>
        /// The row's red button is the only thing that interrupts, and it still does.
        /// </summary>
        [Fact]
        public void TheRowsSendNowButtonInterruptsARunningToolCall() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsSteering = true };
            var vm = StartedSession(engine);

            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "t1", Title = "run_tests" });
            DrainDispatcher();

            vm.InputText = "stop, that's the wrong branch";
            vm.SendCommand.Execute(null);
            DrainDispatcher();
            Assert.Empty(engine.Steers);

            vm.SendPendingNowCommand.Execute(null);
            DrainDispatcher();

            Assert.Single(engine.Steers);
            Assert.Contains("stop, that's the wrong branch", engine.Steers[0], StringComparison.Ordinal);
            Assert.Empty(vm.PendingMessages);
        });

        /// <summary>
        /// Send-now on a backend that cannot steer is a cancel plus a send, and that cancel must not
        /// trip the Stop suppression — otherwise the gesture would interrupt the agent and then decline
        /// to deliver the message that was the entire point of interrupting it.
        /// </summary>
        [Fact]
        public void SendNowWithoutSteeringCancelsAndStillDelivers() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsSteering = false };
            var vm = StartedSession(engine);

            vm.InputText = "stop, that's the wrong branch";
            vm.SendCommand.Execute(null);
            DrainDispatcher();
            vm.SendPendingNowCommand.Execute(null);
            DrainDispatcher();

            Assert.Equal(1, engine.Cancels);

            engine.CompleteTurn();
            DrainDispatcher();

            Assert.Contains(engine.Prompts, p => p.Contains("stop, that's the wrong branch", StringComparison.Ordinal));
            Assert.Empty(vm.PendingMessages);
        });

        /// <summary>
        /// Held messages belong to the conversation that was running when they were typed. A new session
        /// must not inherit them — they would be delivered into a backend that has never seen what they
        /// refer to.
        /// </summary>
        [Fact]
        public void ANewSessionDropsTheTray() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsSteering = false };
            var vm = StartedSession(engine);

            // Something must actually be RUNNING for a message to be held: with no call open the
            // next step is now, and the message is released immediately on every backend (a steer
            // where one exists, an ended turn where it does not). These tests are about what
            // happens to a message that IS waiting.
            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "t1", Title = "build" });
            DrainDispatcher();

            vm.InputText = "one more thing";
            vm.SendCommand.Execute(null);
            DrainDispatcher();

            // Stop rather than letting the turn end, because a turn ending delivers the tray — this is
            // the one route by which a held message can outlive its conversation and reach the New
            // Session button, which is disabled for as long as the agent is working.
            vm.StopCommand.Execute(null);
            DrainDispatcher();
            Assert.Single(vm.PendingMessages);

            vm.NewSessionCommand.Execute(null);
            DrainDispatcher();
            Assert.Empty(vm.PendingMessages);

            // And it must be gone rather than merely hidden: the next conversation's first turn ending
            // is a release trigger, and a surviving message would be delivered into a backend that has
            // never seen what it refers to.
            engine.Prompts.Clear();
            vm.InputText = "a completely different question";
            vm.SendCommand.Execute(null);
            DrainDispatcher();
            engine.CompleteTurn();
            DrainDispatcher();

            Assert.DoesNotContain("one more thing", engine.Prompts);
        });

        /// <summary>
        /// The inverted half of the same defect, and the one with no tray at all: a Kiro v3 write emits
        /// its diff in place of a start, so a turn made of nothing but edits left the open-call set
        /// empty and a message typed during it was delivered straight into the middle of a write —
        /// exactly what holding exists to prevent (issue #190).
        /// </summary>
        [Fact]
        public void AnEditWithNoToolRowStillHoldsTheTray() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsSteering = true };
            var vm = StartedSession(engine);

            // The opening frame of a v3 write: a diff, and no toolStart.
            engine.Raise(new AgentEventDto
            {
                Type = "edit", ToolCallId = "w1", Path = @"C:\repo\src\Program.cs",
                OldText = "old", NewText = "new",
            });
            DrainDispatcher();

            vm.InputText = "actually, leave that file alone";
            vm.SteerCommand.Execute(null);
            DrainDispatcher();

            Assert.Empty(engine.Steers);
            Assert.Single(vm.PendingMessages);
            // ...and the tray can name what it is waiting for, which is the file, not "the next step".
            Assert.Contains("Program.cs", vm.PendingStatus);

            engine.Raise(new AgentEventDto { Type = "toolDone", ToolCallId = "w1", Success = true });
            DrainDispatcher();

            Assert.Single(engine.Steers);
            Assert.Contains("leave that file alone", engine.Steers[0], StringComparison.Ordinal);
        });

        /// <summary>
        /// A background sub-agent's LAUNCH is deliberately not a boundary (see above) — but its RETURN
        /// is, and nothing used to say so (issue #190). The launch id stayed on the ledger for the rest
        /// of the turn, so every later tool call closed against a non-empty set and the tray held to
        /// the turn's end: "Steer" behaving as Queue, while the status line named a row the user could
        /// see had finished.
        /// </summary>
        [Fact]
        public void ABackgroundSubagentReturningIsANextStepBoundary() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsSteering = true };
            var vm = StartedSession(engine);

            engine.Raise(new AgentEventDto
            {
                Type = "toolStart", ToolCallId = "task1", Title = "Task", IsSubagentLaunch = true,
            });
            engine.Raise(new AgentEventDto
            {
                Type = "toolDone", ToolCallId = "task1", Success = true, LaunchedInBackground = true,
            });
            DrainDispatcher();

            vm.InputText = "and check the tests too";
            vm.SteerCommand.Execute(null);
            DrainDispatcher();
            Assert.Single(vm.PendingMessages);   // held: the sub-agent is working

            // An ordinary call comes and goes underneath it. Still held — the sub-agent has not
            // reported back, so this is not a gap between steps.
            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "t1", Title = "grep" });
            engine.Raise(new AgentEventDto { Type = "toolDone", ToolCallId = "t1", Success = true });
            DrainDispatcher();
            Assert.Single(vm.PendingMessages);
            Assert.Empty(engine.Steers);

            // The task reports back. Nothing is running now, so this is the release point.
            engine.Raise(new AgentEventDto { Type = "backgroundTaskReturned" });
            DrainDispatcher();

            Assert.Single(engine.Steers);
            Assert.Contains("check the tests too", engine.Steers[0], StringComparison.Ordinal);
            Assert.Empty(vm.PendingMessages);
        });

        /// <summary>
        /// The tray opens on the mode the host presets it to — the setting behind issue #190's second
        /// half. A preset and not a policy: the pill still moves it, and this pins that the rung the
        /// window opens on is the one that decides where a mid-turn message waits.
        /// </summary>
        [Fact]
        public void ThePresetReleaseModeDecidesWhereAMidTurnMessageWaits() => RunSta(() =>
        {
            Assert.Equal(PendingRelease.TurnEnd, PendingReleaseModes.Parse(null));
            Assert.Equal(PendingRelease.TurnEnd, PendingReleaseModes.Parse("Queue"));
            Assert.Equal(PendingRelease.NextStep, PendingReleaseModes.Parse("steer"));
            Assert.Equal("Steer", PendingReleaseModes.ToName(PendingRelease.NextStep));
            Assert.Equal("Queue", PendingReleaseModes.ToName(PendingRelease.TurnEnd));

            var engine = new StubEngine { SupportsSteering = true };
            var vm = StartedSession(engine);
            vm.PendingReleaseMode = PendingReleaseModes.Parse(PendingReleaseModes.Steer);

            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "t1", Title = "run_tests" });
            DrainDispatcher();
            // Plain Enter, which queues by default — but the tray is a MODE, so the preset decides.
            vm.InputText = "one more thing";
            vm.SendCommand.Execute(null);
            DrainDispatcher();
            Assert.Single(vm.PendingMessages);

            engine.Raise(new AgentEventDto { Type = "toolDone", ToolCallId = "t1", Success = true });
            DrainDispatcher();

            Assert.Single(engine.Steers);
            Assert.Contains("one more thing", engine.Steers[0], StringComparison.Ordinal);
        });

        // ---- helpers --------------------------------------------------------------------------

        /// <summary>
        /// A view-model with a live session and a turn in flight — the state every one of these tests
        /// starts from, since a message can only be held while the agent is working.
        /// </summary>
        /// <summary>
        /// A next step before the first step is not a safe point (issue #273). "No tool call running"
        /// is true before the turn has produced anything, and a steer released there killed the turn
        /// outright on claude-agent-acp 0.63.0 — <c>session/prompt</c> failed with the backend's own
        /// <c>last_content_type=n/a</c>, 2–4 ms after the steer was acknowledged, twice in one evening.
        /// The tray now waits for the turn's first content frame, and says so.
        /// </summary>
        [Fact]
        public void CtrlEnterBeforeTheTurnsFirstContentWaitsForIt() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsSteering = true };
            var vm = StartedSession(engine); // the prompt is on the wire; nothing has come back

            vm.InputText = "also check the docs";
            vm.SteerCommand.Execute(null);
            DrainDispatcher();

            Assert.Empty(engine.Steers);
            Assert.Equal(0, engine.Cancels);
            Assert.Single(vm.PendingMessages);
            // The wait is named. This is the window the pane cannot otherwise tell from streaming,
            // and the tray is where the user is deciding whether a steer is safe right now.
            Assert.Contains("begin its reply", vm.PendingStatus, StringComparison.Ordinal);

            engine.Raise(new AgentEventDto { Type = "text", Text = "Looking at the tests first." });
            DrainDispatcher();

            var delivered = Assert.Single(engine.Steers);
            Assert.Contains("also check the docs", delivered, StringComparison.Ordinal);
            Assert.Empty(vm.PendingMessages);
        });

        /// <summary>
        /// A thought is content too: <c>agent_thought_chunk</c> is one of the two frames the backend's
        /// <c>last_content_type</c> would name, and every steer that landed after content was fine.
        /// </summary>
        [Fact]
        public void AThoughtIsTheTurnsFirstContentToo() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsSteering = true };
            var vm = StartedSession(engine);

            vm.InputText = "also check the docs";
            vm.SteerCommand.Execute(null);
            DrainDispatcher();
            Assert.Empty(engine.Steers);
            Assert.Single(vm.PendingMessages);

            engine.Raise(new AgentEventDto { Type = "thinking", Text = "The user wants the docs checked as well." });
            DrainDispatcher();

            Assert.Single(engine.Steers);
            Assert.Empty(vm.PendingMessages);
        });

        /// <summary>
        /// The signal is per TURN, not per session. A turn a tray release started is as fresh as one
        /// the user started — the last turn's reply says nothing about whether this one has begun.
        /// </summary>
        [Fact]
        public void TheContentSignalResetsWithEachTurn() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsSteering = true };
            var vm = StartedSession(engine);

            engine.Raise(new AgentEventDto { Type = "text", Text = "Done with the first thing." });
            DrainDispatcher();
            vm.InputText = "now the second thing";
            vm.SendCommand.Execute(null); // queued for the turn's end
            DrainDispatcher();
            engine.CompleteTurn();        // releases it, and that delivery starts turn 2
            DrainDispatcher();
            Assert.Contains(engine.Prompts, p => p.Contains("now the second thing", StringComparison.Ordinal));
            Assert.True(vm.IsBusy);

            vm.InputText = "and mind the tests";
            vm.SteerCommand.Execute(null); // turn 2 has produced nothing yet
            DrainDispatcher();

            Assert.Empty(engine.Steers);
            Assert.Single(vm.PendingMessages);

            engine.Raise(new AgentEventDto { Type = "text", Text = "Starting the second thing." });
            DrainDispatcher();

            Assert.Single(engine.Steers);
            Assert.Empty(vm.PendingMessages);
        });

        /// <summary>
        /// The cut-in rung is not gated — the user asked for it twice — but its MECHANISM changes in
        /// the pre-content window. A steer there does not interrupt the turn, it kills it with a
        /// backend error; a cancel loses nothing, because nothing has been produced. So the interrupt
        /// takes the cancel route a non-steering backend always takes, and the message goes as an
        /// ordinary prompt at the turn's end. With content it still steers:
        /// <see cref="TheRowsSendNowButtonInterruptsARunningToolCall"/>.
        /// </summary>
        [Fact]
        public void CuttingInBeforeTheFirstContentCancelsRatherThanSteers() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsSteering = true };
            var vm = StartedSession(engine);

            vm.InputText = "stop, wrong branch";
            vm.SendNowCommand.Execute(null);
            DrainDispatcher();

            Assert.Empty(engine.Steers);
            Assert.Equal(1, engine.Cancels);

            engine.CompleteTurn();
            DrainDispatcher();

            Assert.Empty(engine.Steers);
            var prompt = Assert.Single(engine.Prompts);
            Assert.Contains("stop, wrong branch", prompt, StringComparison.Ordinal);
            Assert.Empty(vm.PendingMessages);
        });

        /// <summary>
        /// A turn that fails leaves nothing on the ledger. An error is the turn's terminal failure and
        /// no turnDone follows it, so the ids it stranded used to outlive the turn and hold the tray
        /// for the whole of the next one, waiting on a row the user could see was dead — issue #273's
        /// open question about a failed turn releasing "through a path that assumes a normal end".
        /// </summary>
        [Fact]
        public void AnErroredTurnLeavesNothingOnTheLedger() => RunSta(() =>
        {
            var engine = new StubEngine { SupportsSteering = true };
            var vm = StartedSession(engine);

            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "t1", Title = "run_tests" });
            engine.Raise(new AgentEventDto
            {
                Type = "error",
                Message = "Internal error: [ede_diagnostic] result_type=user last_content_type=n/a stop_reason=null",
            });
            engine.CompleteTurn(); // the prompt returns after its error, with no turnDone
            DrainDispatcher();
            Assert.False(vm.IsBusy);

            vm.InputText = "try again";
            vm.SendCommand.Execute(null);
            DrainDispatcher();
            engine.Raise(new AgentEventDto { Type = "text", Text = "Trying again." });
            DrainDispatcher();

            vm.InputText = "and skip the slow ones";
            vm.SteerCommand.Execute(null);
            DrainDispatcher();

            var delivered = Assert.Single(engine.Steers);
            Assert.Contains("and skip the slow ones", delivered, StringComparison.Ordinal);
            Assert.Empty(vm.PendingMessages);
        });

        private static ChatViewModel StartedSession(StubEngine engine)
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

        private static void DrainDispatcher() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        // One shared, GATED implementation - see StaTest. Two STA bodies from different test
        // classes used to run concurrently against process-global WPF and clipboard state.
        private static void RunSta(Action action) => StaTest.Run(action, withDispatcherContext: true);

        /// <summary>
        /// Holds a turn open until <see cref="CompleteTurn"/> is called, so a test can type into a live
        /// turn and choose when it ends — which is the trigger half of everything here.
        /// </summary>
        private sealed class StubEngine : IEngineConnection
        {
            private TaskCompletionSource<PromptResponse>? _turn;

            public event Action<AgentEventDto>? AgentEvent;

            public event Action<ProviderModelsDto>? ProviderModelsRefreshed { add { } remove { } }

            public bool SupportsSteering { get; set; }

            public List<string> Prompts { get; } = new();

            public List<string> Steers { get; } = new();

            public int Cancels { get; private set; }

            public void Raise(AgentEventDto ev) => AgentEvent?.Invoke(ev);

            /// <summary>Ends the turn currently in flight, as a backend answering session/prompt does.</summary>
            public void CompleteTurn()
            {
                var turn = _turn;
                _turn = null;
                turn?.TrySetResult(new PromptResponse("end_turn"));
            }

            // A fake with no handshake reports no session, which the panel renders as
            // "no agent session open yet" rather than as absent facts (issue #160).
            public Task<SessionInfoResponse?> SessionInfoAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<SessionInfoResponse?>(null);

            public Task<ListProvidersResponse> ListProvidersAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new ListProvidersResponse(new List<ProviderInfoDto>()));

            public Task<StartSessionResponse> StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new StartSessionResponse("c1", SupportsSteering: SupportsSteering));

            public Task<PromptResponse> PromptAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
            {
                Prompts.Add(text);
                _turn = new TaskCompletionSource<PromptResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                return _turn.Task;
            }

            public Task CancelAsync(CancellationToken cancellationToken = default)
            {
                Cancels++;
                CompleteTurn();
                return Task.CompletedTask;
            }

            public Task<SteerResponse> SteerAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
            {
                Steers.Add(text);
                // A steer pre-empts the turn it lands in, which closes with an ordinary end_turn ~10ms
                // later (measured on the wire). Modelled here because the turn-end release trigger has
                // to cope with a turn that ends without the work being finished.
                CompleteTurn();
                return Task.FromResult(new SteerResponse(nameof(Core.SteerOutcome.Injected)));
            }

            public Task SetModelAsync(string modelId, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<ListBackendSessionsResponse> ListBackendSessionsAsync(
                ListBackendSessionsRequest request, CancellationToken cancellationToken = default)
                => throw new System.NotSupportedException("This stub lists no backend sessions.");

            public Task<TakeImportedHistoryResponse> TakeImportedHistoryAsync(
                TakeImportedHistoryRequest request, CancellationToken cancellationToken = default)
                => throw new System.NotSupportedException("This stub imports no history.");

            public Task<SummarizeResponse> SummarizeAsync(SummarizeRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("These tests never summarize.");
        }
    }
}
