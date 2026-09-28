using System;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using CodeWicket.Ipc;
using CodeWicket.Shell;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// When a flip of the tray's mode (the pill, or Alt+Q) ends. It is for the conversation it was made in:
    /// New and deleting the conversation on screen put the user's default back, and a history open does
    /// not, because a look does not disturb the pane's session — only a send, which resumes the old one,
    /// does.
    /// </summary>
    public sealed class TrayModeResetTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "cwkt-tray-mode-" + Guid.NewGuid().ToString("N"));

        public TrayModeResetTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        [Fact]
        public void NewPutsTheDefaultBack() => RunSta(() =>
        {
            var vm = Prompted(PendingRelease.TurnEnd);
            vm.TogglePendingReleaseCommand.Execute(null);
            Assert.Equal(PendingRelease.NextStep, vm.PendingReleaseMode);

            vm.NewSessionCommand.Execute(null);
            Drain();

            Assert.Equal(PendingRelease.TurnEnd, vm.PendingReleaseMode);
        });

        /// <summary>
        /// A changed setting reaches the NEXT conversation and never the one on screen, whose mode the user
        /// may have flipped for it.
        /// </summary>
        [Fact]
        public void AChangedDefaultWaitsForTheNextNew() => RunSta(() =>
        {
            var vm = Prompted(PendingRelease.TurnEnd);

            vm.SetPendingReleaseDefaultForNextConversation(PendingRelease.NextStep);
            Assert.Equal(PendingRelease.TurnEnd, vm.PendingReleaseMode);

            vm.NewSessionCommand.Execute(null);
            Drain();

            Assert.Equal(PendingRelease.NextStep, vm.PendingReleaseMode);
        });

        [Fact]
        public void DeletingTheConversationOnScreenPutsTheDefaultBack() => RunSta(() =>
        {
            var vm = Prompted(PendingRelease.TurnEnd);
            vm.TogglePendingReleaseCommand.Execute(null);

            vm.RefreshHistory();
            vm.History.Single(h => h.IsCurrent).DeleteCommand.Execute(null);
            Drain();

            Assert.Equal(PendingRelease.TurnEnd, vm.PendingReleaseMode);
        });

        [Fact]
        public void OpeningAConversationFromHistoryLeavesTheModeAlone() => RunSta(() =>
        {
            var vm = Prompted(PendingRelease.TurnEnd);
            vm.RefreshHistory();
            var first = vm.History.Single(h => h.IsCurrent).Id;

            vm.NewSessionCommand.Execute(null);
            Drain();
            Send(vm, "a second conversation");
            vm.TogglePendingReleaseCommand.Execute(null);

            vm.RefreshHistory();
            vm.History.Single(h => h.Id == first).LoadCommand.Execute(null);
            Drain();

            // The open happened — the first conversation is what is on screen — and the flip survived it.
            var said = vm.Items.OfType<MessageItemViewModel>().Where(m => m.Role == MessageRole.User).Select(m => m.Text).ToList();
            Assert.Contains("look at the build", said);
            Assert.DoesNotContain("a second conversation", said);
            Assert.Equal(PendingRelease.NextStep, vm.PendingReleaseMode);
        });

        /// <summary>
        /// A workspace switch replaces the conversation on the user's behalf. With nothing held it has put
        /// a different conversation in charge, so the flip goes back to the default.
        /// </summary>
        [Fact]
        public void AWorkspaceSwitchWithNothingHeldPutsTheDefaultBack() => RunSta(() =>
        {
            var vm = Prompted(PendingRelease.TurnEnd);
            vm.TogglePendingReleaseCommand.Execute(null);

            vm.UpdateWorkspaceRoot(Moved(), notice: null);
            Drain();

            Assert.Equal(PendingRelease.TurnEnd, vm.PendingReleaseMode);
        });

        /// <summary>
        /// ...and with messages held it carries them across in the mode they were meant to go in. The
        /// check that separates "reset when nothing was carried" from "reset always".
        /// </summary>
        [Fact]
        public void AWorkspaceSwitchCarryingHeldMessagesKeepsTheirMode() => RunSta(() =>
        {
            var engine = new ScriptedEngine { Turns = ScriptedEngine.TurnEnding.WhenCompleted, CancelCompletesTurn = true };
            var vm = new ChatViewModel(
                engine,
                new StartSessionRequest("fake", null, _root, "Prompt", null),
                sessionStore: new FileSessionStore(_root));
            vm.InitializeAsync().GetAwaiter().GetResult();
            vm.PresetPendingRelease(PendingRelease.TurnEnd);

            Send(vm, "run the tests");
            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "t1", Title = "run_tests" });
            Drain();
            vm.TogglePendingReleaseCommand.Execute(null); // Next step, with a call running: nothing released
            Send(vm, "then fix what fails");
            Assert.Single(vm.PendingMessages);

            vm.UpdateWorkspaceRoot(Moved(), notice: null);
            Drain();

            Assert.Single(vm.PendingMessages);
            Assert.Equal(PendingRelease.NextStep, vm.PendingReleaseMode);
        });

        // ---- helpers --------------------------------------------------------------------------

        private string Moved()
        {
            var moved = Path.Combine(_root, "moved-to");
            Directory.CreateDirectory(moved);
            return moved;
        }

        // A pane as the hosts leave it — the default supplied and applied — with one finished turn, so
        // there is a saved conversation on screen for the history routes to act on.
        private ChatViewModel Prompted(PendingRelease @default)
        {
            var vm = new ChatViewModel(
                new ScriptedEngine(),
                new StartSessionRequest("fake", null, _root, "Prompt", null),
                sessionStore: new FileSessionStore(_root));
            vm.InitializeAsync().GetAwaiter().GetResult();
            vm.PresetPendingRelease(@default);

            Send(vm, "look at the build");
            Assert.False(vm.IsBusy);
            return vm;
        }

        private static void Send(ChatViewModel vm, string text)
        {
            vm.InputText = text;
            vm.SendCommand.Execute(null);
            Drain();
        }

        private static void Drain()
        {
            for (var i = 0; i < 4; i++)
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        }

        private static void RunSta(Action action) => StaTest.Run(action, withDispatcherContext: true);
    }
}
