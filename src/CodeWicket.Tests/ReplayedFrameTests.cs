using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodeWicket.Core;
using CodeWicket.Core.Ide;
using CodeWicket.Providers.Acp;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Kiro v3 marks every frame of a <c>session/load</c> replay with <c>_meta.kiro.replay: true</c>
    /// (issue #126) — the per-frame fact behind the load window that suppresses a resume's history
    /// today. Frames below are the real capture (2026-08-10, kiro-cli 2.16.2 <c>--agent-engine v3</c>,
    /// a resume of <c>sess_9922f266…</c>: 100 marked frames arrived before the load response, and no
    /// live frame carried the mark).
    /// <para>The trap this class exists to pin is the <b>near-miss field name</b>: <c>replayId</c> sits
    /// on LIVE message chunks and means the opposite of <c>replay</c>. Keying on it would suppress live
    /// output and ingest history, and the two are one character apart in a place nothing type-checks.</para>
    /// </summary>
    public sealed class ReplayedFrameTests
    {
        private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

        private static SessionUpdateParams Frame(string updateJson) =>
            new() { SessionId = "sess-1", Update = Parse(updateJson) };

        // A replayed user message, verbatim from the capture.
        private const string ReplayedUserMessage =
            """
            {
              "sessionUpdate": "user_message_chunk",
              "content": { "type": "text", "text": "what does this solution do?" },
              "_meta": { "kiro": {
                "userMessageTag": "prompt_1a64f822-bc47-4b82-a477-856575da7bdd",
                "messageId": "56228dc5-84dc-4d5a-8e35-c60677b4f133",
                "timestamp": "2026-08-10T21:39:24.482Z",
                "replay": true
              } }
            }
            """;

        // A LIVE message chunk from the same session. `replayId` is a per-utterance id, not a mark.
        private const string LiveChunkWithReplayId =
            """
            {
              "sessionUpdate": "agent_message_chunk",
              "content": { "type": "text", "text": " tasks are" },
              "_meta": { "kiro": { "replayId": "6902ef50-7955-4d17-ad4b-0e2fef54b946-say" } }
            }
            """;

        [Fact]
        public void MarkedFrame_IsRecognisedAsReplay()
        {
            Assert.True(AcpMapper.IsReplayedUpdate(Parse(ReplayedUserMessage)));
        }

        [Theory]
        // The near-miss: a live frame carrying `replayId`. Must NOT read as history.
        [InlineData(LiveChunkWithReplayId)]
        [InlineData("""{ "sessionUpdate": "agent_message_chunk", "_meta": { "kiro": {} } }""")]
        [InlineData("""{ "sessionUpdate": "agent_message_chunk" }""")]
        // Only a literal true counts, so a future engine putting something else under the name cannot
        // quietly turn live work into history.
        [InlineData("""{ "sessionUpdate": "agent_message_chunk", "_meta": { "kiro": { "replay": false } } }""")]
        [InlineData("""{ "sessionUpdate": "agent_message_chunk", "_meta": { "kiro": { "replay": "true" } } }""")]
        [InlineData("""{ "sessionUpdate": "agent_message_chunk", "_meta": { "kiro": { "replay": 1 } } }""")]
        // The mark lives under _meta.kiro, not at the top level or under another vendor's key.
        [InlineData("""{ "sessionUpdate": "agent_message_chunk", "replay": true }""")]
        [InlineData("""{ "sessionUpdate": "agent_message_chunk", "_meta": { "claudeCode": { "replay": true } } }""")]
        [InlineData("\"frame-is-not-an-object\"")]
        public void UnmarkedOrLookalikeFrames_AreNotReplay(string json)
        {
            Assert.False(AcpMapper.IsReplayedUpdate(Parse(json)));
        }

        [Fact]
        public void ClientTarget_MarkedFrame_EmitsNothingAndReportsIt()
        {
            var events = new List<AgentEvent>();
            var replayed = new List<string?>();
            var target = new AcpClientTarget(
                ide: null!, events.Add, () => "sess-1",
                onReplayedUpdate: u => replayed.Add(
                    u.TryGetProperty("sessionUpdate", out var k) ? k.GetString() : null));

            target.OnSessionUpdate(Frame(ReplayedUserMessage));

            Assert.Empty(events);
            Assert.Equal(new[] { "user_message_chunk" }, replayed);
        }

        [Fact]
        public void ClientTarget_LiveFrameWithReplayId_StillReachesTheTranscript()
        {
            var events = new List<AgentEvent>();
            var replayed = new List<string?>();
            var target = new AcpClientTarget(
                ide: null!, events.Add, () => "sess-1",
                onReplayedUpdate: _ => replayed.Add("reported"));

            target.OnSessionUpdate(Frame(LiveChunkWithReplayId));

            // The half that matters: mistaking this for history silently deletes the agent's reply.
            Assert.Equal(" tasks are", Assert.IsType<AgentEvent.AssistantTextDelta>(Assert.Single(events)).Text);
            Assert.Empty(replayed);
        }

        [Fact]
        public async Task ClientTarget_ReplayedToolCall_DoesNotPrimeThePermissionCaches()
        {
            // A replayed tool call ran in a PREVIOUS session, so it must not reach the command cache
            // that a LIVE permission request reads to describe itself — a request naming the same id
            // would otherwise present a command the agent is not about to run, straight into the
            // allow-list matcher. That is why the drop happens before those caches, not after.
            var events = new List<AgentEvent>();
            var permissions = new RecordingPermissions();
            var target = new AcpClientTarget(new PermissionsOnlyIde(permissions), events.Add, () => "sess-1");

            target.OnSessionUpdate(Frame(
                """
                {
                  "sessionUpdate": "tool_call",
                  "toolCallId": "tooluse_replayed",
                  "title": "Terminal",
                  "kind": "execute",
                  "rawInput": { "command": "git push --force" },
                  "_meta": { "kiro": { "messageId": "m1", "timestamp": "2026-08-10T21:39:24.482Z", "replay": true } }
                }
                """));

            Assert.Empty(events);

            // Kiro's permission frame carries no rawInput of its own, so the cache is the only source.
            await target.OnRequestPermissionAsync(new RequestPermissionParams
            {
                SessionId = "sess-1",
                ToolCall = Parse("""{ "toolCallId": "tooluse_replayed", "title": "Terminal" }"""),
                Options = new[] { new PermissionOptionDto("accept", "Allow", "allow_once") },
            });

            Assert.NotNull(permissions.Seen);
            Assert.Null(permissions.Seen!.Command);
        }

        // A Claude-shaped tool call, exactly as a real session/load replay sends one: NO replay marker
        // of any kind (measured on claude-agent-acp 0.70.0 — 413 replayed frames, not one marked), and
        // rawInput carrying the command in full.
        private const string UnmarkedReplayedToolCall =
            """
            {
              "sessionUpdate": "tool_call",
              "toolCallId": "toolu_01ImportedPush",
              "title": "git push --force",
              "kind": "execute",
              "status": "pending",
              "rawInput": { "command": "git push --force" }
            }
            """;

        [Fact]
        public async Task ClientTarget_ImportedToolCall_DoesNotPrimeThePermissionCaches()
        {
            // The security property of the import gate, and the reason it is placed ABOVE the live
            // caches rather than beside the mapping: an imported call ran in a conversation that is
            // over — in the backend's terminal, possibly days ago — and must not be able to describe a
            // permission request the LIVE agent is about to make. The frame here carries no replay
            // mark, so the marked-frame drop above cannot help; only the mode can.
            //
            // The import sink is asserted non-empty on purpose. Without that, a gate that simply threw
            // every frame away would pass this test while destroying the feature.
            var events = new List<AgentEvent>();
            var imported = new List<ImportedTurnEntry>();
            var permissions = new RecordingPermissions();
            var target = new AcpClientTarget(
                new PermissionsOnlyIde(permissions), events.Add, () => "sess-1",
                isImporting: () => true, onImportedEntry: imported.Add);

            target.OnSessionUpdate(Frame(UnmarkedReplayedToolCall));

            Assert.Empty(events);
            Assert.NotEmpty(imported);

            await target.OnRequestPermissionAsync(new RequestPermissionParams
            {
                SessionId = "sess-1",
                ToolCall = Parse("""{ "toolCallId": "toolu_01ImportedPush", "title": "Terminal" }"""),
                Options = new[] { new PermissionOptionDto("accept", "Allow", "allow_once") },
            });

            Assert.NotNull(permissions.Seen);
            Assert.Null(permissions.Seen!.Command);
        }

        [Fact]
        public async Task ClientTarget_ImportOff_UnmarkedToolCall_StillReachesTheLiveCaches()
        {
            // The other half, and it is what stops the gate becoming always-on: with import off, the
            // very same unmarked frame is ordinary live work — it renders, and it primes the cache the
            // permission banner reads. A gate keyed on the frame instead of the mode would fail here.
            var events = new List<AgentEvent>();
            var permissions = new RecordingPermissions();
            var target = new AcpClientTarget(
                new PermissionsOnlyIde(permissions), events.Add, () => "sess-1");

            target.OnSessionUpdate(Frame(UnmarkedReplayedToolCall));

            Assert.NotEmpty(events);

            await target.OnRequestPermissionAsync(new RequestPermissionParams
            {
                SessionId = "sess-1",
                ToolCall = Parse("""{ "toolCallId": "toolu_01ImportedPush", "title": "Terminal" }"""),
                Options = new[] { new PermissionOptionDto("accept", "Allow", "allow_once") },
            });

            Assert.Equal("git push --force", permissions.Seen!.Command);
        }

        [Fact]
        public async Task ClientTarget_UnmarkedReplayDuringAnOrdinaryResume_DoesNotPrimeThePermissionCaches()
        {
            // The gap the two tests above leave open, and it is the COMMON case rather than an exotic
            // one: an ordinary resume on a backend that marks nothing. Kiro v3's mark is what keeps a
            // replayed tool call away from the live caches, and Claude sends no mark at all (measured -
            // 413 replayed frames, none marked), so on that backend every resumed conversation used to
            // prime _commandByToolCallId with commands from a conversation that is over.
            //
            // The events were always discarded downstream by EmitToCurrentTurn's load window, which is
            // why nothing looked wrong; the CACHES are what survived it, and they are read by the
            // permission banner. Not exploitable by accident - a live call carries a fresh toolCallId,
            // so answering from a stale entry needs an id collision - but "a replayed tool call must
            // not prime a cache that answers a live permission request" is either an invariant or it is
            // not, and holding it on one backend only is the version that fails silently.
            var events = new List<AgentEvent>();
            var permissions = new RecordingPermissions();
            var target = new AcpClientTarget(
                new PermissionsOnlyIde(permissions), events.Add, () => "sess-1",
                isLoadingHistory: () => true);

            target.OnSessionUpdate(Frame(UnmarkedReplayedToolCall));

            // Nothing rendered: the load window would have dropped these anyway, so dropping them here
            // costs nothing and is what keeps them away from the caches below.
            Assert.Empty(events);

            await target.OnRequestPermissionAsync(new RequestPermissionParams
            {
                SessionId = "sess-1",
                ToolCall = Parse("""{ "toolCallId": "toolu_01ImportedPush", "title": "Terminal" }"""),
                Options = new[] { new PermissionOptionDto("accept", "Allow", "allow_once") },
            });

            Assert.NotNull(permissions.Seen);
            Assert.Null(permissions.Seen!.Command);
        }

        private sealed class RecordingPermissions : IPermissionHandler
        {
            public PermissionRequest? Seen { get; private set; }

            public Task<PermissionDecision> RequestAsync(
                PermissionRequest request, CancellationToken cancellationToken = default)
            {
                Seen = request;
                return Task.FromResult(new PermissionDecision("accept"));
            }
        }

        // session/update touches none of IIdeServices; only the permission round-trip below does.
        private sealed class PermissionsOnlyIde : IIdeServices
        {
            public PermissionsOnlyIde(IPermissionHandler permissions) => Permissions = permissions;
            public IWorkspaceContext Workspace => throw new NotSupportedException();
            public IEditApplier Edits => throw new NotSupportedException();
            public IToolCatalog Tools => throw new NotSupportedException();
            public IPermissionHandler Permissions { get; }
        }
    }
}
