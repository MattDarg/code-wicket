using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace CodeWicket.UI.Markdown
{
    /// <summary>
    /// Per-message accumulator behind the <c>[md-cost]</c> trace (issue #86): how much UI-thread time a
    /// streaming message actually cost, aggregated over its whole stream rather than per render.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why an aggregate and not a line per render.</b> The trace beside this one
    /// (<see cref="MarkdownRenderDiagnostics"/>) writes a synchronous file append per render, ~10 a
    /// second per streaming message, which costs more than most of what it measures — so it cannot be
    /// used for timing. A handful of lines per turn can. The two must therefore never be enabled
    /// together, which is why they have separate switches: with the per-render trace on, the numbers
    /// this one reports are mostly the other one's file IO.
    /// </para>
    /// <para>
    /// <b>WPF-free on purpose.</b> The arithmetic is where this can silently go wrong (an off-by-one
    /// percentile, a duty cycle over a zero wall, an episode that never closes), and none of it needs a
    /// dispatcher or a visual tree — so it is pinned by <c>MarkdownRenderCostTests</c> in the manner of
    /// <c>ChatInputSizingTests</c>. Time is passed IN rather than read here, which is the whole reason
    /// gap detection is testable at all.
    /// </para>
    /// <para>
    /// <b>Episodes, because a container is RECYCLED.</b> One viewer serves many messages over its life,
    /// so "this viewer's renders" is not "this message's renders". An episode closes when the stream goes
    /// quiet (<see cref="EpisodeGapMs"/> — streaming renders are at most 150ms apart, see
    /// <see cref="MarkdownText"/>) or when the new text is not an EXTENSION of the last one rendered.
    /// </para>
    /// <para>
    /// <b>That test is a prefix comparison, not "shorter than before" — which is wrong under a
    /// scroll.</b> A growing message only ever appends, so shorter did
    /// separate one message from the next while a reply was streaming. Under a SCROLL it does not:
    /// dragging recycles a container across many messages with no gap between them, and any two whose
    /// lengths happen to increase merged into a single "episode" spanning several messages — reporting a
    /// <c>len</c> belonging to the last of them and a <c>wall</c> belonging to none. The line was not
    /// wrong in a way a reader could see, which is the worst kind. An ordinal prefix test costs one
    /// comparison against the previous text — nothing beside a build measured in hundreds of
    /// milliseconds — and answers the actual question: is this the same text, still growing?
    /// </para>
    /// <para>
    /// Episodes below <see cref="MinRenders"/> are dropped: a restored transcript renders every message
    /// once, and a line each would bury the streaming ones this exists to show. <b>Know what that costs
    /// on the scroll path</b> — a realised container renders once, so a drag's episodes are almost all
    /// dropped and a whole drag can produce one line or none. Fixing the boundary above makes what IS
    /// reported honest; it does not make a drag visible. That needs an aggregate keyed on something
    /// other than a message.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Why a rebuild happened. Counted per episode and reported on the <c>[md-cost]</c> line.
    /// </summary>
    /// <remarks>
    /// <b>Because <c>renders=3</c> is the modal episode in the field log and nobody could say which
    /// three.</b> A realisation of the 17,730-char message costs ~80ms of building where one build is
    /// ~27ms, and the candidate explanations — the <c>Text</c> and <c>Links</c> properties each
    /// scheduling, a file reference resolving, the corrective render after a detached realisation — are
    /// indistinguishable from the outside and suggest different fixes. Two of the three would be
    /// legitimate. Guessing which is which is how the document cache and the file-reference theory both
    /// got written down before they were checked, so this counts instead.
    /// </remarks>
    internal enum RenderCause
    {
        /// <summary>The <c>Text</c> or <c>Links</c> property changed — a streamed delta, or a container
        /// being pointed at a different message.</summary>
        Input,

        /// <summary>A file reference finished resolving off the UI thread, so its plain text can become
        /// a link.</summary>
        Resolve,

        /// <summary>The corrective rebuild after a container that rendered while detached went back into
        /// the tree.</summary>
        Reattach,

        /// <summary>The host's themed values changed, so documents holding resolved values must be
        /// rebuilt (the bill for issue #86's palette fix).</summary>
        Theme,
    }

    internal sealed class MarkdownRenderCost
    {
        /// <summary>
        /// Quiet gap that ends a message. Comfortably above <see cref="MarkdownText"/>'s 150ms ceiling,
        /// so a mid-stream stall on a slow machine — exactly what #86 reports — cannot split one message
        /// into two episodes and halve its apparent cost.
        /// </summary>
        internal const double EpisodeGapMs = 2000;

        /// <summary>Fewest renders worth a line. Below this it is a restore, not a stream.</summary>
        internal const int MinRenders = 3;

        /// <summary>Display names for <see cref="RenderCause"/>, indexed by its values.</summary>
        internal static readonly string[] CauseNames = { "input", "resolve", "reattach", "theme" };

        private readonly List<double> _build = new List<double>();
        private readonly List<double> _frame = new List<double>();
        private readonly int[] _causes = new int[CauseNames.Length];
        private double _startMs;
        private double _lastMs;
        private double _parseTotal;
        private double _parseMax;
        private double _attachTotal;
        private double _attachMax;
        private string _lastText = string.Empty;
        private int _finalLength;
        private int _generation;

        /// <summary>An episode is accumulating, i.e. something is owed to the log.</summary>
        internal bool IsOpen => _build.Count > 0;

        /// <summary>
        /// Which episode is open. A <c>frame</c> callback posted before a boundary can run after it, and
        /// under a scroll — where episodes close often — that is common rather than exotic. The caller
        /// captures this when it posts and hands it back, so a stale sample is dropped instead of being
        /// added to a message it did not belong to.
        /// </summary>
        internal int Generation => _generation;

        /// <summary>
        /// Build time recorded by EVERY viewer since the process started. Read by the caller when it
        /// posts a frame probe and again when that probe fires, so the builds that happened inside the
        /// frame window can be taken back out of it — see <see cref="NoteFrame"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Process-wide rather than per-episode, because a drag realises many viewers back to back.</b>
        /// A per-episode version catches only the case where ONE viewer
        /// builds twice inside its own frame window. In the field the common case is the other one: a
        /// 119-char message logged <c>frameTotal=3786.5</c> against its own <c>buildTotal=3.3</c> and a
        /// <c>duty</c> of 296.7%, with a 17,730-char message reporting 833.6ms of builds on the line
        /// written 1ms later. A 119-character document does not take two seconds to lay out; it was
        /// holding someone else's work.
        /// </para>
        /// <para>
        /// Only ever used as a DIFFERENCE between two reads, so it needs no reset and cannot leak
        /// between tests, hosts or windows — which is also why it can be a plain static.
        /// </para>
        /// </remarks>
        internal static double GlobalBuildMs { get; private set; }

        /// <summary>Monotonic milliseconds, the clock this type's callers are expected to pass in.</summary>
        internal static double NowMs() => ToMs(Stopwatch.GetTimestamp());

        /// <summary>Converts a <see cref="Stopwatch"/> tick count (or timestamp) to milliseconds.</summary>
        internal static double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        /// <summary>
        /// Records one completed build. <paramref name="endedAtMs"/> is the moment the build finished, so
        /// the episode's wall clock runs from the first render's START to the last one's END.
        /// </summary>
        /// <returns>
        /// The episode this render CLOSED (a gap, or a different message landing on a recycled
        /// container), or null when it simply continued one. Returning it rather than writing it keeps
        /// this type free of the log — and of the reason it has to be WPF-free.
        /// </returns>
        internal MarkdownRenderEpisode? NoteRender(
            string? text, double buildMs, double endedAtMs, double parseMs = 0, double attachMs = 0,
            RenderCause cause = RenderCause.Input)
        {
            var current = text ?? string.Empty;

            MarkdownRenderEpisode? closed = null;
            // A streaming message only ever appends, so "still the same text, grown" is exactly the
            // question — and an unchanged text (the corrective render after a re-attach, or a rebuild
            // once a file reference resolves) passes it too, which is right: same message, same episode.
            if (IsOpen && (endedAtMs - _lastMs > EpisodeGapMs
                || !current.StartsWith(_lastText, StringComparison.Ordinal)))
                closed = Take();

            if (!IsOpen)
                _startMs = endedAtMs - buildMs;

            _build.Add(buildMs);
            _causes[(int)cause]++;
            GlobalBuildMs += buildMs;
            _lastMs = endedAtMs;
            _lastText = current;
            _finalLength = current.Length;

            _parseTotal += parseMs;
            if (parseMs > _parseMax)
                _parseMax = parseMs;
            _attachTotal += attachMs;
            if (attachMs > _attachMax)
                _attachMax = attachMs;

            return closed;
        }

        /// <summary>
        /// Records one <c>frame</c> sample — the layout pass that followed a build, less
        /// <paramref name="buildMsInWindow"/>, the build time this episode recorded while that sample was
        /// being taken.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Subtracting our own builds is what stops the two measures double-counting.</b> A frame is
        /// timed to a callback posted below <c>Render</c>, so any render that starts before that callback
        /// runs has its build counted twice — once as <c>build</c>, again inside this <c>frame</c>. It
        /// showed up as a <c>duty</c> of 101.3% in the field, which is the useful kind of wrong: an
        /// impossible number rather than a plausible one. A realisation render is synchronous, so under a
        /// scroll this is the normal case and not an edge.
        /// </para>
        /// <para>
        /// It is still an upper bound — unrelated dispatcher work in the window lands in it, and always
        /// will — but it can no longer be inflated by the very thing it is reported beside.
        /// </para>
        /// <para>
        /// Dropped when no episode is open, or when this sample belongs to one that has already been
        /// written: best-effort by construction, and why the line states its sample count separately.
        /// </para>
        /// </remarks>
        internal void NoteFrame(double frameMs, double buildMsInWindow, int generation)
        {
            if (!IsOpen || generation != _generation)
                return;

            var own = frameMs - buildMsInWindow;
            _frame.Add(own > 0 ? own : 0);
        }

        /// <summary>
        /// Closes and returns the open episode if it has gone quiet. Null while renders are still
        /// arriving, so the caller's timer can keep ticking without deciding anything itself.
        /// </summary>
        internal MarkdownRenderEpisode? CloseIfIdle(double nowMs) =>
            IsOpen && nowMs - _lastMs >= EpisodeGapMs ? Take() : null;

        /// <summary>
        /// Summarises and resets. Resets even when the episode is too short to report — a dropped
        /// episode must not leak its renders into the next message's numbers.
        /// </summary>
        private MarkdownRenderEpisode? Take()
        {
            var renders = _build.Count;
            // An episode that only ever rendered EMPTY text is a container being cleared on recycle,
            // not a message. Six of the 21 lines in the first field log were these — 29% of the output,
            // 4ms of wall each, crowding out the ones worth reading. The test is exact rather than a
            // cost threshold because the prefix boundary makes it so: a text going from content to ""
            // is not an extension, so it CLOSES the real episode and opens a separate empty one. The
            // content episode keeps its numbers; only the all-empty successor is dropped.
            var episode = renders >= MinRenders && _finalLength > 0
                ? new MarkdownRenderEpisode(
                    renders: renders,
                    frameSamples: _frame.Count,
                    finalLength: _finalLength,
                    wallMs: _lastMs - _startMs,
                    build: Summarise(_build),
                    frame: Summarise(_frame),
                    parseTotal: _parseTotal,
                    parseMax: _parseMax,
                    attachTotal: _attachTotal,
                    attachMax: _attachMax,
                    causes: (int[])_causes.Clone())
                : null;

            _build.Clear();
            _frame.Clear();
            Array.Clear(_causes, 0, _causes.Length);
            _startMs = 0;
            _lastMs = 0;
            _lastText = string.Empty;
            _finalLength = 0;
            _parseTotal = 0;
            _parseMax = 0;
            _attachTotal = 0;
            _attachMax = 0;
            // Anything still posted against the episode just closed is now stale — see Generation.
            _generation++;
            return episode;
        }

        private static MarkdownRenderStat Summarise(List<double> samples)
        {
            if (samples.Count == 0)
                return new MarkdownRenderStat(0, 0, 0, 0);

            var total = 0.0;
            var max = double.MinValue;
            foreach (var sample in samples)
            {
                total += sample;
                if (sample > max)
                    max = sample;
            }

            return new MarkdownRenderStat(total / samples.Count, Percentile(samples, 0.95), max, total);
        }

        /// <summary>
        /// Nearest-rank percentile over a copy — a mean alone hides the one 300ms rebuild that is what a
        /// user actually feels, which is the whole reason p95 and max are reported beside it.
        /// </summary>
        private static double Percentile(List<double> samples, double fraction)
        {
            var sorted = new List<double>(samples);
            sorted.Sort();
            var rank = (int)Math.Ceiling(fraction * sorted.Count) - 1;
            if (rank < 0)
                rank = 0;
            if (rank >= sorted.Count)
                rank = sorted.Count - 1;
            return sorted[rank];
        }
    }

    /// <summary>Mean / p95 / max / total of one metric over one episode. All milliseconds.</summary>
    internal sealed class MarkdownRenderStat
    {
        internal MarkdownRenderStat(double mean, double p95, double max, double total)
        {
            Mean = mean;
            P95 = p95;
            Max = max;
            Total = total;
        }

        internal double Mean { get; }

        internal double P95 { get; }

        internal double Max { get; }

        internal double Total { get; }
    }

    /// <summary>
    /// One streamed message's cost, as written to <c>render.log</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="Build"/> is exact and <see cref="Frame"/> is an upper bound</b>, and the line says
    /// so because the difference decides what the numbers mean. A build is bounded by the render call
    /// itself; a frame is timed from the document assignment to a callback posted just below
    /// <c>DispatcherPriority.Render</c>, so it covers the layout and line-breaking pass that assignment
    /// only marked as owed — plus anything else the dispatcher happened to run in between.
    /// </para>
    /// <para>
    /// <b><see cref="DutyPercent"/> is the figure to compare across machines.</b> Raw milliseconds are
    /// not comparable between a dev PC and a VDI; the share of wall-clock the render path held the UI
    /// thread is, and it is what an adaptive throttle would be scaled against.
    /// <see cref="BuildDutyPercent"/> is its exact lower half, which matters because a busier dispatcher
    /// inflates <see cref="Frame"/> — so a duty gap between two machines that vanishes in the build half
    /// is a statement about the dispatcher, not about our render cost.
    /// </para>
    /// </remarks>
    internal sealed class MarkdownRenderEpisode
    {
        internal MarkdownRenderEpisode(
            int renders,
            int frameSamples,
            int finalLength,
            double wallMs,
            MarkdownRenderStat build,
            MarkdownRenderStat frame,
            double parseTotal = 0,
            double parseMax = 0,
            double attachTotal = 0,
            double attachMax = 0,
            int[]? causes = null)
        {
            Causes = causes ?? new int[MarkdownRenderCost.CauseNames.Length];
            ParseTotal = parseTotal;
            ParseMax = parseMax;
            AttachTotal = attachTotal;
            AttachMax = attachMax;
            Renders = renders;
            FrameSamples = frameSamples;
            FinalLength = finalLength;
            WallMs = wallMs;
            Build = build;
            Frame = frame;
        }

        internal int Renders { get; }

        internal int FrameSamples { get; }

        internal int FinalLength { get; }

        internal double WallMs { get; }

        internal MarkdownRenderStat Build { get; }

        internal MarkdownRenderStat Frame { get; }

        /// <summary>
        /// The two halves of <see cref="Build"/>: <c>parse</c> is Markdig plus element construction,
        /// <c>attach</c> is <c>viewer.Document = document</c> alone.
        /// </summary>
        /// <remarks>
        /// <b>Split because the offline bench could not reproduce the field and probably cannot.</b>
        /// Measured on the standalone host, build+assign for the field's own message shape is 21.5ms
        /// against the 750-820ms that shape logs inside devenv — 35x, warm, repeated. Attaching into a
        /// live tree IS the more expensive half there (2.8x attaching to a parentless viewer), which is
        /// the resource-resolution signature: every inline code span registers two
        /// <c>SetResourceReference</c> listeners, hundreds per message, and in devenv they register up a
        /// far deeper tree, under a ChatView carrying 28 injected VS-theme overrides. A standalone host
        /// has neither, so it cannot show the effect at devenv's scale however faithful the markdown is.
        /// Rather than keep modelling it offline, the split is reported from the machine that has the
        /// problem.
        /// <para>
        /// <b>It answered on the first drag: <c>attach</c>, by 95-98%.</b> The 17,730-char message logged
        /// <c>parseMax=38.2 attachMax=1106.2</c> and <c>parseMax=42.1 attachMax=835.8</c> on two separate
        /// realisations. Parsing and constructing every element of it costs ~40ms; assigning the finished
        /// document to its viewer costs a full second. That message carries ~621 resource references —
        /// 258 inline code spans at two each, 105 hyperlinks at one — every one registered while the
        /// document is parentless and every one resolved when it meets the tree, at ~1.8ms each in devenv
        /// against ~0.012ms in a standalone host.
        /// </para>
        /// <para>
        /// Two things follow. <b>Caching built documents is worthless</b>: a realisation re-attaches
        /// whatever it reuses, so a cache would remove the 40ms and keep the 1000. And the lever is the
        /// references themselves — the viewer IS in the tree, so the values can be resolved once per
        /// render and assigned, turning ~621 registrations into a handful of lookups.
        /// </para>
        /// </remarks>
        internal double ParseTotal { get; }

        internal double ParseMax { get; }

        internal double AttachTotal { get; }

        internal double AttachMax { get; }

        /// <summary>Renders per <see cref="RenderCause"/>, indexed by its values.</summary>
        internal int[] Causes { get; }

        /// <summary>
        /// Share of the episode's wall-clock spent building and laying out, as a percentage. Zero when
        /// the wall is zero — three renders inside one clock tick is a degenerate episode, not an
        /// infinitely busy one.
        /// </summary>
        internal double DutyPercent => Share(Build.Total + Frame.Total);

        /// <summary>The exact half of <see cref="DutyPercent"/> — builds only.</summary>
        internal double BuildDutyPercent => Share(Build.Total);

        private double Share(double busyMs) => WallMs > 0 ? busyMs / WallMs * 100.0 : 0;

        /// <summary>
        /// The log line. Flat <c>key=value</c> pairs throughout so a field bug report is one grep, and
        /// every duration in milliseconds so nothing has to carry a unit.
        /// </summary>
        internal string Format(string viewerId) =>
            "[md-cost] viewer=" + viewerId
            + " renders=" + Int(Renders)
            + " frames=" + Int(FrameSamples)
            + " len=" + Int(FinalLength)
            + " wall=" + Ms(WallMs)
            + " duty=" + Ms(DutyPercent) + "%"
            + " dutyBuild=" + Ms(BuildDutyPercent) + "%"
            + " buildMean=" + Ms(Build.Mean)
            + " buildP95=" + Ms(Build.P95)
            + " buildMax=" + Ms(Build.Max)
            + " buildTotal=" + Ms(Build.Total)
            + " frameMean=" + Ms(Frame.Mean)
            + " frameP95=" + Ms(Frame.P95)
            + " frameMax=" + Ms(Frame.Max)
            + " frameTotal=" + Ms(Frame.Total)
            + " parseTotal=" + Ms(ParseTotal)
            + " parseMax=" + Ms(ParseMax)
            + " attachTotal=" + Ms(AttachTotal)
            + " attachMax=" + Ms(AttachMax)
            + " causes=" + FormatCauses();

        /// <summary>
        /// The causes that fired, zeros omitted. Omitted rather than printed as <c>:0</c> because the
        /// line is already long and a cause that did not happen is not a finding — the ones present are.
        /// </summary>
        private string FormatCauses()
        {
            var parts = new List<string>();
            for (var i = 0; i < Causes.Length && i < MarkdownRenderCost.CauseNames.Length; i++)
            {
                if (Causes[i] > 0)
                    parts.Add(MarkdownRenderCost.CauseNames[i] + ":" + Int(Causes[i]));
            }
            return parts.Count == 0 ? "none" : string.Join(",", parts.ToArray());
        }

        // Invariant formatting spelled out rather than interpolated: this project multi-targets net472,
        // where an interpolated string does not reach the culture-aware handler overloads (see the
        // Conventions in AGENTS.md) and each fragment would go through the current culture.
        private static string Ms(double value) => value.ToString("F1", CultureInfo.InvariantCulture);

        private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
