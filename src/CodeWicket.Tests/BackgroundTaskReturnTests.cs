using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CodeWicket.Core;
using CodeWicket.Ipc;
using CodeWicket.Providers.Acp;
using CodeWicket.Shell;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// A background sub-agent row stops saying "running" once its task has come back (issue #125).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Reported from the field as "they always say running and never seem to finish".</b> Two things
    /// were true at once: <c>SettleOpenToolRows</c> only ever touched <c>ToolStatus.Running</c>, so a
    /// <c>Launched</c> row was never swept when its turn ended; and the one signal that a background task
    /// had returned was never mapped at all, so nothing in the running system could learn it.
    /// </para>
    /// <para>
    /// <b>Settling launched rows when the turn ends is wrong, and the captures show why.</b> In a
    /// captured session (claude-agent-acp 0.70.0, 2026-08-22) a turn ended with two of three returned and
    /// the third arriving AFTERWARDS, and another capture opened with three returns belonging to the
    /// previous turn's launches. A background task outlives its turn, so settling at
    /// turn end trades "never finishes" for "claims to have finished while still running" — the worse of
    /// the two errors, because the first only withholds information and the second asserts something
    /// untrue.
    /// </para>
    /// <para>
    /// <b>What is sound is a COUNT.</b> Attribution is impossible: the notification names no agent, the
    /// launch-side id never reappears, and the order does not match (launched 1,2,3, returned 1,3,2), so
    /// settling a row per notification mislabels two rows of three. But once the returns have caught up
    /// with the launches, every outstanding task has come back — which is true of each row individually
    /// rather than guessed for it. Until then every row keeps saying "running", which is honest about the
    /// weaker thing that is known: at least one is still going, and we cannot say which.
    /// </para>
    /// </remarks>
    public class BackgroundTaskReturnTests
    {
        // Verbatim from a captured frame (claude-agent-acp 0.70.0, 2026-08-22). The origin key contains a slash, which is why
        // the mapper reads it through the indexer rather than as a property name.
        private const string TaskNotificationFrame = """
            {"sessionUpdate":"usage_update","used":35988,"size":1000000,
             "cost":{"amount":1.4692112499999994,"currency":"USD"},
             "_meta":{"_claude/origin":{"kind":"task-notification"}}}
            """;

        private const string OrdinaryUsageFrame = """
            {"sessionUpdate":"usage_update","used":35988,"size":1000000}
            """;

        // ------------------------------------------------------------------------------------------
        // The wire
        // ------------------------------------------------------------------------------------------

        [Fact]
        public void TheCapturedFrameMapsToAReturn()
        {
            var events = Map(TaskNotificationFrame);

            Assert.Contains(events, e => e is AgentEvent.BackgroundTaskReturned);
            // The same frame carries real consumption figures, so reading it as a notification must not
            // cost the usage ring its update.
            Assert.Contains(events, e => e is AgentEvent.UsageUpdated);
        }

        /// <summary>
        /// An ordinary usage frame is NOT a return. Usage streams repeatedly through every turn, so a
        /// loose match would settle rows whose sub-agents are still working — the exact error the whole
        /// mechanism exists to avoid.
        /// </summary>
        [Fact]
        public void AnOrdinaryUsageFrameIsNotAReturn()
        {
            var events = Map(OrdinaryUsageFrame);

            Assert.DoesNotContain(events, e => e is AgentEvent.BackgroundTaskReturned);
            Assert.Contains(events, e => e is AgentEvent.UsageUpdated);
        }

        // ------------------------------------------------------------------------------------------
        // The transcript
        // ------------------------------------------------------------------------------------------

        [Fact]
        public void ALaunchedRowSaysRunningUntilItsTaskComesBack() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);
            var row = Launch(engine, vm, "t1");

            Assert.Equal("running", row.StatusNote);

            Returned(engine);

            Assert.Equal("finished", row.StatusNote);
            Assert.True(row.LaunchReturned);
        });

        /// <summary>
        /// The measured case: three launches and three returns match in number but not in order, so no
        /// row may settle until the count reaches zero.
        /// </summary>
        [Fact]
        public void NoRowSettlesUntilEveryTaskHasReturned() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);
            var rows = new[] { Launch(engine, vm, "t1"), Launch(engine, vm, "t2"), Launch(engine, vm, "t3") };

            Returned(engine);
            Assert.All(rows, r => Assert.Equal("running", r.StatusNote));

            Returned(engine);
            Assert.All(rows, r => Assert.Equal("running", r.StatusNote));

            Returned(engine);
            Assert.All(rows, r => Assert.Equal("finished", r.StatusNote));
        });

        /// <summary>
        /// The case that killed the simple fix. A turn ended with two of three returned and took the
        /// third afterwards, so the turn ending must neither settle the rows nor forget the count.
        /// </summary>
        [Fact]
        public void ATurnEndingNeitherSettlesALaunchedRowNorForgetsIt() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);
            var rows = new[] { Launch(engine, vm, "t1"), Launch(engine, vm, "t2"), Launch(engine, vm, "t3") };

            Returned(engine);
            Returned(engine);
            engine.Raise(new AgentEventDto { Type = "turnDone" });
            DrainDispatcher();

            Assert.All(rows, r => Assert.Equal("running", r.StatusNote));

            Returned(engine);   // arrives after the turn, exactly as captured

            Assert.All(rows, r => Assert.Equal("finished", r.StatusNote));
        });

        /// <summary>
        /// A launched row is not a stuck one. The end-of-turn sweep marks unsettled rows FAILED, and
        /// doing that to a background call would report a working sub-agent as an error.
        /// </summary>
        [Fact]
        public void TheEndOfTurnSweepDoesNotFailALaunchedRow() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);
            var row = Launch(engine, vm, "t1");

            engine.Raise(new AgentEventDto { Type = "turnDone", StopReason = "cancelled" });
            DrainDispatcher();

            Assert.Equal(ToolStatus.Launched, row.Status);
            Assert.Null(row.ErrorDetail);
        });

        /// <summary>
        /// A STOP settles its own turn's launches, so they cannot strand every fan-out after them.
        /// </summary>
        /// <remarks>
        /// Measured on a live capture (acp.log, 2026-09-08). Six background Tasks across two fan-outs
        /// with a <c>session/cancel</c> between them produced exactly THREE <c>task-notification</c>
        /// frames — lines 122/132/138, all after the cancel at line 53, so all three belonged to the
        /// fan-out launched AFTER it. The three launched before reported nothing, ever.
        /// <para>
        /// Because the count is what settles rows, those three sat in the ledger forever and took every
        /// later fan-out with them: the reported screenshot showed all six rows saying "running", which
        /// is 6 launches minus 3 returns. This is the exact shape of the bug the design calls
        /// unguardable for want of identity — and it is guardable here, because a cancel is a fact the
        /// host holds and the stranded ids are its own turn's.
        /// </para>
        /// </remarks>
        [Fact]
        public void ACancelledTurnsLaunchesDoNotStrandTheNextFanOut() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);

            // Three launched, then stopped before any of them reported — the captured sequence.
            Launch(engine, vm, "t1");
            Launch(engine, vm, "t2");
            Launch(engine, vm, "t3");
            engine.Raise(new AgentEventDto { Type = "turnDone", StopReason = "cancelled" });
            DrainDispatcher();

            // A fresh fan-out in the SAME conversation, reporting normally.
            var fresh = new[] { Launch(engine, vm, "t4"), Launch(engine, vm, "t5"), Launch(engine, vm, "t6") };
            Returned(engine);
            Returned(engine);
            Returned(engine);

            Assert.All(fresh, r => Assert.Equal("finished", r.StatusNote));
        });

        /// <summary>
        /// ...and the stranded rows say so, rather than claiming a completion nobody received.
        /// "finished" would be a lie about a result, and leaving them on "running" is what stranded
        /// them. The task may genuinely still be going — Claude does not stop a background Task on
        /// cancel — so the note reports the missing REPORT and says nothing about the work.
        /// </summary>
        [Fact]
        public void ACancelledLaunchIsMarkedUnreportedRatherThanFinished() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);
            var row = Launch(engine, vm, "t1");

            engine.Raise(new AgentEventDto { Type = "turnDone", StopReason = "cancelled" });
            DrainDispatcher();

            Assert.Equal("unreported", row.StatusNote);
            Assert.Equal(ToolStatus.Launched, row.Status);   // no result was reported; none is claimed
            Assert.Contains("may still be running", row.StatusNoteToolTip, StringComparison.Ordinal);
        });

        /// <summary>
        /// A stop that strands background work SAYS so. A successful cancel adds nothing to the
        /// transcript on its own — only the failure path posts a notice — and a launched row's change is
        /// one word at the other end of the row, so a stop with nothing but Tasks in flight was
        /// invisible: no notice, no status change anyone would notice, nothing in a replayed log. It
        /// cost several rounds of diagnosis to spot that a Stop had even happened.
        /// </summary>
        [Fact]
        public void AStopThatStrandsBackgroundWorkSaysSo() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);
            Launch(engine, vm, "t1");
            Launch(engine, vm, "t2");

            engine.Raise(new AgentEventDto { Type = "turnDone", StopReason = "cancelled" });
            DrainDispatcher();

            var notice = vm.Items.OfType<NoticeItemViewModel>().LastOrDefault();
            Assert.NotNull(notice);
            Assert.Contains("2 background tasks", notice!.Text, StringComparison.Ordinal);
            // The two facts a user cannot infer from the rows: the agent is not going to receive them
            // either, and the work is not necessarily stopped.
            Assert.Contains("agent", notice.Text, StringComparison.Ordinal);
            Assert.Contains("may still be running", notice.Text, StringComparison.Ordinal);
        });

        /// <summary>An ordinary turn end strands nothing, so it says nothing.</summary>
        [Fact]
        public void AnOrdinaryTurnEndPostsNoStopNotice() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);
            Launch(engine, vm, "t1");

            engine.Raise(new AgentEventDto { Type = "turnDone" });
            DrainDispatcher();

            Assert.DoesNotContain(
                vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.Contains("will not report back", StringComparison.Ordinal));
        });

        /// <summary>
        /// A return arriving with nothing outstanding belongs to work from before this transcript was
        /// loaded — captured, one log opens with three of them. Letting the counter go negative would
        /// hold the NEXT real fan-out open forever: the original bug, with a delay on it.
        /// </summary>
        [Fact]
        public void AReturnWithNothingOutstandingCannotStrandTheNextFanOut() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);

            // Three returns belonging to work from before this transcript existed.
            Returned(engine);
            Returned(engine);
            Returned(engine);

            // Then a real fan-out of three. Unfloored, the counter is now at zero with three tasks
            // genuinely outstanding, so the very first return settles all three — two of them while
            // still running. THIS is the shape the floor exists for: a single-launch version of this
            // test passes with the floor removed, because the settle gate is "> 0" and a negative
            // counter clears it just as well as zero.
            var rows = new[] { Launch(engine, vm, "t1"), Launch(engine, vm, "t2"), Launch(engine, vm, "t3") };
            Assert.All(rows, r => Assert.Equal("running", r.StatusNote));

            Returned(engine);
            Assert.All(rows, r => Assert.Equal("running", r.StatusNote));

            Returned(engine);
            Assert.All(rows, r => Assert.Equal("running", r.StatusNote));

            Returned(engine);
            Assert.All(rows, r => Assert.Equal("finished", r.StatusNote));
        });

        /// <summary>
        /// The row keeps <see cref="ToolStatus.Launched"/> rather than flipping to Success: no result was
        /// ever sent for it, and claiming one would put a tick on work whose outcome is unknown. Only the
        /// claim that it is still running goes away.
        /// </summary>
        [Fact]
        public void AReturnedRowClaimsNoResult() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);
            var row = Launch(engine, vm, "t1");

            Returned(engine);

            Assert.Equal(ToolStatus.Launched, row.Status);
            Assert.True(string.IsNullOrEmpty(row.OutputDetail));
            Assert.Contains("come back", row.StatusNoteToolTip, StringComparison.Ordinal);
        });

        /// <summary>
        /// New Session drops the launch tracking, and the reason it must is the count rather than the
        /// rows: a conversation left with a launch outstanding carried that count into the next one,
        /// where the fresh fan-out's return was spent paying off the old debt and its own row never
        /// settled — still claiming a sub-agent is working, in a conversation that has never had one.
        /// <para>
        /// This is a CONVERSATION boundary, not a turn one. The distinction is the whole point of the
        /// remarks above: a background task legitimately outlives its turn (measured), but nothing can
        /// outlive the transcript it was drawn in — those rows are gone, and no later return means
        /// anything for them.
        /// </para>
        /// </summary>
        [Fact]
        public void NewSessionDoesNotCarryAnOutstandingLaunchIntoTheNextConversation() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);
            Launch(engine, vm, "t1"); // ...and never returned: the turn ended with it outstanding

            vm.NewSessionCommand.Execute(null);
            DrainDispatcher();

            // A clean conversation, one launch, one return — which is all it should take.
            var fresh = Launch(engine, vm, "t2");
            Returned(engine);

            Assert.Equal("finished", fresh.StatusNote);
        });

        /// <summary>
        /// The rows themselves go too, so a destroyed transcript's view-models aren't pinned — and the
        /// SECOND return is what makes this check say anything. One return leaves the stale count at 1
        /// either way, so both the fix and the bug hold the old row at "running" and the check is
        /// satisfied by accident. Drive the count to zero and they part: unfixed, the old conversation's
        /// row is still in the ledger and gets settled by traffic from a conversation it has never been
        /// part of.
        /// </summary>
        [Fact]
        public void NewSessionDoesNotSettleRowsFromTheConversationItReplaced() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);
            var old = Launch(engine, vm, "t1");

            vm.NewSessionCommand.Execute(null);
            DrainDispatcher();
            Launch(engine, vm, "t2");
            Returned(engine);
            Returned(engine); // ...enough returns to clear the old count too, had it survived

            Assert.Equal("running", old.StatusNote);
        });

        // ------------------------------------------------------------------------------------------
        // Harness
        // ------------------------------------------------------------------------------------------

        private static List<AgentEvent> Map(string update)
        {
            var events = new List<AgentEvent>();
            // IIdeServices is only touched by the permission/fs handlers, never by session/update.
            var target = new AcpClientTarget(null!, events.Add, () => "s");
            target.OnSessionUpdate(new SessionUpdateParams
            {
                SessionId = "s",
                Update = JsonSerializer.Deserialize<JsonElement>(update),
            });
            return events;
        }

        private static ToolItemViewModel Launch(StubEngine engine, ChatViewModel vm, string id)
        {
            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = id, Title = "Task", Kind = "think" });
            engine.Raise(new AgentEventDto
            {
                Type = "toolDone", ToolCallId = id, Success = true, LaunchedInBackground = true,
            });
            DrainDispatcher();
            return vm.Items.OfType<ToolItemViewModel>().Single(t => t.ToolCallId == id);
        }

        private static void Returned(StubEngine engine)
        {
            engine.Raise(new AgentEventDto { Type = "backgroundTaskReturned" });
            DrainDispatcher();
        }

        private static ChatViewModel NewViewModel(StubEngine engine) => new(
            engine,
            new StartSessionRequest("fake", null, AppContext.BaseDirectory, "Prompt", null));

        private static void DrainDispatcher() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        // One shared, GATED implementation - see StaTest. Two STA bodies from different test
        // classes used to run concurrently against process-global WPF and clipboard state.
        private static void RunSta(Action action) => StaTest.Run(action);

        private sealed class StubEngine : IEngineConnection
        {
            public event Action<AgentEventDto>? AgentEvent;

            public event Action<ProviderModelsDto>? ProviderModelsRefreshed { add { } remove { } }

            public void Raise(AgentEventDto ev) => AgentEvent?.Invoke(ev);

            // A fake with no handshake reports no session, which the panel renders as
            // "no agent session open yet" rather than as absent facts (issue #160).
            public Task<SessionInfoResponse?> SessionInfoAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<SessionInfoResponse?>(null);

            public Task<ListProvidersResponse> ListProvidersAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new ListProvidersResponse(new List<ProviderInfoDto>()));

            public Task<StartSessionResponse> StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("These tests never open a session.");

            public Task<PromptResponse> PromptAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("These tests never prompt.");

            public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<SteerResponse> SteerAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("These tests never steer.");

            public Task SetModelAsync(string modelId, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<ListBackendSessionsResponse> ListBackendSessionsAsync(
                ListBackendSessionsRequest request, CancellationToken cancellationToken = default)
                => throw new System.NotSupportedException("This stub lists no backend sessions.");

            public Task<TakeImportedHistoryResponse> TakeImportedHistoryAsync(
                TakeImportedHistoryRequest request, CancellationToken cancellationToken = default)
                => throw new System.NotSupportedException("This stub imports no history.");

            public Task<SummarizeResponse> SummarizeAsync(SummarizeRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("These tests never summarize.");
        }
    }
}
