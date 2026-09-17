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
    /// A solution RELOAD must not cost the user their session.
    ///
    /// <para>VS reloads a solution whenever the .sln changes underneath it — which a git branch switch
    /// does — and a reload reaches us as a close followed by an open, with nothing on the wire saying
    /// the two are one gesture. The close alone reads as "no solution open", so the host transited the
    /// default workspace and acted on it: the running session was retired, the CLI listing emptied and
    /// a "Workspace changed to …" notice posted, all for a solution that never moved. The user's own
    /// words: <i>"even though the solution was actually the same one it started a new session."</i></para>
    ///
    /// <para><b>What these pin is the SAME root doing nothing</b> — the close parks, the open hands
    /// back the root we already hold, and the whole fix is that the second one is a no-op. So the
    /// assertions are all absences (no new session, no notice, no emptied listing), and an absence is
    /// only worth anything if the tests below it show the same code SPEAKING when the workspace
    /// genuinely moves. Hence the close and the different-solution cases beside the reload one.</para>
    ///
    /// <para><b>The second half of the class is the same path under a LIVE TURN (issue #217)</b>, which
    /// the first three cannot see: they build their state through <c>Started</c>, which completes the
    /// turn before every switch. A move made mid-turn retired the chat and left the turn running, so
    /// the agent went on working in the solution the user had left while its replies streamed into the
    /// clean transcript of the one they had opened. Those cases carry the same burden the reload ones
    /// do, in the same shape: the assertions are absences (nothing rendered, nothing recorded, no
    /// banner, no error notice), and <c>AReloadMidTurnStopsNothing</c> is what stops them being
    /// satisfiable by a host that has simply stopped listening.</para>
    /// </summary>
    public sealed class WorkspaceReloadTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "cwkt-tests", Guid.NewGuid().ToString("N"));

        private readonly string _solution;
        private readonly string _otherSolution;
        private readonly string _defaultWorkspace;

        public WorkspaceReloadTests()
        {
            _solution = Path.Combine(_root, "solution");
            _otherSolution = Path.Combine(_root, "other-solution");
            _defaultWorkspace = Path.Combine(_root, "default-workspace");
            Directory.CreateDirectory(_solution);
            Directory.CreateDirectory(_otherSolution);
            Directory.CreateDirectory(_defaultWorkspace);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }

        [Fact]
        public void AReloadOfTheSameSolutionKeepsTheSession() => RunSta(() =>
        {
            var engine = new StubEngine();
            engine.SessionsByRoot[_solution] = new[]
            {
                new BackendSessionDto("conv-cli", "Started in the terminal", DateTime.UtcNow, _solution),
            };

            var vm = Started(engine);
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();
            Assert.NotEmpty(vm.BackendSessions);
            Assert.Equal(new[] { _solution }, engine.RootsStarted.ToArray());

            // The reload, as VS delivers it: close, then the shell announcing an open, then the open
            // itself arriving at the root we never really left.
            vm.UpdateWorkspaceRoot(_defaultWorkspace, DefaultWorkspaceNotice, isDefaultWorkspace: true);
            vm.NoteSolutionOpening();
            vm.UpdateWorkspaceRoot(_solution, null);

            // Well past the settle delay, so a park that was merely late rather than cancelled still
            // gets the chance to fire and fail this.
            Pump(TimeSpan.FromMilliseconds(200));

            // The session is the whole point: pre-fix the close retired it and the open warm-started a
            // second one, which is the "it started a new session" in the report.
            Assert.Equal(new[] { _solution }, engine.RootsStarted.ToArray());

            // And the conversation is still on screen. Since a workspace move now CLEARS the transcript,
            // this is the sharpest statement of the whole fix: a reload is not a move, so the user's
            // chat is untouched rather than swapped for a clean one.
            Assert.Contains(
                vm.Items.OfType<MessageItemViewModel>(),
                m => m.Role == MessageRole.User && m.Text.Contains("hello"));

            Assert.DoesNotContain(vm.Items.OfType<NoticeItemViewModel>(), n => n.Text.Contains("Workspace changed"));
            Assert.Null(vm.WorkspaceNotice);
            Assert.NotEmpty(vm.BackendSessions);
        });

        [Fact]
        public void AGenuineCloseStillLandsOnTheDefaultWorkspace() => RunSta(() =>
        {
            // The other half of the park, and the reason it is a timer at all: nothing follows a real
            // File > Close Solution, so something has to release it.
            var engine = new StubEngine();
            var vm = Started(engine);

            vm.UpdateWorkspaceRoot(_defaultWorkspace, DefaultWorkspaceNotice, isDefaultWorkspace: true);
            Pump(TimeSpan.FromMilliseconds(300));

            Assert.Contains(vm.Items.OfType<NoticeItemViewModel>(), n => n.Text.Contains(_defaultWorkspace));
            Assert.Equal(DefaultWorkspaceNotice, vm.WorkspaceNotice);
            Assert.Equal(new[] { _solution, _defaultWorkspace }, engine.RootsStarted.ToArray());

            // The new session gets a CLEAN chat. Announced under the outgoing conversation it read as
            // that conversation carrying on into a workspace it has nothing to do with.
            Assert.Empty(vm.Items.OfType<MessageItemViewModel>());
        });

        [Fact]
        public void OpeningADifferentSolutionStillSwitches() => RunSta(() =>
        {
            // Closing A to open B transits the default workspace exactly as a reload does, so the park
            // swallows the same frames — and here the arriving root is genuinely new, which is what
            // separates "held the close" from "stopped noticing solutions".
            var engine = new StubEngine();
            engine.SessionsByRoot[_solution] = new[]
            {
                new BackendSessionDto("conv-cli", "Started in the terminal", DateTime.UtcNow, _solution),
            };

            var vm = Started(engine);
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();
            Assert.NotEmpty(vm.BackendSessions);

            vm.UpdateWorkspaceRoot(_defaultWorkspace, DefaultWorkspaceNotice, isDefaultWorkspace: true);
            vm.NoteSolutionOpening();
            vm.UpdateWorkspaceRoot(_otherSolution, null);
            Pump(TimeSpan.FromMilliseconds(200));

            // One notice, naming where the user ended up — never the folder they passed through.
            var notices = vm.Items.OfType<NoticeItemViewModel>()
                .Where(n => n.Text.Contains("Workspace changed")).ToList();
            Assert.Single(notices);
            Assert.Contains(_otherSolution, notices[0].Text);
            Assert.DoesNotContain(_defaultWorkspace, notices[0].Text);

            Assert.Empty(vm.BackendSessions);
            Assert.Equal(new[] { _solution, _otherSolution }, engine.RootsStarted.ToArray());

            // Clean chat, and the notice is the only thing in it: the conversation that was running
            // belongs to the solution we left, and it is already saved there.
            Assert.Empty(vm.Items.OfType<MessageItemViewModel>());
            Assert.Single(vm.Items);
        });

        // ---------------------------------------------------------------------------------------
        // Issue #217: the move retired the CHAT and left the TURN running.
        //
        // The user's words: "Changing the solution mid-turn clears the chat window and says it will
        // start a new session, but then the agent continues to run and the replies stream in to the
        // 'new session'." Everything the three cases above pin is the idle pane — Started() completes
        // the turn before every switch — so this whole class of state was untested rather than
        // mis-tested. What the turn kept doing meanwhile is the part that was not on screen: it went on
        // working in the solution the user had left, through IDE services that had ALREADY moved to the
        // one they opened (VsIdeServices repoints the workspace context and the edit applier on the
        // close/open), and nothing was ever going to stop it — the engine hosts one session at a time
        // and has no end-session call, so the only teardown is the next StartSessionAsync, which the
        // warm start could not reach past IsBusy.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void AMidTurnSwitchStopsTheTurn() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = MidTurn(engine);

            SwitchTo(vm, _otherSolution);

            Assert.Equal(1, engine.Cancels);
        });

        [Fact]
        public void TheRetiredTurnsOutputNeverReachesTheNewChat() => RunSta(() =>
        {
            // The backend does not stop on being asked, which is the case the guard exists for: a
            // cancel is best-effort, so nothing here may be conditioned on the agent having obeyed it.
            var engine = new StubEngine { CancelCompletesTurn = false };
            var vm = MidTurn(engine);

            SwitchTo(vm, _otherSolution);

            engine.Raise(new AgentEventDto { Type = "text", Text = "still working in the old solution" });
            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "t1", Title = "Read", Kind = "read" });
            Pump();

            // The notice, and nothing else. This is the report itself: a reply from the previous
            // solution's turn drawn in the new solution's chat.
            Assert.Single(vm.Items);
            Assert.Empty(vm.Items.OfType<MessageItemViewModel>());
            Assert.Empty(vm.Items.OfType<ToolItemViewModel>());
        });

        [Fact]
        public void TheRetiredTurnsPermissionRequestIsCancelledAndNeverBanners() => RunSta(() =>
        {
            var engine = new StubEngine { CancelCompletesTurn = false };
            var vm = MidTurn(engine);

            SwitchTo(vm, _otherSolution);

            var decision = vm.RequestPermissionAsync(new PermissionRequestDto(
                "t1", "Write Program.cs", "edit", null, null,
                new[] { new PermissionOptionDto("allow", "Allow", "allow_once") }));
            Pump();

            // Answered, so the backend unblocks rather than hanging — and answered the way a Stop
            // answers one. Shown, it would ask the user to approve a write into the solution they just
            // left, in the chat of the one they just opened, with that session's remembered grants
            // already reset out from under it.
            Assert.True(decision.IsCompleted);
            Assert.True(decision.Result.Cancelled);
            Assert.Null(vm.PendingPermission);
        });

        [Fact]
        public void TheRetiredTurnsFailureIsNotReportedInTheNewChat() => RunSta(() =>
        {
            var engine = new StubEngine { CancelCompletesTurn = false };
            var vm = MidTurn(engine);

            SwitchTo(vm, _otherSolution);

            // The commonest failure on this path is our own doing: warm-starting the new root disposes
            // the engine's only session, which faults the prompt still outstanding on the old one.
            engine.FailTurn(new InvalidOperationException("session disposed"));
            Pump();

            Assert.DoesNotContain(vm.Items.OfType<NoticeItemViewModel>(), n => n.Text.Contains("Engine error"));
            Assert.Single(vm.Items);
        });

        [Fact]
        public void TheNewRootIsWarmStartedOnceTheRetiredTurnReturns() => RunSta(() =>
        {
            var engine = new StubEngine { CancelCompletesTurn = false };
            var vm = MidTurn(engine);

            SwitchTo(vm, _otherSolution);

            // Not before: the engine tears the old session down inside StartSessionAsync, and doing that
            // under a prompt still on the wire is what turns a tidy retirement into an engine error.
            Assert.Equal(new[] { _solution }, engine.RootsStarted.ToArray());

            engine.CompleteTurn();
            Pump();

            // And the turn returning is the signal that releases it — which is also the only thing that
            // GUARANTEES the retired session is gone, the cancel being best-effort.
            Assert.Equal(new[] { _solution, _otherSolution }, engine.RootsStarted.ToArray());
        });

        [Fact]
        public void TheNoticeNamesTheTurnItStopped() => RunSta(() =>
        {
            var engine = new StubEngine { CancelCompletesTurn = false };
            var vm = MidTurn(engine);

            SwitchTo(vm, _otherSolution);

            // The output stops arriving at the moment this notice appears. Unexplained, a stream that
            // simply ceases reads as the agent having hung — and the opposite reading, that the work it
            // had already done was undone, is just as wrong. So the sentence closes both.
            var notice = Assert.Single(vm.Items.OfType<NoticeItemViewModel>());
            Assert.Contains(_otherSolution, notice.Text);
            Assert.Contains(_solution, notice.Text);
            Assert.Contains("was stopped", notice.Text);
            Assert.Contains("still changed", notice.Text);
        });

        [Fact]
        public void AReloadMidTurnStopsNothing() => RunSta(() =>
        {
            // The counterexample, and the reason the absences above are worth anything. A branch switch
            // reloads the .sln mid-turn — an ordinary git checkout while the agent works — and the root
            // it lands back on has not moved, so the early return fires ahead of every one of the
            // behaviours the five cases above pin.
            var engine = new StubEngine { CancelCompletesTurn = false };
            var vm = MidTurn(engine);

            vm.UpdateWorkspaceRoot(_defaultWorkspace, DefaultWorkspaceNotice, isDefaultWorkspace: true);
            vm.NoteSolutionOpening();
            vm.UpdateWorkspaceRoot(_solution, null);
            Pump(TimeSpan.FromMilliseconds(200));

            Assert.Equal(0, engine.Cancels);
            Assert.DoesNotContain(vm.Items.OfType<NoticeItemViewModel>(), n => n.Text.Contains("Workspace changed"));

            // Still the same turn, still this conversation's: its output goes on arriving.
            engine.Raise(new AgentEventDto { Type = "text", Text = "carrying on" });
            Pump();
            Assert.Contains(
                vm.Items.OfType<MessageItemViewModel>(),
                m => m.Role == MessageRole.Assistant && m.Text.Contains("carrying on"));
        });

        /// <summary>
        /// A view-model with a turn actually ON THE WIRE — <see cref="Started"/> without the
        /// <c>CompleteTurn</c>. The session is adopted by then (that happens inside the send, before the
        /// prompt), so this is the state the report describes.
        /// </summary>
        private ChatViewModel MidTurn(StubEngine engine)
        {
            var vm = NewViewModel(engine);
            vm.InputText = "hello";
            vm.SendCommand.Execute(null);
            Pump();
            Assert.True(vm.IsBusy);
            return vm;
        }

        /// <summary>A solution switch as VS delivers it: close, the shell announcing an open, the open.</summary>
        private void SwitchTo(ChatViewModel vm, string root)
        {
            vm.UpdateWorkspaceRoot(_defaultWorkspace, DefaultWorkspaceNotice, isDefaultWorkspace: true);
            vm.NoteSolutionOpening();
            vm.UpdateWorkspaceRoot(root, null);
            Pump(TimeSpan.FromMilliseconds(200));
        }

        private const string DefaultWorkspaceNotice = "No solution open — using the default workspace.";

        /// <summary>
        /// A view-model with a session the user has actually STARTED, which is the state the report is
        /// about: a warm start alone does not adopt a session, so the retire-and-restart path these
        /// tests exist for is unreachable until a message has been sent.
        /// </summary>
        private ChatViewModel Started(StubEngine engine)
        {
            var vm = NewViewModel(engine);
            vm.InputText = "hello";
            vm.SendCommand.Execute(null);
            Pump();
            engine.CompleteTurn();
            Pump();
            return vm;
        }

        private ChatViewModel NewViewModel(StubEngine engine)
        {
            var vm = new ChatViewModel(
                engine,
                new StartSessionRequest("fake", null, _solution, "Prompt", null),
                sessionStore: new FileSessionStore(_root))
            {
                // The park only has to outlive close → NoteSolutionOpening, which in these tests is the
                // very next statement. Shortened so a check need not spend a second of wall clock.
                WorkspaceSettleDelay = TimeSpan.FromMilliseconds(20),
            };
            vm.InitializeAsync().GetAwaiter().GetResult();
            return vm;
        }

        /// <summary>
        /// Runs the dispatcher for <paramref name="duration"/>. Both the notice and the park land at
        /// <see cref="DispatcherPriority.Background"/>, and the park is a timer, so a single drain would
        /// prove only that nothing had happened YET — which is what a broken park looks like too.
        /// </summary>
        private static void Pump(TimeSpan? duration = null)
        {
            var until = DateTime.UtcNow + (duration ?? TimeSpan.FromMilliseconds(50));
            do
            {
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
                Thread.Sleep(5);
            }
            while (DateTime.UtcNow < until);
        }

        // One shared, GATED implementation - see StaTest. The dispatcher context is needed because the
        // warm start awaits with ConfigureAwait(true): without it the continuation resumes on the pool
        // and the pump above never sees the session open.
        private static void RunSta(Action action) => StaTest.Run(action, withDispatcherContext: true);

        /// <summary>An engine whose only jobs are to start sessions and to list what a CLI holds.</summary>
        private sealed class StubEngine : IEngineConnection
        {
            private TaskCompletionSource<PromptResponse>? _turn;

            public event Action<AgentEventDto>? AgentEvent;

            public event Action<ProviderModelsDto>? ProviderModelsRefreshed { add { } remove { } }

            /// <summary>How many times the host asked the backend to stop.</summary>
            public int Cancels { get; private set; }

            /// <summary>
            /// Whether <see cref="CancelAsync"/> ends the turn on the spot. True is the tidy backend;
            /// false is the one issue #217's guard is written for — <c>session/cancel</c> is
            /// best-effort, so a turn that keeps emitting after being asked to stop is a state the host
            /// has to survive rather than an unrealistic one.
            /// </summary>
            public bool CancelCompletesTurn { get; set; } = true;

            /// <summary>An event arriving on the wire from whatever turn the engine last had open.</summary>
            public void Raise(AgentEventDto ev) => AgentEvent?.Invoke(ev);

            /// <summary>Faults the open turn — what the shell sees when the engine disposes the session
            /// its prompt was riding, which is exactly what starting the next one does.</summary>
            public void FailTurn(Exception error)
            {
                var turn = _turn;
                _turn = null;
                turn?.TrySetException(error);
            }

            /// <summary>Every workspace root a session was started in, in order. A retired session and
            /// its replacement are otherwise indistinguishable from one session left alone.</summary>
            public List<string> RootsStarted { get; } = new();

            public Dictionary<string, IReadOnlyList<BackendSessionDto>> SessionsByRoot { get; } =
                new(StringComparer.OrdinalIgnoreCase);

            // A fake with no handshake reports no session, which the panel renders as
            // "no agent session open yet" rather than as absent facts (issue #160).
            public Task<SessionInfoResponse?> SessionInfoAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<SessionInfoResponse?>(null);

            public Task<ListProvidersResponse> ListProvidersAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new ListProvidersResponse(new List<ProviderInfoDto>
                {
                    new ProviderInfoDto("fake", "Fake", new List<ModelInfoDto>(), new List<string>()),
                }));

            public Task<StartSessionResponse> StartSessionAsync(
                StartSessionRequest request, CancellationToken cancellationToken = default)
            {
                RootsStarted.Add(request.WorkspaceRootPath ?? string.Empty);
                return Task.FromResult(new StartSessionResponse(request.ResumeConversationId ?? "fresh"));
            }

            public Task<ListBackendSessionsResponse> ListBackendSessionsAsync(
                ListBackendSessionsRequest request, CancellationToken cancellationToken = default)
            {
                var sessions = SessionsByRoot.TryGetValue(request.WorkspaceRootPath ?? string.Empty, out var forRoot)
                    ? forRoot
                    : Array.Empty<BackendSessionDto>();
                return Task.FromResult(new ListBackendSessionsResponse(true, null, sessions));
            }

            public Task<TakeImportedHistoryResponse> TakeImportedHistoryAsync(
                TakeImportedHistoryRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("These tests never import.");

            /// <summary>Ends the turn the last prompt opened, so the pane is idle again.</summary>
            public void CompleteTurn()
            {
                var turn = _turn;
                _turn = null;
                turn?.TrySetResult(new PromptResponse("end_turn"));
            }

            public Task<PromptResponse> PromptAsync(
                string text, IReadOnlyList<PromptAttachmentDto>? attachments = null,
                CancellationToken cancellationToken = default)
            {
                _turn = new TaskCompletionSource<PromptResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                return _turn.Task;
            }

            public Task CancelAsync(CancellationToken cancellationToken = default)
            {
                Cancels++;
                if (CancelCompletesTurn)
                    CompleteTurn();
                return Task.CompletedTask;
            }

            public Task<SteerResponse> SteerAsync(
                string text, IReadOnlyList<PromptAttachmentDto>? attachments = null,
                CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("These tests never steer.");

            public Task SetModelAsync(string modelId, CancellationToken cancellationToken = default)
                => Task.CompletedTask;

            public Task<SummarizeResponse> SummarizeAsync(
                SummarizeRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("These tests never summarize.");
        }
    }
}
