using System;
using System.IO;
using System.Collections.Generic;
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
    /// What a conversation change does to permission requests that are already open. The rule is the session, not the transcript: where the session the request came from lives
    /// on, the request is KEPT and re-labelled with whose it is; where the session is disposed, it is
    /// cancelled, because nothing is left to answer.
    /// </summary>
    /// <remarks>
    /// <para>Both clears used to cancel everything queued, on the reasoning that the row the banner
    /// outlines is destroyed and Allow would authorise work for the conversation just left. The first
    /// half is true and is dealt with by dropping the row correlation; the second is issue #256's
    /// territory, whose rule is <b>asked and labelled, never cancelled</b> - and cancelling leaves the
    /// backend's blocked <c>request_permission</c> answered "no" for work the user launched and can
    /// still see the result of.</para>
    /// <para><b>A history open can only reach this state out of turn</b>, because it is refused while the
    /// pane is busy: the request open at the swap belongs to steered or background work of the live
    /// session, which is exactly the case #256 was written for.</para>
    /// </remarks>
    public sealed class OpenPermissionRequestsTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "cwkt-tests", Guid.NewGuid().ToString("N"));

        public OpenPermissionRequestsTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }

        [Fact]
        public void AHistoryOpenKeepsOpenRequestsAndSaysWhoseTheyAre() => RunSta(() =>
        {
            var store = new FileSessionStore(_root);
            var engine = HeldTurnEngine();
            var vm = NewViewModel(engine, store);
            var (older, live) = TwoConversations(vm, engine, store);
            var liveTitle = store.Load(_root, live)!.Title;

            var decision = Ask(vm);
            var banner = Assert.IsType<PermissionBannerViewModel>(vm.PendingPermission);
            Assert.False(banner.HasOrigin, "it is the conversation on screen asking, so there is nothing to name");

            Open(vm, older);

            Assert.False(decision.IsCompleted, "the request was answered for the user by a history open");
            Assert.Same(banner, vm.PendingPermission);
            Assert.True(banner.HasOrigin);
            Assert.Equal(PermissionBannerViewModel.OriginSentence(liveTitle), banner.Origin);
            // The row it outlined belongs to the transcript that has just been replaced, so the banner
            // stands on its own - which is what F17 already asks of an off-screen request.
            Assert.Equal(string.Empty, banner.ToolCallId);
            Assert.Null(vm.HighlightedItem);
        });

        [Fact]
        public void ADeleteOfTheConversationOnScreenKeepsThemAndSaysItIsDeleted() => RunSta(() =>
        {
            var store = new FileSessionStore(_root);
            var engine = HeldTurnEngine();
            var vm = NewViewModel(engine, store);
            var (_, live) = TwoConversations(vm, engine, store);
            var liveTitle = store.Load(_root, live)!.Title;

            var decision = Ask(vm);
            var banner = Assert.IsType<PermissionBannerViewModel>(vm.PendingPermission);
            var startsBefore = engine.StartCount;

            vm.RefreshHistory();
            vm.History.Single(h => h.Id == live).DeleteCommand.Execute(null);
            Drain();

            Assert.False(decision.IsCompleted, "the request was answered for the user by a delete");
            Assert.Same(banner, vm.PendingPermission);
            Assert.Equal(PermissionBannerViewModel.DeletedOriginSentence(liveTitle), banner.Origin);

            // And it is still the SAME session holding it. The delete clears the pane back to a fresh
            // conversation, which warm-starts - and a warm start disposes the live session, which is the
            // one thing that must not happen in the gesture that just promised to keep its requests.
            Assert.Equal(startsBefore, engine.StartCount);
        });

        /// <summary>
        /// New disposes the session, so there is nothing left to answer and the backend is unblocked by
        /// the cancel rather than left waiting on a banner nobody can honour.
        /// </summary>
        [Fact]
        public void NewCancelsThemBecauseTheSessionGoes() => RunSta(() =>
        {
            var store = new FileSessionStore(_root);
            var engine = HeldTurnEngine();
            var vm = NewViewModel(engine, store);
            TwoConversations(vm, engine, store);

            var decision = Ask(vm);
            vm.NewSessionCommand.Execute(null);
            Drain();

            Assert.True(decision.IsCompleted);
            Assert.True(decision.Result.Cancelled);
            Assert.Null(vm.PendingPermission);
        });

        /// <summary>
        /// Stop aborts THIS conversation's work and nothing else (issue #256). A request kept from the
        /// conversation the user left is not this one's to answer, and cancelling it is damage where the
        /// person pressing Stop is not looking.
        /// </summary>
        [Fact]
        public void StopDoesNotAnswerAnotherConversationsRequest() => RunSta(() =>
        {
            var store = new FileSessionStore(_root);
            var engine = HeldTurnEngine();
            var vm = NewViewModel(engine, store);
            var (older, _) = TwoConversations(vm, engine, store);

            var decision = Ask(vm);
            Open(vm, older);
            Assert.False(decision.IsCompleted, "it was already cancelled, so this proves nothing");

            vm.StopCommand.Execute(null);
            Drain();

            Assert.False(decision.IsCompleted, "Stop answered a request belonging to another conversation");
            Assert.NotNull(vm.PendingPermission);
        });

        /// <summary>
        /// Reopening the owner takes its request BACK as its own. The label is a projection of "whose is
        /// this", and that resolver can answer NOBODY as well as a name - so a re-attach, which is the
        /// conversation coming back to the screen, has to be able to clear it.
        /// </summary>
        /// <remarks>
        /// A one-way flag cannot express that, and the cost is not cosmetic: the banner goes on saying
        /// the reply is recorded somewhere else while the user is reading the very conversation it
        /// belongs to, and Stop - scoped to this conversation's requests - skips it. That leaves exactly
        /// the state the cancel-on-Stop exists to prevent, where Allow answers a turn that no longer
        /// exists, and it is a regression against the behaviour before the keep, where the swap
        /// cancelled the request outright.
        /// </remarks>
        [Fact]
        public void ReopeningTheOwnerTakesItsRequestBackAsItsOwn() => RunSta(() =>
        {
            var store = new FileSessionStore(_root);
            var engine = HeldTurnEngine();
            var vm = NewViewModel(engine, store);
            var (older, live) = TwoConversations(vm, engine, store);

            var decision = Ask(vm);
            var banner = Assert.IsType<PermissionBannerViewModel>(vm.PendingPermission);

            Open(vm, older);
            Assert.True(banner.HasOrigin, "it was not re-labelled, so this proves nothing");

            Open(vm, live);   // a re-attach: the backend never dropped this session

            Assert.False(decision.IsCompleted);
            Assert.Same(banner, vm.PendingPermission);
            Assert.False(banner.HasOrigin, "the banner still says the reply goes somewhere else");

            // And it is this conversation's again, so Stop answers it.
            vm.StopCommand.Execute(null);
            Drain();
            Assert.True(decision.IsCompleted, "Stop left this conversation's own banner standing");
            Assert.True(decision.Result.Cancelled);
        });

        /// <summary>
        /// And the other side of the rule: once the pane starts a session of its own, the session the
        /// kept request came from IS disposed, so it is cancelled rather than left on screen as a banner
        /// no backend can hear the answer to.
        /// </summary>
        [Fact]
        public void AStartThatDisposesTheSessionCancelsWhatItLeftUnanswered() => RunSta(() =>
        {
            var store = new FileSessionStore(_root);
            var engine = HeldTurnEngine();
            var vm = NewViewModel(engine, store);
            var (older, _) = TwoConversations(vm, engine, store);

            var decision = Ask(vm);
            Open(vm, older);
            Assert.False(decision.IsCompleted, "it was already cancelled, so this proves nothing");

            // Sending in the conversation just opened starts a session, which replaces the one the kept
            // request belongs to.
            vm.InputText = "carry on here";
            vm.SendCommand.Execute(null);
            Drain();

            Assert.True(decision.IsCompleted, "a banner was left over a session the engine no longer holds");
            Assert.True(decision.Result.Cancelled);
            Assert.Null(vm.PendingPermission);
        });

        private static System.Threading.Tasks.Task<PermissionDecisionDto> Ask(ChatViewModel vm)
        {
            var decision = vm.RequestPermissionAsync(new PermissionRequestDto(
                "t1", "Write Program.cs", "edit", null, null,
                new[] { new PermissionOptionDto("allow", "Allow", "allow_once") }));
            Drain();
            return decision;
        }

        private (string older, string live) TwoConversations(ChatViewModel vm, ScriptedEngine engine, FileSessionStore store)
        {
            Prompt(vm, engine, "analyse the solution's projects");
            var older = vm.History.Single(h => h.IsCurrent).Id;

            vm.NewSessionCommand.Execute(null);
            Drain();
            Prompt(vm, engine, "run a background task that sleeps for 30 seconds");
            var live = vm.History.Single(h => h.IsCurrent).Id;

            Assert.NotEqual(older, live);
            return (older, live);
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

        // INITIALIZED, deliberately. Without it Providers is empty, SelectedProvider is null, and
        // WarmStartSession returns at its first line - which silently excludes every warm-start
        // consequence from these tests, and a warm start is what disposes a session.
        private ChatViewModel NewViewModel(ScriptedEngine engine, FileSessionStore store)
        {
            var vm = new ChatViewModel(
                engine,
                new StartSessionRequest("fake", null, _root, "Prompt", null),
                sessionStore: store);
            var init = vm.InitializeAsync(Task.FromResult(new ListProvidersResponse(new List<ProviderInfoDto>
            {
                new("fake", "Fake", new List<ModelInfoDto> { new("auto", "auto") }, new List<string> { "ResumeSession" }),
            })));
            Drain();
            Assert.True(init.IsCompletedSuccessfully);
            Assert.NotNull(vm.SelectedProvider);
            return vm;
        }

        private static void Drain() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        // Turns stay open until the test ends them: an out-of-turn request is the case throughout. A
        // provider is offered because the pane's warm start returns at its first line without one - so
        // an engine with no provider quietly excludes every warm-start consequence from these tests.
        private static ScriptedEngine HeldTurnEngine() =>
            new ScriptedEngine { Turns = ScriptedEngine.TurnEnding.WhenCompleted }
                .WithProvider("fake", "Fake");

        private static void RunSta(Action action) => StaTest.Run(action, withDispatcherContext: true);
    }
}
