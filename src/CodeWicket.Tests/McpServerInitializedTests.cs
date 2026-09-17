using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CodeWicket.Core;
using CodeWicket.Ipc;
using CodeWicket.Providers.Acp;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Kiro's <c>_kiro.dev/mcp/server_initialized</c> handling (issue #19): the backend's own MCP
    /// servers connect asynchronously after <c>session/new</c> — a slow one misses the first
    /// prompt's tool snapshot — so each server's readiness surfaces once as an
    /// <see cref="AgentEvent.McpServerConnected"/> notice. Pins: (1) the notification parse,
    /// (2) the dedupe (Kiro re-sends the same server, observed live around session/new), and
    /// (3) the DTO mapping the shell renders from. Frames mirror the real capture (2026-07-16,
    /// kiro-cli 2.12.3).
    /// <para>The v3 agent engine reports the same fact by a different frame entirely — a repeated
    /// <c>_kiro/mcp/status</c> roster snapshot, no <c>server_initialized</c> anywhere (issue #97) — so
    /// the second half of this class pins that shape and, above all, that <em>only</em> an explicit
    /// <c>connected</c> becomes a notice.</para>
    /// </summary>
    public sealed class McpServerInitializedTests
    {
        // A real notification's params, as captured.
        private const string ServerInitializedJson =
            """{ "sessionId": "5833f865-80c1-4841-907e-ef13a108c308", "serverName": "aws-mcp" }""";

        private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

        [Fact]
        public void MapMcpServerInitialized_ParsesRealFrame()
        {
            Assert.Equal("aws-mcp", AcpMapper.MapMcpServerInitialized(Parse(ServerInitializedJson)));
        }

        [Theory]
        [InlineData("""{ "sessionId": "abc" }""")]
        [InlineData("""{ "serverName": "" }""")]
        [InlineData("""{ "serverName": 42 }""")]
        [InlineData("\"frame-is-not-an-object\"")]
        public void MapMcpServerInitialized_UnusableFrames_ReturnNull(string json)
        {
            Assert.Null(AcpMapper.MapMcpServerInitialized(Parse(json)));
        }

        [Fact]
        public void ClientTarget_DedupesRepeatAnnouncements()
        {
            var events = new List<AgentEvent>();
            var target = new AcpClientTarget(ide: null!, events.Add, () => null);

            // Kiro sends the same server's notification twice around session/new (observed live);
            // a different server still announces.
            target.OnMcpServerInitialized(Parse(ServerInitializedJson));
            target.OnMcpServerInitialized(Parse(ServerInitializedJson));
            target.OnMcpServerInitialized(Parse("""{ "serverName": "gitlab" }"""));

            var notices = events.OfType<AgentEvent.McpServerConnected>().ToList();
            Assert.Equal(2, notices.Count);
            Assert.Equal("aws-mcp", notices[0].ServerName);
            Assert.Equal("gitlab", notices[1].ServerName);

            // The roster tracks the WIRE, not the notice: it is emitted outside the dedupe guard, so a
            // re-sent frame still refreshes the panel. Gating it on the guard would leave the panel
            // silently frozen on whatever the first frame happened to say.
            var rosters = events.OfType<AgentEvent.McpRosterUpdated>().ToList();
            Assert.Equal(3, rosters.Count);

            // This engine names a server only when it comes up, so it can never supply a denominator.
            Assert.All(rosters, r => Assert.False(r.Roster.NamesWholeConfiguredSet));
        }

        // Kiro v3's roster snapshot, as captured (2026-08-10, kiro-cli 2.16.2 --agent-engine v3): the
        // frame it sends INSTEAD of server_initialized, which it never sends at all (issue #97). Note
        // both states in one frame — a snapshot reports every server every time, connected or not.
        // Our own server was named differently at capture; the frame's SHAPE is what is under test and
        // the reader is a pure echo, so the name here tracks whatever we are currently called.
        private const string McpStatusJson =
            """
            {
              "sessionId": "sess_0950b6c5-7e71-4d14-910a-22540ef51453",
              "servers": [
                { "name": "aws-mcp", "authType": "oauth", "status": "connecting" },
                { "name": "code_wicket", "status": "connected",
                  "tools": [ { "name": "echo" } ], "prompts": [], "resources": [] }
              ]
            }
            """;

        [Fact]
        public void MapConnectedMcpServers_ReportsOnlyTheConnectedOnes()
        {
            Assert.Equal(
                new[] { "code_wicket" },
                AcpMapper.MapConnectedMcpServers(Parse(McpStatusJson)));
        }

        [Theory]
        // A state we have not seen must never be read as "working" — the whole point of matching
        // "connected" rather than "not connecting".
        [InlineData("""{ "servers": [ { "name": "aws-mcp", "status": "failed" } ] }""")]
        [InlineData("""{ "servers": [ { "name": "aws-mcp" } ] }""")]
        [InlineData("""{ "servers": [ { "status": "connected" } ] }""")]
        [InlineData("""{ "servers": [] }""")]
        [InlineData("""{ "sessionId": "abc" }""")]
        [InlineData("\"frame-is-not-an-object\"")]
        public void MapConnectedMcpServers_UnusableOrUnconnected_YieldNothing(string json)
        {
            Assert.Empty(AcpMapper.MapConnectedMcpServers(Parse(json)));
        }

        [Fact]
        public void ClientTarget_V3StatusSnapshots_AnnounceEachServerOnce()
        {
            var events = new List<AgentEvent>();
            var target = new AcpClientTarget(ide: null!, events.Add, () => null);

            // The measured sequence: the roster arrives repeatedly, so the same connected server is in
            // frame after frame and the second server flips to connected later.
            target.OnMcpStatus(Parse(McpStatusJson));
            target.OnMcpStatus(Parse(McpStatusJson));
            target.OnMcpStatus(Parse(
                """
                {
                  "servers": [
                    { "name": "aws-mcp", "status": "connected", "authType": "oauth" },
                    { "name": "code_wicket", "status": "connected" }
                  ]
                }
                """));

            var notices = events.OfType<AgentEvent.McpServerConnected>().ToList();
            Assert.Equal(2, notices.Count);
            Assert.Equal("code_wicket", notices[0].ServerName);
            Assert.Equal("aws-mcp", notices[1].ServerName);

            // The whole contract in one assertion: the SNAPSHOT repeats and the NOTICE does not. Three
            // frames in, three rosters out, two notices — which is why widening McpServerConnected to
            // carry the roster was rejected rather than being the obvious simplification it looks like.
            var rosters = events.OfType<AgentEvent.McpRosterUpdated>().ToList();
            Assert.Equal(3, rosters.Count);

            // v3 names every configured server, including ones still connecting, so a denominator is
            // honest here and only here.
            Assert.All(rosters, r => Assert.True(r.Roster.NamesWholeConfiguredSet));
            Assert.Equal(2, rosters[0].Roster.Servers.Count);
            Assert.False(rosters[0].Roster.Servers.Single(s => s.Name == "aws-mcp").IsConnected);
            Assert.True(rosters[2].Roster.Servers.Single(s => s.Name == "aws-mcp").IsConnected);
        }

        [Fact]
        public void ClientTarget_SharesDedupeAcrossBothEngineShapes()
        {
            var events = new List<AgentEvent>();
            var target = new AcpClientTarget(ide: null!, events.Add, () => null);

            // One session speaks one engine's dialect, so this can't happen live — it pins that the
            // dedupe is per-server rather than per-notification-shape, which is what keeps a future
            // engine that sent both from announcing everything twice. The status frame goes FIRST so
            // the assertion also fails outright if v3's roster stopped announcing at all.
            target.OnMcpStatus(Parse("""{ "servers": [ { "name": "aws-mcp", "status": "connected" } ] }"""));
            Assert.Single(events.OfType<AgentEvent.McpServerConnected>());

            target.OnMcpServerInitialized(Parse(ServerInitializedJson));
            Assert.Single(events.OfType<AgentEvent.McpServerConnected>());
        }

        [Fact]
        public void DtoMapping_CarriesServerNameAsTitle()
        {
            var dto = DtoMapping.ToDto(new AgentEvent.McpServerConnected("gitlab"));

            Assert.Equal("mcpServerConnected", dto.Type);
            Assert.Equal("gitlab", dto.Title);
        }
    }
}
