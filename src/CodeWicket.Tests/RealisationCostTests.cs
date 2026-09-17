using System.Linq;
using CodeWicket.UI.Diagnostics;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The <c>[realise]</c> arithmetic (issue #86). Realising a container needs a panel, a dispatcher and
    /// a real drag; deciding what the resulting numbers MEAN does not, and that is where a trace goes
    /// quietly wrong — a repeat factor that reports a clean traversal as thrash, a per-kind tail that
    /// silently stops adding up to the total, an episode that never closes. Same bargain as
    /// <see cref="MarkdownRenderCostTests"/>: the type takes its clock as a parameter.
    /// </summary>
    public sealed class RealisationCostTests
    {
        /// <summary>A drag that realises <paramref name="items"/> distinct rows, once each, 10ms apart.</summary>
        static RealisationCost Traversal(int items, string kind = "Tool", double measureMs = 2)
        {
            var cost = new RealisationCost();
            for (var i = 0; i < items; i++)
            {
                var at = 1000 + (i * 10);
                cost.NoteRealised(kind, itemId: i, atMs: at);
                cost.NoteMeasure(kind, measureMs, atMs: at + measureMs);
            }
            return cost;
        }

        [Fact]
        public void A_clean_traversal_repeats_nothing()
        {
            var episode = Traversal(items: 20).CloseIfIdle(nowMs: 99_000);

            Assert.NotNull(episode);
            Assert.Equal(20, episode!.Realisations);
            Assert.Equal(20, episode.DistinctItems);
            Assert.Equal(1.0, episode.RepeatFactor);
        }

        [Fact]
        public void The_same_items_realised_over_and_over_shows_as_repeat()
        {
            // The finding this trace exists to make visible, and the one that separates "the user dragged
            // past 120 rows" from "the panel rebuilt the same 40 rows three times" — two different
            // problems that cost the same and are indistinguishable without this ratio.
            var cost = new RealisationCost();
            for (var pass = 0; pass < 3; pass++)
            {
                for (var i = 0; i < 40; i++)
                    cost.NoteRealised("Message", itemId: i, atMs: 1000 + (pass * 400) + (i * 10));
            }

            var episode = cost.CloseIfIdle(nowMs: 9000);

            Assert.NotNull(episode);
            Assert.Equal(120, episode!.Realisations);
            Assert.Equal(40, episode.DistinctItems);
            Assert.Equal(3.0, episode.RepeatFactor);
        }

        [Fact]
        public void A_quiet_gap_closes_the_gesture()
        {
            var cost = Traversal(items: 5);

            // Mid-drag, realisations are milliseconds apart, so nothing closes...
            Assert.Null(cost.CloseIfIdle(nowMs: 1042 + 500));
            // ...and the drag ending is simply the absence of the next one.
            Assert.NotNull(cost.CloseIfIdle(nowMs: 1042 + RealisationCost.EpisodeGapMs));
            Assert.False(cost.IsOpen);
        }

        [Fact]
        public void A_gesture_arriving_after_a_gap_starts_a_fresh_one()
        {
            // The case the timer cannot catch: a second drag beginning before the flush tick runs. Its
            // realisations must not join the previous gesture's counts.
            var cost = Traversal(items: 4);

            var closed = cost.NoteRealised("Tool", itemId: 99, atMs: 1032 + RealisationCost.EpisodeGapMs + 1);

            Assert.NotNull(closed);
            Assert.Equal(4, closed!.Realisations);
            Assert.True(cost.IsOpen);

            var second = cost.CloseIfIdle(nowMs: 99_000);
            Assert.Null(second); // one realisation is below the floor, and it did not inherit the first four
        }

        [Fact]
        public void A_long_gesture_is_cut_into_windows_the_dispatcher_trace_can_be_read_against()
        {
            // The field left a question this closes: one [realise] covering 11.3s against four
            // [dispatch] windows whose render cost ranged 690-2434ms, so "does render time track
            // realisation count" — the thing that decides between realising less and dirtying less —
            // could not be asked. Same window on both makes it a comparison.
            var cost = new RealisationCost();
            RealisationEpisode? closed = null;
            for (var i = 0; i < 400 && closed is null; i++)
                closed = cost.NoteRealised("Message", itemId: i, atMs: 1000 + (i * 20));

            Assert.NotNull(closed);
            Assert.Equal(RealisationCost.MaxEpisodeMs, DispatcherActivity.MaxEpisodeMs);
            Assert.True(closed!.WallMs >= RealisationCost.MaxEpisodeMs - 20);
            // The realisation that tripped the window opened the next one rather than being lost.
            Assert.True(cost.IsOpen);
        }

        [Fact]
        public void Layout_is_charged_to_the_kind_that_incurred_it()
        {
            // The other half of the point: naming which template is expensive. One enormous message
            // against many cheap rows is the shape reported from the field, and the numbers must not
            // average it away.
            var cost = new RealisationCost();
            cost.NoteRealised("Message", itemId: 1, atMs: 1000);
            cost.NoteMeasure("Message", 300, atMs: 1300);
            for (var i = 0; i < 10; i++)
            {
                cost.NoteRealised("Tool", itemId: 100 + i, atMs: 1300 + i);
                cost.NoteMeasure("Tool", 2, atMs: 1302 + i);
            }

            var episode = cost.CloseIfIdle(nowMs: 9000);

            Assert.NotNull(episode);
            var message = episode!.Kinds.Single(k => k.Kind == "Message");
            var tool = episode.Kinds.Single(k => k.Kind == "Tool");
            Assert.Equal(300, message.LayoutTotal);
            Assert.Equal(20, tool.LayoutTotal);
            // Most expensive first, because the line exists to name the expensive template.
            Assert.Equal("Message", episode.Kinds[0].Kind);
            Assert.Equal(300, episode.MeasureMax);
        }

        [Fact]
        public void Arrange_counts_toward_the_total_but_never_toward_the_peak()
        {
            // measureMax names one item's worst pass. Folding arrange into it would report a figure no
            // single pass ever cost, which is the kind of wrong a reader cannot see.
            var cost = new RealisationCost();
            cost.NoteRealised("Message", itemId: 1, atMs: 1000);
            cost.NoteMeasure("Message", 40, atMs: 1040);
            cost.NoteRealised("Message", itemId: 2, atMs: 1040);
            cost.NoteArrange("Message", 5, atMs: 1045);

            var episode = cost.CloseIfIdle(nowMs: 9000);

            Assert.NotNull(episode);
            Assert.Equal(40, episode!.MeasureMax);
            Assert.Equal(40, episode.MeasureTotal);
            Assert.Equal(5, episode.ArrangeTotal);
            Assert.Equal(45, episode.LayoutTotal);
            Assert.Equal(1, episode.Measures);
        }

        [Fact]
        public void The_per_kind_tail_is_folded_rather_than_dropped()
        {
            // A reader must be able to add the named kinds up and reach layoutTotal. Truncating the tail
            // would leave a missing share that reads as unexplained rather than as uninteresting.
            var cost = new RealisationCost();
            for (var k = 0; k < RealisationCost.MaxKindsReported + 4; k++)
            {
                cost.NoteRealised("Kind" + k, itemId: k, atMs: 1000 + k);
                cost.NoteMeasure("Kind" + k, 10 - k, atMs: 1000 + k);
            }

            var episode = cost.CloseIfIdle(nowMs: 9000);

            Assert.NotNull(episode);
            Assert.Equal(RealisationCost.MaxKindsReported + 1, episode!.Kinds.Count);
            Assert.Equal("other", episode.Kinds[episode.Kinds.Count - 1].Kind);
            Assert.Equal(episode.LayoutTotal, episode.Kinds.Sum(k => k.LayoutTotal), 3);
            Assert.Equal(episode.Realisations, episode.Kinds.Sum(k => k.Realisations));
        }

        [Fact]
        public void A_single_realisation_is_not_a_gesture()
        {
            // A restore realises a viewport's worth of rows once; one container being re-prepared is not
            // a drag and would only crowd the log.
            var cost = new RealisationCost();
            cost.NoteRealised("Message", itemId: 1, atMs: 1000);

            Assert.Null(cost.CloseIfIdle(nowMs: 9000));
            Assert.False(cost.IsOpen);
        }

        [Fact]
        public void The_line_states_every_figure_it_measured()
        {
            var episode = Traversal(items: 6, kind: "Message", measureMs: 4)
                .CloseIfIdle(nowMs: 99_000);

            var line = episode!.Format();

            Assert.StartsWith("[realise] ", line);
            Assert.Contains("items=6 distinct=6 repeat=1.0x", line);
            Assert.Contains("measures=6", line);
            Assert.Contains("measureTotal=24.0", line);
            Assert.Contains("measureMax=4.0", line);
            Assert.Contains("kinds=Message:6/6/24.0/4.0", line);
        }
    }
}
