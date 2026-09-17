using System.Linq;
using CodeWicket.UI.Diagnostics;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The <c>[dispatch]</c> arithmetic (issue #86). Hooking a dispatcher needs a dispatcher; deciding
    /// what the resulting numbers mean does not, and this line has one figure that must never be got
    /// wrong — <c>unaccounted</c>, which is what sends the investigation to the compositor rather than
    /// to our code.
    /// </summary>
    public sealed class DispatcherActivityTests
    {
        /// <summary>
        /// An operation that did NOT pump a nested frame, so its span is exactly its own time. Most of
        /// these tests are about the arithmetic rather than about nesting, and spelling the start out at
        /// every call site would bury the two cases below where it is the whole point.
        /// </summary>
        static DispatcherEpisode? Note(
            DispatcherActivity activity, string priority, string? callback, double selfMs, double atMs) =>
            activity.Note(priority, callback, selfMs, atMs, startedAtMs: atMs - selfMs);

        /// <summary>A busy stretch: <paramref name="ops"/> operations, back to back, 1ms apart.</summary>
        static DispatcherActivity Busy(int ops, string priority = "Render", double selfMs = 1)
        {
            var activity = new DispatcherActivity();
            for (var i = 0; i < ops; i++)
                Note(activity,priority, callback: null, selfMs: selfMs, atMs: 1000 + (i * 2) + selfMs);
            return activity;
        }

        [Fact]
        public void Busy_and_unaccounted_split_the_wall_between_them()
        {
            // 30 operations of 1ms each, posted 2ms apart: the dispatcher was executing for half the
            // stretch and doing something it cannot name for the other half. That second half is the
            // whole reason this trace exists, so it is stated rather than left to subtraction.
            var episode = Busy(ops: 30).CloseIfIdle(nowMs: 99_000);

            Assert.NotNull(episode);
            Assert.Equal(30, episode!.Operations);
            Assert.Equal(30, episode.BusyMs);
            Assert.Equal(59, episode.WallMs);
            Assert.Equal(29, episode.UnaccountedMs);
        }

        [Fact]
        public void A_nested_operation_is_not_charged_to_its_parent_as_well()
        {
            // The impossible-number guard. An operation that pumps a nested frame contains others in
            // full; charging it their time too would push busy over the wall, which is exactly the
            // duty=101.3% that MarkdownRenderCost had to be fixed for once already. Self-time is
            // computed by the caller, so what this pins is that the accumulator simply adds what it is
            // given — 20 inner + 5 outer over a 25ms span, never 45.
            var activity = new DispatcherActivity();
            for (var i = 0; i < 20; i++)
                Note(activity,"Normal", null, selfMs: 1, atMs: 1001 + i);
            Note(activity,"Render", null, selfMs: 5, atMs: 1025);

            var episode = activity.CloseIfIdle(nowMs: 99_000);

            Assert.Equal(25, episode!.BusyMs);
            Assert.True(episode.BusyMs <= episode.WallMs + 1);
            Assert.True(episode.DutyPercent <= 101);
        }

        [Fact]
        public void Unaccounted_never_goes_negative()
        {
            // A clock read either side of a bucket can round self-time just past the wall. Reporting a
            // negative "time nobody used" would read as a bug in the machine rather than in the trace.
            var activity = new DispatcherActivity();
            for (var i = 0; i < DispatcherActivity.MinOperations; i++)
                Note(activity,"Render", null, selfMs: 10, atMs: 1000 + i);

            var episode = activity.CloseIfIdle(nowMs: 99_000);

            Assert.Equal(0, episode!.UnaccountedMs);
        }

        [Fact]
        public void Only_operations_worth_naming_reach_the_slow_bucket()
        {
            // Naming costs reflection, so the caller resolves it only above a threshold and passes null
            // otherwise. Both still count toward the priority totals — the cheap ones are most of the
            // operations and dropping them would understate the dispatcher's occupancy.
            var activity = new DispatcherActivity();
            for (var i = 0; i < 25; i++)
                Note(activity,"Input", callback: null, selfMs: 0.1, atMs: 1000 + i);
            Note(activity,"Render", callback: "ContextLayoutManager.UpdateLayoutCallback", selfMs: 40, atMs: 1065);

            var episode = activity.CloseIfIdle(nowMs: 99_000);

            Assert.Equal(26, episode!.Operations);
            Assert.Equal(2, episode.Priorities.Count);
            var named = Assert.Single(episode.Slow);
            Assert.Equal("Render/ContextLayoutManager.UpdateLayoutCallback", named.Name);
            Assert.Equal(40, named.TotalMs);
        }

        [Fact]
        public void The_most_expensive_priority_leads()
        {
            // The line exists to name what held the thread, so the reader should not have to sort it.
            var activity = new DispatcherActivity();
            for (var i = 0; i < 30; i++)
                Note(activity,"Input", null, selfMs: 0.2, atMs: 1000 + i);
            Note(activity,"Render", null, selfMs: 300, atMs: 1330);

            var episode = activity.CloseIfIdle(nowMs: 99_000);

            Assert.Equal("Render", episode!.Priorities[0].Name);
            Assert.Equal(300, episode.MaxMs);
        }

        [Fact]
        public void A_quiet_dispatcher_is_not_a_gesture()
        {
            // An idle dispatcher still ticks a few timers; those must not each produce a line.
            var activity = new DispatcherActivity();
            for (var i = 0; i < DispatcherActivity.MinOperations - 1; i++)
                Note(activity,"Background", null, selfMs: 0.1, atMs: 1000 + i);

            Assert.Null(activity.CloseIfIdle(nowMs: 99_000));
            Assert.False(activity.IsOpen);
        }

        [Fact]
        public void A_dispatcher_that_never_goes_quiet_still_reports()
        {
            // The bug that makes the trace write NOTHING. The other two traces close on silence
            // because the thing they watch really does stop; a dispatcher does not, and in devenv it is
            // Visual Studio's. --smoke caught it as 808 operations noted and not one line written, which
            // is indistinguishable from the trace being broken.
            var activity = new DispatcherActivity();
            DispatcherEpisode? closed = null;
            for (var i = 0; i < 400 && closed is null; i++)
                closed = Note(activity,"Render", null, selfMs: 1, atMs: 1000 + (i * 20));

            Assert.NotNull(closed);
            Assert.True(closed!.WallMs >= DispatcherActivity.MaxEpisodeMs - 20);
            // The operation that tripped the window opened the next episode rather than being lost.
            Assert.True(activity.IsOpen);
        }

        [Fact]
        public void A_gap_starts_a_fresh_gesture()
        {
            var activity = Busy(ops: 25);

            var closed = Note(activity,"Render", null, selfMs: 1, atMs: 1049 + DispatcherActivity.EpisodeGapMs + 1);

            Assert.NotNull(closed);
            Assert.Equal(25, closed!.Operations);
            Assert.True(activity.IsOpen);
        }

        [Fact]
        public void The_tail_of_the_priority_split_is_folded_rather_than_dropped()
        {
            var activity = new DispatcherActivity();
            for (var p = 0; p < DispatcherActivity.MaxBucketsReported + 3; p++)
            {
                for (var i = 0; i < 3; i++)
                    Note(activity,"P" + p, null, selfMs: 10 - p, atMs: 1000 + (p * 3) + i);
            }

            var episode = activity.CloseIfIdle(nowMs: 99_000);

            Assert.Equal(DispatcherActivity.MaxBucketsReported + 1, episode!.Priorities.Count);
            Assert.Equal("other", episode.Priorities[episode.Priorities.Count - 1].Name);
            Assert.Equal(episode.BusyMs, episode.Priorities.Sum(b => b.TotalMs), 3);
            Assert.Equal(episode.Operations, episode.Priorities.Sum(b => b.Count));
        }

        [Fact]
        public void An_operation_spanning_episodes_cannot_push_busy_over_the_wall()
        {
            // The field case, in miniature: duty=1301.5% on a line reading busy=64835.1 against
            // wall=4981.5. A long operation pumps a nested frame, so its CHILDREN complete first and
            // open episodes of their own; when the outer one finally completes, its whole self-time is
            // charged to whichever episode is open at that moment — one that began long after the work
            // it is being charged for. Self-time was never the leak. The episode's start is.
            var activity = new DispatcherActivity();

            // A nested burst, all of it inside the outer operation's span, which began at 1000. It has
            // to land within EpisodeGapMs of the outer operation's completion or it forms an episode of
            // its own and the overlap this test is about never happens — and the test
            // passes against the unfixed code.
            for (var i = 0; i < 25; i++)
                Note(activity, "Input", null, selfMs: 0.1, atMs: 65_500 + i);

            // The outer operation completes: began at 1000, 64_000ms of its own time, 65s span. Unfixed,
            // this episode reports busy=64002.5 against a wall of 524.1 — duty over 12000%.
            activity.Note("Send", "SynchronizationContextAwaitTaskContinuation", 64_000, atMs: 66_000, startedAtMs: 1000);

            var episode = activity.CloseIfIdle(nowMs: 99_000);

            Assert.NotNull(episode);
            Assert.True(
                episode!.BusyMs <= episode.WallMs,
                $"busy={episode.BusyMs} must not exceed wall={episode.WallMs}");
            Assert.True(episode.DutyPercent <= 100, $"duty={episode.DutyPercent}% is impossible");
            // The wall reaches back over the operation rather than starting at its completion.
            Assert.Equal(65_000, episode.WallMs);
            // And the finding itself survives intact — this is a report, not a clamp.
            Assert.Equal(64_002.5, episode.BusyMs, 3);
            Assert.Equal(64_000, episode.MaxMs);
        }

        [Fact]
        public void A_lone_expensive_operation_still_earns_a_line()
        {
            // The regression the span fix would otherwise INTRODUCE. Backdating the episode over a long
            // operation makes the next operation trip MaxEpisodeMs immediately, so the episode closes
            // holding one or two operations — below MinOperations, and silently dropped. The trace would
            // stop reporting the very block it was built to find, which is worse than reporting it with
            // impossible arithmetic.
            var activity = new DispatcherActivity();

            activity.Note("Send", "BlockingThing", selfMs: 4_000, atMs: 5_000, startedAtMs: 1_000);
            var episode = activity.CloseIfIdle(nowMs: 99_000);

            Assert.NotNull(episode);
            Assert.Equal(1, episode!.Operations);
            Assert.Equal(4_000, episode.MaxMs);
        }

        [Fact]
        public void A_short_burst_of_cheap_operations_is_still_not_a_gesture()
        {
            // The other half of the cost test: ReportOperationMs must not turn every idle flurry into a
            // line. Same population as the lone-expensive case, nothing expensive in it.
            var activity = new DispatcherActivity();

            activity.Note("Background", null, selfMs: DispatcherActivity.ReportOperationMs - 1, atMs: 1_100, startedAtMs: 1_000);

            Assert.Null(activity.CloseIfIdle(nowMs: 99_000));
        }

        [Fact]
        public void The_line_leads_with_what_the_dispatcher_could_not_account_for()
        {
            var line = Busy(ops: 30).CloseIfIdle(nowMs: 99_000)!.Format();

            Assert.StartsWith("[dispatch] ", line);
            Assert.Contains("ops=30 wall=59.0 busy=30.0 duty=50.8% unaccounted=29.0 maxOp=1.0", line);
            Assert.Contains("byPriority=Render:30/30.0/1.0", line);
            Assert.Contains("slow=none", line);
        }
    }
}
