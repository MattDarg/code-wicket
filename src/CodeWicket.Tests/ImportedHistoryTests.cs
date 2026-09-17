using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CodeWicket.Core;
using CodeWicket.Providers.Acp;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Capturing the conversation a <c>session/load</c> replays, for one started in the backend's own
    /// terminal CLI (issue #108). The frames are the shapes a real replay sends: measured on
    /// claude-agent-acp 0.70.0, a CLI session replayed 413 of them in 4.5s — user and agent message
    /// chunks, tool calls with <c>rawInput</c> and terminal statuses — carrying <b>no replay marker of
    /// any kind</b>, which is why the gate is keyed on the host's MODE and not on the frame.
    /// </summary>
    public sealed class ImportedHistoryTests
    {
        private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

        private static SessionUpdateParams Frame(string updateJson) =>
            new() { SessionId = "sess-1", Update = Parse(updateJson) };

        private static (List<ImportedTurnEntry> Imported, List<AgentEvent> Emitted, AcpClientTarget Target) Importing()
        {
            var imported = new List<ImportedTurnEntry>();
            var emitted = new List<AgentEvent>();
            var target = new AcpClientTarget(
                ide: null!, emitted.Add, () => "sess-1",
                isImporting: () => true, onImportedEntry: imported.Add);
            return (imported, emitted, target);
        }

        [Fact]
        public void Import_CapturesUserWordsAndAgentEvents_InWireOrder()
        {
            var (imported, emitted, target) = Importing();

            target.OnSessionUpdate(Frame(
                """{ "sessionUpdate": "user_message_chunk", "content": { "type": "text", "text": "what does this do?" } }"""));
            target.OnSessionUpdate(Frame(
                """{ "sessionUpdate": "agent_message_chunk", "content": { "type": "text", "text": "It builds a VSIX." } }"""));
            target.OnSessionUpdate(Frame(
                """
                {
                  "sessionUpdate": "tool_call",
                  "toolCallId": "toolu_01Read",
                  "title": "Read",
                  "kind": "read",
                  "rawInput": { "file_path": "C:/ws/Program.cs" }
                }
                """));

            // Nothing is emitted: an import is history being recorded, and a turn-less event routed to
            // the host arrives as live agent activity in the middle of a load.
            Assert.Empty(emitted);

            Assert.Equal(
                new[] { ImportedTurnEntry.UserRole, ImportedTurnEntry.AgentRole, ImportedTurnEntry.AgentRole },
                imported.Select(e => e.Role));
            Assert.Equal("what does this do?", imported[0].Text);
            Assert.Equal("It builds a VSIX.", Assert.IsType<AgentEvent.AssistantTextDelta>(imported[1].Event).Text);
            Assert.Equal("toolu_01Read", Assert.IsType<AgentEvent.ToolCallStarted>(imported[2].Event).ToolCallId);
        }

        [Fact]
        public void Import_StripsOurOwnPromptFraming_FromTheUsersWords()
        {
            // A conversation WE created replays our injected blocks fused to the user's text, and
            // rendering those as the user's words is the thing the mid-turn block is already written
            // against ("or a resume would replay ours as theirs").
            var (imported, _, target) = Importing();

            target.OnSessionUpdate(Frame(
                """
                {
                  "sessionUpdate": "user_message_chunk",
                  "content": {
                    "type": "text",
                    "text": "<mid-turn-message>\nThe user sent this while you were working.\n</mid-turn-message>\n\nalso check the tests"
                  }
                }
                """));

            Assert.Equal("also check the tests", Assert.Single(imported).Text);
        }

        [Fact]
        public void Import_NamesAnImageItCannotRecover_RatherThanDroppingTheMessage()
        {
            // Issue #118's rule read backwards: the bytes are not in the replay and our attachment
            // directory knows nothing of a conversation it never saw, so a named absence is the honest
            // answer. Silence would leave a reader — and, on a summary hand-off, an AGENT — reasoning
            // from an account with the subject removed.
            var (imported, _, target) = Importing();

            target.OnSessionUpdate(Frame(
                """
                {
                  "sessionUpdate": "user_message_chunk",
                  "content": { "type": "image", "mimeType": "image/png", "data": "iVBORw0KGgo=" }
                }
                """));

            var text = Assert.Single(imported).Text;
            Assert.Contains("image", text);
            Assert.Contains("image/png", text);
        }

        [Fact]
        public void Map_YieldsNothingForAUserMessageChunk()
        {
            // Load-bearing, and the reason TryReadUserMessageText is not a Map case: the Claude adapter
            // emits user_message_chunk on a LIVE turn too, so a mapper case would echo the user's own
            // message back into the transcript and the saved log on every ordinary send — with every
            // offline test still green, since nothing offline sends the frame twice.
            var frame = Parse(
                """{ "sessionUpdate": "user_message_chunk", "content": { "type": "text", "text": "hello" } }""");

            Assert.Empty(AcpMapper.Map(frame));
            Assert.True(AcpMapper.TryReadUserMessageText(frame, out var text));
            Assert.Equal("hello", text);
        }

        [Theory]
        [InlineData("agent_message_chunk")]
        [InlineData("tool_call")]
        public void TryReadUserMessageText_ReadsOnlyAUserMessageChunk(string kind)
        {
            var frame = Parse(
                $$"""{ "sessionUpdate": "{{kind}}", "content": { "type": "text", "text": "hello" } }""");

            Assert.False(AcpMapper.TryReadUserMessageText(frame, out _));
        }

        [Fact]
        public void ConfigOptionUpdate_ReachesTheSession_EvenWhileImporting()
        {
            // The reorder that put config options above the replay and import checks. A model selector
            // is CURRENT session state, not history, and neither branch is a home for it: routed into
            // an import it would land in a transcript and the session would never learn its own model.
            // Kiro v3 does not mark it as replayed, so this is a no-op today — which is exactly why it
            // needs pinning rather than leaving to the next engine to break.
            var (imported, emitted, target) = Importing();
            var configFrames = new List<JsonElement>();
            var importingTarget = new AcpClientTarget(
                ide: null!, emitted.Add, () => "sess-1",
                onConfigOptions: configFrames.Add,
                isImporting: () => true, onImportedEntry: imported.Add);

            importingTarget.OnSessionUpdate(Frame(
                """
                {
                  "sessionUpdate": "config_option_update",
                  "configOptions": [ { "id": "model", "category": "model", "currentValue": "sonnet" } ]
                }
                """));

            Assert.Single(configFrames);
            Assert.Empty(imported);
            Assert.Empty(emitted);
        }

        [Fact]
        public void ConfigOptionUpdate_ReachesTheSession_EvenWhenMarkedAsReplay()
        {
            var replayed = new List<JsonElement>();
            var configFrames = new List<JsonElement>();
            var target = new AcpClientTarget(
                ide: null!, _ => { }, () => "sess-1",
                onConfigOptions: configFrames.Add,
                onReplayedUpdate: replayed.Add);

            target.OnSessionUpdate(Frame(
                """
                {
                  "sessionUpdate": "config_option_update",
                  "configOptions": [ { "id": "model", "category": "model", "currentValue": "sonnet" } ],
                  "_meta": { "kiro": { "replay": true } }
                }
                """));

            Assert.Single(configFrames);
            Assert.Empty(replayed);
        }
    }
}
