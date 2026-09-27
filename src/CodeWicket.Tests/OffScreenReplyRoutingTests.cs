using System;
using System.Collections.Generic;
using System.IO;
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
    /// A background task's reply landed in whatever conversation was open, and was then saved in none
    /// (issue #256).
    ///
    /// <para>Opening a saved conversation from the history picker is lazy and display-first: it replays
    /// locally and issues no <c>session/load</c>, so the live session is untouched by the swap. A
    /// background launch ends its turn at launch, so when the task returned there was no turn on the
    /// wire, no epoch to disagree, and the agent's <c>Read</c> row and "Done" reply rendered under a
    /// Kiro analysis with the header reading <c>Kiro</c>. Neither log had it afterwards: the pane on
    /// screen dropped the tool rows as session setup, and the conversation the reply belonged to ended
    /// mid-promise.</para>
    ///
    /// <para>The correlation the fix needs is one the host already holds: the engine runs one session at
    /// a time, and the host knows which conversation it started that session for. So the events go
    /// into THAT conversation's log, the transcript on screen is left alone, and reopening the owner
    /// re-attaches to the session the backend never dropped.</para>
    /// </summary>
    public sealed class OffScreenReplyRoutingTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "cwkt-tests", Guid.NewGuid().ToString("N"));

        public OffScreenReplyRoutingTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }

        /// <summary>
        /// The reported shape, step for step: a conversation with a live session, an older one opened
        /// to read, the live session's reply arriving. Nothing of it is drawn on the conversation being
        /// read, and the pane does not claim to be working — that bar is about the pane on screen.
        /// </summary>
        [Fact]
        public void AReplyArrivingWhileAnotherConversationIsOpenIsNotDrawnThere() => RunSta(() =>
        {
            var store = new FileSessionStore(_root);
            var engine = HeldTurnEngine();
            var vm = NewViewModel(engine, store);
            var (older, live) = TwoConversations(vm, engine, store);

            Open(vm, older);

            BackgroundReplyArrives(engine);

            Assert.Empty(vm.Items.OfType<ToolItemViewModel>());
            Assert.DoesNotContain(vm.Items.OfType<MessageItemViewModel>(), m => m.Text.Contains("Done"));
            Assert.False(vm.IsAgentWorking);
            vm.RefreshHistory();
            Assert.Equal(older, vm.History.Single(h => h.IsCurrent).Id);
            _ = live;
        });

        /// <summary>
        /// And it is saved where it belongs, without waiting for a turn boundary that is never coming:
        /// a background task's "Done" is text after a toolDone, and no turnDone follows it.
        /// </summary>
        [Fact]
        public void TheReplyIsRecordedToTheConversationItBelongsTo() => RunSta(() =>
        {
            var store = new FileSessionStore(_root);
            var engine = HeldTurnEngine();
            var vm = NewViewModel(engine, store);
            var (older, live) = TwoConversations(vm, engine, store);

            Open(vm, older);
            BackgroundReplyArrives(engine);
            // Reopening is what forces the flush, and is the moment the file has to be right.
            Open(vm, live);

            var saved = store.Load(_root, live)!;
            Assert.Contains(saved.Log, e => e.Event is { Type: "toolStart", Title: "Read" });
            Assert.Contains(saved.Log, e => e.Event is { Type: "text" } ev && ev.Text!.Contains("Done"));

            var other = store.Load(_root, older)!;
            Assert.DoesNotContain(other.Log, e => e.Event is { Type: "toolStart" or "toolDone" });
            Assert.DoesNotContain(other.Log, e => e.Event is { Type: "text" } ev && ev.Text!.Contains("Done"));
        });

        /// <summary>
        /// This lives beside its off-screen twin because the ASYMMETRY is the whole finding: the
        /// same reply, for a conversation that is ON screen, was drawn and never saved. `Checkpoint` writes
        /// on turn boundaries, and a background task's "Done" is text after a toolDone with no turnDone
        /// coming - so the saved file ended at that toolDone, and leaving the conversation dropped the
        /// reply. Off screen it was always saved, `RouteOffScreen` marking the owner dirty and arming the
        /// flush this now rides too.
        /// <para>Asserted with NO conversation change, which is the point of the answer chosen over flushing
        /// on the way out (user, 2026-09-20). Teardown DOES run when Visual Studio closes -
        /// <c>ChatToolWindow.Dispose</c>, whose own remarks note that VS caches tool-window panes so it waits
        /// for shutdown - but it SAVES nothing, the view-model not being <c>IDisposable</c>, and every step
        /// of it is best-effort with its own catch; and it does not run at all on a crash or a kill. A
        /// one-second timer is bounded by the second rather than by the exit.</para>
        /// </summary>
        [Fact]
        public void AnOnScreenOutOfTurnReplyIsSavedWithoutLeavingTheConversation() => RunSta(() =>
        {
            var store = new FileSessionStore(_root);
            var engine = HeldTurnEngine();
            var vm = NewViewModel(engine, store);
            Prompt(vm, engine, "run a background task that sleeps for 30 seconds");
            var live = vm.History.Single(h => h.IsCurrent).Id;

            BackgroundReplyArrives(engine); // on screen: routed nowhere, and no turn is running

            Assert.Contains(vm.Items.OfType<MessageItemViewModel>(),
                m => m.Text.Contains("Done", StringComparison.Ordinal));

            PendingSendScenario.PumpUntil(() => SavedReply(store, live), TimeSpan.FromSeconds(10));

            Assert.True(SavedReply(store, live), "the reply is in the saved log with nothing having left the conversation");
        });

        private bool SavedReply(FileSessionStore store, string id) =>
            store.Load(_root, id)!.Log.Any(
                e => e.Event is { Type: "text" } ev && ev.Text!.Contains("Done", StringComparison.Ordinal));

        /// <summary>
        /// Reopening the owner shows the reply AND re-attaches to the live session: the backend never
        /// dropped it, so the next prompt is an ordinary send rather than a resume of a conversation
        /// that was never unloaded. Without the re-attach the reopened pane thinks it is unstarted, and
        /// the reply's rows would draw as session setup and go unrecorded — the report's own wrong
        /// outcome, reached one gesture later.
        /// </summary>
        [Fact]
        public void ReopeningTheOwnerShowsTheReplyAndReattachesToTheLiveSession() => RunSta(() =>
        {
            var store = new FileSessionStore(_root);
            var engine = HeldTurnEngine();
            var vm = NewViewModel(engine, store);
            var (older, live) = TwoConversations(vm, engine, store);

            Open(vm, older);
            BackgroundReplyArrives(engine);
            var startsBefore = engine.StartCount;

            Open(vm, live);

            Assert.Equal("Read", Assert.Single(vm.Items.OfType<ToolItemViewModel>()).Title);
            Assert.Contains(vm.Items.OfType<MessageItemViewModel>(), m => m.Text.Contains("Done"));
            Assert.True(vm.IsPromptedForDiagnostics);

            // A late straggler from the same session now belongs to the pane on screen: drawn as work,
            // and recorded, since this conversation HAS been prompted.
            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "t2", Title = "Grep", Kind = "search" });
            engine.Raise(new AgentEventDto { Type = "toolDone", ToolCallId = "t2" });
            Drain();
            var late = vm.Items.OfType<ToolItemViewModel>().Single(t => t.Title == "Grep");
            Assert.False(late.IsSessionSetup);
            Assert.Contains(store.Load(_root, live)!.Log, e => e.Event is { Type: "toolStart", Title: "Grep" });

            // And the next prompt rides the session that is already open.
            vm.InputText = "thanks";
            vm.SendCommand.Execute(null);
            Drain();
            Assert.Equal(startsBefore, engine.StartCount);
            engine.CompleteTurn();
            Drain();
        });

        /// <summary>
        /// The instrument the report asked for. The out-of-turn window's own line is gated on the
        /// on-screen session having been prompted, so this episode wrote nothing to engine.log and a
        /// reader concluded nothing had happened. Wording asserted, because a line nobody can find is
        /// the same as no line.
        /// </summary>
        [Fact]
        public void AnOffScreenEpisodeLeavesItsLinesInEngineLog() => RunSta(() =>
        {
            var store = new FileSessionStore(_root);
            var engine = HeldTurnEngine();
            var lines = new List<string>();
            var vm = NewViewModel(engine, store, lines.Add);
            var (older, live) = TwoConversations(vm, engine, store);

            Open(vm, older);
            BackgroundReplyArrives(engine);

            var opened = Assert.Single(lines, l => l.StartsWith("[out-of-turn] off screen: 'toolStart'", StringComparison.Ordinal));
            Assert.Contains($"({live})", opened);
            Assert.Contains($"({older}) is on screen", opened);

            Open(vm, live);

            var closed = Assert.Single(lines, l => l.Contains("reopened after"));
            Assert.Contains($"({live}) reopened after 3 event(s)", closed);
        });

        /// <summary>
        /// A permission request from the off-screen session is still asked — the session is in the
        /// same solution, its grants stand, and the user launched the work — and the banner says whose
        /// it is, because its row is not on this transcript and never will be.
        /// </summary>
        [Fact]
        public void AnOffScreenPermissionRequestIsAskedAndSaysWhoseItIs() => RunSta(() =>
        {
            var store = new FileSessionStore(_root);
            var engine = HeldTurnEngine();
            var vm = NewViewModel(engine, store);
            var (older, live) = TwoConversations(vm, engine, store);
            var liveTitle = store.Load(_root, live)!.Title;

            Open(vm, older);

            var decision = vm.RequestPermissionAsync(new PermissionRequestDto(
                "t1", "Write Program.cs", "edit", null, null,
                new[] { new PermissionOptionDto("allow", "Allow", "allow_once") }));
            Drain();

            Assert.False(decision.IsCompleted);
            var banner = Assert.IsType<PermissionBannerViewModel>(vm.PendingPermission);
            Assert.True(banner.HasOrigin);
            Assert.Equal(PermissionBannerViewModel.OriginSentence(liveTitle), banner.Origin);
            Assert.Contains(liveTitle, banner.Origin);
            Assert.Contains("still running", banner.Origin);

            // An ordinary request, for contrast, carries none.
            banner.Options[0].Command.Execute(null);
            Drain();
            Assert.Equal("allow", decision.Result.OptionId);
        });

        /// <summary>
        /// Sending in the conversation being read starts a session for it, which is what actually ends
        /// the old one. From there the wire is this conversation's — nothing more may be recorded to
        /// the previous owner, or the new session's opening frames would land in it.
        /// </summary>
        [Fact]
        public void ANewSessionStartReleasesThePreviousOwner() => RunSta(() =>
        {
            var store = new FileSessionStore(_root);
            var engine = HeldTurnEngine();
            var vm = NewViewModel(engine, store);
            var (older, live) = TwoConversations(vm, engine, store);

            Open(vm, older);
            BackgroundReplyArrives(engine);
            var liveEntries = store.Load(_root, live)!.Log.Count;

            vm.InputText = "carry on";
            vm.SendCommand.Execute(null);
            Drain();
            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "n1", Title = "Read", Kind = "read" });
            engine.Raise(new AgentEventDto { Type = "toolDone", ToolCallId = "n1" });
            Drain();
            engine.CompleteTurn();
            Drain();

            Assert.Equal("Read", Assert.Single(vm.Items.OfType<ToolItemViewModel>()).Title);
            Assert.Contains(store.Load(_root, older)!.Log, e => e.Event is { Type: "toolStart", ToolCallId: "n1" });
            // The previous owner got the reply that was its own (flushed by the release) and nothing since.
            var previous = store.Load(_root, live)!.Log;
            Assert.True(previous.Count > liveEntries);
            Assert.DoesNotContain(previous, e => e.Event is { ToolCallId: "n1" });
        });

        /// <summary>
        /// Deleting the owner while its session is still live: nothing may recreate the file, and the
        /// session's events belong nowhere — not to the conversation on screen, which is the reported
        /// bug reached by one more gesture.
        /// </summary>
        /// <summary>
        /// The deleted owner's session runs on, and its permission request still reaches the banner
        /// (pre-release security review, September 2026). Before the tombstone kept a title the banner could name nobody, so
        /// the user was asked to approve work for a conversation they had deleted, presented as the
        /// one they were reading. Asked, still - the reasons for not cancelling hold - and named as
        /// deleted.
        /// </summary>
        [Fact]
        public void ADeletedOwnersRequestIsStillAskedAndNamedAsDeleted() => RunSta(() =>
        {
            var store = new FileSessionStore(_root);
            var engine = HeldTurnEngine();
            var vm = NewViewModel(engine, store);
            var (older, live) = TwoConversations(vm, engine, store);
            var liveTitle = store.Load(_root, live)!.Title;

            Open(vm, older);
            vm.History.Single(h => h.Id == live).DeleteCommand.Execute(null);
            Drain();

            var decision = vm.RequestPermissionAsync(new PermissionRequestDto(
                "t1", "Write File", "edit", null, null,
                new[] { new PermissionOptionDto("allow", "Allow", "allow_once") },
                Path: Path.Combine(_root, "src", "Program.cs")));
            Drain();

            Assert.False(decision.IsCompleted);
            var banner = Assert.IsType<PermissionBannerViewModel>(vm.PendingPermission);
            Assert.True(banner.HasOrigin);
            Assert.Equal(PermissionBannerViewModel.DeletedOriginSentence(liveTitle), banner.Origin);
            Assert.Contains("you deleted", banner.Origin);
            // And with no row anywhere, the file is still named.
            Assert.True(banner.HasTargetPath);
            Assert.Contains("Program.cs", banner.TargetPath);

            banner.Options[0].Command.Execute(null);
            Drain();
            Assert.Equal("allow", decision.Result.OptionId);
        });

        [Fact]
        public void DeletingTheOwnerNeitherResurrectsItNorRedirectsItsEvents() => RunSta(() =>
        {
            var store = new FileSessionStore(_root);
            var engine = HeldTurnEngine();
            var vm = NewViewModel(engine, store);
            var (older, live) = TwoConversations(vm, engine, store);

            Open(vm, older);
            vm.History.Single(h => h.Id == live).DeleteCommand.Execute(null);
            Drain();

            BackgroundReplyArrives(engine);
            Open(vm, older); // forces every pending flush there could be

            Assert.DoesNotContain(store.List(_root), s => s.Id == live);
            Assert.Empty(vm.Items.OfType<ToolItemViewModel>());
            var other = store.Load(_root, older)!;
            Assert.DoesNotContain(other.Log, e => e.Event is { Type: "text" } ev && ev.Text!.Contains("Done"));
        });

        // ---- helpers --------------------------------------------------------------------------

        /// <summary>
        /// Two saved conversations under one root, the SECOND of which holds the live session: the
        /// first is prompted and left, the second prompted last. Returns (older, live) ids.
        /// </summary>
        private (string older, string live) TwoConversations(ChatViewModel vm, ScriptedEngine engine, FileSessionStore store)
        {
            Prompt(vm, engine, "analyse the solution's projects");
            var older = vm.History.Single(h => h.IsCurrent).Id;

            vm.NewSessionCommand.Execute(null);
            Drain();
            Prompt(vm, engine, "run a background task that sleeps for 30 seconds");
            var live = vm.History.Single(h => h.IsCurrent).Id;

            Assert.NotEqual(older, live);
            Assert.Equal(2, store.List(_root).Count);
            return (older, live);
        }

        /// <summary>What the live session emits when the task returns: the Read of its output, then the reply.</summary>
        private static void BackgroundReplyArrives(ScriptedEngine engine)
        {
            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "t1", Title = "Read", Kind = "read" });
            engine.Raise(new AgentEventDto { Type = "toolDone", ToolCallId = "t1" });
            engine.Raise(new AgentEventDto { Type = "text", Text = "Done — the background task finished with exit code 0." });
            Drain();
        }

        private static void Open(ChatViewModel vm, string sessionId)
        {
            vm.RefreshHistory();
            vm.History.Single(h => h.Id == sessionId).LoadCommand.Execute(null);
            Drain();
        }

        private static void Prompt(ChatViewModel vm, ScriptedEngine engine, string text)
        {
            vm.InputText = text;
            vm.SendCommand.Execute(null);
            Drain();
            engine.CompleteTurn();
            Drain();
            vm.RefreshHistory();
        }

        private ChatViewModel NewViewModel(ScriptedEngine engine, FileSessionStore store, Action<string>? log = null) => new(
            engine,
            new StartSessionRequest("fake", null, _root, "Prompt", null),
            sessionStore: store,
            diagnosticLog: log);

        private static void Drain() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        // Turns stay open until the test ends them: the reply under test arrives after a turn has ended.
        private static ScriptedEngine HeldTurnEngine() => new()
        {
            Turns = ScriptedEngine.TurnEnding.WhenCompleted,
        };

        private static void RunSta(Action action) => StaTest.Run(action, withDispatcherContext: true);
    }
}
