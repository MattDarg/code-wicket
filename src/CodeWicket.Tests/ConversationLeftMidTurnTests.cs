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
    /// A conversation left while one of its tool calls was in flight came back with no row for it
    /// at all.
    /// <para>
    /// Three rules combined, each right on its own. <c>Checkpoint</c> saves on <c>toolDone</c>,
    /// <c>turnDone</c>, <c>error</c>, <c>edit</c> and <c>plan</c> but not on <c>toolStart</c>, so a call
    /// in flight exists only in memory; the move drops the conversation without a save; and the call's
    /// own terminal update arrives after the retirement and is discarded before it can be recorded,
    /// which is issue #217 working as intended. So the row was not "still running" in the saved log - it
    /// was absent, and the agent's report of the work had nothing under it.
    /// </para>
    /// <para>
    /// The fix records the END rather than sweeping the rows: a host-side <c>turnDone</c> with
    /// <c>stopReason: "cancelled"</c>, through the ordinary recording path, whose checkpoint saves it.
    /// The log holds events and not rows, so a sweep of the view-model's rows would persist nothing -
    /// and replay then settles the row through the path a Stop's turn end already takes, rather than a
    /// second rendering of the same idea.
    /// </para>
    /// </summary>
    public sealed class ConversationLeftMidTurnTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "cwkt-left-mid-turn-" + Guid.NewGuid().ToString("N"));

        public ConversationLeftMidTurnTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private string MovedRoot
        {
            get
            {
                var moved = Path.Combine(_root, "moved");
                Directory.CreateDirectory(moved);
                return moved;
            }
        }

        /// <summary>
        /// The durable half: the conversation the move leaves has the call AND its end on disk. Both, and
        /// in that order - the end is what carries the state, and it is also what saves the call.
        /// </summary>
        [Fact]
        public void AMoveRecordsTheEndOfTheTurnTheConversationIsLeftWith() => RunSta(() =>
        {
            var store = new FileSessionStore(_root);
            var engine = HeldTurnEngine();
            var vm = NewViewModel(engine, store);
            var id = PromptWithACallInFlight(vm, engine);

            vm.UpdateWorkspaceRoot(MovedRoot, notice: null);
            DrainAll();

            var log = store.Load(_root, id)!.Log;
            var call = log.FindIndex(e => e.Event is { Type: "toolStart", ToolCallId: "t1" });
            var end = log.FindIndex(e => e.Event is { Type: "turnDone" });
            Assert.True(call >= 0, "the call in flight was not saved");
            Assert.True(end > call, "the turn's end was not recorded after the call it settles");
            Assert.Equal("cancelled", log[end].Event!.StopReason);
        });

        /// <summary>
        /// The reported half, as the user meets it: reopen the conversation and the row is there, saying
        /// the turn was cancelled before the call reported - the wording a Stop's own turn end produces,
        /// because it is the same code.
        /// </summary>
        [Fact]
        public void TheCallInFlightComesBackAsAnInterruptedRow() => RunSta(() =>
        {
            var store = new FileSessionStore(_root);
            var engine = HeldTurnEngine();
            var vm = NewViewModel(engine, store);
            var id = PromptWithACallInFlight(vm, engine);

            vm.UpdateWorkspaceRoot(MovedRoot, notice: null);
            DrainAll();

            // Reopened the way the report reached it: the solution comes back, and the conversation is
            // read out of its saved file rather than out of the pane that wrote it.
            var reopened = NewViewModel(HeldTurnEngine(), store);
            reopened.InitializeAsync().GetAwaiter().GetResult();
            reopened.RestoreMostRecentSession();
            DrainAll();
            reopened.RefreshHistory();
            Assert.Equal(id, reopened.History.Single(h => h.IsCurrent).Id);

            var row = Assert.Single(reopened.Items.OfType<ToolItemViewModel>());
            Assert.Equal("run_tests", row.Title);
            Assert.NotEqual(ToolStatus.Running, row.Status);
            Assert.Contains("cancelled", row.ErrorDetail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        });

        /// <summary>
        /// And only where the turn has not already said so. The lease is returned by the turn runner one
        /// dispatcher hop after the backend's own <c>turnDone</c> has been applied and recorded, so a move
        /// landing in that hop would otherwise write a second, contradicting end onto a turn that finished
        /// normally.
        /// </summary>
        [Fact]
        public void AMoveAfterTheTurnHasAlreadyEndedRecordsNoSecondEnd() => RunSta(() =>
        {
            var store = new FileSessionStore(_root);
            var engine = HeldTurnEngine();
            var vm = NewViewModel(engine, store);
            var id = PromptWithACallInFlight(vm, engine);

            // The backend's own end, applied and recorded while the turn runner has yet to resume.
            engine.Raise(new AgentEventDto { Type = "toolDone", ToolCallId = "t1" });
            engine.Raise(new AgentEventDto { Type = "turnDone", StopReason = "end_turn" });
            Drain();

            vm.UpdateWorkspaceRoot(MovedRoot, notice: null);
            DrainAll();

            var ends = store.Load(_root, id)!.Log.Where(e => e.Event is { Type: "turnDone" }).ToList();
            Assert.Equal("end_turn", Assert.Single(ends).Event!.StopReason);
        });

        // ---- helpers --------------------------------------------------------------------------

        /// <summary>
        /// Prompts, leaves one tool call running, and answers the conversation's local id. The turn is
        /// deliberately still open: that is the whole shape.
        /// </summary>
        private string PromptWithACallInFlight(ChatViewModel vm, ScriptedEngine engine)
        {
            vm.InputText = "run the tests";
            vm.SendCommand.Execute(null);
            Drain();
            engine.Raise(new AgentEventDto
            {
                Type = "toolStart", ToolCallId = "t1", Title = "run_tests", Kind = "other",
            });
            Drain();

            Assert.Equal(ToolStatus.Running, Assert.Single(vm.Items.OfType<ToolItemViewModel>()).Status);
            vm.RefreshHistory();
            return vm.History.Single(h => h.IsCurrent).Id;
        }

        private ChatViewModel NewViewModel(ScriptedEngine engine, FileSessionStore store) => new(
            engine,
            new StartSessionRequest("fake", null, _root, "Prompt", null),
            sessionStore: store);

        private static ScriptedEngine HeldTurnEngine() => new()
        {
            Turns = ScriptedEngine.TurnEnding.WhenCompleted,
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
