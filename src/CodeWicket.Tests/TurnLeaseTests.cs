using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CodeWicket.Ipc;
using CodeWicket.Shell.Sessions;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The turn runner's half of the lease: committed just before the
    /// prompt, returned on every way out of the turn, and never in a place where a throw can strand the pane.
    /// The lifetime's half is <see cref="SessionLifetimeTests"/>.
    /// </summary>
    public sealed class TurnLeaseTests
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "cwkt-turn-lease-" + Guid.NewGuid().ToString("N"));

        /// <summary>
        /// A throw while the turn's queued tail is applied still frees the pane and returns the lease, and the
        /// next send commits cleanly. The finally used to set IsBusy and return the lease AFTER that apply, so a
        /// throw there left the pane busy for good and the next commit facing a lease that never came back.
        /// </summary>
        [Fact]
        public void AThrowWhileApplyingTheTurnsTailStillFreesThePane() => RunSta(() =>
        {
            var engine = HeldTurnEngine();
            var vm = NewViewModel(engine);

            Send(vm, "first");
            Assert.True(vm.IsBusy);

            // Throws on the first row the tail adds, and notes whether the turn was still open then: that is
            // what shows the runner's finally applied it (issue #277's inline apply), not the posted drain.
            bool? busyWhenThrown = null;
            NotifyCollectionChangedEventHandler thrower = (_, e) =>
            {
                if (e.Action != NotifyCollectionChangedAction.Add || busyWhenThrown is not null)
                    return;
                busyWhenThrown = vm.IsBusy;
                throw new InvalidOperationException("injected: applying the turn's tail failed");
            };
            vm.Items.CollectionChanged += thrower;

            // The turn's return is posted first, and the frame queued behind it.
            engine.CompleteTurn();
            Thread.Sleep(200);
            Task.Run(() => engine.Raise(new AgentEventDto { Type = "text", Text = "boom" })).Wait();
            DrainAll();

            vm.Items.CollectionChanged -= thrower;
            Assert.True(busyWhenThrown, "the tail was not applied by the turn's finally");
            Assert.False(vm.IsBusy, "a throw in the turn's tail left the pane busy");
            Assert.False(vm.HasOutstandingTurnForDiagnostics, "a throw in the turn's tail kept the lease");

            Send(vm, "second");
            Assert.Equal(2, engine.Prompts.Count);
        });

        /// <summary>
        /// The lease is back before the pane reads idle: whatever reacts to IsBusy going false (a warm start, a
        /// send) must not find the finished turn's lease still outstanding.
        /// </summary>
        [Fact]
        public void TheLeaseIsReturnedBeforeThePaneReadsIdle() => RunSta(() =>
        {
            var engine = HeldTurnEngine();
            var vm = NewViewModel(engine);
            bool? outstandingWhenIdle = null;
            PropertyChangedEventHandler watch = (_, e) =>
            {
                if (e.PropertyName == nameof(ChatViewModel.IsBusy) && !vm.IsBusy)
                    outstandingWhenIdle = vm.HasOutstandingTurnForDiagnostics;
            };

            Send(vm, "first");
            vm.PropertyChanged += watch;
            engine.CompleteTurn();
            DrainAll();
            vm.PropertyChanged -= watch;

            Assert.False(outstandingWhenIdle ?? true, "the pane read idle with the turn's lease still outstanding");
        });

        /// <summary>
        /// A lease left outstanding is a missed return. The next commit logs it ONCE to the one log the pane has,
        /// voids it, reports one breach, and the prompt still goes. The line used to be written twice:
        /// once by the lifetime and again by the sink the view-model handed it, both into engine.log.
        /// </summary>
        [Fact]
        public void AStaleLeaseIsLoggedOnceVoidedAndTheSendProceeds() => RunSta(() =>
        {
            var engine = HeldTurnEngine();
            var lines = new List<string>();
            var vm = NewViewModel(engine, lines.Add);
            var recorder = new BreachRecorder();

            using (Invariants.OpenScope(recorder))
            {
                vm.LeaveATurnLeaseOutstandingForDiagnostics();
                Send(vm, "hello");
                engine.CompleteTurn();
                DrainAll();
            }

            Assert.Single(engine.Prompts);
            Assert.Single(lines, l => l.Contains("stale turn lease voided", StringComparison.Ordinal));
            Assert.Contains("stale turn lease voided", Assert.Single(recorder.Drain()), StringComparison.Ordinal);
            Assert.False(vm.IsBusy);
        });

        /// <summary>
        /// Nothing that can throw sits between the pane going busy and the turn's try: a commit that
        /// throws is a failed send, and the pane is free again afterwards.
        /// </summary>
        [Fact]
        public void AThrowWhileCommittingStillFreesThePane() => RunSta(() =>
        {
            var engine = HeldTurnEngine();
            var vm = NewViewModel(engine, line =>
            {
                if (line.Contains("stale turn lease voided", StringComparison.Ordinal))
                    throw new IOException("injected: engine.log could not be written");
            });

            var recorder = new BreachRecorder();
            using (Invariants.OpenScope(recorder))
            {
                vm.LeaveATurnLeaseOutstandingForDiagnostics();
                Send(vm, "hello");
                DrainAll();
            }
            recorder.Drain();

            Assert.False(vm.IsBusy, "a throw while committing left the pane busy");
        });

        // ---- helpers --------------------------------------------------------------------------

        private ChatViewModel NewViewModel(ScriptedEngine engine, Action<string>? log = null)
        {
            var vm = new ChatViewModel(
                engine,
                new StartSessionRequest("fake", null, _dir, "Prompt", null),
                diagnosticLog: log);
            vm.InitializeAsync().GetAwaiter().GetResult();
            Drain();
            return vm;
        }

        private static void Send(ChatViewModel vm, string text)
        {
            vm.InputText = text;
            vm.SendCommand.Execute(null);
            DrainAll();
        }

        private static ScriptedEngine HeldTurnEngine() =>
            new ScriptedEngine { Turns = ScriptedEngine.TurnEnding.WhenCompleted }.WithProvider("fake", "Fake");

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
