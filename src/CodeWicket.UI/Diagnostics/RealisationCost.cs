using System;
using System.Collections.Generic;
using System.Globalization;

namespace CodeWicket.UI.Diagnostics
{
    /// <summary>
    /// Per-drag accumulator behind the <c>[realise]</c> trace (issue #86): what the transcript spends
    /// realising and laying out its items, broken down by the KIND of item.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It exists because the trace beside it answered its question and then went quiet.</b>
    /// <c>[md-cost]</c> measures markdown rendering, and after the palette fix that is 1.1-1.9% of a
    /// drag's wall-clock — while the same log showed a <c>frame</c> callback posted at the start of a
    /// 2027ms drag not running until 2014ms in. So the UI thread was held for the whole gesture by work
    /// that is not a markdown build, and <c>[md-cost]</c> cannot say what: it is keyed on a markdown
    /// viewer, so the tool rows the user was dragging THROUGH contribute nothing to it by construction,
    /// and it drops episodes under three renders, which is most of a drag.
    /// </para>
    /// <para>
    /// <b>The two numbers it exists for are <c>repeat</c> and <c>kinds</c>.</b> A drag that realises 125
    /// items of which 41 are distinct is not traversing a transcript, it is thrashing one stretch of it —
    /// and that is a different problem with different fixes than 125 realisations of 125 items. The
    /// per-kind split then says whether the cost is the one enormous message re-formatting or the eighty
    /// small rows around it, which decides whether the lever is item height, item count, or the row
    /// template. Both questions were previously answered by inference.
    /// </para>
    /// <para>
    /// <b>Episodes, on the same terms as <c>MarkdownRenderCost</c>.</b> A drag is a burst; an episode
    /// closes after <see cref="EpisodeGapMs"/> of quiet. There is deliberately no equivalent of that
    /// type's prefix test, because there is no message identity here to change: the episode IS the
    /// gesture. What corresponds to its <c>MinRenders</c> floor is <see cref="MinRealisations"/>, which
    /// exists for the same reason and has to be much lower — a restore realises a viewport's worth of
    /// items once, which is the case worth dropping, while a two-item episode during a drag is already a
    /// fact worth having.
    /// </para>
    /// <para>
    /// <b>WPF-free, so the arithmetic can be pinned by a unit test</b> — the same bargain, and for the
    /// same reason: a percentile, a duty share or an episode that never closes is where a diagnostic goes
    /// quietly wrong, and none of it needs a dispatcher. Time is passed in rather than read here.
    /// </para>
    /// </remarks>
    internal sealed class RealisationCost
    {
        /// <summary>Quiet gap that ends a gesture. Matched to <c>MarkdownRenderCost.EpisodeGapMs</c> so
        /// the two traces cut the same drag at the same place and can be read side by side.</summary>
        internal const double EpisodeGapMs = 2000;

        /// <summary>
        /// Longest an episode may stay open, matched to <see cref="DispatcherActivity.MaxEpisodeMs"/>.
        /// </summary>
        /// <remarks>
        /// <b>So the two lines can be read against each other, which is the question the field left
        /// open.</b> A drag logged one <c>[realise]</c> covering 11.3 seconds while <c>[dispatch]</c>
        /// reported it as four 5-second windows whose render cost ranged from 690ms to 2434ms — so there
        /// was no way to ask whether render time tracks realisation count, which is exactly what decides
        /// between "realise less" and "dirty less area" as the fix. Same window on both makes that a
        /// comparison instead of an inference.
        /// </remarks>
        internal const double MaxEpisodeMs = 5000;

        /// <summary>
        /// Fewest realisations worth a line. Two rather than a handful: the point of this trace is the
        /// gesture, and a drag with two realisations that each cost 300ms is exactly the report wanted.
        /// It is only here to keep a single container being re-prepared out of the log.
        /// </summary>
        internal const int MinRealisations = 2;

        /// <summary>Most kinds named on one line. Beyond this the tail is summed into <c>other</c>
        /// rather than dropped, so the per-kind figures always add up to the total.</summary>
        internal const int MaxKindsReported = 6;

        private sealed class KindTotals
        {
            internal int Realisations;
            internal int Measures;
            internal double LayoutTotal;
            internal double LayoutMax;
        }

        private readonly Dictionary<string, KindTotals> _kinds =
            new Dictionary<string, KindTotals>(StringComparer.Ordinal);

        // Item identity is held as a hash rather than a reference: an episode is short, but a diagnostic
        // that roots view-models for the length of a gesture is a diagnostic that changes what it
        // measures. Collisions can only understate `distinct`, which is stated as a floor.
        private readonly HashSet<int> _distinct = new HashSet<int>();

        private int _realisations;
        private int _measures;
        private double _measureTotal;
        private double _measureMax;
        private double _arrangeTotal;
        private double _startMs;
        private double _lastMs;

        /// <summary>An episode is accumulating, i.e. something is owed to the log.</summary>
        internal bool IsOpen => _realisations > 0 || _measures > 0;

        /// <summary>
        /// Records one container being prepared for an item — a realisation, or a recycled container
        /// being re-pointed at one.
        /// </summary>
        /// <param name="kind">The item's kind, already shortened for display.</param>
        /// <param name="itemId">A stable per-item identity, used only to count repeats.</param>
        /// <returns>The episode this closed (a gap since the last activity), or null if it continued one.</returns>
        internal RealisationEpisode? NoteRealised(string kind, int itemId, double atMs)
        {
            var closed = CloseIfGapped(atMs);

            if (!IsOpen)
                _startMs = atMs;

            _realisations++;
            _distinct.Add(itemId);
            Totals(kind).Realisations++;
            _lastMs = atMs;
            return closed;
        }

        /// <summary>
        /// Records one measure pass over a container — the pass that carries text formatting, and so the
        /// one whose maximum is worth reporting.
        /// </summary>
        /// <remarks>
        /// Layout is charged to the KIND rather than to the item, because the question is which template
        /// is expensive; an individual item's cost is only interesting once its kind is already the
        /// answer, and <c>layoutMax</c> is what points at it.
        /// </remarks>
        internal RealisationEpisode? NoteMeasure(string kind, double ms, double atMs) =>
            Note(kind, ms, atMs, measure: true);

        /// <summary>
        /// Records one arrange pass. Counted into the kind's total but not into <c>measures</c> or the
        /// maximum: arrange places what measure already formatted, so folding it into the peak would
        /// report a number no single pass ever cost.
        /// </summary>
        internal RealisationEpisode? NoteArrange(string kind, double ms, double atMs) =>
            Note(kind, ms, atMs, measure: false);

        private RealisationEpisode? Note(string kind, double ms, double atMs, bool measure)
        {
            var closed = CloseIfGapped(atMs);

            if (!IsOpen)
                _startMs = atMs - ms;

            if (measure)
            {
                _measures++;
                _measureTotal += ms;
                if (ms > _measureMax)
                    _measureMax = ms;
            }
            else
            {
                _arrangeTotal += ms;
            }

            var kindTotals = Totals(kind);
            kindTotals.LayoutTotal += ms;
            if (measure)
            {
                kindTotals.Measures++;
                if (ms > kindTotals.LayoutMax)
                    kindTotals.LayoutMax = ms;
            }

            _lastMs = atMs;
            return closed;
        }

        /// <summary>
        /// Closes and returns the open episode if the gesture has gone quiet. Null while activity is
        /// still arriving, so the caller's timer can keep ticking without deciding anything itself.
        /// </summary>
        internal RealisationEpisode? CloseIfIdle(double nowMs) =>
            IsOpen && nowMs - _lastMs >= EpisodeGapMs ? Take() : null;

        private RealisationEpisode? CloseIfGapped(double atMs) =>
            IsOpen && (atMs - _lastMs > EpisodeGapMs || atMs - _startMs >= MaxEpisodeMs)
                ? Take()
                : null;

        private KindTotals Totals(string kind)
        {
            if (_kinds.TryGetValue(kind, out var existing))
                return existing;
            var created = new KindTotals();
            _kinds[kind] = created;
            return created;
        }

        /// <summary>
        /// Summarises and resets. Resets even when the episode is too short to report — a dropped
        /// episode must not leak its counts into the next gesture's numbers.
        /// </summary>
        private RealisationEpisode? Take()
        {
            var episode = _realisations >= MinRealisations
                ? new RealisationEpisode(
                    realisations: _realisations,
                    distinctItems: _distinct.Count,
                    measures: _measures,
                    wallMs: _lastMs - _startMs,
                    measureTotal: _measureTotal,
                    measureMax: _measureMax,
                    arrangeTotal: _arrangeTotal,
                    kinds: SummariseKinds())
                : null;

            _kinds.Clear();
            _distinct.Clear();
            _realisations = 0;
            _measures = 0;
            _measureTotal = 0;
            _measureMax = 0;
            _arrangeTotal = 0;
            _startMs = 0;
            _lastMs = 0;
            return episode;
        }

        /// <summary>
        /// The per-kind figures, most expensive first — which is the order the reader wants, since the
        /// line exists to name the expensive template.
        /// </summary>
        private List<RealisationKind> SummariseKinds()
        {
            var kinds = new List<RealisationKind>();
            foreach (var pair in _kinds)
            {
                kinds.Add(new RealisationKind(
                    pair.Key, pair.Value.Realisations, pair.Value.Measures,
                    pair.Value.LayoutTotal, pair.Value.LayoutMax));
            }

            kinds.Sort((left, right) => right.LayoutTotal.CompareTo(left.LayoutTotal));
            if (kinds.Count <= MaxKindsReported)
                return kinds;

            // The tail is FOLDED, not truncated: a reader must be able to add the named kinds up and
            // reach the total, or a missing 30% reads as unexplained rather than as uninteresting.
            var folded = new RealisationKind("other", 0, 0, 0, 0);
            for (var i = MaxKindsReported; i < kinds.Count; i++)
                folded = folded.Fold(kinds[i]);

            kinds.RemoveRange(MaxKindsReported, kinds.Count - MaxKindsReported);
            kinds.Add(folded);
            return kinds;
        }
    }

    /// <summary>One kind of transcript item's share of an episode. All durations milliseconds.</summary>
    internal sealed class RealisationKind
    {
        internal RealisationKind(string kind, int realisations, int measures, double layoutTotal, double layoutMax)
        {
            Kind = kind;
            Realisations = realisations;
            Measures = measures;
            LayoutTotal = layoutTotal;
            LayoutMax = layoutMax;
        }

        internal string Kind { get; }

        internal int Realisations { get; }

        internal int Measures { get; }

        internal double LayoutTotal { get; }

        internal double LayoutMax { get; }

        internal RealisationKind Fold(RealisationKind other) => new RealisationKind(
            Kind,
            Realisations + other.Realisations,
            Measures + other.Measures,
            LayoutTotal + other.LayoutTotal,
            LayoutMax > other.LayoutMax ? LayoutMax : other.LayoutMax);

        /// <summary>
        /// <c>Kind:realisations/measures/totalMs/maxMs</c>. Realisations and measures are reported apart
        /// because they answer different questions — how often the panel rebuilt this row, against how
        /// often WPF re-measured it, which under a drag are not the same number.
        /// </summary>
        internal string Format() =>
            Kind + ":" + Int(Realisations) + "/" + Int(Measures)
            + "/" + Ms(LayoutTotal) + "/" + Ms(LayoutMax);

        private static string Ms(double value) => value.ToString("F1", CultureInfo.InvariantCulture);

        private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// One gesture's worth of transcript realisation, as written to <c>render.log</c>.
    /// </summary>
    /// <remarks>
    /// <b>Every duration here is exact</b>, which is what separates this line from <c>[md-cost]</c>'s
    /// <c>frame</c>: layout is timed around the container's own measure and arrange rather than inferred
    /// from a dispatcher callback, so nothing else the dispatcher ran can land in it. The cost is that it
    /// sees only what the transcript's containers do — anything WPF spends elsewhere in the same pass is
    /// outside it, so a large gap between <c>layout</c> and the wall-clock is itself a finding.
    /// </remarks>
    internal sealed class RealisationEpisode
    {
        internal RealisationEpisode(
            int realisations,
            int distinctItems,
            int measures,
            double wallMs,
            double measureTotal,
            double measureMax,
            double arrangeTotal,
            List<RealisationKind> kinds)
        {
            Realisations = realisations;
            DistinctItems = distinctItems;
            Measures = measures;
            WallMs = wallMs;
            MeasureTotal = measureTotal;
            MeasureMax = measureMax;
            ArrangeTotal = arrangeTotal;
            Kinds = kinds;
        }

        internal int Realisations { get; }

        /// <summary>
        /// How many DIFFERENT items those realisations covered — a floor, since identity is held as a
        /// hash. The ratio to <see cref="Realisations"/> is the whole point: 1.0 is a clean traversal,
        /// anything well above it is the panel realising the same stretch over and over.
        /// </summary>
        internal int DistinctItems { get; }

        /// <summary>Measure passes over transcript containers. Arrange is counted into the totals but
        /// not here — see <see cref="RealisationCost.NoteArrange"/>.</summary>
        internal int Measures { get; }

        internal double WallMs { get; }

        internal double MeasureTotal { get; }

        /// <summary>The most expensive single measure pass. The number that names one item.</summary>
        internal double MeasureMax { get; }

        internal double ArrangeTotal { get; }

        internal List<RealisationKind> Kinds { get; }

        /// <summary>Measure plus arrange — all the layout time this trace can see.</summary>
        internal double LayoutTotal => MeasureTotal + ArrangeTotal;

        /// <summary>
        /// Realisations per distinct item. 1.0 means every item was realised once; higher means the
        /// same items were realised repeatedly within one gesture.
        /// </summary>
        internal double RepeatFactor => DistinctItems > 0 ? (double)Realisations / DistinctItems : 0;

        /// <summary>
        /// Share of the gesture's wall-clock spent in transcript layout. Unlike <c>[md-cost]</c>'s duty
        /// this is a LOWER bound on the pane's cost, because it counts only the containers' own passes.
        /// </summary>
        internal double DutyPercent => WallMs > 0 ? LayoutTotal / WallMs * 100.0 : 0;

        /// <summary>
        /// The log line. Flat <c>key=value</c> pairs and milliseconds throughout, matching
        /// <c>[md-cost]</c> so one grep reads both.
        /// </summary>
        internal string Format() =>
            "[realise] items=" + Int(Realisations)
            + " distinct=" + Int(DistinctItems)
            + " repeat=" + Ms(RepeatFactor) + "x"
            + " wall=" + Ms(WallMs)
            + " duty=" + Ms(DutyPercent) + "%"
            + " measures=" + Int(Measures)
            + " layoutTotal=" + Ms(LayoutTotal)
            + " measureTotal=" + Ms(MeasureTotal)
            + " measureMax=" + Ms(MeasureMax)
            + " arrangeTotal=" + Ms(ArrangeTotal)
            + " kinds=" + FormatKinds();

        private string FormatKinds()
        {
            if (Kinds.Count == 0)
                return "none";

            var parts = new string[Kinds.Count];
            for (var i = 0; i < Kinds.Count; i++)
                parts[i] = Kinds[i].Format();
            return string.Join(",", parts);
        }

        // Invariant formatting spelled out rather than interpolated: this project multi-targets net472,
        // where an interpolated string does not reach the culture-aware handler overloads.
        private static string Ms(double value) => value.ToString("F1", CultureInfo.InvariantCulture);

        private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
