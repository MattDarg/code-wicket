using System.Text.Json;
using CodeWicket.Providers.Acp;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// What issue #185's replay count counts, and — the half that matters — what it does not. A
    /// <c>session/load</c> that restored nothing still announces the session's commands and mode, and
    /// on Kiro v3 it also RUNS the new session's opening tool call inside the load window and replays
    /// it marked as history (measured 2026-09-12: four tool frames for an id it never held). A count
    /// of either would call an empty load full, and the check built on it would never fire on the one
    /// case it exists for. Messages are the evidence: a session opening yields tool calls (v3's cloud
    /// settings sync) and never a message, while a conversation with any history has at least its
    /// first user message.
    /// </summary>
    public sealed class ConversationContentFrameTests
    {
        [Theory]
        [InlineData("user_message_chunk")]
        [InlineData("agent_message_chunk")]
        [InlineData("agent_thought_chunk")]
        public void AMessageOfEitherSideCounts(string kind)
        {
            Assert.True(AcpMapper.IsConversationContent(Update(kind)));
        }

        [Theory]
        [InlineData("available_commands_update")]
        [InlineData("current_mode_update")]
        [InlineData("config_option_update")]
        [InlineData("session_info_update")]
        [InlineData("plan")]
        public void AnAnnouncementAboutTheSessionDoesNot(string kind)
        {
            Assert.False(AcpMapper.IsConversationContent(Update(kind)));
        }

        /// <summary>
        /// The measured trap: v3's <c>fetch_cloud_config</c> opening call, live and then replay-marked,
        /// inside the load window of a session it has just created for an id it does not hold.
        /// </summary>
        [Theory]
        [InlineData("tool_call")]
        [InlineData("tool_call_update")]
        public void AToolCallDoesNotBecauseASessionOpeningIsOne(string kind)
        {
            Assert.False(AcpMapper.IsConversationContent(Update(kind)));
        }

        [Fact]
        public void AFrameWithNoKindDoesNot()
        {
            using var doc = JsonDocument.Parse("{\"content\":{\"type\":\"text\",\"text\":\"hi\"}}");
            Assert.False(AcpMapper.IsConversationContent(doc.RootElement));
        }

        private static JsonElement Update(string kind)
        {
            var doc = JsonDocument.Parse("{\"sessionUpdate\":\"" + kind + "\",\"content\":{\"type\":\"text\",\"text\":\"x\"}}");
            return doc.RootElement.Clone();
        }
    }
}
