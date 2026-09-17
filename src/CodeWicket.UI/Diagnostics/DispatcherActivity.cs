using System;
using System.Collections.Generic;
using System.Globalization;

namespace CodeWicket.UI.Diagnostics
{
    /// <summary>
    /// Per-gesture accumulator behind the <c>[dispatch]</c> trace (issue #86): where the UI thread's
    /// time actually goes, by dispatcher priority and — for the expensive ones — by callback.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The third instrument, and it exists because the first two answered by elimination.</b>
    /// <c>[md-cost]</c> put markdown building at ~1% of a drag; <c>[realise]</c> put every transcript
    /// container's measure and arrange at 1.9% of a 21-second one (397ms of 21,439ms, across 214
    /// realisations). Yet the same window shows <c>frameTotal=820.0</c> against a <c>wall</c> of 862.7 —
    /// so roughly 70% of the UI thread's time during a drag is held by work that neither instrument can
    /// see, and no further refinement of either would find it. What is left is the panel's own measure,
    /// flow-document formatting scheduled as separate dispatcher operations, and the render pass. This
    /// tells them apart by asking the dispatcher directly instead of inferring from the outside.
    /// </para>
    /// <para>
    /// <b><see cref="UnaccountedMs"/> is the point of the line, not a leftover.</b> The UI thread also
    /// runs work that is not a <c>DispatcherOperation</c> at all — window messages, input handling inside
    /// the pump — and the RENDER thread's compositing is not on it at any point. So a wall-clock that
    /// dispatcher operations fail to account for is the reading that sends the investigation to the
    /// compositor and the remoting encoder, which is #86's original suspicion and a completely different
    /// fix (a smaller dirty area, not less work). A trace that only reported what it could name would
    /// quietly foreclose that.
    /// </para>
    /// <para>
    /// <b>Self-time, not elapsed time.</b> A dispatcher operation can pump a nested frame, so an
    /// operation's start-to-finish span may contain other operations in full. Charging that span to the
    /// outer one double-counts and can push <c>busy</c> above the wall — the same class of error as the
    /// <c>duty=101.3%</c> that <c>MarkdownRenderCost</c> had to fix, so it is pre-empted here
    /// rather than left for a field log to show. Each operation is charged its own time less its
    /// children's.
    /// </para>
    /// <para>
    /// <b>An episode covers every operation's SPAN, not just the instant each one completed</b> — and
    /// getting that wrong put <c>duty=1301.5%</c> in the field log, the very number self-time was
    /// supposed to have made impossible. An operation is noted when it COMPLETES, so a long one that
    /// pumps a nested frame has its children complete first; those close the episode it began in and
    /// open others, and when the outer finally completes its whole self-time lands in whichever episode
    /// is open then — one whose wall may span 5 s against a 64.7 s charge. Self-time was never the leak:
    /// the leak is an episode measuring its wall from a start LATER than work it is charged for. So
    /// <see cref="Note"/> takes the operation's start and the episode's start moves back to cover it,
    /// which makes <c>busy &lt;= wall</c> true by construction — every operation's span then lies inside
    /// the window, and self-times within one window are disjoint by definition.
    /// </para>
    /// <para>
    /// <b>The consequence to read the log with:</b> an episode holding a long pumping operation OVERLAPS
    /// in wall-clock with the episodes its children produced. That is the honest shape rather than a
    /// residual bug — the outer operation really was running throughout — but it means <c>[dispatch]</c>
    /// lines are no longer guaranteed to partition time, and totalling their walls can now double-count.
    /// </para>
    /// <para>
    /// <b>Naming is paid for only where it matters.</b> Every operation is bucketed by priority, which is
    /// free; resolving a callback's name costs reflection, so it is done only for operations over
    /// <see cref="NameThresholdMs"/>. A trace that named every operation would be measuring itself on a
    /// dispatcher this busy — and in devenv it is not our dispatcher, it is Visual Studio's.
    /// </para>
    /// <para>
    /// WPF-free so the arithmetic is unit-testable; the priority arrives as a string and time is passed in.
    /// </para>
    /// </remarks>
    internal sealed class DispatcherActivity
    {
        /// <summary>Quiet gap that ends a gesture. Matched to the other two traces so one drag cuts at
        /// the same place in all three and the lines can be read side by side.</summary>
        internal const double EpisodeGapMs = 2000;

        /// <summary>
        /// Longest an episode may stay open, however busy the dispatcher is.
        /// </summary>
        /// <remarks>
        /// <b>Without this the trace writes nothing at all on the machine it was built for.</b> The other
        /// two traces close on silence because the thing they watch really does stop; a dispatcher does
        /// not, and in devenv it is Visual Studio's, which is never quiet for two seconds together. So an
        /// idle-only close waits for a lull that never comes, holds one unbounded episode and reports it
        /// at shutdown or never — the failure mode being an EMPTY log, which reads exactly like the trace
        /// not working. Caught by <c>--smoke</c>: 808 operations noted and not one line written. A window
        /// also makes the output more useful than one giant line, since a long drag becomes a sequence
        /// showing what dominated when.
        /// </remarks>
        internal const double MaxEpisodeMs = 5000;

        /// <summary>
        /// Fewest operations worth a line. An idle dispatcher still ticks a handful of timers, and those
        /// are not a gesture.
        /// </summary>
        internal const int MinOperations = 20;

        /// <summary>
        /// A single operation this expensive earns a line on its own, however few others accompanied it.
        /// </summary>
        /// <remarks>
        /// <b>Without this the span fix would DELETE its own finding.</b> Backdating an episode to cover
        /// a 64.7 s operation makes the very next operation trip <see cref="MaxEpisodeMs"/>, so that
        /// episode closes holding one or two operations — under <see cref="MinOperations"/>, and dropped.
        /// The trace would have gone from reporting the block with impossible arithmetic to not reporting
        /// it at all, which is strictly worse: a wrong number invites a second look, an absent one does
        /// not. <see cref="MinOperations"/> exists to suppress an idle dispatcher's timer ticks, and
        /// those are cheap by definition — so the test that separates "not a gesture" from "a gesture too
        /// short to count" is cost, not population.
        /// </remarks>
        internal const double ReportOperationMs = 100;

        /// <summary>
        /// An operation must cost at least this much to be worth naming its callback. Below it the
        /// priority bucket says everything useful and the reflection would cost more than the finding.
        /// </summary>
        internal const double NameThresholdMs = 2.0;

        /// <summary>Most buckets named on one line; the tail folds into <c>other</c> rather than being
        /// dropped, so the parts always add up to the total.</summary>
        internal const int MaxBucketsReported = 8;

        private sealed class BucketTotals
        {
            internal int Count;
            internal double Total;
            internal double Max;
        }

        private readonly Dictionary<string, BucketTotals> _priorities =
            new Dictionary<string, BucketTotals>(StringComparer.Ordinal);

        private readonly Dictionary<string, BucketTotals> _slow =
            new Dictionary<string, BucketTotals>(StringComparer.Ordinal);

        private int _operations;
        private double _busyMs;
        private double _maxMs;
        private double _startMs;
        private double _lastMs;

        /// <summary>An episode is accumulating, i.e. something is owed to the log.</summary>
        internal bool IsOpen => _operations > 0;

        /// <summary>
        /// Records one completed dispatcher operation.
        /// </summary>
        /// <param name="priority">The operation's priority, already named.</param>
        /// <param name="callback">Its callback's name, or null when it was too cheap to be worth
        /// resolving — see <see cref="NameThresholdMs"/>.</param>
        /// <param name="selfMs">Its own time, excluding any nested operations it pumped.</param>
        /// <param name="atMs">When it completed.</param>
        /// <param name="startedAtMs">When it began. Not <paramref name="atMs"/> less
        /// <paramref name="selfMs"/>: an operation that pumped a nested frame spans its children too, and
        /// it is the SPAN the episode has to cover — see the remarks on this type.</param>
        /// <returns>The episode this closed, or null if it continued one.</returns>
        internal DispatcherEpisode? Note(string priority, string? callback, double selfMs, double atMs, double startedAtMs)
        {
            var closed = IsOpen && (atMs - _lastMs > EpisodeGapMs || atMs - _startMs >= MaxEpisodeMs)
                ? Take()
                : null;

            // Opening, or reaching back over an operation that began before this episode did. Both are
            // the same statement: the window must contain every span it is charged for.
            if (!IsOpen || startedAtMs < _startMs)
                _startMs = startedAtMs;

            _operations++;
            _busyMs += selfMs;
            if (selfMs > _maxMs)
                _maxMs = selfMs;

            Add(_priorities, priority, selfMs);
            if (callback is not null)
                Add(_slow, priority + "/" + callback, selfMs);

            _lastMs = atMs;
            return closed;
        }

        /// <summary>
        /// Closes and returns the open episode if the dispatcher has gone quiet. Null while operations
        /// keep arriving, so the caller's timer can keep ticking without deciding anything itself.
        /// </summary>
        internal DispatcherEpisode? CloseIfIdle(double nowMs) =>
            IsOpen && nowMs - _lastMs >= EpisodeGapMs ? Take() : null;

        private static void Add(Dictionary<string, BucketTotals> into, string key, double ms)
        {
            if (!into.TryGetValue(key, out var totals))
            {
                totals = new BucketTotals();
                into[key] = totals;
            }
            totals.Count++;
            totals.Total += ms;
            if (ms > totals.Max)
                totals.Max = ms;
        }

        private DispatcherEpisode? Take()
        {
            var episode = _operations >= MinOperations || _maxMs >= ReportOperationMs
                ? new DispatcherEpisode(
                    operations: _operations,
                    wallMs: _lastMs - _startMs,
                    busyMs: _busyMs,
                    maxMs: _maxMs,
                    priorities: Summarise(_priorities),
                    slow: Summarise(_slow))
                : null;

            _priorities.Clear();
            _slow.Clear();
            _operations = 0;
            _busyMs = 0;
            _maxMs = 0;
            _startMs = 0;
            _lastMs = 0;
            return episode;
        }

        private static List<DispatcherBucket> Summarise(Dictionary<string, BucketTotals> from)
        {
            var buckets = new List<DispatcherBucket>();
            foreach (var pair in from)
                buckets.Add(new DispatcherBucket(pair.Key, pair.Value.Count, pair.Value.Total, pair.Value.Max));

            buckets.Sort((left, right) => right.TotalMs.CompareTo(left.TotalMs));
            if (buckets.Count <= MaxBucketsReported)
                return buckets;

            var folded = new DispatcherBucket("other", 0, 0, 0);
            for (var i = MaxBucketsReported; i < buckets.Count; i++)
                folded = folded.Fold(buckets[i]);

            buckets.RemoveRange(MaxBucketsReported, buckets.Count - MaxBucketsReported);
            buckets.Add(folded);
            return buckets;
        }
    }

    /// <summary>One priority's or one callback's share of an episode. All durations milliseconds.</summary>
    internal sealed class DispatcherBucket
    {
        internal DispatcherBucket(string name, int count, double totalMs, double maxMs)
        {
            Name = name;
            Count = count;
            TotalMs = totalMs;
            MaxMs = maxMs;
        }

        internal string Name { get; }

        internal int Count { get; }

        internal double TotalMs { get; }

        internal double MaxMs { get; }

        internal DispatcherBucket Fold(DispatcherBucket other) => new DispatcherBucket(
            Name,
            Count + other.Count,
            TotalMs + other.TotalMs,
            MaxMs > other.MaxMs ? MaxMs : other.MaxMs);

        internal string Format() =>
            Name + ":" + Count.ToString(CultureInfo.InvariantCulture)
            + "/" + TotalMs.ToString("F1", CultureInfo.InvariantCulture)
            + "/" + MaxMs.ToString("F1", CultureInfo.InvariantCulture);
    }

    /// <summary>One gesture's worth of dispatcher activity, as written to <c>render.log</c>.</summary>
    internal sealed class DispatcherEpisode
    {
        internal DispatcherEpisode(
            int operations,
            double wallMs,
            double busyMs,
            double maxMs,
            List<DispatcherBucket> priorities,
            List<DispatcherBucket> slow)
        {
            Operations = operations;
            WallMs = wallMs;
            BusyMs = busyMs;
            MaxMs = maxMs;
            Priorities = priorities;
            Slow = slow;
        }

        internal int Operations { get; }

        internal double WallMs { get; }

        /// <summary>Total self-time of every operation — what the dispatcher can account for.</summary>
        internal double BusyMs { get; }

        internal double MaxMs { get; }

        internal List<DispatcherBucket> Priorities { get; }

        internal List<DispatcherBucket> Slow { get; }

        /// <summary>
        /// Wall-clock the dispatcher cannot account for: window messages, input handled inside the pump,
        /// and any wait on the render thread. <b>Read this first.</b> A large share here means the UI
        /// thread is not the bottleneck and the next question is about compositing, not about our code.
        /// Floored at zero — self-time cannot exceed the wall, but a clock read either side of a bucket
        /// can round it there.
        /// </summary>
        internal double UnaccountedMs => WallMs - BusyMs > 0 ? WallMs - BusyMs : 0;

        /// <summary>Share of the gesture the dispatcher was executing an operation.</summary>
        internal double DutyPercent => WallMs > 0 ? BusyMs / WallMs * 100.0 : 0;

        internal string Format() =>
            "[dispatch] ops=" + Operations.ToString(CultureInfo.InvariantCulture)
            + " wall=" + Ms(WallMs)
            + " busy=" + Ms(BusyMs)
            + " duty=" + Ms(DutyPercent) + "%"
            + " unaccounted=" + Ms(UnaccountedMs)
            + " maxOp=" + Ms(MaxMs)
            + " byPriority=" + Join(Priorities)
            + " slow=" + Join(Slow);

        private static string Join(List<DispatcherBucket> buckets)
        {
            if (buckets.Count == 0)
                return "none";
            var parts = new string[buckets.Count];
            for (var i = 0; i < buckets.Count; i++)
                parts[i] = buckets[i].Format();
            return string.Join(",", parts);
        }

        private static string Ms(double value) => value.ToString("F1", CultureInfo.InvariantCulture);
    }
}
