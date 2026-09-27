using System;
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
    /// A mid-turn message that never reached the agent unwinds through the SAME give-back every other
    /// unsent message uses.
    /// <para>
    /// It had a give-back of its own: the text alone, joined to the composer with a bare "\n", with the
    /// pasted image and the IDE capture simply dropped and the bubble left standing on the transcript -
    /// so the message was on screen and in the box at once, minus half of itself. A steer is the one
    /// send with no phases, which is why it was the one that grew a second copy of this rule.
    /// </para>
    /// <para>
    /// The log goes too. A steer is recorded BEFORE it is sent - its place among the running turn's
    /// frames is only right there - so a failure is the one case that has to take the entry back out,
    /// or the transcript and the saved log disagree from that moment and the message reappears on the
    /// next reload looking said.
    /// </para>
    /// </summary>
    public sealed class FailedSteerGiveBackTests : IDisposable
    {
        private const string Steered = "actually, check the other branch";
        private const string ImageName = "snip.png";
        private const string ContextLabel = "Debug state - Recurse";

        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "cwkt-failed-steer-" + Guid.NewGuid().ToString("N"));

        public FailedSteerGiveBackTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        /// <summary>
        /// The whole message comes back: text, image and capture, with the bubble off the transcript and
        /// the backend's own reason beside it.
        /// </summary>
        [Fact]
        public void AFailedSteerGivesBackTheWholeMessage() => RunSta(() =>
        {
            var engine = SteeringEngine();
            var vm = PromptedWithACallRunning(engine);
            engine.SteerFails = new InvalidOperationException("the pipe is closed");

            Steer(vm, engine);

            Assert.Empty(engine.Steers);
            Assert.Equal(Steered, vm.InputText);
            Assert.Equal(ImageName, Assert.Single(vm.PendingAttachments).Name);
            Assert.Equal(ContextLabel, Assert.Single(vm.PendingContexts).Label);
            Assert.DoesNotContain(vm.Items.OfType<MessageItemViewModel>(), m => m.Text == Steered);
            Assert.Contains(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.Contains("the pipe is closed", StringComparison.Ordinal));
        });

        /// <summary>
        /// And the saved log keeps nothing of it. The entry is written before the steer goes out, so the
        /// failure is what takes it back out again - the same conversation it was written into, matched
        /// by the entry rather than by position, since the running turn's own frames land around it.
        /// </summary>
        [Fact]
        public void AFailedSteerLeavesNothingInTheSavedLog() => RunSta(() =>
        {
            var store = new FileSessionStore(_root);
            var engine = SteeringEngine();
            var vm = PromptedWithACallRunning(engine, store);
            var id = Current(vm);
            engine.SteerFails = new InvalidOperationException("the pipe is closed");

            Steer(vm, engine);

            Assert.DoesNotContain(store.Load(_root, id)!.Log, e => e.Text == Steered);
        });

        /// <summary>
        /// A steer that LANDS is untouched by any of this: it is an ordinary part of the conversation,
        /// on the transcript and in the log, and the composer is empty behind it.
        /// </summary>
        [Fact]
        public void ASteerThatLandsStaysOnTheTranscriptAndInTheLog() => RunSta(() =>
        {
            var store = new FileSessionStore(_root);
            var engine = SteeringEngine();
            var vm = PromptedWithACallRunning(engine, store);
            var id = Current(vm);

            Steer(vm, engine);

            Assert.Single(engine.Steers);
            Assert.Equal(string.Empty, vm.InputText);
            Assert.Empty(vm.PendingAttachments);
            Assert.Contains(vm.Items.OfType<MessageItemViewModel>(), m => m.Text == Steered);
            Assert.Contains(store.Load(_root, id)!.Log, e => e.Text == Steered);
        });

        /// <summary>
        /// And the un-record never WRITES a conversation the pane has left, because a write recreates one
        /// that was deleted. <c>DeleteSession</c> has no busy gate, removes the file, and releases the live
        /// owner WITHOUT a flush precisely so that a later save cannot bring the file back (#256) - so a
        /// delete mid-turn, which can itself fail the steer in flight, would have had our own catch write
        /// the deleted conversation out again and put it back in the picker.
        /// <para>The removal from the in-memory log still happens; it is the SAVE that is refused, and only
        /// where the conversation it would write is no longer the one on screen.</para>
        /// </summary>
        [Fact]
        public void AFailedSteerNeverRewritesAConversationThePaneHasLeft() => RunSta(() =>
        {
            var store = new FileSessionStore(_root);
            var engine = SteeringEngine();
            var vm = PromptedWithACallRunning(engine, store);
            var id = Current(vm);
            Assert.NotNull(store.Load(_root, id));

            // Held out, so the message has been drawn and RECORDED into the conversation on screen while
            // the request is still in flight. That order is the whole case: the entry the catch removes
            // belongs to a conversation that is about to stop existing.
            engine.GateSteer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Steer(vm, engine);
            Assert.Contains(store.Load(_root, id)!.Log, e => e.Text == Steered);

            // Deleted while it is out - which is also what fails it, the session it was riding being gone.
            engine.SteerFails = new InvalidOperationException("the session was released");
            vm.RefreshHistory();
            vm.History.Single(h => h.Id == id).DeleteCommand.Execute(null);
            DrainAll();
            Assert.Null(store.Load(_root, id));

            engine.GateSteer.SetResult(true);
            PendingSendScenario.PumpUntil(
                () => vm.Items.OfType<NoticeItemViewModel>().Any(n => n.Text.Contains("mid-turn", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(5));

            Assert.Null(store.Load(_root, id));
            Assert.DoesNotContain(store.List(_root), s => s.Id == id);
        });

        // ---- helpers --------------------------------------------------------------------------

        /// <summary>
        /// Holds the message with its chips (Ctrl+Enter), then closes the call it was waiting for, which
        /// is the boundary that releases it into the running turn.
        /// </summary>
        private static void Steer(ChatViewModel vm, ScriptedEngine engine)
        {
            vm.PendingAttachments.Add(new AttachmentViewModel(
                ImageName, "image/png", new byte[] { 1, 2, 3 }, filePath: null, remove: _ => { }));
            vm.PendingContexts.Add(new ContextItemViewModel(
                "debug-state", ContextLabel, "#1 Recurse  Foo.cs:33"));
            vm.InputText = Steered;
            vm.SteerCommand.Execute(null);
            Drain();

            engine.Raise(new AgentEventDto { Type = "toolDone", ToolCallId = "t1", Success = true });
            DrainAll();
        }

        private ChatViewModel PromptedWithACallRunning(ScriptedEngine engine, FileSessionStore? store = null)
        {
            var vm = new ChatViewModel(
                engine,
                new StartSessionRequest("fake", null, _root, "Prompt", null),
                sessionStore: store ?? new FileSessionStore(_root));
            vm.InitializeAsync().GetAwaiter().GetResult();
            vm.InputText = "look at the build";
            vm.SendCommand.Execute(null);
            Drain();
            engine.Raise(new AgentEventDto
            {
                Type = "toolStart", ToolCallId = "t1", Title = "run_tests", Kind = "other",
            });
            Drain();
            Assert.True(vm.IsBusy, "the turn has to be running for a steer to have anywhere to go");
            return vm;
        }

        private static string Current(ChatViewModel vm)
        {
            vm.RefreshHistory();
            return vm.History.Single(h => h.IsCurrent).Id;
        }

        private static ScriptedEngine SteeringEngine() => new()
        {
            Turns = ScriptedEngine.TurnEnding.WhenCompleted,
            SupportsSteering = true,
        };

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
