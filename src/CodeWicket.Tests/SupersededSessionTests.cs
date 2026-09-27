using System;
using System.Collections.Generic;
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
    /// A provider change on a pane that has prompted makes its session SUPERSEDED
    /// rather than dropping it. The engine keeps the session until the next start disposes it, and it is still
    /// this conversation's, so its late work is recorded and drawn as work; the next message does not go into it; and
    /// a picker that lands exactly back on it continues there (user decision, 2026-09-15).
    /// </summary>
    public sealed class SupersededSessionTests : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "cwkt-superseded-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch (IOException) { }
        }

        /// <summary>
        /// A background task the conversation started still returns after the picker moved (issue #256's case, one
        /// gesture over). Before, the change counted the pane as never prompted, so that return was drawn as the
        /// session OPENING: marked as setup, never recorded, and no working bar while the agent worked.
        /// </summary>
        [Fact]
        public void AProviderChangeLeavesTheSessionsLateWorkRecordedAsWork() => RunSta(() =>
        {
            var (vm, engine) = Started();
            vm.SelectedProvider = Provider(vm, "claude-code");
            Drain();

            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "bg", Title = "Run tests", Kind = "other" });
            Drain();
            Assert.True(vm.IsAgentWorkingOutOfTurn, "the superseded session's own work did not open the working window");
            engine.Raise(new AgentEventDto { Type = "toolDone", ToolCallId = "bg" });
            Drain();

            Assert.False(Assert.Single(vm.Items.OfType<ToolItemViewModel>()).IsSessionSetup);
            Assert.Contains(SavedConversation().Log, e => e.Event is { Type: "toolStart", ToolCallId: "bg" });
        });

        /// <summary>The session the change superseded does not take the next message: it runs the backend the user left.</summary>
        [Fact]
        public void TheNextSendAfterAProviderChangeDoesNotGoIntoTheSupersededSession() => RunSta(() =>
        {
            var (vm, engine) = Started();
            vm.SelectedProvider = Provider(vm, "claude-code");
            Drain();
            var prompts = engine.Prompts.Count;
            var starts = engine.StartCount;

            vm.InputText = "carry on";
            vm.SendCommand.Execute(null);
            Drain();

            Assert.False(engine.Prompts.Count > prompts && engine.StartCount == starts,
                "the message went into the session the provider change superseded");
        });

        /// <summary>
        /// User decision (2026-09-15): each change before a send says what the next message will do. Before, only the
        /// first did, so the one notice named the backend the user had since moved away from.
        /// </summary>
        [Fact]
        public void ASecondPickerChangeSaysWhatTheNextMessageWillDoAgain() => RunSta(() =>
        {
            var (vm, _) = Started();
            vm.SelectedProvider = Provider(vm, "claude-code");
            Drain();
            vm.SelectedProvider = Provider(vm, "other");
            Drain();

            var notices = SwitchNotices(vm);
            Assert.Equal(2, notices.Count);
            Assert.StartsWith("Switched to Other", notices[1].Text, StringComparison.Ordinal);
        });

        /// <summary>
        /// User decision (2026-09-15): a picker that lands exactly where the session was opened goes back to it. The
        /// session never closed, so nothing is reloaded, and what the change cleared - the active selection and the
        /// handshake's capabilities - is the session's again.
        /// </summary>
        [Fact]
        public void SwitchingStraightBackContinuesInTheLiveSession() => RunSta(() =>
        {
            var (vm, engine) = Started(steering: true);
            vm.SelectedProvider = Provider(vm, "claude-code");
            Drain();
            vm.SelectedProvider = Provider(vm, "kiro");
            Drain();

            // Backend AND model: the match is on both, and that pair is what decides the session is kept (user, 2026-09-16).
            Assert.Equal("Back on Kiro (auto) — this conversation continues in its current session.",
                vm.Items.OfType<NoticeItemViewModel>().Last().Text);
            Assert.True(vm.CanSteer, "switching back did not restore the session's own handshake");

            var starts = engine.StartCount;
            Prompt(vm, engine, "and now?");
            Assert.Equal(starts, engine.StartCount);
            Assert.Contains("and now?", engine.Prompts.Last(), StringComparison.Ordinal);
            Assert.Null(vm.PendingResume);

            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "bg2", Title = "Run tests", Kind = "other" });
            Drain();
            Assert.True(vm.IsAgentWorkingOutOfTurn);
        });

        /// <summary>
        /// The shape found in the F5 (user, 2026-09-16): the backend comes back first, landing on its default model, which
        /// is not the session's own - so that change says "Switched to …". Re-picking the model completes the match, and
        /// the notice then names BOTH, because the user never left the backend and the model is what moved.
        /// </summary>
        [Fact]
        public void TheModelCompletingTheMatchIsNamedInTheNotice() => RunSta(() =>
        {
            var (vm, engine) = Started(model: "fast");
            vm.SelectedProvider = Provider(vm, "claude-code");
            Drain();
            vm.SelectedProvider = Provider(vm, "kiro"); // lands on Kiro's default, auto: not the session's request
            Drain();
            Assert.StartsWith("Switched to Kiro", SwitchNotices(vm).Last().Text, StringComparison.Ordinal);

            vm.SelectedModel = vm.Models.Single(m => m.Id == "fast");
            Drain();

            Assert.Equal("Back on Kiro (Fast) — this conversation continues in its current session.",
                vm.Items.OfType<NoticeItemViewModel>().Last().Text);

            var starts = engine.StartCount;
            Prompt(vm, engine, "and now?");
            Assert.Equal(starts, engine.StartCount);
            Assert.Contains("and now?", engine.Prompts.Last(), StringComparison.Ordinal);
        });

        /// <summary>
        /// Back means EXACTLY back. A provider flip lands the model picker on the backend's default, so a session that
        /// ran another model is not the one the picker now describes, and is not reused.
        /// </summary>
        [Fact]
        public void SwitchingBackOntoAnotherModelDoesNotReuseTheSession() => RunSta(() =>
        {
            var (vm, engine) = Started(model: "fast");
            vm.SelectedProvider = Provider(vm, "claude-code");
            Drain();
            vm.SelectedProvider = Provider(vm, "kiro");
            Drain();

            Assert.Equal("auto", vm.SelectedModel?.Id);
            Assert.StartsWith("Switched to Kiro", SwitchNotices(vm).Last().Text, StringComparison.Ordinal);

            var prompts = engine.Prompts.Count;
            var starts = engine.StartCount;
            vm.InputText = "carry on";
            vm.SendCommand.Execute(null);
            Drain();
            Assert.False(engine.Prompts.Count > prompts && engine.StartCount == starts,
                "the message went into a session running a different model");
        });

        /// <summary>
        /// The user's choice (2026-09-15), the most consistent one: a pane that has sent something is a started pane,
        /// whatever the picker did since, so a workspace move gives it a clean chat and a new session, not a re-aim.
        /// </summary>
        [Fact]
        public void AWorkspaceMoveTreatsASupersededPaneAsStarted() => RunSta(() =>
        {
            var (vm, _) = Started();
            vm.SelectedProvider = Provider(vm, "claude-code");
            Drain();

            var moved = Path.Combine(_dir, "moved");
            Directory.CreateDirectory(moved);
            vm.UpdateWorkspaceRoot(moved, notice: null);
            DrainAll();

            Assert.Contains(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.Contains("Your next message starts a new session here", StringComparison.Ordinal));
        });

        /// <summary>
        /// Switching back restores the selection the session runs, so a model change after it applies in place, as it
        /// would have before the flip, rather than superseding the session again.
        /// </summary>
        [Fact]
        public void AfterSwitchingBackAModelChangeAppliesToTheLiveSession() => RunSta(() =>
        {
            var (vm, engine) = Started(liveModels: true);
            vm.SelectedProvider = Provider(vm, "claude-code");
            Drain();
            vm.SelectedProvider = Provider(vm, "kiro");
            Drain();
            var starts = engine.StartCount;

            vm.SelectedModel = vm.Models.Single(m => m.Id == "fast");
            Drain();

            Assert.Contains(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.StartsWith("Model switched to Fast", StringComparison.Ordinal));
            Assert.Equal(starts, engine.StartCount);
        });

        // ---- helpers --------------------------------------------------------------------------

        /// <summary>A pane on Kiro that has prompted once and had an answer.</summary>
        private (ChatViewModel Vm, ScriptedEngine Engine) Started(
            string model = "auto", bool steering = false, bool liveModels = false)
        {
            var engine = new ScriptedEngine { Turns = ScriptedEngine.TurnEnding.WhenCompleted, SupportsSteering = steering };
            var vm = new ChatViewModel(
                engine,
                new StartSessionRequest("kiro", null, _dir, "Prompt", null),
                sessionStore: new FileSessionStore(_dir));
            vm.Providers.Add(new ProviderItemViewModel(
                "kiro", "Kiro", new List<ModelItemViewModel> { new("auto", "auto"), new("fast", "Fast") },
                supportsResume: true, supportsModelSelection: liveModels));
            vm.Providers.Add(new ProviderItemViewModel(
                "claude-code", "Claude Code", new List<ModelItemViewModel> { new("opus", "Opus") }, supportsResume: true));
            vm.Providers.Add(new ProviderItemViewModel(
                "other", "Other", new List<ModelItemViewModel>(), supportsResume: true));

            vm.SelectedProvider = Provider(vm, "kiro");
            vm.SelectedModel = vm.Models.Single(m => m.Id == model);
            Drain();

            Prompt(vm, engine, "which fruit?");
            return (vm, engine);
        }

        private static ProviderItemViewModel Provider(ChatViewModel vm, string id) => vm.Providers.Single(p => p.Id == id);

        private static void Prompt(ChatViewModel vm, ScriptedEngine engine, string text)
        {
            vm.InputText = text;
            vm.SendCommand.Execute(null);
            Drain();
            engine.Raise(new AgentEventDto { Type = "text", Text = "the fruit was a mango" });
            Drain();
            engine.CompleteTurn();
            Drain();
        }

        private static List<NoticeItemViewModel> SwitchNotices(ChatViewModel vm) =>
            vm.Items.OfType<NoticeItemViewModel>()
                .Where(n => n.Text.StartsWith("Switched to", StringComparison.Ordinal))
                .ToList();

        private PersistedSession SavedConversation()
        {
            var store = new FileSessionStore(_dir);
            return store.Load(_dir, Assert.Single(store.List(_dir)).Id)!;
        }

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
