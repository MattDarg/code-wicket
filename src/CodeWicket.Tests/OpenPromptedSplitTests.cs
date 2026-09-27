using System;
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
    /// A backend session that has been OPENED is not one that has been PROMPTED. Until a prompt of the conversation actually goes out, nothing about the session is recorded
    /// into the conversation, nothing about the conversation changes, and nothing counts it as started.
    /// </summary>
    /// <remarks>
    /// <para>Each case was seen failing on the code before the split was built.</para>
    /// </remarks>
    public sealed class OpenPromptedSplitTests : IDisposable
    {
        private const string Refusal = "Session not found: sess_old";
        private const string Message = "what changed?";

        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "cwkt-open-prompted-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch (IOException) { }
        }

        /// <summary>
        /// The session a refused reload fell through to announces itself while the send is parked on the
        /// banner. It is a session opening, not the conversation: drawn as setup, and never written into the
        /// reopened conversation's log. Recorded, backing out left the conversation changed on disk, and its
        /// place in the history picker moved, for a message the user took back.
        /// </summary>
        [Fact]
        public void TheRefusedSessionsOpeningFramesAreNotRecordedIntoTheReopenedConversation() => RunSta(() =>
        {
            var engine = RefusingEngine();
            engine.OpeningFrames.Add(new AgentEventDto { Type = "toolStart", ToolCallId = "cfg", Title = "Fetching your cloud config", Kind = "other" });
            engine.OpeningFrames.Add(new AgentEventDto { Type = "toolDone", ToolCallId = "cfg" });
            var vm = Resumable(engine);
            var before = SavedBytes();

            SendAndChooseFull(vm);
            Assert.Equal("Couldn't reload this conversation", vm.PendingResume?.Heading);
            Assert.True(Assert.Single(vm.Items.OfType<ToolItemViewModel>()).IsSessionSetup);

            vm.PendingResume!.CancelCommand.Execute(null);
            Drain();

            Assert.True(before.AsSpan().SequenceEqual(SavedBytes()), "the reopened conversation changed on disk");
        });

        /// <summary>
        /// A backed-out session has still never been prompted, so a frame it sends late - kiro-cli's
        /// opening toolDone lands 55-320 ms after the start returns - is still its opening: never recorded, and
        /// no out-of-turn window over a pane nothing is running in.
        /// </summary>
        [Fact]
        public void ALateOpeningFrameAfterBackingOutIsStillTheSessionOpening() => RunSta(() =>
        {
            var engine = RefusingEngine();
            var vm = Resumable(engine);

            SendAndChooseFull(vm);
            vm.PendingResume!.CancelCommand.Execute(null);
            Drain();
            Assert.False(vm.IsBusy);

            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "cfg", Title = "Fetching your cloud config", Kind = "other" });
            engine.Raise(new AgentEventDto { Type = "toolDone", ToolCallId = "cfg" });
            Drain();

            Assert.False(vm.IsAgentWorkingOutOfTurn, "a never-prompted session's opening opened the working window");
            Assert.True(Assert.Single(vm.Items.OfType<ToolItemViewModel>()).IsSessionSetup);
            Assert.DoesNotContain(SavedConversation().Log, e => e.Event is { Type: "toolStart" or "toolDone" });
        });

        /// <summary>
        /// A failed import after backing out releases the session the refusal was found on, and with it
        /// the refusal. The pane was never prompted, so the next message is asked about again rather than going
        /// unasked into the import's empty session with the transcript's history nowhere.
        /// </summary>
        [Fact]
        public void AFailedImportAfterBackingOutDoesNotSendTheNextMessageUnasked() => RunSta(() =>
        {
            var engine = RefusingEngine();
            var vm = Resumable(engine);

            SendAndChooseFull(vm);
            vm.PendingResume!.CancelCommand.Execute(null);
            Drain();

            var import = vm.ImportBackendSessionAsync(new BackendSessionItemViewModel(
                new BackendSessionDto("imported-1", "Imported", null, null), "fake", "Fake", _ => { }));
            Assert.True(import.IsCompleted);
            Assert.Contains(vm.Items.OfType<NoticeItemViewModel>(), n => n.Text.StartsWith("Couldn't open that conversation", StringComparison.Ordinal));

            vm.SendCommand.Execute(null); // the given-back text
            Drain();

            Assert.Empty(engine.Prompts);
            Assert.True(vm.HasPendingResume, "the next message went out without being asked how to resume");
        });

        /// <summary>
        /// The message is recorded when its prompt goes out, not before the wait for the IDE tools: a
        /// workspace move during the wait left a message saved in the old workspace that was never sent.
        /// </summary>
        [Fact]
        public void NothingIsSavedWhileTheFirstPromptWaitsForTheIdeTools() => RunSta(() =>
        {
            using var s = PendingSendScenario.Reach(PendingPhase.WaitingForTools);
            Assert.DoesNotContain(Load(s).Log, e => e.Role == "user" && e.Text == PendingSendScenario.Message);

            s.Vm.UpdateWorkspaceRoot(s.MovedRoot, notice: null);
            s.Settle();

            Assert.Empty(s.Engine.Prompts);
            var saved = Load(s);
            Assert.DoesNotContain(saved.Log, e => e.Role == "user" && e.Text == PendingSendScenario.Message);
            Assert.Equal("conv-1", saved.ConversationId);
        });

        /// <summary>
        /// A recap is fenced with the directory its transcript was written in (issue #184). Starting the
        /// session whose reload was refused wrote ITS directory onto the conversation before the refusal was
        /// known, so after backing out the recap claimed the new session's root as the transcript's origin.
        /// </summary>
        [Fact]
        public void ARecapAfterBackingOutNamesTheConversationsOwnRoot() => RunSta(() =>
        {
            var original = Path.Combine(_dir, "original");
            var engine = RefusingEngine();
            engine.WorkingDirectory = Path.Combine(_dir, "elsewhere");
            var vm = Resumable(engine, agentWorkingDirectory: original);

            SendAndChooseFull(vm);
            vm.PendingResume!.CancelCommand.Execute(null);
            Drain();

            vm.SendCommand.Execute(null); // the given-back text
            Drain();
            // One question now, the decider's: every send decides from scratch. Answering it with the recap
            // REUSES the session the refusal fell through to, so the refused banner is not raised again on the way.
            Assert.Equal("Continue this conversation?", vm.PendingResume?.Heading);
            vm.PendingResume!.ResumeSummaryCommand.Execute(null);
            DrainAll();

            Assert.Equal(original, Assert.Single(engine.SummarizeRequests).SourceWorkingDirectory);
        });

        /// <summary>
        /// The runner's commit follows the send's last await with nothing in between. In the post where the
        /// session had started and the prompt had not yet gone, the unfixed code had already recorded the
        /// message and stamped the new session's id, so a workspace move landing there left both in the OLD
        /// workspace's file.
        /// </summary>
        [Fact]
        public void AWorkspaceMoveBeforeThePromptGoesLeavesTheOldConversationUntouched() => RunSta(() =>
        {
            using var s = PendingSendScenario.Reach(PendingPhase.Summarizing);
            var acted = false;

            ActAfterEachOperation(
                () => !acted && s.Engine.StartCount >= 1 && s.Engine.Prompts.Count == 0,
                () =>
                {
                    acted = true;
                    s.Vm.UpdateWorkspaceRoot(s.MovedRoot, notice: null);
                },
                () => s.Settle());

            Assert.True(acted, "the send never reached its session start");
            Assert.Empty(s.Engine.Prompts);
            var saved = Load(s);
            Assert.DoesNotContain(saved.Log, e => e.Role == "user" && e.Text == PendingSendScenario.Message);
            Assert.Equal("conv-1", saved.ConversationId);
        });

        /// <summary>
        /// No post separates recording the message from issuing its prompt. Without the fix one did, and a
        /// Stop landing in it cancelled a session with no turn on it while the prompt went out anyway.
        /// </summary>
        [Fact]
        public void TheMessageIsNeverRecordedAPostBeforeItsPromptGoes() => RunSta(() =>
        {
            using var s = PendingSendScenario.Reach(PendingPhase.Summarizing);
            var acted = false;

            ActAfterEachOperation(
                () => !acted && s.Engine.Prompts.Count == 0
                    && Load(s).Log.Any(e => e.Role == "user" && e.Text == PendingSendScenario.Message),
                () =>
                {
                    acted = true;
                    s.Vm.StopCommand.Execute(null);
                },
                () => s.Settle());

            if (acted)
            {
                Assert.Empty(s.Engine.Prompts);
                Assert.Equal(0, s.Engine.CancelCount);
            }
            Assert.False(acted, "the message was recorded in a post before its prompt went");
        });

        /// <summary>
        /// Backing out frees the pane in the same step that gives the message back: without the fix the
        /// message sat in the composer for a post while the pane still read busy.
        /// </summary>
        [Fact]
        public void BackingOutGivesTheMessageBackInTheStepThatFreesThePane() => RunSta(() =>
        {
            using var s = PendingSendScenario.Reach(PendingPhase.AskingRefused);
            var vm = s.Vm;
            var seenBusyWithTheMessageBack = false;

            ActAfterEachOperation(
                () =>
                {
                    if (vm.IsBusy && vm.InputText == PendingSendScenario.Message)
                        seenBusyWithTheMessageBack = true;
                    return false;
                },
                () => { },
                () =>
                {
                    vm.PendingResume!.CancelCommand.Execute(null);
                    s.Settle();
                });

            Assert.Equal(PendingSendScenario.Message, vm.InputText);
            Assert.False(vm.IsBusy);
            Assert.False(seenBusyWithTheMessageBack, "the message was back in the composer while the pane still read busy");
        });

        /// <summary>
        /// A consequence, accepted, of a session counting as prompted only once a prompt goes out: while the
        /// first prompt waits for the IDE
        /// tools there is no turn to steer into, so a mid-turn gesture holds rather than steering.
        /// </summary>
        [Fact]
        public void NothingCanBeSteeredIntoASessionBeforeItsFirstPrompt() => RunSta(() =>
        {
            var engine = new ScriptedEngine { SupportsSteering = true, Turns = ScriptedEngine.TurnEnding.WhenCompleted }
                .WithProvider("fake", "Fake", "Mcp");
            var vm = new ChatViewModel(engine, new StartSessionRequest("fake", null, _dir, "Prompt", null));
            vm.IdeToolsWait = TimeSpan.FromSeconds(10);
            vm.InitializeAsync().GetAwaiter().GetResult();
            Drain();

            vm.InputText = Message;
            vm.SendCommand.Execute(null);
            Drain();
            Assert.True(vm.IsBusy);
            Assert.Equal(1, engine.StartCount);
            Assert.Empty(engine.Prompts);
            Assert.False(vm.CanSteer, "a steer was offered into a session with no turn");

            engine.ServeTools();
            PumpUntil(() => engine.Prompts.Count == 1, TimeSpan.FromSeconds(3));
            Assert.True(vm.CanSteer);
        });

        /// <summary>
        /// A consequence, accepted, of a session counting as prompted only once a prompt goes out: a workspace
        /// move over a backed-out pane,
        /// which was never prompted, is the re-aim case and not a new session's clean chat.
        /// </summary>
        [Fact]
        public void AWorkspaceMoveOverABackedOutPaneReAimsIt() => RunSta(() =>
        {
            var vm = Resumable(RefusingEngine());

            SendAndChooseFull(vm);
            vm.PendingResume!.CancelCommand.Execute(null);
            Drain();

            var moved = Path.Combine(_dir, "moved");
            Directory.CreateDirectory(moved);
            vm.UpdateWorkspaceRoot(moved, notice: null);
            DrainAll();

            Assert.DoesNotContain(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.Contains("Your next message starts a new session here", StringComparison.Ordinal));
            Assert.Equal(Message, vm.InputText);
        });

        /// <summary>
        /// User decision (2026-09-15): a picker change after backing out says nothing, as on every pane that was
        /// never prompted. The backed-out session is left behind with its refusal, so the next message is
        /// asked how to resume rather than sent unasked into the old backend's session.
        /// </summary>
        [Fact]
        public void AProviderChangeAfterBackingOutSaysNothingAndTheNextMessageIsAsked() => RunSta(() =>
        {
            var engine = RefusingEngine().WithProvider("other", "Other", "ResumeSession");
            var vm = Resumable(engine);

            SendAndChooseFull(vm);
            vm.PendingResume!.CancelCommand.Execute(null);
            Drain();

            vm.SelectedProvider = vm.Providers.Single(p => p.Id == "other");
            Drain();

            Assert.DoesNotContain(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.StartsWith("Switched to", StringComparison.Ordinal));

            vm.SendCommand.Execute(null); // the given-back text
            Drain();

            Assert.Empty(engine.Prompts);
            Assert.Equal("Continue this conversation?", vm.PendingResume?.Heading);
        });

        /// <summary>
        /// A send retired before it prompted holds no lease, so the move's warm start is no longer refused; it
        /// must still wait for that send's session start to finish, or two starts race the engine's one-session
        /// teardown. It could not fail while the view-model had an IsBusy gate of its own: that gate refused the
        /// move's warm start, and the retired send's return made it instead.
        /// </summary>
        [Fact]
        public void AWorkspaceMoveDuringASendsStartWarmStartsOnlyOnceThatStartIsDone() => RunSta(() =>
        {
            var engine = RefusingEngine();
            engine.RefuseResume = false;
            engine.GateStart = new TaskCompletionSource<bool>();
            var vm = Resumable(engine);

            SendAndChooseFull(vm);
            Assert.Equal(1, engine.StartCount); // in flight

            var moved = Path.Combine(_dir, "moved");
            Directory.CreateDirectory(moved);
            vm.UpdateWorkspaceRoot(moved, notice: null);
            DrainAll();
            Assert.Equal(1, engine.StartCount);

            var gate = engine.GateStart;
            engine.GateStart = null;
            gate.SetResult(true);
            DrainAll();

            Assert.Equal(2, engine.StartCount);
            Assert.Empty(engine.Prompts);
        });

        /// <summary>
        /// The session a retired send opened was nobody's prompt, so what it sends while the new root's warm
        /// start waits behind it - its opening row - is not drawn in the chat that replaced it.
        /// </summary>
        [Fact]
        public void ASessionARetiredSendOpenedDrawsNothingInTheNewChat() => RunSta(() =>
        {
            var engine = RefusingEngine();
            engine.RefuseResume = false;
            engine.GateStart = new TaskCompletionSource<bool>();
            var vm = Resumable(engine);

            SendAndChooseFull(vm);
            var moved = Path.Combine(_dir, "moved");
            Directory.CreateDirectory(moved);
            vm.UpdateWorkspaceRoot(moved, notice: null);
            DrainAll();

            var gate = engine.GateStart;
            engine.GateStart = null;
            engine.OpeningFrames.Add(new AgentEventDto { Type = "toolStart", ToolCallId = "cfg", Title = "Fetching your cloud config", Kind = "other" });
            gate.SetResult(true);
            DrainAll();

            Assert.Equal(2, engine.StartCount);
            // One row: the new root's own warm session's opening. The retired send's session drew none.
            Assert.Single(vm.Items.OfType<ToolItemViewModel>());
        });

        /// <summary>
        /// Off screen: a session opened for a conversation and never prompted (a refused reload backed out of)
        /// has nothing of its own to record, so with another conversation on screen its frames are not written
        /// into that conversation's log either.
        /// </summary>
        [Fact]
        public void AnUnpromptedOwnersFramesAreNotRecordedOffScreen() => RunSta(() =>
        {
            var engine = RefusingEngine();
            var vm = Resumable(engine);
            var original = SavedConversation().Id;
            var other = new PersistedSession
            {
                WorkspaceRootPath = _dir,
                ProviderId = "fake",
                Title = "Something else",
                UpdatedUtc = DateTime.UtcNow.AddHours(-1),
            };
            other.Log.Add(new TranscriptEntry { Role = "user", Text = "an older question" });
            new FileSessionStore(_dir).Save(other);

            SendAndChooseFull(vm);
            vm.PendingResume!.CancelCommand.Execute(null);
            Drain();

            vm.RefreshHistory();
            vm.History.Single(h => h.Id == other.Id).LoadCommand.Execute(null);
            Drain();

            engine.Raise(new AgentEventDto { Type = "text", Text = "a late word from the refused session" });
            DrainAll();
            Assert.DoesNotContain(vm.Items.OfType<MessageItemViewModel>(), m => m.Text.StartsWith("a late word", StringComparison.Ordinal));

            // Reopening the owner flushes whatever was recorded to it off screen before the file is read (#256),
            // so this reads what the log holds rather than racing the flush timer.
            vm.RefreshHistory();
            vm.History.Single(h => h.Id == original).LoadCommand.Execute(null);
            Drain();

            Assert.DoesNotContain(vm.Items.OfType<MessageItemViewModel>(), m => m.Text.StartsWith("a late word", StringComparison.Ordinal));
            var store = new FileSessionStore(_dir);
            Assert.DoesNotContain(store.Load(_dir, original)!.Log,
                e => e.Event is { Type: "text" } ev && ev.Text is { } t && t.StartsWith("a late word", StringComparison.Ordinal));
        });

        /// <summary>
        /// The session a backed-out send opened is the one the next send prompts, so its
        /// working directory reaches the conversation, and its notice the transcript, when that send commits (#54).
        /// Moving the apply to the commit had left it to the send that STARTED the session, which backed out.
        /// </summary>
        [Fact]
        public void TheSessionABackedOutSendOpenedIsAppliedWhenTheNextSendCommits() => RunSta(() =>
        {
            var original = Path.Combine(_dir, "original");
            var elsewhere = Path.Combine(_dir, "elsewhere");
            var engine = RefusingEngine();
            engine.WorkingDirectory = elsewhere;
            var vm = Resumable(engine, agentWorkingDirectory: original);

            SendAndChooseFull(vm);
            vm.PendingResume!.CancelCommand.Execute(null);
            Drain();

            vm.SendCommand.Execute(null); // the given-back text
            Drain();
            // The decider first, and its recap answer reuses the backed-out session rather than opening
            // another - which is what makes the working directory below THAT session's.
            Assert.Equal("Continue this conversation?", vm.PendingResume?.Heading);
            vm.PendingResume!.ResumeSummaryCommand.Execute(null);
            DrainAll();

            Assert.Single(engine.Prompts);
            // One session throughout: the refusal's fall-through, reused rather than replaced.
            Assert.Equal(1, engine.StartCount);
            Assert.Equal(elsewhere, SavedConversation().AgentWorkingDirectory);
            Assert.Contains(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.StartsWith("Working directory: " + elsewhere, StringComparison.Ordinal));
        });

        /// <summary>
        /// A send waiting on the warm session is a start in progress. If that warm start
        /// fails and a workspace move has retired the send meanwhile, the send's own cold start and the move's warm
        /// start must not both be in the engine, and the move's must be the last, or the engine is left holding the
        /// old root's session for the new root's next send to prompt.
        /// </summary>
        [Fact]
        public void ARetiredSendsColdStartAndTheMovesWarmStartDoNotOverlap() => RunSta(() =>
        {
            var failedWarm = new TaskCompletionSource<bool>();
            var coldStart = new TaskCompletionSource<bool>();
            var engine = new ScriptedEngine { Turns = ScriptedEngine.TurnEnding.WhenCompleted }.WithProvider("fake", "Fake");
            engine.StartGates.Enqueue(failedWarm);
            engine.StartGates.Enqueue(coldStart);
            var vm = new ChatViewModel(engine, new StartSessionRequest("fake", null, _dir, "Prompt", null));
            vm.InitializeAsync().GetAwaiter().GetResult();
            vm.WarmStartSession();
            Drain();
            Assert.Equal(1, engine.StartCount);

            vm.InputText = Message;
            vm.SendCommand.Execute(null);
            Drain();
            Assert.True(vm.IsBusy);
            Assert.Equal(1, engine.StartCount); // the send waits on the warm session

            var moved = Path.Combine(_dir, "moved");
            Directory.CreateDirectory(moved);
            vm.UpdateWorkspaceRoot(moved, notice: null);
            DrainAll();

            failedWarm.SetResult(false);
            DrainAll();
            coldStart.SetResult(true);
            DrainAll();

            Assert.Equal(3, engine.StartCount);
            Assert.Equal(0, engine.OverlappingStarts);
            Assert.Equal(moved, engine.Starts[engine.Starts.Count - 1].WorkspaceRootPath);
            Assert.Empty(engine.Prompts);
        });

        // ---- helpers --------------------------------------------------------------------------

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

        private static PersistedSession Load(PendingSendScenario s) =>
            new FileSessionStore(s.Dir).Load(s.Dir, s.ConversationId)!;

        private PersistedSession SavedConversation()
        {
            var store = new FileSessionStore(_dir);
            return store.Load(_dir, store.List(_dir).Single().Id)!;
        }

        private byte[] SavedBytes() => File.ReadAllBytes(
            Directory.GetFiles(_dir, "*.json", SearchOption.AllDirectories).Single());

        private static void SendAndChooseFull(ChatViewModel vm)
        {
            vm.InputText = Message;
            vm.SendCommand.Execute(null);
            Drain();

            Assert.True(vm.HasPendingResume);
            vm.PendingResume!.ResumeFullCommand.Execute(null);
            Drain();
        }

        // A restored conversation big enough that the send-time banner offers the full-vs-summary choice.
        private ChatViewModel Resumable(ScriptedEngine engine, string? agentWorkingDirectory = null)
        {
            var store = new FileSessionStore(_dir);
            var session = new PersistedSession
            {
                WorkspaceRootPath = _dir,
                ConversationId = "conv-1",
                ProviderId = "fake",
                Title = "Earlier work",
                AgentWorkingDirectory = agentWorkingDirectory,
            };
            SeededConversation.AddExchange(session, new string('u', 2500), new string('a', 2500));
            store.Save(session);

            var vm = new ChatViewModel(
                engine,
                new StartSessionRequest("fake", null, _dir, "Prompt", null),
                sessionStore: store);
            vm.InitializeAsync().GetAwaiter().GetResult();
            vm.RestoreMostRecentSession();
            Drain();
            return vm;
        }

        private static ScriptedEngine RefusingEngine() =>
            new ScriptedEngine { RefuseResume = true, RefusalReason = Refusal, AllowSummarize = true }
                .WithProvider("fake", "Fake", "ResumeSession");

        private static void Drain() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        private static void DrainAll()
        {
            for (var i = 0; i < 4; i++)
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        }

        private static void PumpUntil(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!condition() && DateTime.UtcNow < deadline)
            {
                Drain();
                Thread.Sleep(10);
            }
        }

        private static void RunSta(Action action) => StaTest.Run(action, withDispatcherContext: true);
    }
}
