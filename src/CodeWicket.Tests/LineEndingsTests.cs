using CodeWicket.Core.Ide;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Agent writes must adopt the file's existing line endings. Mixing them is what raises VS's modal
    /// "inconsistent line endings" dialog mid-turn, and it silently degenerates the minimal-edit
    /// computation into a whole-file rewrite (the common prefix stops at the first line break).
    /// </summary>
    public sealed class LineEndingsTests
    {
        [Fact]
        public void LfContentAdoptsCrlfFile()
        {
            var result = LineEndings.Match("a\nb\nc", "x\r\ny\r\nz");
            Assert.Equal("a\r\nb\r\nc", result);
        }

        [Fact]
        public void CrlfContentAdoptsLfFile()
        {
            var result = LineEndings.Match("a\r\nb\r\nc", "x\ny\nz");
            Assert.Equal("a\nb\nc", result);
        }

        [Fact]
        public void MatchingEndingsAreLeftAlone()
        {
            // Idempotent — must not double-convert into "\r\r\n".
            Assert.Equal("a\r\nb", LineEndings.Match("a\r\nb", "x\r\ny"));
            Assert.Equal("a\nb", LineEndings.Match("a\nb", "x\ny"));
        }

        [Fact]
        public void DominantEndingWins()
        {
            // An already-mixed file is repaired, not preserved: ACP writes carry the whole file, so
            // rewriting the content's endings normalizes every line.
            var mostlyCrlf = "a\r\nb\r\nc\nd";
            Assert.Equal("1\r\n2", LineEndings.Match("1\n2", mostlyCrlf));

            var mostlyLf = "a\nb\nc\r\nd";
            Assert.Equal("1\n2", LineEndings.Match("1\r\n2", mostlyLf));
        }

        [Fact]
        public void ContentWithMixedEndingsIsFullyNormalized()
        {
            var result = LineEndings.Match("a\r\nb\nc\r\nd", "x\r\ny");
            Assert.Equal("a\r\nb\r\nc\r\nd", result);
        }

        [Fact]
        public void NothingToMatchLeavesContentUntouched()
        {
            // New/empty file, or a single-line one: whatever the agent sent is consistent by definition.
            Assert.Equal("a\nb", LineEndings.Match("a\nb", ""));
            Assert.Equal("a\nb", LineEndings.Match("a\nb", null!));
            Assert.Equal("a\nb", LineEndings.Match("a\nb", "no line breaks here"));
            Assert.Equal("", LineEndings.Match("", "x\r\ny"));
            Assert.Null(LineEndings.Match(null!, "x\r\ny"));
        }

        [Fact]
        public void LoneCarriageReturnIsNeitherCountedNorRewritten()
        {
            // Classic-Mac endings are vanishingly rare; the rule is simply to leave them be rather than
            // to guess. A file of only lone \r has no countable ending, so content passes through.
            Assert.Equal("a\nb", LineEndings.Match("a\nb", "x\ry\rz"));
            Assert.Equal("a\rb\r\nc", LineEndings.Match("a\rb\nc", "x\r\ny"));
        }
    }
}
