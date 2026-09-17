using CodeWicket.Core.Ide;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The markers three layers that cannot see each other have to agree on — the ACP mapper's clamp
    /// exemption, the shell's side-channel, and the view-model's card dispatch (issue #83, extended for
    /// the breakpoint payloads in issue #73).
    /// </summary>
    public sealed class StructuredResultMarkerTests
    {
        [Fact]
        public void EachMarker_recognisesOnlyItsOwnPayload()
        {
            const string testRun = "{\"resultKind\":\"testRun\",\"succeeded\":true}";
            const string breakpoints = "{\"resultKind\":\"breakpoints\",\"set\":2}";

            Assert.True(StructuredToolResult.IsTestRun(testRun));
            Assert.False(StructuredToolResult.IsBreakpoints(testRun));

            Assert.True(StructuredToolResult.IsBreakpoints(breakpoints));
            Assert.False(StructuredToolResult.IsTestRun(breakpoints));
        }

        /// <summary>
        /// The transport-side question is "is this ours", never a list of kinds. A reader that names
        /// kinds is a reader someone has to remember to extend, and forgetting reproduces issue #83:
        /// the payload is clamped mid-string and throws where the card is parsed.
        /// </summary>
        [Fact]
        public void IsStructured_coversEveryKind()
        {
            Assert.True(StructuredToolResult.IsStructured("{\"resultKind\":\"testRun\",\"a\":1}"));
            Assert.True(StructuredToolResult.IsStructured("{\"resultKind\":\"breakpoints\",\"a\":1}"));
            Assert.False(StructuredToolResult.IsStructured("{\"resultKind\":\"somethingElse\"}"));
            Assert.False(StructuredToolResult.IsStructured(null));
            Assert.False(StructuredToolResult.IsStructured(string.Empty));
        }

        /// <summary>
        /// PREFIX, never Contains: the producer serializes resultKind first precisely so that ordinary
        /// output which merely quotes the marker — a file-read of this repo's own source, say — cannot
        /// false-positive into a card.
        /// </summary>
        [Fact]
        public void QuotingTheMarker_isNotAPayload()
        {
            var quoted = "the marker is " + StructuredToolResult.TestRunPrefix + " and it is a prefix";
            Assert.False(StructuredToolResult.IsTestRun(quoted));

            var quotedBreakpoints = "see " + StructuredToolResult.BreakpointsPrefix;
            Assert.False(StructuredToolResult.IsBreakpoints(quotedBreakpoints));
            Assert.False(StructuredToolResult.IsStructured(quotedBreakpoints));
        }

        /// <summary>
        /// Two kinds must not share a synthetic id. The view-model falls back to it when deduping a
        /// side-channel delivery, so one shared value would make two different payloads look like the
        /// same delivery and silently drop whichever arrived second.
        /// </summary>
        [Fact]
        public void SyntheticIds_areDistinctPerKind()
        {
            var testRun = StructuredToolResult.SyntheticToolCallId("{\"resultKind\":\"testRun\"}");
            var breakpoints = StructuredToolResult.SyntheticToolCallId("{\"resultKind\":\"breakpoints\"}");

            Assert.NotNull(testRun);
            Assert.NotNull(breakpoints);
            Assert.NotEqual(testRun, breakpoints);
        }

        [Fact]
        public void SyntheticId_isNullForAnythingElse()
        {
            Assert.Null(StructuredToolResult.SyntheticToolCallId("plain tool output"));
            Assert.Null(StructuredToolResult.SyntheticToolCallId(null));
        }
    }
}
