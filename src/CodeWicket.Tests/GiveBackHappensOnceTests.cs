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
    /// A send's message goes back to the composer AT MOST ONCE, however many routes reach it.
    /// <para>
    /// Two of them can reach the same send, and neither is hypothetical. Both were found by review after
    /// the give-back was put in <c>EndPending</c>, on the argument that a route already reaching a
    /// conversation change "needed no code of its own" - which is true everywhere the ending also moves
    /// the epoch, and false on the two routes below.
    /// </para>
    /// <para>
    /// The damage is not cosmetic: the text is prepended twice and every chip is inserted into the
    /// composer a second time, so re-sending the message sends the words twice and the image twice, and
    /// nothing on screen explains where the duplicate came from.
    /// </para>
    /// </summary>
    public sealed class GiveBackHappensOnceTests : IDisposable
    {
        private const string Message = "what changed?";
        private const string ImageName = "snip.png";
        private const string ContextLabel = "Debug state - Recurse";

        private static readonly EngineExit Exited = new EngineExit(
            EngineExitDescription.FrameworkMissingFailure,
            new[] { "You must install or update .NET to run this application." });

        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "cwkt-give-back-once-" + Guid.NewGuid().ToString("N"));

        public GiveBackHappensOnceTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        /// <summary>
        /// The engine exit is the ONE in-app ending that leaves the transcript up, so it never replaces
        /// the conversation and the epoch does not move. Its report ends the pending send and gives the
        /// message back; the send's own call then faults with the same exit and reaches the turn runner's
        /// catch, where the epoch is still current and the send is still the one it began - so the message
        /// was handed back a second time.
        /// </summary>
        [Fact]
        public void AnExitThatBothREPORTSAndFAULTSGivesTheMessageBackOnce() => RunSta(() =>
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var engine = new ScriptedEngine { GateStart = gate };
            engine.WithProvider("fake", "Fake");
            var vm = NewViewModel(engine);

            Compose(vm);
            vm.SendCommand.Execute(null);
            Drain();
            Assert.True(vm.IsBusy, "the send has to be waiting on the session start for the exit to fault it");
            Assert.Empty(engine.Prompts);

            // Reported AND faulting, which is what a dead pipe does.
            engine.RaiseExit(Exited);
            PendingSendScenario.PumpUntil(() => !vm.IsBusy, TimeSpan.FromSeconds(5));

            AssertGivenBackExactlyOnce(vm);

            // And named ONCE. The second notice answers a gesture, so it belongs to a send the user made
            // knowing the engine had gone; this one was made before it, so the same card twice would be
            // two reports of one event the user did not cause. Pinned here because nothing else does: the
            // decision of 2026-09-15 is about a LATER send, and its own check asserts two.
            Assert.Single(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.Contains("engine is not running", StringComparison.Ordinal)
                    || n.Text.Contains("could not start", StringComparison.Ordinal));
        });

        /// <summary>
        /// The other route: the user backs out of the refused-reload banner, which marks the message to be
        /// given back when the turn runner ends the send - and that is one or two dispatcher posts later,
        /// the banner's answer completing its source asynchronously. A workspace move landing in that
        /// window reaches the ending with the send still pending AND the back-out's mark still set, so
        /// both give-backs fire for one message.
        /// </summary>
        [Fact]
        public void ABackOutAndAMoveInTheSameWindowGiveTheMessageBackOnce() => RunSta(() =>
        {
            using var s = PendingSendScenario.Reach(PendingPhase.AskingRefused, withChips: true);
            var vm = s.Vm;

            // A held message, so the tray carries a status - which is what makes the window observable:
            // "took back" appears when the back-out runs, and the send stops being pending when the runner
            // ends it. Between those two facts is the window.
            vm.InputText = "and the tests?";
            vm.SendCommand.Execute(null);
            Drain();
            Assert.Single(vm.PendingMessages);

            var moved = s.MovedRoot;
            var acted = false;
            ActAfterEachOperation(
                when: () => !acted
                    && vm.HasPendingSendForDiagnostics
                    && vm.PendingStatus.Contains("took back", StringComparison.Ordinal),
                act: () =>
                {
                    acted = true;
                    vm.UpdateWorkspaceRoot(moved, notice: null);
                },
                body: () =>
                {
                    vm.PendingResume!.CancelCommand.Execute(null);
                    PendingSendScenario.DrainAll();
                });

            Assert.True(acted, "the window never opened, so this test measured nothing");
            AssertGivenBackExactlyOnce(vm);
        });

        // ---- helpers --------------------------------------------------------------------------

        /// <summary>
        /// Once, not twice - and the chips are what say so most plainly. A doubled give-back prepends the
        /// text to itself and inserts every attachment and context a second time.
        /// </summary>
        private static void AssertGivenBackExactlyOnce(ChatViewModel vm)
        {
            Assert.Equal(Message, vm.InputText);
            Assert.Equal(ImageName, Assert.Single(vm.PendingAttachments).Name);
            Assert.Equal(ContextLabel, Assert.Single(vm.PendingContexts).Label);
            Assert.DoesNotContain(vm.Items.OfType<MessageItemViewModel>(), m => m.Text == Message);
        }

        private static void Compose(ChatViewModel vm)
        {
            vm.PendingAttachments.Add(new AttachmentViewModel(
                ImageName, "image/png", new byte[] { 1, 2, 3 }, filePath: null, remove: _ => { }));
            vm.PendingContexts.Add(new ContextItemViewModel(
                "debug-state", ContextLabel, "#1 Recurse  Foo.cs:33"));
            vm.InputText = Message;
        }

        // Runs `body`, and after every dispatcher operation it causes, runs `act` once `when` holds.
        private static void ActAfterEachOperation(Func<bool> when, Action act, Action body)
        {
            var hooks = Dispatcher.CurrentDispatcher.Hooks;
            DispatcherHookEventHandler handler = (_, _) =>
            {
                if (when())
                    act();
            };
            hooks.OperationCompleted += handler;
            try
            {
                body();
            }
            finally
            {
                hooks.OperationCompleted -= handler;
            }
        }

        private ChatViewModel NewViewModel(ScriptedEngine engine)
        {
            var vm = new ChatViewModel(
                engine,
                new StartSessionRequest("fake", null, _root, "Prompt", null),
                sessionStore: new FileSessionStore(_root));
            vm.InitializeAsync().GetAwaiter().GetResult();
            Drain();
            return vm;
        }

        private static void Drain() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        private static void RunSta(Action action) => StaTest.Run(action, withDispatcherContext: true);
    }
}
