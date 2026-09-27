using System;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using CodeWicket.Core;
using CodeWicket.Ipc;
using CodeWicket.Shell;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// A tray release is a send like any other, so it decides how a reopened conversation reconnects
    /// and carries its framing through whatever that decision asks about.
    /// </summary>
    /// <remarks>
    /// <para>The defect these close is one route short of the four that must decide: Enter, the start-over resend and a
    /// banner's answer all ran <c>ResumeDecider</c>, and a release went straight to the turn runner. On
    /// its own that is a send taking no decision; combined with a resume strategy that outlived the send
    /// it was chosen for, it was a held batch silently reloading a whole conversation nobody had been
    /// asked about.</para>
    /// <para>The framing half is the same change rather than a follow-on. A release is the only send
    /// that HAS framing, so until one could park there was nothing for a park to drop, and the moment a
    /// release decides it can park on either of the two banners ahead of the turn runner.</para>
    /// </remarks>
    public sealed class TrayReleaseDecidesTests : IDisposable
    {
        private const string Refusal = "Session not found: sess_old";

        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "cwkt-release-decides-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch (IOException) { }
        }

        /// <summary>
        /// The live route, in the order it was reproduced: the moved-root banner is cancelled, and
        /// the held batch is then released with Send now. The release used to enter below the resume
        /// decision and take the full reload the cancelled send had been asked about — a whole
        /// conversation reloaded into the agent, counting toward usage, with no banner and no notice.
        /// </summary>
        /// <remarks>
        /// <b>Both halves are asserted, because either alone is satisfiable by accident.</b> That a
        /// banner is up says the release decided; that no start asked to resume a conversation says the
        /// reload did not happen anyway while the banner was being drawn.
        /// </remarks>
        [Fact]
        public void AReleaseDecidesRatherThanTakingTheReloadACancelLeftBehind() => RunSta(() =>
        {
            var engine = Engine();
            engine.AgentRoot = Path.Combine(_dir, "elsewhere");
            var vm = Resumable(engine, conversationRoot: _dir);

            vm.InputText = "what changed?";
            vm.SendCommand.Execute(null);
            Drain();
            vm.InputText = "and the tests?";
            vm.SendCommand.Execute(null); // a send is pending, so this is held
            Drain();
            vm.PendingResume!.ResumeFullCommand.Execute(null);
            Drain();
            Assert.Equal("This conversation ran in a different directory", vm.PendingResume!.Heading);
            vm.PendingResume.CancelCommand.Execute(null);
            Drain();

            vm.SendPendingNowCommand.Execute(null);
            DrainAll();

            Assert.Equal("Continue this conversation?", vm.PendingResume!.Heading);
            Assert.DoesNotContain(engine.Starts, s => s.ResumeConversationId is not null);
        });

        /// <summary>
        /// The other half, on the Enter route where it can be seen on its own: the moved-root
        /// banner's Cancel resets the phase, the banner, the tray and the composer, and used to leave
        /// the full reload standing. The next send is a fresh decision, as it is after every other
        /// back-out (<c>BackOutOfResume</c>'s rule, one step earlier).
        /// </summary>
        /// <remarks>
        /// The two states are distinguishable by WHICH banner the second send lands on. With the
        /// strategy left standing the decider is skipped — it is no longer the first continuation — and
        /// the send goes straight back to the moved-root question it had just walked away from.
        /// </remarks>
        [Fact]
        public void CancellingTheMovedRootBannerForgetsTheReloadItWasAskedAbout() => RunSta(() =>
        {
            var engine = Engine();
            engine.AgentRoot = Path.Combine(_dir, "elsewhere");
            var vm = Resumable(engine, conversationRoot: _dir);

            vm.InputText = "what changed?";
            vm.SendCommand.Execute(null);
            Drain();
            vm.PendingResume!.ResumeFullCommand.Execute(null);
            Drain();
            Assert.Equal("This conversation ran in a different directory", vm.PendingResume!.Heading);
            vm.PendingResume.CancelCommand.Execute(null);
            Drain();

            // The message came back; sending it again asks the whole question again.
            Assert.Equal("what changed?", vm.InputText);
            vm.SendCommand.Execute(null);
            Drain();

            Assert.Equal("Continue this conversation?", vm.PendingResume!.Heading);
        });

        /// <summary>
        /// A released batch reaches its prompt with the framing its gesture gave it, however many
        /// banners stood between. The framing lived in <c>DeliverPendingAsync</c>'s call to the turn
        /// runner, and the park had nowhere to put it: a bare string field held the text alone, so a
        /// send that parked went out as though it had been typed at that moment.
        /// </summary>
        /// <remarks>
        /// Asserted on the wire AND on the bubble, for the reason the start-over resend's own check gives: they are two
        /// different losses. The agent is not told its work was cut short, and the user is shown a
        /// message with no note saying how it was sent.
        /// </remarks>
        [Fact]
        public void AReleasedBatchKeepsItsFramingAcrossTheBannerItParksOn() => RunSta(() =>
        {
            var engine = Engine();
            var vm = Resumable(engine);

            vm.InputText = "what changed?";
            vm.SendCommand.Execute(null);
            Drain();
            vm.InputText = "and the tests?";
            vm.SendCommand.Execute(null); // held behind the pending send
            Drain();
            vm.PendingResume!.CancelCommand.Execute(null);
            Drain();

            vm.SendPendingNowCommand.Execute(null);
            Drain();
            Assert.Equal("Continue this conversation?", vm.PendingResume!.Heading);
            // The box still holds what the cancel gave back: a release is not the composer's message, so
            // parking one must not empty it. This batch carries NO chips, which is the case the rule
            // could get wrong - whether the composer still holds a message is STATED by the factory
            // that made it, and a factory working it out from the chips instead reads a chipless
            // release as the composer's.
            Assert.Equal("what changed?", vm.InputText);
            vm.PendingResume.ResumeFullCommand.Execute(null);
            DrainAll();

            var prompt = Assert.Single(engine.Prompts);
            Assert.Contains(HostPromptBlocks.MidTurnMessage.Open, prompt, StringComparison.Ordinal);
            Assert.Equal("and the tests?", LastLine(prompt));
            var shown = Assert.Single(
                vm.Items.OfType<MessageItemViewModel>(), m => m.Text == "and the tests?");
            Assert.NotNull(shown.DeliveryNote);
        });

        /// <summary>
        /// Backing out of the banner a released batch parked on gives the whole message back — its
        /// pictures and its IDE capture included. A released batch's chips are NOT in the composer while
        /// it waits, unlike a typed one's: the tray took them when the message was held, so the parked
        /// send is the only thing holding them and dropping it drops them.
        /// </summary>
        /// <remarks>
        /// <b>And what was typed since survives</b>, which is the give-back's one rule for where text
        /// lands: the released text goes AHEAD of the later thought rather than over it.
        /// </remarks>
        [Fact]
        public void BackingOutOfAReleasedBatchesBannerGivesBackItsChipsAndKeepsTheDraft() => RunSta(() =>
        {
            var engine = Engine();
            var vm = Resumable(engine);

            vm.InputText = "what changed?";
            vm.SendCommand.Execute(null);
            Drain();
            vm.PendingAttachments.Add(new AttachmentViewModel(
                "snip.png", "image/png", new byte[] { 1, 2, 3 }, filePath: null, remove: _ => { }));
            vm.PendingContexts.Add(new ContextItemViewModel(
                "debug-state", "Break state", "#1 Recurse  Foo.cs:33"));
            vm.InputText = "and the tests?";
            vm.SendCommand.Execute(null); // held, with its chips
            Drain();
            Assert.Empty(vm.PendingAttachments);
            vm.PendingResume!.CancelCommand.Execute(null);
            Drain();
            vm.InputText = string.Empty;

            vm.SendPendingNowCommand.Execute(null);
            Drain();
            Assert.Equal("Continue this conversation?", vm.PendingResume!.Heading);
            // Typed while the banner was up: a later thought, which the give-back must not overwrite.
            vm.InputText = "actually, wait";
            vm.PendingResume.CancelCommand.Execute(null);
            Drain();

            Assert.Equal("and the tests?" + Environment.NewLine + "actually, wait", vm.InputText);
            Assert.Equal("snip.png", Assert.Single(vm.PendingAttachments).Name);
            Assert.Equal("Break state", Assert.Single(vm.PendingContexts).Label);
            Assert.Empty(engine.Prompts);
        });

        // Resumes and refuses nothing: these are about the decision, not about what the backend does
        // with it. RefuseResume belongs to StartOverTests, which needs the refusal banner.
        private static ScriptedEngine Engine() =>
            new ScriptedEngine { AllowSummarize = true, RefusalReason = Refusal }
                .WithProvider("fake", "Fake", "ResumeSession");

        // A restored conversation big enough that the send-time banner offers the full-vs-summary
        // choice (ResumeDecider's threshold is 4000 chars of transcript).
        private ChatViewModel Resumable(ScriptedEngine engine, string? conversationRoot = null)
        {
            var store = new FileSessionStore(_dir);
            var session = new PersistedSession
            {
                WorkspaceRootPath = _dir,
                AgentWorkingDirectory = conversationRoot,
                ConversationId = "conv-1",
                ProviderId = "fake",
                Title = "Earlier work",
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

        private static string LastLine(string prompt)
        {
            var lines = prompt.Split('\n');
            return lines[lines.Length - 1].Trim();
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
