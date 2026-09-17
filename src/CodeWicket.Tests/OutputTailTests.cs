using System.Linq;
using CodeWicket.Core.Ide;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Trimming and filtering for the VS output panes we read — the Build pane behind
    /// <c>build_solution</c>'s failure backstop, and the Debug pane behind <c>get_debug_output</c>
    /// (issue #73).
    /// <para>
    /// Small logic, tested because the way it fails is invisible: keeping the wrong end of a long pane
    /// returns plausible, correctly formatted output describing a run nobody asked about, and nothing
    /// downstream can tell.
    /// </para>
    /// </summary>
    public sealed class OutputTailTests
    {
        [Fact]
        public void TextThatFitsIsUntouched()
        {
            const string text = "one\ntwo\nthree";

            Assert.Equal(text, OutputTail.Keep(text, 1000, out var trimmed));
            Assert.False(trimmed);
        }

        /// <summary>
        /// THE test. A pane accumulates across runs, so the newest output is what was asked about — and
        /// the oldest is what a head-trim would return, indistinguishably.
        /// </summary>
        [Fact]
        public void TheNEWESTOutputIsWhatSurvives()
        {
            var text = "OLDEST" + new string('x', 500) + "NEWEST";

            var kept = OutputTail.Keep(text, 100, out var trimmed);

            Assert.True(trimmed);
            Assert.EndsWith("NEWEST", kept);
            Assert.DoesNotContain("OLDEST", kept);
        }

        /// <summary>
        /// The notice is not decoration: a silently shortened pane reads as the whole of a short run, so
        /// a value that scrolled off looks like a value that was never printed.
        /// </summary>
        [Fact]
        public void TrimmingSaysHowMuchItDropped()
        {
            var kept = OutputTail.Keep(new string('x', 500), 100, out _);

            Assert.StartsWith("…(400 earlier chars trimmed)", kept);
        }

        /// <summary>
        /// The flag is reported, never sniffed back out of the returned string. A reader trusts it when
        /// deciding whether what it wanted might have scrolled off, so it must not depend on the notice's
        /// wording — nor be fooled by output that happens to look like a notice.
        /// </summary>
        [Fact]
        public void TheFlagDoesNotDependOnTheWording()
        {
            var looksLikeANotice = "…(999 earlier chars trimmed)\nbut nothing was";

            OutputTail.Keep(looksLikeANotice, 1000, out var trimmed);

            Assert.False(trimmed);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void AnEmptyNeedleKeepsEveryLine(string? needle)
        {
            var kept = OutputTail.LinesContaining("alpha\nbeta\ngamma", needle);

            Assert.Equal(3, kept.Count);
        }

        /// <summary>
        /// The Debug pane is mostly the debugger's own chatter, so the filter is what makes the read
        /// usable at all: the agent pulls back the tracepoint lines it wrote and nothing else.
        /// </summary>
        [Fact]
        public void TheFilterKeepsOnlyMatchingLines()
        {
            const string pane =
                "'App.exe' Loaded 'System.Private.CoreLib.dll'. Skipped loading symbols.\n" +
                "Recurse entered: depth=1 (MaxDepth=10)\n" +
                "'App.exe' Loaded 'System.Threading.dll'. Skipped loading symbols.\n" +
                "Recurse entered: depth=2 (MaxDepth=10)";

            var kept = OutputTail.LinesContaining(pane, "Recurse entered");

            Assert.Equal(2, kept.Count);
            Assert.All(kept, line => Assert.Contains("Recurse entered", line));
        }

        /// <summary>
        /// Case-insensitive because the caller is matching prose it wrote a turn earlier and re-typed
        /// from memory. An exact-case miss would report "nothing was printed" about output sitting right
        /// there — a false negative that reads as a finding.
        /// </summary>
        [Fact]
        public void TheFilterIgnoresCase()
        {
            var kept = OutputTail.LinesContaining("Recurse entered: depth=1", "RECURSE ENTERED");

            Assert.Single(kept);
        }

        [Fact]
        public void AFilterThatMatchesNothingKeepsNothing()
        {
            Assert.Empty(OutputTail.LinesContaining("alpha\nbeta", "gamma"));
        }

        /// <summary>
        /// The LAST n, for the same reason <see cref="OutputTail.Keep"/> keeps the tail: the newest
        /// output is the run being asked about.
        /// </summary>
        [Fact]
        public void LastLinesKeepsTheNewest()
        {
            var lines = Enumerable.Range(1, 100).Select(i => "line " + i).ToList();

            var kept = OutputTail.LastLines(lines, 3);

            Assert.Equal(new[] { "line 98", "line 99", "line 100" }, kept);
        }

        [Theory]
        [InlineData(null)]
        [InlineData(0)]
        [InlineData(-5)]
        public void AnAbsentOrMeaninglessCountKeepsEverything(int? count)
        {
            var lines = new[] { "a", "b", "c" };

            Assert.Equal(3, OutputTail.LastLines(lines, count).Count);
        }

        /// <summary>Asking for more than there is returns what there is, not a padded or empty result.</summary>
        [Fact]
        public void AskingForMoreThanExistsReturnsAllOfIt()
        {
            var lines = new[] { "a", "b" };

            Assert.Equal(2, OutputTail.LastLines(lines, 50).Count);
        }
    }
}
