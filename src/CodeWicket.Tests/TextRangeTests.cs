using CodeWicket.Core.Ide;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The line-range half of <c>fs/read_text_file</c> (<see cref="TextRange.Slice"/>), which had no
    /// coverage at all.
    /// <para>
    /// Both ends of the range are agent-supplied and reach here unvalidated — <c>AcpClientTarget</c>
    /// forwards what the backend sent and <c>VsEditApplier</c> passes it on — so the cases that matter
    /// are the nonsensical ones. Two outcomes are wrong for them and only one is loud: throwing turns
    /// "that range holds nothing" into a JSON-RPC error, and widening a bad range to the whole file
    /// hands the agent 4,000 lines when it asked for 20, which is the defect this type was extracted
    /// to prevent and the one it cannot see happening.
    /// </para>
    /// </summary>
    public sealed class TextRangeTests
    {
        private const string Content = "one\ntwo\nthree\nfour\nfive\n";

        [Fact]
        public void BothNull_ReturnsTheContentUntouched()
            => Assert.Same(Content, TextRange.Slice(Content, line: null, limit: null));

        [Fact]
        public void ReadsTheRequestedWindow()
            => Assert.Equal("two\nthree", TextRange.Slice(Content, line: 2, limit: 2));

        [Fact]
        public void LineAloneReadsToTheEnd()
            => Assert.Equal("four\nfive\n", TextRange.Slice(Content, line: 4, limit: null));

        [Fact]
        public void LimitAloneReadsFromTheTop()
            => Assert.Equal("one\ntwo", TextRange.Slice(Content, line: null, limit: 2));

        [Fact]
        public void LimitPastTheEndYieldsWhatIsThere()
            => Assert.Equal("five\n", TextRange.Slice(Content, line: 5, limit: 999));

        [Fact]
        public void StartPastTheEndIsEmptyNotAnError()
            => Assert.Equal(string.Empty, TextRange.Slice(Content, line: 500, limit: 10));

        [Fact]
        public void CrLfIsNormalizedSoTheLineCountIsTheSameEitherWay()
            => Assert.Equal("two\nthree", TextRange.Slice("one\r\ntwo\r\nthree\r\nfour\r\n", line: 2, limit: 2));

        /// <summary>
        /// A line below 1 clamps to the first line rather than reading backwards off the front. Lines
        /// are 1-based on the wire, so 0 is the value a backend sends when it means "the top".
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public void ANonPositiveLineStartsAtTheTop(int line)
            => Assert.Equal("one\ntwo", TextRange.Slice(Content, line, limit: 2));

        /// <summary>
        /// A negative limit is zero lines. It used to reach <c>string.Join(sep, array, start, count)</c>
        /// as a negative count and throw <c>ArgumentOutOfRangeException</c> — the caller asked for a
        /// nonsense range and got "couldn't look" instead of "nothing there".
        /// </summary>
        [Theory]
        [InlineData(-1)]
        [InlineData(int.MinValue)]
        public void ANegativeLimitIsEmptyNotAnError(int limit)
            => Assert.Equal(string.Empty, TextRange.Slice(Content, line: 2, limit));

        /// <summary>
        /// And specifically NOT the whole file. Reading a negative limit as "no limit" would be the
        /// quiet failure: the agent asks for a window, is handed everything, and nothing in the reply
        /// says its range was dropped.
        /// </summary>
        [Fact]
        public void ANegativeLimitDoesNotWidenToTheWholeFile()
            => Assert.NotEqual(Content, TextRange.Slice(Content, line: null, limit: -1));

        [Fact]
        public void ZeroLimitIsEmpty()
            => Assert.Equal(string.Empty, TextRange.Slice(Content, line: 1, limit: 0));

        [Fact]
        public void EmptyContentIsEmptyForAnyRange()
            => Assert.Equal(string.Empty, TextRange.Slice(string.Empty, line: 3, limit: 3));
    }
}
