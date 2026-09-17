using CodeWicket.Core.Ide;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The two-tier get_diagnostics de-dup: span-aware WITHIN a source (so distinct same-line diagnostics
    /// both count — the under-count that motivated this), coarse ACROSS sources (so an overlapping Error List
    /// row collapses onto the Roslyn one even if their columns drift, never double-reporting).
    /// </summary>
    public sealed class DiagnosticDeduperTests
    {
        const string F = @"C:\ws\Foo.cs";

        // Two genuinely-distinct warnings on one line at different columns (both from Roslyn) must BOTH count —
        // the coarse (file,line,code) key merged them (e.g. two CS8602 at (68,28) and (68,40)).
        [Fact]
        public void SameLineDifferentColumn_BothSurvive()
        {
            var d = new DiagnosticDeduper();
            Assert.True(d.TryAddRoslyn(F, 68, 28, 68, 40, "CS8602"));
            Assert.True(d.TryAddRoslyn(F, 68, 40, 68, 52, "CS8602"));
        }

        // The real XmlRequestReader.cs pair from issue #47: same file, line, code AND start column, differing
        // only in where the span ENDS. A start-column key merged them, under-counting get_diagnostics by one
        // against the build (which counts Error List rows 1:1 and so reported both).
        [Fact]
        public void SameStartColumnDifferentEndColumn_BothSurvive()
        {
            var d = new DiagnosticDeduper();
            Assert.True(d.TryAddRoslyn(F, 68, 28, 68, 40, "CS8602"));
            Assert.True(d.TryAddRoslyn(F, 68, 28, 68, 59, "CS8602"));
        }

        // A multi-line span differing only in its end LINE is likewise distinct.
        [Fact]
        public void SameStartDifferentEndLine_BothSurvive()
        {
            var d = new DiagnosticDeduper();
            Assert.True(d.TryAddRoslyn(F, 30, 9, 30, 20, "CS0618"));
            Assert.True(d.TryAddRoslyn(F, 30, 9, 32, 20, "CS0618"));
        }

        // An exact repeat (same file/line/code and the same full span) is a true duplicate and is dropped —
        // including the same source file compiled once per TFM by a multi-targeted project, whose two
        // diagnostics agree on every component of the key.
        [Fact]
        public void ExactDuplicate_Deduped()
        {
            var d = new DiagnosticDeduper();
            Assert.True(d.TryAddRoslyn(F, 10, 5, 10, 12, "CS0219"));
            Assert.False(d.TryAddRoslyn(F, 10, 5, 10, 12, "CS0219"));
        }

        // The same analyzer code reported by both Roslyn and the Error List (Full scope) collapses onto the
        // Roslyn row — and stays collapsed even when the two sources disagree on the column by one, because the
        // cross-source check is coarse. This is the whole reason the key isn't span-inclusive everywhere: the
        // Error List row carries no end span to compare against.
        [Fact]
        public void ErrorList_CollapsesOntoRoslyn_EvenOnColumnDrift()
        {
            var d = new DiagnosticDeduper();
            Assert.True(d.TryAddRoslyn(F, 12, 5, 12, 30, "CA1822"));
            Assert.False(d.TryAddErrorList(F, 12, 6, "CA1822")); // drifted column, still dropped
            Assert.False(d.TryAddErrorList(F, 12, 5, "CA1822")); // exact, also dropped
        }

        // The Roslyn and Error List fine keys have distinct shapes, so an Error List row can never be dropped
        // by *colliding* with a Roslyn key — only ever by the coarse cross-source check above. Here a Roslyn
        // row on a different line leaves the Error List row untouched.
        [Fact]
        public void ErrorList_NotBlockedByUnrelatedRoslynFineKey()
        {
            var d = new DiagnosticDeduper();
            Assert.True(d.TryAddRoslyn(F, 40, 7, 40, 15, "CA1822"));
            Assert.True(d.TryAddErrorList(F, 41, 7, "CA1822"));
        }

        // Two distinct Error List rows on one line at different columns both survive (fine key within a source),
        // matching how the Error List itself shows them as separate rows.
        [Fact]
        public void DistinctErrorListRows_BothSurvive()
        {
            var d = new DiagnosticDeduper();
            Assert.True(d.TryAddErrorList(F, 20, 3, "IDE0051"));
            Assert.True(d.TryAddErrorList(F, 20, 15, "IDE0051"));
        }

        // With no Roslyn row claiming its coarse key, an Error List row is kept.
        [Fact]
        public void ErrorListOnly_NoRoslyn_Survives()
        {
            var d = new DiagnosticDeduper();
            Assert.True(d.TryAddErrorList(F, 1, 1, "NU1903"));
        }

        // Drive-letter / path case doesn't split a diagnostic (keys are case-insensitive, matching the rest of
        // the diagnostic path handling).
        [Fact]
        public void PathCaseInsensitive()
        {
            var d = new DiagnosticDeduper();
            Assert.True(d.TryAddRoslyn(@"C:\ws\A.cs", 5, 2, 5, 9, "CS0168"));
            Assert.False(d.TryAddRoslyn(@"c:\ws\A.cs", 5, 2, 5, 9, "CS0168"));
        }
    }
}
