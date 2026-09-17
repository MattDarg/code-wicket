using System.Linq;
using System.Xml.Linq;
using CodeWicket.Core.Ide;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The run counts a TRX contributes to the run_tests card (<see cref="TrxSummary"/>), and above all
    /// that they RECONCILE: passed + failed + skipped must never come to less than the file's own total
    /// with nothing saying why (issue #259).
    /// <para>
    /// The three classic fixtures below are trimmed captures of real runs (SDK 10, 2026-09-09), one per
    /// framework, and every one of them writes <c>notExecuted="0"</c> beside results it has just recorded
    /// as <c>NotExecuted</c> — the VSTest TRX logger, not one adapter. Read from the header alone, a suite
    /// with ignored tests is byte-identical to a clean one, which is the failure this pins: the counts
    /// looked ordinary, and the missing tests were findable only by subtracting.
    /// </para>
    /// </summary>
    public sealed class TrxCountsTests
    {
        private const string Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

        private static TrxCounts Read(string xml) => TrxSummary.Read(XDocument.Parse(xml).Root);

        private static string Result(string name, string outcome) =>
            $"<UnitTestResult executionId=\"{name}\" testId=\"{name}\" testName=\"{name}\" outcome=\"{outcome}\" />";

        /// <summary>A TRX in the shape every runner writes one: namespaced, results then a counters header.</summary>
        private static string Run(string results, string counters) =>
            $"<TestRun xmlns=\"{Ns}\"><Results>{results}</Results>"
            + $"<ResultSummary outcome=\"Completed\"><Counters {counters} /></ResultSummary></TestRun>";

        // The counters attribute set is copied verbatim from a real capture: every bucket is present and
        // zero, which is why "the header simply omits skipped" is NOT what happens here — it states a zero.
        private static string Counters(int total, int executed, int passed, int failed, int notExecuted = 0) =>
            $"total=\"{total}\" executed=\"{executed}\" passed=\"{passed}\" failed=\"{failed}\" error=\"0\" "
            + "timeout=\"0\" aborted=\"0\" inconclusive=\"0\" passedButRunAborted=\"0\" notRunnable=\"0\" "
            + $"notExecuted=\"{notExecuted}\" disconnected=\"0\" warning=\"0\" completed=\"0\" inProgress=\"0\" pending=\"0\"";

        [Fact]
        public void NUnitIgnoredAndInconclusiveTestsAreCounted()
        {
            // NUnit 4 + NUnit3TestAdapter 5: [Ignore] and Assert.Inconclusive both land as NotExecuted,
            // and [TestCase] rows are flat top-level results the header counts individually.
            var counts = Read(Run(
                Result("Inconclusive1", "NotExecuted") + Result("Data1(3)", "Passed") + Result("Pass1", "Passed")
                + Result("Data1(2)", "Passed") + Result("Data1(1)", "Passed") + Result("Fail1", "Failed")
                + Result("Ignored1", "NotExecuted"),
                Counters(total: 7, executed: 5, passed: 4, failed: 1)));

            Assert.Equal(7, counts.Total);
            Assert.Equal(4, counts.Passed);
            Assert.Equal(1, counts.Failed);
            Assert.Equal(2, counts.Skipped);
            Assert.Equal(counts.Total, counts.Passed + counts.Failed + counts.Skipped);
        }

        [Fact]
        public void XunitSkippedFactIsCounted()
        {
            var counts = Read(Run(
                Result("Data1(n: 1)", "Passed") + Result("Fail1", "Failed") + Result("Pass1", "Passed")
                + Result("Skipped1", "NotExecuted") + Result("Data1(n: 2)", "Passed"),
                Counters(total: 5, executed: 4, passed: 3, failed: 1)));

            Assert.Equal(1, counts.Skipped);
            Assert.Equal(counts.Total, counts.Passed + counts.Failed + counts.Skipped);
        }

        [Fact]
        public void MsTestIgnoredAndInconclusiveTestsAreCounted()
        {
            var counts = Read(Run(
                Result("Pass1", "Passed") + Result("Data1 (1)", "Passed") + Result("Inconclusive1", "NotExecuted")
                + Result("Ignored1", "NotExecuted") + Result("Fail1", "Failed") + Result("Data1 (2)", "Passed"),
                Counters(total: 6, executed: 4, passed: 3, failed: 1)));

            Assert.Equal(2, counts.Skipped);
            Assert.Equal(counts.Total, counts.Passed + counts.Failed + counts.Skipped);
        }

        [Fact]
        public void MtpHeaderAlreadyCountsSkipsAndIsNotDoubled()
        {
            // Microsoft Testing Platform's own TRX writer fills notExecuted correctly. The results agree
            // with it, so the reconciliation must return that number rather than adding to it.
            var counts = Read(Run(
                Result("Ignored1", "NotExecuted") + Result("Data1 (2)", "Passed") + Result("Pass1", "Passed")
                + Result("Data1 (1)", "Passed") + Result("Fail1", "Failed") + Result("Inconclusive1", "NotExecuted"),
                Counters(total: 6, executed: 4, passed: 3, failed: 1, notExecuted: 2)));

            Assert.Equal(2, counts.Skipped);
            Assert.Equal(counts.Total, counts.Passed + counts.Failed + counts.Skipped);
        }

        [Fact]
        public void HeaderStandsWhenTheResultsAreNotThere()
        {
            // The results are what RAISE a skipped count; a file recording none leaves the header alone.
            var counts = Read($"<TestRun xmlns=\"{Ns}\"><ResultSummary><Counters "
                + Counters(total: 4, executed: 3, passed: 3, failed: 0, notExecuted: 1)
                + " /></ResultSummary></TestRun>");

            Assert.Equal(4, counts.Total);
            Assert.Equal(3, counts.Passed);
            Assert.Equal(1, counts.Skipped);
        }

        [Fact]
        public void SkippedNeverOverflowsTheRunsOwnTotal()
        {
            // Two tests, both reported as run — a third NotExecuted result would put the buckets past the
            // total. Over-reporting is the other way for a card to fail to reconcile, so it is clamped.
            var counts = Read(Run(
                Result("Pass1", "Passed") + Result("Fail1", "Failed") + Result("Ghost", "NotExecuted"),
                Counters(total: 2, executed: 2, passed: 1, failed: 1)));

            Assert.Equal(0, counts.Skipped);
            Assert.Equal(counts.Total, counts.Passed + counts.Failed + counts.Skipped);
        }

        [Fact]
        public void DataDrivenInnerResultsAreCountedOnceNotTwice()
        {
            // Where a runner publishes an aggregate row wrapping its data rows, the aggregate repeats an
            // outcome one of those rows already carries — counting it as well reports a test that is not
            // there. Pinned on a HEADERLESS file deliberately: with a header present the clamp bounds the
            // same mistake, so a fixture with one cannot tell leaf-counting from its fallback.
            var counts = Read($"<TestRun xmlns=\"{Ns}\"><Results>"
                + Result("Pass1", "Passed")
                + "<UnitTestResult testName=\"Data1\" outcome=\"NotExecuted\"><InnerResults>"
                + Result("Data1 (1)", "Passed") + Result("Data1 (2)", "NotExecuted")
                + "</InnerResults></UnitTestResult>"
                + "</Results></TestRun>");

            Assert.Equal(3, counts.Total);
            Assert.Equal(2, counts.Passed);
            Assert.Equal(1, counts.Skipped);
            Assert.Equal(counts.Total, counts.Passed + counts.Failed + counts.Skipped);
        }

        [Fact]
        public void ResultsAreCountedWhenThereIsNoHeaderAtAll()
        {
            // A zero total is how the card says "nothing ran", so a headerless file must not report one
            // while holding results.
            var counts = Read($"<TestRun xmlns=\"{Ns}\"><Results>"
                + Result("Pass1", "Passed") + Result("Fail1", "Failed") + Result("Ignored1", "NotExecuted")
                + "</Results></TestRun>");

            Assert.Equal(3, counts.Total);
            Assert.Equal(1, counts.Passed);
            Assert.Equal(1, counts.Failed);
            Assert.Equal(1, counts.Skipped);
        }

        [Theory]
        [InlineData("Failed")]
        [InlineData("Error")]
        [InlineData("Timeout")]
        [InlineData("Aborted")]
        public void EveryFailingOutcomeIsAFailure(string outcome)
        {
            Assert.True(TrxSummary.IsFailure(outcome));
            Assert.False(TrxSummary.IsSkip(outcome));
            Assert.False(TrxSummary.IsPass(outcome));
        }

        [Theory]
        [InlineData("NotExecuted")]
        [InlineData("Inconclusive")]
        [InlineData("NotRunnable")]
        public void EveryDidNotRunOutcomeIsASkip(string outcome)
        {
            Assert.True(TrxSummary.IsSkip(outcome));
            Assert.False(TrxSummary.IsFailure(outcome));
        }

        [Fact]
        public void OutcomesMatchCaseInsensitivelyAndNullIsNoneOfThem()
        {
            Assert.True(TrxSummary.IsFailure("failed"));
            Assert.True(TrxSummary.IsSkip("notexecuted"));
            Assert.True(TrxSummary.IsPass("passedButRunAborted"));
            Assert.False(TrxSummary.IsFailure(null));
            Assert.False(TrxSummary.IsSkip(null));
            Assert.False(TrxSummary.IsPass(null));
        }

        // ---- which tests did not run, and why (TrxSummary.ReadSkipped) ----------------------------
        // The reason a runner records is the answer to the only question a bare "2 skipped" raises, and
        // all three write it to the same Output/ErrorInfo/Message a failure's message uses (measured).

        private static string Skipped(string name, string reason, string? testId = null) =>
            $"<UnitTestResult testName=\"{name}\" outcome=\"NotExecuted\""
            + (testId is null ? "" : $" testId=\"{testId}\"") + "><Output>"
            + $"<ErrorInfo><Message>{reason}</Message></ErrorInfo></Output></UnitTestResult>";

        /// <summary>The <c>TestDefinitions</c> block, which is where a test's declaring class is recorded.</summary>
        private static string Definitions(params (string TestId, string ClassName)[] tests) =>
            "<TestDefinitions>"
            + string.Concat(tests.Select(t =>
                $"<UnitTest id=\"{t.TestId}\"><TestMethod className=\"{t.ClassName}\" name=\"m\" /></UnitTest>"))
            + "</TestDefinitions>";

        [Fact]
        public void SkippedTestsAreNamedWithTheReasonTheRunnerGave()
        {
            // NUnit's [Ignore("…")] text and an Assert.Inconclusive message, in the shape NUnit 4 writes.
            var skipped = TrxSummary.ReadSkipped(XDocument.Parse(Run(
                Result("Pass1", "Passed")
                + Skipped("Ignored1", "This test is ignored.")
                + Skipped("Inconclusive1", "not ready yet"),
                Counters(total: 3, executed: 1, passed: 1, failed: 0))).Root);

            Assert.Equal(2, skipped.Count);
            Assert.Equal("Ignored1", skipped[0].Name);
            Assert.Equal("NotExecuted", skipped[0].Outcome);
            Assert.Equal("This test is ignored.", skipped[0].Reason);
            Assert.Equal("not ready yet", skipped[1].Reason);
        }

        [Fact]
        public void ASkippedTestWithNoRecordedReasonIsStillNamed()
        {
            // Absence of a reason is not absence of the test — the count would otherwise be the only
            // trace of it, which is the state this whole reader exists to end.
            var skipped = TrxSummary.ReadSkipped(XDocument.Parse(Run(
                Result("Pass1", "Passed") + Result("Ignored1", "NotExecuted"),
                Counters(total: 2, executed: 1, passed: 1, failed: 0))).Root);

            var only = Assert.Single(skipped);
            Assert.Equal("Ignored1", only.Name);
            Assert.Null(only.Reason);
        }

        [Fact]
        public void OnlySkippedTestsAreListed()
        {
            var skipped = TrxSummary.ReadSkipped(XDocument.Parse(Run(
                Result("Pass1", "Passed") + Result("Fail1", "Failed") + Skipped("Ignored1", "why"),
                Counters(total: 3, executed: 2, passed: 1, failed: 1))).Root);

            Assert.Equal("Ignored1", Assert.Single(skipped).Name);
        }

        [Fact]
        public void TheSkippedListAndTheCountAgreeOnWhatALeafIs()
        {
            // Both read the same walk. If only one of them descended through InnerResults, a card could
            // say "1 skipped" and name none of them — or name one it had not counted.
            var xml = $"<TestRun xmlns=\"{Ns}\"><Results>"
                + Result("Pass1", "Passed")
                + "<UnitTestResult testName=\"Data1\" outcome=\"NotExecuted\"><InnerResults>"
                + Result("Data1 (1)", "Passed") + Skipped("Data1 (2)", "row skipped")
                + "</InnerResults></UnitTestResult>"
                + "</Results></TestRun>";

            Assert.Equal(1, Read(xml).Skipped);
            var only = Assert.Single(TrxSummary.ReadSkipped(XDocument.Parse(xml).Root));
            Assert.Equal("Data1 (2)", only.Name);
        }

        [Fact]
        public void ASkippedTestCarriesTheClassThatDeclaresIt()
        {
            // NUnit and MSTest write a BARE method name, so the name alone cannot say which project's
            // TestMethod1 did not run. The class is recorded for tests that never ran too, which is what
            // makes the answer available at all.
            var xml = $"<TestRun xmlns=\"{Ns}\">"
                + Definitions(("t1", "nu.Tests"), ("t2", "nu.Tests+Nested"))
                + "<Results>" + Skipped("Ignored1", "This test is ignored.", "t1")
                + Skipped("Inner1", "nested and ignored", "t2") + "</Results></TestRun>";

            var skipped = TrxSummary.ReadSkipped(XDocument.Parse(xml).Root);

            Assert.Equal("Ignored1", skipped[0].Name);
            Assert.Equal("nu.Tests", skipped[0].ClassName);
            // Verbatim: '+' is the spelling a VSTest FullyQualifiedName filter takes for a nested class,
            // so normalising it to '.' would read better and match nothing.
            Assert.Equal("nu.Tests+Nested", skipped[1].ClassName);
        }

        [Fact]
        public void ASkippedTestWhoseClassIsNotRecordedIsStillNamed()
        {
            var skipped = TrxSummary.ReadSkipped(XDocument.Parse(Run(
                Skipped("Ignored1", "no definitions block here"),
                Counters(total: 1, executed: 0, passed: 0, failed: 0))).Root);

            var only = Assert.Single(skipped);
            Assert.Equal("Ignored1", only.Name);
            Assert.Null(only.ClassName);
        }

        [Fact]
        public void TheNameIsLeftAsTheRunnerWroteItAndNeverJoinedToTheClass()
        {
            // xUnit already qualifies its names, and a [Fact(DisplayName = "…")] result carries the display
            // text INSTEAD of the method name — indistinguishable from a bare NUnit name. So joining class
            // and name would be a confidently wrong value in the one case that cannot be detected. Both
            // facts are reported; neither is composed.
            var xml = $"<TestRun xmlns=\"{Ns}\">"
                + Definitions(("t1", "xu.UnitTest1"), ("t2", "xu.UnitTest1"))
                + "<Results>" + Skipped("xu.UnitTest1.Skipped1", "skipped on purpose", "t1")
                + Skipped("a friendly name", "also skipped", "t2") + "</Results></TestRun>";

            var skipped = TrxSummary.ReadSkipped(XDocument.Parse(xml).Root);

            Assert.Equal("xu.UnitTest1.Skipped1", skipped[0].Name);
            Assert.Equal("a friendly name", skipped[1].Name);
            Assert.All(skipped, s => Assert.Equal("xu.UnitTest1", s.ClassName));
        }

        [Fact]
        public void TheClassMapIsReadOnceForBothItsReaders()
        {
            // The skipped list and the failure walk's test-location rule share it; two readings could
            // disagree about what a test's class is, and only one of them is visible in the payload.
            var map = TrxSummary.ClassNamesByTestId(XDocument.Parse(
                $"<TestRun xmlns=\"{Ns}\">" + Definitions(("t1", "nu.Tests")) + "</TestRun>").Root);

            Assert.Equal("nu.Tests", map["t1"]);
            Assert.Empty(TrxSummary.ClassNamesByTestId(null));
        }

        [Fact]
        public void NoResultsMeansNoSkippedTests()
        {
            Assert.Empty(TrxSummary.ReadSkipped(null));
            Assert.Empty(TrxSummary.ReadSkipped(XDocument.Parse($"<TestRun xmlns=\"{Ns}\" />").Root));
        }

        [Fact]
        public void AnAbsentRootYieldsZeros()
        {
            var counts = TrxSummary.Read(null);
            Assert.Equal(0, counts.Total);
            Assert.Equal(0, counts.Passed);
            Assert.Equal(0, counts.Failed);
            Assert.Equal(0, counts.Skipped);
        }
    }
}
