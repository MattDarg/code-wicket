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
    /// <summary>Where a send can be when something changes the conversation under it, as far as this scenario can drive it.</summary>
    public enum PendingPhase
    {
        /// <summary>The "Continue this conversation?" banner is up: the send has not started, and the pane is not busy.</summary>
        ChoiceBanner,

        /// <summary>The recap is being written: the summarize is held, and the send holds the pane.</summary>
        Summarizing,

        /// <summary>The backend refused the reload, and the send is parked on the banner asking what to do (issue #268).</summary>
        AskingRefused,

        /// <summary>The session is open and the message recorded, and the first prompt waits for our IDE tools.</summary>
        WaitingForTools,
    }

    /// <summary>
    /// A reopened conversation driven into one <see cref="PendingPhase"/>, for the conversation-change ordering
    /// tests and the command sweep (<see cref="PendingSendCommandSweepTests"/>).
    /// </summary>
    /// <remarks>
    /// <para>The workspace holds two saved conversations: the one under test, big enough that a send asks how to
    /// resume it, and an older one to open from history. The catalogue holds a second backend for the picker,
    /// and the engine can answer an import.</para>
    /// <para><b>Each phase is asserted as reached</b>, so a case that slipped past it (the IDE-tools wait
    /// timing out before the test acts, say) fails here rather than testing nothing.</para>
    /// <para>The moved-root banner is not a phase here: it needs an engine that answers the root query, and
    /// <see cref="ScriptedEngine"/> does not.</para>
    /// </remarks>
    internal sealed class PendingSendScenario : IDisposable
    {
        public const string Message = "what changed?";
        public const string OtherProvider = "other";

        /// <summary>The name of the image a <c>withChips</c> scenario pastes before it sends.</summary>
        public const string ImageName = "snip.png";

        /// <summary>The label of the IDE capture a <c>withChips</c> scenario attaches before it sends.</summary>
        public const string ContextLabel = "Debug state - Recurse";

        private PendingSendScenario(
            PendingPhase phase, string dir, ScriptedEngine engine, ChatViewModel vm, string conversationId, string otherId)
        {
            Phase = phase;
            Dir = dir;
            Engine = engine;
            Vm = vm;
            ConversationId = conversationId;
            OtherId = otherId;
        }

        public PendingPhase Phase { get; }

        public string Dir { get; }

        public ScriptedEngine Engine { get; }

        public ChatViewModel Vm { get; }

        /// <summary>The local id of the conversation the pending send belongs to.</summary>
        public string ConversationId { get; }

        /// <summary>The local id of the older conversation, for a history open.</summary>
        public string OtherId { get; }

        /// <summary>The solution a workspace move lands in.</summary>
        public string MovedRoot
        {
            get
            {
                var moved = Path.Combine(Dir, "moved");
                Directory.CreateDirectory(moved);
                return moved;
            }
        }

        /// <param name="phase">Where the send is when the test acts on it.</param>
        /// <param name="withChips">
        /// Paste an image and attach an IDE capture before sending, so a retirement's give-back can be
        /// asserted on the whole message rather than its text alone. Off by default: the suites
        /// that only care where a send IS should not pay for the chips, and their composer assertions
        /// were written without them.
        /// </param>
        public static PendingSendScenario Reach(PendingPhase phase, bool withChips = false)
        {
            var dir = Path.Combine(Path.GetTempPath(), "cwkt-pending-send-" + Guid.NewGuid().ToString("N"));

            var engine = new ScriptedEngine
            {
                AllowSummarize = true,
                RefuseResume = phase == PendingPhase.AskingRefused,
            };
            if (phase == PendingPhase.Summarizing)
                engine.GateSummarize = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.WithProvider("fake", "Fake", phase == PendingPhase.WaitingForTools
                ? new[] { "ResumeSession", "Mcp" }
                : new[] { "ResumeSession" });
            engine.WithProvider(OtherProvider, "Other");
            engine.ImportedHistory.Add(new ImportedEntryDto("user", "an imported question", null));
            engine.ImportedHistory.Add(new ImportedEntryDto("agent", null, new AgentEventDto { Type = "text", Text = "an imported answer" }));

            // The older one first, so the restore picks the conversation under test.
            var store = new FileSessionStore(dir);
            var other = new PersistedSession
            {
                WorkspaceRootPath = dir,
                ProviderId = "fake",
                Title = "Something else",
                UpdatedUtc = DateTime.UtcNow.AddHours(-1),
            };
            other.Log.Add(new TranscriptEntry { Role = "user", Text = "an older question" });
            store.Save(other);

            // Big enough that the send-time banner offers full-vs-summary (ResumeDecider's threshold is 4000 chars).
            var session = new PersistedSession
            {
                WorkspaceRootPath = dir,
                ConversationId = "conv-1",
                ProviderId = "fake",
                Title = "Earlier work",
            };
            SeededConversation.AddExchange(session, new string('u', 2500), new string('a', 2500));
            store.Save(session);

            var vm = new ChatViewModel(
                engine,
                new StartSessionRequest("fake", null, dir, "Prompt", null),
                sessionStore: store);
            // Short, so a change that retires the wait lets the ended send finish inside the test.
            vm.IdeToolsWait = TimeSpan.FromMilliseconds(300);
            vm.InitializeAsync().GetAwaiter().GetResult();
            vm.RestoreMostRecentSession();
            Drain();

            vm.RefreshHistory();
            Assert.Equal(session.Id, vm.History.Single(h => h.IsCurrent).Id);

            if (withChips)
            {
                // A pasted snip: bytes and no path, which is the form the composer holds until the
                // message is actually sent (AttachmentViewModel's own rule).
                vm.PendingAttachments.Add(new AttachmentViewModel(
                    ImageName, "image/png", new byte[] { 1, 2, 3 }, filePath: null, remove: _ => { }));
                vm.PendingContexts.Add(new ContextItemViewModel(
                    "debug-state", ContextLabel, "#1 Recurse  Foo.cs:33"));
            }

            vm.InputText = Message;
            vm.SendCommand.Execute(null);
            Drain();
            Assert.Equal("Continue this conversation?", vm.PendingResume?.Heading);

            switch (phase)
            {
                case PendingPhase.Summarizing:
                    vm.PendingResume!.ResumeSummaryCommand.Execute(null);
                    Drain();
                    Assert.True(vm.IsBusy, "the send holds the pane while the recap is written");
                    Assert.Equal(1, engine.SummarizeCount);
                    Assert.Null(vm.PendingResume);
                    break;

                case PendingPhase.AskingRefused:
                    vm.PendingResume!.ResumeFullCommand.Execute(null);
                    Drain();
                    Assert.Equal("Couldn't reload this conversation", vm.PendingResume?.Heading);
                    Assert.True(vm.HasPendingSendForDiagnostics, "the send is parked on the refused-reload banner");
                    break;

                case PendingPhase.WaitingForTools:
                    vm.PendingResume!.ResumeFullCommand.Execute(null);
                    Drain();
                    Assert.True(vm.IsBusy, "the send holds the pane while it waits for the IDE tools");
                    Assert.Equal(1, engine.StartCount);
                    Assert.Empty(engine.Prompts);
                    Assert.Null(vm.PendingResume);
                    break;
            }

            Assert.Empty(engine.Prompts);
            return new PendingSendScenario(phase, dir, engine, vm, session.Id, other.Id);
        }

        /// <summary>
        /// Lets whatever is still running finish: the held summarize is released, the tools are served, and
        /// the dispatcher is pumped until the pane is idle or parked on a banner.
        /// </summary>
        public void Settle()
        {
            Engine.GateSummarize?.TrySetResult(true);
            if (Phase == PendingPhase.WaitingForTools)
                Engine.ServeTools();

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            do
            {
                DrainAll();
                if (!Vm.IsBusy || Vm.PendingResume is not null)
                    break;
                Thread.Sleep(10);
            }
            while (DateTime.UtcNow < deadline);

            DrainAll();
        }

        /// <summary>Every saved conversation file, by path.</summary>
        public Dictionary<string, byte[]> SavedFiles() =>
            Directory.Exists(Dir)
                ? Directory.GetFiles(Dir, "*.json", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes)
                : new Dictionary<string, byte[]>();

        public static void Drain() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        // Down to ContextIdle, repeatedly: a warm start, a retired turn's return and a start-over each hop again.
        public static void DrainAll()
        {
            for (var i = 0; i < 4; i++)
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        }

        /// <summary>
        /// Pumps until <paramref name="condition"/> holds, or <paramref name="ceiling"/> passes.
        /// </summary>
        /// <remarks>
        /// <para>For anything released across a thread-pool hop: the IDE-tools wait is a
        /// <c>Task.WhenAny</c>, and a held summarize's source completes its continuations asynchronously.
        /// A fixed number of drains is a guess at how many hops are needed, and the guess holds on an idle
        /// machine and runs ahead of the release on a loaded one - so it reads green solo and red inside a
        /// gate matrix or an injection sweep, which is the least useful way round.</para>
        /// <para><b>Only for asserting something DOES happen.</b> No amount of polling establishes a
        /// non-event; that has to spend a fixed period and then look.</para>
        /// </remarks>
        public static void PumpUntil(Func<bool> condition, TimeSpan ceiling)
        {
            var until = DateTime.UtcNow + ceiling;
            while (!condition() && DateTime.UtcNow < until)
            {
                DrainAll();
                Thread.Sleep(10);
            }

            DrainAll();
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
