using System;
using System.Collections.Generic;
using System.Globalization;

namespace CodeWicket.UI.Diagnostics
{
    /// <summary>
    /// What the transcript realised between one WPF render pass and the next, for the
    /// <c>[render-pass]</c> trace (issue #86).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Because five-second windows cannot answer the question that is left.</b> A wheel scroll logged
    /// a SINGLE <c>MediaContext.RenderMessageHandler</c> pass of <b>1342.8ms against 8 realisations</b>,
    /// and another of 1483.5ms — while other windows show 66 passes averaging 18ms. So the cost is not
    /// realisation COUNT, and aggregating either trace over five seconds averages the one fact that
    /// matters away: whether a monster pass follows a particular realisation. The user's own account —
    /// "it kicks in when it loads more stuff" — is a claim about sub-second ordering, and nothing built
    /// so far could confirm or refute it.
    /// </para>
    /// <para>
    /// So this pairs each expensive pass with what was realised immediately before it, and with the
    /// largest container measure among those — which is how the one enormous message identifies itself
    /// (its measure is ~20ms against ~0.5ms for a tool row).
    /// </para>
    /// <para>
    /// <b>A static, and drained by EVERY render pass rather than only the logged ones.</b> There is one
    /// transcript per dispatcher, so a static is honest here; and if only slow passes drained it, the
    /// count would silently mean "since the last SLOW pass" and quietly inflate. Two chat panes in one
    /// process would merge — noted rather than solved, since the trace is opt-in and single-window.
    /// </para>
    /// <para>
    /// <b>But a pass too cheap to report CARRIES its counts forward rather than dropping them</b>, and
    /// that half was missing. Draining unconditionally is right — the numbers mean "immediately before
    /// THIS pass" — yet the value was then discarded whenever the pass came in under
    /// <see cref="ReportPassMs"/>. So realisations followed by a trivial pass and then an expensive one
    /// produced <c>realised=0</c> on the expensive one, which reads as "this 1300ms pass followed no
    /// realisations": a FALSE NEGATIVE pointing away from the very hypothesis the trace exists to test.
    /// A reported line now carries <c>carried=</c>/<c>carriedPasses=</c> beside its own
    /// <c>realised=</c> — the attribution stays exact (they did not happen immediately before this pass,
    /// and the line says so) while nothing is silently lost.
    /// </para>
    /// <para>
    /// Found through <c>--smoke</c>, which asserted <c>realised&gt;0</c> against realisations left over
    /// from an earlier phase: 4 consecutive failures and then 12 consecutive passes on the same commit,
    /// according to whether a sub-threshold pass happened to slip in between. The check now forces its
    /// realisations inside the traced window; this fixes what that flake was pointing at.
    /// </para>
    /// </remarks>
    internal static class RealisationsSinceRender
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, int> Kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        private static int _count;
        private static double _maxMeasureMs;

        // What earlier, too-cheap-to-report passes drained since the last reported line.
        private static readonly Dictionary<string, int> CarriedKinds = new Dictionary<string, int>(StringComparer.Ordinal);
        private static int _carried;
        private static int _carriedPasses;
        private static double _carriedMaxMeasureMs;

        /// <summary>
        /// Fewest milliseconds a render pass must cost to be worth a line of its own. Below it the
        /// aggregate already says enough, and a line per pass would be ~13 a second.
        /// </summary>
        /// <remarks>
        /// <b>A field rather than a const because <c>--smoke</c> has to lower it.</b> The mechanism under
        /// test is that a render pass produces a line carrying its realisation summary, and a standalone
        /// host cannot produce a 100ms render pass on demand — the passes this exists for are a
        /// 17,730-char message meeting the tree inside devenv. Pretending otherwise would mean either an
        /// unassertable check or a threshold chosen to suit the harness rather than the field.
        /// </remarks>
        internal static double ReportPassMs = 100;

        internal static void NoteRealised(string kind)
        {
            lock (Gate)
            {
                _count++;
                Kinds[kind] = Kinds.TryGetValue(kind, out var seen) ? seen + 1 : 1;
            }
        }

        internal static void NoteMeasure(double ms)
        {
            lock (Gate)
            {
                if (ms > _maxMeasureMs)
                    _maxMeasureMs = ms;
            }
        }

        /// <summary>
        /// Takes and clears what has accumulated. Called on EVERY render pass — <paramref name="reporting"/>
        /// says whether this one is expensive enough to be written down. False folds the counts into the
        /// carry bucket and returns null; true returns the summary, carry included, and clears both.
        /// </summary>
        internal static string? Drain(bool reporting)
        {
            lock (Gate)
            {
                if (!reporting)
                {
                    // Fold forward. A pass with nothing realised before it is still counted in
                    // carriedPasses: "three cheap passes happened between the realisations and this line"
                    // is part of what the reader is being told.
                    _carried += _count;
                    _carriedPasses++;
                    if (_maxMeasureMs > _carriedMaxMeasureMs)
                        _carriedMaxMeasureMs = _maxMeasureMs;
                    foreach (var pair in Kinds)
                        CarriedKinds[pair.Key] = CarriedKinds.TryGetValue(pair.Key, out var seen) ? seen + pair.Value : pair.Value;

                    Kinds.Clear();
                    _count = 0;
                    _maxMeasureMs = 0;
                    return null;
                }

                var summary = "realised=" + _count.ToString(CultureInfo.InvariantCulture)
                    + " kinds=" + (Kinds.Count == 0 ? "none" : Format(Kinds))
                    + " maxMeasure=" + _maxMeasureMs.ToString("F1", CultureInfo.InvariantCulture);

                // Only when something was actually folded forward. In the field ReportPassMs is 100, so
                // most passes are cheap and carriedPasses is almost always non-zero — gating on THAT would
                // put four extra fields on nearly every line, burying the case the reader is looking for.
                // With nothing realised there is no loss to report, and the count of quiet passes is
                // context for a carry rather than a finding of its own.
                if (_carried > 0)
                {
                    summary += " carried=" + _carried.ToString(CultureInfo.InvariantCulture)
                        + " carriedPasses=" + _carriedPasses.ToString(CultureInfo.InvariantCulture)
                        + " carriedMax=" + _carriedMaxMeasureMs.ToString("F1", CultureInfo.InvariantCulture)
                        + " carriedKinds=" + (CarriedKinds.Count == 0 ? "none" : Format(CarriedKinds));
                }

                Kinds.Clear();
                _count = 0;
                _maxMeasureMs = 0;
                CarriedKinds.Clear();
                _carried = 0;
                _carriedPasses = 0;
                _carriedMaxMeasureMs = 0;
                return summary;
            }
        }

        /// <summary>Resets everything, so one automated check cannot read another's leftovers.</summary>
        internal static void Reset()
        {
            lock (Gate)
            {
                Kinds.Clear();
                _count = 0;
                _maxMeasureMs = 0;
                CarriedKinds.Clear();
                _carried = 0;
                _carriedPasses = 0;
                _carriedMaxMeasureMs = 0;
            }
        }

        private static string Format(Dictionary<string, int> kinds)
        {
            var parts = new List<string>();
            foreach (var pair in kinds)
                parts.Add(pair.Key + ":" + pair.Value.ToString(CultureInfo.InvariantCulture));
            return string.Join(",", parts.ToArray());
        }
    }
}
