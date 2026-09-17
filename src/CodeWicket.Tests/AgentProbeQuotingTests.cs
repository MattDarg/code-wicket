using CodeWicket.Shell;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// How a custom agent's launch arguments reach its CLI (<see cref="AcpAgentProbe.BuildArguments"/>).
    /// <para>
    /// net472 has no <c>ProcessStartInfo.ArgumentList</c>, so the command line is built by hand and the
    /// CRT on the other side parses it back by the documented Windows rules. Those rules are not
    /// symmetrical with "escape the quotes": a run of backslashes is doubled only where it PRECEDES a
    /// quote — including the closing quote the quoting itself adds.
    /// </para>
    /// <para>
    /// Getting it wrong is expensive out of proportion to the bug, because of where this code sits. A
    /// mangled argument makes the agent fail to start, and the probe reports "did not answer initialize"
    /// — a launch failure wearing the costume of a protocol failure, from the one screen whose entire
    /// job is telling the user why their custom agent will not run.
    /// </para>
    /// </summary>
    public sealed class AgentProbeQuotingTests
    {
        private static string Build(params string[] args) => AcpAgentProbe.BuildArguments(args);

        /// <summary>
        /// The reported shape: a directory argument ending in a separator. Escaping embedded quotes
        /// alone, this serialised as <c>"C:\Program Files\my agent\"</c>, whose final <c>\"</c> escapes
        /// the quote meant to close it — so the CLI receives one argument with everything after it
        /// glued on.
        /// </summary>
        [Fact]
        public void ATrailingBackslashDoesNotEscapeTheClosingQuote()
        {
            Assert.Equal("\"C:\\Program Files\\my agent\\\\\" --acp", Build(@"C:\Program Files\my agent\", "--acp"));
        }

        /// <summary>An interior backslash is literal: only a run before a quote is doubled.</summary>
        [Fact]
        public void InteriorBackslashesAreLeftAlone()
        {
            Assert.Equal("\"C:\\Program Files\\agent.exe\"", Build(@"C:\Program Files\agent.exe"));
        }

        /// <summary>An embedded quote is escaped, as it always was.</summary>
        [Fact]
        public void AnEmbeddedQuoteIsEscaped()
        {
            Assert.Equal("\"say \\\"hello\\\"\"", Build("say \"hello\""));
        }

        /// <summary>
        /// ...and the backslashes in front of one are doubled, which is the other half of the same rule.
        /// One backslash then a quote must arrive as a literal backslash followed by a literal quote.
        /// </summary>
        [Fact]
        public void BackslashesBeforeAnEmbeddedQuoteAreDoubled()
        {
            Assert.Equal("\"a\\\\\\\"b\"", Build("a\\\"b"));
        }

        /// <summary>
        /// An argument needing no quoting is still passed through bare — the fast path is what keeps
        /// ordinary command lines readable in the log, so it must not be swept up by the fix.
        /// </summary>
        [Theory]
        [InlineData("--acp")]
        [InlineData(@"C:\tools\agent.exe")]
        public void AnArgumentWithNothingToQuoteIsUnchanged(string arg)
        {
            Assert.Equal(arg, Build(arg));
        }

        [Fact]
        public void ArgumentsAreSpaceSeparated()
        {
            Assert.Equal("--agent kiro --acp", Build("--agent", "kiro", "--acp"));
        }
    }
}
