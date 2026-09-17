using CodeWicket.UI.Diagnostics;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The <c>[history]</c> trace's arithmetic (issue #86's shape, applied to the picker). WPF-free by
    /// design, because a percentile, a share or a residue is where a diagnostic goes quietly wrong and
    /// none of it needs a dispatcher.
    /// </summary>
    [Collection(RenderSinkCollection.Name)]
    public class HistoryOpenCostTests
    {
        private static HistoryOpenCost Cost(
            double list = 0, double rows = 0, double backends = 0,
            double setter = 0, double paint = 0, int saved = 0, int cli = 0)
        {
            var cost = new HistoryOpenCost();
            cost.NoteList(list, saved);
            cost.NoteRows(rows);
            cost.NoteBackends(backends);
            cost.NoteSetter(setter);
            cost.NotePainted(paint, cli);
            return cost;
        }

        /// <summary>
        /// The residue is the point of the line: setter time the named phases do not account for.
        /// Reported rather than left implicit, so a cost nobody named is still visible.
        /// </summary>
        [Fact]
        public void UnnamedIsTheSetterTimeThePhasesDoNotAccountFor()
        {
            var line = Cost(list: 1, rows: 2, backends: 3, setter: 10).Format();
            Assert.Contains("unnamed=4.0", line);
        }

        /// <summary>
        /// Clocks are read at different moments, so rounding can make the parts exceed the whole. A
        /// negative residue would be reported as a negative duration and read as a bug in the thing
        /// being measured rather than in the measurement.
        /// </summary>
        [Fact]
        public void UnnamedNeverGoesNegative()
        {
            Assert.Contains("unnamed=0.0", Cost(list: 6, rows: 6, backends: 6, setter: 10).Format());
        }

        /// <summary>
        /// The headline has to include the paint. The whole reason for this trace is that the setter
        /// got fast and the picker did not, so a total that stopped at the setter would report the
        /// fix as a success and say nothing about the complaint.
        /// </summary>
        [Fact]
        public void TotalSpansTheSetterAndThePaint()
        {
            var line = Cost(setter: 12.5, paint: 100).Format();
            Assert.Contains("total=112.5", line);
            Assert.Contains("setter=12.5", line);
            Assert.Contains("paint=100.0", line);
        }

        /// <summary>Both counts are carried: a slow open over 3 rows is a different bug from one over 300.</summary>
        [Fact]
        public void RowCountsAreReported()
        {
            var line = Cost(saved: 29, cli: 4).Format();
            Assert.Contains("saved=29", line);
            Assert.Contains("cli=4", line);
        }

        /// <summary>
        /// Invariant formatting: these lines are pasted into issues from machines in any locale, and a
        /// decimal comma turns "list=0,7" into an unparseable field.
        /// </summary>
        [Fact]
        public void NumbersAreInvariant()
        {
            var previous = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    new System.Globalization.CultureInfo("de-DE");
                Assert.Contains("list=0.7", Cost(list: 0.7).Format());
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        /// <summary>
        /// With no sink installed there is nothing to write to, so an open must not allocate a trace -
        /// the "one static reference test per open" property the other traces hold to. Asserted in
        /// BOTH directions, or it passes by never tracing anything.
        /// </summary>
        /// <remarks>
        /// The sink is PROCESS-GLOBAL, so this sets it rather than assuming it. Written the assuming
        /// way first, it passed under a filter and failed in the full suite, where another class's
        /// sink was still installed - the shared-state trap StaTest exists for, one file over.
        /// </remarks>
        [Fact]
        public void BeginFollowsTheSink()
        {
            var previous = CodeWicket.UI.Markdown.RenderDiagnosticsLog.Sink;
            try
            {
                CodeWicket.UI.Markdown.RenderDiagnosticsLog.Sink = null;
                Assert.Null(HistoryOpenCost.Begin());

                CodeWicket.UI.Markdown.RenderDiagnosticsLog.Sink = _ => { };
                Assert.NotNull(HistoryOpenCost.Begin());
            }
            finally
            {
                CodeWicket.UI.Markdown.RenderDiagnosticsLog.Sink = previous;
            }
        }

        // ---- the backend listing's own line -----------------------------------------------------

        /// <summary>
        /// The costs paid per answering backend ACCUMULATE. Two backends each re-read the saved store
        /// to dedupe, and a formatter that kept only the last would report one backend's cost as the
        /// whole of it - understating exactly the case (more than one backend) worth measuring.
        /// </summary>
        [Fact]
        public void BackendCostsAccumulateAcrossBackends()
        {
            var cost = new HistoryBackendListCost();
            cost.AddDedupe(10);
            cost.AddDedupe(15);
            cost.AddRows(3);
            cost.AddRows(4);
            cost.Complete(wallMs: 900, listed: 26, hidden: 5, cached: 1);

            var line = cost.Format();
            Assert.Contains("dedupe=25.0", line);
            Assert.Contains("rows=7.0", line);
            Assert.Contains("wall=900.0", line);
            Assert.Contains("listed=26", line);
            Assert.Contains("hidden=5", line);
            Assert.Contains("cached=1", line);
        }

        /// <summary>
        /// An abandoned listing still spent what it spent, and says so. Without the marker its
        /// partial figures would read as a complete listing that happened to be cheap.
        /// </summary>
        [Fact]
        public void AnAbandonedListingSaysSo()
        {
            var cost = new HistoryBackendListCost();
            Assert.DoesNotContain("abandoned", cost.Format());

            cost.NoteAbandoned();
            Assert.Contains("abandoned=true", cost.Format());
        }

        [Fact]
        public void FailuresAreCounted()
        {
            var cost = new HistoryBackendListCost();
            cost.NoteFailed();
            cost.NoteFailed();
            Assert.Contains("failed=2", cost.Format());
        }

        /// <summary>Same sink rule as the open line - nothing allocated when nothing is listening.</summary>
        [Fact]
        public void BackendTraceFollowsTheSink()
        {
            var previous = CodeWicket.UI.Markdown.RenderDiagnosticsLog.Sink;
            try
            {
                CodeWicket.UI.Markdown.RenderDiagnosticsLog.Sink = null;
                Assert.Null(HistoryBackendListCost.Begin(2));

                CodeWicket.UI.Markdown.RenderDiagnosticsLog.Sink = _ => { };
                Assert.Contains("asked=2", HistoryBackendListCost.Begin(2)!.Format());
            }
            finally
            {
                CodeWicket.UI.Markdown.RenderDiagnosticsLog.Sink = previous;
            }
        }



    }
}
