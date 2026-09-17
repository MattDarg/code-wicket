using CodeWicket.Core.Ide;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The marker that says a breakpoint is the agent's, and which conversation set it (issue #73).
    /// It rides the breakpoint rather than a host-side list because breakpoints live in the <c>.suo</c>
    /// and outlive both the chat window and devenv — verified in Visual Studio, along with the tracepoint message,
    /// the condition and the hit count.
    /// <para>
    /// Every test here is ultimately about the same risk: a tag we claim is a breakpoint we will
    /// DELETE, so both halves of the match have to be wrong-in-the-safe-direction.
    /// </para>
    /// </summary>
    public sealed class BreakpointTagTests
    {
        private const string Conversation = "9f2c41ab7e0d4b5c8a1e6f3d2b7c0e15";
        private const string Other = "1122334455667788990011223344556677";

        [Fact]
        public void ATagWeWroteIsRecognisedAsOurs()
        {
            Assert.True(Breakpoints.IsAgentTag(Breakpoints.Tag(Conversation)));
        }

        [Fact]
        public void AForeignTagIsNotOurs()
        {
            Assert.False(Breakpoints.IsAgentTag("something-else"));
            Assert.False(Breakpoints.IsAgentTag(""));
            Assert.False(Breakpoints.IsAgentTag(null));
        }

        /// <summary>
        /// The colon is the guard, not punctuation. Without it the prefix test would also claim a tag
        /// belonging to some future "code-wicket-helper" — and claiming it means deleting it.
        /// </summary>
        [Fact]
        public void ANeighbouringNameIsNotClaimed()
        {
            Assert.False(Breakpoints.IsAgentTag("code-wicket-helper:abc"));
            Assert.False(Breakpoints.IsAgentTag("code-wicket"));
        }

        /// <summary>The two scopes the single field has to serve: any conversation, and this one.</summary>
        [Fact]
        public void TheTagSeparatesConversationsWhileStayingOurs()
        {
            var mine = Breakpoints.Tag(Conversation);
            var theirs = Breakpoints.Tag(Other);

            Assert.NotEqual(mine, theirs);
            // Both are ours — "clear everything the agent set" must find both...
            Assert.True(Breakpoints.IsAgentTag(mine));
            Assert.True(Breakpoints.IsAgentTag(theirs));
            // ...while "clear what THIS conversation set" separates them.
            Assert.True(Breakpoints.IsFromConversation(mine, Conversation));
            Assert.False(Breakpoints.IsFromConversation(theirs, Conversation));
        }

        /// <summary>
        /// A conversation that cannot identify itself matches NOTHING — not even the tags that equally
        /// could not identify themselves. Two breakpoints that both failed to record where they came
        /// from are not thereby from the same place, and the cost of that mistake is deleting someone
        /// else's breakpoint.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("---")]
        public void AnUnidentifiableConversationMatchesNothing(string? conversationId)
        {
            var unknownTag = Breakpoints.Tag(conversationId);

            // Still ours: it is a breakpoint the agent set, and "clear all" must still reach it.
            Assert.True(Breakpoints.IsAgentTag(unknownTag));
            // But it belongs to no conversation, including another unidentifiable one.
            Assert.False(Breakpoints.IsFromConversation(unknownTag, conversationId));
            Assert.False(Breakpoints.IsFromConversation(unknownTag, Conversation));
            Assert.False(Breakpoints.IsFromConversation(Breakpoints.Tag(Conversation), conversationId));
        }

        /// <summary>
        /// A suffix that could reintroduce the separator would make the prefix test ambiguous. Ids are
        /// GUIDs today and cannot, but a guard that holds only while nobody changes the id format is not
        /// a guard.
        /// </summary>
        [Fact]
        public void TheSuffixCannotReintroduceTheSeparator()
        {
            var tag = Breakpoints.Tag("ab:cd/ef gh");

            Assert.Equal(Breakpoints.TagPrefix + "abcdefgh", tag);
            // Exactly one colon in the whole tag — the separator — so splitting on it can only ever
            // yield the marker and the id, whatever an id turns into later.
            Assert.Equal(2, tag.Split(':').Length);
        }

        /// <summary>Ids differing only in case or punctuation are the same conversation.</summary>
        [Fact]
        public void TheIdIsNormalised()
        {
            Assert.Equal(Breakpoints.Tag("9F2C41AB-7E0D-4B5C"), Breakpoints.Tag("9f2c41ab7e0d4b5c"));
            Assert.True(Breakpoints.IsFromConversation(Breakpoints.Tag(Conversation), Conversation.ToUpperInvariant()));
        }

        /// <summary>
        /// Two conversations agreeing on their first characters would share a tag. Distinctness is what
        /// the suffix is FOR, so it is asserted on a realistic id rather than assumed.
        /// </summary>
        [Fact]
        public void RealisticIdsProduceDistinctTags()
        {
            var a = Breakpoints.Tag("9f2c41ab7e0d4b5c8a1e6f3d2b7c0e15");
            var b = Breakpoints.Tag("9f2c41ac7e0d4b5c8a1e6f3d2b7c0e15");

            Assert.NotEqual(a, b);
        }
    }
}
