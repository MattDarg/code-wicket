using CodeWicket.Core.Ide;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Pins the hunk→full-file widening behind the transcript's diff cards. Every case here is a
    /// bail-out condition or a disambiguation rule that previously lived in the VS-bound Ide project
    /// where none of it could be reached without devenv — the reason it moved to Core.
    ///
    /// What matters about the bail-outs: returning null is not a failure, it's the caller falling back
    /// to showing the raw reported hunk. Getting one WRONG is the bad outcome — a confidently
    /// reconstructed "before" side that never existed, presented to the user as what their file used
    /// to look like.
    /// </summary>
    public sealed class DiffSideBuilderTests
    {
        [Fact]
        public void TryBuild_SplicesTheHunkBackOutToRebuildTheBeforeSide()
        {
            var current = "line one\nCHANGED\nline three\n";

            var sides = DiffSideBuilder.TryBuild(current, oldText: "ORIGINAL", newText: "CHANGED", reportedLine: null);

            Assert.NotNull(sides);
            Assert.Equal("line one\nORIGINAL\nline three\n", sides!.Before);
            Assert.Equal(current, sides.After); // "after" IS the file as it stands
            Assert.Equal(2, sides.Line);
        }

        [Fact]
        public void TryBuild_NormalizesCrLfSoAnLfHunkStillMatches()
        {
            // The agent reports \n; the file on disk is \r\n. Without normalization nothing matches
            // and every diff on a CRLF file silently degrades to the hunk-only view.
            var sides = DiffSideBuilder.TryBuild(
                current: "a\r\nCHANGED\r\nb\r\n", oldText: "ORIGINAL", newText: "CHANGED", reportedLine: null);

            Assert.NotNull(sides);
            Assert.Equal("a\nORIGINAL\nb\n", sides!.Before);
            Assert.Equal("a\nCHANGED\nb\n", sides.After);
        }

        [Fact]
        public void TryBuild_ReturnsNullWhenThereIsNoCurrentContent()
        {
            Assert.Null(DiffSideBuilder.TryBuild(null, "old", "new", null));
        }

        [Fact]
        public void TryBuild_ReturnsNullForAWholeFileWrite()
        {
            // New text IS the file — there is no surrounding context to widen into.
            Assert.Null(DiffSideBuilder.TryBuild("whole file", "old", "whole file", null));
        }

        [Theory]
        [InlineData(null, "new")]   // pure new-file / no previous text
        [InlineData("old", "")]     // pure deletion
        public void TryBuild_ReturnsNullForANonHunkEdit(string? oldText, string? newText)
        {
            Assert.Null(DiffSideBuilder.TryBuild("some file content", oldText, newText, null));
        }

        [Fact]
        public void TryBuild_ReturnsNullWhenTheHunkIsNoLongerPresent()
        {
            // A later edit moved past this hunk: reconstructing anything here would be fiction.
            Assert.Null(DiffSideBuilder.TryBuild("totally different now", "old", "MISSING", null));
        }

        [Fact]
        public void TryBuild_WithNoReportedLine_RequiresAUniqueOccurrence()
        {
            // "x" appears twice and nothing says which one — splicing the wrong one would show the
            // user a "before" for an edit that happened elsewhere in the file.
            Assert.Null(DiffSideBuilder.TryBuild("x\nmiddle\nx\n", "was", "x", reportedLine: null));
        }

        [Fact]
        public void TryBuild_WithAReportedLine_PicksTheNearestOccurrence()
        {
            var current = "x\nmiddle\nx\n";

            var sides = DiffSideBuilder.TryBuild(current, "was", "x", reportedLine: 3);

            Assert.NotNull(sides);
            Assert.Equal(3, sides!.Line);                  // the second "x", not the first
            Assert.Equal("x\nmiddle\nwas\n", sides.Before);
        }

        [Fact]
        public void TryBuild_RejectsAMatchGrosslyFarFromTheReportedLine()
        {
            // A coincidental identical string on line 1 when the edit was reported ~1000 lines down:
            // beyond the tolerance we distrust the match rather than reconstruct from it.
            var current = "target\n" + string.Concat(System.Linq.Enumerable.Repeat("filler\n", 2000));

            Assert.Null(DiffSideBuilder.TryBuild(
                current, "was", "target", reportedLine: DiffSideBuilder.ReportedLineTolerance + 100));
        }

        [Fact]
        public void TryBuild_AcceptsAMatchInsideTheTolerance()
        {
            // The companion to the case above — the tolerance is generous by design, because
            // legitimate shift (later edits inserting lines above) can be large.
            var current = "target\n" + string.Concat(System.Linq.Enumerable.Repeat("filler\n", 2000));

            var sides = DiffSideBuilder.TryBuild(
                current, "was", "target", reportedLine: DiffSideBuilder.ReportedLineTolerance - 100);

            Assert.NotNull(sides);
            Assert.Equal(1, sides!.Line);
        }

        // ---- LocateLine: where "Open file" on an edit card lands ------------------------------
        //
        // The bad outcome here is different from the widening above, and milder: a wrong line scrolls
        // the user to the wrong place, where a wrong splice would show them a file state that never
        // existed. What it must never do is silently answer "the top" when it could have done better,
        // because that is indistinguishable from the bug this replaced.

        /// <summary>
        /// The reported line is what the backend saw when it made the edit; later edits above it move
        /// the code out from under it. Finding the text in the file as it stands now is what makes the
        /// navigation right, so a stale report must LOSE to a successful lookup — not be preferred to it.
        /// </summary>
        [Fact]
        public void LocateLine_PrefersWhereTheEditIsNowOverTheLineReportedThen()
        {
            var current = "added\nadded\nadded\nCHANGED\ntail\n";

            Assert.Equal(4, DiffSideBuilder.LocateLine(current, "ORIGINAL", "CHANGED", reportedLine: 1));
        }

        /// <summary>70% of recorded edits carry no line at all — the whole reason a lookup is needed.</summary>
        [Fact]
        public void LocateLine_FindsTheEditWithNoReportedLineAtAll()
        {
            var current = "one\ntwo\nCHANGED\nfour\n";

            Assert.Equal(3, DiffSideBuilder.LocateLine(current, "ORIGINAL", "CHANGED", reportedLine: null));
        }

        /// <summary>
        /// A whole-file write (a mirrored Kiro edit) has no hunk to find — the new text IS the file —
        /// so the edit is the first line that actually changed. Without this branch every such card
        /// would open at the top, which is exactly the reported complaint.
        /// </summary>
        [Fact]
        public void LocateLine_UsesTheFirstChangedLineForAWholeFileWrite()
        {
            var before = "one\ntwo\nthree\nfour\n";
            var after = "one\ntwo\nTHREE!\nfour\n";

            Assert.Equal(3, DiffSideBuilder.LocateLine(after, before, after, reportedLine: null));
        }

        /// <summary>A pure append diverges at the end of the old text — the first appended line.</summary>
        [Fact]
        public void LocateLine_PointsAtTheFirstAppendedLine()
        {
            var before = "one\ntwo\n";
            var after = "one\ntwo\nthree\n";

            Assert.Equal(3, DiffSideBuilder.LocateLine(after, before, after, reportedLine: null));
        }

        /// <summary>A new file's whole content is "changed", so the top is genuinely the answer.</summary>
        [Fact]
        public void LocateLine_PutsANewFileAtItsFirstLine()
            => Assert.Equal(1, DiffSideBuilder.LocateLine("hello\n", oldText: "", newText: "hello\n", reportedLine: null));

        /// <summary>
        /// The edit was overwritten by later work, so there is nothing to find. The reported line is
        /// then the best guess left — stale, but better than the top.
        /// </summary>
        [Fact]
        public void LocateLine_FallsBackToTheReportedLineWhenTheEditIsGone()
            => Assert.Equal(42, DiffSideBuilder.LocateLine("nothing like it\n", "was", "CHANGED", reportedLine: 42));

        /// <summary>…and with no report either, the top is the honest answer.</summary>
        [Fact]
        public void LocateLine_FallsBackToTheTopWhenNothingIsKnown()
            => Assert.Equal(0, DiffSideBuilder.LocateLine("nothing like it\n", "was", "CHANGED", reportedLine: null));

        /// <summary>An unreadable/missing file is the same "nothing known" case, not a crash.</summary>
        [Fact]
        public void LocateLine_HandlesNoFileContent()
            => Assert.Equal(7, DiffSideBuilder.LocateLine(current: null, "was", "CHANGED", reportedLine: 7));

        /// <summary>
        /// Repeated text is disambiguated by the reported line — the same rule <c>TryBuild</c> splices
        /// by, since both now share <c>LocateHunk</c>. They must never name different lines for one
        /// edit: the diff window's caption states the line this navigates to.
        /// </summary>
        [Fact]
        public void LocateLine_AgreesWithTheSpliceOnWhichRepeatedHunkIsTheEdit()
        {
            var current = "CHANGED\nfiller\nfiller\nCHANGED\n";

            var sides = DiffSideBuilder.TryBuild(current, "was", "CHANGED", reportedLine: 4);
            Assert.NotNull(sides);
            Assert.Equal(sides!.Line, DiffSideBuilder.LocateLine(current, "was", "CHANGED", reportedLine: 4));
            Assert.Equal(4, sides.Line);
        }

        /// <summary>
        /// …and inherits its caution: repeated text with no line to disambiguate is not guessed at.
        /// Picking the first occurrence would be a coin toss dressed as an answer.
        /// </summary>
        [Fact]
        public void LocateLine_WontGuessBetweenRepeatedHunksWithNoReportedLine()
            => Assert.Equal(0, DiffSideBuilder.LocateLine("CHANGED\nfiller\nCHANGED\n", "was", "CHANGED", reportedLine: null));

        /// <summary>Line endings must not decide the answer: a \n hunk still locates in a \r\n file.</summary>
        [Fact]
        public void LocateLine_MatchesAcrossLineEndings()
            => Assert.Equal(2, DiffSideBuilder.LocateLine("one\r\nCHANGED\r\nthree\r\n", "was", "CHANGED", reportedLine: null));

        // --- a report of 0 is not a report ------------------------------------------------------

        /// <summary>
        /// Lines are 1-based, so 0 names no line — but a backend can still send it, since
        /// <c>AcpMapper.ParseLocationLines</c> takes any number the <c>locations</c> array carries.
        /// <para>
        /// Read as a real anchor it measures the tolerance against the top of the file, so a UNIQUE and
        /// correct match past line <see cref="DiffSideBuilder.ReportedLineTolerance"/> is rejected as a
        /// gross mismatch and the widened diff degrades to the bare hunk. The two halves disagreed
        /// about this: <c>LocateLine</c> already filtered 0 out of its own fallback, then handed the
        /// unfiltered value to the shared <c>LocateHunk</c>.
        /// </para>
        /// </summary>
        [Fact]
        public void TryBuild_TreatsAReportOfZeroAsNoReportRatherThanTheTopOfTheFile()
        {
            var current = new string('\n', 900) + "CHANGED\n";

            var sides = DiffSideBuilder.TryBuild(current, oldText: "was", newText: "CHANGED", reportedLine: 0);

            Assert.NotNull(sides);
            Assert.Equal(901, sides!.Line);
        }

        /// <summary>The navigation half of the same edit, which used to land at the top of the file.</summary>
        [Fact]
        public void LocateLine_TreatsAReportOfZeroAsNoReport()
        {
            var current = new string('\n', 900) + "CHANGED\n";

            Assert.Equal(901, DiffSideBuilder.LocateLine(current, "was", "CHANGED", reportedLine: 0));
        }

        /// <summary>
        /// Dropping 0 must not promote it to a tie-breaker either: with no usable report, repeated text
        /// still isn't guessed at — the same caution a null report gets.
        /// </summary>
        [Fact]
        public void TryBuild_WithAReportOfZero_StillRequiresAUniqueOccurrence()
            => Assert.Null(DiffSideBuilder.TryBuild("x\nmiddle\nx\n", "was", "x", reportedLine: 0));
    }
}
