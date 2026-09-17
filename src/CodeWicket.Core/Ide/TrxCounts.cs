using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace CodeWicket.Core.Ide
{
    /// <summary>The four counts one TRX contributes to a run_tests card.</summary>
    public readonly struct TrxCounts
    {
        public TrxCounts(int total, int passed, int failed, int skipped)
        {
            Total = total;
            Passed = passed;
            Failed = failed;
            Skipped = skipped;
        }

        public int Total { get; }
        public int Passed { get; }
        public int Failed { get; }
        public int Skipped { get; }
    }

    /// <summary>One test a TRX records as not having run, and the reason the runner gave for it.</summary>
    public readonly struct TrxSkippedTest
    {
        public TrxSkippedTest(string? name, string? className, string? outcome, string? reason)
        {
            Name = name;
            ClassName = className;
            Outcome = outcome;
            Reason = reason;
        }

        /// <summary>The name as the RUNNER wrote it, which is not the same thing across frameworks — see <see cref="ClassName"/>.</summary>
        public string? Name { get; }

        /// <summary>
        /// The declaring class, from <c>TestDefinitions</c>. What makes the name unambiguous: NUnit and
        /// MSTest write a bare method name, so two projects each holding a <c>TestMethod1</c> are
        /// indistinguishable by <see cref="Name"/> alone. Null when the file records none.
        /// </summary>
        public string? ClassName { get; }

        /// <summary>The TRX outcome as written (all three frameworks say <c>NotExecuted</c>), kept rather than normalised away.</summary>
        public string? Outcome { get; }

        /// <summary>Why it did not run, or null when the runner recorded none.</summary>
        public string? Reason { get; }
    }

    /// <summary>
    /// Reads the run counts out of a VSTest/MTP TRX (run_tests). In Core, and pure, for the reason
    /// <see cref="TestStackFrames"/> is: <c>VsToolCatalog</c> is net472 + VS-bound and no test project can
    /// reference it, so a rule left there is a rule no test can reach.
    /// <para>
    /// <b>The <c>&lt;Counters&gt;</c> header is not the record — the results are</b> (issue #259). The VSTest
    /// TRX logger writes <c>notExecuted="0"</c> whatever the per-result outcomes say: measured on SDK 10
    /// against NUnit 4 (<c>[Ignore]</c> + <c>Assert.Inconclusive</c>), xUnit v2 (<c>[Fact(Skip=…)]</c>) and
    /// MSTest 4 (<c>[Ignore]</c> + <c>Assert.Inconclusive</c>), each of which wrote <c>NotExecuted</c>
    /// results and a zero in the header — so this is the classic runner, not one adapter. (The MTP TRX
    /// writer gets it right, and <c>dotnet test</c>'s own console line does too, which is why the gap
    /// reads as ours.) Skipped tests therefore landed in NO bucket while still counting toward
    /// <c>total</c>, and a run containing them was indistinguishable from a clean one.
    /// </para>
    /// So: the header supplies total/passed/failed, and <b>skipped is whichever of the header and the
    /// results is larger</b> — the results can only ever raise it, never invent one the file doesn't
    /// record. It is then clamped so the buckets can't exceed the run's own total, because a bucket sum
    /// past <c>total</c> would be a second way for the card to fail to reconcile.
    /// <para>
    /// Only LEAF results are counted. A data-driven test may publish one aggregate
    /// <c>UnitTestResult</c> wrapping its rows in <c>InnerResults</c>, and the counters count the rows —
    /// counting the aggregate too would double-count it. (The modern NUnit/xUnit/MSTest adapters all
    /// write the rows flat, with no <c>InnerResults</c> at all.)
    /// </para>
    /// </summary>
    public static class TrxSummary
    {
        /// <summary>TRX outcomes that mean the test ran and failed. The vocabulary is shared with the failure-detail walk.</summary>
        public static bool IsFailure(string? outcome)
            => Is(outcome, "Failed") || Is(outcome, "Error") || Is(outcome, "Timeout") || Is(outcome, "Aborted");

        /// <summary>TRX outcomes that mean the test did not run — NUnit/xUnit/MSTest all report an ignored, skipped OR inconclusive test as <c>NotExecuted</c>.</summary>
        public static bool IsSkip(string? outcome)
            => Is(outcome, "NotExecuted") || Is(outcome, "Inconclusive") || Is(outcome, "NotRunnable");

        /// <summary>TRX outcomes that mean the test ran and passed (<c>PassedButRunAborted</c> is a pass whose RUN was cut short).</summary>
        public static bool IsPass(string? outcome)
            => Is(outcome, "Passed") || Is(outcome, "PassedButRunAborted");

        /// <summary>
        /// Reads one TRX's counts from its root element. A file with no <c>&lt;Counters&gt;</c> header at all
        /// is counted entirely from its results rather than reporting zeros, since a zero total is how the
        /// card says "nothing ran".
        /// </summary>
        public static TrxCounts Read(XElement? root)
        {
            var counters = root?.Elements().FirstOrDefault(e => e.Name.LocalName == "ResultSummary")
                ?.Elements().FirstOrDefault(e => e.Name.LocalName == "Counters");
            int Count(string attr) => int.TryParse((string?)counters?.Attribute(attr), out var v) ? v : 0;

            var results = CountResults(root);

            if (counters is null)
                return new TrxCounts(results.Total, results.Passed, results.Failed, results.Skipped);

            var total = Count("total");
            var passed = Count("passed") + Count("passedButRunAborted");
            var failed = Count("failed") + Count("error") + Count("timeout") + Count("aborted");
            var skipped = Math.Max(Count("notExecuted") + Count("inconclusive") + Count("notRunnable"),
                                   results.Skipped);
            // The results may only fill the header's gap, never overflow the run: what's left after the
            // tests the header says ran is the most that can have been skipped.
            skipped = Math.Min(skipped, Math.Max(0, total - passed - failed));
            return new TrxCounts(total, passed, failed, skipped);
        }

        /// <summary>
        /// testId → the declaring class, from <c>TestDefinitions/UnitTest/TestMethod@className</c>. Two
        /// callers need it and must agree: the skipped list, and the failure walk's test-location rule
        /// (<see cref="TestStackFrames.Test"/>), which uses it to VERIFY which frame is the test method.
        /// <para>
        /// It is recorded for tests that never ran too (measured on NUnit 4, xUnit v2 and MSTest 4), which
        /// is what lets a skipped test be named unambiguously. Kept VERBATIM: a nested class comes through
        /// as <c>Ns.Outer+Nested</c>, which is the spelling a VSTest <c>FullyQualifiedName</c> filter takes
        /// — normalising it here would produce a name that reads better and matches nothing.
        /// </para>
        /// </summary>
        public static IReadOnlyDictionary<string, string> ClassNamesByTestId(XElement? root)
        {
            var byId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var definitions = root?.Elements().FirstOrDefault(e => e.Name.LocalName == "TestDefinitions");
            if (definitions is null)
                return byId;
            foreach (var unitTest in definitions.Elements().Where(e => e.Name.LocalName == "UnitTest"))
            {
                var id = (string?)unitTest.Attribute("id");
                var className = (string?)unitTest.Elements().FirstOrDefault(e => e.Name.LocalName == "TestMethod")
                    ?.Attribute("className");
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(className))
                    byId[id!] = className!;
            }
            return byId;
        }

        /// <summary>
        /// The tests the file records as not having run, each with the reason the runner gave.
        /// <para>
        /// A skipped test's reason is written to the same <c>Output/ErrorInfo/Message</c> a failure's
        /// message is — the <c>[Ignore("…")]</c> text, an <c>Assert.Inconclusive</c> message, the xUnit
        /// <c>Skip</c> reason — measured on NUnit 4, xUnit v2 and MSTest 4 (SDK 10), which is why the count
        /// alone was never all the file had. A runner that records none leaves <c>Reason</c> null: the test
        /// is still named, absence of a reason not being absence of the test.
        /// </para>
        /// <para>
        /// The name is reported as the RUNNER wrote it and the class beside it, never joined into one
        /// "full name". NUnit and MSTest write a bare method name and xUnit writes a qualified one, so
        /// joining would be right for the first two — and for an xUnit <c>[Fact(DisplayName = "…")]</c>,
        /// whose result carries the display text and nothing else, it would produce a name that is neither
        /// the display name nor the test's own: a confidently wrong value in the one case that cannot be
        /// told from the ordinary one (all measured, SDK 10). Two facts the caller can join beat one
        /// composed field that is sometimes false.
        /// </para>
        /// Uncapped — the CALLER decides how many to report, since its cap spans every file in the run.
        /// </summary>
        public static IReadOnlyList<TrxSkippedTest> ReadSkipped(XElement? root)
        {
            var results = root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Results");
            if (results is null)
                return Array.Empty<TrxSkippedTest>();

            var classNames = ClassNamesByTestId(root);
            var skipped = new List<TrxSkippedTest>();
            foreach (var result in LeafResults(results))
            {
                var outcome = (string?)result.Attribute("outcome");
                if (!IsSkip(outcome))
                    continue;
                var reason = result.Elements().FirstOrDefault(e => e.Name.LocalName == "Output")
                    ?.Elements().FirstOrDefault(e => e.Name.LocalName == "ErrorInfo")
                    ?.Elements().FirstOrDefault(e => e.Name.LocalName == "Message")?.Value;
                if (string.IsNullOrWhiteSpace(reason))
                    reason = null;
                classNames.TryGetValue((string?)result.Attribute("testId") ?? string.Empty, out var className);
                skipped.Add(new TrxSkippedTest(
                    (string?)result.Attribute("testName"), className, outcome, reason?.Trim()));
            }
            return skipped;
        }

        /// <summary>Classifies every leaf <c>UnitTestResult</c> (see the type remarks on why leaves only).</summary>
        private static TrxCounts CountResults(XElement? root)
        {
            var results = root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Results");
            if (results is null)
                return default;

            int total = 0, passed = 0, failed = 0, skipped = 0;
            foreach (var outcome in LeafResults(results).Select(r => (string?)r.Attribute("outcome")))
            {
                total++;
                if (IsPass(outcome)) passed++;
                else if (IsFailure(outcome)) failed++;
                else if (IsSkip(outcome)) skipped++;
            }
            return new TrxCounts(total, passed, failed, skipped);
        }

        /// <summary>
        /// Each leaf result under <paramref name="container"/>, descending through <c>InnerResults</c> only.
        /// Shared by the counts and the skipped list so the two cannot disagree about what a result IS.
        /// </summary>
        private static IEnumerable<XElement> LeafResults(XElement container)
        {
            foreach (var result in container.Elements().Where(e => e.Name.LocalName == "UnitTestResult"))
            {
                var inner = result.Elements().FirstOrDefault(e => e.Name.LocalName == "InnerResults");
                if (inner is null)
                {
                    yield return result;
                    continue;
                }
                foreach (var nested in LeafResults(inner))
                    yield return nested;
            }
        }

        private static bool Is(string? outcome, string name)
            => string.Equals(outcome, name, StringComparison.OrdinalIgnoreCase);
    }
}
