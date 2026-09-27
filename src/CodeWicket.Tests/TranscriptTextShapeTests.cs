using System;
using System.Linq;
using CodeWicket.Ipc;
using CodeWicket.Shell;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// How a conversation the product RECORDED renders into the text a resume hands the summarizer —
    /// which is a different question from what that text says about images and captures.
    /// </summary>
    /// <remarks>
    /// <para>A streamed reply is recorded one entry per FRAME, so the transcript text has to put the
    /// frames back together: several `Assistant:` lines where the agent said one thing would tell the
    /// summarizer it had spoken several times, and the recap is the entire knowledge the next agent has
    /// of the exchange.</para>
    /// <para><b>The resume fixtures could not have caught this</b>, which is why it is written down
    /// here. They seeded a whole reply as one entry — so there was nothing to merge — and tagged it
    /// <c>Role = "assistant"</c>, which the product never writes. `SeededConversation` is the
    /// one seeder now, and it writes what a live turn writes.</para>
    /// </remarks>
    public sealed class TranscriptTextShapeTests
    {
        /// <summary>
        /// A reply that arrived in several frames is ONE thing the agent said.
        /// </summary>
        [Fact]
        public void AStreamedReplyIsOneAssistantLine()
        {
            var session = new PersistedSession();
            SeededConversation.AddExchange(session, "what changed?", "I rewrote the parser.", frames: 4);

            var text = ChatViewModel.TranscriptText(session);

            // The frames are a recording detail; the whole sentence is the fact.
            Assert.Contains("Assistant: I rewrote the parser.", text, StringComparison.Ordinal);
            Assert.Equal(1, Lines(text, "Assistant:"));
            Assert.Equal(1, Lines(text, "User:"));
        }

        /// <summary>
        /// And the turn's END is what separates two replies. Without it, two exchanges merge into one
        /// `Assistant:` line carrying both — the same defect in the other direction, and the reason the
        /// seeder closes every exchange rather than leaving the trailing flush to do it.
        /// </summary>
        [Fact]
        public void TwoTurnsStayTwoRepliesRatherThanMerging()
        {
            var session = new PersistedSession();
            SeededConversation.AddExchange(session, "first question", "first answer", frames: 2);
            SeededConversation.AddExchange(session, "second question", "second answer", frames: 2);

            var text = ChatViewModel.TranscriptText(session);

            Assert.Contains("Assistant: first answer", text, StringComparison.Ordinal);
            Assert.Contains("Assistant: second answer", text, StringComparison.Ordinal);
            Assert.Equal(2, Lines(text, "Assistant:"));
        }

        /// <summary>
        /// The split is a recording shape and never a change of content: a fixture that sizes a
        /// transcript against <c>ResumeDecider</c>'s threshold has to keep sizing it the same way,
        /// whatever the frame count, or every resume fixture's banner turns on an implementation detail
        /// of the seeder.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        [InlineData(7)]
        public void TheFrameCountChangesNeitherTheCharacterTotalNorTheText(int frames)
        {
            var reply = new string('a', 2500);
            var session = new PersistedSession();
            SeededConversation.AddExchange(session, new string('u', 2500), reply, frames);

            Assert.Equal(5000, ResumeDecider.TranscriptCharCount(session));
            Assert.Contains("Assistant: " + reply, ChatViewModel.TranscriptText(session), StringComparison.Ordinal);
        }

        /// <summary>
        /// And the role the seeder writes is the product's own, so nothing reading it can pass here and
        /// fail in the pane. A string no code reads is exactly the one a fixture can lie about
        /// indefinitely.
        /// </summary>
        [Fact]
        public void TheSeededRoleIsTheOneTheProductWrites()
        {
            var session = new PersistedSession();
            SeededConversation.AddExchange(session, "q", "a");

            Assert.Equal(SeededConversation.AgentRole, new TranscriptEntry().Role);
            Assert.All(
                session.Log.Where(e => e.Event is not null),
                e => Assert.Equal(SeededConversation.AgentRole, e.Role));
        }

        private static int Lines(string text, string prefix) =>
            text.Replace("\r\n", "\n").Split('\n').Count(l => l.StartsWith(prefix, StringComparison.Ordinal));
    }
}
