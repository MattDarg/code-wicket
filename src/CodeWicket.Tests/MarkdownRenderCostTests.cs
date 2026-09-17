using CodeWicket.UI.Markdown;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The <c>[md-cost]</c> render-cost arithmetic (issue #86). The renders themselves need a dispatcher
    /// and a visual tree; the aggregation does not, and it is where this can go quietly wrong — a
    /// percentile off by one, a duty cycle over a zero wall, a recycled container folding two messages
    /// into one line. So the type takes its clock as a parameter and is pinned here, in the manner of
    /// <see cref="ChatInputSizingTests"/>.
    /// </summary>
    public sealed class MarkdownRenderCostTests
    {
        // A streamed message: renders every 100ms (MarkdownText's throttle), each costing 10ms to build.
        // The text GROWS by append, which is what makes it one message rather than several.
        static MarkdownRenderCost Streaming(int renders, double buildMs = 10, double frameMs = 20)
        {
            var cost = new MarkdownRenderCost();
            for (var i = 0; i < renders; i++)
            {
                cost.NoteRender(Grown(i + 1), buildMs, endedAtMs: 1000 + (i * 100));
                cost.NoteFrame(frameMs, buildMsInWindow: 0, cost.Generation);
            }
            return cost;
        }

        /// <summary>A streaming message after <paramref name="deltas"/> appends — 100 chars each.</summary>
        static string Grown(int deltas) => new string('a', deltas * 100);

        [Fact]
        public void A_quiet_gap_closes_the_message()
        {
            var cost = Streaming(renders: 5);

            // Renders are at most 150ms apart while streaming, so nothing closes mid-message...
            Assert.Null(cost.CloseIfIdle(nowMs: 1400 + 150));
            // ...and the turn ending is simply the absence of the next one.
            var episode = cost.CloseIfIdle(nowMs: 1400 + MarkdownRenderCost.EpisodeGapMs);

            Assert.NotNull(episode);
            Assert.Equal(5, episode!.Renders);
            Assert.Equal(500, episode.FinalLength);
            Assert.False(cost.IsOpen);
        }

        [Fact]
        public void A_text_that_is_not_the_last_one_grown_closes_the_message()
        {
            // The case a timer cannot catch: the virtualising panel re-points a viewer at a DIFFERENT
            // message with no gap at all.
            var cost = Streaming(renders: 4);

            var closed = cost.NoteRender("a completely different message", buildMs: 1, endedAtMs: 1350);

            Assert.NotNull(closed);
            Assert.Equal(4, closed!.Renders);
            Assert.Equal(400, closed.FinalLength);
            // The new render started a fresh episode rather than being lost with the closed one.
            Assert.True(cost.IsOpen);
        }

        [Fact]
        public void A_LONGER_unrelated_message_closes_it_too()
        {
            // The field bug, and the reason the test above is not enough. The boundary used to be
            // "shorter than anything seen", which a scroll defeats: dragging recycles one container
            // across many messages with no gap, and any two whose lengths happen to increase merged into
            // a single episode reporting a len belonging to the last and a wall belonging to none.
            var cost = Streaming(renders: 4);                 // 400 chars of 'a'

            var closed = cost.NoteRender(
                new string('b', 24_000), buildMs: 1693, endedAtMs: 1350);

            Assert.NotNull(closed);
            Assert.Equal(4, closed!.Renders);
            Assert.Equal(400, closed.FinalLength);            // the message that ended, not the new one
            Assert.Equal(10.0, closed.Build.Max);             // the 1693ms build belongs to the NEW one
        }

        [Fact]
        public void Re_rendering_the_same_text_stays_in_the_same_message()
        {
            // The corrective render after a re-attach, and the rebuild once a file reference resolves,
            // both hand over an UNCHANGED text. A prefix test has to accept that or every one of them
            // would split a message in two.
            var cost = Streaming(renders: 3);

            var closed = cost.NoteRender(Grown(3), buildMs: 10, endedAtMs: 1300);

            Assert.Null(closed);
            var episode = cost.CloseIfIdle(nowMs: 9000)!;
            Assert.Equal(4, episode.Renders);
        }

        [Fact]
        public void A_frame_window_does_not_count_builds_that_happened_inside_it()
        {
            // A frame is timed to a callback posted below Render, so a render starting before that
            // callback runs would be counted twice — once as build, again inside frame. It reached the
            // field as duty=101.3%, which is impossible and therefore noticed. A realisation render is
            // synchronous, so under a scroll this is the normal case.
            var cost = new MarkdownRenderCost();
            for (var i = 0; i < 3; i++)
                cost.NoteRender(Grown(i + 1), buildMs: 100, endedAtMs: 1000 + (i * 200));

            // 250ms of wall between the assignment and the callback, of which 200ms was our own builds.
            cost.NoteFrame(250, buildMsInWindow: 200, cost.Generation);

            var episode = cost.CloseIfIdle(nowMs: 9000)!;

            Assert.Equal(50.0, episode.Frame.Total);
            Assert.True(episode.DutyPercent <= 100.0, "duty was " + episode.DutyPercent);
        }

        [Fact]
        public void A_frame_window_swallowed_entirely_by_builds_is_zero_not_negative()
        {
            var cost = Streaming(renders: 3);

            cost.NoteFrame(40, buildMsInWindow: 100, cost.Generation);

            var episode = cost.CloseIfIdle(nowMs: 9000)!;
            Assert.Equal(60.0, episode.Frame.Total);          // 3 x 20 from Streaming, plus 0
            Assert.Equal(4, episode.FrameSamples);
        }

        [Fact]
        public void A_frame_window_excludes_builds_by_OTHER_viewers_too()
        {
            // The field's actual shape, and what the per-episode version missed. A drag realises many
            // containers back to back, so the work landing in one viewer's frame window is mostly OTHER
            // viewers being realised: a 119-char message logged frameTotal=3786.5 against its own
            // buildTotal=3.3 and duty=296.7%, with a 17,730-char message reporting 833.6ms of builds on
            // the line written 1ms later.
            var small = new MarkdownRenderCost();
            var large = new MarkdownRenderCost();

            for (var i = 0; i < 3; i++)
                small.NoteRender(Grown(i + 1), buildMs: 1, endedAtMs: 1000 + (i * 200));

            // The probe posts, then a different viewer builds something enormous before it fires.
            var atPost = MarkdownRenderCost.GlobalBuildMs;
            large.NoteRender(new string('b', 17_730), buildMs: 800, endedAtMs: 1300);
            small.NoteFrame(830, MarkdownRenderCost.GlobalBuildMs - atPost, small.Generation);

            var episode = small.CloseIfIdle(nowMs: 9000)!;

            // To a tolerance, and the reason is in the line above: the window is a DELTA of
            // GlobalBuildMs, a process-wide accumulator that grows across every test in this class. The
            // subtraction of two large near-equal doubles loses the low bits, so 830 - 800 arrives as
            // 29.999999999999545 - and WHETHER it does depends on how much had accumulated first, which
            // depends on execution order. It passed here for a year and failed the first time the gate
            // matrix ran the suite under load. An exact comparison on a value derived this way was
            // always going to be intermittent; the product number is right either way.
            Assert.Equal(30.0, episode.Frame.Total, precision: 6);
            Assert.True(episode.DutyPercent <= 100.0, "duty was " + episode.DutyPercent);
        }

        [Fact]
        public void Build_is_reported_split_into_parse_and_attach()
        {
            // The split exists because the offline bench cannot reproduce what devenv logs, so the
            // question has to be answered from the field: which half of an 800ms build is it? A cache
            // is worth building only if it is parse — a realisation re-attaches whatever it reuses.
            var cost = new MarkdownRenderCost();
            for (var i = 0; i < 3; i++)
                cost.NoteRender(
                    Grown(i + 1), buildMs: 100, endedAtMs: 1000 + (i * 200),
                    parseMs: 14, attachMs: 84);

            var episode = cost.CloseIfIdle(nowMs: 9000)!;

            Assert.Equal(42.0, episode.ParseTotal, 3);
            Assert.Equal(14.0, episode.ParseMax, 3);
            Assert.Equal(252.0, episode.AttachTotal, 3);
            Assert.Equal(84.0, episode.AttachMax, 3);
            Assert.Contains("parseMax=14.0 attachTotal=252.0 attachMax=84.0", episode.Format("v"));
        }

        [Fact]
        public void The_split_does_not_leak_between_messages()
        {
            var cost = new MarkdownRenderCost();
            for (var i = 0; i < 3; i++)
                cost.NoteRender(Grown(i + 1), buildMs: 100, endedAtMs: 1000 + (i * 200),
                    parseMs: 900, attachMs: 900);
            cost.CloseIfIdle(nowMs: 9000);

            for (var i = 0; i < 3; i++)
                cost.NoteRender(Grown(i + 1), buildMs: 10, endedAtMs: 20000 + (i * 200),
                    parseMs: 1, attachMs: 2);
            var next = cost.CloseIfIdle(nowMs: 99000)!;

            Assert.Equal(3.0, next.ParseTotal, 3);
            Assert.Equal(1.0, next.ParseMax, 3);
            Assert.Equal(6.0, next.AttachTotal, 3);
            Assert.Equal(2.0, next.AttachMax, 3);
        }

        [Fact]
        public void An_episode_that_only_rendered_empty_text_is_not_reported()
        {
            // A container being cleared on recycle, not a message. Six of the 21 lines in the first
            // field log were these — 29% of the output at 4ms of wall each.
            var cost = new MarkdownRenderCost();
            for (var i = 0; i < 3; i++)
                cost.NoteRender(string.Empty, buildMs: 0.7, endedAtMs: 1000 + i);

            Assert.Null(cost.CloseIfIdle(nowMs: 9000));
            Assert.False(cost.IsOpen);
        }

        [Fact]
        public void A_message_that_ENDS_empty_still_reports_what_it_rendered()
        {
            // The rule is about an episode that only ever rendered empty text, not about a message that
            // finishes cleared: "" is not an extension of content, so it closes the real episode and
            // opens a separate empty one. The content keeps its numbers.
            var cost = Streaming(renders: 4);

            var closed = cost.NoteRender(string.Empty, buildMs: 0.5, endedAtMs: 1350);

            Assert.NotNull(closed);
            Assert.Equal(4, closed!.Renders);
            Assert.Equal(400, closed.FinalLength);
        }

        [Fact]
        public void A_frame_sample_from_a_closed_message_is_dropped_even_once_the_next_one_is_open()
        {
            // Generation, not IsOpen: under a scroll the next episode opens immediately, so "is anything
            // open" cannot tell a stale callback from a live one.
            var cost = Streaming(renders: 3);
            var stale = cost.Generation;
            cost.CloseIfIdle(nowMs: 9000);

            Continue(cost, from: 9000, renders: 3, buildMs: 10);   // a new episode is open and closed
            var reopened = Streaming(renders: 0);                  // (unused, keeps the shape explicit)
            cost.NoteRender(Grown(1), buildMs: 10, endedAtMs: 40000);

            cost.NoteFrame(500, buildMsInWindow: 0, stale);

            cost.NoteRender(Grown(2), buildMs: 10, endedAtMs: 40100);
            cost.NoteRender(Grown(3), buildMs: 10, endedAtMs: 40200);
            var episode = cost.CloseIfIdle(nowMs: 99000)!;

            Assert.Equal(0, episode.FrameSamples);
            Assert.Equal(0.0, episode.Frame.Total);
            Assert.NotNull(reopened);
        }

        [Fact]
        public void A_restored_transcript_writes_no_lines()
        {
            // One render per message on restore. A line each would bury the streaming ones this exists
            // to show, so an episode below the floor is dropped.
            var cost = new MarkdownRenderCost();
            cost.NoteRender(Grown(40), buildMs: 30, endedAtMs: 1000);

            Assert.Null(cost.CloseIfIdle(nowMs: 9000));
            Assert.False(cost.IsOpen);
        }

        [Fact]
        public void A_dropped_episode_does_not_leak_into_the_next_message()
        {
            var cost = new MarkdownRenderCost();
            cost.NoteRender(Grown(40), buildMs: 999, endedAtMs: 1000);
            Assert.Null(cost.CloseIfIdle(nowMs: 9000));

            var episode = Continue(cost, from: 9000, renders: 3, buildMs: 10);

            Assert.Equal(3, episode.Renders);
            Assert.Equal(30.0, episode.Build.Total);   // not 1029
            Assert.Equal(10.0, episode.Build.Max);     // the dropped render's cost is gone entirely
        }

        [Fact]
        public void Duty_is_the_share_of_wall_clock_the_render_path_held_the_thread()
        {
            // 5 renders 100ms apart, 10ms build + 20ms frame each. Wall runs from the first render's
            // START (1000 - 10) to the last one's end (1400) = 410ms. Busy = 50 + 100 = 150ms.
            var episode = Streaming(renders: 5).CloseIfIdle(nowMs: 5000)!;

            Assert.Equal(410.0, episode.WallMs, 3);
            Assert.Equal(150.0 / 410.0 * 100.0, episode.DutyPercent, 3);
            Assert.Equal(50.0 / 410.0 * 100.0, episode.BuildDutyPercent, 3);
        }

        [Fact]
        public void Duty_over_a_zero_wall_is_zero_not_infinity()
        {
            // Three renders inside one clock tick is a degenerate episode, not an infinitely busy one.
            var cost = new MarkdownRenderCost();
            for (var i = 0; i < 3; i++)
                cost.NoteRender(Grown(1), buildMs: 0, endedAtMs: 1000);

            var episode = cost.CloseIfIdle(nowMs: 9000)!;

            Assert.Equal(0.0, episode.WallMs);
            Assert.Equal(0.0, episode.DutyPercent);
        }

        [Fact]
        public void P95_and_max_expose_the_slow_rebuilds_a_mean_hides()
        {
            // 30 cheap rebuilds and 3 that stall for 300ms — the shape a user actually feels, and the
            // reason the line carries more than a mean.
            var cost = Slow(fast: 30, slow: 3);

            var episode = cost.CloseIfIdle(nowMs: 99000)!;

            Assert.Equal(36.4, episode.Build.Mean, 1);   // the mean barely moves
            Assert.Equal(300.0, episode.Build.P95);
            Assert.Equal(300.0, episode.Build.Max);
        }

        [Fact]
        public void A_lone_stall_reaches_max_but_not_p95()
        {
            // Pinned because it looks like an off-by-one and is not: one sample in 20 IS the top 5%,
            // so a percentile cannot separate it — which is exactly why max is reported beside p95
            // rather than being left to it. "Fixing" this would make p95 a second max.
            var episode = Slow(fast: 19, slow: 1).CloseIfIdle(nowMs: 99000)!;

            Assert.Equal(10.0, episode.Build.P95);
            Assert.Equal(300.0, episode.Build.Max);
        }

        [Fact]
        public void Frame_samples_are_counted_separately_from_renders()
        {
            // The frame probe is a posted callback, so the last render's sample may never land before
            // the episode closes — and a detached render's layout costs nothing. Both are facts about
            // the measurement, so the count is reported rather than assumed equal to the renders.
            var cost = new MarkdownRenderCost();
            for (var i = 0; i < 4; i++)
                cost.NoteRender(Grown(i + 1), buildMs: 10, endedAtMs: 1000 + (i * 100));
            cost.NoteFrame(20, buildMsInWindow: 0, cost.Generation);
            cost.NoteFrame(20, buildMsInWindow: 0, cost.Generation);

            var episode = cost.CloseIfIdle(nowMs: 9000)!;

            Assert.Equal(4, episode.Renders);
            Assert.Equal(2, episode.FrameSamples);
            Assert.Equal(40.0, episode.Frame.Total);
        }

        [Fact]
        public void A_late_frame_callback_never_lands_on_the_next_message()
        {
            var cost = Streaming(renders: 3);
            cost.CloseIfIdle(nowMs: 9000);

            // The dispatcher finally runs a callback from the message that just closed.
            cost.NoteFrame(500, buildMsInWindow: 0, cost.Generation);

            Assert.False(cost.IsOpen);
            var next = Continue(cost, from: 9000, renders: 3, buildMs: 10);
            Assert.Equal(0.0, next.Frame.Total);
            Assert.Equal(0, next.FrameSamples);
        }

        [Fact]
        public void The_line_is_flat_key_value_pairs_and_invariant()
        {
            var line = Streaming(renders: 5).CloseIfIdle(nowMs: 5000)!.Format("1a2b3c4d");

            Assert.Equal(
                "[md-cost] viewer=1a2b3c4d renders=5 frames=5 len=500 wall=410.0 duty=36.6% dutyBuild=12.2% "
                    + "buildMean=10.0 buildP95=10.0 buildMax=10.0 buildTotal=50.0 "
                    + "frameMean=20.0 frameP95=20.0 frameMax=20.0 frameTotal=100.0 "
                    // Zero here because this helper streams without a parse/attach split; the split's
                    // own values are pinned by Build_is_reported_split_into_parse_and_attach.
                    + "parseTotal=0.0 parseMax=0.0 attachTotal=0.0 attachMax=0.0 causes=input:5",
                line);
        }

        static MarkdownRenderEpisode Continue(MarkdownRenderCost cost, double from, int renders, double buildMs)
        {
            for (var i = 0; i < renders; i++)
                cost.NoteRender(Grown(i + 1), buildMs, endedAtMs: from + 1000 + (i * 100));
            return cost.CloseIfIdle(nowMs: from + 20000)!;
        }

        // A message whose rebuilds are cheap apart from the last few, which stall.
        static MarkdownRenderCost Slow(int fast, int slow)
        {
            var cost = new MarkdownRenderCost();
            for (var i = 0; i < fast + slow; i++)
                cost.NoteRender(
                    Grown(i + 1),
                    buildMs: i < fast ? 10 : 300,
                    endedAtMs: 1000 + (i * 100));
            return cost;
        }
    }
}
