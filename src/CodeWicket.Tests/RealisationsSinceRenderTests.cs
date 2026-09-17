using CodeWicket.UI.Diagnostics;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The carry-forward arithmetic behind the <c>[render-pass]</c> trace (issue #86).
    /// <para>
    /// <c>Drain</c> runs on EVERY render pass, but a line is written only for passes over
    /// <c>ReportPassMs</c> — so a pass too cheap to report used to drain the counter and throw the value
    /// away. Realisations, a trivial pass, then an expensive one produced <c>realised=0</c> on the
    /// expensive pass: a false negative pointing away from the hypothesis the trace exists to test.
    /// These pin that nothing is lost and that the attribution stays honest about it.
    /// </para>
    /// <para>
    /// WPF-free on purpose. Whether anything CALLS this is <c>--smoke</c>'s job — the same split as
    /// <see cref="RealisationCostTests"/>, and for the same reason: a wiring that is never invoked writes
    /// an empty log while every arithmetic test stays green.
    /// </para>
    /// </summary>
    public sealed class RealisationsSinceRenderTests
    {
        private static void Realise(params string[] kinds)
        {
            foreach (var kind in kinds)
                RealisationsSinceRender.NoteRealised(kind);
        }

        public RealisationsSinceRenderTests() => RealisationsSinceRender.Reset();

        /// <summary>The ordinary case: a pass reports what it saw, and says nothing about a carry there wasn't.</summary>
        [Fact]
        public void ReportedPass_statesItsOwnRealisationsAndNoCarry()
        {
            Realise("Message", "Message", "Tool");
            RealisationsSinceRender.NoteMeasure(8.6);

            var line = RealisationsSinceRender.Drain(reporting: true);

            Assert.Contains("realised=3", line);
            Assert.Contains("Message:2", line);
            Assert.Contains("Tool:1", line);
            Assert.Contains("maxMeasure=8.6", line);
            // Four extra fields on a line with nothing to report would bury the case worth finding.
            Assert.DoesNotContain("carried=", line);
        }

        /// <summary>
        /// The defect this exists for. A cheap pass writes no line, so if it merely cleared the counter the
        /// realisations would be gone — and the expensive pass that followed them would look innocent.
        /// </summary>
        [Fact]
        public void CheapPass_carriesItsRealisationsToTheNextReportedPass()
        {
            Realise("Message", "Message");
            RealisationsSinceRender.NoteMeasure(20.4);

            Assert.Null(RealisationsSinceRender.Drain(reporting: false));   // no line for a cheap pass

            var line = RealisationsSinceRender.Drain(reporting: true);

            // Exact attribution is kept: these did NOT happen immediately before the reported pass.
            Assert.Contains("realised=0", line);
            Assert.Contains("carried=2", line);
            Assert.Contains("carriedPasses=1", line);
            Assert.Contains("carriedMax=20.4", line);
            Assert.Contains("carriedKinds=Message:2", line);
        }

        /// <summary>Several quiet passes in a row accumulate rather than the last one winning.</summary>
        [Fact]
        public void SuccessiveCheapPasses_accumulateCountsPassesAndTheLargestMeasure()
        {
            Realise("Message");
            RealisationsSinceRender.NoteMeasure(4.0);
            RealisationsSinceRender.Drain(reporting: false);

            Realise("Tool", "Tool");
            RealisationsSinceRender.NoteMeasure(19.2);
            RealisationsSinceRender.Drain(reporting: false);

            Realise("Notice");
            RealisationsSinceRender.NoteMeasure(1.1);
            RealisationsSinceRender.Drain(reporting: false);

            var line = RealisationsSinceRender.Drain(reporting: true);

            Assert.Contains("carried=4", line);
            Assert.Contains("carriedPasses=3", line);      // the three cheap ones; the reported pass is not one of them
            Assert.Contains("carriedMax=19.2", line);      // the largest across all of them, not the last
            Assert.Contains("Message:1", line);
            Assert.Contains("Tool:2", line);
            Assert.Contains("Notice:1", line);
        }

        /// <summary>
        /// A pass that realised nothing still counts toward carriedPasses — "four cheap passes happened in
        /// between" is part of what the carry means — but on its own it is not a carry and prints nothing.
        /// </summary>
        [Fact]
        public void QuietPassesAlone_reportNoCarryAtAll()
        {
            RealisationsSinceRender.Drain(reporting: false);
            RealisationsSinceRender.Drain(reporting: false);

            var line = RealisationsSinceRender.Drain(reporting: true);

            Assert.Contains("realised=0", line);
            Assert.Contains("kinds=none", line);
            Assert.DoesNotContain("carried=", line);
        }

        /// <summary>
        /// A reported pass clears BOTH buckets. Without this the carry would restate itself on every later
        /// line and the number would climb forever — the inflation the drain-every-pass design was chosen
        /// to avoid, reintroduced one level up.
        /// </summary>
        [Fact]
        public void ReportingClearsTheCarry_soItIsNeverCountedTwice()
        {
            Realise("Message");
            RealisationsSinceRender.Drain(reporting: false);
            var first = RealisationsSinceRender.Drain(reporting: true);
            Assert.Contains("carried=1", first);

            var second = RealisationsSinceRender.Drain(reporting: true);
            Assert.Contains("realised=0", second);
            Assert.DoesNotContain("carried=", second);
        }

        /// <summary>Both halves can be non-zero at once, and they are reported separately rather than summed.</summary>
        [Fact]
        public void OwnRealisationsAndCarriedOnesAreReportedApart()
        {
            Realise("Message", "Message");
            RealisationsSinceRender.Drain(reporting: false);

            Realise("Tool");
            var line = RealisationsSinceRender.Drain(reporting: true);

            Assert.Contains("realised=1", line);
            Assert.Contains("kinds=Tool:1", line);
            Assert.Contains("carried=2", line);
            Assert.Contains("carriedKinds=Message:2", line);
        }
    }
}
